//! `auth_*` commands (`TAURI_COMMANDS.md` §2.1) → `auth.*` IPC messages. Secrets (`password`,
//! `qrToken`, `cardId`, `token`) are validated for presence/length only and never logged.

use clubshell_protocol::commands::{
    names, AuthLoginRequest, AuthLoginResponse, AuthLogoutRequest, AuthLogoutResponse,
    AuthStatusResponse,
};
use clubshell_protocol::session::SessionEndReason;
use clubshell_protocol::user::{AuthKind, QrLoginStart};
use tauri::State;

use super::validate;
use crate::state::{AppState, CmdResult, ShellError};

/// `User.username` is 3–32 chars (IPC_PROTOCOL.md §6.3).
const USERNAME_MAX: usize = 32;
const PASSWORD_MAX: usize = 128;
/// QR / card / one-time tokens.
const TOKEN_MAX: usize = 512;

/// `auth_login` → `auth.login`. Caches the returned user/session.
#[tauri::command]
pub async fn auth_login(
    state: State<'_, AppState>,
    req: AuthLoginRequest,
) -> CmdResult<AuthLoginResponse> {
    let req = validate_login(req)?;
    let resp: AuthLoginResponse = state.agent.request(names::auth::LOGIN, &req).await?;
    state.set_user(Some(resp.user.clone()));
    state.set_session(resp.session.clone());
    tracing::info!(kind = %req.kind, user = %resp.user.username, mode = %resp.mode, "login ok");
    Ok(resp)
}

/// `auth_logout` → `auth.logout`. `reason` ∈ `user` (default) | `idle` | `admin`. Clears the
/// user/session caches (the admin unlock is PC-level and stays).
#[tauri::command]
pub async fn auth_logout(
    state: State<'_, AppState>,
    reason: Option<SessionEndReason>,
) -> CmdResult<AuthLogoutResponse> {
    if let Some(reason) = reason {
        if !matches!(
            reason,
            SessionEndReason::User | SessionEndReason::Idle | SessionEndReason::Admin
        ) {
            return Err(ShellError::validation(
                "reason",
                "must be user, idle or admin",
            ));
        }
    }
    let resp: AuthLogoutResponse = state
        .agent
        .request(names::auth::LOGOUT, &AuthLogoutRequest { reason })
        .await?;
    state.set_user(None);
    state.set_session(None);
    tracing::info!(session_ended = resp.session_ended, "logout ok");
    Ok(resp)
}

/// `auth_status` → `auth.status`. Refreshes the user/session caches from the answer.
#[tauri::command]
pub async fn auth_status(state: State<'_, AppState>) -> CmdResult<AuthStatusResponse> {
    let resp: AuthStatusResponse = state.agent.request(names::auth::STATUS, &()).await?;
    state.set_user(resp.user.clone());
    state.set_session(resp.session.clone());
    Ok(resp)
}

/// `auth_qr_start` → `auth.qrStart`.
#[tauri::command]
pub async fn auth_qr_start(state: State<'_, AppState>) -> CmdResult<QrLoginStart> {
    state.agent.request(names::auth::QR_START, &()).await
}

/// Per-kind presence/length checks, done before the pipe is touched (`validation`, `source: tauri`).
/// Trims identifiers (username, tokens) but never the password.
fn validate_login(mut req: AuthLoginRequest) -> CmdResult<AuthLoginRequest> {
    match req.kind {
        AuthKind::Password => {
            req.username = Some(validate::required_text(
                "username",
                req.username.as_deref().unwrap_or_default(),
                USERNAME_MAX,
            )?);
            let password = req.password.as_deref().unwrap_or_default();
            if password.is_empty() {
                return Err(ShellError::validation("password", "required"));
            }
            if password.chars().count() > PASSWORD_MAX {
                return Err(ShellError::validation(
                    "password",
                    &format!("must be at most {PASSWORD_MAX} characters"),
                ));
            }
        }
        AuthKind::Qr => {
            req.qr_token = Some(validate::required_text(
                "qrToken",
                req.qr_token.as_deref().unwrap_or_default(),
                TOKEN_MAX,
            )?);
        }
        AuthKind::Card => {
            req.card_id = Some(validate::required_text(
                "cardId",
                req.card_id.as_deref().unwrap_or_default(),
                TOKEN_MAX,
            )?);
        }
        AuthKind::Token => {
            req.token = Some(validate::required_text(
                "token",
                req.token.as_deref().unwrap_or_default(),
                TOKEN_MAX,
            )?);
        }
        AuthKind::Guest => {}
    }
    Ok(req)
}

#[cfg(test)]
mod tests {
    use super::*;
    use clubshell_protocol::error::ErrorCode;

    fn login(kind: AuthKind) -> AuthLoginRequest {
        AuthLoginRequest {
            kind,
            username: None,
            password: None,
            qr_token: None,
            card_id: None,
            token: None,
        }
    }

    #[test]
    fn password_login_requires_username_and_password() {
        let e = validate_login(AuthLoginRequest {
            username: Some("bob".into()),
            ..login(AuthKind::Password)
        })
        .unwrap_err();
        assert_eq!(e.code, ErrorCode::Validation);
        assert_eq!(e.details.unwrap()["field"], "password");
        let e = validate_login(AuthLoginRequest {
            password: Some("x".into()),
            ..login(AuthKind::Password)
        })
        .unwrap_err();
        assert_eq!(e.details.unwrap()["field"], "username");
        let ok = validate_login(AuthLoginRequest {
            username: Some(" bob ".into()),
            password: Some(" x ".into()),
            ..login(AuthKind::Password)
        })
        .unwrap();
        assert_eq!(ok.username.as_deref(), Some("bob"));
        assert_eq!(
            ok.password.as_deref(),
            Some(" x "),
            "passwords are never trimmed"
        );
    }

    #[test]
    fn other_kinds_require_their_secret() {
        assert!(validate_login(login(AuthKind::Qr)).is_err());
        assert!(validate_login(AuthLoginRequest {
            qr_token: Some("t".into()),
            ..login(AuthKind::Qr)
        })
        .is_ok());
        assert!(validate_login(login(AuthKind::Card)).is_err());
        assert!(validate_login(login(AuthKind::Token)).is_err());
        assert!(validate_login(login(AuthKind::Guest)).is_ok());
    }
}
