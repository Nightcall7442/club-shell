using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Http;
using ClubShell.Core.Realtime;
using ClubShell.Core.Security;
using ClubShell.Windows.Hardware;
using ClubShell.Windows.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Server;

/// <summary>
/// Receives normalized <see cref="ServerCommand"/>s (from the WebSocket or the REST fallback poll) and dispatches
/// them. Implemented by the command receiver; declared here so <see cref="ServerConnection"/> and
/// <see cref="HeartbeatService"/> can route commands without a compile-time dependency on it.
/// </summary>
public interface IServerCommandSink
{
    /// <summary>Handles one command and returns the ack sent back to the server.</summary>
    Task<CommandAck> HandleAsync(ServerCommand command, CancellationToken cancellationToken);
}

/// <summary>Delivers server pushes (<see cref="WsPushKind"/>) to the Shell over IPC.</summary>
public interface IServerPushSink
{
    /// <summary>Publishes a decoded push; <paramref name="payload"/> is the raw frame body.</summary>
    ValueTask PublishAsync(WsPushKind kind, System.Text.Json.JsonElement? payload, CancellationToken cancellationToken);
}

/// <summary>Forwards connectivity transitions to the Shell as <c>sys.connectivity</c> events.</summary>
public interface IConnectivityEventSink
{
    /// <summary>Publishes a connectivity transition.</summary>
    ValueTask PublishAsync(ConnectivityEvent connectivity, CancellationToken cancellationToken);
}

/// <summary>
/// Owns the Agent's relationship with the central server (SERVER_API.md §2, §6): registers the PC (or reuses valid
/// tokens), keeps the access token fresh, and runs the <see cref="RealtimeClient"/> WebSocket under a
/// <see cref="Reconnector"/>. Commands are routed to <see cref="IServerCommandSink"/>, pushes to
/// <see cref="IServerPushSink"/>, and connectivity transitions to <see cref="IConnectivityEventSink"/>. Registered as
/// a singleton and as an <see cref="IHostedService"/>; other services take it to read <see cref="PcId"/>,
/// <see cref="IsOnline"/> and <see cref="ServerTimeOffset"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServerConnection : IHostedService, IDisposable
{
    private const string IdentityFileName = "agent-identity.json";
    private static readonly TimeSpan TokenRefreshLeeway = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RegisterRetryMin = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RegisterRetryMax = TimeSpan.FromSeconds(60);

    private readonly IServerClient _server;
    private readonly ITokenStore _tokens;
    private readonly RealtimeClient _realtime;
    private readonly HardwareInventory _hardware;
    private readonly Hwid _hwid;
    private readonly NetworkProbe _network;
    private readonly IClock _clock;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IServerCommandSink _commandSink;
    private readonly IServerPushSink _pushSink;
    private readonly IConnectivityEventSink _connectivitySink;
    private readonly IOfflineQueueDepth _queueDepth;
    private readonly ILogger<ServerConnection> _logger;
    private readonly Reconnector _reconnector;

    private CancellationTokenSource? _cts;
    private Task? _runner;
    private Guid? _pcId;
    private ConnectivityState _connectivity = ConnectivityState.Offline;
    private DateTimeOffset _connectivitySince;
    private int _disposed;

    /// <summary>Creates the connection. All dependencies are singletons.</summary>
    public ServerConnection(
        IServerClient server,
        ITokenStore tokens,
        RealtimeClient realtime,
        HardwareInventory hardware,
        Hwid hwid,
        NetworkProbe network,
        IClock clock,
        IOptionsMonitor<AgentSettings> settings,
        IServerCommandSink commandSink,
        IServerPushSink pushSink,
        IConnectivityEventSink connectivitySink,
        IOfflineQueueDepth queueDepth,
        ILogger<ServerConnection> logger,
        ILogger<Reconnector> reconnectorLogger)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(realtime);
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(hwid);
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(commandSink);
        ArgumentNullException.ThrowIfNull(pushSink);
        ArgumentNullException.ThrowIfNull(connectivitySink);
        ArgumentNullException.ThrowIfNull(queueDepth);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(reconnectorLogger);

        _server = server;
        _tokens = tokens;
        _realtime = realtime;
        _hardware = hardware;
        _hwid = hwid;
        _network = network;
        _clock = clock;
        _settings = settings;
        _commandSink = commandSink;
        _pushSink = pushSink;
        _connectivitySink = connectivitySink;
        _queueDepth = queueDepth;
        _logger = logger;
        _reconnector = new Reconnector(clock, reconnectorLogger);
        _connectivitySince = clock.UtcNow;
    }

    /// <summary>Raised on every connectivity transition (online/offline).</summary>
    public event EventHandler<ConnectivityState>? StateChanged;

    /// <summary>Current connectivity state, driven by the WebSocket connection.</summary>
    public ConnectivityState Connectivity => _connectivity;

    /// <summary><see langword="true"/> while the WebSocket is connected.</summary>
    public bool IsOnline => _realtime.IsConnected;

    /// <summary>Assigned PC id once registered (or loaded from the identity file); <see langword="null"/> before then.</summary>
    public Guid? PcId => _pcId ?? _tokens.Agent?.PcId;

    /// <summary>Estimated server-clock offset (positive when the server clock is ahead).</summary>
    public TimeSpan ServerTimeOffset => _server.ServerTimeOffset;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _pcId = LoadPersistedPcId();

        _reconnector.StateChanged += OnReconnectorStateChanged;
        _realtime.ConnectivityChanged += OnRealtimeConnectivityChanged;
        _realtime.PushReceived += OnPushReceived;
        _realtime.CommandHandler = (command, ct) => _commandSink.HandleAsync(command, ct);

        // The registration + connect loop runs in the background so the host starts promptly.
        _runner = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _reconnector.StateChanged -= OnReconnectorStateChanged;
        _realtime.ConnectivityChanged -= OnRealtimeConnectivityChanged;
        _realtime.PushReceived -= OnPushReceived;
        _realtime.CommandHandler = null;

        if (_cts is { } cts)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        if (_runner is { } runner)
        {
            try
            {
                await runner.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stop timed out or was cancelled; the runner observes its own token.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Server connection runner faulted during shutdown");
            }
        }

        SetConnectivity(ConnectivityState.Offline);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _cts?.Dispose();
        _reconnector.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _tokens.LoadAsync(cancellationToken).ConfigureAwait(false);
            await EnsureRegisteredAsync(cancellationToken).ConfigureAwait(false);
            await _reconnector.RunAsync(ConnectAndRunAsync, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Server connection loop stopped unexpectedly");
        }
    }

    private async Task EnsureRegisteredAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_tokens.Agent is { } existing && !existing.IsExpired(_clock.UtcNow, TokenRefreshLeeway))
            {
                _pcId = existing.PcId;
                await PersistPcIdAsync(existing.PcId, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Reusing stored agent tokens for PC {PcId}", existing.PcId);
                return;
            }

            try
            {
                await RegisterOnceAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                attempt++;
                var delay = Reconnector.ComputeDelay(attempt, RegisterRetryMin, RegisterRetryMax, 2.0);
                _logger.LogWarning(ex, "Registration attempt {Attempt} failed; retrying in {Delay}", attempt, delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RegisterOnceAsync(CancellationToken cancellationToken)
    {
        var hardware = await _hardware.GetAsync(cancellationToken).ConfigureAwait(false);
        var hwid = await _hwid.GetAsync(cancellationToken).ConfigureAwait(false);
        var net = _network.GetInfo();
        var request = new AgentRegisterRequest(
            hwid,
            Environment.MachineName,
            ClubShellVersion.Current,
            hardware,
            net.Ip,
            net.Mac,
            _pcId);

        var response = await _server.RegisterAsync(request, cancellationToken).ConfigureAwait(false);
        _pcId = response.PcId;
        await PersistPcIdAsync(response.PcId, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Registered as PC {PcId} ({PcName}, zone {Zone})", response.PcId, response.Pc.Name, response.Pc.Zone);
    }

    private async Task ConnectAndRunAsync(CancellationToken cancellationToken)
    {
        await EnsureFreshTokenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _realtime.RunAsync(_reconnector.MarkConnected, cancellationToken).ConfigureAwait(false);
        }
        catch (ServerApiException ex) when (ex.IsAuthFailure)
        {
            _logger.LogWarning("WebSocket rejected the token; refreshing before the next attempt");
            await RefreshOrReregisterAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsureFreshTokenAsync(CancellationToken cancellationToken)
    {
        if (_tokens.Agent is not { } tokens)
        {
            await EnsureRegisteredAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (tokens.IsExpired(_clock.UtcNow, TokenRefreshLeeway))
        {
            await RefreshOrReregisterAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshOrReregisterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _server.RefreshAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Access token refreshed");
        }
        catch (ServerApiException ex) when (ex.IsAuthFailure)
        {
            _logger.LogWarning(ex, "Token refresh rejected; re-registering");
            await RegisterOnceAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnRealtimeConnectivityChanged(object? sender, ConnectivityState state) => SetConnectivity(state);

    private void OnReconnectorStateChanged(object? sender, ConnectionState state)
    {
        // The Reconnector reports backoff/disconnect independently of the socket; treat anything but Connected as offline
        // only when the socket itself is not connected, so a brief handoff does not flap the Shell overlay.
        if (state != ConnectionState.Connected && !_realtime.IsConnected)
        {
            SetConnectivity(ConnectivityState.Offline);
        }
    }

    private void SetConnectivity(ConnectivityState state)
    {
        if (_connectivity == state)
        {
            return;
        }

        _connectivity = state;
        _connectivitySince = _clock.UtcNow;
        _logger.LogInformation("Server connectivity: {State}", state);

        try
        {
            StateChanged?.Invoke(this, state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StateChanged listener threw");
        }

        var evt = new ConnectivityEvent(state, _connectivitySince, _queueDepth.Count);
        _ = PublishConnectivityAsync(evt);
    }

    private async Task PublishConnectivityAsync(ConnectivityEvent evt)
    {
        try
        {
            await _connectivitySink.PublishAsync(evt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Publishing connectivity event to the Shell failed");
        }
    }

    private void OnPushReceived(object? sender, WsFrame frame)
    {
        if (!frame.TryGetPushKind(out var kind))
        {
            _logger.LogDebug("Ignoring push with unknown kind {Name}", frame.Name);
            return;
        }

        _ = PublishPushAsync(kind, frame.Payload);
    }

    private async Task PublishPushAsync(WsPushKind kind, System.Text.Json.JsonElement? payload)
    {
        try
        {
            await _pushSink.PublishAsync(kind, payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Publishing push {Kind} to the Shell failed", kind);
        }
    }

    private string IdentityPath() => _settings.CurrentValue.ResolvePath(IdentityFileName);

    private Guid? LoadPersistedPcId()
    {
        var path = IdentityPath();
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var node = JsonNode.Parse(File.ReadAllText(path));
            var value = node?["pcId"]?.GetValue<string>();
            return Guid.TryParse(value, out var pcId) ? pcId : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not read the agent identity file at {Path}", path);
            return null;
        }
    }

    private async Task PersistPcIdAsync(Guid pcId, CancellationToken cancellationToken)
    {
        var path = IdentityPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var node = new JsonObject
            {
                ["pcId"] = pcId.ToString("D"),
                ["updatedAt"] = _clock.UtcNow.ToString("O"),
            };
            await File.WriteAllTextAsync(path, node.ToJsonString(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not persist the agent identity file at {Path}", path);
        }
    }
}
