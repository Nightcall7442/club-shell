//! Display enumeration (`EnumDisplayMonitors` + `GetMonitorInfoW` + `EnumDisplaySettingsW` +
//! `GetDpiForMonitor`) and a `WM_DISPLAYCHANGE` watcher that re-enumerates from a hidden window.

use std::sync::mpsc::Receiver;

use crate::{Rect, Result};

/// One attached display.
#[derive(Clone, Debug, PartialEq)]
pub struct MonitorInfo {
    /// Position in the enumeration order (stable while the topology does not change).
    pub index: usize,
    /// Raw `HMONITOR` value.
    pub handle: isize,
    /// GDI device name, e.g. `\\.\DISPLAY1`.
    pub name: String,
    /// Full monitor rectangle in virtual-screen coordinates (physical pixels).
    pub rect: Rect,
    /// Work area (monitor minus taskbar / app bars).
    pub work_rect: Rect,
    pub width: i32,
    pub height: i32,
    /// Current refresh rate in Hz (0 when unknown).
    pub hz: u32,
    pub primary: bool,
    /// Effective DPI scale (`dpi / 96`), 1.0 when unavailable.
    pub scale: f32,
}

/// Primary monitor: the one flagged primary, else the one containing the virtual-screen origin,
/// else the first.
pub fn pick_primary(monitors: &[MonitorInfo]) -> Option<&MonitorInfo> {
    monitors
        .iter()
        .find(|m| m.primary)
        .or_else(|| monitors.iter().find(|m| m.rect.contains(0, 0)))
        .or_else(|| monitors.first())
}

/// Monitor whose rectangle contains `(x, y)`, else the nearest by edge distance.
pub fn monitor_containing(monitors: &[MonitorInfo], x: i32, y: i32) -> Option<&MonitorInfo> {
    monitors.iter().find(|m| m.rect.contains(x, y)).or_else(|| {
        monitors.iter().min_by_key(|m| {
            let dx = (m.rect.left - x).max(0).max(x - (m.rect.right - 1));
            let dy = (m.rect.top - y).max(0).max(y - (m.rect.bottom - 1));
            i64::from(dx) * i64::from(dx) + i64::from(dy) * i64::from(dy)
        })
    })
}

#[cfg(windows)]
mod imp {
    use super::*;
    use std::sync::mpsc::{self, Sender};
    use std::thread::JoinHandle;

    use parking_lot::Mutex;
    use windows::core::PCWSTR;
    use windows::Win32::Foundation::{BOOL, HINSTANCE, HWND, LPARAM, LRESULT, POINT, RECT, WPARAM};
    use windows::Win32::Graphics::Gdi::{
        EnumDisplayMonitors, EnumDisplaySettingsW, GetMonitorInfoW, MonitorFromPoint, DEVMODEW, ENUM_CURRENT_SETTINGS,
        HDC, HMONITOR, MONITORINFO, MONITORINFOEXW, MONITOR_DEFAULTTONEAREST,
    };
    use windows::Win32::System::LibraryLoader::GetModuleHandleW;
    use windows::Win32::System::Threading::GetCurrentThreadId;
    use windows::Win32::UI::HiDpi::{GetDpiForMonitor, MDT_EFFECTIVE_DPI};
    use windows::Win32::UI::WindowsAndMessaging::{
        CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GetMessageW, PeekMessageW, PostThreadMessageW,
        RegisterClassW, TranslateMessage, HMENU, MSG, PM_NOREMOVE, WINDOW_EX_STYLE, WM_DISPLAYCHANGE, WM_QUIT,
        WM_SETTINGCHANGE, WM_USER, WNDCLASSW, WS_OVERLAPPED,
    };

    use crate::{last_error, WinUtilError, Win32Ret};

    const MONITORINFOF_PRIMARY: u32 = 1;
    const WM_DPICHANGED: u32 = 0x02E0;
    const ERROR_CLASS_ALREADY_EXISTS: u32 = 1410;
    const WATCH_CLASS: &str = "ClubShellDisplayWatch";

    /// All attached monitors in enumeration order.
    pub fn enumerate() -> Result<Vec<MonitorInfo>> {
        // SAFETY: `lparam` is the address of a live `Vec<HMONITOR>` for the duration of the call.
        unsafe extern "system" fn collect(h: HMONITOR, _dc: HDC, _rect: *mut RECT, lparam: LPARAM) -> BOOL {
            let handles = &mut *(lparam.0 as *mut Vec<HMONITOR>);
            handles.push(h);
            BOOL(1)
        }
        let mut handles: Vec<HMONITOR> = Vec::new();
        // SAFETY: callback and data pointer are valid for the synchronous enumeration.
        unsafe { EnumDisplayMonitors(HDC::default(), None, Some(collect), LPARAM(&mut handles as *mut Vec<HMONITOR> as isize)) }
            .ret("EnumDisplayMonitors")?;
        Ok(handles.into_iter().enumerate().filter_map(|(index, h)| describe(index, h)).collect())
    }

    fn describe(index: usize, handle: HMONITOR) -> Option<MonitorInfo> {
        let mut mi = MONITORINFOEXW {
            monitorInfo: MONITORINFO { cbSize: std::mem::size_of::<MONITORINFOEXW>() as u32, ..Default::default() },
            ..Default::default()
        };
        // SAFETY: `mi` is a MONITORINFOEXW with cbSize set, which GetMonitorInfoW accepts through a
        // MONITORINFO pointer.
        let ok = unsafe { GetMonitorInfoW(handle, (&mut mi as *mut MONITORINFOEXW).cast::<MONITORINFO>()) };
        if !ok.as_bool() {
            return None;
        }
        let name_len = mi.szDevice.iter().position(|&c| c == 0).unwrap_or(mi.szDevice.len());
        let name = String::from_utf16_lossy(&mi.szDevice[..name_len]);

        let mut dm = DEVMODEW { dmSize: std::mem::size_of::<DEVMODEW>() as u16, ..Default::default() };
        // SAFETY: `szDevice` is NUL-terminated (Windows fills it) and `dm` is sized.
        let hz = if unsafe { EnumDisplaySettingsW(PCWSTR(mi.szDevice.as_ptr()), ENUM_CURRENT_SETTINGS, &mut dm) }.as_bool() {
            dm.dmDisplayFrequency
        } else {
            0
        };

        let (mut dpi_x, mut dpi_y) = (0u32, 0u32);
        // SAFETY: out-pointers are valid; fails (→ 1.0) on systems without per-monitor DPI.
        let scale = match unsafe { GetDpiForMonitor(handle, MDT_EFFECTIVE_DPI, &mut dpi_x, &mut dpi_y) } {
            Ok(()) if dpi_x > 0 => dpi_x as f32 / 96.0,
            _ => 1.0,
        };

        let rect = Rect::from(mi.monitorInfo.rcMonitor);
        Some(MonitorInfo {
            index,
            handle: handle.0 as isize,
            name,
            rect,
            work_rect: Rect::from(mi.monitorInfo.rcWork),
            width: rect.width(),
            height: rect.height(),
            hz,
            primary: mi.monitorInfo.dwFlags & MONITORINFOF_PRIMARY != 0,
            scale,
        })
    }

    /// The primary monitor (see [`pick_primary`]).
    pub fn primary() -> Result<MonitorInfo> {
        let all = enumerate()?;
        pick_primary(&all).cloned().ok_or_else(|| WinUtilError::Invalid("no display monitors attached".to_owned()))
    }

    /// Monitor containing (or nearest to) a screen point (`MonitorFromPoint(MONITOR_DEFAULTTONEAREST)`).
    pub fn monitor_at_point(x: i32, y: i32) -> Result<MonitorInfo> {
        // SAFETY: no preconditions.
        let h = unsafe { MonitorFromPoint(POINT { x, y }, MONITOR_DEFAULTTONEAREST) };
        let all = enumerate()?;
        all.iter()
            .find(|m| m.handle == h.0 as isize)
            .or_else(|| monitor_containing(&all, x, y))
            .cloned()
            .ok_or_else(|| WinUtilError::Invalid("no display monitors attached".to_owned()))
    }

    struct Watcher {
        hwnd: isize,
        tx: Sender<Vec<MonitorInfo>>,
        last: Vec<MonitorInfo>,
    }

    static WATCHERS: Mutex<Vec<Watcher>> = Mutex::new(Vec::new());

    fn notify(hwnd: isize) {
        let Ok(list) = enumerate() else { return };
        let mut watchers = WATCHERS.lock();
        if let Some(w) = watchers.iter_mut().find(|w| w.hwnd == hwnd) {
            if w.last != list {
                w.last = list.clone();
                let _ = w.tx.send(list);
            }
        }
    }

    // SAFETY: standard window procedure contract; only forwards to DefWindowProcW.
    unsafe extern "system" fn watch_proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
        if msg == WM_DISPLAYCHANGE || msg == WM_SETTINGCHANGE || msg == WM_DPICHANGED {
            notify(hwnd.0 as isize);
            return LRESULT(0);
        }
        DefWindowProcW(hwnd, msg, wparam, lparam)
    }

    /// Creates the hidden top-level window that receives `WM_DISPLAYCHANGE` broadcasts (message-only
    /// windows do not receive broadcasts, hence a real but never-shown window).
    fn create_hidden_window() -> Result<HWND> {
        let class: Vec<u16> = WATCH_CLASS.encode_utf16().chain(std::iter::once(0)).collect();
        // SAFETY: the class-name buffer outlives both calls; the WNDCLASSW fields are valid.
        unsafe {
            let module = GetModuleHandleW(PCWSTR::null()).ret("GetModuleHandleW")?;
            let instance = HINSTANCE(module.0);
            let wc = WNDCLASSW {
                lpfnWndProc: Some(watch_proc),
                hInstance: instance,
                lpszClassName: PCWSTR(class.as_ptr()),
                ..Default::default()
            };
            if RegisterClassW(&wc) == 0 {
                let err = last_error("RegisterClassW");
                let already = matches!(err, WinUtilError::Win32 { code, .. } if code & 0xFFFF == ERROR_CLASS_ALREADY_EXISTS);
                if !already {
                    return Err(err);
                }
            }
            CreateWindowExW(
                WINDOW_EX_STYLE(0),
                PCWSTR(class.as_ptr()),
                PCWSTR(class.as_ptr()),
                WS_OVERLAPPED,
                0,
                0,
                0,
                0,
                HWND::default(),
                HMENU::default(),
                instance,
                None,
            )
            .ret("CreateWindowExW")
        }
    }

    fn watcher_main(ready: &Sender<Result<u32>>, events: Sender<Vec<MonitorInfo>>) {
        // SAFETY: plain Win32 calls on this thread; `msg` outlives every call that writes to it.
        unsafe {
            let mut msg = MSG::default();
            // Create the message queue before publishing the thread id (see hooks.rs for the rationale).
            let _ = PeekMessageW(&mut msg, HWND::default(), WM_USER, WM_USER, PM_NOREMOVE);
            let hwnd = match create_hidden_window() {
                Ok(h) => h,
                Err(e) => {
                    let _ = ready.send(Err(e));
                    return;
                }
            };
            let raw = hwnd.0 as isize;
            WATCHERS.lock().push(Watcher { hwnd: raw, tx: events, last: enumerate().unwrap_or_default() });
            let _ = ready.send(Ok(GetCurrentThreadId()));
            while GetMessageW(&mut msg, HWND::default(), 0, 0).0 > 0 {
                let _ = TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            WATCHERS.lock().retain(|w| w.hwnd != raw);
            let _ = DestroyWindow(hwnd);
        }
    }

    /// Background thread that emits the new monitor list whenever the display topology, work area
    /// or DPI changes. Dropping it stops the thread.
    pub struct DisplayWatcher {
        thread_id: u32,
        join: Option<JoinHandle<()>>,
    }

    impl DisplayWatcher {
        /// Starts watching; the receiver yields the full monitor list after each change.
        pub fn start() -> Result<(Self, Receiver<Vec<MonitorInfo>>)> {
            let (events_tx, events_rx) = mpsc::channel();
            let (ready_tx, ready_rx) = mpsc::channel::<Result<u32>>();
            let join = std::thread::Builder::new()
                .name("winutil-displaywatch".to_owned())
                .spawn(move || watcher_main(&ready_tx, events_tx))?;
            let thread_id = ready_rx.recv().map_err(|_| WinUtilError::Closed)??;
            tracing::debug!(thread_id, "display watcher started");
            Ok((Self { thread_id, join: Some(join) }, events_rx))
        }
    }

    impl Drop for DisplayWatcher {
        fn drop(&mut self) {
            // SAFETY: posting a thread message has no memory-safety preconditions.
            let _ = unsafe { PostThreadMessageW(self.thread_id, WM_QUIT, WPARAM(0), LPARAM(0)) };
            if let Some(join) = self.join.take() {
                let _ = join.join();
            }
        }
    }
}

#[cfg(not(windows))]
mod imp {
    use super::*;
    use crate::WinUtilError;

    pub fn enumerate() -> Result<Vec<MonitorInfo>> {
        Err(WinUtilError::Unsupported)
    }

    pub fn primary() -> Result<MonitorInfo> {
        Err(WinUtilError::Unsupported)
    }

    pub fn monitor_at_point(_x: i32, _y: i32) -> Result<MonitorInfo> {
        Err(WinUtilError::Unsupported)
    }

    /// Non-Windows stub: [`start`](Self::start) always fails with [`WinUtilError::Unsupported`].
    pub struct DisplayWatcher {
        _private: (),
    }

    impl DisplayWatcher {
        pub fn start() -> Result<(Self, Receiver<Vec<MonitorInfo>>)> {
            Err(WinUtilError::Unsupported)
        }
    }
}

pub use imp::{enumerate, monitor_at_point, primary, DisplayWatcher};

#[cfg(test)]
mod tests {
    use super::*;

    fn mon(index: usize, rect: Rect, primary: bool) -> MonitorInfo {
        MonitorInfo {
            index,
            handle: index as isize + 1,
            name: format!(r"\\.\DISPLAY{}", index + 1),
            rect,
            work_rect: rect,
            width: rect.width(),
            height: rect.height(),
            hz: 144,
            primary,
            scale: 1.0,
        }
    }

    #[test]
    fn primary_selection() {
        let left = mon(0, Rect::from_size(-1920, 0, 1920, 1080), false);
        let main = mon(1, Rect::from_size(0, 0, 2560, 1440), true);
        let right = mon(2, Rect::from_size(2560, 0, 1920, 1080), false);
        let all = [left, main.clone(), right];
        assert_eq!(pick_primary(&all), Some(&main));

        // No primary flag: the monitor at the virtual-screen origin wins.
        let unflagged: Vec<MonitorInfo> = all.iter().cloned().map(|m| MonitorInfo { primary: false, ..m }).collect();
        assert_eq!(pick_primary(&unflagged).map(|m| m.index), Some(1));

        // Nothing at the origin either: first in enumeration order.
        let shifted = [mon(0, Rect::from_size(100, 100, 800, 600), false)];
        assert_eq!(pick_primary(&shifted).map(|m| m.index), Some(0));
        assert_eq!(pick_primary(&[]), None);
    }

    #[test]
    fn monitor_at_point_prefers_containing_then_nearest() {
        let a = mon(0, Rect::from_size(0, 0, 1920, 1080), true);
        let b = mon(1, Rect::from_size(1920, 0, 1920, 1080), false);
        let all = [a, b];
        assert_eq!(monitor_containing(&all, 10, 10).map(|m| m.index), Some(0));
        assert_eq!(monitor_containing(&all, 1920, 10).map(|m| m.index), Some(1));
        assert_eq!(monitor_containing(&all, 5000, 2000).map(|m| m.index), Some(1));
        assert_eq!(monitor_containing(&all, -50, -50).map(|m| m.index), Some(0));
        assert!(monitor_containing(&[], 0, 0).is_none());
    }
}
