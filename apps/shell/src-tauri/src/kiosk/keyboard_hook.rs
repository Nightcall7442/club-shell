//! The process-wide `WH_KEYBOARD_LL` hook (`ARCHITECTURE.md` §5.2, `crates/winutil::hooks`): blocks the
//! effective chord set (`shell.json → kiosk.*` ∪ `policy.explorer`), recognises hotkeys
//! (`kiosk://hotkey`, `TAURI_COMMANDS.md` §3.2), supports lock-all / game / paused modes and feeds the
//! idle detector. The filter runs on the hook thread with a < 1 ms budget: atomics, one uncontended
//! `parking_lot` read lock and a channel send — no logging, no I/O.
//!
//! Windows allows one low-level keyboard hook per process, so the Alt+Tab chords, policy chords and
//! hotkeys all live in this single filter; [`super::alt_tab`] only handles the window side.

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Arc;

use clubshell_protocol::pc::ExplorerPolicy;
use clubshell_winutil::hooks::{
    vk, BlockedCombo, HookAction, KeyEvent, KeyFilter, LowLevelKeyboardHook, LowLevelMouseHook,
    MouseEvent, MouseFilter,
};
use clubshell_winutil::window::{alt_tab_combos, is_foreground};
use parking_lot::{Mutex, RwLock};
use serde::Serialize;
use tokio::sync::mpsc::UnboundedSender;

use super::idle_detector::ActivityFeed;
use crate::config::KioskConfig;

/// Event name (`TAURI_COMMANDS.md` §3.2).
pub const HOTKEY_EVENT: &str = "kiosk://hotkey";

/// Secondary admin-unlock chord (same `exit` semantics as `kiosk.exitHotkey`: opens the PIN dialog).
pub const ADMIN_UNLOCK_CHORD: &str = "Ctrl+Alt+Shift+A";
/// `callAdmin` hotkey (Shell UI only, never while a game runs).
pub const CALL_ADMIN_CHORD: &str = "F1";
/// `lock` hotkey (Shell UI only).
pub const LOCK_CHORD: &str = "Ctrl+L";
/// Dev-mode fullscreen toggle, handled natively (never emitted).
pub const DEV_FULLSCREEN_CHORD: &str = "F11";

const VK_VOLUME_MUTE: &str = "Vk0xAD";
const VK_VOLUME_DOWN: &str = "Vk0xAE";
const VK_VOLUME_UP: &str = "Vk0xAF";

/// A held hotkey auto-repeats; only one event per this many ms is forwarded.
const HOTKEY_DEBOUNCE_MS: u64 = 250;
/// `kiosk://hotkey{blocked}` is rate-limited to one per second (spec).
const BLOCKED_EVENT_MIN_MS: u64 = 1000;

/// What a recognised chord means.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum HotkeyKind {
    Exit,
    CallAdmin,
    Lock,
    VolumeUp,
    VolumeDown,
    Mute,
    /// A policy-blocked chord was suppressed.
    Blocked,
    /// Dev only: toggles the main window's fullscreen state in Rust.
    DevFullscreen,
}

impl HotkeyKind {
    /// `name` on the wire.
    pub const fn wire_name(self) -> &'static str {
        match self {
            Self::Exit => "exit",
            Self::CallAdmin => "callAdmin",
            Self::Lock => "lock",
            Self::VolumeUp => "volumeUp",
            Self::VolumeDown => "volumeDown",
            Self::Mute => "mute",
            Self::Blocked => "blocked",
            Self::DevFullscreen => "devFullscreen",
        }
    }

    /// Chords a running game must still be able to trigger (admin exit, volume keys).
    pub const fn allowed_in_game(self) -> bool {
        matches!(
            self,
            Self::Exit
                | Self::VolumeUp
                | Self::VolumeDown
                | Self::Mute
                | Self::Blocked
                | Self::DevFullscreen
        )
    }

    /// Handled in Rust; never forwarded to the webview.
    pub const fn is_internal(self) -> bool {
        matches!(self, Self::DevFullscreen)
    }
}

/// One registered chord.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Hotkey {
    pub kind: HotkeyKind,
    pub combo: BlockedCombo,
    /// Swallow the key events (`false` for the media keys, which Windows must still see).
    pub consume: bool,
}

/// Internal hook → consumer message.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct HotkeyEvent {
    pub kind: HotkeyKind,
    /// Chord as text (`BlockedCombo`'s `Display`).
    pub combo: String,
}

/// `kiosk://hotkey` payload.
#[derive(Serialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct HotkeyPayload {
    pub name: &'static str,
    pub combo: String,
}

/// Default chord table from `shell.json → kiosk`.
pub fn default_hotkeys(kiosk: &KioskConfig, dev: bool) -> Vec<Hotkey> {
    let mut out: Vec<Hotkey> = Vec::new();
    let mut add = |kind: HotkeyKind, text: &str, consume: bool| match BlockedCombo::parse(text) {
        Ok(combo) => out.push(Hotkey {
            kind,
            combo,
            consume,
        }),
        Err(e) => tracing::warn!(chord = text, error = %e, "hotkey chord ignored"),
    };
    let exit = kiosk.exit_hotkey.trim();
    if !exit.is_empty() {
        add(HotkeyKind::Exit, exit, true);
    }
    add(HotkeyKind::Exit, ADMIN_UNLOCK_CHORD, true);
    add(HotkeyKind::CallAdmin, CALL_ADMIN_CHORD, true);
    add(HotkeyKind::Lock, LOCK_CHORD, true);
    add(HotkeyKind::VolumeUp, VK_VOLUME_UP, false);
    add(HotkeyKind::VolumeDown, VK_VOLUME_DOWN, false);
    add(HotkeyKind::Mute, VK_VOLUME_MUTE, false);
    if dev {
        add(HotkeyKind::DevFullscreen, DEV_FULLSCREEN_CHORD, true);
    }
    out
}

/// Effective blocked-chord policy: `shell.json` flags ∪ `policy.explorer` (a policy can only add).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct HookPolicy {
    pub block_alt_tab: bool,
    pub block_win_key: bool,
    /// Additional chords (`policy.explorer.blockedKeyCombos`, cached policy at start).
    pub extra: Vec<BlockedCombo>,
}

impl HookPolicy {
    pub fn from_config(kiosk: &KioskConfig, extra: &[String]) -> Self {
        Self {
            block_alt_tab: kiosk.block_alt_tab,
            block_win_key: kiosk.block_win_key,
            extra: parse_combos(extra),
        }
    }

    /// ORs the policy flags in and unions its chords.
    pub fn merge_explorer(&mut self, policy: &ExplorerPolicy) {
        self.block_alt_tab |= policy.disable_alt_tab;
        self.block_win_key |= policy.disable_win_key;
        for combo in parse_combos(&policy.blocked_key_combos) {
            if !self.extra.contains(&combo) {
                self.extra.push(combo);
            }
        }
    }

    /// Full chord set for the Shell UI: kiosk defaults filtered by the flags, plus the extras.
    pub fn combos(&self) -> Vec<BlockedCombo> {
        let switchers = alt_tab_combos();
        let mut out: Vec<BlockedCombo> = Vec::new();
        let mut push = |c: BlockedCombo| {
            if !c.is_secure_attention() && !out.contains(&c) {
                out.push(c);
            }
        };
        for c in BlockedCombo::kiosk_defaults() {
            let is_switcher = switchers.contains(&c);
            if (is_switcher && !self.block_alt_tab)
                || (c.win && !is_switcher && !self.block_win_key)
            {
                continue;
            }
            push(c);
        }
        if self.block_alt_tab {
            for c in switchers.iter().copied() {
                push(c);
            }
        }
        for c in self.extra.iter().copied() {
            push(c);
        }
        out
    }

    /// Subset kept while a game runs: `Win` chords (Start menu / desktop) and `Ctrl+Shift+Esc`
    /// (Task Manager); everything else (`Alt+F4`, `PrintScreen`, `Alt+Tab`, …) is released so games
    /// and their overlays keep working.
    pub fn game_combos(&self) -> Vec<BlockedCombo> {
        self.combos()
            .into_iter()
            .filter(|c| c.win || (c.ctrl && c.shift && c.key == Some(vk::ESCAPE)))
            .collect()
    }
}

/// Parses chord strings, skipping malformed ones (logged) and `Ctrl+Alt+Del` (cannot be hooked).
pub fn parse_combos(items: &[String]) -> Vec<BlockedCombo> {
    items
        .iter()
        .filter_map(|s| match BlockedCombo::parse(s) {
            Ok(c) if c.is_secure_attention() => None,
            Ok(c) => Some(c),
            Err(e) => {
                tracing::warn!(chord = %s, error = %e, "blocked key combo ignored");
                None
            }
        })
        .collect()
}

/// State read by the filter on the hook thread.
struct Shared {
    combos: RwLock<Vec<BlockedCombo>>,
    game_combos: RwLock<Vec<BlockedCombo>>,
    hotkeys: RwLock<Vec<Hotkey>>,
    policy: Mutex<HookPolicy>,
    paused: AtomicBool,
    lock_all: AtomicBool,
    game_mode: AtomicBool,
    dev: bool,
    /// Shell window; in dev mode hotkeys fire only while it is the foreground window.
    hwnd: isize,
    feed: Arc<ActivityFeed>,
    tx: UnboundedSender<HotkeyEvent>,
    last_hotkey_ms: AtomicU64,
    last_blocked_ms: AtomicU64,
}

impl Shared {
    /// `true` when at least `min_ms` passed since the slot was last stamped (then stamps it).
    fn debounce(&self, slot: &AtomicU64, min_ms: u64) -> bool {
        let now = self.feed.now_ms();
        if now.saturating_sub(slot.load(Ordering::Relaxed)) < min_ms {
            return false;
        }
        slot.store(now, Ordering::Relaxed);
        true
    }

    fn decide(&self, ev: &KeyEvent) -> HookAction {
        if ev.injected() {
            return HookAction::Pass;
        }
        if !ev.key_up {
            self.feed.touch();
        }
        let game = self.game_mode.load(Ordering::Relaxed);
        if !self.dev || is_foreground(self.hwnd) {
            let hit = self
                .hotkeys
                .read()
                .iter()
                .copied()
                .find(|h| h.combo.matches(ev) && (!game || h.kind.allowed_in_game()));
            if let Some(hotkey) = hit {
                if !ev.key_up && self.debounce(&self.last_hotkey_ms, HOTKEY_DEBOUNCE_MS) {
                    let _ = self.tx.send(HotkeyEvent {
                        kind: hotkey.kind,
                        combo: hotkey.combo.to_string(),
                    });
                }
                return if hotkey.consume {
                    HookAction::Block
                } else {
                    HookAction::Pass
                };
            }
        }
        if self.dev {
            return HookAction::Pass;
        }
        if self.lock_all.load(Ordering::Relaxed) {
            return HookAction::Block;
        }
        if self.paused.load(Ordering::Relaxed) {
            return HookAction::Pass;
        }
        let matched = {
            let combos = if game {
                self.game_combos.read()
            } else {
                self.combos.read()
            };
            combos.iter().copied().find(|c| c.matches(ev))
        };
        match matched {
            Some(combo) => {
                if !ev.key_up && self.debounce(&self.last_blocked_ms, BLOCKED_EVENT_MIN_MS) {
                    let _ = self.tx.send(HotkeyEvent {
                        kind: HotkeyKind::Blocked,
                        combo: combo.to_string(),
                    });
                }
                HookAction::Block
            }
            None => HookAction::Pass,
        }
    }
}

fn key_filter(shared: Arc<Shared>) -> KeyFilter {
    Arc::new(move |ev: &KeyEvent| shared.decide(ev))
}

/// Mouse filter used in lock-all mode: swallows buttons and wheel, lets moves through (the cursor is
/// clipped to the overlay monitor by the kiosk).
fn mouse_filter(shared: Arc<Shared>) -> MouseFilter {
    Arc::new(move |ev: &MouseEvent| {
        if shared.lock_all.load(Ordering::Relaxed) && !ev.injected() && !ev.is_move() {
            HookAction::Block
        } else {
            HookAction::Pass
        }
    })
}

/// Owner of the keyboard hook (and the lazily installed mouse hook for lock mode).
pub struct KeyboardHook {
    shared: Arc<Shared>,
    hook: Mutex<Option<LowLevelKeyboardHook>>,
    mouse: Mutex<Option<LowLevelMouseHook>>,
}

impl KeyboardHook {
    /// Installs the hook. Failure to install (non-Windows, hook already owned by this process) is
    /// logged and leaves an inert instance; `dev` installs a non-blocking filter that only recognises
    /// hotkeys while `shell_hwnd` is the foreground window.
    pub fn install(
        policy: HookPolicy,
        hotkeys: Vec<Hotkey>,
        feed: Arc<ActivityFeed>,
        tx: UnboundedSender<HotkeyEvent>,
        dev: bool,
        shell_hwnd: isize,
    ) -> Self {
        let shared = Arc::new(Shared {
            combos: RwLock::new(policy.combos()),
            game_combos: RwLock::new(policy.game_combos()),
            hotkeys: RwLock::new(hotkeys),
            policy: Mutex::new(policy),
            paused: AtomicBool::new(false),
            lock_all: AtomicBool::new(false),
            game_mode: AtomicBool::new(false),
            dev,
            hwnd: shell_hwnd,
            feed,
            tx,
            last_hotkey_ms: AtomicU64::new(0),
            last_blocked_ms: AtomicU64::new(0),
        });
        let hook = match LowLevelKeyboardHook::install(key_filter(Arc::clone(&shared))) {
            Ok(hook) => {
                tracing::info!(
                    dev,
                    blocked = shared.combos.read().len(),
                    hotkeys = shared.hotkeys.read().len(),
                    "keyboard hook installed"
                );
                Some(hook)
            }
            Err(e) => {
                if dev {
                    tracing::info!(error = %e, "keyboard hook unavailable (dev)");
                } else {
                    tracing::error!(error = %e, "keyboard hook could not be installed; key blocking is OFF");
                }
                None
            }
        };
        Self {
            shared,
            hook: Mutex::new(hook),
            mouse: Mutex::new(None),
        }
    }

    /// `true` while the hook thread holds a live `WH_KEYBOARD_LL` hook.
    pub fn is_installed(&self) -> bool {
        self.hook
            .lock()
            .as_ref()
            .is_some_and(LowLevelKeyboardHook::is_installed)
    }

    /// Replaces the chord sets (policy reload) without reinstalling.
    pub fn set_policy(&self, policy: &HookPolicy) {
        *self.shared.combos.write() = policy.combos();
        *self.shared.game_combos.write() = policy.game_combos();
        *self.shared.policy.lock() = policy.clone();
        tracing::info!(
            blocked = self.shared.combos.read().len(),
            in_game = self.shared.game_combos.read().len(),
            "key policy updated"
        );
    }

    pub fn policy(&self) -> HookPolicy {
        self.shared.policy.lock().clone()
    }

    /// Currently blocked chords (full set).
    pub fn blocked_combos(&self) -> Vec<BlockedCombo> {
        self.shared.combos.read().clone()
    }

    pub fn set_hotkeys(&self, hotkeys: Vec<Hotkey>) {
        *self.shared.hotkeys.write() = hotkeys;
    }

    pub fn hotkeys(&self) -> Vec<Hotkey> {
        self.shared.hotkeys.read().clone()
    }

    /// Paused: nothing is blocked (hotkeys and the activity feed keep working).
    pub fn set_paused(&self, paused: bool) {
        self.shared.paused.store(paused, Ordering::Relaxed);
    }

    pub fn is_paused(&self) -> bool {
        self.shared.paused.load(Ordering::Relaxed)
    }

    /// Game mode: only [`HookPolicy::game_combos`] stay blocked.
    pub fn set_game_mode(&self, on: bool) {
        self.shared.game_mode.store(on, Ordering::Relaxed);
    }

    pub fn is_game_mode(&self) -> bool {
        self.shared.game_mode.load(Ordering::Relaxed)
    }

    /// Lock-all: every key (except hotkeys) and every mouse button/wheel event is swallowed. Installs
    /// the mouse hook on first use; a no-op in dev mode.
    pub fn set_lock_all(&self, on: bool) {
        if self.shared.dev {
            return;
        }
        self.shared.lock_all.store(on, Ordering::Relaxed);
        if !on {
            return;
        }
        let mut mouse = self.mouse.lock();
        if mouse.is_none() {
            match LowLevelMouseHook::install(mouse_filter(Arc::clone(&self.shared))) {
                Ok(hook) => *mouse = Some(hook),
                Err(e) => {
                    tracing::warn!(error = %e, "mouse hook unavailable; lock blocks keys only")
                }
            }
        }
    }

    pub fn is_lock_all(&self) -> bool {
        self.shared.lock_all.load(Ordering::Relaxed)
    }

    /// Removes both hooks (idempotent).
    pub fn uninstall(&self) {
        self.shared.lock_all.store(false, Ordering::Relaxed);
        if self.hook.lock().take().is_some() {
            tracing::info!("keyboard hook removed");
        }
        self.mouse.lock().take();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::ShellConfig;

    fn key(vk_code: u32, ctrl: bool, alt: bool, shift: bool, win: bool) -> KeyEvent {
        KeyEvent {
            vk: vk_code,
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
    fn policy_flags_filter_defaults_and_policy_only_adds() {
        let cfg = ShellConfig::defaults();
        let full = HookPolicy::from_config(&cfg.kiosk, &[]);
        let combos = full.combos();
        assert!(
            BlockedCombo::any_matches(&combos, &key(vk::TAB, false, true, false, false)),
            "Alt+Tab"
        );
        assert!(
            BlockedCombo::any_matches(&combos, &key(vk::LWIN, false, false, false, true)),
            "Win"
        );
        assert!(
            BlockedCombo::any_matches(&combos, &key(vk::F1 + 3, false, true, false, false)),
            "Alt+F4"
        );

        let relaxed = HookPolicy {
            block_alt_tab: false,
            block_win_key: false,
            extra: vec![],
        };
        let combos = relaxed.combos();
        assert!(!BlockedCombo::any_matches(
            &combos,
            &key(vk::TAB, false, true, false, false)
        ));
        assert!(!BlockedCombo::any_matches(
            &combos,
            &key(vk::LWIN, false, false, false, true)
        ));
        assert!(
            BlockedCombo::any_matches(&combos, &key(vk::F1 + 3, false, true, false, false)),
            "Alt+F4 stays"
        );

        let mut merged = relaxed.clone();
        merged.merge_explorer(&ExplorerPolicy {
            disable_task_manager: true,
            disable_run: true,
            disable_settings: true,
            hide_taskbar: true,
            disable_alt_tab: true,
            disable_win_key: false,
            blocked_key_combos: vec!["Ctrl+Alt+Del".into(), "Win+X".into(), "Bogus+Key".into()],
        });
        assert!(merged.block_alt_tab && !merged.block_win_key);
        assert_eq!(merged.extra.len(), 1, "CAD and malformed entries dropped");
        let combos = merged.combos();
        assert!(BlockedCombo::any_matches(
            &combos,
            &key(vk::TAB, false, true, false, false)
        ));
        assert!(BlockedCombo::any_matches(
            &combos,
            &key(u32::from(b'X'), false, false, false, true)
        ));
        let dedup: std::collections::HashSet<BlockedCombo> = combos.iter().copied().collect();
        assert_eq!(dedup.len(), combos.len(), "no duplicates");

        let game = full.game_combos();
        assert!(BlockedCombo::any_matches(
            &game,
            &key(vk::LWIN, false, false, false, true)
        ));
        assert!(
            BlockedCombo::any_matches(&game, &key(vk::ESCAPE, true, false, true, false)),
            "Ctrl+Shift+Esc"
        );
        assert!(
            !BlockedCombo::any_matches(&game, &key(vk::F1 + 3, false, true, false, false)),
            "Alt+F4 released in game"
        );
    }

    #[test]
    fn default_hotkeys_follow_config() {
        let cfg = ShellConfig::defaults();
        let hotkeys = default_hotkeys(&cfg.kiosk, true);
        assert!(hotkeys.iter().any(|h| h.kind == HotkeyKind::Exit
            && h.combo == BlockedCombo::parse("Ctrl+Alt+Shift+F12").unwrap()));
        assert!(hotkeys.iter().any(|h| h.kind == HotkeyKind::DevFullscreen));
        assert!(
            hotkeys
                .iter()
                .filter(|h| h.kind == HotkeyKind::Exit)
                .count()
                >= 2
        );
        let mute = hotkeys.iter().find(|h| h.kind == HotkeyKind::Mute).unwrap();
        assert!(!mute.consume && mute.combo.key == Some(0xAD));
        assert!(default_hotkeys(&cfg.kiosk, false)
            .iter()
            .all(|h| h.kind != HotkeyKind::DevFullscreen));
        assert_eq!(HotkeyKind::CallAdmin.wire_name(), "callAdmin");
        assert!(HotkeyKind::Exit.allowed_in_game() && !HotkeyKind::Lock.allowed_in_game());
    }

    #[test]
    fn filter_blocks_only_outside_dev_and_debounces() {
        let cfg = ShellConfig::defaults();
        let (tx, mut rx) = tokio::sync::mpsc::unbounded_channel();
        let feed = ActivityFeed::new();
        let policy = HookPolicy::from_config(&cfg.kiosk, &[]);
        let shared = Arc::new(Shared {
            combos: RwLock::new(policy.combos()),
            game_combos: RwLock::new(policy.game_combos()),
            hotkeys: RwLock::new(default_hotkeys(&cfg.kiosk, false)),
            policy: Mutex::new(policy),
            paused: AtomicBool::new(false),
            lock_all: AtomicBool::new(false),
            game_mode: AtomicBool::new(false),
            dev: false,
            hwnd: 0,
            feed: Arc::clone(&feed),
            tx,
            last_hotkey_ms: AtomicU64::new(0),
            last_blocked_ms: AtomicU64::new(0),
        });
        // Wait past the debounce windows measured from the feed epoch.
        std::thread::sleep(std::time::Duration::from_millis(1050));
        let filter = key_filter(Arc::clone(&shared));
        assert_eq!(
            filter(&key(u32::from(b'A'), false, false, false, false)),
            HookAction::Pass
        );
        assert_eq!(
            filter(&key(vk::TAB, false, true, false, false)),
            HookAction::Block
        );
        let blocked = rx.try_recv().unwrap();
        assert_eq!(
            (blocked.kind, blocked.combo.as_str()),
            (HotkeyKind::Blocked, "Alt+Tab")
        );
        assert_eq!(
            filter(&key(vk::ESCAPE, false, true, false, false)),
            HookAction::Block
        );
        assert!(
            rx.try_recv().is_err(),
            "second blocked event within 1 s is suppressed"
        );

        let exit = filter(&key(vk::F1 + 11, true, true, true, false));
        assert_eq!(exit, HookAction::Block, "exit chord is consumed");
        assert_eq!(rx.try_recv().unwrap().kind, HotkeyKind::Exit);
        assert_eq!(
            filter(&key(0xAF, false, false, false, false)),
            HookAction::Pass,
            "volume keys pass through"
        );

        shared.lock_all.store(true, Ordering::Relaxed);
        assert_eq!(
            filter(&key(u32::from(b'A'), false, false, false, false)),
            HookAction::Block
        );
        shared.lock_all.store(false, Ordering::Relaxed);
        shared.game_mode.store(true, Ordering::Relaxed);
        assert_eq!(
            filter(&key(vk::F1 + 3, false, true, false, false)),
            HookAction::Pass,
            "Alt+F4 released in game mode"
        );
        assert_eq!(
            filter(&key(vk::LWIN, false, false, false, true)),
            HookAction::Block,
            "Win stays blocked in game mode"
        );
        let injected = KeyEvent {
            flags: clubshell_winutil::hooks::llkhf::INJECTED,
            ..key(vk::LWIN, false, false, false, true)
        };
        assert_eq!(
            filter(&injected),
            HookAction::Pass,
            "our own SendInput passes"
        );
        assert!(
            feed.since() < std::time::Duration::from_secs(1),
            "activity recorded"
        );
    }
}
