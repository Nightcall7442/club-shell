//! Shell configuration (`ARCHITECTURE.md` §12.2, `config/shell.default.json`).
//!
//! Resolution order: embedded defaults → `<data dir>\shell.json` overlay (deep-merged, so a partial
//! file is fine) → `CLUBSHELL_*` environment overrides. The JSON shape is `shell.json` exactly;
//! everything that is not part of the file (data directory, token path, dev mode) lives in
//! [`RuntimeConfig`] and is skipped by serde, so `settings_get_shell_config` never leaks it.

use std::path::{Path, PathBuf};
use std::time::Duration;

use clubshell_protocol::user::Locale;
use serde::{Deserialize, Serialize};
use serde_json::Value;

/// `config/shell.default.json`, shipped inside the binary (path is relative to this source file).
pub const DEFAULT_JSON: &str = include_str!("../../../../config/shell.default.json");

/// File name of the admin-written overlay inside the data directory.
pub const SHELL_JSON: &str = "shell.json";

/// Environment overrides (`ARCHITECTURE.md` §9).
pub mod env {
    /// Root of the data directory (default `%ProgramData%\ClubShell`).
    pub const DATA_DIR: &str = "CLUBSHELL_DATA_DIR";
    /// Pipe name (short or full), overrides `ipc.pipeName`.
    pub const PIPE: &str = "CLUBSHELL_PIPE";
    /// Path of the shell token file (default `<data dir>\secure\shell.token`).
    pub const SHELL_TOKEN: &str = "CLUBSHELL_SHELL_TOKEN";
    /// UI locale (`en` | `ru` | `uz`), overrides `locale`.
    pub const LOCALE: &str = "CLUBSHELL_LOCALE";
    /// `1` / `true` → mock mode: no Agent, canned IPC responses.
    pub const DEV: &str = "CLUBSHELL_DEV";
}

/// Configuration loading failure.
#[derive(Debug, thiserror::Error)]
pub enum ConfigError {
    #[error("cannot read {path}: {source}")]
    Io {
        path: PathBuf,
        #[source]
        source: std::io::Error,
    },
    #[error("invalid JSON in {path}: {source}")]
    Json {
        path: PathBuf,
        #[source]
        source: serde_json::Error,
    },
    #[error("invalid configuration: {0}")]
    Invalid(String),
    #[error("shell token {path}: {reason}")]
    Token { path: PathBuf, reason: String },
}

/// `shell.json` (`ARCHITECTURE.md` §12.2).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ShellConfig {
    pub version: i32,
    pub locale: Locale,
    /// Theme file name in `themes\` without `.json`.
    pub theme: String,
    pub ipc: IpcConfig,
    pub kiosk: KioskConfig,
    pub idle: IdleConfig,
    pub ads: AdsConfig,
    pub gamepad: GamepadConfig,
    pub monitors: MonitorsConfig,
    pub ui: UiConfig,
    pub features: FeaturesConfig,
    pub sound: SoundConfig,
    pub logging: LoggingConfig,
    pub devtools: bool,
    /// Not part of the file.
    #[serde(skip)]
    pub runtime: RuntimeConfig,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct IpcConfig {
    pub pipe_name: String,
    pub connect_timeout_ms: u64,
    pub request_timeout_ms: u64,
    pub reconnect_min_ms: u64,
    pub reconnect_max_ms: u64,
}

impl IpcConfig {
    pub fn connect_timeout(&self) -> Duration {
        Duration::from_millis(self.connect_timeout_ms)
    }

    pub fn request_timeout(&self) -> Duration {
        Duration::from_millis(self.request_timeout_ms)
    }

    pub fn reconnect_min(&self) -> Duration {
        Duration::from_millis(self.reconnect_min_ms)
    }

    pub fn reconnect_max(&self) -> Duration {
        Duration::from_millis(self.reconnect_max_ms)
    }
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct KioskConfig {
    pub fullscreen: bool,
    pub topmost_guard: bool,
    pub hide_taskbar: bool,
    pub block_alt_tab: bool,
    pub block_win_key: bool,
    /// Informational; Ctrl+Alt+Del cannot be blocked by a hook.
    pub block_ctrl_alt_del: bool,
    /// `0` = never.
    pub hide_cursor_after_sec: u32,
    pub overlay_on_lock: bool,
    pub allow_virtual_keyboard: bool,
    /// Opens the admin PIN dialog; empty = disabled.
    pub exit_hotkey: String,
    /// sha256 hex; `None` → PIN validated by the Agent (`sys.unlockAdmin`).
    pub admin_pin_hash: Option<String>,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct IdleConfig {
    pub timeout_sec: u32,
    pub dim_after_sec: u32,
    pub screensaver_after_sec: u32,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct AdsConfig {
    pub enabled: bool,
    pub interval_sec: u32,
    pub duration_sec: u32,
    /// Image/video URLs; empty = server-driven via `shell.command{showAds}`.
    pub playlist: Vec<String>,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct GamepadConfig {
    pub enabled: bool,
    pub poll_ms: u64,
    pub deadzone: f32,
    pub navigation: bool,
}

#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum SecondaryMode {
    Black,
    Mirror,
    Wallpaper,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MonitorsConfig {
    pub primary_index: usize,
    pub secondary_mode: SecondaryMode,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct CurrencyFormat {
    pub locale: String,
    pub minor_digits: u32,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct UiConfig {
    pub default_route: String,
    pub grid_columns: u32,
    pub show_clock: bool,
    pub clock_format: String,
    pub show_metrics_overlay: bool,
    pub show_session_bar: bool,
    pub cover_aspect: String,
    pub currency_format: CurrencyFormat,
}

#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct FeaturesConfig {
    pub shop: bool,
    pub chat: bool,
    pub booking: bool,
    pub tournaments: bool,
    pub profile: bool,
    pub topup: bool,
    pub apps: bool,
    pub call_admin: bool,
}

#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SoundConfig {
    pub ui_sounds: bool,
    pub default_volume: u32,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct LoggingConfig {
    /// `trace` | `debug` | `info` | `warn` | `error`.
    pub level: String,
    /// Log directory, absolute or relative to the data directory.
    pub directory: String,
}

/// Process-level settings that are not part of `shell.json`.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct RuntimeConfig {
    /// `C:\ProgramData\ClubShell` (or `CLUBSHELL_DATA_DIR`).
    pub data_dir: PathBuf,
    /// `secure\shell.token` (or `CLUBSHELL_SHELL_TOKEN`).
    pub token_path: PathBuf,
    /// Mock mode without the Agent (`CLUBSHELL_DEV=1`).
    pub dev: bool,
    /// The overlay file that was actually applied, if it existed.
    pub overlay_path: Option<PathBuf>,
}

/// `%ProgramData%\ClubShell`, overridable with `CLUBSHELL_DATA_DIR`.
pub fn default_data_dir() -> PathBuf {
    if let Some(dir) = non_empty_env(env::DATA_DIR) {
        return PathBuf::from(dir);
    }
    let program_data = non_empty_env("ProgramData").unwrap_or_else(|| r"C:\ProgramData".to_owned());
    Path::new(&program_data).join("ClubShell")
}

fn non_empty_env(name: &str) -> Option<String> {
    std::env::var(name)
        .ok()
        .map(|v| v.trim().to_owned())
        .filter(|v| !v.is_empty())
}

fn env_flag(name: &str) -> bool {
    non_empty_env(name)
        .is_some_and(|v| matches!(v.to_ascii_lowercase().as_str(), "1" | "true" | "yes" | "on"))
}

/// Deep-merges `overlay` into `base`: objects merge key by key, everything else is replaced.
pub fn merge_json(base: &mut Value, overlay: Value) {
    match (base, overlay) {
        (Value::Object(dst), Value::Object(src)) => {
            for (key, value) in src {
                match dst.get_mut(&key) {
                    Some(existing) => merge_json(existing, value),
                    None => {
                        dst.insert(key, value);
                    }
                }
            }
        }
        (dst, src) => *dst = src,
    }
}

impl ShellConfig {
    /// Embedded defaults only (no overlay, no environment), with the default data directory.
    pub fn defaults() -> Self {
        let mut config: ShellConfig =
            serde_json::from_str(DEFAULT_JSON).expect("embedded shell.default.json is valid");
        let data_dir = default_data_dir();
        config.runtime = RuntimeConfig {
            token_path: data_dir.join("secure").join("shell.token"),
            data_dir,
            dev: false,
            overlay_path: None,
        };
        config
    }

    /// Defaults → `<data dir>\shell.json` → `CLUBSHELL_*`, validated.
    pub fn load() -> Result<Self, ConfigError> {
        Self::load_from(&default_data_dir())
    }

    /// [`load`](Self::load) with an explicit data directory.
    pub fn load_from(data_dir: &Path) -> Result<Self, ConfigError> {
        let mut value: Value =
            serde_json::from_str(DEFAULT_JSON).map_err(|source| ConfigError::Json {
                path: PathBuf::from("<embedded shell.default.json>"),
                source,
            })?;

        let overlay_path = data_dir.join(SHELL_JSON);
        let overlay_applied = match std::fs::read_to_string(&overlay_path) {
            Ok(text) => {
                let overlay: Value =
                    serde_json::from_str(&text).map_err(|source| ConfigError::Json {
                        path: overlay_path.clone(),
                        source,
                    })?;
                merge_json(&mut value, overlay);
                true
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => false,
            Err(source) => {
                return Err(ConfigError::Io {
                    path: overlay_path,
                    source,
                })
            }
        };

        if let Some(pipe) = non_empty_env(env::PIPE) {
            if let Some(ipc) = value.get_mut("ipc").and_then(Value::as_object_mut) {
                ipc.insert("pipeName".to_owned(), Value::String(pipe));
            }
        }
        if let Some(locale) = non_empty_env(env::LOCALE) {
            if let Some(root) = value.as_object_mut() {
                root.insert(
                    "locale".to_owned(),
                    Value::String(locale.to_ascii_lowercase()),
                );
            }
        }

        let mut config: ShellConfig =
            serde_json::from_value(value).map_err(|source| ConfigError::Json {
                path: overlay_path.clone(),
                source,
            })?;
        config.runtime = RuntimeConfig {
            data_dir: data_dir.to_path_buf(),
            token_path: non_empty_env(env::SHELL_TOKEN)
                .map(PathBuf::from)
                .unwrap_or_else(|| data_dir.join("secure").join("shell.token")),
            dev: env_flag(env::DEV),
            overlay_path: overlay_applied.then_some(overlay_path),
        };
        config.validate()?;
        Ok(config)
    }

    /// Rejects values that would break the runtime (zero timeouts, inverted backoff, bad level, …).
    pub fn validate(&self) -> Result<(), ConfigError> {
        let invalid = |msg: String| Err(ConfigError::Invalid(msg));
        if self.version != 1 {
            return invalid(format!("version {} unsupported (expected 1)", self.version));
        }
        if self.ipc.pipe_name.trim().is_empty() {
            return invalid("ipc.pipeName is empty".into());
        }
        if self.ipc.connect_timeout_ms == 0 || self.ipc.request_timeout_ms == 0 {
            return invalid("ipc.connectTimeoutMs and ipc.requestTimeoutMs must be > 0".into());
        }
        if self.ipc.reconnect_min_ms == 0 || self.ipc.reconnect_min_ms > self.ipc.reconnect_max_ms {
            return invalid("ipc.reconnectMinMs must be > 0 and <= ipc.reconnectMaxMs".into());
        }
        if self.theme.trim().is_empty() || self.theme.contains(['/', '\\', '.']) {
            return invalid(format!("theme '{}' must be a bare file name", self.theme));
        }
        if self.gamepad.poll_ms == 0 {
            return invalid("gamepad.pollMs must be > 0".into());
        }
        if !(0.0..1.0).contains(&self.gamepad.deadzone) {
            return invalid("gamepad.deadzone must be in [0, 1)".into());
        }
        if self.ui.grid_columns == 0 {
            return invalid("ui.gridColumns must be > 0".into());
        }
        if self.sound.default_volume > 100 {
            return invalid("sound.defaultVolume must be 0..=100".into());
        }
        if self.logging.level.parse::<tracing::Level>().is_err() {
            return invalid(format!(
                "logging.level '{}' is not trace|debug|info|warn|error",
                self.logging.level
            ));
        }
        if self.logging.directory.trim().is_empty() {
            return invalid("logging.directory is empty".into());
        }
        Ok(())
    }

    /// Reads and validates the shell token (64 hex chars, regenerated at every Agent start, so
    /// call this on every `auth.hello`, never cache it).
    pub fn load_shell_token(&self) -> Result<String, ConfigError> {
        let path = &self.runtime.token_path;
        let text = std::fs::read_to_string(path).map_err(|source| ConfigError::Io {
            path: path.clone(),
            source,
        })?;
        let token = text.trim().to_owned();
        if token.len() != 64 || !token.bytes().all(|b| b.is_ascii_hexdigit()) {
            return Err(ConfigError::Token {
                path: path.clone(),
                reason: "expected 64 hex characters".into(),
            });
        }
        Ok(token)
    }

    pub fn is_dev(&self) -> bool {
        self.runtime.dev
    }

    pub fn data_dir(&self) -> &Path {
        &self.runtime.data_dir
    }

    /// `logging.directory`, resolved against the data directory when relative.
    pub fn logs_dir(&self) -> PathBuf {
        self.resolve(&self.logging.directory)
    }

    pub fn themes_dir(&self) -> PathBuf {
        self.runtime.data_dir.join("themes")
    }

    pub fn theme_path(&self, name: &str) -> PathBuf {
        self.themes_dir().join(format!("{name}.json"))
    }

    pub fn media_dir(&self) -> PathBuf {
        self.runtime.data_dir.join("cache").join("media")
    }

    pub fn locales_dir(&self) -> PathBuf {
        self.runtime.data_dir.join("locales")
    }

    pub fn shell_json_path(&self) -> PathBuf {
        self.runtime.data_dir.join(SHELL_JSON)
    }

    fn resolve(&self, p: &str) -> PathBuf {
        let path = Path::new(p);
        if path.is_absolute() {
            path.to_path_buf()
        } else {
            self.runtime.data_dir.join(path)
        }
    }
}

impl Default for ShellConfig {
    fn default() -> Self {
        Self::defaults()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn defaults_parse_and_validate() {
        let cfg = ShellConfig::defaults();
        assert_eq!(cfg.version, 1);
        assert_eq!(cfg.locale, Locale::Ru);
        assert_eq!(cfg.ipc.pipe_name, "clubshell-agent");
        assert_eq!(cfg.ipc.request_timeout(), Duration::from_millis(15_000));
        assert_eq!(cfg.monitors.secondary_mode, SecondaryMode::Black);
        assert!(cfg.validate().is_ok());
        // Round-trips to the same JSON shape (runtime is skipped).
        let json = serde_json::to_value(&cfg).unwrap();
        assert!(json.get("runtime").is_none());
        assert_eq!(json["kiosk"]["adminPinHash"], Value::Null);
    }

    #[test]
    fn overlay_and_env_apply() {
        let dir = std::env::temp_dir().join(format!("clubshell-cfg-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(dir.join(SHELL_JSON), r#"{ "theme": "neon", "ipc": { "requestTimeoutMs": 500 }, "features": { "shop": false } }"#).unwrap();
        let cfg = ShellConfig::load_from(&dir).unwrap();
        assert_eq!(cfg.theme, "neon");
        assert_eq!(cfg.ipc.request_timeout_ms, 500);
        assert_eq!(
            cfg.ipc.connect_timeout_ms, 3000,
            "untouched keys keep defaults"
        );
        assert!(!cfg.features.shop);
        assert!(cfg.features.chat);
        assert_eq!(
            cfg.runtime.overlay_path.as_deref(),
            Some(dir.join(SHELL_JSON).as_path())
        );
        assert_eq!(cfg.logs_dir(), dir.join("logs"));
        assert_eq!(
            cfg.runtime.token_path,
            dir.join("secure").join("shell.token")
        );

        std::fs::write(
            dir.join(SHELL_JSON),
            r#"{ "ipc": { "reconnectMinMs": 9000 } }"#,
        )
        .unwrap();
        assert!(matches!(
            ShellConfig::load_from(&dir),
            Err(ConfigError::Invalid(_))
        ));
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn token_must_be_64_hex() {
        let dir = std::env::temp_dir().join(format!("clubshell-tok-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&dir).unwrap();
        let mut cfg = ShellConfig::defaults();
        cfg.runtime.token_path = dir.join("shell.token");
        assert!(matches!(
            cfg.load_shell_token(),
            Err(ConfigError::Io { .. })
        ));
        std::fs::write(&cfg.runtime.token_path, "abc").unwrap();
        assert!(matches!(
            cfg.load_shell_token(),
            Err(ConfigError::Token { .. })
        ));
        std::fs::write(&cfg.runtime.token_path, format!("{}\r\n", "ab".repeat(32))).unwrap();
        assert_eq!(cfg.load_shell_token().unwrap(), "ab".repeat(32));
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn merge_is_deep() {
        let mut base = serde_json::json!({ "a": { "x": 1, "y": 2 }, "b": [1] });
        merge_json(
            &mut base,
            serde_json::json!({ "a": { "y": 3, "z": 4 }, "b": [2, 3] }),
        );
        assert_eq!(
            base,
            serde_json::json!({ "a": { "x": 1, "y": 3, "z": 4 }, "b": [2, 3] })
        );
    }
}
