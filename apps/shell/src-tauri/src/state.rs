//! Shared Tauri state ([`AppState`]), the command error type ([`ShellError`], `TAURI_COMMANDS.md`
//! §1.1) and the [`KioskControl`] seam through which the kiosk module is driven by agent events.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

use chrono::{DateTime, Utc};
use clubshell_protocol::error::{ErrorCode, IpcError, ProtocolError};
use clubshell_protocol::events::ConnectivityEvent;
use clubshell_protocol::session::Session;
use clubshell_protocol::user::User;
use clubshell_winutil::WinUtilError;
use parking_lot::{Mutex, RwLock};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use tokio::sync::watch;

use crate::agent::AgentClient;
use crate::config::{ConfigError, ShellConfig};

// ───────────────────────────── ShellError ─────────────────────────────

/// Who produced a [`ShellError`] (`TAURI_COMMANDS.md` §1.1).
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum ErrorSource {
    /// The Agent answered with an error envelope.
    Ipc,
    /// Transport failure: `agentOffline` | `timeout` | `protocolError`.
    Pipe,
    /// Rust-side validation / OS failure: `validation` | `internal` | `forbidden` | …
    Tauri,
    /// Produced by the mock transport (dev).
    Mock,
}

/// Error every `#[tauri::command]` rejects with; serializes as
/// `{ code, message, details, source }` with `details` always present (`null` when empty).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ShellError {
    pub code: ErrorCode,
    pub message: String,
    pub details: Option<Value>,
    pub source: ErrorSource,
}

/// Result of every Tauri command.
pub type CmdResult<T> = Result<T, ShellError>;

impl ShellError {
    pub fn new(code: ErrorCode, message: impl Into<String>, source: ErrorSource) -> Self {
        Self {
            code,
            message: message.into(),
            details: None,
            source,
        }
    }

    pub fn with_details(mut self, details: Value) -> Self {
        self.details = Some(details);
        self
    }

    /// `agentOffline` from the pipe (not connected, hello pending too long, connection dropped).
    pub fn agent_offline() -> Self {
        Self::new(
            ErrorCode::AgentOffline,
            "Agent is not connected",
            ErrorSource::Pipe,
        )
    }

    /// `timeout` from the pipe (no response within the request timeout).
    pub fn timeout() -> Self {
        Self::new(
            ErrorCode::Timeout,
            "Agent did not respond in time",
            ErrorSource::Pipe,
        )
    }

    /// `protocolError` from the pipe (bad frame / unexpected payload shape).
    pub fn protocol(message: impl Into<String>) -> Self {
        Self::new(ErrorCode::ProtocolError, message, ErrorSource::Pipe)
    }

    /// `internal` raised in Rust.
    pub fn internal(message: impl Into<String>) -> Self {
        Self::new(ErrorCode::Internal, message, ErrorSource::Tauri)
    }

    /// `validation` raised in Rust before the pipe is touched; `details: { field, reason }`.
    pub fn validation(field: &str, reason: &str) -> Self {
        Self::new(
            ErrorCode::Validation,
            format!("{field}: {reason}"),
            ErrorSource::Tauri,
        )
        .with_details(json!({ "field": field, "reason": reason }))
    }

    pub fn forbidden(message: impl Into<String>) -> Self {
        Self::new(ErrorCode::Forbidden, message, ErrorSource::Tauri)
    }

    pub fn unauthorized(message: impl Into<String>) -> Self {
        Self::new(ErrorCode::Unauthorized, message, ErrorSource::Tauri)
    }

    pub fn not_found(what: &str) -> Self {
        Self::new(
            ErrorCode::NotFound,
            format!("{what} not found"),
            ErrorSource::Tauri,
        )
    }

    /// `policyDenied` raised in Rust; `details: { rule }`.
    pub fn policy_denied(rule: &str) -> Self {
        Self::new(
            ErrorCode::PolicyDenied,
            format!("Blocked by policy: {rule}"),
            ErrorSource::Tauri,
        )
        .with_details(json!({ "rule": rule }))
    }

    /// `internal` for a Windows-only feature on another platform.
    pub fn unsupported() -> Self {
        Self::internal("not supported on this platform")
    }

    /// Error produced by the mock transport.
    pub fn mock(code: ErrorCode, message: impl Into<String>) -> Self {
        Self::new(code, message, ErrorSource::Mock)
    }

    pub fn is_retryable(&self) -> bool {
        self.code.is_retryable()
    }
}

impl std::fmt::Display for ShellError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}: {} ({:?})", self.code, self.message, self.source)
    }
}

impl std::error::Error for ShellError {}

impl From<IpcError> for ShellError {
    fn from(e: IpcError) -> Self {
        Self {
            code: e.code,
            message: e.message,
            details: e.details,
            source: ErrorSource::Ipc,
        }
    }
}

impl From<WinUtilError> for ShellError {
    fn from(e: WinUtilError) -> Self {
        match e {
            WinUtilError::Ipc(err) => Self::from(*err),
            WinUtilError::Closed => Self::agent_offline(),
            WinUtilError::Timeout => Self::timeout(),
            WinUtilError::Io(io) => Self::new(
                ErrorCode::AgentOffline,
                format!("pipe I/O error: {io}"),
                ErrorSource::Pipe,
            ),
            WinUtilError::Frame(p) => Self::protocol(p.to_string()),
            WinUtilError::Unsupported => Self::unsupported(),
            WinUtilError::Invalid(reason) => Self::validation("argument", &reason),
            WinUtilError::Busy(what) => Self::new(
                ErrorCode::Conflict,
                format!("{what} is already installed"),
                ErrorSource::Tauri,
            ),
            win32 @ WinUtilError::Win32 { .. } => Self::internal(win32.to_string()),
        }
    }
}

impl From<ProtocolError> for ShellError {
    fn from(e: ProtocolError) -> Self {
        Self::protocol(e.to_string())
    }
}

impl From<serde_json::Error> for ShellError {
    fn from(e: serde_json::Error) -> Self {
        Self::protocol(format!("JSON error: {e}"))
    }
}

impl From<anyhow::Error> for ShellError {
    fn from(e: anyhow::Error) -> Self {
        Self::internal(format!("{e:#}"))
    }
}

impl From<tauri::Error> for ShellError {
    fn from(e: tauri::Error) -> Self {
        Self::internal(e.to_string())
    }
}

impl From<ConfigError> for ShellError {
    fn from(e: ConfigError) -> Self {
        Self::internal(e.to_string())
    }
}

// ───────────────────────────── Kiosk seam ─────────────────────────────

/// What the agent event pipeline needs from the kiosk module (implemented in `kiosk/`).
pub trait KioskControl: Send + Sync {
    /// `shell.command{lock|unlock}`: show/hide the native lock overlay (when `kiosk.overlayOnLock`).
    fn set_locked(&self, locked: bool);
    /// A game is starting/running (`true`: suspend the foreground guard) or has exited
    /// (`false`: re-arm the guard and re-focus the shell window).
    fn set_game_mode(&self, on: bool);
    /// Drop hooks/guards before the process exits.
    fn shutdown(&self);
}

/// Placeholder until the kiosk module installs the real implementation (also used on non-Windows
/// and in tests).
#[derive(Debug, Default, Clone, Copy)]
pub struct NoopKiosk;

impl KioskControl for NoopKiosk {
    fn set_locked(&self, locked: bool) {
        tracing::debug!(locked, "kiosk control not installed; lock ignored");
    }

    fn set_game_mode(&self, on: bool) {
        tracing::debug!(on, "kiosk control not installed; game mode ignored");
    }

    fn shutdown(&self) {}
}

// ───────────────────────────── AppState ─────────────────────────────

/// Cached `sys.unlockAdmin` result (used by `kiosk_exit`, `kiosk_set_guard`, `kiosk_set_fullscreen`).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct AdminUnlock {
    pub token: String,
    pub expires_at: DateTime<Utc>,
}

/// Everything commands and background tasks share. Managed with `tauri::Builder::manage(state)` and
/// read through `tauri::State<'_, AppState>`; it is a cheap `Clone` handle (one `Arc` inside) that
/// derefs to [`AppStateInner`], so `state.config`, `state.agent`, `state.session_cache` work directly.
#[derive(Clone)]
pub struct AppState {
    inner: Arc<AppStateInner>,
}

/// Fields behind [`AppState`].
pub struct AppStateInner {
    pub config: ShellConfig,
    pub agent: Arc<AgentClient>,
    /// Last `Session` seen (`session.updated` events, `session_*` command results); `None` after
    /// `session.ended` / `auth.expired`.
    pub session_cache: RwLock<Option<Session>>,
    /// Last `User` seen (`auth.login` / `auth.status` results); `None` after `auth.expired`.
    pub user_cache: RwLock<Option<User>>,
    /// Agent ↔ Server link as last reported by `sys.connectivity` (`None` until the first event).
    /// The Shell ↔ Agent pipe state is `agent.state()`.
    pub connectivity: watch::Sender<Option<ConnectivityEvent>>,
    /// Cached `sys.unlockAdmin` result.
    pub admin_unlock: Mutex<Option<AdminUnlock>>,
    /// Serializes `games_launch` / `apps_launch` (`TAURI_COMMANDS.md` §1 "Concurrency").
    pub launch_lock: tokio::sync::Mutex<()>,
    /// `true` between `game.stateChanged{launching|running}` and `{exited|failed|killed}`.
    pub game_running: AtomicBool,
    kiosk: RwLock<Arc<dyn KioskControl>>,
}

impl std::ops::Deref for AppState {
    type Target = AppStateInner;

    fn deref(&self) -> &AppStateInner {
        &self.inner
    }
}

impl AppState {
    /// State with a [`NoopKiosk`]; the kiosk module installs itself with [`set_kiosk`](Self::set_kiosk).
    pub fn new(config: ShellConfig, agent: Arc<AgentClient>) -> Self {
        let (connectivity, _) = watch::channel(None);
        Self {
            inner: Arc::new(AppStateInner {
                config,
                agent,
                session_cache: RwLock::new(None),
                user_cache: RwLock::new(None),
                connectivity,
                admin_unlock: Mutex::new(None),
                launch_lock: tokio::sync::Mutex::new(()),
                game_running: AtomicBool::new(false),
                kiosk: RwLock::new(Arc::new(NoopKiosk)),
            }),
        }
    }

    pub fn kiosk(&self) -> Arc<dyn KioskControl> {
        Arc::clone(&self.kiosk.read())
    }

    pub fn set_kiosk(&self, kiosk: Arc<dyn KioskControl>) {
        *self.kiosk.write() = kiosk;
    }

    pub fn session(&self) -> Option<Session> {
        self.session_cache.read().clone()
    }

    pub fn set_session(&self, session: Option<Session>) {
        *self.session_cache.write() = session;
    }

    pub fn user(&self) -> Option<User> {
        self.user_cache.read().clone()
    }

    pub fn set_user(&self, user: Option<User>) {
        *self.user_cache.write() = user;
    }

    /// Last known Agent ↔ Server connectivity.
    pub fn server_connectivity(&self) -> Option<ConnectivityEvent> {
        *self.connectivity.borrow()
    }

    pub fn game_running(&self) -> bool {
        self.game_running.load(Ordering::Acquire)
    }

    pub fn set_game_running(&self, running: bool) {
        self.game_running.store(running, Ordering::Release);
    }

    pub fn set_admin_unlock(&self, token: String, expires_at: DateTime<Utc>) {
        *self.admin_unlock.lock() = Some(AdminUnlock { token, expires_at });
    }

    pub fn clear_admin_unlock(&self) {
        *self.admin_unlock.lock() = None;
    }

    /// `true` while an unexpired admin unlock is cached.
    pub fn has_admin_unlock(&self) -> bool {
        self.admin_unlock
            .lock()
            .as_ref()
            .is_some_and(|a| a.expires_at > Utc::now())
    }

    /// `true` when `token` equals the cached, unexpired admin token.
    pub fn admin_token_valid(&self, token: &str) -> bool {
        self.admin_unlock
            .lock()
            .as_ref()
            .is_some_and(|a| a.expires_at > Utc::now() && a.token == token)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn shell_error_wire_shape_and_mapping() {
        let e = ShellError::validation("password", "required");
        let json = serde_json::to_value(e).unwrap();
        assert_eq!(
            json,
            json!({ "code": "validation", "message": "password: required", "details": { "field": "password", "reason": "required" }, "source": "tauri" })
        );
        let json = serde_json::to_value(ShellError::agent_offline()).unwrap();
        assert_eq!(json["code"], "agentOffline");
        assert_eq!(
            json["details"],
            Value::Null,
            "details key is always present"
        );
        assert_eq!(json["source"], "pipe");

        assert_eq!(
            ShellError::from(WinUtilError::Timeout).code,
            ErrorCode::Timeout
        );
        assert_eq!(
            ShellError::from(WinUtilError::Closed).code,
            ErrorCode::AgentOffline
        );
        let ipc: ShellError = WinUtilError::from(IpcError::session_not_active()).into();
        assert_eq!(
            (ipc.code, ipc.source),
            (ErrorCode::SessionNotActive, ErrorSource::Ipc)
        );
        assert_eq!(
            ShellError::from(WinUtilError::Unsupported).code,
            ErrorCode::Internal
        );
        assert_eq!(ShellError::from(anyhow::anyhow!("boom")).message, "boom");
    }

    #[tokio::test]
    async fn admin_unlock_expiry() {
        let cfg = ShellConfig::defaults();
        let state = AppState::new(cfg.clone(), AgentClient::mock(&cfg));
        assert!(!state.has_admin_unlock());
        state.set_admin_unlock("t".into(), Utc::now() + chrono::Duration::seconds(60));
        assert!(state.admin_token_valid("t"));
        assert!(!state.admin_token_valid("x"));
        state.set_admin_unlock("t".into(), Utc::now() - chrono::Duration::seconds(1));
        assert!(!state.has_admin_unlock());
        assert!(state.session().is_none());
        assert_eq!(state.server_connectivity(), None);
    }
}
