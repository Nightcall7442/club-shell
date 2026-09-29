using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using ClubShell.Server.Realtime;
using Dapper;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary><c>AdminGame</c>, plus <c>settingsPaths</c> the console's catalog page reads and edits (beyond the contract, S5).</summary>
public sealed record AdminGame(
    Guid Id, string Title, string? CoverUrl, bool Installed, string Launcher, IReadOnlyList<string> Category, bool Hidden, bool Featured,
    IReadOnlyList<string> SettingsPaths);

public sealed record AdminGameList(IReadOnlyList<AdminGame> Items, IReadOnlyList<Guid> Order);

/// <summary>
/// The catalog editor (slice S5): <c>adminGames</c> — every live game of the club, hidden ones too, with the owner's
/// <c>catalog</c> marks and order (which <c>PATCH /admin/club</c> edits); beyond the contract <c>PATCH /admin/games/{id}</c>
/// <c>{settingsPaths}</c> for the owner — where a game keeps a player's own settings (at most 10 non-empty paths of up to
/// 260 characters, trimmed, as the mock; empty = none), which bumps <c>catalog_version</c> and sends <c>refreshConfig
/// {games:true}</c> to the connected PCs (§5.9). Games come from the seed (D-14): there is no create or delete.
/// </summary>
public static class CatalogAdminEndpoints
{
    public static readonly string[] Operations = ["adminGames"];

    public static void MapCatalogAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1/admin/games");
        api.MapGet("", ListAsync).WithMetadata(new AuthRequirement(AuthMode.Staff));
        api.MapPatch("/{id}", SettingsPathsAsync).WithMetadata(new AuthRequirement(AuthMode.Staff, OwnerOnly: true));
    }

    private static async Task<IResult> ListAsync(HttpContext context, NpgsqlDataSource db)
    {
        var staff = context.Features.GetRequiredFeature<StaffContext>();
        await using var c = await db.OpenConnectionAsync();
        var catalog = await c.ExecuteScalarAsync<string?>("SELECT (settings -> 'catalog')::text FROM clubs WHERE id = @ClubId", new { staff.ClubId });
        var (order, hidden, featured) = Marks(catalog);
        var items = (await c.QueryAsync<(Guid Id, string Title, string Data, string[]? SettingsPaths)>(
                "SELECT id, title, data::text, settings_paths FROM games WHERE club_id = @ClubId AND deleted_at IS NULL", new { staff.ClubId }))
            .Select(g =>
            {
                using var doc = JsonDocument.Parse(g.Data);
                var data = doc.RootElement;
                return new AdminGame(
                    g.Id, g.Title,
                    data.TryGetProperty("coverUrl", out var cover) && cover.ValueKind == JsonValueKind.String && cover.GetString() is { Length: > 0 } url ? url : null,
                    data.TryGetProperty("installed", out var installed) && installed.ValueKind == JsonValueKind.True,
                    data.TryGetProperty("launcher", out var launcher) && launcher.ValueKind == JsonValueKind.String ? launcher.GetString()! : "exe",
                    data.TryGetProperty("category", out var category) && category.ValueKind == JsonValueKind.Array
                        ? [.. category.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
                        : [],
                    hidden.Contains(g.Id), featured.Contains(g.Id), g.SettingsPaths ?? []);
            })
            .OrderBy(g => order.IndexOf(g.Id) is var i and >= 0 ? i : int.MaxValue)
            .ThenBy(g => g.Title, StringComparer.Ordinal)
            .ThenBy(g => g.Id)
            .ToList();
        return AdminJson.Ok(new AdminGameList(items, order));
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
                "UPDATE games SET settings_paths = @stored, updated_at = @now WHERE id = @gameId AND club_id = @ClubId AND deleted_at IS NULL RETURNING title",
                new { gameId, staff.ClubId, stored = paths.Length > 0 ? paths : null, now }, tx)
                ?? throw ApiException.NotFound("game");
            await c.ExecuteAsync("UPDATE clubs SET catalog_version = catalog_version + 1, updated_at = @now WHERE id = @ClubId", new { staff.ClubId, now }, tx);
            await Audit.WriteAsync(c, tx, staff, now, "gameSettingsPaths", detail: title, meta: new { gameId, paths = paths.Length });
            queued = await ConfigRefresh.QueueAsync(c, tx, commands, hub, staff.ClubId, new RefreshConfigCommand(Games: true), staff.StaffId);
            await tx.CommitAsync();
        }

        await ConfigRefresh.SendAsync(commands, queued);
        return AdminJson.Ok(new { settingsPaths = paths });
    }

    private static (List<Guid> Order, HashSet<Guid> Hidden, HashSet<Guid> Featured) Marks(string? catalog)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(catalog) ? "{}" : catalog);
        List<Guid> Ids(string name) =>
            doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
                ? [.. list.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String && Guid.TryParse(x.GetString(), out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty)]
                : [];
        return (Ids("order"), [.. Ids("hidden")], [.. Ids("featured")]);
    }
}
