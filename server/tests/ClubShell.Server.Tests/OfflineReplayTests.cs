using System.Text.Json;
using static ClubShell.Server.Tests.PurchaseRulesTests;

namespace ClubShell.Server.Tests;

/// <summary>
/// Offline replay of <c>POST /sessions</c> (DESIGN §5.11, D-11): the agent token alone is enough, the session runs from
/// <c>startedAt</c> under <c>clientSessionId</c>, is priced then and charged with overdraft; too old is 400; events sent
/// before the replay get 404 and their key is not kept, so the same batch succeeds after it.
/// </summary>
public sealed class OfflineReplayTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    [Fact]
    public async Task Replay_with_the_agent_token_alone_runs_from_startedAt_with_overdraft()
    {
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server, balance: 0);
        var clientId = Guid.NewGuid();
        var startedAt = Server.Clock.GetUtcNow().AddMinutes(-30);

        var session = await Players.ReadAsync(await agent.PostAsync("/api/v1/sessions", Replay(agent, player, clientId, startedAt), Guid.NewGuid()), 201);
        Assert.Equal(clientId, session.GetProperty("id").GetGuid());
        Assert.Equal(startedAt, session.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal((1800, 1800), (session.GetProperty("secondsUsed").GetInt32(), session.GetProperty("secondsLeft").GetInt32()));
        Assert.Equal(-1_200_000, await Players.BalanceAsync(Server, player.Id));
        Assert.Equal("offline", await Players.ScalarAsync<string>(Server, "SELECT origin FROM sessions WHERE id = @clientId", new { clientId }));
        Assert.True(await Players.ScalarAsync<bool>(Server, "SELECT overdraft FROM ledger_entries WHERE session_id = @clientId", new { clientId }));
    }

    [Fact]
    public async Task Replay_after_an_evening_offline_is_still_charged()
    {
        // Older than the agent's 240-minute budget: the club's internet was down longer, the game was still played.
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server);
        var clientId = Guid.NewGuid();
        await Players.ReadAsync(await agent.PostAsync("/api/v1/sessions", Replay(agent, player, clientId, Server.Clock.GetUtcNow().AddHours(-5)), Guid.NewGuid()), 201);
        Assert.Equal("offline", await Players.ScalarAsync<string>(Server, "SELECT origin FROM sessions WHERE id = @clientId", new { clientId }));
        Assert.True(await Players.BalanceAsync(Server, player.Id) < 10_000_000);
    }

    [Fact]
    public async Task Replay_older_than_three_days_is_400_too_old()
    {
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server);
        using var response = await agent.PostAsync("/api/v1/sessions", Replay(agent, player, Guid.NewGuid(), Server.Clock.GetUtcNow().AddHours(-73)), Guid.NewGuid());
        var error = await Contract.ReadErrorAsync(response, 400, "validation");
        Assert.Equal(("startedAt", "tooOld"), (Details(error).GetProperty("field").GetString(), Details(error).GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task A_second_replay_of_the_same_client_session_returns_it_without_a_second_charge()
    {
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server);
        var clientId = Guid.NewGuid();
        var body = Replay(agent, player, clientId, Server.Clock.GetUtcNow().AddMinutes(-5));
        var key = Guid.NewGuid();
        await Players.ReadAsync(await agent.PostAsync("/api/v1/sessions", body, key), 201);

        // Same key, fuller body: the stored answer. Another key (agent restarted): the same session, nothing charged again.
        using (var replayed = await agent.PostAsync("/api/v1/sessions", Replay(agent, player, clientId, Server.Clock.GetUtcNow().AddMinutes(-4), minutes: 90), key))
        {
            Assert.Equal(clientId, (await Players.ReadAsync(replayed, 201)).GetProperty("id").GetGuid());
            Assert.Equal("true", replayed.Headers.GetValues("Idempotent-Replayed").Single());
        }

        Assert.Equal(clientId, (await Players.ReadAsync(await agent.PostAsync("/api/v1/sessions", body, Guid.NewGuid()), 201)).GetProperty("id").GetGuid());
        Assert.Equal(10_000_000 - 1_200_000, await Players.BalanceAsync(Server, player.Id));
    }

    [Fact]
    public async Task Postpaid_replay_ignores_the_minute_range_and_a_banned_player_is_refused()
    {
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server);
        var postpaid = await Players.ReadAsync(await agent.PostAsync("/api/v1/sessions",
            Replay(agent, player, Guid.NewGuid(), Server.Clock.GetUtcNow().AddMinutes(-1), minutes: 5000, prepaid: false), Guid.NewGuid()), 201);
        Assert.Equal(-1, postpaid.GetProperty("secondsLeft").GetInt32());

        var other = await TestAgent.CreateAsync(Server);
        var banned = await Players.CreateAsync(Server);
        await Players.ExecuteAsync(Server, "UPDATE users SET banned = true WHERE id = @Id", new { banned.Id });
        using var refused = await other.PostAsync("/api/v1/sessions", Replay(other, banned, Guid.NewGuid(), Server.Clock.GetUtcNow()), Guid.NewGuid());
        Assert.Equal("blacklisted", Details(await Contract.ReadErrorAsync(refused, 403, "policyDenied")).GetProperty("rule").GetString());
    }

    /// <summary>§5.11: the agent already let the game happen; a tariff deleted, a PC put in maintenance or moved out of the tariff's zone meanwhile does not lose it.</summary>
    [Fact]
    public async Task Replay_ignores_a_deleted_tariff_maintenance_and_the_tariff_zone()
    {
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server, balance: 0);
        var tariff = Guid.NewGuid();
        await Players.ExecuteAsync(Server,
            "INSERT INTO tariffs (id, club_id, name, price_per_hour, min_minutes, zones, deleted_at) SELECT @tariff, id, 'Gone', 1200000, 30, ARRAY['VIP'], now() FROM clubs",
            new { tariff });
        await Players.ExecuteAsync(Server, "UPDATE pcs SET maintenance = true WHERE id = @PcId", new { agent.PcId });
        var clientId = Guid.NewGuid();

        var session = await Players.ReadAsync(await agent.PostAsync("/api/v1/sessions",
            Replay(agent, player, clientId, Server.Clock.GetUtcNow().AddMinutes(-120), minutes: 120, tariff: tariff), Guid.NewGuid()), 201);
        Assert.Equal(clientId, session.GetProperty("id").GetGuid());
        Assert.Equal(-2_400_000, await Players.BalanceAsync(Server, player.Id)); // 2 h of play recorded and charged
    }

    [Fact]
    public async Task Events_before_the_replay_are_404_and_the_same_batch_succeeds_after_it()
    {
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server);
        var clientId = Guid.NewGuid();
        var startedAt = Server.Clock.GetUtcNow().AddMinutes(-20);
        var key = Guid.NewGuid();
        var batch = EventsTests.Body(clientId, ("started", startedAt, null), ("paused", startedAt.AddMinutes(5), null), ("resumed", startedAt.AddMinutes(10), null));

        using (var early = await agent.PostAsync($"/api/v1/sessions/{clientId}/events", batch, key))
        {
            Assert.Equal("session", Details(await Contract.ReadErrorAsync(early, 404, "notFound")).GetProperty("what").GetString());
        }

        Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT count(*)::int FROM idempotency_keys WHERE key = @key", new { key }));
        await Players.ReadAsync(await agent.PostAsync("/api/v1/sessions", Replay(agent, player, clientId, startedAt), Guid.NewGuid()), 201);

        using (var late = await agent.PostAsync($"/api/v1/sessions/{clientId}/events", batch, key))
        {
            await Players.ReadAsync(late, 204);
        }

        using var current = await agent.SendAsync(HttpMethod.Get, $"/api/v1/sessions/current?pcId={agent.PcId}");
        Assert.Equal(900, (await Players.ReadAsync(current, 200)).GetProperty("secondsUsed").GetInt32()); // 20 min minus the 5 min offline pause
    }

    internal static object Replay(TestAgent agent, TestPlayer player, Guid clientId, DateTimeOffset startedAt, int minutes = 60, bool prepaid = true, Guid? tariff = null) =>
        new { pcId = agent.PcId, userId = player.Id, tariffId = tariff ?? Players.Standard, minutes, prepaid, startedAt, clientSessionId = clientId };
}
