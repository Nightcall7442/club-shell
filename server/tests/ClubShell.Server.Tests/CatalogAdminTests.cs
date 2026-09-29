using System.Text.Json;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// The catalog editor (slice S5): <c>adminGames</c> lists every game with the owner's <c>catalog</c> marks and order, and
/// beyond the contract <c>PATCH /admin/games/{id}</c> sets where a game keeps player settings (owner only; trimmed, empty
/// and over-long paths dropped, at most 10), bumping <c>catalog_version</c>.
/// </summary>
public sealed class CatalogAdminTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Games_carry_the_catalog_marks_and_the_owner_sets_settings_paths()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var cashier = await LoginAsync(server, CashierPin);
        var (cs2, rust, dota) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        foreach (var (id, title, launcher) in new[] { (cs2, "Counter-Strike 2", "steam"), (rust, "Rust", "steam"), (dota, "Dota 2", "steam") })
        {
            await Players.ExecuteAsync(server,
                """
                INSERT INTO games (id, club_id, title, settings_paths, data, updated_at)
                SELECT @id, c.id, @title, CASE WHEN @title = 'Counter-Strike 2' THEN ARRAY['%USERPROFILE%\cs2'] END,
                       jsonb_build_object('id', @id, 'title', @title, 'launcher', @launcher, 'installed', true, 'category', jsonb_build_array('shooter'),
                                          'coverUrl', 'https://cdn.example.com/' || @title || '.jpg'),
                       now()
                FROM clubs c
                """,
                new { id, title, launcher });
        }

        await ExpectAsync(server, 200, HttpMethod.Patch, "/club", owner, new { catalog = new { order = new[] { dota, cs2 }, hidden = new[] { rust }, featured = new[] { dota } } });
        var list = await ExpectAsync(server, 200, HttpMethod.Get, "/games", cashier);
        Assert.Equal([dota, cs2, rust], list.GetProperty("items").EnumerateArray().Select(g => g.GetProperty("id").GetGuid()));
        Assert.Equal([dota, cs2], list.GetProperty("order").EnumerateArray().Select(g => g.GetGuid()));
        var first = list.GetProperty("items")[0];
        Assert.Equal((true, false, "steam", true), (first.GetProperty("featured").GetBoolean(), first.GetProperty("hidden").GetBoolean(),
            first.GetProperty("launcher").GetString(), first.GetProperty("installed").GetBoolean()));
        Assert.True(list.GetProperty("items")[2].GetProperty("hidden").GetBoolean());
        Assert.Equal(["%USERPROFILE%\\cs2"], list.GetProperty("items")[1].GetProperty("settingsPaths").EnumerateArray().Select(p => p.GetString()));

        // Beyond the contract, so a client without the response validator.
        using var raw = server.CreateDefaultClient();
        async Task<(int, JsonElement)> PatchAsync(string token, string id, object body)
        {
            using var response = await raw.SendAsync(Request(HttpMethod.Patch, $"/games/{id}", token, body));
            return ((int)response.StatusCode, await Players.ReadAsync(response, (int)response.StatusCode));
        }

        var version = await Players.ScalarAsync<int>(server, "SELECT catalog_version FROM clubs");
        var (status, body) = await PatchAsync(owner, rust.ToString(), new
        {
            settingsPaths = new object[] { "  %APPDATA%\\Rust\\cfg  ", "", 42, new string('x', 261), "a", "b", "c", "d", "e", "f", "g", "h", "i", "j" },
        });
        Assert.Equal(200, status);
        Assert.Equal(["%APPDATA%\\Rust\\cfg", "a", "b", "c", "d", "e", "f", "g", "h", "i"], body.GetProperty("settingsPaths").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(version + 1, await Players.ScalarAsync<int>(server, "SELECT catalog_version FROM clubs"));
        (status, body) = await PatchAsync(owner, cs2.ToString(), new { settingsPaths = Array.Empty<string>() });
        Assert.Equal((200, 0), (status, body.GetProperty("settingsPaths").GetArrayLength()));
        Assert.True(await Players.ScalarAsync<bool>(server, "SELECT settings_paths IS NULL FROM games WHERE id = @cs2", new { cs2 }));

        foreach (var (token, id, request, expected, reason) in new (string, string, object, int, string)[]
        {
            (cashier, rust.ToString(), new { settingsPaths = "x" }, 403, "ownerOnly"),
            (owner, Guid.NewGuid().ToString(), new { settingsPaths = Array.Empty<string>() }, 404, "game"),
            (owner, "not-a-game", new { settingsPaths = Array.Empty<string>() }, 404, "game"),
            (owner, rust.ToString(), new { settingsPaths = "x" }, 400, "format"),
            (owner, rust.ToString(), new { other = 1 }, 400, "required"),
        })
        {
            (status, body) = await PatchAsync(token, id, request);
            var details = body.GetProperty("error").GetProperty("details");
            Assert.Equal((expected, reason), (status, details.TryGetProperty("reason", out var r) ? r.GetString() : details.GetProperty("what").GetString()));
        }
    }
}
