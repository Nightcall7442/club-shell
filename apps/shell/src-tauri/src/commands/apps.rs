//! `apps_*` commands (`TAURI_COMMANDS.md` §2.4) → `apps.*` IPC messages. `apps_launch` shares
//! `AppState::launch_lock` with `games_launch` (one launch at a time).
//!
//! SHELL_CHANGES 87: a program the Agent starts opens under the Shell, and the foreground guard takes the screen back
//! within 250 ms. So `apps_launch` gives Windows' leave while the Shell still owns the foreground, hands the
//! foreground off to the program like the dock does, and brings its window up in the background
//! (`kiosk::front::bring_app`); the command answers at once.
//!
//! An app that runs already is not started a second time: the Agent answers `alreadyRunning` and its window comes to
//! the front (`kiosk::front::front_running_app`). Only when it has no window (it lives in the tray) is it started
//! again (`force`), which makes such an app show one. `apps_kill` asks the app's windows to close, then has the Agent
//! end what is left of it.

use std::sync::Arc;
use std::time::{Duration, Instant};

use clubshell_protocol::commands::{
    names, AppsKillRequest, AppsLaunchRequest, AppsLaunchResponse, AppsListResponse,
    AppsRunningResponse, GamesKillResponse, RunningApp,
};
use clubshell_protocol::error::ErrorCode;
use clubshell_protocol::games::App;
use tauri::State;
use uuid::Uuid;

use super::validate;
use crate::kiosk::{front, open_windows, Kiosk};
use crate::state::{AppState, CmdResult, ShellError};

const ARGS_MAX: usize = 512;
/// How long an app's windows get to close on their own (`WM_CLOSE`) before the Agent ends its processes.
const CLOSE_GRACE: Duration = Duration::from_secs(3);
const CLOSE_POLL: Duration = Duration::from_millis(200);

/// `apps_list` → `apps.list` (unwrapped `items`).
#[tauri::command]
pub async fn apps_list(state: State<'_, AppState>) -> CmdResult<Vec<App>> {
    let resp: AppsListResponse = state.agent.request(names::apps::LIST, &()).await?;
    Ok(resp.items)
}

/// `apps_running` → `apps.running` (unwrapped `items`).
#[tauri::command]
pub async fn apps_running(state: State<'_, AppState>) -> CmdResult<Vec<RunningApp>> {
    let resp: AppsRunningResponse = state.agent.request(names::apps::RUNNING, &()).await?;
    Ok(resp.items)
}

/// `apps_launch` → `apps.launch`; a running app is brought to the front instead (`alreadyRunning: true`).
#[tauri::command]
pub async fn apps_launch(
    state: State<'_, AppState>,
    kiosk: State<'_, Arc<Kiosk>>,
    app_id: Uuid,
    args: Option<String>,
) -> CmdResult<AppsLaunchResponse> {
    if app_id.is_nil() {
        return Err(ShellError::validation("appId", "required"));
    }
    let args = validate::optional_text("args", args, ARGS_MAX)?;
    let _launching = state.launch_lock.lock().await;
    // The player just clicked the tile: the Shell owns the foreground and may give it away.
    if !clubshell_winutil::window::allow_set_foreground(None) {
        tracing::debug!("AllowSetForegroundWindow(ASFW_ANY) refused: the Shell is not in front");
    }
    let before = front::current_windows().await;
    let request = |force: Option<bool>| AppsLaunchRequest {
        app_id,
        args: args.clone(),
        force,
    };
    let mut resp: AppsLaunchResponse = state
        .agent
        .request(names::apps::LAUNCH, &request(None))
        .await?;
    if resp.already_running {
        let family = family_of(&resp);
        if kiosk.is_game_mode() || front::front_running_app(Arc::clone(&kiosk), family).await {
            tracing::info!(app_id = %app_id, pid = resp.pid, "app already running; brought to the front");
            return Ok(resp);
        }
        // Running without a window: in the tray. Started again, it shows its window.
        tracing::info!(app_id = %app_id, pid = resp.pid, "app running without a window; started again");
        let again: AppsLaunchResponse = state
            .agent
            .request(names::apps::LAUNCH, &request(Some(true)))
            .await?;
        let mut pids = resp.pids.clone().unwrap_or_default();
        for p in again.pids.iter().flatten() {
            if !pids.contains(p) {
                pids.push(*p);
            }
        }
        resp = AppsLaunchResponse {
            pids: Some(pids),
            already_running: true,
            ..again
        };
    }
    tracing::info!(app_id = %app_id, pid = resp.pid, "app launched");
    if let Ok(pid) = u32::try_from(resp.pid) {
        if pid != 0 && !kiosk.is_game_mode() {
            front::bring_app(Arc::clone(&kiosk), pid, family_of(&resp), before);
        }
    }
    Ok(resp)
}

/// `apps_kill` → `apps.kill`: "Close" on a running app. Its windows are asked to close first (up to
/// [`CLOSE_GRACE`]); the Agent then ends every process of the app that is left (never the Shell's, the
/// Agent's or a game's). When the app was the program in front, the Shell takes the screen back.
#[tauri::command]
pub async fn apps_kill(
    state: State<'_, AppState>,
    kiosk: State<'_, Arc<Kiosk>>,
    app_id: Uuid,
) -> CmdResult<GamesKillResponse> {
    if app_id.is_nil() {
        return Err(ShellError::validation("appId", "required"));
    }
    let running: AppsRunningResponse = state.agent.request(names::apps::RUNNING, &()).await?;
    let own = std::process::id();
    let pids: Vec<u32> = running
        .items
        .iter()
        .find(|a| a.app_id == app_id)
        .map(|a| {
            a.pids
                .iter()
                .filter_map(|&p| u32::try_from(p).ok())
                .filter(|&p| p != 0 && p != own)
                .collect()
        })
        .unwrap_or_default();
    let mut asked = 0;
    if !pids.is_empty() {
        let targets = pids.clone();
        asked = tauri::async_runtime::spawn_blocking(move || {
            open_windows::close_program_windows(&targets)
        })
        .await
        .unwrap_or(0);
        let started = Instant::now();
        while asked > 0 && started.elapsed() < CLOSE_GRACE {
            tokio::time::sleep(CLOSE_POLL).await;
            let targets = pids.clone();
            let open = tauri::async_runtime::spawn_blocking(move || {
                open_windows::has_program_window(&targets)
            })
            .await
            .unwrap_or(false);
            if !open {
                break;
            }
        }
    }
    let resp = match state
        .agent
        .request::<_, GamesKillResponse>(names::apps::KILL, &AppsKillRequest { app_id })
        .await
    {
        Ok(resp) => resp,
        // Everything closed on its own already.
        Err(e) if e.code == ErrorCode::NotFound && asked > 0 => GamesKillResponse {
            killed: 0,
            pids: Vec::new(),
        },
        Err(e) => return Err(e),
    };
    tracing::info!(app_id = %app_id, windows = asked, killed = resp.killed, "app closed");
    if kiosk
        .window()
        .hand_off_pid()
        .is_some_and(|p| pids.contains(&p))
    {
        kiosk.end_hand_off();
        let _ = kiosk.window().focus();
    }
    Ok(resp)
}

/// Every process of the app in `resp` as `u32`, the main one included.
fn family_of(resp: &AppsLaunchResponse) -> Vec<u32> {
    let mut family: Vec<u32> = resp
        .pids
        .iter()
        .flatten()
        .chain(std::iter::once(&resp.pid))
        .filter_map(|&p| u32::try_from(p).ok())
        .filter(|&p| p != 0)
        .collect();
    family.sort_unstable();
    family.dedup();
    family
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::Utc;

    #[test]
    fn family_has_every_pid_once() {
        let resp = AppsLaunchResponse {
            ok: true,
            pid: 7,
            started_at: Utc::now(),
            already_running: true,
            pids: Some(vec![9, 7, 0, -1]),
        };
        assert_eq!(family_of(&resp), vec![7, 9]);
    }
}
