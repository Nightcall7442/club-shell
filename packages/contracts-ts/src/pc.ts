/**
 * PC contracts — mirror of `ClubShell.Contracts.Pcs` (HardwareInfo.cs, PcStatus.cs, PcInfo.cs).
 * `Policy` and its sections live in `commands.ts`.
 */
import type { JsonObject, UpdateChannel } from './commands.js';
import type { Locale } from './user.js';

/** Physical disk type. */
export const DiskType = {
  Hdd: 'hdd',
  Ssd: 'ssd',
  Nvme: 'nvme',
  Network: 'network',
  Unknown: 'unknown',
} as const;
/** Physical disk type. */
export type DiskType = (typeof DiskType)[keyof typeof DiskType];

/** Well-known values of `PeripheralInfo.kind`. */
export const PeripheralKinds = {
  Keyboard: 'keyboard',
  Mouse: 'mouse',
  Headset: 'headset',
  Gamepad: 'gamepad',
  Other: 'other',
} as const;
/** Well-known peripheral kind. */
export type PeripheralKind = (typeof PeripheralKinds)[keyof typeof PeripheralKinds];

/** CPU description. */
export interface CpuInfo {
  /** Model string from SMBIOS/WMI. */
  model: string;
  /** Physical cores. */
  cores: number;
  /** Logical processors. */
  threads: number;
}

/** GPU description. */
export interface GpuInfo {
  /** Adapter model. */
  model: string;
  /** Dedicated VRAM in MiB. */
  vramMb: number;
  /** Driver version. */
  driver: string;
}

/** Logical disk. */
export interface DiskInfo {
  /** Mount point, e.g. `C:`. */
  mount: string;
  /** Capacity in GB (1 fraction digit). */
  totalGb: number;
  /** Free space in GB (1 fraction digit). */
  freeGb: number;
  /** Physical type. */
  type: DiskType;
}

/** Attached monitor. */
export interface MonitorInfo {
  /** Display index (0-based). */
  index: number;
  /** Width in pixels. */
  width: number;
  /** Height in pixels. */
  height: number;
  /** Refresh rate. */
  hz: number;
  /** Primary display. */
  primary: boolean;
}

/** Primary network adapter. */
export interface NetworkInfo {
  /** MAC address `AA:BB:CC:DD:EE:FF`. */
  mac: string;
  /** IPv4 address. */
  ip: string;
  /** Adapter name. */
  adapter: string;
}

/** Operating system. */
export interface OsInfo {
  /** Product name, e.g. `Windows 11 Pro`. */
  version: string;
  /** Build, e.g. `26200.1234`. */
  build: string;
}

/** USB/HID peripheral. */
export interface PeripheralInfo {
  /** Kind ({@link PeripheralKinds}). */
  kind: string;
  /** Device name. */
  name: string;
  /** USB vendor id (hex). */
  vendorId: string;
  /** USB product id (hex). */
  productId: string;
}

/** Hardware inventory of a PC (IPC_PROTOCOL.md §6.6). */
export interface HardwareInfo {
  /** CPU. */
  cpu: CpuInfo;
  /** GPUs. */
  gpu: GpuInfo[];
  /** Installed RAM in MiB. */
  ramMb: number;
  /** Logical disks. */
  disks: DiskInfo[];
  /** Monitors. */
  monitors: MonitorInfo[];
  /** Primary adapter. */
  network: NetworkInfo;
  /** Operating system. */
  os: OsInfo;
  /** Peripherals. */
  peripherals: PeripheralInfo[];
}

/** Seat/PC status as shown on the club map. */
export const PcStatus = {
  Offline: 'offline',
  Free: 'free',
  Busy: 'busy',
  Locked: 'locked',
  Maintenance: 'maintenance',
  Booked: 'booked',
} as const;
/** Seat/PC status as shown on the club map. */
export type PcStatus = (typeof PcStatus)[keyof typeof PcStatus];

/** Agent ⇄ server connectivity. */
export const ConnectivityState = {
  Online: 'online',
  Offline: 'offline',
} as const;
/** Agent ⇄ server connectivity. */
export type ConnectivityState = (typeof ConnectivityState)[keyof typeof ConnectivityState];

/** Temperatures in °C; 0 when unavailable. */
export interface Temperatures {
  /** CPU package temperature. */
  cpu: number;
  /** GPU temperature. */
  gpu: number;
}

/** Network throughput in Mbit/s. */
export interface NetworkThroughput {
  /** Upload. */
  up: number;
  /** Download. */
  down: number;
}

/** One telemetry sample (IPC_PROTOCOL.md §6.7); payload of `sys.metrics`. */
export interface PcMetrics {
  /** CPU utilisation 0–100. */
  cpuPct: number;
  /** GPU utilisation 0–100; 0 if unavailable. */
  gpuPct: number;
  /** RAM in use, MiB. */
  ramUsedMb: number;
  /** Temperatures. */
  temps: Temperatures;
  /** Frame rate from the running game's overlay hook; null when none. */
  fps?: number | null;
  /** Network throughput. */
  netMbps: NetworkThroughput;
  /** OS uptime in seconds. */
  uptimeSec: number;
  /** Sample time. */
  at: string;
}

/** Gaming PC as known to the server (IPC_PROTOCOL.md §6.5). */
export interface Pc {
  /** PC id. */
  id: string;
  /** Name, e.g. `PC-12`. */
  name: string;
  /** Zone, e.g. `VIP`, `Standard`, `Bootcamp`. */
  zone: string;
  /** Seat number. */
  number: number;
  /** Hardware id (sha256 hex); omitted for other PCs in club-wide lists. */
  hwid?: string | null;
  /** IPv4 address. */
  ipAddress: string;
  /** Status. */
  status: PcStatus;
  /** Open session, when any. */
  currentSessionId?: string | null;
  /** Agent semver. */
  agentVersion: string;
  /** Shell semver; `0.0.0` if unknown. */
  shellVersion: string;
  /** Last heartbeat received by the server. */
  lastHeartbeatAt: string;
}

/** Response of `GET /pcs`. */
export interface PcsResponse {
  /** PCs of the club (optionally filtered by zone). */
  items: Pc[];
}

/** Response of `sys.pcInfo` (IPC_PROTOCOL.md §6.21). */
export interface PcInfo {
  /** The PC. */
  pc: Pc;
  /** Agent semver. */
  agentVersion: string;
  /** Shell semver. */
  shellVersion: string;
  /** IPC protocol major. */
  protocolVersion: number;
  /** OS uptime. */
  uptimeSec: number;
  /** Kiosk Windows account name. */
  kioskUser: string;
  /** Server connectivity. */
  connectivity: ConnectivityState;
  /** Agent's best estimate of server time. */
  serverTime: string;
  /** Applied policy version. */
  policyVersion: number;
}

/** Theme palette; every value is `#RRGGBB` or `#RRGGBBAA`. */
export interface ThemeColors {
  /** Page background. */
  bg: string;
  /** Card / panel background. */
  surface: string;
  /** Primary action colour. */
  primary: string;
  /** Accent colour. */
  accent: string;
  /** Primary text. */
  text: string;
  /** Secondary text. */
  muted: string;
  /** Errors / destructive actions. */
  danger: string;
  /** Success state. */
  success: string;
}

/** Shell theme file `themes\<name>.json` (ARCHITECTURE.md §12.4). */
export interface Theme {
  /** Schema version (1). */
  version: number;
  /** Theme id; must equal the file name. */
  name: string;
  /** Human-readable name. */
  displayName: string;
  /** Palette. */
  colors: ThemeColors;
  /** Corner radius in px. */
  radius: number;
  /** Installed font family; falls back to `system-ui`. */
  font: string;
  /** Background video URL/path. */
  backgroundVideo?: string | null;
  /** Wallpaper path (relative to ProgramData root) or absolute URL. */
  wallpaper?: string | null;
  /** Backdrop blur in px; 0 = off. */
  blur: number;
  /** Enable UI animations. */
  animations: boolean;
}

/** Theme the Agent must download into `themes\`. */
export interface ThemeRef {
  /** Theme id (file name without extension). */
  name: string;
  /** Download URL. */
  url: string;
  /** Lower-case hex SHA-256 of the file. */
  sha256: string;
}

/** Daily local-time window (`HH:mm`); `to` < `from` wraps midnight. */
export interface TimeWindow {
  /** Start. */
  from: string;
  /** End. */
  to: string;
}

/** Server override of `agent.json → updates`. */
export interface UpdatesConfigOverride {
  /** Channel. */
  channel?: UpdateChannel | null;
  /** Manifest check interval. */
  checkIntervalSec?: number | null;
  /** Window in which non-mandatory updates may be applied. */
  applyWindow?: TimeWindow | null;
}

/** Server override pushed into `shell.json`. */
export interface ShellConfigOverride {
  /** Default locale. */
  locale?: Locale | null;
  /** Theme id. */
  theme?: string | null;
  /** Partial `shell.json → features`. */
  features?: JsonObject | null;
  /** Partial `shell.json → ads`. */
  ads?: JsonObject | null;
  /** Partial `shell.json → idle`. */
  idle?: JsonObject | null;
}

/** Server-side configuration overrides (`GET /agents/{pcId}/config`), merged over `agent.json` by the Agent. */
export interface AgentServerConfig {
  /** Config version. */
  version: number;
  /** PC name. */
  pcName: string;
  /** Zone. */
  zone: string;
  /** Seat number. */
  number: number;
  /** Partial `agent.json → session`. */
  session?: JsonObject | null;
  /** Partial `agent.json → offline`. */
  offline?: JsonObject | null;
  /** Partial `agent.json → games` (`libraryRoots`, `accountPool`, `cloudSave`). */
  games?: JsonObject | null;
  /** Partial `agent.json → storage`. */
  storage?: JsonObject | null;
  /** Update overrides. */
  updates?: UpdatesConfigOverride | null;
  /** Partial `agent.json → telemetry`. */
  telemetry?: JsonObject | null;
  /** Partial `agent.json → remoteAdmin`. */
  remoteAdmin?: JsonObject | null;
  /** Shell overrides. */
  shell?: ShellConfigOverride | null;
  /** Themes to download. */
  themes?: ThemeRef[] | null;
  /** WebSocket URL override. */
  wsUrl?: string | null;
}
