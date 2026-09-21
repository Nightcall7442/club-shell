//! Mirror of `ClubShell.Contracts.Pcs` (HardwareInfo.cs, PcStatus.cs, PcInfo.cs): hardware
//! inventory, metrics, PC records, policy, theme and server-side agent config.

use chrono::{DateTime, NaiveTime, Utc};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use uuid::Uuid;

use crate::commands::UpdateChannel;
use crate::games::AntiCheatKind;
use crate::user::Locale;

// ───────────────────────────── Hardware ─────────────────────────────

wire_enum! {
    /// Physical disk type.
    DiskType {
        Hdd = "hdd",
        Ssd = "ssd",
        Nvme = "nvme",
        /// Network share / iSCSI.
        Network = "network",
        Unknown = "unknown",
    }
}

/// Well-known values of [`PeripheralInfo::kind`].
pub mod peripheral_kinds {
    pub const KEYBOARD: &str = "keyboard";
    pub const MOUSE: &str = "mouse";
    pub const HEADSET: &str = "headset";
    pub const GAMEPAD: &str = "gamepad";
    pub const OTHER: &str = "other";
}

/// CPU description.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct CpuInfo {
    pub model: String,
    /// Physical cores.
    pub cores: i32,
    /// Logical processors.
    pub threads: i32,
}

/// GPU description.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct GpuInfo {
    pub model: String,
    /// Dedicated VRAM in MiB.
    pub vram_mb: i32,
    pub driver: String,
}

/// Logical disk.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct DiskInfo {
    /// Mount point, e.g. `C:`.
    pub mount: String,
    #[serde(with = "crate::wire::num")]
    pub total_gb: f64,
    #[serde(with = "crate::wire::num")]
    pub free_gb: f64,
    pub r#type: DiskType,
}

/// Attached monitor.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MonitorInfo {
    /// Display index (0-based).
    pub index: i32,
    pub width: i32,
    pub height: i32,
    pub hz: i32,
    pub primary: bool,
}

/// Primary network adapter.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct NetworkInfo {
    /// `AA:BB:CC:DD:EE:FF`.
    pub mac: String,
    pub ip: String,
    pub adapter: String,
}

/// Operating system.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct OsInfo {
    /// Product name, e.g. `Windows 11 Pro`.
    pub version: String,
    /// Build, e.g. `26200.1234`.
    pub build: String,
}

/// USB/HID peripheral.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct PeripheralInfo {
    /// Kind ([`peripheral_kinds`]).
    pub kind: String,
    pub name: String,
    pub vendor_id: String,
    pub product_id: String,
}

/// Hardware inventory of a PC (IPC_PROTOCOL.md §6.6). Sent at registration and on change.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct HardwareInfo {
    pub cpu: CpuInfo,
    pub gpu: Vec<GpuInfo>,
    /// Installed RAM in MiB.
    pub ram_mb: i32,
    pub disks: Vec<DiskInfo>,
    pub monitors: Vec<MonitorInfo>,
    pub network: NetworkInfo,
    pub os: OsInfo,
    pub peripherals: Vec<PeripheralInfo>,
}

// ───────────────────────────── Status / metrics ─────────────────────────────

wire_enum! {
    /// Seat/PC status as shown on the club map.
    PcStatus {
        /// Agent not reachable.
        Offline = "offline",
        /// Online, no session.
        Free = "free",
        /// Session active.
        Busy = "busy",
        /// Locked (session locked or admin lock).
        Locked = "locked",
        /// Taken out of service by an admin.
        Maintenance = "maintenance",
        /// Reserved by a booking.
        Booked = "booked",
    }
}

wire_enum! {
    /// Agent ⇄ server connectivity.
    ConnectivityState {
        Online = "online",
        /// Offline mode (ARCHITECTURE.md §8).
        Offline = "offline",
    }
}

/// Temperatures in °C; 0 when unavailable.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Temperatures {
    #[serde(with = "crate::wire::num")]
    pub cpu: f64,
    #[serde(with = "crate::wire::num")]
    pub gpu: f64,
}

/// Network throughput in Mbit/s.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct NetworkThroughput {
    #[serde(with = "crate::wire::num")]
    pub up: f64,
    #[serde(with = "crate::wire::num")]
    pub down: f64,
}

/// One telemetry sample (IPC_PROTOCOL.md §6.7). Payload of `sys.metrics` and batched to
/// `POST /agents/{pcId}/telemetry`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct PcMetrics {
    /// CPU utilisation 0–100.
    #[serde(with = "crate::wire::num")]
    pub cpu_pct: f64,
    /// GPU utilisation 0–100; 0 if unavailable.
    #[serde(with = "crate::wire::num")]
    pub gpu_pct: f64,
    pub ram_used_mb: i32,
    pub temps: Temperatures,
    /// Frame rate from the running game's overlay hook; `None` when none.
    #[serde(default, with = "crate::wire::num_opt", skip_serializing_if = "Option::is_none")]
    pub fps: Option<f64>,
    pub net_mbps: NetworkThroughput,
    pub uptime_sec: i64,
    #[serde(with = "crate::wire::ts")]
    pub at: DateTime<Utc>,
}

// ───────────────────────────── Pc ─────────────────────────────

/// Gaming PC as known to the server (IPC_PROTOCOL.md §6.5).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Pc {
    pub id: Uuid,
    /// e.g. `PC-12`.
    pub name: String,
    /// e.g. `VIP`, `Standard`, `Bootcamp`.
    pub zone: String,
    /// Seat number.
    pub number: i32,
    /// Hardware id (sha256 hex); omitted for other PCs in club-wide lists.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub hwid: Option<String>,
    pub ip_address: String,
    pub status: PcStatus,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub current_session_id: Option<Uuid>,
    pub agent_version: String,
    /// `0.0.0` if unknown.
    pub shell_version: String,
    #[serde(with = "crate::wire::ts")]
    pub last_heartbeat_at: DateTime<Utc>,
}

/// Response of `GET /pcs`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct PcsResponse {
    pub items: Vec<Pc>,
}

/// Response of `sys.pcInfo` (IPC_PROTOCOL.md §6.21).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct PcInfo {
    pub pc: Pc,
    pub agent_version: String,
    pub shell_version: String,
    /// IPC protocol major.
    pub protocol_version: i32,
    pub uptime_sec: i64,
    /// Kiosk Windows account name.
    pub kiosk_user: String,
    pub connectivity: ConnectivityState,
    /// Agent's best estimate of server time.
    #[serde(with = "crate::wire::ts")]
    pub server_time: DateTime<Utc>,
    pub policy_version: i32,
}

// ───────────────────────────── Policy ─────────────────────────────

wire_enum! {
    /// Semantics of [`ProcessAllowlistPolicy::patterns`].
    AllowlistMode {
        /// Only matching processes may run.
        Allow = "allow",
        /// Matching processes are killed.
        Deny = "deny",
    }
}

/// Shell replacement for the kiosk user.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ShellReplacementPolicy {
    pub enabled: bool,
    pub shell_exe: String,
}

/// Process allow/deny list; patterns are wildcard image names (`*cheat*`).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ProcessAllowlistPolicy {
    pub mode: AllowlistMode,
    pub patterns: Vec<String>,
}

/// USB device policy.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct UsbPolicy {
    pub allow_storage: bool,
    pub allow_hid: bool,
}

/// DNS-based web filter.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct WebFilterPolicy {
    pub enabled: bool,
    pub blocked_domains: Vec<String>,
    /// Non-empty = allow-list mode.
    pub allowed_domains: Vec<String>,
    pub dns_servers: Vec<String>,
}

/// Explorer / input lockdown. Key combos use the grammar
/// `(Ctrl|Alt|Shift|Win)(\+(Ctrl|Alt|Shift|Win))*\+<Key>` with Win32 virtual-key names without `VK_`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ExplorerPolicy {
    pub disable_task_manager: bool,
    pub disable_run: bool,
    pub disable_settings: bool,
    pub hide_taskbar: bool,
    pub disable_alt_tab: bool,
    pub disable_win_key: bool,
    /// Additional blocked combos, e.g. `Ctrl+Shift+Esc`.
    pub blocked_key_combos: Vec<String>,
}

/// Power policy.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct PowerPolicy {
    /// Shut down after this many idle minutes; `None` = never.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub idle_shutdown_min: Option<i32>,
    /// Daily shutdown time (local `HH:mm`); `None` = none.
    #[serde(default, with = "crate::wire::hm_opt", skip_serializing_if = "Option::is_none")]
    pub scheduled_shutdown: Option<NaiveTime>,
}

/// Update policy (overrides `agent.json → updates`).
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct UpdatesPolicy {
    pub channel: UpdateChannel,
    /// Apply staged updates automatically when idle.
    pub auto_install: bool,
}

/// Anti-cheat policy.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct AntiCheatPolicy {
    /// Subsystems whose drivers/services must be healthy.
    pub required: Vec<AntiCheatKind>,
    /// Block launches / lock session on violations.
    pub block_on_violation: bool,
}

/// Kiosk UI policy.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct KioskPolicy {
    pub idle_timeout_sec: i32,
    pub ads_interval_sec: i32,
    pub allow_virtual_keyboard: bool,
}

/// Enforced PC policy (IPC_PROTOCOL.md §6.18, ARCHITECTURE.md §12.3). Snapshot persisted to
/// `policies.json`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Policy {
    /// Monotonic server-assigned version.
    pub version: i32,
    #[serde(with = "crate::wire::ts")]
    pub updated_at: DateTime<Utc>,
    pub shell_replacement: ShellReplacementPolicy,
    pub process_allowlist: ProcessAllowlistPolicy,
    pub usb: UsbPolicy,
    pub web_filter: WebFilterPolicy,
    pub explorer: ExplorerPolicy,
    pub power: PowerPolicy,
    pub updates: UpdatesPolicy,
    /// Wire key `anticheat`.
    pub anticheat: AntiCheatPolicy,
    pub kiosk: KioskPolicy,
}

// ---- BEGIN MANUAL ----
impl Policy {
    /// Top-level policy keys, in wire order; used for `policy.changed.changed`.
    pub const SECTION_KEYS: [&'static str; 9] = [
        "shellReplacement",
        "processAllowlist",
        "usb",
        "webFilter",
        "explorer",
        "power",
        "updates",
        "anticheat",
        "kiosk",
    ];

    /// Names of the top-level sections whose value differs between `self` and `other`.
    pub fn diff_sections(&self, other: &Policy) -> Vec<&'static str> {
        let flags = [
            self.shell_replacement != other.shell_replacement,
            self.process_allowlist != other.process_allowlist,
            self.usb != other.usb,
            self.web_filter != other.web_filter,
            self.explorer != other.explorer,
            self.power != other.power,
            self.updates != other.updates,
            self.anticheat != other.anticheat,
            self.kiosk != other.kiosk,
        ];
        Self::SECTION_KEYS.iter().zip(flags).filter(|(_, changed)| *changed).map(|(k, _)| *k).collect()
    }
}
// ---- END MANUAL ----

// ───────────────────────────── Theme ─────────────────────────────

/// Theme palette; every value is `#RRGGBB` or `#RRGGBBAA`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ThemeColors {
    pub bg: String,
    pub surface: String,
    pub primary: String,
    pub accent: String,
    pub text: String,
    pub muted: String,
    pub danger: String,
    pub success: String,
}

/// Shell theme file `themes\<name>.json` (ARCHITECTURE.md §12.4).
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Theme {
    /// Schema version (1).
    pub version: i32,
    /// Theme id; must equal the file name.
    pub name: String,
    pub display_name: String,
    pub colors: ThemeColors,
    /// Corner radius in px.
    pub radius: i32,
    /// Installed font family; falls back to `system-ui`.
    pub font: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub background_video: Option<String>,
    /// Wallpaper path (relative to ProgramData root) or absolute URL.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub wallpaper: Option<String>,
    /// Backdrop blur in px; 0 = off.
    pub blur: i32,
    pub animations: bool,
}

// ───────────────────────────── Agent server config ─────────────────────────────

/// Theme the Agent must download into `themes\`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ThemeRef {
    /// Theme id (file name without extension).
    pub name: String,
    pub url: String,
    /// Lower-case hex SHA-256 of the file.
    pub sha256: String,
}

/// Daily local-time window (`HH:mm`); `to < from` wraps midnight.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct TimeWindow {
    #[serde(with = "crate::wire::hm")]
    pub from: NaiveTime,
    #[serde(with = "crate::wire::hm")]
    pub to: NaiveTime,
}

/// Server override of `agent.json → updates`.
#[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct UpdatesConfigOverride {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub channel: Option<UpdateChannel>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub check_interval_sec: Option<i32>,
    /// Window in which non-mandatory updates may be applied.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub apply_window: Option<TimeWindow>,
}

/// Server override pushed into `shell.json`.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Default)]
#[serde(rename_all = "camelCase")]
pub struct ShellConfigOverride {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub locale: Option<Locale>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub theme: Option<String>,
    /// Partial `shell.json → features`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub features: Option<Value>,
    /// Partial `shell.json → ads`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ads: Option<Value>,
    /// Partial `shell.json → idle`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub idle: Option<Value>,
}

/// Server-side configuration overrides (`GET /agents/{pcId}/config`, SERVER_API.md §4.1), merged over
/// `agent.json` by the Agent (server wins for keys present). Partial sections mirror the `agent.json`
/// schema and are carried as raw JSON.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct AgentServerConfig {
    pub version: i32,
    pub pc_name: String,
    pub zone: String,
    /// Seat number.
    pub number: i32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub session: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub offline: Option<Value>,
    /// Partial `agent.json → games` (`libraryRoots`, `accountPool`, `cloudSave`).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub games: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub storage: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub updates: Option<UpdatesConfigOverride>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub telemetry: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub remote_admin: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub shell: Option<ShellConfigOverride>,
    /// Themes to download.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub themes: Option<Vec<ThemeRef>>,
    /// WebSocket URL override.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ws_url: Option<String>,
}

// ---- BEGIN MANUAL ----
#[cfg(test)]
pub(crate) fn sample_policy() -> Policy {
    use chrono::TimeZone;
    Policy {
        version: 12,
        updated_at: Utc.with_ymd_and_hms(2026, 9, 1, 8, 0, 0).unwrap(),
        shell_replacement: ShellReplacementPolicy { enabled: true, shell_exe: r"C:\Program Files\ClubShell\Shell\clubshell-shell.exe".into() },
        process_allowlist: ProcessAllowlistPolicy { mode: AllowlistMode::Deny, patterns: vec!["cmd.exe".into(), "*cheat*".into()] },
        usb: UsbPolicy { allow_storage: false, allow_hid: true },
        web_filter: WebFilterPolicy { enabled: true, blocked_domains: vec!["*.torrent-site.example".into()], allowed_domains: vec![], dns_servers: vec!["1.1.1.3".into()] },
        explorer: ExplorerPolicy {
            disable_task_manager: true,
            disable_run: true,
            disable_settings: true,
            hide_taskbar: true,
            disable_alt_tab: true,
            disable_win_key: true,
            blocked_key_combos: vec!["Ctrl+Shift+Esc".into(), "Win+R".into()],
        },
        power: PowerPolicy { idle_shutdown_min: None, scheduled_shutdown: None },
        updates: UpdatesPolicy { channel: UpdateChannel::Stable, auto_install: true },
        anticheat: AntiCheatPolicy { required: vec![], block_on_violation: true },
        kiosk: KioskPolicy { idle_timeout_sec: 300, ads_interval_sec: 900, allow_virtual_keyboard: true },
    }
}

#[cfg(test)]
pub(crate) const SAMPLE_POLICY_JSON: &str = r#"{"version":12,"updatedAt":"2026-09-01T08:00:00.000Z","shellReplacement":{"enabled":true,"shellExe":"C:\\Program Files\\ClubShell\\Shell\\clubshell-shell.exe"},"processAllowlist":{"mode":"deny","patterns":["cmd.exe","*cheat*"]},"usb":{"allowStorage":false,"allowHid":true},"webFilter":{"enabled":true,"blockedDomains":["*.torrent-site.example"],"allowedDomains":[],"dnsServers":["1.1.1.3"]},"explorer":{"disableTaskManager":true,"disableRun":true,"disableSettings":true,"hideTaskbar":true,"disableAltTab":true,"disableWinKey":true,"blockedKeyCombos":["Ctrl+Shift+Esc","Win+R"]},"power":{},"updates":{"channel":"stable","autoInstall":true},"anticheat":{"required":[],"blockOnViolation":true},"kiosk":{"idleTimeoutSec":300,"adsIntervalSec":900,"allowVirtualKeyboard":true}}"#;

#[cfg(test)]
mod tests {
    use super::*;
    use crate::error::assert_wire;
    use chrono::TimeZone;

    #[test]
    fn enum_wire_values() {
        assert_wire(DiskType::ALL, &["hdd", "ssd", "nvme", "network", "unknown"]);
        assert_wire(PcStatus::ALL, &["offline", "free", "busy", "locked", "maintenance", "booked"]);
        assert_wire(ConnectivityState::ALL, &["online", "offline"]);
        assert_wire(AllowlistMode::ALL, &["allow", "deny"]);
    }

    #[test]
    fn policy_json_matches_architecture_snapshot() {
        let p = sample_policy();
        assert_eq!(serde_json::to_string(&p).unwrap(), SAMPLE_POLICY_JSON);
        assert_eq!(serde_json::from_str::<Policy>(SAMPLE_POLICY_JSON).unwrap(), p);
        // ARCHITECTURE.md §12.3 writes explicit nulls and a second-precision timestamp.
        let doc = SAMPLE_POLICY_JSON
            .replace(r#""power":{}"#, r#""power":{"idleShutdownMin":null,"scheduledShutdown":null}"#)
            .replace("2026-09-01T08:00:00.000Z", "2026-09-01T08:00:00Z");
        assert_eq!(serde_json::from_str::<Policy>(&doc).unwrap(), p);

        let mut changed = p.clone();
        changed.power.scheduled_shutdown = NaiveTime::from_hms_opt(4, 0, 0);
        changed.kiosk.idle_timeout_sec = 600;
        assert_eq!(p.diff_sections(&changed), vec!["power", "kiosk"]);
        assert!(p.diff_sections(&p).is_empty());
        assert_eq!(serde_json::to_string(&changed.power).unwrap(), r#"{"scheduledShutdown":"04:00"}"#);
        assert_eq!(Policy::SECTION_KEYS.len(), 9);
    }

    #[test]
    fn hardware_and_metrics_json() {
        let hw = HardwareInfo {
            cpu: CpuInfo { model: "i7".into(), cores: 8, threads: 16 },
            gpu: vec![GpuInfo { model: "RTX 4070".into(), vram_mb: 12288, driver: "560.94".into() }],
            ram_mb: 32768,
            disks: vec![DiskInfo { mount: "C:".into(), total_gb: 931.5, free_gb: 400.0, r#type: DiskType::Nvme }],
            monitors: vec![MonitorInfo { index: 0, width: 2560, height: 1440, hz: 165, primary: true }],
            network: NetworkInfo { mac: "AA:BB:CC:DD:EE:FF".into(), ip: "10.0.1.12".into(), adapter: "Ethernet".into() },
            os: OsInfo { version: "Windows 11 Pro".into(), build: "26200.1234".into() },
            peripherals: vec![PeripheralInfo { kind: peripheral_kinds::MOUSE.into(), name: "G Pro".into(), vendor_id: "046d".into(), product_id: "c539".into() }],
        };
        let json = serde_json::to_string(&hw).unwrap();
        assert_eq!(
            json,
            r#"{"cpu":{"model":"i7","cores":8,"threads":16},"gpu":[{"model":"RTX 4070","vramMb":12288,"driver":"560.94"}],"ramMb":32768,"disks":[{"mount":"C:","totalGb":931.5,"freeGb":400,"type":"nvme"}],"monitors":[{"index":0,"width":2560,"height":1440,"hz":165,"primary":true}],"network":{"mac":"AA:BB:CC:DD:EE:FF","ip":"10.0.1.12","adapter":"Ethernet"},"os":{"version":"Windows 11 Pro","build":"26200.1234"},"peripherals":[{"kind":"mouse","name":"G Pro","vendorId":"046d","productId":"c539"}]}"#
        );
        assert_eq!(serde_json::from_str::<HardwareInfo>(&json).unwrap(), hw);

        let m = PcMetrics {
            cpu_pct: 12.5,
            gpu_pct: 0.0,
            ram_used_mb: 8000,
            temps: Temperatures { cpu: 45.0, gpu: 0.0 },
            fps: None,
            net_mbps: NetworkThroughput { up: 1.2, down: 30.0 },
            uptime_sec: 8123,
            at: Utc.with_ymd_and_hms(2026, 9, 21, 10, 0, 5).unwrap(),
        };
        let json = serde_json::to_string(&m).unwrap();
        assert_eq!(
            json,
            r#"{"cpuPct":12.5,"gpuPct":0,"ramUsedMb":8000,"temps":{"cpu":45,"gpu":0},"netMbps":{"up":1.2,"down":30},"uptimeSec":8123,"at":"2026-09-21T10:00:05.000Z"}"#
        );
        assert_eq!(serde_json::from_str::<PcMetrics>(&json).unwrap(), m);
        let with_fps: PcMetrics = serde_json::from_str(&json.replace(r#""ramUsedMb""#, r#""fps":144,"ramUsedMb""#)).unwrap();
        assert_eq!(with_fps.fps, Some(144.0));
    }

    #[test]
    fn pc_and_config_json() {
        let pc = Pc {
            id: Uuid::nil(),
            name: "PC-12".into(),
            zone: "Standard".into(),
            number: 12,
            hwid: None,
            ip_address: "10.0.1.12".into(),
            status: PcStatus::Busy,
            current_session_id: Some(Uuid::nil()),
            agent_version: "1.4.2".into(),
            shell_version: "0.0.0".into(),
            last_heartbeat_at: Utc.with_ymd_and_hms(2026, 9, 21, 10, 0, 0).unwrap(),
        };
        let json = serde_json::to_string(&pc).unwrap();
        assert_eq!(
            json,
            r#"{"id":"00000000-0000-0000-0000-000000000000","name":"PC-12","zone":"Standard","number":12,"ipAddress":"10.0.1.12","status":"busy","currentSessionId":"00000000-0000-0000-0000-000000000000","agentVersion":"1.4.2","shellVersion":"0.0.0","lastHeartbeatAt":"2026-09-21T10:00:00.000Z"}"#
        );
        assert_eq!(serde_json::from_str::<Pc>(&json).unwrap(), pc);

        let cfg = AgentServerConfig {
            version: 3,
            pc_name: "PC-12".into(),
            zone: "Standard".into(),
            number: 12,
            session: None,
            offline: Some(serde_json::json!({ "maxOfflineMinutes": 120 })),
            games: None,
            storage: None,
            updates: Some(UpdatesConfigOverride {
                channel: Some(UpdateChannel::Beta),
                check_interval_sec: None,
                apply_window: Some(TimeWindow { from: NaiveTime::from_hms_opt(4, 0, 0).unwrap(), to: NaiveTime::from_hms_opt(7, 0, 0).unwrap() }),
            }),
            telemetry: None,
            remote_admin: None,
            shell: Some(ShellConfigOverride { locale: Some(Locale::Uz), theme: None, features: None, ads: None, idle: None }),
            themes: Some(vec![ThemeRef { name: "neon".into(), url: "https://t/neon.json".into(), sha256: "ab".into() }]),
            ws_url: None,
        };
        let json = serde_json::to_string(&cfg).unwrap();
        assert_eq!(
            json,
            r#"{"version":3,"pcName":"PC-12","zone":"Standard","number":12,"offline":{"maxOfflineMinutes":120},"updates":{"channel":"beta","applyWindow":{"from":"04:00","to":"07:00"}},"shell":{"locale":"uz"},"themes":[{"name":"neon","url":"https://t/neon.json","sha256":"ab"}]}"#
        );
        assert_eq!(serde_json::from_str::<AgentServerConfig>(&json).unwrap(), cfg);
    }

    #[test]
    fn theme_json_matches_architecture_example() {
        let t = Theme {
            version: 1,
            name: "default".into(),
            display_name: "ClubShell Default".into(),
            colors: ThemeColors {
                bg: "#0B0F1A".into(),
                surface: "#141A2B".into(),
                primary: "#3B82F6".into(),
                accent: "#22D3EE".into(),
                text: "#F3F4F6".into(),
                muted: "#8B93A7".into(),
                danger: "#EF4444".into(),
                success: "#22C55E".into(),
            },
            radius: 12,
            font: "Inter".into(),
            background_video: None,
            wallpaper: Some("themes/assets/default-wallpaper.jpg".into()),
            blur: 12,
            animations: true,
        };
        let json = serde_json::to_string(&t).unwrap();
        assert_eq!(
            json,
            r#"{"version":1,"name":"default","displayName":"ClubShell Default","colors":{"bg":"#0B0F1A","surface":"#141A2B","primary":"#3B82F6","accent":"#22D3EE","text":"#F3F4F6","muted":"#8B93A7","danger":"#EF4444","success":"#22C55E"},"radius":12,"font":"Inter","wallpaper":"themes/assets/default-wallpaper.jpg","blur":12,"animations":true}"#
        );
        let file_form = json.replace(r#""wallpaper""#, r#""backgroundVideo":null,"wallpaper""#);
        assert_eq!(serde_json::from_str::<Theme>(&file_form).unwrap(), t);
    }
}
// ---- END MANUAL ----
