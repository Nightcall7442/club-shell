//! Tauri command handlers (`TAURI_COMMANDS.md` §2), one module per command group.
//!
//! Conventions shared by every handler in this directory:
//!
//! * The Rust function name is the command name, exactly as listed in `TAURI_COMMANDS.md` §2 / §4.1
//!   (`auth_login`, `session_start`, …).
//! * Arguments: Tauri 2 maps the camelCase JS key to the snake_case Rust parameter by default
//!   (`{ gameId }` → `game_id`), so no `rename_all` attribute is needed. DTO arguments (`req`, `q`,
//!   `patch`, `e`) are `clubshell_protocol` structs that already carry `rename_all = "camelCase"`.
//!   Missing optional keys deserialize as `None`. The `__trace` key the frontend adds in dev builds is
//!   not a declared parameter, so Tauri ignores it.
//! * Every handler is `async`, takes `tauri::State<'_, AppState>` and returns [`CmdResult`], so the
//!   webview promise rejects with a serialized [`ShellError`].
//! * Proxy handlers validate their input first (`validation`, `source: tauri`) and then forward the
//!   protocol payload with `AgentClient::request`; the Agent's error envelope comes back as
//!   `ShellError { source: ipc }`, transport failures as `source: pipe`, mock errors as `source: mock`.
//!
//! Grouping: booking, tournaments and profile (§2.8–§2.10) live in [`session`]; policy and update
//! (§2.12, §2.13) in [`system`]. The kiosk commands (§2.14, `kiosk_*`) are implemented by
//! `crate::kiosk` and appended through the crate-private `invoke_handler!` macro (see [`register`]).

pub mod apps;
pub mod auth;
pub mod chat;
pub mod games;
pub mod session;
pub mod settings;
pub mod shop;
pub mod system;
pub mod wallet;

use serde::Serialize;

use crate::state::{CmdResult, ShellError};

/// Names of every command implemented in this directory (§2.1–§2.13), in `commands.json` order.
/// The `kiosk_*` commands (§2.14) are not included.
pub const COMMAND_NAMES: &[&str] = &[
    "auth_login",
    "auth_logout",
    "auth_status",
    "auth_qr_start",
    "session_get",
    "session_start",
    "session_pause",
    "session_resume",
    "session_end",
    "session_extend",
    "session_lock",
    "session_unlock",
    "session_time_left",
    "games_list",
    "games_get",
    "games_launch",
    "games_kill",
    "games_running",
    "games_install_status",
    "apps_list",
    "apps_launch",
    "wallet_balance",
    "wallet_tariffs",
    "wallet_history",
    "wallet_topup_intent",
    "shop_products",
    "shop_order",
    "shop_order_status",
    "shop_orders",
    "chat_history",
    "chat_send",
    "chat_mark_read",
    "booking_seats",
    "booking_reserve",
    "booking_cancel",
    "tournaments_list",
    "tournaments_join",
    "tournaments_leaderboard",
    "profile_get",
    "profile_update",
    "profile_stats",
    "profile_achievements",
    "profile_loyalty",
    "profile_game_settings",
    "profile_game_settings_reset",
    "settings_get",
    "settings_set",
    "settings_get_theme",
    "settings_list_themes",
    "settings_get_shell_config",
    "policy_get",
    "policy_reload",
    "sys_pc_info",
    "sys_hardware",
    "sys_metrics",
    "sys_call_admin",
    "sys_reboot",
    "sys_shutdown",
    "sys_lock_screen",
    "sys_set_volume",
    "sys_set_locale",
    "sys_unlock_admin",
    "sys_ack_admin_message",
    "sys_log_client_error",
    "update_check",
    "update_apply",
];

/// Builds the `invoke_handler` closure for every command of this directory plus the extra command
/// paths passed as arguments (the `kiosk_*` commands of `crate::kiosk`):
///
/// ```ignore
/// tauri::Builder::default()
///     .invoke_handler(crate::commands::invoke_handler!(crate::kiosk::kiosk_state, crate::kiosk::kiosk_exit))
/// ```
///
/// `tauri::generate_handler!` must see every command in one invocation (a second
/// `Builder::invoke_handler` call replaces the first), which is why the list is a macro rather than
/// a function. Extra paths are forwarded verbatim; a trailing comma is accepted.
macro_rules! invoke_handler {
    ($($extra:tt)*) => {
        ::tauri::generate_handler![
            crate::commands::auth::auth_login,
            crate::commands::auth::auth_logout,
            crate::commands::auth::auth_status,
            crate::commands::auth::auth_qr_start,
            crate::commands::session::session_get,
            crate::commands::session::session_start,
            crate::commands::session::session_pause,
            crate::commands::session::session_resume,
            crate::commands::session::session_end,
            crate::commands::session::session_extend,
            crate::commands::session::session_lock,
            crate::commands::session::session_unlock,
            crate::commands::session::session_time_left,
            crate::commands::games::games_list,
            crate::commands::games::games_get,
            crate::commands::games::games_launch,
            crate::commands::games::games_kill,
            crate::commands::games::games_running,
            crate::commands::games::games_install_status,
            crate::commands::apps::apps_list,
            crate::commands::apps::apps_launch,
            crate::commands::wallet::wallet_balance,
            crate::commands::wallet::wallet_tariffs,
            crate::commands::wallet::wallet_history,
            crate::commands::wallet::wallet_topup_intent,
            crate::commands::shop::shop_products,
            crate::commands::shop::shop_order,
            crate::commands::shop::shop_order_status,
            crate::commands::shop::shop_orders,
            crate::commands::chat::chat_history,
            crate::commands::chat::chat_send,
            crate::commands::chat::chat_mark_read,
            crate::commands::session::booking_seats,
            crate::commands::session::booking_reserve,
            crate::commands::session::booking_cancel,
            crate::commands::session::tournaments_list,
            crate::commands::session::tournaments_join,
            crate::commands::session::tournaments_leaderboard,
            crate::commands::session::profile_get,
            crate::commands::session::profile_update,
            crate::commands::session::profile_stats,
            crate::commands::session::profile_achievements,
            crate::commands::session::profile_loyalty,
            crate::commands::session::profile_game_settings,
            crate::commands::session::profile_game_settings_reset,
            crate::commands::settings::settings_get,
            crate::commands::settings::settings_set,
            crate::commands::settings::settings_get_theme,
            crate::commands::settings::settings_list_themes,
            crate::commands::settings::settings_get_shell_config,
            crate::commands::system::policy_get,
            crate::commands::system::policy_reload,
            crate::commands::system::sys_pc_info,
            crate::commands::system::sys_hardware,
            crate::commands::system::sys_metrics,
            crate::commands::system::sys_call_admin,
            crate::commands::system::sys_reboot,
            crate::commands::system::sys_shutdown,
            crate::commands::system::sys_lock_screen,
            crate::commands::system::sys_set_volume,
            crate::commands::system::sys_set_locale,
            crate::commands::system::sys_unlock_admin,
            crate::commands::system::sys_ack_admin_message,
            crate::commands::system::sys_log_client_error,
            crate::commands::system::update_check,
            crate::commands::system::update_apply,
            $($extra)*
        ]
    };
}
pub(crate) use invoke_handler;

/// Registers every command of this directory on `builder`. Complete only when no `kiosk_*` commands
/// exist; otherwise `lib.rs` must call `builder.invoke_handler(invoke_handler!(<kiosk paths>))`
/// instead, because Tauri keeps a single invoke handler.
pub fn register(builder: tauri::Builder<tauri::Wry>) -> tauri::Builder<tauri::Wry> {
    builder.invoke_handler(crate::commands::invoke_handler!())
}

/// Emits a `kiosk://*` event to every window. Failures are logged, never surfaced to the caller:
/// the command already succeeded on the Agent side.
pub(crate) fn emit<T: Serialize + Clone>(app: &tauri::AppHandle, event: &str, payload: T) {
    if let Err(e) = tauri::Emitter::emit(app, event, payload) {
        tracing::warn!(event, error = %e, "cannot emit event to webview");
    }
}

/// Cuts `s` to at most `max_chars` characters (with a trailing `…` when cut).
pub(crate) fn truncate(s: &str, max_chars: usize) -> String {
    let mut out: String = s.chars().take(max_chars).collect();
    if out.len() < s.len() {
        out.push('…');
    }
    out
}

/// Input checks shared by the command modules; every failure is `ShellError::validation` with
/// `details: { field, reason }` (`TAURI_COMMANDS.md` §1.1, `source: tauri`).
pub(crate) mod validate {
    use super::{CmdResult, ShellError};

    /// Trims `value`; `Ok(None)` when absent or blank; `validation` when longer than `max` chars.
    pub fn optional_text(
        field: &str,
        value: Option<String>,
        max: usize,
    ) -> CmdResult<Option<String>> {
        match value.map(|v| v.trim().to_owned()).filter(|v| !v.is_empty()) {
            Some(v) if v.chars().count() > max => Err(too_long(field, max)),
            other => Ok(other),
        }
    }

    /// Trims `value`; `validation` when blank or longer than `max` chars.
    pub fn required_text(field: &str, value: &str, max: usize) -> CmdResult<String> {
        let v = value.trim();
        if v.is_empty() {
            return Err(ShellError::validation(field, "required"));
        }
        if v.chars().count() > max {
            return Err(too_long(field, max));
        }
        Ok(v.to_owned())
    }

    /// `validation` unless `min <= value <= max`.
    pub fn range(field: &str, value: i32, min: i32, max: i32) -> CmdResult<()> {
        if (min..=max).contains(&value) {
            Ok(())
        } else {
            Err(ShellError::validation(
                field,
                &format!("must be between {min} and {max}"),
            ))
        }
    }

    /// [`range`] for an optional value (absent is fine).
    pub fn optional_range(field: &str, value: Option<i32>, min: i32, max: i32) -> CmdResult<()> {
        value.map_or(Ok(()), |v| range(field, v, min, max))
    }

    /// `page` ≥ 1 and `pageSize` in `1..=max_page_size` when given.
    pub fn paging(page: Option<i32>, page_size: Option<i32>, max_page_size: i32) -> CmdResult<()> {
        optional_range("page", page, 1, i32::MAX)?;
        optional_range("pageSize", page_size, 1, max_page_size)
    }

    /// 4–6 ASCII digits (user / unlock PIN).
    pub fn pin(field: &str, value: &str) -> CmdResult<()> {
        if (4..=6).contains(&value.len()) && value.bytes().all(|b| b.is_ascii_digit()) {
            Ok(())
        } else {
            Err(ShellError::validation(field, "must be 4 to 6 digits"))
        }
    }

    /// Bare file name for `themes\<name>.json` and similar: 1–64 chars of `A-Z a-z 0-9 - _`
    /// (no separators, no dots, so it can never escape its directory).
    pub fn bare_name(field: &str, value: &str) -> CmdResult<()> {
        let ok = !value.is_empty()
            && value.len() <= 64
            && value
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_');
        if ok {
            Ok(())
        } else {
            Err(ShellError::validation(
                field,
                "must be 1-64 characters of letters, digits, '-' or '_'",
            ))
        }
    }

    fn too_long(field: &str, max: usize) -> ShellError {
        ShellError::validation(field, &format!("must be at most {max} characters"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use clubshell_protocol::error::ErrorCode;

    #[test]
    fn handler_lists_every_documented_command_once() {
        // Type-checks every path in the `generate_handler!` list (a wrong path fails to compile).
        let handler: Box<dyn Fn(tauri::ipc::Invoke<tauri::Wry>) -> bool + Send + Sync> =
            Box::new(invoke_handler!());
        drop(handler);
        assert_eq!(COMMAND_NAMES.len(), 66);
        let mut sorted = COMMAND_NAMES.to_vec();
        sorted.sort_unstable();
        sorted.dedup();
        assert_eq!(sorted.len(), COMMAND_NAMES.len(), "duplicate command name");
        assert!(COMMAND_NAMES.iter().all(|n| !n.starts_with("kiosk_")));
    }

    #[test]
    fn validation_helpers() {
        assert_eq!(
            validate::optional_text("f", Some("  hi ".into()), 10)
                .unwrap()
                .as_deref(),
            Some("hi")
        );
        assert_eq!(
            validate::optional_text("f", Some("   ".into()), 10).unwrap(),
            None
        );
        let e = validate::optional_text("f", Some("x".repeat(11)), 10).unwrap_err();
        assert_eq!(
            (e.code, e.source),
            (ErrorCode::Validation, crate::state::ErrorSource::Tauri)
        );
        assert_eq!(e.details.unwrap()["field"], "f");
        assert!(validate::required_text("f", " ", 10).is_err());
        assert!(validate::range("f", 5, 1, 5).is_ok());
        assert!(validate::range("f", 6, 1, 5).is_err());
        assert!(validate::paging(None, None, 100).is_ok());
        assert!(validate::paging(Some(0), None, 100).is_err());
        assert!(validate::paging(Some(1), Some(101), 100).is_err());
        assert!(validate::pin("pin", "1234").is_ok());
        assert!(validate::pin("pin", "12a4").is_err());
        assert!(validate::pin("pin", "1234567").is_err());
        assert!(validate::bare_name("theme", "neon-2_x").is_ok());
        assert!(validate::bare_name("theme", "../etc").is_err());
        assert!(validate::bare_name("theme", "a.b").is_err());
        assert!(validate::bare_name("theme", "").is_err());
        assert_eq!(truncate("abcdef", 3), "abc…");
        assert_eq!(truncate("abc", 3), "abc");
    }
}
