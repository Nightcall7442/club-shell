using System.Globalization;
using System.Text;
using System.Text.Json;
using ClubShell.Contracts.Commands;

namespace ClubShell.Core.Http;

/// <summary>
/// Every REST route of SERVER_API.md §5, as paths relative to <c>server.baseUrl</c> (which already ends in
/// <c>/api/v1</c>). Path parameters are escaped; query strings are built with <see cref="QueryBuilder"/>.
/// </summary>
public static class Endpoints
{
    /// <summary>API version segment.</summary>
    public const string ApiVersion = "v1";

    /// <summary>Path prefix every signed <c>PATH</c> starts with.</summary>
    public const string ApiPrefix = "/api/" + ApiVersion;

    /// <summary>WebSocket path.</summary>
    public const string WsPath = "/ws/agent";

    /// <summary><c>POST /agents/register</c>.</summary>
    public const string AgentsRegister = "agents/register";

    /// <summary><c>POST /agents/refresh</c>.</summary>
    public const string AgentsRefresh = "agents/refresh";

    /// <summary><c>GET /pcs</c>.</summary>
    public const string Pcs = "pcs";

    /// <summary><c>POST /auth/login</c>.</summary>
    public const string AuthLogin = "auth/login";

    /// <summary><c>POST /auth/qr/start</c>.</summary>
    public const string AuthQrStart = "auth/qr/start";

    /// <summary><c>POST /auth/guest</c>.</summary>
    public const string AuthGuest = "auth/guest";

    /// <summary><c>POST /auth/logout</c>.</summary>
    public const string AuthLogout = "auth/logout";

    /// <summary><c>POST /sessions</c>.</summary>
    public const string Sessions = "sessions";

    /// <summary><c>GET /sessions/current</c> (add <c>?pcId=</c>).</summary>
    public const string SessionsCurrent = "sessions/current";

    /// <summary><c>GET /games</c>.</summary>
    public const string Games = "games";

    /// <summary><c>GET /apps</c>.</summary>
    public const string Apps = "apps";

    /// <summary><c>GET /tariffs</c>.</summary>
    public const string Tariffs = "tariffs";

    /// <summary><c>GET /shop/products</c>.</summary>
    public const string ShopProducts = "shop/products";

    /// <summary><c>POST /shop/orders</c> and <c>GET /shop/orders?userId=</c>.</summary>
    public const string ShopOrders = "shop/orders";

    /// <summary><c>GET /booking/seats</c> (add <c>?date=</c>).</summary>
    public const string BookingSeats = "booking/seats";

    /// <summary><c>POST /booking/reserve</c>.</summary>
    public const string BookingReserve = "booking/reserve";

    /// <summary><c>GET /tournaments</c>.</summary>
    public const string Tournaments = "tournaments";

    /// <summary><c>POST /support/call-admin</c>.</summary>
    public const string SupportCallAdmin = "support/call-admin";

    /// <summary><c>POST /anticheat/report</c>.</summary>
    public const string AnticheatReport = "anticheat/report";

    /// <summary><c>POST /agents/{pcId}/heartbeat</c>.</summary>
    public static string AgentHeartbeat(Guid pcId) => $"agents/{Id(pcId)}/heartbeat";

    /// <summary><c>POST /agents/{pcId}/telemetry</c>.</summary>
    public static string AgentTelemetry(Guid pcId) => $"agents/{Id(pcId)}/telemetry";

    /// <summary><c>GET /agents/{pcId}/config</c>.</summary>
    public static string AgentConfig(Guid pcId) => $"agents/{Id(pcId)}/config";

    /// <summary><c>GET /agents/{pcId}/policies</c>.</summary>
    public static string AgentPolicies(Guid pcId) => $"agents/{Id(pcId)}/policies";

    /// <summary><c>GET /agents/{pcId}/commands</c>.</summary>
    public static string AgentCommands(Guid pcId) => $"agents/{Id(pcId)}/commands";

    /// <summary><c>POST /agents/{pcId}/commands/{commandId}/ack</c>.</summary>
    public static string AgentCommandAck(Guid pcId, Guid commandId) => $"agents/{Id(pcId)}/commands/{Id(commandId)}/ack";

    /// <summary><c>GET /pcs/{pcId}</c>.</summary>
    public static string Pc(Guid pcId) => $"pcs/{Id(pcId)}";

    /// <summary><c>GET /auth/qr/{token}</c>.</summary>
    public static string AuthQr(string qrToken) => $"auth/qr/{Segment(qrToken)}";

    /// <summary><c>GET|PATCH /users/{userId}</c>.</summary>
    public static string User(Guid userId) => $"users/{Id(userId)}";

    /// <summary><c>GET /users/{userId}/stats</c>.</summary>
    public static string UserStats(Guid userId) => $"users/{Id(userId)}/stats";

    /// <summary><c>GET /users/{userId}/achievements</c>.</summary>
    public static string UserAchievements(Guid userId) => $"users/{Id(userId)}/achievements";

    /// <summary><c>GET /users/{userId}/game-settings</c>.</summary>
    public static string UserGameSettings(Guid userId) => $"users/{Id(userId)}/game-settings";

    /// <summary><c>GET|PUT|DELETE /users/{userId}/game-settings/{gameId}</c>.</summary>
    public static string UserGameSetting(Guid userId, Guid gameId) => $"users/{Id(userId)}/game-settings/{Id(gameId)}";

    /// <summary><c>POST /users/{userId}/game-settings/{gameId}/upload-target</c>.</summary>
    public static string UserGameSettingUploadTarget(Guid userId, Guid gameId) => $"users/{Id(userId)}/game-settings/{Id(gameId)}/upload-target";

    /// <summary><c>GET /users/{userId}/loyalty</c>.</summary>
    public static string UserLoyalty(Guid userId) => $"users/{Id(userId)}/loyalty";

    /// <summary><c>POST /sessions/{id}/pause</c>.</summary>
    public static string SessionPause(Guid sessionId) => $"sessions/{Id(sessionId)}/pause";

    /// <summary><c>POST /sessions/{id}/resume</c>.</summary>
    public static string SessionResume(Guid sessionId) => $"sessions/{Id(sessionId)}/resume";

    /// <summary><c>POST /sessions/{id}/end</c>.</summary>
    public static string SessionEnd(Guid sessionId) => $"sessions/{Id(sessionId)}/end";

    /// <summary><c>POST /sessions/{id}/extend</c>.</summary>
    public static string SessionExtend(Guid sessionId) => $"sessions/{Id(sessionId)}/extend";

    /// <summary><c>POST /sessions/{id}/events</c>.</summary>
    public static string SessionEvents(Guid sessionId) => $"sessions/{Id(sessionId)}/events";

    /// <summary><c>GET /games/{id}</c>.</summary>
    public static string Game(Guid gameId) => $"games/{Id(gameId)}";

    /// <summary><c>GET /games/{id}/accounts/lease</c> (add <c>?sessionId=</c>).</summary>
    public static string GameAccountLease(Guid gameId) => $"games/{Id(gameId)}/accounts/lease";

    /// <summary><c>POST /games/{id}/accounts/{leaseId}/release</c>.</summary>
    public static string GameAccountLeaseRelease(Guid gameId, Guid leaseId) => $"games/{Id(gameId)}/accounts/{Id(leaseId)}/release";

    /// <summary><c>GET /games/{id}/accounts/{leaseId}/save-upload</c>.</summary>
    public static string GameAccountSaveUpload(Guid gameId, Guid leaseId) => $"games/{Id(gameId)}/accounts/{Id(leaseId)}/save-upload";

    /// <summary><c>POST /games/{id}/launch-report</c>.</summary>
    public static string GameLaunchReport(Guid gameId) => $"games/{Id(gameId)}/launch-report";

    /// <summary><c>GET /wallet/{userId}/balance</c>.</summary>
    public static string WalletBalance(Guid userId) => $"wallet/{Id(userId)}/balance";

    /// <summary><c>GET /wallet/{userId}/transactions</c>.</summary>
    public static string WalletTransactions(Guid userId) => $"wallet/{Id(userId)}/transactions";

    /// <summary><c>POST /wallet/{userId}/topup-intent</c>.</summary>
    public static string WalletTopupIntent(Guid userId) => $"wallet/{Id(userId)}/topup-intent";

    /// <summary><c>GET /wallet/{userId}/topup-intent/{id}</c>.</summary>
    public static string WalletTopupIntentStatus(Guid userId, Guid intentId) => $"wallet/{Id(userId)}/topup-intent/{Id(intentId)}";

    /// <summary><c>GET /shop/orders/{id}</c>.</summary>
    public static string ShopOrder(Guid orderId) => $"shop/orders/{Id(orderId)}";

    /// <summary><c>POST /shop/orders/{id}/cancel</c>.</summary>
    public static string ShopOrderCancel(Guid orderId) => $"shop/orders/{Id(orderId)}/cancel";

    /// <summary><c>GET|POST /chat/{roomId}/messages</c>.</summary>
    public static string ChatMessages(string roomId) => $"chat/{Segment(roomId)}/messages";

    /// <summary><c>POST /chat/{roomId}/read</c>.</summary>
    public static string ChatRead(string roomId) => $"chat/{Segment(roomId)}/read";

    /// <summary><c>DELETE /booking/{id}</c>.</summary>
    public static string Booking(Guid bookingId) => $"booking/{Id(bookingId)}";

    /// <summary><c>POST /tournaments/{id}/join</c>.</summary>
    public static string TournamentJoin(Guid tournamentId) => $"tournaments/{Id(tournamentId)}/join";

    /// <summary><c>GET /tournaments/{id}/leaderboard</c>.</summary>
    public static string TournamentLeaderboard(Guid tournamentId) => $"tournaments/{Id(tournamentId)}/leaderboard";

    /// <summary><c>GET /updates/{channel}/manifest</c> (add <c>?component=&amp;current=&amp;arch=</c>).</summary>
    public static string UpdateManifest(UpdateChannel channel) => $"updates/{QueryBuilder.EnumName(channel)}/manifest";

    /// <summary>Normalizes a base URL so relative paths append correctly (trailing slash).</summary>
    public static Uri NormalizeBaseUrl(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseUrl);
        return new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/", UriKind.Absolute);
    }

    /// <summary>Builds an absolute request URI from a base URL, a relative path and an optional query string.</summary>
    public static Uri BuildUri(string baseUrl, string path, string? query = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var relative = string.IsNullOrEmpty(query) ? path : path + (query.StartsWith('?') ? query : "?" + query);
        return new Uri(NormalizeBaseUrl(baseUrl), relative);
    }

    /// <summary>Builds an absolute request URI with query parameters (null values are skipped).</summary>
    public static Uri BuildUri(string baseUrl, string path, IEnumerable<KeyValuePair<string, string?>> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var builder = new QueryBuilder();
        foreach (var pair in query)
        {
            builder.Add(pair.Key, pair.Value);
        }

        return BuildUri(baseUrl, path, builder.ToString());
    }

    private static string Id(Guid id) => id.ToString("D");

    private static string Segment(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return Uri.EscapeDataString(value);
    }
}

/// <summary>Builds <c>?a=b&amp;c=d</c> query strings with wire-format values (camelCase enums, ISO-8601 UTC, <c>yyyy-MM-dd</c>); null values are omitted.</summary>
public sealed class QueryBuilder
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private readonly StringBuilder _builder = new();

    /// <summary>camelCase wire name of an enum value.</summary>
    public static string EnumName<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    /// <summary>Adds a string parameter (skipped when <see langword="null"/>).</summary>
    public QueryBuilder Add(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (value is null)
        {
            return this;
        }

        _builder.Append(_builder.Length == 0 ? '?' : '&');
        _builder.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
        return this;
    }

    /// <summary>Adds an integer parameter.</summary>
    public QueryBuilder Add(string name, int? value) => Add(name, value?.ToString(CultureInfo.InvariantCulture));

    /// <summary>Adds a boolean parameter (<c>true</c>/<c>false</c>).</summary>
    public QueryBuilder Add(string name, bool? value) => Add(name, value is { } b ? (b ? "true" : "false") : null);

    /// <summary>Adds a UUID parameter.</summary>
    public QueryBuilder Add(string name, Guid? value) => Add(name, value?.ToString("D"));

    /// <summary>Adds an ISO-8601 UTC timestamp parameter.</summary>
    public QueryBuilder Add(string name, DateTimeOffset? value) => Add(name, value?.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));

    /// <summary>Adds a <c>yyyy-MM-dd</c> date parameter.</summary>
    public QueryBuilder Add(string name, DateOnly? value) => Add(name, value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>Adds a camelCase enum parameter.</summary>
    public QueryBuilder AddEnum<TEnum>(string name, TEnum? value)
        where TEnum : struct, Enum =>
        Add(name, value is { } v ? EnumName(v) : null);

    /// <summary>The query string including the leading <c>?</c>, or an empty string when no parameter was added.</summary>
    public override string ToString() => _builder.ToString();
}
