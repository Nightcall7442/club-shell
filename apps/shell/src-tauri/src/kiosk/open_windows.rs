//! Programs open in the player's session, for the dock in the status bar (`kiosk_open_windows`,
//! `kiosk_focus_window`; `TAURI_COMMANDS.md` §2.14). The Agent runs in session 0 and cannot see these
//! windows, so the Shell lists them itself: the top-level windows the taskbar would show (visible,
//! not cloaked, titled, unowned or `WS_EX_APPWINDOW`, no tool or no-activate window) minus the Shell,
//! Windows' own UI and the tools the process policy denies, one entry per process with the exe icon as
//! a small PNG data URL (extracted once per exe path). Focusing one hands the foreground to its process
//! ([`Kiosk::hand_off_foreground`]) so the guard does not pull the Shell back over it.

use std::collections::{HashMap, HashSet};
use std::sync::{Arc, OnceLock};
use std::time::Duration;

use clubshell_winutil::window::{force_foreground, foreground_window, window_pid};
use parking_lot::Mutex;
use serde::Serialize;
use tauri::State;

use super::window_guard::SKIP_CLASSES;
use super::Kiosk;
use crate::state::{AppState, CmdResult, ShellError};

/// Edge of the extracted exe icons in pixels: sharp in the ~32 px dock slot up to 150 % scaling.
pub const ICON_SIZE: u32 = 48;
/// Distinct exe paths whose icons are kept; the cache starts over beyond that.
const ICON_CACHE_MAX: usize = 128;
/// How long `kiosk_focus_window` waits for a (possibly hung) program to come to the front.
const FOCUS_TIMEOUT: Duration = Duration::from_secs(2);
/// A minimized window is restored asynchronously; it gets this long to un-minimize before the
/// foreground request (`RESTORE_POLLS` × `RESTORE_POLL`).
const RESTORE_POLL: Duration = Duration::from_millis(25);
const RESTORE_POLLS: u32 = 10;

/// Never a "program" for the player, by exe file name (lower case): Windows' own UI, overlays that
/// own visible windows, and the tools the Agent's process policy denies (they are killed anyway, the
/// dock must not offer them in the meantime).
const HIDDEN_EXES: &[&str] = &[
    "explorer.exe",
    "textinputhost.exe",
    "shellexperiencehost.exe",
    "startmenuexperiencehost.exe",
    "searchhost.exe",
    "searchapp.exe",
    "searchui.exe",
    "lockapp.exe",
    "applicationframehost.exe",
    "systemsettings.exe",
    "tabtip.exe",
    "ctfmon.exe",
    "dwm.exe",
    "sihost.exe",
    "taskhostw.exe",
    "runtimebroker.exe",
    "widgets.exe",
    "gamebar.exe",
    "gamebarftserver.exe",
    "securityhealthsystray.exe",
    "msedgewebview2.exe",
    "nvidia share.exe",
    "nvidia overlay.exe",
    "nvcontainer.exe",
    "cmd.exe",
    "conhost.exe",
    "openconsole.exe",
    "windowsterminal.exe",
    "powershell.exe",
    "powershell_ise.exe",
    "pwsh.exe",
    "regedit.exe",
    "taskmgr.exe",
    "mmc.exe",
    "control.exe",
    "msconfig.exe",
    "wscript.exe",
    "cscript.exe",
    "mshta.exe",
    "rundll32.exe",
];

/// One top-level window as the dock filter sees it (gathered natively, judged by [`is_program_window`]).
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct WindowDescriptor {
    pub hwnd: isize,
    pub pid: u32,
    pub title: String,
    pub class: String,
    /// Full image path of the owning process; empty when it cannot be read.
    pub exe_path: String,
    pub visible: bool,
    /// Has an owner window (dialogs, popups, splash screens).
    pub owned: bool,
    pub tool_window: bool,
    /// `WS_EX_APPWINDOW`: on the taskbar even when owned.
    pub app_window: bool,
    pub no_activate: bool,
    /// Hidden by DWM (`DWMWA_CLOAKED`): suspended UWP frames, other virtual desktops.
    pub cloaked: bool,
    /// Zero-sized window rectangle.
    pub empty: bool,
}

/// One program with an open window (`kiosk_open_windows`).
#[derive(Serialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct OpenWindow {
    pub pid: u32,
    /// Its front-most window: the one `kiosk_focus_window` raises.
    pub hwnd: isize,
    pub title: String,
    pub exe_path: String,
    /// `data:image/png;base64,…` of the exe icon; `None` when the exe has none.
    pub icon: Option<String>,
}

/// `true` for Windows' own UI processes, the denied tools and ClubShell's own executables.
pub fn is_hidden_exe(exe_path: &str) -> bool {
    let name = exe_path
        .rsplit(['\\', '/'])
        .next()
        .unwrap_or_default()
        .to_ascii_lowercase();
    HIDDEN_EXES.contains(&name.as_str()) || name.starts_with("clubshell")
}

/// `true` for a window the player would call a program: what the taskbar shows, owned by neither
/// the Shell (`own_pid`) nor Windows' own UI.
pub fn is_program_window(w: &WindowDescriptor, own_pid: u32) -> bool {
    w.visible
        && !w.cloaked
        && !w.empty
        && !w.title.trim().is_empty()
        && w.pid != 0
        && w.pid != own_pid
        && (w.app_window || (!w.owned && !w.tool_window && !w.no_activate))
        && !SKIP_CLASSES
            .iter()
            .any(|c| c.eq_ignore_ascii_case(&w.class))
        && !is_hidden_exe(&w.exe_path)
}

/// The programs among `windows` (Z order, top first): one per process, represented by its front-most
/// window, front-most program first.
pub fn programs(windows: &[WindowDescriptor], own_pid: u32) -> Vec<&WindowDescriptor> {
    let mut seen = HashSet::new();
    windows
        .iter()
        .filter(|w| is_program_window(w, own_pid) && seen.insert(w.pid))
        .collect()
}

/// `kiosk_open_windows`: the programs open in this session, front-most first (empty off Windows).
#[tauri::command]
pub async fn kiosk_open_windows() -> CmdResult<Vec<OpenWindow>> {
    // Blocking pool: icon extraction reads the exe from disk.
    Ok(tauri::async_runtime::spawn_blocking(list).await?)
}

fn list() -> Vec<OpenWindow> {
    let windows = native::windows();
    programs(&windows, std::process::id())
        .into_iter()
        .map(|w| OpenWindow {
            pid: w.pid,
            hwnd: w.hwnd,
            title: w.title.trim().to_owned(),
            exe_path: w.exe_path.clone(),
            icon: icon_for(&w.exe_path),
        })
        .collect()
}

/// `kiosk_focus_window`: brings a program listed by `kiosk_open_windows` to the front (restored if
/// minimized; the Shell goes behind it) and hands it the foreground. `forbidden` without an open
/// session or while locked, `notFound` when the window is gone or no longer a program. Returns
/// whether the program is in front afterwards; when it is not, the Shell takes the screen back.
#[tauri::command]
pub async fn kiosk_focus_window(
    state: State<'_, AppState>,
    kiosk: State<'_, Arc<Kiosk>>,
    hwnd: isize,
) -> CmdResult<bool> {
    if kiosk.is_locked() || state.session().is_none() {
        return Err(ShellError::forbidden(
            "switching programs needs an open session",
        ));
    }
    let own_pid = std::process::id();
    // Only a window the dock would list: the webview cannot raise arbitrary (hidden, system) ones.
    let pid = tauri::async_runtime::spawn_blocking(move || {
        programs(&native::windows(), own_pid)
            .into_iter()
            .find(|w| w.hwnd == hwnd)
            .map(|w| w.pid)
    })
    .await?
    .ok_or_else(|| ShellError::not_found("window"))?;

    kiosk.hand_off_foreground(pid);
    let raised = tauri::async_runtime::spawn_blocking(move || bring_to_front(hwnd, pid));
    let front = matches!(
        tokio::time::timeout(FOCUS_TIMEOUT, raised).await,
        Ok(Ok(true))
    );
    if front {
        tracing::info!(pid, "program brought to the front");
    } else {
        tracing::warn!(
            pid,
            "program did not come to the front; the shell takes it back"
        );
        kiosk.end_hand_off();
    }
    Ok(front)
}

/// Restores `hwnd` when minimized and makes it the foreground window; `true` when `pid` owns the
/// foreground afterwards (it may have activated another of its windows, a dialog say).
fn bring_to_front(hwnd: isize, pid: u32) -> bool {
    // The Shell owns the foreground right now: let the program activate its own windows too.
    native::allow_set_foreground(pid);
    if native::restore(hwnd) {
        for _ in 0..RESTORE_POLLS {
            if !native::is_minimized(hwnd) {
                break;
            }
            std::thread::sleep(RESTORE_POLL);
        }
    }
    if let Err(e) = force_foreground(hwnd) {
        tracing::debug!(error = %e, "force_foreground failed");
    }
    window_pid(foreground_window()).is_ok_and(|p| p == pid)
}

// ───────────────────────────── icons ─────────────────────────────

fn icon_cache() -> &'static Mutex<HashMap<String, Option<Arc<str>>>> {
    static CACHE: OnceLock<Mutex<HashMap<String, Option<Arc<str>>>>> = OnceLock::new();
    CACHE.get_or_init(Mutex::default)
}

/// Exe icon as a PNG data URL, extracted once per exe path (a miss is remembered too).
fn icon_for(exe_path: &str) -> Option<String> {
    if exe_path.is_empty() {
        return None;
    }
    let key = exe_path.to_lowercase();
    if let Some(hit) = icon_cache().lock().get(&key) {
        return hit.as_deref().map(str::to_owned);
    }
    let icon: Option<Arc<str>> = native::exe_icon(exe_path, ICON_SIZE)
        .map(|(width, height, rgba)| png_data_url(width, height, &rgba).into());
    let mut cache = icon_cache().lock();
    if cache.len() >= ICON_CACHE_MAX {
        cache.clear();
    }
    cache.insert(key, icon.clone());
    icon.as_deref().map(str::to_owned)
}

/// `data:image/png;base64,…` of an 8-bit RGBA image (rows top to bottom).
pub fn png_data_url(width: u32, height: u32, rgba: &[u8]) -> String {
    format!(
        "data:image/png;base64,{}",
        base64(&encode_png(width, height, rgba))
    )
}

/// Minimal PNG encoder: one IDAT of stored (uncompressed) deflate blocks. An icon is a few KB and
/// is extracted once per exe, so a compression crate is not worth pulling in for it.
fn encode_png(width: u32, height: u32, rgba: &[u8]) -> Vec<u8> {
    let stride = width as usize * 4;
    let mut raw = Vec::with_capacity((stride + 1) * height as usize);
    for row in rgba.chunks_exact(stride.max(1)).take(height as usize) {
        raw.push(0); // filter: none
        raw.extend_from_slice(row);
    }
    let mut header = Vec::with_capacity(13);
    header.extend_from_slice(&width.to_be_bytes());
    header.extend_from_slice(&height.to_be_bytes());
    // 8 bits per channel, RGBA, deflate, adaptive filtering, no interlace.
    header.extend_from_slice(&[8, 6, 0, 0, 0]);
    let mut out = Vec::with_capacity(raw.len() + 64);
    out.extend_from_slice(b"\x89PNG\r\n\x1a\n");
    png_chunk(&mut out, b"IHDR", &header);
    png_chunk(&mut out, b"IDAT", &zlib_stored(&raw));
    png_chunk(&mut out, b"IEND", &[]);
    out
}

fn png_chunk(out: &mut Vec<u8>, kind: &[u8; 4], data: &[u8]) {
    out.extend_from_slice(&(data.len() as u32).to_be_bytes());
    out.extend_from_slice(kind);
    out.extend_from_slice(data);
    out.extend_from_slice(&crc32(kind.iter().chain(data)).to_be_bytes());
}

/// zlib stream of stored deflate blocks (≤ 65 535 bytes each) with the Adler-32 trailer.
fn zlib_stored(data: &[u8]) -> Vec<u8> {
    const BLOCK: usize = 65_535;
    let mut out = Vec::with_capacity(data.len() + data.len() / BLOCK * 5 + 11);
    out.extend_from_slice(&[0x78, 0x01]);
    let blocks = data.len().div_ceil(BLOCK).max(1);
    for (i, block) in data
        .chunks(BLOCK)
        .chain(data.is_empty().then_some(&[][..]))
        .enumerate()
    {
        let len = block.len() as u16;
        out.push(u8::from(i + 1 == blocks)); // BFINAL on the last block, BTYPE 00 (stored)
        out.extend_from_slice(&len.to_le_bytes());
        out.extend_from_slice(&(!len).to_le_bytes());
        out.extend_from_slice(block);
    }
    out.extend_from_slice(&adler32(data).to_be_bytes());
    out
}

const CRC_TABLE: [u32; 256] = {
    let mut table = [0u32; 256];
    let mut n = 0;
    while n < 256 {
        let mut c = n as u32;
        let mut k = 0;
        while k < 8 {
            c = if c & 1 != 0 {
                0xEDB8_8320 ^ (c >> 1)
            } else {
                c >> 1
            };
            k += 1;
        }
        table[n] = c;
        n += 1;
    }
    table
};

fn crc32<'a>(bytes: impl IntoIterator<Item = &'a u8>) -> u32 {
    !bytes.into_iter().fold(!0u32, |c, &b| {
        CRC_TABLE[((c ^ u32::from(b)) & 0xFF) as usize] ^ (c >> 8)
    })
}

fn adler32(data: &[u8]) -> u32 {
    let (mut a, mut b) = (1u32, 0u32);
    for &byte in data {
        a = (a + u32::from(byte)) % 65_521;
        b = (b + a) % 65_521;
    }
    (b << 16) | a
}

/// Standard base64 with padding.
fn base64(bytes: &[u8]) -> String {
    const ALPHABET: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::with_capacity(bytes.len().div_ceil(3) * 4);
    for group in bytes.chunks(3) {
        let n = group
            .iter()
            .enumerate()
            .fold(0u32, |n, (i, &b)| n | (u32::from(b) << (16 - 8 * i)));
        for i in 0..4 {
            if i <= group.len() {
                out.push(ALPHABET[(n >> (18 - 6 * i)) as usize & 63] as char);
            } else {
                out.push('=');
            }
        }
    }
    out
}

// ───────────────────────────── native ─────────────────────────────

#[cfg(windows)]
mod native {
    use std::collections::HashMap;

    use clubshell_winutil::hwnd_from_raw;
    use clubshell_winutil::window::enumerate_windows;
    use windows::core::PCWSTR;
    use windows::Win32::Foundation::{RECT, S_OK};
    use windows::Win32::Graphics::Dwm::{DwmGetWindowAttribute, DWMWA_CLOAKED};
    use windows::Win32::Graphics::Gdi::{
        CreateCompatibleDC, DeleteDC, DeleteObject, GetDIBits, GetObjectW, BITMAP, BITMAPINFO,
        BITMAPINFOHEADER, BI_RGB, DIB_RGB_COLORS, HBITMAP, HDC,
    };
    use windows::Win32::UI::Shell::SHDefExtractIconW;
    use windows::Win32::UI::WindowsAndMessaging::{
        AllowSetForegroundWindow, DestroyIcon, GetIconInfo, GetWindow, GetWindowLongW,
        GetWindowRect, IsIconic, ShowWindowAsync, GWL_EXSTYLE, GW_OWNER, HICON, ICONINFO,
        SW_RESTORE, WS_EX_APPWINDOW, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW,
    };

    use super::WindowDescriptor;
    use crate::kiosk::window_guard::process_path;

    /// Every top-level window in Z order. Only visible, titled ones get the extra queries: the
    /// filter drops the rest anyway.
    pub fn windows() -> Vec<WindowDescriptor> {
        let Ok(list) = enumerate_windows() else {
            return Vec::new();
        };
        let mut exes: HashMap<u32, String> = HashMap::new();
        list.into_iter()
            .map(|w| {
                let mut d = WindowDescriptor {
                    hwnd: w.hwnd,
                    pid: w.pid,
                    title: w.title,
                    class: w.class,
                    visible: w.visible,
                    ..WindowDescriptor::default()
                };
                if d.visible && !d.title.trim().is_empty() {
                    describe(&mut d);
                    d.exe_path = exes
                        .entry(d.pid)
                        .or_insert_with(|| process_path(d.pid).unwrap_or_default())
                        .clone();
                }
                d
            })
            .collect()
    }

    fn describe(d: &mut WindowDescriptor) {
        let h = hwnd_from_raw(d.hwnd);
        // SAFETY: plain queries on a window handle with valid out-pointers; a stale handle just fails.
        unsafe {
            let ex = GetWindowLongW(h, GWL_EXSTYLE) as u32;
            d.tool_window = ex & WS_EX_TOOLWINDOW.0 != 0;
            d.app_window = ex & WS_EX_APPWINDOW.0 != 0;
            d.no_activate = ex & WS_EX_NOACTIVATE.0 != 0;
            d.owned = GetWindow(h, GW_OWNER).is_ok();
            let mut cloaked = 0u32;
            d.cloaked = DwmGetWindowAttribute(
                h,
                DWMWA_CLOAKED,
                (&mut cloaked as *mut u32).cast(),
                size_of::<u32>() as u32,
            )
            .is_ok()
                && cloaked != 0;
            let mut r = RECT::default();
            d.empty = GetWindowRect(h, &mut r).is_err() || r.right <= r.left || r.bottom <= r.top;
        }
    }

    pub fn allow_set_foreground(pid: u32) {
        // SAFETY: no preconditions; it only fails when the caller does not own the foreground.
        let _ = unsafe { AllowSetForegroundWindow(pid) };
    }

    /// Starts restoring `hwnd` without waiting on its (possibly hung) thread; `true` when it was
    /// minimized.
    pub fn restore(hwnd: isize) -> bool {
        let h = hwnd_from_raw(hwnd);
        // SAFETY: no preconditions beyond a window handle.
        unsafe {
            if !IsIconic(h).as_bool() {
                return false;
            }
            let _ = ShowWindowAsync(h, SW_RESTORE);
        }
        true
    }

    pub fn is_minimized(hwnd: isize) -> bool {
        // SAFETY: no preconditions beyond a window handle.
        unsafe { IsIconic(hwnd_from_raw(hwnd)) }.as_bool()
    }

    /// Main icon of the exe at `path`, `size` px square, as `(width, height, RGBA rows top-down)`;
    /// `None` when the file has no icon.
    pub fn exe_icon(path: &str, size: u32) -> Option<(u32, u32, Vec<u8>)> {
        let wide: Vec<u16> = path.encode_utf16().chain(Some(0)).collect();
        let mut icon = HICON::default();
        // SAFETY: `wide` is NUL-terminated and outlives the call; `icon` is a valid out-pointer.
        let hr = unsafe {
            SHDefExtractIconW(
                PCWSTR(wide.as_ptr()),
                0,
                0,
                Some(&mut icon as *mut HICON),
                None,
                size,
            )
        };
        if hr != S_OK || icon.is_invalid() {
            return None; // S_FALSE: no icon in the file
        }
        let pixels = icon_rgba(icon);
        // SAFETY: the icon was created for us by SHDefExtractIconW.
        let _ = unsafe { DestroyIcon(icon) };
        pixels
    }

    fn icon_rgba(icon: HICON) -> Option<(u32, u32, Vec<u8>)> {
        let mut info = ICONINFO::default();
        // SAFETY: valid icon handle and out-pointer; the bitmaps it creates are deleted below.
        unsafe { GetIconInfo(icon, &mut info) }.ok()?;
        let pixels = bitmap_rgba(info.hbmColor, info.hbmMask);
        // SAFETY: GetIconInfo hands over ownership of both bitmaps.
        unsafe {
            if !info.hbmColor.is_invalid() {
                let _ = DeleteObject(info.hbmColor);
            }
            if !info.hbmMask.is_invalid() {
                let _ = DeleteObject(info.hbmMask);
            }
        }
        pixels
    }

    /// The icon's colour bitmap as RGBA; a legacy icon without alpha takes it from the AND mask.
    fn bitmap_rgba(color: HBITMAP, mask: HBITMAP) -> Option<(u32, u32, Vec<u8>)> {
        if color.is_invalid() {
            return None; // monochrome icon
        }
        let mut bm = BITMAP::default();
        // SAFETY: `bm` is a correctly sized out-buffer for a bitmap handle.
        let read = unsafe {
            GetObjectW(
                color,
                size_of::<BITMAP>() as i32,
                Some((&mut bm as *mut BITMAP).cast()),
            )
        };
        let (w, h) = (bm.bmWidth, bm.bmHeight);
        if read == 0 || !(1..=256).contains(&w) || !(1..=256).contains(&h) {
            return None;
        }
        let mut pixels = dib_bits(color, w, h)?;
        if pixels.chunks_exact(4).all(|p| p[3] == 0) {
            // Opacity lives in the mask: black is opaque.
            let m = dib_bits(mask, w, h)?;
            for (p, m) in pixels.chunks_exact_mut(4).zip(m.chunks_exact(4)) {
                p[3] = if m[0] == 0 { 255 } else { 0 };
            }
        }
        for p in pixels.chunks_exact_mut(4) {
            p.swap(0, 2); // BGRA → RGBA
        }
        Some((w as u32, h as u32, pixels))
    }

    /// `bitmap` as 32-bit top-down BGRA.
    fn dib_bits(bitmap: HBITMAP, width: i32, height: i32) -> Option<Vec<u8>> {
        let mut info = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: width,
                biHeight: -height,
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB.0,
                ..BITMAPINFOHEADER::default()
            },
            ..BITMAPINFO::default()
        };
        let mut buf = vec![0u8; width as usize * height as usize * 4];
        // SAFETY: a fresh memory DC; `buf` holds `width × height` 32-bit pixels as `info` describes,
        // and the icon bitmaps are not selected into any DC.
        unsafe {
            let dc = CreateCompatibleDC(HDC::default());
            if dc.is_invalid() {
                return None;
            }
            let lines = GetDIBits(
                dc,
                bitmap,
                0,
                height as u32,
                Some(buf.as_mut_ptr().cast()),
                &mut info,
                DIB_RGB_COLORS,
            );
            let _ = DeleteDC(dc);
            (lines == height).then_some(buf)
        }
    }
}

#[cfg(not(windows))]
mod native {
    use super::WindowDescriptor;

    pub fn windows() -> Vec<WindowDescriptor> {
        Vec::new()
    }

    pub fn allow_set_foreground(_pid: u32) {}

    pub fn restore(_hwnd: isize) -> bool {
        false
    }

    pub fn is_minimized(_hwnd: isize) -> bool {
        false
    }

    pub fn exe_icon(_path: &str, _size: u32) -> Option<(u32, u32, Vec<u8>)> {
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SHELL: u32 = 100;

    fn window(hwnd: isize, pid: u32, title: &str, exe: &str) -> WindowDescriptor {
        WindowDescriptor {
            hwnd,
            pid,
            title: title.into(),
            class: "Chrome_WidgetWin_1".into(),
            exe_path: exe.into(),
            visible: true,
            ..WindowDescriptor::default()
        }
    }

    #[test]
    fn only_what_the_taskbar_would_show_counts() {
        let discord = window(1, 7, "Discord", r"C:\Users\club\Discord\Discord.exe");
        assert!(is_program_window(&discord, SHELL));
        let rejected = [
            WindowDescriptor {
                visible: false,
                ..discord.clone()
            },
            WindowDescriptor {
                cloaked: true,
                ..discord.clone()
            },
            WindowDescriptor {
                empty: true,
                ..discord.clone()
            },
            WindowDescriptor {
                title: "  ".into(),
                ..discord.clone()
            },
            WindowDescriptor {
                owned: true,
                ..discord.clone()
            },
            WindowDescriptor {
                tool_window: true,
                ..discord.clone()
            },
            WindowDescriptor {
                no_activate: true,
                ..discord.clone()
            },
            WindowDescriptor {
                pid: SHELL,
                ..discord.clone()
            },
            WindowDescriptor {
                pid: 0,
                ..discord.clone()
            },
            WindowDescriptor {
                class: "Shell_TrayWnd".into(),
                ..discord.clone()
            },
            window(2, 8, "Program Manager", r"C:\Windows\explorer.exe"),
            window(
                3,
                9,
                "Windows Input Experience",
                r"C:\Windows\SystemApps\TextInputHost.exe",
            ),
            window(4, 10, "Administrator: cmd", r"C:\Windows\System32\CMD.EXE"),
            window(
                5,
                11,
                "ClubShell",
                r"C:\Program Files\ClubShell\clubshell-shell.exe",
            ),
        ];
        for w in &rejected {
            assert!(!is_program_window(w, SHELL), "{w:?}");
        }
        // An owned window forced onto the taskbar is a program; an unreadable exe path is no reason
        // to hide a real window.
        assert!(is_program_window(
            &WindowDescriptor {
                owned: true,
                app_window: true,
                ..discord.clone()
            },
            SHELL
        ));
        assert!(is_program_window(
            &WindowDescriptor {
                exe_path: String::new(),
                ..discord
            },
            SHELL
        ));
    }

    #[test]
    fn one_entry_per_process_front_most_first() {
        let game = r"D:\Games\cs2\game\bin\win64\cs2.exe";
        let chrome = r"C:\Program Files\Google\Chrome\Application\chrome.exe";
        let windows = vec![
            window(10, 20, "Counter-Strike 2", game),
            WindowDescriptor {
                owned: true,
                ..window(11, 30, "Save as", chrome)
            },
            window(12, 30, "News - Google Chrome", chrome),
            window(13, 30, "Mail - Google Chrome", chrome),
            window(14, SHELL, "ClubShell", r"C:\ClubShell\clubshell-shell.exe"),
            window(15, 40, "Discord", r"C:\Discord\Discord.exe"),
        ];
        let found: Vec<(isize, u32)> = programs(&windows, SHELL)
            .into_iter()
            .map(|w| (w.hwnd, w.pid))
            .collect();
        assert_eq!(found, vec![(10, 20), (12, 30), (15, 40)]);
        assert!(programs(&[], SHELL).is_empty());
    }

    #[test]
    fn open_window_wire_shape() {
        let json = serde_json::to_value(OpenWindow {
            pid: 7,
            hwnd: 0x0004_0A2C,
            title: "Discord".into(),
            exe_path: r"C:\Discord\Discord.exe".into(),
            icon: None,
        })
        .unwrap();
        assert_eq!(
            json,
            serde_json::json!({
                "pid": 7,
                "hwnd": 0x0004_0A2C,
                "title": "Discord",
                "exePath": r"C:\Discord\Discord.exe",
                "icon": null
            })
        );
    }

    #[test]
    fn checksums_and_base64_match_the_references() {
        assert_eq!(crc32(b"IEND"), 0xAE42_6082);
        assert_eq!(crc32(b"123456789"), 0xCBF4_3926);
        assert_eq!(adler32(b"Wikipedia"), 0x11E6_0398);
        assert_eq!(base64(b""), "");
        assert_eq!(base64(b"M"), "TQ==");
        assert_eq!(base64(b"Ma"), "TWE=");
        assert_eq!(base64(b"Man"), "TWFu");
        assert_eq!(base64(&[0xFF, 0xEF, 0x00, 0x10]), "/+8AEA==");
    }

    #[test]
    fn png_is_well_formed_and_carries_the_pixels() {
        let rgba = [255, 0, 0, 255, 0, 0, 255, 128]; // 2×1: opaque red, half-transparent blue
        let png = encode_png(2, 1, &rgba);
        assert_eq!(&png[..8], b"\x89PNG\r\n\x1a\n");
        assert_eq!(&png[8..16], b"\x00\x00\x00\x0dIHDR");
        assert_eq!(&png[16..29], &[0, 0, 0, 2, 0, 0, 0, 1, 8, 6, 0, 0, 0]);
        assert_eq!(&png[29..33], &crc32(&png[12..29]).to_be_bytes());
        assert_eq!(
            &png[png.len() - 12..],
            &[0, 0, 0, 0, b'I', b'E', b'N', b'D', 0xAE, 0x42, 0x60, 0x82]
        );
        // IDAT: zlib header, one final stored block of the filtered rows, Adler-32.
        let idat_len = u32::from_be_bytes(png[33..37].try_into().unwrap()) as usize;
        assert_eq!(&png[37..41], b"IDAT");
        let zlib = &png[41..41 + idat_len];
        let rows = [0, 255, 0, 0, 255, 0, 0, 255, 128];
        assert_eq!(&zlib[..2], &[0x78, 0x01]);
        assert_eq!(&zlib[2..7], &[1, 9, 0, 0xF6, 0xFF]);
        assert_eq!(&zlib[7..16], &rows);
        assert_eq!(&zlib[16..], &adler32(&rows).to_be_bytes());
        assert!(png_data_url(2, 1, &rgba).starts_with("data:image/png;base64,iVBORw0KGgo"));
    }

    #[test]
    fn large_streams_split_into_stored_blocks() {
        let data = vec![7u8; 70_000];
        let z = zlib_stored(&data);
        assert_eq!(z.len(), 2 + 5 + 65_535 + 5 + (70_000 - 65_535) + 4);
        assert_eq!(z[2], 0, "first block is not final");
        assert_eq!(z[2 + 5 + 65_535], 1, "second block is final");
        let empty = zlib_stored(&[]);
        assert_eq!(empty, vec![0x78, 0x01, 1, 0, 0, 0xFF, 0xFF, 0, 0, 0, 1]);
    }
}
