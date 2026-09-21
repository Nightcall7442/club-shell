using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Ipc;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Remote;

/// <summary>
/// Runs an interactive remote-control session (SERVER_API.md §6.1 <c>remoteControlStart</c>/<c>remoteControlStop</c>):
/// connects to the relay WebSocket with the session token, streams periodic <see cref="ScreenCapture"/> frames as binary
/// messages at the requested FPS, and — when input is allowed by both the command and <c>remoteAdmin.allowRemoteInput</c>
/// — injects the mouse/keyboard events the relay sends back via <c>SendInput</c> on the input desktop. The session stops
/// on <see cref="StopAsync"/>, when the socket closes, after an idle period, or at a hard duration cap. Frames are never
/// logged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RemoteInputService : IDisposable
{
    /// <summary>Hard cap on a single remote-control session.</summary>
    public static TimeSpan MaxSessionDuration { get; } = TimeSpan.FromHours(2);

    /// <summary>Auto-stop after this long with no input received from the relay.</summary>
    public static TimeSpan IdleTimeout { get; } = TimeSpan.FromMinutes(5);

    private const int StreamMaxWidth = 1280;
    private const uint DesktopAccess = NativeConst.DESKTOP_READOBJECTS | NativeConst.DESKTOP_CREATEWINDOW |
        NativeConst.DESKTOP_WRITEOBJECTS | NativeConst.DESKTOP_ENUMERATE | NativeConst.DESKTOP_JOURNALPLAYBACK;

    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web);

    private readonly ScreenCapture _screen;
    private readonly IAdminEventSink _adminSink;
    private readonly IClock _clock;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly ILogger<RemoteInputService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();

    private ControlSession? _current;

    /// <summary>Creates the service.</summary>
    public RemoteInputService(
        ScreenCapture screen,
        IAdminEventSink adminSink,
        IClock clock,
        IOptionsMonitor<AgentSettings> settings,
        ILogger<RemoteInputService> logger)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(adminSink);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _screen = screen;
        _adminSink = adminSink;
        _clock = clock;
        _settings = settings;
        _logger = logger;
    }

    /// <summary><see langword="true"/> while a remote-control session is running.</summary>
    public bool IsActive
    {
        get { lock (_sync) { return _current is not null; } }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ControlSession? session;
        lock (_sync)
        {
            session = _current;
        }

        try
        {
            session?.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The session runner already finished and disposed it.
        }

        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Starts (or, for the same token, re-acknowledges) a remote-control session.</summary>
    /// <exception cref="IpcException">Screen capture is disabled; the relay could not be reached.</exception>
    public async Task<RemoteControlStartResult> StartAsync(RemoteControlStartCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_settings.CurrentValue.RemoteAdmin.AllowScreenCapture)
        {
            throw new IpcException(IpcError.PolicyDenied("remoteAdmin.allowScreenCapture"));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ControlSession? previous;
            lock (_sync)
            {
                previous = _current;
            }

            if (previous is not null)
            {
                if (string.Equals(previous.Token, command.SessionToken, StringComparison.Ordinal))
                {
                    return new RemoteControlStartResult(previous.StartedAt);
                }

                await StopSessionAsync(previous).ConfigureAwait(false);
            }

            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + command.SessionToken);
            try
            {
                await socket.ConnectAsync(new Uri(command.RelayUrl), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or UriFormatException or InvalidOperationException)
            {
                socket.Dispose();
                throw new IpcException(IpcError.Of(ErrorCode.ServerUnavailable, "Relay connection failed: " + ex.Message));
            }

            var startedAt = _clock.UtcNow;
            var cts = new CancellationTokenSource(MaxSessionDuration);
            var session = new ControlSession(command.SessionToken, startedAt, socket, cts)
            {
                LastActivityUtcTicks = startedAt.UtcTicks,
            };
            lock (_sync)
            {
                _current = session;
            }

            session.Runner = Task.Run(() => RunSessionAsync(session, command), CancellationToken.None);
            await PublishAsync(RemoteControlState.Started, startedAt, command.AdminName).ConfigureAwait(false);
            _logger.LogInformation("Remote control started by {Admin} (input={Input}, {Fps} fps)", command.AdminName, command.AllowInput, command.Fps);
            return new RemoteControlStartResult(startedAt);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops the session that matches <paramref name="command"/>'s token; a no-op returns a zero-duration result.</summary>
    public async Task<RemoteControlStopResult> StopAsync(RemoteControlStopCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ControlSession? session;
            lock (_sync)
            {
                session = _current;
            }

            if (session is null || !string.Equals(session.Token, command.SessionToken, StringComparison.Ordinal))
            {
                return new RemoteControlStopResult(_clock.UtcNow, 0);
            }

            var duration = (int)Math.Max(0, (_clock.UtcNow - session.StartedAt).TotalSeconds);
            await StopSessionAsync(session).ConfigureAwait(false);
            return new RemoteControlStopResult(_clock.UtcNow, duration);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopSessionAsync(ControlSession session)
    {
        try
        {
            await session.Cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }

        try
        {
            await session.Runner.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Remote-control session did not stop within the grace period");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Remote-control session runner faulted while stopping");
        }
    }

    private async Task RunSessionAsync(ControlSession session, RemoteControlStartCommand command)
    {
        var allowInput = command.AllowInput && _settings.CurrentValue.RemoteAdmin.AllowRemoteInput;
        BlockingCollection<InputMessage>? queue = null;
        Thread? inputThread = null;
        try
        {
            if (allowInput)
            {
                queue = new BlockingCollection<InputMessage>(256);
                inputThread = StartInputThread(queue, session.Cts.Token);
            }

            var receive = ReceiveLoopAsync(session, queue);
            var capture = CaptureLoopAsync(session, command.Fps);
            await Task.WhenAny(receive, capture).ConfigureAwait(false);

            try
            {
                await session.Cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Ignore.
            }

            await Task.WhenAll(Swallow(receive), Swallow(capture)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Remote-control session loop faulted");
        }
        finally
        {
            queue?.CompleteAdding();
            inputThread?.Join(TimeSpan.FromSeconds(2));
            queue?.Dispose();

            await CloseSocketAsync(session.Socket).ConfigureAwait(false);
            session.Socket.Dispose();
            session.Cts.Dispose();

            var stoppedAt = _clock.UtcNow;
            await PublishAsync(RemoteControlState.Stopped, stoppedAt, command.AdminName).ConfigureAwait(false);
            lock (_sync)
            {
                if (ReferenceEquals(_current, session))
                {
                    _current = null;
                }
            }

            _logger.LogInformation("Remote control stopped after {Seconds}s", (int)(stoppedAt - session.StartedAt).TotalSeconds);
        }
    }

    private async Task CaptureLoopAsync(ControlSession session, int fps)
    {
        var token = session.Cts.Token;
        var delay = TimeSpan.FromSeconds(1.0 / Math.Clamp(fps <= 0 ? _settings.CurrentValue.RemoteAdmin.CaptureFps : fps, 1, 60));
        var quality = _settings.CurrentValue.RemoteAdmin.CaptureQuality;

        while (!token.IsCancellationRequested && session.Socket.State == WebSocketState.Open)
        {
            var idle = _clock.UtcNow.UtcTicks - Interlocked.Read(ref session.LastActivityUtcTicks);
            if (idle > IdleTimeout.Ticks)
            {
                _logger.LogInformation("Remote control idle for {Timeout}; stopping", IdleTimeout);
                return;
            }

            try
            {
                var frame = await _screen.CaptureAsync(null, quality, StreamMaxWidth, token).ConfigureAwait(false);
                if (frame is not null && session.Socket.State == WebSocketState.Open)
                {
                    await session.Socket.SendAsync(new ArraySegment<byte>(frame.Png), WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IpcException ex)
            {
                _logger.LogWarning("Capture blocked during remote control: {Message}", ex.Error.Message);
                return;
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
                return;
            }

            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ReceiveLoopAsync(ControlSession session, BlockingCollection<InputMessage>? queue)
    {
        var token = session.Cts.Token;
        var buffer = new byte[8192];
        while (!token.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            try
            {
                do
                {
                    result = await session.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            Interlocked.Exchange(ref session.LastActivityUtcTicks, _clock.UtcNow.UtcTicks);
            if (result.MessageType == WebSocketMessageType.Text && queue is not null)
            {
                DispatchInput(message.ToArray(), queue);
            }
        }
    }

    private void DispatchInput(byte[] payload, BlockingCollection<InputMessage> queue)
    {
        InputMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<InputMessage>(payload, InputJson);
        }
        catch (JsonException)
        {
            return;
        }

        if (message is { Type: not null } && !queue.IsAddingCompleted)
        {
            _ = queue.TryAdd(message);
        }
    }

    private Thread StartInputThread(BlockingCollection<InputMessage> queue, CancellationToken token)
    {
        var thread = new Thread(() => InputPump(queue, token))
        {
            IsBackground = true,
            Name = "clubshell-rc-input",
        };
        thread.Start();
        return thread;
    }

    private void InputPump(BlockingCollection<InputMessage> queue, CancellationToken token)
    {
        using var desktop = User32.OpenInputDesktop(0, false, DesktopAccess);
        if (desktop.IsInvalid || !User32.SetThreadDesktop(desktop))
        {
            _logger.LogWarning("Could not bind to the input desktop ({Error}); remote input disabled for this session", Win32Error.Last());
            return;
        }

        try
        {
            foreach (var message in queue.GetConsumingEnumerable(token))
            {
                Inject(message);
            }
        }
        catch (OperationCanceledException)
        {
            // Session ended.
        }
        catch (InvalidOperationException)
        {
            // Adding completed and the collection drained.
        }
    }

    private static void Inject(InputMessage message)
    {
        switch (message.Type)
        {
            case "move":
                Send(INPUT.Mouse(
                    (int)(Math.Clamp(message.X ?? 0, 0, 1) * 65535),
                    (int)(Math.Clamp(message.Y ?? 0, 0, 1) * 65535),
                    0,
                    NativeConst.MOUSEEVENTF_MOVE | NativeConst.MOUSEEVENTF_ABSOLUTE | NativeConst.MOUSEEVENTF_VIRTUALDESK));
                break;

            case "down":
                Send(INPUT.Mouse(0, 0, 0, ButtonFlag(message.Button, down: true)));
                break;

            case "up":
                Send(INPUT.Mouse(0, 0, 0, ButtonFlag(message.Button, down: false)));
                break;

            case "wheel":
                Send(INPUT.Mouse(0, 0, unchecked((uint)(message.Delta ?? 0)), NativeConst.MOUSEEVENTF_WHEEL));
                break;

            case "keyDown":
                Send(INPUT.Keyboard((ushort)(message.Vk ?? 0), 0, message.Extended == true ? NativeConst.KEYEVENTF_EXTENDEDKEY : 0));
                break;

            case "keyUp":
                Send(INPUT.Keyboard(
                    (ushort)(message.Vk ?? 0),
                    0,
                    NativeConst.KEYEVENTF_KEYUP | (message.Extended == true ? NativeConst.KEYEVENTF_EXTENDEDKEY : 0)));
                break;

            default:
                break;
        }
    }

    private static uint ButtonFlag(int? button, bool down) => button switch
    {
        1 => down ? NativeConst.MOUSEEVENTF_RIGHTDOWN : NativeConst.MOUSEEVENTF_RIGHTUP,
        2 => down ? NativeConst.MOUSEEVENTF_MIDDLEDOWN : NativeConst.MOUSEEVENTF_MIDDLEUP,
        _ => down ? NativeConst.MOUSEEVENTF_LEFTDOWN : NativeConst.MOUSEEVENTF_LEFTUP,
    };

    private static void Send(INPUT input) => _ = User32.SendInput(1, ref input, INPUT.Size);

    private async Task PublishAsync(RemoteControlState state, DateTimeOffset at, string adminName)
    {
        try
        {
            var evt = new RemoteControlEvent(state, at, _settings.CurrentValue.RemoteAdmin.ShowIndicator, adminName);
            await _adminSink.PublishRemoteControlAsync(evt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Publishing remote-control {State} to the Shell failed", state);
        }
    }

    private static async Task CloseSocketAsync(ClientWebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "stopped", timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Best effort.
        }
    }

    private static async Task Swallow(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
        }
    }

    private sealed class ControlSession(string token, DateTimeOffset startedAt, ClientWebSocket socket, CancellationTokenSource cts)
    {
        public string Token { get; } = token;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public ClientWebSocket Socket { get; } = socket;

        public CancellationTokenSource Cts { get; } = cts;

        public Task Runner { get; set; } = Task.CompletedTask;

        public long LastActivityUtcTicks;
    }

    private sealed record InputMessage(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("x")] double? X,
        [property: JsonPropertyName("y")] double? Y,
        [property: JsonPropertyName("button")] int? Button,
        [property: JsonPropertyName("delta")] int? Delta,
        [property: JsonPropertyName("vk")] int? Vk,
        [property: JsonPropertyName("extended")] bool? Extended);
}
