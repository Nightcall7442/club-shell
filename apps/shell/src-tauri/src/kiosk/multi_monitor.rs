//! Display topology: enumeration (winutil `EnumDisplayMonitors`, Tauri's monitor list as the
//! non-Windows fallback), the `WM_DISPLAYCHANGE` watcher → `kiosk://monitorChanged`
//! (`TAURI_COMMANDS.md` §3.2), primary selection (`shell.json → monitors.primaryIndex`, falling back
//! to the OS primary) and the always-on-top "ads" windows (`index.html#/ads?monitor=N&mode=…`) that
//! cover every secondary monitor per `monitors.secondaryMode`. Not created in dev mode.

use std::collections::{HashMap, HashSet};
use std::sync::{Arc, Weak};
use std::thread::JoinHandle;

use clubshell_winutil::monitor::{enumerate, pick_primary, DisplayWatcher, MonitorInfo};
use clubshell_winutil::Rect;
use parking_lot::{Mutex, RwLock};
use serde::Serialize;
use tauri::{
    AppHandle, Emitter, Manager, PhysicalPosition, PhysicalSize, WebviewUrl, WebviewWindow,
    WebviewWindowBuilder,
};

use crate::config::{MonitorsConfig, SecondaryMode, ShellConfig};

/// Event name (`TAURI_COMMANDS.md` §3.2).
pub const MONITOR_CHANGED_EVENT: &str = "kiosk://monitorChanged";
/// Label of the first secondary-monitor window; further ones are `ads-2`, `ads-3`, …
pub const ADS_LABEL: &str = "ads";
/// Geometry used when no display can be enumerated (headless CI).
pub const FALLBACK_RECT: Rect = Rect::from_size(0, 0, 1920, 1080);

/// `MonitorInfo` on the wire (`kiosk_monitors`, `kiosk_state.monitors`, `kiosk://monitorChanged`).
#[derive(Serialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct MonitorDto {
    pub index: u32,
    pub name: String,
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
    pub scale: f32,
    pub hz: u32,
    pub primary: bool,
}

impl From<&MonitorInfo> for MonitorDto {
    fn from(m: &MonitorInfo) -> Self {
        Self {
            index: u32::try_from(m.index).unwrap_or(u32::MAX),
            name: m.name.clone(),
            x: m.rect.left,
            y: m.rect.top,
            width: m.width,
            height: m.height,
            scale: m.scale,
            hz: m.hz,
            primary: m.primary,
        }
    }
}

#[derive(Serialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum MonitorChangeReason {
    Added,
    Removed,
    Resolution,
    Dpi,
}

/// `kiosk://monitorChanged` payload.
#[derive(Serialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct MonitorChangedEvent {
    pub monitors: Vec<MonitorDto>,
    pub primary_index: u32,
    pub reason: MonitorChangeReason,
}

/// Callback invoked with the new monitor list after every topology change.
pub type ChangeListener = Arc<dyn Fn(&[MonitorInfo]) + Send + Sync>;

/// Primary of `list`: `preferred` (`monitors.primaryIndex`) when it exists, else the OS primary.
pub fn primary_of(list: &[MonitorInfo], preferred: usize) -> Option<&MonitorInfo> {
    list.get(preferred).or_else(|| pick_primary(list))
}

/// Position of the primary inside `list` (wire `primaryIndex`).
pub fn primary_position(list: &[MonitorInfo], preferred: usize) -> u32 {
    primary_of(list, preferred)
        .and_then(|p| {
            list.iter()
                .position(|m| m.handle == p.handle && m.name == p.name)
        })
        .and_then(|i| u32::try_from(i).ok())
        .unwrap_or(0)
}

/// Bounding rectangle of all monitors (the virtual screen).
pub fn union_rect(list: &[MonitorInfo]) -> Option<Rect> {
    list.iter().map(|m| m.rect).reduce(|a, b| {
        Rect::new(
            a.left.min(b.left),
            a.top.min(b.top),
            a.right.max(b.right),
            a.bottom.max(b.bottom),
        )
    })
}

/// Classifies a topology change for the event's `reason`.
pub fn change_reason(old: &[MonitorInfo], new: &[MonitorInfo]) -> MonitorChangeReason {
    match new.len().cmp(&old.len()) {
        std::cmp::Ordering::Greater => MonitorChangeReason::Added,
        std::cmp::Ordering::Less => MonitorChangeReason::Removed,
        std::cmp::Ordering::Equal => {
            let same_geometry = old.iter().zip(new).all(|(a, b)| a.rect == b.rect);
            let dpi_changed = old
                .iter()
                .zip(new)
                .any(|(a, b)| (a.scale - b.scale).abs() > 1e-3);
            if same_geometry && dpi_changed {
                MonitorChangeReason::Dpi
            } else {
                MonitorChangeReason::Resolution
            }
        }
    }
}

fn apply_bounds(window: &WebviewWindow, rect: Rect) -> tauri::Result<()> {
    window.set_position(PhysicalPosition::new(rect.left, rect.top))?;
    window.set_size(PhysicalSize::new(
        u32::try_from(rect.width()).unwrap_or(0),
        u32::try_from(rect.height()).unwrap_or(0),
    ))
}

/// Monitors from Tauri (non-Windows / winutil failure): no refresh rate, primary by name.
fn tauri_monitors(app: &AppHandle) -> Vec<MonitorInfo> {
    let primary_name = app
        .primary_monitor()
        .ok()
        .flatten()
        .and_then(|m| m.name().cloned());
    app.available_monitors()
        .unwrap_or_default()
        .iter()
        .enumerate()
        .map(|(index, m)| {
            let name = m
                .name()
                .cloned()
                .unwrap_or_else(|| format!("DISPLAY{}", index + 1));
            let rect = Rect::from_size(
                m.position().x,
                m.position().y,
                i32::try_from(m.size().width).unwrap_or(i32::MAX),
                i32::try_from(m.size().height).unwrap_or(i32::MAX),
            );
            MonitorInfo {
                index,
                handle: 0,
                primary: primary_name.as_deref().map_or(index == 0, |p| p == name),
                name,
                rect,
                work_rect: rect,
                width: rect.width(),
                height: rect.height(),
                hz: 0,
                scale: m.scale_factor() as f32,
            }
        })
        .collect()
}

/// winutil enumeration first, Tauri as fallback.
pub fn enumerate_monitors(app: &AppHandle) -> Vec<MonitorInfo> {
    match enumerate() {
        Ok(list) if !list.is_empty() => list,
        Ok(_) => tauri_monitors(app),
        Err(e) => {
            tracing::debug!(error = %e, "winutil monitor enumeration unavailable; using Tauri");
            tauri_monitors(app)
        }
    }
}

/// Display topology owner; see the module docs.
pub struct MultiMonitor {
    app: AppHandle,
    config: MonitorsConfig,
    dev: bool,
    current: RwLock<Vec<MonitorInfo>>,
    /// Secondary-monitor windows keyed by monitor device name.
    ads: Mutex<HashMap<String, WebviewWindow>>,
    listeners: RwLock<Vec<ChangeListener>>,
    watcher: Mutex<Option<DisplayWatcher>>,
    thread: Mutex<Option<JoinHandle<()>>>,
}

impl MultiMonitor {
    /// Enumerates, lays out the secondary windows and starts the display watcher.
    pub fn start(app: &AppHandle, config: &ShellConfig, dev: bool) -> Arc<Self> {
        let monitors = enumerate_monitors(app);
        tracing::info!(
            count = monitors.len(),
            primary = primary_position(&monitors, config.monitors.primary_index),
            "monitors enumerated"
        );
        let me = Arc::new(Self {
            app: app.clone(),
            config: config.monitors.clone(),
            dev,
            current: RwLock::new(monitors),
            ads: Mutex::new(HashMap::new()),
            listeners: RwLock::new(Vec::new()),
            watcher: Mutex::new(None),
            thread: Mutex::new(None),
        });
        me.relayout();
        match DisplayWatcher::start() {
            Ok((watcher, rx)) => {
                *me.watcher.lock() = Some(watcher);
                let weak: Weak<Self> = Arc::downgrade(&me);
                let spawned = std::thread::Builder::new()
                    .name("clubshell-displays".to_owned())
                    .spawn(move || {
                        for list in rx {
                            match weak.upgrade() {
                                Some(monitors) => monitors.handle_change(list),
                                None => break,
                            }
                        }
                    });
                match spawned {
                    Ok(handle) => *me.thread.lock() = Some(handle),
                    Err(e) => tracing::warn!(error = %e, "cannot start display thread"),
                }
            }
            Err(e) => tracing::debug!(error = %e, "display watcher unavailable"),
        }
        me
    }

    /// Current monitor list (winutil type).
    pub fn monitors(&self) -> Vec<MonitorInfo> {
        self.current.read().clone()
    }

    pub fn count(&self) -> usize {
        self.current.read().len()
    }

    /// Wire shape.
    pub fn dtos(&self) -> Vec<MonitorDto> {
        self.current.read().iter().map(MonitorDto::from).collect()
    }

    pub fn primary(&self) -> Option<MonitorInfo> {
        primary_of(&self.current.read(), self.config.primary_index).cloned()
    }

    /// Wire `primaryIndex`.
    pub fn primary_index(&self) -> u32 {
        primary_position(&self.current.read(), self.config.primary_index)
    }

    /// Rectangle of the Shell's monitor ([`FALLBACK_RECT`] when nothing is attached).
    pub fn primary_rect(&self) -> Rect {
        self.primary().map_or(FALLBACK_RECT, |m| m.rect)
    }

    /// The whole virtual screen.
    pub fn virtual_rect(&self) -> Rect {
        union_rect(&self.current.read()).unwrap_or(FALLBACK_RECT)
    }

    /// Rectangle of the monitor with enumeration index `index` (`kiosk_move_to_monitor`).
    pub fn monitor_rect(&self, index: usize) -> Option<Rect> {
        self.current
            .read()
            .iter()
            .find(|m| m.index == index)
            .map(|m| m.rect)
    }

    /// Registers a listener called after every topology change (from the display thread).
    pub fn on_change(&self, listener: impl Fn(&[MonitorInfo]) + Send + Sync + 'static) {
        self.listeners.write().push(Arc::new(listener));
    }

    /// Re-creates / repositions / destroys the secondary-monitor windows for the current list.
    pub fn relayout(&self) {
        if self.dev {
            return;
        }
        let list = self.monitors();
        let Some(primary) = primary_of(&list, self.config.primary_index).cloned() else {
            return;
        };
        let secondaries: Vec<&MonitorInfo> =
            list.iter().filter(|m| m.name != primary.name).collect();
        let wanted: HashSet<&str> = secondaries.iter().map(|m| m.name.as_str()).collect();
        let mut ads = self.ads.lock();
        ads.retain(|name, window| {
            if wanted.contains(name.as_str()) {
                return true;
            }
            tracing::info!(monitor = %name, label = window.label(), "secondary window closed");
            let _ = window.destroy();
            false
        });
        for monitor in secondaries {
            if let Some(window) = ads.get(&monitor.name) {
                if let Err(e) = apply_bounds(window, monitor.rect) {
                    tracing::warn!(monitor = %monitor.name, error = %e, "secondary window re-layout failed");
                }
                continue;
            }
            let taken: HashSet<String> = ads.values().map(|w| w.label().to_owned()).collect();
            match self.create_ads_window(monitor, &taken) {
                Ok(window) => {
                    tracing::info!(monitor = %monitor.name, label = window.label(), "secondary window created");
                    ads.insert(monitor.name.clone(), window);
                }
                Err(e) => {
                    tracing::warn!(monitor = %monitor.name, error = %e, "cannot create secondary window")
                }
            }
        }
    }

    fn free_label(&self, taken: &HashSet<String>) -> String {
        let mut n = 1u32;
        loop {
            let label = if n == 1 {
                ADS_LABEL.to_owned()
            } else {
                format!("{ADS_LABEL}-{n}")
            };
            if !taken.contains(&label) && self.app.get_webview_window(&label).is_none() {
                return label;
            }
            n += 1;
        }
    }

    fn create_ads_window(
        &self,
        monitor: &MonitorInfo,
        taken: &HashSet<String>,
    ) -> tauri::Result<WebviewWindow> {
        let mode = match self.config.secondary_mode {
            SecondaryMode::Black => "black",
            SecondaryMode::Mirror => "mirror",
            SecondaryMode::Wallpaper => "wallpaper",
        };
        let url = format!("index.html#/ads?monitor={}&mode={mode}", monitor.index);
        let label = self.free_label(taken);
        let window = WebviewWindowBuilder::new(&self.app, label, WebviewUrl::App(url.into()))
            .title("ClubShell")
            .decorations(false)
            .always_on_top(true)
            .skip_taskbar(true)
            .resizable(false)
            .maximizable(false)
            .minimizable(false)
            .focused(false)
            .visible(false)
            .shadow(false)
            .build()?;
        apply_bounds(&window, monitor.rect)?;
        window.show()?;
        Ok(window)
    }

    fn handle_change(&self, list: Vec<MonitorInfo>) {
        let reason = change_reason(&self.current.read(), &list);
        *self.current.write() = list.clone();
        tracing::info!(?reason, count = list.len(), "display topology changed");
        self.relayout();
        let event = MonitorChangedEvent {
            monitors: list.iter().map(MonitorDto::from).collect(),
            primary_index: primary_position(&list, self.config.primary_index),
            reason,
        };
        if let Err(e) = self.app.emit(MONITOR_CHANGED_EVENT, event) {
            tracing::warn!(error = %e, "cannot emit kiosk://monitorChanged");
        }
        let listeners: Vec<ChangeListener> = self.listeners.read().clone();
        for listener in listeners {
            listener(&list);
        }
    }

    /// Destroys the secondary windows and stops the watcher; the forwarding thread ends by itself
    /// once the watcher's sender is gone (not joined: it may be waiting on the main thread).
    pub fn shutdown(&self) {
        for (_, window) in self.ads.lock().drain() {
            let _ = window.destroy();
        }
        self.watcher.lock().take();
        self.thread.lock().take();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn mon(index: usize, rect: Rect, primary: bool, scale: f32) -> MonitorInfo {
        MonitorInfo {
            index,
            handle: index as isize + 1,
            name: format!(r"\\.\DISPLAY{}", index + 1),
            rect,
            work_rect: rect,
            width: rect.width(),
            height: rect.height(),
            hz: 60,
            primary,
            scale,
        }
    }

    #[test]
    fn primary_union_and_reason() {
        let a = mon(0, Rect::from_size(0, 0, 1920, 1080), false, 1.0);
        let b = mon(1, Rect::from_size(1920, 0, 2560, 1440), true, 1.25);
        let list = [a.clone(), b.clone()];
        assert_eq!(
            primary_of(&list, 0).map(|m| m.index),
            Some(0),
            "configured index wins"
        );
        assert_eq!(
            primary_of(&list, 7).map(|m| m.index),
            Some(1),
            "out of range → OS primary"
        );
        assert_eq!(primary_position(&list, 7), 1);
        assert_eq!(union_rect(&list), Some(Rect::new(0, 0, 4480, 1440)));
        assert_eq!(union_rect(&[]), None);

        assert_eq!(
            change_reason(std::slice::from_ref(&a), &list),
            MonitorChangeReason::Added
        );
        assert_eq!(
            change_reason(&list, std::slice::from_ref(&a)),
            MonitorChangeReason::Removed
        );
        let dpi = [a.clone(), mon(1, b.rect, true, 1.5)];
        assert_eq!(change_reason(&list, &dpi), MonitorChangeReason::Dpi);
        let res = [a, mon(1, Rect::from_size(1920, 0, 1920, 1080), true, 1.25)];
        assert_eq!(change_reason(&list, &res), MonitorChangeReason::Resolution);

        let dto = MonitorDto::from(&b);
        let json = serde_json::to_value(dto).unwrap();
        assert_eq!(json["x"], 1920);
        assert_eq!(json["primary"], true);
        assert_eq!(json["index"], 1);
    }
}
