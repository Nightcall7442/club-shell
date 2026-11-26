//! `auth_*` commands (`TAURI_COMMANDS.md` §2.1) → `auth.*` IPC messages. Secrets (`password`,
//! `qrToken`, `cardId`, `token`) are validated for presence/length only and never logged.

use clubshell_protocol::commands::{
    names, AuthLoginRequest, AuthLoginResponse, AuthLogoutRequest, AuthLogoutResponse,
    AuthRegisterRequest, AuthStatusResponse,
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
/// A desk guest's seat code (D-96): four ASCII digits.
const GUEST_CODE_LEN: usize = 4;

/// `auth_login` → `auth.login`. Caches the returned user/session. `endElsewhere: true` (the player's
/// confirmation after `409 activeSessionElsewhere`) is passed through as is: the server ends the
/// player's session on the other PC and signs them in here.
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
/// user/session caches (the admin unlock is PC-level and stays) and puts the club's mouse / audio
/// settings back whatever the Agent answers: the UI signs the player out either way.
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
    super::pc::restore_player_changes("logout");
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

/// The player's name on a new account (the desk's limit).
const DISPLAY_NAME_MAX: usize = 64;
/// A new account's password: 4–64 characters (the server's rule).
const NEW_PASSWORD_MIN: usize = 4;
const NEW_PASSWORD_MAX: usize = 64;

/// `auth_register` → `auth.register` (D-92): the player's own account from the lock screen, signed
/// in at once like `auth_login` (the caches are filled the same way). The password is checked for
/// length only and never logged; the club's switch, the login's rules and the per-PC limit are the
/// server's.
#[tauri::command]
pub async fn auth_register(
    state: State<'_, AppState>,
    req: AuthRegisterRequest,
) -> CmdResult<AuthLoginResponse> {
    let req = validate_register(req)?;
    let resp: AuthLoginResponse = state.agent.request(names::auth::REGISTER, &req).await?;
    state.set_user(Some(resp.user.clone()));
    state.set_session(resp.session.clone());
    tracing::info!(user = %resp.user.username, "registered and signed in");
    Ok(resp)
}

fn validate_register(mut req: AuthRegisterRequest) -> CmdResult<AuthRegisterRequest> {
    req.display_name = Some(validate::required_text(
        "displayName",
        req.display_name.as_deref().unwrap_or_default(),
        DISPLAY_NAME_MAX,
    )?);
    req.username = validate::optional_text("username", req.username, USERNAME_MAX)?;
    let length = req.password.as_deref().map_or(0, |p| p.chars().count());
    if length == 0 {
        return Err(ShellError::validation("password", "required"));
    }
    if length < NEW_PASSWORD_MIN {
        return Err(ShellError::validation("password", "min"));
    }
    if length > NEW_PASSWORD_MAX {
        return Err(ShellError::validation("password", "max"));
    }
    // D-104: every new account says the player's gender.
    match req.gender.as_deref() {
        None | Some("") => return Err(ShellError::validation("gender", "required")),
        Some("male" | "female") => {}
        Some(_) => return Err(ShellError::validation("gender", "enum")),
    }
    Ok(req)
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
        AuthKind::Guest => {
            // The code the cash desk gave a walk-in guest for this PC's seat (D-96): exactly four digits when given.
            if let Some(code) = req.code.as_deref().map(str::trim) {
                if code.is_empty() {
                    req.code = None;
                } else if code.len() != GUEST_CODE_LEN || !code.bytes().all(|b| b.is_ascii_digit())
                {
                    return Err(ShellError::validation("code", "must be 4 digits"));
                } else {
                    req.code = Some(code.to_owned());
                }
            }
        }
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
            end_elsewhere: None,
            code: None,
        }
    }

    fn register(gender: Option<&str>) -> AuthRegisterRequest {
        AuthRegisterRequest {
            display_name: Some("Ali".into()),
            username: None,
            password: Some("1234".into()),
            accept_rules: None,
            gender: gender.map(Into::into),
        }
    }

    #[test]
    fn register_requires_a_gender_of_two() {
        for gender in [None, Some(""), Some("other")] {
            let e = validate_register(register(gender)).unwrap_err();
            assert_eq!(e.code, ErrorCode::Validation);
            assert_eq!(e.details.unwrap()["field"], "gender");
        }
        assert!(validate_register(register(Some("female"))).is_ok());
        assert!(validate_register(register(Some("male"))).is_ok());
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
    fn end_elsewhere_reaches_the_agent() {
        // The frontend's retry after `409 activeSessionElsewhere`: `{ req: { …, endElsewhere: true } }`.
        let req: AuthLoginRequest = serde_json::from_value(serde_json::json!({
            "kind": "card", "cardId": " CARD-1 ", "endElsewhere": true
        }))
        .unwrap();
        let ok = validate_login(req).unwrap();
        assert_eq!(ok.end_elsewhere, Some(true));
        assert_eq!(ok.card_id.as_deref(), Some("CARD-1"));
        let wire = serde_json::to_value(&ok).unwrap();
        assert_eq!(wire["endElsewhere"], true);
        // A first attempt says nothing about it.
        let first = validate_login(AuthLoginRequest {
            card_id: Some("CARD-1".into()),
            ..login(AuthKind::Card)
        })
        .unwrap();
        assert!(serde_json::to_value(&first)
            .unwrap()
            .get("endElsewhere")
            .is_none());
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

    #[test]
    fn a_guest_seat_code_is_four_digits() {
        let guest = |code: &str| {
            validate_login(AuthLoginRequest {
                code: Some(code.into()),
                ..login(AuthKind::Guest)
            })
        };
        assert_eq!(guest(" 0420 ").unwrap().code.as_deref(), Some("0420"));
        assert_eq!(guest("  ").unwrap().code, None);
        for bad in ["123", "12345", "12a4", "١٢٣٤"] {
            let e = guest(bad).unwrap_err();
            assert_eq!(e.code, ErrorCode::Validation);
            assert_eq!(e.details.unwrap()["field"], "code");
        }
    }
}
