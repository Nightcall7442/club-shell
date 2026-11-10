using System.Text.Json;
using ClubShell.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace ClubShell.Server.Tests;

/// <summary>
/// What a player token may do to a session (SERVER_API.md §4.5): end it as the player (<c>user</c>, <c>idle</c>) or as the
/// agent's timer (<c>timeUp</c>, <c>agentRestart</c>), never refunded; <c>admin</c>/<c>error</c> — the desk's and the
/// server's, the ones that refund unused time — are <c>403 staffOnly</c>, and so are pause and resume. And the end the agent
/// replays for a session it created offline is settled when it really ended, not when the PC came back. Standard costs
/// 1 200 000 tiyin an hour.
/// </summary>
public sealed class ClientPowersTests(LongClockServerFixture server) : LedgerCheckedTest(server), IClassFixture<LongClockServerFixture>
{
    [Fact]
    public async Task A_player_ending_with_admin_or_error_is_refused_and_gets_no_refund()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player);
        var id = created.GetProperty("id").GetGuid();
        Server.Clock.Advance(TimeSpan.FromMinutes(10));

        foreach (var reason in new[] { "admin", "error" })
        {
            await Contract.ReadErrorAsync(await agent.PostAsync($"/api/v1/sessions/{id}/end", new { reason, secondsUsed = 600 }), 403, "forbidden", "staffOnly");
        }

        Assert.Equal(("active", 8_800_000L), (await StateAsync(id), await Players.BalanceAsync(Server, player.Id)));

        // The player's own end is taken, without a refund of the 50 unused minutes.
        var ended = await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{id}/end", new { reason = "user", secondsUsed = 600 }), 200);
        Assert.Equal((0L, "ended"), (Amount(ended.GetProperty("refunded")), ended.GetProperty("session").GetProperty("state").GetString()));
        Assert.Equal(8_800_000, await Players.BalanceAsync(Server, player.Id));
    }

    /// <summary>The agent's own timer may end with a player signed in (timeUp, agentRestart): taken, never refunded.</summary>
    [Fact]
    public async Task The_agents_timer_reasons_are_taken_with_a_player_token_without_a_refund()
    {
        foreach (var reason in new[] { "timeUp", "agentRestart", "idle" })
        {
            var (agent, player) = await Players.SignedInAsync(Server);
            var (_, created) = await Players.StartAsync(agent, player);
            Server.Clock.Advance(TimeSpan.FromMinutes(5));
            var ended = await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{created.GetProperty("id").GetGuid()}/end", new { reason, secondsUsed = 300 }), 200);
            Assert.Equal(0, Amount(ended.GetProperty("refunded")));
            Assert.Equal(8_800_000, await Players.BalanceAsync(Server, player.Id));
        }
    }

    /// <summary>
    /// An end the server already made, echoed by the agent with <c>admin</c> while the member's token is still valid (the
    /// tick's time-up keeps a member signed in): the session is over, so 409 with the ended session (the agent's success),
    /// not 403. The agent's own <c>admin</c> end without a player token (a power command, a revoked player) is the
    /// server's end and refunds as before.
    /// </summary>
    [Fact]
    public async Task An_echo_of_a_server_end_is_409_and_an_admin_end_without_a_player_token_refunds()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player, minutes: 30);
        var id = created.GetProperty("id").GetGuid();
        Server.Clock.Advance(TimeSpan.FromMinutes(32));
        await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        await Server.Services.GetRequiredService<SessionTickWorker>().RunOnceAsync();
        Assert.Equal("ended", await StateAsync(id));
        await Contract.ReadErrorAsync(await agent.PostAsync($"/api/v1/sessions/{id}/end", new { reason = "admin", secondsUsed = 1800 }), 409, "sessionNotActive");

        var (other, otherPlayer) = await Players.SignedInAsync(Server);
        var (_, second) = await Players.StartAsync(other, otherPlayer);
        other.UserToken = null;
        var ended = await Players.ReadAsync(await other.PostAsync($"/api/v1/sessions/{second.GetProperty("id").GetGuid()}/end", new { reason = "admin", secondsUsed = 0 }), 200);
        Assert.Equal(1_200_000, Amount(ended.GetProperty("refunded")));
    }

    /// <summary>
    /// Played offline 20 minutes of postpaid an hour ago: the replayed create and its end with <c>endedAt</c> bill 20
    /// minutes (400 000), not the 60 until the replay (1 200 000). An <c>endedAt</c> in the future counts as now, one before
    /// the start as the start.
    /// </summary>
    [Fact]
    public async Task The_replayed_end_of_an_offline_session_is_settled_at_its_endedAt()
    {
        var agent = await TestAgent.CreateAsync(Server);
        var player = await Players.CreateAsync(Server);
        var startedAt = Server.Clock.GetUtcNow().AddMinutes(-60);
        var clientId = Guid.NewGuid();
        await Players.ReadAsync(await agent.PostAsync("/api/v1/sessions", OfflineReplayTests.Replay(agent, player, clientId, startedAt, prepaid: false), Guid.NewGuid()), 201);
        var ended = await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{clientId}/end",
            new { reason = "user", secondsUsed = 1200, endedAt = startedAt.AddMinutes(20) }), 200);
        Assert.Equal((400_000L, 1200), (Amount(ended.GetProperty("charged")), ended.GetProperty("session").GetProperty("secondsUsed").GetInt32()));
        Assert.Equal(startedAt.AddMinutes(20), await Players.ScalarAsync<DateTimeOffset>(Server, "SELECT ended_at FROM sessions WHERE id = @clientId", new { clientId }));
        Assert.Equal(10_000_000 - 400_000, await Players.BalanceAsync(Server, player.Id));

        // endedAt in the future: now (30 min played since the replayed start).
        var future = Guid.NewGuid();
        await Players.ReadAsync(await agent.PostAsync("/api/v1/sessions",
            OfflineReplayTests.Replay(agent, player, future, Server.Clock.GetUtcNow().AddMinutes(-30), prepaid: false), Guid.NewGuid()), 201);
        Assert.Equal(600_000, Amount((await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{future}/end",
            new { reason = "user", secondsUsed = 1800, endedAt = Server.Clock.GetUtcNow().AddHours(1) }), 200)).GetProperty("charged")));

        // endedAt before the start: the start, nothing played.
        var early = Guid.NewGuid();
        var earlyStart = Server.Clock.GetUtcNow().AddMinutes(-10);
        await Players.ReadAsync(await agent.PostAsync("/api/v1/sessions", OfflineReplayTests.Replay(agent, player, early, earlyStart, prepaid: false), Guid.NewGuid()), 201);
        Assert.Equal(0, Amount((await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{early}/end",
            new { reason = "user", secondsUsed = 0, endedAt = earlyStart.AddMinutes(-5) }), 200)).GetProperty("charged")));
        Assert.Equal(earlyStart, await Players.ScalarAsync<DateTimeOffset>(Server, "SELECT ended_at FROM sessions WHERE id = @early", new { early }));
    }

    /// <summary>An online session's end is the server's now, whatever <c>endedAt</c> the agent sends.</summary>
    [Fact]
    public async Task An_online_session_is_settled_at_the_servers_now()
    {
        var (agent, player) = await Players.SignedInAsync(Server);
        var (_, created) = await Players.StartAsync(agent, player, prepaid: false);
        var id = created.GetProperty("id").GetGuid();
        var t0 = Server.Clock.GetUtcNow();
        Server.Clock.Advance(TimeSpan.FromMinutes(30));
        var ended = await Players.ReadAsync(await agent.PostAsync($"/api/v1/sessions/{id}/end",
            new { reason = "user", secondsUsed = 60, endedAt = t0.AddMinutes(1) }), 200);
        Assert.Equal(600_000, Amount(ended.GetProperty("charged")));
    }

    private static long Amount(JsonElement money) => money.GetProperty("amount").GetInt64();

    private Task<string> StateAsync(Guid id) => Players.ScalarAsync<string>(Server, "SELECT state FROM sessions WHERE id = @id", new { id });
}
