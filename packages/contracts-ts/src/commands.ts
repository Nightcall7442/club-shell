/**
 * IPC / command contracts — mirror of `ClubShell.Contracts.{Errors,Ipc,Commands}` plus `Policy` (Pcs).
 * Covers the pipe envelope, error codes, every Shell → Agent request/response payload, server commands,
 * agent events, WebSocket frames and the agent-facing REST bodies.
 */
import type { NotificationLevel } from './events.js';
import type { AntiCheatKind, App, GamesSort, LauncherType, Resolution, RunningGame } from './games.js';
import type { AgentServerConfig, ConnectivityState, HardwareInfo, Pc, PcMetrics, PcStatus } from './pc.js';
import type { Session, SessionEndReason, SessionState } from './session.js';
import type { Order, OrderLineRequest, Product, ProductCategory } from './shop.js';
import type {
  Achievement,
  AuthKind,
  Booking,
  ChatMessage,
  LeaderboardEntry,
  Locale,
  Seat,
  Tournament,
  TournamentState,
  User,
} from './user.js';
import type { Money, Tariff, TopupProvider, TransactionType } from './wallet.js';

// ---- BEGIN MANUAL ----
import type { Notification, ShowAdsArgs } from './events.js';
import type { AntiCheatReport, Game, GameInstallStatus, LaunchRequest, LaunchResult } from './games.js';
import type { PcInfo } from './pc.js';
import type { SessionEndResult, SessionEndedEvent, SessionStartedEvent } from './session.js';
import type { Loyalty, ProfileUpdateRequest, QrLoginStart, UserStats } from './user.js';
import type { Balance, TopupIntent, Transaction } from './wallet.js';
// ---- END MANUAL ----

/** Arbitrary JSON object slot (`JsonElement` in C#); never an array or scalar at top level. */
export type JsonObject = Record<string, unknown>;

// ---- BEGIN MANUAL ----
function isRecord(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
// ---- END MANUAL ----

// ---------------------------------------------------------------------------------------------------------------------
// Error codes
// ---------------------------------------------------------------------------------------------------------------------

/** Error codes shared verbatim by IPC, the server REST envelope and the Tauri `ShellError` (IPC_PROTOCOL.md §5). */
export const ErrorCode = {
  Unauthorized: 'unauthorized',
  Forbidden: 'forbidden',
  NotFound: 'notFound',
  Validation: 'validation',
  Conflict: 'conflict',
  InsufficientFunds: 'insufficientFunds',
  SessionNotActive: 'sessionNotActive',
  SessionAlreadyActive: 'sessionAlreadyActive',
  GameNotInstalled: 'gameNotInstalled',
  GameLaunchFailed: 'gameLaunchFailed',
  AccountPoolExhausted: 'accountPoolExhausted',
  AntiCheatBlocked: 'antiCheatBlocked',
  PolicyDenied: 'policyDenied',
  AgentOffline: 'agentOffline',
  ServerUnavailable: 'serverUnavailable',
  Timeout: 'timeout',
  RateLimited: 'rateLimited',
  Internal: 'internal',
  ProtocolError: 'protocolError',
  VersionMismatch: 'versionMismatch',
} as const;
/** Error codes shared verbatim by IPC, the server REST envelope and the Tauri `ShellError`. */
export type ErrorCode = (typeof ErrorCode)[keyof typeof ErrorCode];

// ---- BEGIN MANUAL ----
/** All error codes in declaration order. */
export const ERROR_CODES: readonly ErrorCode[] = Object.values(ErrorCode);

const ERROR_CODE_SET: ReadonlySet<string> = new Set<string>(ERROR_CODES);

/** `true` when `value` is a known {@link ErrorCode}. */
export function isErrorCode(value: unknown): value is ErrorCode {
  return typeof value === 'string' && ERROR_CODE_SET.has(value);
}

/** `true` when the same request may succeed if repeated later (`timeout`, `rateLimited`, `serverUnavailable`, `agentOffline`). */
export function isRetryable(code: ErrorCode): boolean {
  return (
    code === ErrorCode.Timeout ||
    code === ErrorCode.RateLimited ||
    code === ErrorCode.ServerUnavailable ||
    code === ErrorCode.AgentOffline
  );
}

/** `true` for codes that mean the caller must (re)authenticate (`unauthorized`, `forbidden`). */
export function isAuthFailure(code: ErrorCode): boolean {
  return code === ErrorCode.Unauthorized || code === ErrorCode.Forbidden;
}

/** HTTP status equivalent of each error code (IPC_PROTOCOL.md §5). */
export const ERROR_CODE_HTTP_STATUS: Readonly<Record<ErrorCode, number>> = {
  unauthorized: 401,
  forbidden: 403,
  notFound: 404,
  validation: 400,
  conflict: 409,
  insufficientFunds: 402,
  sessionNotActive: 409,
  sessionAlreadyActive: 409,
  gameNotInstalled: 409,
  gameLaunchFailed: 500,
  accountPoolExhausted: 409,
  antiCheatBlocked: 403,
  policyDenied: 403,
  agentOffline: 503,
  serverUnavailable: 503,
  timeout: 504,
  rateLimited: 429,
  internal: 500,
  protocolError: 400,
  versionMismatch: 426,
};
// ---- END MANUAL ----

// ---------------------------------------------------------------------------------------------------------------------
// IPC envelope / errors
// ---------------------------------------------------------------------------------------------------------------------

/** Envelope kind (IPC_PROTOCOL.md §2). */
export const IpcKind = {
  Request: 'request',
  Response: 'response',
  Event: 'event',
} as const;
/** Envelope kind (IPC_PROTOCOL.md §2). */
export type IpcKind = (typeof IpcKind)[keyof typeof IpcKind];

// ---- BEGIN MANUAL ----
const IPC_KIND_SET: ReadonlySet<string> = new Set<string>(Object.values(IpcKind));
// ---- END MANUAL ----

/** Error carried by an IPC response envelope and by `game.stateChanged` / `update.progress` events; `details` is always present. */
export interface IpcError {
  /** Error code. */
  code: ErrorCode;
  /** Human-readable English message; the UI localizes by `code`. */
  message: string;
  /** Structured extra data (see the `*Details` interfaces) or null. */
  details: JsonObject | null;
}

// ---- BEGIN MANUAL ----
/** One pipe frame (IPC_PROTOCOL.md §2); `payload` and `error` keys are always present. */
export interface IpcEnvelope<T = JsonObject> {
  /** Protocol major. */
  v: number;
  /** Request id generated by the sender; a response copies it; events use a fresh id. */
  id: string;
  /** Envelope kind. */
  kind: IpcKind;
  /** Message name `<domain>.<action>`. */
  name: string;
  /** Sender wall clock (UTC), informational only. */
  ts: string;
  /** Body or null when the message carries no data. */
  payload: T | null;
  /** Non-null only on a response; when set, `payload` is null. */
  error: IpcError | null;
}
// ---- END MANUAL ----

/** `details` for `validation`. */
export interface ValidationDetails {
  /** JSON path of the offending field, e.g. `minutes` or `items[2].qty`. */
  field: string;
  /** Machine-readable reason, e.g. `required`, `min`, `max`, `format`. */
  reason: string;
}

/** `details` carrying a single `reason` code (`unauthorized`, `forbidden`, `conflict`). */
export interface ReasonDetails {
  /** Reason code, e.g. `expired`, `banned`, `outOfStock`, `pendingApproval`. */
  reason: string;
}

/** `details` for `notFound` when an IPC message name is unknown. */
export interface NameDetails {
  /** The unknown message name. */
  name: string;
}

/** `details` for `rateLimited`. */
export interface RateLimitDetails {
  /** Seconds the caller should wait before retrying. */
  retryAfterSec: number;
}

/** `details` for `insufficientFunds`. */
export interface InsufficientFundsDetails {
  /** Amount required by the operation. */
  required: Money;
  /** Amount currently available. */
  available: Money;
}

/** `details` for `policyDenied`. */
export interface PolicyDeniedDetails {
  /** Policy rule identifier, e.g. `ageRating`, `alreadyRunning`, `postpaidNotAllowed`. */
  rule: string;
  /** Settings field locked by policy (`settings.set`). */
  field?: string | null;
}

/** `details` for `antiCheatBlocked`. */
export interface AntiCheatBlockedDetails {
  /** Anti-cheat subsystem. */
  kind: AntiCheatKind;
  /** Check that failed, e.g. `driverMissing`, `secureBootOff`. */
  reason: string;
}

/** `details` for `gameLaunchFailed`. */
export interface LaunchFailedDetails {
  /** Launch stage, e.g. `lease`, `inject`, `createProcess`, `waitForWindow`. */
  stage: string;
  /** Launcher exit code when it exited. */
  exitCode?: number | null;
  /** Captured stderr tail, when any. */
  stderr?: string | null;
}

/** `details` for `versionMismatch`. */
export interface VersionMismatchDetails {
  /** Supported protocol majors. */
  supported: number[];
  /** Version received. */
  got: number;
}

/** `details` for `internal` (and any server error mapped to IPC). */
export interface TraceDetails {
  /** Trace id to correlate logs. */
  traceId: string;
}

/** Central-server error object (SERVER_API.md §3); `details` key always present. */
export interface ServerError {
  /** Error code. */
  code: ErrorCode;
  /** Human-readable English message. */
  message: string;
  /** Structured extra data or null. */
  details: JsonObject | null;
  /** Server trace id (echo of `X-Trace-Id`). */
  traceId: string;
}

/** Body of every non-2xx central-server response: `{ error: ServerError }`. */
export interface ServerErrorEnvelope {
  /** The error. */
  error: ServerError;
}

// ---- BEGIN MANUAL ----
/** Converts a server error to an {@link IpcError}; `traceId` is moved into `details.traceId`. */
export function serverErrorToIpcError(error: ServerError): IpcError {
  return { code: error.code, message: error.message, details: { ...(error.details ?? {}), traceId: error.traceId } };
}

/** Type guard for {@link IpcError}: known `code`, string `message`, `details` null/absent/object. */
export function isIpcError(value: unknown): value is IpcError {
  if (!isRecord(value)) {
    return false;
  }
  const details = value['details'];
  return (
    isErrorCode(value['code']) &&
    typeof value['message'] === 'string' &&
    (details === null || details === undefined || isRecord(details))
  );
}

/** `true` for `<domain>.<action>` where both parts are non-empty ASCII letters (≤ 64 chars). */
export function isValidIpcName(name: string): boolean {
  return name.length <= 64 && /^[A-Za-z]+\.[A-Za-z]+$/.test(name);
}

/** Type guard for {@link IpcEnvelope} (structural rules of IPC_PROTOCOL.md §2; `payload`/`error` may be absent on read). */
export function isIpcEnvelope(value: unknown): value is IpcEnvelope<JsonObject> {
  if (!isRecord(value)) {
    return false;
  }
  const { v, id, kind, name, ts, payload, error } = value;
  if (
    typeof v !== 'number' ||
    !Number.isInteger(v) ||
    typeof id !== 'string' ||
    id.length === 0 ||
    typeof kind !== 'string' ||
    !IPC_KIND_SET.has(kind) ||
    typeof name !== 'string' ||
    !isValidIpcName(name) ||
    typeof ts !== 'string'
  ) {
    return false;
  }
  const payloadOk = payload === null || payload === undefined || isRecord(payload);
  const errorOk = error === null || error === undefined || (kind === IpcKind.Response && isIpcError(error));
  const exclusive = !(error !== null && error !== undefined && isRecord(payload));
  return payloadOk && errorOk && exclusive;
}
// ---- END MANUAL ----

// ---------------------------------------------------------------------------------------------------------------------
// Message names
// ---------------------------------------------------------------------------------------------------------------------

/** Every IPC message name (IPC_PROTOCOL.md §7–8); `Events` are Agent → Shell, re-emitted as Tauri `agent://<name>`. */
export const IpcNames = {
  Auth: {
    Hello: 'auth.hello',
    Login: 'auth.login',
    Logout: 'auth.logout',
    Status: 'auth.status',
    QrStart: 'auth.qrStart',
  },
  Session: {
    Get: 'session.get',
    Start: 'session.start',
    Pause: 'session.pause',
    Resume: 'session.resume',
    End: 'session.end',
    Extend: 'session.extend',
    Lock: 'session.lock',
    Unlock: 'session.unlock',
    TimeLeft: 'session.timeLeft',
  },
  Games: {
    List: 'games.list',
    Get: 'games.get',
    Launch: 'games.launch',
    Kill: 'games.kill',
    Running: 'games.running',
    InstallStatus: 'games.installStatus',
  },
  Apps: {
    List: 'apps.list',
    Launch: 'apps.launch',
  },
  Wallet: {
    Balance: 'wallet.balance',
    Tariffs: 'wallet.tariffs',
    History: 'wallet.history',
    TopupIntent: 'wallet.topupIntent',
  },
  Shop: {
    Products: 'shop.products',
    Order: 'shop.order',
    OrderStatus: 'shop.orderStatus',
    Orders: 'shop.orders',
  },
  Chat: {
    History: 'chat.history',
    Send: 'chat.send',
    MarkRead: 'chat.markRead',
  },
  Booking: {
    Seats: 'booking.seats',
    Reserve: 'booking.reserve',
    Cancel: 'booking.cancel',
  },
  Tournaments: {
    List: 'tournaments.list',
    Join: 'tournaments.join',
    Leaderboard: 'tournaments.leaderboard',
  },
  Profile: {
    Get: 'profile.get',
    Update: 'profile.update',
    Stats: 'profile.stats',
    Achievements: 'profile.achievements',
    Loyalty: 'profile.loyalty',
  },
  Settings: {
    Get: 'settings.get',
    Set: 'settings.set',
  },
  Sys: {
    Ping: 'sys.ping',
    Pong: 'sys.pong',
    PcInfo: 'sys.pcInfo',
    Hardware: 'sys.hardware',
    Metrics: 'sys.metrics',
    CallAdmin: 'sys.callAdmin',
    Reboot: 'sys.reboot',
    Shutdown: 'sys.shutdown',
    LockScreen: 'sys.lockScreen',
    SetVolume: 'sys.setVolume',
    SetLocale: 'sys.setLocale',
    UnlockAdmin: 'sys.unlockAdmin',
    LogClientError: 'sys.logClientError',
    AckAdminMessage: 'sys.ackAdminMessage',
  },
  Policy: {
    Get: 'policy.get',
    Reload: 'policy.reload',
  },
  Update: {
    Check: 'update.check',
    Apply: 'update.apply',
  },
  Events: {
    SessionUpdated: 'session.updated',
    SessionWarning: 'session.warning',
    SessionEnded: 'session.ended',
    WalletUpdated: 'wallet.updated',
    ChatMessage: 'chat.message',
    NotificationPush: 'notification.push',
    AdminMessage: 'admin.message',
    AdminRemoteControl: 'admin.remoteControl',
    GameStateChanged: 'game.stateChanged',
    PolicyChanged: 'policy.changed',
    UpdateAvailable: 'update.available',
    UpdateProgress: 'update.progress',
    UpdateReady: 'update.ready',
    SysMetrics: 'sys.metrics',
    SysConnectivity: 'sys.connectivity',
    ShellCommand: 'shell.command',
    AuthExpired: 'auth.expired',
    ShopOrderUpdated: 'shop.orderUpdated',
  },
} as const;

/** Every Shell → Agent IPC request, keyed by C# member name and valued by IPC name (`AgentCommand.SessionStart === 'session.start'`). */
export const AgentCommand = {
  AuthHello: IpcNames.Auth.Hello,
  AuthLogin: IpcNames.Auth.Login,
  AuthLogout: IpcNames.Auth.Logout,
  AuthStatus: IpcNames.Auth.Status,
  AuthQrStart: IpcNames.Auth.QrStart,
  SessionGet: IpcNames.Session.Get,
  SessionStart: IpcNames.Session.Start,
  SessionPause: IpcNames.Session.Pause,
  SessionResume: IpcNames.Session.Resume,
  SessionEnd: IpcNames.Session.End,
  SessionExtend: IpcNames.Session.Extend,
  SessionLock: IpcNames.Session.Lock,
  SessionUnlock: IpcNames.Session.Unlock,
  SessionTimeLeft: IpcNames.Session.TimeLeft,
  GamesList: IpcNames.Games.List,
  GamesGet: IpcNames.Games.Get,
  GamesLaunch: IpcNames.Games.Launch,
  GamesKill: IpcNames.Games.Kill,
  GamesRunning: IpcNames.Games.Running,
  GamesInstallStatus: IpcNames.Games.InstallStatus,
  AppsList: IpcNames.Apps.List,
  AppsLaunch: IpcNames.Apps.Launch,
  WalletBalance: IpcNames.Wallet.Balance,
  WalletTariffs: IpcNames.Wallet.Tariffs,
  WalletHistory: IpcNames.Wallet.History,
  WalletTopupIntent: IpcNames.Wallet.TopupIntent,
  ShopProducts: IpcNames.Shop.Products,
  ShopOrder: IpcNames.Shop.Order,
  ShopOrderStatus: IpcNames.Shop.OrderStatus,
  ShopOrders: IpcNames.Shop.Orders,
  ChatHistory: IpcNames.Chat.History,
  ChatSend: IpcNames.Chat.Send,
  ChatMarkRead: IpcNames.Chat.MarkRead,
  BookingSeats: IpcNames.Booking.Seats,
  BookingReserve: IpcNames.Booking.Reserve,
  BookingCancel: IpcNames.Booking.Cancel,
  TournamentsList: IpcNames.Tournaments.List,
  TournamentsJoin: IpcNames.Tournaments.Join,
  TournamentsLeaderboard: IpcNames.Tournaments.Leaderboard,
  ProfileGet: IpcNames.Profile.Get,
  ProfileUpdate: IpcNames.Profile.Update,
  ProfileStats: IpcNames.Profile.Stats,
  ProfileAchievements: IpcNames.Profile.Achievements,
  ProfileLoyalty: IpcNames.Profile.Loyalty,
  SettingsGet: IpcNames.Settings.Get,
  SettingsSet: IpcNames.Settings.Set,
  SysPing: IpcNames.Sys.Ping,
  SysPcInfo: IpcNames.Sys.PcInfo,
  SysHardware: IpcNames.Sys.Hardware,
  SysMetrics: IpcNames.Sys.Metrics,
  SysCallAdmin: IpcNames.Sys.CallAdmin,
  SysReboot: IpcNames.Sys.Reboot,
  SysShutdown: IpcNames.Sys.Shutdown,
  SysLockScreen: IpcNames.Sys.LockScreen,
  SysSetVolume: IpcNames.Sys.SetVolume,
  SysSetLocale: IpcNames.Sys.SetLocale,
  SysUnlockAdmin: IpcNames.Sys.UnlockAdmin,
  SysLogClientError: IpcNames.Sys.LogClientError,
  SysAckAdminMessage: IpcNames.Sys.AckAdminMessage,
  PolicyGet: IpcNames.Policy.Get,
  PolicyReload: IpcNames.Policy.Reload,
  UpdateCheck: IpcNames.Update.Check,
  UpdateApply: IpcNames.Update.Apply,
} as const;
/** IPC request name of a Shell → Agent request (`'auth.hello' | 'session.start' | ...`). */
export type AgentCommand = (typeof AgentCommand)[keyof typeof AgentCommand];

// ---- BEGIN MANUAL ----
/** All request names in declaration order. */
export const AGENT_COMMANDS: readonly AgentCommand[] = Object.values(AgentCommand);

const AGENT_COMMAND_SET: ReadonlySet<string> = new Set<string>(AGENT_COMMANDS);

/** `true` when `name` is a known Shell → Agent request name. */
export function isAgentCommand(name: string): name is AgentCommand {
  return AGENT_COMMAND_SET.has(name);
}

/** Response name for a request name: identical except `sys.ping` → `sys.pong`. */
export function responseNameFor(requestName: string): string {
  return requestName === IpcNames.Sys.Ping ? IpcNames.Sys.Pong : requestName;
}
// ---- END MANUAL ----

/** Authentication level an IPC request requires (IPC_PROTOCOL.md §7 "auth" column). */
export const IpcAuthLevel = {
  None: 'none',
  Hello: 'hello',
  User: 'user',
  Session: 'session',
} as const;
/** Authentication level an IPC request requires. */
export type IpcAuthLevel = (typeof IpcAuthLevel)[keyof typeof IpcAuthLevel];

// ---- BEGIN MANUAL ----
const USER_LEVEL_COMMANDS: ReadonlySet<string> = new Set<string>([
  AgentCommand.AuthLogout,
  AgentCommand.SessionGet,
  AgentCommand.SessionStart,
  AgentCommand.SessionTimeLeft,
  AgentCommand.WalletBalance,
  AgentCommand.WalletHistory,
  AgentCommand.WalletTopupIntent,
  AgentCommand.ShopOrderStatus,
  AgentCommand.ShopOrders,
  AgentCommand.ChatHistory,
  AgentCommand.ChatSend,
  AgentCommand.ChatMarkRead,
  AgentCommand.BookingReserve,
  AgentCommand.BookingCancel,
  AgentCommand.TournamentsJoin,
  AgentCommand.ProfileGet,
  AgentCommand.ProfileUpdate,
  AgentCommand.ProfileStats,
  AgentCommand.ProfileAchievements,
  AgentCommand.ProfileLoyalty,
]);

const SESSION_LEVEL_COMMANDS: ReadonlySet<string> = new Set<string>([
  AgentCommand.SessionPause,
  AgentCommand.SessionResume,
  AgentCommand.SessionEnd,
  AgentCommand.SessionExtend,
  AgentCommand.SessionLock,
  AgentCommand.SessionUnlock,
  AgentCommand.GamesLaunch,
  AgentCommand.GamesKill,
  AgentCommand.AppsLaunch,
  AgentCommand.ShopOrder,
]);

/** Authentication level `command` requires. */
export function requiredAuth(command: AgentCommand): IpcAuthLevel {
  if (command === AgentCommand.AuthHello || command === AgentCommand.SysPing) {
    return IpcAuthLevel.None;
  }
  if (USER_LEVEL_COMMANDS.has(command)) {
    return IpcAuthLevel.User;
  }
  if (SESSION_LEVEL_COMMANDS.has(command)) {
    return IpcAuthLevel.Session;
  }
  return IpcAuthLevel.Hello;
}
// ---- END MANUAL ----

// ---------------------------------------------------------------------------------------------------------------------
// Common payloads
// ---------------------------------------------------------------------------------------------------------------------

/** Standard page envelope `{ items, total, page, pageSize }` (SERVER_API.md §1). */
export interface PagedResult<T> {
  /** Items of this page. */
  items: T[];
  /** Total items across all pages. */
  total: number;
  /** 1-based page number. */
  page: number;
  /** Page size. */
  pageSize: number;
}

/** Trivial `{ ok: true }` response. */
export interface OkResponse {
  /** Always true. */
  ok: true;
}

/** Shell capabilities advertised in `AuthHelloRequest.capabilities`. */
export const ShellCapabilities = {
  Gamepad: 'gamepad',
  VirtualKeyboard: 'virtualKeyboard',
  MultiMonitor: 'multiMonitor',
  Overlay: 'overlay',
} as const;
/** Shell capability name. */
export type ShellCapability = (typeof ShellCapabilities)[keyof typeof ShellCapabilities];

/** Agent capabilities advertised in `AuthHelloResponse.capabilities`; the Shell hides features whose capability is absent. */
export const AgentCapabilities = {
  AccountPool: 'accountPool',
  CloudSave: 'cloudSave',
  RemoteControl: 'remoteControl',
  ScreenCapture: 'screenCapture',
  VirtualKeyboard: 'virtualKeyboard',
  Wol: 'wol',
  Offline: 'offline',
  Shop: 'shop',
  Chat: 'chat',
  Booking: 'booking',
  Tournaments: 'tournaments',
} as const;
/** Agent capability name. */
export type AgentCapability = (typeof AgentCapabilities)[keyof typeof AgentCapabilities];

// ---------------------------------------------------------------------------------------------------------------------
// auth.*
// ---------------------------------------------------------------------------------------------------------------------

/** Request of `auth.hello`. */
export interface AuthHelloRequest {
  /** 64 hex chars from `secure\shell.token`. */
  shellToken: string;
  /** Shell semver. */
  shellVersion: string;
  /** Shell process id. */
  pid: number;
  /** Windows session id. */
  wtsSessionId: number;
  /** Current UI locale. */
  locale: Locale;
  /** Shell capabilities ({@link ShellCapabilities}). */
  capabilities: string[];
}

/** Response of `auth.hello`. */
export interface AuthHelloResponse {
  /** Agent semver. */
  agentVersion: string;
  /** IPC protocol major. */
  protocol: number;
  /** PC id. */
  pcId: string;
  /** PC name. */
  pcName: string;
  /** Zone. */
  zone: string;
  /** Server reachable. */
  serverOnline: boolean;
  /** Applied policy version. */
  policyVersion: number;
  /** Agent's best estimate of server time. */
  serverTime: string;
  /** Agent capabilities ({@link AgentCapabilities}). */
  capabilities: string[];
  /** Kiosk Windows account name. */
  kioskUser: string;
}

/** Request of `auth.login` (subset of `AuthRequest` without PC identity); secrets are never logged. */
export interface AuthLoginRequest {
  /** Method. */
  kind: AuthKind;
  /** Login name (`password`). */
  username?: string | null;
  /** Password (`password`). */
  password?: string | null;
  /** QR token (`qr`). */
  qrToken?: string | null;
  /** Card id (`card`). */
  cardId?: string | null;
  /** One-time token (`token`). */
  token?: string | null;
}

/** Response of `auth.login`. */
export interface AuthLoginResponse {
  /** Authenticated user. */
  user: User;
  /** Existing session bound to this user on this PC, when any. */
  session?: Session | null;
  /** User access token expiry (token is Agent-held). */
  expiresAt: string;
  /** `offline` when validated from cache. */
  mode: ConnectivityState;
}

/** Request of `auth.logout`; only `user`, `idle`, `admin` are valid from the Shell (default `user`). */
export interface AuthLogoutRequest {
  /** Why. */
  reason?: SessionEndReason | null;
}

/** Response of `auth.logout`. */
export interface AuthLogoutResponse {
  /** Always true. */
  ok: boolean;
  /** Whether an active session was ended first. */
  sessionEnded: boolean;
  /** The ended session, when any. */
  session?: Session | null;
}

/** Response of `auth.status`. */
export interface AuthStatusResponse {
  /** Whether a user is logged in. */
  authenticated: boolean;
  /** Connectivity. */
  mode: ConnectivityState;
  /** Logged-in user. */
  user?: User | null;
  /** Current session. */
  session?: Session | null;
  /** User token expiry. */
  expiresAt?: string | null;
}

// ---------------------------------------------------------------------------------------------------------------------
// session.*
// ---------------------------------------------------------------------------------------------------------------------

/** Request of `session.start`. */
export interface SessionStartRequest {
  /** Tariff. */
  tariffId: string;
  /** true = charge now for `minutes`; false = postpaid open-ended. */
  prepaid: boolean;
  /** Minutes to buy; required unless the tariff is a package. */
  minutes?: number | null;
}

/** Request of `session.pause`. */
export interface SessionPauseRequest {
  /** Free-text reason. */
  reason?: string | null;
}

/** Request of `session.end`. */
export interface SessionEndRequest {
  /** End reason; defaults to `user`. */
  reason?: SessionEndReason | null;
}

/** Request of `session.extend` and body of `POST /sessions/{id}/extend`. */
export interface SessionExtendRequest {
  /** Minutes to add. */
  minutes: number;
  /** Tariff to bill; defaults to the current one. */
  tariffId?: string | null;
}

/** Request of `session.lock`. */
export interface SessionLockRequest {
  /** Free-text reason. */
  reason?: string | null;
}

/** Request of `session.unlock`; exactly one of the secrets is given. */
export interface SessionUnlockRequest {
  /** Account password. */
  password?: string | null;
  /** 4–6 digit user PIN. */
  pin?: string | null;
}

/** Response of `session.timeLeft`; used for drift correction every 30 s. Never errors when idle. */
export interface SessionTimeLeftResponse {
  /** Session state (`idle` when none). */
  state: SessionState;
  /** Seconds remaining (−1 open-ended, 0 when idle). */
  secondsLeft: number;
  /** Seconds used. */
  secondsUsed: number;
  /** Agent's estimate of server time. */
  serverTime: string;
  /** Session id, when any. */
  sessionId?: string | null;
  /** Scheduled end, when any. */
  endsAt?: string | null;
}

// ---------------------------------------------------------------------------------------------------------------------
// games.* / apps.*
// ---------------------------------------------------------------------------------------------------------------------

/** Request of `games.list`. */
export interface GamesListRequest {
  /** Filter by category. */
  category?: string | null;
  /** Free-text search. */
  search?: string | null;
  /** Only installed games. */
  installedOnly?: boolean | null;
  /** Filter by launcher. */
  launcher?: LauncherType | null;
  /** Sort order (default popularity). */
  sort?: GamesSort | null;
  /** 1-based page (default 1). */
  page?: number | null;
  /** Page size (default 100, max 500). */
  pageSize?: number | null;
}

/** Default `games.list` page size. */
export const GAMES_LIST_DEFAULT_PAGE_SIZE = 100;

/** Maximum `games.list` page size. */
export const GAMES_LIST_MAX_PAGE_SIZE = 500;

// ---- BEGIN MANUAL ----
/** Response of `games.list` (served from `cache\games.json` when offline). */
export interface GamesListResponse extends PagedResult<Game> {
  /** Catalogue version (ETag-like). */
  catalogVersion: string;
}
// ---- END MANUAL ----

/** Request of `games.get`. */
export interface GamesGetRequest {
  /** Game id. */
  gameId: string;
}

/** Request of `games.launch`; the Agent fills session/user/timeout to build a `LaunchRequest`. */
export interface GamesLaunchRequest {
  /** Game id. */
  gameId: string;
  /** Defaults to `Game.requiresAccount`. */
  useAccountPool?: boolean | null;
  /** Extra command line (appended after policy sanitization). */
  extraArgs?: string | null;
  /** Requested resolution. */
  resolution?: Resolution | null;
}

/** Request of `games.kill`; at least one of `gameId`/`pid`, none = kill all. */
export interface GamesKillRequest {
  /** Game to kill. */
  gameId?: string | null;
  /** Process to kill. */
  pid?: number | null;
  /** Terminate immediately. */
  force?: boolean | null;
}

/** Response of `games.kill` and result of the `killGame` server command. */
export interface GamesKillResponse {
  /** Number of processes killed. */
  killed: number;
  /** Process ids killed. */
  pids: number[];
}

/** Response of `games.running`. */
export interface GamesRunningResponse {
  /** Running games. */
  items: RunningGame[];
}

/** Request of `games.installStatus`. */
export interface GamesInstallStatusRequest {
  /** Game id. */
  gameId: string;
}

/** Response of `apps.list` and `GET /apps`. */
export interface AppsListResponse {
  /** Apps. */
  items: App[];
}

/** Request of `apps.launch`. */
export interface AppsLaunchRequest {
  /** App id. */
  appId: string;
  /** Extra command line. */
  args?: string | null;
}

/** Response of `apps.launch`. */
export interface AppsLaunchResponse {
  /** Always true. */
  ok: boolean;
  /** Process id. */
  pid: number;
  /** Launch time. */
  startedAt: string;
}

// ---------------------------------------------------------------------------------------------------------------------
// wallet.*
// ---------------------------------------------------------------------------------------------------------------------

/** Request of `wallet.tariffs`. */
export interface WalletTariffsRequest {
  /** Zone; defaults to this PC's zone. */
  zone?: string | null;
}

/** Response of `wallet.tariffs` (cached offline). */
export interface WalletTariffsResponse {
  /** Tariffs. */
  items: Tariff[];
  /** Zone the list applies to. */
  zone: string;
  /** Server time for window evaluation. */
  serverTime: string;
}

/** Response of `GET /tariffs`. */
export interface TariffsResponse {
  /** Tariffs. */
  items: Tariff[];
  /** Server time. */
  serverTime: string;
}

/** Request of `wallet.history` (and query of `GET /wallet/{userId}/transactions`). */
export interface WalletHistoryRequest {
  /** 1-based page. */
  page?: number | null;
  /** Page size. */
  pageSize?: number | null;
  /** Inclusive lower bound. */
  from?: string | null;
  /** Exclusive upper bound. */
  to?: string | null;
  /** Filter by type. */
  type?: TransactionType | null;
}

// ---- BEGIN MANUAL ----
/** Response of `wallet.history`. */
export type WalletHistoryResponse = PagedResult<Transaction>;
// ---- END MANUAL ----

/** Request of `wallet.topupIntent`; `amount` ≥ 1 000 UZS, `cash` creates an admin ticket. */
export interface WalletTopupIntentRequest {
  /** Amount. */
  amount: Money;
  /** Provider. */
  provider: TopupProvider;
}

// ---------------------------------------------------------------------------------------------------------------------
// shop.*
// ---------------------------------------------------------------------------------------------------------------------

/** Request of `shop.products`. */
export interface ShopProductsRequest {
  /** Filter by category. */
  category?: ProductCategory | null;
  /** Free-text search. */
  search?: string | null;
}

/** Response of `shop.products` and `GET /shop/products`. */
export interface ShopProductsResponse {
  /** Products. */
  items: Product[];
}

/** Request of `shop.order`. */
export interface ShopOrderRequest {
  /** Lines (1–20, qty 1–99). */
  items: OrderLineRequest[];
  /** Client-generated key forwarded as `Idempotency-Key`. */
  idempotencyKey: string;
  /** Note for staff. */
  note?: string | null;
}

/** Request of `shop.orderStatus`. */
export interface ShopOrderStatusRequest {
  /** Order id. */
  orderId: string;
}

/** Request of `shop.orders`. */
export interface ShopOrdersRequest {
  /** 1-based page. */
  page?: number | null;
  /** Page size. */
  pageSize?: number | null;
  /** Only orders still in progress. */
  activeOnly?: boolean | null;
}

/** Response of `shop.orders`. */
export interface ShopOrdersResponse {
  /** Orders. */
  items: Order[];
  /** Total orders. */
  total: number;
}

// ---------------------------------------------------------------------------------------------------------------------
// chat.*
// ---------------------------------------------------------------------------------------------------------------------

/** Request of `chat.history`. */
export interface ChatHistoryRequest {
  /** Room; defaults to `pc:<pcId>`. */
  roomId?: string | null;
  /** Return messages older than this message id. */
  before?: string | null;
  /** Page size (default 50, max 200). */
  limit?: number | null;
}

/** Default `chat.history` page size. */
export const CHAT_HISTORY_DEFAULT_LIMIT = 50;

/** Maximum `chat.history` page size. */
export const CHAT_HISTORY_MAX_LIMIT = 200;

/** Response of `chat.history` and `GET /chat/{roomId}/messages`. */
export interface ChatHistoryResponse {
  /** Room. */
  roomId: string;
  /** Messages, oldest first. */
  items: ChatMessage[];
  /** Older messages exist. */
  hasMore: boolean;
  /** Unread count in the room. */
  unread: number;
}

/** Request of `chat.send`. */
export interface ChatSendRequest {
  /** Text (1–2000 chars). */
  text: string;
  /** Client-generated key forwarded as `Idempotency-Key`. */
  idempotencyKey: string;
  /** Room; defaults to `pc:<pcId>`. */
  roomId?: string | null;
}

/** Request of `chat.markRead`. */
export interface ChatMarkReadRequest {
  /** Mark up to and including this message. */
  upToMessageId: string;
  /** Room; defaults to `pc:<pcId>`. */
  roomId?: string | null;
}

/** Response of `chat.markRead` and `POST /chat/{roomId}/read`. */
export interface ChatMarkReadResponse {
  /** Room. */
  roomId: string;
  /** Remaining unread count. */
  unread: number;
}

// ---------------------------------------------------------------------------------------------------------------------
// booking.*
// ---------------------------------------------------------------------------------------------------------------------

/** Request of `booking.seats`. */
export interface BookingSeatsRequest {
  /** Club-local date (`YYYY-MM-DD`). */
  date: string;
}

/** Response of `booking.seats` and `GET /booking/seats`; other users' bookings are anonymized. */
export interface BookingSeatsResponse {
  /** Date (`YYYY-MM-DD`). */
  date: string;
  /** Seat map. */
  seats: Seat[];
  /** All bookings of that day. */
  bookings: Booking[];
  /** Slot granularity. */
  slotMinutes: number;
  /** Club opening time (`HH:mm`, REST only). */
  openFrom?: string | null;
  /** Club closing time (`HH:mm`, REST only). */
  openTo?: string | null;
}

/** Request of `booking.reserve`. */
export interface BookingReserveRequest {
  /** PC. */
  pcId: string;
  /** Start (slot-aligned, future). */
  from: string;
  /** End. */
  to: string;
}

/** Request of `booking.cancel`. */
export interface BookingCancelRequest {
  /** Booking id. */
  bookingId: string;
}

// ---------------------------------------------------------------------------------------------------------------------
// tournaments.* / profile.*
// ---------------------------------------------------------------------------------------------------------------------

/** Request of `tournaments.list`. */
export interface TournamentsListRequest {
  /** Filter by state. */
  state?: TournamentState | null;
  /** Filter by game. */
  gameId?: string | null;
}

/** Response of `tournaments.list` and `GET /tournaments`. */
export interface TournamentsListResponse {
  /** Tournaments. */
  items: Tournament[];
}

/** Request of `tournaments.join`. */
export interface TournamentsJoinRequest {
  /** Tournament id. */
  tournamentId: string;
}

/** Request of `tournaments.leaderboard`. */
export interface TournamentsLeaderboardRequest {
  /** Tournament id. */
  tournamentId: string;
  /** Max entries (≤ 100). */
  limit?: number | null;
}

/** Response of `tournaments.leaderboard` and `GET /tournaments/{id}/leaderboard`. */
export interface TournamentsLeaderboardResponse {
  /** Tournament id. */
  tournamentId: string;
  /** Top entries. */
  entries: LeaderboardEntry[];
  /** Last update. */
  updatedAt: string;
  /** Current user's entry when outside the top list. */
  me?: LeaderboardEntry | null;
}

/** Response of `profile.achievements` and `GET /users/{userId}/achievements`. */
export interface ProfileAchievementsResponse {
  /** Achievements. */
  items: Achievement[];
}

// ---------------------------------------------------------------------------------------------------------------------
// settings.*
// ---------------------------------------------------------------------------------------------------------------------

/** Feature toggles exposed to the UI (`shell.json → features`). */
export interface ShellFeatures {
  /** Shop. */
  shop: boolean;
  /** Chat. */
  chat: boolean;
  /** Booking. */
  booking: boolean;
  /** Tournaments. */
  tournaments: boolean;
  /** Profile. */
  profile: boolean;
  /** Top-up. */
  topup: boolean;
  /** Apps. */
  apps: boolean;
  /** Call admin. */
  callAdmin: boolean;
}

/** Settings subset of `shell.json` exposed to the UI (IPC_PROTOCOL.md §6.21); persisted by the Agent. */
export interface ShellSettings {
  /** UI locale. */
  locale: Locale;
  /** Theme id. */
  theme: string;
  /** Installed themes (read-only). */
  availableThemes: string[];
  /** Volume 0–100. */
  volume: number;
  /** Muted. */
  muted: boolean;
  /** Idle lock timeout. */
  idleTimeoutSec: number;
  /** Show the metrics overlay. */
  showMetricsOverlay: boolean;
  /** Allow the on-screen keyboard. */
  allowVirtualKeyboard: boolean;
  /** UI sounds. */
  uiSounds: boolean;
  /** Feature toggles (read-only). */
  features: ShellFeatures;
}

/** Request of `settings.set`; partial, at least one key. */
export interface SettingsSetRequest {
  /** UI locale. */
  locale?: Locale | null;
  /** Theme id (must exist). */
  theme?: string | null;
  /** Volume 0–100. */
  volume?: number | null;
  /** Muted. */
  muted?: boolean | null;
  /** Idle lock timeout. */
  idleTimeoutSec?: number | null;
  /** Show the metrics overlay. */
  showMetricsOverlay?: boolean | null;
  /** Allow the on-screen keyboard. */
  allowVirtualKeyboard?: boolean | null;
  /** UI sounds. */
  uiSounds?: boolean | null;
}

// ---- BEGIN MANUAL ----
/** `true` when no field of a `settings.set` request is set (request is invalid). */
export function isSettingsSetRequestEmpty(request: SettingsSetRequest): boolean {
  return Object.values(request).every((v) => v === null || v === undefined);
}
// ---- END MANUAL ----

// ---------------------------------------------------------------------------------------------------------------------
// sys.*
// ---------------------------------------------------------------------------------------------------------------------

/** Category of a `sys.callAdmin` ticket. */
export const CallAdminCategory = {
  Help: 'help',
  Technical: 'technical',
  Order: 'order',
  Other: 'other',
} as const;
/** Category of a `sys.callAdmin` ticket. */
export type CallAdminCategory = (typeof CallAdminCategory)[keyof typeof CallAdminCategory];

/** Severity of a client-side error forwarded via `sys.logClientError`. */
export const ClientErrorLevel = {
  Warn: 'warn',
  Error: 'error',
} as const;
/** Severity of a client-side error forwarded via `sys.logClientError`. */
export type ClientErrorLevel = (typeof ClientErrorLevel)[keyof typeof ClientErrorLevel];

/** Request of `sys.ping`. */
export interface SysPingRequest {
  /** Monotonic sequence number. */
  seq: number;
  /** Shell send time. */
  sentAt: string;
}

/** Response (`sys.pong`) to `sys.ping`. */
export interface SysPongResponse {
  /** Echoed sequence number. */
  seq: number;
  /** Echoed send time. */
  sentAt: string;
  /** Agent receive time. */
  receivedAt: string;
  /** Server connectivity. */
  connectivity: ConnectivityState;
}

/** Request of `sys.hardware`. */
export interface SysHardwareRequest {
  /** Force a rescan instead of the cached inventory. */
  refresh?: boolean | null;
}

/** Request of `sys.callAdmin` (rate limited 1 per 30 s). */
export interface SysCallAdminRequest {
  /** Category. */
  category: CallAdminCategory;
  /** Free text. */
  message?: string | null;
}

/** Response of `sys.callAdmin` and `POST /support/call-admin`; offline: client-generated id, `queuePosition` null. */
export interface SysCallAdminResponse {
  /** Ticket id. */
  ticketId: string;
  /** Creation time. */
  createdAt: string;
  /** Position in the admin queue, when known. */
  queuePosition?: number | null;
}

/** Request of `sys.reboot` / `sys.shutdown`. */
export interface SysPowerRequest {
  /** Delay before the action. */
  delaySec?: number | null;
  /** Free-text reason. */
  reason?: string | null;
}

/** Request of `sys.lockScreen`. */
export interface SysLockScreenRequest {
  /** Free-text reason. */
  reason?: string | null;
}

/** Request of `sys.setVolume` and payload of the `setVolume` server command. */
export interface SetVolumeRequest {
  /** Volume 0–100. */
  level: number;
  /** Mute state; unchanged when null. */
  muted?: boolean | null;
}

/** Response of `sys.setVolume` and result of the `setVolume` server command. */
export interface VolumeState {
  /** Applied volume. */
  level: number;
  /** Applied mute state. */
  muted: boolean;
}

/** Request of `sys.setLocale`. */
export interface SysSetLocaleRequest {
  /** New locale. */
  locale: Locale;
}

/** Response of `sys.setLocale`. */
export interface SysSetLocaleResponse {
  /** Applied locale. */
  locale: Locale;
}

/** Request of `sys.unlockAdmin` (rate limited 3 attempts/min). */
export interface SysUnlockAdminRequest {
  /** Admin PIN. */
  pin: string;
}

/** Response of `sys.unlockAdmin`; grants the kiosk exit path for 5 minutes. */
export interface SysUnlockAdminResponse {
  /** Always true. */
  ok: boolean;
  /** Short-lived token passed to `kiosk_exit`. */
  adminToken: string;
  /** Token expiry. */
  expiresAt: string;
}

/** Request of `sys.logClientError` (rate limited 10/s, silently dropped beyond). */
export interface SysLogClientErrorRequest {
  /** Severity. */
  level: ClientErrorLevel;
  /** Message. */
  message: string;
  /** Stack trace. */
  stack?: string | null;
  /** Frontend route. */
  route?: string | null;
}

/** Request of `sys.ackAdminMessage`. */
export interface SysAckAdminMessageRequest {
  /** Id of the acknowledged admin message. */
  id: string;
}

// ---------------------------------------------------------------------------------------------------------------------
// Policy (IPC_PROTOCOL.md §6.18, ARCHITECTURE.md §12.3)
// ---------------------------------------------------------------------------------------------------------------------

/** Semantics of `ProcessAllowlistPolicy.patterns`. */
export const AllowlistMode = {
  Allow: 'allow',
  Deny: 'deny',
} as const;
/** Semantics of `ProcessAllowlistPolicy.patterns`. */
export type AllowlistMode = (typeof AllowlistMode)[keyof typeof AllowlistMode];

/** Shell replacement for the kiosk user. */
export interface ShellReplacementPolicy {
  /** Replace `explorer.exe`. */
  enabled: boolean;
  /** Path of the shell executable. */
  shellExe: string;
}

/** Process allow/deny list; patterns are wildcard image names (`*cheat*`). */
export interface ProcessAllowlistPolicy {
  /** Allow-list or deny-list semantics. */
  mode: AllowlistMode;
  /** Wildcard patterns. */
  patterns: string[];
}

/** USB device policy. */
export interface UsbPolicy {
  /** Allow USB mass storage. */
  allowStorage: boolean;
  /** Allow USB HID devices. */
  allowHid: boolean;
}

/** DNS-based web filter. */
export interface WebFilterPolicy {
  /** Filter active. */
  enabled: boolean;
  /** Wildcard domains to block. */
  blockedDomains: string[];
  /** Non-empty = allow-list mode. */
  allowedDomains: string[];
  /** Filtering DNS resolvers to enforce. */
  dnsServers: string[];
}

/** Explorer / input lockdown; combos use `(Ctrl|Alt|Shift|Win)(\+(Ctrl|Alt|Shift|Win))*\+<Key>`. */
export interface ExplorerPolicy {
  /** Disable Task Manager. */
  disableTaskManager: boolean;
  /** Disable the Run dialog. */
  disableRun: boolean;
  /** Disable Settings / Control Panel. */
  disableSettings: boolean;
  /** Hide the taskbar. */
  hideTaskbar: boolean;
  /** Block Alt+Tab. */
  disableAltTab: boolean;
  /** Block the Windows key. */
  disableWinKey: boolean;
  /** Additional blocked combos, e.g. `Ctrl+Shift+Esc`. */
  blockedKeyCombos: string[];
}

/** Power policy. */
export interface PowerPolicy {
  /** Shut down after this many idle minutes; null = never. */
  idleShutdownMin?: number | null;
  /** Daily shutdown time (local `HH:mm`); null = none. */
  scheduledShutdown?: string | null;
}

/** Update policy (overrides `agent.json → updates`). */
export interface UpdatesPolicy {
  /** Update channel. */
  channel: UpdateChannel;
  /** Apply staged updates automatically when idle. */
  autoInstall: boolean;
}

/** Anti-cheat policy. */
export interface AntiCheatPolicy {
  /** Subsystems whose drivers/services must be healthy. */
  required: AntiCheatKind[];
  /** Block launches / lock session on violations. */
  blockOnViolation: boolean;
}

/** Kiosk UI policy. */
export interface KioskPolicy {
  /** Idle lock timeout. */
  idleTimeoutSec: number;
  /** Interval between ad breaks. */
  adsIntervalSec: number;
  /** Allow the on-screen keyboard. */
  allowVirtualKeyboard: boolean;
}

/** Enforced PC policy; snapshot persisted to `policies.json`. */
export interface Policy {
  /** Monotonic server-assigned version. */
  version: number;
  /** Last change. */
  updatedAt: string;
  /** Shell replacement. */
  shellReplacement: ShellReplacementPolicy;
  /** Process allow/deny list. */
  processAllowlist: ProcessAllowlistPolicy;
  /** USB policy. */
  usb: UsbPolicy;
  /** Web filter. */
  webFilter: WebFilterPolicy;
  /** Explorer lockdown. */
  explorer: ExplorerPolicy;
  /** Power policy. */
  power: PowerPolicy;
  /** Update policy. */
  updates: UpdatesPolicy;
  /** Anti-cheat policy (wire key `anticheat`). */
  anticheat: AntiCheatPolicy;
  /** Kiosk policy. */
  kiosk: KioskPolicy;
}

// ---- BEGIN MANUAL ----
/** Top-level policy section key. */
export type PolicySectionKey = Exclude<keyof Policy, 'version' | 'updatedAt'>;

/** Top-level policy keys in wire order; used for `policy.changed.changed`. */
export const POLICY_SECTION_KEYS: readonly PolicySectionKey[] = [
  'shellReplacement',
  'processAllowlist',
  'usb',
  'webFilter',
  'explorer',
  'power',
  'updates',
  'anticheat',
  'kiosk',
];
// ---- END MANUAL ----

/** Where the policy returned by `policy.reload` came from. */
export const PolicySource = {
  Server: 'server',
  Cache: 'cache',
  File: 'file',
} as const;
/** Where the policy returned by `policy.reload` came from. */
export type PolicySource = (typeof PolicySource)[keyof typeof PolicySource];

/** Request of `policy.reload`. */
export interface PolicyReloadRequest {
  /** Bypass the ETag cache. */
  force?: boolean | null;
}

/** Response of `policy.reload`. */
export interface PolicyReloadResponse {
  /** Policy now in effect. */
  policy: Policy;
  /** Where it came from. */
  source: PolicySource;
  /** Whether it was (re)applied. */
  applied: boolean;
  /** Top-level sections that changed. */
  changed: string[];
}

// ---------------------------------------------------------------------------------------------------------------------
// Updates
// ---------------------------------------------------------------------------------------------------------------------

/** Update channel. */
export const UpdateChannel = {
  Stable: 'stable',
  Beta: 'beta',
} as const;
/** Update channel. */
export type UpdateChannel = (typeof UpdateChannel)[keyof typeof UpdateChannel];

/** Updatable component. */
export const UpdateComponent = {
  Agent: 'agent',
  Shell: 'shell',
} as const;
/** Updatable component. */
export type UpdateComponent = (typeof UpdateComponent)[keyof typeof UpdateComponent];

/** Phase reported by `update.progress`. */
export const UpdatePhase = {
  Downloading: 'downloading',
  Verifying: 'verifying',
  Staging: 'staging',
  Applying: 'applying',
  Failed: 'failed',
} as const;
/** Phase reported by `update.progress`. */
export type UpdatePhase = (typeof UpdatePhase)[keyof typeof UpdatePhase];

/** Update package descriptor (IPC_PROTOCOL.md §6.19; `GET /updates/{channel}/manifest`). */
export interface UpdateManifest {
  /** Channel. */
  channel: UpdateChannel;
  /** Component. */
  component: UpdateComponent;
  /** Package semver. */
  version: string;
  /** HTTPS download URL (Bearer required, `Range` supported). */
  url: string;
  /** Lower-case hex SHA-256 of the package. */
  sha256: string;
  /** Package size in bytes. */
  size: number;
  /** Base64 RSA-PSS-SHA256 signature over the raw package bytes. */
  signature: string;
  /** Markdown release notes. */
  releaseNotes: string;
  /** Must be applied even during a session (after a 60 s notice). */
  mandatory: boolean;
  /** Publication time. */
  publishedAt: string;
  /** Minimum Agent version required (shell packages). */
  minAgentVersion?: string | null;
}

/** Installed component versions. */
export interface ComponentVersions {
  /** Agent semver. */
  agent: string;
  /** Shell semver. */
  shell: string;
}

/** Response of `update.check`. */
export interface UpdateCheckResponse {
  /** Installed versions. */
  current: ComponentVersions;
  /** Available Agent update, when any. */
  agent?: UpdateManifest | null;
  /** Available Shell update, when any. */
  shell?: UpdateManifest | null;
}

/** Request of `update.apply`. */
export interface UpdateApplyRequest {
  /** Component whose staged package to apply. */
  component: UpdateComponent;
}

/** Response of `update.apply` and result of the `update` server command. */
export interface UpdateApplyResponse {
  /** Whether the apply was scheduled. */
  scheduled: boolean;
  /** When it will run. */
  at?: string | null;
}

// ---------------------------------------------------------------------------------------------------------------------
// Typed IPC request → response map
// ---------------------------------------------------------------------------------------------------------------------

// ---- BEGIN MANUAL ----
/** Request/response payload pair per IPC request name; `null` = no payload. */
export interface IpcRequestMap {
  [IpcNames.Auth.Hello]: { request: AuthHelloRequest; response: AuthHelloResponse };
  [IpcNames.Auth.Login]: { request: AuthLoginRequest; response: AuthLoginResponse };
  [IpcNames.Auth.Logout]: { request: AuthLogoutRequest | null; response: AuthLogoutResponse };
  [IpcNames.Auth.Status]: { request: null; response: AuthStatusResponse };
  [IpcNames.Auth.QrStart]: { request: null; response: QrLoginStart };
  [IpcNames.Session.Get]: { request: null; response: Session | null };
  [IpcNames.Session.Start]: { request: SessionStartRequest; response: Session };
  [IpcNames.Session.Pause]: { request: SessionPauseRequest | null; response: Session };
  [IpcNames.Session.Resume]: { request: null; response: Session };
  [IpcNames.Session.End]: { request: SessionEndRequest | null; response: SessionEndResult };
  [IpcNames.Session.Extend]: { request: SessionExtendRequest; response: Session };
  [IpcNames.Session.Lock]: { request: SessionLockRequest | null; response: Session };
  [IpcNames.Session.Unlock]: { request: SessionUnlockRequest; response: Session };
  [IpcNames.Session.TimeLeft]: { request: null; response: SessionTimeLeftResponse };
  [IpcNames.Games.List]: { request: GamesListRequest | null; response: GamesListResponse };
  [IpcNames.Games.Get]: { request: GamesGetRequest; response: Game };
  [IpcNames.Games.Launch]: { request: GamesLaunchRequest; response: LaunchResult };
  [IpcNames.Games.Kill]: { request: GamesKillRequest | null; response: GamesKillResponse };
  [IpcNames.Games.Running]: { request: null; response: GamesRunningResponse };
  [IpcNames.Games.InstallStatus]: { request: GamesInstallStatusRequest; response: GameInstallStatus };
  [IpcNames.Apps.List]: { request: null; response: AppsListResponse };
  [IpcNames.Apps.Launch]: { request: AppsLaunchRequest; response: AppsLaunchResponse };
  [IpcNames.Wallet.Balance]: { request: null; response: Balance };
  [IpcNames.Wallet.Tariffs]: { request: WalletTariffsRequest | null; response: WalletTariffsResponse };
  [IpcNames.Wallet.History]: { request: WalletHistoryRequest | null; response: WalletHistoryResponse };
  [IpcNames.Wallet.TopupIntent]: { request: WalletTopupIntentRequest; response: TopupIntent };
  [IpcNames.Shop.Products]: { request: ShopProductsRequest | null; response: ShopProductsResponse };
  [IpcNames.Shop.Order]: { request: ShopOrderRequest; response: Order };
  [IpcNames.Shop.OrderStatus]: { request: ShopOrderStatusRequest; response: Order };
  [IpcNames.Shop.Orders]: { request: ShopOrdersRequest | null; response: ShopOrdersResponse };
  [IpcNames.Chat.History]: { request: ChatHistoryRequest | null; response: ChatHistoryResponse };
  [IpcNames.Chat.Send]: { request: ChatSendRequest; response: ChatMessage };
  [IpcNames.Chat.MarkRead]: { request: ChatMarkReadRequest; response: ChatMarkReadResponse };
  [IpcNames.Booking.Seats]: { request: BookingSeatsRequest; response: BookingSeatsResponse };
  [IpcNames.Booking.Reserve]: { request: BookingReserveRequest; response: Booking };
  [IpcNames.Booking.Cancel]: { request: BookingCancelRequest; response: Booking };
  [IpcNames.Tournaments.List]: { request: TournamentsListRequest | null; response: TournamentsListResponse };
  [IpcNames.Tournaments.Join]: { request: TournamentsJoinRequest; response: Tournament };
  [IpcNames.Tournaments.Leaderboard]: {
    request: TournamentsLeaderboardRequest;
    response: TournamentsLeaderboardResponse;
  };
  [IpcNames.Profile.Get]: { request: null; response: User };
  [IpcNames.Profile.Update]: { request: ProfileUpdateRequest; response: User };
  [IpcNames.Profile.Stats]: { request: null; response: UserStats };
  [IpcNames.Profile.Achievements]: { request: null; response: ProfileAchievementsResponse };
  [IpcNames.Profile.Loyalty]: { request: null; response: Loyalty };
  [IpcNames.Settings.Get]: { request: null; response: ShellSettings };
  [IpcNames.Settings.Set]: { request: SettingsSetRequest; response: ShellSettings };
  [IpcNames.Sys.Ping]: { request: SysPingRequest; response: SysPongResponse };
  [IpcNames.Sys.PcInfo]: { request: null; response: PcInfo };
  [IpcNames.Sys.Hardware]: { request: SysHardwareRequest | null; response: HardwareInfo };
  [IpcNames.Sys.Metrics]: { request: null; response: PcMetrics };
  [IpcNames.Sys.CallAdmin]: { request: SysCallAdminRequest; response: SysCallAdminResponse };
  [IpcNames.Sys.Reboot]: { request: SysPowerRequest | null; response: ScheduledResult };
  [IpcNames.Sys.Shutdown]: { request: SysPowerRequest | null; response: ScheduledResult };
  [IpcNames.Sys.LockScreen]: { request: SysLockScreenRequest | null; response: OkResponse };
  [IpcNames.Sys.SetVolume]: { request: SetVolumeRequest; response: VolumeState };
  [IpcNames.Sys.SetLocale]: { request: SysSetLocaleRequest; response: SysSetLocaleResponse };
  [IpcNames.Sys.UnlockAdmin]: { request: SysUnlockAdminRequest; response: SysUnlockAdminResponse };
  [IpcNames.Sys.LogClientError]: { request: SysLogClientErrorRequest; response: OkResponse };
  [IpcNames.Sys.AckAdminMessage]: { request: SysAckAdminMessageRequest; response: OkResponse };
  [IpcNames.Policy.Get]: { request: null; response: Policy };
  [IpcNames.Policy.Reload]: { request: PolicyReloadRequest | null; response: PolicyReloadResponse };
  [IpcNames.Update.Check]: { request: null; response: UpdateCheckResponse };
  [IpcNames.Update.Apply]: { request: UpdateApplyRequest; response: UpdateApplyResponse };
}

/** Request payload type of an IPC request name. */
export type IpcRequestOf<N extends AgentCommand> = IpcRequestMap[N]['request'];

/** Response payload type of an IPC request name. */
export type IpcResponseOf<N extends AgentCommand> = IpcRequestMap[N]['response'];
// ---- END MANUAL ----

// ---------------------------------------------------------------------------------------------------------------------
// Server → Agent commands (SERVER_API.md §6.1)
// ---------------------------------------------------------------------------------------------------------------------

/** Command types the server may send over WS / `GET /agents/{pcId}/commands`. */
export const ServerCommandType = {
  Lock: 'lock',
  Unlock: 'unlock',
  Message: 'message',
  Reboot: 'reboot',
  Shutdown: 'shutdown',
  Wake: 'wake',
  EndSession: 'endSession',
  ExtendSession: 'extendSession',
  LaunchGame: 'launchGame',
  KillGame: 'killGame',
  SetPolicy: 'setPolicy',
  ReloadPolicy: 'reloadPolicy',
  Screenshot: 'screenshot',
  RemoteControlStart: 'remoteControlStart',
  RemoteControlStop: 'remoteControlStop',
  Update: 'update',
  ShowAds: 'showAds',
  SetVolume: 'setVolume',
  RefreshConfig: 'refreshConfig',
} as const;
/** Command types the server may send over WS / `GET /agents/{pcId}/commands`. */
export type ServerCommandType = (typeof ServerCommandType)[keyof typeof ServerCommandType];

// ---- BEGIN MANUAL ----
const SERVER_COMMAND_TYPE_SET: ReadonlySet<string> = new Set<string>(Object.values(ServerCommandType));

/** `true` when `name` is a known server command type. */
export function isServerCommandType(name: unknown): name is ServerCommandType {
  return typeof name === 'string' && SERVER_COMMAND_TYPE_SET.has(name);
}
// ---- END MANUAL ----

/** Payload of `lock`; also `shell.command{lock}` args. */
export interface LockCommand {
  /** Machine-readable reason. */
  reason?: string | null;
  /** Message to display. */
  message?: string | null;
}

/** Payload of `message`. */
export interface MessageCommand {
  /** Message id (acked via `sys.ackAdminMessage`). */
  id: string;
  /** Sender name. */
  from: string;
  /** Text. */
  text: string;
  /** Severity. */
  level: NotificationLevel;
  /** User must acknowledge. */
  requiresAck: boolean;
}

/** Result of `message`; a second ack is sent when the user acknowledges. */
export interface MessageDeliveryResult {
  /** When the Shell displayed it. */
  deliveredAt: string;
  /** When the user acknowledged it. */
  ackedAt?: string | null;
}

/** Payload of `reboot` and `shutdown`. */
export interface PowerCommand {
  /** Delay before the action. */
  delaySec: number;
  /** End an active session first. */
  force: boolean;
  /** Message to display. */
  message?: string | null;
}

/** Result of power commands and `sys.reboot`/`sys.shutdown`. */
export interface ScheduledResult {
  /** When the action will run. */
  scheduledAt: string;
}

/** Payload of `wake`. */
export interface WakeCommand {
  /** MAC of the PC to wake, `AA:BB:CC:DD:EE:FF`. */
  targetMac: string;
}

/** Payload of `endSession`. */
export interface EndSessionCommand {
  /** Session to end. */
  sessionId: string;
  /** End reason. */
  reason: SessionEndReason;
}

/** Payload of `extendSession`. */
export interface ExtendSessionCommand {
  /** Session to extend. */
  sessionId: string;
  /** Minutes to add. */
  minutes: number;
  /** Charge the wallet (false when already billed server-side). */
  charge: boolean;
}

/** Result carrying the resulting session. */
export interface SessionResult {
  /** Session after the command. */
  session: Session;
}

/** Payload of `killGame`. */
export interface KillGameCommand {
  /** Game to kill. */
  gameId?: string | null;
  /** Process to kill. */
  pid?: number | null;
  /** Terminate immediately instead of a graceful close. */
  force: boolean;
}

/** Result of `setPolicy`. */
export interface SetPolicyResult {
  /** Policy version. */
  version: number;
  /** Whether it was applied without errors. */
  applied: boolean;
}

/** Result of `reloadPolicy`. */
export interface ReloadPolicyResult {
  /** Policy version now in effect. */
  version: number;
}

/** Payload of `screenshot`. */
export interface ScreenshotCommand {
  /** JPEG quality 1–100. */
  quality: number;
  /** Pre-signed `PUT` URL for the JPEG. */
  uploadUrl: string;
  /** Monitor index; null = primary. */
  monitor?: number | null;
  /** Downscale to this width. */
  maxWidth?: number | null;
}

/** Result of `screenshot`. */
export interface ScreenshotResult {
  /** Image width. */
  width: number;
  /** Image height. */
  height: number;
  /** Uploaded size. */
  bytes: number;
  /** Upload time. */
  uploadedAt: string;
}

/** Payload of `remoteControlStart`. */
export interface RemoteControlStartCommand {
  /** Relay session token. */
  sessionToken: string;
  /** Relay `wss://` URL. */
  relayUrl: string;
  /** Capture frame rate. */
  fps: number;
  /** Allow remote keyboard/mouse input. */
  allowInput: boolean;
  /** Admin shown in the on-screen indicator. */
  adminName: string;
}

/** Result of `remoteControlStart`. */
export interface RemoteControlStartResult {
  /** Start time. */
  startedAt: string;
}

/** Payload of `remoteControlStop`. */
export interface RemoteControlStopCommand {
  /** Relay session token. */
  sessionToken: string;
}

/** Result of `remoteControlStop`. */
export interface RemoteControlStopResult {
  /** Stop time. */
  stoppedAt: string;
  /** Session length. */
  durationSec: number;
}

/** Payload of `update`. */
export interface UpdateCommand {
  /** Component to update. */
  component: UpdateComponent;
  /** Apply immediately (subject to session/mandatory rules). */
  applyNow: boolean;
  /** Manifest to use; null = fetch. */
  manifest?: UpdateManifest | null;
}

/** Payload of `refreshConfig`; all flags false/absent = refresh everything. */
export interface RefreshConfigCommand {
  /** Re-fetch `/agents/{pcId}/config`. */
  config?: boolean | null;
  /** Re-fetch games. */
  games?: boolean | null;
  /** Re-fetch apps. */
  apps?: boolean | null;
  /** Re-fetch tariffs. */
  tariffs?: boolean | null;
  /** Re-fetch shop products. */
  products?: boolean | null;
  /** Re-download themes. */
  themes?: boolean | null;
}

// ---- BEGIN MANUAL ----
/** `true` when a `refreshConfig` command selects no specific cache (refresh everything). */
export function isRefreshConfigAll(command: RefreshConfigCommand | null | undefined): boolean {
  return command == null || !Object.values(command).some((v) => v === true);
}
// ---- END MANUAL ----

/** Result of `refreshConfig`. */
export interface RefreshConfigResult {
  /** Caches refreshed (`config`, `games`, `apps`, `tariffs`, `products`, `themes`). */
  refreshed: string[];
}

// ---- BEGIN MANUAL ----
/** Payload type per server command. */
export interface ServerCommandPayloadMap {
  lock: LockCommand | null;
  unlock: null;
  message: MessageCommand;
  reboot: PowerCommand;
  shutdown: PowerCommand;
  wake: WakeCommand;
  endSession: EndSessionCommand;
  extendSession: ExtendSessionCommand;
  launchGame: LaunchRequest;
  killGame: KillGameCommand;
  setPolicy: Policy;
  reloadPolicy: null;
  screenshot: ScreenshotCommand;
  remoteControlStart: RemoteControlStartCommand;
  remoteControlStop: RemoteControlStopCommand;
  update: UpdateCommand;
  showAds: ShowAdsArgs;
  setVolume: SetVolumeRequest;
  refreshConfig: RefreshConfigCommand | null;
}

/** Ack result type per server command. */
export interface ServerCommandResultMap {
  lock: null;
  unlock: null;
  message: MessageDeliveryResult;
  reboot: ScheduledResult;
  shutdown: ScheduledResult;
  wake: null;
  endSession: SessionResult;
  extendSession: SessionResult;
  launchGame: LaunchResult;
  killGame: GamesKillResponse;
  setPolicy: SetPolicyResult;
  reloadPolicy: ReloadPolicyResult;
  screenshot: ScreenshotResult;
  remoteControlStart: RemoteControlStartResult;
  remoteControlStop: RemoteControlStopResult;
  update: UpdateApplyResponse;
  showAds: null;
  setVolume: VolumeState;
  refreshConfig: RefreshConfigResult;
}

/** Normalized server command as handled by the Agent's command dispatcher. */
export interface ServerCommand<T extends ServerCommandType = ServerCommandType> {
  /** Command id (dedupe key, 24 h). */
  id: string;
  /** Command type. */
  type: T;
  /** Server timestamp. */
  issuedAt: string;
  /** Typed payload or null. */
  payload?: ServerCommandPayloadMap[T] | null;
  /** Admin login or `system`, when known. */
  issuedBy?: string | null;
  /** Earlier pending command this one cancels. */
  supersedes?: string | null;
  /** Discard (ack with `timeout`) after this time. */
  expiresAt?: string | null;
}

/** `true` when the command's `expiresAt` has passed at `now` (ISO string or Date). */
export function isServerCommandExpired(command: Pick<ServerCommand, 'expiresAt'>, now: string | Date): boolean {
  if (command.expiresAt == null) {
    return false;
  }
  const nowMs = typeof now === 'string' ? Date.parse(now) : now.getTime();
  return Date.parse(command.expiresAt) <= nowMs;
}

/** Wire form of a queued command returned by `GET /agents/{pcId}/commands`. */
export interface ServerCommandEnvelope<T extends ServerCommandType = ServerCommandType> {
  /** Command id. */
  id: string;
  /** Server timestamp. */
  ts: string;
  /** Command type. */
  name: T;
  /** Payload or null. */
  payload?: ServerCommandPayloadMap[T] | null;
  /** Admin login, when known. */
  issuedBy?: string | null;
  /** Earlier pending command this one cancels. */
  supersedes?: string | null;
  /** Expiry. */
  expiresAt?: string | null;
}
// ---- END MANUAL ----

/** Response of `GET /agents/{pcId}/commands`. */
export interface ServerCommandsResponse {
  /** Pending commands, oldest first. */
  items: ServerCommandEnvelope[];
}

// ---- BEGIN MANUAL ----
/** Body of `POST /agents/{pcId}/commands/{commandId}/ack` and payload of a WS `ack` frame. */
export interface CommandAck<TResult = JsonObject> {
  /** Whether the command succeeded. */
  ok: boolean;
  /** Failure, when `ok` is false. */
  error?: IpcError | null;
  /** Typed result or null. */
  result?: TResult | null;
}
// ---- END MANUAL ----

// ---------------------------------------------------------------------------------------------------------------------
// Agent → Server events (SERVER_API.md §6.2)
// ---------------------------------------------------------------------------------------------------------------------

/** Events the Agent emits over WS; replayed via REST when offline. */
export const AgentEventType = {
  SessionStarted: 'sessionStarted',
  SessionEnded: 'sessionEnded',
  GameLaunched: 'gameLaunched',
  GameExited: 'gameExited',
  AnticheatViolation: 'anticheatViolation',
  HardwareChanged: 'hardwareChanged',
  OfflineQueueFlushed: 'offlineQueueFlushed',
} as const;
/** Events the Agent emits over WS; replayed via REST when offline. */
export type AgentEventType = (typeof AgentEventType)[keyof typeof AgentEventType];

/** Payload of `gameLaunched`. */
export interface GameLaunchedEvent {
  /** Session. */
  sessionId: string;
  /** Game. */
  gameId: string;
  /** Process id. */
  pid: number;
  /** Lease used, when any. */
  accountLeaseId?: string | null;
  /** Launch time. */
  at: string;
}

/** Payload of `gameExited`. */
export interface GameExitedEvent {
  /** Session. */
  sessionId: string;
  /** Game. */
  gameId: string;
  /** Process id. */
  pid: number;
  /** Exit code. */
  exitCode: number;
  /** Seconds played. */
  playedSec: number;
  /** Exit time. */
  at: string;
}

/** Payload of `hardwareChanged`. */
export interface HardwareChangedEvent {
  /** New inventory. */
  hardware: HardwareInfo;
  /** Changed top-level keys (`gpu`, `disks`, …). */
  diff: string[];
}

/** Payload of `offlineQueueFlushed`. */
export interface OfflineQueueFlushedEvent {
  /** Entries delivered. */
  count: number;
  /** Entries moved to the dead-letter table. */
  deadlettered: number;
  /** Start of the offline period. */
  offlineFrom: string;
  /** End of the offline period. */
  offlineTo: string;
}

// ---- BEGIN MANUAL ----
/** Payload type per agent event. */
export interface AgentEventPayloadMap {
  sessionStarted: SessionStartedEvent;
  sessionEnded: SessionEndedEvent;
  gameLaunched: GameLaunchedEvent;
  gameExited: GameExitedEvent;
  anticheatViolation: AntiCheatReport;
  hardwareChanged: HardwareChangedEvent;
  offlineQueueFlushed: OfflineQueueFlushedEvent;
}

/** Agent → server event. */
export interface AgentEvent<T extends AgentEventType = AgentEventType> {
  /** Event type (also the WS frame `name`). */
  type: T;
  /** Event time. */
  at: string;
  /** Typed payload. */
  payload?: AgentEventPayloadMap[T] | null;
}
// ---- END MANUAL ----

// ---------------------------------------------------------------------------------------------------------------------
// WebSocket (SERVER_API.md §6)
// ---------------------------------------------------------------------------------------------------------------------

/** WS frame type. */
export const WsFrameType = {
  Command: 'command',
  Ack: 'ack',
  Event: 'event',
  Ping: 'ping',
  Pong: 'pong',
  Push: 'push',
} as const;
/** WS frame type. */
export type WsFrameType = (typeof WsFrameType)[keyof typeof WsFrameType];

/** Server → Agent pushes (`type = "push"`, no ack). */
export const WsPushKind = {
  WalletUpdated: 'walletUpdated',
  ChatMessage: 'chatMessage',
  Notification: 'notification',
  OrderUpdated: 'orderUpdated',
  BookingUpdated: 'bookingUpdated',
  TournamentUpdated: 'tournamentUpdated',
  SessionUpdated: 'sessionUpdated',
  PcStatusChanged: 'pcStatusChanged',
  UserRevoked: 'userRevoked',
} as const;
/** Server → Agent pushes (`type = "push"`, no ack). */
export type WsPushKind = (typeof WsPushKind)[keyof typeof WsPushKind];

// ---- BEGIN MANUAL ----
const WS_PUSH_KIND_SET: ReadonlySet<string> = new Set<string>(Object.values(WsPushKind));

/** `true` when `name` is a known push kind. */
export function isWsPushKind(name: unknown): name is WsPushKind {
  return typeof name === 'string' && WS_PUSH_KIND_SET.has(name);
}
// ---- END MANUAL ----

/** Payload of `pcStatusChanged`. */
export interface PcStatusChangedPush {
  /** PC. */
  pcId: string;
  /** New status. */
  status: PcStatus;
}

/** Payload of `userRevoked`. */
export interface UserRevokedPush {
  /** Revoked user. */
  userId: string;
  /** Reason. */
  reason: string;
}

// ---- BEGIN MANUAL ----
/** Payload type per push kind. */
export interface WsPushPayloadMap {
  walletUpdated: Balance;
  chatMessage: ChatMessage;
  notification: Notification;
  orderUpdated: Order;
  bookingUpdated: Booking;
  tournamentUpdated: Tournament;
  sessionUpdated: Session;
  pcStatusChanged: PcStatusChangedPush;
  userRevoked: UserRevokedPush;
}

/** Ack body inside an `ack` frame. */
export interface WsAck<TResult = JsonObject> extends CommandAck<TResult> {
  /** Id of the command being acknowledged. */
  id: string;
}

/** Maximum WS frame size in bytes. */
export const WS_MAX_FRAME_BYTES = 1024 * 1024;

/** WS subprotocol negotiated on connect. */
export const WS_SUBPROTOCOL = 'clubshell.v1';

/** One text frame on `wss://<server>/ws/agent`; `payload` key is always present. */
export interface WsFrame<TPayload = JsonObject> {
  /** Frame type. */
  type: WsFrameType;
  /** Frame id (command id for commands). */
  id: string;
  /** Sender timestamp. */
  ts: string;
  /** `ServerCommandType` / `AgentEventType` / `WsPushKind` wire name; required for command/event/push. */
  name?: string | null;
  /** Body or null. */
  payload: TPayload | null;
  /** Ack body; required for `ack`. */
  ack?: WsAck | null;
  /** Command only: earlier pending command this one cancels. */
  supersedes?: string | null;
  /** Command only: discard after this time. */
  expiresAt?: string | null;
}
// ---- END MANUAL ----

// ---------------------------------------------------------------------------------------------------------------------
// Agent REST (register / refresh / heartbeat / telemetry / support)
// ---------------------------------------------------------------------------------------------------------------------

/** Body of `POST /agents/register` (auth `X-Club-Key`). */
export interface AgentRegisterRequest {
  /** Hardware id (sha256 hex). */
  hwid: string;
  /** Windows machine name. */
  machineName: string;
  /** Agent semver. */
  agentVersion: string;
  /** Inventory. */
  hardware: HardwareInfo;
  /** IPv4 address. */
  ipAddress: string;
  /** Primary MAC. */
  macAddress: string;
  /** Hint for re-registration after reinstall. */
  previousPcId?: string | null;
}

/** Response of `POST /agents/register`; secrets are stored DPAPI-protected by the Agent. */
export interface AgentRegisterResponse {
  /** Assigned PC id. */
  pcId: string;
  /** PC record. */
  pc: Pc;
  /** Agent JWT (≈ 1 h). */
  accessToken: string;
  /** Opaque refresh token (30 d, single-use rotation). */
  refreshToken: string;
  /** Base64 32-byte HMAC key for request signing. */
  signingSecret: string;
  /** Access token expiry. */
  expiresAt: string;
  /** Server clock. */
  serverTime: string;
  /** Server configuration overrides. */
  config: AgentServerConfig;
}

/** Body of `POST /agents/refresh`. */
export interface AgentRefreshRequest {
  /** Current refresh token. */
  refreshToken: string;
  /** Hardware id. */
  hwid: string;
}

/** Response of `POST /agents/refresh`. */
export interface AgentRefreshResponse {
  /** New JWT. */
  accessToken: string;
  /** New refresh token (old one is invalid). */
  refreshToken: string;
  /** Access token expiry. */
  expiresAt: string;
  /** Present only when rotated; must be switched atomically. */
  signingSecret?: string | null;
}

/** Running game summary in a heartbeat. */
export interface HeartbeatRunningGame {
  /** Game. */
  gameId: string;
  /** Process id. */
  pid: number;
  /** Launch time. */
  startedAt: string;
}

/** Body of `POST /agents/{pcId}/heartbeat`. */
export interface HeartbeatRequest {
  /** Agent's own view of the PC status. */
  status: PcStatus;
  /** Open session, when any. */
  currentSessionId?: string | null;
  /** Agent semver. */
  agentVersion: string;
  /** Shell semver. */
  shellVersion: string;
  /** OS uptime. */
  uptimeSec: number;
  /** IPv4 address. */
  ipAddress: string;
  /** Applied policy version. */
  policyVersion: number;
  /** Running games. */
  runningGames: HeartbeatRunningGame[];
  /** Outbox size. */
  offlineQueue: number;
  /** Whether the Shell pipe connection is alive. */
  shellConnected: boolean;
}

/** Response of `POST /agents/{pcId}/heartbeat`. */
export interface HeartbeatResponse {
  /** Server clock (used for offset correction). */
  serverTime: string;
  /** Authoritative status. */
  pcStatus: PcStatus;
  /** Latest policy version; reload when newer than applied. */
  policyVersion: number;
  /** Latest config version. */
  configVersion: number;
  /** Games/apps catalogue version; refresh lists when changed. */
  catalogVersion: string;
  /** Commands queued while WS was down. */
  pendingCommands: number;
  /** Server view of the current session for reconciliation. */
  session?: Session | null;
}

/** Well-known values of `TelemetryEvent.kind`. */
export const TelemetryEventKinds = {
  ShellCrash: 'shellCrash',
  ShellCrashLoop: 'shellCrashLoop',
  PolicyApplyFailed: 'policyApplyFailed',
  UpdateFailed: 'updateFailed',
  PipeError: 'pipeError',
  LauncherError: 'launcherError',
  Deadletter: 'deadletter',
} as const;
/** Well-known telemetry event kind. */
export type TelemetryEventKind = (typeof TelemetryEventKinds)[keyof typeof TelemetryEventKinds];

/** Agent diagnostic event in a telemetry batch. */
export interface TelemetryEvent {
  /** Kind ({@link TelemetryEventKinds}). */
  kind: string;
  /** Event time. */
  at: string;
  /** Structured data. */
  data: JsonObject;
}

/** Body of `POST /agents/{pcId}/telemetry`. */
export interface TelemetryBatch {
  /** Metric samples (≤ 120). */
  samples: PcMetrics[];
  /** Diagnostic events. */
  events: TelemetryEvent[];
  /** Inventory when changed / on rescan. */
  hardware?: HardwareInfo | null;
  /** Last ≤ 50 Warning+ log lines when `events` is non-empty. */
  logsTail?: string[] | null;
}

/** Maximum samples per telemetry batch. */
export const TELEMETRY_MAX_SAMPLES = 120;

/** Maximum log lines per telemetry batch. */
export const TELEMETRY_MAX_LOG_LINES = 50;

/** Body of `POST /support/call-admin` (sent with an `Idempotency-Key`). */
export interface CallAdminTicketRequest {
  /** PC. */
  pcId: string;
  /** Logged-in user, when any. */
  userId?: string | null;
  /** Category. */
  category: CallAdminCategory;
  /** Free text. */
  message?: string | null;
  /** Request time (client clock, for offline replay). */
  at: string;
}
