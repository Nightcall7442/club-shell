using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.Versioning;
using ClubShell.Agent.Games.Accounts;
using ClubShell.Agent.Games.Launchers;
using ClubShell.Agent.Games.Saves;
using ClubShell.Agent.Session;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Sessions;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Processes;
using Microsoft.Extensions.Options;
using PlaySession = ClubShell.Contracts.Sessions.Session;

namespace ClubShell.Agent.Games;

/// <summary>Where the kiosk user is logged on (provided by the watchdog / users subsystem).</summary>
public interface IKioskSessionLocator
{
    /// <summary>WTS session id of the active kiosk session, or <see langword="null"/> when none is logged on.</summary>
    int? ActiveSessionId { get; }

    /// <summary>Kiosk account name (e.g. <c>club</c>).</summary>
    string KioskUser { get; }
}

/// <summary>Pre-launch anti-cheat prerequisites check (provided by the anti-cheat subsystem).</summary>
public interface IAntiCheatGate
{
    /// <summary>Runs the checks relevant to <paramref name="game"/>; one result per check, <see cref="AntiCheatCheckResult.Ok"/> false on violation.</summary>
    Task<AntiCheatCheckResult[]> CheckForLaunchAsync(Game game, CancellationToken cancellationToken);
}

/// <summary>Delivers <c>game.stateChanged</c> events to the Shell (provided by the IPC server).</summary>
public interface IGameEventSink
{
    /// <summary>Publishes a state change.</summary>
    ValueTask PublishAsync(GameStateChanged change, CancellationToken cancellationToken);
}

/// <summary>
/// Orchestrates <c>games.launch</c> / <c>games.kill</c> / <c>games.running</c> (IPC_PROTOCOL.md §6.9): session and
/// policy gates, anti-cheat check, account lease + credential injection, cloud-save restore, launch through the
/// launcher backend, job object, tracking, launch report and <c>game.stateChanged</c> events.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GameLaunchService : IDisposable
{
    /// <summary>
    /// <c>antiCheatBlocked.details.reason</c> and the launch report's <see cref="AntiCheatCheckResult.Reason"/> when a game
    /// with an anti-cheat would start from a network path. The contract keeps the set of reasons open (any string,
    /// <c>AntiCheatChecks</c> in server/contracts/openapi.yaml), so this agent-only value stays out of
    /// <see cref="AntiCheatChecks"/> and its generated TypeScript and Rust mirrors.
    /// </summary>
    public const string NetworkPathReason = "networkPath";

    private const int MaxExtraArgsLength = 512;

    /// <summary>Links followed by <see cref="IsOnNetwork"/> in one chain: more is a loop or a layout no club uses.</summary>
    private const int MaxLinkHops = 8;
    private static readonly TimeSpan LaunchGrace = TimeSpan.FromSeconds(15);

    /// <summary>How long a "cancel launch" waits for the cancelled launch to unwind before it closes what it started.</summary>
    private static readonly TimeSpan CancelWait = TimeSpan.FromSeconds(5);

    private readonly GameLibrary _library;

    /// <summary>Launches still starting, by game: <c>games.kill</c> for such a game cancels its launch ("cancel launch").</summary>
    private readonly ConcurrentDictionary<Guid, InFlightLaunch> _inFlight = new();
    private readonly Dictionary<LauncherType, IGameLauncher> _launchers = new();
    private readonly ISessionService _sessions;
    private readonly IPolicyEnforcer _policy;
    private readonly IAntiCheatGate _antiCheat;
    private readonly AccountPool _pool;
    private readonly AccountInjector _injector;
    private readonly CloudSaveSync _saves;
    private readonly PlayerSettingsSync? _playerSettings;
    private readonly GameSessionTracker _tracker;
    private readonly IKioskSessionLocator _kiosk;
    private readonly IServerClient _server;
    private readonly OfflineSessionStore _outbox;
    private readonly IGameEventSink _events;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<GameLaunchService> _logger;
    private bool _disposed;

    /// <summary>Creates the service and subscribes to lease expiry.</summary>
    public GameLaunchService(
        GameLibrary library,
        IEnumerable<IGameLauncher> launchers,
        ISessionService sessions,
        IPolicyEnforcer policy,
        IAntiCheatGate antiCheat,
        AccountPool pool,
        AccountInjector injector,
        CloudSaveSync saves,
        GameSessionTracker tracker,
        IKioskSessionLocator kiosk,
        IServerClient server,
        OfflineSessionStore outbox,
        IGameEventSink events,
        IOptionsMonitor<AgentSettings> settings,
        IClock clock,
        ILogger<GameLaunchService> logger,
        PlayerSettingsSync? playerSettings = null)
    {
        ArgumentNullException.ThrowIfNull(launchers);
        _playerSettings = playerSettings;
        _library = library;
        _sessions = sessions;
        _policy = policy;
        _antiCheat = antiCheat;
        _pool = pool;
        _injector = injector;
        _saves = saves;
        _tracker = tracker;
        _kiosk = kiosk;
        _server = server;
        _outbox = outbox;
        _events = events;
        _settings = settings;
        _clock = clock;
        _logger = logger;
        foreach (IGameLauncher launcher in launchers)
        {
            _launchers.TryAdd(launcher.Launcher, launcher);
        }

        _pool.LeaseExpired += OnLeaseExpired;
    }

    /// <summary>Launcher backends registered.</summary>
    public IReadOnlyCollection<LauncherType> Launchers => _launchers.Keys;

    /// <summary>Handles <c>games.launch</c>: fills session/user/timeout from the current session and launches.</summary>
    public Task<LaunchResult> LaunchAsync(GamesLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PlaySession? session = _sessions.Current;
        if (session is null || _sessions.State != SessionState.Active)
        {
            return Task.FromResult(LaunchResult.Failure(IpcError.SessionNotActive(), _clock.UtcNow));
        }

        Game? game = _library.Get(request.GameId);
        if (game is null)
        {
            return Task.FromResult(LaunchResult.Failure(IpcError.NotFound($"Game {request.GameId}"), _clock.UtcNow));
        }

        var launch = new LaunchRequest(
            request.GameId,
            session.Id,
            session.UserId,
            request.UseAccountPool ?? game.RequiresAccount,
            null,
            request.ExtraArgs,
            request.Resolution,
            _settings.CurrentValue.Games.LaunchTimeoutSec);
        return LaunchAsync(launch, cancellationToken);
    }

    /// <summary>
    /// Executes a full <see cref="LaunchRequest"/> (IPC or <c>launchGame</c> server command). Never throws for
    /// launch-level failures: returns <see cref="LaunchResult.Ok"/> = <see langword="false"/> with the
    /// <see cref="IpcError"/> (IPC handlers convert it with <see cref="IpcError.ToException"/>).
    /// </summary>
    public async Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        long startedTs = _clock.GetTimestamp();
        DateTimeOffset startedAt = _clock.UtcNow;

        Game? game = await _library.GetAsync(request.GameId, cancellationToken).ConfigureAwait(false);
        if (game is null)
        {
            return LaunchResult.Failure(IpcError.NotFound($"Game {request.GameId}"), startedAt);
        }

        // One launch per game at a time. A second one is refused without events, so the first launch's progress on the
        // Shell is left alone.
        var inFlight = new InFlightLaunch();
        if (!_inFlight.TryAdd(game.Id, inFlight))
        {
            inFlight.Dispose();
            return LaunchResult.Failure(IpcError.Conflict($"{game.Title} is already starting", "alreadyRunning"), startedAt);
        }

        try
        {
            return await LaunchCoreAsync(game, request, startedAt, startedTs, inFlight, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<Guid, InFlightLaunch>(game.Id, inFlight));
            inFlight.Finish();
        }
    }

    private async Task<LaunchResult> LaunchCoreAsync(
        Game game, LaunchRequest request, DateTimeOffset startedAt, long startedTs, InFlightLaunch inFlight, CancellationToken cancellationToken)
    {
        // The steps up to the game's start also stop on "cancel launch"; what follows the start (events, tracking,
        // report) only on the request's own cancellation.
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, inFlight.Token);
        CancellationToken token = cancel.Token;
        var antiCheat = new AntiCheatCheckResult(game.AntiCheat, true);
        ActiveLease? lease = null;
        InjectionResult? injection = null;
        JobObject? job = null;
        IpcError error;
        try
        {
            PlaySession? session = _sessions.Current;
            if (session is null || session.Id != request.SessionId || _sessions.State != SessionState.Active)
            {
                throw IpcError.SessionNotActive().ToException();
            }

            if (PolicyDenies(game, out string rule))
            {
                throw IpcError.PolicyDenied(rule, "exePath").ToException();
            }

            // Not subject to blockOnViolation: where the game is installed is the library's layout, not a finding about
            // this PC, and an anti-cheat that refuses a network path does so whatever the policy says.
            if (NetworkPathCheck(game) is { } onNetwork)
            {
                antiCheat = onNetwork;
                _logger.LogWarning(
                    "{Title} ({Kind}) is on a network path (install {InstallPath}, exe {ExePath}); games with an anti-cheat must be on a local disk",
                    game.Title,
                    onNetwork.Kind,
                    game.InstallPath,
                    game.ExePath);
                throw IpcError.AntiCheatBlocked(onNetwork.Kind, NetworkPathReason).ToException();
            }

            antiCheat = await CheckAntiCheatAsync(game, token).ConfigureAwait(false);
            if (!antiCheat.Ok && (_policy.Current?.Anticheat.BlockOnViolation ?? true))
            {
                throw IpcError.AntiCheatBlocked(antiCheat.Kind, antiCheat.Reason ?? "violation").ToException();
            }

            int wtsSession = _kiosk.ActiveSessionId ?? throw IpcError.GameLaunchFailed("session", "No interactive kiosk session").ToException();
            if (!_launchers.TryGetValue(game.Launcher, out IGameLauncher? launcher))
            {
                throw IpcError.GameLaunchFailed("launcher", $"No launcher backend for {game.Launcher}").ToException();
            }

            if (!await launcher.IsAvailableAsync(token).ConfigureAwait(false))
            {
                throw IpcError.GameLaunchFailed("launcher", $"{game.Launcher} client is not available").ToException();
            }

            if (_tracker.FindByGame(game.Id).Count > 0)
            {
                throw IpcError.Conflict($"{game.Title} is already running", "alreadyRunning").ToException();
            }

            if (request.UseAccountPool || game.RequiresAccount)
            {
                if (!_pool.Enabled)
                {
                    throw IpcError.Of(ErrorCode.AccountPoolExhausted, "Account pool is disabled on this PC").ToException();
                }

                // Lease, credentials and saves are not interrupted half-way (an injection cut short is never restored, a
                // lease the server granted would be lost): "cancel launch" is honoured between the steps.
                lease = await _pool.LeaseAsync(game, request.SessionId, request.AccountLeaseId, cancellationToken).ConfigureAwait(false);
                ThrowIfCancelled(game, inFlight);
                injection = await _injector.InjectAsync(game, lease, cancellationToken).ConfigureAwait(false);
                ThrowIfCancelled(game, inFlight);
                await _saves.DownloadAsync(game, lease, cancellationToken).ConfigureAwait(false);
                ThrowIfCancelled(game, inFlight);
            }

            // The player's own binds / sensitivity / graphics, whatever PC they sit at.
            if (_playerSettings is not null)
            {
                await _playerSettings.RestoreAsync(game, request.UserId, cancellationToken).ConfigureAwait(false);
                ThrowIfCancelled(game, inFlight);
            }

            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (injection is not null)
            {
                foreach ((string key, string value) in injection.EnvVars)
                {
                    env[key] = value;
                }

                if (!string.IsNullOrWhiteSpace(injection.ExtraArgs))
                {
                    env[LauncherBase.LauncherArgsEnvKey] = injection.ExtraArgs;
                }
            }

            int timeoutSec = request.LaunchTimeoutSec > 0 ? request.LaunchTimeoutSec : _settings.CurrentValue.Games.LaunchTimeoutSec;
            LaunchRequest effective = request with
            {
                UseAccountPool = lease is not null,
                AccountLeaseId = lease?.LeaseId,
                ExtraArgs = SanitizeArgs(request.ExtraArgs),
                LaunchTimeoutSec = timeoutSec,
            };
            var context = new LaunchContext(wtsSession, _kiosk.KioskUser, env, request.Resolution);

            await PublishAsync(new GameStateChanged(game.Id, game.Title, GameState.Launching, _clock.UtcNow), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Launching {Title} via {Launcher} in session {WtsSession} (lease {LeaseId})", game.Title, game.Launcher, wtsSession, lease?.LeaseId);

            LaunchResult result;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSec) + LaunchGrace);
                try
                {
                    result = await launcher.LaunchAsync(game, effective, lease?.Contract, context, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw (inFlight.Cancelled ? Cancelled(game) : IpcError.Timeout($"Launch of {game.Title} timed out after {timeoutSec}s")).ToException();
                }
            }

            if (!result.Ok || result.Pid is null)
            {
                throw (result.Error ?? IpcError.GameLaunchFailed("launch", "Launcher returned no process")).ToException();
            }

            int pid = result.Pid.Value;
            if (inFlight.Cancelled)
            {
                // Cancelled while the launcher was finishing: the game started all the same, so it is closed again.
                _logger.LogInformation("{Title} started as pid {Pid} after its launch was cancelled; closing it", game.Title, pid);
                await KillStartedAsync(launcher, game, pid).ConfigureAwait(false);
                throw Cancelled(game).ToException();
            }

            job = CreateJob(pid);
            int durationMs = (int)_clock.GetElapsedTime(startedTs).TotalMilliseconds;
            var running = new RunningGame(game.Id, game.Title, pid, result.StartedAt, lease?.LeaseId, GameState.Running);

            // Running goes out before the tracker's watcher starts: a launcher that hands over to the real game and exits
            // at once (Counter-Strike 1.6's cstrike.exe) had its Exited published first, and the Shell then stayed on
            // "running" for good, its "close game" answered notFound.
            await PublishAsync(new GameStateChanged(game.Id, game.Title, GameState.Running, _clock.UtcNow, pid), cancellationToken).ConfigureAwait(false);
            await _tracker.TrackAsync(new GameLaunchRecord(game, effective, running, lease, injection, antiCheat, job, durationMs), cancellationToken).ConfigureAwait(false);
            Settled(game, inFlight); // tracked: "close game" reaches it through the tracker from here on
            string? injectionError = injection?.Error;
            job = null;
            injection = null;
            lease = null;
            await ReportAsync(game, effective, result, durationMs, antiCheat, injectionError, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("{Title} running as pid {Pid} after {Duration} ms", game.Title, pid, durationMs);
            return result;
        }
        catch (IpcException ex)
        {
            error = ex.Error;
        }
        catch (ServerApiException ex)
        {
            error = ex.ToIpcError();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            _logger.LogError(ex, "Unexpected failure launching {Title}", game.Title);
            error = IpcError.GameLaunchFailed("internal", ex.Message);
        }
        catch (OperationCanceledException) when (inFlight.Cancelled && !cancellationToken.IsCancellationRequested)
        {
            error = Cancelled(game);
        }

        return await FailAsync(game, request, startedAt, startedTs, antiCheat, error, lease, injection, job, () => Settled(game, inFlight), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The player cancelled the launch (<c>games.kill</c> while it was starting): <c>gameLaunchFailed{stage: cancelled}</c>.</summary>
    private static IpcError Cancelled(Game game) => IpcError.GameLaunchFailed("cancelled", $"Launch of {game.Title} cancelled");

    private static void ThrowIfCancelled(Game game, InFlightLaunch inFlight)
    {
        if (inFlight.Cancelled)
        {
            throw Cancelled(game).ToException();
        }
    }

    /// <summary>
    /// The launch has its outcome: it no longer answers "cancel launch" and a retry of the game may start (the failure
    /// cleanup and the reports still running). <see cref="InFlightLaunch.Done"/> completes only after them.
    /// </summary>
    private void Settled(Game game, InFlightLaunch inFlight) => _inFlight.TryRemove(new KeyValuePair<Guid, InFlightLaunch>(game.Id, inFlight));

    /// <summary>Closes a game that started after its launch was cancelled.</summary>
    private async Task KillStartedAsync(IGameLauncher launcher, Game game, int pid)
    {
        try
        {
            await launcher.KillAsync(pid, force: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Closing {Title} pid {Pid} after a cancelled launch failed", game.Title, pid);
        }
    }

    /// <summary>
    /// Handles <c>games.kill</c>: by game, by pid, or everything when neither is given. A game still starting (a launcher
    /// waiting for its game process) has its launch cancelled — the Shell's "cancel launch" — and whatever it started is
    /// closed as well.
    /// </summary>
    public async Task<GamesKillResponse> KillAsync(GamesKillRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<GameLaunchRecord> targets = request switch
        {
            { Pid: { } pid } => _tracker.Find(pid) is { } one ? new[] { one } : throw IpcError.NotFound($"Running game with pid {pid}").ToException(),
            { GameId: { } gameId } => _tracker.FindByGame(gameId),
            _ => _tracker.All(),
        };
        if (request.Pid is null)
        {
            // Waits for a cancelled launch only when nothing tracked answered: then it is what the player sees.
            bool cancelled = await CancelLaunchesAsync(request.GameId, wait: targets.Count == 0, cancellationToken).ConfigureAwait(false);
            if (cancelled && targets.Count == 0)
            {
                // A launch that got as far as tracking its game before it saw the cancel.
                targets = request.GameId is { } gameId ? _tracker.FindByGame(gameId) : _tracker.All();
            }
            else if (request.GameId is { } id && targets.Count == 0)
            {
                throw IpcError.NotFound($"Running game {id}").ToException();
            }
        }

        List<int> killed = await KillRecordsAsync(targets, request.Force == true, AccountLeaseReleaseReason.Manual, cancellationToken).ConfigureAwait(false);
        return new GamesKillResponse(killed.Count, killed);
    }

    /// <summary>Handles <c>games.running</c>.</summary>
    public GamesRunningResponse Running() => new(_tracker.ListRunning());

    /// <summary>Running games in heartbeat form.</summary>
    public IReadOnlyList<HeartbeatRunningGame> HeartbeatGames() =>
        _tracker.ListRunning().Select(r => new HeartbeatRunningGame(r.GameId, r.Pid, r.StartedAt)).ToList();

    /// <summary>Session-end teardown: terminates every tracked game (graceful close first unless the reason is urgent) and releases their leases.</summary>
    public async Task KillAllAsync(SessionEndReason reason, CancellationToken cancellationToken)
    {
        bool force = reason is not SessionEndReason.User;

        // A game still starting would come up after the session is over.
        await CancelLaunchesAsync(null, wait: true, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<GameLaunchRecord> targets = _tracker.All();
        if (targets.Count > 0)
        {
            _logger.LogInformation("Killing {Count} running games (session end: {Reason})", targets.Count, reason);
            await KillRecordsAsync(targets, force, AccountLeaseReleaseReason.SessionEnd, cancellationToken).ConfigureAwait(false);
            TimeSpan wait = TimeSpan.FromSeconds(_settings.CurrentValue.Games.KillGraceSec) + TimeSpan.FromSeconds(5);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(wait);
            foreach (GameLaunchRecord record in targets)
            {
                try
                {
                    await _tracker.WaitForExitAsync(record.Running.Pid, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Exit processing of pid {Pid} did not finish within {Wait}", record.Running.Pid, wait);
                    break;
                }
            }
        }

        await _pool.ReleaseAllAsync(AccountLeaseReleaseReason.SessionEnd, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pool.LeaseExpired -= OnLeaseExpired;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Anti-cheat the launch gate checks for <paramref name="game"/>: the catalogue value, except that a Riot title the
    /// catalogue did not tag is treated as Vanguard (every current Riot game ships it).
    /// </summary>
    public static AntiCheatKind EffectiveAntiCheat(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return game.AntiCheat == AntiCheatKind.None && game.Launcher == LauncherType.Riot ? AntiCheatKind.Vanguard : game.AntiCheat;
    }

    /// <summary>
    /// The failed check (<see cref="NetworkPathReason"/>) when <paramref name="game"/> has an anti-cheat
    /// (<see cref="EffectiveAntiCheat"/>) and its install directory or exe is on the network, directly or through a link
    /// (<see cref="IsOnNetwork"/>):
    /// several anti-cheats refuse to start from a network path, so such games belong on a local disk and the SMB games
    /// share holds the rest of the library (docs/DISKLESS.md). <see langword="null"/> when the launch may go on.
    /// </summary>
    /// <param name="game">Game about to launch, with its detected install path.</param>
    /// <param name="driveType">Drive type by drive root (<c>G:\</c>); <see cref="DriveInfo.DriveType"/> when omitted.</param>
    /// <param name="linkTarget">
    /// Immediate target of a symbolic link or junction, <see langword="null"/> for anything else
    /// (<see cref="FileSystemInfo.LinkTarget"/> when omitted); see <see cref="IsOnNetwork"/>.
    /// </param>
    public static AntiCheatCheckResult? NetworkPathCheck(Game game, Func<string, DriveType>? driveType = null, Func<string, string?>? linkTarget = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        AntiCheatKind kind = EffectiveAntiCheat(game);
        if (kind == AntiCheatKind.None)
        {
            return null;
        }

        // A launcher game (Steam, Riot, Epic...) usually has no exe before its launcher starts it, so the install
        // directory is what is known; a relative exe lies inside it. With neither path known there is nothing to judge
        // and the launch goes on (fail open): the launcher alone decides where the game runs from.
        driveType ??= root => new DriveInfo(root).DriveType;
        linkTarget ??= LinkTargetOf;
        return IsOnNetwork(game.InstallPath, driveType, linkTarget) || IsOnNetwork(game.ExePath, driveType, linkTarget)
            ? new AntiCheatCheckResult(kind, false, NetworkPathReason)
            : null;
    }

    /// <summary>
    /// <see cref="IsNetworkPath"/> of <paramref name="path"/> or of where the links on it lead: a club may link a local
    /// folder to the share (<c>mklink /D C:\Games\VALORANT \\nas\games\VALORANT</c>) so that a launcher takes the library,
    /// and Windows follows such a link to the share. The directories are looked at from the root down and only up to the
    /// first link, whose own target is read, never the share behind it; up to <see cref="MaxLinkHops"/> links in a chain
    /// are followed. A path that cannot be read counts as local (fail open: a local game is never refused for it).
    /// </summary>
    /// <param name="path">Path to classify.</param>
    /// <param name="driveType">Drive type by drive root (<c>G:\</c>).</param>
    /// <param name="linkTarget">Immediate target of a symbolic link or junction, <see langword="null"/> for anything else.</param>
    public static bool IsOnNetwork(string? path, Func<string, DriveType> driveType, Func<string, string?> linkTarget)
    {
        ArgumentNullException.ThrowIfNull(linkTarget);
        for (int hops = 0; ; hops++)
        {
            if (IsNetworkPath(path, driveType))
            {
                return true;
            }

            if (hops == MaxLinkHops || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                path = ThroughFirstLink(path.Trim().Replace('/', '\\'), linkTarget);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
            }

            if (path is null)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// <paramref name="path"/> with its first link, from the root down, replaced by that link's target; <see langword="null"/>
    /// when it has none (or is not a full path).
    /// </summary>
    private static string? ThroughFirstLink(string path, Func<string, string?> linkTarget)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return null;
        }

        string current = Path.GetPathRoot(path)!;
        string[] parts = path[current.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            current = Path.Join(current, parts[i]);
            if (linkTarget(current) is { Length: > 0 } target)
            {
                // A relative target is relative to the directory holding the link.
                string resolved = Path.IsPathFullyQualified(target) ? target : Path.GetFullPath(Path.Join(Path.GetDirectoryName(current), target));
                return i == parts.Length - 1 ? resolved : Path.Join(resolved, string.Join('\\', parts[(i + 1)..]));
            }
        }

        return null;
    }

    /// <summary>The link's immediate target; <see langword="null"/> for a plain file or directory, a missing one or an error.</summary>
    private static string? LinkTargetOf(string path)
    {
        try
        {
            return new DirectoryInfo(path).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// <see langword="true"/> for a UNC path (<c>\\server\share\...</c>, <c>\\?\UNC\server\share\...</c>) or a path on a
    /// drive whose type is <see cref="DriveType.Network"/> (a mapped share, such as the games share's drive letter).
    /// Relative and empty paths are not on the network.
    /// </summary>
    /// <param name="path">Path to classify.</param>
    /// <param name="driveType">Drive type by drive root (<c>G:\</c>).</param>
    public static bool IsNetworkPath(string? path, Func<string, DriveType> driveType)
    {
        ArgumentNullException.ThrowIfNull(driveType);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalized = path.Trim().Replace('/', '\\');
        if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal) || normalized.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            // A device path: \\?\UNC\server\share is a share, \\?\C:\ a drive, \\?\Volume{...}\ a local volume.
            normalized = normalized[4..];
            if (normalized.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        else if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        return normalized.Length >= 2 && normalized[1] == ':' && char.IsAsciiLetter(normalized[0])
            && driveType(normalized[0] + @":\") == DriveType.Network;
    }

    /// <summary>Strips control characters and caps the length of caller-supplied arguments.</summary>
    public static string? SanitizeArgs(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            return null;
        }

        var chars = args.Where(c => !char.IsControl(c)).Take(MaxExtraArgsLength).ToArray();
        string clean = new string(chars).Trim();
        return clean.Length == 0 ? null : clean;
    }

    private async Task<List<int>> KillRecordsAsync(IReadOnlyList<GameLaunchRecord> targets, bool force, AccountLeaseReleaseReason releaseReason, CancellationToken cancellationToken)
    {
        var killed = new List<int>();
        foreach (GameLaunchRecord record in targets)
        {
            int pid = record.Running.Pid;
            _tracker.MarkKilled(pid, releaseReason);
            try
            {
                if (_launchers.TryGetValue(record.Game.Launcher, out IGameLauncher? launcher))
                {
                    await launcher.KillAsync(pid, force, cancellationToken).ConfigureAwait(false);
                }

                // The tree walk misses processes whose parent has already exited — the process a launcher handed over
                // to, its children — but they are still in the game's job.
                TerminateJob(record);
                killed.Add(pid);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ObjectDisposedException)
            {
                _logger.LogWarning(ex, "Kill of {Title} pid {Pid} failed", record.Game.Title, pid);
            }
        }

        return killed;
    }

    /// <summary>
    /// Cancels the launches still starting (of <paramref name="gameId"/>, or all); with <paramref name="wait"/>, waits
    /// up to <see cref="CancelWait"/> for them to unwind. Returns whether there was any.
    /// </summary>
    private async Task<bool> CancelLaunchesAsync(Guid? gameId, bool wait, CancellationToken cancellationToken)
    {
        List<InFlightLaunch> launches = _inFlight.Where(l => gameId is null || l.Key == gameId).Select(l => l.Value).ToList();
        if (launches.Count == 0)
        {
            return false;
        }

        _logger.LogInformation("Cancelling {Count} game launch(es) still starting ({GameId})", launches.Count, gameId?.ToString() ?? "all");
        launches.ForEach(l => l.Cancel());
        if (wait)
        {
            try
            {
                await Task.WhenAll(launches.Select(l => l.Done)).WaitAsync(CancelWait, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Cancelled game launch did not finish within {Wait}", CancelWait);
            }
        }

        return true;
    }

    private void TerminateJob(GameLaunchRecord record)
    {
        try
        {
            record.Job.Terminate();
        }
        catch (Exception ex) when (ex is Win32Exception or ObjectDisposedException or InvalidOperationException)
        {
            // The job is gone with the game (its exit already processed), or the game could not be put in one.
            _logger.LogDebug(ex, "Job of {Title} not terminated", record.Game.Title);
        }
    }

    private bool PolicyDenies(Game game, out string rule)
    {
        rule = "processAllowlist";
        ProcessAllowlistPolicy? allowlist = _policy.Current?.ProcessAllowlist;
        if (allowlist is null || allowlist.Patterns.Count == 0 || string.IsNullOrWhiteSpace(game.ExePath))
        {
            return false;
        }

        string exeName = Path.GetFileName(game.ExePath);
        bool matches = allowlist.Patterns.Any(pattern => ProcessKiller.WildcardToRegex(pattern).IsMatch(exeName));
        return allowlist.Mode == AllowlistMode.Allow ? !matches : matches;
    }

    private async Task<AntiCheatCheckResult> CheckAntiCheatAsync(Game game, CancellationToken cancellationToken)
    {
        game = game with { AntiCheat = EffectiveAntiCheat(game) };
        if (game.AntiCheat == AntiCheatKind.None)
        {
            return new AntiCheatCheckResult(AntiCheatKind.None, true);
        }

        AntiCheatCheckResult[] results = await _antiCheat.CheckForLaunchAsync(game, cancellationToken).ConfigureAwait(false);
        AntiCheatCheckResult? failed = results.FirstOrDefault(r => !r.Ok);
        if (failed is null)
        {
            return results.FirstOrDefault(r => r.Kind == game.AntiCheat) ?? new AntiCheatCheckResult(game.AntiCheat, true);
        }

        _logger.LogWarning("Anti-cheat check {Check} failed for {Title} ({Kind})", failed.Reason, game.Title, failed.Kind);
        return failed;
    }

    private async Task<LaunchResult> FailAsync(
        Game game,
        LaunchRequest request,
        DateTimeOffset startedAt,
        long startedTs,
        AntiCheatCheckResult antiCheat,
        IpcError error,
        ActiveLease? lease,
        InjectionResult? injection,
        JobObject? job,
        Action settled,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning("Launch of {Title} failed: {Code} {Message}", game.Title, error.Code, error.Message);
        CancellationToken ct = cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken;
        job?.Dispose();
        if (injection is not null)
        {
            await _injector.RestoreAsync(injection, game.Launcher, ct).ConfigureAwait(false);
        }

        if (lease is not null)
        {
            await _pool.ReleaseAsync(lease.LeaseId, AccountLeaseReleaseReason.LaunchFailed, null, ct).ConfigureAwait(false);
        }

        var result = LaunchResult.Failure(error, startedAt, lease?.LeaseId);
        await PublishAsync(new GameStateChanged(game.Id, game.Title, GameState.Failed, _clock.UtcNow, null, null, error), ct).ConfigureAwait(false);

        // A retry may start now: after "failed" went out, so that event cannot end the retry's progress on the Shell, and
        // without waiting for the report (a slow or unreachable server).
        settled();
        await ReportAsync(game, request, result, (int)_clock.GetElapsedTime(startedTs).TotalMilliseconds, antiCheat, injection?.Error, ct).ConfigureAwait(false);
        return result;
    }

    private Task ReportAsync(Game game, LaunchRequest request, LaunchResult result, int durationMs, AntiCheatCheckResult antiCheat, string? injectionError, CancellationToken cancellationToken)
    {
        var report = new LaunchReport(request.SessionId, request.UserId, result, durationMs, game.Launcher, antiCheat, LaunchReportPhase.Launch, InjectionError: injectionError);
        return _outbox.SendLaunchReportAsync(_server, game.Id, report, cancellationToken);
    }

    private async Task PublishAsync(GameStateChanged change, CancellationToken cancellationToken)
    {
        try
        {
            await _events.PublishAsync(change, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "game.stateChanged publish failed for {GameId}", change.GameId);
        }
    }

    private JobObject CreateJob(int pid)
    {
        JobObject job = JobObject.Create(null, killOnClose: true);
        try
        {
            job.Assign(pid);
        }
        catch (Win32Exception ex)
        {
            // Already in a launcher-owned job without nesting rights: tree kill by pid still works.
            _logger.LogDebug(ex, "Cannot assign pid {Pid} to a job object", pid);
        }

        return job;
    }

    private void OnLeaseExpired(object? sender, ActiveLease lease)
    {
        IReadOnlyList<GameLaunchRecord> targets = _tracker.All().Where(r => r.Lease?.LeaseId == lease.LeaseId).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        _logger.LogWarning("Lease {LeaseId} expired; stopping {Count} game(s) using it", lease.LeaseId, targets.Count);
        _ = Task.Run(async () =>
        {
            try
            {
                await KillRecordsAsync(targets, force: false, AccountLeaseReleaseReason.Exit, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kill after lease expiry failed");
            }
        });
    }

    /// <summary>A launch between its start and its outcome: what "cancel launch" stops and then waits for.</summary>
    private sealed class InFlightLaunch : IDisposable
    {
        private readonly CancellationTokenSource _cancel = new();
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cancelled;

        /// <summary>Cancelled by <see cref="Cancel"/>.</summary>
        public CancellationToken Token => _cancel.Token;

        /// <summary>The player (or the session's end) cancelled it.</summary>
        public bool Cancelled => Volatile.Read(ref _cancelled) == 1;

        /// <summary>Completes once the launch has its outcome (success, failure or cancellation).</summary>
        public Task Done => _done.Task;

        public void Cancel()
        {
            Volatile.Write(ref _cancelled, 1);
            try
            {
                _cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Finished meanwhile.
            }
        }

        public void Finish()
        {
            _done.TrySetResult();
            Dispose();
        }

        public void Dispose() => _cancel.Dispose();
    }
}
