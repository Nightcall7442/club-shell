using System.Runtime.Versioning;
using ClubShell.Agent.AntiCheat;
using ClubShell.Agent.Games;
using ClubShell.Agent.Games.Accounts;
using ClubShell.Agent.Games.Launchers;
using ClubShell.Agent.Games.Saves;
using ClubShell.Agent.Ipc;
using ClubShell.Agent.Ipc.Handlers;
using ClubShell.Agent.Policy;
using ClubShell.Agent.Power;
using ClubShell.Agent.Remote;
using ClubShell.Agent.Server;
using ClubShell.Agent.Session;
using ClubShell.Agent.Storage;
using ClubShell.Agent.Updates;
using ClubShell.Agent.Users;
using ClubShell.Agent.Watchdog;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Realtime;
using ClubShell.Core.Security;
using ClubShell.Windows.Hardware;
using ClubShell.Windows.Network;
using ClubShell.Windows.Power;
using ClubShell.Windows.Processes;
using ClubShell.Windows.Registry;
using ClubShell.Windows.Sessions;
using ClubShell.Windows.Storage;
using ClubShell.Windows.Users;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent;

/// <summary>
/// Wires the entire Agent object graph (ARCHITECTURE.md §5.1). It registers the platform-neutral Core
/// (<see cref="CoreServiceCollectionExtensions.AddClubShellCore"/>), the Win32 layer, every Agent subsystem as a
/// singleton, all pluggable sets (<see cref="IGameLauncher"/>, <see cref="IPolicyModule"/>,
/// <see cref="IAntiCheatChecker"/>, <see cref="IIpcHandlerGroup"/>), the small adapters that bridge the
/// interfaces two subsystems declare independently, and the hosted services in start order (the
/// <see cref="AgentWorker"/> runs last so every dependency is already constructed).
/// </summary>
public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything the <c>ClubShellAgent</c> host needs. <paramref name="config"/> is the host
    /// configuration (<c>ClubShell:*</c> keys, e.g. <c>ClubShell:ConfigPath</c> / <c>ClubShell:Dev</c>); the effective
    /// Agent configuration itself is loaded from <c>agent.json</c> by <see cref="SettingsLoader"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddClubShellAgent(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        AddWindowsPlatform(services);
        services.AddClubShellCore(config);
        AddSessionSubsystem(services);
        AddGamesSubsystem(services);
        AddPolicySubsystem(services);
        AddPowerSubsystem(services);
        AddStorageSubsystem(services);
        AddAntiCheatSubsystem(services);
        AddUpdatesSubsystem(services);
        AddUsersAndWatchdog(services);
        AddServerSubsystem(services);
        AddIpcSubsystem(services);
        AddCrossSubsystemAdapters(services);
        AddHostedServices(services);
        return services;
    }

    // ---- Win32 layer -----------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static void AddWindowsPlatform(IServiceCollection services)
    {
        services.TryAddSingleton<WmiQueries>();
        services.TryAddSingleton<Disks>();
        services.TryAddSingleton(sp =>
        {
            AgentSettings settings = sp.GetRequiredService<SettingsLoader>().Current;
            var host = Uri.TryCreate(settings.Server.BaseUrl, UriKind.Absolute, out Uri? uri) ? uri.Host : null;
            int port = uri is { Port: > 0 } ? uri.Port : 443;
            return new NetworkProbe(host, port, sp.GetRequiredService<ILogger<NetworkProbe>>());
        });
        services.TryAddSingleton(sp => SensorHub.CreateDefault(
            sp.GetRequiredService<WmiQueries>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<SensorHub>>()));
        services.TryAddSingleton<PerformanceCounters>();

        // HardwareInventory replaces the Core BasicHardwareIdSource; registered before AddClubShellCore's TryAdd runs.
        services.TryAddSingleton<HardwareInventory>();
        services.TryAddSingleton<IHardwareIdSource>(sp => sp.GetRequiredService<HardwareInventory>());

        services.TryAddSingleton<ProcessLauncher>();
        services.TryAddSingleton<ProcessKiller>();
        services.TryAddSingleton<ProcessWatcher>();
        services.TryAddSingleton<SessionChangeWatcher>();
        services.TryAddSingleton<LocalUserManager>();
        services.TryAddSingleton<FolderRedirect>();
        services.TryAddSingleton<ProfileReset>();
        services.TryAddSingleton<ShellRegistry>();
        services.TryAddSingleton<DnsFilter>();
        services.TryAddSingleton<FirewallRules>();
        services.TryAddSingleton<PowerControl>();
        services.TryAddSingleton<IscsiInitiator>();
        services.TryAddSingleton<NetworkShare>();
    }

    // ---- Session ---------------------------------------------------------------------------

    private static void AddSessionSubsystem(IServiceCollection services)
    {
        services.TryAddSingleton<OfflineSessionStore>();
        services.TryAddSingleton<SessionTimer>();
        services.TryAddSingleton<SessionCleanup>();
        services.TryAddSingleton<SessionManager>();
        services.TryAddSingleton<ISessionService>(sp => sp.GetRequiredService<SessionManager>());
        services.TryAddSingleton<SessionLock>();
    }

    // ---- Games -----------------------------------------------------------------------------

    private static void AddGamesSubsystem(IServiceCollection services)
    {
        services.TryAddSingleton<GameDetector>();
        services.TryAddSingleton<GameLibrary>();
        services.TryAddSingleton<AccountPool>();
        services.TryAddSingleton<AccountInjector>();
        services.TryAddSingleton<CloudSaveSync>();
        services.TryAddSingleton<PlayerSettingsSync>();
        services.TryAddSingleton<GameSessionTracker>();
        services.TryAddSingleton<GameLaunchService>();

        services.AddSingleton<IGameLauncher, ExeLauncher>();
        services.AddSingleton<IGameLauncher, SteamLauncher>();
        services.AddSingleton<IGameLauncher, EpicLauncher>();
        services.AddSingleton<IGameLauncher, BattleNetLauncher>();
        services.AddSingleton<IGameLauncher, RiotLauncher>();
        services.AddSingleton<IGameLauncher, EaLauncher>();
        services.AddSingleton<IGameLauncher, UbisoftLauncher>();
    }

    // ---- Policy ----------------------------------------------------------------------------

    private static void AddPolicySubsystem(IServiceCollection services)
    {
        services.TryAddSingleton<PolicyStore>();
        services.TryAddSingleton<ProcessAllowlistModule>();

        // Registration order = apply order; PolicyEnforcer reverts in reverse.
        services.AddSingleton<IPolicyModule, ShellReplacementPolicyModule>();
        services.AddSingleton<IPolicyModule, ExplorerPolicyModule>();
        services.AddSingleton<IPolicyModule>(sp => sp.GetRequiredService<ProcessAllowlistModule>());
        services.AddSingleton<IPolicyModule, UsbPolicyModule>();
        services.AddSingleton<IPolicyModule, WebFilterPolicyModule>();

        services.TryAddSingleton<PolicyEnforcer>();
        services.TryAddSingleton<IPolicyEnforcer>(sp => sp.GetRequiredService<PolicyEnforcer>());
    }

    // ---- Power -----------------------------------------------------------------------------

    private static void AddPowerSubsystem(IServiceCollection services)
    {
        services.TryAddSingleton<PowerCommands>();
        services.TryAddSingleton<PowerScheduler>();
    }

    // ---- Storage ---------------------------------------------------------------------------

    private static void AddStorageSubsystem(IServiceCollection services)
    {
        services.TryAddSingleton<GamesShareMounter>();
        services.TryAddSingleton<LocalCache>();
    }

    // ---- Anti-cheat ------------------------------------------------------------------------

    private static void AddAntiCheatSubsystem(IServiceCollection services)
    {
        services.AddSingleton<IAntiCheatChecker, EacChecker>();
        services.AddSingleton<IAntiCheatChecker, FaceitChecker>();
        services.AddSingleton<IAntiCheatChecker, VanguardChecker>();
        services.AddSingleton<IAntiCheatChecker, SecureBootChecker>();

        services.TryAddSingleton<AntiCheatMonitor>();
        services.TryAddSingleton<ClubShell.Agent.AntiCheat.IAntiCheatGate>(sp => sp.GetRequiredService<AntiCheatMonitor>());
    }

    // ---- Updates ---------------------------------------------------------------------------

    private static void AddUpdatesSubsystem(IServiceCollection services)
    {
        services.TryAddSingleton<ShellUpdater>();
        services.TryAddSingleton<AgentUpdater>();
    }

    // ---- Users + watchdog ------------------------------------------------------------------

    private static void AddUsersAndWatchdog(IServiceCollection services)
    {
        services.TryAddSingleton<TempUserProvisioner>();
        services.TryAddSingleton<IKioskCredentials>(sp => sp.GetRequiredService<TempUserProvisioner>());
        services.TryAddSingleton<ProfileResetService>();
        services.TryAddSingleton<ClubShell.Agent.Users.IProfileResetTrigger>(sp => sp.GetRequiredService<ProfileResetService>());

        services.TryAddSingleton<CrashRecovery>();
        services.TryAddSingleton<ShellLauncher>();
        services.TryAddSingleton<IShellRelauncher>(sp => sp.GetRequiredService<ShellLauncher>());
        services.TryAddSingleton<IKioskSessionLocator>(sp => sp.GetRequiredService<ShellLauncher>());
        services.TryAddSingleton<IKioskProfilePaths>(sp => sp.GetRequiredService<ShellLauncher>());
        services.TryAddSingleton<ShellWatchdog>();
    }

    // ---- Server + remote -------------------------------------------------------------------

    private static void AddServerSubsystem(IServiceCollection services)
    {
        services.TryAddSingleton<TelemetryBus>();
        services.TryAddSingleton<AdminMessageService>();
        services.TryAddSingleton<ScreenCapture>();
        services.TryAddSingleton<RemoteInputService>();

        services.TryAddSingleton<ServerConnection>();
        services.TryAddSingleton<CommandReceiver>();
        services.TryAddSingleton<IServerCommandSink>(sp => sp.GetRequiredService<CommandReceiver>());
        services.TryAddSingleton<IPolicyRefresh>(sp => sp.GetRequiredService<CommandReceiver>());
        services.TryAddSingleton<IConfigRefresh>(sp => sp.GetRequiredService<CommandReceiver>());
    }

    // ---- IPC -------------------------------------------------------------------------------

    private static void AddIpcSubsystem(IServiceCollection services)
    {
        services.TryAddSingleton<ShellUserContext>();
        services.TryAddSingleton<ShellSettingsStore>();
        services.TryAddSingleton<ShellTokenStore>();

        services.TryAddSingleton<IpcEventBridge>();
        services.TryAddSingleton<IIpcEventPublisher>(sp => sp.GetRequiredService<IpcEventBridge>());

        services.AddSingleton<IIpcHandlerGroup, SessionHandlers>();
        services.AddSingleton<IIpcHandlerGroup, UserDomainHandlers>();
        services.AddSingleton<IIpcHandlerGroup, GameHandlers>();
        services.AddSingleton<IIpcHandlerGroup, PolicyHandlers>();
        services.AddSingleton<IIpcHandlerGroup, SystemHandlers>();

        services.TryAddSingleton<MessageDispatcher>();
        services.TryAddSingleton<PipeServer>();
    }

    // ---- Adapters between the interfaces two subsystems declare independently ---------------

    private static void AddCrossSubsystemAdapters(IServiceCollection services)
    {
        // Event sinks / publishers → the pipe bridge (a dependency leaf, so no DI cycle).
        services.TryAddSingleton<ISessionEventSink>(sp => sp.GetRequiredService<IpcEventBridge>());
        services.TryAddSingleton<ClubShell.Agent.Games.IGameEventSink>(sp => sp.GetRequiredService<IpcEventBridge>());
        services.TryAddSingleton<IPolicyEventSink>(sp => sp.GetRequiredService<IpcEventBridge>());
        services.TryAddSingleton<IPowerNotifier>(sp => sp.GetRequiredService<IpcEventBridge>());
        services.TryAddSingleton<IUpdateEventSink>(sp => sp.GetRequiredService<IpcEventBridge>());
        services.TryAddSingleton<IConnectivityEventSink>(sp => sp.GetRequiredService<IpcEventBridge>());
        services.TryAddSingleton<IServerPushSink>(sp => sp.GetRequiredService<IpcEventBridge>());
        services.TryAddSingleton<ISysMetricsSink>(sp => sp.GetRequiredService<IpcEventBridge>());
        services.TryAddSingleton<IAdminEventSink>(sp => sp.GetRequiredService<IpcEventBridge>());
        services.TryAddSingleton<IShellCaptureRequester>(sp => sp.GetRequiredService<IpcEventBridge>());

        // IShellConnectionState is bound to the bridge, not PipeServer: PipeServer transitively depends on the IPC
        // handlers, which depend (via AdminMessageService) on IShellConnectionState — binding it to PipeServer would
        // form a construction cycle. The bridge exposes the same flag (PipeServer.SetConnected sets it).
        services.TryAddSingleton<IShellConnectionState>(sp => sp.GetRequiredService<IpcEventBridge>());

        // shell.command sender → the bridge's event publisher.
        services.TryAddSingleton<IShellCommandSender, ShellCommandSender>();

        // setVolume server command → Core Audio + shell.json, the same path as the sys.setVolume IPC request. Bound
        // through ShellSettingsStore (a leaf) rather than SystemHandlers, which would close a construction cycle via
        // ServerConnection → IServerCommandSink(CommandReceiver).
        services.TryAddSingleton<IVolumeController, ShellVolumeController>();

        // Running games for the heartbeat.
        services.TryAddSingleton<IRunningGamesSource, RunningGamesSourceAdapter>();

        // Offline outbox depth for the heartbeat / connectivity events.
        services.TryAddSingleton<IOfflineQueueDepth, OfflineQueueDepthAdapter>();

        // Agent → server events (WebSocket).
        services.TryAddSingleton<IAgentEventSink, AgentEventPublisher>();

        // Session-end teardown: kill games + release leases. Resolved lazily to break the
        // SessionManager → SessionCleanup → GameLaunchService → ISessionService(SessionManager) cycle.
        services.TryAddSingleton<ClubShell.Agent.Session.IGameSessionCleanup, SessionGameCleanupAdapter>();

        // Anti-cheat launch gate for the Games module (IReadOnlyList → array).
        services.TryAddSingleton<ClubShell.Agent.Games.IAntiCheatGate, AntiCheatGateAdapter>();

        // Runtime anti-cheat game control (kill on violation). Lazy to break the
        // AntiCheatMonitor → GameLaunchService → IAntiCheatGate(AntiCheatMonitor) cycle.
        services.TryAddSingleton<IAntiCheatGameControl, AntiCheatGameControlAdapter>();

        // Processes never killed by the allowlist: the Agent itself, the Shell, and running games.
        services.TryAddSingleton<IProtectedProcesses, ProtectedProcessesAdapter>();

        // A single idle detector serving both the session (auto-lock) and power (idle shutdown) subsystems.
        services.TryAddSingleton<IdleMonitorAdapter>();
        services.TryAddSingleton<IIdleMonitor>(sp => sp.GetRequiredService<IdleMonitorAdapter>());
        services.TryAddSingleton<IIdleSignal>(sp => sp.GetRequiredService<IdleMonitorAdapter>());
    }

    // ---- Hosted services (start order; host stops them in reverse) -------------------------

    private static void AddHostedServices(IServiceCollection services)
    {
        // Kiosk account first: PipeServer.StartAsync writes the Shell token ACL'd to the kiosk SID and the pipe DACL
        // grants that SID; without it a Shell launched by the watchdog could never connect. Hosted services start
        // sequentially in this order (ServicesStartConcurrently is off), so StartAsync completes before the next starts.
        services.AddHostedService(sp => sp.GetRequiredService<TempUserProvisioner>());
        services.AddHostedService(sp => sp.GetRequiredService<PipeServer>());
        services.AddHostedService(sp => sp.GetRequiredService<ServerConnection>());
        services.AddHostedService<HeartbeatService>();
        services.AddHostedService<TelemetryReporter>();
        services.AddHostedService(sp => sp.GetRequiredService<PowerScheduler>());
        services.AddHostedService(sp => sp.GetRequiredService<GamesShareMounter>());
        services.AddHostedService(sp => sp.GetRequiredService<AntiCheatMonitor>());
        services.AddHostedService(sp => sp.GetRequiredService<AgentUpdater>());
        services.AddHostedService(sp => sp.GetRequiredService<ShellWatchdog>());
        // Nothing depends on ProfileResetService; hosting it is what constructs it and subscribes the post-session reset.
        services.AddHostedService(sp => sp.GetRequiredService<ProfileResetService>());
        services.AddHostedService<AgentWorker>();
    }
}

// ============================================================================================
// Adapters. Each bridges an interface one subsystem declares to an implementation another owns.
// ============================================================================================

/// <summary>Publishes a <see cref="ShellCommand"/> to the connected Shell as the <c>shell.command</c> IPC event.</summary>
internal sealed class ShellCommandSender : IShellCommandSender
{
    private readonly IIpcEventPublisher _publisher;

    public ShellCommandSender(IIpcEventPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        _publisher = publisher;
    }

    public ValueTask SendAsync(ShellCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _publisher.PublishAsync(IpcMessages.Events.ShellCommand, command, cancellationToken);
    }
}

/// <summary>
/// Applies the <c>setVolume</c> server command (<see cref="IVolumeController"/>): best-effort Core Audio in session 0,
/// always persisted to <c>shell.json</c> so the Shell applies it in the user session (IPC_PROTOCOL.md §7.12).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ShellVolumeController : IVolumeController
{
    private readonly ShellSettingsStore _settings;
    private readonly ILogger<ShellVolumeController> _logger;

    public ShellVolumeController(ShellSettingsStore settings, ILogger<ShellVolumeController> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
    }

    public ValueTask<VolumeState> SetAsync(SetVolumeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        int level = Math.Clamp(request.Level, 0, 100);
        bool muted = request.Muted ?? _settings.Get().Muted;
        if (!CoreAudioVolume.TryApply(level, muted, _logger))
        {
            _logger.LogDebug("Core Audio endpoint unavailable from the service; volume {Level} (muted {Muted}) persisted only", level, muted);
        }

        return ValueTask.FromResult(_settings.SetVolume(level, muted));
    }
}

/// <summary>Adapts <see cref="GameLaunchService.HeartbeatGames"/> to <see cref="IRunningGamesSource"/>.</summary>
internal sealed class RunningGamesSourceAdapter : IRunningGamesSource
{
    private readonly GameLaunchService _games;

    public RunningGamesSourceAdapter(GameLaunchService games)
    {
        ArgumentNullException.ThrowIfNull(games);
        _games = games;
    }

    public IReadOnlyList<HeartbeatRunningGame> Snapshot() => _games.HeartbeatGames();
}

/// <summary>
/// Exposes the offline outbox depth synchronously (<see cref="IOfflineQueueDepth"/>). The store only offers an async
/// query, so the count is cached and refreshed in the background when stale; callers (heartbeat, connectivity events)
/// tolerate a slightly stale value.
/// </summary>
internal sealed class OfflineQueueDepthAdapter : IOfflineQueueDepth
{
    // ponytail: telemetry-grade counter, refreshed lazily on read; a 2 s TTL is fine for a value only used in the
    // heartbeat/connectivity payloads. Swap for an event-driven counter if it ever needs to be exact.
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(2);

    private readonly OfflineSessionStore _store;
    private readonly IClock _clock;
    private readonly ILogger<OfflineQueueDepthAdapter> _logger;
    private int _cached;
    private long _refreshedTicks;
    private int _refreshing;

    public OfflineQueueDepthAdapter(OfflineSessionStore store, IClock clock, ILogger<OfflineQueueDepthAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _clock = clock;
        _logger = logger;
        _refreshedTicks = clock.UtcNow.UtcTicks - Ttl.Ticks;
    }

    public int Count
    {
        get
        {
            long last = Interlocked.Read(ref _refreshedTicks);
            if (_clock.UtcNow.UtcTicks - last >= Ttl.Ticks && Interlocked.Exchange(ref _refreshing, 1) == 0)
            {
                _ = RefreshAsync();
            }

            return Volatile.Read(ref _cached);
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            OfflineQueueStats stats = await _store.GetQueueStatsAsync(CancellationToken.None).ConfigureAwait(false);
            Volatile.Write(ref _cached, stats.Pending);
            Interlocked.Exchange(ref _refreshedTicks, _clock.UtcNow.UtcTicks);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Offline queue depth refresh failed");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }
}

/// <summary>Forwards Agent → server events (<see cref="AgentEvent"/>) over the WebSocket; drops them while offline.</summary>
internal sealed class AgentEventPublisher : IAgentEventSink
{
    private readonly RealtimeClient _realtime;
    private readonly ILogger<AgentEventPublisher> _logger;

    public AgentEventPublisher(RealtimeClient realtime, ILogger<AgentEventPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(realtime);
        ArgumentNullException.ThrowIfNull(logger);
        _realtime = realtime;
        _logger = logger;
    }

    public ValueTask PublishAsync(AgentEvent agentEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_realtime.TrySendEvent(agentEvent))
        {
            _logger.LogDebug("Agent event {Type} dropped (WebSocket not connected or queue full)", agentEvent.Type);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Session-end teardown for <see cref="SessionCleanup"/>: kills every running game and releases the account-pool
/// leases. Dependencies are resolved lazily because <see cref="GameLaunchService"/> transitively depends on the
/// session service that owns the cleanup.
/// </summary>
internal sealed class SessionGameCleanupAdapter : ClubShell.Agent.Session.IGameSessionCleanup
{
    private readonly IServiceProvider _services;

    public SessionGameCleanupAdapter(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public Task KillAllAsync(ClubShell.Contracts.Sessions.SessionEndReason reason, CancellationToken cancellationToken) =>
        _services.GetRequiredService<GameLaunchService>().KillAllAsync(reason, cancellationToken);

    public Task ReleaseLeasesAsync(ClubShell.Contracts.Sessions.SessionEndReason reason, CancellationToken cancellationToken) =>
        _services.GetRequiredService<AccountPool>().ReleaseAllAsync(AccountLeaseReleaseReason.SessionEnd, cancellationToken);
}

/// <summary>Adapts the anti-cheat monitor (<see cref="ClubShell.Agent.AntiCheat.IAntiCheatGate"/>) to the array-returning gate the Games module expects.</summary>
internal sealed class AntiCheatGateAdapter : ClubShell.Agent.Games.IAntiCheatGate
{
    private readonly ClubShell.Agent.AntiCheat.IAntiCheatGate _monitor;

    public AntiCheatGateAdapter(ClubShell.Agent.AntiCheat.IAntiCheatGate monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _monitor = monitor;
    }

    public async Task<AntiCheatCheckResult[]> CheckForLaunchAsync(Game game, CancellationToken cancellationToken)
    {
        IReadOnlyList<AntiCheatCheckResult> results = await _monitor.CheckForLaunchAsync(game, cancellationToken).ConfigureAwait(false);
        return results as AntiCheatCheckResult[] ?? results.ToArray();
    }
}

/// <summary>
/// Lets the anti-cheat monitor enumerate and kill running games at runtime (<see cref="IAntiCheatGameControl"/>).
/// Resolved lazily to break the monitor → <see cref="GameLaunchService"/> → gate → monitor cycle.
/// </summary>
internal sealed class AntiCheatGameControlAdapter : IAntiCheatGameControl
{
    private readonly IServiceProvider _services;

    public AntiCheatGameControlAdapter(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public IReadOnlyList<RunningGame> Running => _services.GetRequiredService<GameLaunchService>().Running().Items;

    public Task KillAsync(int pid, bool force, CancellationToken cancellationToken) =>
        _services.GetRequiredService<GameLaunchService>().KillAsync(new GamesKillRequest(Pid: pid, Force: force), cancellationToken);
}

/// <summary>
/// Processes the allowlist enforcement must never touch: the Agent process, the kiosk Shell, and every running game.
/// Dependencies are held directly (neither depends on the allowlist module, so there is no cycle).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ProtectedProcessesAdapter : IProtectedProcesses
{
    private readonly ShellLauncher _launcher;
    private readonly GameSessionTracker _games;

    public ProtectedProcessesAdapter(ShellLauncher launcher, GameSessionTracker games)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(games);
        _launcher = launcher;
        _games = games;
    }

    public bool IsProtected(int pid) =>
        pid == Environment.ProcessId
        || pid == _launcher.Current?.Pid
        || _games.Find(pid) is not null;
}

/// <summary>
/// Single idle detector shared by the session auto-lock (<see cref="IIdleSignal"/>) and the power scheduler
/// (<see cref="IIdleMonitor"/>). The threshold tracks <c>session.autoLockOnIdleSec</c> (the auto-lock crossing); the
/// power scheduler reads <see cref="Idle"/> directly for its own <c>idleShutdownMin</c> threshold.
/// </summary>
/// <remarks>
/// The Agent runs in session 0, so <c>GetLastInputInfo</c> cannot observe the interactive user's input directly; this
/// is a known limitation of a session-0 detector and can be superseded by the Shell's <c>kiosk://idle</c> signal.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class IdleMonitorAdapter : IIdleMonitor, IIdleSignal, IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan DefaultThreshold = TimeSpan.FromMinutes(5);

    private readonly ClubShell.Windows.Input.IdleDetector _detector;
    private readonly IDisposable? _settingsSubscription;
    private int _disposed;

    public IdleMonitorAdapter(IOptionsMonitor<AgentSettings> settings, ILogger<IdleMonitorAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _detector = new ClubShell.Windows.Input.IdleDetector(ThresholdFor(settings.CurrentValue), logger: logger);
        _detector.IdleStateChanged += (_, idle) => IdleChanged?.Invoke(this, idle);
        _settingsSubscription = settings.OnChange((updated, _) => _detector.IdleThreshold = ThresholdFor(updated));
        _detector.Start();
    }

    public event EventHandler<bool>? IdleChanged;

    public bool IsIdle => _detector.IsIdle;

    public TimeSpan Idle => _detector.Idle;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _settingsSubscription?.Dispose();
        await _detector.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _settingsSubscription?.Dispose();
        _detector.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private static TimeSpan ThresholdFor(AgentSettings settings)
    {
        int seconds = settings.Session.AutoLockOnIdleSec;
        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : DefaultThreshold;
    }
}
