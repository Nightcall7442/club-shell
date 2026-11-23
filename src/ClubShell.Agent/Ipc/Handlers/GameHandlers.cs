using System.ComponentModel;
using System.Runtime.Versioning;
using ClubShell.Agent.Games;
using ClubShell.Agent.Policy;
using ClubShell.Agent.Saves;
using ClubShell.Agent.Server;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Processes;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Ipc.Handlers;

/// <summary>
/// <c>games.*</c> and <c>apps.*</c> requests (IPC_PROTOCOL.md §7.3, §7.4). Games go through <see cref="GameLibrary"/>
/// and <see cref="GameLaunchService"/>, their art and trailers from <c>cache\media</c> once cached
/// (<see cref="GameMediaCache"/>); the apps catalogue (<c>GET /apps</c>) is cached in <c>cache\apps.json</c> and
/// filtered by the process allow-list, and apps are started in the kiosk session through <see cref="ProcessLauncher"/>.
/// <see cref="RunningApps"/> tells which apps run (<c>apps.running</c>), what <c>apps.kill</c> ends, and makes a second
/// <c>apps.launch</c> of a running app answer <c>alreadyRunning</c> instead of opening another copy.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GameHandlers : IIpcHandlerGroup
{
    private readonly GameLibrary _library;
    private readonly GameMediaCache _media;
    private readonly GameLaunchService _launch;
    private readonly IServerClient _server;
    private readonly ProcessLauncher _processes;
    private readonly IKioskSessionLocator _kiosk;
    private readonly ServerConnection _connection;
    private readonly ShellSettingsStore _shellSettings;
    private readonly ProcessAllowlistModule? _allowlist;
    private readonly IPlayerSaves? _saves;
    private readonly ILogger<GameHandlers> _logger;
    private readonly ContractFileCache<AppsListResponse> _apps;
    private readonly RunningApps _running;
    private readonly IAppProcessSource _sessionProcesses;
    private readonly ProcessKiller _killer;

    /// <summary>
    /// Creates the group. <paramref name="allowlist"/> is optional; without it only <see cref="App.Allowed"/> gates app
    /// launches. With <paramref name="saves"/> a game launch first waits for the player's saves (SHELL_CHANGES 23).
    /// </summary>
    public GameHandlers(
        GameLibrary library,
        GameMediaCache media,
        GameLaunchService launch,
        IServerClient server,
        ProcessLauncher processes,
        IKioskSessionLocator kiosk,
        ServerConnection connection,
        ShellSettingsStore shellSettings,
        IOptionsMonitor<AgentSettings> settings,
        ILogger<GameHandlers> logger,
        RunningApps runningApps,
        IAppProcessSource sessionProcesses,
        ProcessKiller killer,
        ProcessAllowlistModule? allowlist = null,
        IPlayerSaves? saves = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(kiosk);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(shellSettings);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(runningApps);
        ArgumentNullException.ThrowIfNull(sessionProcesses);
        ArgumentNullException.ThrowIfNull(killer);
        _running = runningApps;
        _sessionProcesses = sessionProcesses;
        _killer = killer;
        _library = library;
        _media = media;
        _launch = launch;
        _server = server;
        _processes = processes;
        _kiosk = kiosk;
        _connection = connection;
        _shellSettings = shellSettings;
        _allowlist = allowlist;
        _saves = saves;
        _logger = logger;
        _apps = new ContractFileCache<AppsListResponse>(Path.Combine(settings.CurrentValue.CacheDir, "apps.json"), logger);
    }

    /// <inheritdoc />
    public void Register(MessageDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        dispatcher.RegisterOptional<GamesListRequest, GamesListResponse>(IpcMessages.Games.List, ListAsync);
        dispatcher.Register<GamesGetRequest, Game>(IpcMessages.Games.Get, GetAsync);
        dispatcher.Register<GamesLaunchRequest, LaunchResult>(IpcMessages.Games.Launch, LaunchAsync);
        dispatcher.RegisterOptional<GamesKillRequest, GamesKillResponse>(IpcMessages.Games.Kill, (_, request, cancellationToken) => _launch.KillAsync(request ?? new GamesKillRequest(), cancellationToken));
        dispatcher.RegisterNoPayload<GamesRunningResponse>(IpcMessages.Games.Running, (_, _) => Task.FromResult(_launch.Running()));
        dispatcher.Register<GamesInstallStatusRequest, GameInstallStatus>(IpcMessages.Games.InstallStatus, (_, request, cancellationToken) => _library.GetInstallStatusAsync(request.GameId, cancellationToken));

        dispatcher.RegisterNoPayload<AppsListResponse>(IpcMessages.Apps.List, ListAppsAsync);
        dispatcher.Register<AppsLaunchRequest, AppsLaunchResponse>(IpcMessages.Apps.Launch, LaunchAppAsync);
        dispatcher.RegisterNoPayload<AppsRunningResponse>(IpcMessages.Apps.Running, (_, _) => Task.FromResult(new AppsRunningResponse(RunningAppsNow())));
        dispatcher.Register<AppsKillRequest, GamesKillResponse>(IpcMessages.Apps.Kill, KillAppAsync);
    }

    /// <summary>Apps catalogue with <see cref="App.Allowed"/> combined with the process allow-list; cached copy when offline.</summary>
    public async Task<AppsListResponse> ListAppsAsync(IpcContext context, CancellationToken cancellationToken)
    {
        if (!_shellSettings.Features.Apps)
        {
            return new AppsListResponse(Array.Empty<App>());
        }

        AppsListResponse? cached = _apps.Get();
        AppsListResponse catalogue;
        if (_connection.Connectivity == ConnectivityState.Online)
        {
            try
            {
                EtagResponse<AppsListResponse> response = await _server.GetAppsAsync(cached is null ? null : _apps.ETag, cancellationToken).ConfigureAwait(false);
                if (response.NotModified && cached is not null)
                {
                    catalogue = cached;
                }
                else
                {
                    catalogue = response.Require();
                    _apps.Set(catalogue, response.ETag);
                }
            }
            catch (Exception ex) when (cached is not null && (ex is HttpRequestException || ex is ServerApiException { IsRetryable: true }))
            {
                _logger.LogWarning("apps.list: server unavailable; serving the cached catalogue");
                catalogue = cached;
            }
        }
        else
        {
            catalogue = cached ?? throw IpcError.AgentOffline().ToException();
        }

        IReadOnlyList<App> items = catalogue.Items.Select(app => app with { Allowed = app.Allowed && IsAllowed(app.ExePath) }).ToList();
        return new AppsListResponse(items);
    }

    private async Task<GamesListResponse> ListAsync(IpcContext context, GamesListRequest? request, CancellationToken cancellationToken)
    {
        GamesListResponse list = await _library.ListAsync(request ?? new GamesListRequest(), cancellationToken).ConfigureAwait(false);
        return list with { Items = list.Items.Select(_media.Localize).ToList() };
    }

    private async Task<Game> GetAsync(IpcContext context, GamesGetRequest request, CancellationToken cancellationToken)
    {
        if (request.GameId == Guid.Empty)
        {
            throw IpcError.Validation("gameId", "required").ToException();
        }

        Game game = await _library.GetAsync(request.GameId, cancellationToken).ConfigureAwait(false)
                    ?? throw IpcError.NotFound($"Game {request.GameId}").ToException();
        return _media.Localize(game);
    }

    private async Task<LaunchResult> LaunchAsync(IpcContext context, GamesLaunchRequest request, CancellationToken cancellationToken)
    {
        if (request.GameId == Guid.Empty)
        {
            throw IpcError.Validation("gameId", "required").ToException();
        }

        if (request.Resolution is { } resolution && (resolution.Width <= 0 || resolution.Height <= 0))
        {
            throw IpcError.Validation("resolution", "range").ToException();
        }

        if (_saves is not null)
        {
            // The game must find the player's saves where it looks for them: the launch waits for the attach (up to
            // games.playerSaves.attachTimeoutSec; the Shell shows "loading your saves" meanwhile, saves.status).
            await _saves.WaitForAttachAsync(cancellationToken).ConfigureAwait(false);
        }

        LaunchResult result = await _launch.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            // §7.3: over IPC, failures are errors, never ok = false.
            throw (result.Error ?? IpcError.GameLaunchFailed("launch", "Launch failed")).ToException();
        }

        return result;
    }

    private async Task<AppsLaunchResponse> LaunchAppAsync(IpcContext context, AppsLaunchRequest request, CancellationToken cancellationToken)
    {
        if (request.AppId == Guid.Empty)
        {
            throw IpcError.Validation("appId", "required").ToException();
        }

        AppsListResponse catalogue = await ListAppsAsync(context, cancellationToken).ConfigureAwait(false);
        App app = catalogue.Items.FirstOrDefault(a => a.Id == request.AppId) ?? throw IpcError.NotFound($"App {request.AppId}").ToException();
        if (!app.Allowed)
        {
            throw IpcError.PolicyDenied(IsAllowed(app.ExePath) ? "apps.allowed" : ProcessAllowlistModule.SectionKey).ToException();
        }

        if (string.IsNullOrWhiteSpace(app.ExePath) || !File.Exists(app.ExePath))
        {
            throw IpcError.GameLaunchFailed("resolve", $"Executable of '{app.Title}' was not found").ToException();
        }

        int sessionId = _kiosk.ActiveSessionId ?? throw IpcError.GameLaunchFailed("session", "No interactive kiosk session").ToException();

        // A second tap on the tile used to start a second copy (a second Discord, a second Steam asking to log in again).
        // The running one is brought to the front by the Shell instead.
        IReadOnlyList<int> runningPids = AppPids(app, sessionId);
        if (runningPids.Count > 0 && request.Force != true)
        {
            RunningApp? running = _running.Resolve(new[] { app }, _sessionProcesses.Snapshot(sessionId), GamePids()).FirstOrDefault();
            int pid = running?.Pid ?? runningPids[0];
            _logger.LogInformation("App {Title} ({AppId}) is already running as pid {Pid}; not started again", app.Title, app.Id, pid);
            return new AppsLaunchResponse(true, pid, running?.StartedAt ?? DateTimeOffset.UtcNow, AlreadyRunning: true, Pids: running?.Pids ?? runningPids);
        }

        var spec = new ProcessStartSpec
        {
            Exe = app.ExePath,
            Args = GameLaunchService.SanitizeArgs(request.Args),
            WorkingDir = Path.GetDirectoryName(app.ExePath),
            SessionId = (uint)sessionId,
        };

        try
        {
            using LaunchedProcess launched = await _processes.LaunchAsync(spec, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("App {Title} ({AppId}) launched as pid {Pid} in session {Session}", app.Title, app.Id, launched.Pid, sessionId);
            _running.Record(app.Id, launched.Pid, launched.StartedAt);
            return new AppsLaunchResponse(true, launched.Pid, launched.StartedAt, Pids: new[] { launched.Pid });
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex, "App {Title} could not be started", app.Title);
            throw IpcError.GameLaunchFailed("createProcess", ex.Message, ex.NativeErrorCode).ToException();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "App {Title} could not be started", app.Title);
            throw IpcError.GameLaunchFailed("createProcess", ex.Message).ToException();
        }
    }

    /// <summary>
    /// <c>apps.kill</c>: ends every process of the app in the player's session, descendants first. Never a game's, the
    /// Shell's, the Agent's or Windows' own (<see cref="RunningApps"/>, <see cref="ProcessKiller.SystemCritical"/>). The Shell
    /// has asked the app's windows to close already; what is left is terminated.
    /// </summary>
    private Task<GamesKillResponse> KillAppAsync(IpcContext context, AppsKillRequest request, CancellationToken cancellationToken)
    {
        if (request.AppId == Guid.Empty)
        {
            throw IpcError.Validation("appId", "required").ToException();
        }

        int sessionId = _kiosk.ActiveSessionId ?? throw IpcError.NotFound($"Running app {request.AppId}").ToException();
        App app = Catalogue().FirstOrDefault(a => a.Id == request.AppId) ?? throw IpcError.NotFound($"App {request.AppId}").ToException();
        IReadOnlyList<AppProcess> processes = _sessionProcesses.Snapshot(sessionId);
        IReadOnlyList<int> pids = RunningApps.Match(app, _running.LaunchOf(app.Id), processes, GamePids());
        if (pids.Count == 0)
        {
            throw IpcError.NotFound($"Running app {app.Title}").ToException();
        }

        // Youngest first: a parent that respawns its children (a browser, an updater) is gone before it notices.
        var started = processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First().StartedAt ?? DateTimeOffset.MinValue);
        var killed = new List<int>();
        foreach (int pid in pids.OrderByDescending(p => started.GetValueOrDefault(p, DateTimeOffset.MinValue)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_killer.KillProcess(pid, TimeSpan.Zero) is { } dead)
            {
                killed.Add(dead.Pid);
            }
        }

        _logger.LogInformation("App {Title} ({AppId}) closed: {Killed} of {Count} processes", app.Title, app.Id, killed.Count, pids.Count);
        return Task.FromResult(new GamesKillResponse(killed.Count, killed));
    }

    /// <summary>The running apps of the cached catalogue (no server round trip: the Shell polls this).</summary>
    private IReadOnlyList<RunningApp> RunningAppsNow()
    {
        if (_kiosk.ActiveSessionId is not { } sessionId)
        {
            return Array.Empty<RunningApp>();
        }

        IReadOnlyList<App> catalogue = Catalogue();
        return catalogue.Count == 0
            ? Array.Empty<RunningApp>()
            : _running.Resolve(catalogue, _sessionProcesses.Snapshot(sessionId), GamePids());
    }

    private IReadOnlyList<int> AppPids(App app, int sessionId) =>
        RunningApps.Match(app, _running.LaunchOf(app.Id), _sessionProcesses.Snapshot(sessionId), GamePids());

    private IReadOnlyList<App> Catalogue() => _shellSettings.Features.Apps ? _apps.Get()?.Items ?? Array.Empty<App>() : Array.Empty<App>();

    /// <summary>Pids of the running games: never an app's, so closing Steam does not take the game it started down.</summary>
    private HashSet<int> GamePids() => _launch.Running().Items.Select(r => r.Pid).ToHashSet();

    private bool IsAllowed(string exePath) => _allowlist is null || string.IsNullOrWhiteSpace(exePath) || _allowlist.IsAllowed(exePath);
}
