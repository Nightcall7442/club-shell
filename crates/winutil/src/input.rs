//! Input helpers: idle detection, `BlockInput`, `SendInput`, cursor visibility / clipping / position.
//!
//! `block_input` needs the caller to run as an administrator or with `uiAccess`; the kiosk shell runs
//! as a plain user, so it normally fails with `ERROR_ACCESS_DENIED` and the shell falls back to the
//! hooks in [`crate::hooks`]. It also never blocks `Ctrl+Alt+Del`.

use std::time::Duration;

use crate::{Rect, Result};

#[cfg(windows)]
mod imp {
    use super::*;

    use windows::Win32::Foundation::{BOOL, POINT, RECT};
    use windows::Win32::System::SystemInformation::GetTickCount;
    use windows::Win32::UI::Input::KeyboardAndMouse::{
        BlockInput, GetAsyncKeyState, GetLastInputInfo, SendInput, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT,
        KEYBD_EVENT_FLAGS, KEYEVENTF_KEYUP, KEYEVENTF_UNICODE, LASTINPUTINFO, VIRTUAL_KEY,
    };
    use windows::Win32::UI::WindowsAndMessaging::{ClipCursor, GetCursorPos, SetCursorPos, ShowCursor};

    use crate::{last_error, Win32Ret};

    /// Time since the last keyboard/mouse input in this session (`GetLastInputInfo`, ~16 ms
    /// resolution, wraps every 49.7 days which `wrapping_sub` handles).
    pub fn idle_duration() -> Result<Duration> {
        let mut info = LASTINPUTINFO { cbSize: std::mem::size_of::<LASTINPUTINFO>() as u32, dwTime: 0 };
        // SAFETY: `info` is a correctly sized, writable struct.
        unsafe { GetLastInputInfo(&mut info) }.ret("GetLastInputInfo")?;
        // SAFETY: no preconditions.
        let now = unsafe { GetTickCount() };
        Ok(Duration::from_millis(u64::from(now.wrapping_sub(info.dwTime))))
    }

    /// Blocks (or unblocks) all keyboard and mouse input for the calling thread's desktop.
    /// Requires administrator rights or `uiAccess`; Windows also clears the block when the blocking
    /// thread exits or the user presses `Ctrl+Alt+Del`.
    pub fn block_input(block: bool) -> Result<()> {
        // SAFETY: no preconditions.
        unsafe { BlockInput(BOOL::from(block)) }.ret("BlockInput")
    }

    fn send_inputs(inputs: &[INPUT]) -> Result<()> {
        if inputs.is_empty() {
            return Ok(());
        }
        // SAFETY: `inputs` is a valid slice and `cbsize` is the element size.
        let sent = unsafe { SendInput(inputs, std::mem::size_of::<INPUT>() as i32) };
        if sent as usize == inputs.len() {
            Ok(())
        } else {
            Err(last_error("SendInput"))
        }
    }

    fn key_input(vk: u16, scan: u16, flags: KEYBD_EVENT_FLAGS) -> INPUT {
        INPUT {
            r#type: INPUT_KEYBOARD,
            Anonymous: INPUT_0 { ki: KEYBDINPUT { wVk: VIRTUAL_KEY(vk), wScan: scan, dwFlags: flags, time: 0, dwExtraInfo: 0 } },
        }
    }

    /// Injects one virtual-key transition (`up = false` → key down).
    pub fn send_key(vk: u32, up: bool) -> Result<()> {
        let flags = if up { KEYEVENTF_KEYUP } else { KEYBD_EVENT_FLAGS(0) };
        send_inputs(&[key_input(vk as u16, 0, flags)])
    }

    /// Key down followed by key up.
    pub fn tap_key(vk: u32) -> Result<()> {
        send_inputs(&[key_input(vk as u16, 0, KEYBD_EVENT_FLAGS(0)), key_input(vk as u16, 0, KEYEVENTF_KEYUP)])
    }

    /// Types `text` into the focused window as Unicode key events (`KEYEVENTF_UNICODE`), so it works
    /// independently of the active keyboard layout.
    pub fn send_text(text: &str) -> Result<()> {
        let mut batch: Vec<INPUT> = Vec::with_capacity(text.len() * 2);
        for unit in text.encode_utf16() {
            batch.push(key_input(0, unit, KEYEVENTF_UNICODE));
            batch.push(key_input(0, unit, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        // Chunked so one rejected batch (e.g. a UIPI-protected foreground window) reports precisely.
        for chunk in batch.chunks(128) {
            send_inputs(chunk)?;
        }
        Ok(())
    }

    /// Increments (`show`) or decrements the cursor display counter of the calling thread; the
    /// cursor is visible while the counter is ≥ 0. Returns the new counter.
    pub fn show_cursor(show: bool) -> i32 {
        // SAFETY: no preconditions.
        unsafe { ShowCursor(BOOL::from(show)) }
    }

    /// Confines the cursor to `rect` (screen coordinates) or releases it with `None`.
    pub fn clip_cursor(rect: Option<Rect>) -> Result<()> {
        let r: Option<RECT> = rect.map(RECT::from);
        // SAFETY: the RECT lives for the duration of the call.
        unsafe { ClipCursor(r.as_ref().map(|r| r as *const RECT)) }.ret("ClipCursor")
    }

    /// Current cursor position in screen coordinates.
    pub fn get_cursor_pos() -> Result<(i32, i32)> {
        let mut pt = POINT::default();
        // SAFETY: `pt` is writable.
        unsafe { GetCursorPos(&mut pt) }.ret("GetCursorPos")?;
        Ok((pt.x, pt.y))
    }

    /// Moves the cursor to screen coordinates.
    pub fn set_cursor_pos(x: i32, y: i32) -> Result<()> {
        // SAFETY: no preconditions.
        unsafe { SetCursorPos(x, y) }.ret("SetCursorPos")
    }

    /// `true` while virtual key `vk` is physically down (`GetAsyncKeyState`).
    pub fn is_key_down(vk: u32) -> bool {
        // SAFETY: no preconditions.
        let state = unsafe { GetAsyncKeyState(vk as i32) };
        (state as u16 & 0x8000) != 0
    }
}

#[cfg(not(windows))]
mod imp {
    use super::*;
    use crate::WinUtilError;

    pub fn idle_duration() -> Result<Duration> {
        Err(WinUtilError::Unsupported)
    }

    pub fn block_input(_block: bool) -> Result<()> {
        Err(WinUtilError::Unsupported)
    }

    pub fn send_key(_vk: u32, _up: bool) -> Result<()> {
        Err(WinUtilError::Unsupported)
    }

    pub fn tap_key(_vk: u32) -> Result<()> {
        Err(WinUtilError::Unsupported)
    }

    pub fn send_text(_text: &str) -> Result<()> {
        Err(WinUtilError::Unsupported)
    }

    pub fn show_cursor(_show: bool) -> i32 {
        0
    }

    pub fn clip_cursor(_rect: Option<Rect>) -> Result<()> {
        Err(WinUtilError::Unsupported)
    }

    pub fn get_cursor_pos() -> Result<(i32, i32)> {
        Err(WinUtilError::Unsupported)
    }

    pub fn set_cursor_pos(_x: i32, _y: i32) -> Result<()> {
        Err(WinUtilError::Unsupported)
    }

    pub fn is_key_down(_vk: u32) -> bool {
        false
    }
}

pub use imp::{
    block_input, clip_cursor, get_cursor_pos, idle_duration, is_key_down, send_key, send_text, set_cursor_pos,
    show_cursor, tap_key,
};
