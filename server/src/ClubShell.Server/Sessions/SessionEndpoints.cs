using System.Globalization;
using System.Text.Json;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Sessions;
using ClubShell.Server.Auth;
using ClubShell.Server.Idempotency;
using ClubShell.Server.Infrastructure;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Sessions;

/// <summary>
/// The seven session operations of slice S2 (DESIGN §5, §11): HTTP validation and authorization here, the rules and the
/// money in <see cref="SessionService"/>. <c>POST /sessions</c>, <c>/extend</c> and <c>/events</c> require an
/// <c>Idempotency-Key</c>; the key row and the change commit together (§7.1).
/// </summary>
public static class SessionEndpoints
{
    public static readonly string[] Operations =
        ["getCurrentSession", "createSession", "pauseSession", "resumeSession", "endSession", "extendSession", "postSessionEvents"];

    public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/sessions");
        api.MapGet("/current", CurrentAsync).WithMetadata(new AuthRequirement(AuthMode.Agent));

        // Agent token alone is enough for the offline replay (D-11); the handler demands the player token otherwise.
        api.MapPost("", CreateAsync).WithMetadata(new AuthRequirement(AuthMode.AgentOptionalUser));
        api.MapPost("/{id:guid}/pause", (HttpContext context, Guid id, SessionService sessions) =>
            sessions.PauseAsync(id, context.Features.GetRequiredFeature<UserContext>().UserId, context.Features.GetRequiredFeature<AgentContext>().Pc.Id))
            .WithMetadata(new AuthRequirement(AuthMode.User));
        api.MapPost("/{id:guid}/resume", (HttpContext context, Guid id, SessionService sessions) =>
            sessions.ResumeAsync(id, context.Features.GetRequiredFeature<UserContext>().UserId, context.Features.GetRequiredFeature<AgentContext>().Pc.Id))
            .WithMetadata(new AuthRequirement(AuthMode.User));
        api.MapPost("/{id:guid}/extend", ExtendAsync).WithMetadata(new AuthRequirement(AuthMode.User));

        // The agent ends sessions without a player too (time-up, admin command, power): the user token is optional.
        api.MapPost("/{id:guid}/end", EndAsync).WithMetadata(new AuthRequirement(AuthMode.AgentOptionalUser));
        api.MapPost("/{id:guid}/events", EventsAsync).WithMetadata(new AuthRequirement(AuthMode.Agent));
    }

    /// <summary><c>GET /sessions/current?pcId=</c>: the open session of the agent's own PC, or 204.</summary>
    private static async Task<IResult> CurrentAsync(HttpContext context, string? pcId, NpgsqlDataSource db, TimeProvider clock)
    {
        var pc = context.Features.GetRequiredFeature<AgentContext>().Pc;
        if (!Guid.TryParse(pcId, out var id))
        {
            throw ApiException.Validation("pcId", pcId is null ? "required" : "format");
        }

        if (id != pc.Id)
        {
            throw ApiException.Forbidden("pcMismatch", "pcId does not match the agent token");
        }

        await using var c = await db.OpenConnectionAsync();
        return await SessionService.OpenOfPcAsync(c, id) is { } open ? TypedResults.Ok(open.ToWire(clock.GetUtcNow())) : Results.NoContent();
    }

    private static async Task<IResult> CreateAsync(HttpContext context, [FromBody] JsonElement body, IdempotencyStore store, SessionService sessions)
    {
        var agent = context.Features.GetRequiredFeature<AgentContext>();
        var request = Api.Read<SessionCreateRequest>(body, "pcId", "userId", "tariffId", "prepaid");
        var replay = request.StartedAt is not null && request.ClientSessionId is not null;
        if (!replay)
        {
            // Online: the player of the token buys for themself. The replay ignores a missing, stale or foreign token.
            var user = context.Features.Get<UserContext>()
                ?? throw ApiException.Unauthorized("userToken", "X-User-Token required", context.Features.Get<UserTokenProblem>()?.Problem ?? "invalid");
            if (request.UserId != user.UserId)
            {
                throw ApiException.Forbidden("notOwner", "userId is not the signed-in player");
            }
        }

        if (request.PcId != agent.Pc.Id)
        {
            throw ApiException.Forbidden("pcMismatch", "pcId does not match the agent token");
        }

        var effects = new SessionEffects();
        var result = await store.ExecuteHttpAsync(context, Principal(agent), keyRequired: true, body, async (c, tx) =>
            new IdempotentResult(StatusCodes.Status201Created, JsonDefaults.ToElement(await sessions.CreateAsync(c, tx, agent.Pc, request, replay, effects))));
        await sessions.PublishAsync(effects);
        return result;
    }

    private static async Task<IResult> ExtendAsync(HttpContext context, Guid id, [FromBody] JsonElement body, IdempotencyStore store, SessionService sessions)
    {
        var agent = context.Features.GetRequiredFeature<AgentContext>();
        var user = context.Features.GetRequiredFeature<UserContext>();
        var request = Api.Read<SessionExtendRequest>(body, "minutes");
        if (request.Minutes is < 1 or > 1440)
        {
            throw ApiException.Validation("minutes", request.Minutes < 1 ? "min" : "max");
        }

        var effects = new SessionEffects();
        var result = await store.ExecuteHttpAsync(context, Principal(agent), keyRequired: true, body, async (c, tx) =>
            new IdempotentResult(StatusCodes.Status200OK, JsonDefaults.ToElement(await sessions.ExtendAsync(c, tx, id, user.UserId, agent.Pc.Id, request.Minutes, request.TariffId, effects))));
        await sessions.PublishAsync(effects);
        return result;
    }

    private static async Task<IResult> EndAsync(HttpContext context, Guid id, [FromBody] JsonElement body, SessionService sessions)
    {
        var report = Api.Read<SessionEndReport>(body, "reason", "secondsUsed");
        if (report.Reason == SessionEndReason.Unknown)
        {
            throw ApiException.Validation("reason", "enum");
        }

        if (report.SecondsUsed < 0)
        {
            throw ApiException.Validation("secondsUsed", "min");
        }

        return TypedResults.Ok(await sessions.EndAsync(id, context.Features.GetRequiredFeature<AgentContext>().Pc.Id, report));
    }

    /// <summary>
    /// <c>POST /sessions/{id}/events</c> (§5.12). The session is looked up by id or, for an offline session not yet
    /// replayed, by <c>clientSessionId</c> — before the key: an unknown one is 404 and the key is not stored, so the agent
    /// sends the batch again after the replay.
    /// </summary>
    private static async Task<IResult> EventsAsync(
        HttpContext context, Guid id, [FromBody] JsonElement body, IdempotencyStore store, SessionService sessions, NpgsqlDataSource db)
    {
        var agent = context.Features.GetRequiredFeature<AgentContext>();
        var events = ReadEvents(body);
        SessionRow? session;
        await using (var c = await db.OpenConnectionAsync())
        {
            session = await c.QueryFirstOrDefaultAsync<SessionRow>(
                $"SELECT {SessionRow.Columns} FROM sessions WHERE id = @id OR (pc_id = @pcId AND client_session_id = @id) ORDER BY id = @id DESC LIMIT 1",
                new { id, pcId = agent.Pc.Id });
        }

        if (session is null)
        {
            throw ApiException.NotFound("session");
        }

        if (session.PcId != agent.Pc.Id)
        {
            throw ApiException.Forbidden("pcMismatch", "The session belongs to another PC");
        }

        var effects = new SessionEffects();
        var result = await store.ExecuteHttpAsync(context, Principal(agent), keyRequired: true, body, async (c, tx) =>
        {
            await sessions.ApplyEventsAsync(c, tx, (await SessionService.LockAsync(c, tx, session.Id))!, events, effects);
            return new IdempotentResult(StatusCodes.Status204NoContent, null);
        });
        await sessions.PublishAsync(effects);
        return result;
    }

    /// <summary>The contract's names exactly: <c>Enum.TryParse</c> would also take "Locked", "paused " and "locked,unlocked" (= ended).</summary>
    private static readonly Dictionary<string, SessionEventType> EventTypes =
        Enum.GetValues<SessionEventType>().ToDictionary(t => JsonNamingPolicy.CamelCase.ConvertName(t.ToString()), StringComparer.Ordinal);

    /// <summary>The batch with the contract's field names in errors: <c>events</c> required|max, <c>events[i]</c> format, <c>type</c> enum, <c>at</c> required|format.</summary>
    private static List<SessionEvent> ReadEvents(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("events", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw ApiException.Validation("events", "required");
        }

        if (array.GetArrayLength() > SessionEventsBatch.MaxEvents)
        {
            throw ApiException.Validation("events", "max");
        }

        var events = new List<SessionEvent>();
        var i = 0;
        foreach (var e in array.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object)
            {
                throw ApiException.Validation($"events[{i}]", "format");
            }

            if (!e.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() is not { } name
                || !EventTypes.TryGetValue(name, out var kind))
            {
                throw ApiException.Validation("type", "enum");
            }

            if (!e.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.String || at.GetString() is not { Length: > 0 } text)
            {
                throw ApiException.Validation("at", "required");
            }

            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
            {
                throw ApiException.Validation("at", "format");
            }

            var sessionId = e.TryGetProperty("sessionId", out var sid) && sid.TryGetGuid(out var parsed) ? parsed : Guid.Empty;
            events.Add(new SessionEvent(sessionId, kind, when, e.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Null ? data.Clone() : null));
            i++;
        }

        return events;
    }

    private static string Principal(AgentContext agent) => "pc:" + agent.Pc.Id.ToString("D", CultureInfo.InvariantCulture);
}
