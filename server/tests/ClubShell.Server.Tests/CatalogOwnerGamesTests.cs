using System.Text.Json;
using ClubShell.Server.Games;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// The owner's own games (beyond the contract): <c>POST /admin/games</c> adds one, <c>PUT /admin/games/{id}</c> replaces how
/// it starts and looks, <c>DELETE /admin/games/{id}</c> removes it; each moves <c>catalog_version</c> and the agent's
/// <c>GET /games</c> follows. Whatever the owner touched is <c>origin = 'club'</c>: the seed of the next start neither
/// reverts, restores nor deletes it, while the untouched seed games still follow the file.
/// </summary>
public sealed class CatalogOwnerGamesTests(ExampleCatalogFixture server) : IClassFixture<ExampleCatalogFixture>
{
    private static readonly Guid Cs16 = Guid.Parse("c016600d-9340-4cb1-84c3-3515b75aface");
    private static readonly Guid Cs2 = Guid.Parse("5f504f55-cff1-4d58-bedc-5976a3cee48b");
    private static readonly Guid Dota = Guid.Parse("b3f9dd56-a3ec-4c5e-abf6-412ce22c1e2b");

    [Fact]
    public async Task The_owner_adds_edits_and_deletes_a_game_and_the_agent_follows()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var version = await Players.ScalarAsync<int>(server, "SELECT catalog_version FROM clubs");

        var (status, body) = await SendRawAsync(HttpMethod.Post, "/games", owner, new
        {
            title = "  Half-Life  ", launcher = "exe", exePath = @"G:\Games\Half-Life\hl.exe", args = " -console ", category = new[] { "shooter", " singleplayer ", "shooter" },
            description = "Классика.", videoUrl = " https://cdn.example.com/hl.webm ",
        });
        Assert.Equal(200, status);
        var game = body.GetProperty("game");
        var id = game.GetProperty("id").GetGuid();
        Assert.Equal(("Half-Life", "exe", @"G:\Games\Half-Life\hl.exe", "-console", true, JsonValueKind.Null, "Классика.", "https://cdn.example.com/hl.webm"), (
            game.GetProperty("title").GetString(), game.GetProperty("launcher").GetString(), game.GetProperty("exePath").GetString(),
            game.GetProperty("args").GetString(), game.GetProperty("custom").GetBoolean(), game.GetProperty("coverUrl").ValueKind,
            game.GetProperty("description").GetString(), game.GetProperty("videoUrl").GetString()));
        Assert.Equal(["shooter", "singleplayer"], game.GetProperty("category").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(version + 1, await Players.ScalarAsync<int>(server, "SELECT catalog_version FROM clubs"));

        // The trailer reaches the PCs with the rest of the card: the shell plays it behind the game's art.
        var agent = await TestAgent.CreateAsync(server);
        var seen = (await AgentGamesAsync(agent)).Single(g => g.GetProperty("id").GetGuid() == id);
        Assert.Equal(("exe", @"G:\Games\Half-Life\hl.exe", "-console", "https://cdn.example.com/hl.webm"),
            (seen.GetProperty("launcher").GetString(), seen.GetProperty("exePath").GetString(), seen.GetProperty("args").GetString(),
             seen.GetProperty("videoUrl").GetString()));

        // A Steam game without a cover gets its store art and runs no exe of its own.
        (status, body) = await SendRawAsync(HttpMethod.Post, "/games", owner, new { title = "Terraria", launcher = "steam", launcherAppId = "105600", exePath = @"C:\x.exe" });
        Assert.Equal(200, status);
        Assert.Equal(("https://cdn.cloudflare.steamstatic.com/steam/apps/105600/library_600x900.jpg", JsonValueKind.Null), (
            body.GetProperty("game").GetProperty("coverUrl").GetString(), body.GetProperty("game").GetProperty("exePath").ValueKind));

        (status, body) = await SendRawAsync(HttpMethod.Put, $"/games/{id}", owner, new
        {
            title = "Half-Life 1", launcher = "exe", exePath = @" ""\\nas\games\Half-Life\hl.exe"" ", coverUrl = "https://cdn.example.com/hl.jpg",
        });
        Assert.Equal(200, status);
        game = body.GetProperty("game");
        // A save replaces the card: no videoUrl in it is no trailer, as no args are none.
        Assert.Equal(("Half-Life 1", @"\\nas\games\Half-Life\hl.exe", "https://cdn.example.com/hl.jpg", JsonValueKind.Null, 0, JsonValueKind.Null), (
            game.GetProperty("title").GetString(), game.GetProperty("exePath").GetString(), game.GetProperty("coverUrl").GetString(),
            game.GetProperty("args").ValueKind, game.GetProperty("category").GetArrayLength(), game.GetProperty("videoUrl").ValueKind));
        Assert.Equal(version + 3, await Players.ScalarAsync<int>(server, "SELECT catalog_version FROM clubs"));

        // The contract's list (through the validating client) carries the new fields too.
        var list = await ExpectAsync(server, 200, HttpMethod.Get, "/games", owner);
        Assert.Equal("Half-Life 1", list.GetProperty("items").EnumerateArray().Single(g => g.GetProperty("id").GetGuid() == id).GetProperty("title").GetString());

        Assert.Equal(200, (await SendRawAsync(HttpMethod.Delete, $"/games/{id}", owner)).Status);
        Assert.Equal(200, (await SendRawAsync(HttpMethod.Delete, $"/games/{id}", owner)).Status);
        Assert.Equal(200, (await SendRawAsync(HttpMethod.Delete, "/games/not-a-game", owner)).Status);
        Assert.Equal(version + 4, await Players.ScalarAsync<int>(server, "SELECT catalog_version FROM clubs"));
        list = await ExpectAsync(server, 200, HttpMethod.Get, "/games", owner);
        Assert.DoesNotContain(list.GetProperty("items").EnumerateArray(), g => g.GetProperty("id").GetGuid() == id);
        Assert.DoesNotContain(await AgentGamesAsync(agent), g => g.GetProperty("id").GetGuid() == id);
        Assert.Equal("game", (await SendRawAsync(HttpMethod.Put, $"/games/{id}", owner, new { title = "X", launcher = "exe", exePath = @"C:\x.exe" }))
            .Body.GetProperty("error").GetProperty("details").GetProperty("what").GetString());

        Assert.Equal("gameAdd gameDelete gameSave", await Players.ScalarAsync<string>(server,
            "SELECT string_agg(action, ' ' ORDER BY action) FROM audit_entries WHERE meta ->> 'gameId' = @id", new { id = id.ToString() }));
    }

    [Fact]
    public async Task Bad_cards_are_refused_and_only_the_owner_edits()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var cashier = await LoginAsync(server, CashierPin);
        var good = new { title = "Game", launcher = "exe", exePath = @"G:\Game\game.exe" };
        foreach (var (token, method, path, request, expected, reason) in new (string, HttpMethod, string, object, int, string)[]
        {
            (cashier, HttpMethod.Post, "/games", good, 403, "ownerOnly"),
            (cashier, HttpMethod.Put, $"/games/{Cs16}", good, 403, "ownerOnly"),
            (cashier, HttpMethod.Delete, $"/games/{Cs16}", new { }, 403, "ownerOnly"),
            (owner, HttpMethod.Post, "/games", new { launcher = "exe", exePath = @"G:\Game\game.exe" }, 400, "required"),
            (owner, HttpMethod.Post, "/games", new { title = "   ", launcher = "exe", exePath = @"G:\Game\game.exe" }, 400, "required"),
            (owner, HttpMethod.Post, "/games", new { title = new string('x', 101), launcher = "exe", exePath = @"G:\Game\game.exe" }, 400, "max"),
            (owner, HttpMethod.Post, "/games", new { title = "Game" }, 400, "required"),
            (owner, HttpMethod.Post, "/games", new { title = "Game", launcher = "origin" }, 400, "enum"),
            (owner, HttpMethod.Post, "/games", new { title = "Game", launcher = "exe" }, 400, "required"),
            (owner, HttpMethod.Post, "/games", new { title = "Game", launcher = "exe", exePath = "game.exe" }, 400, "format"),
            (owner, HttpMethod.Post, "/games", new { title = "Game", launcher = "exe", exePath = @"G:\Game\readme.txt" }, 400, "format"),
            (owner, HttpMethod.Post, "/games", new { title = "Game", launcher = "steam" }, 400, "required"),
            (owner, HttpMethod.Post, "/games", new { title = "Game", launcher = "steam", launcherAppId = "abc" }, 400, "format"),
            (owner, HttpMethod.Post, "/games", new { title = "Game", launcher = "exe", exePath = @"G:\g.exe", coverUrl = "ftp://x/y.jpg" }, 400, "format"),
            (owner, HttpMethod.Post, "/games", new { title = "Game", launcher = "exe", exePath = @"G:\g.exe", videoUrl = "http://cdn.example.com/g.mp4" }, 400, "format"),
            (owner, HttpMethod.Post, "/games", new { title = "Game", launcher = "exe", exePath = @"G:\g.exe", videoUrl = "g.mp4" }, 400, "format"),
            (owner, HttpMethod.Post, "/games", new { title = "Game", launcher = "exe", exePath = @"G:\g.exe", videoUrl = "https://x.com/" + new string('v', 2000) }, 400, "max"),
            (owner, HttpMethod.Put, $"/games/{Cs16}", new { title = "Game", launcher = "exe", exePath = @"G:\g.exe", videoUrl = "javascript:alert(1)" }, 400, "format"),
            (owner, HttpMethod.Post, "/games", new { title = "Game", launcher = "exe", exePath = @"G:\g.exe", category = new[] { "a", "b", "c", "d", "e", "f" } }, 400, "max"),
            (owner, HttpMethod.Post, "/games", new { title = "Game", launcher = "exe", exePath = @"G:\g.exe", category = new[] { "bad key" } }, 400, "format"),
            (owner, HttpMethod.Post, "/games", new { title = "Game", launcher = "exe", exePath = @"G:\g.exe", category = "shooter" }, 400, "format"),
            (owner, HttpMethod.Put, $"/games/{Guid.NewGuid()}", good, 404, "game"),
            (owner, HttpMethod.Put, "/games/not-a-game", good, 404, "game"),
        })
        {
            var (status, body) = await SendRawAsync(method, path, token, method == HttpMethod.Delete ? null : request);
            var details = body.GetProperty("error").GetProperty("details");
            Assert.Equal((expected, reason), (status, details.TryGetProperty("reason", out var r) ? r.GetString() : details.GetProperty("what").GetString()));
        }

        Assert.Equal(0, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM games WHERE title = 'Game'"));
    }

    [Fact]
    public async Task Registry_values_ride_the_card_to_the_pcs()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var (status, body) = await SendRawAsync(HttpMethod.Post, "/games", owner, new
        {
            title = "Reg Game", launcher = "exe", exePath = @"G:\Reg\reg.exe",
            registryValues = new object[]
            {
                new { key = @"HKCU\Software\Valve\Steam", name = "Language", type = "string", value = "russian" },
                new { key = @"Software/Studio/Game/", name = " Fullscreen ", type = "dword", value = 4294967295L },
            },
        });
        Assert.Equal(200, status);
        var id = body.GetProperty("game").GetProperty("id").GetGuid();
        var values = body.GetProperty("game").GetProperty("registryValues").EnumerateArray().ToList();
        Assert.Equal([(@"HKCU\Software\Valve\Steam", "Language", "string", "\"russian\""), (@"HKCU\Software\Studio\Game", "Fullscreen", "dword", "4294967295")],
            values.Select(v => (v.GetProperty("key").GetString(), v.GetProperty("name").GetString(), v.GetProperty("type").GetString(), v.GetProperty("value").GetRawText())));

        var agent = await TestAgent.CreateAsync(server);
        var seen = (await AgentGamesAsync(agent)).Single(g => g.GetProperty("id").GetGuid() == id);
        Assert.Equal(2, seen.GetProperty("registryValues").GetArrayLength());

        // A save without the field (an older cash desk) keeps them; an empty list drops them.
        Assert.Equal(200, (await SendRawAsync(HttpMethod.Put, $"/games/{id}", owner, new { title = "Reg Game", launcher = "exe", exePath = @"G:\Reg\reg.exe" })).Status);
        Assert.Equal(2, await Players.ScalarAsync<int>(server, "SELECT jsonb_array_length(data -> 'registryValues') FROM games WHERE id = @id", new { id }));
        (status, body) = await SendRawAsync(HttpMethod.Put, $"/games/{id}", owner, new { title = "Reg Game", launcher = "exe", exePath = @"G:\Reg\reg.exe", registryValues = Array.Empty<object>() });
        Assert.Equal((200, 0), (status, body.GetProperty("game").GetProperty("registryValues").GetArrayLength()));

        foreach (var (bad, reason) in new (object, string)[]
        {
            (new { key = @"HKLM\Software\X", name = "A", type = "dword", value = 1 }, "key"),
            (new { key = @"HKCU\Control Panel\Desktop", name = "A", type = "dword", value = 1 }, "key"),
            (new { key = @"HKCU\Software", name = "A", type = "dword", value = 1 }, "key"),
            (new { key = @"HKCU\Software\Policies\Microsoft", name = "A", type = "dword", value = 1 }, "key"),
            (new { key = @"Software\Microsoft\Windows\CurrentVersion\Policies\System", name = "A", type = "dword", value = 1 }, "key"),
            (new { key = @"Software\..\X", name = "A", type = "dword", value = 1 }, "key"),
            (new { key = @"Software\X", name = " ", type = "dword", value = 1 }, "name"),
            (new { key = @"Software\X", name = "A", type = "dword", value = -1 }, "value"),
            (new { key = @"Software\X", name = "A", type = "dword", value = 4294967296L }, "value"),
            (new { key = @"Software\X", name = "A", type = "dword", value = "1" }, "value"),
            (new { key = @"Software\X", name = "A", type = "string", value = new string('s', 1025) }, "value"),
            (new { key = @"Software\X", name = "A", type = "qword", value = 1 }, "value"),
        })
        {
            (status, body) = await SendRawAsync(HttpMethod.Put, $"/games/{id}", owner, new { title = "Reg Game", launcher = "exe", exePath = @"G:\Reg\reg.exe", registryValues = new[] { bad } });
            Assert.Equal((400, "registryValues", reason), (status, body.GetProperty("error").GetProperty("details").GetProperty("field").GetString(),
                body.GetProperty("error").GetProperty("details").GetProperty("reason").GetString()));
        }

        var many = Enumerable.Range(0, 21).Select(i => new { key = @"Software\X", name = $"V{i}", type = "dword", value = i }).ToArray();
        Assert.Equal(400, (await SendRawAsync(HttpMethod.Put, $"/games/{id}", owner, new { title = "Reg Game", launcher = "exe", exePath = @"G:\Reg\reg.exe", registryValues = many })).Status);
    }

    [Fact]
    public async Task The_seed_leaves_the_club_games_alone()
    {
        var owner = await LoginAsync(server, OwnerPin);
        Assert.Equal(200, (await SendRawAsync(HttpMethod.Put, $"/games/{Cs16}", owner, new { title = "CS 1.6", launcher = "exe", exePath = @"G:\Counter Strike 1.6 PRO\hl.exe" })).Status);
        Assert.Equal(200, (await SendRawAsync(HttpMethod.Delete, $"/games/{Dota}", owner)).Status);
        Assert.Equal(200, (await SendRawAsync(HttpMethod.Patch, $"/games/{Cs2}", owner, new { settingsPaths = new[] { @"%USERPROFILE%\cs2" } })).Status);
        var (_, added) = await SendRawAsync(HttpMethod.Post, "/games", owner, new { title = "Mine", launcher = "exe", exePath = @"C:\Mine\mine.exe" });
        var mine = added.GetProperty("game").GetProperty("id").GetGuid();
        var rust = await Players.ScalarAsync<Guid>(server, "SELECT id FROM games WHERE title = 'Rust'");
        await Players.ExecuteAsync(server, "UPDATE games SET data = data || '{\"description\":\"drift\"}' WHERE id = @rust", new { rust });

        Assert.Equal(1, await CatalogSeed.ApplyAsync(server.Services.GetRequiredService<NpgsqlDataSource>(), server.Settings["Catalog:GamesSeedPath"], server.Clock));

        Assert.Equal(@"CS 1.6|G:\Counter Strike 1.6 PRO\hl.exe", await Players.ScalarAsync<string>(server,
            "SELECT title || '|' || (data ->> 'exePath') FROM games WHERE id = @Cs16", new { Cs16 }));
        Assert.True(await Players.ScalarAsync<bool>(server, "SELECT deleted_at IS NOT NULL FROM games WHERE id = @Dota", new { Dota }));
        Assert.True(await Players.ScalarAsync<bool>(server, "SELECT deleted_at IS NULL FROM games WHERE id = @mine", new { mine }));
        Assert.Equal(@"%USERPROFILE%\cs2", await Players.ScalarAsync<string>(server, "SELECT settings_paths[1] FROM games WHERE id = @Cs2", new { Cs2 }));
        Assert.NotEqual("drift", await Players.ScalarAsync<string>(server, "SELECT data ->> 'description' FROM games WHERE id = @rust", new { rust }));
    }

    private async Task<(int Status, JsonElement Body)> SendRawAsync(HttpMethod method, string path, string token, object? body = null)
    {
        // Beyond the contract, so a client without the response validator.
        using var raw = server.CreateDefaultClient();
        using var response = await raw.SendAsync(Request(method, path, token, body));
        return ((int)response.StatusCode, await Players.ReadAsync(response, (int)response.StatusCode));
    }

    private async Task<List<JsonElement>> AgentGamesAsync(TestAgent agent) =>
        [.. (await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, "/api/v1/games"), 200)).GetProperty("items").EnumerateArray()];
}
