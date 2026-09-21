/**
 * Agent → Shell event payloads (IPC_PROTOCOL.md §8) plus `Notification`; mirror of the "Event payloads" region of
 * `ClubShell.Contracts.Ipc.IpcMessage.cs` and the notification types of `Users/UserProfile.cs`.
 * The Shell re-emits each event as Tauri `agent://<name>` with the same payload.
 */
import type { IpcError, JsonObject, Policy, UpdateComponent, UpdateManifest, UpdatePhase } from './commands.js';
import type { GameState } from './games.js';
import type { ConnectivityState } from './pc.js';
import type { AuthExpiredReason } from './user.js';

// ---- BEGIN MANUAL ----
import { IpcNames, type LockCommand } from './commands.js';
import type { PcMetrics } from './pc.js';
import type { Session, SessionEndedEvent, SessionWarning } from './session.js';
import type { Order } from './shop.js';
import type { ChatMessage } from './user.js';
import type { Balance } from './wallet.js';
// ---- END MANUAL ----

// ---------------------------------------------------------------------------------------------------------------------
// Notifications
// ---------------------------------------------------------------------------------------------------------------------

/** Notification severity. */
export const NotificationLevel = {
  Info: 'info',
  Warning: 'warning',
  Error: 'error',
  Success: 'success',
} as const;
/** Notification severity. */
export type NotificationLevel = (typeof NotificationLevel)[keyof typeof NotificationLevel];

/** Optional call-to-action of a {@link Notification}. */
export interface NotificationAction {
  /** Button label. */
  label: string;
  /** Frontend route (e.g. `/shop`) or an `AgentCommand` IPC name (e.g. `wallet.topupIntent`). */
  command: string;
  /** Arguments for `command`. */
  args?: JsonObject | null;
}

/** Toast/notification (IPC_PROTOCOL.md §6.21); payload of `notification.push`. */
export interface Notification {
  /** Notification id. */
  id: string;
  /** Title. */
  title: string;
  /** Body text. */
  body: string;
  /** Severity. */
  level: NotificationLevel;
  /** Auto-dismiss after this many seconds; null = sticky. */
  ttlSec?: number | null;
  /** Optional call-to-action. */
  action?: NotificationAction | null;
}

// ---------------------------------------------------------------------------------------------------------------------
// Event payloads
// ---------------------------------------------------------------------------------------------------------------------

/** Remote-control state (`admin.remoteControl`). */
export const RemoteControlState = {
  Started: 'started',
  Stopped: 'stopped',
} as const;
/** Remote-control state (`admin.remoteControl`). */
export type RemoteControlState = (typeof RemoteControlState)[keyof typeof RemoteControlState];

/** UI command carried by `shell.command`. */
export const ShellCommandKind = {
  Lock: 'lock',
  Unlock: 'unlock',
  Reboot: 'reboot',
  ShowAds: 'showAds',
  ShowMessage: 'showMessage',
} as const;
/** UI command carried by `shell.command`. */
export type ShellCommandKind = (typeof ShellCommandKind)[keyof typeof ShellCommandKind];

/** Media type of an ad item. */
export const AdMediaType = {
  Image: 'image',
  Video: 'video',
} as const;
/** Media type of an ad item. */
export type AdMediaType = (typeof AdMediaType)[keyof typeof AdMediaType];

/** Payload of `admin.message`; ack via `sys.ackAdminMessage`. */
export interface AdminMessage {
  /** Message id. */
  id: string;
  /** Sender name. */
  from: string;
  /** Text. */
  text: string;
  /** Severity. */
  level: NotificationLevel;
  /** User must acknowledge (modal). */
  requiresAck: boolean;
  /** Receipt time. */
  at: string;
}

/** Payload of `admin.remoteControl`. */
export interface RemoteControlEvent {
  /** Started/stopped. */
  state: RemoteControlState;
  /** Transition time. */
  at: string;
  /** Show the on-screen indicator. */
  showIndicator: boolean;
  /** Admin name, when known. */
  adminName?: string | null;
}

/** Payload of `game.stateChanged`. */
export interface GameStateChanged {
  /** Game. */
  gameId: string;
  /** Game title. */
  title: string;
  /** New state. */
  state: GameState;
  /** Transition time. */
  at: string;
  /** Process id, when known. */
  pid?: number | null;
  /** Exit code (`exited`). */
  exitCode?: number | null;
  /** Failure (`failed`). */
  error?: IpcError | null;
}

/** Payload of `policy.changed`. */
export interface PolicyChanged {
  /** Policy version. */
  version: number;
  /** Policy timestamp. */
  updatedAt: string;
  /** Top-level sections that changed. */
  changed: string[];
  /** Policy now in effect. */
  policy: Policy;
}

/** Payload of `update.available`. */
export interface UpdateAvailable {
  /** Newer package. */
  manifest: UpdateManifest;
  /** Installed version of that component. */
  current: string;
}

/** Payload of `update.progress` (≤ 2/s). */
export interface UpdateProgress {
  /** Component. */
  component: UpdateComponent;
  /** Package version. */
  version: string;
  /** Phase. */
  phase: UpdatePhase;
  /** 0–100. */
  percent: number;
  /** Bytes processed. */
  bytesDone: number;
  /** Total bytes. */
  bytesTotal: number;
  /** Failure (`failed`). */
  error?: IpcError | null;
}

/** Payload of `update.ready` (package staged and verified). */
export interface UpdateReady {
  /** Component. */
  component: UpdateComponent;
  /** Package version. */
  version: string;
  /** Whether applying restarts the component. */
  restartRequired: boolean;
  /** Will be applied even during a session. */
  mandatory: boolean;
  /** Scheduled apply time, when known. */
  applyAt?: string | null;
}

/** Payload of `sys.connectivity` (transitions + every 60 s). */
export interface ConnectivityEvent {
  /** Connectivity. */
  state: ConnectivityState;
  /** When the state was entered. */
  since: string;
  /** Outbox size. */
  queuedEvents: number;
  /** Last heartbeat latency, when online. */
  serverLatencyMs?: number | null;
}

/** Args of `shell.command{reboot}` (informational; the Agent performs the reboot). */
export interface ShellRebootArgs {
  /** Seconds until reboot. */
  delaySec: number;
  /** Message to display. */
  message?: string | null;
}

/** One ad in a {@link ShowAdsArgs} playlist. */
export interface AdItem {
  /** Media URL. */
  url: string;
  /** Media type. */
  type: AdMediaType;
  /** Display duration. */
  durationSec: number;
}

/** Args of `shell.command{showAds}` and payload of the `showAds` server command. */
export interface ShowAdsArgs {
  /** Playlist. */
  items: AdItem[];
  /** User may skip. */
  skippable: boolean;
}

/** Args of `shell.command{showMessage}`. */
export interface ShowMessageArgs {
  /** Title. */
  title: string;
  /** Body. */
  body: string;
  /** Severity. */
  level: NotificationLevel;
  /** Auto-dismiss after this many seconds. */
  ttlSec?: number | null;
}

// ---- BEGIN MANUAL ----
/** Typed `args` per {@link ShellCommandKind}. */
export interface ShellCommandArgsMap {
  lock: LockCommand | null;
  unlock: null;
  reboot: ShellRebootArgs;
  showAds: ShowAdsArgs;
  showMessage: ShowMessageArgs;
}

/** Payload of `shell.command`; discriminated on `command`, `args` key always present. */
export type ShellCommand = {
  [K in ShellCommandKind]: {
    /** Command kind. */
    command: K;
    /** Typed args or null. */
    args: ShellCommandArgsMap[K];
    /** Originating server command id (or a fresh id for Agent-originated commands). */
    commandId: string;
  };
}[ShellCommandKind];
// ---- END MANUAL ----

/** Payload of `auth.expired`; the Shell returns to the login screen. */
export interface AuthExpired {
  /** Why the user context was invalidated. */
  reason: AuthExpiredReason;
}

// ---------------------------------------------------------------------------------------------------------------------
// Event name → payload
// ---------------------------------------------------------------------------------------------------------------------

// ---- BEGIN MANUAL ----
/** Payload type per Agent → Shell event name (IPC_PROTOCOL.md §8). */
export interface AgentEventMap {
  [IpcNames.Events.SessionUpdated]: Session;
  [IpcNames.Events.SessionWarning]: SessionWarning;
  [IpcNames.Events.SessionEnded]: SessionEndedEvent;
  [IpcNames.Events.WalletUpdated]: Balance;
  [IpcNames.Events.ChatMessage]: ChatMessage;
  [IpcNames.Events.NotificationPush]: Notification;
  [IpcNames.Events.AdminMessage]: AdminMessage;
  [IpcNames.Events.AdminRemoteControl]: RemoteControlEvent;
  [IpcNames.Events.GameStateChanged]: GameStateChanged;
  [IpcNames.Events.PolicyChanged]: PolicyChanged;
  [IpcNames.Events.UpdateAvailable]: UpdateAvailable;
  [IpcNames.Events.UpdateProgress]: UpdateProgress;
  [IpcNames.Events.UpdateReady]: UpdateReady;
  [IpcNames.Events.SysMetrics]: PcMetrics;
  [IpcNames.Events.SysConnectivity]: ConnectivityEvent;
  [IpcNames.Events.ShellCommand]: ShellCommand;
  [IpcNames.Events.AuthExpired]: AuthExpired;
  [IpcNames.Events.ShopOrderUpdated]: Order;
}

/** Agent → Shell event name (`'session.updated' | 'session.warning' | ...`). */
export type AgentEventName = keyof AgentEventMap;

/** Discriminated union of every Agent → Shell event: `{ name, payload }`. */
export type ShellEvent = {
  [K in AgentEventName]: {
    /** Event name. */
    name: K;
    /** Typed payload. */
    payload: AgentEventMap[K];
  };
}[AgentEventName];

/** All Agent → Shell event names in declaration order. */
export const AGENT_EVENT_NAMES: readonly AgentEventName[] = Object.values(IpcNames.Events);

const AGENT_EVENT_NAME_SET: ReadonlySet<string> = new Set<string>(AGENT_EVENT_NAMES);

/** `true` when `name` is a known Agent → Shell event name. */
export function isAgentEventName(name: string): name is AgentEventName {
  return AGENT_EVENT_NAME_SET.has(name);
}
// ---- END MANUAL ----
