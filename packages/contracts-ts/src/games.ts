/**
 * Game contracts — mirror of `ClubShell.Contracts.Games` (LauncherType.cs, GameInfo.cs, LaunchRequest.cs).
 */
import type { IpcError, JsonObject } from './commands.js';

/** Game launcher / store client used to start a game. */
export const LauncherType = {
  Steam: 'steam',
  Epic: 'epic',
  BattleNet: 'battleNet',
  Riot: 'riot',
  Ea: 'ea',
  Ubisoft: 'ubisoft',
  Exe: 'exe',
} as const;
/** Game launcher / store client used to start a game. */
export type LauncherType = (typeof LauncherType)[keyof typeof LauncherType];

/** Anti-cheat subsystem a game depends on. */
export const AntiCheatKind = {
  None: 'none',
  Eac: 'eac',
  BattlEye: 'battlEye',
  Vanguard: 'vanguard',
  Faceit: 'faceit',
  Ricochet: 'ricochet',
} as const;
/** Anti-cheat subsystem a game depends on. */
export type AntiCheatKind = (typeof AntiCheatKind)[keyof typeof AntiCheatKind];

/** Severity of an anti-cheat finding. */
export const AntiCheatSeverity = {
  Info: 'info',
  Warning: 'warning',
  Critical: 'critical',
} as const;
/** Severity of an anti-cheat finding. */
export type AntiCheatSeverity = (typeof AntiCheatSeverity)[keyof typeof AntiCheatSeverity];

/** Action the Agent took in response to an anti-cheat finding. */
export const AntiCheatAction = {
  None: 'none',
  BlockedLaunch: 'blockedLaunch',
  KilledGame: 'killedGame',
  LockedSession: 'lockedSession',
} as const;
/** Action the Agent took in response to an anti-cheat finding. */
export type AntiCheatAction = (typeof AntiCheatAction)[keyof typeof AntiCheatAction];

/** Well-known values of `AntiCheatReport.check` (SERVER_API.md §4.14). */
export const AntiCheatChecks = {
  DriverMissing: 'driverMissing',
  ServiceStopped: 'serviceStopped',
  SecureBootOff: 'secureBootOff',
  TpmOff: 'tpmOff',
  HvciOff: 'hvciOff',
  TestSigningOn: 'testSigningOn',
  BlockedProcess: 'blockedProcess',
  InjectedModule: 'injectedModule',
  VmDetected: 'vmDetected',
  DebuggerAttached: 'debuggerAttached',
} as const;
/** Well-known anti-cheat check identifier. */
export type AntiCheatCheck = (typeof AntiCheatChecks)[keyof typeof AntiCheatChecks];

/** Result of the pre-launch anti-cheat check, embedded in a {@link LaunchReport}. */
export interface AntiCheatCheckResult {
  /** Subsystem checked. */
  kind: AntiCheatKind;
  /** Whether prerequisites were satisfied. */
  ok: boolean;
  /** Failed check ({@link AntiCheatChecks}) when `ok` is false. */
  reason?: string | null;
}

/** Body of `POST /anticheat/report` and payload of the `anticheatViolation` agent event. */
export interface AntiCheatReport {
  /** Reporting PC. */
  pcId: string;
  /** Session during which the finding occurred, when any. */
  sessionId?: string | null;
  /** Logged-in user, when any. */
  userId?: string | null;
  /** Game concerned, when any. */
  gameId?: string | null;
  /** Subsystem. */
  kind: AntiCheatKind;
  /** Check identifier ({@link AntiCheatChecks}). */
  check: string;
  /** Severity. */
  severity: AntiCheatSeverity;
  /** Structured evidence; never contains credentials. */
  details: JsonObject;
  /** Time of the finding. */
  at: string;
  /** Action the Agent took. */
  actionTaken: AntiCheatAction;
}

/** Sort order for `games.list`. */
export const GamesSort = {
  Popularity: 'popularity',
  Title: 'title',
  LastPlayed: 'lastPlayed',
} as const;
/** Sort order for `games.list`. */
export type GamesSort = (typeof GamesSort)[keyof typeof GamesSort];

/** Minimum hardware specification of a game. */
export interface GameMinSpec {
  /** CPU description. */
  cpu: string;
  /** GPU description. */
  gpu: string;
  /** RAM in MiB. */
  ramMb: number;
}

/** Catalogue game (IPC_PROTOCOL.md §6.8); `installed`/`installPath` are filled by the Agent's local scan. */
export interface Game {
  /** Game id. */
  id: string;
  /** Localized title. */
  title: string;
  /** Launcher used to start it. */
  launcher: LauncherType;
  /** Steam appid, Epic namespace:item, Battle.net code, etc. */
  launcherAppId?: string | null;
  /** Executable; required for `exe`; may be relative to `installPath`. */
  exePath?: string | null;
  /** Extra command line. */
  args?: string | null;
  /** Resolved local install directory. */
  installPath?: string | null;
  /** Local scan result. */
  installed: boolean;
  /** Categories. */
  category: string[];
  /** Tags. */
  tags: string[];
  /** Cover image (2:3). */
  coverUrl: string;
  /** Wide hero image. */
  heroUrl?: string | null;
  /** Trailer / background video. */
  videoUrl?: string | null;
  /** Localized description; may be empty. */
  description: string;
  /** Minimum age; 0 = none. */
  ageRating: number;
  /** Server rank score. */
  popularity: number;
  /** Last launch by the current user. */
  lastPlayedAt?: string | null;
  /** Needs an account-pool lease. */
  requiresAccount: boolean;
  /** Anti-cheat subsystem. */
  antiCheat: AntiCheatKind;
  /** Minimum spec. */
  minSpec?: GameMinSpec | null;
  /** Install size in GB (1 fraction digit). */
  sizeGb: number;
  /** Installed/catalogue version. */
  version?: string | null;
}

/** Response of `games.installStatus`. */
export interface GameInstallStatus {
  /** Game id. */
  gameId: string;
  /** Install present and verified. */
  installed: boolean;
  /** Resolved install directory. */
  installPath?: string | null;
  /** Size on disk in GB. */
  sizeGb: number;
  /** Detected version. */
  version?: string | null;
  /** Last successful verification. */
  verifiedAt?: string | null;
  /** Launcher client present and logged in (where detectable). */
  launcherReady: boolean;
}

/** Well-known values of `App.category`. */
export const AppCategories = {
  Browser: 'browser',
  Voice: 'voice',
  Media: 'media',
  Tool: 'tool',
} as const;
/** Well-known app category. */
export type AppCategory = (typeof AppCategories)[keyof typeof AppCategories];

/** Non-game application allowed on the kiosk (IPC_PROTOCOL.md §6.10). */
export interface App {
  /** App id. */
  id: string;
  /** Title. */
  title: string;
  /** Executable path (allow-listed by the Agent). */
  exePath: string;
  /** Icon URL. */
  iconUrl: string;
  /** Category ({@link AppCategories}). */
  category: string;
  /** Allowed after policy evaluation. */
  allowed: boolean;
}

/** Lifecycle state of a launched game process. */
export const GameState = {
  Launching: 'launching',
  Running: 'running',
  Exited: 'exited',
  Failed: 'failed',
  Killed: 'killed',
} as const;
/** Lifecycle state of a launched game process. */
export type GameState = (typeof GameState)[keyof typeof GameState];

// ---- BEGIN MANUAL ----
/** `true` for terminal states (`exited`, `failed`, `killed`). */
export function isGameStateTerminal(state: GameState): boolean {
  return state === GameState.Exited || state === GameState.Failed || state === GameState.Killed;
}
// ---- END MANUAL ----

/** Screen resolution requested for a launch. */
export interface Resolution {
  /** Width in pixels. */
  width: number;
  /** Height in pixels. */
  height: number;
}

/** Full launch request as executed by the Agent (IPC_PROTOCOL.md §6.9); payload of the `launchGame` server command. */
export interface LaunchRequest {
  /** Game to launch. */
  gameId: string;
  /** Active session. */
  sessionId: string;
  /** Player. */
  userId: string;
  /** Lease a pooled account and inject credentials. */
  useAccountPool: boolean;
  /** Reuse an existing lease. */
  accountLeaseId?: string | null;
  /** Extra command line (policy-sanitized). */
  extraArgs?: string | null;
  /** Requested resolution. */
  resolution?: Resolution | null;
  /** Seconds to wait for the game process/window before failing. */
  launchTimeoutSec: number;
}

/** Outcome of a launch (IPC_PROTOCOL.md §6.9); over IPC failures are returned as errors, so `ok` is always true there. */
export interface LaunchResult {
  /** Success flag. */
  ok: boolean;
  /** Game process id when started. */
  pid?: number | null;
  /** Launch time. */
  startedAt: string;
  /** Account-pool lease used, when any. */
  accountLeaseId?: string | null;
  /** Failure details when `ok` is false. */
  error?: IpcError | null;
}

/** Game process tracked by the Agent (IPC_PROTOCOL.md §6.9). */
export interface RunningGame {
  /** Game id. */
  gameId: string;
  /** Game title. */
  title: string;
  /** Process id. */
  pid: number;
  /** Launch time. */
  startedAt: string;
  /** Account-pool lease in use, when any. */
  accountLeaseId?: string | null;
  /** Current state. */
  state: GameState;
}

/** Pre-signed download of a user's cloud-save bundle. */
export interface CloudSaveDownload {
  /** Pre-signed HTTPS URL. */
  url: string;
  /** Lower-case hex SHA-256 of the bundle. */
  sha256: string;
  /** Bundle size. */
  sizeBytes: number;
}

/** Account-pool lease (`GET /games/{id}/accounts/lease`); `secret` is AES-256-GCM encrypted, never logged. */
export interface AccountLease {
  /** Lease id. */
  leaseId: string;
  /** Launcher the credentials belong to. */
  launcher: LauncherType;
  /** Account login. */
  username: string;
  /** Encrypted password or token (base64 `nonce||ciphertext||tag`). */
  secret: string;
  /** Launcher-specific extras (`steamGuardSecret`, `authenticatorSeed`, `region`). */
  extra?: JsonObject | null;
  /** Lease TTL. */
  expiresAt: string;
  /** Save bundle to restore before launch, when any. */
  cloudSave?: CloudSaveDownload | null;
}

/** Why an account-pool lease is released. */
export const AccountLeaseReleaseReason = {
  Exit: 'exit',
  SessionEnd: 'sessionEnd',
  LaunchFailed: 'launchFailed',
  Manual: 'manual',
} as const;
/** Why an account-pool lease is released. */
export type AccountLeaseReleaseReason = (typeof AccountLeaseReleaseReason)[keyof typeof AccountLeaseReleaseReason];

/** Cloud-save bundle the Agent uploaded before releasing a lease. */
export interface CloudSaveUpload {
  /** Pre-signed URL the bundle was `PUT` to. */
  uploadUrl?: string | null;
  /** Lower-case hex SHA-256 of the bundle. */
  sha256: string;
  /** Bundle size. */
  sizeBytes: number;
}

/** Body of `POST /games/{id}/accounts/{leaseId}/release`. */
export interface AccountLeaseRelease {
  /** Release reason. */
  reason: AccountLeaseReleaseReason;
  /** Uploaded save bundle, when cloud save is enabled. */
  cloudSave?: CloudSaveUpload | null;
}

/** Response of `GET /games/{id}/accounts/{leaseId}/save-upload`. */
export interface SaveUploadTarget {
  /** Pre-signed `PUT` URL. */
  uploadUrl: string;
  /** URL expiry. */
  expiresAt: string;
  /** Maximum accepted bundle size. */
  maxBytes: number;
}

/** Which lifecycle point a {@link LaunchReport} describes. */
export const LaunchReportPhase = {
  Launch: 'launch',
  Exit: 'exit',
} as const;
/** Which lifecycle point a {@link LaunchReport} describes. */
export type LaunchReportPhase = (typeof LaunchReportPhase)[keyof typeof LaunchReportPhase];

/** Body of `POST /games/{id}/launch-report` (SERVER_API.md §4.6). */
export interface LaunchReport {
  /** Session. */
  sessionId: string;
  /** Player. */
  userId: string;
  /** Launch outcome. */
  result: LaunchResult;
  /** Launch latency in milliseconds. */
  durationMs: number;
  /** Launcher used. */
  launcher: LauncherType;
  /** Pre-launch anti-cheat check. */
  antiCheat: AntiCheatCheckResult;
  /** Lifecycle point. */
  phase: LaunchReportPhase;
  /** Process exit code (`exit` phase). */
  exitCode?: number | null;
  /** Seconds played (`exit` phase). */
  playedSec?: number | null;
}
