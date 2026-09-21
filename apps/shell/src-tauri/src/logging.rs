//! Shell logging (ARCHITECTURE.md §10): `tracing` → JSON lines in a daily-rolling file under
//! `<data dir>\logs\` (`shell.YYYY-MM-DD.log`), plus stderr in dev / debug builds. The `log` façade
//! is bridged into the same subscriber, which is how `tauri-plugin-log` (webview `log()` calls) and
//! `tao`/`wry` records land in the same file. A panic hook records the panic through `tracing` and,
//! because release builds abort on panic before the non-blocking writer can flush, appends it
//! synchronously to `logs\shell-crash.log` as well.

use std::io::Write;
use std::path::Path;

use tracing_appender::non_blocking::WorkerGuard;
use tracing_appender::rolling::{RollingFileAppender, Rotation};
use tracing_subscriber::layer::SubscriberExt;
use tracing_subscriber::util::SubscriberInitExt;
use tracing_subscriber::EnvFilter;

use crate::config::ShellConfig;

/// Daily files kept before the oldest is deleted.
pub const MAX_LOG_FILES: usize = 14;

/// File that receives panics synchronously.
pub const CRASH_LOG: &str = "shell-crash.log";

/// Installs the global subscriber (idempotent: a second call only logs a warning). Keep the
/// returned guard alive for the process lifetime; dropping it flushes and stops the writer thread.
/// `RUST_LOG` overrides `logging.level` when set.
pub fn init_logging(config: &ShellConfig) -> WorkerGuard {
    let logs_dir = config.logs_dir();
    let filter = env_filter(&config.logging.level);
    let (writer, guard, file_ok) = match file_appender(&logs_dir) {
        Ok(appender) => {
            let (writer, guard) = tracing_appender::non_blocking(appender);
            (writer, guard, true)
        }
        Err(e) => {
            eprintln!(
                "clubshell-shell: cannot open log directory {}: {e}; logging to stderr only",
                logs_dir.display()
            );
            let (writer, guard) = tracing_appender::non_blocking(std::io::stderr());
            (writer, guard, false)
        }
    };

    let file_layer = tracing_subscriber::fmt::layer()
        .json()
        .flatten_event(true)
        .with_current_span(false)
        .with_span_list(false)
        .with_target(true)
        .with_writer(writer);
    let stderr_layer = (config.is_dev() || cfg!(debug_assertions)).then(|| {
        tracing_subscriber::fmt::layer()
            .with_ansi(false)
            .with_target(true)
            .with_writer(std::io::stderr)
    });

    // `init` also installs the `log` → `tracing` bridge (tracing-subscriber's `tracing-log` feature).
    if let Err(e) = tracing_subscriber::registry()
        .with(filter)
        .with(file_layer)
        .with(stderr_layer)
        .try_init()
    {
        tracing::warn!(error = %e, "logging already initialised; keeping the existing subscriber");
        return guard;
    }
    install_panic_hook(logs_dir.join(CRASH_LOG));
    tracing::info!(
        version = env!("CARGO_PKG_VERSION"),
        level = %config.logging.level,
        dir = %logs_dir.display(),
        file = file_ok,
        dev = config.is_dev(),
        overlay = ?config.runtime.overlay_path,
        "logging initialised"
    );
    guard
}

/// `tauri-plugin-log` configured to forward webview logs to the global `log` logger (our bridge)
/// instead of installing its own; register with `tauri::Builder::plugin(logging::tauri_log_plugin())`
/// after [`init_logging`].
pub fn tauri_log_plugin<R: tauri::Runtime>() -> tauri::plugin::TauriPlugin<R> {
    tauri_plugin_log::Builder::new().skip_logger().build()
}

fn env_filter(level: &str) -> EnvFilter {
    if let Ok(filter) = EnvFilter::try_from_default_env() {
        return filter;
    }
    let directives = format!("{level},tao=warn,wry=warn,hyper=warn,tokio=warn,mio=warn,gilrs=warn");
    EnvFilter::try_new(directives).unwrap_or_else(|_| EnvFilter::new("info"))
}

fn file_appender(dir: &Path) -> std::io::Result<RollingFileAppender> {
    std::fs::create_dir_all(dir)?;
    RollingFileAppender::builder()
        .rotation(Rotation::DAILY)
        .filename_prefix("shell")
        .filename_suffix("log")
        .max_log_files(MAX_LOG_FILES)
        .build(dir)
        .map_err(|e| std::io::Error::other(e.to_string()))
}

fn install_panic_hook(crash_log: std::path::PathBuf) {
    let previous = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        let payload = info
            .payload()
            .downcast_ref::<&str>()
            .map(|s| (*s).to_owned())
            .or_else(|| info.payload().downcast_ref::<String>().cloned())
            .unwrap_or_else(|| "non-string panic payload".to_owned());
        let location = info
            .location()
            .map(|l| format!("{}:{}:{}", l.file(), l.line(), l.column()))
            .unwrap_or_default();
        tracing::error!(target: "panic", payload = %payload, location = %location, "panic");
        if let Ok(mut file) = std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(&crash_log)
        {
            let _ = writeln!(
                file,
                "{} panic at {location}: {payload}",
                chrono::Utc::now().to_rfc3339()
            );
        }
        previous(info);
    }));
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn env_filter_accepts_config_levels_and_falls_back() {
        assert!(!env_filter("debug").to_string().is_empty());
        // An invalid level in config never panics (validate() rejects it earlier anyway).
        assert!(!env_filter("loud").to_string().is_empty());
    }

    #[test]
    fn file_appender_creates_directory() {
        let dir = std::env::temp_dir().join(format!("clubshell-logs-{}", uuid::Uuid::new_v4()));
        let appender = file_appender(&dir).unwrap();
        drop(appender);
        assert!(dir.is_dir());
        std::fs::remove_dir_all(&dir).unwrap();
    }
}
