using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Contracts.Ipc;

namespace ClubShell.Contracts.Commands;

/// <summary>
/// Every Shell → Agent IPC request (IPC_PROTOCOL.md §7). Wire form is the IPC name (<c>auth.hello</c>, …) via
/// <see cref="AgentCommandJsonConverter"/>; use <see cref="AgentCommands.ToIpcName"/> / <see cref="AgentCommands.TryParse"/>.
/// </summary>
[JsonConverter(typeof(AgentCommandJsonConverter))]
public enum AgentCommand
{
    /// <summary><c>auth.hello</c></summary>
    AuthHello,

    /// <summary><c>auth.login</c></summary>
    AuthLogin,

    /// <summary><c>auth.logout</c></summary>
    AuthLogout,

    /// <summary><c>auth.status</c></summary>
    AuthStatus,

    /// <summary><c>auth.qrStart</c></summary>
    AuthQrStart,

    /// <summary><c>session.get</c></summary>
    SessionGet,

    /// <summary><c>session.start</c></summary>
    SessionStart,

    /// <summary><c>session.pause</c></summary>
    SessionPause,

    /// <summary><c>session.resume</c></summary>
    SessionResume,

    /// <summary><c>session.end</c></summary>
    SessionEnd,

    /// <summary><c>session.extend</c></summary>
    SessionExtend,

    /// <summary><c>session.lock</c></summary>
    SessionLock,

    /// <summary><c>session.unlock</c></summary>
    SessionUnlock,

    /// <summary><c>session.timeLeft</c></summary>
    SessionTimeLeft,

    /// <summary><c>games.list</c></summary>
    GamesList,

    /// <summary><c>games.get</c></summary>
    GamesGet,

    /// <summary><c>games.launch</c></summary>
    GamesLaunch,

    /// <summary><c>games.kill</c></summary>
    GamesKill,

    /// <summary><c>games.running</c></summary>
    GamesRunning,

    /// <summary><c>games.installStatus</c></summary>
    GamesInstallStatus,

    /// <summary><c>apps.list</c></summary>
    AppsList,

    /// <summary><c>apps.launch</c></summary>
    AppsLaunch,

    /// <summary><c>wallet.balance</c></summary>
    WalletBalance,

    /// <summary><c>wallet.tariffs</c></summary>
    WalletTariffs,

    /// <summary><c>wallet.history</c></summary>
    WalletHistory,

    /// <summary><c>wallet.topupIntent</c></summary>
    WalletTopupIntent,

    /// <summary><c>shop.products</c></summary>
    ShopProducts,

    /// <summary><c>shop.order</c></summary>
    ShopOrder,

    /// <summary><c>shop.orderStatus</c></summary>
    ShopOrderStatus,

    /// <summary><c>shop.orders</c></summary>
    ShopOrders,

    /// <summary><c>chat.history</c></summary>
    ChatHistory,

    /// <summary><c>chat.send</c></summary>
    ChatSend,

    /// <summary><c>chat.markRead</c></summary>
    ChatMarkRead,

    /// <summary><c>booking.seats</c></summary>
    BookingSeats,

    /// <summary><c>booking.reserve</c></summary>
    BookingReserve,

    /// <summary><c>booking.cancel</c></summary>
    BookingCancel,

    /// <summary><c>tournaments.list</c></summary>
    TournamentsList,

    /// <summary><c>tournaments.join</c></summary>
    TournamentsJoin,

    /// <summary><c>tournaments.leaderboard</c></summary>
    TournamentsLeaderboard,

    /// <summary><c>profile.get</c></summary>
    ProfileGet,

    /// <summary><c>profile.update</c></summary>
    ProfileUpdate,

    /// <summary><c>profile.stats</c></summary>
    ProfileStats,

    /// <summary><c>profile.achievements</c></summary>
    ProfileAchievements,

    /// <summary><c>profile.loyalty</c></summary>
    ProfileLoyalty,

    /// <summary><c>profile.gameSettings</c></summary>
    ProfileGameSettings,

    /// <summary><c>profile.gameSettingsReset</c></summary>
    ProfileGameSettingsReset,

    /// <summary><c>settings.get</c></summary>
    SettingsGet,

    /// <summary><c>settings.set</c></summary>
    SettingsSet,

    /// <summary><c>sys.ping</c> (answered as <c>sys.pong</c>)</summary>
    SysPing,

    /// <summary><c>sys.pcInfo</c></summary>
    SysPcInfo,

    /// <summary><c>sys.hardware</c></summary>
    SysHardware,

    /// <summary><c>sys.metrics</c></summary>
    SysMetrics,

    /// <summary><c>sys.callAdmin</c></summary>
    SysCallAdmin,

    /// <summary><c>sys.reboot</c></summary>
    SysReboot,

    /// <summary><c>sys.shutdown</c></summary>
    SysShutdown,

    /// <summary><c>sys.lockScreen</c></summary>
    SysLockScreen,

    /// <summary><c>sys.setVolume</c></summary>
    SysSetVolume,

    /// <summary><c>sys.setLocale</c></summary>
    SysSetLocale,

    /// <summary><c>sys.unlockAdmin</c></summary>
    SysUnlockAdmin,

    /// <summary><c>sys.logClientError</c></summary>
    SysLogClientError,

    /// <summary><c>sys.ackAdminMessage</c></summary>
    SysAckAdminMessage,

    /// <summary><c>policy.get</c></summary>
    PolicyGet,

    /// <summary><c>policy.reload</c></summary>
    PolicyReload,

    /// <summary><c>update.check</c></summary>
    UpdateCheck,

    /// <summary><c>update.apply</c></summary>
    UpdateApply,
}

/// <summary>Authentication level an IPC request requires (IPC_PROTOCOL.md §7 "auth" column).</summary>
[JsonConverter(typeof(Serialization.CamelCaseEnumConverter<IpcAuthLevel>))]
public enum IpcAuthLevel
{
    /// <summary>No handshake required (<c>auth.hello</c>, <c>sys.ping</c>).</summary>
    None,

    /// <summary>Successful <c>auth.hello</c> on the connection.</summary>
    Hello,

    /// <summary>A logged-in user.</summary>
    User,

    /// <summary>An active or paused session.</summary>
    Session,
}

/// <summary>Mapping between <see cref="AgentCommand"/> and IPC message names, plus per-command metadata.</summary>
public static class AgentCommands
{
    private static readonly AgentCommand[] AllValues = Enum.GetValues<AgentCommand>();

    private static readonly FrozenDictionary<string, AgentCommand> ByName =
        AllValues.ToFrozenDictionary(ToIpcName, static c => c, StringComparer.Ordinal);

    /// <summary>All commands in declaration order.</summary>
    public static IReadOnlyList<AgentCommand> All => AllValues;

    /// <summary>All IPC request names.</summary>
    public static IReadOnlyCollection<string> Names => ByName.Keys;

    /// <summary>IPC request name of <paramref name="command"/>, e.g. <c>session.start</c>.</summary>
    public static string ToIpcName(this AgentCommand command) => command switch
    {
        AgentCommand.AuthHello => IpcMessages.Auth.Hello,
        AgentCommand.AuthLogin => IpcMessages.Auth.Login,
        AgentCommand.AuthLogout => IpcMessages.Auth.Logout,
        AgentCommand.AuthStatus => IpcMessages.Auth.Status,
        AgentCommand.AuthQrStart => IpcMessages.Auth.QrStart,
        AgentCommand.SessionGet => IpcMessages.Session.Get,
        AgentCommand.SessionStart => IpcMessages.Session.Start,
        AgentCommand.SessionPause => IpcMessages.Session.Pause,
        AgentCommand.SessionResume => IpcMessages.Session.Resume,
        AgentCommand.SessionEnd => IpcMessages.Session.End,
        AgentCommand.SessionExtend => IpcMessages.Session.Extend,
        AgentCommand.SessionLock => IpcMessages.Session.Lock,
        AgentCommand.SessionUnlock => IpcMessages.Session.Unlock,
        AgentCommand.SessionTimeLeft => IpcMessages.Session.TimeLeft,
        AgentCommand.GamesList => IpcMessages.Games.List,
        AgentCommand.GamesGet => IpcMessages.Games.Get,
        AgentCommand.GamesLaunch => IpcMessages.Games.Launch,
        AgentCommand.GamesKill => IpcMessages.Games.Kill,
        AgentCommand.GamesRunning => IpcMessages.Games.Running,
        AgentCommand.GamesInstallStatus => IpcMessages.Games.InstallStatus,
        AgentCommand.AppsList => IpcMessages.Apps.List,
        AgentCommand.AppsLaunch => IpcMessages.Apps.Launch,
        AgentCommand.WalletBalance => IpcMessages.Wallet.Balance,
        AgentCommand.WalletTariffs => IpcMessages.Wallet.Tariffs,
        AgentCommand.WalletHistory => IpcMessages.Wallet.History,
        AgentCommand.WalletTopupIntent => IpcMessages.Wallet.TopupIntent,
        AgentCommand.ShopProducts => IpcMessages.Shop.Products,
        AgentCommand.ShopOrder => IpcMessages.Shop.Order,
        AgentCommand.ShopOrderStatus => IpcMessages.Shop.OrderStatus,
        AgentCommand.ShopOrders => IpcMessages.Shop.Orders,
        AgentCommand.ChatHistory => IpcMessages.Chat.History,
        AgentCommand.ChatSend => IpcMessages.Chat.Send,
        AgentCommand.ChatMarkRead => IpcMessages.Chat.MarkRead,
        AgentCommand.BookingSeats => IpcMessages.Booking.Seats,
        AgentCommand.BookingReserve => IpcMessages.Booking.Reserve,
        AgentCommand.BookingCancel => IpcMessages.Booking.Cancel,
        AgentCommand.TournamentsList => IpcMessages.Tournaments.List,
        AgentCommand.TournamentsJoin => IpcMessages.Tournaments.Join,
        AgentCommand.TournamentsLeaderboard => IpcMessages.Tournaments.Leaderboard,
        AgentCommand.ProfileGet => IpcMessages.Profile.Get,
        AgentCommand.ProfileUpdate => IpcMessages.Profile.Update,
        AgentCommand.ProfileStats => IpcMessages.Profile.Stats,
        AgentCommand.ProfileAchievements => IpcMessages.Profile.Achievements,
        AgentCommand.ProfileLoyalty => IpcMessages.Profile.Loyalty,
        AgentCommand.ProfileGameSettings => IpcMessages.Profile.GameSettings,
        AgentCommand.ProfileGameSettingsReset => IpcMessages.Profile.GameSettingsReset,
        AgentCommand.SettingsGet => IpcMessages.Settings.Get,
        AgentCommand.SettingsSet => IpcMessages.Settings.Set,
        AgentCommand.SysPing => IpcMessages.Sys.Ping,
        AgentCommand.SysPcInfo => IpcMessages.Sys.PcInfo,
        AgentCommand.SysHardware => IpcMessages.Sys.Hardware,
        AgentCommand.SysMetrics => IpcMessages.Sys.Metrics,
        AgentCommand.SysCallAdmin => IpcMessages.Sys.CallAdmin,
        AgentCommand.SysReboot => IpcMessages.Sys.Reboot,
        AgentCommand.SysShutdown => IpcMessages.Sys.Shutdown,
        AgentCommand.SysLockScreen => IpcMessages.Sys.LockScreen,
        AgentCommand.SysSetVolume => IpcMessages.Sys.SetVolume,
        AgentCommand.SysSetLocale => IpcMessages.Sys.SetLocale,
        AgentCommand.SysUnlockAdmin => IpcMessages.Sys.UnlockAdmin,
        AgentCommand.SysLogClientError => IpcMessages.Sys.LogClientError,
        AgentCommand.SysAckAdminMessage => IpcMessages.Sys.AckAdminMessage,
        AgentCommand.PolicyGet => IpcMessages.Policy.Get,
        AgentCommand.PolicyReload => IpcMessages.Policy.Reload,
        AgentCommand.UpdateCheck => IpcMessages.Update.Check,
        AgentCommand.UpdateApply => IpcMessages.Update.Apply,
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    };

    /// <summary>Name the response to <paramref name="command"/> carries (equal to the request name except <c>sys.ping</c> → <c>sys.pong</c>).</summary>
    public static string ResponseName(this AgentCommand command) =>
        command == AgentCommand.SysPing ? IpcMessages.Sys.Pong : command.ToIpcName();

    /// <summary>Authentication level the command requires.</summary>
    public static IpcAuthLevel RequiredAuth(this AgentCommand command) => command switch
    {
        AgentCommand.AuthHello or AgentCommand.SysPing => IpcAuthLevel.None,

        AgentCommand.AuthLogout
            or AgentCommand.SessionGet or AgentCommand.SessionStart or AgentCommand.SessionTimeLeft
            or AgentCommand.WalletBalance or AgentCommand.WalletHistory or AgentCommand.WalletTopupIntent
            or AgentCommand.ShopOrderStatus or AgentCommand.ShopOrders
            or AgentCommand.ChatHistory or AgentCommand.ChatSend or AgentCommand.ChatMarkRead
            or AgentCommand.BookingReserve or AgentCommand.BookingCancel
            or AgentCommand.TournamentsJoin
            or AgentCommand.ProfileGet or AgentCommand.ProfileUpdate or AgentCommand.ProfileStats
            or AgentCommand.ProfileAchievements or AgentCommand.ProfileLoyalty
            or AgentCommand.ProfileGameSettings or AgentCommand.ProfileGameSettingsReset => IpcAuthLevel.User,

        AgentCommand.SessionPause or AgentCommand.SessionResume or AgentCommand.SessionEnd
            or AgentCommand.SessionExtend or AgentCommand.SessionLock or AgentCommand.SessionUnlock
            or AgentCommand.GamesLaunch or AgentCommand.GamesKill
            or AgentCommand.AppsLaunch
            or AgentCommand.ShopOrder => IpcAuthLevel.Session,

        _ => IpcAuthLevel.Hello,
    };

    /// <summary>Parses an IPC request name (ordinal).</summary>
    public static bool TryParse(string? name, out AgentCommand command)
    {
        if (name is not null && ByName.TryGetValue(name, out command))
        {
            return true;
        }

        command = default;
        return false;
    }

    /// <summary>Parses an IPC request name or throws <see cref="ArgumentException"/>.</summary>
    public static AgentCommand Parse(string name) =>
        TryParse(name, out var command) ? command : throw new ArgumentException($"Unknown IPC request name '{name}'", nameof(name));

    /// <summary><see langword="true"/> when <paramref name="name"/> is a known request name.</summary>
    public static bool IsKnown(string? name) => name is not null && ByName.ContainsKey(name);
}

/// <summary>Serializes <see cref="AgentCommand"/> as its IPC name (<c>"session.start"</c>).</summary>
public sealed class AgentCommandJsonConverter : JsonConverter<AgentCommand>
{
    /// <inheritdoc />
    public override AgentCommand Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("AgentCommand must be a string");
        }

        var name = reader.GetString();
        return AgentCommands.TryParse(name, out var command)
            ? command
            : throw new JsonException($"Unknown AgentCommand '{name}'");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, AgentCommand value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToIpcName());
}
