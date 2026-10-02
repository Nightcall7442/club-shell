using System.Text.Json;
using System.Text.RegularExpressions;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Serialization;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// <c>AdminGame</c>, plus what the console's catalog page reads and edits beyond the contract: <c>settingsPaths</c> (S5),
/// how the game starts (<c>launcherAppId</c>, <c>exePath</c>, <c>args</c>), <c>description</c>, and <c>custom</c> — the
/// club's own game (added or changed by the owner), which the seed no longer touches.
/// </summary>
public sealed record AdminGame(
    Guid Id, string Title, string? CoverUrl, bool Installed, string Launcher, IReadOnlyList<string> Category, bool Hidden, bool Featured,
    IReadOnlyList<string> SettingsPaths, string? LauncherAppId = null, string? ExePath = null, string? Args = null, string Description = "",
    bool Custom = false);

public sealed record AdminGameList(IReadOnlyList<AdminGame> Items, IReadOnlyList<Guid> Order);

public sealed record AdminGameResponse(AdminGame Game);

/// <summary>
/// The catalog editor (slice S5): <c>adminGames</c> — every live game of the club, hidden ones too, with the owner's
/// <c>catalog</c> marks and order (which <c>PATCH /admin/club</c> edits); beyond the contract <c>PATCH /admin/games/{id}</c>
/// <c>{settingsPaths}</c> for the owner — where a game keeps a player's own settings (at most 10 non-empty paths of up to
/// 260 characters, trimmed, as the mock; empty = none). Also beyond the contract, the owner's own games: <c>POST</c> adds
/// one, <c>PUT /{id}</c> replaces how it starts and looks, <c>DELETE /{id}</c> removes it (soft; unknown or gone = 200, as
/// tariffs). Every change marks the game <c>origin = 'club'</c>, so the seed of the next start leaves it as the owner left
/// it (<see cref="Games.CatalogSeed"/>), bumps <c>catalog_version</c> and sends <c>refreshConfig {games:true}</c> to the
/// connected PCs (§5.9).
/// </summary>
public static partial class CatalogAdminEndpoints
{
    public static readonly string[] Operations = ["adminGames"];

    private const string SteamCdn = "https://cdn.cloudflare.steamstatic.com/steam/apps/";

    /// <summary>Launchers the owner may pick, by their contract names (<see cref="LauncherType"/>).</summary>
    private static readonly Dictionary<string, LauncherType> Launchers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["exe"] = LauncherType.Exe, ["steam"] = LauncherType.Steam, ["epic"] = LauncherType.Epic, ["battleNet"] = LauncherType.BattleNet,
        ["riot"] = LauncherType.Riot, ["ea"] = LauncherType.Ea, ["ubisoft"] = LauncherType.Ubisoft,
    };

    public static void MapCatalogAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/admin/games");
        api.MapGet("", ListAsync).WithMetadata(new AuthRequirement(AuthMode.Staff));
        api.MapPost("", AddAsync).WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
        api.MapPut("/{id}", SaveAsync).WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
        api.MapPatch("/{id}", SettingsPathsAsync).WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
        api.MapDelete("/{id}", DeleteAsync).WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
    }

    private static async Task<IResult> ListAsync(HttpContext context, NpgsqlDataSource db)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        await using var c = await db.OpenConnectionAsync();
        var catalog = await c.ExecuteScalarAsync<string?>("SELECT (settings -> 'catalog')::text FROM clubs WHERE id = @ClubId", new { staff.ClubId });
        var (order, hidden, featured) = Marks(catalog);
        var items = (await c.QueryAsync<GameRow>(
                $"SELECT {GameRow.Columns} FROM games WHERE club_id = @ClubId AND deleted_at IS NULL", new { staff.ClubId }))
            .Select(g => g.ToWire(hidden.Contains(g.Id), featured.Contains(g.Id)))
            .OrderBy(g => order.IndexOf(g.Id) is var i and >= 0 ? i : int.MaxValue)
            .ThenBy(g => g.Title, StringComparer.Ordinal)
            .ThenBy(g => g.Id)
            .ToList();
        return AdminJson.Ok(new AdminGameList(items, order));
    }

    /// <summary>A new game of the club; the id is the server's.</summary>
    private static async Task<IResult> AddAsync(
        HttpContext context, [FromBody] JsonElement body, NpgsqlDataSource db, CommandDispatcher commands, AgentSocketHub hub, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var input = Parse(body);
        IReadOnlyList<(Guid PcId, ServerCommandEnvelope Command)> queued;
        GameRow row;
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var now = clock.GetUtcNow();
            var game = input.Apply(new Game(
                Guid.CreateVersion7(now), input.Title, input.Launcher, null, null, null, null, false, [], [], "", null, null, "", 0, 0, null, false,
                AntiCheatKind.None, null, 0));
            row = await c.QuerySingleAsync<GameRow>(
                $"""
                INSERT INTO games (id, club_id, title, data, updated_at, origin)
                VALUES (@Id, @ClubId, @Title, @data::jsonb, @now, 'club')
                RETURNING {GameRow.Columns}
                """,
                new { game.Id, staff.ClubId, game.Title, data = Data(game), now }, tx);
            await Audit.WriteAsync(c, tx, staff, now, "gameAdd", detail: game.Title, meta: new { gameId = game.Id, launcher = Name(game.Launcher) });
            queued = await ChangedAsync(c, tx, commands, hub, staff, now);
            await tx.CommitAsync();
        }

        await ConfigRefresh.SendAsync(commands, queued);
        return AdminJson.Ok(new AdminGameResponse(row.ToWire(false, false)));
    }

    /// <summary>
    /// Replaces how a live game starts and looks (<see cref="GameInput"/>); the rest of the seed's card (tags, popularity,
    /// age rating, anti-cheat…) stays. A seed game becomes the club's.
    /// </summary>
    private static async Task<IResult> SaveAsync(
        HttpContext context, string id, [FromBody] JsonElement body, NpgsqlDataSource db, CommandDispatcher commands, AgentSocketHub hub, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        var input = Parse(body);
        var gameId = Guid.TryParse(id, out var parsed) ? parsed : throw ApiException.NotFound("game");
        IReadOnlyList<(Guid PcId, ServerCommandEnvelope Command)> queued;
        GameRow row;
        string? catalog;
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var now = clock.GetUtcNow();
            var data = await c.QuerySingleOrDefaultAsync<string>(
                "SELECT data::text FROM games WHERE id = @gameId AND club_id = @ClubId AND deleted_at IS NULL FOR UPDATE", new { gameId, staff.ClubId }, tx)
                ?? throw ApiException.NotFound("game");
            var game = input.Apply(JsonDefaults.Deserialize<Game>(data)! with { Id = gameId });
            row = await c.QuerySingleAsync<GameRow>(
                $"""
                UPDATE games SET title = @Title, data = @data::jsonb, origin = 'club', updated_at = @now
                WHERE id = @gameId AND club_id = @ClubId
                RETURNING {GameRow.Columns}
                """,
                new { game.Title, data = Data(game), now, gameId, staff.ClubId }, tx);
            await Audit.WriteAsync(c, tx, staff, now, "gameSave", detail: game.Title, meta: new { gameId, launcher = Name(game.Launcher) });
            queued = await ChangedAsync(c, tx, commands, hub, staff, now);
            catalog = await c.ExecuteScalarAsync<string?>("SELECT (settings -> 'catalog')::text FROM clubs WHERE id = @ClubId", new { staff.ClubId }, tx);
            await tx.CommitAsync();
        }

        await ConfigRefresh.SendAsync(commands, queued);
        var (_, hidden, featured) = Marks(catalog);
        return AdminJson.Ok(new AdminGameResponse(row.ToWire(hidden.Contains(gameId), featured.Contains(gameId))));
    }

    /// <summary>Soft delete (launch reports keep their game); a seed game stays deleted after the next start.</summary>
    private static async Task<IResult> DeleteAsync(
        HttpContext context, string id, NpgsqlDataSource db, CommandDispatcher commands, AgentSocketHub hub, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        if (!Guid.TryParse(id, out var gameId))
        {
            return AdminJson.Ok(AdminJson.OkBody);
        }

        IReadOnlyList<(Guid PcId, ServerCommandEnvelope Command)> queued = [];
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var now = clock.GetUtcNow();
            var title = await c.QuerySingleOrDefaultAsync<string>(
                """
                UPDATE games SET deleted_at = @now, origin = 'club', updated_at = @now
                WHERE id = @gameId AND club_id = @ClubId AND deleted_at IS NULL
                RETURNING title
                """,
                new { gameId, staff.ClubId, now }, tx);
            if (title is not null)
            {
                await Audit.WriteAsync(c, tx, staff, now, "gameDelete", detail: title, meta: new { gameId });
                queued = await ChangedAsync(c, tx, commands, hub, staff, now);
            }

            await tx.CommitAsync();
        }

        await ConfigRefresh.SendAsync(commands, queued);
        return AdminJson.Ok(AdminJson.OkBody);
    }

    private static async Task<IResult> SettingsPathsAsync(
        HttpContext context, string id, [FromBody] JsonElement body, NpgsqlDataSource db, CommandDispatcher commands, AgentSocketHub hub, TimeProvider clock)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw ApiException.Validation("body", "schema", "JSON object expected");
        }

        if (!body.TryGetProperty("settingsPaths", out var raw) || raw.ValueKind == JsonValueKind.Null)
        {
            throw ApiException.Validation("settingsPaths", "required");
        }

        if (raw.ValueKind != JsonValueKind.Array)
        {
            throw ApiException.Validation("settingsPaths", "format");
        }

        string[] paths = [.. raw.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!.Trim())
            .Where(p => p.Length is > 0 and <= 260).Take(10)];
        var gameId = Guid.TryParse(id, out var parsed) ? parsed : throw ApiException.NotFound("game");
        IReadOnlyList<(Guid PcId, ServerCommandEnvelope Command)> queued;
        await using (var c = await db.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var now = clock.GetUtcNow();
            var title = await c.QuerySingleOrDefaultAsync<string>(
                """
                UPDATE games SET settings_paths = @stored, origin = 'club', updated_at = @now
                WHERE id = @gameId AND club_id = @ClubId AND deleted_at IS NULL
                RETURNING title
                """,
                new { gameId, staff.ClubId, stored = paths.Length > 0 ? paths : null, now }, tx)
                ?? throw ApiException.NotFound("game");
            await Audit.WriteAsync(c, tx, staff, now, "gameSettingsPaths", detail: title, meta: new { gameId, paths = paths.Length });
            queued = await ChangedAsync(c, tx, commands, hub, staff, now);
            await tx.CommitAsync();
        }

        await ConfigRefresh.SendAsync(commands, queued);
        return AdminJson.Ok(new { settingsPaths = paths });
    }

    /// <summary>A catalog change: <c>catalog_version</c> moves (heartbeat, ETag) and the connected PCs reload the games.</summary>
    private static async Task<IReadOnlyList<(Guid PcId, ServerCommandEnvelope Command)>> ChangedAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, CommandDispatcher commands, AgentSocketHub hub, StaffContext staff, DateTimeOffset now)
    {
        await c.ExecuteAsync("UPDATE clubs SET catalog_version = catalog_version + 1, updated_at = @now WHERE id = @ClubId", new { staff.ClubId, now }, tx);
        return await ConfigRefresh.QueueAsync(c, tx, commands, hub, staff.ClubId, new RefreshConfigCommand(Games: true), staff.StaffId);
    }

    /// <summary>The stored card: the agent's local fields and <c>settingsPaths</c> (a column) never go into <c>data</c>.</summary>
    private static string Data(Game game) =>
        ServerJson.Jsonb(JsonDefaults.Serialize(game with { Installed = false, InstallPath = null, LastPlayedAt = null, SettingsPaths = null }));

    private static string Name(LauncherType launcher) => Launchers.First(l => l.Value == launcher).Key;

    /// <summary>
    /// The owner's game card: <c>title</c> (up to 100) and <c>launcher</c> are required. An <c>exe</c> game needs the full
    /// path of its <c>.exe</c> on the PCs (<c>C:\…</c> or <c>\\server\…</c>, quotes around it dropped); any other launcher needs its
    /// <c>launcherAppId</c> (Steam: the number from the store link) and runs no exe of its own. <c>coverUrl</c> is an
    /// http(s) link; without one a Steam game gets its store cover. <c>category</c>: up to 5 keys as the shell knows them
    /// (<c>shooter</c>, <c>moba</c>, …). Empty texts count as absent.
    /// </summary>
    private static GameInput Parse(JsonElement body)
    {
        var r = Api.Read<GameInputBody>(body, "title", "launcher");
        var title = AdminInput.Text(r.Title?.Trim(), "title", 100);
        var launcher = Launchers.TryGetValue(r.Launcher!.Trim(), out var known) ? known : throw ApiException.Validation("launcher", "enum");
        string? exePath = null, appId = null;
        if (launcher == LauncherType.Exe)
        {
            // Explorer's "Copy as path" wraps the path in quotes.
            exePath = AdminInput.Text(Blank(r.ExePath?.Trim().Trim('"')), "exePath", 260);
            if (!ExePath().IsMatch(exePath))
            {
                throw ApiException.Validation("exePath", "format");
            }
        }
        else
        {
            appId = AdminInput.Text(Blank(r.LauncherAppId), "launcherAppId", 100);
            if (launcher == LauncherType.Steam && !SteamAppId().IsMatch(appId))
            {
                throw ApiException.Validation("launcherAppId", "format");
            }
        }

        var cover = AdminInput.OptionalText(Blank(r.CoverUrl), "coverUrl", 2000);
        if (cover is not null && !(Uri.TryCreate(cover, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)))
        {
            throw ApiException.Validation("coverUrl", "format");
        }

        var category = (r.Category ?? []).Select(x => x?.Trim() ?? "").Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (category.Count > 5)
        {
            throw ApiException.Validation("category", "max");
        }

        if (category.Any(x => !CategoryKey().IsMatch(x)))
        {
            throw ApiException.Validation("category", "format");
        }

        return new GameInput(
            title, launcher, appId, exePath, AdminInput.OptionalText(Blank(r.Args), "args", 500), cover, category,
            AdminInput.OptionalText(Blank(r.Description), "description", 1000) ?? "");
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static (List<Guid> Order, HashSet<Guid> Hidden, HashSet<Guid> Featured) Marks(string? catalog)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(catalog) ? "{}" : catalog);
        List<Guid> Ids(string name) =>
            doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
                ? [.. list.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String && Guid.TryParse(x.GetString(), out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty)]
                : [];
        return (Ids("order"), [.. Ids("hidden")], [.. Ids("featured")]);
    }

    /// <summary>A full Windows path to an <c>.exe</c>: a drive (<c>G:\</c>) or a share (<c>\\nas\games\</c>).</summary>
    [GeneratedRegex(@"^(?:[A-Za-z]:\\|\\\\[^\\/:*?""<>|]+\\[^\\/:*?""<>|]+\\)[^/:*?""<>|]*\.exe$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ExePath();

    [GeneratedRegex("^[0-9]{1,10}$", RegexOptions.CultureInvariant)]
    private static partial Regex SteamAppId();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex CategoryKey();

    /// <summary>The request body as sent; <see cref="Parse"/> checks it.</summary>
    private sealed record GameInputBody(
        string? Title, string? Launcher, string? LauncherAppId, string? ExePath, string? Args, string? CoverUrl, string?[]? Category, string? Description);

    private sealed record GameInput(
        string Title, LauncherType Launcher, string? LauncherAppId, string? ExePath, string? Args, string? CoverUrl, List<string> Category, string Description)
    {
        /// <summary>
        /// <paramref name="game"/> started and shown as this input says. A Steam game without a cover gets its store art; a
        /// Steam game's wide art follows its app id, another launcher keeps it only while its launcher and id stay the same.
        /// </summary>
        public Game Apply(Game game)
        {
            var steam = Launcher == LauncherType.Steam;
            var same = game.Launcher == Launcher && string.Equals(game.LauncherAppId, LauncherAppId, StringComparison.Ordinal);
            return game with
            {
                Title = Title,
                Launcher = Launcher,
                LauncherAppId = LauncherAppId,
                ExePath = ExePath,
                Args = Args,
                CoverUrl = CoverUrl ?? (steam ? $"{SteamCdn}{LauncherAppId}/library_600x900.jpg" : ""),
                HeroUrl = steam ? $"{SteamCdn}{LauncherAppId}/library_hero.jpg" : same ? game.HeroUrl : null,
                Category = Category,
                Description = Description,
            };
        }
    }

    /// <summary>A <c>games</c> row (properties: Dapper hands <c>text[]</c> to a constructor as <see cref="Array"/>).</summary>
    private sealed class GameRow
    {
        public const string Columns = "id, title, data::text AS data, settings_paths, origin";

        public Guid Id { get; init; }

        public string Title { get; init; } = "";

        public string Data { get; init; } = "{}";

        public string[]? SettingsPaths { get; init; }

        public string Origin { get; init; } = "seed";

        public AdminGame ToWire(bool hidden, bool featured)
        {
            using var doc = JsonDocument.Parse(Data);
            var data = doc.RootElement;
            string? Text(string name) =>
                data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
            return new AdminGame(
                Id, Title, Text("coverUrl"),
                data.TryGetProperty("installed", out var installed) && installed.ValueKind == JsonValueKind.True,
                Text("launcher") ?? "exe",
                data.TryGetProperty("category", out var category) && category.ValueKind == JsonValueKind.Array
                    ? [.. category.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
                    : [],
                hidden, featured, SettingsPaths ?? [], Text("launcherAppId"), Text("exePath"), Text("args"), Text("description") ?? "", Origin == "club");
        }
    }
}
