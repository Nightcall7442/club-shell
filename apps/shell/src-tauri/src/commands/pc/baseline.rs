//! The club's values and their restore. [`Tracker::start`] remembers what the Shell found at start (the *baseline*);
//! the first player change writes it to the baseline file (`%LOCALAPPDATA%\ClubShell\pc-baseline.json`) and marks the
//! PC dirty; [`Tracker::restore`] puts the baseline back and deletes the file.
//!
//! A file found at start means the previous Shell never restored: a restart in the middle of a session (same boot:
//! keep it as the baseline, restore when the player leaves) or a reboot (restore now: the SPI values are already the
//! registry's again, but the default output device is machine-wide and survived).

use std::path::PathBuf;

use parking_lot::Mutex;
use serde::{Deserialize, Serialize};

use super::audio::AudioDefaults;
use super::mouse::MouseState;
use crate::state::CmdResult;

/// Two boot times closer than this are the same boot (tick-count rounding, small clock corrections).
const SAME_BOOT_TOLERANCE_SEC: u64 = 120;

/// What is restored when the player leaves.
#[derive(Serialize, Deserialize, Clone, Debug, Default, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Baseline {
    /// Boot time (Unix seconds) of the run that wrote the file.
    pub boot_unix: u64,
    pub mouse: Option<MouseState>,
    pub audio: Option<AudioDefaults>,
}

/// `true` when two boot times belong to the same boot.
pub fn same_boot(a: u64, b: u64) -> bool {
    a.abs_diff(b) <= SAME_BOOT_TOLERANCE_SEC
}

/// The native side of the tracked settings; a fake in tests, so the restore logic never touches this PC's settings.
pub trait Backend: Send + Sync {
    fn read_mouse(&self) -> CmdResult<MouseState>;
    fn write_mouse(&self, next: &MouseState, current: &MouseState) -> CmdResult<()>;
    fn audio_defaults(&self) -> CmdResult<AudioDefaults>;
    fn set_audio_defaults(&self, wanted: &AudioDefaults, current: &AudioDefaults) -> CmdResult<()>;
}

/// [`Backend`] of the running Shell (needs a COM-initialised thread for the audio part).
pub struct NativeBackend;

impl Backend for NativeBackend {
    fn read_mouse(&self) -> CmdResult<MouseState> {
        super::mouse::native::read()
    }

    fn write_mouse(&self, next: &MouseState, current: &MouseState) -> CmdResult<()> {
        super::mouse::native::write(next, current)
    }

    fn audio_defaults(&self) -> CmdResult<AudioDefaults> {
        super::audio::native::defaults()
    }

    fn set_audio_defaults(&self, wanted: &AudioDefaults, current: &AudioDefaults) -> CmdResult<()> {
        super::audio::native::set_defaults(wanted, current)
    }
}

#[derive(Default)]
struct Tracked {
    baseline: Option<Baseline>,
    /// A player changed something since the last restore (the baseline file exists).
    dirty: bool,
}

/// Baseline + dirty flag behind one lock, which also serializes every write of a tracked setting.
pub struct Tracker {
    backend: Box<dyn Backend>,
    /// Baseline file; `None` when `%LOCALAPPDATA%` is unknown (the restore then only works within one Shell run).
    file: Option<PathBuf>,
    tracked: Mutex<Tracked>,
    boot_unix: u64,
}

impl Tracker {
    /// `boot_unix`: boot time of this run, stamped into the file so the next run can tell a restart from a reboot.
    pub fn new(backend: Box<dyn Backend>, file: Option<PathBuf>, boot_unix: u64) -> Self {
        Self {
            backend,
            file,
            tracked: Mutex::new(Tracked::default()),
            boot_unix,
        }
    }

    /// Shell start: adopts a leftover baseline file (restoring it now after a reboot) or reads the club's values.
    pub fn start(&self) {
        let boot_unix = self.boot_unix;
        if let Some(left) = self.load() {
            let rebooted = !same_boot(left.boot_unix, boot_unix);
            tracing::warn!(
                rebooted,
                "player PC settings were not restored by the previous run"
            );
            {
                let mut t = self.tracked.lock();
                t.baseline = Some(left);
                t.dirty = true;
            }
            if rebooted {
                self.restore("reboot");
            }
            return;
        }
        let captured = self.capture(boot_unix);
        tracing::info!(
            mouse = captured.mouse.is_some(),
            audio = captured.audio.is_some(),
            "club PC settings remembered"
        );
        let mut t = self.tracked.lock();
        if t.baseline.is_none() {
            t.baseline = Some(captured);
        }
    }

    /// Runs `change` (a player's edit) after making sure the baseline is remembered and on disk. A part that could not
    /// be read at start is read now, before the first edit touches it.
    pub fn change<T>(
        &self,
        change: impl FnOnce(&dyn Backend, &Baseline) -> CmdResult<T>,
    ) -> CmdResult<T> {
        let mut t = self.tracked.lock();
        let boot_unix = self.boot_unix;
        let mut baseline = t.baseline.take().unwrap_or_default();
        let mut completed = false;
        if baseline.mouse.is_none() {
            baseline.mouse = self.backend.read_mouse().ok();
            completed |= baseline.mouse.is_some();
        }
        if baseline.audio.is_none() {
            baseline.audio = self.backend.audio_defaults().ok();
            completed |= baseline.audio.is_some();
        }
        t.baseline = Some(baseline.clone());
        if !t.dirty || completed {
            t.dirty = true;
            self.save(&Baseline {
                boot_unix,
                ..baseline.clone()
            });
        }
        change(self.backend.as_ref(), &baseline)
    }

    /// Reads under the lock (so a read never sees half of a restore).
    pub fn read<T>(&self, read: impl FnOnce(&dyn Backend) -> CmdResult<T>) -> CmdResult<T> {
        let _t = self.tracked.lock();
        read(self.backend.as_ref())
    }

    /// The player left: puts the baseline back when something changed. Returns whether a restore ran.
    pub fn restore(&self, reason: &str) -> bool {
        let mut t = self.tracked.lock();
        if !t.dirty {
            return false;
        }
        if let Some(baseline) = t.baseline.clone() {
            if let Some(mouse) = baseline.mouse {
                match self.backend.read_mouse() {
                    Ok(current) if current != mouse => {
                        if let Err(e) = self.backend.write_mouse(&mouse, &current) {
                            tracing::warn!(error = %e, "cannot restore the club's mouse settings");
                        }
                    }
                    Ok(_) => {}
                    Err(e) => {
                        tracing::warn!(error = %e, "cannot read the mouse settings to restore them")
                    }
                }
            }
            if let Some(audio) = baseline.audio {
                match self.backend.audio_defaults() {
                    Ok(current) if current != audio => {
                        if let Err(e) = self.backend.set_audio_defaults(&audio, &current) {
                            tracing::warn!(error = %e, "cannot restore the club's audio output");
                        }
                    }
                    Ok(_) => {}
                    Err(e) => {
                        tracing::warn!(error = %e, "cannot read the audio outputs to restore them")
                    }
                }
            }
        }
        t.dirty = false;
        self.remove_file();
        tracing::info!(reason, "club PC settings restored");
        true
    }

    pub fn is_dirty(&self) -> bool {
        self.tracked.lock().dirty
    }

    fn capture(&self, boot_unix: u64) -> Baseline {
        Baseline {
            boot_unix,
            mouse: self
                .backend
                .read_mouse()
                .map_err(|e| tracing::warn!(error = %e, "cannot read the mouse settings"))
                .ok(),
            audio: self
                .backend
                .audio_defaults()
                .map_err(|e| tracing::warn!(error = %e, "cannot read the default audio outputs"))
                .ok(),
        }
    }

    fn load(&self) -> Option<Baseline> {
        let path = self.file.as_ref()?;
        let text = match std::fs::read_to_string(path) {
            Ok(text) => text,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return None,
            Err(e) => {
                tracing::warn!(path = %path.display(), error = %e, "baseline file not readable");
                return None;
            }
        };
        match serde_json::from_str(&text) {
            Ok(baseline) => Some(baseline),
            Err(e) => {
                tracing::warn!(path = %path.display(), error = %e, "baseline file invalid; ignored");
                self.remove_file();
                None
            }
        }
    }

    /// Best effort: without the file the restore still works until the Shell restarts.
    fn save(&self, baseline: &Baseline) {
        let Some(path) = self.file.as_ref() else {
            return;
        };
        let write = || -> std::io::Result<()> {
            if let Some(dir) = path.parent() {
                std::fs::create_dir_all(dir)?;
            }
            let json = serde_json::to_vec_pretty(baseline).map_err(std::io::Error::other)?;
            std::fs::write(path, json)
        };
        if let Err(e) = write() {
            tracing::warn!(path = %path.display(), error = %e, "cannot write the baseline file");
        }
    }

    fn remove_file(&self) {
        if let Some(path) = self.file.as_ref() {
            match std::fs::remove_file(path) {
                Ok(()) => {}
                Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
                Err(e) => {
                    tracing::warn!(path = %path.display(), error = %e, "cannot delete the baseline file")
                }
            }
        }
    }
}

#[cfg(test)]
pub(super) mod tests {
    use std::sync::Arc;

    use super::*;

    /// In-memory PC: what the Shell would read and write.
    #[derive(Default)]
    pub struct FakePc {
        pub mouse: Mutex<Option<MouseState>>,
        pub audio: Mutex<AudioDefaults>,
        pub writes: Mutex<u32>,
    }

    impl Backend for Arc<FakePc> {
        fn read_mouse(&self) -> CmdResult<MouseState> {
            self.mouse
                .lock()
                .ok_or_else(crate::state::ShellError::unsupported)
        }

        fn write_mouse(&self, next: &MouseState, _current: &MouseState) -> CmdResult<()> {
            *self.writes.lock() += 1;
            *self.mouse.lock() = Some(*next);
            Ok(())
        }

        fn audio_defaults(&self) -> CmdResult<AudioDefaults> {
            Ok(self.audio.lock().clone())
        }

        fn set_audio_defaults(
            &self,
            wanted: &AudioDefaults,
            _current: &AudioDefaults,
        ) -> CmdResult<()> {
            *self.writes.lock() += 1;
            *self.audio.lock() = wanted.clone();
            Ok(())
        }
    }

    const CLUB: MouseState = MouseState {
        speed: 10,
        params: [6, 10, 1],
        double_click_ms: 500,
    };

    fn pc() -> Arc<FakePc> {
        let pc = Arc::new(FakePc::default());
        *pc.mouse.lock() = Some(CLUB);
        *pc.audio.lock() = AudioDefaults::all("speakers");
        pc
    }

    fn temp_file(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "clubshell-pc-{name}-{}-{}",
            std::process::id(),
            uuid::Uuid::new_v4()
        ));
        dir.join("pc-baseline.json")
    }

    fn player_changes(tracker: &Tracker) {
        tracker
            .change(|b, _| {
                let current = b.read_mouse()?;
                b.write_mouse(
                    &MouseState {
                        speed: 18,
                        params: [0, 0, 0],
                        ..current
                    },
                    &current,
                )?;
                let audio = b.audio_defaults()?;
                b.set_audio_defaults(&AudioDefaults::all("headphones"), &audio)
            })
            .unwrap();
    }

    #[test]
    fn restore_puts_back_the_values_found_at_start() {
        let pc = pc();
        let file = temp_file("restore");
        let tracker = Tracker::new(Box::new(Arc::clone(&pc)), Some(file.clone()), 1_000);
        tracker.start();
        assert!(!tracker.is_dirty());
        assert!(!tracker.restore("logout"), "nothing changed: no restore");
        assert_eq!(*pc.writes.lock(), 0);

        player_changes(&tracker);
        assert!(tracker.is_dirty());
        assert!(
            file.exists(),
            "the baseline is on disk once the player changed something"
        );
        let on_disk: Baseline = serde_json::from_slice(&std::fs::read(&file).unwrap()).unwrap();
        assert_eq!(on_disk.mouse, Some(CLUB));
        assert_eq!(on_disk.audio, Some(AudioDefaults::all("speakers")));
        assert_eq!(on_disk.boot_unix, 1_000);

        assert!(tracker.restore("sessionEnded"));
        assert_eq!(*pc.mouse.lock(), Some(CLUB));
        assert_eq!(*pc.audio.lock(), AudioDefaults::all("speakers"));
        assert!(!file.exists(), "the file goes away with the restore");
        assert!(!tracker.is_dirty());
        assert!(
            !tracker.restore("logout"),
            "a second end of session is a no-op"
        );
        let _ = std::fs::remove_dir_all(file.parent().unwrap());
    }

    #[test]
    fn a_restart_in_the_same_boot_keeps_the_saved_baseline() {
        let pc = pc();
        let file = temp_file("restart");
        let first = Tracker::new(Box::new(Arc::clone(&pc)), Some(file.clone()), 5_000);
        first.start();
        player_changes(&first);
        drop(first); // the Shell crashed mid-session

        let second = Tracker::new(Box::new(Arc::clone(&pc)), Some(file.clone()), 5_030);
        second.start();
        assert!(second.is_dirty());
        assert_eq!(
            pc.mouse.lock().unwrap().speed,
            18,
            "the player's values stay for now"
        );
        assert!(second.restore("logout"));
        assert_eq!(
            *pc.mouse.lock(),
            Some(CLUB),
            "the club's values, not the player's"
        );
        assert_eq!(*pc.audio.lock(), AudioDefaults::all("speakers"));
        let _ = std::fs::remove_dir_all(file.parent().unwrap());
    }

    #[test]
    fn a_reboot_restores_at_start() {
        let pc = pc();
        let file = temp_file("reboot");
        let first = Tracker::new(Box::new(Arc::clone(&pc)), Some(file.clone()), 5_000);
        first.start();
        player_changes(&first);
        drop(first);

        let after_reboot = Tracker::new(Box::new(Arc::clone(&pc)), Some(file.clone()), 90_000);
        after_reboot.start();
        assert!(!after_reboot.is_dirty());
        assert!(!file.exists());
        assert_eq!(*pc.audio.lock(), AudioDefaults::all("speakers"));
        assert_eq!(*pc.mouse.lock(), Some(CLUB));
        let _ = std::fs::remove_dir_all(file.parent().unwrap());
    }

    #[test]
    fn a_new_file_is_stamped_with_the_current_boot() {
        let pc = pc();
        let file = temp_file("stamp");
        std::fs::create_dir_all(file.parent().unwrap()).unwrap();
        std::fs::write(
            &file,
            serde_json::to_vec(&Baseline {
                boot_unix: 1,
                mouse: Some(CLUB),
                audio: None,
            })
            .unwrap(),
        )
        .unwrap();
        let tracker = Tracker::new(Box::new(Arc::clone(&pc)), Some(file.clone()), 100_000);
        tracker.start(); // reboot: restored and cleaned
        player_changes(&tracker);
        let on_disk: Baseline = serde_json::from_slice(&std::fs::read(&file).unwrap()).unwrap();
        assert_eq!(
            on_disk.boot_unix, 100_000,
            "a restart later in this boot must not look like a reboot"
        );
        let _ = std::fs::remove_dir_all(file.parent().unwrap());
    }

    #[test]
    fn an_invalid_file_is_dropped_and_values_are_read_fresh() {
        let pc = pc();
        let file = temp_file("invalid");
        std::fs::create_dir_all(file.parent().unwrap()).unwrap();
        std::fs::write(&file, "{ not json").unwrap();
        let tracker = Tracker::new(Box::new(Arc::clone(&pc)), Some(file.clone()), 10);
        tracker.start();
        assert!(!tracker.is_dirty());
        assert!(!file.exists());
        let _ = std::fs::remove_dir_all(file.parent().unwrap());
    }

    #[test]
    fn without_a_file_the_restore_still_works() {
        let pc = pc();
        *pc.mouse.lock() = None; // unreadable at start
        let tracker = Tracker::new(Box::new(Arc::clone(&pc)), None, 10);
        tracker.start();
        *pc.mouse.lock() = Some(CLUB);
        player_changes(&tracker);
        assert!(tracker.restore("logout"));
        // The mouse was unknown at start: captured lazily before the first change.
        assert_eq!(*pc.mouse.lock(), Some(CLUB));
    }

    #[test]
    fn a_change_before_the_start_reading_still_restores_the_club_values() {
        let pc = pc();
        let file = temp_file("early");
        let tracker = Tracker::new(Box::new(Arc::clone(&pc)), Some(file.clone()), 7_000);
        player_changes(&tracker); // the background start() has not run yet
        tracker.start();
        assert!(tracker.is_dirty());
        assert!(tracker.restore("logout"));
        assert_eq!(*pc.mouse.lock(), Some(CLUB));
        assert_eq!(*pc.audio.lock(), AudioDefaults::all("speakers"));
        let _ = std::fs::remove_dir_all(file.parent().unwrap());
    }

    #[test]
    fn boot_tolerance() {
        assert!(same_boot(1_000, 1_000));
        assert!(same_boot(1_000, 1_100));
        assert!(same_boot(1_100, 1_000));
        assert!(!same_boot(1_000, 1_000 + SAME_BOOT_TOLERANCE_SEC + 1));
    }
}
