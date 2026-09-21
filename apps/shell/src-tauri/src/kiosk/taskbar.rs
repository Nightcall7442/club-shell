//! Taskbar hide/show (`shell.json → kiosk.hideTaskbar` ∪ `policy.explorer.hideTaskbar`). The kiosk
//! user has no `explorer.exe` (shell replacement), so normally there is nothing to hide; the 5 s
//! re-hide poll catches an administrator starting explorer, and [`Taskbar::rehide`] is called on
//! display changes, which re-show the tray windows. `ShowWindow(SW_HIDE)` is the primary mechanism,
//! `SHAppBarMessage(ABM_SETSTATE, ABS_AUTOHIDE)` the fallback. Restored on shutdown and on drop.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Weak};
use std::time::Duration;

use clubshell_winutil::window::{hide_taskbar, show_taskbar, taskbar_autohide};
use parking_lot::Mutex;
use tauri::async_runtime::JoinHandle;

/// Re-hide poll period while hidden.
pub const REHIDE_INTERVAL: Duration = Duration::from_secs(5);

/// Owner of the taskbar state.
pub struct Taskbar {
    hidden: AtomicBool,
    autohide_applied: AtomicBool,
    task: Mutex<Option<JoinHandle<()>>>,
}

impl Taskbar {
    /// Creates the controller and hides the taskbar right away when `hide`.
    pub fn start(hide: bool) -> Arc<Self> {
        let taskbar = Arc::new(Self {
            hidden: AtomicBool::new(false),
            autohide_applied: AtomicBool::new(false),
            task: Mutex::new(None),
        });
        if hide {
            taskbar.set_hidden(true);
        }
        taskbar
    }

    pub fn is_hidden(&self) -> bool {
        self.hidden.load(Ordering::Acquire)
    }

    /// Hides (and starts the re-hide poll) or shows (and stops it). Idempotent.
    pub fn set_hidden(self: &Arc<Self>, hidden: bool) {
        if self.hidden.swap(hidden, Ordering::AcqRel) == hidden {
            return;
        }
        if hidden {
            self.apply_hide(true);
            self.spawn_poll();
        } else {
            self.stop_poll();
            self.apply_show();
        }
        tracing::info!(hidden, "taskbar");
    }

    /// Re-applies the hide immediately (display change / explorer restart) when hidden.
    pub fn rehide(&self) {
        if self.is_hidden() {
            self.apply_hide(false);
        }
    }

    /// Shows the taskbar and stops the poll (shutdown / hand-over to explorer).
    pub fn restore(self: &Arc<Self>) {
        self.set_hidden(false);
    }

    fn spawn_poll(self: &Arc<Self>) {
        let weak: Weak<Self> = Arc::downgrade(self);
        let task = tauri::async_runtime::spawn(async move {
            let mut tick = tokio::time::interval(REHIDE_INTERVAL);
            tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
            loop {
                tick.tick().await;
                let Some(taskbar) = weak.upgrade() else { break };
                if !taskbar.is_hidden() {
                    break;
                }
                taskbar.apply_hide(false);
            }
        });
        if let Some(previous) = self.task.lock().replace(task) {
            previous.abort();
        }
    }

    fn stop_poll(&self) {
        if let Some(task) = self.task.lock().take() {
            task.abort();
        }
    }

    fn apply_hide(&self, first: bool) {
        match hide_taskbar() {
            Ok(0) => {}
            Ok(windows) => {
                if first {
                    tracing::info!(windows, "taskbar hidden");
                } else {
                    tracing::info!(
                        windows,
                        "taskbar re-hidden (explorer restarted or display changed)"
                    );
                }
            }
            Err(e) => {
                tracing::trace!(error = %e, "hide_taskbar failed; trying auto-hide");
                if taskbar_autohide(true).is_ok() {
                    self.autohide_applied.store(true, Ordering::Release);
                }
            }
        }
    }

    fn apply_show(&self) {
        match show_taskbar() {
            Ok(windows) if windows > 0 => tracing::info!(windows, "taskbar shown"),
            Ok(_) => {}
            Err(e) => tracing::trace!(error = %e, "show_taskbar failed"),
        }
        if self.autohide_applied.swap(false, Ordering::AcqRel) {
            let _ = taskbar_autohide(false);
        }
    }
}

impl Drop for Taskbar {
    fn drop(&mut self) {
        self.stop_poll();
        if self.hidden.swap(false, Ordering::AcqRel) {
            self.apply_show();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Hiding is not exercised here: on a developer machine it would flip the real taskbar.
    #[test]
    fn starts_visible_and_rehide_is_a_noop_while_visible() {
        let taskbar = Taskbar::start(false);
        assert!(!taskbar.is_hidden());
        taskbar.rehide();
        assert!(taskbar.task.lock().is_none());
        assert!(!taskbar.autohide_applied.load(Ordering::Acquire));
    }
}
