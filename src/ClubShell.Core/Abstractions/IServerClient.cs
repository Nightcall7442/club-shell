using System.Net;
using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Shop;
using ClubShell.Contracts.Users;
using ClubShell.Contracts.Wallet;

namespace ClubShell.Core.Abstractions;

/// <summary>
/// Failure of a central-server call (SERVER_API.md §3). Carries the parsed error envelope when the server sent one;
/// transport failures map to <see cref="ErrorCode.ServerUnavailable"/> / <see cref="ErrorCode.Timeout"/>.
/// </summary>
public sealed class ServerApiException : Exception
{
    /// <summary>Creates a generic internal error.</summary>
    public ServerApiException()
        : this(ErrorCode.Internal, HttpStatusCode.InternalServerError, null, null)
    {
    }

    /// <summary>Creates a generic internal error with a message.</summary>
    public ServerApiException(string message)
        : this(ErrorCode.Internal, HttpStatusCode.InternalServerError, null, null, message)
    {
    }

    /// <summary>Creates a generic internal error with a message and inner exception.</summary>
    public ServerApiException(string message, Exception innerException)
        : this(ErrorCode.Internal, HttpStatusCode.InternalServerError, null, null, message, innerException)
    {
    }

    /// <summary>Creates an exception from an HTTP outcome.</summary>
    /// <param name="code">Error code (from the envelope, or derived from the status).</param>
    /// <param name="status">HTTP status; <see cref="HttpStatusCode.ServiceUnavailable"/> / <see cref="HttpStatusCode.GatewayTimeout"/> for transport failures.</param>
    /// <param name="error">Parsed server error envelope, when any.</param>
    /// <param name="traceId">Trace id (<c>X-Trace-Id</c>), when known.</param>
    /// <param name="message">Override message; defaults to the envelope message or the code description.</param>
    /// <param name="innerException">Transport exception, when any.</param>
    public ServerApiException(
        ErrorCode code,
        HttpStatusCode status,
        ServerError? error,
        string? traceId,
        string? message = null,
        Exception? innerException = null)
        : base(message ?? error?.Message ?? code.Describe(), innerException)
    {
        Code = code;
        Status = status;
        Error = error;
        TraceId = traceId ?? error?.TraceId;
    }

    /// <summary>Error code.</summary>
    public ErrorCode Code { get; }

    /// <summary>HTTP status of the response (or the equivalent for transport failures).</summary>
    public HttpStatusCode Status { get; }

    /// <summary>Server error envelope, when the response carried one.</summary>
    public ServerError? Error { get; }

    /// <summary>Trace id for log correlation.</summary>
    public string? TraceId { get; }

    /// <summary><c>details.reason</c> of the envelope (<c>expired</c>, <c>clockSkew</c>, <c>userToken</c>, <c>pendingApproval</c>, …), when present.</summary>
    public string? Reason =>
        Error?.Details is { ValueKind: JsonValueKind.Object } details
        && details.TryGetProperty("reason", out var reason)
        && reason.ValueKind == JsonValueKind.String
            ? reason.GetString()
            : null;

    /// <summary><see langword="true"/> when the same call may succeed later (<see cref="ErrorCodes.IsRetryable"/>).</summary>
    public bool IsRetryable => Code.IsRetryable();

    /// <summary><see langword="true"/> for 401/403 class failures.</summary>
    public bool IsAuthFailure => Code.IsAuthFailure();

    /// <summary>Maps to the IPC error returned to the Shell (SERVER_API.md §3: details passed through, trace id moved into <c>details.traceId</c>).</summary>
    public IpcError ToIpcError()
    {
        if (Error is not null)
        {
            return Error.ToIpcError();
        }

        return TraceId is null
            ? IpcError.Of(Code, Message)
            : new IpcError(Code, Message, IpcError.WithTraceId(null, TraceId));
    }
}

/// <summary>Result of a conditional (<c>If-None-Match</c>) GET.</summary>
/// <typeparam name="T">Body type.</typeparam>
/// <param name="Value">Fresh body; <see langword="null"/> when <paramref name="NotModified"/>.</param>
/// <param name="ETag">ETag of the current representation (echoed on 304), to store with the cache.</param>
/// <param name="NotModified"><see langword="true"/> when the server answered 304 and the cached copy is current.</param>
public sealed record EtagResponse<T>(T? Value, string? ETag, bool NotModified)
    where T : class
{
    /// <summary>A 304 result carrying the ETag that was sent.</summary>
    public static EtagResponse<T> Unchanged(string? etag) => new(null, etag, true);

    /// <summary>Returns <see cref="Value"/> or throws when the result is a 304 (caller has no cache).</summary>
    public T Require() => Value ?? throw new InvalidOperationException("Server returned 304 Not Modified but no cached value is available");
}

/// <summary>Agent identity, heartbeat, telemetry, config, policies, commands and club-wide PC list (SERVER_API.md §4.1–4.2).</summary>
public interface IAgentApi
{
    /// <summary><c>POST /agents/register</c> (auth: <c>X-Club-Key</c>). On success the returned tokens are stored in the token store.</summary>
    Task<AgentRegisterResponse> RegisterAsync(AgentRegisterRequest request, CancellationToken cancellationToken);

    /// <summary><c>POST /agents/refresh</c> using the stored refresh token and the hardware id; rotates stored tokens. Single-flight.</summary>
    Task<AgentRefreshResponse> RefreshAsync(CancellationToken cancellationToken);

    /// <summary><c>POST /agents/{pcId}/heartbeat</c>. Also updates the server time offset.</summary>
    Task<HeartbeatResponse> HeartbeatAsync(Guid pcId, HeartbeatRequest request, CancellationToken cancellationToken);

    /// <summary><c>POST /agents/{pcId}/telemetry</c>.</summary>
    Task SendTelemetryAsync(Guid pcId, TelemetryBatch batch, CancellationToken cancellationToken);

    /// <summary><c>GET /agents/{pcId}/config</c> with ETag support.</summary>
    Task<EtagResponse<AgentServerConfig>> GetConfigAsync(Guid pcId, string? etag, CancellationToken cancellationToken);

    /// <summary><c>GET /agents/{pcId}/policies</c> with ETag support.</summary>
    Task<EtagResponse<Policy>> GetPoliciesAsync(Guid pcId, string? etag, CancellationToken cancellationToken);

    /// <summary><c>GET /agents/{pcId}/commands</c> (fallback when WS is down).</summary>
    Task<ServerCommandsResponse> GetCommandsAsync(Guid pcId, CancellationToken cancellationToken);

    /// <summary><c>POST /agents/{pcId}/commands/{commandId}/ack</c>.</summary>
    Task AckCommandAsync(Guid pcId, Guid commandId, CommandAck ack, CancellationToken cancellationToken);

    /// <summary><c>GET /pcs?zone=</c>.</summary>
    Task<PcsResponse> GetPcsAsync(string? zone, CancellationToken cancellationToken);

    /// <summary><c>GET /pcs/{pcId}</c>.</summary>
    Task<Pc> GetPcAsync(Guid pcId, CancellationToken cancellationToken);
}

/// <summary>User authentication and profile (SERVER_API.md §4.3–4.4).</summary>
public interface IAuthApi
{
    /// <summary><c>POST /auth/login</c>.</summary>
    Task<AuthResponse> LoginAsync(AuthRequest request, CancellationToken cancellationToken);

    /// <summary><c>POST /auth/qr/start</c>.</summary>
    Task<QrLoginStart> StartQrLoginAsync(QrStartRequest request, CancellationToken cancellationToken);

    /// <summary><c>GET /auth/qr/{token}</c>.</summary>
    Task<QrLoginStatus> GetQrLoginStatusAsync(string qrToken, CancellationToken cancellationToken);

    /// <summary><c>POST /auth/guest</c>.</summary>
    Task<AuthResponse> GuestLoginAsync(GuestAuthRequest request, CancellationToken cancellationToken);

    /// <summary><c>POST /auth/logout</c> (auth: user).</summary>
    Task LogoutAsync(LogoutRequest request, CancellationToken cancellationToken);

    /// <summary><c>GET /users/{userId}</c>.</summary>
    Task<User> GetUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary><c>PATCH /users/{userId}</c>.</summary>
    Task<User> UpdateUserAsync(Guid userId, ProfileUpdateRequest request, CancellationToken cancellationToken);

    /// <summary><c>GET /users/{userId}/stats</c>.</summary>
    Task<UserStats> GetUserStatsAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary><c>GET /users/{userId}/achievements</c>.</summary>
    Task<ProfileAchievementsResponse> GetUserAchievementsAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary><c>GET /users/{userId}/loyalty</c>.</summary>
    Task<Loyalty> GetUserLoyaltyAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Sessions (SERVER_API.md §4.5).</summary>
public interface ISessionApi
{
    /// <summary><c>GET /sessions/current?pcId=</c>; <see langword="null"/> on 204.</summary>
    Task<Session?> GetCurrentSessionAsync(Guid pcId, CancellationToken cancellationToken);

    /// <summary><c>POST /sessions</c> (auth: user, idempotent).</summary>
    Task<Session> CreateSessionAsync(SessionCreateRequest request, Guid idempotencyKey, CancellationToken cancellationToken);

    /// <summary><c>POST /sessions/{id}/pause</c>.</summary>
    Task<Session> PauseSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary><c>POST /sessions/{id}/resume</c>.</summary>
    Task<Session> ResumeSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary><c>POST /sessions/{id}/end</c>. A <c>sessionNotActive</c> conflict whose <c>details.session.state</c> is <c>ended</c> is returned as success.</summary>
    Task<SessionEndResult> EndSessionAsync(Guid sessionId, SessionEndReport report, CancellationToken cancellationToken);

    /// <summary><c>POST /sessions/{id}/extend</c> (auth: user, idempotent).</summary>
    Task<Session> ExtendSessionAsync(Guid sessionId, SessionExtendRequest request, Guid idempotencyKey, CancellationToken cancellationToken);

    /// <summary><c>POST /sessions/{id}/events</c> (idempotent, ≤ 100 events).</summary>
    Task PostSessionEventsAsync(Guid sessionId, SessionEventsBatch batch, Guid idempotencyKey, CancellationToken cancellationToken);
}

/// <summary>Games catalogue, account pool, launch reports and apps (SERVER_API.md §4.6–4.7).</summary>
public interface IGamesApi
{
    /// <summary><c>GET /games</c> with ETag support.</summary>
    Task<EtagResponse<GamesListResponse>> GetGamesAsync(string? zone, int? page, int? pageSize, string? etag, CancellationToken cancellationToken);

    /// <summary><c>GET /games/{id}</c>.</summary>
    Task<Game> GetGameAsync(Guid gameId, CancellationToken cancellationToken);

    /// <summary><c>GET /games/{id}/accounts/lease?sessionId=</c> (auth: user).</summary>
    Task<AccountLease> LeaseAccountAsync(Guid gameId, Guid sessionId, CancellationToken cancellationToken);

    /// <summary><c>POST /games/{id}/accounts/{leaseId}/release</c>; 404 is treated as success.</summary>
    Task ReleaseAccountLeaseAsync(Guid gameId, Guid leaseId, AccountLeaseRelease release, CancellationToken cancellationToken);

    /// <summary><c>GET /games/{id}/accounts/{leaseId}/save-upload</c>.</summary>
    Task<SaveUploadTarget> GetSaveUploadTargetAsync(Guid gameId, Guid leaseId, CancellationToken cancellationToken);

    /// <summary><c>POST /games/{id}/launch-report</c>.</summary>
    Task SendLaunchReportAsync(Guid gameId, LaunchReport report, CancellationToken cancellationToken);

    /// <summary><c>GET /apps</c> with ETag support.</summary>
    Task<EtagResponse<AppsListResponse>> GetAppsAsync(string? etag, CancellationToken cancellationToken);
}

/// <summary>Wallet, tariffs and top-ups (SERVER_API.md §4.8).</summary>
public interface IWalletApi
{
    /// <summary><c>GET /wallet/{userId}/balance</c>.</summary>
    Task<Balance> GetBalanceAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary><c>GET /wallet/{userId}/transactions</c>.</summary>
    Task<PagedResult<Transaction>> GetTransactionsAsync(Guid userId, WalletHistoryRequest query, CancellationToken cancellationToken);

    /// <summary><c>GET /tariffs?zone=</c> with ETag support.</summary>
    Task<EtagResponse<TariffsResponse>> GetTariffsAsync(string? zone, string? etag, CancellationToken cancellationToken);

    /// <summary><c>POST /wallet/{userId}/topup-intent</c> (idempotent).</summary>
    Task<TopupIntent> CreateTopupIntentAsync(Guid userId, TopupIntentCreateRequest request, Guid idempotencyKey, CancellationToken cancellationToken);

    /// <summary><c>GET /wallet/{userId}/topup-intent/{id}</c>.</summary>
    Task<TopupIntent> GetTopupIntentAsync(Guid userId, Guid intentId, CancellationToken cancellationToken);
}

/// <summary>Shop (SERVER_API.md §4.9).</summary>
public interface IShopApi
{
    /// <summary><c>GET /shop/products?category=</c> with ETag support.</summary>
    Task<EtagResponse<ShopProductsResponse>> GetProductsAsync(ProductCategory? category, string? etag, CancellationToken cancellationToken);

    /// <summary><c>POST /shop/orders</c> (idempotent).</summary>
    Task<Order> CreateOrderAsync(OrderCreateRequest request, Guid idempotencyKey, CancellationToken cancellationToken);

    /// <summary><c>GET /shop/orders/{id}</c>.</summary>
    Task<Order> GetOrderAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary><c>GET /shop/orders?userId=</c>.</summary>
    Task<PagedResult<Order>> GetOrdersAsync(Guid userId, int? page, int? pageSize, bool? activeOnly, CancellationToken cancellationToken);

    /// <summary><c>POST /shop/orders/{id}/cancel</c>.</summary>
    Task<Order> CancelOrderAsync(Guid orderId, CancellationToken cancellationToken);
}

/// <summary>Chat (SERVER_API.md §4.10).</summary>
public interface IChatApi
{
    /// <summary><c>GET /chat/{roomId}/messages</c>.</summary>
    Task<ChatHistoryResponse> GetMessagesAsync(string roomId, Guid? before, int? limit, CancellationToken cancellationToken);

    /// <summary><c>POST /chat/{roomId}/messages</c> (idempotent).</summary>
    Task<ChatMessage> PostMessageAsync(string roomId, ChatPostRequest request, Guid idempotencyKey, CancellationToken cancellationToken);

    /// <summary><c>POST /chat/{roomId}/read</c>.</summary>
    Task<ChatMarkReadResponse> MarkReadAsync(string roomId, ChatReadRequest request, CancellationToken cancellationToken);
}

/// <summary>Booking (SERVER_API.md §4.11).</summary>
public interface IBookingApi
{
    /// <summary><c>GET /booking/seats?date=</c>.</summary>
    Task<BookingSeatsResponse> GetSeatsAsync(DateOnly day, CancellationToken cancellationToken);

    /// <summary><c>POST /booking/reserve</c> (idempotent).</summary>
    Task<Booking> ReserveAsync(BookingCreateRequest request, Guid idempotencyKey, CancellationToken cancellationToken);

    /// <summary><c>DELETE /booking/{id}</c>.</summary>
    Task<Booking> CancelBookingAsync(Guid bookingId, CancellationToken cancellationToken);
}

/// <summary>Tournaments (SERVER_API.md §4.12).</summary>
public interface ITournamentsApi
{
    /// <summary><c>GET /tournaments?state=&amp;gameId=</c>.</summary>
    Task<TournamentsListResponse> GetTournamentsAsync(TournamentState? state, Guid? gameId, CancellationToken cancellationToken);

    /// <summary><c>POST /tournaments/{id}/join</c> (auth: user).</summary>
    Task<Tournament> JoinTournamentAsync(Guid tournamentId, CancellationToken cancellationToken);

    /// <summary><c>GET /tournaments/{id}/leaderboard?limit=</c>.</summary>
    Task<TournamentsLeaderboardResponse> GetLeaderboardAsync(Guid tournamentId, int? limit, CancellationToken cancellationToken);
}

/// <summary>Updates (SERVER_API.md §4.13).</summary>
public interface IUpdatesApi
{
    /// <summary><c>GET /updates/{channel}/manifest?component=&amp;current=&amp;arch=x64</c>; <see langword="null"/> on 204 (up to date).</summary>
    Task<UpdateManifest?> GetUpdateManifestAsync(UpdateChannel channel, UpdateComponent component, string currentVersion, CancellationToken cancellationToken);
}

/// <summary>Support tickets and anti-cheat reports (SERVER_API.md §4.14).</summary>
public interface ISupportApi
{
    /// <summary><c>POST /support/call-admin</c> (idempotent).</summary>
    Task<SysCallAdminResponse> CallAdminAsync(CallAdminTicketRequest request, Guid idempotencyKey, CancellationToken cancellationToken);

    /// <summary><c>POST /anticheat/report</c>.</summary>
    Task ReportAntiCheatAsync(AntiCheatReport report, CancellationToken cancellationToken);
}

/// <summary>
/// Typed client of the central server REST API (SERVER_API.md). Every method throws <see cref="ServerApiException"/>
/// on non-2xx responses and transport failures, and <see cref="OperationCanceledException"/> on cancellation.
/// </summary>
public interface IServerClient : IAgentApi, IAuthApi, ISessionApi, IGamesApi, IWalletApi, IShopApi, IChatApi, IBookingApi, ITournamentsApi, IUpdatesApi, ISupportApi
{
    /// <summary>Estimated <c>server − local</c> clock offset, learned from <c>X-Server-Time</c> / heartbeat responses.</summary>
    TimeSpan ServerTimeOffset { get; }

    /// <summary>Best estimate of the server's current time.</summary>
    DateTimeOffset ServerNow { get; }

    /// <summary>Shell version sent as <c>X-Shell-Version</c>; updated by the Agent after <c>auth.hello</c>.</summary>
    string ShellVersion { get; set; }

    /// <summary>Value of <c>Accept-Language</c> (<c>ru</c>, <c>uz</c>, <c>en</c>) for localized catalogue fields; <see langword="null"/> = not sent.</summary>
    string? AcceptLanguage { get; set; }
}
