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
/// Games catalog (slice S3, DESIGN §4.2, §7.3): <c>getGames</c> (owner's order first, hidden left out, ETag
/// <c>"g&lt;catalog_version&gt;-&lt;hash(zone, player's lastPlayedAt)&gt;"</c>, pages up to 1000), <c>getGame</c> and
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
        var (version, catalog) = await c.QuerySingleAsync<(int, string?)>(
            "SELECT catalog_version, (settings -> 'catalog')::text FROM clubs WHERE id = @ClubId", new { pc.ClubId });
        var played = await LastPlayedAsync(c, userId, null);
        var tag = $"g{version}-{Hash(zone + "|" + string.Join(',', played.OrderBy(p => p.Key).Select(p => $"{p.Key:N}={p.Value.ToUnixTimeMilliseconds()}")))}";
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
            .OrderBy(g => Array.IndexOf(order, g.Id) is var i and >= 0 ? i : int.MaxValue)
            .ThenBy(g => g.Title, StringComparer.Ordinal)
            .ThenBy(g => g.Id)
            .ToList();

        // No page and no pageSize: the whole catalog at once; only page: pages of 1000 (mock paginate(…, 1000)).
        var (p, size) = page is null && pageSize is null ? (1, all.Count) : Paging.Normalize(page, pageSize ?? "1000", MaxPageSize);
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
    /// </summary>
    private static async Task<IResult> LaunchReportAsync(HttpContext context, Guid id, [FromBody] JsonElement body, NpgsqlDataSource db, TimeProvider clock)
    {
        var pc = context.Features.GetRequiredFeature<AgentContext>().Pc;
        var report = Api.Read<LaunchReport>(body, "sessionId", "userId", "result", "durationMs", "launcher", "antiCheat", "phase");
        if (!body.GetProperty("result").TryGetProperty("startedAt", out _))
        {
            throw ApiException.Validation("result.startedAt", "required");
        }

        if (report.Launcher == LauncherType.Unknown)
        {
            throw ApiException.Validation("launcher", "enum");
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

        var now = clock.GetUtcNow();
        await c.ExecuteAsync(
            """
            INSERT INTO launch_reports (id, club_id, pc_id, game_id, user_id, session_id, phase, started_at, data, created_at)
            VALUES (@rowId, @ClubId, @pcId, @id, @UserId, @SessionId, @phase, @startedAt, @data::jsonb, @now)
            ON CONFLICT (session_id, phase, started_at) DO NOTHING
            """,
            new
            {
                rowId = Guid.CreateVersion7(now), pc.ClubId, pcId = pc.Id, id, report.UserId, report.SessionId,
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

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    /// <summary><c>clubs.settings.catalog</c> (<c>AdminClubSettings.catalog</c>, written by S5).</summary>
    private sealed class CatalogSettings
    {
        public Guid[]? Order { get; init; }
        public Guid[]? Hidden { get; init; }
    }
}
