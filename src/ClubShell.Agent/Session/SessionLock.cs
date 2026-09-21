using System.Collections.Concurrent;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Users;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Security;
using Microsoft.Extensions.Options;
using PlaySession = ClubShell.Contracts.Sessions.Session;

namespace ClubShell.Agent.Session;

/// <summary>Source of the player's idle state (the Shell over IPC, or a hook-based detector in the interactive session).</summary>
public interface IIdleSignal
{
    /// <summary><see langword="true"/> when the player went idle past the configured threshold, <see langword="false"/> on activity.</summary>
    event EventHandler<bool>? IdleChanged;
}

/// <summary>Raised by <see cref="SessionLock.LockChanged"/>.</summary>
/// <param name="Locked">New lock state.</param>
/// <param name="Reason">Reason given when locking; <see langword="null"/> on unlock.</param>
/// <param name="At">Transition time.</param>
public sealed record SessionLockChanged(bool Locked, string? Reason, DateTimeOffset At);

/// <summary>
/// Lock screen state on top of <see cref="ISessionService"/>: lock with a reason, unlock after verifying the player's PIN
/// (local PBKDF2 cache) or password (server login when online, <see cref="OfflineSessionStore"/> otherwise), with
/// 5 failures/min rate limiting (IPC_PROTOCOL.md §7.2), plus auto-lock on idle when <c>session.autoLockOnIdleSec</c> &gt; 0.
/// Whether the timer keeps running while locked is decided by the session service (<see cref="SessionManager.PauseTimerOnLock"/>).
/// </summary>
public sealed class SessionLock : IDisposable
{
    /// <summary>Failed unlock attempts tolerated per minute before <c>rateLimited</c>.</summary>
    public const int MaxFailuresPerMinute = 5;

    /// <summary>Reason used for idle auto-locks.</summary>
    public const string IdleReason = "idle";

    private const int PinIterations = 50_000;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);

    private readonly ISessionService _sessions;
    private readonly IServerClient _server;
    private readonly OfflineSessionStore _store;
    private readonly Hwid _hwid;
    private readonly IClock _clock;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly ILogger<SessionLock> _logger;
    private readonly IIdleSignal? _idleSignal;
    private readonly ConcurrentDictionary<Guid, string> _pinHashes = new();
    private readonly Queue<long> _failures = new();
    private readonly object _failuresGate = new();
    private bool _disposed;

    /// <summary>Creates the lock and subscribes to session transitions and (optionally) the idle signal.</summary>
    public SessionLock(
        ISessionService sessions,
        IServerClient server,
        OfflineSessionStore store,
        Hwid hwid,
        IClock clock,
        IOptionsMonitor<AgentSettings> settings,
        ILogger<SessionLock> logger,
        IIdleSignal? idleSignal = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(hwid);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _sessions = sessions;
        _server = server;
        _store = store;
        _hwid = hwid;
        _clock = clock;
        _settings = settings;
        _logger = logger;
        _idleSignal = idleSignal;
        _sessions.Changed += OnSessionChanged;
        if (_idleSignal is not null)
        {
            _idleSignal.IdleChanged += OnIdleChanged;
        }
    }

    /// <summary>Raised after every lock/unlock, including ones initiated directly through the session service.</summary>
    public event EventHandler<SessionLockChanged>? LockChanged;

    /// <summary><see langword="true"/> while the session is locked.</summary>
    public bool IsLocked => _sessions.State == SessionState.Locked;

    /// <summary>Reason of the current lock, when known.</summary>
    public string? Reason { get; private set; }

    /// <summary>Time of the current lock.</summary>
    public DateTimeOffset? LockedAt { get; private set; }

    /// <summary>Idle auto-lock enabled (<c>session.autoLockOnIdleSec</c> &gt; 0).</summary>
    public bool AutoLockOnIdle => _settings.CurrentValue.Session.AutoLockOnIdleSec > 0;

    /// <summary>
    /// Caches a PBKDF2 hash of the user's PIN for <see cref="UnlockAsync"/> (call when the profile PIN is set or after a
    /// successful login that carries it). Digits only, 4–6 characters.
    /// </summary>
    public void RememberPin(Guid userId, string pin)
    {
        if (!IsValidPin(pin))
        {
            throw IpcError.Validation("pin", "must be 4-6 digits").ToException();
        }

        // ponytail: in-memory only; after an Agent restart the player unlocks with the password. Upgrade: persist in the users table.
        _pinHashes[userId] = OfflineSessionStore.HashPassword(pin, PinIterations);
    }

    /// <summary>Forgets the cached PIN of <paramref name="userId"/> (logout).</summary>
    public void ForgetPin(Guid userId) => _pinHashes.TryRemove(userId, out _);

    /// <summary>Locks the active session. Errors: <c>sessionNotActive</c>.</summary>
    public async Task<PlaySession> LockAsync(string? reason, CancellationToken cancellationToken)
    {
        Reason = reason;
        try
        {
            return await _sessions.LockAsync(reason, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!IsLocked)
            {
                Reason = null;
            }

            throw;
        }
    }

    /// <summary>
    /// Verifies <paramref name="request"/> (PIN or password) for <paramref name="user"/> and unlocks. A password check
    /// re-authenticates against the server when online (this refreshes the stored user tokens) and against the offline
    /// login cache otherwise. Errors: <c>sessionNotActive</c>, <c>forbidden</c> (other user), <c>validation</c>,
    /// <c>unauthorized</c>, <c>rateLimited</c>.
    /// </summary>
    public async Task<PlaySession> UnlockAsync(User user, SessionUnlockRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);
        var current = _sessions.Current;
        if (current is null || _sessions.State != SessionState.Locked)
        {
            throw IpcError.SessionNotActive().ToException();
        }

        if (current.UserId != user.Id)
        {
            throw IpcError.Forbidden("Session belongs to another user", "otherUser").ToException();
        }

        ThrowIfRateLimited();
        bool ok;
        if (!string.IsNullOrEmpty(request.Pin))
        {
            ok = VerifyPin(user.Id, request.Pin);
        }
        else if (!string.IsNullOrEmpty(request.Password))
        {
            ok = await VerifyPasswordAsync(user, request.Password, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw IpcError.Validation("pin", "pin or password is required").ToException();
        }

        if (!ok)
        {
            RecordFailure();
            _logger.LogWarning("Unlock refused for user {UserId}: wrong secret", user.Id);
            throw IpcError.Unauthorized("Wrong PIN or password", "badSecret").ToException();
        }

        ClearFailures();
        var session = await _sessions.UnlockAsync(cancellationToken).ConfigureAwait(false);
        Reason = null;
        return session;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessions.Changed -= OnSessionChanged;
        if (_idleSignal is not null)
        {
            _idleSignal.IdleChanged -= OnIdleChanged;
        }

        _pinHashes.Clear();
        GC.SuppressFinalize(this);
    }

    private static bool IsValidPin(string? pin) =>
        pin is { Length: >= 4 and <= 6 } && pin.All(char.IsAsciiDigit);

    private bool VerifyPin(Guid userId, string pin) =>
        IsValidPin(pin) && _pinHashes.TryGetValue(userId, out var hash) && OfflineSessionStore.VerifyPassword(hash, pin);

    private async Task<bool> VerifyPasswordAsync(User user, string password, CancellationToken cancellationToken)
    {
        if (_settings.CurrentValue.PcId is { } pcId)
        {
            try
            {
                var hwid = await _hwid.GetAsync(cancellationToken).ConfigureAwait(false);
                var response = await _server.LoginAsync(new AuthRequest(AuthKind.Password, user.Username, password, null, null, null, pcId, hwid), cancellationToken).ConfigureAwait(false);
                if (response.User.Id != user.Id)
                {
                    return false;
                }

                try
                {
                    await _store.CacheUserAsync(user.Username, response.OfflineHash, response.User, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not refresh the offline login cache for {Username}", user.Username);
                }

                return true;
            }
            catch (ServerApiException ex) when (ex.IsRetryable)
            {
                _logger.LogInformation("Server unreachable ({Code}); verifying password offline", ex.Code);
            }
            catch (ServerApiException ex)
            {
                _logger.LogInformation("Server refused password check for {Username}: {Code}", user.Username, ex.Code);
                return false;
            }
        }

        var cached = await _store.TryOfflineLoginAsync(user.Username, password, cancellationToken).ConfigureAwait(false);
        return cached is not null && cached.Id == user.Id;
    }

    private void ThrowIfRateLimited()
    {
        lock (_failuresGate)
        {
            Prune();
            if (_failures.Count < MaxFailuresPerMinute)
            {
                return;
            }

            var oldest = _failures.Peek();
            var retryAfter = FailureWindow - _clock.GetElapsedTime(oldest);
            throw IpcError.RateLimited((int)Math.Max(1, Math.Ceiling(retryAfter.TotalSeconds))).ToException();
        }
    }

    private void RecordFailure()
    {
        lock (_failuresGate)
        {
            Prune();
            _failures.Enqueue(_clock.GetTimestamp());
        }
    }

    private void ClearFailures()
    {
        lock (_failuresGate)
        {
            _failures.Clear();
        }
    }

    private void Prune()
    {
        while (_failures.Count > 0 && _clock.GetElapsedTime(_failures.Peek()) >= FailureWindow)
        {
            _failures.Dequeue();
        }
    }

    private void OnSessionChanged(object? sender, SessionEvent sessionEvent)
    {
        switch (sessionEvent.Type)
        {
            case SessionEventType.Locked:
                LockedAt = sessionEvent.At;
                Raise(new SessionLockChanged(true, Reason, sessionEvent.At));
                break;
            case SessionEventType.Unlocked:
            case SessionEventType.Ended:
                if (LockedAt is null)
                {
                    break;
                }

                LockedAt = null;
                Reason = null;
                Raise(new SessionLockChanged(false, null, sessionEvent.At));
                break;
            default:
                break;
        }
    }

    private void OnIdleChanged(object? sender, bool idle)
    {
        if (!idle || _disposed || !AutoLockOnIdle || _sessions.State != SessionState.Active)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await LockAsync(IdleReason, CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation("Session auto-locked on idle");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Idle auto-lock failed");
            }
        });
    }

    private void Raise(SessionLockChanged change)
    {
        try
        {
            LockChanged?.Invoke(this, change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LockChanged handler failed");
        }
    }
}
