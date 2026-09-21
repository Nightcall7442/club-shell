//! `games_*` commands (`TAURI_COMMANDS.md` §2.3) → `games.*` IPC messages.
//!
//! `games_launch` is serialized with `apps_launch` through `AppState::launch_lock`, runs with a
//! 120 s timeout and suspends the kiosk foreground guard (`KioskControl::set_game_mode(true)`)
//! before the launcher starts so the game window can take the foreground. The guard is re-armed by
//! the `game.stateChanged{exited|failed|killed}` event (`agent::events`), or right here when the
//! launch fails without such an event (transport error).

use std::time::Duration;

use clubshell_protocol::commands::{
    names, GamesGetRequest, GamesInstallStatusRequest, GamesKillRequest, GamesKillResponse,
    GamesLaunchRequest, GamesListRequest, GamesListResponse, GamesRunningResponse,
};
use clubshell_protocol::games::{Game, GameInstallStatus, LaunchResult, RunningGame};
use tauri::State;
use uuid::Uuid;

use super::validate;
use crate::state::{AppState, CmdResult, ShellError};

/// `games.launch` waits for launcher + game process (`TAURI_COMMANDS.md` §1 "Timeout").
pub const LAUNCH_TIMEOUT: Duration = Duration::from_secs(120);
const SEARCH_MAX: usize = 200;
const CATEGORY_MAX: usize = 64;
const EXTRA_ARGS_MAX: usize = 512;

/// `games_list` → `games.list`.
#[tauri::command]
pub async fn games_list(
    state: State<'_, AppState>,
    q: Option<GamesListRequest>,
) -> CmdResult<GamesListResponse> {
    let mut q = q.unwrap_or_default();
    validate::paging(q.page, q.page_size, GamesListRequest::MAX_PAGE_SIZE)?;
    q.search = validate::optional_text("search", q.search, SEARCH_MAX)?;
    q.category = validate::optional_text("category", q.category, CATEGORY_MAX)?;
    state.agent.request(names::games::LIST, &q).await
}

/// `games_get` → `games.get`.
#[tauri::command]
pub async fn games_get(state: State<'_, AppState>, game_id: Uuid) -> CmdResult<Game> {
    state
        .agent
        .request(names::games::GET, &GamesGetRequest { game_id })
        .await
}

/// `games_launch` → `games.launch` (120 s, serialized, guard suspended; see module docs).
#[tauri::command]
pub async fn games_launch(
    state: State<'_, AppState>,
    req: GamesLaunchRequest,
) -> CmdResult<LaunchResult> {
    let req = validate_launch(req)?;
    let _launching = state.launch_lock.lock().await;

    // A game already in the foreground keeps its guard state whatever this launch does.
    let was_running = state.game_running();
    if !was_running {
        state.set_game_running(true);
        state.kiosk().set_game_mode(true);
    }
    tracing::info!(game_id = %req.game_id, account_pool = ?req.use_account_pool, "launching game");

    let result = state
        .agent
        .request_with_timeout::<_, LaunchResult>(names::games::LAUNCH, &req, LAUNCH_TIMEOUT)
        .await;
    match &result {
        Ok(r) if r.ok => tracing::info!(game_id = %req.game_id, pid = ?r.pid, "game launched"),
        Ok(r) => {
            tracing::warn!(game_id = %req.game_id, error = ?r.error, "launch reported failure");
            rearm_guard(&state, was_running);
        }
        Err(e) => {
            tracing::warn!(game_id = %req.game_id, error = %e, "launch failed");
            rearm_guard(&state, was_running);
        }
    }
    result
}

/// `games_kill` → `games.kill`; no target = kill every tracked game.
#[tauri::command]
pub async fn games_kill(
    state: State<'_, AppState>,
    game_id: Option<Uuid>,
    pid: Option<i32>,
    force: Option<bool>,
) -> CmdResult<GamesKillResponse> {
    if pid.is_some_and(|p| p <= 0) {
        return Err(ShellError::validation("pid", "must be positive"));
    }
    let resp: GamesKillResponse = state
        .agent
        .request(
            names::games::KILL,
            &GamesKillRequest {
                game_id,
                pid,
                force,
            },
        )
        .await?;
    tracing::info!(killed = resp.killed, pids = ?resp.pids, "games killed");
    Ok(resp)
}

/// `games_running` → `games.running` (unwrapped `items`).
#[tauri::command]
pub async fn games_running(state: State<'_, AppState>) -> CmdResult<Vec<RunningGame>> {
    let resp: GamesRunningResponse = state.agent.request(names::games::RUNNING, &()).await?;
    Ok(resp.items)
}

/// `games_install_status` → `games.installStatus`.
#[tauri::command]
pub async fn games_install_status(
    state: State<'_, AppState>,
    game_id: Uuid,
) -> CmdResult<GameInstallStatus> {
    state
        .agent
        .request(
            names::games::INSTALL_STATUS,
            &GamesInstallStatusRequest { game_id },
        )
        .await
}

/// Re-arms the guard after a failed launch unless a game was already running before it, and unless
/// the `game.stateChanged{failed}` event already did it.
fn rearm_guard(state: &AppState, was_running: bool) {
    if !was_running && state.game_running() {
        state.set_game_running(false);
        state.kiosk().set_game_mode(false);
    }
}

fn validate_launch(mut req: GamesLaunchRequest) -> CmdResult<GamesLaunchRequest> {
    if req.game_id.is_nil() {
        return Err(ShellError::validation("gameId", "required"));
    }
    req.extra_args = validate::optional_text("extraArgs", req.extra_args, EXTRA_ARGS_MAX)?;
    if let Some(r) = req.resolution {
        validate::range("resolution.width", r.width, 320, 7680)?;
        validate::range("resolution.height", r.height, 240, 4320)?;
    }
    Ok(req)
}

#[cfg(test)]
mod tests {
    use super::*;
    use clubshell_protocol::games::Resolution;

    #[test]
    fn launch_request_is_checked() {
        let req = |resolution| GamesLaunchRequest {
            game_id: Uuid::from_u128(1),
            use_account_pool: None,
            extra_args: Some("  ".into()),
            resolution,
        };
        let ok = validate_launch(req(Some(Resolution {
            width: 1920,
            height: 1080,
        })))
        .unwrap();
        assert_eq!(ok.extra_args, None, "blank extra args are dropped");
        assert!(validate_launch(req(Some(Resolution {
            width: 10,
            height: 1080
        })))
        .is_err());
        assert!(validate_launch(GamesLaunchRequest {
            game_id: Uuid::nil(),
            ..req(None)
        })
        .is_err());
    }
}
