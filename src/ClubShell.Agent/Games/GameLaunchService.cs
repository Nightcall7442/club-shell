using System.ComponentModel;
using System.Runtime.Versioning;
using ClubShell.Agent.Games.Accounts;
using ClubShell.Agent.Games.Launchers;
using ClubShell.Agent.Games.Saves;
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
    private const int MaxExtraArgsLength = 512;
    private static readonly TimeSpan LaunchGrace = TimeSpan.FromSeconds(15);

    private readonly GameLibrary _library;
    private readonly Dictionary<LauncherType, IGameLauncher> _launchers = new();
    private readonly ISessionService _sessions;
    private readonly IPolicyEnforcer _policy;
    private readonly IAntiCheatGate _antiCheat;
    private readonly AccountPool _pool;
    private readonly AccountInjector _injector;
    private readonly CloudSaveSync _saves;
    private readonly GameSessionTracker _tracker;
    private readonly IKioskSessionLocator _kiosk;
    private readonly IServerClient _server;
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
        IGameEventSink events,
        IOptionsMonitor<AgentSettings> settings,
        IClock clock,
        ILogger<GameLaunchService> logger)
    {
        ArgumentNullException.ThrowIfNull(launchers);
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

            antiCheat = await CheckAntiCheatAsync(game, cancellationToken).ConfigureAwait(false);
            if (!antiCheat.Ok && (_policy.Current?.Anticheat.BlockOnViolation ?? true))
            {
                throw IpcError.AntiCheatBlocked(antiCheat.Kind, antiCheat.Reason ?? "violation").ToException();
            }

            int wtsSession = _kiosk.ActiveSessionId ?? throw IpcError.GameLaunchFailed("session", "No interactive kiosk session").ToException();
            if (!_launchers.TryGetValue(game.Launcher, out IGameLauncher? launcher))
            {
                throw IpcError.GameLaunchFailed("launcher", $"No launcher backend for {game.Launcher}").ToException();
            }

            if (!await launcher.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
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

                lease = await _pool.LeaseAsync(game, request.SessionId, request.AccountLeaseId, cancellationToken).ConfigureAwait(false);
                injection = await _injector.InjectAsync(game, lease, cancellationToken).ConfigureAwait(false);
                await _saves.DownloadAsync(game, lease, cancellationToken).ConfigureAwait(false);
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
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSec) + LaunchGrace);
                try
                {
                    result = await launcher.LaunchAsync(game, effective, lease?.Contract, context, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw IpcError.Timeout($"Launch of {game.Title} timed out after {timeoutSec}s").ToException();
                }
            }

            if (!result.Ok || result.Pid is null)
            {
                throw (result.Error ?? IpcError.GameLaunchFailed("launch", "Launcher returned no process")).ToException();
            }

            int pid = result.Pid.Value;
            job = CreateJob(pid);
            int durationMs = (int)_clock.GetElapsedTime(startedTs).TotalMilliseconds;
            var running = new RunningGame(game.Id, game.Title, pid, result.StartedAt, lease?.LeaseId, GameState.Running);
            await _tracker.TrackAsync(new GameLaunchRecord(game, effective, running, lease, injection, antiCheat, job, durationMs), cancellationToken).ConfigureAwait(false);
            job = null;
            injection = null;
            lease = null;

            await PublishAsync(new GameStateChanged(game.Id, game.Title, GameState.Running, _clock.UtcNow, pid), cancellationToken).ConfigureAwait(false);
            await ReportAsync(game, effective, result, durationMs, antiCheat, cancellationToken).ConfigureAwait(false);
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

        return await FailAsync(game, request, startedAt, startedTs, antiCheat, error, lease, injection, job, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Handles <c>games.kill</c>: by game, by pid, or everything when neither is given.</summary>
    public async Task<GamesKillResponse> KillAsync(GamesKillRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<GameLaunchRecord> targets = request switch
        {
            { Pid: { } pid } => _tracker.Find(pid) is { } one ? new[] { one } : throw IpcError.NotFound($"Running game with pid {pid}").ToException(),
            { GameId: { } gameId } => _tracker.FindByGame(gameId),
            _ => _tracker.All(),
        };
        if (request.GameId is { } id && request.Pid is null && targets.Count == 0)
        {
            throw IpcError.NotFound($"Running game {id}").ToException();
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
                else
                {
                    record.Job.Terminate();
                }

                killed.Add(pid);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ObjectDisposedException)
            {
                _logger.LogWarning(ex, "Kill of {Title} pid {Pid} failed", record.Game.Title, pid);
            }
        }

        return killed;
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
        await ReportAsync(game, request, result, (int)_clock.GetElapsedTime(startedTs).TotalMilliseconds, antiCheat, ct).ConfigureAwait(false);
        return result;
    }

    private async Task ReportAsync(Game game, LaunchRequest request, LaunchResult result, int durationMs, AntiCheatCheckResult antiCheat, CancellationToken cancellationToken)
    {
        var report = new LaunchReport(request.SessionId, request.UserId, result, durationMs, game.Launcher, antiCheat, LaunchReportPhase.Launch);
        try
        {
            await _server.SendLaunchReportAsync(game.Id, report, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ServerApiException or HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Launch report for {Title} not delivered", game.Title);
        }
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

}
