using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Shop;
using ClubShell.Contracts.Users;
using ClubShell.Contracts.Wallet;

namespace ClubShell.Contracts.Ipc;

/// <summary>
/// Every IPC message name (IPC_PROTOCOL.md §7–8). Requests are Shell → Agent; <see cref="Events"/> are Agent → Shell
/// and are re-emitted by the Shell as Tauri events <c>agent://&lt;name&gt;</c>.
/// </summary>
public static class IpcMessages
{
    /// <summary><c>auth.*</c></summary>
    public static class Auth
    {
        /// <summary>Handshake; payload <see cref="AuthHelloRequest"/> → <see cref="AuthHelloResponse"/>.</summary>
        public const string Hello = "auth.hello";

        /// <summary><see cref="AuthLoginRequest"/> → <see cref="AuthLoginResponse"/>.</summary>
        public const string Login = "auth.login";

        /// <summary><see cref="AuthLogoutRequest"/> → <see cref="AuthLogoutResponse"/>.</summary>
        public const string Logout = "auth.logout";

        /// <summary>— → <see cref="AuthStatusResponse"/>.</summary>
        public const string Status = "auth.status";

        /// <summary>— → <see cref="QrLoginStart"/>.</summary>
        public const string QrStart = "auth.qrStart";
    }

    /// <summary><c>session.*</c></summary>
    public static class Session
    {
        /// <summary>— → <see cref="Sessions.Session"/> or <see langword="null"/>.</summary>
        public const string Get = "session.get";

        /// <summary><see cref="SessionStartRequest"/> → <see cref="Sessions.Session"/>.</summary>
        public const string Start = "session.start";

        /// <summary><see cref="SessionPauseRequest"/> → <see cref="Sessions.Session"/>.</summary>
        public const string Pause = "session.pause";

        /// <summary>— → <see cref="Sessions.Session"/>.</summary>
        public const string Resume = "session.resume";

        /// <summary><see cref="SessionEndRequest"/> → <see cref="SessionEndResult"/>.</summary>
        public const string End = "session.end";

        /// <summary><see cref="SessionExtendRequest"/> → <see cref="Sessions.Session"/>.</summary>
        public const string Extend = "session.extend";

        /// <summary><see cref="SessionLockRequest"/> → <see cref="Sessions.Session"/>.</summary>
        public const string Lock = "session.lock";

        /// <summary><see cref="SessionUnlockRequest"/> → <see cref="Sessions.Session"/>.</summary>
        public const string Unlock = "session.unlock";

        /// <summary>— → <see cref="SessionTimeLeftResponse"/>.</summary>
        public const string TimeLeft = "session.timeLeft";
    }

    /// <summary><c>games.*</c></summary>
    public static class Games
    {
        /// <summary><see cref="GamesListRequest"/> → <see cref="GamesListResponse"/>.</summary>
        public const string List = "games.list";

        /// <summary><see cref="GamesGetRequest"/> → <see cref="Game"/>.</summary>
        public const string Get = "games.get";

        /// <summary><see cref="GamesLaunchRequest"/> → <see cref="LaunchResult"/>.</summary>
        public const string Launch = "games.launch";

        /// <summary><see cref="GamesKillRequest"/> → <see cref="GamesKillResponse"/>.</summary>
        public const string Kill = "games.kill";

        /// <summary>— → <see cref="GamesRunningResponse"/>.</summary>
        public const string Running = "games.running";

        /// <summary><see cref="GamesInstallStatusRequest"/> → <see cref="GameInstallStatus"/>.</summary>
        public const string InstallStatus = "games.installStatus";
    }

    /// <summary><c>apps.*</c></summary>
    public static class Apps
    {
        /// <summary>— → <see cref="AppsListResponse"/>.</summary>
        public const string List = "apps.list";

        /// <summary><see cref="AppsLaunchRequest"/> → <see cref="AppsLaunchResponse"/>.</summary>
        public const string Launch = "apps.launch";
    }

    /// <summary><c>wallet.*</c></summary>
    public static class Wallet
    {
        /// <summary>— → <see cref="Contracts.Wallet.Balance"/>.</summary>
        public const string Balance = "wallet.balance";

        /// <summary><see cref="WalletTariffsRequest"/> → <see cref="WalletTariffsResponse"/>.</summary>
        public const string Tariffs = "wallet.tariffs";

        /// <summary><see cref="WalletHistoryRequest"/> → <see cref="WalletHistoryResponse"/>.</summary>
        public const string History = "wallet.history";

        /// <summary><see cref="WalletTopupIntentRequest"/> → <see cref="Contracts.Wallet.TopupIntent"/>.</summary>
        public const string TopupIntent = "wallet.topupIntent";
    }

    /// <summary><c>shop.*</c></summary>
    public static class Shop
    {
        /// <summary><see cref="ShopProductsRequest"/> → <see cref="ShopProductsResponse"/>.</summary>
        public const string Products = "shop.products";

        /// <summary><see cref="ShopOrderRequest"/> → <see cref="Contracts.Shop.Order"/>.</summary>
        public const string Order = "shop.order";

        /// <summary><see cref="ShopOrderStatusRequest"/> → <see cref="Contracts.Shop.Order"/>.</summary>
        public const string OrderStatus = "shop.orderStatus";

        /// <summary><see cref="ShopOrdersRequest"/> → <see cref="ShopOrdersResponse"/>.</summary>
        public const string Orders = "shop.orders";
    }

    /// <summary><c>chat.*</c></summary>
    public static class Chat
    {
        /// <summary><see cref="ChatHistoryRequest"/> → <see cref="ChatHistoryResponse"/>.</summary>
        public const string History = "chat.history";

        /// <summary><see cref="ChatSendRequest"/> → <see cref="ChatMessage"/>.</summary>
        public const string Send = "chat.send";

        /// <summary><see cref="ChatMarkReadRequest"/> → <see cref="ChatMarkReadResponse"/>.</summary>
        public const string MarkRead = "chat.markRead";
    }

    /// <summary><c>booking.*</c></summary>
    public static class Booking
    {
        /// <summary><see cref="BookingSeatsRequest"/> → <see cref="BookingSeatsResponse"/>.</summary>
        public const string Seats = "booking.seats";

        /// <summary><see cref="BookingReserveRequest"/> → <see cref="Users.Booking"/>.</summary>
        public const string Reserve = "booking.reserve";

        /// <summary><see cref="BookingCancelRequest"/> → <see cref="Users.Booking"/>.</summary>
        public const string Cancel = "booking.cancel";
    }

    /// <summary><c>tournaments.*</c></summary>
    public static class Tournaments
    {
        /// <summary><see cref="TournamentsListRequest"/> → <see cref="TournamentsListResponse"/>.</summary>
        public const string List = "tournaments.list";

        /// <summary><see cref="TournamentsJoinRequest"/> → <see cref="Tournament"/>.</summary>
        public const string Join = "tournaments.join";

        /// <summary><see cref="TournamentsLeaderboardRequest"/> → <see cref="TournamentsLeaderboardResponse"/>.</summary>
        public const string Leaderboard = "tournaments.leaderboard";
    }

    /// <summary><c>profile.*</c></summary>
    public static class Profile
    {
        /// <summary>— → <see cref="User"/>.</summary>
        public const string Get = "profile.get";

        /// <summary><see cref="ProfileUpdateRequest"/> → <see cref="User"/>.</summary>
        public const string Update = "profile.update";

        /// <summary>— → <see cref="UserStats"/>.</summary>
        public const string Stats = "profile.stats";

        /// <summary>— → <see cref="ProfileAchievementsResponse"/>.</summary>
        public const string Achievements = "profile.achievements";

        /// <summary>— → <see cref="Users.Loyalty"/>.</summary>
        public const string Loyalty = "profile.loyalty";

        /// <summary>— → <see cref="PlayerSettingsListResponse"/>: games whose settings follow the player.</summary>
        public const string GameSettings = "profile.gameSettings";

        /// <summary><see cref="PlayerSettingsResetRequest"/> → <see cref="PlayerSettingsListResponse"/>.</summary>
        public const string GameSettingsReset = "profile.gameSettingsReset";
    }

    /// <summary><c>settings.*</c></summary>
    public static class Settings
    {
        /// <summary>— → <see cref="ShellSettings"/>.</summary>
        public const string Get = "settings.get";

        /// <summary><see cref="SettingsSetRequest"/> → <see cref="ShellSettings"/>.</summary>
        public const string Set = "settings.set";
    }

    /// <summary><c>sys.*</c></summary>
    public static class Sys
    {
        /// <summary><see cref="SysPingRequest"/> → response named <see cref="Pong"/> with <see cref="SysPongResponse"/>.</summary>
        public const string Ping = "sys.ping";

        /// <summary>Response name of <see cref="Ping"/>.</summary>
        public const string Pong = "sys.pong";

        /// <summary>— → <see cref="Pcs.PcInfo"/>.</summary>
        public const string PcInfo = "sys.pcInfo";

        /// <summary><see cref="SysHardwareRequest"/> → <see cref="HardwareInfo"/>.</summary>
        public const string Hardware = "sys.hardware";

        /// <summary>— → <see cref="PcMetrics"/> (last sample).</summary>
        public const string Metrics = "sys.metrics";

        /// <summary><see cref="SysCallAdminRequest"/> → <see cref="SysCallAdminResponse"/>.</summary>
        public const string CallAdmin = "sys.callAdmin";

        /// <summary><see cref="SysPowerRequest"/> → <see cref="ScheduledResult"/>.</summary>
        public const string Reboot = "sys.reboot";

        /// <summary><see cref="SysPowerRequest"/> → <see cref="ScheduledResult"/>.</summary>
        public const string Shutdown = "sys.shutdown";

        /// <summary><see cref="SysLockScreenRequest"/> → <see cref="OkResponse"/>.</summary>
        public const string LockScreen = "sys.lockScreen";

        /// <summary><see cref="SetVolumeRequest"/> → <see cref="VolumeState"/>.</summary>
        public const string SetVolume = "sys.setVolume";

        /// <summary><see cref="SysSetLocaleRequest"/> → <see cref="SysSetLocaleResponse"/>.</summary>
        public const string SetLocale = "sys.setLocale";

        /// <summary><see cref="SysUnlockAdminRequest"/> → <see cref="SysUnlockAdminResponse"/>.</summary>
        public const string UnlockAdmin = "sys.unlockAdmin";

        /// <summary><see cref="SysLogClientErrorRequest"/> → <see cref="OkResponse"/>.</summary>
        public const string LogClientError = "sys.logClientError";

        /// <summary><see cref="SysAckAdminMessageRequest"/> → <see cref="OkResponse"/>.</summary>
        public const string AckAdminMessage = "sys.ackAdminMessage";
    }

    /// <summary><c>policy.*</c></summary>
    public static class Policy
    {
        /// <summary>— → <see cref="Pcs.Policy"/>.</summary>
        public const string Get = "policy.get";

        /// <summary><see cref="PolicyReloadRequest"/> → <see cref="PolicyReloadResponse"/>.</summary>
        public const string Reload = "policy.reload";
    }

    /// <summary><c>update.*</c></summary>
    public static class Update
    {
        /// <summary>— → <see cref="UpdateCheckResponse"/>.</summary>
        public const string Check = "update.check";

        /// <summary><see cref="UpdateApplyRequest"/> → <see cref="UpdateApplyResponse"/>.</summary>
        public const string Apply = "update.apply";
    }

    /// <summary>Agent → Shell events (IPC_PROTOCOL.md §8).</summary>
    public static class Events
    {
        /// <summary>Payload <see cref="Sessions.Session"/>; any state/field change (≤ 1/s).</summary>
        public const string SessionUpdated = "session.updated";

        /// <summary>Payload <see cref="Sessions.SessionWarning"/>.</summary>
        public const string SessionWarning = "session.warning";

        /// <summary>Payload <see cref="SessionEndedEvent"/>.</summary>
        public const string SessionEnded = "session.ended";

        /// <summary>Payload <see cref="Contracts.Wallet.Balance"/>.</summary>
        public const string WalletUpdated = "wallet.updated";

        /// <summary>Payload <see cref="Users.ChatMessage"/>.</summary>
        public const string ChatMessage = "chat.message";

        /// <summary>Payload <see cref="Users.Notification"/>.</summary>
        public const string NotificationPush = "notification.push";

        /// <summary>Payload <see cref="Ipc.AdminMessage"/>.</summary>
        public const string AdminMessage = "admin.message";

        /// <summary>Payload <see cref="Ipc.RemoteControlEvent"/>.</summary>
        public const string AdminRemoteControl = "admin.remoteControl";

        /// <summary>Payload <see cref="Ipc.GameStateChanged"/>.</summary>
        public const string GameStateChanged = "game.stateChanged";

        /// <summary>Payload <see cref="Ipc.PolicyChanged"/>.</summary>
        public const string PolicyChanged = "policy.changed";

        /// <summary>Payload <see cref="Ipc.UpdateAvailable"/>.</summary>
        public const string UpdateAvailable = "update.available";

        /// <summary>Payload <see cref="Ipc.UpdateProgress"/>.</summary>
        public const string UpdateProgress = "update.progress";

        /// <summary>Payload <see cref="Ipc.UpdateReady"/>.</summary>
        public const string UpdateReady = "update.ready";

        /// <summary>Payload <see cref="PcMetrics"/>.</summary>
        public const string SysMetrics = "sys.metrics";

        /// <summary>Payload <see cref="Ipc.ConnectivityEvent"/>.</summary>
        public const string SysConnectivity = "sys.connectivity";

        /// <summary>Payload <see cref="Ipc.ShellCommand"/>.</summary>
        public const string ShellCommand = "shell.command";

        /// <summary>Payload <see cref="Ipc.AuthExpired"/>.</summary>
        public const string AuthExpired = "auth.expired";

        /// <summary>Payload <see cref="Contracts.Shop.Order"/>.</summary>
        public const string ShopOrderUpdated = "shop.orderUpdated";

        /// <summary>All event names.</summary>
        public static IReadOnlyList<string> All { get; } = new[]
        {
            SessionUpdated, SessionWarning, SessionEnded, WalletUpdated, ChatMessage, NotificationPush, AdminMessage,
            AdminRemoteControl, GameStateChanged, PolicyChanged, UpdateAvailable, UpdateProgress, UpdateReady, SysMetrics,
            SysConnectivity, ShellCommand, AuthExpired, ShopOrderUpdated,
        };
    }
}

#region Common

/// <summary>Standard page envelope <c>{ items, total, page, pageSize }</c> (SERVER_API.md §1).</summary>
/// <typeparam name="T">Item type.</typeparam>
/// <param name="Items">Items of this page.</param>
/// <param name="Total">Total items across all pages.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Page size.</param>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Page,
    int PageSize)
{
    /// <summary><see langword="true"/> when more pages follow.</summary>
    [JsonIgnore]
    public bool HasMore => (long)Page * PageSize < Total;
}

/// <summary>Trivial <c>{ ok: true }</c> response.</summary>
/// <param name="Ok">Always <see langword="true"/>.</param>
public sealed record OkResponse(bool Ok = true)
{
    /// <summary>Shared instance.</summary>
    public static readonly OkResponse Instance = new();
}

#endregion

#region Auth payloads

/// <summary>Shell capabilities advertised in <see cref="AuthHelloRequest.Capabilities"/>.</summary>
public static class ShellCapabilities
{
    /// <summary>Gamepad navigation.</summary>
    public const string Gamepad = "gamepad";

    /// <summary>On-screen keyboard.</summary>
    public const string VirtualKeyboard = "virtualKeyboard";

    /// <summary>Multi-monitor handling.</summary>
    public const string MultiMonitor = "multiMonitor";

    /// <summary>Overlay window.</summary>
    public const string Overlay = "overlay";
}

/// <summary>Agent capabilities advertised in <see cref="AuthHelloResponse.Capabilities"/>; the Shell hides features whose capability is absent.</summary>
public static class AgentCapabilities
{
    /// <summary>Account-pool credential injection.</summary>
    public const string AccountPool = "accountPool";

    /// <summary>Cloud save sync.</summary>
    public const string CloudSave = "cloudSave";

    /// <summary>Remote control by admins.</summary>
    public const string RemoteControl = "remoteControl";

    /// <summary>Screen capture.</summary>
    public const string ScreenCapture = "screenCapture";

    /// <summary>On-screen keyboard allowed.</summary>
    public const string VirtualKeyboard = "virtualKeyboard";

    /// <summary>Wake-on-LAN relay.</summary>
    public const string Wol = "wol";

    /// <summary>Offline mode.</summary>
    public const string Offline = "offline";

    /// <summary>Shop.</summary>
    public const string Shop = "shop";

    /// <summary>Chat.</summary>
    public const string Chat = "chat";

    /// <summary>Booking.</summary>
    public const string Booking = "booking";

    /// <summary>Tournaments.</summary>
    public const string Tournaments = "tournaments";
}

/// <summary>Request of <c>auth.hello</c>.</summary>
/// <param name="ShellToken">64 hex chars from <c>secure\shell.token</c>; compared in constant time.</param>
/// <param name="ShellVersion">Shell semver.</param>
/// <param name="Pid">Shell process id.</param>
/// <param name="WtsSessionId">Windows session id.</param>
/// <param name="Locale">Current UI locale.</param>
/// <param name="Capabilities">Shell capabilities (<see cref="ShellCapabilities"/>).</param>
public sealed record AuthHelloRequest(
    string ShellToken,
    string ShellVersion,
    int Pid,
    int WtsSessionId,
    Locale Locale,
    IReadOnlyList<string> Capabilities);

/// <summary>Response of <c>auth.hello</c>.</summary>
/// <param name="AgentVersion">Agent semver.</param>
/// <param name="Protocol">IPC protocol major (<see cref="IpcEnvelope.CurrentVersion"/>).</param>
/// <param name="PcId">PC id.</param>
/// <param name="PcName">PC name.</param>
/// <param name="Zone">Zone.</param>
/// <param name="ServerOnline">Server reachable.</param>
/// <param name="PolicyVersion">Applied policy version.</param>
/// <param name="ServerTime">Agent's best estimate of server time.</param>
/// <param name="Capabilities">Agent capabilities (<see cref="AgentCapabilities"/>).</param>
/// <param name="KioskUser">Kiosk Windows account name.</param>
public sealed record AuthHelloResponse(
    string AgentVersion,
    int Protocol,
    Guid PcId,
    string PcName,
    string Zone,
    bool ServerOnline,
    int PolicyVersion,
    DateTimeOffset ServerTime,
    IReadOnlyList<string> Capabilities,
    string KioskUser);

/// <summary>Request of <c>auth.login</c> (subset of <see cref="AuthRequest"/> without PC identity). Secrets are never logged.</summary>
/// <param name="Kind">Method.</param>
/// <param name="Username">Login name (<see cref="AuthKind.Password"/>).</param>
/// <param name="Password">Password (<see cref="AuthKind.Password"/>).</param>
/// <param name="QrToken">QR token (<see cref="AuthKind.Qr"/>).</param>
/// <param name="CardId">Card id (<see cref="AuthKind.Card"/>).</param>
/// <param name="Token">One-time token (<see cref="AuthKind.Token"/>).</param>
public sealed record AuthLoginRequest(
    AuthKind Kind,
    string? Username = null,
    string? Password = null,
    string? QrToken = null,
    string? CardId = null,
    string? Token = null)
{
    /// <summary>Builds the server-facing <see cref="AuthRequest"/>.</summary>
    public AuthRequest ToServerRequest(Guid pcId, string hwid) =>
        new(Kind, Username, Password, QrToken, CardId, Token, pcId, hwid);
}

/// <summary>Response of <c>auth.login</c>.</summary>
/// <param name="User">Authenticated user.</param>
/// <param name="Session">Existing session bound to this user on this PC, when any.</param>
/// <param name="ExpiresAt">User access token expiry (token is Agent-held).</param>
/// <param name="Mode"><see cref="ConnectivityState.Offline"/> when validated from cache.</param>
public sealed record AuthLoginResponse(
    User User,
    Session? Session,
    DateTimeOffset ExpiresAt,
    ConnectivityState Mode);

/// <summary>Request of <c>auth.logout</c>.</summary>
/// <param name="Reason">Why; defaults to <see cref="SessionEndReason.User"/>. Only <c>user</c>, <c>idle</c>, <c>admin</c> are valid from the Shell.</param>
public sealed record AuthLogoutRequest(SessionEndReason? Reason = null);

/// <summary>Response of <c>auth.logout</c>.</summary>
/// <param name="Ok">Always <see langword="true"/>.</param>
/// <param name="SessionEnded">Whether an active session was ended first.</param>
/// <param name="Session">The ended session, when any.</param>
public sealed record AuthLogoutResponse(
    bool Ok,
    bool SessionEnded,
    Session? Session = null);

/// <summary>Response of <c>auth.status</c>.</summary>
/// <param name="Authenticated">Whether a user is logged in.</param>
/// <param name="Mode">Connectivity.</param>
/// <param name="User">Logged-in user.</param>
/// <param name="Session">Current session.</param>
/// <param name="ExpiresAt">User token expiry.</param>
public sealed record AuthStatusResponse(
    bool Authenticated,
    ConnectivityState Mode,
    User? User = null,
    Session? Session = null,
    DateTimeOffset? ExpiresAt = null);

#endregion

#region Session payloads

/// <summary>Request of <c>session.start</c>.</summary>
/// <param name="TariffId">Tariff.</param>
/// <param name="Prepaid"><see langword="true"/> = charge now for <paramref name="Minutes"/>; <see langword="false"/> = postpaid open-ended.</param>
/// <param name="Minutes">Minutes to buy; required unless the tariff is a package; within <c>minMinutes..maxMinutes</c>.</param>
public sealed record SessionStartRequest(
    Guid TariffId,
    bool Prepaid,
    int? Minutes = null);

/// <summary>Request of <c>session.pause</c>.</summary>
/// <param name="Reason">Free-text reason.</param>
public sealed record SessionPauseRequest(string? Reason = null);

/// <summary>Request of <c>session.end</c>.</summary>
/// <param name="Reason">End reason; defaults to <see cref="SessionEndReason.User"/>.</param>
public sealed record SessionEndRequest(SessionEndReason? Reason = null);

/// <summary>Request of <c>session.extend</c> and body of <c>POST /sessions/{id}/extend</c>.</summary>
/// <param name="Minutes">Minutes to add.</param>
/// <param name="TariffId">Tariff to bill; defaults to the current one.</param>
public sealed record SessionExtendRequest(
    int Minutes,
    Guid? TariffId = null);

/// <summary>Request of <c>session.lock</c>.</summary>
/// <param name="Reason">Free-text reason.</param>
public sealed record SessionLockRequest(string? Reason = null);

/// <summary>Request of <c>session.unlock</c>; exactly one of the secrets is given.</summary>
/// <param name="Password">Account password.</param>
/// <param name="Pin">4–6 digit user PIN.</param>
public sealed record SessionUnlockRequest(
    string? Password = null,
    string? Pin = null);

/// <summary>Response of <c>session.timeLeft</c>; used for drift correction every 30 s. Never errors when idle.</summary>
/// <param name="State">Session state (<see cref="SessionState.Idle"/> when none).</param>
/// <param name="SecondsLeft">Seconds remaining (−1 open-ended, 0 when idle).</param>
/// <param name="SecondsUsed">Seconds used.</param>
/// <param name="ServerTime">Agent's estimate of server time.</param>
/// <param name="SessionId">Session id, when any.</param>
/// <param name="EndsAt">Scheduled end, when any.</param>
public sealed record SessionTimeLeftResponse(
    SessionState State,
    int SecondsLeft,
    int SecondsUsed,
    DateTimeOffset ServerTime,
    Guid? SessionId = null,
    DateTimeOffset? EndsAt = null);

#endregion

#region Games / apps payloads

/// <summary>Request of <c>games.list</c>.</summary>
/// <param name="Category">Filter by category.</param>
/// <param name="Search">Free-text search.</param>
/// <param name="InstalledOnly">Only installed games.</param>
/// <param name="Launcher">Filter by launcher.</param>
/// <param name="Sort">Sort order (default popularity).</param>
/// <param name="Page">1-based page (default 1).</param>
/// <param name="PageSize">Page size (default 100, max 500).</param>
public sealed record GamesListRequest(
    string? Category = null,
    string? Search = null,
    bool? InstalledOnly = null,
    LauncherType? Launcher = null,
    GamesSort? Sort = null,
    int? Page = null,
    int? PageSize = null)
{
    /// <summary>Default page size.</summary>
    public const int DefaultPageSize = 100;

    /// <summary>Maximum page size.</summary>
    public const int MaxPageSize = 500;
}

/// <summary>Response of <c>games.list</c> (served from <c>cache\games.json</c> when offline).</summary>
/// <param name="Items">Games of this page.</param>
/// <param name="Total">Total matching games.</param>
/// <param name="Page">Page number.</param>
/// <param name="PageSize">Page size.</param>
/// <param name="CatalogVersion">Catalogue version (ETag-like).</param>
public sealed record GamesListResponse(
    IReadOnlyList<Game> Items,
    int Total,
    int Page,
    int PageSize,
    string CatalogVersion);

/// <summary>Request of <c>games.get</c>.</summary>
/// <param name="GameId">Game id.</param>
public sealed record GamesGetRequest(Guid GameId);

/// <summary>Request of <c>games.launch</c>; the Agent fills session/user/timeout to build a <see cref="LaunchRequest"/>.</summary>
/// <param name="GameId">Game id.</param>
/// <param name="UseAccountPool">Defaults to <see cref="Game.RequiresAccount"/>.</param>
/// <param name="ExtraArgs">Extra command line (appended after policy sanitization).</param>
/// <param name="Resolution">Requested resolution.</param>
public sealed record GamesLaunchRequest(
    Guid GameId,
    bool? UseAccountPool = null,
    string? ExtraArgs = null,
    Resolution? Resolution = null);

/// <summary>Request of <c>games.kill</c>; at least one of <paramref name="GameId"/>/<paramref name="Pid"/>, none = kill all.</summary>
/// <param name="GameId">Game to kill.</param>
/// <param name="Pid">Process to kill.</param>
/// <param name="Force">Terminate immediately.</param>
public sealed record GamesKillRequest(
    Guid? GameId = null,
    int? Pid = null,
    bool? Force = null);

/// <summary>Response of <c>games.kill</c> and result of the <c>killGame</c> server command.</summary>
/// <param name="Killed">Number of processes killed.</param>
/// <param name="Pids">Process ids killed.</param>
public sealed record GamesKillResponse(
    int Killed,
    IReadOnlyList<int> Pids);

/// <summary>Response of <c>games.running</c>.</summary>
/// <param name="Items">Running games.</param>
public sealed record GamesRunningResponse(IReadOnlyList<RunningGame> Items);

/// <summary>Request of <c>games.installStatus</c>.</summary>
/// <param name="GameId">Game id.</param>
public sealed record GamesInstallStatusRequest(Guid GameId);

/// <summary>Response of <c>apps.list</c> and <c>GET /apps</c>.</summary>
/// <param name="Items">Apps.</param>
public sealed record AppsListResponse(IReadOnlyList<App> Items);

/// <summary>Request of <c>apps.launch</c>.</summary>
/// <param name="AppId">App id.</param>
/// <param name="Args">Extra command line.</param>
public sealed record AppsLaunchRequest(
    Guid AppId,
    string? Args = null);

/// <summary>Response of <c>apps.launch</c>.</summary>
/// <param name="Ok">Always <see langword="true"/>.</param>
/// <param name="Pid">Process id.</param>
/// <param name="StartedAt">Launch time.</param>
public sealed record AppsLaunchResponse(
    bool Ok,
    int Pid,
    DateTimeOffset StartedAt);

#endregion

#region Wallet payloads

/// <summary>Request of <c>wallet.tariffs</c>.</summary>
/// <param name="Zone">Zone; defaults to this PC's zone.</param>
public sealed record WalletTariffsRequest(string? Zone = null);

/// <summary>Response of <c>wallet.tariffs</c> (cached offline).</summary>
/// <param name="Items">Tariffs.</param>
/// <param name="Zone">Zone the list applies to.</param>
/// <param name="ServerTime">Server time for window evaluation.</param>
public sealed record WalletTariffsResponse(
    IReadOnlyList<Tariff> Items,
    string Zone,
    DateTimeOffset ServerTime);

/// <summary>Response of <c>GET /tariffs</c>.</summary>
/// <param name="Items">Tariffs.</param>
/// <param name="ServerTime">Server time.</param>
public sealed record TariffsResponse(
    IReadOnlyList<Tariff> Items,
    DateTimeOffset ServerTime);

/// <summary>Request of <c>wallet.history</c> (and query of <c>GET /wallet/{userId}/transactions</c>).</summary>
/// <param name="Page">1-based page.</param>
/// <param name="PageSize">Page size.</param>
/// <param name="From">Inclusive lower bound.</param>
/// <param name="To">Exclusive upper bound.</param>
/// <param name="Type">Filter by type.</param>
public sealed record WalletHistoryRequest(
    int? Page = null,
    int? PageSize = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    TransactionType? Type = null);

/// <summary>Response of <c>wallet.history</c>.</summary>
/// <param name="Items">Transactions.</param>
/// <param name="Total">Total transactions.</param>
/// <param name="Page">Page number.</param>
/// <param name="PageSize">Page size.</param>
public sealed record WalletHistoryResponse(
    IReadOnlyList<Transaction> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>Request of <c>wallet.topupIntent</c>.</summary>
/// <param name="Amount">Amount ≥ 1 000 UZS; <see cref="TopupProvider.Cash"/> creates an admin ticket.</param>
/// <param name="Provider">Provider.</param>
public sealed record WalletTopupIntentRequest(
    Money Amount,
    TopupProvider Provider);

#endregion

#region Shop payloads

/// <summary>Request of <c>shop.products</c>.</summary>
/// <param name="Category">Filter by category.</param>
/// <param name="Search">Free-text search.</param>
public sealed record ShopProductsRequest(
    ProductCategory? Category = null,
    string? Search = null);

/// <summary>Response of <c>shop.products</c> and <c>GET /shop/products</c>.</summary>
/// <param name="Items">Products.</param>
public sealed record ShopProductsResponse(IReadOnlyList<Product> Items);

/// <summary>Request of <c>shop.order</c>.</summary>
/// <param name="Items">Lines (1–20, qty 1–99).</param>
/// <param name="IdempotencyKey">Client-generated key forwarded as <c>Idempotency-Key</c>.</param>
/// <param name="Note">Note for staff.</param>
public sealed record ShopOrderRequest(
    IReadOnlyList<OrderLineRequest> Items,
    Guid IdempotencyKey,
    string? Note = null);

/// <summary>Request of <c>shop.orderStatus</c>.</summary>
/// <param name="OrderId">Order id.</param>
public sealed record ShopOrderStatusRequest(Guid OrderId);

/// <summary>Request of <c>shop.orders</c>.</summary>
/// <param name="Page">1-based page.</param>
/// <param name="PageSize">Page size.</param>
/// <param name="ActiveOnly">Only orders still in progress.</param>
public sealed record ShopOrdersRequest(
    int? Page = null,
    int? PageSize = null,
    bool? ActiveOnly = null);

/// <summary>Response of <c>shop.orders</c>.</summary>
/// <param name="Items">Orders.</param>
/// <param name="Total">Total orders.</param>
public sealed record ShopOrdersResponse(
    IReadOnlyList<Order> Items,
    int Total);

#endregion

#region Chat payloads

/// <summary>Request of <c>chat.history</c>.</summary>
/// <param name="RoomId">Room; defaults to <c>pc:&lt;pcId&gt;</c>.</param>
/// <param name="Before">Return messages older than this message id.</param>
/// <param name="Limit">Page size (default 50, max 200).</param>
public sealed record ChatHistoryRequest(
    string? RoomId = null,
    Guid? Before = null,
    int? Limit = null)
{
    /// <summary>Default page size.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Maximum page size.</summary>
    public const int MaxLimit = 200;
}

/// <summary>Response of <c>chat.history</c> and <c>GET /chat/{roomId}/messages</c>.</summary>
/// <param name="RoomId">Room.</param>
/// <param name="Items">Messages, oldest first.</param>
/// <param name="HasMore">Older messages exist.</param>
/// <param name="Unread">Unread count in the room.</param>
public sealed record ChatHistoryResponse(
    string RoomId,
    IReadOnlyList<ChatMessage> Items,
    bool HasMore,
    int Unread);

/// <summary>Request of <c>chat.send</c>.</summary>
/// <param name="Text">Text (1–2000 chars).</param>
/// <param name="IdempotencyKey">Client-generated key forwarded as <c>Idempotency-Key</c>.</param>
/// <param name="RoomId">Room; defaults to <c>pc:&lt;pcId&gt;</c>.</param>
public sealed record ChatSendRequest(
    string Text,
    Guid IdempotencyKey,
    string? RoomId = null);

/// <summary>Request of <c>chat.markRead</c>.</summary>
/// <param name="UpToMessageId">Mark up to and including this message.</param>
/// <param name="RoomId">Room; defaults to <c>pc:&lt;pcId&gt;</c>.</param>
public sealed record ChatMarkReadRequest(
    Guid UpToMessageId,
    string? RoomId = null);

/// <summary>Response of <c>chat.markRead</c> and <c>POST /chat/{roomId}/read</c>.</summary>
/// <param name="RoomId">Room.</param>
/// <param name="Unread">Remaining unread count.</param>
public sealed record ChatMarkReadResponse(
    string RoomId,
    int Unread);

#endregion

#region Booking payloads

/// <summary>Request of <c>booking.seats</c>.</summary>
/// <param name="Date">Club-local date.</param>
public sealed record BookingSeatsRequest(DateOnly Date);

/// <summary>Response of <c>booking.seats</c> and <c>GET /booking/seats</c>. Other users' bookings are anonymized (<c>userId</c> = <see cref="Guid.Empty"/>).</summary>
/// <param name="Date">Date.</param>
/// <param name="Seats">Seat map.</param>
/// <param name="Bookings">All bookings of that day.</param>
/// <param name="SlotMinutes">Slot granularity.</param>
/// <param name="OpenFrom">Club opening time (REST only).</param>
/// <param name="OpenTo">Club closing time (REST only).</param>
public sealed record BookingSeatsResponse(
    DateOnly Date,
    IReadOnlyList<Seat> Seats,
    IReadOnlyList<Booking> Bookings,
    int SlotMinutes,
    TimeOnly? OpenFrom = null,
    TimeOnly? OpenTo = null);

/// <summary>Request of <c>booking.reserve</c>.</summary>
/// <param name="PcId">PC.</param>
/// <param name="From">Start (slot-aligned, future).</param>
/// <param name="To">End.</param>
public sealed record BookingReserveRequest(
    Guid PcId,
    DateTimeOffset From,
    DateTimeOffset To);

/// <summary>Request of <c>booking.cancel</c>.</summary>
/// <param name="BookingId">Booking id.</param>
public sealed record BookingCancelRequest(Guid BookingId);

#endregion

#region Tournament payloads

/// <summary>Request of <c>tournaments.list</c>.</summary>
/// <param name="State">Filter by state.</param>
/// <param name="GameId">Filter by game.</param>
public sealed record TournamentsListRequest(
    TournamentState? State = null,
    Guid? GameId = null);

/// <summary>Response of <c>tournaments.list</c> and <c>GET /tournaments</c>.</summary>
/// <param name="Items">Tournaments.</param>
public sealed record TournamentsListResponse(IReadOnlyList<Tournament> Items);

/// <summary>Request of <c>tournaments.join</c>.</summary>
/// <param name="TournamentId">Tournament id.</param>
public sealed record TournamentsJoinRequest(Guid TournamentId);

/// <summary>Request of <c>tournaments.leaderboard</c>.</summary>
/// <param name="TournamentId">Tournament id.</param>
/// <param name="Limit">Max entries (≤ 100).</param>
public sealed record TournamentsLeaderboardRequest(
    Guid TournamentId,
    int? Limit = null);

/// <summary>Response of <c>tournaments.leaderboard</c> and <c>GET /tournaments/{id}/leaderboard</c>.</summary>
/// <param name="TournamentId">Tournament id.</param>
/// <param name="Entries">Top entries.</param>
/// <param name="UpdatedAt">Last update.</param>
/// <param name="Me">Current user's entry when outside the top list.</param>
public sealed record TournamentsLeaderboardResponse(
    Guid TournamentId,
    IReadOnlyList<LeaderboardEntry> Entries,
    DateTimeOffset UpdatedAt,
    LeaderboardEntry? Me = null);

#endregion

#region Profile payloads

/// <summary>Response of <c>profile.achievements</c> and <c>GET /users/{userId}/achievements</c>.</summary>
/// <param name="Items">Achievements.</param>
public sealed record ProfileAchievementsResponse(IReadOnlyList<Achievement> Items);

#endregion

#region Settings payloads

/// <summary>Feature toggles exposed to the UI (<c>shell.json → features</c>).</summary>
/// <param name="Shop">Shop.</param>
/// <param name="Chat">Chat.</param>
/// <param name="Booking">Booking.</param>
/// <param name="Tournaments">Tournaments.</param>
/// <param name="Profile">Profile.</param>
/// <param name="Topup">Top-up.</param>
/// <param name="Apps">Apps.</param>
/// <param name="CallAdmin">Call admin.</param>
public sealed record ShellFeatures(
    bool Shop,
    bool Chat,
    bool Booking,
    bool Tournaments,
    bool Profile,
    bool Topup,
    bool Apps,
    bool CallAdmin);

/// <summary>Settings subset of <c>shell.json</c> exposed to the UI (IPC_PROTOCOL.md §6.21); persisted by the Agent.</summary>
/// <param name="Locale">UI locale.</param>
/// <param name="Theme">Theme id.</param>
/// <param name="AvailableThemes">Installed themes (read-only).</param>
/// <param name="Volume">Volume 0–100.</param>
/// <param name="Muted">Muted.</param>
/// <param name="IdleTimeoutSec">Idle lock timeout.</param>
/// <param name="ShowMetricsOverlay">Show the metrics overlay.</param>
/// <param name="AllowVirtualKeyboard">Allow the on-screen keyboard.</param>
/// <param name="UiSounds">UI sounds.</param>
/// <param name="Features">Feature toggles (read-only).</param>
/// <param name="Club">Club branding, banners and rules (read-only); <see langword="null"/> when the server sent none.</param>
public sealed record ShellSettings(
    Locale Locale,
    string Theme,
    IReadOnlyList<string> AvailableThemes,
    int Volume,
    bool Muted,
    int IdleTimeoutSec,
    bool ShowMetricsOverlay,
    bool AllowVirtualKeyboard,
    bool UiSounds,
    ShellFeatures Features,
    ShellClub? Club = null);

/// <summary>Request of <c>settings.set</c>; partial, at least one key.</summary>
/// <param name="Locale">UI locale.</param>
/// <param name="Theme">Theme id (must exist).</param>
/// <param name="Volume">Volume 0–100.</param>
/// <param name="Muted">Muted.</param>
/// <param name="IdleTimeoutSec">Idle lock timeout.</param>
/// <param name="ShowMetricsOverlay">Show the metrics overlay.</param>
/// <param name="AllowVirtualKeyboard">Allow the on-screen keyboard.</param>
/// <param name="UiSounds">UI sounds.</param>
public sealed record SettingsSetRequest(
    Locale? Locale = null,
    string? Theme = null,
    int? Volume = null,
    bool? Muted = null,
    int? IdleTimeoutSec = null,
    bool? ShowMetricsOverlay = null,
    bool? AllowVirtualKeyboard = null,
    bool? UiSounds = null)
{
    /// <summary><see langword="true"/> when no field is set (request is invalid).</summary>
    [JsonIgnore]
    public bool IsEmpty => Locale is null && Theme is null && Volume is null && Muted is null && IdleTimeoutSec is null
                           && ShowMetricsOverlay is null && AllowVirtualKeyboard is null && UiSounds is null;
}

#endregion

#region Sys payloads

/// <summary>Category of a <c>sys.callAdmin</c> ticket.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<CallAdminCategory>))]
public enum CallAdminCategory
{
    /// <summary>General help.</summary>
    Help,

    /// <summary>Technical problem.</summary>
    Technical,

    /// <summary>Shop order issue.</summary>
    Order,

    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>Severity of a client-side error forwarded via <c>sys.logClientError</c>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ClientErrorLevel>))]
public enum ClientErrorLevel
{
    /// <summary>Warning.</summary>
    Warn,

    /// <summary>Error.</summary>
    Error,
}

/// <summary>Request of <c>sys.ping</c>.</summary>
/// <param name="Seq">Monotonic sequence number.</param>
/// <param name="SentAt">Shell send time.</param>
public sealed record SysPingRequest(
    long Seq,
    DateTimeOffset SentAt);

/// <summary>Response (<c>sys.pong</c>) to <c>sys.ping</c>.</summary>
/// <param name="Seq">Echoed sequence number.</param>
/// <param name="SentAt">Echoed send time.</param>
/// <param name="ReceivedAt">Agent receive time.</param>
/// <param name="Connectivity">Server connectivity.</param>
public sealed record SysPongResponse(
    long Seq,
    DateTimeOffset SentAt,
    DateTimeOffset ReceivedAt,
    ConnectivityState Connectivity);

/// <summary>Request of <c>sys.hardware</c>.</summary>
/// <param name="Refresh">Force a rescan instead of the cached inventory.</param>
public sealed record SysHardwareRequest(bool? Refresh = null);

/// <summary>Request of <c>sys.callAdmin</c> (rate limited 1 per 30 s).</summary>
/// <param name="Category">Category.</param>
/// <param name="Message">Free text.</param>
public sealed record SysCallAdminRequest(
    CallAdminCategory Category,
    string? Message = null);

/// <summary>Response of <c>sys.callAdmin</c> and <c>POST /support/call-admin</c>. Offline: client-generated ticket id, <paramref name="QueuePosition"/> <see langword="null"/>.</summary>
/// <param name="TicketId">Ticket id.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="QueuePosition">Position in the admin queue, when known.</param>
public sealed record SysCallAdminResponse(
    Guid TicketId,
    DateTimeOffset CreatedAt,
    int? QueuePosition = null);

/// <summary>Request of <c>sys.reboot</c> / <c>sys.shutdown</c>.</summary>
/// <param name="DelaySec">Delay before the action.</param>
/// <param name="Reason">Free-text reason.</param>
public sealed record SysPowerRequest(
    int? DelaySec = null,
    string? Reason = null);

/// <summary>Request of <c>sys.lockScreen</c>.</summary>
/// <param name="Reason">Free-text reason.</param>
public sealed record SysLockScreenRequest(string? Reason = null);

/// <summary>Request of <c>sys.setVolume</c> and payload of the <c>setVolume</c> server command.</summary>
/// <param name="Level">Volume 0–100.</param>
/// <param name="Muted">Mute state; unchanged when <see langword="null"/>.</param>
public sealed record SetVolumeRequest(
    int Level,
    bool? Muted = null);

/// <summary>Response of <c>sys.setVolume</c> and result of the <c>setVolume</c> server command.</summary>
/// <param name="Level">Applied volume.</param>
/// <param name="Muted">Applied mute state.</param>
public sealed record VolumeState(
    int Level,
    bool Muted);

/// <summary>Request of <c>sys.setLocale</c>.</summary>
/// <param name="Locale">New locale.</param>
public sealed record SysSetLocaleRequest(Locale Locale);

/// <summary>Response of <c>sys.setLocale</c>.</summary>
/// <param name="Locale">Applied locale.</param>
public sealed record SysSetLocaleResponse(Locale Locale);

/// <summary>Request of <c>sys.unlockAdmin</c> (rate limited 3 attempts/min).</summary>
/// <param name="Pin">Admin PIN.</param>
public sealed record SysUnlockAdminRequest(string Pin);

/// <summary>Response of <c>sys.unlockAdmin</c>; grants the kiosk exit path for 5 minutes.</summary>
/// <param name="Ok">Always <see langword="true"/>.</param>
/// <param name="AdminToken">Short-lived token passed to <c>kiosk_exit</c>.</param>
/// <param name="ExpiresAt">Token expiry.</param>
public sealed record SysUnlockAdminResponse(
    bool Ok,
    string AdminToken,
    DateTimeOffset ExpiresAt);

/// <summary>Request of <c>sys.logClientError</c> (rate limited 10/s, silently dropped beyond).</summary>
/// <param name="Level">Severity.</param>
/// <param name="Message">Message.</param>
/// <param name="Stack">Stack trace.</param>
/// <param name="Route">Frontend route.</param>
public sealed record SysLogClientErrorRequest(
    ClientErrorLevel Level,
    string Message,
    string? Stack = null,
    string? Route = null);

/// <summary>Request of <c>sys.ackAdminMessage</c>.</summary>
/// <param name="Id">Id of the acknowledged <see cref="AdminMessage"/>.</param>
public sealed record SysAckAdminMessageRequest(Guid Id);

#endregion

#region Policy / update payloads

/// <summary>Where the policy returned by <c>policy.reload</c> came from.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<PolicySource>))]
public enum PolicySource
{
    /// <summary>Fetched from the server.</summary>
    Server,

    /// <summary>Loaded from <c>cache\policies.json</c>.</summary>
    Cache,

    /// <summary>Loaded from <c>policies.json</c> (last applied snapshot).</summary>
    File,
}

/// <summary>Request of <c>policy.reload</c>.</summary>
/// <param name="Force">Bypass the ETag cache.</param>
public sealed record PolicyReloadRequest(bool? Force = null);

/// <summary>Response of <c>policy.reload</c>.</summary>
/// <param name="Policy">Policy now in effect.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="Applied">Whether it was (re)applied.</param>
/// <param name="Changed">Top-level sections that changed.</param>
public sealed record PolicyReloadResponse(
    Policy Policy,
    PolicySource Source,
    bool Applied,
    IReadOnlyList<string> Changed);

/// <summary>Installed component versions.</summary>
/// <param name="Agent">Agent semver.</param>
/// <param name="Shell">Shell semver.</param>
public sealed record ComponentVersions(
    string Agent,
    string Shell);

/// <summary>Response of <c>update.check</c>.</summary>
/// <param name="Current">Installed versions.</param>
/// <param name="Agent">Available Agent update, when any.</param>
/// <param name="Shell">Available Shell update, when any.</param>
public sealed record UpdateCheckResponse(
    ComponentVersions Current,
    UpdateManifest? Agent = null,
    UpdateManifest? Shell = null);

/// <summary>Request of <c>update.apply</c>.</summary>
/// <param name="Component">Component whose staged package to apply.</param>
public sealed record UpdateApplyRequest(UpdateComponent Component);

/// <summary>Response of <c>update.apply</c> and result of the <c>update</c> server command.</summary>
/// <param name="Scheduled">Whether the apply was scheduled.</param>
/// <param name="At">When it will run.</param>
public sealed record UpdateApplyResponse(
    bool Scheduled,
    DateTimeOffset? At = null);

#endregion

#region Event payloads

/// <summary>Remote-control state (<c>admin.remoteControl</c>).</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<RemoteControlState>))]
public enum RemoteControlState
{
    /// <summary>Remote control started.</summary>
    Started,

    /// <summary>Remote control stopped.</summary>
    Stopped,
}

/// <summary>UI command carried by <c>shell.command</c>.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ShellCommandKind>))]
public enum ShellCommandKind
{
    /// <summary>Show the lock screen; args <see cref="LockCommand"/>.</summary>
    Lock,

    /// <summary>Hide the lock screen; no args.</summary>
    Unlock,

    /// <summary>Reboot notice (Agent performs the reboot); args <see cref="ShellRebootArgs"/>.</summary>
    Reboot,

    /// <summary>Play ads; args <see cref="ShowAdsArgs"/>.</summary>
    ShowAds,

    /// <summary>Show a message; args <see cref="ShowMessageArgs"/>.</summary>
    ShowMessage,
}

/// <summary>Media type of an ad item.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<AdMediaType>))]
public enum AdMediaType
{
    /// <summary>Still image.</summary>
    Image,

    /// <summary>Video.</summary>
    Video,
}

/// <summary>Payload of <c>admin.message</c>.</summary>
/// <param name="Id">Message id (ack via <c>sys.ackAdminMessage</c>).</param>
/// <param name="From">Sender name.</param>
/// <param name="Text">Text.</param>
/// <param name="Level">Severity.</param>
/// <param name="RequiresAck">User must acknowledge (modal).</param>
/// <param name="At">Receipt time.</param>
public sealed record AdminMessage(
    Guid Id,
    string From,
    string Text,
    NotificationLevel Level,
    bool RequiresAck,
    DateTimeOffset At)
{
    /// <summary>Builds from the server command payload.</summary>
    public static AdminMessage FromCommand(MessageCommand command, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new(command.Id, command.From, command.Text, command.Level, command.RequiresAck, at);
    }
}

/// <summary>Payload of <c>admin.remoteControl</c>.</summary>
/// <param name="State">Started/stopped.</param>
/// <param name="At">Transition time.</param>
/// <param name="ShowIndicator">Show the on-screen indicator.</param>
/// <param name="AdminName">Admin name, when known.</param>
public sealed record RemoteControlEvent(
    RemoteControlState State,
    DateTimeOffset At,
    bool ShowIndicator,
    string? AdminName = null);

/// <summary>Payload of <c>game.stateChanged</c>.</summary>
/// <param name="GameId">Game.</param>
/// <param name="Title">Game title.</param>
/// <param name="State">New state.</param>
/// <param name="At">Transition time.</param>
/// <param name="Pid">Process id, when known.</param>
/// <param name="ExitCode">Exit code (<see cref="GameState.Exited"/>).</param>
/// <param name="Error">Failure (<see cref="GameState.Failed"/>).</param>
public sealed record GameStateChanged(
    Guid GameId,
    string Title,
    GameState State,
    DateTimeOffset At,
    int? Pid = null,
    int? ExitCode = null,
    IpcError? Error = null);

/// <summary>Payload of <c>policy.changed</c>.</summary>
/// <param name="Version">Policy version.</param>
/// <param name="UpdatedAt">Policy timestamp.</param>
/// <param name="Changed">Top-level sections that changed.</param>
/// <param name="Policy">Policy now in effect.</param>
public sealed record PolicyChanged(
    int Version,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> Changed,
    Policy Policy);

/// <summary>Payload of <c>update.available</c>.</summary>
/// <param name="Manifest">Newer package.</param>
/// <param name="Current">Installed version of that component.</param>
public sealed record UpdateAvailable(
    UpdateManifest Manifest,
    string Current);

/// <summary>Payload of <c>update.progress</c> (≤ 2/s).</summary>
/// <param name="Component">Component.</param>
/// <param name="Version">Package version.</param>
/// <param name="Phase">Phase.</param>
/// <param name="Percent">0–100.</param>
/// <param name="BytesDone">Bytes processed.</param>
/// <param name="BytesTotal">Total bytes.</param>
/// <param name="Error">Failure (<see cref="UpdatePhase.Failed"/>).</param>
public sealed record UpdateProgress(
    UpdateComponent Component,
    string Version,
    UpdatePhase Phase,
    int Percent,
    long BytesDone,
    long BytesTotal,
    IpcError? Error = null);

/// <summary>Payload of <c>update.ready</c> (package staged and verified).</summary>
/// <param name="Component">Component.</param>
/// <param name="Version">Package version.</param>
/// <param name="RestartRequired">Whether applying restarts the component.</param>
/// <param name="Mandatory">Will be applied even during a session.</param>
/// <param name="ApplyAt">Scheduled apply time, when known.</param>
public sealed record UpdateReady(
    UpdateComponent Component,
    string Version,
    bool RestartRequired,
    bool Mandatory,
    DateTimeOffset? ApplyAt = null);

/// <summary>Payload of <c>sys.connectivity</c> (transitions + every 60 s).</summary>
/// <param name="State">Connectivity.</param>
/// <param name="Since">When the state was entered.</param>
/// <param name="QueuedEvents">Outbox size.</param>
/// <param name="ServerLatencyMs">Last heartbeat latency, when online.</param>
public sealed record ConnectivityEvent(
    ConnectivityState State,
    DateTimeOffset Since,
    int QueuedEvents,
    int? ServerLatencyMs = null);

/// <summary>Args of <see cref="ShellCommandKind.Reboot"/> (informational; the Agent performs the reboot).</summary>
/// <param name="DelaySec">Seconds until reboot.</param>
/// <param name="Message">Message to display.</param>
public sealed record ShellRebootArgs(
    int DelaySec,
    string? Message = null);

/// <summary>One ad in a <see cref="ShowAdsArgs"/> playlist.</summary>
/// <param name="Url">Media URL.</param>
/// <param name="Type">Media type.</param>
/// <param name="DurationSec">Display duration.</param>
public sealed record AdItem(
    string Url,
    AdMediaType Type,
    int DurationSec);

/// <summary>Args of <see cref="ShellCommandKind.ShowAds"/> and payload of the <c>showAds</c> server command.</summary>
/// <param name="Items">Playlist.</param>
/// <param name="Skippable">User may skip.</param>
public sealed record ShowAdsArgs(
    IReadOnlyList<AdItem> Items,
    bool Skippable);

/// <summary>Args of <see cref="ShellCommandKind.ShowMessage"/>.</summary>
/// <param name="Title">Title.</param>
/// <param name="Body">Body.</param>
/// <param name="Level">Severity.</param>
/// <param name="TtlSec">Auto-dismiss after this many seconds.</param>
public sealed record ShowMessageArgs(
    string Title,
    string Body,
    NotificationLevel Level,
    int? TtlSec = null);

/// <summary>Payload of <c>shell.command</c>.</summary>
/// <param name="Command">Command kind.</param>
/// <param name="Args">Typed args (see <see cref="ShellCommandKind"/>) or <see langword="null"/>; key always present.</param>
/// <param name="CommandId">Originating server command id (or a fresh id for Agent-originated commands).</param>
public sealed record ShellCommand(
    ShellCommandKind Command,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] JsonElement? Args,
    Guid CommandId)
{
    /// <summary>Builds a command with typed args.</summary>
    public static ShellCommand Of<TArgs>(ShellCommandKind command, TArgs? args, Guid commandId) where TArgs : class =>
        new(command, args is null ? null : JsonDefaults.ToElement(args), commandId);

    /// <summary>Deserializes <see cref="Args"/> as <typeparamref name="TArgs"/>; <see langword="null"/> when absent.</summary>
    public TArgs? ArgsAs<TArgs>() where TArgs : class =>
        Args is { ValueKind: JsonValueKind.Object } a ? JsonDefaults.FromElement<TArgs>(a) : null;
}

/// <summary>Payload of <c>auth.expired</c>; the Shell returns to the login screen.</summary>
/// <param name="Reason">Why the user context was invalidated.</param>
public sealed record AuthExpired(AuthExpiredReason Reason);

#endregion
