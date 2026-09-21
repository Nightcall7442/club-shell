using System.Text.Json.Serialization;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Sessions;

namespace ClubShell.Contracts.Users;

/// <summary>Authentication method.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<AuthKind>))]
public enum AuthKind
{
    /// <summary>Username + password.</summary>
    Password,

    /// <summary>QR code scanned with the club's mobile app.</summary>
    Qr,

    /// <summary>Transient guest account.</summary>
    Guest,

    /// <summary>NFC/RFID card.</summary>
    Card,

    /// <summary>One-time admin/web token.</summary>
    Token,
}

/// <summary>State of a QR login handshake.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<QrStatus>))]
public enum QrStatus
{
    /// <summary>Not yet scanned.</summary>
    Pending,

    /// <summary>Scanned, awaiting confirmation in the app.</summary>
    Scanned,

    /// <summary>Confirmed; <see cref="QrLoginStatus.Auth"/> is set (single read).</summary>
    Confirmed,

    /// <summary>Expired or already consumed.</summary>
    Expired,
}

/// <summary>Why the user context was invalidated (<c>auth.expired</c> event).</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<AuthExpiredReason>))]
public enum AuthExpiredReason
{
    /// <summary>Access token expired and could not be refreshed.</summary>
    TokenExpired,

    /// <summary>Server revoked the user (<c>userRevoked</c> push).</summary>
    Revoked,

    /// <summary>Admin forced logout.</summary>
    Admin,
}

/// <summary>
/// Server-facing login request (<c>POST /auth/login</c>, IPC_PROTOCOL.md §6.20). The IPC <c>auth.login</c> payload
/// is <see cref="Ipc.AuthLoginRequest"/> (same fields without <paramref name="PcId"/>/<paramref name="Hwid"/>).
/// <paramref name="Password"/> must never be logged (Serilog destructuring replaces it with <c>***</c>).
/// </summary>
/// <param name="Kind">Method.</param>
/// <param name="Username">Login name (<see cref="AuthKind.Password"/>).</param>
/// <param name="Password">Password (<see cref="AuthKind.Password"/>).</param>
/// <param name="QrToken">QR token (<see cref="AuthKind.Qr"/>).</param>
/// <param name="CardId">Card id (<see cref="AuthKind.Card"/>).</param>
/// <param name="Token">One-time token (<see cref="AuthKind.Token"/>).</param>
/// <param name="PcId">PC the login originates from.</param>
/// <param name="Hwid">Hardware id of the PC (sha256 hex).</param>
public sealed record AuthRequest(
    AuthKind Kind,
    string? Username,
    string? Password,
    string? QrToken,
    string? CardId,
    string? Token,
    Guid PcId,
    string Hwid)
{
    /// <summary>Copy with secrets blanked, for logging/telemetry.</summary>
    public AuthRequest Redacted() => this with
    {
        Password = Password is null ? null : "***",
        QrToken = QrToken is null ? null : "***",
        CardId = CardId is null ? null : "***",
        Token = Token is null ? null : "***",
    };
}

/// <summary>Result of a successful login (IPC_PROTOCOL.md §6.20, SERVER_API.md §4.3).</summary>
/// <param name="User">Authenticated user.</param>
/// <param name="Session">Open session already bound to this user on this PC (e.g. after an Agent restart).</param>
/// <param name="AccessToken">User access token (<c>X-User-Token</c>); Agent-held, never sent to the Shell.</param>
/// <param name="RefreshToken">User refresh token.</param>
/// <param name="ExpiresAt">Access token expiry.</param>
/// <param name="OfflineHash">Argon2id PHC string for offline password verification, when the club allows offline login.</param>
public sealed record AuthResponse(
    User User,
    Session? Session,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string? OfflineHash = null);

/// <summary>Body of <c>POST /auth/qr/start</c>.</summary>
/// <param name="PcId">PC requesting the QR login.</param>
public sealed record QrStartRequest(Guid PcId);

/// <summary>Response of <c>auth.qrStart</c> and <c>POST /auth/qr/start</c>.</summary>
/// <param name="QrToken">Opaque token to poll with.</param>
/// <param name="QrUrl">Deep link to render as a QR code (<c>https://&lt;server&gt;/q/&lt;qrToken&gt;</c>).</param>
/// <param name="ExpiresAt">Token expiry.</param>
/// <param name="PollIntervalSec">Suggested poll interval.</param>
public sealed record QrLoginStart(
    string QrToken,
    string QrUrl,
    DateTimeOffset ExpiresAt,
    int PollIntervalSec);

/// <summary>Response of <c>GET /auth/qr/{token}</c>.</summary>
/// <param name="Status">Handshake state.</param>
/// <param name="Auth">Set only when <paramref name="Status"/> is <see cref="QrStatus.Confirmed"/> (single read).</param>
public sealed record QrLoginStatus(
    QrStatus Status,
    AuthResponse? Auth = null);

/// <summary>Body of <c>POST /auth/guest</c>.</summary>
/// <param name="PcId">PC.</param>
/// <param name="Hwid">Hardware id.</param>
/// <param name="DisplayName">Optional display name.</param>
/// <param name="Locale">Preferred locale.</param>
public sealed record GuestAuthRequest(
    Guid PcId,
    string Hwid,
    string? DisplayName = null,
    Locale? Locale = null);

/// <summary>Body of <c>POST /auth/logout</c>.</summary>
/// <param name="Reason">Why the user is logged out; the server records it as the session end reason.</param>
public sealed record LogoutRequest(SessionEndReason Reason);
