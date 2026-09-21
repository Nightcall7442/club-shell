//! Gamepad input (`ARCHITECTURE.md` §5.2): a dedicated thread polls `gilrs` every `gamepad.pollMs`
//! and emits `kiosk://gamepad` (`TAURI_COMMANDS.md` §3.2) for button edges, axis changes beyond the
//! dead-zone and connect/disconnect. With `gamepad.navigation` the left stick is turned into
//! `up`/`down`/`left`/`right` presses and held directions auto-repeat, so the UI can navigate with a
//! pad the same way it does with the D-pad. `kiosk_gamepad_state` reads the last snapshot.

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use gilrs::{Axis, Button, Event, EventType, Gamepad, Gilrs};
use parking_lot::Mutex;
use serde::Serialize;
use tauri::{AppHandle, Emitter};

use crate::config::GamepadConfig;

/// Event name (`TAURI_COMMANDS.md` §3.2).
pub const GAMEPAD_EVENT: &str = "kiosk://gamepad";
/// Held direction: first repeat after this delay …
pub const REPEAT_DELAY: Duration = Duration::from_millis(400);
/// … then one repeat per interval.
pub const REPEAT_INTERVAL: Duration = Duration::from_millis(120);
/// Left-stick navigation hysteresis.
const STICK_PRESS: f32 = 0.6;
const STICK_RELEASE: f32 = 0.4;
/// Poll period while disabled (events are drained and dropped).
const DISABLED_POLL: Duration = Duration::from_millis(250);

/// `GamepadButton` on the wire; the discriminant is the bit in `GamepadState.buttons`.
#[derive(Serialize, Clone, Copy, Debug, PartialEq, Eq, Hash)]
#[serde(rename_all = "lowercase")]
pub enum GamepadButton {
    A,
    B,
    X,
    Y,
    Lb,
    Rb,
    Back,
    Start,
    Ls,
    Rs,
    Up,
    Down,
    Left,
    Right,
    Guide,
}

impl GamepadButton {
    pub const ALL: [Self; 15] = [
        Self::A,
        Self::B,
        Self::X,
        Self::Y,
        Self::Lb,
        Self::Rb,
        Self::Back,
        Self::Start,
        Self::Ls,
        Self::Rs,
        Self::Up,
        Self::Down,
        Self::Left,
        Self::Right,
        Self::Guide,
    ];

    /// Bit in `GamepadState.buttons`.
    pub const fn bit(self) -> u32 {
        1 << (self as u32)
    }

    pub const fn is_direction(self) -> bool {
        matches!(self, Self::Up | Self::Down | Self::Left | Self::Right)
    }

    fn from_gilrs(button: Button) -> Option<Self> {
        Some(match button {
            Button::South => Self::A,
            Button::East => Self::B,
            Button::West => Self::X,
            Button::North => Self::Y,
            Button::LeftTrigger => Self::Lb,
            Button::RightTrigger => Self::Rb,
            Button::Select => Self::Back,
            Button::Start => Self::Start,
            Button::LeftThumb => Self::Ls,
            Button::RightThumb => Self::Rs,
            Button::DPadUp => Self::Up,
            Button::DPadDown => Self::Down,
            Button::DPadLeft => Self::Left,
            Button::DPadRight => Self::Right,
            Button::Mode => Self::Guide,
            _ => return None,
        })
    }

    fn to_gilrs(self) -> Button {
        match self {
            Self::A => Button::South,
            Self::B => Button::East,
            Self::X => Button::West,
            Self::Y => Button::North,
            Self::Lb => Button::LeftTrigger,
            Self::Rb => Button::RightTrigger,
            Self::Back => Button::Select,
            Self::Start => Button::Start,
            Self::Ls => Button::LeftThumb,
            Self::Rs => Button::RightThumb,
            Self::Up => Button::DPadUp,
            Self::Down => Button::DPadDown,
            Self::Left => Button::DPadLeft,
            Self::Right => Button::DPadRight,
            Self::Guide => Button::Mode,
        }
    }
}

/// Axis names on the wire.
#[derive(Serialize, Clone, Copy, Debug, PartialEq, Eq, Hash)]
#[serde(rename_all = "camelCase")]
pub enum GamepadAxis {
    LeftX,
    LeftY,
    RightX,
    RightY,
    Lt,
    Rt,
}

impl GamepadAxis {
    fn from_gilrs(axis: Axis) -> Option<Self> {
        Some(match axis {
            Axis::LeftStickX => Self::LeftX,
            Axis::LeftStickY => Self::LeftY,
            Axis::RightStickX => Self::RightX,
            Axis::RightStickY => Self::RightY,
            Axis::LeftZ => Self::Lt,
            Axis::RightZ => Self::Rt,
            _ => return None,
        })
    }
}

/// Snapshot returned by `kiosk_gamepad_state`.
#[derive(Serialize, Clone, Copy, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct GamepadState {
    pub index: u32,
    pub connected: bool,
    /// Bitmask of [`GamepadButton::bit`].
    pub buttons: u32,
    pub left_x: f32,
    pub left_y: f32,
    pub right_x: f32,
    pub right_y: f32,
    pub lt: f32,
    pub rt: f32,
}

/// `kiosk://gamepad` payload.
#[derive(Serialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct GamepadEvent {
    pub index: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub button: Option<GamepadButton>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub axis: Option<GamepadAxis>,
    pub value: f32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pressed: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub connected: Option<bool>,
}

impl GamepadEvent {
    pub fn button(index: u32, button: GamepadButton, pressed: bool) -> Self {
        Self { index, button: Some(button), axis: None, value: if pressed { 1.0 } else { 0.0 }, pressed: Some(pressed), connected: None }
    }

    pub fn axis(index: u32, axis: GamepadAxis, value: f32) -> Self {
        Self { index, button: None, axis: Some(axis), value, pressed: None, connected: None }
    }

    pub fn connection(index: u32, connected: bool) -> Self {
        Self { index, button: None, axis: None, value: 0.0, pressed: None, connected: Some(connected) }
    }
}

struct Shared {
    enabled: AtomicBool,
    stop: AtomicBool,
    states: Mutex<Vec<GamepadState>>,
}

/// Owner of the poll thread.
pub struct GamepadService {
    shared: Arc<Shared>,
    thread: Mutex<Option<JoinHandle<()>>>,
}

impl GamepadService {
    /// Starts the poll thread (`gamepad.enabled` decides whether events are emitted; the thread runs
    /// either way so `set_enabled(true)` works later).
    pub fn start(app: &AppHandle, config: &GamepadConfig) -> Arc<Self> {
        let shared = Arc::new(Shared { enabled: AtomicBool::new(config.enabled), stop: AtomicBool::new(false), states: Mutex::new(Vec::new()) });
        let poller = Poller::new(app.clone(), config.clone(), Arc::clone(&shared));
        let thread = match std::thread::Builder::new().name("clubshell-gamepad".to_owned()).spawn(move || poller.run()) {
            Ok(handle) => Some(handle),
            Err(e) => {
                tracing::error!(error = %e, "cannot start gamepad thread");
                None
            }
        };
        Arc::new(Self { shared, thread: Mutex::new(thread) })
    }

    pub fn set_enabled(&self, on: bool) {
        if self.shared.enabled.swap(on, Ordering::AcqRel) != on {
            tracing::info!(enabled = on, "gamepad");
        }
    }

    pub fn is_enabled(&self) -> bool {
        self.shared.enabled.load(Ordering::Acquire)
    }

    /// Last known state of every connected pad (`kiosk_gamepad_state`).
    pub fn snapshot(&self) -> Vec<GamepadState> {
        self.shared.states.lock().clone()
    }

    /// `kiosk_state.gamepadConnected`.
    pub fn any_connected(&self) -> bool {
        self.shared.states.lock().iter().any(|s| s.connected)
    }

    /// Stops the thread (joins; at most one poll period).
    pub fn stop(&self) {
        self.shared.stop.store(true, Ordering::Release);
        if let Some(handle) = self.thread.lock().take() {
            let _ = handle.join();
        }
    }
}

/// A held navigation direction (auto-repeat state).
struct NavHold {
    dir: GamepadButton,
    since: Instant,
    last: Instant,
}

struct Poller {
    app: AppHandle,
    cfg: GamepadConfig,
    shared: Arc<Shared>,
    /// Last reported axis value, quantized to 1/100.
    axes: HashMap<(u32, GamepadAxis), i32>,
    /// Directions currently synthesized from the left stick.
    stick_dirs: HashMap<(u32, GamepadButton), bool>,
    holds: HashMap<u32, NavHold>,
}

fn pad_index(id: gilrs::GamepadId) -> u32 {
    u32::try_from(usize::from(id)).unwrap_or(u32::MAX)
}

fn trigger_value(pad: &Gamepad<'_>, button: Button, axis: Axis) -> f32 {
    pad.button_data(button).map_or(0.0, |d| d.value()).max(pad.value(axis))
}

/// Quantizes an axis value after the dead-zone, so only real changes are reported.
fn quantize(value: f32, deadzone: f32) -> i32 {
    let v = if value.abs() < deadzone { 0.0 } else { value.clamp(-1.0, 1.0) };
    (v * 100.0).round() as i32
}

impl Poller {
    fn new(app: AppHandle, cfg: GamepadConfig, shared: Arc<Shared>) -> Self {
        Self { app, cfg, shared, axes: HashMap::new(), stick_dirs: HashMap::new(), holds: HashMap::new() }
    }

    fn run(mut self) {
        let mut gilrs = match Gilrs::new() {
            Ok(g) => g,
            Err(gilrs::Error::NotImplemented(g)) => {
                tracing::warn!("gamepad support is not implemented on this platform");
                g
            }
            Err(e) => {
                tracing::error!(error = %e, "gilrs initialisation failed; gamepad disabled");
                return;
            }
        };
        let poll = Duration::from_millis(self.cfg.poll_ms.max(1));
        for (id, pad) in gilrs.gamepads() {
            tracing::info!(index = pad_index(id), name = pad.name(), "gamepad present");
            self.emit(GamepadEvent::connection(pad_index(id), true));
        }
        self.refresh(&gilrs);
        tracing::info!(poll_ms = self.cfg.poll_ms, deadzone = self.cfg.deadzone, navigation = self.cfg.navigation, "gamepad poll thread started");
        while !self.shared.stop.load(Ordering::Acquire) {
            let enabled = self.shared.enabled.load(Ordering::Acquire);
            let mut dirty = false;
            while let Some(Event { id, event, .. }) = gilrs.next_event() {
                dirty = true;
                if enabled {
                    self.handle(pad_index(id), event);
                }
            }
            if enabled && self.cfg.navigation {
                self.repeat();
            }
            if dirty {
                self.refresh(&gilrs);
            }
            std::thread::sleep(if enabled { poll } else { DISABLED_POLL });
        }
        tracing::info!("gamepad poll thread stopped");
    }

    fn handle(&mut self, index: u32, event: EventType) {
        match event {
            EventType::Connected => {
                tracing::info!(index, "gamepad connected");
                self.emit(GamepadEvent::connection(index, true));
            }
            EventType::Disconnected => {
                tracing::info!(index, "gamepad disconnected");
                self.holds.remove(&index);
                self.stick_dirs.retain(|(i, _), _| *i != index);
                self.axes.retain(|(i, _), _| *i != index);
                self.emit(GamepadEvent::connection(index, false));
            }
            EventType::ButtonPressed(button, _) => {
                if let Some(b) = GamepadButton::from_gilrs(button) {
                    self.press(index, b, true);
                }
            }
            EventType::ButtonReleased(button, _) => {
                if let Some(b) = GamepadButton::from_gilrs(button) {
                    self.press(index, b, false);
                }
            }
            EventType::ButtonChanged(button, value, _) => match button {
                Button::LeftTrigger2 => self.axis(index, GamepadAxis::Lt, value),
                Button::RightTrigger2 => self.axis(index, GamepadAxis::Rt, value),
                _ => {}
            },
            EventType::AxisChanged(axis, value, _) => {
                if let Some(a) = GamepadAxis::from_gilrs(axis) {
                    self.axis(index, a, value);
                    if self.cfg.navigation {
                        match a {
                            GamepadAxis::LeftX => self.stick_dir(index, value, GamepadButton::Left, GamepadButton::Right),
                            GamepadAxis::LeftY => self.stick_dir(index, value, GamepadButton::Down, GamepadButton::Up),
                            _ => {}
                        }
                    }
                }
            }
            _ => {}
        }
    }

    fn press(&mut self, index: u32, button: GamepadButton, pressed: bool) {
        self.emit(GamepadEvent::button(index, button, pressed));
        if !self.cfg.navigation || !button.is_direction() {
            return;
        }
        if pressed {
            let now = Instant::now();
            self.holds.insert(index, NavHold { dir: button, since: now, last: now });
        } else if self.holds.get(&index).is_some_and(|h| h.dir == button) {
            self.holds.remove(&index);
        }
    }

    fn axis(&mut self, index: u32, axis: GamepadAxis, value: f32) {
        let q = quantize(value, self.cfg.deadzone);
        if self.axes.insert((index, axis), q) != Some(q) {
            self.emit(GamepadEvent::axis(index, axis, q as f32 / 100.0));
        }
    }

    /// Turns a stick axis into `negative` / `positive` direction presses with hysteresis
    /// (gilrs: positive Y is up).
    fn stick_dir(&mut self, index: u32, value: f32, negative: GamepadButton, positive: GamepadButton) {
        for (dir, v) in [(negative, -value), (positive, value)] {
            let key = (index, dir);
            let held = self.stick_dirs.get(&key).copied().unwrap_or(false);
            let now_held = if held { v >= STICK_RELEASE } else { v >= STICK_PRESS };
            if now_held != held {
                self.stick_dirs.insert(key, now_held);
                self.press(index, dir, now_held);
            }
        }
    }

    fn repeat(&mut self) {
        let now = Instant::now();
        let mut fire: Vec<(u32, GamepadButton)> = Vec::new();
        for (index, hold) in &mut self.holds {
            if now.duration_since(hold.since) >= REPEAT_DELAY && now.duration_since(hold.last) >= REPEAT_INTERVAL {
                hold.last = now;
                fire.push((*index, hold.dir));
            }
        }
        for (index, dir) in fire {
            self.emit(GamepadEvent::button(index, dir, true));
        }
    }

    fn refresh(&self, gilrs: &Gilrs) {
        let states: Vec<GamepadState> = gilrs
            .gamepads()
            .map(|(id, pad)| {
                let buttons = GamepadButton::ALL.iter().filter(|b| pad.is_pressed(b.to_gilrs())).fold(0u32, |acc, b| acc | b.bit());
                GamepadState {
                    index: pad_index(id),
                    connected: pad.is_connected(),
                    buttons,
                    left_x: pad.value(Axis::LeftStickX),
                    left_y: pad.value(Axis::LeftStickY),
                    right_x: pad.value(Axis::RightStickX),
                    right_y: pad.value(Axis::RightStickY),
                    lt: trigger_value(&pad, Button::LeftTrigger2, Axis::LeftZ),
                    rt: trigger_value(&pad, Button::RightTrigger2, Axis::RightZ),
                }
            })
            .collect();
        *self.shared.states.lock() = states;
    }

    fn emit(&self, event: GamepadEvent) {
        if let Err(e) = self.app.emit(GAMEPAD_EVENT, event) {
            tracing::warn!(error = %e, "cannot emit kiosk://gamepad");
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn wire_names_bits_and_quantization() {
        assert_eq!(serde_json::to_value(GamepadButton::Lb).unwrap(), "lb");
        assert_eq!(serde_json::to_value(GamepadAxis::LeftX).unwrap(), "leftX");
        assert_eq!(GamepadButton::A.bit(), 1);
        assert_eq!(GamepadButton::Guide.bit(), 1 << 14);
        assert!(GamepadButton::Up.is_direction() && !GamepadButton::Start.is_direction());
        assert_eq!(GamepadButton::from_gilrs(Button::South), Some(GamepadButton::A));
        assert_eq!(GamepadButton::from_gilrs(GamepadButton::Rs.to_gilrs()), Some(GamepadButton::Rs));
        assert_eq!(GamepadButton::from_gilrs(Button::Unknown), None);
        assert_eq!(quantize(0.1, 0.25), 0);
        assert_eq!(quantize(-0.734, 0.25), -73);
        assert_eq!(quantize(1.7, 0.25), 100);
        let ev = serde_json::to_value(GamepadEvent::button(0, GamepadButton::Up, true)).unwrap();
        assert_eq!(ev, serde_json::json!({ "index": 0, "button": "up", "value": 1.0, "pressed": true }));
        let ev = serde_json::to_value(GamepadEvent::connection(1, false)).unwrap();
        assert_eq!(ev, serde_json::json!({ "index": 1, "value": 0.0, "connected": false }));
    }
}
