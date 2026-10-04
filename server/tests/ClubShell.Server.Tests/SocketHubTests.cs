using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Server.Agents;
using ClubShell.Server.Auth;
using ClubShell.Server.Realtime;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ClubShell.Server.Tests;

/// <summary>
/// <c>/ws/agent</c> on a real Kestrel socket (DESIGN §6.1–6.3, §6.7): 401 before the upgrade, 4426 without
/// <c>clubshell.v1</c>, Bearer vs <c>?token=</c>, replacement 1000, pong timeout, 1009, redelivery on connect (a live
/// command after the backlog), 4401 at <c>exp</c> and on revocation (also one racing the connect), 1001 on shutdown.
/// </summary>
public sealed class SocketHubTests(KestrelServerFixture server) : IClassFixture<KestrelServerFixture>
{
    private AgentSocketHub Hub => server.Services.GetRequiredService<AgentSocketHub>();

    [Fact]
    public async Task Bad_token_is_refused_before_the_upgrade()
    {
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.AddSubProtocol(WsFrame.Subprotocol);
        socket.Options.SetRequestHeader("Authorization", "Bearer not-a-token");
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(server.WsUri, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, socket.HttpStatusCode);
    }

    [Fact]
    public async Task Without_the_subprotocol_the_socket_is_closed_4426()
    {
        var agent = await TestAgent.CreateAsync(server);
        using var socket = await WsTestSocket.ConnectAsync(server, agent.AccessToken, subprotocol: null);
        Assert.Equal((WebSocketCloseStatus)4426, await socket.ClosedAsync());
    }

    [Fact]
    public async Task Query_token_works_alone_and_a_different_bearer_closes_4401()
    {
        var agent = await TestAgent.CreateAsync(server);
        var other = await TestAgent.CreateAsync(server);

        using (var legacy = await WsTestSocket.ConnectAsync(server, bearer: null, queryToken: agent.AccessToken))
        {
            Assert.Equal(WsFrame.Subprotocol, legacy.Socket.SubProtocol);
            Contract.AssertMessage("ping", await legacy.ReceiveAnyAsync() ?? throw new InvalidOperationException("closed"));
        }

        using (var same = await WsTestSocket.ConnectAsync(server, agent.AccessToken, queryToken: agent.AccessToken))
        {
            Assert.Equal("ping", (await same.ReceiveAnyAsync())!.Value.GetProperty("type").GetString());
        }

        using var mismatch = await WsTestSocket.ConnectAsync(server, agent.AccessToken, queryToken: other.AccessToken);
        Assert.Equal((WebSocketCloseStatus)4401, await mismatch.ClosedAsync());
    }

    [Fact]
    public async Task A_new_connection_replaces_the_old_one_with_1000()
    {
        var agent = await TestAgent.CreateAsync(server);
        using var first = await WsTestSocket.ConnectAsync(server, agent.AccessToken);
        await Wait.UntilAsync(() => Hub.IsConnected(agent.PcId));
        using var second = await WsTestSocket.ConnectAsync(server, agent.AccessToken);

        Assert.Equal(WebSocketCloseStatus.NormalClosure, await first.ClosedAsync());
        Assert.Equal("ping", (await second.ReceiveAnyAsync())!.Value.GetProperty("type").GetString());
        Assert.True(Hub.IsConnected(agent.PcId));
    }

    [Fact]
    public async Task Pings_are_answered_by_the_agent_and_a_missing_pong_drops_the_connection()
    {
        var agent = await TestAgent.CreateAsync(server);
        using var socket = await WsTestSocket.ConnectAsync(server, agent.AccessToken);

        // The agent may ping too (receiveAgentPing): the server answers with the same id.
        var id = Guid.NewGuid();
        await socket.SendAsync(new { type = "ping", id, ts = DateTimeOffset.UtcNow, payload = (object?)null });
        var pong = await socket.ReceiveAsync();
        Contract.AssertMessage("serverPong", pong);
        Assert.Equal(id, pong.GetProperty("id").GetGuid());

        // Two answered pings keep it open; then silence: the server gives up after PongTimeoutSec.
        for (var i = 0; i < 2; i++)
        {
            var ping = (await socket.ReceiveAnyAsync())!.Value;
            await socket.SendAsync(new { type = "pong", id = ping.GetProperty("id").GetGuid(), ts = DateTimeOffset.UtcNow, payload = (object?)null });
        }

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await socket.ClosedAsync(answerPings: false));
        await Wait.UntilAsync(() => !Hub.IsConnected(agent.PcId));
    }

    [Fact]
    public async Task A_frame_over_one_mebibyte_closes_1009()
    {
        var agent = await TestAgent.CreateAsync(server);
        using var socket = await WsTestSocket.ConnectAsync(server, agent.AccessToken);
        var huge = new string('x', WsFrame.MaxFrameBytes);
        await socket.Socket.SendAsync(Encoding.UTF8.GetBytes($$"""{"type":"event","name":"x","payload":"{{huge}}"}"""), WebSocketMessageType.Text, true, CancellationToken.None);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, await socket.ClosedAsync());
    }

    [Fact]
    public async Task Pending_commands_are_delivered_on_connect_and_acked_over_ws()
    {
        var agent = await TestAgent.CreateAsync(server);
        var dispatcher = server.Services.GetRequiredService<CommandDispatcher>();
        var club = await ClubOfAsync(agent);
        var first = await dispatcher.EnqueueAsync(club, agent.PcId, NewCommand.Lock(new LockCommand("staff", "Подойдите к стойке")));
        var second = await dispatcher.EnqueueAsync(club, agent.PcId, NewCommand.Unlock(), supersedes: first.Id);
        var third = await dispatcher.EnqueueAsync(club, agent.PcId, NewCommand.ReloadPolicy());

        using var socket = await WsTestSocket.ConnectAsync(server, agent.AccessToken);
        var delivered = new[] { await socket.ReceiveAsync(), await socket.ReceiveAsync() };
        Contract.AssertMessage("commandUnlock", delivered[0]);
        Contract.AssertMessage("commandReloadPolicy", delivered[1]);
        Assert.Equal([second.Id, third.Id], delivered.Select(f => f.GetProperty("id").GetGuid()));

        var ack = new { type = "ack", id = Guid.NewGuid(), ts = DateTimeOffset.UtcNow, payload = (object?)null, ack = new { id = third.Id, ok = true, result = new { version = 1 } } };
        Contract.AssertMessage("ackReloadPolicy", JsonSerializer.SerializeToElement(ack));
        await socket.SendAsync(ack);
        var stored = await server.Services.GetRequiredService<CommandRepository>().WaitForAckAsync(third.Id, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(1, stored!.Result!.Value.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Token_expiry_closes_4401()
    {
        var agent = await TestAgent.CreateAsync(server);
        server.Clock.Advance(TimeSpan.FromMinutes(60) - TimeSpan.FromSeconds(2));
        using var socket = await WsTestSocket.ConnectAsync(server, agent.AccessToken);
        Assert.Equal((WebSocketCloseStatus)4401, await socket.ClosedAsync());
    }

    [Fact]
    public async Task Re_registration_revokes_the_open_socket_4401()
    {
        var agent = await TestAgent.CreateAsync(server);
        using var socket = await WsTestSocket.ConnectAsync(server, agent.AccessToken);
        await Wait.UntilAsync(() => Hub.IsConnected(agent.PcId));
        await TestAgent.CreateAsync(server, agent.Hwid);
        Assert.Equal((WebSocketCloseStatus)4401, await socket.ClosedAsync());
    }

    [Fact]
    public async Task A_revocation_between_the_token_check_and_the_registration_still_closes_4401()
    {
        var agent = await TestAgent.CreateAsync(server);
        var db = server.Services.GetRequiredService<NpgsqlDataSource>();
        var connected = $"Agent {agent.PcId} connected over WebSocket";

        // The hook runs after the socket is registered, before the hub reads cv again: a re-registration committed
        // there (cv + 1, then RevokeAsync) is the race where RevokeAsync found no socket to close.
        server.Services.GetRequiredService<ILoggerFactory>().AddProvider(new LogHook(message =>
        {
            if (message == connected)
            {
                using var c = db.OpenConnection();
                c.Execute("UPDATE pcs SET credentials_version = credentials_version + 1 WHERE id = @id", new { id = agent.PcId });
            }
        }));

        // Bounded: an open socket answers pings forever, so ClosedAsync alone would never return.
        using var socket = await WsTestSocket.ConnectAsync(server, agent.AccessToken);
        Assert.Equal((WebSocketCloseStatus)4401, await socket.ClosedAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_live_command_waits_for_the_backlog_of_a_new_connection()
    {
        var agent = await TestAgent.CreateAsync(server);
        var dispatcher = server.Services.GetRequiredService<CommandDispatcher>();
        var club = await ClubOfAsync(agent);
        var expected = new List<Guid>();
        for (var i = 0; i < 300; i++)
        {
            expected.Add((await dispatcher.EnqueueAsync(club, agent.PcId, NewCommand.ReloadPolicy())).Id);
        }

        // The command is issued the moment the socket is registered, while the 300 older ones are still going out.
        var connecting = WsTestSocket.ConnectAsync(server, agent.AccessToken);
        Assert.True(SpinWait.SpinUntil(() => Hub.IsConnected(agent.PcId), TimeSpan.FromSeconds(10)));
        var unlock = await dispatcher.EnqueueAsync(club, agent.PcId, NewCommand.Unlock());
        expected.Add(unlock.Id);

        using var socket = await connecting;
        var received = new List<Guid>();
        while (!received.Contains(unlock.Id))
        {
            received.Add((await socket.ReceiveAsync()).GetProperty("id").GetGuid());
        }

        Assert.Equal(expected, received);
    }

    [Fact]
    public async Task A_command_queued_between_the_registration_and_the_backlog_read_goes_out_once()
    {
        var agent = await TestAgent.CreateAsync(server);
        var dispatcher = server.Services.GetRequiredService<CommandDispatcher>();
        var club = await ClubOfAsync(agent);
        var connected = $"Agent {agent.PcId} connected over WebSocket";
        ServerCommandEnvelope? raced = null;
        Task? live = null;

        // The hook runs after the socket is registered, before the backlog is read: the command commits there, so the
        // backlog carries it, and its live send (the cashier's request) waits for the backlog and must not send it again.
        server.Services.GetRequiredService<ILoggerFactory>().AddProvider(new LogHook(message =>
        {
            if (message == connected && raced is null)
            {
                raced = dispatcher.QueueAsync(null, club, agent.PcId, NewCommand.ReloadPolicy()).GetAwaiter().GetResult();
                live = Task.Run(() => dispatcher.SendAsync(agent.PcId, raced));
            }
        }));

        using var socket = await WsTestSocket.ConnectAsync(server, agent.AccessToken);
        // The handshake may complete before the hook ran: the first frame comes after it.
        var first = (await socket.ReceiveAsync()).GetProperty("id").GetGuid();
        Assert.Equal(raced!.Id, first);
        await live!;
        var next = await dispatcher.EnqueueAsync(club, agent.PcId, NewCommand.Unlock());
        Assert.Equal(next.Id, (await socket.ReceiveAsync()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Server_shutdown_closes_1001_without_waiting_for_the_host_timeout()
    {
        var own = new KestrelServerFixture();
        await own.InitializeAsync();
        var agent = await TestAgent.CreateAsync(own);
        var hub = own.Services.GetRequiredService<AgentSocketHub>();
        using var socket = await WsTestSocket.ConnectAsync(own, agent.AccessToken);
        await Wait.UntilAsync(() => hub.IsConnected(agent.PcId));

        var watch = Stopwatch.StartNew();
        var stopping = ((IAsyncLifetime)own).DisposeAsync();
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, await socket.ClosedAsync());
        await socket.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await stopping;
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"host stopped after {watch.Elapsed}");
    }

    [Fact]
    public async Task Only_one_instance_holds_the_hub_lock()
    {
        var db = server.Services.GetRequiredService<NpgsqlDataSource>();
        var logs = server.Services.GetRequiredService<ILoggerFactory>();
        await using var first = new HubLock(db, logs.CreateLogger<HubLock>());
        await using var second = new HubLock(db, logs.CreateLogger<HubLock>());
        await first.AcquireAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.AcquireAsync());
        await first.DisposeAsync();
        await second.AcquireAsync();
    }

    private async Task<Guid> ClubOfAsync(TestAgent agent)
    {
        await using var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        return await c.QuerySingleAsync<Guid>("SELECT club_id FROM pcs WHERE id = @id", new { id = agent.PcId });
    }

    /// <summary>Runs a callback inside the server's logging call: a hook at a known point of its flow.</summary>
    private sealed class LogHook(Action<string> onMessage) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            onMessage(formatter(state, exception));

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// The seven agent events AsyncAPI requires the server to receive (DESIGN §6.3) are stored; an unknown frame type or
/// event name is ignored and the connection stays usable.
/// </summary>
public sealed class WsEventTests(KestrelServerFixture server) : IClassFixture<KestrelServerFixture>
{
    [Fact]
    public async Task All_seven_events_are_stored_and_unknown_frames_are_ignored()
    {
        var agent = await TestAgent.CreateAsync(server);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var session = new
        {
            id = Guid.NewGuid(), userId = Guid.NewGuid(), pcId = agent.PcId, state = "active", startedAt = now, tariffId = Guid.NewGuid(),
            secondsLeft = 3600, secondsUsed = 0, cost = new { amount = 1_200_000L, currency = "UZS" }, isPrepaid = true, warningsSent = Array.Empty<int>(),
        };
        var hardware = JsonElement.Parse(TestAgent.RegisterBody("x", "aa:bb:cc:dd:ee:ff")).GetProperty("hardware");
        var events = new (string Name, object Payload)[]
        {
            ("sessionStarted", new { session }),
            ("sessionEnded", new { session = session with { state = "ended" }, reason = "user", charged = new { amount = 0L, currency = "UZS" } }),
            ("gameLaunched", new { sessionId = session.id, gameId = Guid.NewGuid(), pid = 4242, at = now }),
            ("gameExited", new { sessionId = session.id, gameId = Guid.NewGuid(), pid = 4242, exitCode = 0, playedSec = 600, at = now }),
            ("anticheatViolation", new { pcId = agent.PcId, kind = "vanguard", check = "driverMissing", severity = "critical", details = new { }, at = now, actionTaken = "blockedLaunch" }),
            ("hardwareChanged", new { hardware, diff = new[] { "gpu" } }),
            ("offlineQueueFlushed", new { count = 3, deadlettered = 0, offlineFrom = now.AddMinutes(-5), offlineTo = now }),
        };

        using var socket = await WsTestSocket.ConnectAsync(server, agent.AccessToken);
        await socket.SendAsync("""{"type":"bogus","id":"0197a0b0-0000-7000-8000-000000000001","ts":"2026-09-29T10:00:00.000Z","payload":null}""");
        await socket.SendAsync(new { type = "event", id = Guid.NewGuid(), ts = now, name = "noSuchEvent", payload = new { } });
        foreach (var (name, payload) in events)
        {
            var frame = JsonSerializer.SerializeToElement(new { type = "event", id = Guid.NewGuid(), ts = now, name, payload });
            Contract.AssertMessage("event" + char.ToUpperInvariant(name[0]) + name[1..], frame);
            await socket.SendAsync(frame.GetRawText());
        }

        // Frames are handled in order: the pong to this ping means every event before it has been stored.
        var id = Guid.NewGuid();
        await socket.SendAsync(new { type = "ping", id, ts = now, payload = (object?)null });
        Assert.Equal(id, (await socket.ReceiveAsync()).GetProperty("id").GetGuid());

        await using var c = await server.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        var kinds = (await c.QueryAsync<string>("SELECT kind FROM telemetry_events WHERE pc_id = @id ORDER BY kind", new { id = agent.PcId })).ToList();
        Assert.Equal(["gameExited", "gameLaunched", "hardwareChanged", "offlineQueueFlushed", "sessionEnded", "sessionStarted"], kinds);
        Assert.Equal("driverMissing", await c.QuerySingleAsync<string>("SELECT data->>'check' FROM anticheat_reports WHERE pc_id = @id", new { id = agent.PcId }));
        Assert.Equal(16384, await c.QuerySingleAsync<int>("SELECT (hardware->>'ramMb')::int FROM pcs WHERE id = @id", new { id = agent.PcId }));
    }
}
