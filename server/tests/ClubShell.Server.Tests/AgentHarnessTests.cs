using System.Net;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Pcs;
using ClubShell.Core.Abstractions;
using ClubShell.Server.Agents;
using ClubShell.Server.Realtime;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ClubShell.Server.Tests;

/// <summary>A fixture where registration waits for the owner (production default, D-7).</summary>
public sealed class ApprovalServerFixture : ServerFixture
{
    public ApprovalServerFixture() => Settings["Club:AutoApprovePcs"] = "false";
}

/// <summary>
/// Slice S1 through the real agent <see cref="ServerClient"/> over the in-process handler (DESIGN §10.b): register →
/// pendingApproval → approval → register → heartbeat → config → policies (304 on the second) → commands/ack →
/// telemetry; access expiry → reactive refresh; <c>clockSkew</c> → offset correction and one retry.
/// </summary>
public sealed class AgentHarnessRestTests(ApprovalServerFixture server) : IClassFixture<ApprovalServerFixture>
{
    [Fact]
    public async Task S1_scenario_through_the_real_server_client()
    {
        await using var agent = await AgentHarness.CreateAsync(server);
        var client = agent.Client;

        var pending = await Assert.ThrowsAsync<ServerApiException>(() => client.RegisterAsync(agent.RegisterRequest(), CancellationToken.None));
        Assert.Equal(ErrorCode.Forbidden, pending.Code);
        Assert.Equal("pendingApproval", pending.Reason);
        var pcId = pending.Error!.Details!.Value.GetProperty("pcId").GetGuid();

        // The owner approves (PATCH /admin/pcs/{pcId} {maintenance:false} arrives with S4).
        await using (var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        {
            await c.ExecuteAsync("UPDATE pcs SET approved = true, maintenance = false WHERE id = @pcId", new { pcId });
        }

        var registered = await client.RegisterAsync(agent.RegisterRequest(), CancellationToken.None);
        Assert.Equal(pcId, registered.PcId);
        Assert.Equal(pcId, agent.PcId);

        var heartbeat = await client.HeartbeatAsync(pcId, AgentHarness.Heartbeat(), CancellationToken.None);
        Assert.Equal(PcStatus.Free, heartbeat.PcStatus);
        Assert.Equal(0, heartbeat.PendingCommands);

        var config = await client.GetConfigAsync(pcId, null, CancellationToken.None);
        Assert.Equal(heartbeat.ConfigVersion, config.Value!.Version);
        Assert.Equal($"\"c{heartbeat.ConfigVersion}\"", config.ETag);

        var policy = await client.GetPoliciesAsync(pcId, null, CancellationToken.None);
        Assert.Equal(heartbeat.PolicyVersion, policy.Value!.Version);
        var again = await client.GetPoliciesAsync(pcId, policy.ETag, CancellationToken.None);
        Assert.True(again.NotModified);

        // A command queued while the PC has no socket: pendingCommands → GET /commands → REST ack.
        var queued = await server.Services.GetRequiredService<CommandDispatcher>().EnqueueAsync(
            await ClubOfAsync(pcId), pcId, NewCommand.Lock(new LockCommand("staff", "Подойдите к стойке")));
        Assert.Equal(1, (await client.HeartbeatAsync(pcId, AgentHarness.Heartbeat(), CancellationToken.None)).PendingCommands);
        var command = ServerCommand.FromEnvelope(Assert.Single((await client.GetCommandsAsync(pcId, CancellationToken.None)).Items));
        Assert.Equal(ServerCommandType.Lock, command.Type);
        Assert.Equal("Подойдите к стойке", command.PayloadAs<LockCommand>()!.Message);
        await client.AckCommandAsync(pcId, command.Id, CommandAck.Success(), CancellationToken.None);
        Assert.True((await server.Services.GetRequiredService<CommandRepository>().WaitForAckAsync(queued.Id, TimeSpan.FromSeconds(5), CancellationToken.None))!.Ok);
        Assert.Equal(0, (await client.HeartbeatAsync(pcId, AgentHarness.Heartbeat(), CancellationToken.None)).PendingCommands);

        await client.SendTelemetryAsync(pcId, new TelemetryBatch(
            [new PcMetrics(10, 20, 8000, new Temperatures(50, 60), null, new NetworkThroughput(1, 2), 3600, agent.Clock.GetUtcNow())],
            [new TelemetryEvent(TelemetryEventKinds.ShellCrash, agent.Clock.GetUtcNow(), System.Text.Json.JsonElement.Parse("""{"exitCode":1}"""))]),
            CancellationToken.None);

        // Access token expired on both clocks: the 401 expired triggers one refresh and the call is repeated.
        var refreshToken = agent.Tokens.Agent!.RefreshToken;
        server.Clock.Advance(TimeSpan.FromMinutes(61));
        agent.Clock.Advance(TimeSpan.FromMinutes(61));
        await client.HeartbeatAsync(pcId, AgentHarness.Heartbeat(), CancellationToken.None);
        Assert.NotEqual(refreshToken, agent.Tokens.Agent!.RefreshToken);

        // The agent's clock runs 10 min ahead: 401 clockSkew → offset from X-Server-Time → one retry succeeds.
        agent.Clock.Advance(TimeSpan.FromMinutes(10));
        await client.HeartbeatAsync(pcId, AgentHarness.Heartbeat(), CancellationToken.None);
        Assert.InRange(client.ServerTimeOffset, TimeSpan.FromMinutes(-10) - TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(-10) + TimeSpan.FromSeconds(2));
        Assert.Equal(server.Clock.GetUtcNow(), client.ServerNow, TimeSpan.FromSeconds(2));
    }

    private async Task<Guid> ClubOfAsync(Guid pcId)
    {
        await using var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        return await c.QuerySingleAsync<Guid>("SELECT club_id FROM pcs WHERE id = @pcId", new { pcId });
    }
}

/// <summary>
/// The real <c>RealtimeClient</c> against Kestrel (DESIGN §10.b, S1 exit): handshake and keepalive, <c>lock</c> over
/// the live socket acked over it, <c>lock</c> while the socket is down drained over REST, replacement 1000, 4401 at
/// <c>exp</c>.
/// </summary>
public sealed class AgentHarnessRealtimeTests(KestrelServerFixture server) : IClassFixture<KestrelServerFixture>
{
    private AgentSocketHub Hub => server.Services.GetRequiredService<AgentSocketHub>();

    [Fact]
    public async Task Lock_on_the_live_socket_is_acked_over_ws_and_lock_while_down_over_rest()
    {
        await using var agent = await AgentHarness.CreateAsync(server);
        var registered = await agent.Client.RegisterAsync(agent.RegisterRequest(), CancellationToken.None);
        var pcId = registered.PcId;
        var dispatcher = server.Services.GetRequiredService<CommandDispatcher>();
        var commands = server.Services.GetRequiredService<CommandRepository>();
        var club = await ClubOfAsync(pcId);

        var handled = new List<ServerCommand>();
        agent.Realtime.CommandHandler = (command, _) =>
        {
            lock (handled)
            {
                handled.Add(command);
            }

            return Task.FromResult(CommandAck.Success());
        };
        using var stop = new CancellationTokenSource();
        var run = agent.Realtime.RunAsync(null, stop.Token);
        await Wait.UntilAsync(() => Hub.IsConnected(pcId));

        // Server pings every second in this fixture; the real client answers, the connection survives several rounds.
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.True(agent.Realtime.IsConnected);

        var live = await dispatcher.EnqueueAsync(club, pcId, NewCommand.Lock(new LockCommand("staff")));
        var ack = await commands.WaitForAckAsync(live.Id, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(ack!.Ok);
        Assert.Equal(ServerCommandType.Lock, Assert.Single(handled).Type);
        Assert.Equal(0, (await agent.Client.HeartbeatAsync(pcId, AgentHarness.Heartbeat(), CancellationToken.None)).PendingCommands);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await Wait.UntilAsync(() => !Hub.IsConnected(pcId));

        var down = await dispatcher.EnqueueAsync(club, pcId, NewCommand.Lock(new LockCommand("staff")));
        Assert.Equal(1, (await agent.Client.HeartbeatAsync(pcId, AgentHarness.Heartbeat(), CancellationToken.None)).PendingCommands);
        var item = Assert.Single((await agent.Client.GetCommandsAsync(pcId, CancellationToken.None)).Items);
        Assert.Equal(down.Id, item.Id);
        await agent.Client.AckCommandAsync(pcId, item.Id, CommandAck.Success(), CancellationToken.None);
        Assert.True((await commands.WaitForAckAsync(down.Id, TimeSpan.FromSeconds(5), CancellationToken.None))!.Ok);
        Assert.Equal(0, (await agent.Client.HeartbeatAsync(pcId, AgentHarness.Heartbeat(), CancellationToken.None)).PendingCommands);
    }

    [Fact]
    public async Task A_second_connection_of_the_pc_replaces_the_first_with_1000()
    {
        await using var agent = await AgentHarness.CreateAsync(server);
        var pcId = (await agent.Client.RegisterAsync(agent.RegisterRequest(), CancellationToken.None)).PcId;
        var second = ActivatorUtilities.CreateInstance<ClubShell.Core.Realtime.RealtimeClient>(agent.Services);

        using var stop = new CancellationTokenSource();
        var first = agent.Realtime.RunAsync(null, stop.Token);
        await Wait.UntilAsync(() => agent.Realtime.IsConnected);
        var replacing = second.RunAsync(null, stop.Token);

        // 1000 is a normal close: RunAsync returns without an error and the agent's Reconnector would dial again.
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        await Wait.UntilAsync(() => second.IsConnected && Hub.IsConnected(pcId));
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replacing);
    }

    [Fact]
    public async Task Access_token_expiry_closes_4401_and_the_client_reports_unauthorized()
    {
        await using var agent = await AgentHarness.CreateAsync(server);
        await agent.Client.RegisterAsync(agent.RegisterRequest(), CancellationToken.None);
        server.Clock.Advance(TimeSpan.FromMinutes(60) - TimeSpan.FromSeconds(2));

        var error = await Assert.ThrowsAsync<ServerApiException>(() => agent.Realtime.RunAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(ErrorCode.Unauthorized, error.Code);
        Assert.Equal(HttpStatusCode.Unauthorized, error.Status);
    }

    private async Task<Guid> ClubOfAsync(Guid pcId)
    {
        await using var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        return await c.QuerySingleAsync<Guid>("SELECT club_id FROM pcs WHERE id = @pcId", new { pcId });
    }
}

/// <summary>
/// Coverage of slice S1 (DESIGN §10.a, §11): each of its eight operations got a contract-valid success response through
/// the real agent client in this fixture.
/// </summary>
public sealed class CoverageTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task S1_operations_all_have_a_contract_valid_success_response()
    {
        await using var agent = await AgentHarness.CreateAsync(server);
        var client = agent.Client;
        var pcId = (await client.RegisterAsync(agent.RegisterRequest(), CancellationToken.None)).PcId;
        await client.RefreshAsync(CancellationToken.None);
        await client.HeartbeatAsync(pcId, AgentHarness.Heartbeat(), CancellationToken.None);
        await client.SendTelemetryAsync(pcId, new TelemetryBatch([], []), CancellationToken.None);
        await client.GetConfigAsync(pcId, null, CancellationToken.None);
        await client.GetPoliciesAsync(pcId, null, CancellationToken.None);
        await server.Services.GetRequiredService<CommandDispatcher>().EnqueueAsync(await ClubOfAsync(pcId), pcId, NewCommand.Unlock());
        var item = Assert.Single((await client.GetCommandsAsync(pcId, CancellationToken.None)).Items);
        await client.AckCommandAsync(pcId, item.Id, CommandAck.Success(), CancellationToken.None);

        var success = new Dictionary<string, int>
        {
            ["register"] = 200, ["refresh"] = 200, ["heartbeat"] = 200, ["sendTelemetry"] = 204,
            ["getConfig"] = 200, ["getPolicies"] = 200, ["getCommands"] = 200, ["ackCommand"] = 204,
        };
        Assert.Equal(AgentEndpoints.Operations.Order(), success.Keys.Order());
        Assert.All(success, op => Assert.True(server.Covered.ContainsKey($"{op.Key} {op.Value}"), $"{op.Key} {op.Value} not covered"));
    }

    private async Task<Guid> ClubOfAsync(Guid pcId)
    {
        await using var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        return await c.QuerySingleAsync<Guid>("SELECT club_id FROM pcs WHERE id = @pcId", new { pcId });
    }
}
