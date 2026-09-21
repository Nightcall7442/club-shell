using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Wallet;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Options;
using PlaySession = ClubShell.Contracts.Sessions.Session;

namespace ClubShell.Agent.Session;

/// <summary>
/// Receives every session transition for delivery to the Shell (IPC events <c>session.updated</c>, <c>session.warning</c>,
/// <c>session.ended</c>) or the server. Implemented by the IPC layer; all registrations are invoked in order and a failing
/// sink never affects the others or the session itself.
/// </summary>
public interface ISessionEventSink
{
    /// <summary>A transition happened (<see cref="SessionEvent.Type"/> tells which; warnings carry <see cref="SessionWarningData"/>).</summary>
    ValueTask PublishAsync(SessionEvent sessionEvent, CancellationToken cancellationToken);

    /// <summary>The session snapshot changed (maps to <c>session.updated</c>; never sent for plain 1 s ticks).</summary>
    ValueTask PublishSessionUpdatedAsync(PlaySession session, CancellationToken cancellationToken);

    /// <summary>The session ended and was settled (maps to <c>session.ended</c>).</summary>
    ValueTask PublishSessionEndedAsync(SessionEndedEvent ended, CancellationToken cancellationToken);
}

/// <summary>
/// Owner of the single play session of this PC (ARCHITECTURE.md §5.1): a state machine
/// <c>Idle → Starting → Active ⇄ Paused/Locked → Ending → Ended</c> behind one <see cref="SemaphoreSlim"/>, driven by
/// <see cref="SessionTimer"/>. Server calls fall back to <see cref="OfflineSessionStore"/> (local id, queued
/// <see cref="SessionEvent"/>s) when the server is unreachable, and <see cref="SyncWithServerAsync"/> reconciles once it is
/// back. Every transition is raised through <see cref="Changed"/>, <see cref="WatchAsync"/> and the registered
/// <see cref="ISessionEventSink"/>s.
/// </summary>
public sealed class SessionManager : ISessionService, IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan TariffRefreshInterval = TimeSpan.FromMinutes(15);

    private readonly IServerClient _server;
    private readonly OfflineSessionStore _store;
    private readonly SessionTimer _timer;
    private readonly IClock _clock;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IReadOnlyList<ISessionEventSink> _sinks;
    private readonly ILogger<SessionManager> _logger;
    private readonly SessionCleanup? _cleanup;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Channel<SessionEvent>> _watchers = new();

    private PlaySession? _session;
    private SessionState _state = SessionState.Idle;
    private SessionState _stateBeforeLock = SessionState.Active;
    private OfflineSessionCreate? _pendingCreate;
    private CancellationTokenSource? _graceCts;
    private bool _timerPausedByLock;
    private long _lastPersistAt;
    private int _lastChargedUsedSec;
    private IReadOnlyList<Tariff> _tariffs = [];
    private string? _tariffsEtag;
    private long _tariffsFetchedAt;
    private bool _tariffsFetched;
    private bool _disposed;

    /// <summary>Creates the manager and subscribes to <paramref name="timer"/>. Call <see cref="RestoreAsync"/> once at startup.</summary>
    public SessionManager(
        IServerClient server,
        OfflineSessionStore store,
        SessionTimer timer,
        IClock clock,
        IOptionsMonitor<AgentSettings> settings,
        IEnumerable<ISessionEventSink> sinks,
        ILogger<SessionManager> logger,
        SessionCleanup? cleanup = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timer);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sinks);
        ArgumentNullException.ThrowIfNull(logger);
        _server = server;
        _store = store;
        _timer = timer;
        _clock = clock;
        _settings = settings;
        _sinks = sinks.ToArray();
        _logger = logger;
        _cleanup = cleanup;
        _timer.Tick += OnTimerTick;
        _timer.Warning += OnTimerWarning;
        _timer.Expired += OnTimerExpired;
    }

    /// <inheritdoc />
    public event EventHandler<SessionEvent>? Changed;

    /// <inheritdoc />
    public PlaySession? Current => Snapshot();

    /// <inheritdoc />
    public SessionState State => _state;

    /// <inheritdoc />
    public TimeSpan TimeLeft => _timer.TimeLeft;

    /// <summary><see langword="true"/> while the current session exists only locally (created offline, not yet accepted by the server).</summary>
    public bool IsOfflineSession => _pendingCreate is not null;

    /// <summary>Pause the timer while locked (tariff/policy driven; default: keep counting, as IPC_PROTOCOL.md §7.2 prescribes).</summary>
    public bool PauseTimerOnLock { get; set; }

    /// <summary>Interval at which a <see cref="SessionEventType.Charged"/> event is recorded for postpaid sessions.</summary>
    public int PostpaidChargeIntervalMinutes { get; set; } = 5;

    /// <summary>Loads the persisted session (if any), restarts its timer and reconciles with the server. Safe to call when nothing is persisted.</summary>
    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is null)
            {
                var stored = await _store.LoadCurrentAsync(cancellationToken).ConfigureAwait(false);
                if (stored is not null)
                {
                    await RestoreCoreAsync(stored, cancellationToken).ConfigureAwait(false);
                }
            }

            await SyncCoreAsync(cancellationToken).ConfigureAwait(false);
            if (_session is not null && _state.IsTimerRunning() && !_session.IsOpenEnded && _timer.SecondsLeft <= 0 && _state != SessionState.Ending)
            {
                _logger.LogInformation("Session {SessionId} expired while the agent was down; ending", _session.Id);
                await EndCoreAsync(SessionEndReason.TimeUp, settleWithServer: true, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Replays an offline-created session to the server, adopts the server's view of the current session
    /// (<c>GET /sessions/current</c>) and flushes the outbox. Retryable server failures are swallowed (still offline).
    /// </summary>
    public async Task SyncWithServerAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SyncCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Applies a server-pushed session (WS <c>sessionUpdated</c>, heartbeat <c>session</c>): adopts, merges or ends the local session accordingly.</summary>
    public async Task ApplyServerSessionAsync(PlaySession serverSession, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serverSession);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReconcileCoreAsync(serverSession, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Payload of <c>session.timeLeft</c> (never fails; <c>state = idle</c> when no session).</summary>
    public SessionTimeLeftResponse GetTimeLeft()
    {
        var snapshot = Snapshot();
        return snapshot is null
            ? new SessionTimeLeftResponse(SessionState.Idle, 0, 0, _server.ServerNow)
            : new SessionTimeLeftResponse(snapshot.State, snapshot.SecondsLeft, snapshot.SecondsUsed, _server.ServerNow, snapshot.Id, snapshot.EndsAt);
    }

    /// <inheritdoc />
    public async Task<PlaySession> StartAsync(SessionCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state.IsOpen())
            {
                throw IpcError.SessionAlreadyActive().ToException();
            }

            _state = SessionState.Starting;
            PlaySession created;
            OfflineSessionCreate? pending = null;
            var key = Guid.NewGuid();
            try
            {
                try
                {
                    created = await _server.CreateSessionAsync(request, key, cancellationToken).ConfigureAwait(false);
                }
                catch (ServerApiException ex) when (ex.IsRetryable)
                {
                    _logger.LogWarning("Server unreachable ({Code}); creating session offline", ex.Code);
                    (created, pending) = await CreateOfflineAsync(request, key, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                _state = SessionState.Idle;
                throw;
            }

            await ActivateCoreAsync(created, pending, cancellationToken).ConfigureAwait(false);
            return Snapshot()!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PlaySession> PauseAsync(string? reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = RequireSession();
            if (_state == SessionState.Paused)
            {
                throw IpcError.Conflict("Session is already paused", "alreadyPaused").ToException();
            }

            if (_state != SessionState.Active)
            {
                throw IpcError.SessionNotActive().ToException();
            }

            var offline = _pendingCreate is not null;
            if (!offline)
            {
                try
                {
                    AdoptServerFields(await _server.PauseSessionAsync(session.Id, cancellationToken).ConfigureAwait(false));
                }
                catch (ServerApiException ex) when (ex.IsRetryable)
                {
                    _logger.LogWarning("Pause not confirmed by server ({Code}); applying locally", ex.Code);
                    offline = true;
                }
            }

            _timer.Pause();
            var now = _clock.UtcNow;
            _session = _session! with { State = SessionState.Paused, PausedAt = now };
            _state = SessionState.Paused;
            _logger.LogInformation("Session {SessionId} paused ({Reason})", session.Id, reason ?? "user");
            await PersistCoreAsync(cancellationToken).ConfigureAwait(false);
            await RecordAsync(SessionEvent.Of(session.Id, SessionEventType.Paused, now), offline, cancellationToken).ConfigureAwait(false);
            return Snapshot()!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PlaySession> ResumeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = RequireSession();
            if (_state != SessionState.Paused)
            {
                throw IpcError.Conflict("Session is not paused", "notPaused").ToException();
            }

            var offline = _pendingCreate is not null;
            if (!offline)
            {
                try
                {
                    AdoptServerFields(await _server.ResumeSessionAsync(session.Id, cancellationToken).ConfigureAwait(false));
                }
                catch (ServerApiException ex) when (ex.IsRetryable)
                {
                    _logger.LogWarning("Resume not confirmed by server ({Code}); applying locally", ex.Code);
                    offline = true;
                }
            }

            _timer.Resume();
            var now = _clock.UtcNow;
            _session = _session! with { State = SessionState.Active, PausedAt = null };
            _state = SessionState.Active;
            _logger.LogInformation("Session {SessionId} resumed", session.Id);
            await PersistCoreAsync(cancellationToken).ConfigureAwait(false);
            await RecordAsync(SessionEvent.Of(session.Id, SessionEventType.Resumed, now), offline, cancellationToken).ConfigureAwait(false);
            return Snapshot()!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SessionEndResult> EndAsync(SessionEndReason reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = RequireSession();
            return await EndCoreAsync(reason, settleWithServer: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PlaySession> ExtendAsync(int minutes, Guid? tariffId, CancellationToken cancellationToken)
    {
        if (minutes <= 0)
        {
            throw IpcError.Validation("minutes", "must be > 0").ToException();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = RequireSession();
            if (_state is not (SessionState.Active or SessionState.Paused or SessionState.Locked or SessionState.Ending))
            {
                throw IpcError.SessionNotActive().ToException();
            }

            var offline = _pendingCreate is not null;
            var costBefore = _session!.Cost;
            PlaySession? fromServer = null;
            if (!offline)
            {
                try
                {
                    fromServer = await _server.ExtendSessionAsync(session.Id, new SessionExtendRequest(minutes, tariffId), Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
                }
                catch (ServerApiException ex) when (ex.IsRetryable)
                {
                    _logger.LogWarning("Extend not confirmed by server ({Code}); applying locally", ex.Code);
                    offline = true;
                }
            }

            Money cost;
            if (fromServer is not null)
            {
                _timer.Extend(minutes * 60);
                AdoptServerFields(fromServer);
                cost = fromServer.Cost - costBefore;
            }
            else
            {
                if (session.IsOpenEnded)
                {
                    throw IpcError.Validation("minutes", "open-ended session cannot be extended offline").ToException();
                }

                var tariff = await FindTariffAsync(tariffId ?? session.TariffId, cancellationToken).ConfigureAwait(false);
                cost = tariff?.PriceFor(minutes) ?? Money.Zero;
                _timer.Extend(minutes * 60);
                _session = session with { Cost = session.Cost + cost, TariffId = tariffId ?? session.TariffId };
            }

            if (_state == SessionState.Ending)
            {
                CancelGrace();
                _state = SessionState.Active;
                _session = _session! with { State = SessionState.Active };
            }

            var now = _clock.UtcNow;
            _logger.LogInformation("Session {SessionId} extended by {Minutes} min (cost {Cost})", session.Id, minutes, cost);
            await PersistCoreAsync(cancellationToken).ConfigureAwait(false);
            await RecordAsync(SessionEvent.Extended(session.Id, now, minutes, cost), offline, cancellationToken).ConfigureAwait(false);
            return Snapshot()!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PlaySession> LockAsync(string? reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = RequireSession();
            if (_state == SessionState.Locked)
            {
                return Snapshot()!;
            }

            if (_state is not (SessionState.Active or SessionState.Paused))
            {
                throw IpcError.SessionNotActive().ToException();
            }

            _stateBeforeLock = _state;
            _timerPausedByLock = _state == SessionState.Active && PauseTimerOnLock;
            if (_timerPausedByLock)
            {
                _timer.Pause();
            }

            var now = _clock.UtcNow;
            _session = session with { State = SessionState.Locked, PausedAt = _timer.IsPaused ? (session.PausedAt ?? now) : null };
            _state = SessionState.Locked;
            _logger.LogInformation("Session {SessionId} locked ({Reason})", session.Id, reason ?? "user");
            await PersistCoreAsync(cancellationToken).ConfigureAwait(false);
            await RecordAsync(SessionEvent.Of(session.Id, SessionEventType.Locked, now), queue: true, cancellationToken).ConfigureAwait(false);
            return Snapshot()!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PlaySession> UnlockAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = RequireSession();
            if (_state != SessionState.Locked)
            {
                throw IpcError.Conflict("Session is not locked", "notLocked").ToException();
            }

            if (_timerPausedByLock)
            {
                _timer.Resume();
                _timerPausedByLock = false;
            }

            var restored = _stateBeforeLock == SessionState.Paused ? SessionState.Paused : SessionState.Active;
            if (_timer.IsExpired)
            {
                restored = SessionState.Ending;
            }

            var now = _clock.UtcNow;
            _session = session with { State = restored, PausedAt = restored == SessionState.Paused ? session.PausedAt ?? now : null };
            _state = restored;
            _logger.LogInformation("Session {SessionId} unlocked", session.Id);
            await PersistCoreAsync(cancellationToken).ConfigureAwait(false);
            await RecordAsync(SessionEvent.Of(session.Id, SessionEventType.Unlocked, now), queue: true, cancellationToken).ConfigureAwait(false);
            return Snapshot()!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SessionEvent> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<SessionEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var key = Guid.NewGuid();
        _watchers[key] = channel;
        try
        {
            await foreach (var sessionEvent in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return sessionEvent;
            }
        }
        finally
        {
            _watchers.TryRemove(key, out _);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Tick -= OnTimerTick;
        _timer.Warning -= OnTimerWarning;
        _timer.Expired -= OnTimerExpired;
        CancelGrace();
        foreach (var watcher in _watchers.Values)
        {
            watcher.Writer.TryComplete();
        }

        _watchers.Clear();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Core (caller holds the gate)

    private PlaySession RequireSession() =>
        _session is { } session && _state.IsOpen() ? session : throw IpcError.SessionNotActive().ToException();

    private PlaySession? Snapshot()
    {
        var session = _session;
        if (session is null)
        {
            return null;
        }

        var left = _timer.SecondsLeft;
        DateTimeOffset? endsAt;
        if (session.IsOpenEnded)
        {
            endsAt = null;
        }
        else if (_timer.IsRunning && !_timer.IsPaused)
        {
            endsAt = _clock.UtcNow + TimeSpan.FromSeconds(Math.Max(left, 0));
        }
        else
        {
            endsAt = session.EndsAt;
        }

        return session with
        {
            State = _state,
            SecondsLeft = session.IsOpenEnded ? PlaySession.OpenEnded : left,
            SecondsUsed = _timer.SecondsUsed,
            EndsAt = endsAt,
            WarningsSent = _timer.WarningsSent,
        };
    }

    private async Task<(PlaySession Session, OfflineSessionCreate Pending)> CreateOfflineAsync(SessionCreateRequest request, Guid key, CancellationToken cancellationToken)
    {
        var offline = _settings.CurrentValue.Offline;
        if (!offline.Enabled || !offline.AllowNewSessions)
        {
            throw IpcError.AgentOffline().ToException();
        }

        var tariff = await FindTariffAsync(request.TariffId, cancellationToken).ConfigureAwait(false);
        var minutes = request.Minutes ?? tariff?.PackageMinutes;
        if (request.Prepaid && minutes is null or <= 0)
        {
            throw IpcError.Validation("minutes", "required for a prepaid session").ToException();
        }

        var cap = Math.Max(1, offline.MaxOfflineMinutes);
        var budgetMinutes = Math.Min(minutes ?? cap, cap);
        var cost = request.Prepaid ? tariff?.PriceFor(budgetMinutes) ?? Money.Zero : Money.Zero;
        if (request.Prepaid && cost.IsPositive)
        {
            var cached = await _store.GetCachedUserAsync(request.UserId, cancellationToken).ConfigureAwait(false);
            if (cached is not null && cached.Balance.IsSameCurrency(cost) && cached.Balance < cost)
            {
                throw IpcError.InsufficientFunds(cost, cached.Balance).ToException();
            }
        }

        var now = _clock.UtcNow;
        var id = request.ClientSessionId ?? Guid.NewGuid();
        var session = new PlaySession(
            id,
            request.UserId,
            request.PcId,
            SessionState.Active,
            now,
            now + TimeSpan.FromMinutes(budgetMinutes),
            null,
            request.TariffId,
            budgetMinutes * 60,
            0,
            cost,
            request.Prepaid,
            []);
        var pending = new OfflineSessionCreate(request with { StartedAt = now, ClientSessionId = id, Minutes = budgetMinutes }, key);
        _logger.LogInformation("Offline session {SessionId} created for {Minutes} min (cap {Cap})", id, budgetMinutes, cap);
        return (session, pending);
    }

    private async Task ActivateCoreAsync(PlaySession session, OfflineSessionCreate? pending, CancellationToken cancellationToken)
    {
        CancelGrace();
        var state = session.State switch
        {
            SessionState.Paused => SessionState.Paused,
            SessionState.Locked => SessionState.Locked,
            _ => SessionState.Active,
        };
        _session = session with { State = state, PausedAt = state == SessionState.Active ? null : session.PausedAt ?? _clock.UtcNow };
        _pendingCreate = pending;
        _state = state;
        _stateBeforeLock = SessionState.Active;
        _timerPausedByLock = false;
        _timer.Start(session.SecondsLeft, session.IsOpenEnded, session.WarningsSent, Math.Max(0, session.SecondsUsed));
        if (state == SessionState.Paused)
        {
            _timer.Pause();
        }

        _lastChargedUsedSec = Math.Max(0, session.SecondsUsed);
        _logger.LogInformation("Session {SessionId} active for user {UserId}: {SecondsLeft}s left, prepaid={Prepaid}, offline={Offline}", session.Id, session.UserId, session.SecondsLeft, session.IsPrepaid, pending is not null);
        await PersistCoreAsync(cancellationToken).ConfigureAwait(false);
        await RecordAsync(SessionEvent.Of(session.Id, SessionEventType.Started, session.StartedAt), pending is not null, cancellationToken).ConfigureAwait(false);
    }

    private async Task RestoreCoreAsync(StoredSession stored, CancellationToken cancellationToken)
    {
        var session = stored.Session;
        var now = _clock.UtcNow;
        var gap = (int)Math.Max(0, (now - stored.UpdatedAt).TotalSeconds);
        var left = session.SecondsLeft;
        var used = session.SecondsUsed;
        var timerWasRunning = session.State.IsTimerRunning() && !(session.State == SessionState.Locked && session.PausedAt is not null);
        if (timerWasRunning)
        {
            if (session.IsOpenEnded)
            {
                used += gap;
            }
            else
            {
                var consumed = Math.Min(gap, Math.Max(0, left));
                left = Math.Max(0, left - gap);
                used += consumed;
            }
        }

        var state = session.State switch
        {
            SessionState.Paused => SessionState.Paused,
            SessionState.Locked => SessionState.Locked,
            _ => SessionState.Active,
        };
        _session = session with { State = state, SecondsLeft = session.IsOpenEnded ? PlaySession.OpenEnded : left, SecondsUsed = used };
        _pendingCreate = stored.PendingCreate;
        _state = state;
        _stateBeforeLock = SessionState.Active;
        _timerPausedByLock = state == SessionState.Locked && session.PausedAt is not null;
        _timer.Start(left, session.IsOpenEnded, session.WarningsSent, used);
        if (state == SessionState.Paused || _timerPausedByLock)
        {
            _timer.Pause();
        }

        _lastChargedUsedSec = used;
        _logger.LogInformation("Restored session {SessionId} ({State}) after {Gap}s: {SecondsLeft}s left, offline={Offline}", session.Id, state, gap, left, _pendingCreate is not null);
        await PersistCoreAsync(cancellationToken).ConfigureAwait(false);
        await PublishUpdatedAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SyncCoreAsync(CancellationToken cancellationToken)
    {
        var pcId = _settings.CurrentValue.PcId;
        if (pcId is null)
        {
            return;
        }

        if (_session is not null && _pendingCreate is { } pending)
        {
            try
            {
                var created = await _server.CreateSessionAsync(pending.Request, pending.IdempotencyKey, cancellationToken).ConfigureAwait(false);
                await AdoptCreatedAsync(created, cancellationToken).ConfigureAwait(false);
            }
            catch (ServerApiException ex) when (ex.IsRetryable)
            {
                _logger.LogDebug("Offline session replay deferred: {Code}", ex.Code);
                return;
            }
            catch (ServerApiException ex)
            {
                _logger.LogError("Server rejected offline session {SessionId}: {Code} ({Message}); ending locally", _session.Id, ex.Code, ex.Message);
                await EndCoreAsync(SessionEndReason.Error, settleWithServer: false, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        PlaySession? remote;
        try
        {
            remote = await _server.GetCurrentSessionAsync(pcId.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (ServerApiException ex) when (ex.IsRetryable)
        {
            _logger.LogDebug("Session sync deferred: {Code}", ex.Code);
            return;
        }

        await ReconcileCoreAsync(remote, cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.FlushAsync(_server, cancellationToken).ConfigureAwait(false);
        }
        catch (ServerApiException ex)
        {
            _logger.LogWarning("Outbox flush failed: {Code}", ex.Code);
        }
    }

    private async Task AdoptCreatedAsync(PlaySession created, CancellationToken cancellationToken)
    {
        var local = _session!;
        if (created.Id != local.Id)
        {
            await _store.RemapSessionAsync(local.Id, created.Id, cancellationToken).ConfigureAwait(false);
            _session = local with { Id = created.Id };
        }

        _pendingCreate = null;
        if (created.State is SessionState.Ended or SessionState.Idle)
        {
            _logger.LogWarning("Server reports offline session {SessionId} as already ended", created.Id);
            await EndCoreAsync(SessionEndReason.Admin, settleWithServer: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        AdoptServerFields(created);
        _logger.LogInformation("Offline session accepted by server as {SessionId}", created.Id);
        await PersistCoreAsync(cancellationToken).ConfigureAwait(false);
        await PublishUpdatedAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileCoreAsync(PlaySession? remote, CancellationToken cancellationToken)
    {
        if (_pendingCreate is not null)
        {
            // Still offline-only; the server view is irrelevant until the create is replayed.
            return;
        }

        if (remote is null || !remote.State.IsOpen())
        {
            if (_session is not null && (remote is null || remote.Id == _session.Id))
            {
                _logger.LogInformation("Server has no open session {SessionId} for this PC; ending locally", _session.Id);
                await EndCoreAsync(SessionEndReason.Admin, settleWithServer: false, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (_session is null)
        {
            _logger.LogInformation("Adopting server session {SessionId}", remote.Id);
            await ActivateCoreAsync(remote, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (remote.Id != _session.Id)
        {
            _logger.LogWarning("Server session {RemoteId} differs from local {LocalId}; server wins", remote.Id, _session.Id);
            await EndCoreAsync(SessionEndReason.AgentRestart, settleWithServer: false, cancellationToken).ConfigureAwait(false);
            await ActivateCoreAsync(remote, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        AdoptServerFields(remote);
        var now = _clock.UtcNow;
        if (remote.State == SessionState.Paused && _state == SessionState.Active)
        {
            _timer.Pause();
            _session = _session with { State = SessionState.Paused, PausedAt = remote.PausedAt ?? now };
            _state = SessionState.Paused;
            await RecordAsync(SessionEvent.Of(_session.Id, SessionEventType.Paused, now), queue: false, cancellationToken).ConfigureAwait(false);
        }
        else if (remote.State is (SessionState.Active or SessionState.Ending) && _state == SessionState.Paused)
        {
            _timer.Resume();
            _session = _session with { State = SessionState.Active, PausedAt = null };
            _state = SessionState.Active;
            await RecordAsync(SessionEvent.Of(_session.Id, SessionEventType.Resumed, now), queue: false, cancellationToken).ConfigureAwait(false);
        }

        if (_state == SessionState.Ending && !_timer.IsExpired)
        {
            CancelGrace();
            _state = SessionState.Active;
            _session = _session with { State = SessionState.Active };
        }

        await PersistCoreAsync(cancellationToken).ConfigureAwait(false);
        await PublishUpdatedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes billing fields and the server's remaining time (drift correction) for the current session.</summary>
    private void AdoptServerFields(PlaySession fromServer)
    {
        var local = _session!;
        if (fromServer.IsOpenEnded != local.IsOpenEnded)
        {
            _timer.Start(fromServer.SecondsLeft, fromServer.IsOpenEnded, fromServer.WarningsSent, Math.Max(_timer.SecondsUsed, fromServer.SecondsUsed));
            if (_state == SessionState.Paused || _timerPausedByLock)
            {
                _timer.Pause();
            }
        }
        else if (!fromServer.IsOpenEnded)
        {
            _timer.Resync(fromServer.SecondsLeft, _clock.UtcNow);
        }

        _session = local with
        {
            UserId = fromServer.UserId,
            TariffId = fromServer.TariffId,
            IsPrepaid = fromServer.IsPrepaid,
            Cost = fromServer.Cost,
            SecondsLeft = fromServer.SecondsLeft,
            StartedAt = fromServer.StartedAt,
        };
    }

    private async Task<SessionEndResult> EndCoreAsync(SessionEndReason reason, bool settleWithServer, CancellationToken cancellationToken)
    {
        CancelGrace();
        _state = SessionState.Ending;
        if (settleWithServer && _pendingCreate is { } pending)
        {
            // Last chance to get an offline-created session onto the server before it is settled.
            try
            {
                var created = await _server.CreateSessionAsync(pending.Request, pending.IdempotencyKey, cancellationToken).ConfigureAwait(false);
                var current = _session!;
                if (created.Id != current.Id)
                {
                    await _store.RemapSessionAsync(current.Id, created.Id, cancellationToken).ConfigureAwait(false);
                    _session = current with { Id = created.Id };
                }

                _pendingCreate = null;
            }
            catch (ServerApiException ex)
            {
                // ponytail: an offline-created session that ends before the server is back is reported only through the
                // outbox events (which will dead-letter with 404); upgrade: persist pending creates in their own table.
                _logger.LogWarning("Offline session {SessionId} could not be replayed before ending: {Code}", _session!.Id, ex.Code);
            }
        }

        var session = _session!;
        var now = _clock.UtcNow;
        var used = _timer.SecondsUsed;
        var local = new SessionEndResult(
            Snapshot()! with { State = SessionState.Ended, PausedAt = null },
            session.IsPrepaid ? Money.Zero with { Currency = session.Cost.Currency } : session.Cost,
            Money.Zero with { Currency = session.Cost.Currency });
        var result = local;
        var wasPending = _pendingCreate is not null;
        var offline = wasPending || !settleWithServer;
        if (!offline)
        {
            try
            {
                result = await _server.EndSessionAsync(session.Id, new SessionEndReport(reason, used, now), cancellationToken).ConfigureAwait(false);
            }
            catch (ServerApiException ex) when (ex.Code == ErrorCode.SessionNotActive)
            {
                _logger.LogInformation("Session {SessionId} already ended server-side", session.Id);
            }
            catch (ServerApiException ex) when (ex.IsRetryable)
            {
                _logger.LogWarning("End not confirmed by server ({Code}); settling offline", ex.Code);
                offline = true;
            }
        }

        _timer.Stop();
        var final = result.Session with { State = SessionState.Ended, SecondsUsed = Math.Max(result.Session.SecondsUsed, used), PausedAt = null };
        result = result with { Session = final };
        _session = null;
        _pendingCreate = null;
        _timerPausedByLock = false;
        _state = SessionState.Idle;
        _logger.LogInformation("Session {SessionId} ended ({Reason}): used={SecondsUsed}s charged={Charged} refunded={Refunded} offline={Offline}", final.Id, reason, final.SecondsUsed, result.Charged, result.Refunded, offline);

        await _store.ClearSessionAsync(cancellationToken).ConfigureAwait(false);
        var ended = SessionEvent.Ended(final.Id, now, reason);
        if (offline && !wasPending && settleWithServer)
        {
            await EnqueueAsync(ended, cancellationToken).ConfigureAwait(false);
        }

        Raise(ended);
        foreach (var sink in _sinks)
        {
            try
            {
                await sink.PublishAsync(ended, cancellationToken).ConfigureAwait(false);
                await sink.PublishSessionEndedAsync(new SessionEndedEvent(final, reason, result.Charged), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Session sink {Sink} failed on ended", sink.GetType().Name);
            }
        }

        if (_cleanup is not null)
        {
            try
            {
                var report = await _cleanup.RunAsync(reason, cancellationToken).ConfigureAwait(false);
                if (!report.Ok)
                {
                    _logger.LogWarning("Session cleanup finished with {Errors} error(s) in {Duration}", report.Errors.Count, report.Duration);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Session cleanup failed");
            }
        }

        return result;
    }

    private Task PersistCoreAsync(CancellationToken cancellationToken)
    {
        _lastPersistAt = _clock.GetTimestamp();
        var snapshot = Snapshot();
        return snapshot is null ? Task.CompletedTask : _store.SaveSessionAsync(snapshot, _pendingCreate, cancellationToken);
    }

    private async Task<Tariff?> FindTariffAsync(Guid tariffId, CancellationToken cancellationToken)
    {
        var known = _tariffs.FirstOrDefault(t => t.Id == tariffId);
        if (known is not null)
        {
            return known;
        }

        if (_tariffsFetched && _clock.GetElapsedTime(_tariffsFetchedAt) < TariffRefreshInterval)
        {
            return null;
        }

        try
        {
            var response = await _server.GetTariffsAsync(_settings.CurrentValue.Zone, _tariffsEtag, cancellationToken).ConfigureAwait(false);
            _tariffsFetched = true;
            _tariffsFetchedAt = _clock.GetTimestamp();
            if (!response.NotModified && response.Value is not null)
            {
                _tariffs = response.Value.Items;
                _tariffsEtag = response.ETag;
            }
        }
        catch (ServerApiException ex)
        {
            _logger.LogDebug("Tariff lookup failed: {Code}", ex.Code);
            _tariffsFetched = true;
            _tariffsFetchedAt = _clock.GetTimestamp();
        }

        return _tariffs.FirstOrDefault(t => t.Id == tariffId);
    }

    #endregion

    #region Publishing

    private async Task RecordAsync(SessionEvent sessionEvent, bool queue, CancellationToken cancellationToken)
    {
        if (queue)
        {
            await EnqueueAsync(sessionEvent, cancellationToken).ConfigureAwait(false);
        }

        Raise(sessionEvent);
        var snapshot = Snapshot();
        foreach (var sink in _sinks)
        {
            try
            {
                await sink.PublishAsync(sessionEvent, cancellationToken).ConfigureAwait(false);
                if (snapshot is not null)
                {
                    await sink.PublishSessionUpdatedAsync(snapshot, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Session sink {Sink} failed on {EventType}", sink.GetType().Name, sessionEvent.Type);
            }
        }
    }

    private async Task PublishUpdatedAsync(CancellationToken cancellationToken)
    {
        var snapshot = Snapshot();
        if (snapshot is null)
        {
            return;
        }

        foreach (var sink in _sinks)
        {
            try
            {
                await sink.PublishSessionUpdatedAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Session sink {Sink} failed on update", sink.GetType().Name);
            }
        }
    }

    private async Task EnqueueAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        try
        {
            await _store.EnqueueEventAsync(sessionEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to queue {EventType} for session {SessionId}", sessionEvent.Type, sessionEvent.SessionId);
        }
    }

    private void Raise(SessionEvent sessionEvent)
    {
        try
        {
            Changed?.Invoke(this, sessionEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session Changed handler failed");
        }

        foreach (var watcher in _watchers.Values)
        {
            watcher.Writer.TryWrite(sessionEvent);
        }
    }

    #endregion

    #region Timer callbacks (run outside the gate; take it themselves)

    private void OnTimerTick(object? sender, SessionTick tick)
    {
        if (_disposed || _session is null)
        {
            return;
        }

        var persistDue = _clock.GetElapsedTime(_lastPersistAt) >= TimeSpan.FromSeconds(Math.Max(1, _settings.CurrentValue.Session.PersistIntervalSec));
        var chargeDue = !_session.IsPrepaid
            && PostpaidChargeIntervalMinutes > 0
            && _state.IsTimerRunning()
            && tick.SecondsUsed - _lastChargedUsedSec >= PostpaidChargeIntervalMinutes * 60;
        if (!persistDue && !chargeDue)
        {
            return;
        }

        Fire(() => TickCoreAsync(chargeDue));
    }

    private async Task TickCoreAsync(bool chargeDue)
    {
        // A mutation in flight persists on its own; skip rather than queue behind it.
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (_session is null)
            {
                return;
            }

            if (chargeDue && !_session.IsPrepaid && _state.IsTimerRunning())
            {
                var used = _timer.SecondsUsed;
                var minutes = PostpaidChargeIntervalMinutes;
                var intervals = (used - _lastChargedUsedSec) / (minutes * 60);
                if (intervals > 0)
                {
                    var tariff = await FindTariffAsync(_session.TariffId, CancellationToken.None).ConfigureAwait(false);
                    _lastChargedUsedSec += intervals * minutes * 60;
                    if (tariff is not null)
                    {
                        var amount = tariff.PriceFor(intervals * minutes);
                        _session = _session with { Cost = _session.Cost + amount };
                        await RecordAsync(SessionEvent.Charged(_session.Id, _clock.UtcNow, amount), queue: true, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }

            await PersistCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnTimerWarning(object? sender, int minutesLeft)
    {
        Fire(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_session is null || !_state.IsTimerRunning())
                {
                    return;
                }

                _logger.LogInformation("Session {SessionId}: {MinutesLeft} min left", _session.Id, minutesLeft);
                await PersistCoreAsync(CancellationToken.None).ConfigureAwait(false);
                await RecordAsync(SessionEvent.Warning(_session.Id, _clock.UtcNow, minutesLeft), queue: true, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    private void OnTimerExpired(object? sender, EventArgs e)
    {
        Fire(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_session is null || _session.IsOpenEnded || !_state.IsTimerRunning() || _state == SessionState.Ending)
                {
                    return;
                }

                var grace = TimeSpan.FromSeconds(Math.Max(0, _settings.CurrentValue.Session.GraceSec));
                _logger.LogInformation("Session {SessionId} time is up; grace {Grace}", _session.Id, grace);
                if (_state == SessionState.Locked)
                {
                    _stateBeforeLock = SessionState.Active;
                }

                _state = SessionState.Ending;
                _session = _session with { State = SessionState.Ending };
                await PersistCoreAsync(CancellationToken.None).ConfigureAwait(false);
                await RecordAsync(SessionEvent.Warning(_session.Id, _clock.UtcNow, 0), queue: true, CancellationToken.None).ConfigureAwait(false);
                CancelGrace();
                _graceCts = new CancellationTokenSource();
                _ = GraceThenEndAsync(grace, _graceCts.Token);
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    private async Task GraceThenEndAsync(TimeSpan grace, CancellationToken cancellationToken)
    {
        try
        {
            await _clock.Delay(grace, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_session is null || _state != SessionState.Ending || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await EndCoreAsync(SessionEndReason.TimeUp, settleWithServer: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Ending expired session failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void CancelGrace()
    {
        var cts = _graceCts;
        _graceCts = null;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already gone.
        }

        cts.Dispose();
    }

    private void Fire(Func<Task> work)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Disposed while a callback was in flight.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session timer callback failed");
            }
        });
    }

    #endregion
}
