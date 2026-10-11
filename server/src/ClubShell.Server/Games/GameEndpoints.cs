using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Serialization;
using ClubShell.Server.Agents;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Games;

/// <summary>
/// Games catalog (slice S3, DESIGN §4.2, §7.3): <c>getGames</c> (owner's order first, hidden left out, games that need
/// Vanguard left out on a PC whose last heartbeat says it cannot run (D-74), ETag
/// <c>"g&lt;catalog_version&gt;-&lt;hash(page, zone, player's lastPlayedAt, no Vanguard)&gt;"</c>, pages up to 1000), <c>getGame</c> and
/// <c>sendLaunchReport</c>. <c>X-User-Token</c> only fills <c>lastPlayedAt</c> — the newest successful launch of the
/// player — and an invalid one is ignored. <c>settingsPaths</c> never leaves the server.
/// ponytail: <c>zone</c> is echoed into the ETag but filters nothing (no per-zone catalog in the club settings yet), as the mock.
/// </summary>
public static class GameEndpoints
{
    public static readonly string[] Operations = ["getGames", "getGame", "sendLaunchReport"];

    public const int MaxPageSize = 1000;

    public static void MapGameEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/games");
        api.MapGet("", ListAsync).WithMetadata(new AuthRequirement(AuthMode.AgentOptionalUser));
        api.MapGet("/{id:guid}", GetAsync).WithMetadata(new AuthRequirement(AuthMode.AgentOptionalUser));
        api.MapPost("/{id:guid}/launch-report", LaunchReportAsync).WithMetadata(new AuthRequirement(AuthMode.Agent));
    }

    private static async Task<IResult> ListAsync(HttpContext context, string? zone, string? page, string? pageSize, NpgsqlDataSource db)
    {
        var pc = context.Features.GetRequiredFeature<AgentContext>().Pc;
        var userId = context.Features.Get<UserContext>()?.UserId;
        await using var c = await db.OpenConnectionAsync();

        // The PC's last heartbeat says Vanguard cannot run (vgk missing or not loaded): Riot games would only fail at launch
        // with antiCheatBlocked (D-74). No antiCheat in it (an older agent, no heartbeat yet) or a field the agent could not
        // read (null, left out) hides nothing. The club's policy may require Vanguard for every game with an anti-cheat
        // (anticheat.required, as the seed does): while it blocks on a violation, the agent's gate refuses those games too.
        var (version, catalog, noVanguard, vanguardRequired) = await c.QuerySingleAsync<(int, string?, bool, bool)>(
            """
            SELECT catalog_version, (settings -> 'catalog')::text,
                   coalesce((SELECT (p.last_heartbeat -> 'antiCheat' -> 'vanguardInstalled') = 'false'::jsonb
                                    OR (p.last_heartbeat -> 'antiCheat' -> 'vanguardLoaded') = 'false'::jsonb
                             FROM pcs p WHERE p.id = @PcId), false),
                   coalesce((policy -> 'anticheat' -> 'required') @> '["vanguard"]'::jsonb
                            AND (policy -> 'anticheat' -> 'blockOnViolation') IS DISTINCT FROM 'false'::jsonb, false)
            FROM clubs WHERE id = @ClubId
            """,
            new { pc.ClubId, PcId = pc.Id });
        var played = await LastPlayedAsync(c, userId, null);

        // No page and no pageSize: the whole catalog at once; only page: pages of 1000 (mock paginate(…, 1000)). The applied
        // page is part of the ETag: every page is its own representation, and so is each set without the Vanguard games
        // (left out of the hash otherwise, so the ETags of every other PC stay as they were).
        var whole = page is null && pageSize is null;
        var (p, size) = whole ? (1, 0) : Paging.Normalize(page, pageSize ?? "1000", MaxPageSize);
        var view = whole ? "all" : $"{p}x{size}";
        var lastPlayed = string.Join(',', played.OrderBy(g => g.Key).Select(g => $"{g.Key:N}={g.Value.ToUnixTimeMilliseconds()}"));
        var hiding = !noVanguard ? "" : vanguardRequired ? "|noVanguard|required" : "|noVanguard";
        var tag = $"g{version}-{Hash($"{view}|{zone}|{lastPlayed}{hiding}")}";
        if (AgentEndpoints.NotModified(context, tag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var settings = catalog is null ? null : JsonSerializer.Deserialize<CatalogSettings>(catalog, JsonSerializerOptions.Web);
        var hidden = settings?.Hidden?.ToHashSet() ?? [];
        var order = settings?.Order ?? [];
        var all = (await c.QueryAsync<(Guid Id, string Data)>("SELECT id, data::text FROM games WHERE club_id = @ClubId AND deleted_at IS NULL", new { pc.ClubId }))
            .Where(g => !hidden.Contains(g.Id))
            .Select(g => JsonDefaults.Deserialize<Game>(g.Data)! with { LastPlayedAt = played.TryGetValue(g.Id, out var at) ? at : null })
            .Where(g => !noVanguard || !NeedsVanguard(EffectiveAntiCheat(g), vanguardRequired))
            .OrderBy(g => Array.IndexOf(order, g.Id) is var i and >= 0 ? i : int.MaxValue)
            .ThenBy(g => g.Title, StringComparer.Ordinal)
            .ThenBy(g => g.Id)
            .ToList();

        size = whole ? all.Count : size;
        IReadOnlyList<Game> items = all.Skip((int)Math.Min((long)(p - 1) * size, int.MaxValue)).Take(size).ToList();
        return TypedResults.Ok(new GamesListResponse(items, all.Count, p, size, version.ToString(CultureInfo.InvariantCulture)));
    }

    private static async Task<IResult> GetAsync(HttpContext context, Guid id, NpgsqlDataSource db)
    {
        var pc = context.Features.GetRequiredFeature<AgentContext>().Pc;
        await using var c = await db.OpenConnectionAsync();
        var data = await c.QuerySingleOrDefaultAsync<string>(
            "SELECT data::text FROM games WHERE id = @id AND club_id = @ClubId AND deleted_at IS NULL", new { id, pc.ClubId })
            ?? throw ApiException.NotFound("game");
        var played = await LastPlayedAsync(c, context.Features.Get<UserContext>()?.UserId, id);
        return TypedResults.Ok(JsonDefaults.Deserialize<Game>(data)! with { LastPlayedAt = played.TryGetValue(id, out var at) ? at : null });
    }

    /// <summary>
    /// <c>POST /games/{id}/launch-report</c>: stored once per (session, phase, <c>result.startedAt</c>) — the agent repeats
    /// undelivered reports from its offline queue. A soft-deleted game still takes reports of launches made before.
    /// Session and player are kept only when the session is this PC's and the body names its player; otherwise the report
    /// is stored without them (still 204: the contract has no error for it), so a PC can neither set another player's
    /// <c>lastPlayedAt</c> nor take the dedup slot of another PC's report.
    /// </summary>
    private static async Task<IResult> LaunchReportAsync(HttpContext context, Guid id, [FromBody] JsonElement body, NpgsqlDataSource db, TimeProvider clock)
    {
        var pc = context.Features.GetRequiredFeature<AgentContext>().Pc;

        // phase has no Unknown fallback: the binder would answer an unknown value with format, the contract wants enum (§2.4).
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("phase", out var phase) && phase.ValueKind == JsonValueKind.String
            && phase.GetString() is not ("launch" or "exit"))
        {
            throw ApiException.Validation("phase", "enum");
        }

        var report = Api.Read<LaunchReport>(
            body, "sessionId", "userId", "result", "result.ok", "result.startedAt", "durationMs", "launcher", "antiCheat", "antiCheat.kind", "antiCheat.ok", "phase");
        if (report.Launcher == LauncherType.Unknown || report.AntiCheat.Kind == AntiCheatKind.Unknown)
        {
            throw ApiException.Validation(report.Launcher == LauncherType.Unknown ? "launcher" : "antiCheat.kind", "enum");
        }

        if (report.DurationMs < 0 || report.PlayedSec < 0)
        {
            throw ApiException.Validation(report.DurationMs < 0 ? "durationMs" : "playedSec", "min");
        }

        await using var c = await db.OpenConnectionAsync();
        if (!await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM games WHERE id = @id AND club_id = @ClubId)", new { id, pc.ClubId }))
        {
            throw ApiException.NotFound("game");
        }

        var own = await c.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM sessions WHERE id = @SessionId AND pc_id = @pcId AND user_id = @UserId)",
            new { report.SessionId, pcId = pc.Id, report.UserId });
        var now = clock.GetUtcNow();
        await c.ExecuteAsync(
            """
            INSERT INTO launch_reports (id, club_id, pc_id, game_id, user_id, session_id, phase, started_at, data, created_at)
            VALUES (@rowId, @ClubId, @pcId, @id, @userId, @sessionId, @phase, @startedAt, @data::jsonb, @now)
            ON CONFLICT (session_id, phase, started_at) DO NOTHING
            """,
            new
            {
                rowId = Guid.CreateVersion7(now), pc.ClubId, pcId = pc.Id, id, userId = own ? report.UserId : (Guid?)null,
                sessionId = own ? report.SessionId : (Guid?)null,
                phase = report.Phase == LaunchReportPhase.Exit ? "exit" : "launch", startedAt = report.Result.StartedAt,
                data = ServerJson.Jsonb(body.GetRawText()), now,
            });
        return Results.NoContent();
    }

    /// <summary>Game id → the player's newest successful launch (all games, or only <paramref name="gameId"/>).</summary>
    private static async Task<Dictionary<Guid, DateTimeOffset>> LastPlayedAsync(NpgsqlConnection c, Guid? userId, Guid? gameId)
    {
        if (userId is null)
        {
            return [];
        }

        var rows = await c.QueryAsync<(Guid GameId, DateTimeOffset At)>(
            """
            SELECT game_id, max(started_at) FROM launch_reports
            WHERE user_id = @userId AND phase = 'launch' AND (data -> 'result' ->> 'ok')::boolean AND (@gameId IS NULL OR game_id = @gameId)
            GROUP BY game_id
            """,
            new { userId, gameId });
        return rows.ToDictionary(r => r.GameId, r => r.At);
    }

    /// <summary>
    /// The anti-cheat the agent's launch gate checks for <paramref name="game"/> — the agent's
    /// <c>GameLaunchService.EffectiveAntiCheat</c>: the catalog's, except that a Riot game it did not tag is Vanguard.
    /// </summary>
    private static AntiCheatKind EffectiveAntiCheat(Game game) =>
        game.AntiCheat == AntiCheatKind.None && game.Launcher == LauncherType.Riot ? AntiCheatKind.Vanguard : game.AntiCheat;

    /// <summary>
    /// Whether a PC without a loaded vgk cannot start a game whose effective anti-cheat is <paramref name="kind"/>: a
    /// Vanguard game never starts there (Riot's own client refuses it), and any other game with an anti-cheat is refused
    /// by the agent's gate when the club's policy requires Vanguard and blocks on a violation
    /// (<c>AntiCheatMonitor.CheckForLaunchAsync</c> checks <c>policy.required ∪ {kind}</c>). A game without one starts:
    /// the gate is not called for it.
    /// </summary>
    private static bool NeedsVanguard(AntiCheatKind kind, bool vanguardRequired) =>
        kind == AntiCheatKind.Vanguard || (kind != AntiCheatKind.None && vanguardRequired);

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    /// <summary><c>clubs.settings.catalog</c> (<c>AdminClubSettings.catalog</c>, written by S5).</summary>
    private sealed class CatalogSettings
    {
        public Guid[]? Order { get; init; }
        public Guid[]? Hidden { get; init; }
    }
}
