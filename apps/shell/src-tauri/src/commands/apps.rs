//! `apps_*` commands (`TAURI_COMMANDS.md` §2.4) → `apps.*` IPC messages. `apps_launch` shares
//! `AppState::launch_lock` with `games_launch` (one launch at a time).
//!
//! SHELL_CHANGES 87: a program the Agent starts opens under the Shell, and the foreground guard takes the screen back
//! within 250 ms. So `apps_launch` gives Windows' leave while the Shell still owns the foreground, hands the
//! foreground off to the program like the dock does, and brings its window up in the background
//! (`kiosk::front::bring_app`); the command answers at once.

use std::sync::Arc;

use clubshell_protocol::commands::{
    names, AppsLaunchRequest, AppsLaunchResponse, AppsListResponse,
};
use clubshell_protocol::games::App;
use tauri::State;
use uuid::Uuid;

use super::validate;
use crate::kiosk::{front, Kiosk};
use crate::state::{AppState, CmdResult, ShellError};

const ARGS_MAX: usize = 512;

/// `apps_list` → `apps.list` (unwrapped `items`).
#[tauri::command]
pub async fn apps_list(state: State<'_, AppState>) -> CmdResult<Vec<App>> {
    let resp: AppsListResponse = state.agent.request(names::apps::LIST, &()).await?;
    Ok(resp.items)
}

/// `apps_launch` → `apps.launch`.
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
    let resp: AppsLaunchResponse = state
        .agent
        .request(names::apps::LAUNCH, &AppsLaunchRequest { app_id, args })
        .await?;
    tracing::info!(app_id = %app_id, pid = resp.pid, "app launched");
    if let Ok(pid) = u32::try_from(resp.pid) {
        if pid != 0 && !kiosk.is_game_mode() {
            front::bring_app(Arc::clone(&kiosk), pid, before);
        }
    }
    Ok(resp)
}
