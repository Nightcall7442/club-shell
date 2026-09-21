//! Low-level keyboard and mouse hooks (`WH_KEYBOARD_LL`, `WH_MOUSE_LL`) for kiosk hardening.
//!
//! Each hook runs on its own thread with a private message loop: a low-level hook is called on the
//! thread that installed it, and Windows silently removes the hook when that thread does not pump
//! messages within `LowLevelHooksTimeout` (default 300 ms). The filter callback therefore executes on
//! the hook thread and must return well under 1 ms — no I/O, no logging above `trace`, no blocking
//! locks. A panicking filter is caught and treated as [`HookAction::Pass`].
//!
//! One `WH_KEYBOARD_LL` and one `WH_MOUSE_LL` hook per process: installing a second returns
//! [`WinUtilError::Busy`]; swap the filter with [`LowLevelKeyboardHook::set_filter`] instead.
//!
//! `Ctrl+Alt+Del` is the Secure Attention Sequence: winlogon consumes it before any hook sees it, so a
//! [`BlockedCombo`] for it parses (policy files list it) but never matches. It is disabled through
//! policy (`ExplorerPolicy.disableTaskManager` + `DisableCAD`), not here.

use std::fmt;
use std::str::FromStr;
use std::sync::Arc;

use crate::{Rect, Result, WinUtilError};

/// Virtual-key codes used by the combo parser (subset of `winuser.h`).
pub mod vk {
    pub const BACK: u32 = 0x08;
    pub const TAB: u32 = 0x09;
    pub const RETURN: u32 = 0x0D;
    pub const SHIFT: u32 = 0x10;
    pub const CONTROL: u32 = 0x11;
    pub const MENU: u32 = 0x12;
    pub const PAUSE: u32 = 0x13;
    pub const CAPITAL: u32 = 0x14;
    pub const ESCAPE: u32 = 0x1B;
    pub const SPACE: u32 = 0x20;
    pub const PRIOR: u32 = 0x21;
    pub const NEXT: u32 = 0x22;
    pub const END: u32 = 0x23;
    pub const HOME: u32 = 0x24;
    pub const LEFT: u32 = 0x25;
    pub const UP: u32 = 0x26;
    pub const RIGHT: u32 = 0x27;
    pub const DOWN: u32 = 0x28;
    pub const SNAPSHOT: u32 = 0x2C;
    pub const INSERT: u32 = 0x2D;
    pub const DELETE: u32 = 0x2E;
    pub const LWIN: u32 = 0x5B;
    pub const RWIN: u32 = 0x5C;
    pub const APPS: u32 = 0x5D;
    pub const F1: u32 = 0x70;
    pub const F24: u32 = 0x87;
    pub const NUMLOCK: u32 = 0x90;
    pub const SCROLL: u32 = 0x91;
    pub const LSHIFT: u32 = 0xA0;
    pub const RSHIFT: u32 = 0xA1;
    pub const LCONTROL: u32 = 0xA2;
    pub const RCONTROL: u32 = 0xA3;
    pub const LMENU: u32 = 0xA4;
    pub const RMENU: u32 = 0xA5;
}

/// `KBDLLHOOKSTRUCT.flags` bits.
pub mod llkhf {
    pub const EXTENDED: u32 = 0x01;
    pub const INJECTED: u32 = 0x10;
    pub const ALTDOWN: u32 = 0x20;
    pub const UP: u32 = 0x80;
}

/// Filter verdict: let the event through or swallow it.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum HookAction {
    Pass,
    Block,
}

/// One keyboard event as seen by the low-level hook. Modifier flags reflect the state tracked by the
/// hook itself (so a swallowed `Win` press still counts as held for the following `Win+D`).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct KeyEvent {
    /// Virtual-key code.
    pub vk: u32,
    /// Hardware scan code.
    pub scan: u32,
    /// Raw `KBDLLHOOKSTRUCT.flags` (see [`llkhf`]).
    pub flags: u32,
    pub alt: bool,
    pub ctrl: bool,
    pub shift: bool,
    pub win: bool,
    /// `true` for `WM_KEYUP` / `WM_SYSKEYUP`.
    pub key_up: bool,
}

impl KeyEvent {
    /// `true` when the event was produced by `SendInput` (our own [`crate::input`] calls included).
    pub fn injected(&self) -> bool {
        self.flags & llkhf::INJECTED != 0
    }
}

/// Keyboard filter run on the hook thread (< 1 ms budget).
pub type KeyFilter = Arc<dyn Fn(&KeyEvent) -> HookAction + Send + Sync>;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum ModifierKind {
    Ctrl,
    Alt,
    Shift,
    Win,
}

fn modifier_of(vk: u32) -> Option<ModifierKind> {
    match vk {
        vk::CONTROL | vk::LCONTROL | vk::RCONTROL => Some(ModifierKind::Ctrl),
        vk::MENU | vk::LMENU | vk::RMENU => Some(ModifierKind::Alt),
        vk::SHIFT | vk::LSHIFT | vk::RSHIFT => Some(ModifierKind::Shift),
        vk::LWIN | vk::RWIN => Some(ModifierKind::Win),
        _ => None,
    }
}

fn modifier_from_name(token: &str) -> Option<ModifierKind> {
    match token.to_ascii_lowercase().as_str() {
        "ctrl" | "control" | "ctl" => Some(ModifierKind::Ctrl),
        "alt" => Some(ModifierKind::Alt),
        "shift" => Some(ModifierKind::Shift),
        "win" | "windows" | "meta" | "super" | "cmd" | "lwin" | "rwin" => Some(ModifierKind::Win),
        _ => None,
    }
}

const NAMED_KEYS: &[(&str, u32)] = &[
    ("Del", vk::DELETE),
    ("Tab", vk::TAB),
    ("Esc", vk::ESCAPE),
    ("Enter", vk::RETURN),
    ("Space", vk::SPACE),
    ("Backspace", vk::BACK),
    ("Insert", vk::INSERT),
    ("Home", vk::HOME),
    ("End", vk::END),
    ("PageUp", vk::PRIOR),
    ("PageDown", vk::NEXT),
    ("Left", vk::LEFT),
    ("Up", vk::UP),
    ("Right", vk::RIGHT),
    ("Down", vk::DOWN),
    ("PrintScreen", vk::SNAPSHOT),
    ("Pause", vk::PAUSE),
    ("CapsLock", vk::CAPITAL),
    ("NumLock", vk::NUMLOCK),
    ("ScrollLock", vk::SCROLL),
    ("Apps", vk::APPS),
];

fn key_from_name(token: &str) -> Option<u32> {
    let n = token.to_ascii_lowercase();
    let alias = match n.as_str() {
        "delete" => Some(vk::DELETE),
        "escape" => Some(vk::ESCAPE),
        "return" => Some(vk::RETURN),
        "back" => Some(vk::BACK),
        "ins" => Some(vk::INSERT),
        "pgup" | "prior" => Some(vk::PRIOR),
        "pgdn" | "next" => Some(vk::NEXT),
        "prtsc" | "prtscn" | "snapshot" | "print" => Some(vk::SNAPSHOT),
        "break" => Some(vk::PAUSE),
        "contextmenu" => Some(vk::APPS),
        _ => None,
    };
    if alias.is_some() {
        return alias;
    }
    if let Some((_, code)) = NAMED_KEYS
        .iter()
        .find(|(name, _)| name.eq_ignore_ascii_case(&n))
    {
        return Some(*code);
    }
    if let Some(num) = n.strip_prefix('f') {
        if let Ok(i) = num.parse::<u32>() {
            if (1..=24).contains(&i) {
                return Some(vk::F1 + i - 1);
            }
        }
    }
    if let Some(hex) = n.strip_prefix("vk0x").or_else(|| n.strip_prefix("0x")) {
        return u32::from_str_radix(hex, 16)
            .ok()
            .filter(|v| (1..=0xFE).contains(v));
    }
    let mut chars = n.chars();
    match (chars.next(), chars.next()) {
        (Some(c), None) if c.is_ascii_alphanumeric() => Some(c.to_ascii_uppercase() as u32),
        _ => None,
    }
}

/// Canonical name of a virtual key for [`BlockedCombo`]'s `Display`.
pub fn key_name(vk: u32) -> String {
    if let Some((name, _)) = NAMED_KEYS.iter().find(|(_, code)| *code == vk) {
        return (*name).to_owned();
    }
    if (vk::F1..=vk::F24).contains(&vk) {
        return format!("F{}", vk - vk::F1 + 1);
    }
    if (0x30..=0x39).contains(&vk) || (0x41..=0x5A).contains(&vk) {
        return char::from_u32(vk)
            .map(|c| c.to_string())
            .unwrap_or_default();
    }
    format!("Vk0x{vk:02X}")
}

/// A key chord such as `Alt+Tab`, `Win`, `Ctrl+Shift+Esc` or `F12`, parsed from the
/// `policy.explorer.blockedKeyCombos` / `shell.json → kiosk.exitHotkey` string form.
///
/// Matching is a superset match: every listed modifier must be held, extra modifiers are ignored
/// (`Alt+Tab` also matches `Alt+Shift+Tab`). A modifier-only combo (`Win`) matches presses of that
/// modifier key itself, both left and right variants.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub struct BlockedCombo {
    pub ctrl: bool,
    pub alt: bool,
    pub shift: bool,
    pub win: bool,
    /// Non-modifier key; `None` for a modifier-only combo.
    pub key: Option<u32>,
}

/// Built-in kiosk set applied when no policy is cached (ARCHITECTURE.md §5). `Win` alone already
/// swallows every `Win+…` chord; the explicit entries are belt and braces for hooks installed after
/// the key went down.
pub const KIOSK_DEFAULTS: &[&str] = &[
    "Alt+Tab",
    "Alt+Esc",
    "Ctrl+Esc",
    "Alt+F4",
    "Ctrl+Shift+Esc",
    "Win",
    "Win+D",
    "Win+R",
    "Win+E",
    "Win+L",
    "Win+I",
    "Win+Tab",
    "PrintScreen",
];

impl BlockedCombo {
    /// Parses `"Ctrl+Alt+Del"`-style text (case-insensitive tokens joined by `+`).
    pub fn parse(text: &str) -> Result<Self> {
        text.parse()
    }

    /// Parses a list, failing on the first malformed entry.
    pub fn parse_all<S: AsRef<str>>(items: &[S]) -> Result<Vec<Self>> {
        items.iter().map(|s| Self::parse(s.as_ref())).collect()
    }

    /// [`KIOSK_DEFAULTS`] parsed.
    pub fn kiosk_defaults() -> Vec<Self> {
        KIOSK_DEFAULTS
            .iter()
            .map(|s| Self::parse(s).expect("built-in combos parse"))
            .collect()
    }

    fn set(&mut self, m: ModifierKind) {
        match m {
            ModifierKind::Ctrl => self.ctrl = true,
            ModifierKind::Alt => self.alt = true,
            ModifierKind::Shift => self.shift = true,
            ModifierKind::Win => self.win = true,
        }
    }

    fn has(&self, m: ModifierKind) -> bool {
        match m {
            ModifierKind::Ctrl => self.ctrl,
            ModifierKind::Alt => self.alt,
            ModifierKind::Shift => self.shift,
            ModifierKind::Win => self.win,
        }
    }

    /// `Ctrl+Alt+Del`: parses for config compatibility but can never be intercepted by a hook.
    pub fn is_secure_attention(&self) -> bool {
        self.ctrl && self.alt && self.key == Some(vk::DELETE)
    }

    /// `true` when `ev` (down or up) is this chord.
    pub fn matches(&self, ev: &KeyEvent) -> bool {
        let held = (!self.ctrl || ev.ctrl)
            && (!self.alt || ev.alt)
            && (!self.shift || ev.shift)
            && (!self.win || ev.win);
        if !held {
            return false;
        }
        match self.key {
            Some(k) => ev.vk == k,
            None => modifier_of(ev.vk).is_some_and(|m| self.has(m)),
        }
    }

    /// `true` when any combo in `combos` matches.
    pub fn any_matches(combos: &[BlockedCombo], ev: &KeyEvent) -> bool {
        combos.iter().any(|c| c.matches(ev))
    }
}

impl FromStr for BlockedCombo {
    type Err = WinUtilError;

    fn from_str(s: &str) -> Result<Self> {
        let mut combo = BlockedCombo::default();
        let mut seen = false;
        for token in s.split('+').map(str::trim) {
            if token.is_empty() {
                return Err(WinUtilError::Invalid(format!(
                    "empty token in key combo '{s}'"
                )));
            }
            seen = true;
            if let Some(m) = modifier_from_name(token) {
                combo.set(m);
                continue;
            }
            if combo.key.is_some() {
                return Err(WinUtilError::Invalid(format!(
                    "more than one key in combo '{s}'"
                )));
            }
            let code = key_from_name(token).ok_or_else(|| {
                WinUtilError::Invalid(format!("unknown key '{token}' in combo '{s}'"))
            })?;
            combo.key = Some(code);
        }
        if !seen {
            return Err(WinUtilError::Invalid("empty key combo".to_owned()));
        }
        Ok(combo)
    }
}

impl fmt::Display for BlockedCombo {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let mut parts: Vec<String> = Vec::new();
        if self.ctrl {
            parts.push("Ctrl".into());
        }
        if self.alt {
            parts.push("Alt".into());
        }
        if self.shift {
            parts.push("Shift".into());
        }
        if self.win {
            parts.push("Win".into());
        }
        if let Some(k) = self.key {
            parts.push(key_name(k));
        }
        f.write_str(&parts.join("+"))
    }
}

/// Filter that blocks every chord in `combos` and passes everything else.
pub fn block_combos(combos: Vec<BlockedCombo>) -> KeyFilter {
    Arc::new(move |ev| {
        if BlockedCombo::any_matches(&combos, ev) {
            HookAction::Block
        } else {
            HookAction::Pass
        }
    })
}

/// One mouse event as seen by the low-level hook.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct MouseEvent {
    /// Screen coordinates (physical pixels).
    pub x: i32,
    pub y: i32,
    /// `WM_MOUSEMOVE`, `WM_LBUTTONDOWN`, `WM_MOUSEWHEEL`, …
    pub message: u32,
    /// Raw `MSLLHOOKSTRUCT.mouseData` (wheel delta / X button in the high word).
    pub data: u32,
    /// Raw `MSLLHOOKSTRUCT.flags` (`LLMHF_INJECTED = 1`).
    pub flags: u32,
}

impl MouseEvent {
    pub fn injected(&self) -> bool {
        self.flags & 1 != 0
    }

    pub fn is_move(&self) -> bool {
        self.message == 0x0200
    }

    pub fn is_wheel(&self) -> bool {
        matches!(self.message, 0x020A | 0x020E)
    }

    pub fn is_button(&self) -> bool {
        matches!(self.message, 0x0201..=0x0209 | 0x020B..=0x020D)
    }
}

/// Mouse filter run on the hook thread (< 1 ms budget).
pub type MouseFilter = Arc<dyn Fn(&MouseEvent) -> HookAction + Send + Sync>;

/// Filter that swallows events within `margin` px of the edges of `bounds` (or only its four corners
/// when `corners_only`), so the cursor cannot reach hot corners / an auto-hidden taskbar strip.
pub fn edge_blocker(bounds: Rect, margin: i32, corners_only: bool) -> MouseFilter {
    Arc::new(move |ev| {
        let near_x = ev.x < bounds.left + margin || ev.x >= bounds.right - margin;
        let near_y = ev.y < bounds.top + margin || ev.y >= bounds.bottom - margin;
        let hit = if corners_only {
            near_x && near_y
        } else {
            near_x || near_y
        };
        if hit {
            HookAction::Block
        } else {
            HookAction::Pass
        }
    })
}

#[cfg(windows)]
mod imp {
    use super::*;
    use std::panic::{catch_unwind, AssertUnwindSafe};
    use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
    use std::sync::mpsc;
    use std::thread::JoinHandle;

    use parking_lot::{Mutex, RwLock};
    use windows::core::PCWSTR;
    use windows::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
    use windows::Win32::System::LibraryLoader::GetModuleHandleW;
    use windows::Win32::System::Threading::GetCurrentThreadId;
    use windows::Win32::UI::WindowsAndMessaging::{
        CallNextHookEx, DispatchMessageW, GetMessageW, PeekMessageW, PostThreadMessageW,
        SetWindowsHookExW, TranslateMessage, UnhookWindowsHookEx, HHOOK, HOOKPROC, KBDLLHOOKSTRUCT,
        MSG, MSLLHOOKSTRUCT, PM_NOREMOVE, WH_KEYBOARD_LL, WH_MOUSE_LL, WINDOWS_HOOK_ID, WM_KEYUP,
        WM_QUIT, WM_SYSKEYUP, WM_USER,
    };

    use crate::{from_error, Win32Ret};

    const MOD_CTRL: u32 = 1;
    const MOD_ALT: u32 = 2;
    const MOD_SHIFT: u32 = 4;
    const MOD_WIN: u32 = 8;

    struct KeyboardState {
        filter: RwLock<KeyFilter>,
        /// Modifier keys currently held, as observed by this hook (`MOD_*` bits).
        mods: AtomicU32,
    }

    struct MouseState {
        filter: RwLock<MouseFilter>,
    }

    // Hook procedures carry no user data, so the active hook's state lives in a process-wide slot.
    // Uncontended parking_lot locks are a single CAS: well inside the hook budget.
    static KEYBOARD: Mutex<Option<Arc<KeyboardState>>> = Mutex::new(None);
    static MOUSE: Mutex<Option<Arc<MouseState>>> = Mutex::new(None);

    impl KeyboardState {
        fn event(&self, info: &KBDLLHOOKSTRUCT, key_up: bool) -> KeyEvent {
            let vk = info.vkCode;
            let bit = match modifier_of(vk) {
                Some(ModifierKind::Ctrl) => MOD_CTRL,
                Some(ModifierKind::Alt) => MOD_ALT,
                Some(ModifierKind::Shift) => MOD_SHIFT,
                Some(ModifierKind::Win) => MOD_WIN,
                None => 0,
            };
            let mods = if bit == 0 {
                self.mods.load(Ordering::Relaxed)
            } else if key_up {
                self.mods.fetch_and(!bit, Ordering::Relaxed) & !bit
            } else {
                self.mods.fetch_or(bit, Ordering::Relaxed) | bit
            };
            let flags = info.flags.0;
            KeyEvent {
                vk,
                scan: info.scanCode,
                flags,
                alt: (mods & MOD_ALT != 0) || (flags & llkhf::ALTDOWN != 0),
                ctrl: mods & MOD_CTRL != 0,
                shift: mods & MOD_SHIFT != 0,
                win: mods & MOD_WIN != 0,
                key_up,
            }
        }

        fn decide(&self, ev: &KeyEvent) -> HookAction {
            let filter = self.filter.read().clone();
            catch_unwind(AssertUnwindSafe(|| filter(ev))).unwrap_or(HookAction::Pass)
        }
    }

    impl MouseState {
        fn decide(&self, ev: &MouseEvent) -> HookAction {
            let filter = self.filter.read().clone();
            catch_unwind(AssertUnwindSafe(|| filter(ev))).unwrap_or(HookAction::Pass)
        }
    }

    // SAFETY (both procs): invoked by user32 on the hook thread with `lparam` pointing at a valid
    // `KBDLLHOOKSTRUCT` / `MSLLHOOKSTRUCT` whenever `code >= 0` (documented contract of low-level hooks).
    unsafe extern "system" fn keyboard_proc(code: i32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
        if code >= 0 {
            let state = KEYBOARD.lock().clone();
            if let Some(state) = state {
                let info = &*(lparam.0 as *const KBDLLHOOKSTRUCT);
                let msg = wparam.0 as u32;
                let ev = state.event(info, msg == WM_KEYUP || msg == WM_SYSKEYUP);
                if state.decide(&ev) == HookAction::Block {
                    tracing::trace!(vk = ev.vk, key_up = ev.key_up, "blocked key");
                    return LRESULT(1);
                }
            }
        }
        CallNextHookEx(HHOOK::default(), code, wparam, lparam)
    }

    unsafe extern "system" fn mouse_proc(code: i32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
        if code >= 0 {
            let state = MOUSE.lock().clone();
            if let Some(state) = state {
                let info = &*(lparam.0 as *const MSLLHOOKSTRUCT);
                let ev = MouseEvent {
                    x: info.pt.x,
                    y: info.pt.y,
                    message: wparam.0 as u32,
                    data: info.mouseData,
                    flags: info.flags,
                };
                if state.decide(&ev) == HookAction::Block {
                    return LRESULT(1);
                }
            }
        }
        CallNextHookEx(HHOOK::default(), code, wparam, lparam)
    }

    /// Dedicated thread owning one low-level hook and pumping its message loop.
    struct HookThread {
        thread_id: u32,
        join: Option<JoinHandle<()>>,
        installed: Arc<AtomicBool>,
    }

    impl HookThread {
        fn spawn(kind: WINDOWS_HOOK_ID, proc_: HOOKPROC, name: &'static str) -> Result<Self> {
            let (ready_tx, ready_rx) = mpsc::channel::<Result<u32>>();
            let installed = Arc::new(AtomicBool::new(false));
            let flag = Arc::clone(&installed);
            let join = std::thread::Builder::new()
                .name(format!("winutil-{name}"))
                .spawn(move || hook_thread_main(kind, proc_, &ready_tx, &flag))?;
            let thread_id = ready_rx.recv().map_err(|_| WinUtilError::Closed)??;
            Ok(Self {
                thread_id,
                join: Some(join),
                installed,
            })
        }
    }

    fn hook_thread_main(
        kind: WINDOWS_HOOK_ID,
        proc_: HOOKPROC,
        ready: &mpsc::Sender<Result<u32>>,
        installed: &AtomicBool,
    ) {
        // SAFETY: plain Win32 calls on this thread; `msg` outlives every call that writes to it and the
        // hook is removed before the thread exits.
        unsafe {
            let mut msg = MSG::default();
            // Force creation of this thread's message queue before publishing the thread id, so that
            // `PostThreadMessageW(WM_QUIT)` from `Drop` can never fail with ERROR_INVALID_THREAD_ID.
            let _ = PeekMessageW(&mut msg, HWND::default(), WM_USER, WM_USER, PM_NOREMOVE);
            let module = match GetModuleHandleW(PCWSTR::null()) {
                Ok(m) => m,
                Err(e) => {
                    let _ = ready.send(Err(from_error("GetModuleHandleW", e)));
                    return;
                }
            };
            let hook = match SetWindowsHookExW(kind, proc_, module, 0).ret("SetWindowsHookExW") {
                Ok(h) => h,
                Err(e) => {
                    let _ = ready.send(Err(e));
                    return;
                }
            };
            installed.store(true, Ordering::Release);
            let _ = ready.send(Ok(GetCurrentThreadId()));
            while GetMessageW(&mut msg, HWND::default(), 0, 0).0 > 0 {
                let _ = TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            let _ = UnhookWindowsHookEx(hook);
            installed.store(false, Ordering::Release);
        }
    }

    impl Drop for HookThread {
        fn drop(&mut self) {
            // SAFETY: posting a thread message has no memory-safety preconditions.
            let _ = unsafe { PostThreadMessageW(self.thread_id, WM_QUIT, WPARAM(0), LPARAM(0)) };
            if let Some(join) = self.join.take() {
                let _ = join.join();
            }
        }
    }

    /// Process-wide `WH_KEYBOARD_LL` hook. Uninstalled on drop (posts `WM_QUIT`, joins the thread).
    pub struct LowLevelKeyboardHook {
        state: Arc<KeyboardState>,
        thread: HookThread,
    }

    impl LowLevelKeyboardHook {
        /// Installs the hook with `filter`; [`WinUtilError::Busy`] when one is already active.
        pub fn install(filter: KeyFilter) -> Result<Self> {
            let state = Arc::new(KeyboardState {
                filter: RwLock::new(filter),
                mods: AtomicU32::new(0),
            });
            {
                let mut slot = KEYBOARD.lock();
                if slot.is_some() {
                    return Err(WinUtilError::Busy("WH_KEYBOARD_LL hook"));
                }
                *slot = Some(Arc::clone(&state));
            }
            match HookThread::spawn(WH_KEYBOARD_LL, Some(keyboard_proc), "kbhook") {
                Ok(thread) => {
                    tracing::info!("WH_KEYBOARD_LL installed");
                    Ok(Self { state, thread })
                }
                Err(e) => {
                    *KEYBOARD.lock() = None;
                    Err(e)
                }
            }
        }

        /// Installs the hook blocking exactly `combos`.
        pub fn with_blocked(combos: Vec<BlockedCombo>) -> Result<Self> {
            Self::install(block_combos(combos))
        }

        /// Replaces the filter without reinstalling (policy reload).
        pub fn set_filter(&self, filter: KeyFilter) {
            *self.state.filter.write() = filter;
        }

        /// `true` while the hook thread holds a live hook.
        pub fn is_installed(&self) -> bool {
            self.thread.installed.load(Ordering::Acquire)
        }
    }

    impl Drop for LowLevelKeyboardHook {
        fn drop(&mut self) {
            *KEYBOARD.lock() = None;
            tracing::info!("WH_KEYBOARD_LL removed");
        }
    }

    /// Process-wide `WH_MOUSE_LL` hook. Uninstalled on drop.
    pub struct LowLevelMouseHook {
        state: Arc<MouseState>,
        thread: HookThread,
    }

    impl LowLevelMouseHook {
        /// Installs the hook with `filter`; [`WinUtilError::Busy`] when one is already active.
        pub fn install(filter: MouseFilter) -> Result<Self> {
            let state = Arc::new(MouseState {
                filter: RwLock::new(filter),
            });
            {
                let mut slot = MOUSE.lock();
                if slot.is_some() {
                    return Err(WinUtilError::Busy("WH_MOUSE_LL hook"));
                }
                *slot = Some(Arc::clone(&state));
            }
            match HookThread::spawn(WH_MOUSE_LL, Some(mouse_proc), "mousehook") {
                Ok(thread) => {
                    tracing::info!("WH_MOUSE_LL installed");
                    Ok(Self { state, thread })
                }
                Err(e) => {
                    *MOUSE.lock() = None;
                    Err(e)
                }
            }
        }

        /// Installs an [`edge_blocker`] filter.
        pub fn block_edges(bounds: Rect, margin: i32, corners_only: bool) -> Result<Self> {
            Self::install(edge_blocker(bounds, margin, corners_only))
        }

        pub fn set_filter(&self, filter: MouseFilter) {
            *self.state.filter.write() = filter;
        }

        pub fn is_installed(&self) -> bool {
            self.thread.installed.load(Ordering::Acquire)
        }
    }

    impl Drop for LowLevelMouseHook {
        fn drop(&mut self) {
            *MOUSE.lock() = None;
            tracing::info!("WH_MOUSE_LL removed");
        }
    }
}

#[cfg(not(windows))]
mod imp {
    use super::*;

    /// Non-Windows stub: [`install`](Self::install) always fails with [`WinUtilError::Unsupported`].
    pub struct LowLevelKeyboardHook {
        _private: (),
    }

    impl LowLevelKeyboardHook {
        pub fn install(_filter: KeyFilter) -> Result<Self> {
            Err(WinUtilError::Unsupported)
        }

        pub fn with_blocked(_combos: Vec<BlockedCombo>) -> Result<Self> {
            Err(WinUtilError::Unsupported)
        }

        pub fn set_filter(&self, _filter: KeyFilter) {}

        pub fn is_installed(&self) -> bool {
            false
        }
    }

    /// Non-Windows stub: [`install`](Self::install) always fails with [`WinUtilError::Unsupported`].
    pub struct LowLevelMouseHook {
        _private: (),
    }

    impl LowLevelMouseHook {
        pub fn install(_filter: MouseFilter) -> Result<Self> {
            Err(WinUtilError::Unsupported)
        }

        pub fn block_edges(_bounds: Rect, _margin: i32, _corners_only: bool) -> Result<Self> {
            Err(WinUtilError::Unsupported)
        }

        pub fn set_filter(&self, _filter: MouseFilter) {}

        pub fn is_installed(&self) -> bool {
            false
        }
    }
}

pub use imp::{LowLevelKeyboardHook, LowLevelMouseHook};

#[cfg(test)]
mod tests {
    use super::*;

    fn ev(vk: u32, ctrl: bool, alt: bool, shift: bool, win: bool) -> KeyEvent {
        KeyEvent {
            vk,
            scan: 0,
            flags: 0,
            alt,
            ctrl,
            shift,
            win,
            key_up: false,
        }
    }

    #[test]
    fn parses_documented_forms() {
        let cases: &[(&str, BlockedCombo)] = &[
            (
                "Ctrl+Alt+Del",
                BlockedCombo {
                    ctrl: true,
                    alt: true,
                    key: Some(vk::DELETE),
                    ..Default::default()
                },
            ),
            (
                "Alt+Tab",
                BlockedCombo {
                    alt: true,
                    key: Some(vk::TAB),
                    ..Default::default()
                },
            ),
            (
                "Win",
                BlockedCombo {
                    win: true,
                    ..Default::default()
                },
            ),
            (
                "Alt+F4",
                BlockedCombo {
                    alt: true,
                    key: Some(vk::F1 + 3),
                    ..Default::default()
                },
            ),
            (
                "Ctrl+Shift+Esc",
                BlockedCombo {
                    ctrl: true,
                    shift: true,
                    key: Some(vk::ESCAPE),
                    ..Default::default()
                },
            ),
            (
                "Ctrl+Esc",
                BlockedCombo {
                    ctrl: true,
                    key: Some(vk::ESCAPE),
                    ..Default::default()
                },
            ),
            (
                "Win+D",
                BlockedCombo {
                    win: true,
                    key: Some(u32::from(b'D')),
                    ..Default::default()
                },
            ),
            (
                "win + r",
                BlockedCombo {
                    win: true,
                    key: Some(u32::from(b'R')),
                    ..Default::default()
                },
            ),
            (
                "Win+L",
                BlockedCombo {
                    win: true,
                    key: Some(u32::from(b'L')),
                    ..Default::default()
                },
            ),
            (
                "Alt+Esc",
                BlockedCombo {
                    alt: true,
                    key: Some(vk::ESCAPE),
                    ..Default::default()
                },
            ),
            (
                "F1",
                BlockedCombo {
                    key: Some(vk::F1),
                    ..Default::default()
                },
            ),
            (
                "f24",
                BlockedCombo {
                    key: Some(vk::F24),
                    ..Default::default()
                },
            ),
            (
                "PrintScreen",
                BlockedCombo {
                    key: Some(vk::SNAPSHOT),
                    ..Default::default()
                },
            ),
            (
                "Ctrl+Alt+Shift+F12",
                BlockedCombo {
                    ctrl: true,
                    alt: true,
                    shift: true,
                    key: Some(vk::F1 + 11),
                    ..Default::default()
                },
            ),
            (
                "Ctrl+Alt+Delete",
                BlockedCombo {
                    ctrl: true,
                    alt: true,
                    key: Some(vk::DELETE),
                    ..Default::default()
                },
            ),
            (
                "Vk0x5D",
                BlockedCombo {
                    key: Some(vk::APPS),
                    ..Default::default()
                },
            ),
        ];
        for (text, expected) in cases {
            let parsed = BlockedCombo::parse(text).unwrap_or_else(|e| panic!("{text}: {e}"));
            assert_eq!(&parsed, expected, "{text}");
            // Display round-trips through the parser.
            assert_eq!(
                BlockedCombo::parse(&parsed.to_string()).unwrap(),
                parsed,
                "{text} → {parsed}"
            );
        }
        assert_eq!(
            BlockedCombo::parse("Ctrl+Alt+Del").unwrap().to_string(),
            "Ctrl+Alt+Del"
        );
        assert_eq!(BlockedCombo::parse("Win+D").unwrap().to_string(), "Win+D");
        assert_eq!(BlockedCombo::parse("f4").unwrap().to_string(), "F4");
        assert!(BlockedCombo::parse("Ctrl+Alt+Del")
            .unwrap()
            .is_secure_attention());
        assert!(!BlockedCombo::parse("Alt+Tab")
            .unwrap()
            .is_secure_attention());
    }

    #[test]
    fn rejects_malformed() {
        for bad in [
            "",
            "+",
            "Ctrl+",
            "Ctrl+Foo",
            "Alt+Tab+Esc",
            "F25",
            "Vk0x00",
            "Hyper+X",
        ] {
            assert!(
                matches!(BlockedCombo::parse(bad), Err(WinUtilError::Invalid(_))),
                "{bad}"
            );
        }
        assert!(BlockedCombo::parse_all(&["Alt+Tab", "Nope"]).is_err());
        assert_eq!(
            BlockedCombo::parse_all(&["Alt+Tab", "Win"]).unwrap().len(),
            2
        );
        assert_eq!(BlockedCombo::kiosk_defaults().len(), KIOSK_DEFAULTS.len());
    }

    #[test]
    fn matching_is_superset_on_modifiers() {
        let alt_tab = BlockedCombo::parse("Alt+Tab").unwrap();
        assert!(alt_tab.matches(&ev(vk::TAB, false, true, false, false)));
        assert!(
            alt_tab.matches(&ev(vk::TAB, false, true, true, false)),
            "Alt+Shift+Tab is also a switcher"
        );
        assert!(!alt_tab.matches(&ev(vk::TAB, false, false, false, false)));
        assert!(!alt_tab.matches(&ev(vk::ESCAPE, false, true, false, false)));

        let win = BlockedCombo::parse("Win").unwrap();
        assert!(win.matches(&ev(vk::LWIN, false, false, false, true)));
        assert!(win.matches(&ev(vk::RWIN, false, false, false, true)));
        assert!(!win.matches(&ev(vk::LCONTROL, true, false, false, false)));
        assert!(
            !win.matches(&ev(u32::from(b'D'), false, false, false, true)),
            "Win+D is a separate combo"
        );

        let f1 = BlockedCombo::parse("F1").unwrap();
        assert!(f1.matches(&ev(vk::F1, true, false, false, false)));

        let combos = BlockedCombo::kiosk_defaults();
        assert!(
            BlockedCombo::any_matches(&combos, &ev(vk::ESCAPE, true, false, true, false)),
            "Ctrl+Shift+Esc"
        );
        assert!(!BlockedCombo::any_matches(
            &combos,
            &ev(u32::from(b'A'), false, false, false, false)
        ));

        let filter = block_combos(combos);
        assert_eq!(
            filter(&ev(vk::SNAPSHOT, false, false, false, false)),
            HookAction::Block
        );
        assert_eq!(
            filter(&ev(vk::SPACE, false, false, false, false)),
            HookAction::Pass
        );
    }

    #[test]
    fn edge_blocker_geometry() {
        let bounds = Rect::from_size(0, 0, 1920, 1080);
        let mouse = |x, y| MouseEvent {
            x,
            y,
            message: 0x0200,
            data: 0,
            flags: 0,
        };
        let corners = edge_blocker(bounds, 4, true);
        assert_eq!(corners(&mouse(0, 0)), HookAction::Block);
        assert_eq!(corners(&mouse(1919, 1079)), HookAction::Block);
        assert_eq!(corners(&mouse(960, 0)), HookAction::Pass);
        let edges = edge_blocker(bounds, 4, false);
        assert_eq!(edges(&mouse(960, 0)), HookAction::Block);
        assert_eq!(edges(&mouse(960, 540)), HookAction::Pass);
        assert!(mouse(0, 0).is_move());
        assert!(MouseEvent {
            message: 0x0201,
            ..mouse(0, 0)
        }
        .is_button());
        assert!(MouseEvent {
            message: 0x020A,
            ..mouse(0, 0)
        }
        .is_wheel());
    }

    #[test]
    fn key_names() {
        assert_eq!(key_name(vk::DELETE), "Del");
        assert_eq!(key_name(vk::F1 + 9), "F10");
        assert_eq!(key_name(u32::from(b'Q')), "Q");
        assert_eq!(key_name(0xBA), "Vk0xBA");
        assert!(KeyEvent {
            flags: llkhf::INJECTED,
            ..ev(1, false, false, false, false)
        }
        .injected());
    }
}
