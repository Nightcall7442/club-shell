//! Mirror of `ClubShell.Contracts.Ipc.IpcMessages` + request/response payloads (IpcMessage.cs) and
//! `ClubShell.Contracts.Commands` (AgentCommand.cs, ServerCommand.cs): IPC message names, every
//! Shell → Agent payload, the [`AgentCommand`] enum, server → agent commands, agent → server events,
//! WebSocket frames and the agent REST surface.

use chrono::{DateTime, NaiveDate, NaiveTime, Utc};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use uuid::Uuid;

use crate::error::IpcError;
use crate::games::{App, Game, GamesSort, LauncherType, Resolution, RunningGame};
use crate::pc::{
    AgentServerConfig, ConnectivityState, HardwareInfo, Pc, PcMetrics, PcStatus, Policy,
};
use crate::session::{Session, SessionEndReason, SessionState};
use crate::shop::{Order, OrderLineRequest, Product, ProductCategory};
use crate::user::{
    Achievement, AuthKind, Booking, ChatMessage, LeaderboardEntry, Locale, NotificationLevel, Seat,
    Tournament, TournamentState, User,
};
use crate::wallet::{Money, Tariff, TopupProvider, Transaction, TransactionType};

// ---- BEGIN MANUAL ----
use std::fmt;

use serde::de::DeserializeOwned;
use serde::{Deserializer, Serializer};

use crate::error::{ProtocolError, UnknownWireName};
use crate::user::AuthRequest;

pub use crate::events::{AdItem, ShowAdsArgs};
pub use crate::pc::{
    AllowlistMode, AntiCheatPolicy, ExplorerPolicy, KioskPolicy, PowerPolicy, ProcessAllowlistPolicy,
    ShellConfigOverride, ShellReplacementPolicy, ThemeRef, TimeWindow, UpdatesConfigOverride, UpdatesPolicy, UsbPolicy,
    WebFilterPolicy,
};
// ---- END MANUAL ----

// ───────────────────────────── Message names ─────────────────────────────

/// Every IPC message name (IPC_PROTOCOL.md §7–8). Requests are Shell → Agent; [`names::events`] are
/// Agent → Shell and are re-emitted by the Shell as Tauri events `agent://<name>`.
pub mod names {
    pub mod auth {
        pub const HELLO: &str = "auth.hello";
        pub const LOGIN: &str = "auth.login";
        pub const LOGOUT: &str = "auth.logout";
        pub const STATUS: &str = "auth.status";
        pub const QR_START: &str = "auth.qrStart";
    }

    pub mod session {
        pub const GET: &str = "session.get";
        pub const START: &str = "session.start";
        pub const PAUSE: &str = "session.pause";
        pub const RESUME: &str = "session.resume";
        pub const END: &str = "session.end";
        pub const EXTEND: &str = "session.extend";
        pub const LOCK: &str = "session.lock";
        pub const UNLOCK: &str = "session.unlock";
        pub const TIME_LEFT: &str = "session.timeLeft";
    }

    pub mod games {
        pub const LIST: &str = "games.list";
        pub const GET: &str = "games.get";
        pub const LAUNCH: &str = "games.launch";
        pub const KILL: &str = "games.kill";
        pub const RUNNING: &str = "games.running";
        pub const INSTALL_STATUS: &str = "games.installStatus";
    }

    pub mod apps {
        pub const LIST: &str = "apps.list";
        pub const LAUNCH: &str = "apps.launch";
    }

    pub mod wallet {
        pub const BALANCE: &str = "wallet.balance";
        pub const TARIFFS: &str = "wallet.tariffs";
        pub const HISTORY: &str = "wallet.history";
        pub const TOPUP_INTENT: &str = "wallet.topupIntent";
    }

    pub mod shop {
        pub const PRODUCTS: &str = "shop.products";
        pub const ORDER: &str = "shop.order";
        pub const ORDER_STATUS: &str = "shop.orderStatus";
        pub const ORDERS: &str = "shop.orders";
    }

    pub mod chat {
        pub const HISTORY: &str = "chat.history";
        pub const SEND: &str = "chat.send";
        pub const MARK_READ: &str = "chat.markRead";
    }

    pub mod booking {
        pub const SEATS: &str = "booking.seats";
        pub const RESERVE: &str = "booking.reserve";
        pub const CANCEL: &str = "booking.cancel";
    }

    pub mod tournaments {
        pub const LIST: &str = "tournaments.list";
        pub const JOIN: &str = "tournaments.join";
        pub const LEADERBOARD: &str = "tournaments.leaderboard";
    }

    pub mod profile {
        pub const GET: &str = "profile.get";
        pub const UPDATE: &str = "profile.update";
        pub const STATS: &str = "profile.stats";
        pub const ACHIEVEMENTS: &str = "profile.achievements";
        pub const LOYALTY: &str = "profile.loyalty";
    }

    pub mod settings {
        pub const GET: &str = "settings.get";
        pub const SET: &str = "settings.set";
    }

    pub mod sys {
        pub const PING: &str = "sys.ping";
        /// Response name of [`PING`].
        pub const PONG: &str = "sys.pong";
        pub const PC_INFO: &str = "sys.pcInfo";
        pub const HARDWARE: &str = "sys.hardware";
        pub const METRICS: &str = "sys.metrics";
        pub const CALL_ADMIN: &str = "sys.callAdmin";
        pub const REBOOT: &str = "sys.reboot";
        pub const SHUTDOWN: &str = "sys.shutdown";
        pub const LOCK_SCREEN: &str = "sys.lockScreen";
        pub const SET_VOLUME: &str = "sys.setVolume";
        pub const SET_LOCALE: &str = "sys.setLocale";
        pub const UNLOCK_ADMIN: &str = "sys.unlockAdmin";
        pub const LOG_CLIENT_ERROR: &str = "sys.logClientError";
        pub const ACK_ADMIN_MESSAGE: &str = "sys.ackAdminMessage";
    }

    pub mod policy {
        pub const GET: &str = "policy.get";
        pub const RELOAD: &str = "policy.reload";
    }

    pub mod update {
        pub const CHECK: &str = "update.check";
        pub const APPLY: &str = "update.apply";
    }

    /// Agent → Shell events (IPC_PROTOCOL.md §8).
    pub mod events {
        pub const SESSION_UPDATED: &str = "session.updated";
        pub const SESSION_WARNING: &str = "session.warning";
        pub const SESSION_ENDED: &str = "session.ended";
        pub const WALLET_UPDATED: &str = "wallet.updated";
        pub const CHAT_MESSAGE: &str = "chat.message";
        pub const NOTIFICATION_PUSH: &str = "notification.push";
        pub const ADMIN_MESSAGE: &str = "admin.message";
        pub const ADMIN_REMOTE_CONTROL: &str = "admin.remoteControl";
        pub const GAME_STATE_CHANGED: &str = "game.stateChanged";
        pub const POLICY_CHANGED: &str = "policy.changed";
        pub const UPDATE_AVAILABLE: &str = "update.available";
        pub const UPDATE_PROGRESS: &str = "update.progress";
        pub const UPDATE_READY: &str = "update.ready";
        pub const SYS_METRICS: &str = "sys.metrics";
        pub const SYS_CONNECTIVITY: &str = "sys.connectivity";
        pub const SHELL_COMMAND: &str = "shell.command";
        pub const AUTH_EXPIRED: &str = "auth.expired";
        pub const SHOP_ORDER_UPDATED: &str = "shop.orderUpdated";

        /// All event names.
        pub const ALL: &[&str] = &[
            SESSION_UPDATED, SESSION_WARNING, SESSION_ENDED, WALLET_UPDATED, CHAT_MESSAGE, NOTIFICATION_PUSH,
            ADMIN_MESSAGE, ADMIN_REMOTE_CONTROL, GAME_STATE_CHANGED, POLICY_CHANGED, UPDATE_AVAILABLE,
            UPDATE_PROGRESS, UPDATE_READY, SYS_METRICS, SYS_CONNECTIVITY, SHELL_COMMAND, AUTH_EXPIRED,
            SHOP_ORDER_UPDATED,
        ];
    }
}

// ───────────────────────────── AgentCommand ─────────────────────────────

wire_enum! {
    /// Authentication level an IPC request requires (IPC_PROTOCOL.md §7 "auth" column).
    IpcAuthLevel {
        /// No handshake required (`auth.hello`, `sys.ping`).
        None = "none",
        /// Successful `auth.hello` on the connection.
        Hello = "hello",
        /// A logged-in user.
        User = "user",
        /// An active or paused session.
        Session = "session",
    }
}

// ---- BEGIN MANUAL ----
macro_rules! agent_commands {
    ( $( $(#[$meta:meta])* $variant:ident => $name:path, $auth:ident; )+ ) => {
        /// Every Shell → Agent IPC request (IPC_PROTOCOL.md §7). Wire form is the IPC name
        /// (`"session.start"`), see [`AgentCommand::name`] / `TryFrom<&str>`.
        #[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
        pub enum AgentCommand {
            $( $(#[$meta])* $variant, )+
        }

        impl AgentCommand {
            /// All commands in declaration order.
            pub const ALL: &'static [AgentCommand] = &[ $( AgentCommand::$variant, )+ ];

            /// IPC request name, e.g. `session.start`.
            pub const fn name(self) -> &'static str {
                match self { $( AgentCommand::$variant => $name, )+ }
            }

            /// Authentication level the command requires.
            pub const fn required_auth(self) -> IpcAuthLevel {
                match self { $( AgentCommand::$variant => IpcAuthLevel::$auth, )+ }
            }
        }
    };
}

agent_commands! {
    AuthHello => names::auth::HELLO, None;
    AuthLogin => names::auth::LOGIN, Hello;
    AuthLogout => names::auth::LOGOUT, User;
    AuthStatus => names::auth::STATUS, Hello;
    AuthQrStart => names::auth::QR_START, Hello;
    SessionGet => names::session::GET, User;
    SessionStart => names::session::START, User;
    SessionPause => names::session::PAUSE, Session;
    SessionResume => names::session::RESUME, Session;
    SessionEnd => names::session::END, Session;
    SessionExtend => names::session::EXTEND, Session;
    SessionLock => names::session::LOCK, Session;
    SessionUnlock => names::session::UNLOCK, Session;
    SessionTimeLeft => names::session::TIME_LEFT, User;
    GamesList => names::games::LIST, Hello;
    GamesGet => names::games::GET, Hello;
    GamesLaunch => names::games::LAUNCH, Session;
    GamesKill => names::games::KILL, Session;
    GamesRunning => names::games::RUNNING, Hello;
    GamesInstallStatus => names::games::INSTALL_STATUS, Hello;
    AppsList => names::apps::LIST, Hello;
    AppsLaunch => names::apps::LAUNCH, Session;
    WalletBalance => names::wallet::BALANCE, User;
    WalletTariffs => names::wallet::TARIFFS, Hello;
    WalletHistory => names::wallet::HISTORY, User;
    WalletTopupIntent => names::wallet::TOPUP_INTENT, User;
    ShopProducts => names::shop::PRODUCTS, Hello;
    ShopOrder => names::shop::ORDER, Session;
    ShopOrderStatus => names::shop::ORDER_STATUS, User;
    ShopOrders => names::shop::ORDERS, User;
    ChatHistory => names::chat::HISTORY, User;
    ChatSend => names::chat::SEND, User;
    ChatMarkRead => names::chat::MARK_READ, User;
    BookingSeats => names::booking::SEATS, Hello;
    BookingReserve => names::booking::RESERVE, User;
    BookingCancel => names::booking::CANCEL, User;
    TournamentsList => names::tournaments::LIST, Hello;
    TournamentsJoin => names::tournaments::JOIN, User;
    TournamentsLeaderboard => names::tournaments::LEADERBOARD, Hello;
    ProfileGet => names::profile::GET, User;
    ProfileUpdate => names::profile::UPDATE, User;
    ProfileStats => names::profile::STATS, User;
    ProfileAchievements => names::profile::ACHIEVEMENTS, User;
    ProfileLoyalty => names::profile::LOYALTY, User;
    SettingsGet => names::settings::GET, Hello;
    SettingsSet => names::settings::SET, Hello;
    /// Answered as `sys.pong`.
    SysPing => names::sys::PING, None;
    SysPcInfo => names::sys::PC_INFO, Hello;
    SysHardware => names::sys::HARDWARE, Hello;
    SysMetrics => names::sys::METRICS, Hello;
    SysCallAdmin => names::sys::CALL_ADMIN, Hello;
    SysReboot => names::sys::REBOOT, Hello;
    SysShutdown => names::sys::SHUTDOWN, Hello;
    SysLockScreen => names::sys::LOCK_SCREEN, Hello;
    SysSetVolume => names::sys::SET_VOLUME, Hello;
    SysSetLocale => names::sys::SET_LOCALE, Hello;
    SysUnlockAdmin => names::sys::UNLOCK_ADMIN, Hello;
    SysLogClientError => names::sys::LOG_CLIENT_ERROR, Hello;
    SysAckAdminMessage => names::sys::ACK_ADMIN_MESSAGE, Hello;
    PolicyGet => names::policy::GET, Hello;
    PolicyReload => names::policy::RELOAD, Hello;
    UpdateCheck => names::update::CHECK, Hello;
    UpdateApply => names::update::APPLY, Hello;
}

impl AgentCommand {
    /// Name the response carries (equal to the request name except `sys.ping` → `sys.pong`).
    pub const fn response_name(self) -> &'static str {
        match self {
            AgentCommand::SysPing => names::sys::PONG,
            other => other.name(),
        }
    }

    /// Parses an IPC request name (exact match).
    pub fn parse(name: &str) -> Option<AgentCommand> {
        Self::ALL.iter().copied().find(|c| c.name() == name)
    }

    /// `true` when `name` is a known request name.
    pub fn is_known(name: &str) -> bool {
        Self::parse(name).is_some()
    }
}

impl TryFrom<&str> for AgentCommand {
    type Error = UnknownWireName;

    fn try_from(name: &str) -> Result<Self, Self::Error> {
        Self::parse(name).ok_or_else(|| UnknownWireName { kind: "AgentCommand", got: name.to_owned() })
    }
}

impl std::str::FromStr for AgentCommand {
    type Err = UnknownWireName;

    fn from_str(s: &str) -> Result<Self, Self::Err> {
        Self::try_from(s)
    }
}

impl fmt::Display for AgentCommand {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.name())
    }
}

impl Serialize for AgentCommand {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_str(self.name())
    }
}

impl<'de> Deserialize<'de> for AgentCommand {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let name = String::deserialize(deserializer)?;
        AgentCommand::try_from(name.as_str()).map_err(serde::de::Error::custom)
    }
}
// ---- END MANUAL ----

// ───────────────────────────── Common ─────────────────────────────

/// Standard page envelope `{ items, total, page, pageSize }` (SERVER_API.md §1).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct PagedResult<T> {
    pub items: Vec<T>,
    /// Total items across all pages.
    pub total: i32,
    /// 1-based page number.
    pub page: i32,
    pub page_size: i32,
}

// ---- BEGIN MANUAL ----
impl<T> PagedResult<T> {
    /// `true` when more pages follow.
    pub fn has_more(&self) -> bool {
        i64::from(self.page) * i64::from(self.page_size) < i64::from(self.total)
    }
}
// ---- END MANUAL ----

/// Trivial `{ ok: true }` response.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct OkResponse {
    pub ok: bool,
}

// ---- BEGIN MANUAL ----
impl OkResponse {
    /// `{ ok: true }`.
    pub const OK: OkResponse = OkResponse { ok: true };
}

impl Default for OkResponse {
    fn default() -> Self {
        Self::OK
    }
}
// ---- END MANUAL ----

/// Shell capabilities advertised in [`AuthHelloRequest::capabilities`].
pub mod shell_capabilities {
    pub const GAMEPAD: &str = "gamepad";
    pub const VIRTUAL_KEYBOARD: &str = "virtualKeyboard";
    pub const MULTI_MONITOR: &str = "multiMonitor";
    pub const OVERLAY: &str = "overlay";
}

/// Agent capabilities advertised in [`AuthHelloResponse::capabilities`]; the Shell hides features
/// whose capability is absent.
pub mod agent_capabilities {
    pub const ACCOUNT_POOL: &str = "accountPool";
    pub const CLOUD_SAVE: &str = "cloudSave";
    pub const REMOTE_CONTROL: &str = "remoteControl";
    pub const SCREEN_CAPTURE: &str = "screenCapture";
    pub const VIRTUAL_KEYBOARD: &str = "virtualKeyboard";
    pub const WOL: &str = "wol";
    pub const OFFLINE: &str = "offline";
    pub const SHOP: &str = "shop";
    pub const CHAT: &str = "chat";
    pub const BOOKING: &str = "booking";
    pub const TOURNAMENTS: &str = "tournaments";
}

// ───────────────────────────── Auth payloads ─────────────────────────────

/// Request of `auth.hello`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct AuthHelloRequest {
    /// 64 hex chars from `secure\shell.token`; compared in constant time.
    pub shell_token: String,
    pub shell_version: String,
    /// Shell process id.
    pub pid: i32,
    /// Windows session id.
    pub wts_session_id: i32,
    pub locale: Locale,
    /// [`shell_capabilities`].
    pub capabilities: Vec<String>,
}

/// Response of `auth.hello`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct AuthHelloResponse {
    pub agent_version: String,
    /// IPC protocol major.
    pub protocol: i32,
    pub pc_id: Uuid,
    pub pc_name: String,
    pub zone: String,
    pub server_online: bool,
    pub policy_version: i32,
    /// Agent's best estimate of server time.
    #[serde(with = "crate::wire::ts")]
    pub server_time: DateTime<Utc>,
    /// [`agent_capabilities`].
    pub capabilities: Vec<String>,
    /// Kiosk Windows account name.
    pub kiosk_user: String,
}

/// Request of `auth.login` (subset of [`AuthRequest`] without PC identity). Secrets are never logged.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct AuthLoginRequest {
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
}

// ---- BEGIN MANUAL ----
impl AuthLoginRequest {
    /// Builds the server-facing [`AuthRequest`].
    pub fn to_server_request(&self, pc_id: Uuid, hwid: &str) -> AuthRequest {
        AuthRequest {
            kind: self.kind,
            username: self.username.clone(),
            password: self.password.clone(),
            qr_token: self.qr_token.clone(),
            card_id: self.card_id.clone(),
            token: self.token.clone(),
            pc_id,
            hwid: hwid.to_owned(),
        }
    }
}
// ---- END MANUAL ----

/// Response of `auth.login`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct AuthLoginResponse {
    pub user: User,
    /// Existing session bound to this user on this PC, when any.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub session: Option<Session>,
    /// User access token expiry (token is Agent-held).
    #[serde(with = "crate::wire::ts")]
    pub expires_at: DateTime<Utc>,
    /// `offline` when validated from cache.
    pub mode: ConnectivityState,
}

/// Request of `auth.logout`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct AuthLogoutRequest {
    /// Defaults to `user`; only `user`, `idle`, `admin` are valid from the Shell.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<SessionEndReason>,
}

/// Response of `auth.logout`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct AuthLogoutResponse {
    pub ok: bool,
    /// Whether an active session was ended first.
    pub session_ended: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub session: Option<Session>,
}

/// Response of `auth.status`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct AuthStatusResponse {
    pub authenticated: bool,
    pub mode: ConnectivityState,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub user: Option<User>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub session: Option<Session>,
    #[serde(default, with = "crate::wire::ts_opt", skip_serializing_if = "Option::is_none")]
    pub expires_at: Option<DateTime<Utc>>,
}

// ───────────────────────────── Session payloads ─────────────────────────────

/// Request of `session.start`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SessionStartRequest {
    pub tariff_id: Uuid,
    /// `true` = charge now for `minutes`; `false` = postpaid open-ended.
    pub prepaid: bool,
    /// Required unless the tariff is a package; within `minMinutes..maxMinutes`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub minutes: Option<i32>,
}

/// Request of `session.pause`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct SessionPauseRequest {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
}

/// Request of `session.end`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct SessionEndRequest {
    /// Defaults to `user`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<SessionEndReason>,
}

/// Request of `session.extend` and body of `POST /sessions/{id}/extend`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SessionExtendRequest {
    pub minutes: i32,
    /// Tariff to bill; defaults to the current one.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub tariff_id: Option<Uuid>,
}

/// Request of `session.lock`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct SessionLockRequest {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
}

/// Request of `session.unlock`; exactly one of the secrets is given.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct SessionUnlockRequest {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub password: Option<String>,
    /// 4–6 digit user PIN.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pin: Option<String>,
}

/// Response of `session.timeLeft`; used for drift correction every 30 s. Never errors when idle.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct SessionTimeLeftResponse {
    /// `idle` when none.
    pub state: SessionState,
    /// −1 open-ended, 0 when idle.
    pub seconds_left: i32,
    pub seconds_used: i32,
    #[serde(with = "crate::wire::ts")]
    pub server_time: DateTime<Utc>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub session_id: Option<Uuid>,
    #[serde(default, with = "crate::wire::ts_opt", skip_serializing_if = "Option::is_none")]
    pub ends_at: Option<DateTime<Utc>>,
}

// ───────────────────────────── Games / apps payloads ─────────────────────────────

/// Request of `games.list`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct GamesListRequest {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub category: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub search: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub installed_only: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub launcher: Option<LauncherType>,
    /// Default popularity.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sort: Option<GamesSort>,
    /// 1-based (default 1).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub page: Option<i32>,
    /// Default 100, max 500.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub page_size: Option<i32>,
}

impl GamesListRequest {
    pub const DEFAULT_PAGE_SIZE: i32 = 100;
    pub const MAX_PAGE_SIZE: i32 = 500;
}

/// Response of `games.list` (served from `cache\games.json` when offline).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct GamesListResponse {
    pub items: Vec<Game>,
    pub total: i32,
    pub page: i32,
    pub page_size: i32,
    /// Catalogue version (ETag-like).
    pub catalog_version: String,
}

/// Request of `games.get`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct GamesGetRequest {
    pub game_id: Uuid,
}

/// Request of `games.launch`; the Agent fills session/user/timeout to build a `LaunchRequest`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct GamesLaunchRequest {
    pub game_id: Uuid,
    /// Defaults to `Game.requiresAccount`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub use_account_pool: Option<bool>,
    /// Appended after policy sanitization.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub extra_args: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub resolution: Option<Resolution>,
}

/// Request of `games.kill`; at least one of `game_id`/`pid`, none = kill all.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct GamesKillRequest {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub game_id: Option<Uuid>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pid: Option<i32>,
    /// Terminate immediately.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub force: Option<bool>,
}

/// Response of `games.kill` and result of the `killGame` server command.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct GamesKillResponse {
    pub killed: i32,
    pub pids: Vec<i32>,
}

/// Response of `games.running`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct GamesRunningResponse {
    pub items: Vec<RunningGame>,
}

/// Request of `games.installStatus`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct GamesInstallStatusRequest {
    pub game_id: Uuid,
}

/// Response of `apps.list` and `GET /apps`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct AppsListResponse {
    pub items: Vec<App>,
}

/// Request of `apps.launch`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct AppsLaunchRequest {
    pub app_id: Uuid,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub args: Option<String>,
}

/// Response of `apps.launch`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct AppsLaunchResponse {
    pub ok: bool,
    pub pid: i32,
    #[serde(with = "crate::wire::ts")]
    pub started_at: DateTime<Utc>,
}

// ───────────────────────────── Wallet payloads ─────────────────────────────

/// Request of `wallet.tariffs`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct WalletTariffsRequest {
    /// Defaults to this PC's zone.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub zone: Option<String>,
}

/// Response of `wallet.tariffs` (cached offline).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct WalletTariffsResponse {
    pub items: Vec<Tariff>,
    pub zone: String,
    #[serde(with = "crate::wire::ts")]
    pub server_time: DateTime<Utc>,
}

/// Response of `GET /tariffs`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct TariffsResponse {
    pub items: Vec<Tariff>,
    #[serde(with = "crate::wire::ts")]
    pub server_time: DateTime<Utc>,
}

/// Request of `wallet.history` (and query of `GET /wallet/{userId}/transactions`).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct WalletHistoryRequest {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub page: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub page_size: Option<i32>,
    /// Inclusive lower bound.
    #[serde(default, with = "crate::wire::ts_opt", skip_serializing_if = "Option::is_none")]
    pub from: Option<DateTime<Utc>>,
    /// Exclusive upper bound.
    #[serde(default, with = "crate::wire::ts_opt", skip_serializing_if = "Option::is_none")]
    pub to: Option<DateTime<Utc>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub r#type: Option<TransactionType>,
}

/// Response of `wallet.history` (same shape as [`PagedResult`]`<Transaction>`).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct WalletHistoryResponse {
    pub items: Vec<Transaction>,
    /// Total transactions.
    pub total: i32,
    /// 1-based page number.
    pub page: i32,
    pub page_size: i32,
}

// ---- BEGIN MANUAL ----
impl WalletHistoryResponse {
    /// `true` when more pages follow.
    pub fn has_more(&self) -> bool {
        i64::from(self.page) * i64::from(self.page_size) < i64::from(self.total)
    }
}
// ---- END MANUAL ----

/// Request of `wallet.topupIntent`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct WalletTopupIntentRequest {
    /// ≥ 1 000 UZS; `cash` creates an admin ticket.
    pub amount: Money,
    pub provider: TopupProvider,
}

// ───────────────────────────── Shop payloads ─────────────────────────────

/// Request of `shop.products`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct ShopProductsRequest {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub category: Option<ProductCategory>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub search: Option<String>,
}

/// Response of `shop.products` and `GET /shop/products`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ShopProductsResponse {
    pub items: Vec<Product>,
}

/// Request of `shop.order`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ShopOrderRequest {
    /// 1–20 lines, qty 1–99.
    pub items: Vec<OrderLineRequest>,
    /// Client-generated key forwarded as `Idempotency-Key`.
    pub idempotency_key: Uuid,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub note: Option<String>,
}

/// Request of `shop.orderStatus`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ShopOrderStatusRequest {
    pub order_id: Uuid,
}

/// Request of `shop.orders`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct ShopOrdersRequest {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub page: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub page_size: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub active_only: Option<bool>,
}

/// Response of `shop.orders`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ShopOrdersResponse {
    pub items: Vec<Order>,
    pub total: i32,
}

// ───────────────────────────── Chat payloads ─────────────────────────────

/// Request of `chat.history`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct ChatHistoryRequest {
    /// Defaults to `pc:<pcId>`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub room_id: Option<String>,
    /// Return messages older than this message id.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub before: Option<Uuid>,
    /// Default 50, max 200.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub limit: Option<i32>,
}

impl ChatHistoryRequest {
    pub const DEFAULT_LIMIT: i32 = 50;
    pub const MAX_LIMIT: i32 = 200;
}

/// Response of `chat.history` and `GET /chat/{roomId}/messages`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ChatHistoryResponse {
    pub room_id: String,
    /// Oldest first.
    pub items: Vec<ChatMessage>,
    pub has_more: bool,
    pub unread: i32,
}

/// Request of `chat.send`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ChatSendRequest {
    /// 1–2000 chars.
    pub text: String,
    /// Client-generated key forwarded as `Idempotency-Key`.
    pub idempotency_key: Uuid,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub room_id: Option<String>,
}

/// Request of `chat.markRead`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ChatMarkReadRequest {
    pub up_to_message_id: Uuid,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub room_id: Option<String>,
}

/// Response of `chat.markRead` and `POST /chat/{roomId}/read`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ChatMarkReadResponse {
    pub room_id: String,
    pub unread: i32,
}

// ───────────────────────────── Booking payloads ─────────────────────────────

/// Request of `booking.seats`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct BookingSeatsRequest {
    /// Club-local date (`YYYY-MM-DD`).
    pub date: NaiveDate,
}

/// Response of `booking.seats` and `GET /booking/seats`. Other users' bookings are anonymized
/// (`userId` = nil UUID).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct BookingSeatsResponse {
    pub date: NaiveDate,
    pub seats: Vec<Seat>,
    pub bookings: Vec<Booking>,
    pub slot_minutes: i32,
    /// Club opening time (REST only).
    #[serde(default, with = "crate::wire::hm_opt", skip_serializing_if = "Option::is_none")]
    pub open_from: Option<NaiveTime>,
    /// Club closing time (REST only).
    #[serde(default, with = "crate::wire::hm_opt", skip_serializing_if = "Option::is_none")]
    pub open_to: Option<NaiveTime>,
}

/// Request of `booking.reserve`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct BookingReserveRequest {
    pub pc_id: Uuid,
    #[serde(with = "crate::wire::ts")]
    pub from: DateTime<Utc>,
    #[serde(with = "crate::wire::ts")]
    pub to: DateTime<Utc>,
}

/// Request of `booking.cancel`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct BookingCancelRequest {
    pub booking_id: Uuid,
}

// ───────────────────────────── Tournament payloads ─────────────────────────────

/// Request of `tournaments.list`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct TournamentsListRequest {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub state: Option<TournamentState>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub game_id: Option<Uuid>,
}

/// Response of `tournaments.list` and `GET /tournaments`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct TournamentsListResponse {
    pub items: Vec<Tournament>,
}

/// Request of `tournaments.join`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct TournamentsJoinRequest {
    pub tournament_id: Uuid,
}

/// Request of `tournaments.leaderboard`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct TournamentsLeaderboardRequest {
    pub tournament_id: Uuid,
    /// ≤ 100.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub limit: Option<i32>,
}

/// Response of `tournaments.leaderboard` and `GET /tournaments/{id}/leaderboard`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct TournamentsLeaderboardResponse {
    pub tournament_id: Uuid,
    pub entries: Vec<LeaderboardEntry>,
    #[serde(with = "crate::wire::ts")]
    pub updated_at: DateTime<Utc>,
    /// Current user's entry when outside the top list.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub me: Option<LeaderboardEntry>,
}

// ───────────────────────────── Profile / settings payloads ─────────────────────────────

/// Response of `profile.achievements` and `GET /users/{userId}/achievements`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ProfileAchievementsResponse {
    pub items: Vec<Achievement>,
}

/// Feature toggles exposed to the UI (`shell.json → features`).
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ShellFeatures {
    pub shop: bool,
    pub chat: bool,
    pub booking: bool,
    pub tournaments: bool,
    pub profile: bool,
    pub topup: bool,
    pub apps: bool,
    pub call_admin: bool,
}

/// Settings subset of `shell.json` exposed to the UI (IPC_PROTOCOL.md §6.21); persisted by the Agent.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ShellSettings {
    pub locale: Locale,
    pub theme: String,
    /// Installed themes (read-only).
    pub available_themes: Vec<String>,
    /// 0–100.
    pub volume: i32,
    pub muted: bool,
    pub idle_timeout_sec: i32,
    pub show_metrics_overlay: bool,
    pub allow_virtual_keyboard: bool,
    pub ui_sounds: bool,
    /// Read-only.
    pub features: ShellFeatures,
}

/// Request of `settings.set`; partial, at least one key.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct SettingsSetRequest {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub locale: Option<Locale>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub theme: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub volume: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub muted: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub idle_timeout_sec: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub show_metrics_overlay: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub allow_virtual_keyboard: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ui_sounds: Option<bool>,
}

// ---- BEGIN MANUAL ----
impl SettingsSetRequest {
    /// `true` when no field is set (request is invalid).
    pub fn is_empty(&self) -> bool {
        self == &SettingsSetRequest::default()
    }
}
// ---- END MANUAL ----

// ───────────────────────────── Sys payloads ─────────────────────────────

wire_enum! {
    /// Category of a `sys.callAdmin` ticket.
    CallAdminCategory {
        Help = "help",
        Technical = "technical",
        /// Shop order issue.
        Order = "order",
        Other = "other",
    }
}

wire_enum! {
    /// Severity of a client-side error forwarded via `sys.logClientError`.
    ClientErrorLevel {
        Warn = "warn",
        Error = "error",
    }
}

/// Request of `sys.ping`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SysPingRequest {
    /// Monotonic sequence number.
    pub seq: i64,
    #[serde(with = "crate::wire::ts")]
    pub sent_at: DateTime<Utc>,
}

/// Response (`sys.pong`) to `sys.ping`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SysPongResponse {
    pub seq: i64,
    #[serde(with = "crate::wire::ts")]
    pub sent_at: DateTime<Utc>,
    #[serde(with = "crate::wire::ts")]
    pub received_at: DateTime<Utc>,
    pub connectivity: ConnectivityState,
}

/// Request of `sys.hardware`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct SysHardwareRequest {
    /// Force a rescan instead of the cached inventory.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub refresh: Option<bool>,
}

/// Request of `sys.callAdmin` (rate limited 1 per 30 s).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SysCallAdminRequest {
    pub category: CallAdminCategory,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub message: Option<String>,
}

/// Response of `sys.callAdmin` and `POST /support/call-admin`. Offline: client-generated ticket id,
/// `queue_position` `None`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SysCallAdminResponse {
    pub ticket_id: Uuid,
    #[serde(with = "crate::wire::ts")]
    pub created_at: DateTime<Utc>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub queue_position: Option<i32>,
}

/// Request of `sys.reboot` / `sys.shutdown`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct SysPowerRequest {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub delay_sec: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
}

/// Request of `sys.lockScreen`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct SysLockScreenRequest {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
}

/// Request of `sys.setVolume` and payload of the `setVolume` server command.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SetVolumeRequest {
    /// 0–100.
    pub level: i32,
    /// Unchanged when `None`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub muted: Option<bool>,
}

/// Response of `sys.setVolume` and result of the `setVolume` server command.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct VolumeState {
    pub level: i32,
    pub muted: bool,
}

/// Request of `sys.setLocale`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SysSetLocaleRequest {
    pub locale: Locale,
}

/// Response of `sys.setLocale`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SysSetLocaleResponse {
    pub locale: Locale,
}

/// Request of `sys.unlockAdmin` (rate limited 3 attempts/min).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SysUnlockAdminRequest {
    pub pin: String,
}

/// Response of `sys.unlockAdmin`; grants the kiosk exit path for 5 minutes.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SysUnlockAdminResponse {
    pub ok: bool,
    /// Short-lived token passed to `kiosk_exit`.
    pub admin_token: String,
    #[serde(with = "crate::wire::ts")]
    pub expires_at: DateTime<Utc>,
}

/// Request of `sys.logClientError` (rate limited 10/s, silently dropped beyond).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SysLogClientErrorRequest {
    pub level: ClientErrorLevel,
    pub message: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub stack: Option<String>,
    /// Frontend route.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub route: Option<String>,
}

/// Request of `sys.ackAdminMessage`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SysAckAdminMessageRequest {
    /// Id of the acknowledged `AdminMessage`.
    pub id: Uuid,
}

// ───────────────────────────── Policy / update payloads ─────────────────────────────

wire_enum! {
    /// Where the policy returned by `policy.reload` came from.
    PolicySource {
        Server = "server",
        /// `cache\policies.json`.
        Cache = "cache",
        /// `policies.json` (last applied snapshot).
        File = "file",
    }
}

/// Request of `policy.reload`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct PolicyReloadRequest {
    /// Bypass the ETag cache.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub force: Option<bool>,
}

/// Response of `policy.reload`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct PolicyReloadResponse {
    pub policy: Policy,
    pub source: PolicySource,
    /// Whether it was (re)applied.
    pub applied: bool,
    /// Top-level sections that changed.
    pub changed: Vec<String>,
}

/// Installed component versions.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ComponentVersions {
    pub agent: String,
    pub shell: String,
}

/// Response of `update.check`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct UpdateCheckResponse {
    pub current: ComponentVersions,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub agent: Option<UpdateManifest>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub shell: Option<UpdateManifest>,
}

/// Request of `update.apply`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct UpdateApplyRequest {
    pub component: UpdateComponent,
}

/// Response of `update.apply` and result of the `update` server command.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct UpdateApplyResponse {
    pub scheduled: bool,
    #[serde(default, with = "crate::wire::ts_opt", skip_serializing_if = "Option::is_none")]
    pub at: Option<DateTime<Utc>>,
}

// ───────────────────────────── Updates ─────────────────────────────

wire_enum! {
    /// Update channel.
    UpdateChannel {
        Stable = "stable",
        Beta = "beta",
    }
}

wire_enum! {
    /// Updatable component.
    UpdateComponent {
        Agent = "agent",
        Shell = "shell",
    }
}

wire_enum! {
    /// Phase reported by `update.progress`.
    UpdatePhase {
        Downloading = "downloading",
        /// Verifying SHA-256 and signature.
        Verifying = "verifying",
        /// Copying to `pending-update\`.
        Staging = "staging",
        Applying = "applying",
        /// Failed; `error` set.
        Failed = "failed",
    }
}

/// Update package descriptor (IPC_PROTOCOL.md §6.19; `GET /updates/{channel}/manifest`).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct UpdateManifest {
    pub channel: UpdateChannel,
    pub component: UpdateComponent,
    /// Package semver.
    pub version: String,
    /// HTTPS download URL (Bearer required, `Range` supported).
    pub url: String,
    /// Lower-case hex SHA-256 of the package.
    pub sha256: String,
    /// Package size in bytes.
    pub size: i64,
    /// Base64 RSA-PSS-SHA256 signature over the raw package bytes.
    pub signature: String,
    /// Markdown release notes.
    pub release_notes: String,
    /// Must be applied even during a session (after a 60 s notice).
    pub mandatory: bool,
    #[serde(with = "crate::wire::ts")]
    pub published_at: DateTime<Utc>,
    /// Minimum Agent version required (shell packages).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub min_agent_version: Option<String>,
}

// ───────────────────────────── Server → Agent commands ─────────────────────────────

wire_enum! {
    /// Command types the server may send over WS / `GET /agents/{pcId}/commands` (SERVER_API.md §6.1).
    ServerCommandType {
        /// Payload [`LockCommand`]; result `null`.
        Lock = "lock",
        /// No payload; result `null`.
        Unlock = "unlock",
        /// Payload [`MessageCommand`]; result [`MessageDeliveryResult`].
        Message = "message",
        /// Payload [`PowerCommand`]; result [`ScheduledResult`].
        Reboot = "reboot",
        /// Payload [`PowerCommand`]; result [`ScheduledResult`].
        Shutdown = "shutdown",
        /// Payload [`WakeCommand`]; result `null`.
        Wake = "wake",
        /// Payload [`EndSessionCommand`]; result [`SessionResult`].
        EndSession = "endSession",
        /// Payload [`ExtendSessionCommand`]; result [`SessionResult`].
        ExtendSession = "extendSession",
        /// Payload `LaunchRequest`; result `LaunchResult`.
        LaunchGame = "launchGame",
        /// Payload [`KillGameCommand`]; result [`GamesKillResponse`].
        KillGame = "killGame",
        /// Payload [`Policy`]; result [`SetPolicyResult`].
        SetPolicy = "setPolicy",
        /// No payload; result [`ReloadPolicyResult`].
        ReloadPolicy = "reloadPolicy",
        /// Payload [`ScreenshotCommand`]; result [`ScreenshotResult`].
        Screenshot = "screenshot",
        /// Payload [`RemoteControlStartCommand`]; result [`RemoteControlStartResult`].
        RemoteControlStart = "remoteControlStart",
        /// Payload [`RemoteControlStopCommand`]; result [`RemoteControlStopResult`].
        RemoteControlStop = "remoteControlStop",
        /// Payload [`UpdateCommand`]; result [`UpdateApplyResponse`].
        Update = "update",
        /// Payload [`ShowAdsArgs`]; result `null`.
        ShowAds = "showAds",
        /// Payload [`SetVolumeRequest`]; result [`VolumeState`].
        SetVolume = "setVolume",
        /// Payload [`RefreshConfigCommand`]; result [`RefreshConfigResult`].
        RefreshConfig = "refreshConfig",
    }
}

/// Normalized server command as handled by the Agent's command dispatcher.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ServerCommand {
    /// Command id (dedupe key, 24 h).
    pub id: Uuid,
    pub r#type: ServerCommandType,
    #[serde(with = "crate::wire::ts")]
    pub issued_at: DateTime<Utc>,
    /// Typed payload (see [`ServerCommandType`]) or absent.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub payload: Option<Value>,
    /// Admin login or `system`, when known.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub issued_by: Option<String>,
    /// Earlier pending command this one cancels.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub supersedes: Option<Uuid>,
    /// Discard (ack with `timeout`) after this time.
    #[serde(default, with = "crate::wire::ts_opt", skip_serializing_if = "Option::is_none")]
    pub expires_at: Option<DateTime<Utc>>,
}

// ---- BEGIN MANUAL ----
impl ServerCommand {
    /// `true` when `expires_at` has passed at `now`.
    pub fn is_expired(&self, now: DateTime<Utc>) -> bool {
        self.expires_at.is_some_and(|e| e <= now)
    }

    /// Deserializes `payload` as `T`; `Ok(None)` when absent or not an object.
    pub fn payload_as<T: DeserializeOwned>(&self) -> Result<Option<T>, ProtocolError> {
        object_as(self.payload.as_ref())
    }

    /// Builds from a REST polling envelope.
    pub fn from_envelope(envelope: ServerCommandEnvelope) -> Self {
        Self {
            id: envelope.id,
            r#type: envelope.name,
            issued_at: envelope.ts,
            payload: envelope.payload,
            issued_by: envelope.issued_by,
            supersedes: envelope.supersedes,
            expires_at: envelope.expires_at,
        }
    }

    /// Builds from a WS `command` frame; `None` for other frame types or an unknown command name.
    pub fn from_frame(frame: &WsFrame) -> Option<Self> {
        if frame.r#type != WsFrameType::Command {
            return None;
        }
        let r#type = ServerCommandType::parse(frame.name.as_deref()?)?;
        Some(Self {
            id: frame.id,
            r#type,
            issued_at: frame.ts,
            payload: frame.payload.clone(),
            issued_by: None,
            supersedes: frame.supersedes,
            expires_at: frame.expires_at,
        })
    }
}
// ---- END MANUAL ----

/// Wire form of a queued command returned by `GET /agents/{pcId}/commands`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ServerCommandEnvelope {
    pub id: Uuid,
    #[serde(with = "crate::wire::ts")]
    pub ts: DateTime<Utc>,
    pub name: ServerCommandType,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub payload: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub issued_by: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub supersedes: Option<Uuid>,
    #[serde(default, with = "crate::wire::ts_opt", skip_serializing_if = "Option::is_none")]
    pub expires_at: Option<DateTime<Utc>>,
}

/// Response of `GET /agents/{pcId}/commands`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ServerCommandsResponse {
    /// Pending commands, oldest first.
    pub items: Vec<ServerCommandEnvelope>,
}

/// Body of `POST /agents/{pcId}/commands/{commandId}/ack` and payload of a WS `ack` frame.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct CommandAck {
    pub ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<IpcError>,
    /// Typed result (see [`ServerCommandType`]) or absent.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result: Option<Value>,
}

// ---- BEGIN MANUAL ----
impl CommandAck {
    /// Successful ack without a result.
    pub fn success() -> Self {
        Self { ok: true, error: None, result: None }
    }

    /// Successful ack with a typed result.
    pub fn success_with<T: Serialize>(result: &T) -> Result<Self, ProtocolError> {
        Ok(Self { ok: true, error: None, result: Some(serde_json::to_value(result)?) })
    }

    /// Failed ack.
    pub fn failure(error: IpcError) -> Self {
        Self { ok: false, error: Some(error), result: None }
    }
}
// ---- END MANUAL ----

/// Payload of [`ServerCommandType::Lock`]; also `shell.command{lock}` args.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct LockCommand {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub message: Option<String>,
}

/// Payload of [`ServerCommandType::Message`].
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MessageCommand {
    /// Acked via `sys.ackAdminMessage`.
    pub id: Uuid,
    pub from: String,
    pub text: String,
    pub level: NotificationLevel,
    pub requires_ack: bool,
}

/// Result of [`ServerCommandType::Message`]; a second ack is sent when the user acknowledges.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MessageDeliveryResult {
    #[serde(with = "crate::wire::ts")]
    pub delivered_at: DateTime<Utc>,
    #[serde(default, with = "crate::wire::ts_opt", skip_serializing_if = "Option::is_none")]
    pub acked_at: Option<DateTime<Utc>>,
}

/// Payload of [`ServerCommandType::Reboot`] and [`ServerCommandType::Shutdown`].
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct PowerCommand {
    pub delay_sec: i32,
    /// End an active session first.
    pub force: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub message: Option<String>,
}

/// Result of power commands and `sys.reboot`/`sys.shutdown`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ScheduledResult {
    #[serde(with = "crate::wire::ts")]
    pub scheduled_at: DateTime<Utc>,
}

/// Payload of [`ServerCommandType::Wake`].
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct WakeCommand {
    /// `AA:BB:CC:DD:EE:FF`.
    pub target_mac: String,
}

/// Payload of [`ServerCommandType::EndSession`].
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct EndSessionCommand {
    pub session_id: Uuid,
    pub reason: SessionEndReason,
}

/// Payload of [`ServerCommandType::ExtendSession`].
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ExtendSessionCommand {
    pub session_id: Uuid,
    pub minutes: i32,
    /// Charge the wallet (`false` when already billed server-side).
    pub charge: bool,
}

/// Result carrying the resulting session.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct SessionResult {
    pub session: Session,
}

/// Payload of [`ServerCommandType::KillGame`].
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct KillGameCommand {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub game_id: Option<Uuid>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pid: Option<i32>,
    /// Terminate immediately instead of a graceful close.
    pub force: bool,
}

/// Result of [`ServerCommandType::SetPolicy`].
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SetPolicyResult {
    pub version: i32,
    pub applied: bool,
}

/// Result of [`ServerCommandType::ReloadPolicy`].
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ReloadPolicyResult {
    pub version: i32,
}

/// Payload of [`ServerCommandType::Screenshot`].
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ScreenshotCommand {
    /// JPEG quality 1–100.
    pub quality: i32,
    /// Pre-signed `PUT` URL for the JPEG.
    pub upload_url: String,
    /// Monitor index; `None` = primary.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub monitor: Option<i32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub max_width: Option<i32>,
}

/// Result of [`ServerCommandType::Screenshot`].
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ScreenshotResult {
    pub width: i32,
    pub height: i32,
    pub bytes: i64,
    #[serde(with = "crate::wire::ts")]
    pub uploaded_at: DateTime<Utc>,
}

/// Payload of [`ServerCommandType::RemoteControlStart`].
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RemoteControlStartCommand {
    pub session_token: String,
    /// Relay `wss://` URL.
    pub relay_url: String,
    pub fps: i32,
    pub allow_input: bool,
    /// Admin shown in the on-screen indicator.
    pub admin_name: String,
}

/// Result of [`ServerCommandType::RemoteControlStart`].
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RemoteControlStartResult {
    #[serde(with = "crate::wire::ts")]
    pub started_at: DateTime<Utc>,
}

/// Payload of [`ServerCommandType::RemoteControlStop`].
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RemoteControlStopCommand {
    pub session_token: String,
}

/// Result of [`ServerCommandType::RemoteControlStop`].
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RemoteControlStopResult {
    #[serde(with = "crate::wire::ts")]
    pub stopped_at: DateTime<Utc>,
    pub duration_sec: i32,
}

/// Payload of [`ServerCommandType::Update`].
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct UpdateCommand {
    pub component: UpdateComponent,
    /// Apply immediately (subject to session/mandatory rules).
    pub apply_now: bool,
    /// `None` = fetch.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub manifest: Option<UpdateManifest>,
}

/// Payload of [`ServerCommandType::RefreshConfig`]; all flags `false`/absent = refresh everything.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct RefreshConfigCommand {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub config: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub games: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub apps: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub tariffs: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub products: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub themes: Option<bool>,
}

// ---- BEGIN MANUAL ----
impl RefreshConfigCommand {
    /// `true` when no specific cache was selected.
    pub fn is_all(&self) -> bool {
        ![self.config, self.games, self.apps, self.tariffs, self.products, self.themes].contains(&Some(true))
    }
}
// ---- END MANUAL ----

/// Result of [`ServerCommandType::RefreshConfig`].
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RefreshConfigResult {
    /// `config`, `games`, `apps`, `tariffs`, `products`, `themes`.
    pub refreshed: Vec<String>,
}

// ───────────────────────────── Agent → Server events ─────────────────────────────

wire_enum! {
    /// Events the Agent emits over WS (SERVER_API.md §6.2); replayed via REST when offline.
    AgentEventType {
        /// Payload `SessionStartedEvent`.
        SessionStarted = "sessionStarted",
        /// Payload `SessionEndedEvent`.
        SessionEnded = "sessionEnded",
        /// Payload [`GameLaunchedEvent`].
        GameLaunched = "gameLaunched",
        /// Payload [`GameExitedEvent`].
        GameExited = "gameExited",
        /// Payload `AntiCheatReport`.
        AnticheatViolation = "anticheatViolation",
        /// Payload [`HardwareChangedEvent`].
        HardwareChanged = "hardwareChanged",
        /// Payload [`OfflineQueueFlushedEvent`].
        OfflineQueueFlushed = "offlineQueueFlushed",
    }
}

/// Agent → server event.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct AgentEvent {
    pub r#type: AgentEventType,
    #[serde(with = "crate::wire::ts")]
    pub at: DateTime<Utc>,
    /// Typed payload (see [`AgentEventType`]).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub payload: Option<Value>,
}

// ---- BEGIN MANUAL ----
impl AgentEvent {
    /// Event with a typed payload.
    pub fn of<T: Serialize>(r#type: AgentEventType, at: DateTime<Utc>, payload: &T) -> Result<Self, ProtocolError> {
        Ok(Self { r#type, at, payload: Some(serde_json::to_value(payload)?) })
    }

    /// camelCase wire name used in [`WsFrame::name`].
    pub const fn wire_name(&self) -> &'static str {
        self.r#type.wire_name()
    }
}
// ---- END MANUAL ----

/// Payload of [`AgentEventType::GameLaunched`].
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct GameLaunchedEvent {
    pub session_id: Uuid,
    pub game_id: Uuid,
    pub pid: i32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub account_lease_id: Option<Uuid>,
    #[serde(with = "crate::wire::ts")]
    pub at: DateTime<Utc>,
}

/// Payload of [`AgentEventType::GameExited`].
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct GameExitedEvent {
    pub session_id: Uuid,
    pub game_id: Uuid,
    pub pid: i32,
    pub exit_code: i32,
    pub played_sec: i32,
    #[serde(with = "crate::wire::ts")]
    pub at: DateTime<Utc>,
}

/// Payload of [`AgentEventType::HardwareChanged`].
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct HardwareChangedEvent {
    pub hardware: HardwareInfo,
    /// Changed top-level keys (`gpu`, `disks`, …).
    pub diff: Vec<String>,
}

/// Payload of [`AgentEventType::OfflineQueueFlushed`].
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct OfflineQueueFlushedEvent {
    /// Entries delivered.
    pub count: i32,
    /// Entries moved to the dead-letter table.
    pub deadlettered: i32,
    #[serde(with = "crate::wire::ts")]
    pub offline_from: DateTime<Utc>,
    #[serde(with = "crate::wire::ts")]
    pub offline_to: DateTime<Utc>,
}

// ───────────────────────────── WebSocket ─────────────────────────────

wire_enum! {
    /// WS frame type (SERVER_API.md §6).
    WsFrameType {
        /// Server → Agent command; must be acked.
        Command = "command",
        /// Agent → Server ack of a command.
        Ack = "ack",
        /// Agent → Server event; not acked.
        Event = "event",
        /// Keepalive request (server every 20 s).
        Ping = "ping",
        /// Keepalive reply (within 10 s).
        Pong = "pong",
        /// Server → Agent push; not acked.
        Push = "push",
    }
}

wire_enum! {
    /// Server → Agent pushes ([`WsFrameType::Push`], SERVER_API.md §6.3).
    WsPushKind {
        /// Payload `Balance` → IPC `wallet.updated`.
        WalletUpdated = "walletUpdated",
        /// Payload `ChatMessage` → IPC `chat.message`.
        ChatMessage = "chatMessage",
        /// Payload `Notification` → IPC `notification.push`.
        Notification = "notification",
        /// Payload `Order` → IPC `notification.push` + `shop.orderUpdated`.
        OrderUpdated = "orderUpdated",
        /// Payload `Booking` → IPC `notification.push`.
        BookingUpdated = "bookingUpdated",
        /// Payload `Tournament` → IPC `notification.push`.
        TournamentUpdated = "tournamentUpdated",
        /// Payload `Session` → reconcile + IPC `session.updated`.
        SessionUpdated = "sessionUpdated",
        /// Payload [`PcStatusChangedPush`] → seat-map cache.
        PcStatusChanged = "pcStatusChanged",
        /// Payload [`UserRevokedPush`] → IPC `auth.expired{revoked}`, end session.
        UserRevoked = "userRevoked",
    }
}

/// Payload of [`WsPushKind::PcStatusChanged`].
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct PcStatusChangedPush {
    pub pc_id: Uuid,
    pub status: PcStatus,
}

/// Payload of [`WsPushKind::UserRevoked`].
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct UserRevokedPush {
    pub user_id: Uuid,
    pub reason: String,
}

/// Ack body inside a [`WsFrameType::Ack`] frame.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct WsAck {
    /// Id of the command being acknowledged.
    pub id: Uuid,
    pub ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<IpcError>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result: Option<Value>,
}

// ---- BEGIN MANUAL ----
impl WsAck {
    /// Builds from a REST-style [`CommandAck`].
    pub fn from_ack(command_id: Uuid, ack: CommandAck) -> Self {
        Self { id: command_id, ok: ack.ok, error: ack.error, result: ack.result }
    }
}
// ---- END MANUAL ----

/// One text frame on `wss://<server>/ws/agent` (SERVER_API.md §6). Max 1 MiB. `payload` is always
/// present on the wire (`null` when empty).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct WsFrame {
    pub r#type: WsFrameType,
    /// Frame id (command id for commands).
    pub id: Uuid,
    #[serde(with = "crate::wire::ts")]
    pub ts: DateTime<Utc>,
    /// [`ServerCommandType`] / [`AgentEventType`] / [`WsPushKind`] wire name; required for command/event/push.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    pub payload: Option<Value>,
    /// Required for [`WsFrameType::Ack`].
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ack: Option<WsAck>,
    /// Command only: earlier pending command this one cancels.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub supersedes: Option<Uuid>,
    /// Command only: discard after this time.
    #[serde(default, with = "crate::wire::ts_opt", skip_serializing_if = "Option::is_none")]
    pub expires_at: Option<DateTime<Utc>>,
}

impl WsFrame {
    /// Maximum frame size in bytes.
    pub const MAX_FRAME_BYTES: usize = crate::WS_MAX_FRAME_BYTES;

    /// Subprotocol negotiated on connect.
    pub const SUBPROTOCOL: &'static str = crate::WS_SUBPROTOCOL;
}

// ---- BEGIN MANUAL ----
impl WsFrame {
    fn bare(r#type: WsFrameType, id: Uuid, ts: DateTime<Utc>) -> Self {
        Self { r#type, id, ts, name: None, payload: None, ack: None, supersedes: None, expires_at: None }
    }

    /// [`WsFrameType::Ack`] frame for `command_id`.
    pub fn ack_of(command_id: Uuid, ack: CommandAck) -> Self {
        Self { ack: Some(WsAck::from_ack(command_id, ack)), ..Self::bare(WsFrameType::Ack, Uuid::new_v4(), Utc::now()) }
    }

    /// [`WsFrameType::Event`] frame.
    pub fn event_of(event: &AgentEvent) -> Self {
        Self {
            name: Some(event.wire_name().to_owned()),
            payload: event.payload.clone(),
            ..Self::bare(WsFrameType::Event, Uuid::new_v4(), event.at)
        }
    }

    /// [`WsFrameType::Pong`] reply to `ping`.
    pub fn pong_for(ping: &WsFrame) -> Self {
        Self::bare(WsFrameType::Pong, ping.id, Utc::now())
    }

    /// [`WsFrameType::Ping`] frame.
    pub fn ping() -> Self {
        Self::bare(WsFrameType::Ping, Uuid::new_v4(), Utc::now())
    }

    /// Parses `name` as a push kind (frames of type [`WsFrameType::Push`]).
    pub fn push_kind(&self) -> Option<WsPushKind> {
        (self.r#type == WsFrameType::Push).then(|| WsPushKind::parse(self.name.as_deref()?)).flatten()
    }

    /// Deserializes `payload` as `T`; `Ok(None)` when absent or not an object.
    pub fn payload_as<T: DeserializeOwned>(&self) -> Result<Option<T>, ProtocolError> {
        object_as(self.payload.as_ref())
    }
}

/// Deserializes a JSON object slot; `Ok(None)` when absent, `null` or not an object.
pub(crate) fn object_as<T: DeserializeOwned>(value: Option<&Value>) -> Result<Option<T>, ProtocolError> {
    match value {
        Some(v @ Value::Object(_)) => Ok(Some(serde_json::from_value(v.clone())?)),
        _ => Ok(None),
    }
}
// ---- END MANUAL ----

// ───────────────────────────── Agent REST ─────────────────────────────

/// Body of `POST /agents/register` (auth `X-Club-Key`).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct AgentRegisterRequest {
    /// sha256 hex.
    pub hwid: String,
    pub machine_name: String,
    pub agent_version: String,
    pub hardware: HardwareInfo,
    pub ip_address: String,
    pub mac_address: String,
    /// Hint for re-registration after reinstall.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub previous_pc_id: Option<Uuid>,
}

/// Response of `POST /agents/register`. Secrets are stored DPAPI-protected in `secure\agent.tokens`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct AgentRegisterResponse {
    pub pc_id: Uuid,
    pub pc: Pc,
    /// Agent JWT (≈ 1 h).
    pub access_token: String,
    /// Opaque refresh token (30 d, single-use rotation).
    pub refresh_token: String,
    /// Base64 32-byte HMAC key for request signing.
    pub signing_secret: String,
    #[serde(with = "crate::wire::ts")]
    pub expires_at: DateTime<Utc>,
    #[serde(with = "crate::wire::ts")]
    pub server_time: DateTime<Utc>,
    pub config: AgentServerConfig,
}

/// Body of `POST /agents/refresh`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct AgentRefreshRequest {
    pub refresh_token: String,
    pub hwid: String,
}

/// Response of `POST /agents/refresh`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct AgentRefreshResponse {
    pub access_token: String,
    /// Old one is invalid.
    pub refresh_token: String,
    #[serde(with = "crate::wire::ts")]
    pub expires_at: DateTime<Utc>,
    /// Present only when rotated; must be switched atomically.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub signing_secret: Option<String>,
}

/// Running game summary in a heartbeat.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct HeartbeatRunningGame {
    pub game_id: Uuid,
    pub pid: i32,
    #[serde(with = "crate::wire::ts")]
    pub started_at: DateTime<Utc>,
}

/// Body of `POST /agents/{pcId}/heartbeat` (every `session.heartbeatSec`).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct HeartbeatRequest {
    /// Agent's own view of the PC status.
    pub status: PcStatus,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub current_session_id: Option<Uuid>,
    pub agent_version: String,
    pub shell_version: String,
    pub uptime_sec: i64,
    pub ip_address: String,
    /// Applied policy version.
    pub policy_version: i32,
    pub running_games: Vec<HeartbeatRunningGame>,
    /// Outbox size.
    pub offline_queue: i32,
    /// Whether the Shell pipe connection is alive.
    pub shell_connected: bool,
}

/// Response of `POST /agents/{pcId}/heartbeat`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct HeartbeatResponse {
    /// Used for offset correction.
    #[serde(with = "crate::wire::ts")]
    pub server_time: DateTime<Utc>,
    /// Authoritative status.
    pub pc_status: PcStatus,
    /// Latest policy version; reload when newer than applied.
    pub policy_version: i32,
    pub config_version: i32,
    /// Games/apps catalogue version; refresh lists when changed.
    pub catalog_version: String,
    /// Commands queued while WS was down.
    pub pending_commands: i32,
    /// Server view of the current session for reconciliation.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub session: Option<Session>,
}

/// Well-known values of [`TelemetryEvent::kind`].
pub mod telemetry_event_kinds {
    pub const SHELL_CRASH: &str = "shellCrash";
    pub const SHELL_CRASH_LOOP: &str = "shellCrashLoop";
    pub const POLICY_APPLY_FAILED: &str = "policyApplyFailed";
    pub const UPDATE_FAILED: &str = "updateFailed";
    pub const PIPE_ERROR: &str = "pipeError";
    pub const LAUNCHER_ERROR: &str = "launcherError";
    pub const DEADLETTER: &str = "deadletter";
}

/// Agent diagnostic event in a telemetry batch.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct TelemetryEvent {
    /// [`telemetry_event_kinds`].
    pub kind: String,
    #[serde(with = "crate::wire::ts")]
    pub at: DateTime<Utc>,
    pub data: Value,
}

/// Body of `POST /agents/{pcId}/telemetry`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct TelemetryBatch {
    /// ≤ 120.
    pub samples: Vec<PcMetrics>,
    pub events: Vec<TelemetryEvent>,
    /// Inventory when changed / on rescan.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub hardware: Option<HardwareInfo>,
    /// Last ≤ 50 Warning+ log lines when `events` is non-empty.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub logs_tail: Option<Vec<String>>,
}

impl TelemetryBatch {
    pub const MAX_SAMPLES: usize = 120;
    pub const MAX_LOG_LINES: usize = 50;
}

/// Body of `POST /support/call-admin`. Sent with an `Idempotency-Key`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct CallAdminTicketRequest {
    pub pc_id: Uuid,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub user_id: Option<Uuid>,
    pub category: CallAdminCategory,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub message: Option<String>,
    /// Client clock, for offline replay.
    #[serde(with = "crate::wire::ts")]
    pub at: DateTime<Utc>,
}

// ---- BEGIN MANUAL ----
#[cfg(test)]
mod tests {
    use super::*;
    use crate::error::{assert_wire, ErrorCode};
    use crate::session::{sample_session, SAMPLE_SESSION_JSON};
    use crate::user::{sample_user, SAMPLE_USER_JSON};
    use chrono::TimeZone;

    fn t(h: u32, m: u32, s: u32) -> DateTime<Utc> {
        Utc.with_ymd_and_hms(2026, 9, 21, h, m, s).unwrap()
    }

    #[test]
    fn enum_wire_values() {
        assert_wire(IpcAuthLevel::ALL, &["none", "hello", "user", "session"]);
        assert_wire(CallAdminCategory::ALL, &["help", "technical", "order", "other"]);
        assert_wire(ClientErrorLevel::ALL, &["warn", "error"]);
        assert_wire(PolicySource::ALL, &["server", "cache", "file"]);
        assert_wire(UpdateChannel::ALL, &["stable", "beta"]);
        assert_wire(UpdateComponent::ALL, &["agent", "shell"]);
        assert_wire(UpdatePhase::ALL, &["downloading", "verifying", "staging", "applying", "failed"]);
        assert_wire(
            ServerCommandType::ALL,
            &[
                "lock", "unlock", "message", "reboot", "shutdown", "wake", "endSession", "extendSession", "launchGame",
                "killGame", "setPolicy", "reloadPolicy", "screenshot", "remoteControlStart", "remoteControlStop",
                "update", "showAds", "setVolume", "refreshConfig",
            ],
        );
        assert_wire(
            AgentEventType::ALL,
            &["sessionStarted", "sessionEnded", "gameLaunched", "gameExited", "anticheatViolation", "hardwareChanged", "offlineQueueFlushed"],
        );
        assert_wire(WsFrameType::ALL, &["command", "ack", "event", "ping", "pong", "push"]);
        assert_wire(
            WsPushKind::ALL,
            &[
                "walletUpdated", "chatMessage", "notification", "orderUpdated", "bookingUpdated", "tournamentUpdated",
                "sessionUpdated", "pcStatusChanged", "userRevoked",
            ],
        );
        assert_eq!(ServerCommandType::parse("RemoteControlStart"), Some(ServerCommandType::RemoteControlStart));
    }

    #[test]
    fn agent_command_table_matches_ipc_protocol() {
        assert_eq!(AgentCommand::ALL.len(), 63);
        let expected: &[&str] = &[
            "auth.hello", "auth.login", "auth.logout", "auth.status", "auth.qrStart",
            "session.get", "session.start", "session.pause", "session.resume", "session.end", "session.extend",
            "session.lock", "session.unlock", "session.timeLeft",
            "games.list", "games.get", "games.launch", "games.kill", "games.running", "games.installStatus",
            "apps.list", "apps.launch",
            "wallet.balance", "wallet.tariffs", "wallet.history", "wallet.topupIntent",
            "shop.products", "shop.order", "shop.orderStatus", "shop.orders",
            "chat.history", "chat.send", "chat.markRead",
            "booking.seats", "booking.reserve", "booking.cancel",
            "tournaments.list", "tournaments.join", "tournaments.leaderboard",
            "profile.get", "profile.update", "profile.stats", "profile.achievements", "profile.loyalty",
            "settings.get", "settings.set",
            "sys.ping", "sys.pcInfo", "sys.hardware", "sys.metrics", "sys.callAdmin", "sys.reboot", "sys.shutdown",
            "sys.lockScreen", "sys.setVolume", "sys.setLocale", "sys.unlockAdmin", "sys.logClientError",
            "sys.ackAdminMessage",
            "policy.get", "policy.reload",
            "update.check", "update.apply",
        ];
        let names: Vec<&str> = AgentCommand::ALL.iter().map(|c| c.name()).collect();
        assert_eq!(names, expected);
        for c in AgentCommand::ALL {
            assert_eq!(AgentCommand::try_from(c.name()).unwrap(), *c);
            assert_eq!(serde_json::to_string(c).unwrap(), format!("\"{}\"", c.name()));
            assert_eq!(serde_json::from_str::<AgentCommand>(&format!("\"{}\"", c.name())).unwrap(), *c);
            assert!(crate::ipc::IpcEnvelope::is_valid_name(c.name()));
        }
        assert_eq!(AgentCommand::SysPing.response_name(), "sys.pong");
        assert_eq!(AgentCommand::GamesList.response_name(), "games.list");
        assert_eq!(AgentCommand::AuthHello.required_auth(), IpcAuthLevel::None);
        assert_eq!(AgentCommand::SysPing.required_auth(), IpcAuthLevel::None);
        assert_eq!(AgentCommand::GamesList.required_auth(), IpcAuthLevel::Hello);
        assert_eq!(AgentCommand::WalletBalance.required_auth(), IpcAuthLevel::User);
        assert_eq!(AgentCommand::ShopOrder.required_auth(), IpcAuthLevel::Session);
        assert_eq!(AgentCommand::GamesLaunch.required_auth(), IpcAuthLevel::Session);
        assert!(AgentCommand::try_from("nope.nope").is_err());
        assert!(!AgentCommand::is_known("Session.Start"));
        assert_eq!(names::events::ALL.len(), 18);
    }

    #[test]
    fn hello_payloads_match_ipc_protocol_example() {
        let req = AuthHelloRequest {
            shell_token: "9f".repeat(32),
            shell_version: "1.4.2".into(),
            pid: 5120,
            wts_session_id: 1,
            locale: Locale::Ru,
            capabilities: vec![shell_capabilities::GAMEPAD.into(), shell_capabilities::OVERLAY.into()],
        };
        assert_eq!(
            serde_json::to_string(&req).unwrap(),
            format!(r#"{{"shellToken":"{}","shellVersion":"1.4.2","pid":5120,"wtsSessionId":1,"locale":"ru","capabilities":["gamepad","overlay"]}}"#, "9f".repeat(32))
        );
        let res = AuthHelloResponse {
            agent_version: "1.4.2".into(),
            protocol: 1,
            pc_id: Uuid::nil(),
            pc_name: "PC-12".into(),
            zone: "Standard".into(),
            server_online: true,
            policy_version: 12,
            server_time: t(10, 0, 0),
            capabilities: vec![agent_capabilities::ACCOUNT_POOL.into()],
            kiosk_user: "club".into(),
        };
        let json = serde_json::to_string(&res).unwrap();
        assert_eq!(
            json,
            r#"{"agentVersion":"1.4.2","protocol":1,"pcId":"00000000-0000-0000-0000-000000000000","pcName":"PC-12","zone":"Standard","serverOnline":true,"policyVersion":12,"serverTime":"2026-09-21T10:00:00.000Z","capabilities":["accountPool"],"kioskUser":"club"}"#
        );
        assert_eq!(serde_json::from_str::<AuthHelloResponse>(&json).unwrap(), res);
    }

    #[test]
    fn auth_and_session_payloads() {
        let login = AuthLoginRequest { kind: AuthKind::Password, username: Some("player1".into()), password: Some("***".into()), qr_token: None, card_id: None, token: None };
        assert_eq!(serde_json::to_string(&login).unwrap(), r#"{"kind":"password","username":"player1","password":"***"}"#);
        let server = login.to_server_request(Uuid::nil(), "hw");
        assert_eq!(server.hwid, "hw");
        assert_eq!(server.username.as_deref(), Some("player1"));

        let res = AuthLoginResponse { user: sample_user(), session: None, expires_at: t(22, 0, 0), mode: ConnectivityState::Online };
        let json = serde_json::to_string(&res).unwrap();
        assert_eq!(json, format!(r#"{{"user":{SAMPLE_USER_JSON},"expiresAt":"2026-09-21T22:00:00.000Z","mode":"online"}}"#));
        assert_eq!(serde_json::from_str::<AuthLoginResponse>(&json).unwrap(), res);

        let status = AuthStatusResponse { authenticated: false, mode: ConnectivityState::Offline, user: None, session: None, expires_at: None };
        assert_eq!(serde_json::to_string(&status).unwrap(), r#"{"authenticated":false,"mode":"offline"}"#);
        let logout = AuthLogoutResponse { ok: true, session_ended: true, session: Some(sample_session()) };
        assert_eq!(serde_json::to_string(&logout).unwrap(), format!(r#"{{"ok":true,"sessionEnded":true,"session":{SAMPLE_SESSION_JSON}}}"#));
        assert_eq!(serde_json::to_string(&AuthLogoutRequest::default()).unwrap(), "{}");
        assert_eq!(serde_json::to_string(&AuthLogoutRequest { reason: Some(SessionEndReason::Idle) }).unwrap(), r#"{"reason":"idle"}"#);

        let start = SessionStartRequest { tariff_id: Uuid::nil(), prepaid: true, minutes: Some(60) };
        assert_eq!(serde_json::to_string(&start).unwrap(), r#"{"tariffId":"00000000-0000-0000-0000-000000000000","prepaid":true,"minutes":60}"#);
        let parsed: SessionStartRequest = serde_json::from_str(r#"{"tariffId":"00000000-0000-0000-0000-000000000000","minutes":60,"prepaid":true}"#).unwrap();
        assert_eq!(parsed, start);
        let tl = SessionTimeLeftResponse { state: SessionState::Idle, seconds_left: 0, seconds_used: 0, server_time: t(10, 0, 0), session_id: None, ends_at: None };
        assert_eq!(serde_json::to_string(&tl).unwrap(), r#"{"state":"idle","secondsLeft":0,"secondsUsed":0,"serverTime":"2026-09-21T10:00:00.000Z"}"#);
        assert_eq!(serde_json::to_string(&SessionUnlockRequest { password: None, pin: Some("1234".into()) }).unwrap(), r#"{"pin":"1234"}"#);
        assert_eq!(serde_json::to_string(&SessionExtendRequest { minutes: 30, tariff_id: None }).unwrap(), r#"{"minutes":30}"#);
    }

    #[test]
    fn games_wallet_shop_chat_payloads() {
        let list = GamesListRequest { installed_only: Some(true), sort: Some(GamesSort::LastPlayed), page: Some(1), page_size: Some(50), ..Default::default() };
        assert_eq!(serde_json::to_string(&list).unwrap(), r#"{"installedOnly":true,"sort":"lastPlayed","page":1,"pageSize":50}"#);
        assert_eq!(serde_json::to_string(&GamesListRequest::default()).unwrap(), "{}");
        let launch = GamesLaunchRequest { game_id: Uuid::nil(), use_account_pool: None, extra_args: None, resolution: Some(Resolution { width: 1920, height: 1080 }) };
        assert_eq!(serde_json::to_string(&launch).unwrap(), r#"{"gameId":"00000000-0000-0000-0000-000000000000","resolution":{"width":1920,"height":1080}}"#);
        assert_eq!(serde_json::to_string(&GamesKillResponse { killed: 1, pids: vec![7788] }).unwrap(), r#"{"killed":1,"pids":[7788]}"#);
        assert_eq!(serde_json::to_string(&AppsLaunchResponse { ok: true, pid: 5, started_at: t(1, 2, 3) }).unwrap(), r#"{"ok":true,"pid":5,"startedAt":"2026-09-21T01:02:03.000Z"}"#);

        let hist = WalletHistoryRequest { page: Some(2), from: Some(t(0, 0, 0)), r#type: Some(TransactionType::Charge), ..Default::default() };
        assert_eq!(serde_json::to_string(&hist).unwrap(), r#"{"page":2,"from":"2026-09-21T00:00:00.000Z","type":"charge"}"#);
        let paged: WalletHistoryResponse = serde_json::from_str(r#"{"items":[],"total":120,"page":2,"pageSize":50}"#).unwrap();
        assert!(paged.has_more());
        assert_eq!(serde_json::to_string(&paged).unwrap(), r#"{"items":[],"total":120,"page":2,"pageSize":50}"#);
        let topup = WalletTopupIntentRequest { amount: Money::uzs(100_000), provider: TopupProvider::Click };
        assert_eq!(serde_json::to_string(&topup).unwrap(), r#"{"amount":{"amount":100000,"currency":"UZS"},"provider":"click"}"#);

        let order = ShopOrderRequest { items: vec![OrderLineRequest { product_id: Uuid::nil(), qty: 2 }], idempotency_key: Uuid::nil(), note: None };
        assert_eq!(
            serde_json::to_string(&order).unwrap(),
            r#"{"items":[{"productId":"00000000-0000-0000-0000-000000000000","qty":2}],"idempotencyKey":"00000000-0000-0000-0000-000000000000"}"#
        );
        assert_eq!(serde_json::to_string(&ShopProductsRequest { category: Some(ProductCategory::Drink), search: None }).unwrap(), r#"{"category":"drink"}"#);
        let send = ChatSendRequest { text: "hi".into(), idempotency_key: Uuid::nil(), room_id: None };
        assert_eq!(serde_json::to_string(&send).unwrap(), r#"{"text":"hi","idempotencyKey":"00000000-0000-0000-0000-000000000000"}"#);
        assert_eq!(serde_json::to_string(&ChatMarkReadResponse { room_id: "club".into(), unread: 0 }).unwrap(), r#"{"roomId":"club","unread":0}"#);
        let history = ChatHistoryResponse { room_id: "club".into(), items: vec![], has_more: false, unread: 3 };
        assert_eq!(serde_json::to_string(&history).unwrap(), r#"{"roomId":"club","items":[],"hasMore":false,"unread":3}"#);
    }

    #[test]
    fn booking_tournament_settings_payloads() {
        let seats = BookingSeatsRequest { date: NaiveDate::from_ymd_opt(2026, 9, 21).unwrap() };
        assert_eq!(serde_json::to_string(&seats).unwrap(), r#"{"date":"2026-09-21"}"#);
        let res = BookingSeatsResponse {
            date: seats.date,
            seats: vec![],
            bookings: vec![],
            slot_minutes: 30,
            open_from: Some(NaiveTime::from_hms_opt(9, 0, 0).unwrap()),
            open_to: None,
        };
        let json = serde_json::to_string(&res).unwrap();
        assert_eq!(json, r#"{"date":"2026-09-21","seats":[],"bookings":[],"slotMinutes":30,"openFrom":"09:00"}"#);
        assert_eq!(serde_json::from_str::<BookingSeatsResponse>(&json).unwrap(), res);
        let reserve = BookingReserveRequest { pc_id: Uuid::nil(), from: t(12, 0, 0), to: t(13, 0, 0) };
        assert_eq!(
            serde_json::to_string(&reserve).unwrap(),
            r#"{"pcId":"00000000-0000-0000-0000-000000000000","from":"2026-09-21T12:00:00.000Z","to":"2026-09-21T13:00:00.000Z"}"#
        );
        let lb = TournamentsLeaderboardResponse { tournament_id: Uuid::nil(), entries: vec![], updated_at: t(0, 0, 0), me: None };
        assert_eq!(serde_json::to_string(&lb).unwrap(), r#"{"tournamentId":"00000000-0000-0000-0000-000000000000","entries":[],"updatedAt":"2026-09-21T00:00:00.000Z"}"#);
        assert_eq!(serde_json::to_string(&TournamentsListRequest { state: Some(TournamentState::Live), game_id: None }).unwrap(), r#"{"state":"live"}"#);

        let settings = ShellSettings {
            locale: Locale::En,
            theme: "default".into(),
            available_themes: vec!["default".into(), "neon".into()],
            volume: 60,
            muted: false,
            idle_timeout_sec: 300,
            show_metrics_overlay: false,
            allow_virtual_keyboard: true,
            ui_sounds: true,
            features: ShellFeatures { shop: true, chat: true, booking: true, tournaments: true, profile: true, topup: true, apps: true, call_admin: true },
        };
        let json = serde_json::to_string(&settings).unwrap();
        assert_eq!(
            json,
            r#"{"locale":"en","theme":"default","availableThemes":["default","neon"],"volume":60,"muted":false,"idleTimeoutSec":300,"showMetricsOverlay":false,"allowVirtualKeyboard":true,"uiSounds":true,"features":{"shop":true,"chat":true,"booking":true,"tournaments":true,"profile":true,"topup":true,"apps":true,"callAdmin":true}}"#
        );
        assert_eq!(serde_json::from_str::<ShellSettings>(&json).unwrap(), settings);
        assert!(SettingsSetRequest::default().is_empty());
        let set = SettingsSetRequest { volume: Some(80), muted: Some(false), ..Default::default() };
        assert!(!set.is_empty());
        assert_eq!(serde_json::to_string(&set).unwrap(), r#"{"volume":80,"muted":false}"#);
    }

    #[test]
    fn sys_policy_update_payloads() {
        let ping = SysPingRequest { seq: 7, sent_at: t(10, 0, 0) };
        assert_eq!(serde_json::to_string(&ping).unwrap(), r#"{"seq":7,"sentAt":"2026-09-21T10:00:00.000Z"}"#);
        let pong = SysPongResponse { seq: 7, sent_at: t(10, 0, 0), received_at: t(10, 0, 1), connectivity: ConnectivityState::Online };
        assert_eq!(
            serde_json::to_string(&pong).unwrap(),
            r#"{"seq":7,"sentAt":"2026-09-21T10:00:00.000Z","receivedAt":"2026-09-21T10:00:01.000Z","connectivity":"online"}"#
        );
        let call = SysCallAdminRequest { category: CallAdminCategory::Technical, message: Some("no sound".into()) };
        assert_eq!(serde_json::to_string(&call).unwrap(), r#"{"category":"technical","message":"no sound"}"#);
        let ticket = SysCallAdminResponse { ticket_id: Uuid::nil(), created_at: t(10, 0, 0), queue_position: None };
        assert_eq!(serde_json::to_string(&ticket).unwrap(), r#"{"ticketId":"00000000-0000-0000-0000-000000000000","createdAt":"2026-09-21T10:00:00.000Z"}"#);
        assert_eq!(serde_json::to_string(&SetVolumeRequest { level: 40, muted: None }).unwrap(), r#"{"level":40}"#);
        assert_eq!(serde_json::to_string(&VolumeState { level: 40, muted: false }).unwrap(), r#"{"level":40,"muted":false}"#);
        assert_eq!(serde_json::to_string(&OkResponse::OK).unwrap(), r#"{"ok":true}"#);
        let log = SysLogClientErrorRequest { level: ClientErrorLevel::Error, message: "boom".into(), stack: None, route: Some("/home".into()) };
        assert_eq!(serde_json::to_string(&log).unwrap(), r#"{"level":"error","message":"boom","route":"/home"}"#);
        let unlock = SysUnlockAdminResponse { ok: true, admin_token: "tok".into(), expires_at: t(10, 5, 0) };
        assert_eq!(serde_json::to_string(&unlock).unwrap(), r#"{"ok":true,"adminToken":"tok","expiresAt":"2026-09-21T10:05:00.000Z"}"#);

        let reload = PolicyReloadResponse { policy: crate::pc::sample_policy(), source: PolicySource::Cache, applied: true, changed: vec!["kiosk".into()] };
        let json = serde_json::to_string(&reload).unwrap();
        assert!(json.starts_with(&format!(r#"{{"policy":{}"#, crate::pc::SAMPLE_POLICY_JSON)));
        assert!(json.ends_with(r#""source":"cache","applied":true,"changed":["kiosk"]}"#));
        assert_eq!(serde_json::from_str::<PolicyReloadResponse>(&json).unwrap(), reload);

        let manifest = sample_manifest();
        let check = UpdateCheckResponse { current: ComponentVersions { agent: "1.4.2".into(), shell: "1.4.2".into() }, agent: None, shell: Some(manifest.clone()) };
        let json = serde_json::to_string(&check).unwrap();
        assert_eq!(json, format!(r#"{{"current":{{"agent":"1.4.2","shell":"1.4.2"}},"shell":{}}}"#, SAMPLE_MANIFEST_JSON));
        assert_eq!(serde_json::from_str::<UpdateCheckResponse>(&json).unwrap(), check);
        assert_eq!(serde_json::to_string(&UpdateApplyResponse { scheduled: true, at: Some(t(4, 0, 0)) }).unwrap(), r#"{"scheduled":true,"at":"2026-09-21T04:00:00.000Z"}"#);
        assert_eq!(serde_json::to_string(&UpdateApplyRequest { component: UpdateComponent::Shell }).unwrap(), r#"{"component":"shell"}"#);
    }

    const SAMPLE_MANIFEST_JSON: &str = r#"{"channel":"stable","component":"shell","version":"1.5.0","url":"https://u/shell-1.5.0.msi","sha256":"ab","size":52428800,"signature":"c2ln","releaseNotes":"# 1.5.0","mandatory":false,"publishedAt":"2026-09-21T03:00:00.000Z","minAgentVersion":"1.4.0"}"#;

    fn sample_manifest() -> UpdateManifest {
        UpdateManifest {
            channel: UpdateChannel::Stable,
            component: UpdateComponent::Shell,
            version: "1.5.0".into(),
            url: "https://u/shell-1.5.0.msi".into(),
            sha256: "ab".into(),
            size: 52_428_800,
            signature: "c2ln".into(),
            release_notes: "# 1.5.0".into(),
            mandatory: false,
            published_at: t(3, 0, 0),
            min_agent_version: Some("1.4.0".into()),
        }
    }

    #[test]
    fn update_manifest_json() {
        let m = sample_manifest();
        assert_eq!(serde_json::to_string(&m).unwrap(), SAMPLE_MANIFEST_JSON);
        assert_eq!(serde_json::from_str::<UpdateManifest>(SAMPLE_MANIFEST_JSON).unwrap(), m);
        let cmd = UpdateCommand { component: UpdateComponent::Agent, apply_now: true, manifest: None };
        assert_eq!(serde_json::to_string(&cmd).unwrap(), r#"{"component":"agent","applyNow":true}"#);
    }

    #[test]
    fn server_command_from_ws_frame_matches_server_api_example() {
        let json = r#"{"type":"command","id":"c0a80000-0000-4000-8000-000000000001","ts":"2026-09-21T10:20:00.000Z","name":"lock","payload":{"reason":"admin","message":"Please come to the desk"},"expiresAt":"2026-09-21T10:25:00.000Z"}"#;
        let frame: WsFrame = serde_json::from_str(json).unwrap();
        assert_eq!(serde_json::to_string(&frame).unwrap(), json);
        let cmd = ServerCommand::from_frame(&frame).unwrap();
        assert_eq!(cmd.r#type, ServerCommandType::Lock);
        assert_eq!(cmd.issued_at, t(10, 20, 0));
        let lock: LockCommand = cmd.payload_as().unwrap().unwrap();
        assert_eq!(lock.reason.as_deref(), Some("admin"));
        assert!(!cmd.is_expired(t(10, 24, 59)));
        assert!(cmd.is_expired(t(10, 25, 0)));
        assert_eq!(
            serde_json::to_string(&cmd).unwrap(),
            r#"{"id":"c0a80000-0000-4000-8000-000000000001","type":"lock","issuedAt":"2026-09-21T10:20:00.000Z","payload":{"reason":"admin","message":"Please come to the desk"},"expiresAt":"2026-09-21T10:25:00.000Z"}"#
        );
        // Unknown command names and non-command frames yield None.
        let bad: WsFrame = serde_json::from_str(&json.replace(r#""name":"lock""#, r#""name":"dance""#)).unwrap();
        assert!(ServerCommand::from_frame(&bad).is_none());
        let ping = WsFrame::ping();
        assert!(ServerCommand::from_frame(&ping).is_none());
        assert_eq!(ping.payload_as::<LockCommand>().unwrap(), None);

        let env: ServerCommandEnvelope = serde_json::from_str(
            r#"{"id":"c0a80000-0000-4000-8000-000000000001","ts":"2026-09-21T10:20:00.000Z","name":"unlock","payload":null,"issuedBy":"admin1"}"#,
        )
        .unwrap();
        let cmd = ServerCommand::from_envelope(env);
        assert_eq!(cmd.r#type, ServerCommandType::Unlock);
        assert_eq!(cmd.payload, None);
        assert_eq!(cmd.issued_by.as_deref(), Some("admin1"));
        let items: ServerCommandsResponse = serde_json::from_str(r#"{"items":[]}"#).unwrap();
        assert!(items.items.is_empty());
    }

    #[test]
    fn ws_ack_event_pong_frames() {
        let ack = WsFrame::ack_of(Uuid::nil(), CommandAck::success_with(&ScheduledResult { scheduled_at: t(10, 20, 30) }).unwrap());
        let json = serde_json::to_string(&ack).unwrap();
        assert!(json.starts_with(r#"{"type":"ack","id":""#));
        assert!(json.ends_with(r#""payload":null,"ack":{"id":"00000000-0000-0000-0000-000000000000","ok":true,"result":{"scheduledAt":"2026-09-21T10:20:30.000Z"}}}"#));
        let back: WsFrame = serde_json::from_str(&json).unwrap();
        assert_eq!(back, ack);

        let failed = WsAck::from_ack(Uuid::nil(), CommandAck::failure(IpcError::timeout(None)));
        assert_eq!(
            serde_json::to_string(&failed).unwrap(),
            r#"{"id":"00000000-0000-0000-0000-000000000000","ok":false,"error":{"code":"timeout","message":"Operation timed out","details":null}}"#
        );
        assert_eq!(serde_json::to_string(&CommandAck::success()).unwrap(), r#"{"ok":true}"#);

        let launched = GameLaunchedEvent { session_id: Uuid::nil(), game_id: Uuid::nil(), pid: 7788, account_lease_id: None, at: t(10, 21, 0) };
        let event = AgentEvent::of(AgentEventType::GameLaunched, t(10, 21, 0), &launched).unwrap();
        assert_eq!(event.wire_name(), "gameLaunched");
        assert_eq!(
            serde_json::to_string(&event).unwrap(),
            r#"{"type":"gameLaunched","at":"2026-09-21T10:21:00.000Z","payload":{"sessionId":"00000000-0000-0000-0000-000000000000","gameId":"00000000-0000-0000-0000-000000000000","pid":7788,"at":"2026-09-21T10:21:00.000Z"}}"#
        );
        let frame = WsFrame::event_of(&event);
        assert_eq!(frame.r#type, WsFrameType::Event);
        assert_eq!(frame.name.as_deref(), Some("gameLaunched"));
        assert_eq!(frame.ts, t(10, 21, 0));
        assert_eq!(frame.payload_as::<GameLaunchedEvent>().unwrap().unwrap(), launched);

        let ping: WsFrame = serde_json::from_str(r#"{"type":"ping","id":"00000000-0000-0000-0000-000000000000","ts":"2026-09-21T10:00:00.000Z","payload":null}"#).unwrap();
        let pong = WsFrame::pong_for(&ping);
        assert_eq!(pong.r#type, WsFrameType::Pong);
        assert_eq!(pong.id, ping.id);
        assert_eq!(pong.push_kind(), None);

        let push: WsFrame = serde_json::from_str(r#"{"type":"push","id":"00000000-0000-0000-0000-000000000000","ts":"2026-09-21T10:22:00.000Z","name":"pcStatusChanged","payload":{"pcId":"00000000-0000-0000-0000-000000000000","status":"busy"}}"#).unwrap();
        assert_eq!(push.push_kind(), Some(WsPushKind::PcStatusChanged));
        let p: PcStatusChangedPush = push.payload_as().unwrap().unwrap();
        assert_eq!(p.status, PcStatus::Busy);
        assert_eq!(WsFrame::MAX_FRAME_BYTES, 1_048_576);
        assert_eq!(WsFrame::SUBPROTOCOL, "clubshell.v1");
    }

    #[test]
    fn rest_agent_payloads() {
        let hb = HeartbeatRequest {
            status: PcStatus::Busy,
            current_session_id: Some(Uuid::nil()),
            agent_version: "1.4.2".into(),
            shell_version: "1.4.2".into(),
            uptime_sec: 8123,
            ip_address: "10.0.1.12".into(),
            policy_version: 12,
            running_games: vec![HeartbeatRunningGame { game_id: Uuid::nil(), pid: 7788, started_at: t(10, 21, 0) }],
            offline_queue: 0,
            shell_connected: true,
        };
        let json = serde_json::to_string(&hb).unwrap();
        assert_eq!(
            json,
            r#"{"status":"busy","currentSessionId":"00000000-0000-0000-0000-000000000000","agentVersion":"1.4.2","shellVersion":"1.4.2","uptimeSec":8123,"ipAddress":"10.0.1.12","policyVersion":12,"runningGames":[{"gameId":"00000000-0000-0000-0000-000000000000","pid":7788,"startedAt":"2026-09-21T10:21:00.000Z"}],"offlineQueue":0,"shellConnected":true}"#
        );
        assert_eq!(serde_json::from_str::<HeartbeatRequest>(&json).unwrap(), hb);
        let res: HeartbeatResponse = serde_json::from_str(
            r#"{"serverTime":"2026-09-21T10:21:00.000Z","pcStatus":"busy","policyVersion":13,"configVersion":3,"catalogVersion":"abc","pendingCommands":2}"#,
        )
        .unwrap();
        assert_eq!(res.policy_version, 13);
        assert_eq!(res.session, None);

        let refresh = AgentRefreshResponse { access_token: "a".into(), refresh_token: "r".into(), expires_at: t(11, 0, 0), signing_secret: None };
        assert_eq!(serde_json::to_string(&refresh).unwrap(), r#"{"accessToken":"a","refreshToken":"r","expiresAt":"2026-09-21T11:00:00.000Z"}"#);
        let batch = TelemetryBatch { samples: vec![], events: vec![TelemetryEvent { kind: telemetry_event_kinds::PIPE_ERROR.into(), at: t(1, 0, 0), data: serde_json::json!({"code":109}) }], hardware: None, logs_tail: Some(vec!["warn".into()]) };
        assert_eq!(
            serde_json::to_string(&batch).unwrap(),
            r#"{"samples":[],"events":[{"kind":"pipeError","at":"2026-09-21T01:00:00.000Z","data":{"code":109}}],"logsTail":["warn"]}"#
        );
        let ticket = CallAdminTicketRequest { pc_id: Uuid::nil(), user_id: None, category: CallAdminCategory::Help, message: None, at: t(1, 0, 0) };
        assert_eq!(serde_json::to_string(&ticket).unwrap(), r#"{"pcId":"00000000-0000-0000-0000-000000000000","category":"help","at":"2026-09-21T01:00:00.000Z"}"#);
        assert_eq!(TelemetryBatch::MAX_SAMPLES, 120);
    }

    #[test]
    fn command_payloads_and_results() {
        assert!(RefreshConfigCommand::default().is_all());
        assert!(RefreshConfigCommand { config: Some(false), ..Default::default() }.is_all());
        assert!(!RefreshConfigCommand { games: Some(true), ..Default::default() }.is_all());
        assert_eq!(serde_json::to_string(&RefreshConfigCommand { games: Some(true), ..Default::default() }).unwrap(), r#"{"games":true}"#);
        let msg = MessageCommand { id: Uuid::nil(), from: "admin".into(), text: "hi".into(), level: NotificationLevel::Warning, requires_ack: true };
        assert_eq!(
            serde_json::to_string(&msg).unwrap(),
            r#"{"id":"00000000-0000-0000-0000-000000000000","from":"admin","text":"hi","level":"warning","requiresAck":true}"#
        );
        assert_eq!(serde_json::to_string(&PowerCommand { delay_sec: 30, force: false, message: None }).unwrap(), r#"{"delaySec":30,"force":false}"#);
        assert_eq!(serde_json::to_string(&KillGameCommand { game_id: None, pid: Some(1), force: true }).unwrap(), r#"{"pid":1,"force":true}"#);
        let shot = ScreenshotCommand { quality: 60, upload_url: "https://up".into(), monitor: None, max_width: Some(1280) };
        assert_eq!(serde_json::to_string(&shot).unwrap(), r#"{"quality":60,"uploadUrl":"https://up","maxWidth":1280}"#);
        let rc = RemoteControlStartCommand { session_token: "t".into(), relay_url: "wss://r".into(), fps: 5, allow_input: true, admin_name: "Bob".into() };
        assert_eq!(serde_json::to_string(&rc).unwrap(), r#"{"sessionToken":"t","relayUrl":"wss://r","fps":5,"allowInput":true,"adminName":"Bob"}"#);
        let stop = RemoteControlStopResult { stopped_at: t(10, 30, 0), duration_sec: 600 };
        assert_eq!(serde_json::to_string(&stop).unwrap(), r#"{"stoppedAt":"2026-09-21T10:30:00.000Z","durationSec":600}"#);
        let ext = ExtendSessionCommand { session_id: Uuid::nil(), minutes: 15, charge: false };
        assert_eq!(serde_json::to_string(&ext).unwrap(), r#"{"sessionId":"00000000-0000-0000-0000-000000000000","minutes":15,"charge":false}"#);
        let end = EndSessionCommand { session_id: Uuid::nil(), reason: SessionEndReason::Admin };
        assert_eq!(serde_json::to_string(&end).unwrap(), r#"{"sessionId":"00000000-0000-0000-0000-000000000000","reason":"admin"}"#);
        assert_eq!(serde_json::to_string(&SetPolicyResult { version: 13, applied: true }).unwrap(), r#"{"version":13,"applied":true}"#);
        assert_eq!(serde_json::to_string(&RefreshConfigResult { refreshed: vec!["games".into()] }).unwrap(), r#"{"refreshed":["games"]}"#);
        let flushed = OfflineQueueFlushedEvent { count: 5, deadlettered: 1, offline_from: t(9, 0, 0), offline_to: t(9, 30, 0) };
        assert_eq!(serde_json::to_string(&flushed).unwrap(), r#"{"count":5,"deadlettered":1,"offlineFrom":"2026-09-21T09:00:00.000Z","offlineTo":"2026-09-21T09:30:00.000Z"}"#);
        let err_ack = CommandAck::failure(IpcError::of(ErrorCode::PolicyDenied));
        assert!(!err_ack.ok);
        assert_eq!(serde_json::to_string(&WakeCommand { target_mac: "AA:BB:CC:DD:EE:FF".into() }).unwrap(), r#"{"targetMac":"AA:BB:CC:DD:EE:FF"}"#);
        let delivered = MessageDeliveryResult { delivered_at: t(10, 0, 0), acked_at: None };
        assert_eq!(serde_json::to_string(&delivered).unwrap(), r#"{"deliveredAt":"2026-09-21T10:00:00.000Z"}"#);
    }
}
// ---- END MANUAL ----
