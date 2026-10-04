using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClubShell.Contracts.Errors;
using ClubShell.Server.Infrastructure;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Idempotency;

/// <summary>A handler's answer, or the stored one when <see cref="Replayed"/> (sent with <c>Idempotent-Replayed: true</c>).</summary>
public sealed record IdempotentResult(int Status, JsonElement? Body, bool Replayed = false);

/// <summary>
/// <c>Idempotency-Key</c> (DESIGN §7.1): the key row and the business change commit in one transaction.
/// <code>
/// INSERT … ON CONFLICT DO NOTHING   -- a concurrent twin blocks on the unique index until the first commits
/// 0 rows  → stored status and body (replay)
/// 1 row   → handler in the same transaction → UPDATE status, body → COMMIT
/// error / status ≥ 400 → ROLLBACK: errors are not stored, a repeat runs again
/// </code>
/// Key rows are scoped by <c>principal</c> (<c>pc:&lt;id&gt;</c> | <c>club:&lt;id&gt;</c>), method and path without query.
/// A different request hash under the same key is only logged: an offline replay legitimately re-sends a fuller body. The
/// desk routes of cash desk part 3 (bar sale and void, session move, bulk commands) ask for <c>strictBody</c> (D-70): there
/// a known key with another body is <c>409 conflict reason=idempotencyKeyReused</c> and nothing is replayed.
/// </summary>
public sealed class IdempotencyStore(NpgsqlDataSource db, ILogger<IdempotencyStore> logger)
{
    public const string KeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    /// <summary>
    /// HTTP glue: the <c>Idempotency-Key</c> of the request (<c>400 validation</c> when <paramref name="keyRequired"/> and
    /// absent, or not a UUID — its version is not checked, the events key is a hash), the handler in the key's transaction,
    /// and the answer — a replay with <c>Idempotent-Replayed: true</c>. <paramref name="body"/> is hashed: for the log, or
    /// with <paramref name="strictBody"/> to refuse a reused key (D-70).
    /// </summary>
    public async Task<IResult> ExecuteHttpAsync(
        HttpContext context, string principal, bool keyRequired, JsonElement? body,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<IdempotentResult>> handler, bool strictBody = false) =>
        ToHttp(await ExecuteHttpResultAsync(context, principal, keyRequired, body, handler, strictBody));

    /// <summary>
    /// <see cref="ExecuteHttpAsync"/> without the last step: the stored or fresh answer itself, for a route that still has
    /// work after the commit (the bulk commands wait for the PCs' acks and answer more than they stored).
    /// </summary>
    public async Task<IdempotentResult> ExecuteHttpResultAsync(
        HttpContext context, string principal, bool keyRequired, JsonElement? body,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<IdempotentResult>> handler, bool strictBody = false)
    {
        var raw = context.Request.Headers[KeyHeader].ToString();
        Guid? key = raw.Length == 0 ? null : Guid.TryParse(raw, out var parsed) ? parsed : throw ApiException.Validation(KeyHeader, "format");
        if (key is null && keyRequired)
        {
            throw ApiException.Validation(KeyHeader, "required");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(body?.GetRawText() ?? ""));
        var result = await ExecuteAsync(principal, context.Request.Method, context.Request.Path.Value ?? "", key, hash, handler, context.RequestAborted, strictBody);
        if (result.Replayed)
        {
            context.Response.Headers[ReplayedHeader] = "true";
        }

        return result;
    }

    /// <summary>The HTTP answer of an <see cref="IdempotentResult"/>: its status and JSON body, or none.</summary>
    public static IResult ToHttp(IdempotentResult result) =>
        result.Body is { } stored
            ? Results.Content(stored.GetRawText(), "application/json; charset=utf-8", statusCode: result.Status)
            : Results.StatusCode(result.Status);

    /// <summary>
    /// Runs <paramref name="handler"/> in a READ COMMITTED transaction with <c>lock_timeout = 10s</c> (below the agent's
    /// 15 s request timeout). Without a key the handler still gets the transaction, nothing is stored. With
    /// <paramref name="strictBody"/> a stored key whose request hash differs is <c>409 idempotencyKeyReused</c>, not a replay.
    /// </summary>
    public async Task<IdempotentResult> ExecuteAsync(
        string principal,
        string method,
        string path,
        Guid? key,
        byte[] requestHash,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<IdempotentResult>> handler,
        CancellationToken cancellationToken = default,
        bool strictBody = false)
    {
        await using var c = await db.OpenConnectionAsync(cancellationToken);
        await using var tx = await c.BeginTransactionAsync(cancellationToken);
        await c.ExecuteAsync("SET LOCAL lock_timeout = '10s'", transaction: tx);

        var row = new { principal, method, path, key, requestHash };
        if (key is not null)
        {
            var inserted = await c.ExecuteAsync(
                """
                INSERT INTO idempotency_keys (principal, method, path, key, request_hash)
                VALUES (@principal, @method, @path, @key, @requestHash)
                ON CONFLICT DO NOTHING
                """,
                row,
                tx);
            if (inserted == 0)
            {
                var stored = await c.QuerySingleAsync<(int Status, string? Response, byte[] RequestHash)>(
                    "SELECT status_code, response::text, request_hash FROM idempotency_keys WHERE principal = @principal AND method = @method AND path = @path AND key = @key",
                    row,
                    tx);
                await tx.CommitAsync(cancellationToken);
                if (!stored.RequestHash.AsSpan().SequenceEqual(requestHash))
                {
                    if (strictBody)
                    {
                        throw new ApiException(StatusCodes.Status409Conflict, ErrorCode.Conflict, "Conflict: idempotencyKeyReused",
                            new { reason = "idempotencyKeyReused" });
                    }

                    logger.LogInformation("Idempotency key {Key} of {Principal} replayed for {Method} {Path} with a different body", key, principal, method, path);
                }

                return new IdempotentResult(stored.Status, stored.Response is null ? null : JsonElement.Parse(stored.Response), Replayed: true);
            }
        }

        var result = await handler(c, tx);
        if (result.Status >= 400)
        {
            await tx.RollbackAsync(cancellationToken);
            return result;
        }

        if (key is not null)
        {
            await c.ExecuteAsync(
                "UPDATE idempotency_keys SET status_code = @status, response = @response::jsonb WHERE principal = @principal AND method = @method AND path = @path AND key = @key",
                new { principal, method, path, key, status = result.Status, response = result.Body?.GetRawText() },
                tx);
        }

        await tx.CommitAsync(cancellationToken);
        return result;
    }
}
