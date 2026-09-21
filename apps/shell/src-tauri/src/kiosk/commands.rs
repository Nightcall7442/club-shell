//! `kiosk_*` commands (`TAURI_COMMANDS.md` §2.14): local, no IPC. Registered by `lib.rs` through
//! `crate::commands::invoke_handler!` next to the proxy commands; every handler takes the managed
//! `AppState` and the managed `Arc<Kiosk>` (`kiosk::spawn_all` + `app.manage`).

use std::collections::BTreeMap;
use std::path::{Component, Path, PathBuf};
use std::sync::Arc;

use clubshell_protocol::user::Locale;
use serde::Serialize;
use serde_json::Value;
use tauri::{AppHandle, Manager, State, WebviewWindow};

use super::window_guard::MAIN_LABEL;
use super::{Kiosk, KioskSnapshot, MonitorDto, OverlayKind};
use crate::gamepad::GamepadState;
use crate::state::{AppState, CmdResult, ShellError};

/// Frontend bundles (`apps/shell/src/i18n/*.json`), the base of `kiosk_i18n_bundle`.
const EMBEDDED_EN: &str = include_str!("../../../src/i18n/en.json");
const EMBEDDED_RU: &str = include_str!("../../../src/i18n/ru.json");
const EMBEDDED_UZ: &str = include_str!("../../../src/i18n/uz.json");

/// Result of `kiosk_state`: the native snapshot plus process-level fields.
#[derive(Serialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct KioskState {
    pub agent_connected: bool,
    #[serde(flatten)]
    pub native: KioskSnapshot,
    pub version: &'static str,
    pub devtools: bool,
}

/// `kiosk_state`.
#[tauri::command]
pub async fn kiosk_state(
    state: State<'_, AppState>,
    kiosk: State<'_, Arc<Kiosk>>,
) -> CmdResult<KioskState> {
    Ok(KioskState {
        agent_connected: state.agent.is_connected(),
        native: kiosk.snapshot(),
        version: env!("CARGO_PKG_VERSION"),
        devtools: state.config.devtools,
    })
}

/// `kiosk_set_guard`: `false` only while a game runs or with a cached admin unlock.
#[tauri::command]
pub async fn kiosk_set_guard(
    state: State<'_, AppState>,
    kiosk: State<'_, Arc<Kiosk>>,
    active: bool,
) -> CmdResult<()> {
    if !active && !(state.game_running() || state.has_admin_unlock()) {
        return Err(ShellError::forbidden(
            "disabling the guard needs a running game or an admin unlock",
        ));
    }
    kiosk.set_guard(active);
    Ok(())
}

/// `kiosk_set_fullscreen`: admin unlock required (dev mode is exempt; F11 does the same there).
#[tauri::command]
pub async fn kiosk_set_fullscreen(
    state: State<'_, AppState>,
    kiosk: State<'_, Arc<Kiosk>>,
    on: bool,
) -> CmdResult<()> {
    if !(state.has_admin_unlock() || kiosk.is_dev()) {
        return Err(ShellError::forbidden(
            "fullscreen change needs an admin unlock",
        ));
    }
    kiosk.window().set_fullscreen(on)
}

/// `kiosk_show_overlay`; `kind: "none"` hides it.
#[tauri::command]
pub async fn kiosk_show_overlay(
    kiosk: State<'_, Arc<Kiosk>>,
    kind: OverlayKind,
    payload: Option<Value>,
) -> CmdResult<()> {
    kiosk.overlay().show(kind, payload, None)
}

/// `kiosk_monitors`.
#[tauri::command]
pub async fn kiosk_monitors(kiosk: State<'_, Arc<Kiosk>>) -> CmdResult<Vec<MonitorDto>> {
    Ok(kiosk.monitors().dtos())
}

/// `kiosk_move_to_monitor`; `notFound` for an unknown index.
#[tauri::command]
pub async fn kiosk_move_to_monitor(kiosk: State<'_, Arc<Kiosk>>, index: u32) -> CmdResult<()> {
    let rect = kiosk
        .monitors()
        .monitor_rect(index as usize)
        .ok_or_else(|| ShellError::not_found("monitor"))?;
    kiosk.window().move_to_monitor(rect)
}

/// `kiosk_virtual_keyboard`; `policyDenied` unless `shell.json → kiosk.allowVirtualKeyboard`.
#[tauri::command]
pub async fn kiosk_virtual_keyboard(
    state: State<'_, AppState>,
    kiosk: State<'_, Arc<Kiosk>>,
    show: bool,
) -> CmdResult<()> {
    if !state.config.kiosk.allow_virtual_keyboard {
        return Err(ShellError::policy_denied("allowVirtualKeyboard"));
    }
    kiosk.virtual_keyboard(show)
}

/// `kiosk_focus`: re-asserts the Shell window in the foreground.
#[tauri::command]
pub async fn kiosk_focus(kiosk: State<'_, Arc<Kiosk>>) -> CmdResult<()> {
    if !kiosk.window().focus() {
        tracing::debug!("kiosk_focus: window is not in the foreground after the request");
    }
    Ok(())
}

/// `kiosk_exit`: needs the unexpired token from `sys_unlock_admin`; `action` ∈ `explorer` | `quit`.
#[tauri::command]
pub async fn kiosk_exit(
    state: State<'_, AppState>,
    kiosk: State<'_, Arc<Kiosk>>,
    admin_token: String,
    action: String,
) -> CmdResult<()> {
    let start_explorer = match action.as_str() {
        "explorer" => true,
        "quit" => false,
        _ => return Err(ShellError::validation("action", "must be explorer or quit")),
    };
    if !state.admin_token_valid(&admin_token) {
        tracing::warn!(action = %action, "kiosk_exit rejected: invalid or expired admin token");
        return Err(ShellError::unauthorized("invalid or expired admin token"));
    }
    kiosk.exit(start_explorer);
    Ok(())
}

/// `kiosk_reload`: reloads the main webview (dev mode or admin unlock).
#[tauri::command]
pub async fn kiosk_reload(
    app: AppHandle,
    state: State<'_, AppState>,
    kiosk: State<'_, Arc<Kiosk>>,
) -> CmdResult<()> {
    if !(state.has_admin_unlock() || kiosk.is_dev()) {
        return Err(ShellError::forbidden("reload needs an admin unlock"));
    }
    let window = app
        .get_webview_window(MAIN_LABEL)
        .ok_or_else(|| ShellError::not_found("main window"))?;
    tracing::info!("webview reload requested");
    window.eval("window.location.reload()")?;
    Ok(())
}

/// `kiosk_open_devtools`: `forbidden` unless `shell.json → devtools`; `internal` when this build has
/// no devtools (release without the `devtools` feature).
#[tauri::command]
pub async fn kiosk_open_devtools(
    state: State<'_, AppState>,
    kiosk: State<'_, Arc<Kiosk>>,
) -> CmdResult<()> {
    if !state.config.devtools {
        return Err(ShellError::forbidden("devtools are disabled in shell.json"));
    }
    open_devtools(kiosk.window().window())
}

#[cfg(any(debug_assertions, feature = "devtools"))]
fn open_devtools(window: &WebviewWindow) -> CmdResult<()> {
    window.open_devtools();
    Ok(())
}

#[cfg(not(any(debug_assertions, feature = "devtools")))]
fn open_devtools(_window: &WebviewWindow) -> CmdResult<()> {
    Err(ShellError::internal(
        "devtools are not compiled into this build",
    ))
}

/// `kiosk_gamepad_state`: last snapshot of every pad.
#[tauri::command]
pub async fn kiosk_gamepad_state(kiosk: State<'_, Arc<Kiosk>>) -> CmdResult<Vec<GamepadState>> {
    Ok(kiosk.gamepad().snapshot())
}

/// `kiosk_idle_reset`: marks activity (gamepad navigation, touch UI).
#[tauri::command]
pub async fn kiosk_idle_reset(kiosk: State<'_, Arc<Kiosk>>) -> CmdResult<()> {
    kiosk.idle().reset();
    Ok(())
}

/// `kiosk_i18n_bundle`: embedded bundle for `locale` flattened to `a.b.c` keys, with
/// `<data dir>\locales\<locale>.json` overrides merged on top (missing / invalid files are ignored).
#[tauri::command]
pub async fn kiosk_i18n_bundle(
    state: State<'_, AppState>,
    locale: Locale,
) -> CmdResult<BTreeMap<String, String>> {
    let embedded = match locale {
        Locale::En => EMBEDDED_EN,
        Locale::Ru => EMBEDDED_RU,
        Locale::Uz => EMBEDDED_UZ,
    };
    let mut bundle = BTreeMap::new();
    merge_bundle(&mut bundle, embedded, &format!("embedded {locale}"));
    let path = state
        .config
        .locales_dir()
        .join(format!("{}.json", locale.wire_name()));
    match std::fs::read_to_string(&path) {
        Ok(text) => merge_bundle(&mut bundle, &text, &path.display().to_string()),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
        Err(e) => {
            tracing::warn!(path = %path.display(), error = %e, "locale override not readable")
        }
    }
    Ok(bundle)
}

/// Parses `text` as a (possibly nested) JSON object and flattens it into `into`.
fn merge_bundle(into: &mut BTreeMap<String, String>, text: &str, source: &str) {
    if text.trim().is_empty() {
        return;
    }
    match serde_json::from_str::<Value>(text) {
        Ok(Value::Object(map)) => flatten("", &map, into),
        Ok(_) => tracing::warn!(source, "i18n bundle is not a JSON object; ignored"),
        Err(e) => tracing::warn!(source, error = %e, "i18n bundle invalid; ignored"),
    }
}

fn flatten(
    prefix: &str,
    map: &serde_json::Map<String, Value>,
    into: &mut BTreeMap<String, String>,
) {
    for (key, value) in map {
        let full = if prefix.is_empty() {
            key.clone()
        } else {
            format!("{prefix}.{key}")
        };
        match value {
            Value::Object(nested) => flatten(&full, nested, into),
            Value::String(s) => {
                into.insert(full, s.clone());
            }
            Value::Null => {}
            other => {
                into.insert(full, other.to_string());
            }
        }
    }
}

/// `kiosk_asset_url`: `asset://` URL (`convertFileSrc` shape) for a data-directory-relative path
/// under `themes\` or `cache\media\` (Agent `LocalCache`); `forbidden` elsewhere, `notFound` when the file is missing.
#[tauri::command]
pub async fn kiosk_asset_url(state: State<'_, AppState>, path: String) -> CmdResult<String> {
    let full = resolve_asset(state.config.data_dir(), &path)?;
    if !full.is_file() {
        return Err(ShellError::not_found("asset"));
    }
    Ok(asset_url(&full))
}

/// Validates `relative` (either separator) against the allowed roots and joins it to `data_dir`.
fn resolve_asset(data_dir: &Path, relative: &str) -> CmdResult<PathBuf> {
    let normalized = relative.trim().replace('\\', "/");
    let mut parts: Vec<&str> = Vec::new();
    for component in Path::new(&normalized).components() {
        match component {
            Component::Normal(part) => match part.to_str() {
                Some(s) if !s.is_empty() => parts.push(s),
                _ => return Err(ShellError::validation("path", "invalid path component")),
            },
            Component::CurDir => {}
            _ => {
                return Err(ShellError::forbidden(
                    "asset path must be relative to the data directory",
                ))
            }
        }
    }
    if !matches!(
        parts.as_slice(),
        ["themes", _, ..] | ["cache", "media", _, ..]
    ) {
        return Err(ShellError::forbidden(
            "asset path must be under themes\\ or cache\\media\\",
        ));
    }
    Ok(parts
        .iter()
        .fold(data_dir.to_path_buf(), |acc, part| acc.join(part)))
}

/// `convertFileSrc(path, "asset")`: `http://asset.localhost/<encoded>` on Windows, `asset://localhost/<encoded>` elsewhere.
fn asset_url(full: &Path) -> String {
    let encoded = encode_uri_component(&full.to_string_lossy());
    if cfg!(windows) {
        format!("http://asset.localhost/{encoded}")
    } else {
        format!("asset://localhost/{encoded}")
    }
}

/// JavaScript `encodeURIComponent` (unreserved set `A-Z a-z 0-9 - _ . ! ~ * ' ( )`).
fn encode_uri_component(s: &str) -> String {
    let mut out = String::with_capacity(s.len() * 3);
    for byte in s.bytes() {
        match byte {
            b'A'..=b'Z'
            | b'a'..=b'z'
            | b'0'..=b'9'
            | b'-'
            | b'_'
            | b'.'
            | b'!'
            | b'~'
            | b'*'
            | b'\''
            | b'('
            | b')' => out.push(byte as char),
            _ => out.push_str(&format!("%{byte:02X}")),
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use clubshell_protocol::error::ErrorCode;

    #[test]
    fn asset_paths_are_confined_to_allowed_roots() {
        let root = Path::new(r"C:\ProgramData\ClubShell");
        assert_eq!(
            resolve_asset(root, r"themes\neon\bg.png").unwrap(),
            root.join("themes").join("neon").join("bg.png")
        );
        assert_eq!(
            resolve_asset(root, "cache/media/1.jpg").unwrap(),
            root.join("cache").join("media").join("1.jpg")
        );
        assert_eq!(
            resolve_asset(root, "./themes/x.json").unwrap(),
            root.join("themes").join("x.json")
        );
        for bad in [
            r"..\secure\shell.token",
            "themes/../secure/shell.token",
            "cache/x.jpg",
            "cache/covers/1.jpg",
            "themes",
            "/etc/passwd",
            r"C:\Windows\x",
            "secure/shell.token",
        ] {
            let e = resolve_asset(root, bad).unwrap_err();
            assert_eq!(e.code, ErrorCode::Forbidden, "{bad}");
        }
        assert_eq!(
            encode_uri_component(r"C:\A b\ö.png"),
            "C%3A%5CA%20b%5C%C3%B6.png"
        );
        let url = asset_url(Path::new(r"C:\x\y.png"));
        assert!(url.ends_with("/C%3A%5Cx%5Cy.png"));
    }

    #[test]
    fn i18n_bundles_flatten_and_override() {
        let mut bundle = BTreeMap::new();
        merge_bundle(
            &mut bundle,
            r#"{ "a": { "b": "1", "c": { "d": "2" } }, "n": 5, "z": null }"#,
            "base",
        );
        merge_bundle(&mut bundle, r#"{ "a": { "b": "override" } }"#, "overlay");
        merge_bundle(&mut bundle, "", "empty");
        merge_bundle(&mut bundle, "[1]", "array");
        merge_bundle(&mut bundle, "{ not json", "broken");
        assert_eq!(bundle.get("a.b").map(String::as_str), Some("override"));
        assert_eq!(bundle.get("a.c.d").map(String::as_str), Some("2"));
        assert_eq!(bundle.get("n").map(String::as_str), Some("5"));
        assert!(!bundle.contains_key("z"));
        assert_eq!(bundle.len(), 3);
    }

    #[test]
    fn kiosk_state_wire_shape_is_flat() {
        let json = serde_json::to_value(KioskState {
            agent_connected: true,
            native: KioskSnapshot {
                fullscreen: true,
                guard_active: false,
                hooks_active: false,
                monitors: vec![],
                idle: false,
                idle_sec: 0,
                gamepad_connected: false,
                locked: false,
                game_mode: false,
                overlay: OverlayKind::Hidden,
                dev: true,
            },
            version: "1.0.0",
            devtools: false,
        })
        .unwrap();
        assert_eq!(json["agentConnected"], true);
        assert_eq!(json["guardActive"], false);
        assert_eq!(json["version"], "1.0.0");
        assert!(json.get("native").is_none(), "snapshot is flattened");
    }
}
