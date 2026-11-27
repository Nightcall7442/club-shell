using System.Globalization;
using System.Net.NetworkInformation;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Users;
using ClubShell.Server.Admin;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using Npgsql;

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

    private const string AgentVersionHeader = "X-Agent-Version";

    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/agents");
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
        pc.MapPost("/staff/verify", StaffVerifyAsync);
        pc.MapPost("/players/{userId:guid}/token", PlayerTokenAsync);
        pc.MapPost("/commands/{commandId:guid}/ack", async (Guid pcId, Guid commandId, CommandAck ack, CommandRepository commands) =>
        {
            if (!await commands.AckAsync(pcId, commandId, ack))
            {
                throw ApiException.NotFound("command");
            }

            return Results.NoContent();
        });
    }

    /// <summary>
    /// Beyond the contract: <c>POST /agents/{pcId}/staff/verify</c> — a staff member's PIN typed on this PC, before the Agent
    /// lets them sign in to their own Windows account to service it (<see cref="StaffTokens.VerifyForPcAsync"/>). The PIN is
    /// 4–8 digits (<c>400 pin digits4to8</c>, not counted as a wrong PIN).
    /// </summary>
    private static async Task<IResult> StaffVerifyAsync(HttpContext context, StaffVerifyRequest request, StaffTokens staff)
    {
        var pin = request?.Pin ?? throw ApiException.Validation("pin", "required");
        if (!StaffAdminEndpoints.IsPin(pin))
        {
            throw ApiException.Validation("pin", "digits4to8");
        }

        var member = await staff.VerifyForPcAsync(pin, context.Features.GetRequiredFeature<AgentContext>().Pc);
        return TypedResults.Ok(new StaffVerifyResponse(member.StaffId!.Value, member.Name, member.Role));
    }

    /// <summary>
    /// Beyond the contract (D-100): <c>POST /agents/{pcId}/players/{userId}/token</c> — the PC signed its player in from its
    /// offline cache while the server was away and holds no player token, so every player call of theirs would be refused
    /// (<c>401 userToken</c>). The PC's own credentials are not enough to sign anyone in: a token is issued only for the
    /// player of the session open on this very PC (<c>403 notSessionPlayer</c> otherwise — no session, another player's,
    /// or one on another PC), and not to a banned or blacklisted account (<c>403 banned</c>); <c>404 user</c> for an unknown
    /// one. Under the PC's and the player's locks, like a sign-in; answered like one (the open session included), no
    /// <c>offlineHash</c>.
    /// </summary>
    private static async Task<IResult> PlayerTokenAsync(
        HttpContext context, Guid userId, NpgsqlDataSource db, UserTokens tokens, Sessions.SessionService sessions, TimeProvider clock, ILoggerFactory logs)
    {
        var pc = context.Features.GetRequiredFeature<AgentContext>().Pc;
        await using var c = await db.OpenConnectionAsync();
        var club = await Sessions.SessionService.ClubAsync(c, null, pc.ClubId);
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("SET LOCAL lock_timeout = '10s'", transaction: tx);
        await AdvisoryLocks.PcAsync(c, tx, pc.Id);
        await AdvisoryLocks.UserAsync(c, tx, userId);
        var buyer = await Sessions.SessionService.BuyerAsync(c, tx, club, userId) ?? throw ApiException.NotFound("user");
        if (buyer.Banned || buyer.Blacklisted)
        {
            throw ApiException.Forbidden("banned", "The account is banned");
        }

        if (await Sessions.SessionService.OpenOfPcAsync(c, pc.Id, tx) is not { } open || open.UserId != userId)
        {
            logs.CreateLogger(typeof(AgentEndpoints).FullName!).LogWarning("PC {PcId} asked for a player token of {UserId}, who has no open session there", pc.Id, userId);
            throw ApiException.Forbidden("notSessionPlayer", "The player has no open session on this PC");
        }

        var (token, expiresAt) = await tokens.IssueAsync(c, tx, userId, pc.Id);
        var now = clock.GetUtcNow();
        await c.ExecuteAsync("UPDATE users SET last_seen_at = @now WHERE id = @userId", new { userId, now }, tx);
        await tx.CommitAsync();
        var user = await c.QuerySingleAsync<Users.UserRow>($"{Users.UserRow.Select} WHERE u.id = @userId", new { userId });
        return TypedResults.Ok(new AuthResponse(user.ToWire(club.Pricing), open.ToWire(now), token, UserTokens.NewToken(), expiresAt));
    }

    private static async Task<IResult> RegisterAsync(
        HttpContext context, AgentRegisterRequest request, PcRepository pcs, ClubRepository clubs, TokenService tokens, AgentSocketHub hub,
        ReplayLog replays, ClubOptions club, AuthOptions auth, AgentOptions agents, SessionsOptions sessions, TimeProvider clock, ILoggerFactory logs)
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
        var gate = new RegisterProofGate(
            agents.RegisterProof, context.Features.Get<SignedRequest>(), replays, TimeSpan.FromSeconds(auth.SignatureWindowSec), hub.IsConnected,
            TimeSpan.FromSeconds(agents.OfflineAfterSec));
        var (pc, refresh, proof, problem) = await pcs.RegisterAsync(
            clubId, request, NormalizeMac(request.MacAddress), club.AutoApprovePcs, TimeSpan.FromDays(auth.RefreshTokenDays), now, gate);

        // Security events (DESIGN §3.7, D-78), never with the signature headers.
        var ip = context.Connection.RemoteIpAddress;
        var agentVersion = context.Request.Headers[AgentVersionHeader].ToString() is { Length: > 0 } v ? v[..Math.Min(v.Length, 32)] : "?";
        switch (proof)
        {
            case ProofOutcome.Refused when pc.Stateless:
                // A diskless seat back from a reboot before the server noticed it went (SHELL_CHANGES 57): it asks again and
                // gets in once it counts as offline. Noise, not the security event D-78 decides on.
                logger.LogInformation("Re-registration of PC {PcId} without persistent storage refused while it still counts as online ({Problem}) from {Ip}, agent {AgentVersion}", pc.Id, problem, ip, agentVersion);
                break;
            case ProofOutcome.Refused:
                logger.LogWarning("Re-registration of PC {PcId} refused without a valid proof ({Problem}) from {Ip}, agent {AgentVersion}", pc.Id, problem, ip, agentVersion);
                break;
            case ProofOutcome.Unproven:
                logger.LogWarning(
                    "PC {PcId} re-registered without a valid proof ({Problem}) from {Ip}, agent {AgentVersion}; Agents:RegisterProof=enforce would refuse it",
                    pc.Id, problem, ip, agentVersion);
                break;
            case ProofOutcome.Reenrolled:
                logger.LogWarning("PC {PcId} re-enrolled in the owner's maintenance window without a proof ({Problem}) from {Ip}, agent {AgentVersion}", pc.Id, problem, ip, agentVersion);
                break;
            case ProofOutcome.Stateless:
                // Expected at every boot of a diskless seat (SHELL_CHANGES 25): an information, not a security event.
                logger.LogInformation("PC {PcId} without persistent storage re-registered without a proof ({Problem}) from {Ip}, agent {AgentVersion}", pc.Id, problem, ip, agentVersion);
                break;
        }

        if (refresh is null)
        {
            // Security event (DESIGN §3.7). The agent retries registration with backoff until the owner approves the PC; a
            // refused re-registration answers the same, and the PC keeps the credentials it has (D-78).
            if (proof != ProofOutcome.Refused)
            {
                logger.LogInformation("PC {PcId} ({MachineName}) awaits approval", pc.Id, request.MachineName);
            }

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

    /// <summary>
    /// <c>heartbeat</c>: recorded on the PC, and the token of the player whose session is open there lives on
    /// (<see cref="UserTokens.SlideAsync"/>).
    /// </summary>
    private static async Task<IResult> HeartbeatAsync(
        HttpContext context, HeartbeatRequest request, PcRepository pcs, ClubRepository clubs, CommandRepository commands, AgentSocketHub hub,
        UserTokens tokens, AgentOptions agents, TimeProvider clock)
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
        await tokens.SlideAsync(pc.Id);
        var live = await tokens.LiveOfPcAsync(pc.Id);
        var view = await clubs.GetAgentViewAsync(pc.ClubId);
        return TypedResults.Ok(new HeartbeatResponse(
            ServerTime: now,
            PcStatus: pc.Status(connected: true, now, TimeSpan.FromSeconds(agents.OfflineAfterSec)),
            PolicyVersion: view.PolicyVersion,
            ConfigVersion: view.ConfigVersion,
            CatalogVersion: view.CatalogVersion.ToString(CultureInfo.InvariantCulture),
            PendingCommands: await commands.PendingCountAsync(pc.Id, hub.LiveDelivered(pc.Id)),
            Stateless: pc.Stateless,
            PlayerToken: live is { } t ? new HeartbeatPlayerToken(t.UserId, t.ExpiresAt.AddTicks(-(t.ExpiresAt.Ticks % TimeSpan.TicksPerMillisecond))) : null));
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
    private static IResult WithETag(HttpContext context, string tag, Func<IResult> body) =>
        NotModified(context, tag) ? Results.StatusCode(StatusCodes.Status304NotModified) : body();

    /// <summary>Sets the strong <c>ETag</c> <paramref name="tag"/>; true when <c>If-None-Match</c> matches it (answer 304).</summary>
    public static bool NotModified(HttpContext context, string tag)
    {
        var etag = new EntityTagHeaderValue($"\"{tag}\"");
        context.Response.Headers.ETag = etag.ToString();
        return context.Request.GetTypedHeaders().IfNoneMatch.Any(m => m.Equals(EntityTagHeaderValue.Any) || m.Compare(etag, useStrongComparison: false));
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
