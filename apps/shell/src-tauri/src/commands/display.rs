//! `display_*` commands: monitor refresh rates for the player (Profile → Settings → Monitor). Local
//! to the Shell, which runs in the player's session (the Agent in session 0 cannot touch its display
//! modes), and registered by `lib.rs` next to the `kiosk_*` commands, so they are not in
//! `COMMAND_NAMES`.
//!
//! A switch works like Windows' own "Keep these display settings?": [`display_set_refresh_rate`]
//! applies the rate for this session only and arms a [`CONFIRM_TIMEOUT_SECS`] timer;
//! [`display_confirm`] saves it to the user's display settings, [`display_revert`] or the timer puts
//! the previous rate back. One change waits for confirmation at a time, and [`revert_pending`] (shell
//! exit) never leaves an unconfirmed mode behind. A confirmed rate is not undone at logout: the
//! monitor's best rate suits the next player as well.

use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

use chrono::{DateTime, Utc};
use clubshell_protocol::error::ErrorCode;
use clubshell_winutil::display::{self, refresh_rates, DisplayMode};
use clubshell_winutil::monitor::{self, MonitorInfo};
use parking_lot::Mutex;
use serde::Serialize;

use crate::state::{CmdResult, ErrorSource, ShellError};

/// Seconds the player has to confirm a new rate before it is undone (Windows waits 15 s as well).
pub const CONFIRM_TIMEOUT_SECS: u64 = 15;

/// One display (`display_list` and the result of every other `display_*` command).
#[derive(Serialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct DisplayDto {
    /// Enumeration index, as in `kiosk_monitors`.
    pub index: u32,
    /// GDI device name (`\\.\DISPLAY1`): the key the other `display_*` commands take.
    pub device: String,
    pub primary: bool,
    pub width: u32,
    pub height: u32,
    /// Current refresh rate in Hz (0 when unknown).
    pub hz: u32,
    /// Rates the player may pick at the current resolution, ascending; empty when the driver lists none.
    pub rates: Vec<u32>,
    /// The unconfirmed change on this display, if any.
    pub pending: Option<PendingDto>,
}

/// An applied but not yet confirmed rate.
#[derive(Serialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct PendingDto {
    /// Rate restored unless the change is confirmed.
    pub previous_hz: u32,
    /// When the previous rate comes back on its own.
    pub revert_at: DateTime<Utc>,
}

/// `display_list`: every attached display with its current and selectable refresh rates.
#[tauri::command]
pub async fn display_list() -> CmdResult<Vec<DisplayDto>> {
    blocking(|| SWITCHER.list()).await
}

/// `display_set_refresh_rate`: applies `hz` (one of the display's `rates`) to `device` for this
/// session and starts the confirmation countdown (`pending` in the result). Choosing the rate the
/// change started from cancels it.
#[tauri::command]
pub async fn display_set_refresh_rate(device: String, hz: u32) -> CmdResult<DisplayDto> {
    let (dto, timer) = blocking(move || SWITCHER.set(&device, hz, Utc::now())).await?;
    if let Some(id) = timer {
        tauri::async_runtime::spawn(async move {
            tokio::time::sleep(Duration::from_secs(CONFIRM_TIMEOUT_SECS)).await;
            let expired = tauri::async_runtime::spawn_blocking(move || SWITCHER.expire(id)).await;
            if let Err(e) = expired {
                tracing::error!(error = %e, "refresh rate confirmation timer failed");
            }
        });
    }
    Ok(dto)
}

/// `display_confirm`: keeps the pending rate of `device` and saves it to the user's display
/// settings; `notFound` when nothing is pending there (already reverted).
#[tauri::command]
pub async fn display_confirm(device: String) -> CmdResult<DisplayDto> {
    blocking(move || SWITCHER.confirm(&device)).await
}

/// `display_revert`: puts the previous rate of `device` back now (no-op when nothing is pending).
#[tauri::command]
pub async fn display_revert(device: String) -> CmdResult<DisplayDto> {
    blocking(move || SWITCHER.revert(&device)).await
}

/// Reverts an unconfirmed change on the calling thread; called when the Shell exits.
pub fn revert_pending() {
    SWITCHER.revert_any();
}

/// Mode switches block for a second or two (the screen blanks), so they run off the async workers.
async fn blocking<T: Send + 'static>(
    f: impl FnOnce() -> CmdResult<T> + Send + 'static,
) -> CmdResult<T> {
    tauri::async_runtime::spawn_blocking(f).await?
}

// ───────────────────────────── switcher ─────────────────────────────

/// The Win32 side of a switch, behind a seam so the confirm / revert rules are testable.
trait Driver: Send + Sync {
    fn monitors(&self) -> CmdResult<Vec<MonitorInfo>>;
    fn current_mode(&self, device: &str) -> CmdResult<DisplayMode>;
    /// Driver-listed modes; empty when they cannot be read.
    fn modes(&self, device: &str) -> Vec<DisplayMode>;
    fn set_refresh_rate(&self, device: &str, hz: u32) -> CmdResult<()>;
    fn save_current_mode(&self, device: &str) -> CmdResult<()>;
    /// Back to the saved (registry) mode: the fallback when the previous rate cannot be set.
    fn restore_saved_mode(&self, device: &str) -> CmdResult<()>;
}

struct Win32;

impl Driver for Win32 {
    fn monitors(&self) -> CmdResult<Vec<MonitorInfo>> {
        Ok(monitor::enumerate()?)
    }

    fn current_mode(&self, device: &str) -> CmdResult<DisplayMode> {
        Ok(display::current_mode(device)?)
    }

    fn modes(&self, device: &str) -> Vec<DisplayMode> {
        display::modes(device).unwrap_or_else(|e| {
            tracing::debug!(device, error = %e, "display modes unavailable");
            Vec::new()
        })
    }

    fn set_refresh_rate(&self, device: &str, hz: u32) -> CmdResult<()> {
        Ok(display::set_refresh_rate(device, hz)?)
    }

    fn save_current_mode(&self, device: &str) -> CmdResult<()> {
        Ok(display::save_current_mode(device)?)
    }

    fn restore_saved_mode(&self, device: &str) -> CmdResult<()> {
        Ok(display::restore_saved_mode(device)?)
    }
}

static SWITCHER: Switcher<Win32> = Switcher::new(Win32);

struct Pending {
    /// Ties the confirmation timer to this change, so a stale timer never reverts a newer one.
    id: u64,
    device: String,
    previous_hz: u32,
    revert_at: DateTime<Utc>,
}

impl Pending {
    fn dto(&self) -> PendingDto {
        PendingDto {
            previous_hz: self.previous_hz,
            revert_at: self.revert_at,
        }
    }
}

/// The pending change and every operation on it. The lock is held across the driver calls, so a
/// switch, a confirmation and the timer never interleave.
struct Switcher<D> {
    driver: D,
    pending: Mutex<Option<Pending>>,
    next_id: AtomicU64,
}

impl<D: Driver> Switcher<D> {
    const fn new(driver: D) -> Self {
        Self {
            driver,
            pending: Mutex::new(None),
            next_id: AtomicU64::new(1),
        }
    }

    fn list(&self) -> CmdResult<Vec<DisplayDto>> {
        let pending = self.pending.lock();
        Ok(self
            .driver
            .monitors()?
            .iter()
            .map(|m| self.describe(m, pending.as_ref()))
            .collect())
    }

    /// Applies `hz` and arms the pending change; returns the id the confirmation timer must carry
    /// (`None` when nothing waits for confirmation afterwards).
    fn set(
        &self,
        device: &str,
        hz: u32,
        now: DateTime<Utc>,
    ) -> CmdResult<(DisplayDto, Option<u64>)> {
        let mut pending = self.pending.lock();
        let monitor = self.monitor(device)?;
        if pending.as_ref().is_some_and(|p| p.device != device) {
            return Err(ShellError::new(
                ErrorCode::Conflict,
                "another display change waits for confirmation",
                ErrorSource::Tauri,
            ));
        }
        let current = self.driver.current_mode(device)?;
        if !refresh_rates(&self.driver.modes(device), &current).contains(&hz) {
            return Err(ShellError::validation("hz", "not offered by this display"));
        }
        // A second pick before confirming still reverts to the rate the change started from.
        let previous_hz = pending.as_ref().map_or(current.hz, |p| p.previous_hz);
        if hz != current.hz {
            self.driver.set_refresh_rate(device, hz)?;
            tracing::info!(device, from = current.hz, to = hz, "refresh rate switched");
        }
        let timer = if hz == previous_hz {
            *pending = None;
            None
        } else {
            let id = self.next_id.fetch_add(1, Ordering::Relaxed);
            *pending = Some(Pending {
                id,
                device: device.to_owned(),
                previous_hz,
                revert_at: now + chrono::Duration::seconds(CONFIRM_TIMEOUT_SECS as i64),
            });
            Some(id)
        };
        Ok((self.describe(&monitor, pending.as_ref()), timer))
    }

    fn confirm(&self, device: &str) -> CmdResult<DisplayDto> {
        let mut pending = self.pending.lock();
        if !pending.as_ref().is_some_and(|p| p.device == device) {
            return Err(ShellError::not_found("pending display change"));
        }
        *pending = None;
        match self.driver.save_current_mode(device) {
            Ok(()) => tracing::info!(device, "refresh rate confirmed"),
            // The rate stays for this session; only the saved default is missing.
            Err(e) => tracing::warn!(device, error = %e, "confirmed refresh rate not saved"),
        }
        let monitor = self.monitor(device)?;
        Ok(self.describe(&monitor, None))
    }

    fn revert(&self, device: &str) -> CmdResult<DisplayDto> {
        let mut pending = self.pending.lock();
        if pending.as_ref().is_some_and(|p| p.device == device) {
            if let Some(p) = pending.take() {
                self.undo(&p, "reverted by the player");
            }
        }
        let monitor = self.monitor(device)?;
        Ok(self.describe(&monitor, pending.as_ref()))
    }

    /// Confirmation timer: reverts the change `id` unless it was confirmed, reverted or replaced.
    fn expire(&self, id: u64) {
        let mut pending = self.pending.lock();
        if pending.as_ref().is_some_and(|p| p.id == id) {
            if let Some(p) = pending.take() {
                self.undo(&p, "not confirmed in time");
            }
        }
    }

    fn revert_any(&self) {
        if let Some(p) = self.pending.lock().take() {
            self.undo(&p, "shell exiting");
        }
    }

    /// Puts the previous rate back; when the driver refuses it, the saved mode, which the change
    /// never touched. Never fails: the pending change is gone either way.
    fn undo(&self, p: &Pending, why: &str) {
        match self.driver.set_refresh_rate(&p.device, p.previous_hz) {
            Ok(()) => {
                tracing::info!(device = %p.device, hz = p.previous_hz, why, "refresh rate reverted")
            }
            Err(e) => {
                tracing::warn!(device = %p.device, error = %e, why, "previous refresh rate refused; restoring the saved mode");
                if let Err(e) = self.driver.restore_saved_mode(&p.device) {
                    tracing::error!(device = %p.device, error = %e, "cannot restore the saved display mode");
                }
            }
        }
    }

    fn monitor(&self, device: &str) -> CmdResult<MonitorInfo> {
        self.driver
            .monitors()?
            .into_iter()
            .find(|m| m.name == device)
            .ok_or_else(|| ShellError::not_found("display"))
    }

    fn describe(&self, m: &MonitorInfo, pending: Option<&Pending>) -> DisplayDto {
        let (current, rates) = match self.driver.current_mode(&m.name) {
            Ok(current) => (
                current,
                refresh_rates(&self.driver.modes(&m.name), &current),
            ),
            Err(e) => {
                tracing::debug!(device = %m.name, error = %e, "current display mode unavailable");
                let fallback = DisplayMode {
                    width: u32::try_from(m.width).unwrap_or(0),
                    height: u32::try_from(m.height).unwrap_or(0),
                    hz: m.hz,
                    ..DisplayMode::default()
                };
                (fallback, Vec::new())
            }
        };
        DisplayDto {
            index: u32::try_from(m.index).unwrap_or(u32::MAX),
            device: m.name.clone(),
            primary: m.primary,
            width: current.width,
            height: current.height,
            hz: current.hz,
            rates,
            pending: pending.filter(|p| p.device == m.name).map(Pending::dto),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use clubshell_winutil::Rect;

    /// Two 1080p displays; records every mode set and save.
    struct Fake {
        hz: Mutex<Vec<(String, u32)>>,
        calls: Mutex<Vec<String>>,
        refuse_hz: Option<u32>,
    }

    impl Fake {
        fn new() -> Self {
            Self {
                hz: Mutex::new(vec![(DISPLAY1.into(), 60), (DISPLAY2.into(), 144)]),
                calls: Mutex::new(Vec::new()),
                refuse_hz: None,
            }
        }

        fn hz_of(&self, device: &str) -> u32 {
            self.hz
                .lock()
                .iter()
                .find(|(d, _)| d == device)
                .map_or(0, |(_, hz)| *hz)
        }

        fn calls(&self) -> Vec<String> {
            self.calls.lock().clone()
        }
    }

    const DISPLAY1: &str = r"\\.\DISPLAY1";
    const DISPLAY2: &str = r"\\.\DISPLAY2";

    fn mode(hz: u32) -> DisplayMode {
        DisplayMode {
            width: 1920,
            height: 1080,
            bits_per_pixel: 32,
            hz,
            interlaced: false,
        }
    }

    impl Driver for Fake {
        fn monitors(&self) -> CmdResult<Vec<MonitorInfo>> {
            Ok([DISPLAY1, DISPLAY2]
                .iter()
                .enumerate()
                .map(|(index, name)| {
                    let rect = Rect::from_size(1920 * index as i32, 0, 1920, 1080);
                    MonitorInfo {
                        index,
                        handle: index as isize + 1,
                        name: (*name).to_owned(),
                        rect,
                        work_rect: rect,
                        width: 1920,
                        height: 1080,
                        hz: self.hz_of(name),
                        primary: index == 0,
                        scale: 1.0,
                    }
                })
                .collect())
        }

        fn current_mode(&self, device: &str) -> CmdResult<DisplayMode> {
            Ok(mode(self.hz_of(device)))
        }

        fn modes(&self, _device: &str) -> Vec<DisplayMode> {
            [59, 60, 120, 144, 240].map(mode).to_vec()
        }

        fn set_refresh_rate(&self, device: &str, hz: u32) -> CmdResult<()> {
            self.calls.lock().push(format!("set {device} {hz}"));
            if self.refuse_hz == Some(hz) {
                return Err(ShellError::internal("refused"));
            }
            if let Some(entry) = self.hz.lock().iter_mut().find(|(d, _)| d == device) {
                entry.1 = hz;
            }
            Ok(())
        }

        fn save_current_mode(&self, device: &str) -> CmdResult<()> {
            self.calls.lock().push(format!("save {device}"));
            Ok(())
        }

        fn restore_saved_mode(&self, device: &str) -> CmdResult<()> {
            self.calls.lock().push(format!("restore {device}"));
            Ok(())
        }
    }

    fn now() -> DateTime<Utc> {
        DateTime::parse_from_rfc3339("2026-10-02T12:00:00Z")
            .unwrap()
            .with_timezone(&Utc)
    }

    #[test]
    fn list_offers_the_rates_of_each_display() {
        let s = Switcher::new(Fake::new());
        let list = s.list().unwrap();
        assert_eq!(list.len(), 2);
        assert_eq!(
            (list[0].device.as_str(), list[0].hz, list[0].primary),
            (DISPLAY1, 60, true)
        );
        assert_eq!(list[0].rates, vec![60, 120, 144, 240]);
        assert_eq!(list[1].hz, 144);
        assert!(list.iter().all(|d| d.pending.is_none()));
        let json = serde_json::to_value(&list[0]).unwrap();
        assert_eq!(json["device"], DISPLAY1);
        assert!(json["pending"].is_null());
    }

    #[test]
    fn switch_then_confirm_saves_the_new_rate() {
        let s = Switcher::new(Fake::new());
        let (dto, timer) = s.set(DISPLAY1, 240, now()).unwrap();
        assert_eq!(dto.hz, 240);
        let pending = dto.pending.expect("waits for confirmation");
        assert_eq!(pending.previous_hz, 60);
        assert_eq!(pending.revert_at, now() + chrono::Duration::seconds(15));
        assert!(timer.is_some());
        assert_eq!(
            serde_json::to_value(&pending).unwrap()["revertAt"],
            "2026-10-02T12:00:15Z"
        );

        let dto = s.confirm(DISPLAY1).unwrap();
        assert_eq!((dto.hz, dto.pending), (240, None));
        assert_eq!(
            s.driver.calls(),
            vec![format!("set {DISPLAY1} 240"), format!("save {DISPLAY1}")]
        );
        // The timer firing afterwards changes nothing.
        s.expire(timer.unwrap());
        assert_eq!(s.driver.hz_of(DISPLAY1), 240);
        assert_eq!(
            s.confirm(DISPLAY1).unwrap_err().code,
            ErrorCode::NotFound,
            "nothing left to confirm"
        );
    }

    #[test]
    fn unconfirmed_switch_reverts_on_timeout_revert_or_exit() {
        let s = Switcher::new(Fake::new());
        let (_, timer) = s.set(DISPLAY1, 144, now()).unwrap();
        s.expire(timer.unwrap());
        assert_eq!(s.driver.hz_of(DISPLAY1), 60);
        assert!(s.list().unwrap().iter().all(|d| d.pending.is_none()));
        assert_eq!(s.confirm(DISPLAY1).unwrap_err().code, ErrorCode::NotFound);

        s.set(DISPLAY1, 120, now()).unwrap();
        let dto = s.revert(DISPLAY1).unwrap();
        assert_eq!((dto.hz, dto.pending), (60, None));
        assert_eq!(s.revert(DISPLAY1).unwrap().hz, 60, "revert is idempotent");

        s.set(DISPLAY2, 240, now()).unwrap();
        s.revert_any();
        assert_eq!(s.driver.hz_of(DISPLAY2), 144);
        assert!(!s.driver.calls().iter().any(|c| c.starts_with("save")));
    }

    #[test]
    fn stale_timer_leaves_a_newer_pick_alone() {
        let s = Switcher::new(Fake::new());
        let (_, first) = s.set(DISPLAY1, 144, now()).unwrap();
        let (dto, second) = s.set(DISPLAY1, 240, now()).unwrap();
        assert_eq!(
            dto.pending.map(|p| p.previous_hz),
            Some(60),
            "still reverts to the starting rate"
        );
        s.expire(first.unwrap());
        assert_eq!(s.driver.hz_of(DISPLAY1), 240);
        s.expire(second.unwrap());
        assert_eq!(s.driver.hz_of(DISPLAY1), 60);
    }

    #[test]
    fn picking_the_starting_rate_cancels_the_change() {
        let s = Switcher::new(Fake::new());
        s.set(DISPLAY1, 144, now()).unwrap();
        let (dto, timer) = s.set(DISPLAY1, 60, now()).unwrap();
        assert_eq!((dto.hz, dto.pending, timer), (60, None, None));
        let (_, timer) = s.set(DISPLAY1, 60, now()).unwrap();
        assert_eq!(timer, None, "the current rate is a no-op");
        assert_eq!(
            s.driver.calls(),
            vec![format!("set {DISPLAY1} 144"), format!("set {DISPLAY1} 60")]
        );
    }

    #[test]
    fn bad_requests_touch_nothing() {
        let s = Switcher::new(Fake::new());
        assert_eq!(
            s.set(DISPLAY1, 75, now()).unwrap_err().code,
            ErrorCode::Validation
        );
        assert_eq!(
            s.set(DISPLAY1, 59, now()).unwrap_err().code,
            ErrorCode::Validation,
            "the NTSC twin is not offered"
        );
        assert_eq!(
            s.set(r"\\.\DISPLAY9", 60, now()).unwrap_err().code,
            ErrorCode::NotFound
        );
        s.set(DISPLAY1, 144, now()).unwrap();
        assert_eq!(
            s.set(DISPLAY2, 60, now()).unwrap_err().code,
            ErrorCode::Conflict,
            "one pending change at a time"
        );
        assert_eq!(s.driver.calls(), vec![format!("set {DISPLAY1} 144")]);
    }

    #[test]
    fn refused_revert_falls_back_to_the_saved_mode() {
        let mut fake = Fake::new();
        fake.refuse_hz = Some(60);
        let s = Switcher::new(fake);
        s.set(DISPLAY1, 240, now()).unwrap();
        let dto = s.revert(DISPLAY1).unwrap();
        assert_eq!(dto.pending, None);
        assert_eq!(
            s.driver.calls(),
            vec![
                format!("set {DISPLAY1} 240"),
                format!("set {DISPLAY1} 60"),
                format!("restore {DISPLAY1}"),
            ]
        );
    }
}
