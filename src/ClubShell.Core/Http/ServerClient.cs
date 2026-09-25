using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Shop;
using ClubShell.Contracts.Users;
using ClubShell.Contracts.Wallet;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace ClubShell.Core.Http;

/// <summary>Version of the running component, derived from the entry assembly's informational version.</summary>
public static class ClubShellVersion
{
    /// <summary>Semver of the running process (build metadata after <c>+</c> stripped); <c>0.0.0</c> when unknown.</summary>
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(ClubShellVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }
}

/// <summary>
/// <see cref="IServerClient"/> over the resilient <see cref="RetryPolicy.HttpClientName"/> client: Bearer agent JWT with
/// HMAC request signing (SERVER_API.md §2.2), single-flight token refresh on <c>401</c>, one automatic retry after a
/// <c>clockSkew</c> rejection using <c>X-Server-Time</c>, <c>Idempotency-Key</c> on creating POSTs, ETag conditional GETs,
/// and error-envelope parsing into <see cref="ServerApiException"/>. Register/login responses are stored in the
/// <see cref="ITokenStore"/> automatically.
/// </summary>
public sealed class ServerClient : IServerClient, IDisposable
{
    private const string TraceHeader = "X-Trace-Id";
    private const string AgentVersionHeader = "X-Agent-Version";
    private const string ShellVersionHeader = "X-Shell-Version";
    private const string UserTokenHeader = "X-User-Token";
    private const string ClubKeyHeader = "X-Club-Key";
    private const string ReasonClockSkew = "clockSkew";
    private const string ReasonUserToken = "userToken";
    private const string DefaultArch = "x64";

    private static readonly AsyncLocal<Guid?> CurrentTrace = new();

    private readonly HttpClient _http;
    private readonly ITokenStore _tokens;
    private readonly Hwid _hwid;
    private readonly IClock _clock;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly ILogger<ServerClient> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private long _serverOffsetTicks;
    private bool _disposed;

    /// <summary>Creates the client over the <see cref="RetryPolicy.HttpClientName"/> named client.</summary>
    public ServerClient(
        IHttpClientFactory httpClientFactory,
        ITokenStore tokens,
        Hwid hwid,
        IClock clock,
        IOptionsMonitor<AgentSettings> settings,
        ILogger<ServerClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(hwid);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _http = httpClientFactory.CreateClient(RetryPolicy.HttpClientName);
        _tokens = tokens;
        _hwid = hwid;
        _clock = clock;
        _settings = settings;
        _logger = logger;
    }

    private enum AuthMode
    {
        None,
        Club,
        Agent,
        User,
    }

    /// <inheritdoc />
    public TimeSpan ServerTimeOffset => TimeSpan.FromTicks(Volatile.Read(ref _serverOffsetTicks));

    /// <inheritdoc />
    public DateTimeOffset ServerNow => _clock.UtcNow + ServerTimeOffset;

    /// <inheritdoc />
    public string ShellVersion { get; set; } = "0.0.0";

    /// <inheritdoc />
    public string? AcceptLanguage { get; set; }

    /// <summary>Agent version sent as <c>X-Agent-Version</c> / <c>User-Agent</c>.</summary>
    public string AgentVersion { get; } = ClubShellVersion.Current;

    /// <summary>
    /// Sets the <c>X-Trace-Id</c> for calls made in the current async flow until the returned scope is disposed
    /// (the Agent passes the IPC envelope id, ARCHITECTURE.md §10).
    /// </summary>
    public static IDisposable BeginTrace(Guid traceId)
    {
        var previous = CurrentTrace.Value;
        CurrentTrace.Value = traceId;
        return new TraceScope(previous);
    }

    #region Agents / PCs

    /// <inheritdoc />
    public async Task<AgentRegisterResponse> RegisterAsync(AgentRegisterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await SendJsonAsync<AgentRegisterRequest, AgentRegisterResponse>(HttpMethod.Post, Endpoints.AgentsRegister, request, AuthMode.Club, null, cancellationToken).ConfigureAwait(false);
        await _tokens.SetAgentAsync(new AgentTokens(response.PcId, response.AccessToken, response.RefreshToken, response.SigningSecret, response.ExpiresAt), cancellationToken).ConfigureAwait(false);
        ApplyServerTime(response.ServerTime);
        _logger.LogInformation("Registered as PC {PcId} ({PcName})", response.PcId, response.Pc.Name);
        return response;
    }

    /// <inheritdoc />
    public async Task<AgentRefreshResponse> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _tokens.Agent ?? throw NotRegistered();
            return await RefreshCoreAsync(current, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<HeartbeatResponse> HeartbeatAsync(Guid pcId, HeartbeatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await SendJsonAsync<HeartbeatRequest, HeartbeatResponse>(HttpMethod.Post, Endpoints.AgentHeartbeat(pcId), request, AuthMode.Agent, null, cancellationToken).ConfigureAwait(false);
        ApplyServerTime(response.ServerTime);
        return response;
    }

    /// <inheritdoc />
    public Task SendTelemetryAsync(Guid pcId, TelemetryBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return SendJsonNoContentAsync(HttpMethod.Post, Endpoints.AgentTelemetry(pcId), batch, AuthMode.Agent, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<EtagResponse<AgentServerConfig>> GetConfigAsync(Guid pcId, string? etag, CancellationToken cancellationToken) =>
        GetConditionalAsync<AgentServerConfig>(Endpoints.AgentConfig(pcId), etag, AuthMode.Agent, cancellationToken);

    /// <inheritdoc />
    public Task<EtagResponse<Policy>> GetPoliciesAsync(Guid pcId, string? etag, CancellationToken cancellationToken) =>
        GetConditionalAsync<Policy>(Endpoints.AgentPolicies(pcId), etag, AuthMode.Agent, cancellationToken);

    /// <inheritdoc />
    public Task<ServerCommandsResponse> GetCommandsAsync(Guid pcId, CancellationToken cancellationToken) =>
        GetAsync<ServerCommandsResponse>(Endpoints.AgentCommands(pcId), AuthMode.Agent, cancellationToken);

    /// <inheritdoc />
    public Task AckCommandAsync(Guid pcId, Guid commandId, CommandAck ack, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ack);
        return SendJsonNoContentAsync(HttpMethod.Post, Endpoints.AgentCommandAck(pcId, commandId), ack, AuthMode.Agent, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PcsResponse> GetPcsAsync(string? zone, CancellationToken cancellationToken) =>
        GetAsync<PcsResponse>(Endpoints.Pcs + new QueryBuilder().Add("zone", zone), AuthMode.Agent, cancellationToken);

    /// <inheritdoc />
    public Task<Pc> GetPcAsync(Guid pcId, CancellationToken cancellationToken) =>
        GetAsync<Pc>(Endpoints.Pc(pcId), AuthMode.Agent, cancellationToken);

    #endregion

    #region Auth / users

    /// <inheritdoc />
    public async Task<AuthResponse> LoginAsync(AuthRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await SendJsonAsync<AuthRequest, AuthResponse>(HttpMethod.Post, Endpoints.AuthLogin, request, AuthMode.Agent, null, cancellationToken).ConfigureAwait(false);
        await StoreUserTokensAsync(response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc />
    public Task<QrLoginStart> StartQrLoginAsync(QrStartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync<QrStartRequest, QrLoginStart>(HttpMethod.Post, Endpoints.AuthQrStart, request, AuthMode.Agent, null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<QrLoginStatus> GetQrLoginStatusAsync(string qrToken, CancellationToken cancellationToken)
    {
        var status = await GetAsync<QrLoginStatus>(Endpoints.AuthQr(qrToken), AuthMode.Agent, cancellationToken).ConfigureAwait(false);
        if (status.Auth is { } auth)
        {
            await StoreUserTokensAsync(auth, cancellationToken).ConfigureAwait(false);
        }

        return status;
    }

    /// <inheritdoc />
    public async Task<AuthResponse> GuestLoginAsync(GuestAuthRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await SendJsonAsync<GuestAuthRequest, AuthResponse>(HttpMethod.Post, Endpoints.AuthGuest, request, AuthMode.Agent, null, cancellationToken).ConfigureAwait(false);
        await StoreUserTokensAsync(response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc />
    public async Task LogoutAsync(LogoutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            await SendJsonNoContentAsync(HttpMethod.Post, Endpoints.AuthLogout, request, AuthMode.User, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _tokens.SetUserAsync(null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task<User> GetUserAsync(Guid userId, CancellationToken cancellationToken) =>
        GetAsync<User>(Endpoints.User(userId), AuthMode.User, cancellationToken);

    /// <inheritdoc />
    public Task<User> UpdateUserAsync(Guid userId, ProfileUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync<ProfileUpdateRequest, User>(HttpMethod.Patch, Endpoints.User(userId), request, AuthMode.User, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<UserStats> GetUserStatsAsync(Guid userId, CancellationToken cancellationToken) =>
        GetAsync<UserStats>(Endpoints.UserStats(userId), AuthMode.User, cancellationToken);

    /// <inheritdoc />
    public Task<ProfileAchievementsResponse> GetUserAchievementsAsync(Guid userId, CancellationToken cancellationToken) =>
        GetAsync<ProfileAchievementsResponse>(Endpoints.UserAchievements(userId), AuthMode.User, cancellationToken);

    /// <inheritdoc />
    public Task<Loyalty> GetUserLoyaltyAsync(Guid userId, CancellationToken cancellationToken) =>
        GetAsync<Loyalty>(Endpoints.UserLoyalty(userId), AuthMode.User, cancellationToken);

    #endregion

    #region Sessions

    /// <inheritdoc />
    public Task<Session?> GetCurrentSessionAsync(Guid pcId, CancellationToken cancellationToken) =>
        GetOrNoContentAsync<Session>(Endpoints.SessionsCurrent + new QueryBuilder().Add("pcId", pcId), AuthMode.Agent, cancellationToken);

    /// <inheritdoc />
    public Task<Session> CreateSessionAsync(SessionCreateRequest request, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync<SessionCreateRequest, Session>(HttpMethod.Post, Endpoints.Sessions, request, AuthMode.User, idempotencyKey, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Session> PauseSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        PostEmptyAsync<Session>(Endpoints.SessionPause(sessionId), AuthMode.User, cancellationToken);

    /// <inheritdoc />
    public Task<Session> ResumeSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        PostEmptyAsync<Session>(Endpoints.SessionResume(sessionId), AuthMode.User, cancellationToken);

    /// <inheritdoc />
    public async Task<SessionEndResult> EndSessionAsync(Guid sessionId, SessionEndReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        try
        {
            return await SendJsonAsync<SessionEndReport, SessionEndResult>(HttpMethod.Post, Endpoints.SessionEnd(sessionId), report, AuthMode.User, null, cancellationToken).ConfigureAwait(false);
        }
        catch (ServerApiException ex) when (ex.Code == ErrorCode.SessionNotActive && TryGetEndedSession(ex, out var session))
        {
            _logger.LogInformation("Session {SessionId} was already ended server-side", sessionId);
            return new SessionEndResult(session, Money.Zero, Money.Zero);
        }
    }

    /// <inheritdoc />
    public Task<Session> ExtendSessionAsync(Guid sessionId, SessionExtendRequest request, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync<SessionExtendRequest, Session>(HttpMethod.Post, Endpoints.SessionExtend(sessionId), request, AuthMode.User, idempotencyKey, cancellationToken);
    }

    /// <inheritdoc />
    public Task PostSessionEventsAsync(Guid sessionId, SessionEventsBatch batch, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return SendJsonNoContentAsync(HttpMethod.Post, Endpoints.SessionEvents(sessionId), batch, AuthMode.Agent, idempotencyKey, cancellationToken);
    }

    #endregion

    #region Games / apps

    /// <inheritdoc />
    public Task<EtagResponse<GamesListResponse>> GetGamesAsync(string? zone, int? page, int? pageSize, string? etag, CancellationToken cancellationToken)
    {
        var query = new QueryBuilder().Add("zone", zone).Add("page", page).Add("pageSize", pageSize);
        return GetConditionalAsync<GamesListResponse>(Endpoints.Games + query, etag, AuthMode.User, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Game> GetGameAsync(Guid gameId, CancellationToken cancellationToken) =>
        GetAsync<Game>(Endpoints.Game(gameId), AuthMode.User, cancellationToken);

    /// <inheritdoc />
    public Task<AccountLease> LeaseAccountAsync(Guid gameId, Guid sessionId, CancellationToken cancellationToken) =>
        GetAsync<AccountLease>(Endpoints.GameAccountLease(gameId) + new QueryBuilder().Add("sessionId", sessionId), AuthMode.User, cancellationToken);

    /// <inheritdoc />
    public async Task ReleaseAccountLeaseAsync(Guid gameId, Guid leaseId, AccountLeaseRelease release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        try
        {
            await SendJsonNoContentAsync(HttpMethod.Post, Endpoints.GameAccountLeaseRelease(gameId, leaseId), release, AuthMode.Agent, null, cancellationToken).ConfigureAwait(false);
        }
        catch (ServerApiException ex) when (ex.Code == ErrorCode.NotFound)
        {
            _logger.LogDebug("Lease {LeaseId} already released", leaseId);
        }
    }

    /// <inheritdoc />
    public Task<SaveUploadTarget> GetSaveUploadTargetAsync(Guid gameId, Guid leaseId, CancellationToken cancellationToken) =>
        GetAsync<SaveUploadTarget>(Endpoints.GameAccountSaveUpload(gameId, leaseId), AuthMode.Agent, cancellationToken);

    /// <inheritdoc />
    public Task<PlayerSettingsBundle?> GetPlayerSettingsAsync(Guid userId, Guid gameId, CancellationToken cancellationToken) =>
        GetOrNoContentAsync<PlayerSettingsBundle>(Endpoints.UserGameSetting(userId, gameId), AuthMode.Agent, cancellationToken);

    /// <inheritdoc />
    public Task<SaveUploadTarget> GetPlayerSettingsUploadTargetAsync(Guid userId, Guid gameId, CancellationToken cancellationToken) =>
        PostEmptyAsync<SaveUploadTarget>(Endpoints.UserGameSettingUploadTarget(userId, gameId), AuthMode.Agent, cancellationToken);

    /// <inheritdoc />
    public Task<PlayerSettingsBundle> CommitPlayerSettingsAsync(Guid userId, Guid gameId, PlayerSettingsCommitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync<PlayerSettingsCommitRequest, PlayerSettingsBundle>(HttpMethod.Put, Endpoints.UserGameSetting(userId, gameId), request, AuthMode.Agent, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PlayerSettingsListResponse> ListPlayerSettingsAsync(Guid userId, CancellationToken cancellationToken) =>
        GetAsync<PlayerSettingsListResponse>(Endpoints.UserGameSettings(userId), AuthMode.User, cancellationToken);

    /// <inheritdoc />
    public async Task DeletePlayerSettingsAsync(Guid userId, Guid gameId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, Endpoints.UserGameSetting(userId, gameId), null, AuthMode.User, null, null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SendLaunchReportAsync(Guid gameId, LaunchReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        return SendJsonNoContentAsync(HttpMethod.Post, Endpoints.GameLaunchReport(gameId), report, AuthMode.Agent, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<EtagResponse<AppsListResponse>> GetAppsAsync(string? etag, CancellationToken cancellationToken) =>
        GetConditionalAsync<AppsListResponse>(Endpoints.Apps, etag, AuthMode.Agent, cancellationToken);

    #endregion

    #region Wallet / tariffs

    /// <inheritdoc />
    public Task<Balance> GetBalanceAsync(Guid userId, CancellationToken cancellationToken) =>
        GetAsync<Balance>(Endpoints.WalletBalance(userId), AuthMode.User, cancellationToken);

    /// <inheritdoc />
    public Task<PagedResult<Transaction>> GetTransactionsAsync(Guid userId, WalletHistoryRequest query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var q = new QueryBuilder()
            .Add("page", query.Page)
            .Add("pageSize", query.PageSize)
            .Add("from", query.From)
            .Add("to", query.To)
            .AddEnum("type", query.Type);
        return GetAsync<PagedResult<Transaction>>(Endpoints.WalletTransactions(userId) + q, AuthMode.User, cancellationToken);
    }

    /// <inheritdoc />
    public Task<EtagResponse<TariffsResponse>> GetTariffsAsync(string? zone, string? etag, CancellationToken cancellationToken) =>
        GetConditionalAsync<TariffsResponse>(Endpoints.Tariffs + new QueryBuilder().Add("zone", zone), etag, AuthMode.Agent, cancellationToken);

    /// <inheritdoc />
    public Task<TopupIntent> CreateTopupIntentAsync(Guid userId, TopupIntentCreateRequest request, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync<TopupIntentCreateRequest, TopupIntent>(HttpMethod.Post, Endpoints.WalletTopupIntent(userId), request, AuthMode.User, idempotencyKey, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TopupIntent> GetTopupIntentAsync(Guid userId, Guid intentId, CancellationToken cancellationToken) =>
        GetAsync<TopupIntent>(Endpoints.WalletTopupIntentStatus(userId, intentId), AuthMode.User, cancellationToken);

    #endregion

    #region Shop

    /// <inheritdoc />
    public Task<EtagResponse<ShopProductsResponse>> GetProductsAsync(ProductCategory? category, string? etag, CancellationToken cancellationToken) =>
        GetConditionalAsync<ShopProductsResponse>(Endpoints.ShopProducts + new QueryBuilder().AddEnum("category", category), etag, AuthMode.Agent, cancellationToken);

    /// <inheritdoc />
    public Task<Order> CreateOrderAsync(OrderCreateRequest request, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync<OrderCreateRequest, Order>(HttpMethod.Post, Endpoints.ShopOrders, request, AuthMode.User, idempotencyKey, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Order> GetOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        GetAsync<Order>(Endpoints.ShopOrder(orderId), AuthMode.User, cancellationToken);

    /// <inheritdoc />
    public Task<PagedResult<Order>> GetOrdersAsync(Guid userId, int? page, int? pageSize, bool? activeOnly, CancellationToken cancellationToken)
    {
        var query = new QueryBuilder().Add("userId", userId).Add("page", page).Add("pageSize", pageSize).Add("activeOnly", activeOnly);
        return GetAsync<PagedResult<Order>>(Endpoints.ShopOrders + query, AuthMode.User, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Order> CancelOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        PostEmptyAsync<Order>(Endpoints.ShopOrderCancel(orderId), AuthMode.User, cancellationToken);

    #endregion

    #region Chat

    /// <inheritdoc />
    public Task<ChatHistoryResponse> GetMessagesAsync(string roomId, Guid? before, int? limit, CancellationToken cancellationToken)
    {
        var query = new QueryBuilder().Add("before", before).Add("limit", limit);
        return GetAsync<ChatHistoryResponse>(Endpoints.ChatMessages(roomId) + query, AuthMode.User, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ChatMessage> PostMessageAsync(string roomId, ChatPostRequest request, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync<ChatPostRequest, ChatMessage>(HttpMethod.Post, Endpoints.ChatMessages(roomId), request, AuthMode.User, idempotencyKey, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ChatMarkReadResponse> MarkReadAsync(string roomId, ChatReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync<ChatReadRequest, ChatMarkReadResponse>(HttpMethod.Post, Endpoints.ChatRead(roomId), request, AuthMode.User, null, cancellationToken);
    }

    #endregion

    #region Booking

    /// <inheritdoc />
    public Task<BookingSeatsResponse> GetSeatsAsync(DateOnly day, CancellationToken cancellationToken) =>
        GetAsync<BookingSeatsResponse>(Endpoints.BookingSeats + new QueryBuilder().Add("date", day), AuthMode.Agent, cancellationToken);

    /// <inheritdoc />
    public Task<Booking> ReserveAsync(BookingCreateRequest request, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync<BookingCreateRequest, Booking>(HttpMethod.Post, Endpoints.BookingReserve, request, AuthMode.User, idempotencyKey, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Booking> CancelBookingAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, Endpoints.Booking(bookingId), null, AuthMode.User, null, null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadBodyAsync<Booking>(response, cancellationToken).ConfigureAwait(false);
    }

    #endregion

    #region Tournaments

    /// <inheritdoc />
    public Task<TournamentsListResponse> GetTournamentsAsync(TournamentState? state, Guid? gameId, CancellationToken cancellationToken)
    {
        var query = new QueryBuilder().AddEnum("state", state).Add("gameId", gameId);
        return GetAsync<TournamentsListResponse>(Endpoints.Tournaments + query, AuthMode.User, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Tournament> JoinTournamentAsync(Guid tournamentId, CancellationToken cancellationToken) =>
        PostEmptyAsync<Tournament>(Endpoints.TournamentJoin(tournamentId), AuthMode.User, cancellationToken);

    /// <inheritdoc />
    public Task<TournamentsLeaderboardResponse> GetLeaderboardAsync(Guid tournamentId, int? limit, CancellationToken cancellationToken) =>
        GetAsync<TournamentsLeaderboardResponse>(Endpoints.TournamentLeaderboard(tournamentId) + new QueryBuilder().Add("limit", limit), AuthMode.User, cancellationToken);

    #endregion

    #region Updates / support / anti-cheat

    /// <inheritdoc />
    public Task<UpdateManifest?> GetUpdateManifestAsync(UpdateChannel channel, UpdateComponent component, string currentVersion, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(currentVersion);
        var query = new QueryBuilder().Add("component", QueryBuilder.EnumName(component)).Add("current", currentVersion).Add("arch", DefaultArch);
        return GetOrNoContentAsync<UpdateManifest>(Endpoints.UpdateManifest(channel) + query, AuthMode.Agent, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SysCallAdminResponse> CallAdminAsync(CallAdminTicketRequest request, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync<CallAdminTicketRequest, SysCallAdminResponse>(HttpMethod.Post, Endpoints.SupportCallAdmin, request, AuthMode.User, idempotencyKey, cancellationToken);
    }

    /// <inheritdoc />
    public Task ReportAntiCheatAsync(AntiCheatReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        return SendJsonNoContentAsync(HttpMethod.Post, Endpoints.AnticheatReport, report, AuthMode.Agent, null, cancellationToken);
    }

    #endregion

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
        _refreshLock.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Transport

    private static ServerApiException NotRegistered() =>
        new(ErrorCode.Unauthorized, HttpStatusCode.Unauthorized, null, null, "Agent is not registered (no tokens); call RegisterAsync");

    private static string? ReadTraceId(HttpResponseMessage response) =>
        response.Headers.TryGetValues(TraceHeader, out var values) ? values.FirstOrDefault() : null;

    private static bool TryReadServerTime(HttpResponseMessage response, out DateTimeOffset serverTime)
    {
        serverTime = default;
        return response.Headers.TryGetValues(Signing.ServerTimeHeader, out var values)
            && values.FirstOrDefault() is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out serverTime);
    }

    private static bool TryGetEndedSession(ServerApiException error, out Session session)
    {
        if (error.Error?.Details is { ValueKind: JsonValueKind.Object } details
            && details.TryGetProperty("session", out var element)
            && element.ValueKind == JsonValueKind.Object
            && JsonDefaults.FromElement<Session>(element) is { State: SessionState.Ended } ended)
        {
            session = ended;
            return true;
        }

        session = null!;
        return false;
    }

    private static async Task<ServerApiException> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ServerError? error = null;
        if (response.Content.Headers.ContentLength != 0)
        {
            try
            {
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    error = (await JsonDefaults.DeserializeAsync<ServerErrorEnvelope>(stream, cancellationToken).ConfigureAwait(false))?.Error;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or HttpRequestException)
            {
                // Non-JSON error body (proxy page, empty 5xx): fall back to the status mapping below.
            }
        }

        var code = error?.Code ?? ErrorCodes.FromHttpStatus((int)response.StatusCode);
        return new ServerApiException(code, response.StatusCode, error, error?.TraceId ?? ReadTraceId(response));
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<TResponse> ReadBodyAsync<TResponse>(HttpResponseMessage response, CancellationToken cancellationToken)
        where TResponse : class
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var value = await JsonDefaults.DeserializeAsync<TResponse>(stream, cancellationToken).ConfigureAwait(false);
                return value ?? throw new ServerApiException(ErrorCode.ProtocolError, response.StatusCode, null, ReadTraceId(response), "Empty response body");
            }
        }
        catch (JsonException ex)
        {
            throw new ServerApiException(ErrorCode.ProtocolError, response.StatusCode, null, ReadTraceId(response), "Malformed response body", ex);
        }
    }

    private async Task<TResponse> GetAsync<TResponse>(string path, AuthMode auth, CancellationToken cancellationToken)
        where TResponse : class
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, auth, null, null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadBodyAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse?> GetOrNoContentAsync<TResponse>(string path, AuthMode auth, CancellationToken cancellationToken)
        where TResponse : class
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, auth, null, null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadBodyAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EtagResponse<TResponse>> GetConditionalAsync<TResponse>(string path, string? etag, AuthMode auth, CancellationToken cancellationToken)
        where TResponse : class
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, auth, null, etag, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return EtagResponse<TResponse>.Unchanged(response.Headers.ETag?.Tag ?? etag);
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var value = await ReadBodyAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
        return new EtagResponse<TResponse>(value, response.Headers.ETag?.Tag, false);
    }

    private async Task<TResponse> PostEmptyAsync<TResponse>(string path, AuthMode auth, CancellationToken cancellationToken)
        where TResponse : class
    {
        using var response = await SendAsync(HttpMethod.Post, path, null, auth, null, null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadBodyAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> SendJsonAsync<TRequest, TResponse>(HttpMethod method, string path, TRequest body, AuthMode auth, Guid? idempotencyKey, CancellationToken cancellationToken)
        where TResponse : class
    {
        using var response = await SendAsync(method, path, JsonDefaults.SerializeToUtf8Bytes(body), auth, idempotencyKey, null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadBodyAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendJsonNoContentAsync<TRequest>(HttpMethod method, string path, TRequest body, AuthMode auth, Guid? idempotencyKey, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, path, JsonDefaults.SerializeToUtf8Bytes(body), auth, idempotencyKey, null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends one logical request: signs, sends, and handles <c>401</c> (clock skew → retry once, expired token → refresh once).</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, byte[]? body, AuthMode auth, Guid? idempotencyKey, string? etag, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var traceId = CurrentTrace.Value ?? Guid.NewGuid();
        var refreshed = false;
        var skewCorrected = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var server = _settings.CurrentValue.Server;
            var uri = Endpoints.BuildUri(server.BaseUrl, path);
            var (response, accessToken) = await SendOnceAsync(method, uri, body, auth, idempotencyKey, etag, server, traceId, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized || auth is AuthMode.None or AuthMode.Club)
            {
                return response;
            }

            var error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            var hasServerTime = TryReadServerTime(response, out var serverTime);
            response.Dispose();
            var reason = error.Reason;
            if (!skewCorrected && hasServerTime && string.Equals(reason, ReasonClockSkew, StringComparison.Ordinal))
            {
                skewCorrected = true;
                ApplyServerTime(serverTime);
                _logger.LogWarning("Server rejected the request signature for clock skew; offset corrected to {Offset} and retrying", ServerTimeOffset);
                continue;
            }

            if (!refreshed && !string.Equals(reason, ReasonUserToken, StringComparison.Ordinal))
            {
                refreshed = true;
                _logger.LogInformation("Agent token rejected ({Reason}); refreshing", reason ?? "unauthorized");
                await RefreshIfStaleAsync(accessToken, cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw error;
        }
    }

    private async Task<(HttpResponseMessage Response, string? AccessToken)> SendOnceAsync(
        HttpMethod method,
        Uri uri,
        byte[]? body,
        AuthMode auth,
        Guid? idempotencyKey,
        string? etag,
        ServerSettings server,
        Guid traceId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(TraceHeader, traceId.ToString("D"));
        request.Headers.Add(AgentVersionHeader, AgentVersion);
        request.Headers.Add(ShellVersionHeader, ShellVersion);
        request.Headers.UserAgent.ParseAdd($"ClubShellAgent/{AgentVersion} (Windows NT 10.0)");
        if (AcceptLanguage is { Length: > 0 } language)
        {
            request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(language));
        }

        if (idempotencyKey is { } key)
        {
            request.Headers.Add(RetryPolicy.IdempotencyKeyHeader, key.ToString("D"));
        }

        if (!string.IsNullOrEmpty(etag))
        {
            _ = request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        var accessToken = Authorize(request, auth, body, uri, server);
        var started = _clock.GetTimestamp();
        try
        {
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("{Method} {Path} -> {Status} in {ElapsedMs} ms", method.Method, uri.PathAndQuery, (int)response.StatusCode, _clock.GetElapsedTime(started).TotalMilliseconds);
            return (response, accessToken);
        }
        catch (HttpRequestException ex)
        {
            throw Transport(ErrorCode.ServerUnavailable, HttpStatusCode.ServiceUnavailable, ex, traceId, uri);
        }
        catch (TimeoutRejectedException ex)
        {
            throw Transport(ErrorCode.Timeout, HttpStatusCode.GatewayTimeout, ex, traceId, uri);
        }
        catch (BrokenCircuitException ex)
        {
            throw Transport(ErrorCode.ServerUnavailable, HttpStatusCode.ServiceUnavailable, ex, traceId, uri);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw Transport(ErrorCode.Timeout, HttpStatusCode.GatewayTimeout, ex, traceId, uri);
        }
    }

    private ServerApiException Transport(ErrorCode code, HttpStatusCode status, Exception exception, Guid traceId, Uri uri)
    {
        _logger.LogWarning(exception, "{Path} failed: {Code}", uri.PathAndQuery, code);
        return new ServerApiException(code, status, null, traceId.ToString("D"), $"{code.Describe()}: {exception.Message}", exception);
    }

    private string? Authorize(HttpRequestMessage request, AuthMode auth, byte[]? body, Uri uri, ServerSettings server)
    {
        switch (auth)
        {
            case AuthMode.None:
                return null;

            case AuthMode.Club:
                if (string.IsNullOrEmpty(server.ClubApiKey))
                {
                    throw new ServerApiException(ErrorCode.Unauthorized, HttpStatusCode.Unauthorized, null, null, "server.clubApiKey is not configured; cannot register");
                }

                request.Headers.Add(ClubKeyHeader, server.ClubApiKey);
                return null;

            default:
                var tokens = _tokens.Agent ?? throw NotRegistered();
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
                if (server.SigningEnabled)
                {
                    var timestamp = ServerNow.ToUnixTimeSeconds();
                    var bodyHash = body is null ? Signing.EmptyBodySha256 : Signing.Sha256Hex(body);
                    var signature = Signing.Sign(tokens.DecodeSigningSecret(), timestamp, request.Method.Method, uri.PathAndQuery, bodyHash);
                    request.Headers.Add(Signing.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
                    request.Headers.Add(Signing.SignatureHeader, signature);
                }

                if (auth == AuthMode.User && _tokens.User is { } user)
                {
                    request.Headers.Add(UserTokenHeader, user.AccessToken);
                }

                return tokens.AccessToken;
        }
    }

    private async Task RefreshIfStaleAsync(string? staleAccessToken, CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _tokens.Agent ?? throw NotRegistered();
            if (staleAccessToken is not null && !string.Equals(current.AccessToken, staleAccessToken, StringComparison.Ordinal))
            {
                return; // Another caller already refreshed.
            }

            await RefreshCoreAsync(current, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<AgentRefreshResponse> RefreshCoreAsync(AgentTokens current, CancellationToken cancellationToken)
    {
        var hwid = await _hwid.GetAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendJsonAsync<AgentRefreshRequest, AgentRefreshResponse>(HttpMethod.Post, Endpoints.AgentsRefresh, new AgentRefreshRequest(current.RefreshToken, hwid), AuthMode.None, null, cancellationToken).ConfigureAwait(false);
        var rotated = current with
        {
            AccessToken = response.AccessToken,
            RefreshToken = response.RefreshToken,
            ExpiresAt = response.ExpiresAt,
            SigningSecret = response.SigningSecret ?? current.SigningSecret,
        };
        await _tokens.SetAgentAsync(rotated, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Agent tokens refreshed; access token valid until {ExpiresAt}", response.ExpiresAt);
        return response;
    }

    private Task StoreUserTokensAsync(AuthResponse auth, CancellationToken cancellationToken) =>
        _tokens.SetUserAsync(new UserTokens(auth.User.Id, auth.AccessToken, auth.RefreshToken, auth.ExpiresAt), cancellationToken);

    private void ApplyServerTime(DateTimeOffset serverTime) =>
        Interlocked.Exchange(ref _serverOffsetTicks, (serverTime - _clock.UtcNow).Ticks);

    #endregion

    private sealed class TraceScope : IDisposable
    {
        private readonly Guid? _previous;

        public TraceScope(Guid? previous)
        {
            _previous = previous;
        }

        public void Dispose() => CurrentTrace.Value = _previous;
    }
}
