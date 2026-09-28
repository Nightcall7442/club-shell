using System.Globalization;
using System.Net.NetworkInformation;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Net.Http.Headers;

namespace ClubShell.Server.Agents;

/// <summary>
/// The eight agent operations of slice S1 (DESIGN §3.2, §5.9, §6.4, §11; port of club-server <c>Agents/AgentEndpoints.cs</c>):
/// register, refresh, heartbeat, telemetry, config, policies, commands, ack. <c>/agents/{pcId}/*</c> must name the PC
/// of the token (<c>403 pcMismatch</c>).
/// </summary>
public static class AgentEndpoints
{
    public static readonly string[] Operations =
        ["register", "refresh", "heartbeat", "sendTelemetry", "getConfig", "getPolicies", "getCommands", "ackCommand"];

    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        // Any Content-Type reaches the handler: body binding then rejects a non-JSON one (415 → 400 validation). With the
        // inferred application/json only, routing would skip these endpoints and the /api/v1 fallback would answer 404.
        var api = app.MapGroup("/api/v1/agents");
        ((IEndpointConventionBuilder)api).Finally(endpoint => endpoint.Metadata.Add(new AcceptsMetadata(["application/json", "*/*"])));
        api.MapPost("/register", RegisterAsync).WithMetadata(new AuthRequirement(AuthMode.Club));
        api.MapPost("/refresh", RefreshAsync).WithMetadata(new AuthRequirement(AuthMode.None));

        var pc = api.MapGroup("/{pcId:guid}").WithMetadata(new AuthRequirement(AuthMode.Agent)).AddEndpointFilter((context, next) =>
        {
            if (context.HttpContext.GetRouteValue("pcId") is string raw && Guid.TryParse(raw, out var pcId)
                && pcId != context.HttpContext.Features.GetRequiredFeature<AgentContext>().Pc.Id)
            {
                throw ApiException.Forbidden("pcMismatch", "pcId does not match the agent token");
            }

            return next(context);
        });
        pc.MapPost("/heartbeat", HeartbeatAsync);
        pc.MapPost("/telemetry", TelemetryAsync);
        pc.MapGet("/config", ConfigAsync);
        pc.MapGet("/policies", PoliciesAsync);
        pc.MapGet("/commands", async (Guid pcId, CommandRepository commands) => TypedResults.Ok(new ServerCommandsResponse(await commands.PendingAsync(pcId))));
        pc.MapPost("/commands/{commandId:guid}/ack", async (Guid pcId, Guid commandId, CommandAck ack, CommandRepository commands) =>
        {
            if (!await commands.AckAsync(pcId, commandId, ack))
            {
                throw ApiException.NotFound("command");
            }

            return Results.NoContent();
        });
    }

    private static async Task<IResult> RegisterAsync(
        HttpContext context, AgentRegisterRequest request, PcRepository pcs, ClubRepository clubs, TokenService tokens, AgentSocketHub hub,
        ClubOptions club, AuthOptions auth, AgentOptions agents, SessionsOptions sessions, TimeProvider clock, ILoggerFactory logs)
    {
        var logger = logs.CreateLogger(typeof(AgentEndpoints).FullName!);
        Require(request.Hwid, "hwid");
        Require(request.MachineName, "machineName");
        Require(request.AgentVersion, "agentVersion");
        Require(request.IpAddress, "ipAddress");
        Require(request.MacAddress, "macAddress");
        if (request.Hardware is null)
        {
            throw ApiException.Validation("hardware", "required");
        }

        var clubId = context.Features.GetRequiredFeature<ClubContext>().ClubId;
        var now = clock.GetUtcNow();
        var (pc, refresh) = await pcs.RegisterAsync(clubId, request, NormalizeMac(request.MacAddress), club.AutoApprovePcs, TimeSpan.FromDays(auth.RefreshTokenDays), now);
        if (refresh is null)
        {
            // Security event (DESIGN §3.7). The agent retries registration with backoff until the owner approves the PC.
            logger.LogInformation("PC {PcId} ({MachineName}) awaits approval", pc.Id, request.MachineName);
            throw new ApiException(StatusCodes.Status403Forbidden, ErrorCode.Forbidden, "PC is waiting for approval", new { reason = "pendingApproval", pcId = pc.Id });
        }

        // cv was bumped: a socket still open on the old token goes (§6.7).
        await hub.RevokeAsync(pc.Id);
        logger.LogInformation("PC {PcId} ({MachineName}) registered, credentials version {Cv}", pc.Id, request.MachineName, pc.CredentialsVersion);
        var (access, expiresAt) = tokens.IssueAccessToken(pc.Id, pc.ClubId, pc.Hwid!, pc.CredentialsVersion);
        var view = await clubs.GetAgentViewAsync(pc.ClubId);
        return TypedResults.Ok(new AgentRegisterResponse(
            pc.Id,
            pc.ToPc(pc.Status(hub.IsConnected(pc.Id), now, TimeSpan.FromSeconds(agents.OfflineAfterSec)), withHwid: true),
            access,
            refresh,
            Convert.ToBase64String(pc.SigningSecret!),
            expiresAt,
            now,
            AgentConfig.Build(pc, view, agents, sessions)));
    }

    private static async Task<IResult> RefreshAsync(
        AgentRefreshRequest request, PcRepository pcs, TokenService tokens, AgentSocketHub hub, AuthOptions auth, TimeProvider clock, ILoggerFactory logs)
    {
        Require(request.RefreshToken, "refreshToken");
        Require(request.Hwid, "hwid");
        var (outcome, pc, refresh) = await pcs.RefreshAsync(request.RefreshToken, request.Hwid, TimeSpan.FromDays(auth.RefreshTokenDays), clock.GetUtcNow());
        switch (outcome)
        {
            case RefreshOutcome.Reused:
                // A used token came back: it may have leaked. Everything of the PC is revoked, its socket closed with
                // 4401; the agent re-registers. Security event (DESIGN §3.7).
                logs.CreateLogger(typeof(AgentEndpoints).FullName!).LogWarning("Refresh token of PC {PcId} reused; credentials revoked", pc!.Id);
                await hub.RevokeAsync(pc.Id);
                throw ApiException.Unauthorized("reused", "Refresh token already used");
            case RefreshOutcome.Expired:
                throw ApiException.Unauthorized("expired", "Refresh token expired");
            case RefreshOutcome.Revoked:
                throw ApiException.Unauthorized("revoked", "Refresh token revoked");
        }

        var (access, expiresAt) = tokens.IssueAccessToken(pc!.Id, pc.ClubId, pc.Hwid!, pc.CredentialsVersion);
        return TypedResults.Ok(new AgentRefreshResponse(access, refresh!, expiresAt));
    }

    private static async Task<IResult> HeartbeatAsync(
        HttpContext context, HeartbeatRequest request, PcRepository pcs, ClubRepository clubs, CommandRepository commands, AgentSocketHub hub,
        AgentOptions agents, TimeProvider clock)
    {
        Require(request.AgentVersion, "agentVersion");

        // Keys required, values may be empty (the contract sets no minLength): an agent without a usable IPv4 adapter
        // sends "" and must still beat, or it never learns versions, pending commands or the clock offset.
        if (request.ShellVersion is null || request.IpAddress is null)
        {
            throw ApiException.Validation(request.ShellVersion is null ? "shellVersion" : "ipAddress", "required");
        }

        if (request.RunningGames is null)
        {
            throw ApiException.Validation("runningGames", "required");
        }

        var pc = context.Features.GetRequiredFeature<AgentContext>().Pc;
        var now = clock.GetUtcNow();
        await pcs.RecordHeartbeatAsync(pc.Id, request, now);
        var view = await clubs.GetAgentViewAsync(pc.ClubId);
        return TypedResults.Ok(new HeartbeatResponse(
            ServerTime: now,
            PcStatus: pc.Status(connected: true, now, TimeSpan.FromSeconds(agents.OfflineAfterSec)),
            PolicyVersion: view.PolicyVersion,
            ConfigVersion: view.ConfigVersion,
            CatalogVersion: view.CatalogVersion.ToString(CultureInfo.InvariantCulture),
            PendingCommands: await commands.PendingCountAsync(pc.Id, hub.LiveDelivered(pc.Id))));
    }

    /// <summary>The contract obliges the server to take up to 120 samples and 1024 events: old agents do not split batches.</summary>
    private static async Task<IResult> TelemetryAsync(HttpContext context, TelemetryBatch batch, PcRepository pcs, TimeProvider clock)
    {
        if (batch.Samples is null || batch.Samples.Count > TelemetryBatch.MaxSamples)
        {
            throw ApiException.Validation("samples", batch.Samples is null ? "required" : "max");
        }

        if (batch.Events is null || batch.Events.Count > 1024) // the agent's offline buffer
        {
            throw ApiException.Validation("events", batch.Events is null ? "required" : "max");
        }

        for (var i = 0; i < batch.Events.Count; i++)
        {
            if (string.IsNullOrEmpty(batch.Events[i]?.Kind) || batch.Events[i].Data.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            {
                throw ApiException.Validation($"events[{i}]", "required");
            }
        }

        await pcs.IngestTelemetryAsync(context.Features.GetRequiredFeature<AgentContext>().Pc, batch, clock.GetUtcNow());
        return Results.NoContent();
    }

    private static async Task<IResult> ConfigAsync(HttpContext context, ClubRepository clubs, AgentOptions agents, SessionsOptions sessions)
    {
        var pc = context.Features.GetRequiredFeature<AgentContext>().Pc;
        var view = await clubs.GetAgentViewAsync(pc.ClubId);
        return WithETag(context, $"c{view.ConfigVersion}", () => TypedResults.Ok(AgentConfig.Build(pc, view, agents, sessions)));
    }

    private static async Task<IResult> PoliciesAsync(HttpContext context, ClubRepository clubs)
    {
        var (version, json) = await clubs.GetPolicyAsync(context.Features.GetRequiredFeature<AgentContext>().Pc.ClubId);
        return WithETag(context, $"p{version}", () => Results.Content(json!, "application/json; charset=utf-8"));
    }

    /// <summary>Strong ETag (RFC 9110, quoted); a matching <c>If-None-Match</c> gets <c>304</c> without a body.</summary>
    private static IResult WithETag(HttpContext context, string tag, Func<IResult> body)
    {
        var etag = new EntityTagHeaderValue($"\"{tag}\"");
        context.Response.Headers.ETag = etag.ToString();
        var match = context.Request.GetTypedHeaders().IfNoneMatch;
        return match.Any(m => m.Equals(EntityTagHeaderValue.Any) || m.Compare(etag, useStrongComparison: false))
            ? Results.StatusCode(StatusCodes.Status304NotModified)
            : body();
    }

    /// <summary><c>pcs.mac_address</c>: lower case with colons (DESIGN §9); an unparsable value is kept lower-cased.</summary>
    public static string NormalizeMac(string mac)
    {
        var hex = new string(mac.Where(char.IsAsciiHexDigit).ToArray());
        return hex.Length == 12 && PhysicalAddress.TryParse(hex, out var address)
            ? string.Join(':', address.GetAddressBytes().Select(b => b.ToString("x2", CultureInfo.InvariantCulture)))
            : mac.Trim().ToLowerInvariant();
    }

    private static void Require(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw ApiException.Validation(field, "required");
        }
    }
}
