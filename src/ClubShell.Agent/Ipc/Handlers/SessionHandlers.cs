using System.Buffers;
using System.Text.Json;
using ClubShell.Agent.Policy;
using ClubShell.Agent.Saves;
using ClubShell.Agent.Server;
using ClubShell.Agent.Session;
using ClubShell.Agent.Updates;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Shop;
using ClubShell.Contracts.Users;
using ClubShell.Contracts.Wallet;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Http;
using ClubShell.Core.Security;
using Microsoft.Extensions.Options;
using PlayerSettingsListResponse = ClubShell.Contracts.Games.PlayerSettingsListResponse;
using PlayerSettingsResetRequest = ClubShell.Contracts.Games.PlayerSettingsResetRequest;
using PlaySession = ClubShell.Contracts.Sessions.Session;

namespace ClubShell.Agent.Ipc.Handlers;

/// <summary><c>auth.*</c> and <c>session.*</c> requests (IPC_PROTOCOL.md §7.1, §7.2).</summary>
public sealed class SessionHandlers : IIpcHandlerGroup
{
    /// <summary>
    /// How long <c>auth.logout</c> may take for the session end (games closed, settlement), the server sign-out and the
    /// local clear. Its own budget, never the request's token: the profile reset after the session closes the Shell, and
    /// with it the pipe, while the sign-out is still running.
    /// </summary>
    public static readonly TimeSpan LogoutBudget = TimeSpan.FromSeconds(45);

    /// <summary>Error reason of a request only the cash desk may make (pause, resume, an end or sign-out reason other than <c>user</c>/<c>idle</c>).</summary>
    public const string StaffOnlyReason = "staffOnly";

    /// <summary>Error reason of a request that needs the club server and is not served offline.</summary>
    public const string OfflineUnavailableReason = "offlineUnavailable";

    private readonly SessionManager _sessions;
    private readonly SessionLock _lock;
    private readonly OfflineSessionStore _store;
    private readonly IServerClient _server;
    private readonly Hwid _hwid;
    private readonly ShellUserContext _users;
    private readonly ShellTokenStore _token;
    private readonly IServerLinkState _connection;
    private readonly IPolicyEnforcer _policy;
    private readonly IKioskCredentials _kiosk;
    private readonly ShellSettingsStore _shellSettings;
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<SessionHandlers> _logger;
    private readonly ITokenStore? _tokens;

    /// <summary>Creates the group. <paramref name="services"/> resolves <see cref="ShellUpdater"/> lazily (it depends on the watchdog).</summary>
    public SessionHandlers(
        SessionManager sessions,
        SessionLock sessionLock,
        OfflineSessionStore store,
        IServerClient server,
        Hwid hwid,
        ShellUserContext users,
        ShellTokenStore token,
        IServerLinkState connection,
        IPolicyEnforcer policy,
        IKioskCredentials kiosk,
        ShellSettingsStore shellSettings,
        IServiceProvider services,
        IOptionsMonitor<AgentSettings> settings,
        IClock clock,
        ILogger<SessionHandlers> logger,
        ITokenStore? tokens = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(sessionLock);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(hwid);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(kiosk);
        ArgumentNullException.ThrowIfNull(shellSettings);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _sessions = sessions;
        _lock = sessionLock;
        _store = store;
        _server = server;
        _hwid = hwid;
        _users = users;
        _token = token;
        _connection = connection;
        _policy = policy;
        _kiosk = kiosk;
        _shellSettings = shellSettings;
        _services = services;
        _settings = settings;
        _clock = clock;
        _logger = logger;
        _tokens = tokens;
    }

    /// <inheritdoc />
    public void Register(MessageDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        dispatcher.Register<AuthHelloRequest, AuthHelloResponse>(IpcMessages.Auth.Hello, HelloAsync);
        dispatcher.Register<AuthLoginRequest, AuthLoginResponse>(IpcMessages.Auth.Login, LoginAsync);
        dispatcher.RegisterOptional<AuthLogoutRequest, AuthLogoutResponse>(IpcMessages.Auth.Logout, LogoutAsync);
        dispatcher.RegisterNoPayload<AuthStatusResponse>(IpcMessages.Auth.Status, (_, cancellationToken) => StatusAsync(cancellationToken));
        dispatcher.RegisterNoPayload<QrLoginStart>(IpcMessages.Auth.QrStart, QrStartAsync);

        dispatcher.RegisterNoPayload<PlaySession?>(IpcMessages.Session.Get, (context, _) => Task.FromResult(SessionOf(context.User)));
        dispatcher.Register<SessionStartRequest, PlaySession>(IpcMessages.Session.Start, StartAsync);
        // Billing never stops on the player's request: pause and resume are the cash desk's (server commands).
        dispatcher.RegisterOptional<SessionPauseRequest, PlaySession>(IpcMessages.Session.Pause, (_, _, _) => throw StaffOnly("Only the cash desk pauses a session"));
        dispatcher.RegisterNoPayload<PlaySession>(IpcMessages.Session.Resume, (_, _) => throw StaffOnly("Only the cash desk resumes a session"));
        dispatcher.RegisterOptional<SessionEndRequest, SessionEndResult>(IpcMessages.Session.End, EndAsync);
        dispatcher.Register<SessionExtendRequest, PlaySession>(IpcMessages.Session.Extend, ExtendAsync);
        dispatcher.RegisterOptional<SessionLockRequest, PlaySession>(IpcMessages.Session.Lock, LockAsync);
        dispatcher.RegisterOptional<SessionUnlockRequest, PlaySession>(IpcMessages.Session.Unlock, (context, request, cancellationToken) => _lock.UnlockAsync(context.RequireUser(), request ?? new SessionUnlockRequest(), IsOnline, cancellationToken));
        dispatcher.RegisterNoPayload<SessionTimeLeftResponse>(IpcMessages.Session.TimeLeft, (_, _) => Task.FromResult(_sessions.GetTimeLeft()));
        dispatcher.Register<SessionQuoteIpcRequest, SessionQuote>(IpcMessages.Session.Quote, QuoteAsync);
    }

    /// <summary>Agent capabilities advertised in <c>auth.hello</c> (IPC_PROTOCOL.md §3), derived from settings, policy and <c>shell.json</c> features.</summary>
    public IReadOnlyList<string> Capabilities()
    {
        AgentSettings s = _settings.CurrentValue;
        ShellFeatures features = _shellSettings.Features;
        bool keyboard = (_policy.Current?.Kiosk.AllowVirtualKeyboard ?? true) && _shellSettings.Get().AllowVirtualKeyboard;
        var list = new List<string>(11);
        Add(list, AgentCapabilities.AccountPool, s.Games.AccountPool.Enabled);
        Add(list, AgentCapabilities.CloudSave, s.Games.CloudSave.Enabled);
        Add(list, AgentCapabilities.RemoteControl, s.RemoteAdmin.AllowRemoteInput);
        Add(list, AgentCapabilities.ScreenCapture, s.RemoteAdmin.AllowScreenCapture);
        Add(list, AgentCapabilities.VirtualKeyboard, keyboard);
        Add(list, AgentCapabilities.Wol, s.Power.WolEnabled);
        Add(list, AgentCapabilities.Offline, s.Offline.Enabled);
        Add(list, AgentCapabilities.Shop, features.Shop);
        Add(list, AgentCapabilities.Chat, features.Chat);
        Add(list, AgentCapabilities.Booking, features.Booking);
        Add(list, AgentCapabilities.Tournaments, features.Tournaments);
        return list;

        static void Add(List<string> target, string capability, bool enabled)
        {
            if (enabled)
            {
                target.Add(capability);
            }
        }
    }

    // ---- auth -------------------------------------------------------------------------------

    private async Task<AuthHelloResponse> HelloAsync(IpcContext context, AuthHelloRequest request, CancellationToken cancellationToken)
    {
        if (!ShellTokenStore.IsHex64(request.ShellToken))
        {
            throw IpcError.Validation("shellToken", "format").ToException();
        }

        if (string.IsNullOrWhiteSpace(request.ShellVersion) || request.ShellVersion.Length > 64)
        {
            throw IpcError.Validation("shellVersion", "required").ToException();
        }

        if (request.Capabilities is null)
        {
            throw IpcError.Validation("capabilities", "required").ToException();
        }

        if (!_token.Verify(request.ShellToken))
        {
            _logger.LogWarning("auth.hello from pid {Pid} refused: shell token mismatch", context.ClientPid);
            throw IpcError.Unauthorized("Shell token mismatch", "shellToken").ToException();
        }

        if (request.Pid != context.ClientPid)
        {
            _logger.LogWarning("auth.hello refused: claimed pid {Claimed} but the pipe client is pid {Actual}", request.Pid, context.ClientPid);
            throw IpcError.Forbidden("Process id does not match the pipe client", "pidMismatch").ToException();
        }

        if (context.WtsSessionId >= 0 && request.WtsSessionId != context.WtsSessionId)
        {
            _logger.LogWarning("auth.hello refused: claimed WTS session {Claimed} but the pipe client runs in {Actual}", request.WtsSessionId, context.WtsSessionId);
            throw IpcError.Forbidden("Session id does not match the pipe client", "sessionMismatch").ToException();
        }

        context.Elevate(IpcAuthLevel.Hello, null, null);
        _server.ShellVersion = request.ShellVersion;
        _server.AcceptLanguage = ShellSettingsStore.WireName(request.Locale);
        await ConfirmShellVersionAsync(request.ShellVersion, cancellationToken).ConfigureAwait(false);

        AgentSettings settings = _settings.CurrentValue;
        var response = new AuthHelloResponse(
            ClubShellVersion.Current,
            IpcEnvelope.CurrentVersion,
            PcId,
            settings.PcName ?? Environment.MachineName,
            settings.Zone ?? string.Empty,
            _connection.Connectivity == ConnectivityState.Online,
            _policy.Current?.Version ?? 0,
            _server.ServerNow,
            Capabilities(),
            _kiosk.UserName);
        _logger.LogInformation("Shell {Version} (pid {Pid}, session {Session}, locale {Locale}) connected; capabilities [{Capabilities}]", request.ShellVersion, request.Pid, request.WtsSessionId, request.Locale, string.Join(",", response.Capabilities));
        return response;
    }

    private async Task<AuthLoginResponse> LoginAsync(IpcContext context, AuthLoginRequest request, CancellationToken cancellationToken)
    {
        ValidateLogin(request);
        RequireReady();
        if (_sessions.State.IsOpen() && _sessions.Current is { } open && _users.User is { } current && open.UserId == current.Id
            && !string.Equals(current.Username, request.Username, StringComparison.OrdinalIgnoreCase) && request.Kind == AuthKind.Password)
        {
            throw IpcError.Conflict("Another user has an active session on this PC", "sessionActive").ToException();
        }

        Guid pcId = PcId;
        if (pcId == Guid.Empty)
        {
            throw IpcError.AgentOffline().ToException();
        }

        string hwid = await _hwid.GetAsync(cancellationToken).ConfigureAwait(false);
        AuthResponse? auth = null;
        if (IsOnline)
        {
            try
            {
                auth = await LoginOnlineAsync(request, pcId, hwid, cancellationToken).ConfigureAwait(false);
            }
            catch (ServerApiException ex) when (ex.IsRetryable)
            {
                _logger.LogWarning("Server login failed ({Code}); trying the offline cache", ex.Code);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Server unreachable for login; trying the offline cache");
            }
        }

        if (auth is null)
        {
            return await LoginOfflineAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (auth.User.HasFlag(UserFlags.Banned))
        {
            throw IpcError.Forbidden("Account is banned", "banned").ToException();
        }

        // A guest's username field carries the display name they typed, not a login name.
        string cacheKey = request.Kind == AuthKind.Guest ? auth.User.Username : request.Username ?? auth.User.Username;
        await CacheUserAsync(cacheKey, auth, cancellationToken).ConfigureAwait(false);
        await DropQueuedLogoutsAsync().ConfigureAwait(false);
        _users.Set(auth.User, auth.ExpiresAt, ConnectivityState.Online);
        PlaySession? session = auth.Session;
        if (session is not null)
        {
            try
            {
                await _sessions.ApplyServerSessionAsync(session, cancellationToken).ConfigureAwait(false);
                session = _sessions.Current ?? session;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Existing server session {SessionId} could not be applied locally", session.Id);
            }
        }
        else
        {
            session = SessionOf(auth.User);
        }

        session = await ResumeAfterSignInAsync(auth.User, cancellationToken).ConfigureAwait(false) ?? session;
        _logger.LogInformation("User {UserId} ({Username}) logged in via {Kind} (online); session {SessionId}", auth.User.Id, auth.User.Username, request.Kind, session?.Id);
        Saves?.SignedIn(auth.User, guest: request.Kind == AuthKind.Guest);
        return new AuthLoginResponse(auth.User, session, auth.ExpiresAt, ConnectivityState.Online);
    }

    private async Task<AuthResponse> LoginOnlineAsync(AuthLoginRequest request, Guid pcId, string hwid, CancellationToken cancellationToken)
    {
        switch (request.Kind)
        {
            case AuthKind.Guest:
                // The Shell sends the name a guest typed in the username field (empty: the server names the guest).
                string? displayName = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim();
                return await _server.GuestLoginAsync(new GuestAuthRequest(pcId, hwid, displayName, _shellSettings.Get().Locale), cancellationToken).ConfigureAwait(false);
            case AuthKind.Qr:
            {
                QrLoginStatus status = await _server.GetQrLoginStatusAsync(request.QrToken!, cancellationToken).ConfigureAwait(false);
                if (status.Status == QrStatus.Confirmed && status.Auth is { } confirmed)
                {
                    return confirmed;
                }

                string wire = status.Status switch
                {
                    QrStatus.Pending => "pending",
                    QrStatus.Scanned => "scanned",
                    QrStatus.Expired => "expired",
                    _ => "expired",
                };
                throw IpcError.Of(ErrorCode.Unauthorized, status.Status == QrStatus.Expired ? "QR login expired" : "QR login not confirmed yet", Details(("status", wire), ("reason", wire))).ToException();
            }

            default:
                return await _server.LoginAsync(request.ToServerRequest(pcId, hwid), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AuthLoginResponse> LoginOfflineAsync(AuthLoginRequest request, CancellationToken cancellationToken)
    {
        OfflineSettings offline = _settings.CurrentValue.Offline;
        if (request.Kind != AuthKind.Password || !offline.Enabled)
        {
            throw IpcError.AgentOffline().ToException();
        }

        User? user = await _store.TryOfflineLoginAsync(request.Username!, request.Password!, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            _logger.LogWarning("Offline login refused for {Username}", request.Username);
            throw IpcError.Unauthorized("Wrong username or password (offline)", "badCredentials").ToException();
        }

        if (user.HasFlag(UserFlags.Banned))
        {
            throw IpcError.Forbidden("Account is banned", "banned").ToException();
        }

        DateTimeOffset expiresAt = _clock.UtcNow.AddMinutes(Math.Max(1, offline.MaxOfflineMinutes));
        if (_tokens?.User is { } held && held.UserId != user.Id)
        {
            // Another player's token must not be sent on this player's behalf once the server is back.
            await _tokens.SetUserAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        await DropQueuedLogoutsAsync().ConfigureAwait(false);
        _users.Set(user, expiresAt, ConnectivityState.Offline);
        PlaySession? session = await ResumeAfterSignInAsync(user, cancellationToken).ConfigureAwait(false) ?? SessionOf(user);
        _logger.LogInformation("User {UserId} ({Username}) logged in from the offline cache", user.Id, user.Username);
        // The club server is on the local network: the saves are laid out without the cash desk too.
        Saves?.SignedIn(user);
        return new AuthLoginResponse(user, session, expiresAt, ConnectivityState.Offline);
    }

    /// <summary>
    /// A session of <paramref name="user"/> locked because their sign-in ended (token refused, sign-in revoked) goes on
    /// now that they signed in again (G3). Returns the unlocked session, or <see langword="null"/> when nothing changed.
    /// </summary>
    private async Task<PlaySession?> ResumeAfterSignInAsync(User user, CancellationToken cancellationToken)
    {
        try
        {
            return await _lock.ResumeAfterSignInAsync(user, cancellationToken).ConfigureAwait(false) ? SessionOf(user) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Session of {UserId} could not be unlocked after the new sign-in", user.Id);
            return null;
        }
    }

    /// <summary>A sign-in supersedes a sign-out still queued for the server (see <see cref="OfflineSessionStore.DropQueuedLogoutsAsync"/>).</summary>
    private async Task DropQueuedLogoutsAsync()
    {
        try
        {
            await _store.DropQueuedLogoutsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Queued sign-outs could not be dropped");
        }
    }

    /// <summary>
    /// Signs the player out as one step (G1): their session ends (settled by the server, or queued in the outbox when it
    /// cannot be reached), the server drops their token (<c>POST /auth/logout</c>, queued too when unreachable) and the
    /// signed-in player is cleared — in a <see langword="finally"/>, whatever failed. All of it runs under
    /// <see cref="LogoutBudget"/>, not under <paramref name="cancellationToken"/>: the session end starts the profile reset,
    /// which closes the Shell and with it this request's pipe, and a sign-out cancelled half-way left the player signed in
    /// (the Shell came back signed in and opened a new session). The profile reset waits for the sign-out
    /// (<see cref="ShellUserContext.WaitForSignOutAsync"/>).
    /// </summary>
    private async Task<AuthLogoutResponse> LogoutAsync(IpcContext context, AuthLogoutRequest? request, CancellationToken cancellationToken)
    {
        SessionEndReason reason = request?.Reason ?? SessionEndReason.User;
        RequirePlayerReason(reason);
        User user = context.RequireUser();
        if (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("auth.logout of {UserId}: the request was cancelled; signing out all the same", user.Id);
        }

        using IDisposable signingOut = _users.BeginSignOut();
        using var budget = new CancellationTokenSource(LogoutBudget);
        PlaySession? ended = null;
        bool sessionEnded = false;
        bool queued = false;
        try
        {
            // Only the player's own session ends with the sign-out: a session the cashier opened for someone else on this PC
            // (a guest signed in meanwhile) stays theirs — ending it here settled it as "user" with no refund.
            bool ownSession = OwnsOpenSession(user);
            if (ownSession)
            {
                try
                {
                    SessionEndResult result = await _sessions.EndAsync(reason, budget.Token).ConfigureAwait(false);
                    ended = result.Session;
                }
                catch (Exception ex) when (ex is ServerApiException or IpcException or HttpRequestException or OperationCanceledException or TimeoutException)
                {
                    // The server's sign-out below settles a session it still has open.
                    _logger.LogWarning(ex, "Session end at the sign-out of {UserId} did not complete; the server's sign-out settles it", user.Id);
                }

                sessionEnded = !OwnsOpenSession(user);
            }

            queued = await ServerLogoutAsync(user, reason, budget.Token).ConfigureAwait(false);
            if (ownSession && !sessionEnded && !queued)
            {
                // Settled by the server's sign-out: take its view so the local session ends too.
                try
                {
                    await _sessions.SyncWithServerAsync(budget.Token).ConfigureAwait(false);
                    sessionEnded = !OwnsOpenSession(user);
                }
                catch (Exception ex) when (ex is ServerApiException or HttpRequestException or OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Session sync after the sign-out of {UserId} failed", user.Id);
                }
            }

            if (!ownSession)
            {
                // Signed out without a session end of their own: no cleanup closes the games, so the release does that first.
                // A session end has already started it (SessionCleanup, saves.detach).
                Saves?.BeginRelease(reason, closeGames: true);
            }
        }
        finally
        {
            if (_users.User?.Id == user.Id)
            {
                _users.Clear();
            }
        }

        _logger.LogInformation("User {UserId} logged out ({Reason}); session ended: {SessionEnded}; server sign-out {Delivery}", user.Id, reason, sessionEnded, queued ? "queued" : "done");
        return new AuthLogoutResponse(true, sessionEnded, ended);
    }

    /// <summary>
    /// <c>POST /auth/logout</c> for <paramref name="user"/>; queued in the outbox when the server cannot be reached
    /// (replayed with the player token, which is kept for it). <see langword="true"/> when queued.
    /// </summary>
    private async Task<bool> ServerLogoutAsync(User user, SessionEndReason reason, CancellationToken cancellationToken)
    {
        if (_tokens is not null && _tokens.User?.UserId != user.Id)
        {
            // Signed in from the offline cache, or the token is already gone: the server has nothing of this sign-in to
            // revoke, and a sign-out sent now would carry no token or another player's.
            return false;
        }

        if (IsOnline)
        {
            try
            {
                await _server.LogoutAsync(new LogoutRequest(reason), cancellationToken).ConfigureAwait(false);
                return false;
            }
            catch (ServerApiException ex) when (!ex.IsRetryable)
            {
                // Refused: the token is gone server-side already, which is what the sign-out wanted.
                _logger.LogInformation("Server refused the sign-out of {UserId} ({Code}); nothing left to revoke", user.Id, ex.Code);
                return false;
            }
            catch (Exception ex) when (ex is ServerApiException or HttpRequestException or OperationCanceledException or TimeoutException)
            {
                _logger.LogWarning("Server sign-out of {UserId} not delivered ({Message}); queued", user.Id, ex.Message);
            }
        }

        try
        {
            await _store.QueueLogoutAsync(user.Id, reason, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Sign-out of {UserId} could not be queued for the server", user.Id);
            return false;
        }
    }

    private async Task<AuthStatusResponse> StatusAsync(CancellationToken cancellationToken)
    {
        User? user = _users.User;
        PcReadiness? readiness = PcReadiness?.Current;
        // The link as it is now, not as it was at sign-in: a Shell (re)started while the server is down must show offline.
        ConnectivityState mode = _connection.Connectivity;
        bool lockedByStaff = _lock.IsLockedByStaff;
        if (user is null)
        {
            return new AuthStatusResponse(false, mode, Readiness: readiness, LockedByStaff: lockedByStaff);
        }

        bool pinSet = user.HasFlag(SessionLock.PinSetFlag) || await _lock.HasCachedPinAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return new AuthStatusResponse(true, mode, user, SessionOf(user), _users.ExpiresAt, readiness, lockedByStaff, pinSet);
    }

    private async Task<QrLoginStart> QrStartAsync(IpcContext context, CancellationToken cancellationToken)
    {
        RequireOnline();
        RequireReady();
        Guid pcId = PcId;
        if (pcId == Guid.Empty)
        {
            throw IpcError.AgentOffline().ToException();
        }

        return await _server.StartQrLoginAsync(new QrStartRequest(pcId), cancellationToken).ConfigureAwait(false);
    }

    // ---- session ----------------------------------------------------------------------------

    private Task<PlaySession> StartAsync(IpcContext context, SessionStartRequest request, CancellationToken cancellationToken)
    {
        RequireReady();
        User user = context.RequireUser();
        if (request.TariffId == Guid.Empty)
        {
            throw IpcError.Validation("tariffId", "required").ToException();
        }

        if (request.Minutes is { } minutes && minutes <= 0)
        {
            throw IpcError.Validation("minutes", "min").ToException();
        }

        if (user.HasFlag(UserFlags.Banned))
        {
            throw IpcError.Forbidden("Account is banned", "banned").ToException();
        }

        // Online the server decides who may play as they go (members from their balance, guests per club settings).
        // Offline nobody does: that time is billed by the server as it runs, against a debt limit only it knows.
        if (!request.Prepaid && !IsOnline)
        {
            throw OfflineUnavailable("Pay-as-you-go needs the club server");
        }

        Guid pcId = PcId;
        if (pcId == Guid.Empty)
        {
            throw IpcError.AgentOffline().ToException();
        }

        return StartCoreAsync(user, new SessionCreateRequest(pcId, user.Id, request.TariffId, request.Minutes, request.Prepaid), cancellationToken);
    }

    private async Task<PlaySession> StartCoreAsync(User user, SessionCreateRequest create, CancellationToken cancellationToken)
    {
        PlaySession session = await _sessions.StartAsync(create, cancellationToken).ConfigureAwait(false);
        await RefreshOfflineBalanceAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return session;
    }

    private async Task<PlaySession> ExtendAsync(IpcContext context, SessionExtendRequest request, CancellationToken cancellationToken)
    {
        if (request.Minutes <= 0)
        {
            throw IpcError.Validation("minutes", "min").ToException();
        }

        User user = context.RequireUser();
        RequireOwnSessionOffline(user);
        PlaySession session = await _sessions.ExtendAsync(request.Minutes, request.TariffId, cancellationToken).ConfigureAwait(false);
        await RefreshOfflineBalanceAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return session;
    }

    /// <summary><c>session.end</c> from the Shell: only <c>user</c> and <c>idle</c> (G4); any other reason is the cash desk's.</summary>
    private Task<SessionEndResult> EndAsync(IpcContext context, SessionEndRequest? request, CancellationToken cancellationToken)
    {
        SessionEndReason reason = request?.Reason ?? SessionEndReason.User;
        RequirePlayerReason(reason);
        RequireOwnSessionOffline(context.RequireUser());
        return _sessions.EndAsync(reason, cancellationToken);
    }

    /// <summary>
    /// <c>session.lock</c> from the Shell: a guest has no PIN or password to unlock with (<c>forbidden {reason: guestLock}</c>,
    /// G15), and a player locks only their own session (<c>forbidden {reason: otherUser}</c>).
    /// </summary>
    private Task<PlaySession> LockAsync(IpcContext context, SessionLockRequest? request, CancellationToken cancellationToken)
    {
        User user = context.RequireUser();
        if (user.Role == UserRole.Guest)
        {
            throw IpcError.Forbidden("A guest has no PIN or password to unlock with", "guestLock").ToException();
        }

        if (_sessions.Current is { } session && session.UserId != user.Id)
        {
            throw IpcError.Forbidden("Session belongs to another user", "otherUser").ToException();
        }

        return _lock.LockAsync(request?.Reason, cancellationToken);
    }

    /// <summary>
    /// <c>session.quote</c> (G13): the server's price of <paramref name="request"/> for this PC and the signed-in player,
    /// with the day rate and the player's discount. Offline: <c>conflict {reason: offlineUnavailable}</c>.
    /// </summary>
    private async Task<SessionQuote> QuoteAsync(IpcContext context, SessionQuoteIpcRequest request, CancellationToken cancellationToken)
    {
        User user = context.RequireUser();
        if (request.TariffId == Guid.Empty)
        {
            throw IpcError.Validation("tariffId", "required").ToException();
        }

        if (request.Minutes is { } minutes && minutes <= 0)
        {
            throw IpcError.Validation("minutes", "min").ToException();
        }

        if (!IsOnline)
        {
            throw OfflineUnavailable("Prices come from the club server");
        }

        Guid pcId = PcId;
        if (pcId == Guid.Empty)
        {
            throw OfflineUnavailable("This PC is not registered with the club server yet");
        }

        try
        {
            return await _server.QuoteSessionAsync(new SessionQuoteRequest(pcId, user.Id, request.TariffId, request.Minutes), cancellationToken).ConfigureAwait(false);
        }
        catch (ServerApiException ex) when (ex.IsRetryable && ex.Code != ErrorCode.RateLimited)
        {
            _logger.LogWarning("session.quote: server unavailable ({Code})", ex.Code);
            throw OfflineUnavailable("Prices come from the club server");
        }
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>The player's saves (SHELL_CHANGES 23); resolved lazily, absent in tests that build the handlers by hand.</summary>
    private IPlayerSaves? Saves => _services.GetService<IPlayerSaves>();

    /// <summary>Whether the PC lets a player in yet (SHELL_CHANGES 86); absent in tests that build the handlers by hand.</summary>
    private Readiness.PcReadinessService? PcReadiness => _services.GetService<Readiness.PcReadinessService>();

    /// <summary>
    /// Sign-in, QR and session start wait for the games disks (SHELL_CHANGES 86), whatever sends them: the Shell keeps its
    /// sign-in screen closed meanwhile, but a card tap or a phone's QR confirmation still arrives.
    /// </summary>
    private void RequireReady()
    {
        if (PcReadiness is { IsReady: false } readiness)
        {
            throw IpcError.Of(
                ErrorCode.Conflict,
                "The PC is still preparing its games disks",
                Details(("reason", "pcNotReady"), ("state", readiness.Current.State == PcReadinessState.NotReady ? "notReady" : "preparing"))).ToException();
        }
    }

    private Guid PcId => _settings.CurrentValue.PcId ?? _connection.PcId ?? Guid.Empty;

    private bool IsOnline => _connection.Connectivity == ConnectivityState.Online;

    private void RequireOnline()
    {
        if (!IsOnline)
        {
            throw IpcError.AgentOffline().ToException();
        }
    }

    private PlaySession? SessionOf(User? user)
    {
        PlaySession? current = _sessions.Current;
        return current is not null && _sessions.State.IsOpen() && (user is null || current.UserId == user.Id) ? current : null;
    }

    private bool OwnsOpenSession(User user) =>
        _sessions.State.IsOpen() && _sessions.Current is { } open && open.UserId == user.Id;

    /// <summary>
    /// Without the server nobody checks whose session a request touches (online the server refuses another player's):
    /// offline, or for a session the server has not seen yet, the session must be the signed-in player's (G7).
    /// </summary>
    private void RequireOwnSessionOffline(User user)
    {
        if ((!IsOnline || _sessions.IsOfflineSession) && _sessions.Current is { } session && session.UserId != user.Id)
        {
            throw IpcError.Forbidden("Session belongs to another user", "otherUser").ToException();
        }
    }

    /// <summary>The cached balance after an offline purchase (taken from it locally) mirrored into the signed-in player.</summary>
    private async Task RefreshOfflineBalanceAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (IsOnline && !_sessions.IsOfflineSession)
        {
            return;
        }

        try
        {
            if (await _store.GetCachedUserAsync(userId, cancellationToken).ConfigureAwait(false) is { } cached && _users.User is { } user && user.Id == userId && user.Balance != cached.Balance)
            {
                _users.Update(user with { Balance = cached.Balance });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Cached balance of {UserId} not read", userId);
        }
    }

    /// <summary>Only <c>user</c> and <c>idle</c> may come from the Shell; anything else is <c>forbidden {reason: staffOnly}</c>.</summary>
    private static void RequirePlayerReason(SessionEndReason reason)
    {
        if (reason is not (SessionEndReason.User or SessionEndReason.Idle))
        {
            throw StaffOnly($"Only the cash desk ends a session as '{JsonNamingPolicy.CamelCase.ConvertName(reason.ToString())}'");
        }
    }

    private static IpcException StaffOnly(string message) => IpcError.Forbidden(message, StaffOnlyReason).ToException();

    private static IpcException OfflineUnavailable(string message) => IpcError.Conflict(message, OfflineUnavailableReason).ToException();

    private async Task CacheUserAsync(string username, AuthResponse auth, CancellationToken cancellationToken)
    {
        try
        {
            await _store.CacheUserAsync(username, auth.OfflineHash, auth.User, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Offline login cache could not be updated for {Username}", username);
        }
    }

    private async Task ConfirmShellVersionAsync(string shellVersion, CancellationToken cancellationToken)
    {
        if (_services.GetService<ShellUpdater>() is not { } updater)
        {
            return;
        }

        try
        {
            await updater.ConfirmRunningVersionAsync(shellVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Shell version {Version} could not be confirmed to the updater", shellVersion);
        }
    }

    private static void ValidateLogin(AuthLoginRequest request)
    {
        switch (request.Kind)
        {
            case AuthKind.Password:
                // The cash desk creates logins of 1–2 characters too; the server validates the rest.
                if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 32)
                {
                    throw IpcError.Validation("username", "required").ToException();
                }

                if (string.IsNullOrEmpty(request.Password) || request.Password.Length > 256)
                {
                    throw IpcError.Validation("password", "required").ToException();
                }

                break;
            case AuthKind.Qr:
                if (string.IsNullOrWhiteSpace(request.QrToken))
                {
                    throw IpcError.Validation("qrToken", "required").ToException();
                }

                break;
            case AuthKind.Card:
                if (string.IsNullOrWhiteSpace(request.CardId))
                {
                    throw IpcError.Validation("cardId", "required").ToException();
                }

                break;
            case AuthKind.Token:
                if (string.IsNullOrWhiteSpace(request.Token))
                {
                    throw IpcError.Validation("token", "required").ToException();
                }

                break;
            case AuthKind.Guest:
                // Optional display name in the username field.
                if (request.Username is { Length: > UserDomainHandlers.MaxDisplayNameLength })
                {
                    throw IpcError.Validation("username", "length").ToException();
                }

                break;
            default:
                throw IpcError.Validation("kind", "invalid").ToException();
        }
    }

    /// <summary>Builds a flat <c>{ key: value, … }</c> details object without needing a contract type.</summary>
    internal static JsonElement Details(params (string Key, string Value)[] pairs)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach ((string key, string value) in pairs)
            {
                writer.WriteString(key, value);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}

/// <summary>
/// <c>wallet.*</c>, <c>shop.*</c>, <c>chat.*</c>, <c>booking.*</c>, <c>tournaments.*</c> and <c>profile.*</c> requests
/// (IPC_PROTOCOL.md §7.5–§7.10): proxies to <see cref="IServerClient"/> with the user / PC / session ids filled in.
/// Tariffs and products are cached on disk for offline use; the balance falls back to the last known value.
/// </summary>
public sealed class UserDomainHandlers : IIpcHandlerGroup
{
    /// <summary>Longest accepted order note.</summary>
    public const int MaxNoteLength = 500;

    /// <summary>Longest accepted display name.</summary>
    public const int MaxDisplayNameLength = 64;

    /// <summary>Longest booking accepted from the Shell.</summary>
    public static readonly TimeSpan MaxBookingDuration = TimeSpan.FromHours(12);

    private readonly IServerClient _server;
    private readonly ShellUserContext _users;
    private readonly ISessionService _sessions;
    private readonly IServerLinkState _connection;
    private readonly SessionLock _lock;
    private readonly ShellSettingsStore _shellSettings;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<UserDomainHandlers> _logger;
    private readonly ContractFileCache<TariffsResponse> _tariffs;
    private readonly ContractFileCache<ShopProductsResponse> _products;

    /// <summary>Creates the group.</summary>
    public UserDomainHandlers(
        IServerClient server,
        ShellUserContext users,
        ISessionService sessions,
        IServerLinkState connection,
        SessionLock sessionLock,
        ShellSettingsStore shellSettings,
        IOptionsMonitor<AgentSettings> settings,
        IClock clock,
        ILogger<UserDomainHandlers> logger)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sessionLock);
        ArgumentNullException.ThrowIfNull(shellSettings);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _server = server;
        _users = users;
        _sessions = sessions;
        _connection = connection;
        _lock = sessionLock;
        _shellSettings = shellSettings;
        _settings = settings;
        _clock = clock;
        _logger = logger;
        string cacheDir = settings.CurrentValue.CacheDir;
        _tariffs = new ContractFileCache<TariffsResponse>(Path.Combine(cacheDir, "tariffs.json"), logger);
        _products = new ContractFileCache<ShopProductsResponse>(Path.Combine(cacheDir, "products.json"), logger);
    }

    /// <inheritdoc />
    public void Register(MessageDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        dispatcher.RegisterNoPayload<Balance>(IpcMessages.Wallet.Balance, BalanceAsync);
        dispatcher.RegisterOptional<WalletTariffsRequest, WalletTariffsResponse>(IpcMessages.Wallet.Tariffs, TariffsAsync);
        dispatcher.RegisterOptional<WalletHistoryRequest, WalletHistoryResponse>(IpcMessages.Wallet.History, HistoryAsync);
        dispatcher.Register<WalletTopupIntentRequest, TopupIntent>(IpcMessages.Wallet.TopupIntent, TopupIntentAsync);

        dispatcher.RegisterOptional<ShopProductsRequest, ShopProductsResponse>(IpcMessages.Shop.Products, ProductsAsync);
        dispatcher.Register<ShopOrderRequest, Order>(IpcMessages.Shop.Order, OrderAsync);
        dispatcher.Register<ShopOrderStatusRequest, Order>(IpcMessages.Shop.OrderStatus, OrderStatusAsync);
        dispatcher.RegisterOptional<ShopOrdersRequest, ShopOrdersResponse>(IpcMessages.Shop.Orders, OrdersAsync);

        dispatcher.RegisterOptional<ChatHistoryRequest, ChatHistoryResponse>(IpcMessages.Chat.History, ChatHistoryAsync);
        dispatcher.Register<ChatSendRequest, ChatMessage>(IpcMessages.Chat.Send, ChatSendAsync);
        dispatcher.Register<ChatMarkReadRequest, ChatMarkReadResponse>(IpcMessages.Chat.MarkRead, ChatMarkReadAsync);

        dispatcher.Register<BookingSeatsRequest, BookingSeatsResponse>(IpcMessages.Booking.Seats, SeatsAsync);
        dispatcher.Register<BookingReserveRequest, Booking>(IpcMessages.Booking.Reserve, ReserveAsync);
        dispatcher.Register<BookingCancelRequest, Booking>(IpcMessages.Booking.Cancel, CancelBookingAsync);

        dispatcher.RegisterOptional<TournamentsListRequest, TournamentsListResponse>(IpcMessages.Tournaments.List, TournamentsAsync);
        dispatcher.Register<TournamentsJoinRequest, Tournament>(IpcMessages.Tournaments.Join, JoinTournamentAsync);
        dispatcher.Register<TournamentsLeaderboardRequest, TournamentsLeaderboardResponse>(IpcMessages.Tournaments.Leaderboard, LeaderboardAsync);

        dispatcher.RegisterNoPayload<User>(IpcMessages.Profile.Get, ProfileGetAsync);
        dispatcher.Register<ProfileUpdateRequest, User>(IpcMessages.Profile.Update, ProfileUpdateAsync);
        dispatcher.RegisterNoPayload<UserStats>(IpcMessages.Profile.Stats, (context, cancellationToken) => Online(() => _server.GetUserStatsAsync(context.RequireUser().Id, cancellationToken)));
        dispatcher.RegisterNoPayload<ProfileAchievementsResponse>(IpcMessages.Profile.Achievements, (context, cancellationToken) => Online(() => _server.GetUserAchievementsAsync(context.RequireUser().Id, cancellationToken)));
        dispatcher.RegisterNoPayload<Loyalty>(IpcMessages.Profile.Loyalty, (context, cancellationToken) => Online(() => _server.GetUserLoyaltyAsync(context.RequireUser().Id, cancellationToken)));
        dispatcher.RegisterNoPayload<PlayerSettingsListResponse>(IpcMessages.Profile.GameSettings, (context, cancellationToken) => Online(() => _server.ListPlayerSettingsAsync(context.RequireUser().Id, cancellationToken)));
        dispatcher.Register<PlayerSettingsResetRequest, PlayerSettingsListResponse>(IpcMessages.Profile.GameSettingsReset, GameSettingsResetAsync);
    }

    /// <summary>Forgets the player's saved settings of one game and returns what is left.</summary>
    private Task<PlayerSettingsListResponse> GameSettingsResetAsync(IpcContext context, PlayerSettingsResetRequest request, CancellationToken cancellationToken)
    {
        Guid userId = context.RequireUser().Id;
        return Online(async () =>
        {
            await _server.DeletePlayerSettingsAsync(userId, request.GameId, cancellationToken).ConfigureAwait(false);
            return await _server.ListPlayerSettingsAsync(userId, cancellationToken).ConfigureAwait(false);
        });
    }

    // ---- wallet -----------------------------------------------------------------------------

    private async Task<Balance> BalanceAsync(IpcContext context, CancellationToken cancellationToken)
    {
        User user = context.RequireUser();
        if (IsOnline)
        {
            try
            {
                Balance balance = await _server.GetBalanceAsync(user.Id, cancellationToken).ConfigureAwait(false);
                _users.UpdateBalance(balance);
                return balance;
            }
            catch (Exception ex) when (IsConnectivity(ex))
            {
                _logger.LogWarning("wallet.balance: server unavailable; serving the cached balance");
            }
        }

        // The signed-in player's snapshot follows every walletUpdated push and offline charge; the bonus comes from the
        // last full answer of the server.
        User current = _users.User is { } signedIn && signedIn.Id == user.Id ? signedIn : user;
        Balance? last = _users.LastBalance;
        return last is not null && last.UserId == user.Id && last.Amount == current.Balance
            ? last
            : new Balance(user.Id, current.Balance, last?.UserId == user.Id ? last.Bonus : Money.Zero with { Currency = current.Balance.Currency }, current.Balance.Currency, last?.UpdatedAt ?? current.LastSeenAt ?? current.CreatedAt);
    }

    private async Task<WalletTariffsResponse> TariffsAsync(IpcContext context, WalletTariffsRequest? request, CancellationToken cancellationToken)
    {
        string ownZone = _settings.CurrentValue.Zone ?? string.Empty;
        string zone = string.IsNullOrWhiteSpace(request?.Zone) ? ownZone : request!.Zone!;
        TariffsResponse tariffs = string.Equals(zone, ownZone, StringComparison.Ordinal)
            ? await FetchOrCachedAsync(_tariffs, (etag, ct) => _server.GetTariffsAsync(zone, etag, ct), cancellationToken).ConfigureAwait(false)
            : (await _server.GetTariffsAsync(zone, null, cancellationToken).ConfigureAwait(false)).Require();
        return new WalletTariffsResponse(tariffs.Items, zone, tariffs.ServerTime);
    }

    private async Task<WalletHistoryResponse> HistoryAsync(IpcContext context, WalletHistoryRequest? request, CancellationToken cancellationToken)
    {
        User user = context.RequireUser();
        RequireOnline();
        WalletHistoryRequest query = request ?? new WalletHistoryRequest();
        if (query.Page is < 1 || query.PageSize is < 1 or > 500)
        {
            throw IpcError.Validation(query.Page is < 1 ? "page" : "pageSize", "range").ToException();
        }

        PagedResult<Transaction> page = await _server.GetTransactionsAsync(user.Id, query, cancellationToken).ConfigureAwait(false);
        return new WalletHistoryResponse(page.Items, page.Total, page.Page, page.PageSize);
    }

    private Task<TopupIntent> TopupIntentAsync(IpcContext context, WalletTopupIntentRequest request, CancellationToken cancellationToken)
    {
        User user = context.RequireUser();
        if (!_shellSettings.Features.Topup)
        {
            throw IpcError.PolicyDenied("features.topup").ToException();
        }

        if (request.Amount.Amount < TopupIntentCreateRequest.MinAmountMinor)
        {
            throw IpcError.Validation("amount", "min", $"Minimum top-up is {TopupIntentCreateRequest.MinAmountMinor} minor units").ToException();
        }

        if (string.IsNullOrWhiteSpace(request.Amount.Currency))
        {
            throw IpcError.Validation("amount.currency", "required").ToException();
        }

        RequireOnline();
        return _server.CreateTopupIntentAsync(user.Id, new TopupIntentCreateRequest(request.Amount, request.Provider, PcId), Guid.NewGuid(), cancellationToken);
    }

    // ---- shop -------------------------------------------------------------------------------

    private async Task<ShopProductsResponse> ProductsAsync(IpcContext context, ShopProductsRequest? request, CancellationToken cancellationToken)
    {
        if (!_shellSettings.Features.Shop)
        {
            throw IpcError.PolicyDenied("features.shop").ToException();
        }

        ShopProductsResponse all = await FetchOrCachedAsync(_products, (etag, ct) => _server.GetProductsAsync(null, etag, ct), cancellationToken).ConfigureAwait(false);
        if (request is null || (request.Category is null && string.IsNullOrWhiteSpace(request.Search)))
        {
            return all;
        }

        string? search = request.Search?.Trim();
        IReadOnlyList<Product> items = all.Items
            .Where(p => request.Category is null || p.Category == request.Category)
            .Where(p => string.IsNullOrEmpty(search)
                || p.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || p.Tags.Any(t => t.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return new ShopProductsResponse(items);
    }

    private Task<Order> OrderAsync(IpcContext context, ShopOrderRequest request, CancellationToken cancellationToken)
    {
        User user = context.RequireUser();
        if (!_shellSettings.Features.Shop)
        {
            throw IpcError.PolicyDenied("features.shop").ToException();
        }

        if (user.HasFlag(UserFlags.NoShop))
        {
            throw IpcError.PolicyDenied("noShop").ToException();
        }

        if (request.Items is null || request.Items.Count is < 1 or > OrderLineRequest.MaxLines)
        {
            throw IpcError.Validation("items", "count").ToException();
        }

        for (int i = 0; i < request.Items.Count; i++)
        {
            OrderLineRequest line = request.Items[i];
            if (line.ProductId == Guid.Empty)
            {
                throw IpcError.Validation($"items[{i}].productId", "required").ToException();
            }

            if (line.Qty is < 1 or > OrderLineRequest.MaxQty)
            {
                throw IpcError.Validation($"items[{i}].qty", "range").ToException();
            }
        }

        if (request.IdempotencyKey == Guid.Empty)
        {
            throw IpcError.Validation("idempotencyKey", "required").ToException();
        }

        if (request.Note is { Length: > MaxNoteLength })
        {
            throw IpcError.Validation("note", "max").ToException();
        }

        RequireOnline();
        var create = new OrderCreateRequest(user.Id, PcId, context.Session?.Id, request.Items, request.Note);
        return _server.CreateOrderAsync(create, request.IdempotencyKey, cancellationToken);
    }

    private async Task<Order> OrderStatusAsync(IpcContext context, ShopOrderStatusRequest request, CancellationToken cancellationToken)
    {
        User user = context.RequireUser();
        RequireOnline();
        Order order = await _server.GetOrderAsync(request.OrderId, cancellationToken).ConfigureAwait(false);
        return order.UserId == user.Id ? order : throw IpcError.NotFound("Order").ToException();
    }

    private async Task<ShopOrdersResponse> OrdersAsync(IpcContext context, ShopOrdersRequest? request, CancellationToken cancellationToken)
    {
        User user = context.RequireUser();
        RequireFeature(_shellSettings.Features.Shop, "features.shop");
        RequireOnline();
        PagedResult<Order> page = await _server.GetOrdersAsync(user.Id, request?.Page, request?.PageSize, request?.ActiveOnly, cancellationToken).ConfigureAwait(false);
        return new ShopOrdersResponse(page.Items, page.Total);
    }

    // ---- chat -------------------------------------------------------------------------------

    private Task<ChatHistoryResponse> ChatHistoryAsync(IpcContext context, ChatHistoryRequest? request, CancellationToken cancellationToken)
    {
        _ = context.RequireUser();
        RequireFeature(_shellSettings.Features.Chat, "features.chat");
        RequireOnline();
        int limit = Math.Clamp(request?.Limit ?? ChatHistoryRequest.DefaultLimit, 1, ChatHistoryRequest.MaxLimit);
        return _server.GetMessagesAsync(RoomOf(request?.RoomId), request?.Before, limit, cancellationToken);
    }

    private Task<ChatMessage> ChatSendAsync(IpcContext context, ChatSendRequest request, CancellationToken cancellationToken)
    {
        _ = context.RequireUser();
        RequireFeature(_shellSettings.Features.Chat, "features.chat");
        string text = request.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            throw IpcError.Validation("text", "required").ToException();
        }

        if (text.Length > ChatRooms.MaxTextLength)
        {
            throw IpcError.Validation("text", "max").ToException();
        }

        if (request.IdempotencyKey == Guid.Empty)
        {
            throw IpcError.Validation("idempotencyKey", "required").ToException();
        }

        RequireOnline();
        return _server.PostMessageAsync(RoomOf(request.RoomId), new ChatPostRequest(text), request.IdempotencyKey, cancellationToken);
    }

    private Task<ChatMarkReadResponse> ChatMarkReadAsync(IpcContext context, ChatMarkReadRequest request, CancellationToken cancellationToken)
    {
        _ = context.RequireUser();
        if (request.UpToMessageId == Guid.Empty)
        {
            throw IpcError.Validation("upToMessageId", "required").ToException();
        }

        RequireOnline();
        return _server.MarkReadAsync(RoomOf(request.RoomId), new ChatReadRequest(request.UpToMessageId), cancellationToken);
    }

    // ---- booking ----------------------------------------------------------------------------

    private async Task<BookingSeatsResponse> SeatsAsync(IpcContext context, BookingSeatsRequest request, CancellationToken cancellationToken)
    {
        RequireFeature(_shellSettings.Features.Booking, "features.booking");
        if (request.Date == default)
        {
            throw IpcError.Validation("date", "required").ToException();
        }

        RequireOnline();
        BookingSeatsResponse seats = await _server.GetSeatsAsync(request.Date, cancellationToken).ConfigureAwait(false);
        Guid me = context.User?.Id ?? Guid.Empty;
        IReadOnlyList<Booking> bookings = seats.Bookings.Select(b => b.UserId == me ? b : b with { UserId = Guid.Empty }).ToList();
        return seats with { Bookings = bookings, OpenFrom = null, OpenTo = null };
    }

    private Task<Booking> ReserveAsync(IpcContext context, BookingReserveRequest request, CancellationToken cancellationToken)
    {
        User user = context.RequireUser();
        RequireFeature(_shellSettings.Features.Booking, "features.booking");
        if (request.PcId == Guid.Empty)
        {
            throw IpcError.Validation("pcId", "required").ToException();
        }

        if (request.To <= request.From)
        {
            throw IpcError.Validation("to", "range", "'to' must be after 'from'").ToException();
        }

        if (request.From < _clock.UtcNow.AddMinutes(-1))
        {
            throw IpcError.Validation("from", "past").ToException();
        }

        if (request.To - request.From > MaxBookingDuration)
        {
            throw IpcError.Validation("to", "max", $"Bookings are limited to {MaxBookingDuration.TotalHours} hours").ToException();
        }

        RequireOnline();
        return _server.ReserveAsync(new BookingCreateRequest(user.Id, request.PcId, request.From, request.To), Guid.NewGuid(), cancellationToken);
    }

    private Task<Booking> CancelBookingAsync(IpcContext context, BookingCancelRequest request, CancellationToken cancellationToken)
    {
        _ = context.RequireUser();
        if (request.BookingId == Guid.Empty)
        {
            throw IpcError.Validation("bookingId", "required").ToException();
        }

        RequireOnline();
        return _server.CancelBookingAsync(request.BookingId, cancellationToken);
    }

    // ---- tournaments ------------------------------------------------------------------------

    private Task<TournamentsListResponse> TournamentsAsync(IpcContext context, TournamentsListRequest? request, CancellationToken cancellationToken)
    {
        RequireFeature(_shellSettings.Features.Tournaments, "features.tournaments");
        RequireOnline();
        return _server.GetTournamentsAsync(request?.State, request?.GameId, cancellationToken);
    }

    private Task<Tournament> JoinTournamentAsync(IpcContext context, TournamentsJoinRequest request, CancellationToken cancellationToken)
    {
        _ = context.RequireUser();
        RequireFeature(_shellSettings.Features.Tournaments, "features.tournaments");
        if (request.TournamentId == Guid.Empty)
        {
            throw IpcError.Validation("tournamentId", "required").ToException();
        }

        RequireOnline();
        return _server.JoinTournamentAsync(request.TournamentId, cancellationToken);
    }

    private Task<TournamentsLeaderboardResponse> LeaderboardAsync(IpcContext context, TournamentsLeaderboardRequest request, CancellationToken cancellationToken)
    {
        RequireFeature(_shellSettings.Features.Tournaments, "features.tournaments");
        if (request.TournamentId == Guid.Empty)
        {
            throw IpcError.Validation("tournamentId", "required").ToException();
        }

        int? limit = request.Limit is { } l ? Math.Clamp(l, 1, 100) : null;
        RequireOnline();
        return _server.GetLeaderboardAsync(request.TournamentId, limit, cancellationToken);
    }

    // ---- profile ----------------------------------------------------------------------------

    private async Task<User> ProfileGetAsync(IpcContext context, CancellationToken cancellationToken)
    {
        User user = context.RequireUser();
        if (!IsOnline)
        {
            return user;
        }

        try
        {
            User fresh = await _server.GetUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
            _users.Update(fresh);
            return fresh;
        }
        catch (Exception ex) when (IsConnectivity(ex))
        {
            _logger.LogDebug(ex, "profile.get: server unavailable; serving the cached user");
            return user;
        }
    }

    private async Task<User> ProfileUpdateAsync(IpcContext context, ProfileUpdateRequest request, CancellationToken cancellationToken)
    {
        User user = context.RequireUser();
        if (user.Role == UserRole.Guest)
        {
            throw IpcError.Forbidden("Guests cannot edit the profile", "guest").ToException();
        }

        if (request.DisplayName is { } name && (string.IsNullOrWhiteSpace(name) || name.Length > MaxDisplayNameLength))
        {
            throw IpcError.Validation("displayName", "length").ToException();
        }

        if (request.AvatarUrl is { } avatar && !AvatarUrls.IsValid(avatar))
        {
            throw IpcError.Validation("avatarUrl", "format").ToException();
        }

        if (request.Pin is { } pin && !(pin.Length is >= 4 and <= 6 && pin.All(char.IsAsciiDigit)))
        {
            throw IpcError.Validation("pin", "format", "PIN must be 4–6 digits").ToException();
        }

        if (request.DisplayName is null && request.AvatarUrl is null && request.Locale is null && request.Pin is null)
        {
            throw IpcError.Validation("payload", "empty").ToException();
        }

        RequireOnline();
        User updated = await _server.UpdateUserAsync(user.Id, request, cancellationToken).ConfigureAwait(false);
        if (request.Pin is not null && !updated.HasFlag(SessionLock.PinSetFlag))
        {
            // An older server without the flag: the PIN is set all the same.
            updated = updated with { Flags = [.. updated.Flags, SessionLock.PinSetFlag] };
        }

        _users.Update(updated);
        if (request.Pin is { } newPin)
        {
            // Only a salted hash is kept, for an unlock while the server is unreachable (G12).
            try
            {
                await _lock.RememberPinAsync(user.Id, newPin, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "The new PIN of {UserId} could not be cached for offline unlock", user.Id);
            }
        }

        if (request.Locale is { } locale)
        {
            _ = _shellSettings.SetLocale(locale);
        }

        return updated;
    }

    // ---- helpers ----------------------------------------------------------------------------

    private Guid PcId => _settings.CurrentValue.PcId ?? _connection.PcId ?? Guid.Empty;

    private bool IsOnline => _connection.Connectivity == ConnectivityState.Online;

    private string RoomOf(string? roomId) => string.IsNullOrWhiteSpace(roomId) ? ChatRooms.ForPc(PcId) : roomId.Trim();

    private void RequireOnline()
    {
        if (!IsOnline)
        {
            throw IpcError.AgentOffline().ToException();
        }
    }

    private Task<T> Online<T>(Func<Task<T>> call)
    {
        RequireOnline();
        return call();
    }

    private static void RequireFeature(bool enabled, string rule)
    {
        if (!enabled)
        {
            throw IpcError.PolicyDenied(rule).ToException();
        }
    }

    private static bool IsConnectivity(Exception exception) =>
        exception is HttpRequestException || exception is ServerApiException { IsRetryable: true };

    private async Task<T> FetchOrCachedAsync<T>(ContractFileCache<T> cache, Func<string?, CancellationToken, Task<EtagResponse<T>>> fetch, CancellationToken cancellationToken)
        where T : class
    {
        T? cached = cache.Get();
        if (!IsOnline)
        {
            return cached ?? throw IpcError.AgentOffline().ToException();
        }

        try
        {
            EtagResponse<T> response = await fetch(cached is null ? null : cache.ETag, cancellationToken).ConfigureAwait(false);
            if (response.NotModified && cached is not null)
            {
                return cached;
            }

            T value = response.Require();
            cache.Set(value, response.ETag);
            return value;
        }
        catch (Exception ex) when (IsConnectivity(ex) && cached is not null)
        {
            _logger.LogWarning("{Type}: server unavailable; serving the cached copy", typeof(T).Name);
            return cached;
        }
    }

    // ponytail: in-memory last balance only; a disk cache is unnecessary until offline balance across restarts matters.
}

/// <summary>Disk + memory cache of one contract document (<c>cache\*.json</c>), with the last ETag kept in memory.</summary>
/// <typeparam name="T">Contract type registered in <see cref="ContractsJsonContext"/>.</typeparam>
internal sealed class ContractFileCache<T>
    where T : class
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private T? _value;
    private string? _etag;
    private bool _loaded;

    public ContractFileCache(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
    }

    /// <summary>ETag of the cached value (memory only).</summary>
    public string? ETag
    {
        get
        {
            lock (_gate)
            {
                return _etag;
            }
        }
    }

    /// <summary>Cached value, read from disk on first use.</summary>
    public T? Get()
    {
        lock (_gate)
        {
            if (!_loaded)
            {
                _loaded = true;
                _value = Read();
            }

            return _value;
        }
    }

    /// <summary>Stores a fresh value in memory and (best effort) on disk.</summary>
    public void Set(T value, string? etag)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            _value = value;
            _etag = etag;
            _loaded = true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temp = _path + ".tmp";
                File.WriteAllBytes(temp, JsonDefaults.SerializeToUtf8Bytes(value));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Cache file {Path} could not be written", _path);
            }
        }
    }

    private T? Read()
    {
        try
        {
            return File.Exists(_path) ? JsonDefaults.Deserialize<T>(File.ReadAllBytes(_path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Cache file {Path} could not be read", _path);
            return null;
        }
    }
}
