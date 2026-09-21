//! Kiosk hardening (`ARCHITECTURE.md` §6.2 step 3, §5.2; `TAURI_COMMANDS.md` §2.14 / §3.2).
//!
//! One [`Kiosk`] owns every native guard — keyboard hook, Alt+Tab/topmost guard, taskbar, window
//! guard, overlay, multi-monitor, idle detector, gamepad, tray — implements [`KioskControl`] for the
//! agent event pipeline (`lock`/`unlock`, game mode, shutdown), applies `policy.explorer` /
//! `policy.kiosk` updates, and bridges Agent events that need native handling (game pids, ads /
//! admin toasts over a running game, remote-control indicator).
//!
//! Dev mode (`CLUBSHELL_DEV=1` or a debug build) disables all hardening: the main window becomes a
//! normal window, nothing is blocked, no secondary windows are created; hotkeys still work while the
//! Shell window is focused (F11 toggles fullscreen) and everything is logged.
//!
//! Wiring (`lib.rs`): `let kiosk = kiosk::spawn_all(&handle, &state)?; app.manage(kiosk);` — commands
//! then take `tauri::State<'_, Arc<Kiosk>>` for everything beyond the [`KioskControl`] seam.

pub mod alt_tab;
pub mod commands;
pub mod idle_detector;
pub mod keyboard_hook;
pub mod multi_monitor;
pub mod overlay;
pub mod taskbar;
pub mod window_guard;

use std::process::{Child, Command};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Duration;

use clubshell_protocol::commands::names::events as ev;
use clubshell_protocol::events::{
    GameStateChanged, PolicyChanged, RemoteControlEvent, RemoteControlState, ShellCommand,
    ShellCommandKind, ShowMessageArgs,
};
use clubshell_protocol::games::GameState;
use clubshell_protocol::ipc::IpcEnvelope;
use clubshell_protocol::pc::{ExplorerPolicy, Policy};
use clubshell_winutil::input::clip_cursor;
use parking_lot::Mutex;
use serde::Serialize;
use tauri::async_runtime::JoinHandle;
use tauri::{AppHandle, Emitter};
use tokio::sync::broadcast::error::RecvError;
use tokio::sync::mpsc;

pub use alt_tab::AltTabBlocker;
pub use commands::KioskState;
pub use idle_detector::{ActivityFeed, IdleDetector, IdleStage, IdleStatus, IdleThresholds};
pub use keyboard_hook::{
    HookPolicy, Hotkey, HotkeyKind, HotkeyPayload, KeyboardHook, HOTKEY_EVENT,
};
pub use multi_monitor::{MonitorChangeReason, MonitorChangedEvent, MonitorDto, MultiMonitor};
pub use overlay::{Overlay, OverlayEvent, OverlayKind};
pub use taskbar::Taskbar;
pub use window_guard::{FocusEvent, WindowGuard};

use crate::config::ShellConfig;
use crate::gamepad::GamepadService;
use crate::state::{AppState, CmdResult, KioskControl, ShellError};
use crate::tray::Tray;

/// How often the bridge re-checks the admin unlock to show/hide the tray.
pub const ADMIN_MODE_POLL: Duration = Duration::from_secs(5);
/// Overlay TTL for admin messages / session warnings shown over a game.
pub const TOAST_TTL: Duration = Duration::from_secs(15);
/// Path of the Windows touch keyboard host.
pub const TABTIP_RELATIVE: &str = r"microsoft shared\ink\TabTip.exe";

/// `true` when hardening must stay off (`CLUBSHELL_DEV=1` or a debug build).
pub fn dev_mode(config: &ShellConfig) -> bool {
    config.is_dev() || cfg!(debug_assertions)
}

/// Native part of `kiosk_state` (`TAURI_COMMANDS.md` §2.14); the command adds `agentConnected`,
/// `version` and `devtools`.
#[derive(Serialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct KioskSnapshot {
    pub fullscreen: bool,
    pub guard_active: bool,
    pub hooks_active: bool,
    pub monitors: Vec<MonitorDto>,
    pub idle: bool,
    pub idle_sec: u64,
    pub gamepad_connected: bool,
    pub locked: bool,
    pub game_mode: bool,
    pub overlay: OverlayKind,
    pub dev: bool,
}

/// Owner of all native kiosk guards; see the module docs.
pub struct Kiosk {
    app: AppHandle,
    config: ShellConfig,
    dev: bool,
    base_policy: HookPolicy,
    keyboard: KeyboardHook,
    alt_tab: AltTabBlocker,
    taskbar: Arc<Taskbar>,
    window: Arc<WindowGuard>,
    overlay: Arc<Overlay>,
    monitors: Arc<MultiMonitor>,
    idle: Arc<IdleDetector>,
    gamepad: Arc<GamepadService>,
    tray: Mutex<Option<Tray>>,
    tabtip: Mutex<Option<Child>>,
    locked: AtomicBool,
    game_mode: AtomicBool,
    /// `kiosk_set_guard` state, applied whenever no game runs.
    guard_wanted: AtomicBool,
    admin_mode: AtomicBool,
    shut: AtomicBool,
    tasks: Mutex<Vec<JoinHandle<()>>>,
}

/// Starts the kiosk, installs it as the [`KioskControl`] of `state`, attaches the tray and the
/// Agent event bridge. Call once from `setup`.
pub fn spawn_all(app: &AppHandle, state: &AppState) -> anyhow::Result<Arc<Kiosk>> {
    let kiosk = Kiosk::start(app, &state.config, Vec::new())?;
    let control: Arc<dyn KioskControl> = kiosk.clone();
    state.set_kiosk(control);
    match Tray::install(app, state.clone()) {
        Ok(tray) => *kiosk.tray.lock() = Some(tray),
        Err(e) => tracing::warn!(error = %e, "tray unavailable"),
    }
    let bridge = spawn_bridge(Arc::clone(&kiosk), state.clone());
    kiosk.tasks.lock().push(bridge);
    Ok(kiosk)
}

impl Kiosk {
    /// Builds every guard from `config`; `policy_combos` are extra blocked chords from a cached policy
    /// (`policy.explorer.blockedKeyCombos`), usually empty until `policy.changed` arrives.
    pub fn start(
        app: &AppHandle,
        config: &ShellConfig,
        policy_combos: Vec<String>,
    ) -> anyhow::Result<Arc<Self>> {
        let dev = dev_mode(config);
        if dev {
            tracing::warn!(
                env_dev = config.is_dev(),
                debug = cfg!(debug_assertions),
                "DEV MODE: kiosk hardening disabled"
            );
        }
        let monitors = MultiMonitor::start(app, config, dev);
        let window = WindowGuard::start(app, config, dev, monitors.primary_rect())?;
        let alt_tab = AltTabBlocker::start(window.hwnd(), config.kiosk.topmost_guard, dev);
        let feed = ActivityFeed::new();
        let (hotkey_tx, hotkey_rx) = mpsc::unbounded_channel();
        let base_policy = HookPolicy::from_config(&config.kiosk, &policy_combos);
        let keyboard = KeyboardHook::install(
            base_policy.clone(),
            keyboard_hook::default_hotkeys(&config.kiosk, dev),
            Arc::clone(&feed),
            hotkey_tx,
            dev,
            window.hwnd(),
        );
        let taskbar = Taskbar::start(config.kiosk.hide_taskbar && !dev);
        let overlay = Overlay::new(app, monitors.virtual_rect());
        let idle = IdleDetector::start(app, &config.idle, feed);
        let gamepad = GamepadService::start(app, &config.gamepad);

        let kiosk = Arc::new(Self {
            app: app.clone(),
            config: config.clone(),
            dev,
            base_policy,
            keyboard,
            alt_tab,
            taskbar,
            window,
            overlay,
            monitors,
            idle,
            gamepad,
            tray: Mutex::new(None),
            tabtip: Mutex::new(None),
            locked: AtomicBool::new(false),
            game_mode: AtomicBool::new(false),
            guard_wanted: AtomicBool::new(!dev),
            admin_mode: AtomicBool::new(dev),
            shut: AtomicBool::new(false),
            tasks: Mutex::new(Vec::new()),
        });

        // Display changes: main window back to the primary, overlay spans the new virtual screen,
        // taskbar re-hidden (Explorer re-shows it on WM_DISPLAYCHANGE).
        {
            let (window, overlay, taskbar) = (
                Arc::clone(&kiosk.window),
                Arc::clone(&kiosk.overlay),
                Arc::clone(&kiosk.taskbar),
            );
            let preferred = config.monitors.primary_index;
            kiosk.monitors.on_change(move |list| {
                if let Some(primary) = multi_monitor::primary_of(list, preferred) {
                    if let Err(e) = window.move_to_monitor(primary.rect) {
                        tracing::warn!(error = %e, "cannot re-place the main window after a display change");
                    }
                }
                if let Some(rect) = multi_monitor::union_rect(list) {
                    overlay.set_bounds(rect);
                }
                taskbar.rehide();
            });
        }
        let consumer = spawn_hotkey_consumer(app.clone(), Arc::clone(&kiosk.window), hotkey_rx);
        kiosk.tasks.lock().push(consumer);
        tracing::info!(
            dev,
            hooks = kiosk.keyboard.is_installed(),
            guard = kiosk.alt_tab.is_enabled(),
            taskbar_hidden = kiosk.taskbar.is_hidden(),
            monitors = kiosk.monitors.count(),
            "kiosk started"
        );
        Ok(kiosk)
    }

    // ───────────────────────────── accessors ─────────────────────────────

    pub fn app(&self) -> &AppHandle {
        &self.app
    }

    pub fn config(&self) -> &ShellConfig {
        &self.config
    }

    pub fn is_dev(&self) -> bool {
        self.dev
    }

    pub fn keyboard(&self) -> &KeyboardHook {
        &self.keyboard
    }

    pub fn alt_tab(&self) -> &AltTabBlocker {
        &self.alt_tab
    }

    pub fn taskbar(&self) -> &Arc<Taskbar> {
        &self.taskbar
    }

    pub fn window(&self) -> &Arc<WindowGuard> {
        &self.window
    }

    pub fn overlay(&self) -> &Arc<Overlay> {
        &self.overlay
    }

    pub fn monitors(&self) -> &Arc<MultiMonitor> {
        &self.monitors
    }

    pub fn idle(&self) -> &Arc<IdleDetector> {
        &self.idle
    }

    pub fn gamepad(&self) -> &Arc<GamepadService> {
        &self.gamepad
    }

    pub fn is_locked(&self) -> bool {
        self.locked.load(Ordering::Acquire)
    }

    pub fn is_game_mode(&self) -> bool {
        self.game_mode.load(Ordering::Acquire)
    }

    /// `kiosk_state.guardActive`.
    pub fn guard_active(&self) -> bool {
        self.alt_tab.is_enabled() && self.window.is_active()
    }

    /// `kiosk_state.hooksActive`.
    pub fn hooks_active(&self) -> bool {
        self.keyboard.is_installed() && !self.dev
    }

    /// Native half of `kiosk_state`.
    pub fn snapshot(&self) -> KioskSnapshot {
        let idle = self.idle.status();
        KioskSnapshot {
            fullscreen: self.window.is_fullscreen(),
            guard_active: self.guard_active(),
            hooks_active: self.hooks_active(),
            monitors: self.monitors.dtos(),
            idle: idle.idle,
            idle_sec: idle.idle_sec,
            gamepad_connected: self.gamepad.any_connected(),
            locked: self.is_locked(),
            game_mode: self.is_game_mode(),
            overlay: self.overlay.kind(),
            dev: self.dev,
        }
    }

    // ───────────────────────────── controls ─────────────────────────────

    /// `kiosk_set_guard`: topmost/foreground guard + stray-window sweep on/off (the caller checks the
    /// admin token / running game). Remembered and re-applied when a game exits.
    pub fn set_guard(&self, active: bool) {
        self.guard_wanted.store(active, Ordering::Release);
        let effective = active && !self.is_game_mode();
        self.alt_tab.set_enabled(effective);
        self.window.set_active(effective);
    }

    /// Applies an `ExplorerPolicy` on top of `shell.json` (chords, Alt+Tab / Win flags, taskbar).
    pub fn update_policy(&self, explorer: &ExplorerPolicy) {
        let mut policy = self.base_policy.clone();
        policy.merge_explorer(explorer);
        self.keyboard.set_policy(&policy);
        if !self.dev {
            self.taskbar
                .set_hidden(self.config.kiosk.hide_taskbar || explorer.hide_taskbar);
        }
        tracing::info!(
            alt_tab = policy.block_alt_tab,
            win = policy.block_win_key,
            extra = policy.extra.len(),
            "explorer policy applied"
        );
    }

    /// `policy.changed` / `policy_get`: explorer section + `kiosk.idleTimeoutSec`.
    pub fn apply_policy(&self, policy: &Policy) {
        self.update_policy(&policy.explorer);
        self.idle
            .set_timeout(u32::try_from(policy.kiosk.idle_timeout_sec).unwrap_or(0));
    }

    /// Admin mode (unexpired `sys.unlockAdmin`): shows the tray. Dev mode is always admin mode.
    pub fn set_admin_mode(&self, on: bool) {
        let on = on || self.dev;
        if self.admin_mode.swap(on, Ordering::AcqRel) != on {
            tracing::info!(admin = on, "admin mode");
        }
        if let Some(tray) = self.tray.lock().as_ref() {
            tray.set_visible(on);
        }
    }

    pub fn is_admin_mode(&self) -> bool {
        self.admin_mode.load(Ordering::Acquire)
    }

    /// `kiosk_virtual_keyboard`: starts `TabTip.exe` (allow-listed for the foreground) or kills it.
    pub fn virtual_keyboard(&self, show: bool) -> CmdResult<()> {
        let mut slot = self.tabtip.lock();
        if !show {
            if let Some(mut child) = slot.take() {
                self.window.disallow_foreground_pid(child.id());
                let _ = child.kill();
                let _ = child.wait();
            }
            return Ok(());
        }
        if let Some(child) = slot.as_mut() {
            match child.try_wait() {
                Ok(Some(_)) | Err(_) => {
                    self.window.disallow_foreground_pid(child.id());
                    *slot = None;
                }
                Ok(None) => return Ok(()),
            }
        }
        if !cfg!(windows) {
            return Err(ShellError::unsupported());
        }
        let common = std::env::var("CommonProgramFiles")
            .unwrap_or_else(|_| r"C:\Program Files\Common Files".to_owned());
        let path = std::path::Path::new(&common).join(TABTIP_RELATIVE);
        let child = Command::new(&path)
            .spawn()
            .map_err(|e| ShellError::internal(format!("cannot start {}: {e}", path.display())))?;
        self.window.allow_foreground_pid(child.id());
        tracing::info!(pid = child.id(), "virtual keyboard started");
        *slot = Some(child);
        Ok(())
    }

    /// `kiosk_exit`: tears everything down, optionally starts `explorer.exe`, exits the process.
    pub fn exit(&self, start_explorer: bool) {
        tracing::warn!(start_explorer, "kiosk exit");
        self.shutdown();
        if start_explorer {
            match Command::new("explorer.exe").spawn() {
                Ok(child) => tracing::info!(pid = child.id(), "explorer.exe started"),
                Err(e) => tracing::warn!(error = %e, "cannot start explorer.exe"),
            }
        }
        self.app.exit(0);
    }

    // ───────────────────────────── event bridge ─────────────────────────────

    /// Native side effects of Agent events (the webview forwarding is done by `EventForwarder`).
    pub fn on_agent_event(&self, state: &AppState, env: &IpcEnvelope) {
        match env.name.as_str() {
            ev::POLICY_CHANGED => match env.payload_as::<PolicyChanged>() {
                Ok(Some(changed)) => self.apply_policy(&changed.policy),
                Ok(None) => {}
                Err(e) => tracing::warn!(error = %e, "policy.changed payload malformed"),
            },
            ev::GAME_STATE_CHANGED => {
                if let Ok(Some(change)) = env.payload_as::<GameStateChanged>() {
                    match change.state {
                        GameState::Launching | GameState::Running => {
                            if let Some(pid) = change.pid.and_then(|p| u32::try_from(p).ok()) {
                                self.window.allow_foreground_pid(pid);
                            }
                        }
                        GameState::Exited | GameState::Failed | GameState::Killed => {
                            self.window.clear_allowed_pids()
                        }
                    }
                }
            }
            ev::SHELL_COMMAND => {
                if let Ok(Some(command)) = env.payload_as::<ShellCommand>() {
                    self.on_shell_command(state, command);
                }
            }
            ev::ADMIN_MESSAGE | ev::SESSION_WARNING => {
                if state.game_running() {
                    self.toast(env.payload.clone(), Some(TOAST_TTL));
                }
            }
            ev::ADMIN_REMOTE_CONTROL => {
                if let Ok(Some(remote)) = env.payload_as::<RemoteControlEvent>() {
                    if remote.show_indicator && state.game_running() {
                        match remote.state {
                            RemoteControlState::Started => self.toast(env.payload.clone(), None),
                            RemoteControlState::Stopped => {
                                self.overlay.hide_unless(OverlayKind::Lock)
                            }
                        }
                    }
                }
            }
            _ => {}
        }
    }

    fn on_shell_command(&self, state: &AppState, command: ShellCommand) {
        if !state.game_running() {
            return; // the React screens handle it
        }
        match command.command {
            ShellCommandKind::ShowAds => {
                let ttl = Duration::from_secs(u64::from(self.config.ads.duration_sec.max(1)));
                self.show_overlay(OverlayKind::Ads, command.args, Some(ttl));
            }
            ShellCommandKind::ShowMessage => {
                let ttl = command
                    .args
                    .clone()
                    .and_then(|a| serde_json::from_value::<ShowMessageArgs>(a).ok())
                    .and_then(|a| a.ttl_sec)
                    .and_then(|s| u64::try_from(s).ok())
                    .map_or(TOAST_TTL, Duration::from_secs);
                self.show_overlay(OverlayKind::Message, command.args, Some(ttl));
            }
            ShellCommandKind::Lock | ShellCommandKind::Unlock | ShellCommandKind::Reboot => {}
        }
    }

    fn toast(&self, payload: Option<serde_json::Value>, ttl: Option<Duration>) {
        if self.is_locked() {
            return;
        }
        self.show_overlay(OverlayKind::Message, payload, ttl);
    }

    fn show_overlay(
        &self,
        kind: OverlayKind,
        payload: Option<serde_json::Value>,
        ttl: Option<Duration>,
    ) {
        if let Err(e) = self.overlay.show(kind, payload, ttl) {
            tracing::warn!(?kind, error = %e, "overlay show failed");
        }
    }

    fn refresh_admin_mode(&self, state: &AppState) {
        self.set_admin_mode(state.has_admin_unlock());
    }
}

impl KioskControl for Kiosk {
    fn set_locked(&self, locked: bool) {
        if self.locked.swap(locked, Ordering::AcqRel) == locked {
            return;
        }
        tracing::info!(locked, "kiosk lock");
        self.window.set_locked(locked);
        if !self.dev {
            self.keyboard.set_lock_all(locked);
            let clip = locked.then(|| self.monitors.primary_rect());
            if let Err(e) = clip_cursor(clip) {
                tracing::debug!(error = %e, "clip_cursor failed");
            }
        }
        let result = if locked {
            self.overlay.show(OverlayKind::Lock, None, None)
        } else {
            self.overlay.hide()
        };
        if let Err(e) = result {
            tracing::warn!(locked, error = %e, "lock overlay failed");
        }
    }

    fn set_game_mode(&self, on: bool) {
        if self.game_mode.swap(on, Ordering::AcqRel) == on {
            return;
        }
        tracing::info!(on, "game mode");
        self.keyboard.set_game_mode(on);
        let guard = !on && self.guard_wanted.load(Ordering::Acquire);
        self.alt_tab.set_enabled(guard);
        self.window.set_active(guard);
        self.window.set_game_mode(on);
        self.idle.set_paused(on);
        if !on {
            self.overlay.hide_unless(OverlayKind::Lock);
            self.window.clear_allowed_pids();
        }
    }

    fn shutdown(&self) {
        if self.shut.swap(true, Ordering::AcqRel) {
            return;
        }
        tracing::info!("kiosk shutdown");
        for task in self.tasks.lock().drain(..) {
            task.abort();
        }
        self.keyboard.uninstall();
        let _ = clip_cursor(None);
        self.alt_tab.shutdown();
        self.window.shutdown();
        self.overlay.shutdown();
        self.monitors.shutdown();
        self.taskbar.restore();
        self.idle.stop();
        self.gamepad.stop();
        if let Some(tray) = self.tray.lock().as_ref() {
            tray.remove();
        }
        if let Some(mut child) = self.tabtip.lock().take() {
            let _ = child.kill();
        }
    }
}

/// Hotkey channel → `kiosk://hotkey` (dev fullscreen toggle handled natively).
fn spawn_hotkey_consumer(
    app: AppHandle,
    window: Arc<WindowGuard>,
    mut rx: mpsc::UnboundedReceiver<keyboard_hook::HotkeyEvent>,
) -> JoinHandle<()> {
    tauri::async_runtime::spawn(async move {
        while let Some(event) = rx.recv().await {
            if event.kind.is_internal() {
                if let Err(e) = window.toggle_fullscreen() {
                    tracing::warn!(error = %e, "fullscreen toggle failed");
                }
                continue;
            }
            match event.kind {
                HotkeyKind::Blocked => tracing::debug!(combo = %event.combo, "blocked chord"),
                kind => tracing::info!(name = kind.wire_name(), combo = %event.combo, "hotkey"),
            }
            let payload = HotkeyPayload {
                name: event.kind.wire_name(),
                combo: event.combo,
            };
            if let Err(e) = app.emit(HOTKEY_EVENT, payload) {
                tracing::warn!(error = %e, "cannot emit kiosk://hotkey");
            }
        }
    })
}

/// Agent events → native side effects, plus the periodic admin-mode refresh.
fn spawn_bridge(kiosk: Arc<Kiosk>, state: AppState) -> JoinHandle<()> {
    tauri::async_runtime::spawn(async move {
        let mut events = state.agent.events();
        let mut tick = tokio::time::interval(ADMIN_MODE_POLL);
        tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
        loop {
            tokio::select! {
                next = events.recv() => match next {
                    Ok(env) => kiosk.on_agent_event(&state, &env),
                    Err(RecvError::Lagged(n)) => tracing::warn!(skipped = n, "kiosk bridge lagged"),
                    Err(RecvError::Closed) => break,
                },
                _ = tick.tick() => kiosk.refresh_admin_mode(&state),
            }
        }
        tracing::info!("kiosk bridge stopped");
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn snapshot_wire_shape() {
        let snapshot = KioskSnapshot {
            fullscreen: true,
            guard_active: true,
            hooks_active: false,
            monitors: vec![],
            idle: false,
            idle_sec: 3,
            gamepad_connected: false,
            locked: false,
            game_mode: false,
            overlay: OverlayKind::Hidden,
            dev: true,
        };
        let json = serde_json::to_value(snapshot).unwrap();
        assert_eq!(json["guardActive"], true);
        assert_eq!(json["idleSec"], 3);
        assert_eq!(json["overlay"], "none");
        assert_eq!(json["gamepadConnected"], false);
    }

    #[test]
    fn dev_mode_follows_env_or_debug_build() {
        let mut cfg = ShellConfig::defaults();
        cfg.runtime.dev = true;
        assert!(dev_mode(&cfg));
        cfg.runtime.dev = false;
        assert_eq!(dev_mode(&cfg), cfg!(debug_assertions));
    }
}
