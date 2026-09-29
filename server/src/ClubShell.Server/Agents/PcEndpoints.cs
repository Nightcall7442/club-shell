using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Games;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace ClubShell.Server.Agents;

/// <summary>
/// Slice S3 for the PC itself: <c>getPc</c> (any PC of the club, <c>hwid</c> only of the caller's own) and, beyond the
/// contract, which still marks it notImplemented, <c>POST /anticheat/report</c> (<c>reportAntiCheat</c>, 204): the body is
/// the WS event <c>anticheatViolation</c>'s and is stored the same way, so a report the agent queued offline is not lost
/// (DESIGN §5.9 <c>anticheat.reportViolations = true</c>, §12.2 item 1).
/// </summary>
public static class PcEndpoints
{
    public static readonly string[] Operations = ["getPc", "reportAntiCheat"];

    public static void MapPcEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1");
        api.MapGet("/pcs/{pcId}", GetAsync).WithMetadata(new AuthRequirement(AuthMode.Agent));
        api.MapPost("/anticheat/report", ReportAsync).WithMetadata(new AuthRequirement(AuthMode.Agent));
    }

    private static async Task<IResult> GetAsync(
        HttpContext context, string pcId, PcRepository pcs, AgentSocketHub hub, AgentOptions agents, TimeProvider clock)
    {
        var self = context.Features.GetRequiredFeature<AgentContext>().Pc;
        if (!Guid.TryParse(pcId, out var id))
        {
            throw ApiException.Validation("pcId", "format");
        }

        var pc = await pcs.FindAsync(id);
        if (pc is null || pc.DeletedAt is not null || pc.ClubId != self.ClubId)
        {
            throw ApiException.NotFound("pc");
        }

        return TypedResults.Ok(pc.ToPc(pc.Status(hub.IsConnected(pc.Id), clock.GetUtcNow(), TimeSpan.FromSeconds(agents.OfflineAfterSec)), withHwid: pc.Id == self.Id));
    }

    private static async Task<IResult> ReportAsync(HttpContext context, [FromBody] JsonElement body, PcRepository pcs, TimeProvider clock)
    {
        var pc = context.Features.GetRequiredFeature<AgentContext>().Pc;
        var report = Api.Read<AntiCheatReport>(body, "pcId", "kind", "check", "severity", "details", "at", "actionTaken");
        if (report.Kind == AntiCheatKind.Unknown)
        {
            throw ApiException.Validation("kind", "enum");
        }

        if (report.Details.ValueKind != JsonValueKind.Object)
        {
            throw ApiException.Validation("details", "format");
        }

        if (report.PcId != pc.Id)
        {
            throw ApiException.Forbidden("pcMismatch", "pcId does not match the agent token");
        }

        await pcs.WriteAgentEventAsync(pc, AgentEventType.AnticheatViolation, "anticheatViolation", report.At, body, clock.GetUtcNow());
        return Results.NoContent();
    }
}
