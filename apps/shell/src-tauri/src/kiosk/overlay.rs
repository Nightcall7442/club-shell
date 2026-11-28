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
//!
//! A click-through kind (a low-time warning, an admin text, ads) is also **never activated** (D-109):
//! tao's `show()` is `ShowWindow(SW_SHOW)`, which activates the window, so a borderless game lost the
//! keyboard focus to every toast (and a game that minimizes on focus loss went to the taskbar). Such
//! a kind is made non-focusable (`WS_EX_NOACTIVATE`) and shown natively with `SW_SHOWNOACTIVATE`
//! on top of the topmost band, on the main thread after tauri's own queued setters. tao then
//! believes the window hidden, so [`Overlay::hide`] hides it natively too.
//!
//! What a window can and cannot cover: a game in a window or in borderless (windowed) fullscreen is
//! composed by DWM, so the topmost overlay is drawn over it. So is most "exclusive" fullscreen of
//! DirectX 10–12 games on Windows 10/11 (fullscreen optimizations turn it into a flip-model
//! borderless swap chain). A game that really owns the display — DirectX 9 exclusive mode, or
//! fullscreen optimizations turned off for it — is not composed: no window shows over it (only an
//! overlay injected into the game's own swap chain could, as Steam or Discord do, which this kiosk
//! does not do: it would trip anti-cheats). There the player still hears the chime
//! ([`super::alert`]) and the HUD hotkey still opens the panel (the game leaves exclusive mode).

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

/// What the `hud` hotkey does from a given overlay state.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum HudAction {
    Show,
    Hide,
    /// An admin lock is up: the player may not toggle it away, and the HUD does not open over it.
    Keep,
}

/// How a kind goes on screen.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Presentation {
    /// Takes the mouse and keyboard: shown and focused (the game loses the focus, on purpose).
    Interactive,
    /// Click-through and never activated: the game keeps the focus and stays in front (D-109).
    Passive,
}

const fn presentation(kind: OverlayKind) -> Presentation {
    match kind {
        OverlayKind::Lock | OverlayKind::Hud => Presentation::Interactive,
        _ => Presentation::Passive,
    }
}

const fn hud_action(current: OverlayKind) -> HudAction {
    match current {
        OverlayKind::Hud => HudAction::Hide,
        OverlayKind::Lock => HudAction::Keep,
        _ => HudAction::Show,
    }
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

    /// Kinds that take pointer and keyboard input (everything else is click-through and passive).
    const fn interactive(kind: OverlayKind) -> bool {
        matches!(presentation(kind), Presentation::Interactive)
    }

    /// Hotkey: hides the HUD when it is up, shows it otherwise (any other overlay kind is replaced).
    pub fn toggle_hud(self: &Arc<Self>) -> CmdResult<()> {
        match hud_action(self.kind()) {
            HudAction::Show => self.show(OverlayKind::Hud, None, Some(HUD_TTL)),
            HudAction::Hide => self.hide(),
            HudAction::Keep => Ok(()),
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
        let interactive = Self::interactive(kind);
        window.set_focusable(interactive)?;
        window.set_ignore_cursor_events(!interactive)?;
        apply_bounds(&window, *self.bounds.read())?;
        if interactive {
            window.show()?;
            window.set_always_on_top(true)?;
            let _ = window.set_focus();
        } else {
            self.show_passive(&window)?;
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
            self.hide_passive(&window)?;
        }
        if previous != OverlayKind::Hidden {
            self.emit(OverlayKind::Hidden, None);
            tracing::info!(was = ?previous, "overlay hidden");
        }
        Ok(())
    }

    /// Shows a click-through kind without activating it (see the module docs). Queued on the main
    /// thread behind the setters above: tauri runs those there, and tao, which thinks the window
    /// hidden, would hide it again if it applied them after this.
    #[cfg(windows)]
    fn show_passive(&self, window: &WebviewWindow) -> CmdResult<()> {
        let hwnd = window.hwnd()?.0 as isize;
        self.app.run_on_main_thread(move || {
            if let Err(e) = clubshell_winutil::window::show_no_activate(hwnd) {
                tracing::warn!(error = %e, "overlay show (no activate) failed");
            }
        })?;
        Ok(())
    }

    #[cfg(not(windows))]
    fn show_passive(&self, window: &WebviewWindow) -> CmdResult<()> {
        window.show()?;
        Ok(())
    }

    /// The native counterpart of [`show_passive`](Self::show_passive): tao's `hide()` does nothing
    /// for a window it did not show itself.
    #[cfg(windows)]
    fn hide_passive(&self, window: &WebviewWindow) -> CmdResult<()> {
        let hwnd = window.hwnd()?.0 as isize;
        self.app
            .run_on_main_thread(move || clubshell_winutil::window::hide_window(hwnd))?;
        Ok(())
    }

    #[cfg(not(windows))]
    fn hide_passive(&self, _window: &WebviewWindow) -> CmdResult<()> {
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

    #[test]
    fn only_the_lock_and_the_hud_take_the_focus_from_a_game() {
        assert_eq!(presentation(OverlayKind::Lock), Presentation::Interactive);
        assert_eq!(presentation(OverlayKind::Hud), Presentation::Interactive);
        // A low-time warning, an admin text and ads never activate: the game keeps the keyboard.
        assert_eq!(presentation(OverlayKind::Message), Presentation::Passive);
        assert_eq!(presentation(OverlayKind::Ads), Presentation::Passive);
    }

    #[test]
    fn hud_hotkey_toggles_and_never_dismisses_a_lock() {
        assert_eq!(hud_action(OverlayKind::Hidden), HudAction::Show);
        assert_eq!(hud_action(OverlayKind::Message), HudAction::Show);
        assert_eq!(hud_action(OverlayKind::Ads), HudAction::Show);
        // Second press closes it.
        assert_eq!(hud_action(OverlayKind::Hud), HudAction::Hide);
        // An admin lock is not a panel the player may toggle away.
        assert_eq!(hud_action(OverlayKind::Lock), HudAction::Keep);
        assert!(
            HUD_TTL.as_secs() >= 30,
            "the backstop must outlast the route's own idle timer"
        );
    }
}
