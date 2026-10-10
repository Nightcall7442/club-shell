using System.Net.Http.Json;
using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Server.Infrastructure;

namespace ClubShell.Server.Tests;

/// <summary>A fixture whose <c>Catalog:GamesSeedPath</c> holds 1001 games (three agent pages of 500); game 0 carries <c>settingsPaths</c>.</summary>
public sealed class CatalogServerFixture : ServerFixture
{
    public const int Count = 1001;

    public CatalogServerFixture()
    {
        Directory.CreateDirectory(DataDir);
        var path = Path.Combine(DataDir, "games.json");
        var games = Enumerable.Range(0, Count).Select(i => new Game(
            GameId(i), $"Game {i:D4}", LauncherType.Steam, "730", null, null, null, false, ["shooter"], [], "https://cdn.example/cover.jpg",
            null, null, "", 0, i, null, false, AntiCheatKind.None, null, 1.5, null, i == 0 ? [@"%APPDATA%\Game0\cfg"] : null)).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(games, ServerJson.Options));
        Settings["Catalog:GamesSeedPath"] = path;
    }

    public static Guid GameId(int i) => DevSeed.Sid("game:" + i);
}

/// <summary>
/// <c>getGames</c>/<c>getGame</c> (DESIGN §7.3, §7.4, S3): pages up to 1000 and the whole catalog without paging, hidden games
/// left out and the owner's order first, the ETag and <c>catalogVersion</c> of the heartbeat, no <c>settingsPaths</c>.
/// </summary>
public sealed class GamesTests(CatalogServerFixture server) : IClassFixture<CatalogServerFixture>
{
    [Fact]
    public async Task Pages_go_up_to_1000_and_no_paging_is_the_whole_catalog()
    {
        var agent = await TestAgent.CreateAsync(server);
        foreach (var (query, page, size, items) in new[]
        {
            ("?page=1&pageSize=500", 1, 500, 500), ("?page=3&pageSize=500", 3, 500, 1), ("?pageSize=5000", 1, 1000, 1000),
            ("", 1, CatalogServerFixture.Count, CatalogServerFixture.Count), ("?page=2", 2, 1000, 1), ("?page=0&pageSize=abc", 1, 50, 50),
        })
        {
            var body = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, "/api/v1/games" + query), 200);
            Assert.Equal((CatalogServerFixture.Count, page, size, items),
                (body.GetProperty("total").GetInt32(), body.GetProperty("page").GetInt32(), body.GetProperty("pageSize").GetInt32(), body.GetProperty("items").GetArrayLength()));
        }

        var first = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, "/api/v1/games?page=1&pageSize=2"), 200);
        Assert.Equal(["Game 0000", "Game 0001"], first.GetProperty("items").EnumerateArray().Select(g => g.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task Hidden_games_are_left_out_and_the_owners_order_comes_first()
    {
        var agent = await TestAgent.CreateAsync(server);
        await Players.ExecuteAsync(server,
            "UPDATE clubs SET settings = settings || jsonb_build_object('catalog', @catalog::jsonb), catalog_version = catalog_version + 1",
            new { catalog = JsonSerializer.Serialize(new { order = new[] { CatalogServerFixture.GameId(5), CatalogServerFixture.GameId(3) }, hidden = new[] { CatalogServerFixture.GameId(7) } }) });
        try
        {
            var body = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, "/api/v1/games"), 200);
            var ids = body.GetProperty("items").EnumerateArray().Select(g => g.GetProperty("id").GetGuid()).ToList();
            Assert.Equal((CatalogServerFixture.Count - 1, CatalogServerFixture.GameId(5), CatalogServerFixture.GameId(3)), (body.GetProperty("total").GetInt32(), ids[0], ids[1]));
            Assert.DoesNotContain(CatalogServerFixture.GameId(7), ids);

            // By id a hidden game is still served (the agent's fallback for a game it already knows).
            await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, $"/api/v1/games/{CatalogServerFixture.GameId(7)}"), 200);
        }
        finally
        {
            await Players.ExecuteAsync(server, "UPDATE clubs SET settings = settings - 'catalog', catalog_version = catalog_version + 1");
        }
    }

    [Fact]
    public async Task ETag_answers_304_until_the_catalog_changes_and_catalogVersion_is_the_heartbeats()
    {
        var agent = await TestAgent.CreateAsync(server);
        var heartbeat = await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        using var first = await agent.SendAsync(HttpMethod.Get, "/api/v1/games?page=1&pageSize=500");
        var body = await Players.ReadAsync(first, 200);
        Assert.Equal(heartbeat.GetProperty("catalogVersion").GetString(), body.GetProperty("catalogVersion").GetString());
        var etag = first.Headers.ETag!.ToString();
        Assert.Matches("^\"g[0-9]+-[0-9a-f]+\"$", etag);

        Assert.Equal(304, await StatusWithETagAsync(agent, etag));
        Assert.Equal(200, await StatusWithETagAsync(agent, etag, "?page=2&pageSize=500")); // another page is another representation
        Assert.Equal(200, await StatusWithETagAsync(agent, etag, "?page=1&pageSize=499"));
        await Players.ExecuteAsync(server, "UPDATE clubs SET catalog_version = catalog_version + 1");
        Assert.Equal(200, await StatusWithETagAsync(agent, etag));
    }

    [Fact]
    public async Task Settings_paths_never_leave_the_server_and_an_unknown_game_is_404()
    {
        var agent = await TestAgent.CreateAsync(server);
        var game = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, $"/api/v1/games/{CatalogServerFixture.GameId(0)}"), 200);
        Assert.False(game.TryGetProperty("settingsPaths", out _));
        Assert.False(game.GetProperty("installed").GetBoolean());
        Assert.Equal(new[] { @"%APPDATA%\Game0\cfg" }, await Players.ScalarAsync<string[]>(server, "SELECT settings_paths FROM games WHERE id = @id", new { id = CatalogServerFixture.GameId(0) }));
        var listed = (await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, "/api/v1/games?page=1&pageSize=1"), 200)).GetProperty("items")[0];
        Assert.Equal(CatalogServerFixture.GameId(0), listed.GetProperty("id").GetGuid());
        Assert.False(listed.TryGetProperty("settingsPaths", out _));

        using var missing = await agent.SendAsync(HttpMethod.Get, $"/api/v1/games/{Guid.NewGuid()}");
        Assert.Equal("game", (await Contract.ReadErrorAsync(missing, 404, "notFound")).GetProperty("error").GetProperty("details").GetProperty("what").GetString());
    }

    public static async Task<int> StatusWithETagAsync(TestAgent agent, string etag, string query = "?page=1&pageSize=500")
    {
        using var request = agent.Request(HttpMethod.Get, "/api/v1/games" + query);
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var response = await agent.Server.Http.SendAsync(request);
        return (int)response.StatusCode;
    }
}

/// <summary>
/// A fixture with one game of each anti-cheat case of D-74: none, tagged Vanguard by the catalog, a Riot game the catalog
/// did not tag (the agent's gate treats it as Vanguard) and a Riot game tagged with another anti-cheat. The club's policy
/// is the seed's, which requires Vanguard for every game with an anti-cheat (<c>anticheat.required</c>).
/// </summary>
public class VanguardCatalogFixture : ServerFixture
{
    public static readonly Guid Plain = DevSeed.Sid("game:vg-plain");
    public static readonly Guid Tagged = DevSeed.Sid("game:vg-tagged");
    public static readonly Guid RiotUntagged = DevSeed.Sid("game:vg-riot");
    public static readonly Guid RiotEac = DevSeed.Sid("game:vg-riot-eac");

    public VanguardCatalogFixture()
    {
        Directory.CreateDirectory(DataDir);
        var path = Path.Combine(DataDir, "games.json");
        static Game Make(Guid id, string title, LauncherType launcher, AntiCheatKind antiCheat) => new(
            id, title, launcher, "1", null, null, null, false, ["shooter"], [], "https://cdn.example/cover.jpg",
            null, null, "", 0, 0, null, false, antiCheat, null, 1.5, null, null);
        File.WriteAllText(path, JsonSerializer.Serialize(new[]
        {
            Make(Plain, "Counter-Strike 2", LauncherType.Steam, AntiCheatKind.None),
            Make(Tagged, "Tagged Vanguard", LauncherType.Steam, AntiCheatKind.Vanguard),
            Make(RiotUntagged, "League of Legends", LauncherType.Riot, AntiCheatKind.None),
            Make(RiotEac, "Riot with EAC", LauncherType.Riot, AntiCheatKind.Eac),
        }, ServerJson.Options));
        Settings["Catalog:GamesSeedPath"] = path;
    }
}

/// <summary>
/// <c>getGames</c> on a PC without Vanguard (D-74): when the PC's last heartbeat says vgk is missing or not loaded, the games
/// the agent's gate would refuse there are left out — under the seed's policy, which requires Vanguard for every game with
/// an anti-cheat, all of those, the EAC game too; a heartbeat that does not say (an older agent, unknown fields) or a loaded
/// vgk hides nothing; the ETag follows the visible set.
/// </summary>
public sealed class VanguardGamesTests(VanguardCatalogFixture server) : IClassFixture<VanguardCatalogFixture>
{
    private static readonly Guid[] All =
        [VanguardCatalogFixture.Plain, VanguardCatalogFixture.Tagged, VanguardCatalogFixture.RiotUntagged, VanguardCatalogFixture.RiotEac];

    private static readonly Guid[] WithoutVanguard = [VanguardCatalogFixture.Plain];

    [Fact]
    public async Task Games_that_need_Vanguard_are_hidden_when_the_heartbeat_reports_vgk_missing_or_not_loaded()
    {
        foreach (var antiCheat in new object[]
        {
            new { vanguardInstalled = false, vanguardLoaded = false, secureBoot = true, tpm = true },
            new { vanguardInstalled = true, vanguardLoaded = false, secureBoot = true, tpm = true },
            new { vanguardInstalled = (bool?)null, vanguardLoaded = false },
        })
        {
            var agent = await TestAgent.CreateAsync(server);
            await Players.ReadAsync(await agent.HeartbeatAsync(antiCheat: antiCheat), 200);
            Assert.Equal(Sorted(WithoutVanguard), await ListedAsync(agent));

            // By id a hidden game is still served, as a game the owner hid (the agent's fallback for a game it knows).
            await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, $"/api/v1/games/{VanguardCatalogFixture.Tagged}"), 200);
        }
    }

    [Fact]
    public async Task Every_game_is_listed_when_vgk_is_loaded_or_the_heartbeat_does_not_say()
    {
        var agent = await TestAgent.CreateAsync(server);
        Assert.Equal(Sorted(All), await ListedAsync(agent)); // no heartbeat yet
        foreach (var antiCheat in new object?[]
        {
            null, // an agent without antiCheat in its heartbeat
            new { vanguardInstalled = (bool?)null, vanguardLoaded = (bool?)null, secureBoot = false, tpm = false },
            new { vanguardInstalled = true, vanguardLoaded = true, secureBoot = true, tpm = true },
        })
        {
            await Players.ReadAsync(await agent.HeartbeatAsync(antiCheat: antiCheat), 200);
            Assert.Equal(Sorted(All), await ListedAsync(agent));
        }
    }

    [Fact]
    public async Task The_ETag_changes_with_the_visible_set()
    {
        var agent = await TestAgent.CreateAsync(server);
        await Players.ReadAsync(await agent.HeartbeatAsync(antiCheat: new { vanguardInstalled = true, vanguardLoaded = true }), 200);
        var full = await ETagAsync(agent);

        // A PC that reports no anti-cheat state gets the same ETag for the same list: the hash only grows when games are hidden.
        Assert.Equal(304, await GamesTests.StatusWithETagAsync(await TestAgent.CreateAsync(server), full));

        // vgk no longer loaded: the cached full list is not confirmed, and the shorter one has an ETag of its own.
        await Players.ReadAsync(await agent.HeartbeatAsync(antiCheat: new { vanguardInstalled = true, vanguardLoaded = false }), 200);
        Assert.Equal(200, await GamesTests.StatusWithETagAsync(agent, full));
        var reduced = await ETagAsync(agent);
        Assert.NotEqual(full, reduced);
        Assert.Equal(304, await GamesTests.StatusWithETagAsync(agent, reduced));

        // Loaded again after a reboot: the full list is back under its old ETag.
        await Players.ReadAsync(await agent.HeartbeatAsync(antiCheat: new { vanguardInstalled = true, vanguardLoaded = true }), 200);
        Assert.Equal(200, await GamesTests.StatusWithETagAsync(agent, reduced));
        Assert.Equal(304, await GamesTests.StatusWithETagAsync(agent, full));
    }

    internal static Guid[] Sorted(IEnumerable<Guid> ids) => [.. ids.Order()];

    internal static async Task<Guid[]> ListedAsync(TestAgent agent)
    {
        var body = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, "/api/v1/games"), 200);
        var ids = body.GetProperty("items").EnumerateArray().Select(g => g.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(ids.Count, body.GetProperty("total").GetInt32());
        return Sorted(ids);
    }

    private static async Task<string> ETagAsync(TestAgent agent)
    {
        using var response = await agent.SendAsync(HttpMethod.Get, "/api/v1/games?page=1&pageSize=500");
        Assert.Equal(200, (int)response.StatusCode);
        return response.Headers.ETag!.ToString();
    }
}

/// <summary>The games of <see cref="VanguardCatalogFixture"/> under a policy that requires EAC only.</summary>
public sealed class VanguardOptionalCatalogFixture : VanguardCatalogFixture
{
    public VanguardOptionalCatalogFixture()
    {
        var seed = JsonDefaults.Deserialize<Policy>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "seed", "policies.example.json")))!;
        var path = Path.Combine(DataDir, "policy.json");
        File.WriteAllText(path, JsonDefaults.Serialize(seed with { Anticheat = new AntiCheatPolicy([AntiCheatKind.Eac], BlockOnViolation: true) }));
        Settings["Catalog:PolicySeedPath"] = path;
    }
}

/// <summary>
/// D-74 follows the club's policy: without Vanguard in <c>anticheat.required</c>, or with <c>blockOnViolation</c> off, the
/// agent's gate lets a game of another anti-cheat start on a PC without vgk, so only the Vanguard games are hidden there.
/// </summary>
public sealed class VanguardOptionalGamesTests(VanguardOptionalCatalogFixture server) : IClassFixture<VanguardOptionalCatalogFixture>
{
    [Fact]
    public async Task Only_Vanguard_games_are_hidden_when_the_policy_does_not_make_every_launch_need_Vanguard()
    {
        var agent = await TestAgent.CreateAsync(server);
        await Players.ReadAsync(await agent.HeartbeatAsync(antiCheat: new { vanguardInstalled = true, vanguardLoaded = false }), 200);
        Guid[] startable = VanguardGamesTests.Sorted([VanguardCatalogFixture.Plain, VanguardCatalogFixture.RiotEac]);
        Assert.Equal(startable, await VanguardGamesTests.ListedAsync(agent));

        // Vanguard required, but a violation only reported, not blocked: the EAC game still starts.
        await Players.ScalarAsync<int>(server, """
            UPDATE clubs SET policy = jsonb_set(jsonb_set(policy, '{anticheat,required}', '["vanguard"]'), '{anticheat,blockOnViolation}', 'false')
            RETURNING 1
            """);
        Assert.Equal(startable, await VanguardGamesTests.ListedAsync(agent));
    }
}

/// <summary><c>sendLaunchReport</c> (S3): a report repeated from the agent's offline queue is stored once; <c>lastPlayedAt</c> is the player's newest successful launch.</summary>
public sealed class LaunchReportTests(CatalogServerFixture server) : IClassFixture<CatalogServerFixture>
{
    [Fact]
    public async Task A_repeated_report_is_stored_once_and_sets_lastPlayedAt()
    {
        var (agent, player) = await Players.SignedInAsync(server);
        var session = (await Players.StartAsync(agent, player)).Body.GetProperty("id").GetGuid();
        var startedAt = server.Clock.GetUtcNow().AddMinutes(-1);
        var game = CatalogServerFixture.GameId(2);
        string etagBefore;
        using (var before = await agent.SendAsync(HttpMethod.Get, "/api/v1/games?page=1&pageSize=5"))
        {
            etagBefore = before.Headers.ETag!.ToString();
        }

        for (var i = 0; i < 2; i++)
        {
            await Players.ReadAsync(await agent.PostAsync($"/api/v1/games/{game}/launch-report", Report(session, player.Id, startedAt, ok: true, "launch")), 204);
        }

        await Players.ReadAsync(await agent.PostAsync($"/api/v1/games/{game}/launch-report", Report(session, player.Id, startedAt, ok: true, "exit")), 204);
        await Players.ReadAsync(await agent.PostAsync($"/api/v1/games/{CatalogServerFixture.GameId(3)}/launch-report",
            Report(Guid.NewGuid(), player.Id, startedAt, ok: false, "launch")), 204);
        Assert.Equal(2, await Players.ScalarAsync<int>(server, "SELECT count(*)::int FROM launch_reports WHERE session_id = @session", new { session }));

        var played = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, $"/api/v1/games/{game}"), 200);
        Assert.Equal(startedAt, played.GetProperty("lastPlayedAt").GetDateTimeOffset());
        var failed = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, $"/api/v1/games/{CatalogServerFixture.GameId(3)}"), 200);
        Assert.False(failed.TryGetProperty("lastPlayedAt", out _));

        // The list carries it too, and the ETag follows the player's lastPlayedAt: another player's cached page is no match.
        var list = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, "/api/v1/games?page=1&pageSize=5"), 200);
        Assert.Equal(startedAt, list.GetProperty("items")[2].GetProperty("lastPlayedAt").GetDateTimeOffset());
        using var mine = await agent.SendAsync(HttpMethod.Get, "/api/v1/games?page=1&pageSize=5");
        var anonymous = await TestAgent.CreateAsync(server);
        Assert.Equal(200, await GamesTests.StatusWithETagAsync(anonymous, mine.Headers.ETag!.ToString(), "?page=1&pageSize=5"));
        Assert.Equal(200, await GamesTests.StatusWithETagAsync(agent, etagBefore, "?page=1&pageSize=5")); // same player, new lastPlayedAt
    }

    /// <summary>
    /// A PC reports only for its own sessions: another PC's <c>sessionId</c>/<c>userId</c> is stored without them, so it
    /// neither sets that player's <c>lastPlayedAt</c> nor takes the dedup slot of that PC's real report.
    /// </summary>
    [Fact]
    public async Task A_report_naming_another_pcs_session_is_stored_without_user_and_session()
    {
        var (other, _) = await Players.SignedInAsync(server);
        var (owner, player) = await Players.SignedInAsync(server);
        var session = (await Players.StartAsync(owner, player)).Body.GetProperty("id").GetGuid();
        var startedAt = server.Clock.GetUtcNow().AddMinutes(-1);
        var game = $"/api/v1/games/{CatalogServerFixture.GameId(4)}";
        await Players.ReadAsync(await other.PostAsync(game + "/launch-report", Report(session, player.Id, startedAt, ok: true, "launch")), 204);
        await Players.ReadAsync(await owner.PostAsync(game + "/launch-report", Report(session, Guid.NewGuid(), startedAt, ok: true, "exit")), 204); // not its player
        Assert.Equal(2, await Players.ScalarAsync<int>(server,
            "SELECT count(*)::int FROM launch_reports WHERE game_id = @game AND user_id IS NULL AND session_id IS NULL", new { game = CatalogServerFixture.GameId(4) }));
        Assert.False((await Players.ReadAsync(await owner.SendAsync(HttpMethod.Get, game), 200)).TryGetProperty("lastPlayedAt", out _));

        await Players.ReadAsync(await owner.PostAsync(game + "/launch-report", Report(session, player.Id, startedAt, ok: true, "launch")), 204);
        Assert.Equal(startedAt, (await Players.ReadAsync(await owner.SendAsync(HttpMethod.Get, game), 200)).GetProperty("lastPlayedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Unknown_game_is_404_and_a_bad_report_400()
    {
        var agent = await TestAgent.CreateAsync(server);
        using (var missing = await agent.PostAsync($"/api/v1/games/{Guid.NewGuid()}/launch-report", Report(Guid.NewGuid(), Guid.NewGuid(), server.Clock.GetUtcNow(), true, "launch")))
        {
            await Contract.ReadErrorAsync(missing, 404, "notFound");
        }

        var game = CatalogServerFixture.GameId(1);
        var now = server.Clock.GetUtcNow();
        object Body(object? result = null, object? antiCheat = null, string phase = "launch") => new
        {
            sessionId = Guid.NewGuid(), userId = Guid.NewGuid(), result = result ?? new { ok = true, startedAt = now }, durationMs = 10, launcher = "steam",
            antiCheat = antiCheat ?? new { kind = "eac", ok = true }, phase,
        };
        foreach (var (body, field, reason) in new (object, string, string)[]
        {
            (new { sessionId = Guid.NewGuid() }, "userId", "required"),
            (new
            {
                sessionId = Guid.NewGuid(), userId = Guid.NewGuid(), result = new { ok = true, startedAt = now }, launcher = "steam",
                antiCheat = new { kind = "eac", ok = true }, phase = "launch",
            }, "durationMs", "required"),
            (Body(phase: "foo"), "phase", "enum"),
            (Body(antiCheat: new { kind = "vac", ok = true }), "antiCheat.kind", "enum"),
            (Body(result: new { startedAt = now }), "result.ok", "required"),
            (Body(antiCheat: new { ok = true }), "antiCheat.kind", "required"),
            (Body(antiCheat: new { kind = "eac" }), "antiCheat.ok", "required"),
        })
        {
            using var response = await agent.PostAsync($"/api/v1/games/{game}/launch-report", body);
            var details = (await Contract.ReadErrorAsync(response, 400, "validation")).GetProperty("error").GetProperty("details");
            Assert.Equal((field, reason), (details.GetProperty("field").GetString(), details.GetProperty("reason").GetString()));
        }
    }

    public static object Report(Guid sessionId, Guid userId, DateTimeOffset startedAt, bool ok, string phase) => new
    {
        sessionId,
        userId,
        result = new { ok, pid = 4812, startedAt },
        durationMs = 2140,
        launcher = "steam",
        antiCheat = new { kind = "eac", ok = true },
        phase,
    };
}

/// <summary><c>getUpdateManifest</c> (S3, owner's decision): always 204 once the request is valid; 404 channel/component; 400 current.</summary>
public sealed class ManifestTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Up_to_date_is_204_and_the_request_is_validated()
    {
        var agent = await TestAgent.CreateAsync(server);
        await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, "/api/v1/updates/stable/manifest?component=agent&current=1.4.2&arch=x64"), 204);
        await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, "/api/v1/updates/beta/manifest?component=shell&current=1.4.2"), 204);
        foreach (var (query, status, detail) in new[]
        {
            ("nightly/manifest?component=agent&current=1.0.0", 404, "channel"), ("stable/manifest?component=kiosk&current=1.0.0", 404, "component"),
            ("stable/manifest?component=shell", 400, "current"), ("stable/manifest?current=1.0.0", 400, "component"),
        })
        {
            using var response = await agent.SendAsync(HttpMethod.Get, "/api/v1/updates/" + query);
            var details = (await Contract.ReadErrorAsync(response, status, status == 404 ? "notFound" : "validation")).GetProperty("error").GetProperty("details");
            Assert.Equal(detail, details.GetProperty(status == 404 ? "what" : "field").GetString());
        }
    }
}

/// <summary><c>getPc</c> and <c>reportAntiCheat</c> (S3): <c>hwid</c> only of the caller's own PC; a report of another PC is <c>403 pcMismatch</c>.</summary>
public sealed class PcTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Hwid_is_only_given_for_the_callers_own_pc()
    {
        var self = await TestAgent.CreateAsync(server);
        var other = await TestAgent.CreateAsync(server);
        var own = await Players.ReadAsync(await self.SendAsync(HttpMethod.Get, $"/api/v1/pcs/{self.PcId}"), 200);
        Assert.Equal(self.Hwid, own.GetProperty("hwid").GetString());
        var theirs = await Players.ReadAsync(await self.SendAsync(HttpMethod.Get, $"/api/v1/pcs/{other.PcId}"), 200);
        Assert.Equal(other.PcId, theirs.GetProperty("id").GetGuid());
        Assert.False(theirs.TryGetProperty("hwid", out _));

        using (var missing = await self.SendAsync(HttpMethod.Get, $"/api/v1/pcs/{Guid.NewGuid()}"))
        {
            await Contract.ReadErrorAsync(missing, 404, "notFound");
        }

        using var bad = await self.SendAsync(HttpMethod.Get, "/api/v1/pcs/not-a-guid");
        await Contract.ReadErrorAsync(bad, 400, "validation");
    }

    [Fact]
    public async Task Anticheat_report_is_stored_and_must_name_the_callers_pc()
    {
        var self = await TestAgent.CreateAsync(server);
        var other = await TestAgent.CreateAsync(server);
        await Players.ReadAsync(await self.PostAsync("/api/v1/anticheat/report", Violation(self.PcId)), 204);
        Assert.Equal("blockedProcess", await Players.ScalarAsync<string>(server, "SELECT data->>'check' FROM anticheat_reports WHERE pc_id = @PcId", new { self.PcId }));

        using var response = await self.PostAsync("/api/v1/anticheat/report", Violation(other.PcId));
        await Contract.ReadErrorAsync(response, 403, "forbidden", "pcMismatch");
    }

    private object Violation(Guid pcId) => new
    {
        pcId, kind = "eac", check = "blockedProcess", severity = "warning", details = new { process = "cheat.exe" },
        at = server.Clock.GetUtcNow(), actionTaken = "none",
    };
}

/// <summary>
/// Slice S3 through the real agent <see cref="ClubShell.Core.Http.ServerClient"/> (DESIGN §10.b): the catalog is assembled
/// exactly as <c>GameLibrary.RefreshAsync</c> does it (page 1 of 500 with the stored ETag, then the rest without it until
/// <c>total</c>) — <c>GameLibrary</c> itself is net8.0-windows and cannot load in this net10 test host — and its repeat is
/// a 304; <c>GetPcAsync</c> has the PC's own <c>hwid</c>; the manifest is "up to date"; launch and anti-cheat reports
/// go through. Coverage: the five S3 operations and <c>reportAntiCheat</c> got a contract-valid success.
/// </summary>
public sealed class AgentHarnessS3Tests(CatalogServerFixture server) : IClassFixture<CatalogServerFixture>
{
    [Fact]
    public async Task S3_scenario_through_the_real_server_client()
    {
        var ct = CancellationToken.None;
        await using var agent = await AgentHarness.CreateAsync(server);
        var client = agent.Client;
        var pcId = (await client.RegisterAsync(agent.RegisterRequest(), ct)).PcId;

        var first = await client.GetGamesAsync(null, 1, 500, null, ct);
        var page = first.Require();
        var games = page.Items.ToList();
        var pages = 1;
        for (var n = 2; games.Count < page.Total && page.Items.Count > 0; n++, pages++)
        {
            page = (await client.GetGamesAsync(null, n, 500, null, ct)).Require();
            games.AddRange(page.Items);
        }

        Assert.Equal((CatalogServerFixture.Count, CatalogServerFixture.Count, 3), (games.Count, games.Select(g => g.Id).Distinct().Count(), pages));
        Assert.True((await client.GetGamesAsync(null, 1, 500, first.ETag, ct)).NotModified);

        var pc = await client.GetPcAsync(pcId, ct);
        Assert.Equal((pcId, agent.Hwid), (pc.Id, pc.Hwid));
        Assert.Null(await client.GetUpdateManifestAsync(UpdateChannel.Stable, UpdateComponent.Agent, "1.4.2", ct));
        Assert.Equal("Game 0001", (await client.GetGameAsync(CatalogServerFixture.GameId(1), ct)).Title);

        var now = agent.Clock.GetUtcNow();
        await client.SendLaunchReportAsync(CatalogServerFixture.GameId(1), new LaunchReport(
            Guid.NewGuid(), Guid.NewGuid(), LaunchResult.Success(4812, now), 2140, LauncherType.Steam, new AntiCheatCheckResult(AntiCheatKind.Eac, true),
            LaunchReportPhase.Launch), ct);
        await client.ReportAntiCheatAsync(new AntiCheatReport(
            pcId, null, null, CatalogServerFixture.GameId(1), AntiCheatKind.Eac, AntiCheatChecks.BlockedProcess, AntiCheatSeverity.Warning,
            JsonElement.Parse("""{"process":"cheat.exe"}"""), now, AntiCheatAction.None), ct);

        var success = new Dictionary<string, int>
        {
            ["getGames"] = 200, ["getGame"] = 200, ["sendLaunchReport"] = 204, ["getUpdateManifest"] = 204, ["getPc"] = 200, ["reportAntiCheat"] = 204,
        };
        Assert.Equal(
            Games.GameEndpoints.Operations.Concat(Updates.UpdateEndpoints.Operations).Concat(Agents.PcEndpoints.Operations).Order(), success.Keys.Order());
        Assert.All(success, op => Assert.True(server.Covered.ContainsKey($"{op.Key} {op.Value}"), $"{op.Key} {op.Value} not covered"));
        Assert.True(server.Covered.ContainsKey("getGames 304"));
    }
}
