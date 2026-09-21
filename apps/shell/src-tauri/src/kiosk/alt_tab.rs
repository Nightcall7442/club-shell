//! Window-switch prevention. The switch chords (`Alt+Tab`, `Alt+Esc`, `Win+Tab`, `Ctrl+Alt+Tab`) are
//! blocked by the single process-wide keyboard hook through
//! [`HookPolicy::block_alt_tab`](super::keyboard_hook::HookPolicy); this module owns the window side:
//! winutil's [`TopmostGuard`] re-asserting `HWND_TOPMOST` + foreground every 250 ms
//! (`ARCHITECTURE.md` §5.2 "Window guard") and `minimize_others` when the guard is (re-)armed.
//! Disabled while a game runs so the guard never steals the game's focus.

use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

use clubshell_winutil::hooks::BlockedCombo;
use clubshell_winutil::window::{
    alt_tab_combos, force_foreground, minimize_others, set_topmost, TopmostGuard,
};
use parking_lot::Mutex;

/// Re-assert period of the topmost/foreground guard.
pub const GUARD_INTERVAL: Duration = Duration::from_millis(250);

/// Foreground owner of the kiosk: the Shell window while enabled, whoever wants it while disabled.
pub struct AltTabBlocker {
    hwnd: isize,
    dev: bool,
    guard: Mutex<Option<TopmostGuard>>,
    enabled: AtomicBool,
}

impl AltTabBlocker {
    /// Chords the keyboard hook must block for this module to be effective.
    pub fn combos() -> Vec<BlockedCombo> {
        alt_tab_combos()
    }

    /// Starts the guard for `hwnd` when `topmost_guard` (`shell.json → kiosk.topmostGuard`) and not in
    /// dev mode. A failed guard start (non-Windows) is logged; the blocker still tracks state.
    pub fn start(hwnd: isize, topmost_guard: bool, dev: bool) -> Self {
        let wanted = topmost_guard && !dev && hwnd != 0;
        let guard = if wanted {
            match TopmostGuard::start(hwnd, GUARD_INTERVAL) {
                Ok(guard) => {
                    tracing::info!(
                        interval_ms = GUARD_INTERVAL.as_millis() as u64,
                        "topmost guard started"
                    );
                    Some(guard)
                }
                Err(e) => {
                    tracing::warn!(error = %e, "topmost guard unavailable");
                    None
                }
            }
        } else {
            None
        };
        Self {
            hwnd,
            dev,
            guard: Mutex::new(guard),
            enabled: AtomicBool::new(wanted),
        }
    }

    pub fn hwnd(&self) -> isize {
        self.hwnd
    }

    /// `true` when a live guard thread exists.
    pub fn has_guard(&self) -> bool {
        self.guard.lock().is_some()
    }

    /// `true` while the guard re-asserts the Shell window (`kiosk_state.guardActive`).
    pub fn is_enabled(&self) -> bool {
        self.enabled.load(Ordering::Acquire)
    }

    /// Enables (re-arm: topmost + foreground + minimize others) or disables (drop topmost so a game
    /// or an allowed process can take the screen). No-op in dev mode.
    pub fn set_enabled(&self, on: bool) {
        if self.dev {
            return;
        }
        self.enabled.store(on, Ordering::Release);
        if let Some(guard) = self.guard.lock().as_ref() {
            if on {
                guard.resume();
            } else {
                guard.pause();
            }
        }
        if self.hwnd == 0 {
            return;
        }
        if on {
            self.assert_foreground();
        } else if let Err(e) = set_topmost(self.hwnd, false) {
            tracing::debug!(error = %e, "cannot drop topmost");
        }
        tracing::info!(enabled = on, "foreground guard");
    }

    /// One-shot: topmost + foreground + minimize every other visible window. Returns whether the
    /// Shell window ended up in the foreground.
    pub fn assert_foreground(&self) -> bool {
        if self.hwnd == 0 {
            return false;
        }
        if let Err(e) = set_topmost(self.hwnd, true) {
            tracing::debug!(error = %e, "cannot set topmost");
        }
        let foreground = match force_foreground(self.hwnd) {
            Ok(ok) => ok,
            Err(e) => {
                tracing::debug!(error = %e, "force_foreground failed");
                false
            }
        };
        match minimize_others(self.hwnd) {
            Ok(n) if n > 0 => tracing::info!(minimized = n, "other windows minimized"),
            Ok(_) => {}
            Err(e) => tracing::debug!(error = %e, "minimize_others failed"),
        }
        foreground
    }

    /// Stops the guard thread and drops topmost (before exit / handing over to explorer).
    pub fn shutdown(&self) {
        self.enabled.store(false, Ordering::Release);
        if let Some(guard) = self.guard.lock().take() {
            guard.pause();
            tracing::info!("topmost guard stopped");
        }
        if self.hwnd != 0 {
            let _ = set_topmost(self.hwnd, false);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stubbed_or_missing_window_is_inert() {
        let blocker = AltTabBlocker::start(0, true, false);
        assert!(!blocker.is_enabled() && !blocker.has_guard());
        assert!(!blocker.assert_foreground());
        blocker.set_enabled(true);
        assert!(
            blocker.is_enabled(),
            "state is tracked even without a window"
        );
        blocker.shutdown();
        assert!(!blocker.is_enabled());
        assert_eq!(AltTabBlocker::combos().len(), 4);
        let dev = AltTabBlocker::start(0, true, true);
        dev.set_enabled(true);
        assert!(!dev.is_enabled(), "dev mode ignores enable");
    }
}
