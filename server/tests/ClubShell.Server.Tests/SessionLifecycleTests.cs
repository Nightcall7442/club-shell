using System.Net.Http.Json;
using System.Text.Json;
using static ClubShell.Server.Tests.PurchaseRulesTests;

namespace ClubShell.Server.Tests;

/// <summary>
/// The billing clock of a session (DESIGN §5.4–§5.6): <c>secondsLeft</c> after pause and resume, a lock does not stop
/// billing (D-12), extension (package minutes, the tariff maximum, postpaid refused), <c>GET /sessions/current</c>.
/// </summary>
public sealed class SessionLifecycleTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    [Fact]
    public async Task Pause_stops_the_clock_and_resume_restarts_it()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(3600, created.GetProperty("secondsLeft").GetInt32());
        Assert.Equal(1_200_000, created.GetProperty("cost").GetProperty("amount").GetInt64());
        Assert.Equal(8_800_000, await Players.BalanceAsync(Server, player.Id));

        Server.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(3000, (await CurrentAsync(agent)).GetProperty("secondsLeft").GetInt32());

        var paused = await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{id}/pause", null), 200);
        Assert.Equal(("paused", 3000), (paused.GetProperty("state").GetString(), paused.GetProperty("secondsLeft").GetInt32()));
        Assert.Equal(Server.Clock.GetUtcNow(), paused.GetProperty("pausedAt").GetDateTimeOffset());
        Assert.False(paused.TryGetProperty("endsAt", out _));

        using (var again = await agent.PostAsync($"/api/v1/sessions/{id}/pause", null))
        {
            await Contract.ReadErrorAsync(again, 409, "conflict", "alreadyPaused");
        }

        Server.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(3000, (await CurrentAsync(agent)).GetProperty("secondsLeft").GetInt32());

        var resumed = await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{id}/resume", null), 200);
        Assert.Equal("active", resumed.GetProperty("state").GetString());
        Assert.Equal(Server.Clock.GetUtcNow().AddSeconds(3000), resumed.GetProperty("endsAt").GetDateTimeOffset());
        using (var again = await agent.PostAsync($"/api/v1/sessions/{id}/resume", null))
        {
            await Contract.ReadErrorAsync(again, 409, "conflict", "notPaused");
        }

        Server.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2940, (await CurrentAsync(agent)).GetProperty("secondsLeft").GetInt32());
    }

    [Fact]
    public async Task A_lock_does_not_stop_billing()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player);
        var id = created.GetProperty("id").GetGuid();
        using (var locked = await agent.PostAsync($"/api/v1/sessions/{id}/events",
            new { events = new[] { new { sessionId = id, type = "locked", at = Server.Clock.GetUtcNow() } } }, Guid.NewGuid()))
        {
            await Players.ReadAsync(locked, 204);
        }

        Server.Clock.Advance(TimeSpan.FromMinutes(10));
        var current = await CurrentAsync(agent);
        Assert.Equal(("locked", 3000), (current.GetProperty("state").GetString(), current.GetProperty("secondsLeft").GetInt32()));

        // locked comes from queued agent events and is informational: the player may have unlocked already, pause works.
        var paused = await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{id}/pause", null), 200);
        Assert.Equal(("paused", 3000), (paused.GetProperty("state").GetString(), paused.GetProperty("secondsLeft").GetInt32()));
    }

    /// <summary>The second a pause falls in is used: resume/pause every 950 ms is not free play (a floor billed 0 s).</summary>
    [Fact]
    public async Task Pause_counts_the_started_second()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player);
        var id = created.GetProperty("id").GetGuid();
        await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{id}/pause", null), 200);
        for (var i = 0; i < 10; i++)
        {
            await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{id}/resume", null), 200);
            Server.Clock.Advance(TimeSpan.FromMilliseconds(950));
            await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{id}/pause", null), 200);
        }

        Assert.Equal(10, (await CurrentAsync(agent)).GetProperty("secondsUsed").GetInt32());
    }

    /// <summary>§5.10: grace is free. An extension bought 30 s into grace starts from the bought end, not from the used time.</summary>
    [Fact]
    public async Task Extend_in_grace_does_not_pay_for_the_grace()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player, minutes: 30);
        var id = created.GetProperty("id").GetGuid();
        Server.Clock.Advance(TimeSpan.FromSeconds(1830));

        var extended = await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{id}/extend", new { minutes = 30 }, Guid.NewGuid()), 200);
        Assert.Equal(1800, extended.GetProperty("secondsLeft").GetInt32());
        Assert.Equal(Server.Clock.GetUtcNow().AddSeconds(1800), extended.GetProperty("endsAt").GetDateTimeOffset());
    }

    /// <summary>A player token of PC A acts only on A: signed in on A and B, the player's session on B is not paused, resumed or extended from A.</summary>
    [Fact]
    public async Task Pause_resume_and_extend_of_the_players_session_on_another_pc_are_403_pc_mismatch()
    {
        var (a, player) = await Players.SignedInAsync(Server);
        var b = await TestAgent.CreateAsync(Server);
        await b.LoginAsync(player);
        var (_, created) = await Players.StartAsync(b, player);
        var id = created.GetProperty("id").GetGuid();

        foreach (var (path, body, key) in new (string, object?, Guid?)[] { ("pause", null, null), ("resume", null, null), ("extend", new { minutes = 30 }, Guid.NewGuid()) })
        {
            using var response = await a.PostAsync($"/api/v1/sessions/{id}/{path}", body, key);
            await Contract.ReadErrorAsync(response, 403, "forbidden", "pcMismatch");
        }

        Assert.Equal(("active", 8_800_000L), ((await CurrentAsync(b)).GetProperty("state").GetString(), await Players.BalanceAsync(Server, player.Id)));
    }

    [Fact]
    public async Task Extend_adds_time_and_a_package_brings_its_own_minutes()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player);
        var id = created.GetProperty("id").GetGuid();
        Server.Clock.Advance(TimeSpan.FromMinutes(20));

        var extended = await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{id}/extend", new { minutes = 30 }, Guid.NewGuid()), 200);
        Assert.Equal(2400 + 1800, extended.GetProperty("secondsLeft").GetInt32());
        Assert.Equal(1_800_000, extended.GetProperty("cost").GetProperty("amount").GetInt64());

        var package = Guid.NewGuid();
        await Players.ExecuteAsync(Server,
            "INSERT INTO tariffs (id, club_id, name, price_per_hour, min_minutes, max_minutes, is_package, package_minutes, package_price) SELECT @package, id, 'Pack 3h', 600000, 180, 180, true, 180, 1500000 FROM clubs",
            new { package });
        var packed = await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{id}/extend", new { minutes = 5, tariffId = package }, Guid.NewGuid()), 200);
        Assert.Equal(4200 + (180 * 60), packed.GetProperty("secondsLeft").GetInt32());
        Assert.Equal(package, packed.GetProperty("tariffId").GetGuid());
        Assert.Equal(10_000_000 - 1_200_000 - 600_000 - 1_500_000, await Players.BalanceAsync(Server, player.Id));
    }

    [Fact]
    public async Task Extend_is_refused_past_the_tariff_maximum_and_for_postpaid()
    {
        var (agent, player) = await Players.SignedInAsync(Server, balance: 20_000_000);
        var (_, created) = await Players.StartAsync(agent, player, minutes: 700);
        using (var over = await agent.PostAsync($"/api/v1/sessions/{created.GetProperty("id").GetGuid()}/extend", new { minutes = 30 }, Guid.NewGuid()))
        {
            var error = await Contract.ReadErrorAsync(over, 400, "validation");
            Assert.Equal("max", error.GetProperty("error").GetProperty("details").GetProperty("reason").GetString());
        }

        var (other, postpaidPlayer) = await Players.SignedInAsync(Server);
        var (_, postpaid) = await Players.StartAsync(other, postpaidPlayer, prepaid: false);
        Assert.Equal(-1, postpaid.GetProperty("secondsLeft").GetInt32());
        using var refused = await other.PostAsync($"/api/v1/sessions/{postpaid.GetProperty("id").GetGuid()}/extend", new { minutes = 30 }, Guid.NewGuid());
        await Contract.ReadErrorAsync(refused, 409, "conflict", "postpaidSession");
    }

    [Fact]
    public async Task Current_session_is_the_own_pcs_or_204()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        using (var none = await agent.SendAsync(HttpMethod.Get, $"/api/v1/sessions/current?pcId={agent.PcId}"))
        {
            Assert.Equal(204, (int)none.StatusCode);
        }

        using (var foreign = await agent.SendAsync(HttpMethod.Get, $"/api/v1/sessions/current?pcId={Guid.NewGuid()}"))
        {
            await Contract.ReadErrorAsync(foreign, 403, "forbidden", "pcMismatch");
        }

        var (_, created) = await Players.StartAsync(agent, player);
        Assert.Equal(created.GetProperty("id").GetGuid(), (await CurrentAsync(agent)).GetProperty("id").GetGuid());

        // The heartbeat derives the PC status from the open session.
        var beat = await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        Assert.Equal("busy", beat.GetProperty("pcStatus").GetString());
    }

    [Fact]
    public async Task Pause_of_another_players_session_is_403_not_owner()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player);
        var (stranger, _) = await Players.SignedInAsync(Server);
        using var response = await stranger.PostAsync($"/api/v1/sessions/{created.GetProperty("id").GetGuid()}/pause", null);
        await Contract.ReadErrorAsync(response, 403, "forbidden", "notOwner");
        using var missing = await stranger.PostAsync($"/api/v1/sessions/{Guid.NewGuid()}/pause", null);
        Assert.Equal("session", Details(await Contract.ReadErrorAsync(missing, 404, "notFound")).GetProperty("what").GetString());
    }

    private static async Task<JsonElement> CurrentAsync(TestAgent agent)
    {
        using var response = await agent.SendAsync(HttpMethod.Get, $"/api/v1/sessions/current?pcId={agent.PcId}");
        Assert.Equal(200, (int)response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
