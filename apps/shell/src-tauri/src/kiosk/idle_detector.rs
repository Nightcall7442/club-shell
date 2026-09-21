//! Idle detection (`ARCHITECTURE.md` §5.2): `GetLastInputInfo` polled every second, merged with the
//! [`ActivityFeed`] that the keyboard hook and `kiosk_idle_reset` touch, emitting `kiosk://idle`
//! (`TAURI_COMMANDS.md` §3.2) whenever the stage crosses `idle.dimAfterSec`, `idle.timeoutSec` or
//! `idle.screensaverAfterSec`, and once more on return to activity.

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use clubshell_winutil::input::idle_duration;
use parking_lot::{Mutex, RwLock};
use serde::Serialize;
use tauri::async_runtime::JoinHandle;
use tauri::{AppHandle, Emitter};

use crate::config::IdleConfig;

/// Event name (`TAURI_COMMANDS.md` §3.2).
pub const IDLE_EVENT: &str = "kiosk://idle";

/// Sampling interval of the detector task.
pub const POLL_INTERVAL: Duration = Duration::from_secs(1);

/// Lock-free "last activity" timestamp shared with the hook thread (one atomic store per key press).
/// `GetLastInputInfo` cannot be reset by software, so the detector takes the *minimum* of the OS idle
/// time and the time since this feed was last touched.
pub struct ActivityFeed {
    epoch: Instant,
    last_ms: AtomicU64,
}

impl ActivityFeed {
    pub fn new() -> Arc<Self> {
        Arc::new(Self { epoch: Instant::now(), last_ms: AtomicU64::new(0) })
    }

    /// Milliseconds since the feed was created (monotonic).
    pub fn now_ms(&self) -> u64 {
        u64::try_from(self.epoch.elapsed().as_millis()).unwrap_or(u64::MAX)
    }

    /// Records activity now. Safe to call from the hook thread.
    pub fn touch(&self) {
        self.last_ms.fetch_max(self.now_ms(), Ordering::Relaxed);
    }

    /// Time since the last [`touch`](Self::touch) (or since creation).
    pub fn since(&self) -> Duration {
        Duration::from_millis(self.now_ms().saturating_sub(self.last_ms.load(Ordering::Relaxed)))
    }
}

/// Idle stage in ascending order of inactivity.
#[derive(Serialize, Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
#[serde(rename_all = "camelCase")]
pub enum IdleStage {
    Active,
    Dim,
    Idle,
    Screensaver,
}

/// `kiosk://idle` payload and the value of `kiosk_state.idle` / `idleSec`.
#[derive(Serialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct IdleStatus {
    pub idle: bool,
    pub idle_sec: u64,
    pub stage: IdleStage,
}

/// Stage thresholds in seconds; `0` disables a stage.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct IdleThresholds {
    pub dim_sec: u32,
    pub idle_sec: u32,
    pub screensaver_sec: u32,
}

impl IdleThresholds {
    pub fn from_config(config: &IdleConfig) -> Self {
        Self { dim_sec: config.dim_after_sec, idle_sec: config.timeout_sec, screensaver_sec: config.screensaver_after_sec }
    }

    /// Stage for `secs` of inactivity (highest enabled threshold that was crossed).
    pub fn stage_for(&self, secs: u64) -> IdleStage {
        let crossed = |t: u32| t > 0 && secs >= u64::from(t);
        if crossed(self.screensaver_sec) {
            IdleStage::Screensaver
        } else if crossed(self.idle_sec) {
            IdleStage::Idle
        } else if crossed(self.dim_sec) {
            IdleStage::Dim
        } else {
            IdleStage::Active
        }
    }
}

/// Background idle sampler; see the module docs.
pub struct IdleDetector {
    app: AppHandle,
    feed: Arc<ActivityFeed>,
    thresholds: RwLock<IdleThresholds>,
    paused: AtomicBool,
    stopped: AtomicBool,
    last: Mutex<IdleStatus>,
    task: Mutex<Option<JoinHandle<()>>>,
}

impl IdleDetector {
    /// Starts the sampler task on the Tauri runtime.
    pub fn start(app: &AppHandle, config: &IdleConfig, feed: Arc<ActivityFeed>) -> Arc<Self> {
        let detector = Arc::new(Self {
            app: app.clone(),
            feed,
            thresholds: RwLock::new(IdleThresholds::from_config(config)),
            paused: AtomicBool::new(false),
            stopped: AtomicBool::new(false),
            last: Mutex::new(IdleStatus { idle: false, idle_sec: 0, stage: IdleStage::Active }),
            task: Mutex::new(None),
        });
        let me = Arc::clone(&detector);
        let task = tauri::async_runtime::spawn(async move {
            let mut tick = tokio::time::interval(POLL_INTERVAL);
            tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
            while !me.stopped.load(Ordering::Acquire) {
                tick.tick().await;
                me.evaluate();
            }
        });
        *detector.task.lock() = Some(task);
        detector
    }

    /// The feed the keyboard hook touches.
    pub fn feed(&self) -> &Arc<ActivityFeed> {
        &self.feed
    }

    /// `kiosk_idle_reset`: marks activity and re-evaluates immediately.
    pub fn reset(&self) {
        self.feed.touch();
        self.evaluate();
    }

    /// Sets the `idle` threshold only (`policy.kiosk.idleTimeoutSec`, `settings_set{idleTimeoutSec}`).
    pub fn set_timeout(&self, sec: u32) {
        self.thresholds.write().idle_sec = sec;
        self.evaluate();
    }

    pub fn set_thresholds(&self, thresholds: IdleThresholds) {
        *self.thresholds.write() = thresholds;
        self.evaluate();
    }

    pub fn thresholds(&self) -> IdleThresholds {
        *self.thresholds.read()
    }

    /// While paused (a game is running) the detector reports `active` and emits nothing.
    pub fn set_paused(&self, paused: bool) {
        self.paused.store(paused, Ordering::Release);
        self.evaluate();
    }

    pub fn is_paused(&self) -> bool {
        self.paused.load(Ordering::Acquire)
    }

    /// Last evaluated status.
    pub fn status(&self) -> IdleStatus {
        *self.last.lock()
    }

    /// Stops the sampler task.
    pub fn stop(&self) {
        self.stopped.store(true, Ordering::Release);
        if let Some(task) = self.task.lock().take() {
            task.abort();
        }
    }

    /// Seconds of inactivity: min(OS idle, feed idle); the feed alone when the OS value is unavailable.
    pub fn idle_seconds(&self) -> u64 {
        let local = self.feed.since();
        idle_duration().map_or(local, |os| os.min(local)).as_secs()
    }

    fn evaluate(&self) -> IdleStatus {
        let secs = if self.is_paused() { 0 } else { self.idle_seconds() };
        let stage = self.thresholds.read().stage_for(secs);
        let status = IdleStatus { idle: stage >= IdleStage::Idle, idle_sec: secs, stage };
        let changed = {
            let mut last = self.last.lock();
            let changed = last.stage != stage;
            *last = status;
            changed
        };
        if changed {
            tracing::info!(?stage, idle_sec = secs, "idle stage changed");
            if let Err(e) = self.app.emit(IDLE_EVENT, status) {
                tracing::warn!(error = %e, "cannot emit kiosk://idle");
            }
        }
        status
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stages_follow_thresholds_and_zero_disables() {
        let t = IdleThresholds { dim_sec: 120, idle_sec: 300, screensaver_sec: 600 };
        assert_eq!(t.stage_for(0), IdleStage::Active);
        assert_eq!(t.stage_for(119), IdleStage::Active);
        assert_eq!(t.stage_for(120), IdleStage::Dim);
        assert_eq!(t.stage_for(300), IdleStage::Idle);
        assert_eq!(t.stage_for(10_000), IdleStage::Screensaver);
        let no_dim = IdleThresholds { dim_sec: 0, ..t };
        assert_eq!(no_dim.stage_for(200), IdleStage::Active);
        assert!(IdleStage::Idle >= IdleStage::Dim);
        let json = serde_json::to_value(IdleStatus { idle: true, idle_sec: 301, stage: IdleStage::Idle }).unwrap();
        assert_eq!(json, serde_json::json!({ "idle": true, "idleSec": 301, "stage": "idle" }));
    }

    #[test]
    fn feed_is_monotonic() {
        let feed = ActivityFeed::new();
        feed.touch();
        assert!(feed.since() < Duration::from_secs(1));
        feed.last_ms.store(5, Ordering::Relaxed);
        feed.touch();
        assert!(feed.last_ms.load(Ordering::Relaxed) >= 5);
    }
}
