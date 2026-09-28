using System.Text.Json;
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
/// A different request hash under the same key is only logged: an offline replay legitimately re-sends a fuller body.
/// </summary>
public sealed class IdempotencyStore(NpgsqlDataSource db, ILogger<IdempotencyStore> logger)
{
    public const string KeyHeader = "Idempotency-Key";

    /// <summary>
    /// Runs <paramref name="handler"/> in a READ COMMITTED transaction with <c>lock_timeout = 10s</c> (below the agent's
    /// 15 s request timeout). Without a key the handler still gets the transaction, nothing is stored.
    /// </summary>
    public async Task<IdempotentResult> ExecuteAsync(
        string principal,
        string method,
        string path,
        Guid? key,
        byte[] requestHash,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<IdempotentResult>> handler,
        CancellationToken cancellationToken = default)
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
