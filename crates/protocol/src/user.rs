//! Mirror of `ClubShell.Contracts.Users` (AuthRequest.cs, UserProfile.cs): users, auth, stats,
//! notifications, chat, booking and tournaments.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use uuid::Uuid;

use crate::pc::PcStatus;
use crate::session::{Session, SessionEndReason};
use crate::wallet::Money;

// ───────────────────────────── User ─────────────────────────────

wire_enum! {
    /// User role; ordered by privilege.
    UserRole {
        /// Transient guest.
        Guest = "guest",
        Member = "member",
        Vip = "vip",
        /// Club staff / admin.
        Admin = "admin",
    }
}

wire_enum! {
    /// UI locale.
    Locale {
        En = "en",
        Ru = "ru",
        Uz = "uz",
    }
}

/// Well-known values of [`User::flags`].
pub mod user_flags {
    pub const BANNED: &str = "banned";
    pub const NO_SHOP: &str = "noShop";
    pub const STAFF: &str = "staff";
}

/// Player account (IPC_PROTOCOL.md §6.3).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct User {
    pub id: Uuid,
    /// Login name, 3–32 chars.
    pub username: String,
    pub display_name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub avatar_url: Option<String>,
    pub role: UserRole,
    /// Main balance (excludes bonus).
    pub balance: Money,
    /// 0-based loyalty level.
    pub loyalty_level: i32,
    pub loyalty_points: i32,
    #[serde(with = "crate::wire::ts")]
    pub created_at: DateTime<Utc>,
    #[serde(default, with = "crate::wire::ts_opt", skip_serializing_if = "Option::is_none")]
    pub last_seen_at: Option<DateTime<Utc>>,
    pub locale: Locale,
    /// Free-form server flags ([`user_flags`]); empty allowed.
    pub flags: Vec<String>,
}

// ---- BEGIN MANUAL ----
impl User {
    /// `true` when `flag` is present (exact comparison).
    pub fn has_flag(&self, flag: &str) -> bool {
        self.flags.iter().any(|f| f == flag)
    }

    /// `true` when the user is a registered member or higher.
    pub fn is_member(&self) -> bool {
        !matches!(self.role, UserRole::Guest)
    }
}
// ---- END MANUAL ----

/// Body of `profile.update` and `PATCH /users/{userId}`; all fields optional.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct ProfileUpdateRequest {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub display_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub avatar_url: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub locale: Option<Locale>,
    /// New 4–6 digit PIN used by `session.unlock`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pin: Option<String>,
}

// ───────────────────────── Stats / achievements / loyalty ─────────────────────────

/// Play time of one game for the stats view.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct FavoriteGame {
    pub game_id: Uuid,
    #[serde(with = "crate::wire::num")]
    pub hours: f64,
}

/// Aggregate statistics of a user (IPC_PROTOCOL.md §6.17).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct UserStats {
    #[serde(with = "crate::wire::num")]
    pub total_hours: f64,
    pub sessions_count: i32,
    pub favorite_games: Vec<FavoriteGame>,
    pub spent: Money,
    /// Club-wide rank.
    pub rank: i32,
}

/// Progress towards an achievement.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct AchievementProgress {
    pub current: i32,
    pub target: i32,
}

/// Achievement (IPC_PROTOCOL.md §6.17).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Achievement {
    pub id: Uuid,
    pub title: String,
    pub description: String,
    pub icon_url: String,
    /// Unlock time; `None` while locked.
    #[serde(default, with = "crate::wire::ts_opt", skip_serializing_if = "Option::is_none")]
    pub unlocked_at: Option<DateTime<Utc>>,
    pub progress: AchievementProgress,
}

/// Loyalty programme status (IPC_PROTOCOL.md §6.17).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Loyalty {
    /// Current level (0-based).
    pub level: i32,
    pub points: i32,
    /// Points required for the next level.
    pub next_level_at: i32,
    /// Localized perk descriptions.
    pub perks: Vec<String>,
}

// ───────────────────────────── Notifications ─────────────────────────────

wire_enum! {
    /// Notification severity.
    NotificationLevel {
        Info = "info",
        Warning = "warning",
        Error = "error",
        Success = "success",
    }
}

/// Optional call-to-action of a [`Notification`].
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct NotificationAction {
    pub label: String,
    /// Frontend route (e.g. `/shop`) or an `AgentCommand` IPC name (e.g. `wallet.topupIntent`).
    pub command: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub args: Option<Value>,
}

/// Toast/notification (IPC_PROTOCOL.md §6.21). Payload of `notification.push`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Notification {
    pub id: Uuid,
    pub title: String,
    pub body: String,
    pub level: NotificationLevel,
    /// Auto-dismiss after this many seconds; `None` = sticky.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ttl_sec: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub action: Option<NotificationAction>,
}

// ───────────────────────────── Chat ─────────────────────────────

wire_enum! {
    /// Kind of chat message.
    ChatMessageKind {
        /// Regular user message.
        Text = "text",
        /// System-generated notice.
        System = "system",
        /// Message from club staff.
        Admin = "admin",
    }
}

// ---- BEGIN MANUAL ----
/// Chat room id helpers (SERVER_API.md §4.10).
pub mod chat_rooms {
    use uuid::Uuid;

    /// Club-wide room.
    pub const CLUB: &str = "club";

    /// Maximum message length.
    pub const MAX_TEXT_LENGTH: usize = 2000;

    /// Support room of a PC: `pc:<pcId>`.
    pub fn for_pc(pc_id: Uuid) -> String {
        format!("pc:{pc_id}")
    }

    /// Zone room: `zone:<zone>`.
    pub fn for_zone(zone: &str) -> String {
        format!("zone:{zone}")
    }

    /// Direct-message room: `dm:<a>:<b>` with ids sorted by `System.Guid.CompareTo` order.
    pub fn for_direct(a: Uuid, b: Uuid) -> String {
        let (first, second) = if guid_key(a) <= guid_key(b) { (a, b) } else { (b, a) };
        format!("dm:{first}:{second}")
    }

    /// `System.Guid.CompareTo` ordering: `_a` as i32, `_b`/`_c` as i16, remaining bytes unsigned.
    fn guid_key(u: Uuid) -> (i32, i16, i16, [u8; 8]) {
        let b = u.as_bytes();
        let mut tail = [0u8; 8];
        tail.copy_from_slice(&b[8..16]);
        (
            i32::from_be_bytes([b[0], b[1], b[2], b[3]]),
            i16::from_be_bytes([b[4], b[5]]),
            i16::from_be_bytes([b[6], b[7]]),
            tail,
        )
    }
}
// ---- END MANUAL ----

/// Chat message (IPC_PROTOCOL.md §6.14).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ChatMessage {
    pub id: Uuid,
    /// Room ([`chat_rooms`]).
    pub room_id: String,
    pub sender_id: Uuid,
    pub sender_name: String,
    pub sender_role: UserRole,
    /// Text, ≤ 2000 chars.
    pub text: String,
    #[serde(with = "crate::wire::ts")]
    pub created_at: DateTime<Utc>,
    /// When the current user read it.
    #[serde(default, with = "crate::wire::ts_opt", skip_serializing_if = "Option::is_none")]
    pub read_at: Option<DateTime<Utc>>,
    pub kind: ChatMessageKind,
}

/// Body of `POST /chat/{roomId}/messages`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ChatPostRequest {
    pub text: String,
}

/// Body of `POST /chat/{roomId}/read`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ChatReadRequest {
    pub up_to_message_id: Uuid,
}

// ───────────────────────────── Booking ─────────────────────────────

wire_enum! {
    /// Booking lifecycle.
    BookingStatus {
        /// Reserved, awaiting confirmation/deposit.
        Reserved = "reserved",
        Confirmed = "confirmed",
        Cancelled = "cancelled",
        /// Expired unused.
        Expired = "expired",
    }
}

/// Seat on the club map (IPC_PROTOCOL.md §6.15).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Seat {
    pub pc_id: Uuid,
    pub name: String,
    pub zone: String,
    /// Grid column.
    pub x: i32,
    /// Grid row.
    pub y: i32,
    pub status: PcStatus,
}

/// Seat reservation (IPC_PROTOCOL.md §6.15). In `booking.seats` lists, `user_id` is the caller's
/// id or [`Uuid::nil`] for other users.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Booking {
    pub id: Uuid,
    pub user_id: Uuid,
    pub pc_id: Uuid,
    #[serde(with = "crate::wire::ts")]
    pub from: DateTime<Utc>,
    #[serde(with = "crate::wire::ts")]
    pub to: DateTime<Utc>,
    pub status: BookingStatus,
}

/// Body of `POST /booking/reserve`. Sent with an `Idempotency-Key`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct BookingCreateRequest {
    pub user_id: Uuid,
    pub pc_id: Uuid,
    #[serde(with = "crate::wire::ts")]
    pub from: DateTime<Utc>,
    #[serde(with = "crate::wire::ts")]
    pub to: DateTime<Utc>,
}

// ───────────────────────────── Tournaments ─────────────────────────────

wire_enum! {
    /// Tournament lifecycle.
    TournamentState {
        /// Announced; registration not open.
        Upcoming = "upcoming",
        Registration = "registration",
        Live = "live",
        Finished = "finished",
    }
}

/// Bracket match; `a`, `b`, `winner` are user ids.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct BracketMatch {
    pub id: Uuid,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub a: Option<Uuid>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub b: Option<Uuid>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub winner: Option<Uuid>,
    /// Score, e.g. `2-1`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub score: Option<String>,
}

/// Bracket round.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct BracketRound {
    pub matches: Vec<BracketMatch>,
}

/// Tournament bracket.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Bracket {
    pub rounds: Vec<BracketRound>,
}

/// Tournament (IPC_PROTOCOL.md §6.16).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Tournament {
    pub id: Uuid,
    pub title: String,
    pub game_id: Uuid,
    #[serde(with = "crate::wire::ts")]
    pub starts_at: DateTime<Utc>,
    pub state: TournamentState,
    pub prize_pool: Money,
    pub max_players: i32,
    /// Current player count.
    pub players: i32,
    /// Whether the current user has joined.
    pub joined: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub bracket: Option<Bracket>,
}

/// Leaderboard row (IPC_PROTOCOL.md §6.16).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct LeaderboardEntry {
    /// 1-based rank.
    pub rank: i32,
    pub user_id: Uuid,
    pub name: String,
    pub score: i64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub avatar_url: Option<String>,
}

// ───────────────────────────── Auth ─────────────────────────────

wire_enum! {
    /// Authentication method.
    AuthKind {
        /// Username + password.
        Password = "password",
        /// QR code scanned with the club's mobile app.
        Qr = "qr",
        /// Transient guest account.
        Guest = "guest",
        /// NFC/RFID card.
        Card = "card",
        /// One-time admin/web token.
        Token = "token",
    }
}

wire_enum! {
    /// State of a QR login handshake.
    QrStatus {
        Pending = "pending",
        Scanned = "scanned",
        /// Confirmed; [`QrLoginStatus::auth`] is set (single read).
        Confirmed = "confirmed",
        /// Expired or already consumed.
        Expired = "expired",
    }
}

wire_enum! {
    /// Why the user context was invalidated (`auth.expired` event).
    AuthExpiredReason {
        TokenExpired = "tokenExpired",
        /// Server revoked the user (`userRevoked` push).
        Revoked = "revoked",
        /// Admin forced logout.
        Admin = "admin",
    }
}

/// Server-facing login request (`POST /auth/login`, IPC_PROTOCOL.md §6.20). The IPC `auth.login`
/// payload is `AuthLoginRequest` (same fields without `pc_id`/`hwid`). `password` must never be logged.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct AuthRequest {
    pub kind: AuthKind,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub username: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub password: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub qr_token: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub card_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub token: Option<String>,
    pub pc_id: Uuid,
    /// Hardware id of the PC (sha256 hex).
    pub hwid: String,
}

// ---- BEGIN MANUAL ----
impl AuthRequest {
    /// Copy with secrets blanked, for logging/telemetry.
    pub fn redacted(&self) -> AuthRequest {
        let mask = |v: &Option<String>| v.as_ref().map(|_| "***".to_owned());
        AuthRequest {
            kind: self.kind,
            username: self.username.clone(),
            password: mask(&self.password),
            qr_token: mask(&self.qr_token),
            card_id: mask(&self.card_id),
            token: mask(&self.token),
            pc_id: self.pc_id,
            hwid: self.hwid.clone(),
        }
    }
}
// ---- END MANUAL ----

/// Result of a successful login (IPC_PROTOCOL.md §6.20, SERVER_API.md §4.3).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct AuthResponse {
    pub user: User,
    /// Open session already bound to this user on this PC (e.g. after an Agent restart).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub session: Option<Session>,
    /// User access token (`X-User-Token`); Agent-held, never sent to the Shell.
    pub access_token: String,
    pub refresh_token: String,
    #[serde(with = "crate::wire::ts")]
    pub expires_at: DateTime<Utc>,
    /// Argon2id PHC string for offline password verification, when the club allows offline login.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub offline_hash: Option<String>,
}

/// Body of `POST /auth/qr/start`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct QrStartRequest {
    pub pc_id: Uuid,
}

/// Response of `auth.qrStart` and `POST /auth/qr/start`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct QrLoginStart {
    /// Opaque token to poll with.
    pub qr_token: String,
    /// Deep link to render as a QR code (`https://<server>/q/<qrToken>`).
    pub qr_url: String,
    #[serde(with = "crate::wire::ts")]
    pub expires_at: DateTime<Utc>,
    pub poll_interval_sec: i32,
}

/// Response of `GET /auth/qr/{token}`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct QrLoginStatus {
    pub status: QrStatus,
    /// Set only when `status` is `confirmed` (single read).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub auth: Option<AuthResponse>,
}

/// Body of `POST /auth/guest`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct GuestAuthRequest {
    pub pc_id: Uuid,
    pub hwid: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub display_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub locale: Option<Locale>,
}

/// Body of `POST /auth/logout`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct LogoutRequest {
    pub reason: SessionEndReason,
}

// ---- BEGIN MANUAL ----
#[cfg(test)]
pub(crate) fn sample_user() -> User {
    use chrono::TimeZone;
    User {
        id: Uuid::parse_str("3f9a1d2e-6c4b-4f5a-9a1b-2c3d4e5f6a7b").unwrap(),
        username: "player1".into(),
        display_name: "Player One".into(),
        avatar_url: Some("https://cdn.example.uz/a/1.png".into()),
        role: UserRole::Member,
        balance: Money::uzs(1_500_000),
        loyalty_level: 2,
        loyalty_points: 340,
        created_at: Utc.with_ymd_and_hms(2025, 1, 10, 9, 0, 0).unwrap(),
        last_seen_at: Some(Utc.with_ymd_and_hms(2026, 9, 21, 10, 0, 0).unwrap()),
        locale: Locale::Ru,
        flags: vec![],
    }
}

#[cfg(test)]
pub(crate) const SAMPLE_USER_JSON: &str = r#"{"id":"3f9a1d2e-6c4b-4f5a-9a1b-2c3d4e5f6a7b","username":"player1","displayName":"Player One","avatarUrl":"https://cdn.example.uz/a/1.png","role":"member","balance":{"amount":1500000,"currency":"UZS"},"loyaltyLevel":2,"loyaltyPoints":340,"createdAt":"2025-01-10T09:00:00.000Z","lastSeenAt":"2026-09-21T10:00:00.000Z","locale":"ru","flags":[]}"#;

#[cfg(test)]
mod tests {
    use super::*;
    use crate::error::assert_wire;
    use chrono::TimeZone;

    #[test]
    fn enum_wire_values() {
        assert_wire(UserRole::ALL, &["guest", "member", "vip", "admin"]);
        assert_wire(Locale::ALL, &["en", "ru", "uz"]);
        assert_wire(NotificationLevel::ALL, &["info", "warning", "error", "success"]);
        assert_wire(ChatMessageKind::ALL, &["text", "system", "admin"]);
        assert_wire(BookingStatus::ALL, &["reserved", "confirmed", "cancelled", "expired"]);
        assert_wire(TournamentState::ALL, &["upcoming", "registration", "live", "finished"]);
        assert_wire(AuthKind::ALL, &["password", "qr", "guest", "card", "token"]);
        assert_wire(QrStatus::ALL, &["pending", "scanned", "confirmed", "expired"]);
        assert_wire(AuthExpiredReason::ALL, &["tokenExpired", "revoked", "admin"]);
    }

    #[test]
    fn user_json_matches_ipc_protocol_example() {
        let u = sample_user();
        assert_eq!(serde_json::to_string(&u).unwrap(), SAMPLE_USER_JSON);
        assert_eq!(serde_json::from_str::<User>(SAMPLE_USER_JSON).unwrap(), u);
        assert!(u.is_member());
        assert!(!u.has_flag(user_flags::BANNED));
        assert!(!User { role: UserRole::Guest, ..u }.is_member());
    }

    #[test]
    fn auth_request_redacts_secrets() {
        let r = AuthRequest {
            kind: AuthKind::Password,
            username: Some("player1".into()),
            password: Some("hunter2".into()),
            qr_token: None,
            card_id: None,
            token: None,
            pc_id: Uuid::nil(),
            hwid: "ab".into(),
        };
        assert_eq!(
            serde_json::to_string(&r).unwrap(),
            r#"{"kind":"password","username":"player1","password":"hunter2","pcId":"00000000-0000-0000-0000-000000000000","hwid":"ab"}"#
        );
        let red = r.redacted();
        assert_eq!(red.password.as_deref(), Some("***"));
        assert_eq!(red.username.as_deref(), Some("player1"));
        assert_eq!(red.qr_token, None);
    }

    #[test]
    fn notification_and_chat_json() {
        let n = Notification {
            id: Uuid::nil(),
            title: "t".into(),
            body: "b".into(),
            level: NotificationLevel::Success,
            ttl_sec: Some(5),
            action: Some(NotificationAction { label: "Shop".into(), command: "/shop".into(), args: None }),
        };
        assert_eq!(
            serde_json::to_string(&n).unwrap(),
            r#"{"id":"00000000-0000-0000-0000-000000000000","title":"t","body":"b","level":"success","ttlSec":5,"action":{"label":"Shop","command":"/shop"}}"#
        );
        let m = ChatMessage {
            id: Uuid::nil(),
            room_id: chat_rooms::for_pc(Uuid::nil()),
            sender_id: Uuid::nil(),
            sender_name: "Admin".into(),
            sender_role: UserRole::Admin,
            text: "hi".into(),
            created_at: Utc.with_ymd_and_hms(2026, 9, 21, 10, 0, 0).unwrap(),
            read_at: None,
            kind: ChatMessageKind::Admin,
        };
        let json = serde_json::to_string(&m).unwrap();
        assert_eq!(
            json,
            r#"{"id":"00000000-0000-0000-0000-000000000000","roomId":"pc:00000000-0000-0000-0000-000000000000","senderId":"00000000-0000-0000-0000-000000000000","senderName":"Admin","senderRole":"admin","text":"hi","createdAt":"2026-09-21T10:00:00.000Z","kind":"admin"}"#
        );
        assert_eq!(serde_json::from_str::<ChatMessage>(&json).unwrap(), m);
        let a = Uuid::parse_str("00000000-0000-0000-0000-000000000002").unwrap();
        let b = Uuid::parse_str("00000000-0000-0000-0000-000000000001").unwrap();
        assert_eq!(chat_rooms::for_direct(a, b), format!("dm:{b}:{a}"));
        assert_eq!(chat_rooms::for_zone("VIP"), "zone:VIP");
    }

    #[test]
    fn tournament_and_stats_json() {
        let t = Tournament {
            id: Uuid::nil(),
            title: "Cup".into(),
            game_id: Uuid::nil(),
            starts_at: Utc.with_ymd_and_hms(2026, 10, 1, 18, 0, 0).unwrap(),
            state: TournamentState::Registration,
            prize_pool: Money::uzs(10_000_000),
            max_players: 16,
            players: 3,
            joined: false,
            bracket: Some(Bracket {
                rounds: vec![BracketRound { matches: vec![BracketMatch { id: Uuid::nil(), a: None, b: None, winner: None, score: Some("2-1".into()) }] }],
            }),
        };
        let json = serde_json::to_string(&t).unwrap();
        assert!(json.ends_with(r#""joined":false,"bracket":{"rounds":[{"matches":[{"id":"00000000-0000-0000-0000-000000000000","score":"2-1"}]}]}}"#));
        assert_eq!(serde_json::from_str::<Tournament>(&json).unwrap(), t);

        let s = UserStats {
            total_hours: 12.5,
            sessions_count: 4,
            favorite_games: vec![FavoriteGame { game_id: Uuid::nil(), hours: 3.0 }],
            spent: Money::uzs(1),
            rank: 7,
        };
        assert_eq!(
            serde_json::to_string(&s).unwrap(),
            r#"{"totalHours":12.5,"sessionsCount":4,"favoriteGames":[{"gameId":"00000000-0000-0000-0000-000000000000","hours":3}],"spent":{"amount":1,"currency":"UZS"},"rank":7}"#
        );
        let seat = Seat { pc_id: Uuid::nil(), name: "PC-1".into(), zone: "VIP".into(), x: 1, y: 2, status: PcStatus::Free };
        assert_eq!(
            serde_json::to_string(&seat).unwrap(),
            r#"{"pcId":"00000000-0000-0000-0000-000000000000","name":"PC-1","zone":"VIP","x":1,"y":2,"status":"free"}"#
        );
    }
}
// ---- END MANUAL ----
