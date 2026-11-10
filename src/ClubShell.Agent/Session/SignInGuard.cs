using ClubShell.Agent.Ipc;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Users;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Security;

namespace ClubShell.Agent.Session;

/// <summary>
/// The end of a player's sign-in that the player did not ask for: the server refused their token (<c>401 userToken</c>),
/// the token expired on its own, or the server revoked the sign-in (<c>userRevoked</c>). The player is signed out and the
/// Shell told why (<c>auth.expired {reason}</c>), but a session of theirs that is still open is not dropped: it is locked
/// (<see cref="SessionLockOrigin.SignIn"/>) and goes on as soon as the same player signs in again. Without a session the
/// player is simply signed out, as before.
/// </summary>
public sealed class SignInGuard : IDisposable
{
    private readonly ShellUserContext _users;
    private readonly SessionManager _sessions;
    private readonly SessionLock _lock;
    private readonly ITokenStore _tokens;
    private readonly IIpcEventPublisher _publisher;
    private readonly IServerClient _server;
    private readonly IClock _clock;
    private readonly ILogger<SignInGuard> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>Creates the guard and subscribes to <see cref="IServerClient.UserTokenRejected"/>.</summary>
    public SignInGuard(
        ShellUserContext users,
        SessionManager sessions,
        SessionLock sessionLock,
        ITokenStore tokens,
        IIpcEventPublisher publisher,
        IServerClient server,
        IClock clock,
        ILogger<SignInGuard> logger)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(sessionLock);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _users = users;
        _sessions = sessions;
        _lock = sessionLock;
        _tokens = tokens;
        _publisher = publisher;
        _server = server;
        _clock = clock;
        _logger = logger;
        _server.UserTokenRejected += OnUserTokenRejected;
    }

    /// <summary>Maps a <c>userRevoked</c> reason (SERVER_API.md §6.3) to the reason the Shell shows; an unknown one reads as <see cref="AuthExpiredReason.Revoked"/>.</summary>
    public static AuthExpiredReason MapRevokedReason(string? reason) => reason switch
    {
        "sessionEnded" => AuthExpiredReason.SessionEnded,
        "seatTaken" => AuthExpiredReason.SeatTaken,
        "seatMoved" => AuthExpiredReason.SeatMoved,
        "signedInElsewhere" => AuthExpiredReason.SignedInElsewhere,
        "blacklisted" => AuthExpiredReason.Blacklisted,
        "passwordReset" => AuthExpiredReason.PasswordReset,
        "admin" => AuthExpiredReason.Admin,
        "tokenExpired" or "expired" => AuthExpiredReason.TokenExpired,
        _ => AuthExpiredReason.Revoked,
    };

    /// <summary>
    /// A call with the player's token got <c>401 userToken</c>: the signed-in player (when it is the token's) is signed out
    /// with <c>tokenExpired</c>, an open session of theirs locked until they sign in again. Ignored during a sign-out.
    /// </summary>
    public async Task HandleTokenRejectedAsync(Guid? userId, CancellationToken cancellationToken)
    {
        if (_users.IsSigningOut || _users.User is not { } user || (userId is { } id && id != user.Id))
        {
            return;
        }

        await EndSignInAsync(user, AuthExpiredReason.TokenExpired, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Maintenance tick: the player's access token (or offline grace window) has expired. With no session of theirs open
    /// the player is signed out (<c>auth.expired {tokenExpired}</c>, as before). With one, nothing happens here: the
    /// server extends the token while the player plays on this PC, so the local expiry is stale and a real expiry comes
    /// back as <c>401 userToken</c> (<see cref="HandleTokenRejectedAsync"/>).
    /// </summary>
    public async Task ExpireIfDueAsync(CancellationToken cancellationToken)
    {
        if (_users.IsSigningOut || _users.User is not { } user || _users.ExpiresAt is not { } expiresAt || expiresAt > _clock.UtcNow)
        {
            return;
        }

        if (OwnSessionOpen(user))
        {
            return;
        }

        _logger.LogInformation("Access token of user {UserId} expired at {ExpiresAt}", user.Id, expiresAt);
        await EndSignInAsync(user, AuthExpiredReason.TokenExpired, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>userRevoked</c> push: for another player than the signed-in one it is ignored. Otherwise the server's view of
    /// the session is taken first (it ended, moved or handed over the seat itself; settling with a revoked token would only
    /// fail), then the player is signed out with the mapped reason, a session of theirs still open locked.
    /// </summary>
    public async Task HandleRevokedAsync(UserRevokedPush revoked, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revoked);
        if (_users.User is not { } user || user.Id != revoked.UserId)
        {
            _logger.LogDebug("userRevoked for {UserId} ignored: not the signed-in player", revoked.UserId);
            return;
        }

        AuthExpiredReason reason = MapRevokedReason(revoked.Reason);
        _logger.LogWarning("User {UserId} revoked by the server: {Reason}", user.Id, revoked.Reason);
        try
        {
            await _sessions.SyncWithServerAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Session sync after the revocation of {UserId} failed; the local session is kept", user.Id);
        }

        await EndSignInAsync(user, reason, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _server.UserTokenRejected -= OnUserTokenRejected;
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool OwnSessionOpen(User user) =>
        _sessions.State.IsOpen() && _sessions.Current is { } session && session.UserId == user.Id;

    private void OnUserTokenRejected(object? sender, UserTokenRejectedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // Off the caller's flow: the caller may hold the session gate (a time-up end with the player's token).
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleTokenRejectedAsync(e.UserId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not ObjectDisposedException)
            {
                _logger.LogError(ex, "Handling the refused token of {UserId} failed", e.UserId);
            }
        });
    }

    private async Task EndSignInAsync(User user, AuthExpiredReason reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_users.User?.Id != user.Id)
            {
                return;
            }

            bool locked = false;
            if (OwnSessionOpen(user))
            {
                try
                {
                    locked = await _lock.LockForSignInAsync(LockReasonOf(reason), cancellationToken).ConfigureAwait(false) is not null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Session of {UserId} could not be locked after the sign-in ended", user.Id);
                }
            }

            _users.Clear();
            if (_tokens.User?.UserId == user.Id)
            {
                try
                {
                    await _tokens.SetUserAsync(null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    _logger.LogWarning(ex, "Player token of {UserId} could not be dropped", user.Id);
                }
            }

            _logger.LogInformation("User {UserId} signed out ({Reason}); session kept and locked: {Locked}", user.Id, reason, locked);
            await _publisher.PublishAsync(IpcMessages.Events.AuthExpired, new AuthExpired(reason), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string LockReasonOf(AuthExpiredReason reason) =>
        reason == AuthExpiredReason.TokenExpired ? SessionLock.SignInReason : System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(reason.ToString());
}
