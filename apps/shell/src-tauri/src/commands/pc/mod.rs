//! `pc_*` commands (`TAURI_COMMANDS.md` §2.15, local, no IPC): the player's pointer settings ([`mouse`]), audio output
//! device ([`audio`]) and graphics-card vendor panel ([`gpu_panel`]), shown in Profile → Settings.
//!
//! They run in the Shell process because they belong to the player's logon session (SPI values, the vendor panel on the
//! player's desktop); the Agent lives in session 0 and can do neither. The kiosk profile is not reset between players,
//! so the club's values found at start are put back when the player leaves — `auth_logout`, `session.ended`,
//! `auth.expired` — and the vendor panel is closed ([`baseline`], [`restore_player_changes`]).
//!
//! Core Audio and app activation are COM: those calls run on a short-lived thread of their own ([`com_call`]), never on
//! Tauri's worker threads, which are not set up for COM and must not be left in an apartment.

pub mod audio;
pub mod baseline;
pub mod gpu_panel;
pub mod mouse;

use std::path::PathBuf;
use std::sync::{Arc, OnceLock};

use clubshell_protocol::commands::names::events as ev;
use tauri::State;
use tokio::sync::broadcast::error::RecvError;

use self::audio::{AudioDefaults, AudioOutputs};
use self::baseline::{NativeBackend, Tracker};
use self::gpu_panel::{GpuPanelInfo, GpuVendor, PanelHost};
use self::mouse::{MousePatch, MouseSettings};
use crate::kiosk::Kiosk;
use crate::state::{AppState, CmdResult, ShellError};

/// Baseline file under `%LOCALAPPDATA%` (the kiosk user can always write there).
const BASELINE_FILE: &str = r"ClubShell\pc-baseline.json";

/// Process-wide state of the `pc_*` commands.
pub struct PcRuntime {
    pub tracker: Tracker,
    pub panels: PanelHost,
}

static RUNTIME: OnceLock<PcRuntime> = OnceLock::new();

pub fn runtime() -> &'static PcRuntime {
    RUNTIME.get_or_init(|| PcRuntime {
        tracker: Tracker::new(
            Box::new(NativeBackend),
            baseline_path(),
            native::boot_unix(),
        ),
        panels: PanelHost::default(),
    })
}

fn baseline_path() -> Option<PathBuf> {
    std::env::var_os("LOCALAPPDATA")
        .filter(|v| !v.is_empty())
        .map(|dir| PathBuf::from(dir).join(BASELINE_FILE))
}

/// Called once from `setup`: remembers the club's values (or adopts the baseline a previous run left) and restores
/// them whenever the Agent reports the player gone. The reading runs in the background so a slow audio service never
/// holds the kiosk window back; a change that comes first captures the baseline itself ([`Tracker::change`]).
pub fn spawn(state: &AppState) {
    spawn_com("baseline", || runtime().tracker.start());
    let state = state.clone();
    tauri::async_runtime::spawn(async move {
        let mut events = state.agent.events();
        loop {
            match events.recv().await {
                Ok(env) if env.name == ev::SESSION_ENDED || env.name == ev::AUTH_EXPIRED => {
                    restore_player_changes(if env.name == ev::SESSION_ENDED {
                        "sessionEnded"
                    } else {
                        "authExpired"
                    });
                }
                Ok(_) => {}
                Err(RecvError::Lagged(n)) => tracing::warn!(skipped = n, "pc bridge lagged"),
                Err(RecvError::Closed) => break,
            }
        }
    });
}

/// The player left (logout, end of session): closes the vendor panel and puts the club's values back, off the
/// caller's thread. Idempotent: nothing happens when nothing changed.
pub fn restore_player_changes(reason: &'static str) {
    runtime().panels.close();
    spawn_com("restore", move || {
        runtime().tracker.restore(reason);
    });
}

// ───────────────────────────── commands ─────────────────────────────

/// Pointer and panel changes are a player's: refused without one, so the restore at logout always covers them.
fn require_player(state: &AppState) -> CmdResult<()> {
    if state.user().is_some() {
        Ok(())
    } else {
        Err(ShellError::forbidden("needs a signed-in player"))
    }
}

/// `pc_mouse_get`.
#[tauri::command]
pub async fn pc_mouse_get() -> CmdResult<MouseSettings> {
    blocking(|| {
        runtime()
            .tracker
            .read(|b| b.read_mouse())
            .map(|m| m.settings())
    })
    .await
}

/// `pc_mouse_set`: speed 1–20, double-click 200–900 ms (`validation` otherwise); returns the values now in effect.
#[tauri::command]
pub async fn pc_mouse_set(
    state: State<'_, AppState>,
    patch: MousePatch,
) -> CmdResult<MouseSettings> {
    patch.validate()?;
    require_player(&state)?;
    blocking(move || {
        runtime().tracker.change(|backend, club| {
            let current = backend.read_mouse()?;
            let next = current.patched(&patch, club.mouse.as_ref());
            if next != current {
                backend.write_mouse(&next, &current)?;
                tracing::info!(
                    speed = next.speed,
                    precision = next.settings().enhance_precision,
                    double_click_ms = next.double_click_ms,
                    "mouse settings changed"
                );
            }
            Ok(next.settings())
        })
    })
    .await
}

/// `pc_audio_outputs`: active outputs, the default one marked.
#[tauri::command]
pub async fn pc_audio_outputs() -> CmdResult<AudioOutputs> {
    com_call("audio", || {
        runtime().tracker.read(|_| audio::native::list())
    })
    .await
}

/// `pc_audio_set_output`: makes `deviceId` the default output for every role; `notFound` for an unknown or inactive
/// device, `forbidden` when this PC cannot switch (`canSwitch: false`).
#[tauri::command]
pub async fn pc_audio_set_output(
    state: State<'_, AppState>,
    device_id: String,
) -> CmdResult<AudioOutputs> {
    require_player(&state)?;
    com_call("audio", move || {
        let list = audio::native::list()?;
        let id = audio::validate_device_id(&device_id, &list.devices)?;
        if !list.can_switch {
            return Err(ShellError::forbidden(
                "the default audio device cannot be changed on this PC",
            ));
        }
        runtime().tracker.change(|backend, _| {
            let current = backend.audio_defaults()?;
            let wanted = AudioDefaults::all(&id);
            if wanted != current {
                backend.set_audio_defaults(&wanted, &current)?;
                tracing::info!(device = %id, "default audio output changed");
            }
            Ok(())
        })?;
        audio::native::list()
    })
    .await
}

/// `pc_gpu_panels`: vendor panels installed for the kiosk user (the UI matches them against `sys_hardware`'s GPUs).
#[tauri::command]
pub async fn pc_gpu_panels() -> CmdResult<Vec<GpuPanelInfo>> {
    blocking(|| Ok(gpu_panel::installed_here())).await
}

/// `pc_gpu_panel_open`: `vendor` ∈ `nvidia` | `amd` | `intel`. Pauses the kiosk guard and opens the panel; the Shell
/// comes back on top when its window closes. `notFound` when that panel is not installed, `forbidden` without a player
/// or while the PC is locked.
#[tauri::command]
pub async fn pc_gpu_panel_open(
    state: State<'_, AppState>,
    kiosk: State<'_, Arc<Kiosk>>,
    vendor: String,
) -> CmdResult<()> {
    let vendor = GpuVendor::parse(&vendor)
        .ok_or_else(|| ShellError::validation("vendor", "must be nvidia, amd or intel"))?;
    require_player(&state)?;
    if kiosk.is_locked() {
        return Err(ShellError::forbidden("the PC is locked"));
    }
    // The club's switch (`features.gpuPanel`, off by default), checked here too and not only by the hidden button.
    let settings: serde_json::Value = state
        .agent
        .request(clubshell_protocol::commands::names::settings::GET, &())
        .await?;
    if settings["features"]["gpuPanel"] != serde_json::Value::Bool(true) {
        return Err(ShellError::forbidden(
            "the club turned the graphics panel off",
        ));
    }
    let launch = blocking(move || Ok(gpu_panel::resolve_here(vendor)))
        .await?
        .ok_or_else(|| ShellError::not_found("graphics panel"))?;
    let kiosk = Arc::clone(&kiosk);
    kiosk.begin_external_window();
    match com_call("gpu-panel", move || gpu_panel::native::launch(&launch)).await {
        Ok(pid) => {
            runtime().panels.opened(kiosk, vendor, pid);
            Ok(())
        }
        Err(e) => {
            tracing::warn!(?vendor, error = %e, "graphics panel did not start");
            runtime().panels.launch_failed(&kiosk);
            Err(e)
        }
    }
}

// ───────────────────────────── threads ─────────────────────────────

async fn blocking<T: Send + 'static>(
    f: impl FnOnce() -> CmdResult<T> + Send + 'static,
) -> CmdResult<T> {
    tauri::async_runtime::spawn_blocking(f)
        .await
        .map_err(|e| ShellError::internal(format!("worker failed: {e}")))?
}

/// Runs `f` on a new COM-initialised thread and awaits its result.
async fn com_call<T: Send + 'static>(
    what: &'static str,
    f: impl FnOnce() -> CmdResult<T> + Send + 'static,
) -> CmdResult<T> {
    let (tx, rx) = tokio::sync::oneshot::channel();
    std::thread::Builder::new()
        .name(format!("pc-{what}"))
        .spawn(move || {
            let _com = ComGuard::init();
            let _ = tx.send(f());
        })
        .map_err(|e| ShellError::internal(format!("cannot start the {what} thread: {e}")))?;
    rx.await
        .map_err(|_| ShellError::internal(format!("the {what} thread ended without an answer")))?
}

/// Fire-and-forget [`com_call`].
pub(crate) fn spawn_com(what: &'static str, f: impl FnOnce() + Send + 'static) {
    let spawned = std::thread::Builder::new()
        .name(format!("pc-{what}"))
        .spawn(move || {
            let _com = ComGuard::init();
            f();
        });
    if let Err(e) = spawned {
        tracing::warn!(what, error = %e, "cannot start a COM thread");
    }
}

/// COM apartment (STA) of the current thread for the guard's lifetime.
pub(crate) struct ComGuard {
    #[cfg_attr(not(windows), allow(dead_code))]
    initialized: bool,
}

#[cfg(windows)]
impl ComGuard {
    pub(crate) fn init() -> Self {
        use windows::Win32::System::Com::{
            CoInitializeEx, COINIT_APARTMENTTHREADED, COINIT_DISABLE_OLE1DDE,
        };
        // SAFETY: balanced by CoUninitialize in Drop when it succeeded (S_OK or S_FALSE).
        let hr = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE) };
        Self {
            initialized: hr.is_ok(),
        }
    }
}

#[cfg(windows)]
impl Drop for ComGuard {
    fn drop(&mut self) {
        if self.initialized {
            // SAFETY: matches the successful CoInitializeEx of this thread.
            unsafe { windows::Win32::System::Com::CoUninitialize() };
        }
    }
}

#[cfg(not(windows))]
impl ComGuard {
    pub(crate) fn init() -> Self {
        Self { initialized: false }
    }
}

#[cfg(windows)]
mod native {
    /// Boot time in Unix seconds (now − uptime).
    pub fn boot_unix() -> u64 {
        // SAFETY: no preconditions.
        let uptime_ms = unsafe { windows::Win32::System::SystemInformation::GetTickCount64() };
        super::now_unix().saturating_sub(uptime_ms / 1000)
    }
}

#[cfg(not(windows))]
mod native {
    pub fn boot_unix() -> u64 {
        0
    }
}

fn now_unix() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn baseline_lives_in_local_app_data() {
        if let Some(path) = baseline_path() {
            assert!(
                path.ends_with(r"ClubShell\pc-baseline.json") || path.ends_with("pc-baseline.json")
            );
        }
        assert!(native::boot_unix() <= now_unix());
    }

    #[test]
    fn com_calls_answer_from_their_own_thread() {
        let caller = std::thread::current().id();
        let answer = tauri::async_runtime::block_on(com_call("test", move || {
            Ok(std::thread::current().id() != caller)
        }));
        assert_eq!(answer, Ok(true));
        let failed: CmdResult<()> =
            tauri::async_runtime::block_on(com_call("test", || Err(ShellError::not_found("x"))));
        assert_eq!(
            failed.unwrap_err().code,
            clubshell_protocol::error::ErrorCode::NotFound
        );
    }
}
