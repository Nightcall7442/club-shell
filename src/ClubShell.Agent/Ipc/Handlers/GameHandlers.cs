using System.ComponentModel;
using System.Runtime.Versioning;
using ClubShell.Agent.Games;
using ClubShell.Agent.Policy;
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
/// and <see cref="GameLaunchService"/>; the apps catalogue (<c>GET /apps</c>) is cached in <c>cache\apps.json</c> and
/// filtered by the process allow-list, and apps are started in the kiosk session through <see cref="ProcessLauncher"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GameHandlers : IIpcHandlerGroup
{
    private readonly GameLibrary _library;
    private readonly GameLaunchService _launch;
    private readonly IServerClient _server;
    private readonly ProcessLauncher _processes;
    private readonly IKioskSessionLocator _kiosk;
    private readonly ServerConnection _connection;
    private readonly ShellSettingsStore _shellSettings;
    private readonly ProcessAllowlistModule? _allowlist;
    private readonly ILogger<GameHandlers> _logger;
    private readonly ContractFileCache<AppsListResponse> _apps;

    /// <summary>Creates the group. <paramref name="allowlist"/> is optional; without it only <see cref="App.Allowed"/> gates app launches.</summary>
    public GameHandlers(
        GameLibrary library,
        GameLaunchService launch,
        IServerClient server,
        ProcessLauncher processes,
        IKioskSessionLocator kiosk,
        ServerConnection connection,
        ShellSettingsStore shellSettings,
        IOptionsMonitor<AgentSettings> settings,
        ILogger<GameHandlers> logger,
        ProcessAllowlistModule? allowlist = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(kiosk);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(shellSettings);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _library = library;
        _launch = launch;
        _server = server;
        _processes = processes;
        _kiosk = kiosk;
        _connection = connection;
        _shellSettings = shellSettings;
        _allowlist = allowlist;
        _logger = logger;
        _apps = new ContractFileCache<AppsListResponse>(Path.Combine(settings.CurrentValue.CacheDir, "apps.json"), logger);
    }

    /// <inheritdoc />
    public void Register(MessageDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        dispatcher.RegisterOptional<GamesListRequest, GamesListResponse>(IpcMessages.Games.List, (_, request, cancellationToken) => _library.ListAsync(request ?? new GamesListRequest(), cancellationToken));
        dispatcher.Register<GamesGetRequest, Game>(IpcMessages.Games.Get, GetAsync);
        dispatcher.Register<GamesLaunchRequest, LaunchResult>(IpcMessages.Games.Launch, LaunchAsync);
        dispatcher.RegisterOptional<GamesKillRequest, GamesKillResponse>(IpcMessages.Games.Kill, (_, request, cancellationToken) => _launch.KillAsync(request ?? new GamesKillRequest(), cancellationToken));
        dispatcher.RegisterNoPayload<GamesRunningResponse>(IpcMessages.Games.Running, (_, _) => Task.FromResult(_launch.Running()));
        dispatcher.Register<GamesInstallStatusRequest, GameInstallStatus>(IpcMessages.Games.InstallStatus, (_, request, cancellationToken) => _library.GetInstallStatusAsync(request.GameId, cancellationToken));

        dispatcher.RegisterNoPayload<AppsListResponse>(IpcMessages.Apps.List, ListAppsAsync);
        dispatcher.Register<AppsLaunchRequest, AppsLaunchResponse>(IpcMessages.Apps.Launch, LaunchAppAsync);
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

    private async Task<Game> GetAsync(IpcContext context, GamesGetRequest request, CancellationToken cancellationToken)
    {
        if (request.GameId == Guid.Empty)
        {
            throw IpcError.Validation("gameId", "required").ToException();
        }

        return await _library.GetAsync(request.GameId, cancellationToken).ConfigureAwait(false)
               ?? throw IpcError.NotFound($"Game {request.GameId}").ToException();
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
            return new AppsLaunchResponse(true, launched.Pid, launched.StartedAt);
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

    private bool IsAllowed(string exePath) => _allowlist is null || string.IsNullOrWhiteSpace(exePath) || _allowlist.IsAllowed(exePath);
}
