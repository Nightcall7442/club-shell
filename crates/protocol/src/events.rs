//! Agent → Shell event payloads (IPC_PROTOCOL.md §8; C# `IpcMessage.cs` "Event payloads" region).
//! Payloads defined next to their domain are re-exported here so every event payload is reachable
//! from this module: [`SessionWarning`], [`SessionEndedEvent`], [`SessionEvent`], [`Notification`].
//! Names live in [`names::events`](crate::commands::names::events).

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use uuid::Uuid;

use crate::commands::{UpdateComponent, UpdateManifest, UpdatePhase};
use crate::error::IpcError;
use crate::games::GameState;
use crate::pc::{ConnectivityState, Policy};
use crate::user::{AuthExpiredReason, NotificationLevel};

// ---- BEGIN MANUAL ----
use serde::de::DeserializeOwned;

use crate::commands::{object_as, MessageCommand};
use crate::error::ProtocolError;

pub use crate::commands::names::events as event_names;
pub use crate::commands::LockCommand;
pub use crate::session::{SessionEndedEvent, SessionEvent, SessionEventType, SessionWarning};
pub use crate::user::{Notification, NotificationAction};
// ---- END MANUAL ----

wire_enum! {
    /// Remote-control state (`admin.remoteControl`).
    RemoteControlState {
        Started = "started",
        Stopped = "stopped",
    }
}

wire_enum! {
    /// UI command carried by `shell.command`.
    ShellCommandKind {
        /// Show the lock screen; args [`LockCommand`].
        Lock = "lock",
        /// Hide the lock screen; no args.
        Unlock = "unlock",
        /// Reboot notice (Agent performs the reboot); args [`ShellRebootArgs`].
        Reboot = "reboot",
        /// Play ads; args [`ShowAdsArgs`].
        ShowAds = "showAds",
        /// Show a message; args [`ShowMessageArgs`].
        ShowMessage = "showMessage",
    }
}

wire_enum! {
    /// Media type of an ad item.
    AdMediaType {
        Image = "image",
        Video = "video",
    }
}

/// Payload of `admin.message`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct AdminMessage {
    /// Ack via `sys.ackAdminMessage`.
    pub id: Uuid,
    /// Sender name.
    pub from: String,
    pub text: String,
    pub level: NotificationLevel,
    /// User must acknowledge (modal).
    pub requires_ack: bool,
    /// Receipt time.
    #[serde(with = "crate::wire::ts")]
    pub at: DateTime<Utc>,
}

// ---- BEGIN MANUAL ----
impl AdminMessage {
    /// Builds from the server command payload.
    pub fn from_command(command: &MessageCommand, at: DateTime<Utc>) -> Self {
        Self {
            id: command.id,
            from: command.from.clone(),
            text: command.text.clone(),
            level: command.level,
            requires_ack: command.requires_ack,
            at,
        }
    }
}
// ---- END MANUAL ----

/// Payload of `admin.remoteControl`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RemoteControlEvent {
    pub state: RemoteControlState,
    #[serde(with = "crate::wire::ts")]
    pub at: DateTime<Utc>,
    /// Show the on-screen indicator.
    pub show_indicator: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub admin_name: Option<String>,
}

/// Payload of `game.stateChanged`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct GameStateChanged {
    pub game_id: Uuid,
    pub title: String,
    pub state: GameState,
    #[serde(with = "crate::wire::ts")]
    pub at: DateTime<Utc>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pid: Option<i32>,
    /// Exit code (`exited`).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub exit_code: Option<i32>,
    /// Failure (`failed`).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<IpcError>,
}

/// Payload of `policy.changed`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct PolicyChanged {
    pub version: i32,
    #[serde(with = "crate::wire::ts")]
    pub updated_at: DateTime<Utc>,
    /// Top-level sections that changed.
    pub changed: Vec<String>,
    pub policy: Policy,
}

/// Payload of `update.available`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct UpdateAvailable {
    pub manifest: UpdateManifest,
    /// Installed version of that component.
    pub current: String,
}

/// Payload of `update.progress` (≤ 2/s).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct UpdateProgress {
    pub component: UpdateComponent,
    pub version: String,
    pub phase: UpdatePhase,
    /// 0–100.
    pub percent: i32,
    pub bytes_done: i64,
    pub bytes_total: i64,
    /// Failure (`failed`).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<IpcError>,
}

/// Payload of `update.ready` (package staged and verified).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct UpdateReady {
    pub component: UpdateComponent,
    pub version: String,
    /// Whether applying restarts the component.
    pub restart_required: bool,
    /// Will be applied even during a session.
    pub mandatory: bool,
    #[serde(default, with = "crate::wire::ts_opt", skip_serializing_if = "Option::is_none")]
    pub apply_at: Option<DateTime<Utc>>,
}

/// Payload of `sys.connectivity` (transitions + every 60 s).
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ConnectivityEvent {
    pub state: ConnectivityState,
    /// When the state was entered.
    #[serde(with = "crate::wire::ts")]
    pub since: DateTime<Utc>,
    /// Outbox size.
    pub queued_events: i32,
    /// Last heartbeat latency, when online.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub server_latency_ms: Option<i32>,
}

/// Args of [`ShellCommandKind::Reboot`] (informational; the Agent performs the reboot).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ShellRebootArgs {
    pub delay_sec: i32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub message: Option<String>,
}

/// One ad in a [`ShowAdsArgs`] playlist.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct AdItem {
    pub url: String,
    pub r#type: AdMediaType,
    pub duration_sec: i32,
}

/// Args of [`ShellCommandKind::ShowAds`] and payload of the `showAds` server command.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ShowAdsArgs {
    pub items: Vec<AdItem>,
    pub skippable: bool,
}

/// Args of [`ShellCommandKind::ShowMessage`].
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ShowMessageArgs {
    pub title: String,
    pub body: String,
    pub level: NotificationLevel,
    /// Auto-dismiss after this many seconds.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ttl_sec: Option<i32>,
}

/// Payload of `shell.command`. `args` is always present on the wire (`null` when the command has none).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ShellCommand {
    pub command: ShellCommandKind,
    /// Typed args (see [`ShellCommandKind`]) or `None`.
    pub args: Option<Value>,
    /// Originating server command id (or a fresh id for Agent-originated commands).
    pub command_id: Uuid,
}

// ---- BEGIN MANUAL ----
impl ShellCommand {
    /// Command with typed args.
    pub fn of<T: Serialize>(command: ShellCommandKind, args: Option<&T>, command_id: Uuid) -> Result<Self, ProtocolError> {
        let args = match args {
            Some(a) => Some(serde_json::to_value(a)?),
            None => None,
        };
        Ok(Self { command, args, command_id })
    }

    /// Deserializes `args` as `T`; `Ok(None)` when absent or not an object.
    pub fn args_as<T: DeserializeOwned>(&self) -> Result<Option<T>, ProtocolError> {
        object_as(self.args.as_ref())
    }
}
// ---- END MANUAL ----

/// Payload of `auth.expired`; the Shell returns to the login screen.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct AuthExpired {
    pub reason: AuthExpiredReason,
}

// ---- BEGIN MANUAL ----
#[cfg(test)]
mod tests {
    use super::*;
    use crate::commands::{UpdateChannel, UpdateManifest};
    use crate::error::{assert_wire, ErrorCode};
    use chrono::TimeZone;

    fn t(h: u32, m: u32, s: u32) -> DateTime<Utc> {
        Utc.with_ymd_and_hms(2026, 9, 21, h, m, s).unwrap()
    }

    #[test]
    fn enum_wire_values() {
        assert_wire(RemoteControlState::ALL, &["started", "stopped"]);
        assert_wire(ShellCommandKind::ALL, &["lock", "unlock", "reboot", "showAds", "showMessage"]);
        assert_wire(AdMediaType::ALL, &["image", "video"]);
    }

    #[test]
    fn shell_command_keeps_null_args() {
        let cmd = ShellCommand::of::<LockCommand>(ShellCommandKind::Unlock, None, Uuid::nil()).unwrap();
        assert_eq!(
            serde_json::to_string(&cmd).unwrap(),
            r#"{"command":"unlock","args":null,"commandId":"00000000-0000-0000-0000-000000000000"}"#
        );
        assert_eq!(cmd.args_as::<LockCommand>().unwrap(), None);
        let lock = LockCommand { reason: Some("admin".into()), message: Some("Please come to the desk".into()) };
        let cmd = ShellCommand::of(ShellCommandKind::Lock, Some(&lock), Uuid::nil()).unwrap();
        let json = serde_json::to_string(&cmd).unwrap();
        assert_eq!(
            json,
            r#"{"command":"lock","args":{"reason":"admin","message":"Please come to the desk"},"commandId":"00000000-0000-0000-0000-000000000000"}"#
        );
        let back: ShellCommand = serde_json::from_str(&json).unwrap();
        assert_eq!(back.args_as::<LockCommand>().unwrap(), Some(lock));
        let ads = ShowAdsArgs { items: vec![AdItem { url: "https://a.mp4".into(), r#type: AdMediaType::Video, duration_sec: 15 }], skippable: true };
        assert_eq!(serde_json::to_string(&ads).unwrap(), r#"{"items":[{"url":"https://a.mp4","type":"video","durationSec":15}],"skippable":true}"#);
        let msg = ShowMessageArgs { title: "t".into(), body: "b".into(), level: NotificationLevel::Info, ttl_sec: None };
        assert_eq!(serde_json::to_string(&msg).unwrap(), r#"{"title":"t","body":"b","level":"info"}"#);
        assert_eq!(serde_json::to_string(&ShellRebootArgs { delay_sec: 30, message: None }).unwrap(), r#"{"delaySec":30}"#);
    }

    #[test]
    fn admin_and_remote_control_events() {
        let cmd = MessageCommand { id: Uuid::nil(), from: "Admin".into(), text: "hi".into(), level: NotificationLevel::Warning, requires_ack: true };
        let m = AdminMessage::from_command(&cmd, t(10, 0, 0));
        let json = serde_json::to_string(&m).unwrap();
        assert_eq!(
            json,
            r#"{"id":"00000000-0000-0000-0000-000000000000","from":"Admin","text":"hi","level":"warning","requiresAck":true,"at":"2026-09-21T10:00:00.000Z"}"#
        );
        assert_eq!(serde_json::from_str::<AdminMessage>(&json).unwrap(), m);
        let rc = RemoteControlEvent { state: RemoteControlState::Started, at: t(10, 0, 0), show_indicator: true, admin_name: Some("Bob".into()) };
        assert_eq!(
            serde_json::to_string(&rc).unwrap(),
            r#"{"state":"started","at":"2026-09-21T10:00:00.000Z","showIndicator":true,"adminName":"Bob"}"#
        );
        assert_eq!(serde_json::to_string(&AuthExpired { reason: AuthExpiredReason::Revoked }).unwrap(), r#"{"reason":"revoked"}"#);
        let conn = ConnectivityEvent { state: ConnectivityState::Offline, since: t(9, 0, 0), queued_events: 4, server_latency_ms: None };
        assert_eq!(serde_json::to_string(&conn).unwrap(), r#"{"state":"offline","since":"2026-09-21T09:00:00.000Z","queuedEvents":4}"#);
    }

    #[test]
    fn game_policy_update_events() {
        let g = GameStateChanged { game_id: Uuid::nil(), title: "CS2".into(), state: GameState::Failed, at: t(10, 21, 0), pid: None, exit_code: None, error: Some(IpcError::of(ErrorCode::GameNotInstalled)) };
        let json = serde_json::to_string(&g).unwrap();
        assert_eq!(
            json,
            r#"{"gameId":"00000000-0000-0000-0000-000000000000","title":"CS2","state":"failed","at":"2026-09-21T10:21:00.000Z","error":{"code":"gameNotInstalled","message":"Game is not installed","details":null}}"#
        );
        assert_eq!(serde_json::from_str::<GameStateChanged>(&json).unwrap(), g);

        let p = PolicyChanged { version: 12, updated_at: t(8, 0, 0), changed: vec!["kiosk".into()], policy: crate::pc::sample_policy() };
        let json = serde_json::to_string(&p).unwrap();
        assert!(json.starts_with(r#"{"version":12,"updatedAt":"2026-09-21T08:00:00.000Z","changed":["kiosk"],"policy":{"version":12"#));
        assert_eq!(serde_json::from_str::<PolicyChanged>(&json).unwrap(), p);

        let manifest = UpdateManifest {
            channel: UpdateChannel::Beta,
            component: UpdateComponent::Agent,
            version: "1.5.0".into(),
            url: "https://u".into(),
            sha256: "ab".into(),
            size: 1,
            signature: "s".into(),
            release_notes: String::new(),
            mandatory: true,
            published_at: t(3, 0, 0),
            min_agent_version: None,
        };
        let avail = UpdateAvailable { manifest, current: "1.4.2".into() };
        let json = serde_json::to_string(&avail).unwrap();
        assert!(json.starts_with(r#"{"manifest":{"channel":"beta","component":"agent","version":"1.5.0""#));
        assert!(json.ends_with(r#""publishedAt":"2026-09-21T03:00:00.000Z"},"current":"1.4.2"}"#));
        let progress = UpdateProgress { component: UpdateComponent::Agent, version: "1.5.0".into(), phase: UpdatePhase::Downloading, percent: 42, bytes_done: 4200, bytes_total: 10000, error: None };
        assert_eq!(
            serde_json::to_string(&progress).unwrap(),
            r#"{"component":"agent","version":"1.5.0","phase":"downloading","percent":42,"bytesDone":4200,"bytesTotal":10000}"#
        );
        let ready = UpdateReady { component: UpdateComponent::Shell, version: "1.5.0".into(), restart_required: true, mandatory: false, apply_at: Some(t(4, 0, 0)) };
        assert_eq!(
            serde_json::to_string(&ready).unwrap(),
            r#"{"component":"shell","version":"1.5.0","restartRequired":true,"mandatory":false,"applyAt":"2026-09-21T04:00:00.000Z"}"#
        );
        assert_eq!(event_names::ALL.len(), 18);
    }
}
// ---- END MANUAL ----
