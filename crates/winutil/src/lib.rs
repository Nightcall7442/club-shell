//! `clubshell-winutil` — safe Win32 helpers for the ClubShell kiosk shell (`apps/shell/src-tauri`).
//!
//! Modules:
//! - [`hooks`]: `WH_KEYBOARD_LL` / `WH_MOUSE_LL` on a dedicated message-loop thread, blocked-combo parsing.
//! - [`input`]: idle time (`GetLastInputInfo`), `BlockInput`, `SendInput`, cursor show/clip/position.
//! - [`monitor`]: display enumeration (`EnumDisplayMonitors`) and a `WM_DISPLAYCHANGE` watcher.
//! - [`pipe`]: async client for `\\.\pipe\clubshell-agent` (IPC_PROTOCOL.md §1–§4).
//! - [`window`]: topmost / borderless fullscreen / foreground / taskbar / capture-exclusion helpers.
//!
//! Every FFI call is wrapped in a safe function whose invariants are documented at the call site.
//! Window handles cross this API as `isize` (the raw `HWND` value) so callers do not have to agree on
//! a `windows` crate version. On non-Windows targets every platform function keeps its signature and
//! returns [`WinUtilError::Unsupported`], so the pure logic (combo parsing, framing, monitor
//! selection, the pipe state machine) builds and is tested on Linux CI.

pub mod hooks;
pub mod input;
pub mod monitor;
pub mod pipe;
pub mod window;

pub use hooks::{
    BlockedCombo, HookAction, KeyEvent, KeyFilter, LowLevelKeyboardHook, LowLevelMouseHook, MouseEvent, MouseFilter,
};
pub use monitor::{DisplayWatcher, MonitorInfo};
pub use pipe::{CloseReason, ConnectionState, PipeClient, PipeOptions};
pub use window::{TopmostGuard, WindowInfo};

use clubshell_protocol::error::{IpcError, ProtocolError};

/// Errors of this crate.
#[derive(Debug, thiserror::Error)]
pub enum WinUtilError {
    /// A Win32 API failed. `code` is the HRESULT form (`0x8007xxxx` for a Win32 error code).
    #[error("{api} failed: {msg} (0x{code:08X})")]
    Win32 {
        /// Name of the API that failed.
        api: &'static str,
        /// HRESULT.
        code: u32,
        /// System message for `code`.
        msg: String,
    },
    /// The function needs Windows (or a Windows feature that is absent on this host).
    #[error("not supported on this platform")]
    Unsupported,
    /// I/O failure (pipe open/read/write, thread spawn).
    #[error("I/O error: {0}")]
    Io(#[from] std::io::Error),
    /// A request or connect attempt exceeded its deadline.
    #[error("operation timed out")]
    Timeout,
    /// The pipe connection is closed (peer closed, heartbeat lost or shut down locally).
    #[error("connection closed")]
    Closed,
    /// Frame encoding/decoding failure (IPC_PROTOCOL.md §1).
    #[error("framing error: {0}")]
    Frame(#[from] ProtocolError),
    /// The Agent answered a request with an error envelope.
    #[error("agent error: {0}")]
    Ipc(Box<IpcError>),
    /// A caller-supplied value is malformed (unknown key name, missing window, …).
    #[error("invalid argument: {0}")]
    Invalid(String),
    /// A per-process singleton (a low-level hook) is already installed.
    #[error("{0} is already installed in this process")]
    Busy(&'static str),
}

impl From<IpcError> for WinUtilError {
    fn from(e: IpcError) -> Self {
        Self::Ipc(Box::new(e))
    }
}

/// Result alias of this crate.
pub type Result<T, E = WinUtilError> = std::result::Result<T, E>;

/// Screen rectangle in physical pixels (`RECT` semantics: `right`/`bottom` exclusive).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub struct Rect {
    pub left: i32,
    pub top: i32,
    pub right: i32,
    pub bottom: i32,
}

impl Rect {
    /// Rectangle from edges.
    pub const fn new(left: i32, top: i32, right: i32, bottom: i32) -> Self {
        Self { left, top, right, bottom }
    }

    /// Rectangle from origin and size.
    pub const fn from_size(x: i32, y: i32, width: i32, height: i32) -> Self {
        Self { left: x, top: y, right: x + width, bottom: y + height }
    }

    pub const fn width(&self) -> i32 {
        self.right - self.left
    }

    pub const fn height(&self) -> i32 {
        self.bottom - self.top
    }

    /// `true` when `(x, y)` lies inside (right/bottom edges exclusive).
    pub const fn contains(&self, x: i32, y: i32) -> bool {
        x >= self.left && x < self.right && y >= self.top && y < self.bottom
    }

    pub const fn is_empty(&self) -> bool {
        self.right <= self.left || self.bottom <= self.top
    }
}

#[cfg(windows)]
mod win {
    use super::{Rect, Result, WinUtilError};
    use windows::Win32::Foundation::{BOOL, HWND, RECT};

    /// Error for the calling thread's `GetLastError`, attributed to `api`.
    pub fn last_error(api: &'static str) -> WinUtilError {
        from_error(api, windows::core::Error::from_win32())
    }

    pub(crate) fn from_error(api: &'static str, e: windows::core::Error) -> WinUtilError {
        WinUtilError::Win32 { api, code: e.code().0 as u32, msg: e.message() }
    }

    /// Wraps a raw `HWND` value (as passed across this crate's API).
    pub fn hwnd_from_raw(raw: isize) -> HWND {
        HWND(raw as *mut core::ffi::c_void)
    }

    /// Raw value of an `HWND`.
    pub fn hwnd_to_raw(hwnd: HWND) -> isize {
        hwnd.0 as isize
    }

    /// Uniform "did the call succeed" conversion for the three Win32 return conventions used here:
    /// `BOOL` (false → `GetLastError`), `windows::core::Result<T>` and a nullable `HWND`.
    pub(crate) trait Win32Ret<T> {
        fn ret(self, api: &'static str) -> Result<T>;
    }

    impl Win32Ret<()> for BOOL {
        fn ret(self, api: &'static str) -> Result<()> {
            if self.as_bool() {
                Ok(())
            } else {
                Err(last_error(api))
            }
        }
    }

    impl<T> Win32Ret<T> for windows::core::Result<T> {
        fn ret(self, api: &'static str) -> Result<T> {
            self.map_err(|e| from_error(api, e))
        }
    }

    impl Win32Ret<HWND> for HWND {
        fn ret(self, api: &'static str) -> Result<HWND> {
            if self.0.is_null() {
                Err(last_error(api))
            } else {
                Ok(self)
            }
        }
    }

    impl From<windows::core::Error> for WinUtilError {
        fn from(e: windows::core::Error) -> Self {
            from_error("win32", e)
        }
    }

    impl From<RECT> for Rect {
        fn from(r: RECT) -> Self {
            Rect { left: r.left, top: r.top, right: r.right, bottom: r.bottom }
        }
    }

    impl From<Rect> for RECT {
        fn from(r: Rect) -> Self {
            RECT { left: r.left, top: r.top, right: r.right, bottom: r.bottom }
        }
    }
}

#[cfg(windows)]
pub use win::{hwnd_from_raw, hwnd_to_raw, last_error};
#[cfg(windows)]
pub(crate) use win::{from_error, Win32Ret};

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rect_geometry() {
        let r = Rect::from_size(10, 20, 100, 50);
        assert_eq!(r, Rect::new(10, 20, 110, 70));
        assert_eq!((r.width(), r.height()), (100, 50));
        assert!(r.contains(10, 20));
        assert!(!r.contains(110, 20));
        assert!(!r.is_empty());
        assert!(Rect::new(5, 5, 5, 9).is_empty());
    }

    #[test]
    fn error_display() {
        let e = WinUtilError::Win32 { api: "SetWindowPos", code: 0x8007_0005, msg: "Access is denied.".into() };
        assert_eq!(e.to_string(), "SetWindowPos failed: Access is denied. (0x80070005)");
        assert_eq!(WinUtilError::Busy("WH_KEYBOARD_LL hook").to_string(), "WH_KEYBOARD_LL hook is already installed in this process");
        let ipc: WinUtilError = IpcError::timeout(None).into();
        assert!(matches!(ipc, WinUtilError::Ipc(ref e) if e.code == clubshell_protocol::error::ErrorCode::Timeout));
    }
}
