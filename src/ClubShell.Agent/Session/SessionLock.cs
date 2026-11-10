using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Agent.Ipc;
using ClubShell.Contracts.Errors;
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

/// <summary>Who locked the session, which decides what may unlock it. A stronger origin replaces a weaker one on a locked session.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionLockOrigin>))]
public enum SessionLockOrigin
{
    /// <summary>The player (or the idle auto-lock): their PIN or password unlocks (<c>session.unlock</c>).</summary>
    Player,

    /// <summary>
    /// The player's sign-in ended while the session runs (player token refused, sign-in revoked): signing in again as the
    /// same player unlocks (<see cref="SessionLock.ResumeAfterSignInAsync"/>).
    /// </summary>
    SignIn,

    /// <summary>The cash desk (server command <c>lock</c>): only the server's <c>unlock</c> unlocks; <c>session.unlock</c> answers <c>forbidden {reason: staffLock}</c>.</summary>
    Staff,
}

/// <summary>
/// Lock screen state on top of <see cref="ISessionService"/>: lock with a reason and an origin (<see cref="SessionLockOrigin"/>),
/// unlock after verifying the player's PIN (the server's <c>POST /users/{id}/pin/verify</c> online, a cached salted hash of
/// the last PIN verified or set on this PC offline) or password (server login when online, <see cref="OfflineSessionStore"/>
/// otherwise), with 5 failures/min rate limiting (IPC_PROTOCOL.md §7.2), plus auto-lock on idle when
/// <c>session.autoLockOnIdleSec</c> &gt; 0. A lock from the cash desk is unlocked only by the cash desk, and the origin
/// survives an Agent restart (<c>cache\session-lock.json</c>). Whether the timer keeps running while locked is decided by the
/// session service (<see cref="SessionManager.PauseTimerOnLock"/>).
/// </summary>
public sealed class SessionLock : IDisposable
{
    /// <summary>Failed unlock attempts tolerated per minute before <c>rateLimited</c>.</summary>
    public const int MaxFailuresPerMinute = 5;

    /// <summary>Reason used for idle auto-locks.</summary>
    public const string IdleReason = "idle";

    /// <summary>Reason used when the player's sign-in ended while the session runs.</summary>
    public const string SignInReason = "tokenExpired";

    /// <summary><c>User.Flags</c> entry the server sets when the player has an unlock PIN.</summary>
    public const string PinSetFlag = "pinSet";

    private const int PinIterations = 50_000;
    private const string OriginFileName = "session-lock.json";
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);

    private readonly ISessionService _sessions;
    private readonly IServerClient _server;
    private readonly OfflineSessionStore _store;
    private readonly Hwid _hwid;
    private readonly IClock _clock;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly ILogger<SessionLock> _logger;
    private readonly IIdleSignal? _idleSignal;
    private readonly ITokenStore? _tokens;
    private readonly ISessionEventSink? _sessionEvents;
    private readonly ShellUserContext? _users;
    private readonly Queue<long> _failures = new();
    private readonly object _failuresGate = new();
    private readonly object _originGate = new();
    private LockOriginRecord? _origin;
    private bool _originLoaded;
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
        IIdleSignal? idleSignal = null,
        ITokenStore? tokens = null,
        ISessionEventSink? sessionEvents = null,
        ShellUserContext? users = null)
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
        _tokens = tokens;
        _sessionEvents = sessionEvents;
        _users = users;
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

    /// <summary>Who locked the current session (<see cref="SessionLockOrigin.Player"/> when not recorded), or <see langword="null"/> when it is not locked.</summary>
    public SessionLockOrigin? Origin
    {
        get
        {
            if (!IsLocked || _sessions.Current is not { } session)
            {
                return null;
            }

            lock (_originGate)
            {
                LoadOrigin();
                return _origin is { } record && record.SessionId == session.Id ? record.Origin : SessionLockOrigin.Player;
            }
        }
    }

    /// <summary><see langword="true"/> while the session is locked from the cash desk (<c>auth.status.lockedByStaff</c>).</summary>
    public bool IsLockedByStaff => Origin == SessionLockOrigin.Staff;

    /// <summary>Reason of the current lock, when known.</summary>
    public string? Reason { get; private set; }

    /// <summary>Time of the current lock.</summary>
    public DateTimeOffset? LockedAt { get; private set; }

    /// <summary>Idle auto-lock enabled (<c>session.autoLockOnIdleSec</c> &gt; 0).</summary>
    public bool AutoLockOnIdle => _settings.CurrentValue.Session.AutoLockOnIdleSec > 0;

    /// <summary>Hash of a valid PIN for the cache (4–6 digits; <c>validation</c> otherwise).</summary>
    public static string HashPin(string pin) =>
        IsValidPin(pin)
            ? OfflineSessionStore.HashPassword(pin, PinIterations)
            : throw IpcError.Validation("pin", "format", "PIN must be 4-6 digits").ToException();

    /// <summary>
    /// Keeps a salted hash of <paramref name="pin"/> for <paramref name="userId"/> in the offline store, for an unlock while
    /// the server is unreachable (call when the profile PIN is set; a PIN the server verified is cached by the unlock).
    /// Only the hash is stored. Digits only, 4–6 characters.
    /// </summary>
    public Task RememberPinAsync(Guid userId, string pin, CancellationToken cancellationToken) =>
        _store.SavePinHashAsync(userId, HashPin(pin), cancellationToken);

    /// <summary><see langword="true"/> when a PIN of <paramref name="userId"/> is cached on this PC.</summary>
    public async Task<bool> HasCachedPinAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            return await _store.GetPinHashAsync(userId, cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Cached PIN of {UserId} not read", userId);
            return false;
        }
    }

    /// <summary>Locks the active session for the player (or idle). A guest cannot lock: <c>forbidden {reason: guestLock}</c>. Errors: <c>sessionNotActive</c>.</summary>
    public Task<PlaySession> LockAsync(string? reason, CancellationToken cancellationToken)
    {
        if (OwnerIsGuest())
        {
            throw IpcError.Forbidden("A guest has no PIN or password to unlock with", "guestLock").ToException();
        }

        return LockCoreAsync(reason, SessionLockOrigin.Player, cancellationToken);
    }

    /// <summary>Locks the session from the cash desk (server command <c>lock</c>); a lock already in place becomes a staff lock.</summary>
    public Task<PlaySession> LockByStaffAsync(string? reason, CancellationToken cancellationToken) =>
        LockCoreAsync(reason, SessionLockOrigin.Staff, cancellationToken);

    /// <summary>
    /// Locks the session because the player's sign-in ended (<see cref="SessionLockOrigin.SignIn"/>); a staff lock stays a
    /// staff lock. No-op (returns <see langword="null"/>) when no session is open.
    /// </summary>
    public async Task<PlaySession?> LockForSignInAsync(string? reason, CancellationToken cancellationToken)
    {
        if (!_sessions.State.IsOpen() || _sessions.Current is null)
        {
            return null;
        }

        try
        {
            return await LockCoreAsync(reason ?? SignInReason, SessionLockOrigin.SignIn, cancellationToken).ConfigureAwait(false);
        }
        catch (IpcException ex) when (ex.Code == ErrorCode.SessionNotActive)
        {
            // Starting or ending: nothing to protect.
            return null;
        }
    }

    /// <summary>
    /// After <paramref name="user"/> signed in again: a session of theirs locked because their sign-in ended is unlocked
    /// and goes on. <see langword="true"/> when it was unlocked.
    /// </summary>
    public async Task<bool> ResumeAfterSignInAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (Origin != SessionLockOrigin.SignIn || _sessions.Current is not { } session || session.UserId != user.Id)
        {
            return false;
        }

        await _sessions.UnlockAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Session {SessionId} unlocked: {UserId} signed in again", session.Id, user.Id);
        return true;
    }

    /// <summary>Unlocks from the cash desk (server command <c>unlock</c>), whatever locked the session. No-op when it is not locked.</summary>
    public async Task<PlaySession?> UnlockByStaffAsync(CancellationToken cancellationToken)
    {
        if (!IsLocked)
        {
            return _sessions.Current;
        }

        return await _sessions.UnlockAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies <paramref name="request"/> (PIN or password) for <paramref name="user"/> and unlocks. Online
    /// (<paramref name="online"/>) a PIN is checked by the server and a password by a server login (which refreshes the
    /// stored user tokens); offline against the PIN cached on this PC and the offline login cache. Errors:
    /// <c>sessionNotActive</c>, <c>forbidden</c> (<c>otherUser</c>, <c>staffLock</c>), <c>validation</c>,
    /// <c>unauthorized</c> (<c>badSecret</c> with <c>attemptsLeft</c> when the server counts, <c>noPin</c>), <c>rateLimited</c>.
    /// </summary>
    public async Task<PlaySession> UnlockAsync(User user, SessionUnlockRequest request, bool online, CancellationToken cancellationToken)
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

        if (IsLockedByStaff)
        {
            throw IpcError.Forbidden("Locked from the cash desk: only the cash desk unlocks", "staffLock").ToException();
        }

        ThrowIfRateLimited();
        if (!string.IsNullOrEmpty(request.Pin))
        {
            await VerifyPinAsync(user, request.Pin, online, cancellationToken).ConfigureAwait(false);
        }
        else if (!string.IsNullOrEmpty(request.Password))
        {
            await VerifyPasswordAsync(user, request.Password, online, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw IpcError.Validation("pin", "pin or password is required").ToException();
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

        GC.SuppressFinalize(this);
    }

    private static bool IsValidPin(string? pin) =>
        pin is { Length: >= 4 and <= 6 } && pin.All(char.IsAsciiDigit);

    private static SessionLockOrigin Stronger(SessionLockOrigin a, SessionLockOrigin b) => (int)a >= (int)b ? a : b;

    /// <summary>The open session belongs to a guest (the signed-in user is that guest).</summary>
    private bool OwnerIsGuest() =>
        _users?.User is { Role: UserRole.Guest } user && _sessions.Current is { } session && session.UserId == user.Id;

    private async Task<PlaySession> LockCoreAsync(string? reason, SessionLockOrigin origin, CancellationToken cancellationToken)
    {
        var session = _sessions.Current;
        var wasLocked = IsLocked;
        var previous = Origin;
        var effective = wasLocked && previous is { } held ? Stronger(held, origin) : origin;
        var previousReason = Reason;
        var upgraded = wasLocked && effective != previous;
        if (session is not null && (!wasLocked || upgraded))
        {
            // Recorded before the lock, so an auth.status read on the session.updated of that lock already sees it.
            SetOrigin(session.Id, effective, reason);
            Reason = reason;
        }

        PlaySession snapshot;
        try
        {
            snapshot = await _sessions.LockAsync(reason, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!IsLocked)
            {
                ClearOrigin();
                Reason = null;
            }
            else
            {
                Reason = previousReason;
            }

            throw;
        }

        if (upgraded)
        {
            _logger.LogInformation("Session {SessionId} lock is now {Origin} ({Reason})", snapshot.Id, effective, reason ?? "-");
            await PublishUpdatedAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

        return snapshot;
    }

    private async Task PublishUpdatedAsync(PlaySession snapshot, CancellationToken cancellationToken)
    {
        if (_sessionEvents is null)
        {
            return;
        }

        try
        {
            await _sessionEvents.PublishSessionUpdatedAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "session.updated after the lock change was not published");
        }
    }

    private async Task VerifyPinAsync(User user, string pin, bool online, CancellationToken cancellationToken)
    {
        if (!IsValidPin(pin))
        {
            throw IpcError.Validation("pin", "format", "PIN must be 4-6 digits").ToException();
        }

        if (online)
        {
            try
            {
                await _server.VerifyPinAsync(user.Id, new PinVerifyRequest(pin), cancellationToken).ConfigureAwait(false);
                await CachePinAsync(user.Id, pin).ConfigureAwait(false);
                return;
            }
            catch (ServerApiException ex) when (ex.Code == ErrorCode.Unauthorized && string.Equals(ex.Reason, "badSecret", StringComparison.Ordinal))
            {
                RecordFailure();
                _logger.LogWarning("Unlock refused for user {UserId}: wrong PIN (server)", user.Id);
                throw IpcDetails.Error(ErrorCode.Unauthorized, "Wrong PIN", "badSecret", ("attemptsLeft", IpcDetails.IntOf(ex.Error?.Details, "attemptsLeft")));
            }
            catch (ServerApiException ex) when (ex.Code == ErrorCode.NotFound)
            {
                // The server holds no PIN for the player: a hash cached here is stale.
                await ForgetPinAsync(user.Id).ConfigureAwait(false);
                throw IpcError.Unauthorized("No unlock PIN is set; use the password", "noPin").ToException();
            }
            catch (ServerApiException ex) when (ex.IsRetryable && ex.Code != ErrorCode.RateLimited)
            {
                _logger.LogInformation("Server unreachable ({Code}); verifying the PIN against the copy cached on this PC", ex.Code);
            }
        }

        var hash = await _store.GetPinHashAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (hash is null)
        {
            throw IpcError.Unauthorized("No unlock PIN is known on this PC offline; use the password", "noPin").ToException();
        }

        if (!OfflineSessionStore.VerifyPassword(hash, pin))
        {
            RecordFailure();
            _logger.LogWarning("Unlock refused for user {UserId}: wrong PIN (offline)", user.Id);
            throw IpcError.Unauthorized("Wrong PIN", "badSecret").ToException();
        }
    }

    private async Task VerifyPasswordAsync(User user, string password, bool online, CancellationToken cancellationToken)
    {
        // The PC id lives in the agent tokens (registration); agent.json has none.
        if (online && (_settings.CurrentValue.PcId ?? _tokens?.Agent?.PcId) is { } pcId)
        {
            try
            {
                var hwid = await _hwid.GetAsync(cancellationToken).ConfigureAwait(false);
                var response = await _server.LoginAsync(new AuthRequest(AuthKind.Password, user.Username, password, null, null, null, pcId, hwid), cancellationToken).ConfigureAwait(false);
                if (response.User.Id != user.Id)
                {
                    RecordFailure();
                    throw IpcError.Unauthorized("Wrong password", "badSecret").ToException();
                }

                try
                {
                    await _store.CacheUserAsync(user.Username, response.OfflineHash, response.User, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not refresh the offline login cache for {Username}", user.Username);
                }

                return;
            }
            catch (ServerApiException ex) when (ex.IsRetryable && ex.Code != ErrorCode.RateLimited)
            {
                _logger.LogInformation("Server unreachable ({Code}); verifying password offline", ex.Code);
            }
            catch (ServerApiException ex) when (ex.Code == ErrorCode.Unauthorized)
            {
                RecordFailure();
                _logger.LogInformation("Server refused the password check for {Username}: {Reason}", user.Username, ex.Reason);
                throw IpcDetails.Error(ErrorCode.Unauthorized, "Wrong password", "badSecret", ("attemptsLeft", IpcDetails.IntOf(ex.Error?.Details, "attemptsLeft")));
            }
        }

        var cached = await _store.TryOfflineLoginAsync(user.Username, password, cancellationToken).ConfigureAwait(false);
        if (cached is null || cached.Id != user.Id)
        {
            RecordFailure();
            _logger.LogWarning("Unlock refused for user {UserId}: wrong password (offline)", user.Id);
            throw IpcError.Unauthorized("Wrong password", "badSecret").ToException();
        }
    }

    private async Task CachePinAsync(Guid userId, string pin)
    {
        try
        {
            await _store.SavePinHashAsync(userId, HashPin(pin), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "PIN of {UserId} could not be cached for offline unlock", userId);
        }
    }

    private async Task ForgetPinAsync(Guid userId)
    {
        try
        {
            await _store.DeletePinHashAsync(userId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Cached PIN of {UserId} not deleted", userId);
        }
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
                ClearOrigin();
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

        if (OwnerIsGuest())
        {
            // A guest has nothing to unlock with: the idle lock would shut them out of the time they paid for.
            _logger.LogDebug("Idle auto-lock skipped: the session is a guest's");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await LockCoreAsync(IdleReason, SessionLockOrigin.Player, CancellationToken.None).ConfigureAwait(false);
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

    // ---- lock origin, kept on disk so a staff lock survives an Agent restart -------------------

    private string OriginPath()
    {
        AgentSettings settings = _settings.CurrentValue;
        return Path.Combine(settings.ResolvePersistentPath(settings.Paths.Cache), OriginFileName);
    }

    private void SetOrigin(Guid sessionId, SessionLockOrigin origin, string? reason)
    {
        lock (_originGate)
        {
            _originLoaded = true;
            _origin = new LockOriginRecord(sessionId, origin, reason);
            try
            {
                string path = OriginPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_origin, AgentStateJson.Default.LockOriginRecord));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Lock origin of session {SessionId} could not be remembered", sessionId);
            }
        }
    }

    private void ClearOrigin()
    {
        lock (_originGate)
        {
            if (_originLoaded && _origin is null)
            {
                return;
            }

            _originLoaded = true;
            _origin = null;
            try
            {
                File.Delete(OriginPath());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Lock origin file not deleted");
            }
        }
    }

    private void LoadOrigin()
    {
        if (_originLoaded)
        {
            return;
        }

        _originLoaded = true;
        try
        {
            string path = OriginPath();
            if (File.Exists(path))
            {
                _origin = JsonSerializer.Deserialize(File.ReadAllText(path), AgentStateJson.Default.LockOriginRecord);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Lock origin file unreadable; the lock counts as the player's");
        }
    }
}

/// <summary>What <c>cache\session-lock.json</c> holds.</summary>
/// <param name="SessionId">Session the lock belongs to (a record of another session is ignored).</param>
/// <param name="Origin">Who locked it.</param>
/// <param name="Reason">Reason given.</param>
internal sealed record LockOriginRecord(Guid SessionId, SessionLockOrigin Origin, string? Reason);
