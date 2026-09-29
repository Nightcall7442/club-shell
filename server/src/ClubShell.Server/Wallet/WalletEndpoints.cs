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
/// <c>getTariffs</c> (S2) and <c>GET /wallet/{userId}/balance</c> — implemented beyond the contract, which still marks it
/// notImplemented: the agent turns its 501 into a hard error in the shell (DESIGN §1, §12.2 item 1).
/// </summary>
public static class WalletEndpoints
{
    public static readonly string[] Operations = ["getTariffs", "getBalance"];

    public static void MapWalletEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1");
        api.MapGet("/tariffs", TariffsAsync).WithMetadata(new AuthRequirement(AuthMode.Agent));
        api.MapGroup("/wallet/{userId:guid}").OwnedByPlayer().MapGet("/balance", async (Guid userId, NpgsqlDataSource db) =>
        {
            await using var c = await db.OpenConnectionAsync();
            return TypedResults.Ok(await Ledger.BalanceAsync(c, userId) ?? throw ApiException.NotFound("user"));
        });
    }

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
