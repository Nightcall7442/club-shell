//! `apps_*` commands (`TAURI_COMMANDS.md` §2.4) → `apps.*` IPC messages. `apps_launch` shares
//! `AppState::launch_lock` with `games_launch` (one launch at a time).

use clubshell_protocol::commands::{
    names, AppsLaunchRequest, AppsLaunchResponse, AppsListResponse,
};
use clubshell_protocol::games::App;
use tauri::State;
use uuid::Uuid;

use super::validate;
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
    app_id: Uuid,
    args: Option<String>,
) -> CmdResult<AppsLaunchResponse> {
    if app_id.is_nil() {
        return Err(ShellError::validation("appId", "required"));
    }
    let args = validate::optional_text("args", args, ARGS_MAX)?;
    let _launching = state.launch_lock.lock().await;
    let resp: AppsLaunchResponse = state
        .agent
        .request(names::apps::LAUNCH, &AppsLaunchRequest { app_id, args })
        .await?;
    tracing::info!(app_id = %app_id, pid = resp.pid, "app launched");
    Ok(resp)
}
