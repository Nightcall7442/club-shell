//! Agent → webview fan-out (`TAURI_COMMANDS.md` §3): every IPC event becomes the Tauri event
//! `agent://<name>` with the same payload, pipe state changes become `kiosk://connectivity`, and a
//! few events also update [`AppState`] caches / the kiosk seam before they are forwarded.

use std::time::{Duration, Instant};

use clubshell_protocol::commands::names::events as ev;
use clubshell_protocol::events::{ConnectivityEvent, GameStateChanged, ShellCommand, ShellCommandKind};
use clubshell_protocol::games::GameState;
use clubshell_protocol::ipc::IpcEnvelope;
use clubshell_protocol::session::Session;
use serde_json::Value;
use tauri::{AppHandle, Emitter};
use tokio::sync::broadcast::error::RecvError;

use super::ConnectivityStatus;
use crate::state::AppState;

/// Minimum interval between two forwarded `sys.metrics` events.
pub const METRICS_MIN_INTERVAL: Duration = Duration::from_secs(1);

/// Tauri event name for an IPC event name.
pub fn agent_event_name(ipc_name: &str) -> String {
    format!("agent://{ipc_name}")
}

/// `kiosk://connectivity` event name.
pub const CONNECTIVITY_EVENT: &str = "kiosk://connectivity";

/// Background task: `state.agent.events()` + `state.agent.state()` → webview events.
pub struct EventForwarder;

impl EventForwarder {
    /// Spawns the forwarder on the Tauri runtime. `state` must be the same handle that was passed to
    /// `tauri::Builder::manage`. The task ends when the agent's event channel closes.
    pub fn spawn(app: AppHandle, state: AppState) -> tauri::async_runtime::JoinHandle<()> {
        tauri::async_runtime::spawn(async move {
            let mut events = state.agent.events();
            let mut link = state.agent.state();
            let mut filter = EventFilter::default();
            // Publish the current pipe state once so a late-listening webview still learns it.
            emit_connectivity(&app, &link.borrow_and_update().clone());
            loop {
                tokio::select! {
                    next = events.recv() => match next {
                        Ok(env) => forward(&app, &state, &mut filter, env),
                        Err(RecvError::Lagged(n)) => tracing::warn!(skipped = n, "webview event forwarding lagged"),
                        Err(RecvError::Closed) => break,
                    },
                    changed = link.changed() => {
                        if changed.is_err() {
                            break;
                        }
                        emit_connectivity(&app, &link.borrow_and_update().clone());
                    }
                }
            }
            tracing::info!("event forwarder stopped");
        })
    }
}

/// Per-event rate limiting state.
#[derive(Default)]
struct EventFilter {
    last_metrics: Option<Instant>,
}

impl EventFilter {
    /// `true` when a `sys.metrics` event may be forwarded now.
    fn allow_metrics(&mut self) -> bool {
        let now = Instant::now();
        if self.last_metrics.is_some_and(|t| now.duration_since(t) < METRICS_MIN_INTERVAL) {
            return false;
        }
        self.last_metrics = Some(now);
        true
    }
}

fn emit_connectivity(app: &AppHandle, status: &ConnectivityStatus) {
    if let Err(e) = app.emit(CONNECTIVITY_EVENT, status) {
        tracing::warn!(error = %e, "cannot emit kiosk://connectivity");
    }
}

/// Applies the side effects of one Agent event and forwards it as `agent://<name>`.
fn forward(app: &AppHandle, state: &AppState, filter: &mut EventFilter, env: IpcEnvelope) {
    if !ev::ALL.contains(&env.name.as_str()) {
        tracing::debug!(name = %env.name, "unknown agent event ignored");
        return;
    }
    let payload = env.payload.unwrap_or(Value::Null);
    match env.name.as_str() {
        ev::SESSION_UPDATED => match serde_json::from_value::<Session>(payload.clone()) {
            Ok(session) => state.set_session(Some(session)),
            Err(e) => tracing::warn!(error = %e, "session.updated payload not a Session"),
        },
        ev::SESSION_ENDED => {
            state.set_session(None);
            state.set_game_running(false);
        }
        ev::AUTH_EXPIRED => {
            state.set_user(None);
            state.set_session(None);
            state.clear_admin_unlock();
        }
        ev::SYS_CONNECTIVITY => match serde_json::from_value::<ConnectivityEvent>(payload.clone()) {
            Ok(connectivity) => {
                let _ = state.connectivity.send_replace(Some(connectivity));
            }
            Err(e) => tracing::warn!(error = %e, "sys.connectivity payload malformed"),
        },
        ev::SYS_METRICS => {
            if !filter.allow_metrics() {
                return;
            }
        }
        ev::GAME_STATE_CHANGED => match serde_json::from_value::<GameStateChanged>(payload.clone()) {
            Ok(change) => {
                let running = matches!(change.state, GameState::Launching | GameState::Running);
                if state.game_running() != running {
                    state.set_game_running(running);
                    state.kiosk().set_game_mode(running);
                }
                tracing::info!(game = %change.title, state = %change.state, pid = ?change.pid, exit_code = ?change.exit_code, "game state changed");
            }
            Err(e) => tracing::warn!(error = %e, "game.stateChanged payload malformed"),
        },
        ev::SHELL_COMMAND => match serde_json::from_value::<ShellCommand>(payload.clone()) {
            Ok(command) => {
                tracing::info!(command = %command.command, id = %command.command_id, "shell command");
                if state.config.kiosk.overlay_on_lock {
                    match command.command {
                        ShellCommandKind::Lock => state.kiosk().set_locked(true),
                        ShellCommandKind::Unlock => state.kiosk().set_locked(false),
                        _ => {}
                    }
                }
            }
            Err(e) => tracing::warn!(error = %e, "shell.command payload malformed"),
        },
        _ => {}
    }
    if let Err(e) = app.emit(&agent_event_name(&env.name), payload) {
        tracing::warn!(name = %env.name, error = %e, "cannot emit agent event to webview");
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn metrics_are_rate_limited_to_one_per_second() {
        let mut f = EventFilter::default();
        assert!(f.allow_metrics());
        assert!(!f.allow_metrics());
        f.last_metrics = Some(Instant::now() - METRICS_MIN_INTERVAL - Duration::from_millis(1));
        assert!(f.allow_metrics());
    }

    #[test]
    fn event_names_are_mirrored() {
        assert_eq!(agent_event_name(ev::SESSION_UPDATED), "agent://session.updated");
        assert_eq!(ev::ALL.len(), 18);
    }
}
