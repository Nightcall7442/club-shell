using ClubShell.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace ClubShell.Server.Tests;

/// <summary>
/// <see cref="SessionTickWorker"/> with the fixture's clock (DESIGN §5.10): ending at <c>ends_at</c>, <c>timeUp</c> after
/// the free grace with an <c>endSession</c> command for the agent; a PC that is offline is left alone until 240 min of
/// silence; postpaid stops when its cost reaches the balance (D-10). A PC is online while its heartbeat is fresh.
/// </summary>
public sealed class TickTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    private SessionTickWorker Tick => Server.Services.GetRequiredService<SessionTickWorker>();

    [Fact]
    public async Task Prepaid_goes_ending_then_time_up_after_the_free_grace()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player, minutes: 30);
        var id = created.GetProperty("id").GetGuid();

        await AdvanceOnlineAsync(agent, TimeSpan.FromMinutes(30));
        Assert.Equal("ending", await StateAsync(id));

        await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(59));
        Assert.Equal("ending", await StateAsync(id));

        await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(1));
        Assert.Equal("ended", await StateAsync(id));
        Assert.Equal("timeUp", await Players.ScalarAsync<string>(Server, "SELECT end_reason FROM sessions WHERE id = @id", new { id }));
        Assert.Equal(1800, await Players.ScalarAsync<int>(Server, "SELECT used_before_sec FROM sessions WHERE id = @id", new { id }));
        Assert.Equal(9_400_000, await Players.BalanceAsync(Server, player.Id)); // 30 min, no refund on timeUp

        using var commands = await agent.SendAsync(HttpMethod.Get, agent.Path("commands"));
        var command = Assert.Single((await Players.ReadAsync(commands, 200)).GetProperty("items").EnumerateArray());
        Assert.Equal("endSession", command.GetProperty("name").GetString());
        Assert.Equal(id, command.GetProperty("payload").GetProperty("sessionId").GetGuid());
        Assert.Equal("timeUp", command.GetProperty("payload").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Offline_pc_is_left_to_its_agent_until_240_minutes_of_silence()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        var (_, created) = await Players.StartAsync(agent, player, minutes: 30);
        var id = created.GetProperty("id").GetGuid();

        Server.Clock.Advance(TimeSpan.FromMinutes(239));
        await Tick.RunOnceAsync();
        Assert.Equal("active", await StateAsync(id));

        Server.Clock.Advance(TimeSpan.FromMinutes(1));
        await Tick.RunOnceAsync();
        Assert.Equal("ended", await StateAsync(id));
        Assert.Equal(0, await Players.ScalarAsync<int>(Server, "SELECT used_before_sec FROM sessions WHERE id = @id", new { id })); // at the last heartbeat
    }

    /// <summary>D-10 at the minute boundary: 50 000 at 20 000/min pays 2 minutes (40 000); the 3rd is not started, the 0 limit holds.</summary>
    [Fact]
    public async Task Postpaid_stops_at_the_last_minute_the_balance_pays()
    {
        var (agent, player) = await Players.SignedInAsync(Server, balance: 50_000);
        var (_, created) = await Players.StartAsync(agent, player, prepaid: false);
        var id = created.GetProperty("id").GetGuid();

        await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(119)); // the 2nd minute is paid for
        Assert.Equal("active", await StateAsync(id));

        await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(1)); // 120 s: the next second would start a 3rd minute (60 000)
        Assert.Equal("ended", await StateAsync(id));
        Assert.Equal(10_000, await Players.BalanceAsync(Server, player.Id));
    }

    /// <summary>A tick 30 s late still stops at 120 s: the unaffordable 3rd minute is neither played for free nor charged.</summary>
    [Fact]
    public async Task A_late_tick_stops_postpaid_at_the_minute_boundary()
    {
        var (agent, player) = await Players.SignedInAsync(Server, balance: 50_000);
        var (_, created) = await Players.StartAsync(agent, player, prepaid: false);
        var id = created.GetProperty("id").GetGuid();

        await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(150));
        Assert.Equal(("ended", 120), (await StateAsync(id), await Players.ScalarAsync<int>(Server, "SELECT used_before_sec FROM sessions WHERE id = @id", new { id })));
        Assert.Equal(10_000, await Players.BalanceAsync(Server, player.Id));
    }

    /// <summary>A postpaid start on a wallet that cannot pay the first minute is 402 (it was 201, then ended 1 s later with a 20 000 overdraft).</summary>
    [Fact]
    public async Task Postpaid_on_a_wallet_short_of_one_minute_is_402()
    {
        var (agent, player) = await Players.SignedInAsync(Server, balance: 19_900);
        var (status, body) = await Players.StartAsync(agent, player, prepaid: false);
        Assert.Equal(402, status);
        Contract.AssertError(body, "insufficientFunds");
        Assert.Equal(19_900, await Players.BalanceAsync(Server, player.Id));
    }

    /// <summary>
    /// §5.10: a PC back from offline is not settled before its queued events land. Postpaid at 20 000/min on 1 000 000,
    /// the player ends offline at 30 min, the PC reports back at 120 min with 1 event queued: the tick waits, and the
    /// agent's end bills 30 min (600 000) — not 121 min (2 420 000) into overdraft.
    /// </summary>
    [Fact]
    public async Task A_pc_back_from_offline_is_not_settled_before_its_queued_events()
    {
        var (agent, player) = await Players.SignedInAsync(Server, balance: 1_000_000);
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        var (_, created) = await Players.StartAsync(agent, player, prepaid: false);
        var id = created.GetProperty("id").GetGuid();
        var t0 = Server.Clock.GetUtcNow();

        Server.Clock.Advance(TimeSpan.FromMinutes(120));
        await Players.ReadAsync(await agent.HeartbeatAsync(offlineQueue: 1), 200);
        await Tick.RunOnceAsync();
        Assert.Equal("active", await StateAsync(id));

        await EventsTests.PostAsync(agent, id, Guid.NewGuid(), ("ended", t0.AddMinutes(30), new { reason = "user" }));
        await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(1));
        Assert.Equal(400_000, await Players.BalanceAsync(Server, player.Id));
    }

    /// <summary>
    /// An offline session replayed with its creation minutes (60) and extended offline (+60 at 50 min): the tick must
    /// not end it at 60 + grace before the <c>extended</c> event lands (the player kept 40 paid minutes).
    /// </summary>
    [Fact]
    public async Task An_offline_session_is_not_ended_before_its_queued_extension()
    {
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server);
        var clientId = Guid.NewGuid();
        var t0 = Server.Clock.GetUtcNow().AddMinutes(-80);
        await Players.ReadAsync(await agent.HeartbeatAsync(offlineQueue: 2), 200);
        await Players.ReadAsync(await agent.PostAsync("/api/v1/sessions", OfflineReplayTests.Replay(agent, player, clientId, t0), Guid.NewGuid()), 201);
        await Tick.RunOnceAsync();
        Assert.Equal("active", await StateAsync(clientId));

        await EventsTests.PostAsync(agent, clientId, Guid.NewGuid(), ("extended", t0.AddMinutes(50), new { minutes = 60, cost = new { amount = 1_200_000, currency = "UZS" } }));
        await AdvanceOnlineAsync(agent, TimeSpan.FromSeconds(1));
        Assert.Equal("active", await StateAsync(clientId));
        Assert.Equal(10_000_000 - 2_400_000, await Players.BalanceAsync(Server, player.Id));
    }

    /// <summary>
    /// The offline timeout closes at the last heartbeat (a PC switched off runs up no bill), but when the agent comes back
    /// with its own end, the offline play is billed: 230 min played, 10 of them paused → 220 min × 20 000.
    /// </summary>
    [Fact]
    public async Task The_agents_late_end_bills_the_play_after_the_offline_timeout()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        var (_, created) = await Players.StartAsync(agent, player, prepaid: false);
        var id = created.GetProperty("id").GetGuid();
        var t0 = Server.Clock.GetUtcNow();
        Server.Clock.Advance(TimeSpan.FromMinutes(240));
        await Tick.RunOnceAsync();
        Assert.Equal(("ended", 10_000_000L), (await StateAsync(id), await Players.BalanceAsync(Server, player.Id))); // closed at the last heartbeat

        await EventsTests.PostAsync(agent, id, Guid.NewGuid(),
            ("paused", t0.AddMinutes(10), null), ("resumed", t0.AddMinutes(20), null), ("ended", t0.AddMinutes(230), new { reason = "user" }));
        Assert.Equal(10_000_000 - 4_400_000, await Players.BalanceAsync(Server, player.Id));
        Assert.Equal(t0.AddMinutes(230), await Players.ScalarAsync<DateTimeOffset>(Server, "SELECT ended_at FROM sessions WHERE id = @id", new { id }));
    }

    /// <summary>100 expired sessions of offline PCs (earliest ends_at) no longer fill the tick's batch and starve an online PC.</summary>
    [Fact]
    public async Task Sessions_of_offline_pcs_do_not_starve_online_ones()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var salt = Guid.NewGuid().ToString("N");
        var now = Server.Clock.GetUtcNow();
        await Players.ExecuteAsync(Server,
            """
            INSERT INTO pcs (id, club_id, number, name, last_heartbeat_at) SELECT md5(@salt || 'pc' || i)::uuid, id, i, 'dark ' || i, @dark FROM clubs, generate_series(1, 100) i;
            INSERT INTO users (id, network_id, username, display_name, created_at) SELECT md5(@salt || 'u' || i)::uuid, network_id, 'dark-' || left(@salt, 8) || '-' || i, 'dark', @now FROM clubs, generate_series(1, 100) i;
            INSERT INTO wallets (user_id, network_id, updated_at) SELECT md5(@salt || 'u' || i)::uuid, network_id, @now FROM clubs, generate_series(1, 100) i;
            INSERT INTO sessions (id, club_id, pc_id, user_id, tariff_id, state, is_prepaid, origin, started_at, purchased_sec, running_since, ends_at,
                                  last_transition_at, price_per_hour_snapshot, day_pct, discount_pct, created_at, updated_at)
            SELECT md5(@salt || 's' || i)::uuid, id, md5(@salt || 'pc' || i)::uuid, md5(@salt || 'u' || i)::uuid, @Standard, 'active', true, 'kiosk',
                   @dark, 1800, @dark, @dark + interval '30 minutes', @dark, 1200000, 100, 0, @now, @now
            FROM clubs, generate_series(1, 100) i;
            """,
            new { salt, now, dark = now.AddMinutes(-60), Players.Standard });
        try
        {
            var (_, created) = await Players.StartAsync(agent, player, minutes: 30);
            await AdvanceOnlineAsync(agent, TimeSpan.FromMinutes(31));
            Assert.Equal("ended", await StateAsync(created.GetProperty("id").GetGuid()));
        }
        finally
        {
            await Players.ExecuteAsync(Server, "UPDATE sessions SET state = 'ended', ended_at = started_at WHERE pc_id IN (SELECT id FROM pcs WHERE name LIKE 'dark %')");
        }
    }

    private async Task AdvanceOnlineAsync(TestAgent agent, TimeSpan by)
    {
        Server.Clock.Advance(by);
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        await Tick.RunOnceAsync();
    }

    private Task<string> StateAsync(Guid id) => Players.ScalarAsync<string>(Server, "SELECT state FROM sessions WHERE id = @id", new { id });
}

/// <summary>With <c>Workers:Enabled</c> the tick runs as a hosted loop under its advisory lock (DESIGN §8).</summary>
public sealed class TickWorkerHostedTests(TickWorkerHostedTests.Fixture server) : LedgerCheckedTest(server), IClassFixture<TickWorkerHostedTests.Fixture>
{
    public sealed class Fixture : ServerFixture
    {
        public Fixture()
        {
            Settings["Workers:Enabled"] = "true";
            Settings["Sessions:TickMs"] = "50";
        }
    }

    [Fact]
    public async Task The_hosted_loop_ends_a_session_that_ran_out()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player, minutes: 30);
        Server.Clock.Advance(TimeSpan.FromMinutes(31));
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);

        var id = created.GetProperty("id").GetGuid();
        for (var i = 0; i < 100 && await Players.ScalarAsync<string>(Server, "SELECT state FROM sessions WHERE id = @id", new { id }) != "ended"; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal("ended", await Players.ScalarAsync<string>(Server, "SELECT state FROM sessions WHERE id = @id", new { id }));
        Assert.True(await Players.ScalarAsync<bool>(Server, "SELECT granted FROM pg_locks WHERE locktype = 'advisory' AND database = (SELECT oid FROM pg_database WHERE datname = current_database()) AND objid = (@key & 4294967295)::oid AND classid = (@key >> 32)::oid",
            new { key = Infrastructure.AdvisoryLocks.Sessions }));
    }
}
