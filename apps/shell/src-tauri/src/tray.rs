//! Tray icon (`tauri.conf.json → app.trayIcon`, id `main`): an operator tool, visible only in dev
//! mode or while an admin unlock (`sys.unlockAdmin`) is cached, hidden in kiosk mode. Menu:
//! *Reconnect agent* (drops the pipe so the supervisor reconnects), *Show logs folder*,
//! *Toggle devtools* (debug / `devtools` feature builds) and *Exit* (needs the admin unlock; without it
//! the frontend is asked to open the PIN dialog through `kiosk://hotkey{exit}`).

use std::path::Path;
use std::sync::atomic::{AtomicBool, Ordering};

use tauri::menu::{IsMenuItem, Menu, MenuItem, PredefinedMenuItem};
use tauri::tray::{TrayIcon, TrayIconBuilder};
use tauri::{AppHandle, Emitter, Manager, Wry};

use crate::agent::AgentClient;
use crate::kiosk::keyboard_hook::{HotkeyPayload, HOTKEY_EVENT};
use crate::state::AppState;

const ID_RECONNECT: &str = "reconnect";
const ID_LOGS: &str = "logs";
const ID_DEVTOOLS: &str = "devtools";
const ID_EXIT: &str = "exit";

/// Tray controller.
pub struct Tray {
    icon: TrayIcon<Wry>,
    visible: AtomicBool,
}

impl Tray {
    /// Tray id from `tauri.conf.json`.
    pub const ID: &'static str = "main";

    /// Attaches the menu to the configured tray icon (creating one when the config has none) and
    /// shows it only in dev mode.
    pub fn install(app: &AppHandle, state: AppState) -> anyhow::Result<Self> {
        let dev = state.config.is_dev() || cfg!(debug_assertions);
        let icon = match app.tray_by_id(Self::ID) {
            Some(icon) => icon,
            None => {
                let image = app
                    .default_window_icon()
                    .cloned()
                    .ok_or_else(|| anyhow::anyhow!("no default window icon for the tray"))?;
                TrayIconBuilder::with_id(Self::ID)
                    .icon(image)
                    .tooltip("ClubShell")
                    .build(app)?
            }
        };
        icon.set_menu(Some(build_menu(app)?))?;
        icon.set_tooltip(Some("ClubShell"))?;
        icon.on_menu_event(move |app, event| on_menu(app, &state, event.id().0.as_str()));
        icon.set_visible(dev)?;
        tracing::info!(visible = dev, "tray installed");
        Ok(Self {
            icon,
            visible: AtomicBool::new(dev),
        })
    }

    /// Shows / hides the icon (idempotent).
    pub fn set_visible(&self, visible: bool) {
        if self.visible.swap(visible, Ordering::AcqRel) == visible {
            return;
        }
        if let Err(e) = self.icon.set_visible(visible) {
            tracing::warn!(error = %e, visible, "tray visibility change failed");
        }
    }

    pub fn is_visible(&self) -> bool {
        self.visible.load(Ordering::Acquire)
    }

    /// Hides the icon before exit.
    pub fn remove(&self) {
        self.set_visible(false);
    }
}

fn build_menu(app: &AppHandle) -> tauri::Result<Menu<Wry>> {
    let reconnect = MenuItem::with_id(app, ID_RECONNECT, "Reconnect agent", true, None::<&str>)?;
    let logs = MenuItem::with_id(app, ID_LOGS, "Show logs folder", true, None::<&str>)?;
    let separator = PredefinedMenuItem::separator(app)?;
    let exit = MenuItem::with_id(app, ID_EXIT, "Exit ClubShell", true, None::<&str>)?;
    #[cfg(any(debug_assertions, feature = "devtools"))]
    let devtools = MenuItem::with_id(app, ID_DEVTOOLS, "Toggle devtools", true, None::<&str>)?;

    let mut items: Vec<&dyn IsMenuItem<Wry>> = vec![&reconnect, &logs];
    #[cfg(any(debug_assertions, feature = "devtools"))]
    items.push(&devtools);
    items.push(&separator);
    items.push(&exit);
    Menu::with_items(app, &items)
}

fn on_menu(app: &AppHandle, state: &AppState, id: &str) {
    tracing::info!(id, "tray menu");
    match id {
        ID_RECONNECT => reconnect(state),
        ID_LOGS => open_folder(&state.config.logs_dir()),
        ID_DEVTOOLS => toggle_devtools(app),
        ID_EXIT => exit(app, state),
        _ => {}
    }
}

/// Closes the current pipe client; the supervisor reconnects with backoff.
fn reconnect(state: &AppState) {
    match state.agent.as_ref() {
        AgentClient::Pipe(pipe) => match pipe.transport().client() {
            Some(client) => {
                tracing::info!("reconnect requested; closing pipe");
                client.close();
            }
            None => {
                tracing::info!("reconnect requested; not connected (supervisor already retrying)")
            }
        },
        AgentClient::Mock(_) => {
            tracing::info!("reconnect requested on the mock agent; nothing to do")
        }
    }
}

fn open_folder(dir: &Path) {
    #[cfg(windows)]
    {
        if let Err(e) = std::process::Command::new("explorer.exe").arg(dir).spawn() {
            tracing::warn!(error = %e, dir = %dir.display(), "cannot open logs folder");
        }
    }
    #[cfg(not(windows))]
    {
        tracing::info!(dir = %dir.display(), "logs folder");
    }
}

#[cfg(any(debug_assertions, feature = "devtools"))]
fn toggle_devtools(app: &AppHandle) {
    if let Some(window) = app.get_webview_window(crate::kiosk::window_guard::MAIN_LABEL) {
        if window.is_devtools_open() {
            window.close_devtools();
        } else {
            window.open_devtools();
        }
    }
}

#[cfg(not(any(debug_assertions, feature = "devtools")))]
fn toggle_devtools(_app: &AppHandle) {
    tracing::info!("devtools are not compiled into this build");
}

/// Exits when an admin unlock is cached (or in dev mode); otherwise asks the UI for the PIN dialog.
fn exit(app: &AppHandle, state: &AppState) {
    let dev = state.config.is_dev() || cfg!(debug_assertions);
    if dev || state.has_admin_unlock() {
        tracing::warn!("exit requested from the tray");
        crate::shutdown(app);
    } else if let Err(e) = app.emit(
        HOTKEY_EVENT,
        HotkeyPayload {
            name: "exit",
            combo: "tray".to_owned(),
        },
    ) {
        tracing::warn!(error = %e, "cannot emit kiosk://hotkey");
    }
}
