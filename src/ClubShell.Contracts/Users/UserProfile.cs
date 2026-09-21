using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Wallet;

namespace ClubShell.Contracts.Users;

#region User

/// <summary>User role; ordered by privilege.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<UserRole>))]
public enum UserRole
{
    /// <summary>Transient guest.</summary>
    Guest,

    /// <summary>Registered member.</summary>
    Member,

    /// <summary>VIP member.</summary>
    Vip,

    /// <summary>Club staff / admin.</summary>
    Admin,
}

/// <summary>UI locale.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<Locale>))]
public enum Locale
{
    /// <summary>English.</summary>
    En,

    /// <summary>Russian.</summary>
    Ru,

    /// <summary>Uzbek.</summary>
    Uz,
}

/// <summary>Well-known values of <see cref="User.Flags"/>.</summary>
public static class UserFlags
{
    /// <summary>Account banned.</summary>
    public const string Banned = "banned";

    /// <summary>Shop disabled for this user.</summary>
    public const string NoShop = "noShop";

    /// <summary>Club staff.</summary>
    public const string Staff = "staff";
}

/// <summary>Player account (IPC_PROTOCOL.md §6.3).</summary>
/// <param name="Id">User id.</param>
/// <param name="Username">Login name, 3–32 chars.</param>
/// <param name="DisplayName">Display name.</param>
/// <param name="AvatarUrl">Absolute avatar URL.</param>
/// <param name="Role">Role.</param>
/// <param name="Balance">Main balance (excludes bonus).</param>
/// <param name="LoyaltyLevel">0-based loyalty level.</param>
/// <param name="LoyaltyPoints">Loyalty points.</param>
/// <param name="CreatedAt">Registration time.</param>
/// <param name="LastSeenAt">Last activity.</param>
/// <param name="Locale">Preferred locale.</param>
/// <param name="Flags">Free-form server flags (<see cref="UserFlags"/>); empty allowed.</param>
public sealed record User(
    Guid Id,
    string Username,
    string DisplayName,
    string? AvatarUrl,
    UserRole Role,
    Money Balance,
    int LoyaltyLevel,
    int LoyaltyPoints,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    Locale Locale,
    IReadOnlyList<string> Flags)
{
    /// <summary><see langword="true"/> when <paramref name="flag"/> is present (ordinal comparison).</summary>
    public bool HasFlag(string flag) => Flags.Contains(flag, StringComparer.Ordinal);

    /// <summary><see langword="true"/> when the user is a registered member or higher.</summary>
    [JsonIgnore]
    public bool IsMember => Role >= UserRole.Member;
}

/// <summary>Body of <c>profile.update</c> and <c>PATCH /users/{userId}</c>; all fields optional.</summary>
/// <param name="DisplayName">New display name.</param>
/// <param name="AvatarUrl">New avatar URL.</param>
/// <param name="Locale">New locale.</param>
/// <param name="Pin">New 4–6 digit PIN used by <c>session.unlock</c>.</param>
public sealed record ProfileUpdateRequest(
    string? DisplayName = null,
    string? AvatarUrl = null,
    Locale? Locale = null,
    string? Pin = null);

#endregion

#region Stats / achievements / loyalty

/// <summary>Play time of one game for the stats view.</summary>
/// <param name="GameId">Game.</param>
/// <param name="Hours">Hours played.</param>
public sealed record FavoriteGame(
    Guid GameId,
    double Hours);

/// <summary>Aggregate statistics of a user (IPC_PROTOCOL.md §6.17).</summary>
/// <param name="TotalHours">Total hours played.</param>
/// <param name="SessionsCount">Number of sessions.</param>
/// <param name="FavoriteGames">Most played games.</param>
/// <param name="Spent">Total spent.</param>
/// <param name="Rank">Club-wide rank.</param>
public sealed record UserStats(
    double TotalHours,
    int SessionsCount,
    IReadOnlyList<FavoriteGame> FavoriteGames,
    Money Spent,
    int Rank);

/// <summary>Progress towards an achievement.</summary>
/// <param name="Current">Current value.</param>
/// <param name="Target">Value required to unlock.</param>
public sealed record AchievementProgress(
    int Current,
    int Target);

/// <summary>Achievement (IPC_PROTOCOL.md §6.17).</summary>
/// <param name="Id">Achievement id.</param>
/// <param name="Title">Title.</param>
/// <param name="Description">Description.</param>
/// <param name="IconUrl">Icon URL.</param>
/// <param name="UnlockedAt">Unlock time; <see langword="null"/> while locked.</param>
/// <param name="Progress">Progress.</param>
public sealed record Achievement(
    Guid Id,
    string Title,
    string Description,
    string IconUrl,
    DateTimeOffset? UnlockedAt,
    AchievementProgress Progress);

/// <summary>Loyalty programme status (IPC_PROTOCOL.md §6.17).</summary>
/// <param name="Level">Current level (0-based).</param>
/// <param name="Points">Current points.</param>
/// <param name="NextLevelAt">Points required for the next level.</param>
/// <param name="Perks">Localized perk descriptions.</param>
public sealed record Loyalty(
    int Level,
    int Points,
    int NextLevelAt,
    IReadOnlyList<string> Perks);

#endregion

#region Notifications

/// <summary>Notification severity.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<NotificationLevel>))]
public enum NotificationLevel
{
    /// <summary>Informational.</summary>
    Info,

    /// <summary>Warning.</summary>
    Warning,

    /// <summary>Error.</summary>
    Error,

    /// <summary>Success.</summary>
    Success,
}

/// <summary>Optional call-to-action of a <see cref="Notification"/>.</summary>
/// <param name="Label">Button label.</param>
/// <param name="Command">Frontend route (e.g. <c>/shop</c>) or an <see cref="Commands.AgentCommand"/> IPC name (e.g. <c>wallet.topupIntent</c>).</param>
/// <param name="Args">Arguments for <paramref name="Command"/>.</param>
public sealed record NotificationAction(
    string Label,
    string Command,
    JsonElement? Args = null);

/// <summary>Toast/notification (IPC_PROTOCOL.md §6.21). Payload of <c>notification.push</c>.</summary>
/// <param name="Id">Notification id.</param>
/// <param name="Title">Title.</param>
/// <param name="Body">Body text.</param>
/// <param name="Level">Severity.</param>
/// <param name="TtlSec">Auto-dismiss after this many seconds; <see langword="null"/> = sticky.</param>
/// <param name="Action">Optional call-to-action.</param>
public sealed record Notification(
    Guid Id,
    string Title,
    string Body,
    NotificationLevel Level,
    int? TtlSec = null,
    NotificationAction? Action = null);

#endregion

#region Chat

/// <summary>Kind of chat message.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ChatMessageKind>))]
public enum ChatMessageKind
{
    /// <summary>Regular user message.</summary>
    Text,

    /// <summary>System-generated notice.</summary>
    System,

    /// <summary>Message from club staff.</summary>
    Admin,
}

/// <summary>Chat room id helpers (SERVER_API.md §4.10).</summary>
public static class ChatRooms
{
    /// <summary>Club-wide room.</summary>
    public const string Club = "club";

    /// <summary>Maximum message length.</summary>
    public const int MaxTextLength = 2000;

    /// <summary>Support room of a PC: <c>pc:&lt;pcId&gt;</c>.</summary>
    public static string ForPc(Guid pcId) => $"pc:{pcId:D}";

    /// <summary>Zone room: <c>zone:&lt;zone&gt;</c>.</summary>
    public static string ForZone(string zone)
    {
        ArgumentException.ThrowIfNullOrEmpty(zone);
        return $"zone:{zone}";
    }

    /// <summary>Direct-message room: <c>dm:&lt;a&gt;:&lt;b&gt;</c> with ids sorted.</summary>
    public static string ForDirect(Guid a, Guid b)
    {
        var (first, second) = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
        return $"dm:{first:D}:{second:D}";
    }
}

/// <summary>Chat message (IPC_PROTOCOL.md §6.14).</summary>
/// <param name="Id">Message id.</param>
/// <param name="RoomId">Room (<see cref="ChatRooms"/>).</param>
/// <param name="SenderId">Sender user id.</param>
/// <param name="SenderName">Sender display name.</param>
/// <param name="SenderRole">Sender role.</param>
/// <param name="Text">Text, ≤ 2000 chars.</param>
/// <param name="CreatedAt">Send time.</param>
/// <param name="ReadAt">When the current user read it.</param>
/// <param name="Kind">Kind.</param>
public sealed record ChatMessage(
    Guid Id,
    string RoomId,
    Guid SenderId,
    string SenderName,
    UserRole SenderRole,
    string Text,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    ChatMessageKind Kind);

/// <summary>Body of <c>POST /chat/{roomId}/messages</c>.</summary>
/// <param name="Text">Message text.</param>
public sealed record ChatPostRequest(string Text);

/// <summary>Body of <c>POST /chat/{roomId}/read</c>.</summary>
/// <param name="UpToMessageId">Mark everything up to and including this message as read.</param>
public sealed record ChatReadRequest(Guid UpToMessageId);

#endregion

#region Booking

/// <summary>Booking lifecycle.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<BookingStatus>))]
public enum BookingStatus
{
    /// <summary>Reserved, awaiting confirmation/deposit.</summary>
    Reserved,

    /// <summary>Confirmed.</summary>
    Confirmed,

    /// <summary>Cancelled.</summary>
    Cancelled,

    /// <summary>Expired unused.</summary>
    Expired,
}

/// <summary>Seat on the club map (IPC_PROTOCOL.md §6.15).</summary>
/// <param name="PcId">PC.</param>
/// <param name="Name">PC name.</param>
/// <param name="Zone">Zone.</param>
/// <param name="X">Grid column.</param>
/// <param name="Y">Grid row.</param>
/// <param name="Status">Current status.</param>
public sealed record Seat(
    Guid PcId,
    string Name,
    string Zone,
    int X,
    int Y,
    PcStatus Status);

/// <summary>Seat reservation (IPC_PROTOCOL.md §6.15). In <c>booking.seats</c> lists, <paramref name="UserId"/> is the caller's id or <see cref="Guid.Empty"/> for other users.</summary>
/// <param name="Id">Booking id.</param>
/// <param name="UserId">Owner (anonymized for others).</param>
/// <param name="PcId">PC.</param>
/// <param name="From">Start.</param>
/// <param name="To">End.</param>
/// <param name="Status">Status.</param>
public sealed record Booking(
    Guid Id,
    Guid UserId,
    Guid PcId,
    DateTimeOffset From,
    DateTimeOffset To,
    BookingStatus Status);

/// <summary>Body of <c>POST /booking/reserve</c>. Sent with an <c>Idempotency-Key</c>.</summary>
/// <param name="UserId">Owner.</param>
/// <param name="PcId">PC.</param>
/// <param name="From">Start (slot-aligned).</param>
/// <param name="To">End (slot-aligned).</param>
public sealed record BookingCreateRequest(
    Guid UserId,
    Guid PcId,
    DateTimeOffset From,
    DateTimeOffset To);

#endregion

#region Tournaments

/// <summary>Tournament lifecycle.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<TournamentState>))]
public enum TournamentState
{
    /// <summary>Announced; registration not open.</summary>
    Upcoming,

    /// <summary>Registration open.</summary>
    Registration,

    /// <summary>In progress.</summary>
    Live,

    /// <summary>Finished.</summary>
    Finished,
}

/// <summary>Bracket match; <paramref name="A"/>, <paramref name="B"/>, <paramref name="Winner"/> are user ids.</summary>
/// <param name="Id">Match id.</param>
/// <param name="A">First player.</param>
/// <param name="B">Second player.</param>
/// <param name="Winner">Winner.</param>
/// <param name="Score">Score, e.g. <c>2-1</c>.</param>
public sealed record BracketMatch(
    Guid Id,
    Guid? A = null,
    Guid? B = null,
    Guid? Winner = null,
    string? Score = null);

/// <summary>Bracket round.</summary>
/// <param name="Matches">Matches in this round.</param>
public sealed record BracketRound(IReadOnlyList<BracketMatch> Matches);

/// <summary>Tournament bracket.</summary>
/// <param name="Rounds">Rounds in order.</param>
public sealed record Bracket(IReadOnlyList<BracketRound> Rounds);

/// <summary>Tournament (IPC_PROTOCOL.md §6.16).</summary>
/// <param name="Id">Tournament id.</param>
/// <param name="Title">Title.</param>
/// <param name="GameId">Game.</param>
/// <param name="StartsAt">Start time.</param>
/// <param name="State">State.</param>
/// <param name="PrizePool">Prize pool.</param>
/// <param name="MaxPlayers">Capacity.</param>
/// <param name="Players">Current player count.</param>
/// <param name="Joined">Whether the current user has joined.</param>
/// <param name="Bracket">Bracket, when published.</param>
public sealed record Tournament(
    Guid Id,
    string Title,
    Guid GameId,
    DateTimeOffset StartsAt,
    TournamentState State,
    Money PrizePool,
    int MaxPlayers,
    int Players,
    bool Joined,
    Bracket? Bracket = null);

/// <summary>Leaderboard row (IPC_PROTOCOL.md §6.16).</summary>
/// <param name="Rank">1-based rank.</param>
/// <param name="UserId">User.</param>
/// <param name="Name">Display name.</param>
/// <param name="Score">Score.</param>
/// <param name="AvatarUrl">Avatar URL.</param>
public sealed record LeaderboardEntry(
    int Rank,
    Guid UserId,
    string Name,
    long Score,
    string? AvatarUrl = null);

#endregion
