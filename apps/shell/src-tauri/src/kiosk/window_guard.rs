//! Main-window guard: fullscreen borderless on the primary monitor, close prevention, foreground
//! re-assertion on focus loss (honouring a process allowlist: the running game, `TabTip.exe`, a
//! program the dock or a launch handed the foreground to, with the processes of that program's own
//! folder), a periodic sweep that minimizes stray windows of non-allowlisted processes,
//! screen-capture affinity, and the `kiosk://focus` event (`TAURI_COMMANDS.md` §3.2). Dev mode turns
//! the window into a normal resizable one and disables every guard.
//!
//! While a program is handed the foreground, a quick check ([`HAND_OFF_CHECK`]) brings the Shell back
//! the moment the program is no longer what the player sees: it closed, it was minimized (Windows
//! may leave a minimized window "in front" with nothing on the screen but black), or something
//! outside the allowlist took over.

use std::collections::HashSet;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, OnceLock, Weak};
use std::time::Duration;

use clubshell_winutil::window::{
    enumerate_windows, force_foreground, foreground_window, place_window,
    set_window_display_affinity, window_pid,
};
use clubshell_winutil::Rect;
use parking_lot::{Mutex, RwLock};
use serde::Serialize;
use tauri::async_runtime::JoinHandle;
use tauri::{
    AppHandle, Emitter, LogicalSize, Manager, PhysicalPosition, PhysicalSize, WebviewWindow,
    WindowEvent,
};

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
/// Period of the hand-off check: how soon the Shell is back once the program handed the foreground
/// is minimized or closed (the 3 s sweep left the player looking at a black screen meanwhile).
pub const HAND_OFF_CHECK: Duration = Duration::from_millis(400);
/// Last folder names many programs share: a program in one of them owns no folder ([`own_folder`]).
const SHARED_FOLDERS: [&str; 16] = [
    "windows",
    "system32",
    "syswow64",
    "program files",
    "program files (x86)",
    "programdata",
    "users",
    "appdata",
    "local",
    "locallow",
    "roaming",
    "programs",
    "temp",
    "desktop",
    "downloads",
    "games",
];
const DEV_WINDOW_SIZE: (f64, f64) = (1280.0, 800.0);
/// Window classes never minimized: desktop/shell, our display watcher, the touch keyboard, system UI.
pub(crate) const SKIP_CLASSES: [&str; 9] = [
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
    use windows::Win32::System::Threading::{
        OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32,
        PROCESS_QUERY_LIMITED_INFORMATION,
    };
    use windows::Win32::UI::WindowsAndMessaging::{IsIconic, ShowWindow, SW_FORCEMINIMIZE};

    pub fn minimize(hwnd: isize) {
        // SAFETY: no preconditions beyond a window handle; SW_FORCEMINIMIZE works across threads.
        let _ = unsafe { ShowWindow(hwnd_from_raw(hwnd), SW_FORCEMINIMIZE) };
    }

    pub fn is_iconic(hwnd: isize) -> bool {
        // SAFETY: no preconditions beyond a window handle.
        unsafe { IsIconic(hwnd_from_raw(hwnd)) }.as_bool()
    }

    /// Full image path (`C:\Games\game.exe`) of `pid`, when accessible.
    pub fn process_path(pid: u32) -> Option<String> {
        // SAFETY: the handle is closed before returning; the buffer is writable and `len` holds its size.
        unsafe {
            let handle =
                OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, BOOL::from(false), pid).ok()?;
            let mut buf = [0u16; 1024];
            let mut len = buf.len() as u32;
            let ok = QueryFullProcessImageNameW(
                handle,
                PROCESS_NAME_WIN32,
                PWSTR(buf.as_mut_ptr()),
                &mut len,
            )
            .is_ok();
            let _ = CloseHandle(handle);
            ok.then(|| String::from_utf16_lossy(&buf[..(len as usize).min(buf.len())]))
        }
    }
}

#[cfg(not(windows))]
mod native {
    pub fn minimize(_hwnd: isize) {}

    pub fn is_iconic(_hwnd: isize) -> bool {
        false
    }

    pub fn process_path(_pid: u32) -> Option<String> {
        None
    }
}

/// Full image path of `pid`, when accessible (`None` off Windows).
pub(crate) fn process_path(pid: u32) -> Option<String> {
    native::process_path(pid)
}

/// `C:/Program Files/X/` → `c:\program files\x`: one spelling for comparing Windows paths.
pub(crate) fn normalize_path(path: &str) -> String {
    path.trim()
        .trim_matches('"')
        .replace('/', "\\")
        .trim_end_matches('\\')
        .to_lowercase()
}

/// The folder a program's executable owns (normalized), or `None` when that folder is shared by
/// other programs: under the Windows directory, a drive root or a first-level folder, or a
/// well-known shared one (`Program Files`, `AppData\Local`, ...). The Agent's `RunningApps` applies
/// the same rule to tell an app's processes.
pub(crate) fn own_folder(exe_path: &str) -> Option<String> {
    let exe = normalize_path(exe_path);
    let (dir, _) = exe.rsplit_once('\\')?;
    let parts: Vec<&str> = dir.split('\\').filter(|p| !p.is_empty()).collect();
    if parts.len() < 3 || parts[1] == "windows" || SHARED_FOLDERS.contains(&parts[parts.len() - 1])
    {
        return None;
    }
    Some(dir.to_owned())
}

/// Executable name (`game.exe`) of `pid`, when accessible.
/// Image name of `pid` (`hl.exe`), for the log; `None` for pid 0 or when it cannot be read.
pub(crate) fn process_name_of(pid: u32) -> Option<String> {
    if pid == 0 {
        None
    } else {
        process_name(pid)
    }
}

fn process_name(pid: u32) -> Option<String> {
    let path = process_path(pid)?;
    path.rsplit(['\\', '/'])
        .next()
        .filter(|s| !s.is_empty())
        .map(str::to_owned)
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
    /// Process the dock handed the foreground to, until the Shell is focused again.
    hand_off: Mutex<Option<u32>>,
    /// Own folder of the hand-off program ([`own_folder`]): its other processes (Discord's helpers,
    /// Steam's `steamwebhelper.exe`) open windows and take the foreground too.
    hand_off_dir: RwLock<Option<String>>,
    /// Run when the Shell takes the foreground back from a hand-off (the kiosk re-arms its guard).
    on_return: OnceLock<Box<dyn Fn() + Send + Sync>>,
    /// Run when the Shell's window gets the focus during a game (the kiosk may give it back to the game).
    on_game_focus: OnceLock<Box<dyn Fn() + Send + Sync>>,
    refocus: Mutex<Option<JoinHandle<()>>>,
    sweep: Mutex<Option<JoinHandle<()>>>,
    hand_off_check: Mutex<Option<JoinHandle<()>>>,
}

impl WindowGuard {
    /// Attaches to the `main` window, applies the kiosk (or dev) window state and starts the sweep.
    pub fn start(
        app: &AppHandle,
        config: &ShellConfig,
        dev: bool,
        primary: Rect,
    ) -> anyhow::Result<Arc<Self>> {
        let window = app
            .get_webview_window(MAIN_LABEL)
            .ok_or_else(|| anyhow::anyhow!("window '{MAIN_LABEL}' not found"))?;
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
            hand_off: Mutex::new(None),
            hand_off_dir: RwLock::new(None),
            on_return: OnceLock::new(),
            on_game_focus: OnceLock::new(),
            refocus: Mutex::new(None),
            sweep: Mutex::new(None),
            hand_off_check: Mutex::new(None),
        });
        if dev {
            guard.apply_dev_window();
        } else {
            if config.kiosk.fullscreen {
                if let Err(e) = guard.enter_fullscreen(primary) {
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
        guard.spawn_hand_off_check();
        tracing::info!(
            hwnd,
            dev,
            fullscreen = guard.is_fullscreen(),
            "window guard started"
        );
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
        self.window
            .is_fullscreen()
            .unwrap_or_else(|_| self.fullscreen.load(Ordering::Acquire))
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

    /// Startup: puts the window on `rect` (the primary monitor) and makes it fullscreen.
    fn enter_fullscreen(&self, rect: Rect) -> CmdResult<()> {
        let w = &self.window;
        w.set_position(PhysicalPosition::new(rect.left, rect.top))?;
        w.set_size(PhysicalSize::new(
            u32::try_from(rect.width()).unwrap_or(0),
            u32::try_from(rect.height()).unwrap_or(0),
        ))?;
        w.set_fullscreen(true)?;
        Ok(())
    }

    /// Moves the window onto `rect` (a monitor rectangle) without activating it, leaving its
    /// fullscreen state alone (`kiosk_move_to_monitor`, display changes; SHELL_CHANGES 80). Leaving
    /// and re-entering fullscreen activated the Shell, and a fullscreen game that is switching its
    /// display mode loses the foreground to it and drops back to the desktop. Nothing is done when
    /// the window already covers `rect`.
    pub fn move_to_monitor(&self, rect: Rect) -> CmdResult<()> {
        if self.hwnd != 0 {
            if place_window(self.hwnd, rect)? {
                tracing::info!(?rect, "main window re-placed");
            }
            return Ok(());
        }
        let w = &self.window;
        w.set_position(PhysicalPosition::new(rect.left, rect.top))?;
        w.set_size(PhysicalSize::new(
            u32::try_from(rect.width()).unwrap_or(0),
            u32::try_from(rect.height()).unwrap_or(0),
        ))?;
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
        if on && !self.dev {
            // SHELL_CHANGES 81: tao keeps its own ALWAYS_ON_TOP flag (set at start) and writes WS_EX_TOPMOST back on
            // its next style change; `set_topmost(false)` alone (the Alt+Tab guard) would not stick. No activation.
            let _ = self.window.set_always_on_top(false);
        }
        if !on && self.should_guard() {
            let _ = self.window.set_always_on_top(true);
            self.focus();
        }
    }

    pub fn is_game_mode(&self) -> bool {
        self.game_mode.load(Ordering::Acquire)
    }

    /// tao's own always-on-top flag (SHELL_CHANGES 81): tao writes `WS_EX_TOPMOST` back from it on its
    /// next style change, so a program put in front of the Shell with `set_topmost(false)` alone found
    /// the Shell back on top of it a moment later. No activation. No-op in dev mode.
    pub fn set_topmost_flag(&self, on: bool) {
        if !self.dev {
            let _ = self.window.set_always_on_top(on);
        }
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
        pid == self.own_pid || self.allowed.read().contains(&pid) || self.in_hand_off_folder(pid)
    }

    /// `pid` runs from the own folder of the program handed the foreground.
    fn in_hand_off_folder(&self, pid: u32) -> bool {
        if pid == 0 {
            return false;
        }
        let Some(dir) = self.hand_off_dir.read().clone() else {
            return false;
        };
        process_path(pid).is_some_and(|path| {
            normalize_path(&path)
                .strip_prefix(&dir)
                .is_some_and(|rest| rest.starts_with('\\'))
        })
    }

    /// Marks `pid` as the program the dock put in front (it must also be allowlisted); returns the
    /// previous one. While set, the hand-off check re-focuses the Shell when anything else takes over
    /// or the program is minimized.
    pub fn begin_hand_off(&self, pid: u32) -> Option<u32> {
        *self.hand_off_dir.write() = process_path(pid).as_deref().and_then(own_folder);
        self.hand_off.lock().replace(pid)
    }

    /// Clears the hand-off; returns the program it was for.
    pub fn take_hand_off(&self) -> Option<u32> {
        *self.hand_off_dir.write() = None;
        self.hand_off.lock().take()
    }

    /// The program the foreground is handed to, if any.
    pub fn hand_off_pid(&self) -> Option<u32> {
        *self.hand_off.lock()
    }

    pub fn is_handed_off(&self) -> bool {
        self.hand_off.lock().is_some()
    }

    /// Sets what runs (on the async runtime) when the Shell is focused during a hand-off. Once.
    pub fn on_return(&self, f: impl Fn() + Send + Sync + 'static) {
        if self.on_return.set(Box::new(f)).is_err() {
            tracing::warn!("hand-off return handler already set");
        }
    }

    /// Sets the handler run when the Shell's window gets the focus during a game (SHELL_CHANGES 81).
    pub fn on_game_focus(&self, f: impl Fn() + Send + Sync + 'static) {
        if self.on_game_focus.set(Box::new(f)).is_err() {
            tracing::warn!("game focus handler already set");
        }
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
        if let Some(task) = self.hand_off_check.lock().take() {
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
        let foreground_process = if focused || pid == 0 {
            None
        } else {
            process_name(pid)
        };
        if self.is_game_mode() {
            // SHELL_CHANGES 83: who has the foreground during a game is worth the log.
            tracing::info!(has_focus = focused, pid, process = ?foreground_process, "focus changed during the game");
        } else {
            tracing::debug!(has_focus = focused, pid, process = ?foreground_process, "focus changed");
        }
        if let Err(e) = self.app.emit(
            FOCUS_EVENT,
            FocusEvent {
                has_focus: focused,
                foreground_process,
            },
        ) {
            tracing::warn!(error = %e, "cannot emit kiosk://focus");
        }
        if focused && self.is_game_mode() && !self.is_locked() {
            let me = Arc::clone(self);
            tauri::async_runtime::spawn(async move {
                if let Some(f) = me.on_game_focus.get() {
                    f();
                }
            });
        }
        if focused && self.is_handed_off() {
            let me = Arc::clone(self);
            tauri::async_runtime::spawn(async move {
                if let Some(f) = me.on_return.get() {
                    f();
                }
            });
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
        !self.dev
            && self.is_active()
            && !self.is_game_mode()
            && !self.shutting_down.load(Ordering::Acquire)
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

    /// During a hand-off the topmost guard is paused: should the program close, be minimized or
    /// lose the foreground to something else, nobody else brings the Shell back.
    fn spawn_hand_off_check(self: &Arc<Self>) {
        let weak: Weak<Self> = Arc::downgrade(self);
        let task = tauri::async_runtime::spawn(async move {
            let mut tick = tokio::time::interval(HAND_OFF_CHECK);
            tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
            loop {
                tick.tick().await;
                let Some(guard) = weak.upgrade() else { break };
                if guard.should_guard() && guard.is_handed_off() {
                    guard.check_hand_off();
                }
            }
        });
        *self.hand_off_check.lock() = Some(task);
    }

    /// One hand-off check: the Shell takes the screen back when the foreground is nobody's, a
    /// minimized window (what minimizing the only program in front leaves) or outside the allowlist.
    fn check_hand_off(&self) {
        let hwnd = foreground_window();
        let pid = if hwnd == 0 {
            0
        } else {
            window_pid(hwnd).unwrap_or(0)
        };
        if pid == self.own_pid {
            return; // the Shell (or its HUD) is in front already
        }
        let minimized = hwnd != 0 && native::is_iconic(hwnd);
        if hwnd != 0 && !minimized && self.is_allowed_pid(pid) {
            return;
        }
        let ok = self.focus();
        tracing::info!(
            pid,
            minimized,
            ok,
            "handed-off program no longer in front; the shell takes the screen back"
        );
    }

    /// Minimizes visible, titled top-level windows of processes that are neither us nor allowlisted.
    /// Returns how many were minimized.
    pub fn sweep_stray_windows(&self) -> usize {
        let Ok(windows) = enumerate_windows() else {
            return 0;
        };
        let mut count = 0;
        for w in windows {
            if w.hwnd == self.hwnd || !w.visible || w.title.is_empty() || self.is_allowed_pid(w.pid)
            {
                continue;
            }
            if SKIP_CLASSES
                .iter()
                .any(|c| c.eq_ignore_ascii_case(&w.class))
                || native::is_iconic(w.hwnd)
            {
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
        let json = serde_json::to_value(FocusEvent {
            has_focus: false,
            foreground_process: Some("game.exe".into()),
        })
        .unwrap();
        assert_eq!(
            json,
            serde_json::json!({ "hasFocus": false, "foregroundProcess": "game.exe" })
        );
        let json = serde_json::to_value(FocusEvent {
            has_focus: true,
            foreground_process: None,
        })
        .unwrap();
        assert_eq!(json, serde_json::json!({ "hasFocus": true }));
        assert!(SKIP_CLASSES
            .iter()
            .any(|c| c.eq_ignore_ascii_case("shell_traywnd")));
    }

    #[test]
    fn own_folder_skips_shared_folders() {
        assert_eq!(
            own_folder(r"C:\Program Files (x86)\Steam\steam.exe").as_deref(),
            Some(r"c:\program files (x86)\steam")
        );
        assert_eq!(
            own_folder(r"C:/Users/kiosk/AppData/Local/Discord/Update.exe").as_deref(),
            Some(r"c:\users\kiosk\appdata\local\discord")
        );
        for shared in [
            r"C:\Windows\System32\notepad.exe",
            r"C:\Windows\notepad.exe",
            r"C:\Program Files\tool.exe",
            r"D:\Apps\tool.exe",
            r"C:\Users\kiosk\AppData\Local\tool.exe",
            r"tool.exe",
            "",
        ] {
            assert_eq!(own_folder(shared), None, "{shared}");
        }
        assert_eq!(
            normalize_path(r#""C:\Games\X\""#),
            r"c:\games\x",
            "quotes and the trailing separator go"
        );
    }
}
