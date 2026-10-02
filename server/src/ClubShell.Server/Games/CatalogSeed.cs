using System.Text.Json;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Serialization;
using ClubShell.Server.Infrastructure;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Games;

/// <summary>
/// The games catalog from seed JSON (DESIGN §12 D-14: the contract has no games CRUD). <c>Catalog:GamesSeedPath</c> is a
/// JSON array of contract <c>Game</c> objects, optionally with <c>settingsPaths</c>. Every start upserts it by id into each
/// club; a game missing from the file is soft-deleted; any change bumps <c>catalog_version</c> (the heartbeat tells the
/// agents, <c>GET /games</c> answers a new ETag). No file: the catalog stays as it is. Games of <c>origin = 'club'</c> —
/// added by the owner, or a seed game the owner edited or deleted (<see cref="Admin.CatalogAdminEndpoints"/>) — are the
/// club's: the seed neither overwrites, restores nor deletes them.
/// </summary>
public static class CatalogSeed
{
    public static async Task<int> ApplyAsync(NpgsqlDataSource db, string path, TimeProvider clock)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        var games = JsonSerializer.Deserialize<List<Game>>(File.ReadAllText(path), ServerJson.Options)
            ?? throw new InvalidOperationException($"Games seed {path} is empty");
        var now = clock.GetUtcNow();
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var changed = 0;
        foreach (var clubId in await c.QueryAsync<Guid>("SELECT id FROM clubs FOR UPDATE", transaction: tx))
        {
            var rows = 0;
            foreach (var game in games)
            {
                // Local fields are the agent's (it overwrites them with its own scan); lastPlayedAt is per request.
                var data = ServerJson.Jsonb(JsonDefaults.Serialize(game with
                {
                    Installed = false, InstallPath = null, LastPlayedAt = null, SettingsPaths = null,
                }));
                rows += await c.ExecuteAsync(
                    """
                    INSERT INTO games (id, club_id, title, settings_paths, data, updated_at)
                    VALUES (@Id, @clubId, @Title, @paths, @data::jsonb, @now)
                    ON CONFLICT (id) DO UPDATE SET title = excluded.title, settings_paths = excluded.settings_paths,
                                                   data = excluded.data, updated_at = excluded.updated_at, deleted_at = NULL
                    WHERE games.club_id = excluded.club_id AND games.origin = 'seed' AND (games.data <> excluded.data OR games.deleted_at IS NOT NULL
                          OR games.settings_paths IS DISTINCT FROM excluded.settings_paths)
                    """,
                    new { game.Id, clubId, game.Title, paths = game.SettingsPaths?.ToArray(), data, now },
                    tx);
            }

            rows += await c.ExecuteAsync(
                "UPDATE games SET deleted_at = @now WHERE club_id = @clubId AND origin = 'seed' AND deleted_at IS NULL AND NOT (id = ANY(@ids))",
                new { clubId, now, ids = games.Select(g => g.Id).ToArray() },
                tx);
            if (rows > 0)
            {
                await c.ExecuteAsync("UPDATE clubs SET catalog_version = catalog_version + 1, updated_at = @now WHERE id = @clubId", new { clubId, now }, tx);
                changed++;
            }
        }

        await tx.CommitAsync();
        return changed;
    }
}
