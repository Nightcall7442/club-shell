using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Channels;
using ClubShell.Agent.Games;
using ClubShell.Agent.Ipc.Handlers;
using ClubShell.Agent.Policy;
using ClubShell.Agent.Power;
using ClubShell.Agent.Remote;
using ClubShell.Agent.Server;
using ClubShell.Agent.Session;
using ClubShell.Agent.Updates;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Shop;
using ClubShell.Contracts.Users;
using ClubShell.Contracts.Wallet;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Options;
using PlaySession = ClubShell.Contracts.Sessions.Session;

namespace ClubShell.Agent.Ipc;

/// <summary>Publishes Agent → Shell events (IPC_PROTOCOL.md §8) to every authenticated pipe connection.</summary>
public interface IIpcEventPublisher
{
    /// <summary>Publishes <paramref name="payload"/> (a Contracts type) as event <paramref name="eventName"/>.</summary>
    ValueTask PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken cancellationToken);

    /// <summary>Publishes a ready-made event envelope.</summary>
    ValueTask PublishAsync(IpcEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>
/// Outbox between the Agent subsystems and the pipe: every <c>*EventSink</c> interface of the L1 subsystems is
/// implemented here by translating the call into an IPC event envelope that <see cref="PipeServer"/> broadcasts.
/// The bridge is a dependency leaf (no handler or server references) so it can be injected into
/// <c>SessionManager</c>, <c>GameLaunchService</c>, <c>PolicyEnforcer</c>, … without cycles; it also carries the
/// <see cref="IShellConnectionState"/> flag the server maintains. Events published while no Shell is connected are
/// dropped (the Shell resyncs with <c>auth.status</c> / <c>session.get</c> after reconnecting).
/// </summary>
public sealed class IpcEventBridge :
    IIpcEventPublisher,
    IShellConnectionState,
    ISessionEventSink,
    IGameEventSink,
    IPolicyEventSink,
    IPowerNotifier,
    IUpdateEventSink,
    IConnectivityEventSink,
    ISysMetricsSink,
    IAdminEventSink,
    IServerPushSink,
    IShellCaptureRequester
{
    /// <summary>Events buffered while the broadcast loop is busy; older ones are dropped beyond this.</summary>
    public const int OutboxCapacity = 2048;

    private readonly Channel<IpcEnvelope> _outbox = Channel.CreateBounded<IpcEnvelope>(new BoundedChannelOptions(OutboxCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });

    private readonly ShellUserContext _users;
    private readonly ShellSettingsStore _shellSettings;
    private readonly IServiceProvider _services;
    private readonly IClock _clock;
    private readonly ILogger<IpcEventBridge> _logger;
    private PcMetrics? _lastMetrics;
    private ConnectivityEvent? _lastConnectivity;
    private int _connected;
    private long _published;
    private long _dropped;

    /// <summary>Creates the bridge. <paramref name="services"/> resolves the session service lazily (it depends on this bridge).</summary>
    public IpcEventBridge(ShellUserContext users, ShellSettingsStore shellSettings, IServiceProvider services, IClock clock, ILogger<IpcEventBridge> logger)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(shellSettings);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _users = users;
        _shellSettings = shellSettings;
        _services = services;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Raised when <see cref="IsConnected"/> changes.</summary>
    public event EventHandler<bool>? ConnectedChanged;

    /// <summary>Events waiting for the broadcast loop of <see cref="PipeServer"/>.</summary>
    public ChannelReader<IpcEnvelope> Outbox => _outbox.Reader;

    /// <inheritdoc />
    public bool IsConnected => Volatile.Read(ref _connected) == 1;

    /// <summary>Last metrics sample seen (served by <c>sys.metrics</c>).</summary>
    public PcMetrics? LastMetrics => Volatile.Read(ref _lastMetrics);

    /// <summary>Last connectivity event published.</summary>
    public ConnectivityEvent? LastConnectivity => Volatile.Read(ref _lastConnectivity);

    /// <summary>Events accepted into the outbox.</summary>
    public long Published => Interlocked.Read(ref _published);

    /// <summary>Events dropped because the outbox was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <inheritdoc />
    public ValueTask PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken cancellationToken) =>
        PublishAsync(IpcEnvelope.Event(eventName, payload, _clock.UtcNow), cancellationToken);

    /// <inheritdoc />
    public ValueTask PublishAsync(IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();
        if (envelope.Kind != IpcKind.Event)
        {
            throw new ArgumentException("Only event envelopes can be published", nameof(envelope));
        }

        if (_outbox.Writer.TryWrite(envelope))
        {
            Interlocked.Increment(ref _published);
        }
        else
        {
            Interlocked.Increment(ref _dropped);
            _logger.LogWarning("IPC outbox closed; event {Name} dropped", envelope.Name);
        }

        return ValueTask.CompletedTask;
    }

    // ---- ISessionEventSink ------------------------------------------------------------------

    /// <inheritdoc />
    ValueTask ISessionEventSink.PublishAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        if (sessionEvent.Type != SessionEventType.Warning)
        {
            // Every other transition is mirrored by session.updated / session.ended.
            return ValueTask.CompletedTask;
        }

        int minutesLeft = sessionEvent.DataAs<SessionWarningData>()?.MinutesLeft ?? 0;
        PlaySession? current = _services.GetService<ISessionService>()?.Current;
        int secondsLeft = current is { SecondsLeft: >= 0 } s ? s.SecondsLeft : minutesLeft * 60;
        DateTimeOffset endsAt = current?.EndsAt ?? sessionEvent.At.AddSeconds(secondsLeft);
        return PublishAsync(IpcMessages.Events.SessionWarning, new SessionWarning(sessionEvent.SessionId, minutesLeft, secondsLeft, endsAt), cancellationToken);
    }

    /// <inheritdoc />
    ValueTask ISessionEventSink.PublishSessionUpdatedAsync(PlaySession session, CancellationToken cancellationToken) =>
        PublishAsync(IpcMessages.Events.SessionUpdated, session, cancellationToken);

    /// <inheritdoc />
    async ValueTask ISessionEventSink.PublishSessionEndedAsync(SessionEndedEvent ended, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ended);
        await PublishAsync(IpcMessages.Events.SessionEnded, ended, cancellationToken).ConfigureAwait(false);
        if (ended.Reason == SessionEndReason.TimeUp)
        {
            // IPC_PROTOCOL.md §9.2: session.ended{timeUp} → shell.command{lock}.
            await PublishAsync(IpcMessages.Events.ShellCommand, ShellCommand.Of(ShellCommandKind.Lock, new LockCommand("timeUp", null), Guid.NewGuid()), cancellationToken).ConfigureAwait(false);
        }
    }

    // ---- IGameEventSink / IPolicyEventSink / IPowerNotifier ---------------------------------

    /// <inheritdoc />
    ValueTask IGameEventSink.PublishAsync(GameStateChanged change, CancellationToken cancellationToken) =>
        PublishAsync(IpcMessages.Events.GameStateChanged, change, cancellationToken);

    /// <inheritdoc />
    ValueTask IPolicyEventSink.PublishAsync(PolicyChanged changed, CancellationToken cancellationToken) =>
        PublishAsync(IpcMessages.Events.PolicyChanged, changed, cancellationToken);

    /// <inheritdoc />
    ValueTask IPowerNotifier.NotifyAsync(ShellCommand command, CancellationToken cancellationToken) =>
        PublishAsync(IpcMessages.Events.ShellCommand, command, cancellationToken);

    // ---- IUpdateEventSink -------------------------------------------------------------------

    /// <inheritdoc />
    ValueTask IUpdateEventSink.PublishAsync(UpdateAvailable available, CancellationToken cancellationToken) =>
        PublishAsync(IpcMessages.Events.UpdateAvailable, available, cancellationToken);

    /// <inheritdoc />
    ValueTask IUpdateEventSink.PublishAsync(UpdateProgress progress, CancellationToken cancellationToken) =>
        PublishAsync(IpcMessages.Events.UpdateProgress, progress, cancellationToken);

    /// <inheritdoc />
    ValueTask IUpdateEventSink.PublishAsync(UpdateReady ready, CancellationToken cancellationToken) =>
        PublishAsync(IpcMessages.Events.UpdateReady, ready, cancellationToken);

    // ---- IConnectivityEventSink / ISysMetricsSink -------------------------------------------

    /// <inheritdoc />
    ValueTask IConnectivityEventSink.PublishAsync(ConnectivityEvent connectivity, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _lastConnectivity, connectivity);
        return PublishAsync(IpcMessages.Events.SysConnectivity, connectivity, cancellationToken);
    }

    /// <inheritdoc />
    ValueTask ISysMetricsSink.PublishAsync(PcMetrics metrics, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _lastMetrics, metrics);
        bool wanted = _shellSettings.Get().ShowMetricsOverlay || (_services.GetService<ISessionService>()?.State.IsOpen() ?? false);
        return wanted ? PublishAsync(IpcMessages.Events.SysMetrics, metrics, cancellationToken) : ValueTask.CompletedTask;
    }

    // ---- IAdminEventSink --------------------------------------------------------------------

    /// <inheritdoc />
    ValueTask IAdminEventSink.PublishMessageAsync(AdminMessage message, CancellationToken cancellationToken) =>
        PublishAsync(IpcMessages.Events.AdminMessage, message, cancellationToken);

    /// <inheritdoc />
    ValueTask IAdminEventSink.PublishRemoteControlAsync(RemoteControlEvent remoteControl, CancellationToken cancellationToken) =>
        PublishAsync(IpcMessages.Events.AdminRemoteControl, remoteControl, cancellationToken);

    // ---- IServerPushSink --------------------------------------------------------------------

    /// <inheritdoc />
    async ValueTask IServerPushSink.PublishAsync(WsPushKind kind, JsonElement? payload, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case WsPushKind.WalletUpdated:
                await OnWalletUpdatedAsync(payload, cancellationToken).ConfigureAwait(false);
                break;
            case WsPushKind.ChatMessage:
                await PublishRawAsync(IpcMessages.Events.ChatMessage, payload, cancellationToken).ConfigureAwait(false);
                break;
            case WsPushKind.Notification:
                await PublishRawAsync(IpcMessages.Events.NotificationPush, payload, cancellationToken).ConfigureAwait(false);
                break;
            case WsPushKind.OrderUpdated:
                await PublishRawAsync(IpcMessages.Events.ShopOrderUpdated, payload, cancellationToken).ConfigureAwait(false);
                if (Decode<Order>(payload) is { } order)
                {
                    var notification = new Notification(Guid.NewGuid(), "Order", $"Order {ShortId(order.Id)}: {order.Status}", order.Status == OrderStatus.Cancelled ? NotificationLevel.Warning : NotificationLevel.Info, 10);
                    await PublishAsync(IpcMessages.Events.NotificationPush, notification, cancellationToken).ConfigureAwait(false);
                }

                break;
            case WsPushKind.BookingUpdated:
                // SERVER_API.md §6.3: bookingUpdated → notification.push.
                if (Decode<Booking>(payload) is { } booking)
                {
                    NotificationLevel level = booking.Status is BookingStatus.Cancelled or BookingStatus.Expired ? NotificationLevel.Warning : NotificationLevel.Info;
                    var notification = new Notification(Guid.NewGuid(), "Booking", $"Booking {ShortId(booking.Id)}: {booking.Status}", level, 10);
                    await PublishAsync(IpcMessages.Events.NotificationPush, notification, cancellationToken).ConfigureAwait(false);
                }

                break;
            case WsPushKind.TournamentUpdated:
                // SERVER_API.md §6.3: tournamentUpdated → notification.push.
                if (Decode<Tournament>(payload) is { } tournament)
                {
                    var notification = new Notification(Guid.NewGuid(), tournament.Title, $"Tournament {tournament.State}", NotificationLevel.Info, 10);
                    await PublishAsync(IpcMessages.Events.NotificationPush, notification, cancellationToken).ConfigureAwait(false);
                }

                break;
            case WsPushKind.SessionUpdated:
                if (Decode<PlaySession>(payload) is { } session && _services.GetService<SessionManager>() is { } sessions)
                {
                    await sessions.ApplyServerSessionAsync(session, cancellationToken).ConfigureAwait(false);
                }

                break;
            case WsPushKind.UserRevoked:
                if (Decode<UserRevokedPush>(payload) is not { } revoked)
                {
                    break;
                }

                if (_services.GetService<SignInGuard>() is { } guard)
                {
                    await guard.HandleRevokedAsync(revoked, cancellationToken).ConfigureAwait(false);
                }
                else if (_users.User is { } user && user.Id == revoked.UserId)
                {
                    // Hosts without the guard (tests): sign out with the server's reason.
                    _logger.LogWarning("User {UserId} revoked by the server: {Reason}", revoked.UserId, revoked.Reason);
                    _users.Clear();
                    await PublishAsync(IpcMessages.Events.AuthExpired, new AuthExpired(SignInGuard.MapRevokedReason(revoked.Reason)), cancellationToken).ConfigureAwait(false);
                }

                break;
            default:
                _logger.LogDebug("Server push {Kind} has no IPC event; ignored", kind);
                break;
        }
    }

    // ---- IShellCaptureRequester -------------------------------------------------------------

    /// <summary>
    /// The IPC protocol has no Agent → Shell request (events only), so a Shell-side capture cannot be requested;
    /// always <see langword="null"/>. <c>ScreenCapture</c> falls back to its own GDI capture.
    /// </summary>
    Task<byte[]?> IShellCaptureRequester.RequestCaptureAsync(int? monitor, int quality, int? maxWidth, CancellationToken cancellationToken) =>
        Task.FromResult<byte[]?>(null);

    /// <summary>Called by <see cref="PipeServer"/> when the set of authenticated connections changes.</summary>
    internal void SetConnected(bool connected)
    {
        int value = connected ? 1 : 0;
        if (Interlocked.Exchange(ref _connected, value) != value)
        {
            ConnectedChanged?.Invoke(this, connected);
        }
    }

    /// <summary>
    /// Re-sends the current server connectivity (<c>sys.connectivity</c>) after a Shell completed <c>auth.hello</c>: a
    /// Shell that starts or reconnects assumes it is online until told otherwise, and a transition published while no
    /// Shell was connected is lost.
    /// </summary>
    public ValueTask PublishCurrentConnectivityAsync(CancellationToken cancellationToken)
    {
        int queued = _services.GetService<IOfflineQueueDepth>()?.Count ?? LastConnectivity?.QueuedEvents ?? 0;
        ConnectivityEvent current = _services.GetService<IServerLinkState>() is { } link
            ? new ConnectivityEvent(link.Connectivity, link.ConnectivitySince, queued)
            : LastConnectivity ?? new ConnectivityEvent(ConnectivityState.Offline, _clock.UtcNow, queued);
        Volatile.Write(ref _lastConnectivity, current);
        return PublishAsync(IpcMessages.Events.SysConnectivity, current, cancellationToken);
    }

    private static string ShortId(Guid id) => id.ToString("N")[..8];

    /// <summary>
    /// SERVER_API.md §6.3 <c>walletUpdated</c>: only the signed-in player's balance is taken (a push for another player,
    /// e.g. one who left, is dropped): the user snapshot and the offline cache follow it, then the Shell gets
    /// <c>wallet.updated</c>.
    /// </summary>
    private async ValueTask OnWalletUpdatedAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        if (Decode<Balance>(payload) is not { } balance)
        {
            _logger.LogWarning("walletUpdated push without a balance; ignored");
            return;
        }

        if (!_users.UpdateBalance(balance))
        {
            _logger.LogDebug("walletUpdated for {UserId} ignored: not the signed-in player", balance.UserId);
            return;
        }

        if (_services.GetService<OfflineSessionStore>() is { } store)
        {
            try
            {
                await store.UpdateCachedBalanceAsync(balance.UserId, balance.Amount, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Cached balance of {UserId} not updated", balance.UserId);
            }
        }

        await PublishRawAsync(IpcMessages.Events.WalletUpdated, payload, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask PublishRawAsync(string eventName, JsonElement? payload, CancellationToken cancellationToken)
    {
        if (payload is not { ValueKind: JsonValueKind.Object })
        {
            _logger.LogWarning("Server push for {Event} carried no object payload; ignored", eventName);
            return ValueTask.CompletedTask;
        }

        return PublishAsync(IpcEnvelope.Event(eventName, payload, _clock.UtcNow), cancellationToken);
    }

    private T? Decode<T>(JsonElement? payload) where T : class
    {
        if (payload is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        try
        {
            return JsonDefaults.FromElement<T>(element);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Server push payload is not a valid {Type}", typeof(T).Name);
            return null;
        }
    }
}

/// <summary>Counters of <see cref="PipeServer"/>.</summary>
/// <param name="Connections">Open connections.</param>
/// <param name="Accepted">Connections accepted since start.</param>
/// <param name="Rejected">Connections refused by client validation.</param>
/// <param name="Requests">Requests answered.</param>
/// <param name="Failed">Requests answered with an error.</param>
/// <param name="EventsSent">Event envelopes queued to connections.</param>
/// <param name="EventsDropped">Events with no authenticated connection or a full queue.</param>
/// <param name="RateLimited">Requests refused by the per-connection token bucket.</param>
public sealed record PipeServerStats(
    int Connections,
    long Accepted,
    long Rejected,
    long Requests,
    long Failed,
    long EventsSent,
    long EventsDropped,
    long RateLimited);

/// <summary>
/// The named-pipe server the Shell connects to (IPC_PROTOCOL.md §1–§4): <c>\\.\pipe\&lt;ipc.pipeName&gt;</c>,
/// byte stream with <c>[u32 LE length][JSON]</c> frames, DACL from <see cref="PipeSecurityFactory"/>, client process
/// verification at accept, <c>auth.hello</c> state machine, per-connection token bucket, bounded concurrency,
/// request timeout, single writer per connection, ping watchdog (3 missed → connection dropped and the Shell process
/// terminated so the watchdog restarts it) and event broadcast from <see cref="IpcEventBridge"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PipeServer : BackgroundService
{
    private const int BufferSize = 64 * 1024;
    private static readonly TimeSpan HousekeepingInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CreateRetryDelay = TimeSpan.FromSeconds(1);

    private readonly MessageDispatcher _dispatcher;
    private readonly IpcEventBridge _bridge;
    private readonly ShellUserContext _users;
    private readonly ShellTokenStore _token;
    private readonly ISessionService _sessions;
    private readonly IKioskCredentials _kiosk;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<PipeServer> _logger;
    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();
    private readonly ConcurrentDictionary<Guid, Task> _connectionTasks = new();
    private readonly ConcurrentDictionary<int, DateTime?> _silentShells = new();
    private SemaphoreSlim? _slots;
    private long _accepted;
    private long _rejected;
    private long _requests;
    private long _failed;
    private long _eventsSent;
    private long _eventsDropped;
    private long _rateLimited;

    /// <summary>Creates the server.</summary>
    public PipeServer(
        MessageDispatcher dispatcher,
        IpcEventBridge bridge,
        ShellUserContext users,
        ShellTokenStore token,
        ISessionService sessions,
        IKioskCredentials kiosk,
        IOptionsMonitor<AgentSettings> settings,
        IClock clock,
        ILogger<PipeServer> logger,
        IServiceProvider? services = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        // Resolved per check, not at construction: the games module and this server share dependencies.
        IsGameRunning = () => services?.GetService<IRunningGamesSource>()?.Snapshot().Count > 0;
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(kiosk);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _dispatcher = dispatcher;
        _bridge = bridge;
        _users = users;
        _token = token;
        _sessions = sessions;
        _kiosk = kiosk;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>How strictly kiosk-user clients are verified (default: must run from <c>shell.exePath</c>).</summary>
    public ClientValidationMode ClientValidation { get; set; } = ClientValidationMode.ExePath;

    /// <summary>Signer thumbprint for <see cref="ClientValidationMode.SignatureVerified"/>; <see langword="null"/> = the Agent's own signer.</summary>
    public string? ExpectedSignerThumbprint { get; set; }

    /// <summary>
    /// Terminate the Shell process when its connection misses <c>ipc.missedHeartbeatsBeforeKill</c> pings (default on);
    /// while a game runs only the connection is dropped (D-99).
    /// </summary>
    public bool KillShellOnHeartbeatLoss { get; set; } = true;

    /// <summary>Whether a game runs now (the heartbeat watchdog leaves the Shell's process alone then). Defaults to the games module.</summary>
    public Func<bool> IsGameRunning { get; set; }

    /// <summary>Server-side request timeout (answered with <c>timeout</c>). Long enough for a game launch.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>A connection that has not sent <c>auth.hello</c> within this time is closed.</summary>
    public TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Requests processed concurrently per connection; further frames wait in the pipe.</summary>
    public int MaxConcurrentRequestsPerConnection { get; set; } = 8;

    /// <summary>Short pipe name (<c>agent.json → ipc.pipeName</c>).</summary>
    public string PipeName => _settings.CurrentValue.Ipc.PipeName;

    /// <summary><see langword="true"/> while at least one connection completed <c>auth.hello</c> (mirrors <see cref="IpcEventBridge.IsConnected"/>).</summary>
    public bool IsShellConnected => _bridge.IsConnected;

    /// <summary>Open connections.</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>Counter snapshot.</summary>
    public PipeServerStats Stats => new(
        _connections.Count,
        Interlocked.Read(ref _accepted),
        Interlocked.Read(ref _rejected),
        Interlocked.Read(ref _requests),
        Interlocked.Read(ref _failed),
        Interlocked.Read(ref _eventsSent),
        Interlocked.Read(ref _eventsDropped),
        Interlocked.Read(ref _rateLimited));

    /// <summary>Writes the Shell token (ARCHITECTURE.md §6.1 step 10) before the pipe opens, so a Shell launched afterwards can read it.</summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _token.Generate(_kiosk.Sid);
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        foreach (Connection connection in _connections.Values)
        {
            Close(connection, "agent stopping");
        }

        Task[] pending = _connectionTasks.Values.ToArray();
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("{Count} pipe connections did not close within 5 s", pending.Length);
            }
            catch (OperationCanceledException)
            {
                // Host shutdown timed out; connections are disposed below anyway.
            }
        }

        foreach (Connection connection in _connections.Values)
        {
            connection.Dispose();
        }

        _connections.Clear();
        _bridge.SetConnected(false);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _slots?.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        IpcSettings ipc = _settings.CurrentValue.Ipc;
        _slots = new SemaphoreSlim(Math.Max(1, ipc.MaxConnections), Math.Max(1, ipc.MaxConnections));
        _logger.LogInformation("IPC server listening on \\\\.\\pipe\\{Pipe} (max {Max} connections, {Rate} req/s, client validation {Mode})", ipc.PipeName, ipc.MaxConnections, ipc.RequestsPerSecond, ClientValidation);

        Task[] loops =
        {
            AcceptLoopAsync(ipc, stoppingToken),
            BroadcastLoopAsync(stoppingToken),
            HousekeepingLoopAsync(stoppingToken),
        };

        try
        {
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    // ---- accept -----------------------------------------------------------------------------

    private async Task AcceptLoopAsync(IpcSettings ipc, CancellationToken stoppingToken)
    {
        SemaphoreSlim slots = _slots!;
        while (!stoppingToken.IsCancellationRequested)
        {
            await slots.WaitAsync(stoppingToken).ConfigureAwait(false);
            string kioskSid = _kiosk.Sid;
            _ = _token.EnsureAcl(kioskSid);

            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    ipc.PipeName,
                    PipeDirection.InOut,
                    Math.Max(1, ipc.MaxConnections),
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    BufferSize,
                    BufferSize,
                    PipeSecurityFactory.Create(kioskSid));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                slots.Release();
                _logger.LogError(ex, "Named pipe {Pipe} could not be created; retrying in {Delay}s", ipc.PipeName, CreateRetryDelay.TotalSeconds);
                await Task.Delay(CreateRetryDelay, stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                slots.Release();
                break;
            }
            catch (IOException ex)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                slots.Release();
                _logger.LogWarning(ex, "WaitForConnection failed on {Pipe}", ipc.PipeName);
                continue;
            }

            PipeClientInfo? client;
            try
            {
                client = PipeSecurityFactory.ValidateClient(pipe, kioskSid, _settings.CurrentValue.Shell.ExePath, ClientValidation, ExpectedSignerThumbprint, _logger);
            }
            catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Pipe client validation failed");
                client = null;
            }

            if (client is null)
            {
                Interlocked.Increment(ref _rejected);
                await pipe.DisposeAsync().ConfigureAwait(false);
                slots.Release();
                continue;
            }

            var connection = new Connection(pipe, client, _clock.UtcNow, _clock.GetTimestamp(), ipc, MaxConcurrentRequestsPerConnection, stoppingToken);
            connection.Elevate = (level, _, _) => SetHello(connection, level >= IpcAuthLevel.Hello);
            _connections[connection.Id] = connection;
            Interlocked.Increment(ref _accepted);
            _logger.LogInformation("Pipe client connected: {ConnectionId} pid {Pid} session {Session} ({Kind}) {Exe}", connection.Id, client.Pid, client.WtsSessionId, client.IsKiosk ? "kiosk" : "privileged", client.ExePath);

            _connectionTasks[connection.Id] = RunConnectionAsync(connection);
        }
    }

    // ---- per-connection ---------------------------------------------------------------------

    private async Task RunConnectionAsync(Connection connection)
    {
        CancellationToken token = connection.Cts.Token;
        Task writer = WriteLoopAsync(connection);
        string closeReason = "peer closed";
        try
        {
            while (!token.IsCancellationRequested)
            {
                Frame frame = await ReadFrameAsync(connection, token).ConfigureAwait(false);
                if (frame.Closed)
                {
                    closeReason = frame.Reason ?? closeReason;
                    break;
                }

                if (frame.Reply is not null)
                {
                    await connection.Outbound.Writer.WriteAsync(frame.Reply, token).ConfigureAwait(false);
                    if (frame.Reply.Error is { Code: ErrorCode.VersionMismatch })
                    {
                        connection.Outbound.Writer.TryComplete();
                    }
                }

                if (frame.Envelope is not null)
                {
                    await HandleEnvelopeAsync(connection, frame.Envelope, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            closeReason = connection.CloseReason ?? "cancelled";
        }
        catch (EndOfStreamException)
        {
            closeReason = "peer closed";
        }
        catch (IOException ex)
        {
            closeReason = "pipe error: " + ex.Message;
        }
        catch (ObjectDisposedException)
        {
            closeReason = "disposed";
        }
        catch (ChannelClosedException)
        {
            closeReason = connection.CloseReason ?? "closing";
        }
        catch (Exception ex)
        {
            closeReason = "unexpected error";
            _logger.LogError(ex, "Pipe connection {ConnectionId} failed", connection.Id);
        }
        finally
        {
            connection.Outbound.Writer.TryComplete();
            try
            {
                await writer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The peer stopped reading; the pipe is torn down below.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug(ex, "Writer of {ConnectionId} ended with an error", connection.Id);
            }

            connection.Cancel();
            _connections.TryRemove(connection.Id, out _);
            _connectionTasks.TryRemove(connection.Id, out _);
            UpdateShellConnected();
            connection.Dispose();
            _slots?.Release();
            _logger.LogInformation("Pipe client disconnected: {ConnectionId} pid {Pid} ({Reason}; {Requests} requests)", connection.Id, connection.Client.Pid, closeReason, connection.RequestCount);
        }
    }

    private async Task WriteLoopAsync(Connection connection)
    {
        CancellationToken token = connection.Cts.Token;
        try
        {
            await foreach (IpcEnvelope envelope in connection.Outbound.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                byte[]? frame = Encode(envelope);
                if (frame is null)
                {
                    continue;
                }

                await connection.Pipe.WriteAsync(frame, token).ConfigureAwait(false);
                await connection.Pipe.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Connection closing.
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Write to {ConnectionId} failed", connection.Id);
        }
        catch (ObjectDisposedException)
        {
            // Pipe disposed by the reader side.
        }
        finally
        {
            // Everything queued has been flushed (or the pipe is gone): stop the reader.
            connection.Cancel();
        }
    }

    private async Task<Frame> ReadFrameAsync(Connection connection, CancellationToken token)
    {
        byte[] header = connection.Header;
        try
        {
            await connection.Pipe.ReadExactlyAsync(header, token).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return Frame.Close("peer closed");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        int max = Math.Min(connection.MaxMessageBytes, IpcEnvelope.MaxFrameBytes);
        if (length <= 0 || length > max)
        {
            _logger.LogWarning("Pipe client {ConnectionId} sent a frame of {Length} bytes (max {Max}); closing", connection.Id, length, max);
            return Frame.Close("protocolError: frame size");
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            try
            {
                await connection.Pipe.ReadExactlyAsync(buffer.AsMemory(0, length), token).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return Frame.Close("peer closed mid-frame");
            }

            return Decode(connection, buffer.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private Frame Decode(Connection connection, ReadOnlySpan<byte> json)
    {
        IpcEnvelope? envelope;
        try
        {
            envelope = JsonDefaults.Deserialize<IpcEnvelope>(json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Pipe client {ConnectionId} sent malformed JSON: {Message}", connection.Id, ex.Message);
            return TrySalvageId(json, out Guid id, out string? name)
                ? Frame.ReplyOnly(IpcEnvelope.Fail(id, ResponseNameOf(name), IpcError.ProtocolError("Malformed envelope: " + ex.Message), _clock.UtcNow))
                : Frame.Close("protocolError: malformed JSON");
        }

        if (envelope is null)
        {
            return Frame.Close("protocolError: null envelope");
        }

        IpcError? error = envelope.Validate();
        if (error is not null)
        {
            _logger.LogWarning("Pipe client {ConnectionId} sent an invalid envelope ({Name}): {Message}", connection.Id, envelope.Name, error.Message);
            Guid id = envelope.Id == Guid.Empty ? Guid.NewGuid() : envelope.Id;
            return Frame.ReplyOnly(IpcEnvelope.Fail(id, ResponseNameOf(envelope.Name), error, _clock.UtcNow));
        }

        return Frame.Of(envelope);
    }

    private async Task HandleEnvelopeAsync(Connection connection, IpcEnvelope envelope, CancellationToken token)
    {
        if (envelope.Kind != IpcKind.Request)
        {
            _logger.LogDebug("Ignoring {Kind} {Name} from {ConnectionId}: the Shell only sends requests", envelope.Kind, envelope.Name, connection.Id);
            return;
        }

        DateTimeOffset now = _clock.UtcNow;
        bool ping = string.Equals(envelope.Name, IpcMessages.Sys.Ping, StringComparison.Ordinal);
        if (ping)
        {
            connection.MarkPing(_clock.GetTimestamp());
            _ = ProcessAsync(connection, envelope, gated: false, token);
            return;
        }

        if (!connection.Bucket.TryTake(now))
        {
            Interlocked.Increment(ref _rateLimited);
            await connection.Outbound.Writer.WriteAsync(IpcEnvelope.Fail(envelope, IpcError.RateLimited(1), now), token).ConfigureAwait(false);
            return;
        }

        await connection.Gate.WaitAsync(token).ConfigureAwait(false);
        _ = ProcessAsync(connection, envelope, gated: true, token);
    }

    private async Task ProcessAsync(Connection connection, IpcEnvelope request, bool gated, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(RequestTimeout);
            IpcContext context = BuildContext(connection);
            IpcEnvelope response = await _dispatcher.DispatchAsync(context, request, timeout.Token).ConfigureAwait(false);
            connection.RequestCount++;
            Interlocked.Increment(ref _requests);
            if (response.IsError)
            {
                Interlocked.Increment(ref _failed);
            }

            await connection.Outbound.Writer.WriteAsync(response, token).ConfigureAwait(false);
            if (string.Equals(request.Name, IpcMessages.Auth.Hello, StringComparison.Ordinal)
                && response.Error is { Code: ErrorCode.Unauthorized or ErrorCode.Forbidden or ErrorCode.VersionMismatch })
            {
                // §3: mismatch → response flushed, then the connection is closed.
                connection.CloseReason = "auth.hello rejected: " + response.Error.Code;
                connection.Outbound.Writer.TryComplete();
            }
            else if (string.Equals(request.Name, IpcMessages.Auth.Hello, StringComparison.Ordinal) && !response.IsError)
            {
                // After the answer is queued, so the Shell has its hello before the state it then shows.
                await _bridge.PublishCurrentConnectivityAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Connection closing.
        }
        catch (ChannelClosedException)
        {
            // Connection closing.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Processing {Name} on {ConnectionId} failed", request.Name, connection.Id);
        }
        finally
        {
            if (gated)
            {
                connection.Gate.Release();
            }
        }
    }

    private IpcContext BuildContext(Connection connection)
    {
        User? user = _users.User;
        PlaySession? session = _sessions.Current;
        bool open = session is not null && _sessions.State.IsOpen();
        IpcAuthLevel auth = !connection.HelloDone ? IpcAuthLevel.None
            : user is null ? IpcAuthLevel.Hello
            : open ? IpcAuthLevel.Session
            : IpcAuthLevel.User;
        return new IpcContext(connection.Id, auth, user, open ? session : null, connection.Client.Pid, connection.Client.WtsSessionId, connection.Elevate);
    }

    private void SetHello(Connection connection, bool done)
    {
        connection.HelloDone = done;
        if (done)
        {
            connection.MarkPing(_clock.GetTimestamp());
        }

        UpdateShellConnected();
    }

    private void UpdateShellConnected() => _bridge.SetConnected(_connections.Values.Any(c => c.HelloDone));

    private void Close(Connection connection, string reason)
    {
        connection.CloseReason = reason;
        connection.Outbound.Writer.TryComplete();
        connection.Cancel();
    }

    // ---- broadcast / housekeeping -----------------------------------------------------------

    private async Task BroadcastLoopAsync(CancellationToken stoppingToken)
    {
        await foreach (IpcEnvelope envelope in _bridge.Outbox.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            bool delivered = false;
            foreach (Connection connection in _connections.Values)
            {
                if (!connection.HelloDone)
                {
                    continue;
                }

                if (connection.Outbound.Writer.TryWrite(envelope))
                {
                    delivered = true;
                    Interlocked.Increment(ref _eventsSent);
                }
                else
                {
                    Interlocked.Increment(ref _eventsDropped);
                    _logger.LogWarning("Event {Name} dropped for {ConnectionId}: outbound queue full", envelope.Name, connection.Id);
                }
            }

            if (!delivered)
            {
                Interlocked.Increment(ref _eventsDropped);
                _logger.LogDebug("Event {Name} dropped: no authenticated Shell connection", envelope.Name);
            }
        }
    }

    private async Task HousekeepingLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(HousekeepingInterval);
        long lastTick = _clock.GetTimestamp();
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            // D-99: every gap is measured on the monotonic clock: a wall-clock step (time sync, a DST or admin change)
            // made a live Shell look silent and killed it.
            long now = _clock.GetTimestamp();
            IpcSettings ipc = _settings.CurrentValue.Ipc;
            TimeSpan pingLimit = TimeSpan.FromSeconds(Math.Max(1, ipc.HeartbeatIntervalSec) * Math.Max(1, ipc.MissedHeartbeatsBeforeKill));
            TimeSpan sinceTick = _clock.Provider.GetElapsedTime(lastTick, now);
            if (ResumedFromSuspend(sinceTick))
            {
                // A free PC slept (or the machine stalled): nobody could ping while it was away, so the gap is not a hung
                // Shell. Start every connection's count afresh instead of dropping the Shell on wake.
                _logger.LogInformation("Resumed after {Gap:F0}s without a tick; heartbeat counts restart", sinceTick.TotalSeconds);
                foreach (Connection connection in _connections.Values)
                {
                    connection.MarkPing(now);
                }
            }

            lastTick = now;
            bool gameRunning = GameRunningSafe();
            foreach (Connection connection in _connections.Values)
            {
                if (connection.Cts.IsCancellationRequested)
                {
                    continue;
                }

                if (!connection.HelloDone)
                {
                    if (_clock.Provider.GetElapsedTime(connection.ConnectedTs, now) > HelloTimeout)
                    {
                        _logger.LogWarning("Pipe client {ConnectionId} pid {Pid} sent no auth.hello within {Timeout}s; closing", connection.Id, connection.Client.Pid, HelloTimeout.TotalSeconds);
                        Close(connection, "no auth.hello");
                    }

                    continue;
                }

                if (!connection.Client.IsKiosk)
                {
                    continue;
                }

                HeartbeatVerdict verdict = JudgeHeartbeat(_clock.Provider.GetElapsedTime(connection.LastPingTs, now), pingLimit, KillShellOnHeartbeatLoss, gameRunning);
                if (verdict == HeartbeatVerdict.Alive)
                {
                    continue;
                }

                int pid = connection.Client.Pid;
                Close(connection, "heartbeat lost");
                if (verdict == HeartbeatVerdict.DropAndKill)
                {
                    _logger.LogError("Shell pid {Pid} missed {Missed} heartbeats ({Limit}s); dropping the connection and terminating the Shell", pid, ipc.MissedHeartbeatsBeforeKill, pingLimit.TotalSeconds);
                    KillShell(pid, ShellStart(pid));
                }
                else if (KillShellOnHeartbeatLoss)
                {
                    // A game runs: the Shell is not killed under it. The connection is dropped so that a Shell that is only
                    // slow reconnects (and restores its game mode); one that stays silent is terminated once no game runs.
                    _logger.LogError("Shell pid {Pid} missed {Missed} heartbeats ({Limit}s) during a game; connection dropped, the Shell is left running until the game ends", pid, ipc.MissedHeartbeatsBeforeKill, pingLimit.TotalSeconds);
                    _silentShells[pid] = ShellStart(pid);
                }
                else
                {
                    _logger.LogError("Shell pid {Pid} missed {Missed} heartbeats ({Limit}s); dropping the connection", pid, ipc.MissedHeartbeatsBeforeKill, pingLimit.TotalSeconds);
                }
            }

            ReapSilentShells(gameRunning);
        }
    }

    /// <summary>A Shell left running during a game whose connection was dropped: back (it said hello again) or, once no game runs, terminated.</summary>
    private void ReapSilentShells(bool gameRunning)
    {
        foreach ((int pid, DateTime? started) in _silentShells)
        {
            if (_connections.Values.Any(c => c.HelloDone && c.Client.Pid == pid && !c.Cts.IsCancellationRequested))
            {
                _logger.LogInformation("Shell pid {Pid} reconnected after its heartbeat loss", pid);
                _silentShells.TryRemove(pid, out _);
            }
            else if (!gameRunning)
            {
                _logger.LogError("Shell pid {Pid} stayed silent after its heartbeat loss and no game runs any more; terminating it", pid);
                _silentShells.TryRemove(pid, out _);
                KillShell(pid, started);
            }
        }
    }

    private bool GameRunningSafe()
    {
        try
        {
            return IsGameRunning();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Running games not readable; the Shell is treated as in a game");
            return true;
        }
    }

    /// <summary>What a missing heartbeat leads to (D-99).</summary>
    public enum HeartbeatVerdict
    {
        /// <summary>Pinged within the limit.</summary>
        Alive,

        /// <summary>Drop the connection only: the Shell may reconnect.</summary>
        DropOnly,

        /// <summary>Drop the connection and terminate the Shell (the watchdog starts a new one).</summary>
        DropAndKill,
    }

    /// <summary>
    /// A kiosk Shell silent for longer than <paramref name="pingLimit"/> loses its connection; it is terminated too
    /// (<paramref name="killShell"/>) only while no game runs (D-99): a slow Shell reconnects and restores its game mode,
    /// and its process is not killed under a game.
    /// </summary>
    public static HeartbeatVerdict JudgeHeartbeat(TimeSpan sinceLastPing, TimeSpan pingLimit, bool killShell, bool gameRunning) =>
        sinceLastPing <= pingLimit ? HeartbeatVerdict.Alive
        : killShell && !gameRunning ? HeartbeatVerdict.DropAndKill
        : HeartbeatVerdict.DropOnly;

    /// <summary>
    /// Whether the housekeeping timer (every <see cref="HousekeepingInterval"/>) last ticked so long ago that the machine
    /// must have been suspended or stalled: a gap of more than <see cref="SuspendGap"/> on the monotonic clock.
    /// </summary>
    public static bool ResumedFromSuspend(TimeSpan sinceLastTick) => sinceLastTick > SuspendGap;

    /// <summary>A housekeeping gap longer than this is a sleep or a stall, not a Shell that stopped answering.</summary>
    public static readonly TimeSpan SuspendGap = TimeSpan.FromSeconds(10);

    private static DateTime? ShellStart(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Terminates the Shell's own process, never its tree: whatever it started (a game, a launcher) stays (D-99).
    /// <paramref name="started"/> guards against a pid the Shell's exit freed and another process took.
    /// </summary>
    private void KillShell(int pid, DateTime? started)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (started is { } expected && process.StartTime != expected)
            {
                _logger.LogInformation("Pid {Pid} is no longer the Shell; not terminated", pid);
                return;
            }

            process.Kill(entireProcessTree: false);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex, "Terminating the Shell (pid {Pid}) failed", pid);
        }
    }

    // ---- framing ----------------------------------------------------------------------------

    private byte[]? Encode(IpcEnvelope envelope)
    {
        byte[] json = JsonDefaults.SerializeToUtf8Bytes(envelope);
        int max = Math.Min(_settings.CurrentValue.Ipc.MaxMessageBytes, IpcEnvelope.MaxFrameBytes);
        if (json.Length > max)
        {
            if (envelope.Kind == IpcKind.Response)
            {
                _logger.LogError("Response {Name} ({Id}) is {Length} bytes, above the {Max} byte frame limit; replaced with internal error", envelope.Name, envelope.Id, json.Length, max);
                json = JsonDefaults.SerializeToUtf8Bytes(IpcEnvelope.Fail(envelope.Id, envelope.Name, IpcError.Internal(Guid.NewGuid().ToString("N")), _clock.UtcNow));
            }
            else
            {
                _logger.LogError("Event {Name} is {Length} bytes, above the {Max} byte frame limit; dropped", envelope.Name, json.Length, max);
                return null;
            }
        }

        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame, 4);
        return frame;
    }

    private static string ResponseNameOf(string? requestName) =>
        IpcEnvelope.IsValidName(requestName) ? IpcEnvelope.ResponseNameFor(requestName!) : "sys.protocolError";

    private static bool TrySalvageId(ReadOnlySpan<byte> json, out Guid id, out string? name)
    {
        id = Guid.Empty;
        name = null;
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (document.RootElement.TryGetProperty("name", out JsonElement nameElement) && nameElement.ValueKind == JsonValueKind.String)
            {
                name = nameElement.GetString();
            }

            return document.RootElement.TryGetProperty("id", out JsonElement idElement)
                && idElement.ValueKind == JsonValueKind.String
                && Guid.TryParse(idElement.GetString(), out id)
                && id != Guid.Empty;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private readonly record struct Frame(IpcEnvelope? Envelope, IpcEnvelope? Reply, bool Closed, string? Reason)
    {
        public static Frame Of(IpcEnvelope envelope) => new(envelope, null, false, null);

        public static Frame ReplyOnly(IpcEnvelope reply) => new(null, reply, false, null);

        public static Frame Close(string reason) => new(null, null, true, reason);
    }

    /// <summary>Token bucket: <c>ipc.requestsPerSecond</c> sustained, twice that as burst.</summary>
    private sealed class TokenBucket
    {
        private readonly double _rate;
        private readonly double _capacity;
        private readonly object _gate = new();
        private double _tokens;
        private DateTimeOffset _last;

        public TokenBucket(int ratePerSecond, DateTimeOffset now)
        {
            _rate = Math.Max(1, ratePerSecond);
            _capacity = _rate * 2;
            _tokens = _capacity;
            _last = now;
        }

        public bool TryTake(DateTimeOffset now)
        {
            lock (_gate)
            {
                double elapsed = Math.Max(0, (now - _last).TotalSeconds);
                _last = now;
                _tokens = Math.Min(_capacity, _tokens + (elapsed * _rate));
                if (_tokens < 1)
                {
                    return false;
                }

                _tokens -= 1;
                return true;
            }
        }
    }

    private sealed class Connection : IDisposable
    {
        private int _cancelled;

        public Connection(NamedPipeServerStream pipe, PipeClientInfo client, DateTimeOffset connectedAt, long connectedTs, IpcSettings ipc, int concurrency, CancellationToken stoppingToken)
        {
            Pipe = pipe;
            Client = client;
            ConnectedAt = connectedAt;
            ConnectedTs = connectedTs;
            LastPingTs = connectedTs;
            Cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            Gate = new SemaphoreSlim(Math.Max(1, concurrency), Math.Max(1, concurrency));
            Bucket = new TokenBucket(ipc.RequestsPerSecond, connectedAt);
            MaxMessageBytes = ipc.MaxMessageBytes > 0 ? ipc.MaxMessageBytes : IpcEnvelope.MaxFrameBytes;
            Outbound = Channel.CreateBounded<IpcEnvelope>(new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
            Elevate = static (_, _, _) => { };
        }

        public Guid Id { get; } = Guid.NewGuid();

        public NamedPipeServerStream Pipe { get; }

        public PipeClientInfo Client { get; }

        public DateTimeOffset ConnectedAt { get; }

        /// <summary>Monotonic timestamp of the connection (<see cref="TimeProvider.GetTimestamp"/>).</summary>
        public long ConnectedTs { get; }

        public CancellationTokenSource Cts { get; }

        public SemaphoreSlim Gate { get; }

        public TokenBucket Bucket { get; }

        public Channel<IpcEnvelope> Outbound { get; }

        public int MaxMessageBytes { get; }

        public byte[] Header { get; } = new byte[4];

        public Action<IpcAuthLevel, User?, PlaySession?> Elevate { get; set; }

        public volatile bool HelloDone;

        /// <summary>Monotonic timestamp of the last ping (or hello).</summary>
        public long LastPingTs { get; private set; }

        public long RequestCount { get; set; }

        public string? CloseReason { get; set; }

        public void MarkPing(long timestamp) => LastPingTs = timestamp;

        public void Cancel()
        {
            if (Interlocked.Exchange(ref _cancelled, 1) == 0)
            {
                try
                {
                    Cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Disposed concurrently.
                }
            }
        }

        public void Dispose()
        {
            Cancel();
            Pipe.Dispose();
            Cts.Dispose();
            Gate.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
