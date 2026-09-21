//! Main-window guard: fullscreen borderless on the primary monitor, close prevention, foreground
//! re-assertion on focus loss (honouring a process allowlist: the running game, `TabTip.exe`),
//! a periodic sweep that minimizes stray windows of non-allowlisted processes, screen-capture
//! affinity, and the `kiosk://focus` event (`TAURI_COMMANDS.md` §3.2). Dev mode turns the window into
//! a normal resizable one and disables every guard.

use std::collections::HashSet;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Weak};
use std::time::Duration;

use clubshell_winutil::window::{enumerate_windows, force_foreground, foreground_window, set_window_display_affinity, window_pid};
use clubshell_winutil::Rect;
use parking_lot::{Mutex, RwLock};
use serde::Serialize;
use tauri::async_runtime::JoinHandle;
use tauri::{AppHandle, Emitter, LogicalSize, Manager, PhysicalPosition, PhysicalSize, WebviewWindow, WindowEvent};

use crate::config::ShellConfig;
use crate::state::{CmdResult, ShellError};

/// Event name (`TAURI_COMMANDS.md` §3.2).
pub const FOCUS_EVENT: &str = "kiosk://focus";
/// Label of the kiosk window (`tauri.conf.json`).
pub const MAIN_LABEL: &str = "main";
/// Period of the stray-window sweep.
pub const SWEEP_INTERVAL: Duration = Duration::from_secs(3);
/// Delay before re-taking the foreground after a focus loss (lets transient popups settle).
const REFOCUS_DELAY: Duration = Duration::from_millis(200);
const DEV_WINDOW_SIZE: (f64, f64) = (1280.0, 800.0);
/// Window classes never minimized: desktop/shell, our display watcher, the touch keyboard, system UI.
const SKIP_CLASSES: [&str; 9] = [
    "Shell_TrayWnd",
    "Shell_SecondaryTrayWnd",
    "Progman",
    "WorkerW",
    "ClubShellDisplayWatch",
    "IPTIP_Main_Window",
    "Windows.UI.Core.CoreWindow",
    "XamlExplorerHostIslandWindow",
    "NotifyIconOverflowWindow",
];

/// `kiosk://focus` payload.
#[derive(Serialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct FocusEvent {
    pub has_focus: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub foreground_process: Option<String>,
}

#[cfg(windows)]
mod native {
    use clubshell_winutil::hwnd_from_raw;
    use windows::core::PWSTR;
    use windows::Win32::Foundation::{CloseHandle, BOOL};
    use windows::Win32::System::Threading::{OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION};
    use windows::Win32::UI::WindowsAndMessaging::{IsIconic, ShowWindow, SW_FORCEMINIMIZE};

    pub fn minimize(hwnd: isize) {
        // SAFETY: no preconditions beyond a window handle; SW_FORCEMINIMIZE works across threads.
        let _ = unsafe { ShowWindow(hwnd_from_raw(hwnd), SW_FORCEMINIMIZE) };
    }

    pub fn is_iconic(hwnd: isize) -> bool {
        // SAFETY: no preconditions beyond a window handle.
        unsafe { IsIconic(hwnd_from_raw(hwnd)) }.as_bool()
    }

    /// Executable name (`game.exe`) of `pid`, when accessible.
    pub fn process_name(pid: u32) -> Option<String> {
        // SAFETY: the handle is closed before returning; the buffer is writable and `len` holds its size.
        unsafe {
            let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, BOOL::from(false), pid).ok()?;
            let mut buf = [0u16; 1024];
            let mut len = buf.len() as u32;
            let ok = QueryFullProcessImageNameW(handle, PROCESS_NAME_WIN32, PWSTR(buf.as_mut_ptr()), &mut len).is_ok();
            let _ = CloseHandle(handle);
            if !ok {
                return None;
            }
            let path = String::from_utf16_lossy(&buf[..(len as usize).min(buf.len())]);
            path.rsplit(['\\', '/']).next().filter(|s| !s.is_empty()).map(str::to_owned)
        }
    }
}

#[cfg(not(windows))]
mod native {
    pub fn minimize(_hwnd: isize) {}

    pub fn is_iconic(_hwnd: isize) -> bool {
        false
    }

    pub fn process_name(_pid: u32) -> Option<String> {
        None
    }
}

#[cfg(windows)]
fn raw_hwnd(window: &WebviewWindow) -> isize {
    window.hwnd().map(|h| h.0 as isize).unwrap_or(0)
}

#[cfg(not(windows))]
fn raw_hwnd(_window: &WebviewWindow) -> isize {
    0
}

/// Foreground window's process id (0 when none).
fn foreground_pid() -> u32 {
    let hwnd = foreground_window();
    if hwnd == 0 {
        0
    } else {
        window_pid(hwnd).unwrap_or(0)
    }
}

/// Guard of the `main` window; see the module docs.
pub struct WindowGuard {
    app: AppHandle,
    window: WebviewWindow,
    hwnd: isize,
    own_pid: u32,
    dev: bool,
    fullscreen: AtomicBool,
    /// Guard enabled (`kiosk_set_guard`); off in dev mode.
    active: AtomicBool,
    game_mode: AtomicBool,
    locked: AtomicBool,
    shutting_down: AtomicBool,
    allowed: RwLock<HashSet<u32>>,
    refocus: Mutex<Option<JoinHandle<()>>>,
    sweep: Mutex<Option<JoinHandle<()>>>,
}

impl WindowGuard {
    /// Attaches to the `main` window, applies the kiosk (or dev) window state and starts the sweep.
    pub fn start(app: &AppHandle, config: &ShellConfig, dev: bool, primary: Rect) -> anyhow::Result<Arc<Self>> {
        let window = app.get_webview_window(MAIN_LABEL).ok_or_else(|| anyhow::anyhow!("window '{MAIN_LABEL}' not found"))?;
        let hwnd = raw_hwnd(&window);
        let guard = Arc::new(Self {
            app: app.clone(),
            window,
            hwnd,
            own_pid: std::process::id(),
            dev,
            fullscreen: AtomicBool::new(config.kiosk.fullscreen && !dev),
            active: AtomicBool::new(!dev),
            game_mode: AtomicBool::new(false),
            locked: AtomicBool::new(false),
            shutting_down: AtomicBool::new(false),
            allowed: RwLock::new(HashSet::new()),
            refocus: Mutex::new(None),
            sweep: Mutex::new(None),
        });
        if dev {
            guard.apply_dev_window();
        } else {
            if config.kiosk.fullscreen {
                if let Err(e) = guard.move_to_monitor(primary) {
                    tracing::warn!(error = %e, "cannot place the window on the primary monitor");
                }
            }
            let _ = guard.window.set_always_on_top(true);
            let _ = guard.window.set_skip_taskbar(true);
        }
        let weak: Weak<Self> = Arc::downgrade(&guard);
        guard.window.on_window_event(move |event| {
            if let Some(guard) = weak.upgrade() {
                guard.on_event(event);
            }
        });
        guard.spawn_sweep();
        tracing::info!(hwnd, dev, fullscreen = guard.is_fullscreen(), "window guard started");
        Ok(guard)
    }

    pub fn window(&self) -> &WebviewWindow {
        &self.window
    }

    /// Raw `HWND` of the main window (0 off Windows).
    pub fn hwnd(&self) -> isize {
        self.hwnd
    }

    pub fn is_fullscreen(&self) -> bool {
        self.window.is_fullscreen().unwrap_or_else(|_| self.fullscreen.load(Ordering::Acquire))
    }

    /// `kiosk_set_fullscreen`.
    pub fn set_fullscreen(&self, on: bool) -> CmdResult<()> {
        self.window.set_fullscreen(on)?;
        self.fullscreen.store(on, Ordering::Release);
        Ok(())
    }

    pub fn toggle_fullscreen(&self) -> CmdResult<()> {
        self.set_fullscreen(!self.is_fullscreen())
    }

    /// Moves the window to `rect` (a monitor rectangle) keeping the fullscreen state
    /// (`kiosk_move_to_monitor`, display changes).
    pub fn move_to_monitor(&self, rect: Rect) -> CmdResult<()> {
        let w = &self.window;
        w.set_fullscreen(false)?;
        w.set_position(PhysicalPosition::new(rect.left, rect.top))?;
        w.set_size(PhysicalSize::new(u32::try_from(rect.width()).unwrap_or(0), u32::try_from(rect.height()).unwrap_or(0)))?;
        if self.fullscreen.load(Ordering::Acquire) {
            w.set_fullscreen(true)?;
        }
        Ok(())
    }

    /// Brings the Shell window to the front (`kiosk_focus`, game exit). Returns whether it is the
    /// foreground window afterwards (always `true` off Windows).
    pub fn focus(&self) -> bool {
        let _ = self.window.unminimize();
        let _ = self.window.show();
        let _ = self.window.set_focus();
        if self.hwnd == 0 {
            return true;
        }
        match force_foreground(self.hwnd) {
            Ok(ok) => ok,
            Err(e) => {
                tracing::debug!(error = %e, "force_foreground failed");
                false
            }
        }
    }

    /// Guard on/off (`kiosk_set_guard`). Off: no refocus, no sweep.
    pub fn set_active(&self, active: bool) {
        if self.dev {
            return;
        }
        self.active.store(active, Ordering::Release);
    }

    pub fn is_active(&self) -> bool {
        self.active.load(Ordering::Acquire)
    }

    /// Game mode suspends refocus and the sweep; leaving it re-focuses the Shell (when the guard is
    /// active, which the kiosk sets before calling this).
    pub fn set_game_mode(&self, on: bool) {
        self.game_mode.store(on, Ordering::Release);
        if !on && self.should_guard() {
            let _ = self.window.set_always_on_top(true);
            self.focus();
        }
    }

    pub fn is_game_mode(&self) -> bool {
        self.game_mode.load(Ordering::Acquire)
    }

    pub fn set_locked(&self, locked: bool) {
        self.locked.store(locked, Ordering::Release);
    }

    pub fn is_locked(&self) -> bool {
        self.locked.load(Ordering::Acquire)
    }

    /// Lets `pid` own the foreground without being re-focused away or minimized.
    pub fn allow_foreground_pid(&self, pid: u32) {
        if pid != 0 && self.allowed.write().insert(pid) {
            tracing::info!(pid, "foreground allowlist +");
        }
    }

    pub fn disallow_foreground_pid(&self, pid: u32) {
        if self.allowed.write().remove(&pid) {
            tracing::info!(pid, "foreground allowlist -");
        }
    }

    pub fn clear_allowed_pids(&self) {
        let mut allowed = self.allowed.write();
        if !allowed.is_empty() {
            tracing::info!(count = allowed.len(), "foreground allowlist cleared");
            allowed.clear();
        }
    }

    pub fn allowed_pids(&self) -> Vec<u32> {
        self.allowed.read().iter().copied().collect()
    }

    pub fn is_allowed_pid(&self, pid: u32) -> bool {
        pid == self.own_pid || self.allowed.read().contains(&pid)
    }

    /// `WDA_EXCLUDEFROMCAPTURE` on / off (remote-control capture must see the Shell: keep `false`).
    pub fn set_capture_excluded(&self, exclude: bool) -> CmdResult<()> {
        if self.hwnd == 0 {
            return Err(ShellError::unsupported());
        }
        set_window_display_affinity(self.hwnd, exclude)?;
        Ok(())
    }

    /// Allows the window to close, stops the tasks and drops always-on-top.
    pub fn shutdown(&self) {
        self.shutting_down.store(true, Ordering::Release);
        self.active.store(false, Ordering::Release);
        if let Some(task) = self.sweep.lock().take() {
            task.abort();
        }
        if let Some(task) = self.refocus.lock().take() {
            task.abort();
        }
        let _ = self.window.set_always_on_top(false);
    }

    fn apply_dev_window(&self) {
        let w = &self.window;
        let _ = w.set_fullscreen(false);
        let _ = w.set_always_on_top(false);
        let _ = w.set_decorations(true);
        let _ = w.set_resizable(true);
        let _ = w.set_closable(true);
        let _ = w.set_minimizable(true);
        let _ = w.set_maximizable(true);
        let _ = w.set_skip_taskbar(false);
        let _ = w.set_size(LogicalSize::new(DEV_WINDOW_SIZE.0, DEV_WINDOW_SIZE.1));
        let _ = w.center();
        tracing::info!("dev mode: main window is a normal window");
    }

    fn on_event(self: &Arc<Self>, event: &WindowEvent) {
        match event {
            WindowEvent::CloseRequested { api, .. } => {
                if !self.dev && !self.shutting_down.load(Ordering::Acquire) {
                    api.prevent_close();
                    tracing::info!("close request ignored (kiosk)");
                }
            }
            WindowEvent::Focused(focused) => self.on_focus(*focused),
            _ => {}
        }
    }

    fn on_focus(self: &Arc<Self>, focused: bool) {
        let pid = if focused { 0 } else { foreground_pid() };
        let foreground_process = if focused || pid == 0 { None } else { native::process_name(pid) };
        tracing::debug!(has_focus = focused, pid, process = ?foreground_process, "focus changed");
        if let Err(e) = self.app.emit(FOCUS_EVENT, FocusEvent { has_focus: focused, foreground_process }) {
            tracing::warn!(error = %e, "cannot emit kiosk://focus");
        }
        if focused || !self.should_guard() || self.is_allowed_pid(pid) {
            return;
        }
        let me = Arc::clone(self);
        let task = tauri::async_runtime::spawn(async move {
            tokio::time::sleep(REFOCUS_DELAY).await;
            me.refocus_if_needed();
        });
        if let Some(previous) = self.refocus.lock().replace(task) {
            previous.abort();
        }
    }

    /// Guarding is wanted: kiosk mode, guard active, no game, not exiting.
    fn should_guard(&self) -> bool {
        !self.dev && self.is_active() && !self.is_game_mode() && !self.shutting_down.load(Ordering::Acquire)
    }

    fn refocus_if_needed(&self) {
        if !self.should_guard() {
            return;
        }
        let pid = foreground_pid();
        if self.is_allowed_pid(pid) {
            return;
        }
        let ok = self.focus();
        tracing::debug!(pid, ok, "foreground re-asserted");
    }

    fn spawn_sweep(self: &Arc<Self>) {
        let weak: Weak<Self> = Arc::downgrade(self);
        let task = tauri::async_runtime::spawn(async move {
            let mut tick = tokio::time::interval(SWEEP_INTERVAL);
            tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
            loop {
                tick.tick().await;
                let Some(guard) = weak.upgrade() else { break };
                if guard.should_guard() {
                    guard.sweep_stray_windows();
                }
            }
        });
        *self.sweep.lock() = Some(task);
    }

    /// Minimizes visible, titled top-level windows of processes that are neither us nor allowlisted.
    /// Returns how many were minimized.
    pub fn sweep_stray_windows(&self) -> usize {
        let Ok(windows) = enumerate_windows() else { return 0 };
        let mut count = 0;
        for w in windows {
            if w.hwnd == self.hwnd || !w.visible || w.title.is_empty() || self.is_allowed_pid(w.pid) {
                continue;
            }
            if SKIP_CLASSES.iter().any(|c| c.eq_ignore_ascii_case(&w.class)) || native::is_iconic(w.hwnd) {
                continue;
            }
            tracing::info!(pid = w.pid, title = %w.title, class = %w.class, "stray window minimized");
            native::minimize(w.hwnd);
            count += 1;
        }
        count
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn focus_event_wire_shape() {
        let json = serde_json::to_value(FocusEvent { has_focus: false, foreground_process: Some("game.exe".into()) }).unwrap();
        assert_eq!(json, serde_json::json!({ "hasFocus": false, "foregroundProcess": "game.exe" }));
        let json = serde_json::to_value(FocusEvent { has_focus: true, foreground_process: None }).unwrap();
        assert_eq!(json, serde_json::json!({ "hasFocus": true }));
        assert!(SKIP_CLASSES.iter().any(|c| c.eq_ignore_ascii_case("shell_traywnd")));
    }
}
