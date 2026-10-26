using System.Net.WebSockets;
using ClubShell.Server.Agents;
using ClubShell.Server.Realtime;
using Microsoft.Extensions.DependencyInjection;

namespace ClubShell.Server.Tests;

/// <summary>
/// SHELL_CHANGES 44: an agent that closes its socket itself (it stops: Windows shuts down or restarts, the service stops)
/// leaves <c>pcs.goodbye_at</c>; its answer to the server's close does not, and connecting again clears it.
/// </summary>
public sealed class GoodbyeSocketTests(KestrelServerFixture server) : IClassFixture<KestrelServerFixture>
{
    private AgentSocketHub Hub => server.Services.GetRequiredService<AgentSocketHub>();

    [Fact]
    public async Task The_agents_own_close_is_recorded_and_a_new_connection_clears_it()
    {
        var agent = await TestAgent.CreateAsync(server);
        using (var socket = await WsTestSocket.ConnectAsync(server, agent.AccessToken))
        {
            await Wait.UntilAsync(() => Hub.IsConnected(agent.PcId));
            await socket.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
        }

        await Wait.UntilAsync(async () => await GoodbyeAsync(agent) is not null);

        using var again = await WsTestSocket.ConnectAsync(server, agent.AccessToken);
        await Wait.UntilAsync(async () => await GoodbyeAsync(agent) is null);

        // Replaced by a new connection: the server closes the old one first, and the agent's answer is not a goodbye.
        using var replacing = await WsTestSocket.ConnectAsync(server, agent.AccessToken);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await again.ClosedAsync());
        await Wait.UntilAsync(() => Hub.IsConnected(agent.PcId));
        Assert.Null(await GoodbyeAsync(agent));
    }

    private Task<DateTimeOffset?> GoodbyeAsync(TestAgent agent) =>
        Players.ScalarAsync<DateTimeOffset?>(server, "SELECT goodbye_at FROM pcs WHERE id = @PcId", new { agent.PcId });
}

/// <summary>
/// SHELL_CHANGES 44: a PC that goes offline within <c>Agents:PlannedOfflineMin</c> of a <c>reboot</c>/<c>shutdown</c> command
/// or of its agent's own socket close gets no <c>pcOffline</c> incident (nor its webhook); later, or without either, it does.
/// </summary>
public sealed class PlannedOfflineTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task A_planned_stop_is_not_an_incident()
    {
        var (rebooted, stopped, stale, crashed) =
            (await TestAgent.CreateAsync(server), await TestAgent.CreateAsync(server), await TestAgent.CreateAsync(server), await TestAgent.CreateAsync(server));
        foreach (var agent in new[] { rebooted, stopped, stale, crashed })
        {
            await Players.ReadAsync(await agent.HeartbeatAsync(), 200);
        }

        var worker = server.Services.GetRequiredService<PcStatusWorker>();
        await worker.RunOnceAsync();
        var now = server.Clock.GetUtcNow();
        await Players.ExecuteAsync(server,
            """
            INSERT INTO agent_commands (id, club_id, pc_id, name, payload, created_at)
            SELECT gen_random_uuid(), club_id, id, 'reboot', '{"delaySec":5,"force":false}'::jsonb, @now FROM pcs WHERE id = @rebooted
            """,
            new { now, rebooted = rebooted.PcId });
        await Players.ExecuteAsync(server, "UPDATE pcs SET goodbye_at = @now WHERE id = @PcId", new { now, stopped.PcId });
        await Players.ExecuteAsync(server, "UPDATE pcs SET goodbye_at = @at WHERE id = @PcId", new { at = now.AddMinutes(-20), stale.PcId });

        server.Clock.Advance(TimeSpan.FromSeconds(91));
        await worker.RunOnceAsync();
        await worker.RunOnceAsync();

        foreach (var (agent, incidents) in new[] { (rebooted, 0), (stopped, 0), (stale, 1), (crashed, 1) })
        {
            Assert.Equal(incidents, await Players.ScalarAsync<int>(server,
                "SELECT count(*)::int FROM telemetry_events WHERE pc_id = @PcId AND kind = 'pcOffline'", new { agent.PcId }));
        }
    }
}
