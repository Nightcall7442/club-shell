//! Windows the Shell puts in front of itself (SHELL_CHANGES 81, 87).
//!
//! A game or a program started for the player is created by the Agent's service (session 0) or by a launcher, and
//! neither owns the foreground: its window opens under the Shell's full-screen window. The leave the Shell gives with
//! `AllowSetForegroundWindow` before the launch is gone at the player's next input to the Shell (a mouse move over the
//! launch HUD will do). So the Shell itself, while it still owns the foreground, waits for the new window and brings it
//! to the front:
//! - a game: after its `running` event (launched from the Shell or from the cash desk), up to [`GAME_WAIT`];
//! - a program from the "Apps" screen: after `apps.launch`, up to [`APP_WAIT`], handed off like the dock does it so the
//!   foreground guard and the stray-window sweep leave it alone; its window may belong to a child process or to an
//!   instance that was already running, so a program window that was not there before the launch counts too.
//!
//! Waiting stops once the target owns the foreground, when the game ends, or when the player went to another program
//! themselves (the foreground is neither the Shell's nor the target's).

use std::collections::HashSet;
use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use clubshell_winutil::window::{foreground_window, window_pid};

use super::open_windows::{bring_to_front, program_windows, WindowDescriptor};
use super::Kiosk;

/// How long a game's window is waited for after its `running` event.
pub const GAME_WAIT: Duration = Duration::from_secs(30);

/// How long a program's window is waited for after `apps.launch`.
pub const APP_WAIT: Duration = Duration::from_secs(15);

/// Polling interval of both waits.
pub const POLL: Duration = Duration::from_millis(250);

/// When the foreground owner is logged once after a game's `running` event (SHELL_CHANGES 83).
const FOREGROUND_REPORT_AFTER: Duration = Duration::from_secs(5);

/// The running game the Shell keeps in front of itself, and the wait for its window.
#[derive(Default)]
pub struct GameFront {
    /// Bumped by every new target and by the game's end: a wait whose generation is stale stops.
    generation: AtomicU64,
    pid: AtomicU32,
}

/// One poll of the foreground and the program windows.
struct Poll {
    foreground_pid: u32,
    windows: Vec<WindowDescriptor>,
}

impl Poll {
    fn take() -> Self {
        let fg = foreground_window();
        Self {
            foreground_pid: if fg == 0 {
                0
            } else {
                window_pid(fg).unwrap_or(0)
            },
            windows: program_windows(),
        }
    }

    /// The foreground is another program's: the player went there.
    fn elsewhere(&self, target: u32) -> bool {
        self.foreground_pid != 0
            && self.foreground_pid != std::process::id()
            && self.foreground_pid != target
    }
}

impl GameFront {
    pub fn new() -> Arc<Self> {
        Arc::new(Self::default())
    }

    /// The game being kept in front, if any.
    pub fn game_pid(&self) -> Option<u32> {
        match self.pid.load(Ordering::Acquire) {
            0 => None,
            pid => Some(pid),
        }
    }

    /// The game ended (exited, failed, killed): no more waiting or re-raising.
    pub fn clear(&self) {
        self.generation.fetch_add(1, Ordering::AcqRel);
        self.pid.store(0, Ordering::Release);
    }

    /// `running` with `pid` (a successor replaces the target): waits for the game's window and brings it to the front.
    pub fn track(self: &Arc<Self>, pid: u32) {
        let generation = self.generation.fetch_add(1, Ordering::AcqRel) + 1;
        self.pid.store(pid, Ordering::Release);
        let me = Arc::clone(self);
        tauri::async_runtime::spawn(async move { me.wait_game(generation, pid).await });
    }

    fn current(&self, generation: u64) -> bool {
        self.generation.load(Ordering::Acquire) == generation
    }

    async fn wait_game(self: Arc<Self>, generation: u64, pid: u32) {
        let started = Instant::now();
        let mut seen_others: HashSet<u32> = HashSet::new();
        let mut found = false;
        let mut reported = false;
        loop {
            if !self.current(generation) {
                return;
            }
            let elapsed = started.elapsed();
            if !reported && elapsed >= FOREGROUND_REPORT_AFTER {
                reported = true;
                log_foreground("5 s after running", pid);
            }
            if elapsed > GAME_WAIT {
                if !found {
                    tracing::info!(
                        pid,
                        "game window not found within {} s",
                        GAME_WAIT.as_secs()
                    );
                }
                return;
            }
            let Ok(poll) = tauri::async_runtime::spawn_blocking(Poll::take).await else {
                return;
            };
            log_new_windows(&poll.windows, pid, &mut seen_others);
            if poll.foreground_pid == pid {
                tracing::info!(pid, ms = elapsed.as_millis(), "game window in front");
                if !reported {
                    log_foreground("game in front", pid);
                }
                return;
            }
            if poll.elsewhere(pid) {
                tracing::info!(
                    pid,
                    foreground = poll.foreground_pid,
                    "the player moved to another program; the game is not pulled forward"
                );
                return;
            }
            if let Some(window) = poll.windows.iter().find(|w| w.pid == pid) {
                if !found {
                    found = true;
                    tracing::info!(pid, hwnd = window.hwnd, ms = elapsed.as_millis(), title = %window.title, "game window found");
                }
                let (hwnd, target) = (window.hwnd, pid);
                let ok = tauri::async_runtime::spawn_blocking(move || bring_to_front(hwnd, target))
                    .await
                    .unwrap_or(false);
                tracing::info!(pid, hwnd, ok, "game window brought to front");
                if ok {
                    return;
                }
            }
            tokio::time::sleep(POLL).await;
        }
    }

    /// The Shell's own window got the foreground during a game, with no input from the player (a game re-creating
    /// its window on a display-mode change, say): the game's window goes back in front (SHELL_CHANGES 81).
    pub fn reclaim(self: &Arc<Self>) {
        let Some(pid) = self.game_pid() else {
            return;
        };
        let me = Arc::clone(self);
        tauri::async_runtime::spawn(async move {
            let raised = tauri::async_runtime::spawn_blocking(move || {
                let poll = Poll::take();
                poll.windows
                    .iter()
                    .find(|w| w.pid == pid)
                    .map(|w| (w.hwnd, bring_to_front(w.hwnd, pid)))
            })
            .await
            .ok()
            .flatten();
            if me.game_pid() != Some(pid) {
                return;
            }
            match raised {
                Some((hwnd, ok)) => tracing::info!(pid, hwnd, ok, "shell got the foreground during the game; game window brought back"),
                None => tracing::info!(pid, "shell got the foreground during the game; the game has no window to bring back"),
            }
        });
    }
}

/// `apps.launch` answered with `pid` (SHELL_CHANGES 87): hands the foreground off to it and, in the background, waits
/// for its window — or a child's, or a new program window — and brings it to the front. `before` are the program
/// windows open before the launch.
pub fn bring_app(kiosk: Arc<Kiosk>, pid: u32, before: HashSet<isize>) {
    kiosk.hand_off_foreground(pid);
    tauri::async_runtime::spawn(async move {
        let started = Instant::now();
        let mut target = pid;
        loop {
            if kiosk.is_locked() {
                return;
            }
            let elapsed = started.elapsed();
            if elapsed > APP_WAIT {
                tracing::info!(
                    pid,
                    "program window not found within {} s; the shell takes the screen back",
                    APP_WAIT.as_secs()
                );
                kiosk.end_hand_off();
                return;
            }
            let Ok(poll) = tauri::async_runtime::spawn_blocking(Poll::take).await else {
                return;
            };
            if poll.foreground_pid == target {
                tracing::info!(
                    pid = target,
                    ms = elapsed.as_millis(),
                    "program window in front"
                );
                return;
            }
            if poll.elsewhere(target) {
                tracing::info!(
                    pid = target,
                    foreground = poll.foreground_pid,
                    "the player moved to another program; the program is not pulled forward"
                );
                return;
            }
            // Its own window first; else one that was not there before the launch (a stub launcher's child, or the
            // instance that was already running and took the work over).
            let window = poll
                .windows
                .iter()
                .find(|w| w.pid == target)
                .or_else(|| poll.windows.iter().find(|w| !before.contains(&w.hwnd)));
            if let Some(window) = window {
                let (hwnd, owner) = (window.hwnd, window.pid);
                if owner != target {
                    tracing::info!(pid, owner, hwnd, title = %window.title, "program window belongs to another process");
                    target = owner;
                    kiosk.hand_off_foreground(owner);
                }
                let ok = tauri::async_runtime::spawn_blocking(move || bring_to_front(hwnd, owner))
                    .await
                    .unwrap_or(false);
                tracing::info!(
                    pid = owner,
                    hwnd,
                    ms = elapsed.as_millis(),
                    ok,
                    "program window brought to front"
                );
                if ok {
                    return;
                }
            }
            tokio::time::sleep(POLL).await;
        }
    });
}

/// Program windows open right now (before a launch): `bring_app` tells new ones from these.
pub async fn current_windows() -> HashSet<isize> {
    tauri::async_runtime::spawn_blocking(|| program_windows().into_iter().map(|w| w.hwnd).collect())
        .await
        .unwrap_or_default()
}

fn log_new_windows(windows: &[WindowDescriptor], game: u32, seen: &mut HashSet<u32>) {
    for w in windows.iter().filter(|w| w.pid != game) {
        if seen.insert(w.pid) {
            tracing::info!(pid = w.pid, exe = %w.exe_path, title = %w.title, "other program window while waiting for the game");
        }
    }
}

fn log_foreground(when: &str, game: u32) {
    let fg = foreground_window();
    let pid = if fg == 0 {
        0
    } else {
        window_pid(fg).unwrap_or(0)
    };
    tracing::info!(when, game, foreground = pid, process = ?super::window_guard::process_name_of(pid), "foreground during the game");
}
