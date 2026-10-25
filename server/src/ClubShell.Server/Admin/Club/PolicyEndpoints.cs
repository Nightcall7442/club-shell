using System.Text.Json;
using System.Text.Json.Nodes;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>The club's PC policy as the console reads it: the document the agents get, and whether it is the club's own.</summary>
/// <param name="Policy">The stored contract <see cref="Policy"/> (<c>version</c> = <c>policy_version</c>).</param>
/// <param name="Custom">True once the owner saved it; the startup seed then leaves it alone.</param>
public sealed record AdminPolicy(JsonElement Policy, bool Custom);

/// <summary>
/// The club's PC policy beyond the contract (SHELL_CHANGES 27), owner only: <c>GET /admin/policy</c> reads it, <c>PUT</c>
/// replaces it with a contract <c>Policy</c> (its <c>version</c> and <c>updatedAt</c> are the server's: whatever was sent is
/// overwritten) and makes it the club's own, so the seed applied at startup (D-14) no longer overwrites it; <c>DELETE</c>
/// goes back to the seed. A change bumps <c>policy_version</c> and sends <c>reloadPolicy</c> to the connected PCs; the
/// others learn the version from their next heartbeat. A body that is not a valid <c>Policy</c> is <c>400 validation</c>.
/// </summary>
public static class PolicyEndpoints
{
    public static void MapPolicyEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/admin/policy").WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
        api.MapGet("", GetAsync);
        api.MapPut("", SaveAsync);
        api.MapDelete("", ResetAsync);
    }

    private static async Task<IResult> GetAsync(HttpContext context, NpgsqlDataSource db)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        await using var c = await db.OpenConnectionAsync();
        var (json, custom) = await c.QuerySingleAsync<(string?, bool)>(
            "SELECT policy::text, policy_custom FROM clubs WHERE id = @ClubId", new { staff.ClubId });
        if (json is null)
        {
            throw ApiException.NotFound("policy");
        }

        return AdminJson.Ok(new AdminPolicy(JsonElement.Parse(json), custom));
    }

    private static async Task<IResult> SaveAsync(
        HttpContext context, [FromBody] JsonElement body, NpgsqlDataSource db, CommandDispatcher commands, AgentSocketHub hub, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        if (JsonNode.Parse(body.GetRawText()) is not JsonObject document)
        {
            throw ApiException.Validation("body", "schema", "JSON object expected");
        }

        var now = clock.GetUtcNow();
        document["version"] = 0;
        document["updatedAt"] = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var element = JsonSerializer.SerializeToElement(document);
        ContractSchemas.Validate("Policy", element);
        var policy = JsonDefaults.Deserialize<Policy>(element.GetRawText())
            ?? throw ApiException.Validation("body", "schema", "Policy expected");
        return await StoreAsync(staff, db, commands, hub, policy, custom: true, now, "policySave");
    }

    private static async Task<IResult> ResetAsync(HttpContext context, NpgsqlDataSource db, CommandDispatcher commands, AgentSocketHub hub, ClubSeeds seeds, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var seed = JsonDefaults.Deserialize<Policy>(await File.ReadAllTextAsync(seeds.PolicyPath))
            ?? throw new InvalidOperationException($"Policy seed {seeds.PolicyPath} is empty");
        return await StoreAsync(staff, db, commands, hub, seed, custom: false, clock.GetUtcNow(), "policyReset");
    }

    private static async Task<IResult> StoreAsync(
        StaffContext staff, NpgsqlDataSource db, CommandDispatcher commands, AgentSocketHub hub, Policy policy, bool custom, DateTimeOffset now, string action)
    {
        Policy stored;
        IReadOnlyList<(Guid PcId, Contracts.Commands.ServerCommandEnvelope Command)> queued;
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var version = await c.ExecuteScalarAsync<int>("SELECT policy_version FROM clubs WHERE id = @ClubId FOR UPDATE", new { staff.ClubId }, tx);
            stored = await ClubRepository.StorePolicyAsync(c, tx, staff.ClubId, version, policy, custom, now);
            await Audit.WriteAsync(c, tx, staff, now, action, detail: $"v{stored.Version}", meta: new { version = stored.Version, custom });
            queued = await ConfigRefresh.QueueAsync(c, tx, commands, hub, staff.ClubId, NewCommand.ReloadPolicy(), staff.StaffId);
            await tx.CommitAsync();
        }

        await ConfigRefresh.SendAsync(commands, queued);
        return AdminJson.Ok(new AdminPolicy(JsonDefaults.ToElement(stored), custom));
    }
}
