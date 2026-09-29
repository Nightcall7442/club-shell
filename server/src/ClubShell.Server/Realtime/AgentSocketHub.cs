using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Serialization;
using ClubShell.Server.Agents;
using ClubShell.Server.Auth;
using ClubShell.Server.Infrastructure;
using Npgsql;

namespace ClubShell.Server.Realtime;

/// <summary><c>Realtime:*</c> (DESIGN §2.5).</summary>
public sealed class RealtimeOptions
{
    public int PingSec { get; set; } = 20;

    public int PongTimeoutSec { get; set; } = 10;

    public int MaxFrameBytes { get; set; } = WsFrame.MaxFrameBytes;
}

/// <summary>
/// <c>/ws/agent</c> (DESIGN §6; port of club-server <c>Realtime/AgentSocketHub.cs</c>, AsyncAPI §1–§10). A bad token
/// is refused before the upgrade with HTTP 401 (the agent refreshes); the <c>clubshell.v1</c> subprotocol is required
/// (else close 4426). One connection per PC: a new one replaces the old (close 1000). On connect every pending command
/// is delivered, oldest first. The server pings every <see cref="RealtimeOptions.PingSec"/> and drops a connection
/// whose pong does not come within <see cref="RealtimeOptions.PongTimeoutSec"/>; the token's <c>exp</c>, a revocation
/// or a Bearer/<c>?token=</c> mismatch close it with 4401, a frame over 1 MiB with 1009, server shutdown with 1001.
/// Agent events are stored, acks go to <see cref="CommandRepository"/>, unknown frame types are ignored. Single instance
/// (D-20): the registry lives in memory, commands in the database, so a restart loses nothing.
/// </summary>
public sealed class AgentSocketHub(
    TokenService tokens,
    PcRepository pcs,
    CommandRepository commands,
    RealtimeOptions options,
    TimeProvider clock,
    IHostApplicationLifetime host,
    ILogger<AgentSocketHub> logger)
{
    public const WebSocketCloseStatus Unauthorized = (WebSocketCloseStatus)4401;
    public const WebSocketCloseStatus SubprotocolRequired = (WebSocketCloseStatus)4426;

    /// <summary>N1: a command the live socket carried within this window is not counted in <c>pendingCommands</c>.</summary>
    private static readonly TimeSpan LiveDeliveryWindow = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();

    public bool IsConnected(Guid pcId) => _connections.ContainsKey(pcId);

    /// <summary>Commands delivered over the PC's current socket within the last 5 min (heartbeat rule N1, §6.4).</summary>
    public IReadOnlyCollection<Guid> LiveDelivered(Guid pcId)
    {
        if (!_connections.TryGetValue(pcId, out var connection))
        {
            return [];
        }

        var since = clock.GetUtcNow() - LiveDeliveryWindow;
        return connection.Delivered.Where(d => d.Value > since).Select(d => d.Key).ToList();
    }

    /// <summary>
    /// Sends a queued command if the PC is connected; a failure loses nothing, the command stays queued. A connection
    /// still delivering its backlog is waited for: a live command must not overtake older pending ones (§6.2, oldest
    /// first; the agent honours <c>supersedes</c> only for a command it already has).
    /// </summary>
    public async Task<bool> TrySendCommandAsync(Guid pcId, ServerCommandEnvelope command)
    {
        if (!_connections.TryGetValue(pcId, out var connection))
        {
            return false;
        }

        try
        {
            await connection.Ready.Task.WaitAsync(connection.Lifetime.Token);
            await DeliverAsync(connection, [command], connection.Lifetime.Token);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// A push frame (<c>WsPushFrame</c>, DESIGN §6.5) to the PC if it is connected: at most once, never queued — the agent
    /// reconciles over REST (<c>GET /sessions/current</c>, <c>GET /wallet/…/balance</c>) and the tick resyncs sessions.
    /// </summary>
    public async Task<bool> TryPushAsync(Guid pcId, WsPushKind kind, JsonElement payload)
    {
        if (!_connections.TryGetValue(pcId, out var connection))
        {
            return false;
        }

        try
        {
            await connection.Ready.Task.WaitAsync(connection.Lifetime.Token);
            await connection.SendAsync(new WsFrame(WsFrameType.Push, Guid.NewGuid(), clock.GetUtcNow(), kind.ToWireName(), payload), connection.Lifetime.Token);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Credentials revoked (<c>cv</c> bumped, PC deleted): the socket closes with 4401 at once (§6.7).</summary>
    public Task RevokeAsync(Guid pcId) =>
        _connections.TryGetValue(pcId, out var connection) ? connection.CloseAsync(Unauthorized, "credentials revoked") : Task.CompletedTask;

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            throw ApiException.Validation("upgrade", "required", "WebSocket upgrade required");
        }

        var header = AgentAuthMiddleware.BearerToken(context);
        var query = context.Request.Query["token"].ToString();
        var agent = await AgentAuthMiddleware.AuthenticateTokenAsync(header ?? query, tokens, pcs);

        if (!context.WebSockets.WebSocketRequestedProtocols.Contains(WsFrame.Subprotocol))
        {
            using var rejected = await context.WebSockets.AcceptWebSocketAsync();
            await CloseQuietlyAsync(rejected, SubprotocolRequired, "subprotocol clubshell.v1 required");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = WsFrame.Subprotocol,
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });

        if (header is not null && query.Length > 0 && query != header)
        {
            await CloseQuietlyAsync(socket, Unauthorized, "Authorization and ?token= differ");
            return;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var connection = new Connection(socket, lifetime);
        Connection? previous = null;
        _connections.AddOrUpdate(agent.Pc.Id, connection, (_, old) =>
        {
            previous = old;
            return connection;
        });
        if (previous is not null)
        {
            await previous.CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced by a new connection");
        }

        logger.LogInformation("Agent {PcId} connected over WebSocket", agent.Pc.Id);
        using var stopping = host.ApplicationStopping.Register(() => _ = connection.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "server stopping"));
        Task[] background = [];
        try
        {
            // A revocation committed between the token check above and the registration found no socket to close
            // (RevokeAsync runs after the commit): cv is read again now that RevokeAsync would see this one (§6.7).
            if (await pcs.FindAsync(agent.Pc.Id) is not { DeletedAt: null } pc || pc.CredentialsVersion != agent.Principal.CredentialsVersion)
            {
                await connection.CloseAsync(Unauthorized, "credentials revoked");
            }
            else
            {
                // Registered before the backlog is read, so no command falls in between; live sends wait for Ready.
                await DeliverAsync(connection, await commands.PendingAsync(agent.Pc.Id), lifetime.Token);
                connection.Ready.TrySetResult();

                // Keepalive and expiry only send our close frame; the receive loop reads the peer's answer and ends
                // the connection (CloseAsync drops it after a grace period when the peer stays silent).
                background = [KeepAliveLoopAsync(connection, lifetime.Token), ExpiryAsync(agent, connection, lifetime.Token)];
            }

            await Swallow(ReceiveLoopAsync(agent, connection, lifetime.Token));
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // The peer vanished during redelivery; the commands stay queued.
        }
        catch (NpgsqlException ex)
        {
            // Past the upgrade the error middleware cannot answer; the agent reconnects and gets the backlog again.
            logger.LogWarning(ex, "Database error on the socket of {PcId}", agent.Pc.Id);
        }
        finally
        {
            await lifetime.CancelAsync();
            await Task.WhenAll(background.Select(Swallow));
            _connections.TryRemove(new KeyValuePair<Guid, Connection>(agent.Pc.Id, connection));
            logger.LogInformation("Agent {PcId} disconnected ({Status})", agent.Pc.Id, socket.CloseStatus);
        }
    }

    /// <summary>A command frame (<c>WsCommandFrame</c>): the envelope without <c>issuedBy</c>, <c>payload</c> always present.</summary>
    public static WsFrame CommandFrame(ServerCommandEnvelope command) =>
        new(WsFrameType.Command, command.Id, command.Ts, command.Name, command.Payload, null, command.Supersedes, command.ExpiresAt);

    private async Task DeliverAsync(Connection connection, IReadOnlyList<ServerCommandEnvelope> batch, CancellationToken cancellationToken)
    {
        foreach (var command in batch)
        {
            await connection.SendAsync(CommandFrame(command), cancellationToken);
            connection.Delivered[command.Id] = clock.GetUtcNow();
        }

        if (batch.Count > 0)
        {
            try
            {
                await commands.MarkDeliveredAsync(batch.Select(c => c.Id).ToList());
            }
            catch (NpgsqlException ex)
            {
                // The frames went out: a missing delivered_at only lets a later supersedes miss them, nothing is lost.
                // Throwing would fail EnqueueAsync for a command that is queued and delivered, and a retry duplicates it.
                logger.LogWarning(ex, "delivered_at not stored for {Count} command(s)", batch.Count);
            }
        }
    }

    private async Task ReceiveLoopAsync(AgentContext agent, Connection connection, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        while (!cancellationToken.IsCancellationRequested)
        {
            buffer.Clear();
            ValueWebSocketReceiveResult result;
            do
            {
                result = await connection.Socket.ReceiveAsync(buffer.GetMemory(16 * 1024), cancellationToken);
                buffer.Advance(result.Count);
                if (buffer.WrittenCount > options.MaxFrameBytes)
                {
                    await connection.CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame larger than 1 MiB");
                    return;
                }
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
                return;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            WsFrame? frame;
            try
            {
                frame = JsonDefaults.Deserialize<WsFrame>(buffer.WrittenSpan);
            }
            catch (JsonException ex)
            {
                logger.LogWarning("Malformed WS frame from {PcId}: {Error}", agent.Pc.Id, ex.Message);
                continue;
            }

            // After a 4401 the token no longer speaks for the PC: frames arriving within the close grace are dropped.
            if (frame is not null && !connection.Revoked)
            {
                await HandleFrameAsync(agent, connection, frame, cancellationToken);
            }
        }
    }

    private async Task HandleFrameAsync(AgentContext agent, Connection connection, WsFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case WsFrameType.Pong:
                connection.OnPong(frame.Id);
                break;
            case WsFrameType.Ping:
                await connection.SendAsync(WsFrame.PongFor(frame, clock.GetUtcNow()), cancellationToken);
                break;
            case WsFrameType.Ack when frame.Ack is { } ack:
                try
                {
                    if (!await commands.AckAsync(agent.Pc.Id, ack.Id, new CommandAck(ack.Ok, ack.Error, ack.Result)))
                    {
                        logger.LogWarning("Agent {PcId} acked unknown command {CommandId}", agent.Pc.Id, ack.Id);
                    }
                }
                catch (NpgsqlException ex)
                {
                    // As for events below: the socket stays, the command stays pending and its redelivery is re-acked.
                    logger.LogWarning(ex, "Ack of {CommandId} from {PcId} not stored", ack.Id, agent.Pc.Id);
                }

                break;
            case WsFrameType.Event when frame.Name is { Length: > 0 } name && char.IsAsciiLetter(name[0])
                && Enum.TryParse<AgentEventType>(name, ignoreCase: true, out var type) && Enum.IsDefined(type):
                try
                {
                    await pcs.WriteAgentEventAsync(agent.Pc, type, name, frame.Ts, frame.Payload ?? JsonElement.Parse("null"), clock.GetUtcNow());
                }
                catch (NpgsqlException ex)
                {
                    // An event that cannot be stored must not cost the command channel; there is no ack for events.
                    logger.LogWarning(ex, "Event {Name} of {PcId} not stored", name, agent.Pc.Id);
                }

                break;
            default:
                // Unknown types, unknown events and server-side frames (command, push) from the agent are ignored.
                logger.LogDebug("Ignoring WS frame {Type} {Name} from {PcId}", frame.Type, frame.Name, agent.Pc.Id);
                break;
        }
    }

    /// <summary>Ping every <c>PingSec</c>; the next ping waits for the previous pong, a missing pong drops the connection.</summary>
    private async Task KeepAliveLoopAsync(Connection connection, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(options.PingSec), clock, cancellationToken);
            var ping = WsFrame.Ping(clock.GetUtcNow());
            var pong = connection.ExpectPong(ping.Id);
            await connection.SendAsync(ping, cancellationToken);
            try
            {
                await pong.WaitAsync(TimeSpan.FromSeconds(options.PongTimeoutSec), clock, cancellationToken);
            }
            catch (TimeoutException)
            {
                await connection.CloseAsync(WebSocketCloseStatus.PolicyViolation, "pong timeout");
                return;
            }
        }
    }

    /// <summary>4401 when the access token expires (§6.7): the agent refreshes 2 min before <c>exp</c> and reconnects.</summary>
    private async Task ExpiryAsync(AgentContext agent, Connection connection, CancellationToken cancellationToken)
    {
        var left = agent.Principal.ExpiresAt - clock.GetUtcNow();
        if (left > TimeSpan.Zero)
        {
            await Task.Delay(left, clock, cancellationToken);
        }

        await connection.CloseAsync(Unauthorized, "token expired");
    }

    private static async Task Swallow(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
        }
    }

    private static async Task CloseQuietlyAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.CloseAsync(status, description, timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private sealed class Connection(WebSocket socket, CancellationTokenSource lifetime)
    {
        /// <summary>After our close frame the peer has this long to answer before the connection is dropped.</summary>
        private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);

        private readonly SemaphoreSlim _send = new(1, 1);
        private readonly Lock _pongLock = new();
        private (Guid Id, TaskCompletionSource Pong) _ping;

        public WebSocket Socket { get; } = socket;

        public CancellationTokenSource Lifetime { get; } = lifetime;

        private bool _revoked;

        /// <summary>Command id → when this socket delivered it.</summary>
        public ConcurrentDictionary<Guid, DateTimeOffset> Delivered { get; } = new();

        /// <summary>Set once the backlog of pending commands went out; live sends wait for it.</summary>
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>A 4401 close has started: incoming frames are no longer handled.</summary>
        public bool Revoked => Volatile.Read(ref _revoked);

        public Task ExpectPong(Guid pingId)
        {
            lock (_pongLock)
            {
                _ping = (pingId, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                return _ping.Pong.Task;
            }
        }

        public void OnPong(Guid id)
        {
            lock (_pongLock)
            {
                if (_ping.Id == id)
                {
                    _ping.Pong?.TrySetResult();
                }
            }
        }

        public async Task SendAsync(WsFrame frame, CancellationToken cancellationToken)
        {
            var bytes = JsonDefaults.SerializeToUtf8Bytes(frame);
            await _send.WaitAsync(cancellationToken);
            try
            {
                await Socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            }
            finally
            {
                _send.Release();
            }
        }

        /// <summary>Sends our close frame (once the socket still allows it) and drops the connection if the peer stays silent.</summary>
        public async Task CloseAsync(WebSocketCloseStatus status, string description)
        {
            if (status == Unauthorized)
            {
                Volatile.Write(ref _revoked, true);
            }

            try
            {
                await _send.WaitAsync(Lifetime.Token);
                try
                {
                    if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await Socket.CloseOutputAsync(status, description, Lifetime.Token);
                    }
                }
                finally
                {
                    _send.Release();
                }

                Lifetime.CancelAfter(CloseGrace);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }
}
