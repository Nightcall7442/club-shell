using System.Text.Json;
using ClubShell.Contracts.Games;
using ClubShell.Server.Infrastructure;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>A game without an anti-cheat, a FACEIT game and an EAC game; the seed's policy requires no anti-cheat for every launch.</summary>
public sealed class ZonesFaceitCatalogFixture : ServerFixture
{
    public static readonly Guid Plain = DevSeed.Sid("game:zf-plain");
    public static readonly Guid Faceit = DevSeed.Sid("game:zf-faceit");
    public static readonly Guid Eac = DevSeed.Sid("game:zf-eac");

    public ZonesFaceitCatalogFixture()
    {
        Directory.CreateDirectory(DataDir);
        var path = Path.Combine(DataDir, "games.json");
        static Game Make(Guid id, string title, AntiCheatKind antiCheat) => new(
            id, title, LauncherType.Steam, "1", null, null, null, false, ["shooter"], [], "https://cdn.example/cover.jpg",
            null, null, "", 0, 0, null, false, antiCheat, null, 1.5, null, null);
        File.WriteAllText(path, JsonSerializer.Serialize(new[]
        {
            Make(Plain, "Counter-Strike 2", AntiCheatKind.None),
            Make(Faceit, "CS2 FACEIT", AntiCheatKind.Faceit),
            Make(Eac, "Apex Legends", AntiCheatKind.Eac),
        }, ServerJson.Options));
        Settings["Catalog:GamesSeedPath"] = path;
    }
}

/// <summary>
/// SHELL_CHANGES 43: <c>getGames</c> leaves out the games of other zones (the PC's own zone, set in the hall) and, as D-74
/// for Vanguard, the FACEIT games of a PC whose heartbeat says the FACEIT driver is missing — every game with an
/// anti-cheat when the club's policy requires FACEIT for each and blocks on a violation. The owner sets a game's zones with
/// <c>PUT /admin/games/{id}/zones</c> (beyond the contract).
/// </summary>
public sealed class CatalogZonesFaceitTests(ZonesFaceitCatalogFixture server) : IClassFixture<ZonesFaceitCatalogFixture>
{
    private static readonly Guid[] All = VanguardGamesTests.Sorted([ZonesFaceitCatalogFixture.Plain, ZonesFaceitCatalogFixture.Faceit, ZonesFaceitCatalogFixture.Eac]);

    [Fact]
    public async Task Faceit_games_are_hidden_when_the_heartbeat_reports_the_driver_missing()
    {
        var agent = await TestAgent.CreateAsync(server);
        foreach (var antiCheat in new object?[] { null, new { faceitInstalled = (bool?)null }, new { faceitInstalled = true, vanguardLoaded = true } })
        {
            await Players.ReadAsync(await agent.HeartbeatAsync(antiCheat: antiCheat), 200);
            Assert.Equal(All, await VanguardGamesTests.ListedAsync(agent));
        }

        var full = await ETagAsync(agent);
        await Players.ReadAsync(await agent.HeartbeatAsync(antiCheat: new { faceitInstalled = false, vanguardInstalled = true, vanguardLoaded = true }), 200);
        Assert.Equal(VanguardGamesTests.Sorted([ZonesFaceitCatalogFixture.Plain, ZonesFaceitCatalogFixture.Eac]), await VanguardGamesTests.ListedAsync(agent));
        Assert.Equal(200, await GamesTests.StatusWithETagAsync(agent, full));
        Assert.NotEqual(full, await ETagAsync(agent));

        // FACEIT required for every game with an anti-cheat, blocking on a violation: the EAC game is refused there too.
        await Players.ScalarAsync<int>(server, """
            UPDATE clubs SET policy = jsonb_set(jsonb_set(policy, '{anticheat,required}', '["faceit"]'), '{anticheat,blockOnViolation}', 'true')
            RETURNING 1
            """);
        try
        {
            Assert.Equal(VanguardGamesTests.Sorted([ZonesFaceitCatalogFixture.Plain]), await VanguardGamesTests.ListedAsync(agent));
        }
        finally
        {
            await Players.ScalarAsync<int>(server, "UPDATE clubs SET policy = jsonb_set(policy, '{anticheat,required}', '[]') RETURNING 1");
        }
    }

    [Fact]
    public async Task A_game_with_zones_is_listed_only_on_the_pcs_of_those_zones()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var cashier = await LoginAsync(server, CashierPin);
        var agent = await TestAgent.CreateAsync(server);
        await SetZoneAsync(agent, "Standard");
        var version = await Players.ScalarAsync<int>(server, "SELECT catalog_version FROM clubs");

        var (status, body) = await PutZonesAsync(owner, ZonesFaceitCatalogFixture.Eac.ToString(), new { zones = new[] { " VIP ", "Bootcamp", "VIP" } });
        Assert.Equal(200, status);
        Assert.Equal(["VIP", "Bootcamp"], body.GetProperty("zones").EnumerateArray().Select(z => z.GetString()));
        Assert.Equal(version + 1, await Players.ScalarAsync<int>(server, "SELECT catalog_version FROM clubs"));
        Assert.Equal("seed", await Players.ScalarAsync<string>(server, "SELECT origin FROM games WHERE id = @id", new { id = ZonesFaceitCatalogFixture.Eac }));

        var withoutEac = VanguardGamesTests.Sorted([ZonesFaceitCatalogFixture.Plain, ZonesFaceitCatalogFixture.Faceit]);
        Assert.Equal(withoutEac, await VanguardGamesTests.ListedAsync(agent));
        // The PC's own zone decides, not the zone the agent sends.
        Assert.DoesNotContain(await IdsAsync(agent, "/api/v1/games?zone=VIP"), id => id == ZonesFaceitCatalogFixture.Eac);
        await SetZoneAsync(agent, "VIP");
        Assert.Equal(All, await VanguardGamesTests.ListedAsync(agent));

        // The console lists the zones; an empty list offers the game in every zone again.
        using (var raw = server.CreateDefaultClient())
        using (var response = await raw.SendAsync(Request(HttpMethod.Get, "/games", cashier, null)))
        {
            var list = await Players.ReadAsync(response, 200);
            var eac = list.GetProperty("items").EnumerateArray().Single(g => g.GetProperty("id").GetGuid() == ZonesFaceitCatalogFixture.Eac);
            Assert.Equal(["VIP", "Bootcamp"], eac.GetProperty("zones").EnumerateArray().Select(z => z.GetString()));
        }

        await SetZoneAsync(agent, "Standard");
        Assert.Equal(200, (await PutZonesAsync(owner, ZonesFaceitCatalogFixture.Eac.ToString(), new { zones = Array.Empty<string>() })).Status);
        Assert.True(await Players.ScalarAsync<bool>(server, "SELECT zones IS NULL FROM games WHERE id = @id", new { id = ZonesFaceitCatalogFixture.Eac }));
        Assert.Equal(All, await VanguardGamesTests.ListedAsync(agent));

        foreach (var (token, id, request, expected, reason) in new (string, string, object, int, string)[]
        {
            (cashier, ZonesFaceitCatalogFixture.Eac.ToString(), new { zones = new[] { "VIP" } }, 403, "ownerOnly"),
            (owner, Guid.NewGuid().ToString(), new { zones = new[] { "VIP" } }, 404, "game"),
            (owner, ZonesFaceitCatalogFixture.Eac.ToString(), new { zones = "VIP" }, 400, "format"),
            (owner, ZonesFaceitCatalogFixture.Eac.ToString(), new { zones = new object[] { "VIP", 1 } }, 400, "format"),
            (owner, ZonesFaceitCatalogFixture.Eac.ToString(), new { zones = new[] { new string('z', 33) } }, 400, "format"),
            (owner, ZonesFaceitCatalogFixture.Eac.ToString(), new { zones = Enumerable.Range(0, 21).Select(i => $"Z{i}").ToArray() }, 400, "max"),
            (owner, ZonesFaceitCatalogFixture.Eac.ToString(), new { other = 1 }, 400, "required"),
        })
        {
            (status, body) = await PutZonesAsync(token, id, request);
            var details = body.GetProperty("error").GetProperty("details");
            Assert.Equal((expected, reason), (status, details.TryGetProperty("reason", out var r) ? r.GetString() : details.GetProperty("what").GetString()));
        }
    }

    private async Task SetZoneAsync(TestAgent agent, string zone) =>
        await Players.ScalarAsync<int>(server, "UPDATE pcs SET zone = @zone WHERE id = @id RETURNING 1", new { zone, id = agent.PcId });

    private async Task<(int Status, JsonElement Body)> PutZonesAsync(string token, string id, object body)
    {
        // Beyond the contract, so a client without the response validator.
        using var raw = server.CreateDefaultClient();
        using var response = await raw.SendAsync(Request(HttpMethod.Put, $"/games/{id}/zones", token, body));
        return ((int)response.StatusCode, await Players.ReadAsync(response, (int)response.StatusCode));
    }

    private static async Task<List<Guid>> IdsAsync(TestAgent agent, string path) =>
        [.. (await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, path), 200)).GetProperty("items").EnumerateArray().Select(g => g.GetProperty("id").GetGuid())];

    private static async Task<string> ETagAsync(TestAgent agent)
    {
        using var response = await agent.SendAsync(HttpMethod.Get, "/api/v1/games?page=1&pageSize=500");
        Assert.Equal(200, (int)response.StatusCode);
        return response.Headers.ETag!.ToString();
    }
}
