using System.Collections.Concurrent;
using System.Runtime.Versioning;
using ClubShell.Agent.Games.Accounts;
using ClubShell.Agent.Games.Saves;
using ClubShell.Agent.Session;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Processes;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Games;

/// <summary>Everything the tracker needs to follow one launched game to its exit.</summary>
/// <param name="Game">Catalogue entry.</param>
/// <param name="Request">Executed launch request.</param>
/// <param name="Running">Process as reported to the Shell.</param>
/// <param name="Lease">Account-pool lease in use, when any.</param>
/// <param name="Injection">Credential injection to undo on exit, when any.</param>
/// <param name="AntiCheat">Pre-launch anti-cheat result (echoed in the exit report).</param>
/// <param name="Job">Job object the game process was assigned to (disposed on exit; kill-on-close).</param>
/// <param name="LaunchDurationMs">Launch latency.</param>
public sealed record GameLaunchRecord(
    Game Game,
    LaunchRequest Request,
    RunningGame Running,
    ActiveLease? Lease,
    InjectionResult? Injection,
    AntiCheatCheckResult AntiCheat,
    JobObject Job,
    int LaunchDurationMs);

/// <summary>Raised once a tracked game has exited and all cleanup ran.</summary>
/// <param name="Record">Launch record.</param>
/// <param name="FinalState"><see cref="GameState.Exited"/> or <see cref="GameState.Killed"/>.</param>
/// <param name="ExitCode">Process exit code (-1 when unknown).</param>
/// <param name="PlayedSec">Seconds between launch and exit.</param>
/// <param name="At">Exit time.</param>
public sealed record GameExitedEventArgs(GameLaunchRecord Record, GameState FinalState, int ExitCode, int PlayedSec, DateTimeOffset At);

/// <summary>
/// Tracks running games by pid: waits for process exit, publishes <c>game.stateChanged</c>, restores injected
/// credentials, uploads cloud saves, releases account leases, sends the <see cref="LaunchReportPhase.Exit"/> report
/// (through the offline outbox when the server is unreachable) and disposes the per-launch <see cref="JobObject"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GameSessionTracker : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(5);

    /// <summary>How long after a game process exits a successor (a launcher's hand-over) is looked for.</summary>
    private static readonly TimeSpan SuccessorWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SuccessorPoll = TimeSpan.FromMilliseconds(250);

    private readonly IServerClient _server;
    private readonly OfflineSessionStore _outbox;
    private readonly AccountPool _pool;
    private readonly AccountInjector _injector;
    private readonly CloudSaveSync _saves;
    private readonly PlayerSettingsSync? _playerSettings;
    private readonly IGameEventSink _events;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<GameSessionTracker> _logger;
    private readonly ConcurrentDictionary<int, TrackedGame> _games = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Creates the tracker.</summary>
    public GameSessionTracker(
        IServerClient server,
        OfflineSessionStore outbox,
        AccountPool pool,
        AccountInjector injector,
        CloudSaveSync saves,
        IGameEventSink events,
        IOptionsMonitor<AgentSettings> settings,
        IClock clock,
        ILogger<GameSessionTracker> logger,
        PlayerSettingsSync? playerSettings = null)
    {
        _playerSettings = playerSettings;
        _server = server;
        _outbox = outbox;
        _pool = pool;
        _injector = injector;
        _saves = saves;
        _events = events;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>A tracked game exited (after cleanup).</summary>
    public event EventHandler<GameExitedEventArgs>? Exited;

    /// <summary>Number of tracked games.</summary>
    public int Count => _games.Count;

    /// <summary>Running games as reported over IPC / heartbeat.</summary>
    public IReadOnlyList<RunningGame> ListRunning() => _games.Values.Select(t => t.Record.Running).OrderBy(r => r.StartedAt).ToList();

    /// <summary>All launch records currently tracked.</summary>
    public IReadOnlyList<GameLaunchRecord> All() => _games.Values.Select(t => t.Record).ToList();

    /// <summary>Record for <paramref name="pid"/>, or <see langword="null"/>.</summary>
    public GameLaunchRecord? Find(int pid) => _games.TryGetValue(pid, out TrackedGame? tracked) ? tracked.Record : null;

    /// <summary>Records of every running instance of <paramref name="gameId"/>.</summary>
    public IReadOnlyList<GameLaunchRecord> FindByGame(Guid gameId) => _games.Values.Where(t => t.Record.Game.Id == gameId).Select(t => t.Record).ToList();

    /// <summary>Starts following <paramref name="record"/>; returns once the watcher is attached (not when the game exits).</summary>
    public Task TrackAsync(GameLaunchRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_cts.IsCancellationRequested, this);
        var tracked = new TrackedGame(record);
        if (!_games.TryAdd(record.Running.Pid, tracked))
        {
            throw IpcError.Conflict($"Pid {record.Running.Pid} is already tracked").ToException();
        }

        tracked.Watch = Task.Run(() => WatchAsync(tracked), CancellationToken.None);
        _logger.LogInformation("Tracking {Title} pid {Pid} (session {SessionId}, lease {LeaseId})", record.Game.Title, record.Running.Pid, record.Request.SessionId, record.Lease?.LeaseId);
        return Task.CompletedTask;
    }

    /// <summary>Marks <paramref name="pid"/> as killed by the Agent so its exit is reported as <see cref="GameState.Killed"/>.</summary>
    public void MarkKilled(int pid, AccountLeaseReleaseReason releaseReason = AccountLeaseReleaseReason.Manual)
    {
        if (_games.TryGetValue(pid, out TrackedGame? tracked))
        {
            tracked.Killed = true;
            tracked.ReleaseReason = releaseReason;
        }
    }

    /// <summary>Completes when <paramref name="pid"/> is no longer tracked (exit processed) or immediately when unknown.</summary>
    public async Task WaitForExitAsync(int pid, CancellationToken cancellationToken)
    {
        if (!_games.TryGetValue(pid, out TrackedGame? tracked) || tracked.Watch is null)
        {
            return;
        }

        await tracked.Watch.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        Task[] watchers = _games.Values.Select(t => t.Watch ?? Task.CompletedTask).ToArray();
        try
        {
            await Task.WhenAll(watchers).WaitAsync(DisposeWait).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Tracker watchers did not finish within {Wait}", DisposeWait);
        }

        _cts.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private async Task WatchAsync(TrackedGame tracked)
    {
        int pid = tracked.Record.Running.Pid;
        CancellationToken ct = _cts.Token;
        int exitCode;
        while (true)
        {
            try
            {
                exitCode = await ProcessWatcher.WatchPidAsync(pid, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Agent shutting down: leave the game alone, keep leases for the next Agent instance to reconcile.
                return;
            }
            catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception)
            {
                _logger.LogDebug(ex, "Pid {Pid} already gone when the watcher attached", pid);
                exitCode = -1;
            }

            // A launcher that hands over and exits (Counter-Strike 1.6's cstrike.exe restarts itself): follow the
            // process that carries on, so the game stays "running", can be closed and is ended with the session.
            int? next;
            try
            {
                next = tracked.Killed ? null : await FindSuccessorAsync(tracked, pid, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (next is not { } successor || !_games.TryAdd(successor, tracked))
            {
                break;
            }

            _games.TryRemove(pid, out _);
            tracked.Record = tracked.Record with { Running = tracked.Record.Running with { Pid = successor } };
            _logger.LogInformation("{Title}: pid {Pid} exited and handed over to pid {Successor}; following it", tracked.Record.Game.Title, pid, successor);
            await PublishAsync(new GameStateChanged(tracked.Record.Game.Id, tracked.Record.Game.Title, GameState.Running, _clock.UtcNow, successor), ct).ConfigureAwait(false);
            pid = successor;
        }

        GameLaunchRecord record = tracked.Record;
        _games.TryRemove(pid, out _);
        DateTimeOffset now = _clock.UtcNow;
        int playedSec = (int)Math.Max(0, (now - record.Running.StartedAt).TotalSeconds);
        GameState state = tracked.Killed ? GameState.Killed : GameState.Exited;
        _logger.LogInformation("{Title} pid {Pid} {State} with exit code {ExitCode} after {PlayedSec}s", record.Game.Title, pid, state, exitCode, playedSec);

        await PublishAsync(new GameStateChanged(record.Game.Id, record.Game.Title, state, now, pid, exitCode), ct).ConfigureAwait(false);

        if (record.Injection is not null)
        {
            try
            {
                await _injector.RestoreAsync(record.Injection, record.Game.Launcher, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Credential restore failed for {Title}", record.Game.Title);
            }
        }

        if (record.Lease is { } lease)
        {
            CloudSaveUpload? upload = await _saves.UploadAsync(record.Game, lease, ct).ConfigureAwait(false);
            if (_settings.CurrentValue.Games.AccountPool.ReleaseOnExit || tracked.Killed)
            {
                await _pool.ReleaseAsync(lease.LeaseId, tracked.ReleaseReason ?? AccountLeaseReleaseReason.Exit, upload, ct).ConfigureAwait(false);
            }
        }

        var report = new LaunchReport(
            record.Request.SessionId,
            record.Request.UserId,
            LaunchResult.Success(pid, record.Running.StartedAt, record.Lease?.LeaseId),
            record.LaunchDurationMs,
            record.Game.Launcher,
            record.AntiCheat,
            LaunchReportPhase.Exit,
            exitCode,
            playedSec);
        await _outbox.SendLaunchReportAsync(_server, record.Game.Id, report, ct).ConfigureAwait(false);

        // After the account is back in the pool and the exit is reported: the settings upload must not hold up the
        // session-end cleanup, which only waits a few seconds for exit processing.
        if (_playerSettings is not null)
        {
            await _playerSettings.SaveAsync(record.Game, record.Request.UserId, ct).ConfigureAwait(false);
        }

        try
        {
            record.Job.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Job dispose failed for pid {Pid}", pid);
        }

        try
        {
            Exited?.Invoke(this, new GameExitedEventArgs(record, state, exitCode, playedSec, now));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GameSessionTracker.Exited handler threw");
        }
    }

    /// <summary>
    /// The process a game's exited launcher handed over to, looked for during <see cref="SuccessorWait"/> (real time):
    /// first what is left in the game's job (children inherit it), else a process with the game's image name in the
    /// same session, started since the launch and not tracked already.
    /// </summary>
    private async Task<int?> FindSuccessorAsync(TrackedGame tracked, int exitedPid, CancellationToken cancellationToken)
    {
        GameLaunchRecord record = tracked.Record;
        string? name = string.IsNullOrWhiteSpace(record.Game.ExePath) ? null : Path.GetFileNameWithoutExtension(record.Game.ExePath);
        long deadline = Environment.TickCount64 + (long)SuccessorWait.TotalMilliseconds;
        while (true)
        {
            if (Successor(record, exitedPid, name, tracked.SessionId) is { } found)
            {
                return found;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return null;
            }

            await Task.Delay(SuccessorPoll, cancellationToken).ConfigureAwait(false);
        }
    }

    private int? Successor(GameLaunchRecord record, int exitedPid, string? name, uint? sessionId)
    {
        try
        {
            IReadOnlyList<int> inJob = record.Job.QueryProcessIds().Where(id => id != exitedPid && !_games.ContainsKey(id)).ToList();
            if (inJob.Count > 0)
            {
                return inJob.FirstOrDefault(id => name is not null && ImageNameIs(id, name)) is var named and > 0 ? named : inJob[0];
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Job of {Title} not queryable", record.Game.Title);
        }

        if (name is null || sessionId is null)
        {
            return null;
        }

        DateTime launchedLocal = record.Running.StartedAt.LocalDateTime.AddSeconds(-5);
        foreach (System.Diagnostics.Process candidate in System.Diagnostics.Process.GetProcessesByName(name))
        {
            using (candidate)
            {
                try
                {
                    if (candidate.Id != exitedPid && !_games.ContainsKey(candidate.Id) && (uint)candidate.SessionId == sessionId && candidate.StartTime >= launchedLocal)
                    {
                        return candidate.Id;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Exited meanwhile or not queryable.
                }
            }
        }

        return null;
    }

    private static bool ImageNameIs(int pid, string name)
    {
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid);
            return string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
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

    private sealed class TrackedGame
    {
        public TrackedGame(GameLaunchRecord record)
        {
            Record = record;
            SessionId = ClubShell.Windows.Native.Kernel32.ProcessIdToSessionId((uint)record.Running.Pid, out uint session) ? session : null;
        }

        /// <summary>Re-pointed at the successor when a launcher hands over (see <c>FindSuccessorAsync</c>).</summary>
        public GameLaunchRecord Record { get; set; }

        /// <summary>WTS session of the launched process, for finding its successor.</summary>
        public uint? SessionId { get; }

        public Task? Watch { get; set; }

        public bool Killed { get; set; }

        public AccountLeaseReleaseReason? ReleaseReason { get; set; }
    }
}
