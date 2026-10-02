//! Graphics-card vendor control panel: which one is installed for the kiosk user, launching it from the Shell process
//! (a packaged app through `IApplicationActivationManager`, a classic one as a child process), and handing it the
//! screen. While a panel window is open the kiosk's topmost/foreground guard and stray-window sweep pause
//! ([`Kiosk::begin_external_window`]); once the window is closed or minimized the guard is re-armed and the Shell comes
//! back on top. Panels outlive their launcher (Radeon Software stays in the tray, a second launch hands over to the
//! running instance), so the watch follows the panel's windows by process id *and* image name, not the child process.

use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use clubshell_winutil::window::WindowInfo;
use parking_lot::Mutex;
use serde::Serialize;
use tauri::async_runtime::JoinHandle;

use crate::kiosk::Kiosk;

/// How often the panel's windows are looked for.
const WATCH_INTERVAL: Duration = Duration::from_secs(1);
/// A panel that shows no window within this time is taken as failed to start.
const LAUNCH_GRACE: Duration = Duration::from_secs(25);
/// Consecutive looks without a window before the panel counts as closed.
const CLOSE_MISSES: u8 = 2;
/// Top-level frame of packaged UWP apps; the app's own window is a child owned by the app process.
const FRAME_CLASS: &str = "ApplicationFrameWindow";
/// Per-user registry of installed packages (one sub-key per package full name).
pub const PACKAGES_KEY: &str = r"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

#[derive(Serialize, Clone, Copy, Debug, PartialEq, Eq, Hash)]
#[serde(rename_all = "lowercase")]
pub enum GpuVendor {
    Nvidia,
    Amd,
    Intel,
}

impl GpuVendor {
    pub fn parse(s: &str) -> Option<Self> {
        match s.trim().to_ascii_lowercase().as_str() {
            "nvidia" => Some(Self::Nvidia),
            "amd" => Some(Self::Amd),
            "intel" => Some(Self::Intel),
            _ => None,
        }
    }

    fn panel(self) -> &'static PanelDef {
        PANELS
            .iter()
            .find(|p| p.vendor == self)
            .expect("every vendor has a panel")
    }
}

/// Store (MSIX) package carrying a panel.
#[derive(Debug)]
pub struct StorePackage {
    pub name: &'static str,
    pub publisher_id: &'static str,
    pub app: &'static str,
}

impl StorePackage {
    /// `PackageFamilyName!AppId`.
    pub fn aumid(&self) -> String {
        format!("{}_{}!{}", self.name, self.publisher_id, self.app)
    }

    /// `true` for a package full name of this package (`Name_Version_Arch_ResourceId_PublisherId`).
    pub fn matches(&self, full_name: &str) -> bool {
        let lower = full_name.to_ascii_lowercase();
        lower.starts_with(&format!("{}_", self.name.to_ascii_lowercase()))
            && lower.ends_with(&format!("_{}", self.publisher_id.to_ascii_lowercase()))
    }
}

#[derive(Debug)]
pub struct PanelDef {
    pub vendor: GpuVendor,
    /// Product name shown on the button (not translated).
    pub title: &'static str,
    pub store: Option<StorePackage>,
    /// Classic install, relative to `%ProgramFiles%`.
    pub exe: Option<&'static str>,
    /// Image names of the processes that own the panel's windows.
    pub images: &'static [&'static str],
}

/// NVIDIA: DCH drivers ship the panel as a Store app, older "Standard" drivers as `nvcplui.exe`. AMD: Radeon Software
/// (Adrenalin). Intel: Graphics Command Center, a Store app (UWP, framed by `ApplicationFrameHost`).
pub const PANELS: [PanelDef; 3] = [
    PanelDef {
        vendor: GpuVendor::Nvidia,
        title: "NVIDIA Control Panel",
        store: Some(StorePackage {
            name: "NVIDIACorp.NVIDIAControlPanel",
            publisher_id: "56jybvy8sckqj",
            app: "NVIDIACorp.NVIDIAControlPanel",
        }),
        exe: Some(r"NVIDIA Corporation\Control Panel Client\nvcplui.exe"),
        images: &["nvcplui.exe"],
    },
    PanelDef {
        vendor: GpuVendor::Amd,
        title: "AMD Software",
        store: None,
        exe: Some(r"AMD\CNext\CNext\RadeonSoftware.exe"),
        images: &["RadeonSoftware.exe"],
    },
    PanelDef {
        vendor: GpuVendor::Intel,
        title: "Intel Graphics Command Center",
        store: Some(StorePackage {
            name: "AppUp.IntelGraphicsExperience",
            publisher_id: "8j3eq9eme6ctt",
            app: "App",
        }),
        exe: None,
        images: &["IGCC.exe"],
    },
];

/// `pc_gpu_panels` item.
#[derive(Serialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct GpuPanelInfo {
    pub vendor: GpuVendor,
    pub name: &'static str,
}

/// How a panel is started.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Launch {
    Store { aumid: String },
    Exe(PathBuf),
}

/// The packaged app when it is registered for the user (current drivers), else the classic executable when present.
pub fn resolve(
    def: &PanelDef,
    packages: &[String],
    program_files: Option<&Path>,
    exists: impl Fn(&Path) -> bool,
) -> Option<Launch> {
    if let Some(store) = &def.store {
        if packages.iter().any(|p| store.matches(p)) {
            return Some(Launch::Store {
                aumid: store.aumid(),
            });
        }
    }
    let path = program_files?.join(def.exe?);
    exists(&path).then_some(Launch::Exe(path))
}

/// Every panel [`resolve`] finds.
pub fn installed(
    packages: &[String],
    program_files: Option<&Path>,
    exists: impl Fn(&Path) -> bool,
) -> Vec<GpuPanelInfo> {
    PANELS
        .iter()
        .filter(|def| resolve(def, packages, program_files, &exists).is_some())
        .map(|def| GpuPanelInfo {
            vendor: def.vendor,
            name: def.title,
        })
        .collect()
}

fn program_files() -> Option<PathBuf> {
    std::env::var_os("ProgramFiles")
        .filter(|v| !v.is_empty())
        .map(PathBuf::from)
}

/// [`installed`] on this PC.
pub fn installed_here() -> Vec<GpuPanelInfo> {
    installed(
        &native::registered_packages(),
        program_files().as_deref(),
        Path::is_file,
    )
}

/// [`resolve`] on this PC.
pub fn resolve_here(vendor: GpuVendor) -> Option<Launch> {
    resolve(
        vendor.panel(),
        &native::registered_packages(),
        program_files().as_deref(),
        Path::is_file,
    )
}

// ───────────────────────────── watching the panel ─────────────────────────────

/// Processes that make up an open panel: the launched pid(s) plus any process running one of its images.
pub struct PanelTargets {
    pids: Mutex<HashSet<u32>>,
    images: &'static [&'static str],
}

impl PanelTargets {
    pub fn new(vendor: GpuVendor) -> Self {
        Self {
            pids: Mutex::new(HashSet::new()),
            images: vendor.panel().images,
        }
    }

    pub fn add(&self, pid: u32) {
        if pid != 0 {
            self.pids.lock().insert(pid);
        }
    }

    /// `image_of` resolves a pid to its executable name (cached by the caller).
    pub fn is_target(&self, pid: u32, image_of: &mut impl FnMut(u32) -> Option<String>) -> bool {
        if pid == 0 {
            return false;
        }
        if self.pids.lock().contains(&pid) {
            return true;
        }
        image_of(pid)
            .is_some_and(|image| self.images.iter().any(|i| i.eq_ignore_ascii_case(&image)))
    }
}

/// Top-level windows that belong to the panel: visible, titled, not minimized, owned by a target process — directly, or
/// as the child of a UWP frame.
pub fn panel_windows(
    windows: &[WindowInfo],
    is_target: &mut impl FnMut(u32) -> bool,
    children: impl Fn(isize) -> Vec<WindowInfo>,
    iconic: impl Fn(isize) -> bool,
) -> Vec<isize> {
    windows
        .iter()
        .filter(|w| w.visible && !w.title.is_empty() && !iconic(w.hwnd))
        .filter(|w| {
            is_target(w.pid)
                || (w.class.eq_ignore_ascii_case(FRAME_CLASS)
                    && children(w.hwnd).iter().any(|c| is_target(c.pid)))
        })
        .map(|w| w.hwnd)
        .collect()
}

/// What the watch concluded after one look.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum WatchStep {
    /// No window yet; still within the launch grace.
    Starting,
    Open,
    Closed,
}

/// Open/closed decision from successive looks at the panel's windows.
#[derive(Clone, Copy, Debug)]
pub struct PanelWatch {
    started: Instant,
    seen: bool,
    misses: u8,
}

impl PanelWatch {
    pub fn new(started: Instant) -> Self {
        Self {
            started,
            seen: false,
            misses: 0,
        }
    }

    pub fn step(&mut self, window_open: bool, now: Instant) -> WatchStep {
        if window_open {
            self.seen = true;
            self.misses = 0;
            return WatchStep::Open;
        }
        if self.seen {
            self.misses = self.misses.saturating_add(1);
            return if self.misses >= CLOSE_MISSES {
                WatchStep::Closed
            } else {
                WatchStep::Open
            };
        }
        if now.duration_since(self.started) >= LAUNCH_GRACE {
            WatchStep::Closed
        } else {
            WatchStep::Starting
        }
    }
}

struct PanelSession {
    generation: u64,
    vendor: GpuVendor,
    targets: Arc<PanelTargets>,
    kiosk: Arc<Kiosk>,
    task: JoinHandle<()>,
}

/// The open vendor panel, if any (one at a time).
#[derive(Default)]
pub struct PanelHost {
    session: Mutex<Option<PanelSession>>,
    generation: AtomicU64,
}

impl PanelHost {
    /// The panel was launched (`pid` when known; the guard is already paused): watch its windows.
    pub fn opened(&self, kiosk: Arc<Kiosk>, vendor: GpuVendor, pid: Option<u32>) {
        let mut slot = self.session.lock();
        let targets = match slot.take() {
            Some(old) => {
                old.task.abort();
                if old.vendor == vendor {
                    old.targets
                } else {
                    Arc::new(PanelTargets::new(vendor))
                }
            }
            None => Arc::new(PanelTargets::new(vendor)),
        };
        if let Some(pid) = pid {
            targets.add(pid);
        }
        let generation = self.generation.fetch_add(1, Ordering::AcqRel) + 1;
        let task = spawn_watch(generation, Arc::clone(&targets));
        tracing::info!(?vendor, pid, "graphics panel opened");
        *slot = Some(PanelSession {
            generation,
            vendor,
            targets,
            kiosk,
            task,
        });
    }

    /// The launch failed: re-arm the guard unless an earlier panel is still open.
    pub fn launch_failed(&self, kiosk: &Kiosk) {
        if self.session.lock().is_none() {
            kiosk.end_external_window();
        }
    }

    /// The watch saw the panel go away (or never come up): guard back on, Shell on top.
    fn finished(&self, generation: u64) {
        let mut slot = self.session.lock();
        if slot.as_ref().is_some_and(|s| s.generation == generation) {
            if let Some(session) = slot.take() {
                tracing::info!(vendor = ?session.vendor, "graphics panel closed; shell back on top");
                session.kiosk.end_external_window();
            }
        }
    }

    /// The player left: closes the panel's windows and re-arms the guard.
    pub fn close(&self) {
        let Some(session) = self.session.lock().take() else {
            return;
        };
        session.task.abort();
        let closed = native::close_panel_windows(&session.targets);
        tracing::info!(vendor = ?session.vendor, windows = closed, "graphics panel closed for the next player");
        session.kiosk.end_external_window();
    }
}

fn spawn_watch(generation: u64, targets: Arc<PanelTargets>) -> JoinHandle<()> {
    tauri::async_runtime::spawn(async move {
        let mut watch = PanelWatch::new(Instant::now());
        let mut tick = tokio::time::interval(WATCH_INTERVAL);
        tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
        loop {
            tick.tick().await;
            let look = Arc::clone(&targets);
            let open = tauri::async_runtime::spawn_blocking(move || native::panel_open(&look))
                .await
                .unwrap_or(false);
            if watch.step(open, Instant::now()) == WatchStep::Closed {
                break;
            }
        }
        super::runtime().panels.finished(generation);
    })
}

/// Visible windows of the panel right now (resolving image names once per pid).
fn find_panel_windows(targets: &PanelTargets) -> Vec<isize> {
    let Ok(windows) = clubshell_winutil::window::enumerate_windows() else {
        return Vec::new();
    };
    let mut images: HashMap<u32, Option<String>> = HashMap::new();
    let mut image_of = |pid: u32| {
        images
            .entry(pid)
            .or_insert_with(|| native::process_image(pid))
            .clone()
    };
    let mut is_target = |pid: u32| targets.is_target(pid, &mut image_of);
    panel_windows(
        &windows,
        &mut is_target,
        clubshell_winutil::window::child_windows,
        native::is_iconic,
    )
}

#[cfg(windows)]
pub(super) mod native {
    use windows::core::{PCWSTR, PWSTR};
    use windows::Win32::Foundation::{
        CloseHandle, BOOL, ERROR_NO_MORE_ITEMS, ERROR_SUCCESS, LPARAM, WPARAM,
    };
    use windows::Win32::System::Com::{CoCreateInstance, CLSCTX_LOCAL_SERVER};
    use windows::Win32::System::Registry::{
        RegCloseKey, RegEnumKeyExW, RegOpenKeyExW, HKEY, HKEY_CURRENT_USER, KEY_READ,
    };
    use windows::Win32::System::Threading::{
        OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32,
        PROCESS_QUERY_LIMITED_INFORMATION,
    };
    use windows::Win32::UI::Shell::{
        ApplicationActivationManager, IApplicationActivationManager, AO_NONE,
    };
    use windows::Win32::UI::WindowsAndMessaging::{IsIconic, PostMessageW, WM_CLOSE};

    use super::{find_panel_windows, Launch, PanelTargets, PACKAGES_KEY};
    use crate::state::{CmdResult, ShellError};

    fn wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(std::iter::once(0)).collect()
    }

    /// Package full names registered for the current user (empty when the key cannot be read).
    pub fn registered_packages() -> Vec<String> {
        let path = wide(PACKAGES_KEY);
        let mut key = HKEY::default();
        // SAFETY: `path` is NUL-terminated; `key` receives a handle closed below.
        let opened = unsafe {
            RegOpenKeyExW(
                HKEY_CURRENT_USER,
                PCWSTR(path.as_ptr()),
                0,
                KEY_READ,
                &mut key,
            )
        };
        if opened != ERROR_SUCCESS {
            tracing::debug!(code = opened.0, "package repository key not readable");
            return Vec::new();
        }
        let mut names = Vec::new();
        let mut buf = [0u16; 512];
        for index in 0.. {
            let mut len = buf.len() as u32;
            // SAFETY: `buf` is writable for `len` characters; class and time outputs are not requested.
            let status = unsafe {
                RegEnumKeyExW(
                    key,
                    index,
                    PWSTR(buf.as_mut_ptr()),
                    &mut len,
                    None,
                    PWSTR::null(),
                    None,
                    None,
                )
            };
            if status == ERROR_NO_MORE_ITEMS {
                break;
            }
            if status == ERROR_SUCCESS {
                names.push(String::from_utf16_lossy(
                    &buf[..(len as usize).min(buf.len())],
                ));
            }
        }
        // SAFETY: `key` was opened above.
        let _ = unsafe { RegCloseKey(key) };
        names
    }

    /// Starts the panel; the pid when known. A packaged app needs a COM-initialised (STA) thread.
    pub fn launch(launch: &Launch) -> CmdResult<Option<u32>> {
        match launch {
            Launch::Store { aumid } => {
                // SAFETY: plain COM activation on a thread the caller initialised for COM.
                let manager: IApplicationActivationManager = unsafe {
                    CoCreateInstance(&ApplicationActivationManager, None, CLSCTX_LOCAL_SERVER)
                }
                .map_err(|e| ShellError::internal(format!("ApplicationActivationManager: {e}")))?;
                let id = wide(aumid);
                // SAFETY: `id` is NUL-terminated and outlives the call; no arguments.
                let pid = unsafe {
                    manager.ActivateApplication(PCWSTR(id.as_ptr()), PCWSTR::null(), AO_NONE)
                }
                .map_err(|e| ShellError::internal(format!("cannot open {aumid}: {e}")))?;
                Ok(Some(pid))
            }
            Launch::Exe(path) => {
                let mut command = std::process::Command::new(path);
                if let Some(dir) = path.parent() {
                    command.current_dir(dir);
                }
                let child = command.spawn().map_err(|e| {
                    ShellError::internal(format!("cannot start {}: {e}", path.display()))
                })?;
                Ok(Some(child.id()))
            }
        }
    }

    /// Executable name (`nvcplui.exe`) of `pid`, when accessible.
    pub fn process_image(pid: u32) -> Option<String> {
        // SAFETY: the handle is closed before returning; the buffer is writable and `len` holds its size.
        unsafe {
            let handle =
                OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, BOOL::from(false), pid).ok()?;
            let mut buf = [0u16; 1024];
            let mut len = buf.len() as u32;
            let ok = QueryFullProcessImageNameW(
                handle,
                PROCESS_NAME_WIN32,
                PWSTR(buf.as_mut_ptr()),
                &mut len,
            )
            .is_ok();
            let _ = CloseHandle(handle);
            if !ok {
                return None;
            }
            let path = String::from_utf16_lossy(&buf[..(len as usize).min(buf.len())]);
            path.rsplit(['\\', '/'])
                .next()
                .filter(|s| !s.is_empty())
                .map(str::to_owned)
        }
    }

    pub fn is_iconic(hwnd: isize) -> bool {
        // SAFETY: no preconditions beyond a window handle.
        unsafe { IsIconic(clubshell_winutil::hwnd_from_raw(hwnd)) }.as_bool()
    }

    pub fn panel_open(targets: &PanelTargets) -> bool {
        !find_panel_windows(targets).is_empty()
    }

    /// Asks every panel window to close (`WM_CLOSE`); returns how many were asked.
    pub fn close_panel_windows(targets: &PanelTargets) -> usize {
        let windows = find_panel_windows(targets);
        for &hwnd in &windows {
            // SAFETY: posting to a window handle has no preconditions; a stale handle just fails.
            let _ = unsafe {
                PostMessageW(
                    clubshell_winutil::hwnd_from_raw(hwnd),
                    WM_CLOSE,
                    WPARAM(0),
                    LPARAM(0),
                )
            };
        }
        windows.len()
    }
}

#[cfg(not(windows))]
pub(super) mod native {
    use super::{Launch, PanelTargets};
    use crate::state::{CmdResult, ShellError};

    pub fn registered_packages() -> Vec<String> {
        Vec::new()
    }

    pub fn launch(_launch: &Launch) -> CmdResult<Option<u32>> {
        Err(ShellError::unsupported())
    }

    pub fn process_image(_pid: u32) -> Option<String> {
        None
    }

    pub fn is_iconic(_hwnd: isize) -> bool {
        false
    }

    pub fn panel_open(_targets: &PanelTargets) -> bool {
        false
    }

    pub fn close_panel_windows(_targets: &PanelTargets) -> usize {
        0
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn window(hwnd: isize, pid: u32, title: &str, class: &str) -> WindowInfo {
        WindowInfo {
            hwnd,
            pid,
            title: title.to_owned(),
            class: class.to_owned(),
            visible: true,
        }
    }

    #[test]
    fn vendors_parse_and_serialize() {
        assert_eq!(GpuVendor::parse("NVIDIA"), Some(GpuVendor::Nvidia));
        assert_eq!(GpuVendor::parse(" amd "), Some(GpuVendor::Amd));
        assert_eq!(GpuVendor::parse("intel"), Some(GpuVendor::Intel));
        assert_eq!(GpuVendor::parse("matrox"), None);
        let json = serde_json::to_value(GpuPanelInfo {
            vendor: GpuVendor::Amd,
            name: "AMD Software",
        })
        .unwrap();
        assert_eq!(
            json,
            serde_json::json!({ "vendor": "amd", "name": "AMD Software" })
        );
    }

    #[test]
    fn store_packages_match_full_names_only() {
        let nvidia = GpuVendor::Nvidia.panel().store.as_ref().unwrap();
        assert_eq!(
            nvidia.aumid(),
            "NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj!NVIDIACorp.NVIDIAControlPanel"
        );
        assert!(nvidia.matches("NVIDIACorp.NVIDIAControlPanel_8.1.969.0_x64__56jybvy8sckqj"));
        assert!(nvidia.matches("nvidiacorp.nvidiacontrolpanel_8.1.967.0_x64__56JYBVY8SCKQJ"));
        assert!(!nvidia.matches("NVIDIACorp.NVIDIAControlPanelBeta_8.1.0.0_x64__56jybvy8sckqj"));
        assert!(!nvidia.matches("NVIDIACorp.NVIDIAControlPanel_8.1.969.0_x64__otherpublisher"));
        let intel = GpuVendor::Intel.panel().store.as_ref().unwrap();
        assert_eq!(
            intel.aumid(),
            "AppUp.IntelGraphicsExperience_8j3eq9eme6ctt!App"
        );
        assert!(intel.matches("AppUp.IntelGraphicsExperience_1.100.5536.0_x64__8j3eq9eme6ctt"));
    }

    #[test]
    fn resolve_prefers_the_store_app_then_the_classic_exe() {
        let pf = Path::new(r"C:\Program Files");
        let packages = vec![
            "Microsoft.WindowsCalculator_11.2405.2.0_x64__8wekyb3d8bbwe".to_owned(),
            "NVIDIACorp.NVIDIAControlPanel_8.1.969.0_x64__56jybvy8sckqj".to_owned(),
        ];
        let nothing_on_disk = |_: &Path| false;
        assert_eq!(
            resolve(
                GpuVendor::Nvidia.panel(),
                &packages,
                Some(pf),
                nothing_on_disk
            ),
            Some(Launch::Store {
                aumid: "NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj!NVIDIACorp.NVIDIAControlPanel"
                    .into()
            })
        );
        let legacy = pf.join(r"NVIDIA Corporation\Control Panel Client\nvcplui.exe");
        let only_legacy = |p: &Path| p == legacy;
        assert_eq!(
            resolve(GpuVendor::Nvidia.panel(), &[], Some(pf), only_legacy),
            Some(Launch::Exe(legacy.clone()))
        );
        assert_eq!(
            resolve(GpuVendor::Nvidia.panel(), &[], None, |_: &Path| true),
            None
        );

        let radeon = pf.join(r"AMD\CNext\CNext\RadeonSoftware.exe");
        let only_radeon = |p: &Path| p == radeon;
        assert_eq!(
            installed(&[], Some(pf), only_radeon),
            vec![GpuPanelInfo {
                vendor: GpuVendor::Amd,
                name: "AMD Software"
            }]
        );
        assert!(installed(&[], Some(pf), nothing_on_disk).is_empty());
        let all = installed(
            &[
                "AppUp.IntelGraphicsExperience_1.100.5536.0_x64__8j3eq9eme6ctt".to_owned(),
                packages[1].clone(),
            ],
            Some(pf),
            only_radeon,
        );
        let vendors: Vec<GpuVendor> = all.iter().map(|p| p.vendor).collect();
        assert_eq!(
            vendors,
            [GpuVendor::Nvidia, GpuVendor::Amd, GpuVendor::Intel]
        );
    }

    #[test]
    fn panel_windows_follow_pids_images_and_uwp_frames() {
        let targets = PanelTargets::new(GpuVendor::Nvidia);
        targets.add(100);
        let images: HashMap<u32, &str> = [
            (100, "nvcplui.exe"),
            (200, "NVCPLUI.EXE"),
            (300, "explorer.exe"),
            (400, "ApplicationFrameHost.exe"),
        ]
        .into_iter()
        .collect();
        let mut image_of = |pid: u32| images.get(&pid).map(|s| (*s).to_owned());
        let mut is_target = |pid: u32| targets.is_target(pid, &mut image_of);
        let windows = vec![
            window(1, 100, "NVIDIA Control Panel", "#32770"),
            window(2, 200, "NVIDIA Control Panel", "#32770"), // second instance, by image name
            window(3, 300, "File Explorer", "CabinetWClass"),
            window(4, 100, "", "Hidden"),                     // untitled
            window(5, 400, "Some UWP app", FRAME_CLASS),      // frame of another app
            window(6, 100, "NVIDIA Control Panel", "#32770"), // minimized
        ];
        let found = panel_windows(&windows, &mut is_target, |_| Vec::new(), |h| h == 6);
        assert_eq!(found, [1, 2]);

        let intel = PanelTargets::new(GpuVendor::Intel);
        intel.add(500);
        let mut none = |_: u32| None;
        let mut is_intel = |pid: u32| intel.is_target(pid, &mut none);
        let frame = vec![window(
            7,
            400,
            "Intel® Graphics Command Center",
            FRAME_CLASS,
        )];
        let children = |_: isize| {
            vec![window(
                8,
                500,
                "Intel® Graphics Command Center",
                "Windows.UI.Core.CoreWindow",
            )]
        };
        assert_eq!(
            panel_windows(&frame, &mut is_intel, children, |_| false),
            [7]
        );
        assert!(panel_windows(&frame, &mut is_intel, |_| Vec::new(), |_| false).is_empty());
    }

    #[test]
    fn watch_waits_for_the_window_then_for_it_to_go() {
        let t0 = Instant::now();
        let mut watch = PanelWatch::new(t0);
        assert_eq!(
            watch.step(false, t0 + Duration::from_secs(1)),
            WatchStep::Starting
        );
        assert_eq!(
            watch.step(true, t0 + Duration::from_secs(2)),
            WatchStep::Open
        );
        assert_eq!(
            watch.step(false, t0 + Duration::from_secs(3)),
            WatchStep::Open,
            "one miss is a blink"
        );
        assert_eq!(
            watch.step(true, t0 + Duration::from_secs(4)),
            WatchStep::Open
        );
        assert_eq!(
            watch.step(false, t0 + Duration::from_secs(5)),
            WatchStep::Open
        );
        assert_eq!(
            watch.step(false, t0 + Duration::from_secs(6)),
            WatchStep::Closed
        );

        let mut never = PanelWatch::new(t0);
        assert_eq!(
            never.step(false, t0 + LAUNCH_GRACE - Duration::from_secs(1)),
            WatchStep::Starting
        );
        assert_eq!(never.step(false, t0 + LAUNCH_GRACE), WatchStep::Closed);
    }

    /// Read-only: the package list of this PC can be read (it may be empty) and detection does not panic.
    #[test]
    fn detection_reads_this_pc() {
        let packages = native::registered_packages();
        let panels = installed_here();
        if cfg!(windows) {
            assert!(packages.iter().all(|p| !p.is_empty()));
        }
        assert!(panels.len() <= PANELS.len());
    }
}
