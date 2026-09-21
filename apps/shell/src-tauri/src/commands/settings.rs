//! `settings_*` commands (`TAURI_COMMANDS.md` §2.11). `settings_get` / `settings_set` proxy to the
//! Agent (which persists `ShellSettings`); the theme and shell-config commands are local: themes are
//! `themes\<name>.json` under the data directory with the embedded `config/themes/default.json` as
//! the fallback that always exists.
//!
//! After the Agent confirms a `theme` / `locale` change, `settings_set` emits `kiosk://themeChanged`
//! (payload `Theme`) / `kiosk://localeChanged` (payload `{ locale }`) so every window re-renders.

use std::path::Path;

use clubshell_protocol::commands::{
    names, SettingsSetRequest, ShellSettings, SysSetLocaleResponse,
};
use clubshell_protocol::pc::Theme;
use tauri::{AppHandle, State};

use super::{emit, validate};
use crate::config::ShellConfig;
use crate::state::{AppState, CmdResult, ShellError};

/// `kiosk://themeChanged` (`TAURI_COMMANDS.md` §3.2), payload `Theme`.
pub const THEME_CHANGED_EVENT: &str = "kiosk://themeChanged";
/// `kiosk://localeChanged` (`TAURI_COMMANDS.md` §3.2), payload `{ locale }`.
pub const LOCALE_CHANGED_EVENT: &str = "kiosk://localeChanged";
/// Theme that always exists (embedded copy of `config/themes/default.json`).
pub const DEFAULT_THEME_NAME: &str = "default";
/// `config/themes/default.json`, shipped inside the binary (path is relative to this source file).
pub const DEFAULT_THEME_JSON: &str = include_str!("../../../../../config/themes/default.json");

/// `idleTimeoutSec` upper bound (24 h); 0 = never.
const IDLE_TIMEOUT_MAX: i32 = 86_400;

/// The embedded default theme.
pub fn default_theme() -> Theme {
    serde_json::from_str(DEFAULT_THEME_JSON).expect("embedded config/themes/default.json is valid")
}

/// Reads `themes\<name>.json`. A missing or invalid file falls back to the embedded default (the
/// kiosk must always have a theme); the fallback keeps its own `name`, so the caller can tell.
pub fn load_theme(config: &ShellConfig, name: &str) -> Theme {
    load_theme_from(&config.theme_path(name), name)
}

fn load_theme_from(path: &Path, name: &str) -> Theme {
    match std::fs::read_to_string(path) {
        Ok(text) => match serde_json::from_str::<Theme>(&text) {
            Ok(theme) => {
                if theme.name != name {
                    tracing::warn!(path = %path.display(), theme = %theme.name, "theme name does not match its file name");
                }
                return theme;
            }
            Err(e) => {
                tracing::warn!(path = %path.display(), error = %e, "theme file invalid; using embedded default")
            }
        },
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
            if name != DEFAULT_THEME_NAME {
                tracing::warn!(path = %path.display(), "theme file missing; using embedded default");
            }
        }
        Err(e) => {
            tracing::warn!(path = %path.display(), error = %e, "cannot read theme file; using embedded default")
        }
    }
    default_theme()
}

/// Installed theme names: `themes\*.json` file stems (bare names only) plus `default`, sorted.
pub fn list_themes(config: &ShellConfig) -> Vec<String> {
    list_themes_in(&config.themes_dir())
}

fn list_themes_in(dir: &Path) -> Vec<String> {
    let mut names = vec![DEFAULT_THEME_NAME.to_owned()];
    match std::fs::read_dir(dir) {
        Ok(entries) => {
            for entry in entries.flatten() {
                let path = entry.path();
                let is_json = path
                    .extension()
                    .and_then(|e| e.to_str())
                    .is_some_and(|e| e.eq_ignore_ascii_case("json"));
                if let Some(stem) = path
                    .file_stem()
                    .and_then(|s| s.to_str())
                    .filter(|_| is_json)
                {
                    if validate::bare_name("theme", stem).is_ok() {
                        names.push(stem.to_owned());
                    }
                }
            }
        }
        Err(e) => {
            tracing::debug!(dir = %dir.display(), error = %e, "themes directory not readable")
        }
    }
    names.sort_unstable();
    names.dedup();
    names
}

/// `settings_get` → `settings.get`. `availableThemes` is filled from the local `themes\` listing
/// when the Agent leaves it empty.
#[tauri::command]
pub async fn settings_get(state: State<'_, AppState>) -> CmdResult<ShellSettings> {
    let mut settings: ShellSettings = state.agent.request(names::settings::GET, &()).await?;
    if settings.available_themes.is_empty() {
        settings.available_themes = list_themes(&state.config);
    }
    Ok(settings)
}

/// `settings_set` → `settings.set` (partial patch, at least one key), then `kiosk://themeChanged` /
/// `kiosk://localeChanged` when those keys were part of the patch.
#[tauri::command]
pub async fn settings_set(
    app: AppHandle,
    state: State<'_, AppState>,
    patch: SettingsSetRequest,
) -> CmdResult<ShellSettings> {
    if patch.is_empty() {
        return Err(ShellError::validation(
            "patch",
            "at least one setting required",
        ));
    }
    validate::optional_range("volume", patch.volume, 0, 100)?;
    validate::optional_range(
        "idleTimeoutSec",
        patch.idle_timeout_sec,
        0,
        IDLE_TIMEOUT_MAX,
    )?;
    if let Some(theme) = patch.theme.as_deref() {
        validate::bare_name("theme", theme)?;
        if !list_themes(&state.config).iter().any(|t| t == theme) {
            return Err(ShellError::validation("theme", "not installed"));
        }
    }

    let settings: ShellSettings = state.agent.request(names::settings::SET, &patch).await?;
    tracing::info!(theme = %settings.theme, locale = %settings.locale, volume = settings.volume, "settings updated");
    if patch.theme.is_some() {
        emit(
            &app,
            THEME_CHANGED_EVENT,
            load_theme(&state.config, &settings.theme),
        );
    }
    if patch.locale.is_some() {
        emit(
            &app,
            LOCALE_CHANGED_EVENT,
            SysSetLocaleResponse {
                locale: settings.locale,
            },
        );
    }
    Ok(settings)
}

/// `settings_get_theme` (local): `themes\<name>.json`, default `shell.json → theme`; embedded
/// default when the file is missing.
#[tauri::command]
pub async fn settings_get_theme(
    state: State<'_, AppState>,
    name: Option<String>,
) -> CmdResult<Theme> {
    let name = name
        .map(|n| n.trim().to_owned())
        .filter(|n| !n.is_empty())
        .unwrap_or_else(|| state.config.theme.clone());
    validate::bare_name("name", &name)?;
    Ok(load_theme(&state.config, &name))
}

/// `settings_list_themes` (local): installed theme names.
#[tauri::command]
pub async fn settings_list_themes(state: State<'_, AppState>) -> CmdResult<Vec<String>> {
    Ok(list_themes(&state.config))
}

/// `settings_get_shell_config` (local): the effective `shell.json` (defaults + overlay + env).
/// `kiosk.adminPinHash` is blanked: the webview never needs it and a PIN hash is brute-forceable.
#[tauri::command]
pub async fn settings_get_shell_config(state: State<'_, AppState>) -> CmdResult<ShellConfig> {
    let mut config = state.config.clone();
    config.kiosk.admin_pin_hash = None;
    Ok(config)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn embedded_default_theme_parses() {
        let theme = default_theme();
        assert_eq!(theme.name, DEFAULT_THEME_NAME);
        assert_eq!(theme.version, 1);
    }

    #[test]
    fn themes_are_listed_and_loaded_with_fallback() {
        let dir = std::env::temp_dir().join(format!("clubshell-themes-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&dir).unwrap();
        let mut neon = default_theme();
        neon.name = "neon".into();
        std::fs::write(dir.join("neon.json"), serde_json::to_string(&neon).unwrap()).unwrap();
        std::fs::write(dir.join("broken.json"), "{ not json").unwrap();
        std::fs::write(dir.join("bad name.json"), "{}").unwrap();
        std::fs::write(dir.join("readme.txt"), "x").unwrap();

        assert_eq!(
            list_themes_in(&dir),
            vec!["broken".to_owned(), "default".to_owned(), "neon".to_owned()]
        );
        assert_eq!(
            list_themes_in(&dir.join("missing")),
            vec!["default".to_owned()]
        );
        assert_eq!(load_theme_from(&dir.join("neon.json"), "neon").name, "neon");
        assert_eq!(
            load_theme_from(&dir.join("broken.json"), "broken").name,
            DEFAULT_THEME_NAME
        );
        assert_eq!(
            load_theme_from(&dir.join("nope.json"), "nope").name,
            DEFAULT_THEME_NAME
        );
        std::fs::remove_dir_all(&dir).unwrap();
    }
}
