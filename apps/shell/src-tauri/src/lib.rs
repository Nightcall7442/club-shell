//! ClubShell kiosk shell (`ARCHITECTURE.md` §6.2): Tauri 2 host for the React UI, named-pipe client
//! to the ClubShell Agent, kiosk hardening, gamepad and tray.
//!
//! Startup ([`run`]): `shell.json` → logging → single-instance mutex → Agent transport (pipe, or the
//! mock in dev) → Tauri builder (plugins, [`AppState`], one invoke handler with every command) →
//! `setup` (reconnect supervisor, event forwarder, [`kiosk::spawn_all`]: hardening, gamepad, tray) →
//! event loop. Every exit path goes through [`shutdown`] or the `RunEvent` handler, which drops the
//! hooks and closes the pipe; the Agent watchdog restarts the Shell when the process ends.

pub mod agent;
pub mod commands;
pub mod config;
pub mod gamepad;
pub mod kiosk;
pub mod logging;
pub mod state;
pub mod tray;

use std::sync::atomic::{AtomicBool, Ordering};

use tauri::{AppHandle, Manager, RunEvent, WindowEvent};

use crate::agent::{AgentClient, EventForwarder};
use crate::config::ShellConfig;
use crate::kiosk::window_guard::MAIN_LABEL;
use crate::state::AppState;

/// Name of the process-wide mutex that keeps a second Shell from starting.
pub const SINGLE_INSTANCE_MUTEX: &str = "Global\\ClubShellShell";

/// Set once an exit is under way, so a `Destroyed` main window during shutdown does not request a
/// second exit.
static EXITING: AtomicBool = AtomicBool::new(false);

/// Process entry point (called from `main.rs`).
#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let (config, config_error) = match ShellConfig::load() {
        Ok(config) => (config, None),
        Err(e) => {
            eprintln!("clubshell-shell: {e}; continuing with the embedded defaults");
            (ShellConfig::defaults(), Some(e))
        }
    };
    // Alive until the event loop returns, so the writer thread flushes on exit.
    let _log_guard = logging::init_logging(&config);
    if let Some(e) = config_error {
        tracing::error!(error = %e, "shell.json rejected; running with embedded defaults");
    }

    if !config.is_dev() && !acquire_single_instance() {
        tracing::warn!("another ClubShell instance is running; focusing it and exiting");
        focus_existing_instance();
        return;
    }

    let agent = tauri::async_runtime::block_on(AgentClient::connect(&config));
    tracing::info!(mock = agent.is_mock(), pipe = %config.ipc.pipe_name, "agent transport ready");
    let state = AppState::new(config, agent);

    let setup_state = state.clone();
    let app = tauri::Builder::default()
        .plugin(logging::tauri_log_plugin())
        .plugin(tauri_plugin_process::init())
        .plugin(tauri_plugin_os::init())
        .manage(state.clone())
        .invoke_handler(commands::invoke_handler!(
            kiosk::commands::kiosk_state,
            kiosk::commands::kiosk_set_guard,
            kiosk::commands::kiosk_set_fullscreen,
            kiosk::commands::kiosk_show_overlay,
            kiosk::commands::kiosk_monitors,
            kiosk::commands::kiosk_move_to_monitor,
            kiosk::commands::kiosk_virtual_keyboard,
            kiosk::commands::kiosk_focus,
            kiosk::commands::kiosk_exit,
            kiosk::commands::kiosk_reload,
            kiosk::commands::kiosk_open_devtools,
            kiosk::commands::kiosk_gamepad_state,
            kiosk::commands::kiosk_idle_reset,
            kiosk::commands::kiosk_i18n_bundle,
            kiosk::commands::kiosk_asset_url,
        ))
        .setup(move |app| {
            let handle = app.handle().clone();
            setup_state.agent.start(None);
            EventForwarder::spawn(handle.clone(), setup_state.clone());
            let kiosk = kiosk::spawn_all(&handle, &setup_state)?;
            if !kiosk.is_dev() && !kiosk.window().focus() {
                tracing::warn!("main window did not take the foreground at startup");
            }
            app.manage(kiosk);
            tracing::info!(version = env!("CARGO_PKG_VERSION"), "shell started");
            Ok(())
        })
        .build(tauri::generate_context!());

    let app = match app {
        Ok(app) => app,
        Err(e) => {
            tracing::error!(error = %e, "cannot build the Tauri application");
            std::process::exit(1);
        }
    };

    app.run(move |app, event| match event {
        RunEvent::WindowEvent { label, event: WindowEvent::Destroyed, .. } if label == MAIN_LABEL => {
            if !EXITING.load(Ordering::Acquire) {
                // The kiosk window is gone (webview crash): leave cleanly so the Agent watchdog restarts us.
                tracing::error!("main window destroyed; shutting down");
                shutdown(app);
            }
        }
        RunEvent::ExitRequested { code, .. } => {
            EXITING.store(true, Ordering::Release);
            tracing::info!(code = ?code, "exit requested");
            cleanup(&state);
        }
        RunEvent::Exit => {
            cleanup(&state);
            tracing::info!("shell exited");
        }
        _ => {}
    });
}

/// Orderly shutdown from anywhere (tray, admin exit): drops the kiosk guards, closes the pipe and
/// ends the event loop. Idempotent.
pub fn shutdown(app: &AppHandle) {
    EXITING.store(true, Ordering::Release);
    if let Some(state) = app.try_state::<AppState>() {
        cleanup(&state);
    }
    app.exit(0);
}

fn cleanup(state: &AppState) {
    state.kiosk().shutdown();
    state.agent.shutdown();
}

/// Creates [`SINGLE_INSTANCE_MUTEX`]; `false` when another process already owns it. The handle is
/// intentionally never closed: the OS releases it when the process ends.
#[cfg(windows)]
#[allow(unsafe_code)]
fn acquire_single_instance() -> bool {
    use windows::core::PCWSTR;
    use windows::Win32::Foundation::{GetLastError, BOOL, ERROR_ALREADY_EXISTS};
    use windows::Win32::System::Threading::CreateMutexW;

    let name: Vec<u16> = SINGLE_INSTANCE_MUTEX.encode_utf16().chain(std::iter::once(0)).collect();
    // SAFETY: `name` is a NUL-terminated UTF-16 buffer that outlives the call; no security attributes.
    let created = unsafe { CreateMutexW(None, BOOL::from(false), PCWSTR(name.as_ptr())) };
    match created {
        Ok(_handle) => {
            // SAFETY: no preconditions; reads the calling thread's last-error value right after the call.
            let last_error = unsafe { GetLastError() };
            last_error != ERROR_ALREADY_EXISTS
        }
        Err(e) => {
            tracing::warn!(error = %e, "cannot create the single-instance mutex; continuing");
            true
        }
    }
}

#[cfg(not(windows))]
fn acquire_single_instance() -> bool {
    true
}

/// Brings the running instance's window to the front before this process exits.
fn focus_existing_instance() {
    match clubshell_winutil::window::find_window(None, Some("ClubShell")) {
        Ok(Some(hwnd)) => {
            if let Err(e) = clubshell_winutil::window::force_foreground(hwnd) {
                tracing::debug!(error = %e, "cannot focus the running instance");
            }
        }
        Ok(None) => tracing::debug!("running instance has no visible window yet"),
        Err(e) => tracing::debug!(error = %e, "window lookup failed"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Every command path (proxy + kiosk) is type-checked by the `invoke_handler!` call in `run`.
    #[test]
    fn second_mutex_acquisition_loses_on_windows() {
        let _first = acquire_single_instance();
        assert_eq!(acquire_single_instance(), !cfg!(windows));
        assert!(SINGLE_INSTANCE_MUTEX.starts_with("Global\\"));
    }
}
