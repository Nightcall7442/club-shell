//! `sys_*` commands (`TAURI_COMMANDS.md` §2.13) plus `policy_*` (§2.12) and `update_*` (§2.13),
//! all proxied to the Agent. `sys_unlock_admin` caches the admin token in `AppState` for the
//! `kiosk_*` commands; `sys_set_locale` emits `kiosk://localeChanged`; `sys_log_client_error` is
//! fire-and-forget (logged locally at target `webview`, forwarded without waiting).

use std::sync::Arc;

use clubshell_protocol::commands::{
    names, CallAdminCategory, ClientErrorLevel, OkResponse, PolicyReloadRequest,
    PolicyReloadResponse, ScheduledResult, SetVolumeRequest, SysAckAdminMessageRequest,
    SysCallAdminRequest, SysCallAdminResponse, SysHardwareRequest, SysLockScreenRequest,
    SysLogClientErrorRequest, SysPowerRequest, SysSetLocaleRequest, SysSetLocaleResponse,
    SysUnlockAdminRequest, SysUnlockAdminResponse, UpdateApplyRequest, UpdateApplyResponse,
    UpdateCheckResponse, UpdateComponent, VolumeState,
};
use clubshell_protocol::pc::{HardwareInfo, PcInfo, PcMetrics, Policy};
use clubshell_protocol::user::Locale;
use tauri::{AppHandle, State};
use uuid::Uuid;

use super::settings::LOCALE_CHANGED_EVENT;
use super::{emit, truncate, validate};
use crate::state::{AppState, CmdResult, ShellError};

const CALL_ADMIN_MESSAGE_MAX: usize = 500;
const REASON_MAX: usize = 200;
/// `delaySec` for reboot/shutdown (1 h).
const POWER_DELAY_MAX: i32 = 3600;
const ADMIN_PIN_MAX: usize = 32;
/// Client error fields are truncated, not rejected: a log line must never fail to be written.
const CLIENT_ERROR_MESSAGE_MAX: usize = 4000;
const CLIENT_ERROR_STACK_MAX: usize = 16_000;
const CLIENT_ERROR_ROUTE_MAX: usize = 200;

// ───────────────────────────── sys ─────────────────────────────

/// `sys_pc_info` → `sys.pcInfo`.
#[tauri::command]
pub async fn sys_pc_info(state: State<'_, AppState>) -> CmdResult<PcInfo> {
    state.agent.request(names::sys::PC_INFO, &()).await
}

/// `sys_hardware` → `sys.hardware`; `refresh` forces a rescan.
#[tauri::command]
pub async fn sys_hardware(
    state: State<'_, AppState>,
    refresh: Option<bool>,
) -> CmdResult<HardwareInfo> {
    state
        .agent
        .request(names::sys::HARDWARE, &SysHardwareRequest { refresh })
        .await
}

/// `sys_metrics` → `sys.metrics` (last sample; continuous samples arrive as `agent://sys.metrics`).
#[tauri::command]
pub async fn sys_metrics(state: State<'_, AppState>) -> CmdResult<PcMetrics> {
    state.agent.request(names::sys::METRICS, &()).await
}

/// `sys_call_admin` → `sys.callAdmin` (Agent rate-limits to 1 per 30 s).
#[tauri::command]
pub async fn sys_call_admin(
    state: State<'_, AppState>,
    category: CallAdminCategory,
    message: Option<String>,
) -> CmdResult<SysCallAdminResponse> {
    let message = validate::optional_text("message", message, CALL_ADMIN_MESSAGE_MAX)?;
    let resp: SysCallAdminResponse = state
        .agent
        .request(
            names::sys::CALL_ADMIN,
            &SysCallAdminRequest { category, message },
        )
        .await?;
    tracing::info!(category = %category, ticket_id = %resp.ticket_id, queue_position = ?resp.queue_position, "admin called");
    Ok(resp)
}

/// `sys_reboot` → `sys.reboot`.
#[tauri::command]
pub async fn sys_reboot(
    state: State<'_, AppState>,
    delay_sec: Option<i32>,
    reason: Option<String>,
) -> CmdResult<ScheduledResult> {
    let req = validate_power(delay_sec, reason)?;
    tracing::warn!(delay_sec = ?req.delay_sec, reason = ?req.reason, "reboot requested");
    state.agent.request(names::sys::REBOOT, &req).await
}

/// `sys_shutdown` → `sys.shutdown`.
#[tauri::command]
pub async fn sys_shutdown(
    state: State<'_, AppState>,
    delay_sec: Option<i32>,
    reason: Option<String>,
) -> CmdResult<ScheduledResult> {
    let req = validate_power(delay_sec, reason)?;
    tracing::warn!(delay_sec = ?req.delay_sec, reason = ?req.reason, "shutdown requested");
    state.agent.request(names::sys::SHUTDOWN, &req).await
}

/// `sys_lock_screen` → `sys.lockScreen` (locks the session when one is active, else the idle overlay).
#[tauri::command]
pub async fn sys_lock_screen(
    state: State<'_, AppState>,
    reason: Option<String>,
) -> CmdResult<OkResponse> {
    let reason = validate::optional_text("reason", reason, REASON_MAX)?;
    state
        .agent
        .request(names::sys::LOCK_SCREEN, &SysLockScreenRequest { reason })
        .await
}

/// `sys_set_volume` → `sys.setVolume`; `level` 0–100, `muted` unchanged when absent.
#[tauri::command]
pub async fn sys_set_volume(
    state: State<'_, AppState>,
    level: i32,
    muted: Option<bool>,
) -> CmdResult<VolumeState> {
    validate::range("level", level, 0, 100)?;
    state
        .agent
        .request(names::sys::SET_VOLUME, &SetVolumeRequest { level, muted })
        .await
}

/// `sys_set_locale` → `sys.setLocale`, then `kiosk://localeChanged`.
#[tauri::command]
pub async fn sys_set_locale(
    app: AppHandle,
    state: State<'_, AppState>,
    locale: Locale,
) -> CmdResult<SysSetLocaleResponse> {
    let resp: SysSetLocaleResponse = state
        .agent
        .request(names::sys::SET_LOCALE, &SysSetLocaleRequest { locale })
        .await?;
    tracing::info!(locale = %resp.locale, "locale changed");
    emit(&app, LOCALE_CHANGED_EVENT, resp);
    Ok(resp)
}

/// `sys_unlock_admin` → `sys.unlockAdmin`; the returned token is cached for `kiosk_exit` /
/// `kiosk_set_guard` / `kiosk_set_fullscreen` until it expires. The PIN is validated by the Agent
/// (also when `shell.json → kiosk.adminPinHash` is set) and never logged.
#[tauri::command]
pub async fn sys_unlock_admin(
    state: State<'_, AppState>,
    pin: String,
) -> CmdResult<SysUnlockAdminResponse> {
    let pin = pin.trim().to_owned();
    if pin.is_empty() {
        return Err(ShellError::validation("pin", "required"));
    }
    if pin.chars().count() > ADMIN_PIN_MAX {
        return Err(ShellError::validation(
            "pin",
            &format!("must be at most {ADMIN_PIN_MAX} characters"),
        ));
    }
    let resp: SysUnlockAdminResponse = state
        .agent
        .request(names::sys::UNLOCK_ADMIN, &SysUnlockAdminRequest { pin })
        .await?;
    state.set_admin_unlock(resp.admin_token.clone(), resp.expires_at);
    tracing::info!(expires_at = %resp.expires_at, "admin unlock granted");
    Ok(resp)
}

/// `sys_ack_admin_message` → `sys.ackAdminMessage`.
#[tauri::command]
pub async fn sys_ack_admin_message(state: State<'_, AppState>, id: Uuid) -> CmdResult<OkResponse> {
    if id.is_nil() {
        return Err(ShellError::validation("id", "required"));
    }
    state
        .agent
        .request(
            names::sys::ACK_ADMIN_MESSAGE,
            &SysAckAdminMessageRequest { id },
        )
        .await
}

/// `sys_log_client_error` → `sys.logClientError`, fire-and-forget: written to the shell log
/// (target `webview`) and forwarded on a background task; resolves with `null` immediately.
#[tauri::command]
pub async fn sys_log_client_error(
    state: State<'_, AppState>,
    e: SysLogClientErrorRequest,
) -> CmdResult<()> {
    let req = SysLogClientErrorRequest {
        level: e.level,
        message: truncate(e.message.trim(), CLIENT_ERROR_MESSAGE_MAX),
        stack: e
            .stack
            .as_deref()
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .map(|s| truncate(s, CLIENT_ERROR_STACK_MAX)),
        route: e
            .route
            .as_deref()
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .map(|s| truncate(s, CLIENT_ERROR_ROUTE_MAX)),
    };
    match req.level {
        ClientErrorLevel::Error => {
            tracing::error!(target: "webview", route = ?req.route, stack = ?req.stack, "{}", req.message)
        }
        ClientErrorLevel::Warn => {
            tracing::warn!(target: "webview", route = ?req.route, stack = ?req.stack, "{}", req.message)
        }
    }
    let agent = Arc::clone(&state.agent);
    tauri::async_runtime::spawn(async move {
        let payload = serde_json::to_value(&req).ok();
        if let Err(err) = agent
            .request_raw(names::sys::LOG_CLIENT_ERROR, payload)
            .await
        {
            tracing::debug!(error = %err, "client error not forwarded to the agent");
        }
    });
    Ok(())
}

fn validate_power(delay_sec: Option<i32>, reason: Option<String>) -> CmdResult<SysPowerRequest> {
    validate::optional_range("delaySec", delay_sec, 0, POWER_DELAY_MAX)?;
    let reason = validate::optional_text("reason", reason, REASON_MAX)?;
    Ok(SysPowerRequest { delay_sec, reason })
}

// ───────────────────────────── policy (§2.12) ─────────────────────────────

/// `policy_get` → `policy.get`.
#[tauri::command]
pub async fn policy_get(state: State<'_, AppState>) -> CmdResult<Policy> {
    state.agent.request(names::policy::GET, &()).await
}

/// `policy_reload` → `policy.reload`; `force` bypasses the ETag cache.
#[tauri::command]
pub async fn policy_reload(
    state: State<'_, AppState>,
    force: Option<bool>,
) -> CmdResult<PolicyReloadResponse> {
    let resp: PolicyReloadResponse = state
        .agent
        .request(names::policy::RELOAD, &PolicyReloadRequest { force })
        .await?;
    tracing::info!(version = resp.policy.version, source = %resp.source, applied = resp.applied, changed = ?resp.changed, "policy reloaded");
    Ok(resp)
}

// ───────────────────────────── update (§2.13) ─────────────────────────────

/// `update_check` → `update.check`.
#[tauri::command]
pub async fn update_check(state: State<'_, AppState>) -> CmdResult<UpdateCheckResponse> {
    state.agent.request(names::update::CHECK, &()).await
}

/// `update_apply` → `update.apply`.
#[tauri::command]
pub async fn update_apply(
    state: State<'_, AppState>,
    component: UpdateComponent,
) -> CmdResult<UpdateApplyResponse> {
    let resp: UpdateApplyResponse = state
        .agent
        .request(names::update::APPLY, &UpdateApplyRequest { component })
        .await?;
    tracing::info!(component = %component, scheduled = resp.scheduled, at = ?resp.at, "update apply requested");
    Ok(resp)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn power_request_is_checked() {
        assert!(validate_power(Some(-1), None).is_err());
        assert!(validate_power(Some(POWER_DELAY_MAX + 1), None).is_err());
        let ok = validate_power(Some(30), Some("  admin  ".into())).unwrap();
        assert_eq!(
            (ok.delay_sec, ok.reason.as_deref()),
            (Some(30), Some("admin"))
        );
        assert_eq!(
            validate_power(None, None).unwrap(),
            SysPowerRequest::default()
        );
    }
}
