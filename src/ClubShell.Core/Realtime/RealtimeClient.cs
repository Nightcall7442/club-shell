using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClubShell.Core.Realtime;

/// <summary>
/// WebSocket client of <c>wss://&lt;server&gt;/ws/agent</c> (SERVER_API.md §6): connects with the agent JWT and the
/// <c>clubshell.v1</c> subprotocol, reads <see cref="WsFrame"/>s (≤ 1 MiB), answers server pings, dispatches commands
/// (deduplicated by id for 24 h, expired ones acked with <c>timeout</c>, <c>supersedes</c> honoured) to
/// <see cref="CommandHandler"/> and acks them, raises pushes through <see cref="PushReceived"/>, and sends agent
/// events from a bounded outbound channel. One <see cref="RunAsync"/> call is one connection; wrap it in a
/// <see cref="Reconnector"/>.
/// </summary>
public sealed class RealtimeClient
{
    /// <summary>Outbound frames buffered per connection.</summary>
    public const int OutboundCapacity = 1024;

    /// <summary>Connection is considered dead when no frame arrives for this long (server pings every 20 s).</summary>
    public static TimeSpan ReceiveTimeout { get; } = TimeSpan.FromSeconds(75);

    /// <summary>WebSocket-level keepalive interval (SERVER_API.md §6: 30 s).</summary>
    public static TimeSpan KeepAliveInterval { get; } = TimeSpan.FromSeconds(30);

    /// <summary>How long command ids are remembered for deduplication.</summary>
    public static TimeSpan DedupeWindow { get; } = TimeSpan.FromHours(24);

    private const int CloseStatusUnauthorized = 4401;
    private const int CloseStatusUpgradeRequired = 4426;
    private const int ReceiveChunkBytes = 16 * 1024;

    private readonly ITokenStore _tokens;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<RealtimeClient> _logger;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _seenCommands = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _inflight = new();
    private Channel<WsFrame>? _outbound;
    private int _connected;

    /// <summary>Creates the client.</summary>
    public RealtimeClient(ITokenStore tokens, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<RealtimeClient> logger)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _tokens = tokens;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Raised for every <see cref="WsFrameType.Push"/> frame (<see cref="WsFrame.TryGetPushKind"/> for the kind).</summary>
    public event EventHandler<WsFrame>? PushReceived;

    /// <summary>Raised on connect (<see cref="ConnectivityState.Online"/>) and disconnect (<see cref="ConnectivityState.Offline"/>).</summary>
    public event EventHandler<ConnectivityState>? ConnectivityChanged;

    /// <summary>Handler invoked for every server command; the returned ack is sent back as a <c>WsFrame{ack}</c>. A missing handler acks with <see cref="ErrorCode.NotFound"/>.</summary>
    public Func<ServerCommand, CancellationToken, Task<CommandAck>>? CommandHandler { get; set; }

    /// <summary>WebSocket URL override from <c>AgentServerConfig.wsUrl</c>; <see langword="null"/> = <c>server.wsUrl</c>.</summary>
    public string? WsUrlOverride { get; set; }

    /// <summary><see langword="true"/> while a connection is open.</summary>
    public bool IsConnected => Volatile.Read(ref _connected) == 1;

    /// <summary>When the current connection was established.</summary>
    public DateTimeOffset? ConnectedSince { get; private set; }

    /// <summary>Last frame received from the server.</summary>
    public DateTimeOffset? LastReceivedAt { get; private set; }

    /// <summary>Frames waiting to be sent on the current connection.</summary>
    public int OutboundQueueLength => _outbound?.Reader.Count ?? 0;

    /// <summary>Commands currently being handled.</summary>
    public int InflightCommands => _inflight.Count;

    /// <summary>
    /// Connects, pumps until the connection drops or <paramref name="cancellationToken"/> is cancelled, then closes
    /// gracefully. Throws <see cref="ServerApiException"/> (<see cref="ErrorCode.Unauthorized"/>) when the handshake
    /// or close status says the token is invalid so the caller can refresh before reconnecting;
    /// <see cref="WebSocketException"/>/<see cref="TimeoutException"/> for transport failures.
    /// </summary>
    /// <param name="onConnected">Invoked once the socket is open (typically <see cref="Reconnector.MarkConnected"/>).</param>
    /// <param name="cancellationToken">Stops the pump.</param>
    public async Task RunAsync(Action? onConnected, CancellationToken cancellationToken)
    {
        var tokens = _tokens.Agent ?? throw new ServerApiException(ErrorCode.Unauthorized, HttpStatusCode.Unauthorized, null, null, "Agent is not registered (no tokens)");
        var uri = BuildUri(tokens.AccessToken);

        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(WsFrame.Subprotocol);
        socket.Options.SetRequestHeader("Authorization", "Bearer " + tokens.AccessToken);
        socket.Options.KeepAliveInterval = KeepAliveInterval;
        socket.Options.CollectHttpResponseDetails = true;

        try
        {
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            if (socket.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new ServerApiException(ErrorCode.Unauthorized, socket.HttpStatusCode, null, null, "WebSocket handshake rejected: token invalid or expired", ex);
            }

            throw;
        }

        _logger.LogInformation("WebSocket connected to {Host} (subprotocol {Subprotocol})", uri.Host, socket.SubProtocol ?? "none");
        var outbound = Channel.CreateBounded<WsFrame>(new BoundedChannelOptions(OutboundCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        Volatile.Write(ref _outbound, outbound);
        ConnectedSince = _clock.UtcNow;
        LastReceivedAt = ConnectedSince;
        SetConnected(true);
        onConnected?.Invoke();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var writer = WriteLoopAsync(socket, outbound.Reader, linked);
        Exception? readError = null;
        try
        {
            await ReadLoopAsync(socket, outbound.Writer, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown requested; close gracefully below and surface the cancellation afterwards.
        }
        catch (OperationCanceledException)
        {
            // The writer failed and cancelled the linked token; its exception is observed below.
        }
        catch (Exception ex)
        {
            readError = ex;
        }
        finally
        {
            Volatile.Write(ref _outbound, null);
            SetConnected(false);
            ConnectedSince = null;
            _ = outbound.Writer.TryComplete();
            await linked.CancelAsync().ConfigureAwait(false);
            CancelInflight();
        }

        try
        {
            await writer.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the linked token was cancelled by the read side.
        }
        catch (Exception ex) when (readError is null)
        {
            readError = ex;
        }

        await CloseAsync(socket).ConfigureAwait(false);
        if (readError is not null)
        {
            if (readError is WebSocketException or TimeoutException or ServerApiException or InvalidDataException)
            {
                ExceptionDispatchInfo.Throw(readError);
            }

            throw new WebSocketException(WebSocketError.Faulted, readError.Message, readError);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var closeStatus = socket.CloseStatus;
        if (closeStatus is { } status)
        {
            ThrowIfAuthClose((int)status, socket.CloseStatusDescription);
        }
    }

    /// <summary>Queues an agent event for the current connection; <see langword="false"/> when disconnected or the buffer is full (caller stores it in the outbox).</summary>
    public bool TrySendEvent(AgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        return TrySendFrame(WsFrame.EventOf(agentEvent, _clock.UtcNow));
    }

    /// <summary>Queues a frame without waiting; <see langword="false"/> when disconnected or the buffer is full.</summary>
    public bool TrySendFrame(WsFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var channel = Volatile.Read(ref _outbound);
        return channel is not null && channel.Writer.TryWrite(frame);
    }

    /// <summary>Queues a frame, waiting for buffer space; throws <see cref="InvalidOperationException"/> when disconnected.</summary>
    public async ValueTask SendFrameAsync(WsFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var channel = Volatile.Read(ref _outbound) ?? throw new InvalidOperationException("WebSocket is not connected");
        try
        {
            await channel.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex)
        {
            throw new InvalidOperationException("WebSocket is not connected", ex);
        }
    }

    private static void ThrowIfAuthClose(int status, string? description)
    {
        if (status == CloseStatusUnauthorized)
        {
            throw new ServerApiException(ErrorCode.Unauthorized, HttpStatusCode.Unauthorized, null, null, $"WebSocket closed by server: {description ?? "unauthorized"}");
        }

        if (status == CloseStatusUpgradeRequired)
        {
            throw new ServerApiException(ErrorCode.VersionMismatch, HttpStatusCode.UpgradeRequired, null, null, $"WebSocket closed by server: {description ?? "protocol unsupported"}");
        }
    }

    private static async Task CloseAsync(ClientWebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Best-effort close; the socket is disposed by the caller anyway.
        }
    }

    private Uri BuildUri(string accessToken)
    {
        var builder = new UriBuilder(WsUrlOverride ?? _settings.CurrentValue.Server.WsUrl);
        var tokenQuery = "token=" + Uri.EscapeDataString(accessToken);
        builder.Query = string.IsNullOrEmpty(builder.Query) ? tokenQuery : builder.Query.TrimStart('?') + "&" + tokenQuery;
        return builder.Uri;
    }

    private async Task ReadLoopAsync(ClientWebSocket socket, ChannelWriter<WsFrame> outbound, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>(ReceiveChunkBytes);
        while (!cancellationToken.IsCancellationRequested)
        {
            buffer.Clear();
            ValueWebSocketReceiveResult result;
            using (var idle = new CancellationTokenSource(ReceiveTimeout))
            using (var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token))
            {
                try
                {
                    do
                    {
                        var memory = buffer.GetMemory(ReceiveChunkBytes);
                        result = await socket.ReceiveAsync(memory, receiveCts.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _logger.LogInformation("Server closed the WebSocket ({Status}: {Description})", socket.CloseStatus, socket.CloseStatusDescription);
                            return;
                        }

                        buffer.Advance(result.Count);
                        if (buffer.WrittenCount > WsFrame.MaxFrameBytes)
                        {
                            throw new InvalidDataException($"WebSocket frame exceeds {WsFrame.MaxFrameBytes} bytes");
                        }
                    }
                    while (!result.EndOfMessage);
                }
                catch (OperationCanceledException) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"No WebSocket frame received for {ReceiveTimeout}; assuming the connection is dead");
                }
            }

            LastReceivedAt = _clock.UtcNow;
            if (result.MessageType != WebSocketMessageType.Text)
            {
                _logger.LogDebug("Ignoring binary WebSocket frame of {Bytes} bytes", buffer.WrittenCount);
                continue;
            }

            WsFrame? frame;
            try
            {
                frame = JsonDefaults.Deserialize<WsFrame>(buffer.WrittenSpan);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Malformed WebSocket frame ({Bytes} bytes) ignored", buffer.WrittenCount);
                continue;
            }

            if (frame is null)
            {
                continue;
            }

            Dispatch(frame, outbound, cancellationToken);
        }
    }

    private void Dispatch(WsFrame frame, ChannelWriter<WsFrame> outbound, CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case WsFrameType.Ping:
                if (!outbound.TryWrite(WsFrame.PongFor(frame, _clock.UtcNow)))
                {
                    _logger.LogWarning("Outbound buffer full; pong for {Id} dropped", frame.Id);
                }

                break;

            case WsFrameType.Command:
                DispatchCommand(frame, outbound, cancellationToken);
                break;

            case WsFrameType.Push:
                RaisePush(frame);
                break;

            default:
                _logger.LogDebug("Ignoring WebSocket frame of type {Type} ({Name})", frame.Type, frame.Name);
                break;
        }
    }

    private void DispatchCommand(WsFrame frame, ChannelWriter<WsFrame> outbound, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        PruneSeen(now);
        if (!_seenCommands.TryAdd(frame.Id, now))
        {
            _logger.LogInformation("Duplicate command {Id} ({Name}) ignored", frame.Id, frame.Name);
            return;
        }

        ServerCommand command;
        try
        {
            command = ServerCommand.FromFrame(frame);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Unknown server command {Name} ({Id}): {Message}", frame.Name, frame.Id, ex.Message);
            Ack(outbound, frame.Id, CommandAck.Failure(IpcError.Of(ErrorCode.NotFound, $"Unknown command '{frame.Name}'")));
            return;
        }

        if (command.IsExpired(now))
        {
            _logger.LogWarning("Command {Id} ({Type}) expired at {ExpiresAt}; acking timeout", command.Id, command.Type, command.ExpiresAt);
            Ack(outbound, command.Id, CommandAck.Failure(IpcError.Timeout("Command expired before delivery")));
            return;
        }

        if (command.Supersedes is { } superseded && _inflight.TryRemove(superseded, out var previous))
        {
            _logger.LogInformation("Command {Id} supersedes {Superseded}; cancelling it", command.Id, superseded);
            SafeCancel(previous);
        }

        _ = HandleCommandAsync(command, outbound, cancellationToken);
    }

    private static void SafeCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The handler already finished and disposed its token source.
        }
    }

    private async Task HandleCommandAsync(ServerCommand command, ChannelWriter<WsFrame> outbound, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _inflight[command.Id] = cts;
        CommandAck ack;
        try
        {
            var handler = CommandHandler;
            if (handler is null)
            {
                ack = CommandAck.Failure(IpcError.Of(ErrorCode.NotFound, "No command handler registered"));
            }
            else
            {
                ack = await handler(command, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            ack = CommandAck.Failure(IpcError.Conflict("Command superseded", "superseded"));
        }
        catch (IpcException ex)
        {
            ack = CommandAck.Failure(ex.Error);
        }
        catch (ServerApiException ex)
        {
            ack = CommandAck.Failure(ex.ToIpcError());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command {Id} ({Type}) handler threw", command.Id, command.Type);
            ack = CommandAck.Failure(IpcError.Internal(command.Id.ToString("D")));
        }
        finally
        {
            _ = _inflight.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(command.Id, cts));
        }

        if (!Ack(outbound, command.Id, ack))
        {
            // Not delivered: forget the id so the server's redelivery (at-least-once) is processed again.
            _ = _seenCommands.TryRemove(command.Id, out _);
        }
    }

    private bool Ack(ChannelWriter<WsFrame> outbound, Guid commandId, CommandAck ack)
    {
        var frame = WsFrame.AckOf(commandId, ack, _clock.UtcNow);
        if (outbound.TryWrite(frame))
        {
            _logger.LogDebug("Acked command {Id} ok={Ok}", commandId, ack.Ok);
            return true;
        }

        _logger.LogWarning("Could not queue ack for command {Id} (disconnected or buffer full)", commandId);
        return false;
    }

    private void RaisePush(WsFrame frame)
    {
        try
        {
            PushReceived?.Invoke(this, frame);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Push handler for {Name} threw", frame.Name);
        }
    }

    private void PruneSeen(DateTimeOffset now)
    {
        if (_seenCommands.Count < 4096)
        {
            return;
        }

        var cutoff = now - DedupeWindow;
        foreach (var pair in _seenCommands)
        {
            if (pair.Value < cutoff)
            {
                _ = _seenCommands.TryRemove(pair.Key, out _);
            }
        }
    }

    private void CancelInflight()
    {
        foreach (var pair in _inflight)
        {
            if (_inflight.TryRemove(pair.Key, out var cts))
            {
                SafeCancel(cts);
            }
        }
    }

    private async Task WriteLoopAsync(ClientWebSocket socket, ChannelReader<WsFrame> outbound, CancellationTokenSource linked)
    {
        try
        {
            await foreach (var frame in outbound.ReadAllAsync(linked.Token).ConfigureAwait(false))
            {
                var bytes = JsonDefaults.SerializeToUtf8Bytes(frame);
                if (bytes.Length > WsFrame.MaxFrameBytes)
                {
                    _logger.LogWarning("Outbound frame {Type} {Name} of {Bytes} bytes exceeds the limit; dropped", frame.Type, frame.Name, bytes.Length);
                    continue;
                }

                await socket.SendAsync(new ReadOnlyMemory<byte>(bytes), WebSocketMessageType.Text, true, linked.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "WebSocket send failed; dropping the connection");
            await linked.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void SetConnected(bool connected)
    {
        var value = connected ? 1 : 0;
        if (Interlocked.Exchange(ref _connected, value) == value)
        {
            return;
        }

        try
        {
            ConnectivityChanged?.Invoke(this, connected ? ConnectivityState.Online : ConnectivityState.Offline);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConnectivityChanged listener threw");
        }
    }
}
