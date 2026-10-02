using System.Globalization;
using System.Security.Cryptography;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Wallet;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Sessions.Billing;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;

namespace ClubShell.Server.Wallet;

/// <summary>
/// <c>getTariffs</c> (S2), and beyond the contract, which still marks them notImplemented, <c>GET /wallet/{userId}/balance</c>
/// and <c>/transactions</c>: the agent turns their 501 into a hard error in the shell's wallet (DESIGN §1, §12.2 item 1).
/// The top-up intents stay 501: the club has no payment provider, players top up at the counter (<c>adminTopUp</c>).
/// </summary>
public static class WalletEndpoints
{
    public static readonly string[] Operations = ["getTariffs", "getBalance", "getTransactions"];

    /// <summary>Contract <c>TransactionType</c>: the <c>ledger_entries.type</c> values, matched case-sensitively like the mock.</summary>
    private static readonly string[] TransactionTypes = ["topUp", "charge", "refund", "bonus", "purchase", "adjustment"];

    public static void MapWalletEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1");
        api.MapGet("/tariffs", TariffsAsync).WithMetadata(new AuthRequirement(AuthMode.Agent));
        var wallet = api.MapGroup("/wallet/{userId:guid}").OwnedByPlayer();
        wallet.MapGet("/balance", async (Guid userId, NpgsqlDataSource db) =>
        {
            await using var c = await db.OpenConnectionAsync();
            return TypedResults.Ok(await Ledger.BalanceAsync(c, userId) ?? throw ApiException.NotFound("user"));
        });
        wallet.MapGet("/transactions", TransactionsAsync);
    }

    /// <summary>
    /// The player's ledger, newest first (<c>created_at</c>, then id, as the counter's client card). <c>from</c> inclusive,
    /// <c>to</c> exclusive; checked in the mock's order: <c>type</c> (<c>enum</c>), <c>from</c>, <c>to</c> (<c>format</c>).
    /// Paging is normalized, never 400 (<see cref="Paging"/>): default 50, at most 200, the applied size is echoed.
    /// </summary>
    private static async Task<IResult> TransactionsAsync(
        Guid userId, string? page, string? pageSize, string? from, string? to, string? type, NpgsqlDataSource db)
    {
        if (!string.IsNullOrEmpty(type) && !TransactionTypes.Contains(type, StringComparer.Ordinal))
        {
            throw ApiException.Validation("type", "enum");
        }

        var since = Instant(from, "from");
        var until = Instant(to, "to");
        var (p, size) = Paging.Normalize(page, pageSize);
        var where = "user_id = @userId"
            + (string.IsNullOrEmpty(type) ? "" : " AND type = @type")
            + (since is null ? "" : " AND created_at >= @since")
            + (until is null ? "" : " AND created_at < @until");
        var args = new { userId, type, since, until, size, offset = (long)(p - 1) * size };

        await using var c = await db.OpenConnectionAsync();
        var total = await c.ExecuteScalarAsync<int>($"SELECT count(*)::int FROM ledger_entries WHERE {where}", args);
        var rows = await c.QueryAsync<TransactionRow>(
            $"SELECT {TransactionRow.Columns} FROM ledger_entries WHERE {where} ORDER BY created_at DESC, id DESC LIMIT @size OFFSET @offset", args);
        return TypedResults.Ok(new PagedResult<Transaction>(rows.Select(r => r.ToWire()).ToList(), total, p, size));
    }

    /// <summary>An ISO-8601 bound (UTC unless it carries an offset); empty — none, unparseable — <c>400 format</c>.</summary>
    private static DateTimeOffset? Instant(string? value, string field) =>
        string.IsNullOrEmpty(value) ? null
        : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToUniversalTime()
        : throw ApiException.Validation(field, "format");

    /// <summary>
    /// Live tariffs of the agent's club, those of <paramref name="zone"/> when given (empty zones = every zone). The
    /// <c>ETag</c> hashes <c>items</c> only, and the answer is always 200: <c>If-None-Match</c> is ignored (DESIGN §7.3).
    /// </summary>
    private static async Task<IResult> TariffsAsync(HttpContext context, string? zone, NpgsqlDataSource db, TimeProvider clock)
    {
        var clubId = context.Features.GetRequiredFeature<AgentContext>().Pc.ClubId;
        await using var c = await db.OpenConnectionAsync();
        var rows = await c.QueryAsync<TariffRow>(
            $"SELECT {TariffRow.Columns} FROM tariffs WHERE club_id = @clubId AND deleted_at IS NULL ORDER BY created_at, id", new { clubId });
        IReadOnlyList<Tariff> items = rows.Select(r => r.ToWire())
            .Where(t => string.IsNullOrEmpty(zone) || t.Zones.Count == 0 || t.Zones.Contains(zone, StringComparer.OrdinalIgnoreCase))
            .ToList();
        context.Response.Headers.ETag = $"\"t{Convert.ToHexStringLower(SHA256.HashData(JsonDefaults.SerializeToUtf8Bytes(items)))[..16]}\"";
        return TypedResults.Ok(new TariffsResponse(items, clock.GetUtcNow()));
    }
}
