//! Window helpers for the kiosk: topmost / borderless fullscreen / foreground stealing, taskbar
//! hide + auto-hide, window enumeration, a background [`TopmostGuard`] and screenshot protection via
//! `SetWindowDisplayAffinity`.
//!
//! Handles are raw `HWND` values (`isize`) — Tauri exposes them through `WebviewWindow::hwnd()`.

use std::time::Duration;

use crate::hooks::{BlockedCombo, LowLevelKeyboardHook};
use crate::{Rect, Result};

/// One top-level (or child) window.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct WindowInfo {
    pub hwnd: isize,
    pub pid: u32,
    pub title: String,
    pub class: String,
    pub visible: bool,
}

/// Chords that switch windows; feed them to the main keyboard hook (`disableAltTab` policy).
pub fn alt_tab_combos() -> Vec<BlockedCombo> {
    ["Alt+Tab", "Alt+Esc", "Win+Tab", "Ctrl+Alt+Tab"].iter().map(|s| BlockedCombo::parse(s).expect("built-in combos parse")).collect()
}

/// Installs a keyboard hook that blocks [`alt_tab_combos`]. There is one low-level keyboard hook per
/// process, so when the kiosk hook is already installed add [`alt_tab_combos`] to its filter instead.
pub fn disable_alt_tab_switching() -> Result<LowLevelKeyboardHook> {
    LowLevelKeyboardHook::with_blocked(alt_tab_combos())
}

#[cfg(windows)]
mod imp {
    use super::*;
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::sync::Arc;
    use std::thread::JoinHandle;

    use windows::Win32::Foundation::{BOOL, HWND, LPARAM, RECT};
    use windows::Win32::System::Threading::{AttachThreadInput, GetCurrentThreadId};
    use windows::Win32::UI::Shell::{SHAppBarMessage, APPBARDATA};
    use windows::Win32::UI::WindowsAndMessaging::{
        BringWindowToTop, EnumChildWindows, EnumWindows, GetClassNameW, GetForegroundWindow, GetWindowRect,
        GetWindowTextW, GetWindowThreadProcessId, IsIconic, IsWindowVisible, SetForegroundWindow,
        SetWindowDisplayAffinity, SetWindowPos, ShowWindow, GWL_EXSTYLE, GWL_STYLE, HWND_NOTOPMOST, HWND_TOPMOST,
        SWP_FRAMECHANGED, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOOWNERZORDER, SWP_NOSIZE, SWP_NOZORDER, SWP_SHOWWINDOW,
        SW_FORCEMINIMIZE, SW_HIDE, SW_SHOW, WDA_EXCLUDEFROMCAPTURE, WDA_NONE, WINDOW_LONG_PTR_INDEX, WS_CAPTION,
        WS_EX_CLIENTEDGE, WS_EX_DLGMODALFRAME, WS_EX_STATICEDGE, WS_EX_TOOLWINDOW, WS_EX_WINDOWEDGE, WS_MAXIMIZEBOX,
        WS_MINIMIZEBOX, WS_POPUP, WS_SYSMENU, WS_THICKFRAME,
    };
    #[cfg(target_pointer_width = "64")]
    use windows::Win32::UI::WindowsAndMessaging::{GetWindowLongPtrW, SetWindowLongPtrW};
    #[cfg(not(target_pointer_width = "64"))]
    use windows::Win32::UI::WindowsAndMessaging::{GetWindowLongW, SetWindowLongW};

    use crate::hooks::vk;
    use crate::{hwnd_from_raw, last_error, WinUtilError, Win32Ret};

    const ABM_SETSTATE: u32 = 0x0000_000A;
    const ABS_AUTOHIDE: u32 = 0x1;
    const ABS_ALWAYSONTOP: u32 = 0x2;
    const TASKBAR_CLASSES: [&str; 2] = ["Shell_TrayWnd", "Shell_SecondaryTrayWnd"];
    /// Desktop / shell windows that must never be minimized by [`minimize_others`].
    const SHELL_CLASSES: [&str; 5] = ["Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "ClubShellDisplayWatch"];

    #[cfg(target_pointer_width = "64")]
    fn get_window_long(hwnd: HWND, index: WINDOW_LONG_PTR_INDEX) -> isize {
        // SAFETY: no preconditions beyond a window handle.
        unsafe { GetWindowLongPtrW(hwnd, index) }
    }

    #[cfg(target_pointer_width = "64")]
    fn set_window_long(hwnd: HWND, index: WINDOW_LONG_PTR_INDEX, value: isize) -> isize {
        // SAFETY: no preconditions beyond a window handle.
        unsafe { SetWindowLongPtrW(hwnd, index, value) }
    }

    #[cfg(not(target_pointer_width = "64"))]
    fn get_window_long(hwnd: HWND, index: WINDOW_LONG_PTR_INDEX) -> isize {
        // SAFETY: no preconditions beyond a window handle.
        unsafe { GetWindowLongW(hwnd, index) as isize }
    }

    #[cfg(not(target_pointer_width = "64"))]
    fn set_window_long(hwnd: HWND, index: WINDOW_LONG_PTR_INDEX, value: isize) -> isize {
        // SAFETY: no preconditions beyond a window handle.
        unsafe { SetWindowLongW(hwnd, index, value as i32) as isize }
    }

    fn read_text(n: i32, buf: &[u16]) -> String {
        let n = usize::try_from(n).unwrap_or(0).min(buf.len());
        String::from_utf16_lossy(&buf[..n])
    }

    // SAFETY: invoked synchronously by user32 with a valid window handle.
    unsafe fn describe(hwnd: HWND) -> WindowInfo {
        let mut title = [0u16; 512];
        let title_len = GetWindowTextW(hwnd, &mut title);
        let mut class = [0u16; 256];
        let class_len = GetClassNameW(hwnd, &mut class);
        let mut pid = 0u32;
        GetWindowThreadProcessId(hwnd, Some(&mut pid as *mut u32));
        WindowInfo {
            hwnd: hwnd.0 as isize,
            pid,
            title: read_text(title_len, &title),
            class: read_text(class_len, &class),
            visible: IsWindowVisible(hwnd).as_bool(),
        }
    }

    // SAFETY: `lparam` is the address of a live `Vec<WindowInfo>` for the duration of the enumeration.
    unsafe extern "system" fn collect(hwnd: HWND, lparam: LPARAM) -> BOOL {
        let out = &mut *(lparam.0 as *mut Vec<WindowInfo>);
        out.push(describe(hwnd));
        BOOL(1)
    }

    /// Every top-level window in Z order (topmost first), including invisible ones.
    pub fn enumerate_windows() -> Result<Vec<WindowInfo>> {
        let mut out: Vec<WindowInfo> = Vec::new();
        // SAFETY: callback and data pointer are valid for the synchronous enumeration.
        unsafe { EnumWindows(Some(collect), LPARAM(&mut out as *mut Vec<WindowInfo> as isize)) }.ret("EnumWindows")?;
        Ok(out)
    }

    /// Direct and indirect children of `parent`.
    pub fn child_windows(parent: isize) -> Vec<WindowInfo> {
        let mut out: Vec<WindowInfo> = Vec::new();
        // SAFETY: as for `enumerate_windows`; the BOOL result only says whether the callback stopped.
        let _ = unsafe { EnumChildWindows(hwnd_from_raw(parent), Some(collect), LPARAM(&mut out as *mut Vec<WindowInfo> as isize)) };
        out
    }

    /// First top-level window matching `class` (case-insensitive) and/or exact `title`.
    pub fn find_window(class: Option<&str>, title: Option<&str>) -> Result<Option<isize>> {
        if class.is_none() && title.is_none() {
            return Err(WinUtilError::Invalid("find_window needs a class or a title".to_owned()));
        }
        Ok(enumerate_windows()?
            .into_iter()
            .find(|w| {
                let class_ok = if let Some(c) = class { w.class.eq_ignore_ascii_case(c) } else { true };
                let title_ok = if let Some(t) = title { w.title == t } else { true };
                class_ok && title_ok
            })
            .map(|w| w.hwnd))
    }

    /// Adds or removes the `HWND_TOPMOST` z-order band without moving, resizing or activating.
    pub fn set_topmost(hwnd: isize, topmost: bool) -> Result<()> {
        let after = if topmost { HWND_TOPMOST } else { HWND_NOTOPMOST };
        // SAFETY: no preconditions beyond a window handle.
        unsafe { SetWindowPos(hwnd_from_raw(hwnd), after, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE) }.ret("SetWindowPos")
    }

    /// Strips the caption/frame styles and sizes the window to `monitor` (a [`crate::monitor::MonitorInfo::rect`]).
    pub fn set_fullscreen_borderless(hwnd: isize, monitor: Rect) -> Result<()> {
        let h = hwnd_from_raw(hwnd);
        let style = get_window_long(h, GWL_STYLE) as u32;
        let style = (style & !(WS_CAPTION.0 | WS_THICKFRAME.0 | WS_MINIMIZEBOX.0 | WS_MAXIMIZEBOX.0 | WS_SYSMENU.0)) | WS_POPUP.0;
        set_window_long(h, GWL_STYLE, style as isize);
        let ex = get_window_long(h, GWL_EXSTYLE) as u32;
        let ex = ex & !(WS_EX_DLGMODALFRAME.0 | WS_EX_CLIENTEDGE.0 | WS_EX_STATICEDGE.0 | WS_EX_WINDOWEDGE.0);
        set_window_long(h, GWL_EXSTYLE, ex as isize);
        // SAFETY: no preconditions beyond a window handle.
        unsafe {
            SetWindowPos(
                h,
                HWND::default(),
                monitor.left,
                monitor.top,
                monitor.width(),
                monitor.height(),
                SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW,
            )
        }
        .ret("SetWindowPos")
    }

    /// Raw `HWND` of the current foreground window (0 when none).
    pub fn foreground_window() -> isize {
        // SAFETY: no preconditions.
        unsafe { GetForegroundWindow() }.0 as isize
    }

    pub fn is_foreground(hwnd: isize) -> bool {
        foreground_window() == hwnd
    }

    /// Process id owning `hwnd`.
    pub fn window_pid(hwnd: isize) -> Result<u32> {
        let mut pid = 0u32;
        // SAFETY: `pid` is a valid out-pointer.
        let tid = unsafe { GetWindowThreadProcessId(hwnd_from_raw(hwnd), Some(&mut pid as *mut u32)) };
        if tid == 0 {
            Err(last_error("GetWindowThreadProcessId"))
        } else {
            Ok(pid)
        }
    }

    pub fn window_rect(hwnd: isize) -> Result<Rect> {
        let mut r = RECT::default();
        // SAFETY: `r` is a valid out-pointer.
        unsafe { GetWindowRect(hwnd_from_raw(hwnd), &mut r) }.ret("GetWindowRect")?;
        Ok(Rect::from(r))
    }

    /// Brings `hwnd` to the foreground, working around the `SetForegroundWindow` restrictions by
    /// attaching to the current foreground thread's input queue and, if that is not enough, tapping
    /// `Alt` through `SendInput`. Returns whether `hwnd` is the foreground window afterwards.
    pub fn force_foreground(hwnd: isize) -> Result<bool> {
        let h = hwnd_from_raw(hwnd);
        // SAFETY: the calls have no memory-safety preconditions; a stale handle just fails.
        unsafe {
            let fg = GetForegroundWindow();
            if fg == h {
                return Ok(true);
            }
            let fg_thread = if fg.0.is_null() { 0 } else { GetWindowThreadProcessId(fg, None) };
            let me = GetCurrentThreadId();
            let attached = fg_thread != 0 && fg_thread != me && AttachThreadInput(me, fg_thread, BOOL::from(true)).as_bool();
            let _ = ShowWindow(h, SW_SHOW);
            let _ = BringWindowToTop(h);
            let mut ok = SetForegroundWindow(h).as_bool();
            if attached {
                let _ = AttachThreadInput(me, fg_thread, BOOL::from(false));
            }
            if !ok || GetForegroundWindow() != h {
                crate::input::tap_key(vk::MENU)?;
                ok = SetForegroundWindow(h).as_bool();
            }
            Ok(ok && GetForegroundWindow() == h)
        }
    }

    fn set_taskbar_visible(visible: bool) -> Result<usize> {
        let cmd = if visible { SW_SHOW } else { SW_HIDE };
        let mut count = 0;
        for w in enumerate_windows()? {
            if !TASKBAR_CLASSES.iter().any(|c| c.eq_ignore_ascii_case(&w.class)) {
                continue;
            }
            // SAFETY: no preconditions beyond a window handle.
            let _ = unsafe { ShowWindow(hwnd_from_raw(w.hwnd), cmd) };
            count += 1;
            if w.class.eq_ignore_ascii_case("Shell_TrayWnd") {
                for child in child_windows(w.hwnd) {
                    if child.class.eq_ignore_ascii_case("Button") {
                        // SAFETY: as above.
                        let _ = unsafe { ShowWindow(hwnd_from_raw(child.hwnd), cmd) };
                        count += 1;
                    }
                }
            }
        }
        Ok(count)
    }

    /// Hides the primary and secondary taskbars (and the legacy Start button). Returns the number of
    /// windows hidden. Explorer re-shows them on restart, so the shell re-applies after `explorer.exe`
    /// appears.
    pub fn hide_taskbar() -> Result<usize> {
        set_taskbar_visible(false)
    }

    pub fn show_taskbar() -> Result<usize> {
        set_taskbar_visible(true)
    }

    /// Switches the taskbar auto-hide state (`SHAppBarMessage(ABM_SETSTATE)`), a softer alternative
    /// to [`hide_taskbar`] that keeps the work area correct.
    pub fn taskbar_autohide(enable: bool) -> Result<()> {
        let tray = find_window(Some("Shell_TrayWnd"), None)?.ok_or_else(|| WinUtilError::Invalid("taskbar window not found".to_owned()))?;
        let state: u32 = if enable { ABS_AUTOHIDE } else { ABS_ALWAYSONTOP };
        let mut data = APPBARDATA {
            cbSize: std::mem::size_of::<APPBARDATA>() as u32,
            hWnd: hwnd_from_raw(tray),
            lParam: LPARAM(state as isize),
            ..Default::default()
        };
        // SAFETY: `data` is a correctly sized APPBARDATA. ABM_SETSTATE has no meaningful result.
        let _ = unsafe { SHAppBarMessage(ABM_SETSTATE, &mut data) };
        Ok(())
    }

    /// Minimizes every visible, titled top-level window except `except`, windows of its process and
    /// shell/desktop windows. Returns how many were minimized.
    pub fn minimize_others(except: isize) -> Result<usize> {
        let own_pid = window_pid(except).unwrap_or(0);
        let mut count = 0;
        for w in enumerate_windows()? {
            if w.hwnd == except || !w.visible || w.title.is_empty() || (own_pid != 0 && w.pid == own_pid) {
                continue;
            }
            if SHELL_CLASSES.iter().any(|c| c.eq_ignore_ascii_case(&w.class)) {
                continue;
            }
            let h = hwnd_from_raw(w.hwnd);
            let ex = get_window_long(h, GWL_EXSTYLE) as u32;
            // SAFETY: no preconditions beyond a window handle.
            if ex & WS_EX_TOOLWINDOW.0 != 0 || unsafe { IsIconic(h) }.as_bool() {
                continue;
            }
            // SAFETY: as above; SW_FORCEMINIMIZE works across threads.
            let _ = unsafe { ShowWindow(h, SW_FORCEMINIMIZE) };
            count += 1;
        }
        Ok(count)
    }

    /// Excludes the window from screen capture (`WDA_EXCLUDEFROMCAPTURE`, Windows 10 2004+) or
    /// restores normal capture. Only the owning process may call this for its own windows.
    pub fn set_window_display_affinity(hwnd: isize, exclude_from_capture: bool) -> Result<()> {
        let affinity = if exclude_from_capture { WDA_EXCLUDEFROMCAPTURE } else { WDA_NONE };
        // SAFETY: no preconditions beyond a window handle.
        unsafe { SetWindowDisplayAffinity(hwnd_from_raw(hwnd), affinity) }.ret("SetWindowDisplayAffinity")
    }

    fn reassert(hwnd: isize) {
        if let Err(e) = set_topmost(hwnd, true) {
            tracing::trace!(error = %e, "topmost re-assert failed");
        }
        if !is_foreground(hwnd) {
            match force_foreground(hwnd) {
                Ok(true) => {}
                Ok(false) => tracing::trace!("foreground re-assert refused"),
                Err(e) => tracing::trace!(error = %e, "foreground re-assert failed"),
            }
        }
    }

    /// Background thread that re-asserts `HWND_TOPMOST` and the foreground state of a window every
    /// `interval` while active (`shell.json → kiosk.topmostGuard`). Pause it while a game runs, or the
    /// guard steals focus from the game. Dropping stops the thread.
    pub struct TopmostGuard {
        active: Arc<AtomicBool>,
        stop: Arc<AtomicBool>,
        thread: Option<JoinHandle<()>>,
    }

    impl TopmostGuard {
        pub fn start(hwnd: isize, interval: Duration) -> Result<Self> {
            let active = Arc::new(AtomicBool::new(true));
            let stop = Arc::new(AtomicBool::new(false));
            let (active_t, stop_t) = (Arc::clone(&active), Arc::clone(&stop));
            let thread = std::thread::Builder::new().name("winutil-topmost".to_owned()).spawn(move || {
                while !stop_t.load(Ordering::Acquire) {
                    if active_t.load(Ordering::Acquire) {
                        reassert(hwnd);
                    }
                    std::thread::park_timeout(interval);
                }
            })?;
            Ok(Self { active, stop, thread: Some(thread) })
        }

        /// Stops re-asserting (e.g. while a game is in the foreground).
        pub fn pause(&self) {
            self.active.store(false, Ordering::Release);
        }

        pub fn resume(&self) {
            self.active.store(true, Ordering::Release);
            if let Some(t) = &self.thread {
                t.thread().unpark();
            }
        }

        pub fn is_active(&self) -> bool {
            self.active.load(Ordering::Acquire)
        }
    }

    impl Drop for TopmostGuard {
        fn drop(&mut self) {
            self.stop.store(true, Ordering::Release);
            if let Some(t) = self.thread.take() {
                t.thread().unpark();
                let _ = t.join();
            }
        }
    }
}

#[cfg(not(windows))]
mod imp {
    use super::*;
    use crate::WinUtilError;

    pub fn enumerate_windows() -> Result<Vec<WindowInfo>> {
        Err(WinUtilError::Unsupported)
    }

    pub fn child_windows(_parent: isize) -> Vec<WindowInfo> {
        Vec::new()
    }

    pub fn find_window(_class: Option<&str>, _title: Option<&str>) -> Result<Option<isize>> {
        Err(WinUtilError::Unsupported)
    }

    pub fn set_topmost(_hwnd: isize, _topmost: bool) -> Result<()> {
        Err(WinUtilError::Unsupported)
    }

    pub fn set_fullscreen_borderless(_hwnd: isize, _monitor: Rect) -> Result<()> {
        Err(WinUtilError::Unsupported)
    }

    pub fn foreground_window() -> isize {
        0
    }

    pub fn is_foreground(_hwnd: isize) -> bool {
        false
    }

    pub fn window_pid(_hwnd: isize) -> Result<u32> {
        Err(WinUtilError::Unsupported)
    }

    pub fn window_rect(_hwnd: isize) -> Result<Rect> {
        Err(WinUtilError::Unsupported)
    }

    pub fn force_foreground(_hwnd: isize) -> Result<bool> {
        Err(WinUtilError::Unsupported)
    }

    pub fn hide_taskbar() -> Result<usize> {
        Err(WinUtilError::Unsupported)
    }

    pub fn show_taskbar() -> Result<usize> {
        Err(WinUtilError::Unsupported)
    }

    pub fn taskbar_autohide(_enable: bool) -> Result<()> {
        Err(WinUtilError::Unsupported)
    }

    pub fn minimize_others(_except: isize) -> Result<usize> {
        Err(WinUtilError::Unsupported)
    }

    pub fn set_window_display_affinity(_hwnd: isize, _exclude_from_capture: bool) -> Result<()> {
        Err(WinUtilError::Unsupported)
    }

    /// Non-Windows stub: [`start`](Self::start) always fails with [`WinUtilError::Unsupported`].
    pub struct TopmostGuard {
        _private: (),
    }

    impl TopmostGuard {
        pub fn start(_hwnd: isize, _interval: Duration) -> Result<Self> {
            Err(WinUtilError::Unsupported)
        }

        pub fn pause(&self) {}

        pub fn resume(&self) {}

        pub fn is_active(&self) -> bool {
            false
        }
    }
}

pub use imp::{
    child_windows, enumerate_windows, find_window, force_foreground, foreground_window, hide_taskbar, is_foreground,
    minimize_others, set_fullscreen_borderless, set_topmost, set_window_display_affinity, show_taskbar,
    taskbar_autohide, window_pid, window_rect, TopmostGuard,
};

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn alt_tab_combos_parse_and_match() {
        let combos = alt_tab_combos();
        assert_eq!(combos.len(), 4);
        let alt_tab = crate::hooks::KeyEvent { vk: crate::hooks::vk::TAB, scan: 0, flags: 0, alt: true, ctrl: false, shift: false, win: false, key_up: false };
        assert!(BlockedCombo::any_matches(&combos, &alt_tab));
    }
}
