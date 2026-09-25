using System.Buffers;
using System.Text.Json;
using ClubShell.Agent.Policy;
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
    private readonly SessionManager _sessions;
    private readonly SessionLock _lock;
    private readonly OfflineSessionStore _store;
    private readonly IServerClient _server;
    private readonly Hwid _hwid;
    private readonly ShellUserContext _users;
    private readonly ShellTokenStore _token;
    private readonly ServerConnection _connection;
    private readonly IPolicyEnforcer _policy;
    private readonly IKioskCredentials _kiosk;
    private readonly ShellSettingsStore _shellSettings;
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<SessionHandlers> _logger;

    /// <summary>Creates the group. <paramref name="services"/> resolves <see cref="ShellUpdater"/> lazily (it depends on the watchdog).</summary>
    public SessionHandlers(
        SessionManager sessions,
        SessionLock sessionLock,
        OfflineSessionStore store,
        IServerClient server,
        Hwid hwid,
        ShellUserContext users,
        ShellTokenStore token,
        ServerConnection connection,
        IPolicyEnforcer policy,
        IKioskCredentials kiosk,
        ShellSettingsStore shellSettings,
        IServiceProvider services,
        IOptionsMonitor<AgentSettings> settings,
        IClock clock,
        ILogger<SessionHandlers> logger)
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
    }

    /// <inheritdoc />
    public void Register(MessageDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        dispatcher.Register<AuthHelloRequest, AuthHelloResponse>(IpcMessages.Auth.Hello, HelloAsync);
        dispatcher.Register<AuthLoginRequest, AuthLoginResponse>(IpcMessages.Auth.Login, LoginAsync);
        dispatcher.RegisterOptional<AuthLogoutRequest, AuthLogoutResponse>(IpcMessages.Auth.Logout, LogoutAsync);
        dispatcher.RegisterNoPayload<AuthStatusResponse>(IpcMessages.Auth.Status, (_, _) => Task.FromResult(Status()));
        dispatcher.RegisterNoPayload<QrLoginStart>(IpcMessages.Auth.QrStart, QrStartAsync);

        dispatcher.RegisterNoPayload<PlaySession?>(IpcMessages.Session.Get, (context, _) => Task.FromResult(SessionOf(context.User)));
        dispatcher.Register<SessionStartRequest, PlaySession>(IpcMessages.Session.Start, StartAsync);
        dispatcher.RegisterOptional<SessionPauseRequest, PlaySession>(IpcMessages.Session.Pause, (_, request, cancellationToken) => _sessions.PauseAsync(request?.Reason, cancellationToken));
        dispatcher.RegisterNoPayload<PlaySession>(IpcMessages.Session.Resume, (_, cancellationToken) => _sessions.ResumeAsync(cancellationToken));
        dispatcher.RegisterOptional<SessionEndRequest, SessionEndResult>(IpcMessages.Session.End, (_, request, cancellationToken) => _sessions.EndAsync(request?.Reason ?? SessionEndReason.User, cancellationToken));
        dispatcher.Register<SessionExtendRequest, PlaySession>(IpcMessages.Session.Extend, ExtendAsync);
        dispatcher.RegisterOptional<SessionLockRequest, PlaySession>(IpcMessages.Session.Lock, (_, request, cancellationToken) => _lock.LockAsync(request?.Reason, cancellationToken));
        dispatcher.RegisterOptional<SessionUnlockRequest, PlaySession>(IpcMessages.Session.Unlock, (context, request, cancellationToken) => _lock.UnlockAsync(context.RequireUser(), request ?? new SessionUnlockRequest(), cancellationToken));
        dispatcher.RegisterNoPayload<SessionTimeLeftResponse>(IpcMessages.Session.TimeLeft, (_, _) => Task.FromResult(_sessions.GetTimeLeft()));
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

        await CacheUserAsync(request.Username ?? auth.User.Username, auth, cancellationToken).ConfigureAwait(false);
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

        _logger.LogInformation("User {UserId} ({Username}) logged in via {Kind} (online); session {SessionId}", auth.User.Id, auth.User.Username, request.Kind, session?.Id);
        return new AuthLoginResponse(auth.User, session, auth.ExpiresAt, ConnectivityState.Online);
    }

    private async Task<AuthResponse> LoginOnlineAsync(AuthLoginRequest request, Guid pcId, string hwid, CancellationToken cancellationToken)
    {
        switch (request.Kind)
        {
            case AuthKind.Guest:
                return await _server.GuestLoginAsync(new GuestAuthRequest(pcId, hwid, null, _shellSettings.Get().Locale), cancellationToken).ConfigureAwait(false);
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
        _users.Set(user, expiresAt, ConnectivityState.Offline);
        _logger.LogInformation("User {UserId} ({Username}) logged in from the offline cache", user.Id, user.Username);
        return new AuthLoginResponse(user, SessionOf(user), expiresAt, ConnectivityState.Offline);
    }

    private async Task<AuthLogoutResponse> LogoutAsync(IpcContext context, AuthLogoutRequest? request, CancellationToken cancellationToken)
    {
        SessionEndReason reason = request?.Reason ?? SessionEndReason.User;
        if (reason is not (SessionEndReason.User or SessionEndReason.Idle or SessionEndReason.Admin))
        {
            throw IpcError.Validation("reason", "invalid").ToException();
        }

        User user = context.RequireUser();
        PlaySession? ended = null;
        bool sessionEnded = false;
        if (_sessions.State.IsOpen())
        {
            SessionEndResult result = await _sessions.EndAsync(reason, cancellationToken).ConfigureAwait(false);
            ended = result.Session;
            sessionEnded = true;
        }

        if (IsOnline)
        {
            try
            {
                await _server.LogoutAsync(new LogoutRequest(reason), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ServerApiException or HttpRequestException)
            {
                _logger.LogDebug(ex, "Server logout failed; user tokens dropped locally");
            }
        }

        _lock.ForgetPin(user.Id);
        _users.Clear();
        _logger.LogInformation("User {UserId} logged out ({Reason}); session ended: {SessionEnded}", user.Id, reason, sessionEnded);
        return new AuthLogoutResponse(true, sessionEnded, ended);
    }

    private AuthStatusResponse Status()
    {
        User? user = _users.User;
        return user is null
            ? new AuthStatusResponse(false, _connection.Connectivity)
            : new AuthStatusResponse(true, _users.Mode, user, SessionOf(user), _users.ExpiresAt);
    }

    private async Task<QrLoginStart> QrStartAsync(IpcContext context, CancellationToken cancellationToken)
    {
        RequireOnline();
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

        if (!request.Prepaid && !user.IsMember)
        {
            throw IpcError.PolicyDenied("postpaidNotAllowed").ToException();
        }

        Guid pcId = PcId;
        if (pcId == Guid.Empty)
        {
            throw IpcError.AgentOffline().ToException();
        }

        return _sessions.StartAsync(new SessionCreateRequest(pcId, user.Id, request.TariffId, request.Minutes, request.Prepaid), cancellationToken);
    }

    private Task<PlaySession> ExtendAsync(IpcContext context, SessionExtendRequest request, CancellationToken cancellationToken)
    {
        if (request.Minutes <= 0)
        {
            throw IpcError.Validation("minutes", "min").ToException();
        }

        return _sessions.ExtendAsync(request.Minutes, request.TariffId, cancellationToken);
    }

    // ---- helpers ----------------------------------------------------------------------------

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
                if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length is < 3 or > 32)
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
    private readonly ServerConnection _connection;
    private readonly SessionLock _lock;
    private readonly ShellSettingsStore _shellSettings;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<UserDomainHandlers> _logger;
    private readonly ContractFileCache<TariffsResponse> _tariffs;
    private readonly ContractFileCache<ShopProductsResponse> _products;
    private Balance? _lastBalance;

    /// <summary>Creates the group.</summary>
    public UserDomainHandlers(
        IServerClient server,
        ShellUserContext users,
        ISessionService sessions,
        ServerConnection connection,
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
                _lastBalance = balance;
                return balance;
            }
            catch (Exception ex) when (IsConnectivity(ex))
            {
                _logger.LogWarning("wallet.balance: server unavailable; serving the cached balance");
            }
        }

        Balance? cached = _lastBalance;
        return cached is not null && cached.UserId == user.Id
            ? cached
            : new Balance(user.Id, user.Balance, Money.Zero, user.Balance.Currency, user.LastSeenAt ?? user.CreatedAt);
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

        if (request.AvatarUrl is { } avatar && !(Uri.TryCreate(avatar, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)))
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
        _users.Update(updated);
        if (request.Pin is { } newPin)
        {
            _lock.RememberPin(user.Id, newPin);
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
