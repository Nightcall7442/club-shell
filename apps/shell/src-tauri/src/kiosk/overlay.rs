//! Native always-on-top overlay: a second, transparent, lazily created webview window (label
//! `overlay`, `index.html#/overlay`) spanning the whole virtual screen. Used for the admin lock
//! (`shell.command{lock}` with `kiosk.overlayOnLock`), for ads and for session/admin/remote-control
//! toasts while a game owns the primary monitor and the main webview is hidden
//! (`kiosk_show_overlay`, `kiosk://overlay`; `TAURI_COMMANDS.md` §2.14/§3.2).
//!
//! Every `show` broadcasts `kiosk://overlay { kind, payload }` to all windows; the overlay route
//! renders from it. `lock` and `hud` take the mouse and keyboard; every other kind is click-through so
//! the game keeps receiving input. `hud` is the player's in-game quick panel, toggled by the `hud`
//! hotkey and auto-hidden after [`HUD_TTL`] as a backstop should the webview stop responding.

use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use std::time::Duration;

use clubshell_winutil::Rect;
use parking_lot::{Mutex, RwLock};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use tauri::async_runtime::JoinHandle;
use tauri::{
    AppHandle, Emitter, Manager, PhysicalPosition, PhysicalSize, WebviewUrl, WebviewWindow,
    WebviewWindowBuilder,
};

use crate::state::CmdResult;

/// Event name (`TAURI_COMMANDS.md` §3.2).
pub const OVERLAY_EVENT: &str = "kiosk://overlay";
/// Window label.
pub const OVERLAY_LABEL: &str = "overlay";
const OVERLAY_URL: &str = "index.html#/overlay";
/// Backstop auto-hide of the HUD (the route hides itself sooner on inactivity).
pub const HUD_TTL: Duration = Duration::from_secs(60);

/// `kind` on the wire (`"none"` = hidden).
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum OverlayKind {
    Lock,
    Ads,
    Message,
    Hud,
    #[serde(rename = "none")]
    Hidden,
}

/// `kiosk://overlay` payload.
#[derive(Serialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct OverlayEvent {
    pub kind: OverlayKind,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub payload: Option<Value>,
}

/// Controller of the overlay window.
pub struct Overlay {
    app: AppHandle,
    kind: Mutex<OverlayKind>,
    /// Bumped on every show/hide so a stale TTL task never hides a newer overlay.
    generation: AtomicU64,
    bounds: RwLock<Rect>,
    ttl: Mutex<Option<JoinHandle<()>>>,
}

fn apply_bounds(window: &WebviewWindow, rect: Rect) -> CmdResult<()> {
    window.set_position(PhysicalPosition::new(rect.left, rect.top))?;
    window.set_size(PhysicalSize::new(
        u32::try_from(rect.width()).unwrap_or(0),
        u32::try_from(rect.height()).unwrap_or(0),
    ))?;
    Ok(())
}

impl Overlay {
    /// Creates the controller; the window itself is built on the first `show`.
    pub fn new(app: &AppHandle, bounds: Rect) -> Arc<Self> {
        Arc::new(Self {
            app: app.clone(),
            kind: Mutex::new(OverlayKind::Hidden),
            generation: AtomicU64::new(0),
            bounds: RwLock::new(bounds),
            ttl: Mutex::new(None),
        })
    }

    /// Current kind (`Hidden` when not shown).
    pub fn kind(&self) -> OverlayKind {
        *self.kind.lock()
    }

    pub fn is_visible(&self) -> bool {
        self.kind() != OverlayKind::Hidden
    }

    /// Kinds that take pointer and keyboard input (everything else is click-through).
    const fn interactive(kind: OverlayKind) -> bool {
        matches!(kind, OverlayKind::Lock | OverlayKind::Hud)
    }

    /// Hotkey: hides the HUD when it is up, shows it otherwise (any other overlay kind is replaced).
    pub fn toggle_hud(self: &Arc<Self>) -> CmdResult<()> {
        if self.kind() == OverlayKind::Hud {
            self.hide()
        } else {
            self.show(OverlayKind::Hud, None, Some(HUD_TTL))
        }
    }

    /// Existing overlay window, if it was ever created.
    pub fn window(&self) -> Option<WebviewWindow> {
        self.app.get_webview_window(OVERLAY_LABEL)
    }

    fn ensure_window(&self) -> CmdResult<WebviewWindow> {
        if let Some(window) = self.window() {
            return Ok(window);
        }
        let builder = WebviewWindowBuilder::new(
            &self.app,
            OVERLAY_LABEL,
            WebviewUrl::App(OVERLAY_URL.into()),
        )
        .title("ClubShell Overlay")
        .decorations(false)
        .always_on_top(true)
        .skip_taskbar(true)
        .resizable(false)
        .maximizable(false)
        .minimizable(false)
        .focused(false)
        .visible(false)
        .shadow(false);
        // `transparent` needs the private-API feature on macOS; the overlay is a Windows feature anyway.
        #[cfg(not(target_os = "macos"))]
        let builder = builder.transparent(true);
        let window = builder.build()?;
        apply_bounds(&window, *self.bounds.read())?;
        tracing::info!("overlay window created");
        Ok(window)
    }

    /// Shows `kind` with an optional payload for the overlay route and an optional auto-hide TTL.
    /// `Hidden` behaves like [`hide`](Self::hide).
    pub fn show(
        self: &Arc<Self>,
        kind: OverlayKind,
        payload: Option<Value>,
        ttl: Option<Duration>,
    ) -> CmdResult<()> {
        if kind == OverlayKind::Hidden {
            return self.hide();
        }
        let window = self.ensure_window()?;
        window.set_ignore_cursor_events(!Self::interactive(kind))?;
        apply_bounds(&window, *self.bounds.read())?;
        window.show()?;
        window.set_always_on_top(true)?;
        if Self::interactive(kind) {
            let _ = window.set_focus();
        }
        *self.kind.lock() = kind;
        let generation = self.generation.fetch_add(1, Ordering::AcqRel) + 1;
        self.emit(kind, payload);
        self.cancel_ttl();
        if let Some(ttl) = ttl {
            let me = Arc::clone(self);
            let task = tauri::async_runtime::spawn(async move {
                tokio::time::sleep(ttl).await;
                if me.generation.load(Ordering::Acquire) == generation {
                    if let Err(e) = me.hide() {
                        tracing::warn!(error = %e, "overlay auto-hide failed");
                    }
                }
            });
            *self.ttl.lock() = Some(task);
        }
        tracing::info!(
            ?kind,
            ttl_ms = ttl.map(|d| d.as_millis() as u64),
            "overlay shown"
        );
        Ok(())
    }

    /// Hides the overlay (keeps the window for reuse) and broadcasts `kind: "none"` if it was shown.
    pub fn hide(&self) -> CmdResult<()> {
        self.cancel_ttl();
        self.generation.fetch_add(1, Ordering::AcqRel);
        let previous = std::mem::replace(&mut *self.kind.lock(), OverlayKind::Hidden);
        if let Some(window) = self.window() {
            window.hide()?;
        }
        if previous != OverlayKind::Hidden {
            self.emit(OverlayKind::Hidden, None);
            tracing::info!(was = ?previous, "overlay hidden");
        }
        Ok(())
    }

    /// Hides unless the current kind is `keep` (e.g. keep the lock when a game exits).
    pub fn hide_unless(&self, keep: OverlayKind) {
        if self.kind() != keep {
            if let Err(e) = self.hide() {
                tracing::warn!(error = %e, "overlay hide failed");
            }
        }
    }

    /// New virtual-screen rectangle (display change); re-applied when visible.
    pub fn set_bounds(&self, rect: Rect) {
        *self.bounds.write() = rect;
        if self.is_visible() {
            if let Some(window) = self.window() {
                if let Err(e) = apply_bounds(&window, rect) {
                    tracing::warn!(error = %e, "overlay re-layout failed");
                }
            }
        }
    }

    pub fn bounds(&self) -> Rect {
        *self.bounds.read()
    }

    /// Destroys the window.
    pub fn shutdown(&self) {
        self.cancel_ttl();
        *self.kind.lock() = OverlayKind::Hidden;
        if let Some(window) = self.window() {
            let _ = window.destroy();
        }
    }

    fn cancel_ttl(&self) {
        if let Some(task) = self.ttl.lock().take() {
            task.abort();
        }
    }

    fn emit(&self, kind: OverlayKind, payload: Option<Value>) {
        if let Err(e) = self.app.emit(OVERLAY_EVENT, OverlayEvent { kind, payload }) {
            tracing::warn!(error = %e, "cannot emit kiosk://overlay");
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn kind_wire_names() {
        assert_eq!(serde_json::to_value(OverlayKind::Hidden).unwrap(), "none");
        assert_eq!(serde_json::to_value(OverlayKind::Lock).unwrap(), "lock");
        assert_eq!(serde_json::to_value(OverlayKind::Hud).unwrap(), "hud");
        assert!(
            Overlay::interactive(OverlayKind::Hud) && !Overlay::interactive(OverlayKind::Message)
        );
        assert_eq!(
            serde_json::from_value::<OverlayKind>(serde_json::json!("message")).unwrap(),
            OverlayKind::Message
        );
        let ev = serde_json::to_value(OverlayEvent {
            kind: OverlayKind::Ads,
            payload: None,
        })
        .unwrap();
        assert_eq!(ev, serde_json::json!({ "kind": "ads" }));
    }
}
