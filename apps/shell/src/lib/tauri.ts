/**
 * Frontend invoke wrapper (docs/TAURI_COMMANDS.md §4).
 *
 * Inside the Tauri webview every call goes through `@tauri-apps/api/core` and every event through
 * `@tauri-apps/api/event`. In a plain browser (`VITE_MOCK=1`, Vite dev, Playwright) calls are dispatched
 * to the in-memory mock registry in `@/mocks/handlers` and events to its bus, so the whole UI is usable
 * offline. Errors are always normalized to {@link ShellApiError}.
 */
import { invoke as tauriInvoke } from '@tauri-apps/api/core';
import { listen as tauriListen } from '@tauri-apps/api/event';
import type {
  Achievement,
  AgentEventMap,
  App,
  AppsLaunchResponse,
  AuthLoginRequest,
  AuthLoginResponse,
  AuthLogoutResponse,
  AuthStatusResponse,
  Balance,
  Booking,
  BookingSeatsResponse,
  CallAdminCategory,
  ChatHistoryRequest,
  ChatHistoryResponse,
  ChatMarkReadResponse,
  ChatMessage,
  ErrorCode,
  Game,
  GameInstallStatus,
  GamesKillRequest,
  GamesKillResponse,
  GamesLaunchRequest,
  GamesListRequest,
  GamesListResponse,
  HardwareInfo,
  LaunchResult,
  Locale,
  Loyalty,
  Money,
  MonitorInfo,
  OkResponse,
  Order,
  PcInfo,
  PcMetrics,
  Policy,
  PolicyReloadResponse,
  Product,
  ProfileUpdateRequest,
  QrLoginStart,
  RunningGame,
  ScheduledResult,
  Session,
  SessionEndReason,
  SessionEndResult,
  SessionStartRequest,
  SessionTimeLeftResponse,
  SettingsSetRequest,
  ShellFeatures,
  ShellSettings,
  ShopOrderRequest,
  ShopOrdersRequest,
  ShopOrdersResponse,
  ShopProductsRequest,
  SysCallAdminResponse,
  SysLogClientErrorRequest,
  SysSetLocaleResponse,
  SysUnlockAdminResponse,
  Theme,
  TopupIntent,
  TopupProvider,
  Tournament,
  TournamentsLeaderboardResponse,
  TournamentsListRequest,
  UpdateApplyResponse,
  UpdateCheckResponse,
  UpdateComponent,
  User,
  UserStats,
  VolumeState,
  WalletHistoryRequest,
  WalletHistoryResponse,
  WalletTariffsResponse,
} from '@clubshell/contracts';
import { isErrorCode } from '@clubshell/contracts';
import { emitMock, registry, subscribeMock } from '@/mocks/handlers';

// ---------------------------------------------------------------------------------------------------------------------
// Errors
// ---------------------------------------------------------------------------------------------------------------------

/** Where a {@link ShellError} was produced. */
export type ShellErrorSource = 'ipc' | 'pipe' | 'tauri' | 'mock';

/** Error object a Tauri command rejects with (TAURI_COMMANDS.md §1.1). */
export type ShellError = {
  code: ErrorCode;
  message: string;
  details?: unknown;
  source?: ShellErrorSource;
};

/** Normalized error thrown by {@link invoke} and every `api.*` method. */
export class ShellApiError extends Error {
  readonly code: ErrorCode;
  readonly details?: unknown;
  readonly source: ShellErrorSource;

  constructor(e: ShellError) {
    super(e.message);
    this.name = 'ShellApiError';
    this.code = e.code;
    this.details = e.details ?? undefined;
    this.source = e.source ?? 'tauri';
  }

  /** Plain object form (for logging / analytics). */
  toJSON(): ShellError {
    return { code: this.code, message: this.message, details: this.details, source: this.source };
  }
}

/** `true` when `e` is a {@link ShellApiError} (also across module instances). */
export function isShellApiError(e: unknown): e is ShellApiError {
  return (
    e instanceof ShellApiError ||
    (typeof e === 'object' && e !== null && (e as { name?: unknown }).name === 'ShellApiError')
  );
}

/** Converts any rejection value into a {@link ShellApiError}. */
export function toShellApiError(e: unknown, fallbackSource: ShellErrorSource = 'tauri'): ShellApiError {
  if (e instanceof ShellApiError) {
    return e;
  }
  if (typeof e === 'object' && e !== null) {
    const o = e as { code?: unknown; message?: unknown; details?: unknown; source?: unknown };
    if (isErrorCode(o.code)) {
      const source =
        o.source === 'ipc' || o.source === 'pipe' || o.source === 'tauri' || o.source === 'mock'
          ? o.source
          : fallbackSource;
      return new ShellApiError({
        code: o.code,
        message: typeof o.message === 'string' ? o.message : o.code,
        details: o.details ?? undefined,
        source,
      });
    }
    if (e instanceof Error) {
      return new ShellApiError({
        code: 'internal',
        message: e.message,
        details: { name: e.name },
        source: fallbackSource,
      });
    }
  }
  return new ShellApiError({
    code: 'internal',
    message: typeof e === 'string' ? e : 'Unknown error',
    source: fallbackSource,
  });
}

// ---------------------------------------------------------------------------------------------------------------------
// Environment
// ---------------------------------------------------------------------------------------------------------------------

/** `true` when running inside the Tauri webview (and not forced into mock mode). */
export function isTauri(): boolean {
  return typeof window !== 'undefined' && window.__TAURI_INTERNALS__ !== undefined && import.meta.env.VITE_MOCK !== '1';
}

/** RFC 4122 v4 id; falls back to `getRandomValues` where `randomUUID` is unavailable (non-secure contexts). */
export function uuid(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  bytes[6] = ((bytes[6] ?? 0) & 0x0f) | 0x40;
  bytes[8] = ((bytes[8] ?? 0) & 0x3f) | 0x80;
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

// ---------------------------------------------------------------------------------------------------------------------
// invoke / listen
// ---------------------------------------------------------------------------------------------------------------------

/** Options of {@link invoke}. */
export interface InvokeOptions {
  /** Reject with `timeout` after this many ms (default 20 000; `games_launch` uses 120 000). */
  timeoutMs?: number;
  /** Abort → reject with `timeout` and `details.aborted = true`. */
  signal?: AbortSignal;
}

const DEFAULT_TIMEOUT_MS = 20_000;
const LAUNCH_TIMEOUT_MS = 120_000;

function withTimeout<T>(work: Promise<T>, cmd: string, opts: InvokeOptions | undefined): Promise<T> {
  const timeoutMs =
    opts?.timeoutMs ?? (cmd === 'games_launch' || cmd === 'apps_launch' ? LAUNCH_TIMEOUT_MS : DEFAULT_TIMEOUT_MS);
  const signal = opts?.signal;
  if (signal?.aborted) {
    return Promise.reject(
      new ShellApiError({ code: 'timeout', message: `${cmd} aborted`, details: { aborted: true }, source: 'pipe' }),
    );
  }
  return new Promise<T>((resolve, reject) => {
    let settled = false;
    const timer = setTimeout(() => {
      if (!settled) {
        settled = true;
        reject(
          new ShellApiError({
            code: 'timeout',
            message: `${cmd} timed out after ${timeoutMs} ms`,
            details: { timeoutMs },
            source: 'pipe',
          }),
        );
      }
    }, timeoutMs);
    const onAbort = (): void => {
      if (!settled) {
        settled = true;
        clearTimeout(timer);
        reject(
          new ShellApiError({ code: 'timeout', message: `${cmd} aborted`, details: { aborted: true }, source: 'pipe' }),
        );
      }
    };
    signal?.addEventListener('abort', onAbort, { once: true });
    work.then(
      (v) => {
        if (!settled) {
          settled = true;
          clearTimeout(timer);
          signal?.removeEventListener('abort', onAbort);
          resolve(v);
        }
      },
      (e: unknown) => {
        if (!settled) {
          settled = true;
          clearTimeout(timer);
          signal?.removeEventListener('abort', onAbort);
          reject(toShellApiError(e));
        }
      },
    );
  });
}

/**
 * Calls a Tauri command (or its mock). Rejects with {@link ShellApiError} only.
 * `args` keys are camelCase (Tauri maps them to the snake_case Rust parameters).
 */
export function invoke<T>(cmd: string, args?: Record<string, unknown>, opts?: InvokeOptions): Promise<T> {
  if (isTauri()) {
    return withTimeout(tauriInvoke<T>(cmd, args), cmd, opts);
  }
  const handler = registry[cmd];
  if (!handler) {
    return Promise.reject(
      new ShellApiError({
        code: 'notFound',
        message: `No mock for command ${cmd}`,
        details: { name: cmd },
        source: 'mock',
      }),
    );
  }
  const work = Promise.resolve()
    .then(() => handler(args ?? {}))
    .then(
      (v) => v as T,
      (e: unknown) => Promise.reject(toShellApiError(e, 'mock')),
    );
  return withTimeout(work, cmd, opts);
}

/**
 * Subscribes to a Tauri event (`agent://…` / `kiosk://…`) or to the mock bus.
 * Returns a synchronous unsubscribe; an unsubscribe before the native listener is attached is honoured.
 */
export function listen<T>(event: string, handler: (payload: T) => void): () => void {
  if (!isTauri()) {
    return subscribeMock<T>(event, handler);
  }
  let active = true;
  let unlisten: (() => void) | null = null;
  tauriListen<T>(event, (e) => {
    if (active) {
      handler(e.payload);
    }
  }).then(
    (fn) => {
      if (active) {
        unlisten = fn;
      } else {
        fn();
      }
    },
    (e: unknown) => {
      console.warn(`[tauri] listen(${event}) failed`, e);
    },
  );
  return () => {
    active = false;
    if (unlisten) {
      unlisten();
      unlisten = null;
    }
  };
}

/** Dev only: fires an event on the mock bus (no-op inside Tauri). */
export function emitMockEvent<T>(event: string, payload: T): void {
  if (!isTauri()) {
    emitMock(event, payload);
  }
}

const ABSOLUTE_URL = /^(https?:|data:|blob:|asset:|tauri:|file:)/i;

/**
 * Resolves a ProgramData-relative asset path (`themes\…`, `cache\media\…`) to a URL the webview may load.
 * Absolute URLs pass through untouched.
 */
export function assetUrl(path: string): Promise<string> {
  if (path.length === 0 || ABSOLUTE_URL.test(path)) {
    return Promise.resolve(path);
  }
  return invoke<string>('kiosk_asset_url', { path });
}

// ---------------------------------------------------------------------------------------------------------------------
// Local (kiosk) types not covered by the contracts package
// ---------------------------------------------------------------------------------------------------------------------

/** Monitor as reported by the kiosk layer (`MonitorDto`); superset of the contracts `MonitorInfo`. */
export interface KioskMonitor extends MonitorInfo {
  name: string;
  x: number;
  y: number;
  scale: number;
}

/** Native overlay kind. */
export type OverlayKind = 'lock' | 'ads' | 'message' | 'none';

/** Response of `kiosk_state`. */
export interface KioskState {
  agentConnected: boolean;
  fullscreen: boolean;
  guardActive: boolean;
  hooksActive: boolean;
  monitors: KioskMonitor[];
  idle: boolean;
  idleSec: number;
  gamepadConnected: boolean;
  locked: boolean;
  gameMode: boolean;
  overlay: OverlayKind;
  dev: boolean;
  version: string;
  devtools: boolean;
}

/** Snapshot of one gamepad (`kiosk_gamepad_state`). */
export interface GamepadState {
  index: number;
  connected: boolean;
  buttons: number;
  leftX: number;
  leftY: number;
  rightX: number;
  rightY: number;
  lt: number;
  rt: number;
}

/** Gamepad button names of `kiosk://gamepad`. */
export type GamepadButtonName =
  | 'a'
  | 'b'
  | 'x'
  | 'y'
  | 'lb'
  | 'rb'
  | 'back'
  | 'start'
  | 'ls'
  | 'rs'
  | 'up'
  | 'down'
  | 'left'
  | 'right'
  | 'guide';

/** Gamepad axis names of `kiosk://gamepad`. */
export type GamepadAxisName = 'leftX' | 'leftY' | 'rightX' | 'rightY' | 'lt' | 'rt';

/** Hotkey names of `kiosk://hotkey`. */
export type HotkeyName = 'exit' | 'callAdmin' | 'lock' | 'volumeUp' | 'volumeDown' | 'mute' | 'blocked';

/** `shell.json` as returned by `settings_get_shell_config`. */
export interface ShellConfig {
  version: number;
  locale: Locale;
  theme: string;
  ipc: {
    pipeName: string;
    connectTimeoutMs: number;
    requestTimeoutMs: number;
    reconnectMinMs: number;
    reconnectMaxMs: number;
  };
  kiosk: {
    fullscreen: boolean;
    topmostGuard: boolean;
    hideTaskbar: boolean;
    blockAltTab: boolean;
    blockWinKey: boolean;
    blockCtrlAltDel: boolean;
    hideCursorAfterSec: number;
    overlayOnLock: boolean;
    allowVirtualKeyboard: boolean;
    exitHotkey: string;
    adminPinHash: string | null;
  };
  idle: { timeoutSec: number; dimAfterSec: number; screensaverAfterSec: number };
  ads: {
    enabled: boolean;
    intervalSec: number;
    durationSec: number;
    playlist: { url: string; type: 'image' | 'video'; durationSec: number }[];
  };
  gamepad: { enabled: boolean; pollMs: number; deadzone: number; navigation: boolean };
  monitors: { primaryIndex: number; secondaryMode: 'black' | 'ads' | 'mirror' };
  ui: {
    defaultRoute: string;
    gridColumns: number;
    showClock: boolean;
    clockFormat: string;
    showMetricsOverlay: boolean;
    showSessionBar: boolean;
    coverAspect: string;
    currencyFormat: { locale: string; minorDigits: number };
  };
  features: ShellFeatures;
  sound: { uiSounds: boolean; defaultVolume: number };
  logging: { level: string; directory: string };
  devtools: boolean;
}

// ---------------------------------------------------------------------------------------------------------------------
// Typed command surface (one method per command in TAURI_COMMANDS.md §2)
// ---------------------------------------------------------------------------------------------------------------------

export const api = {
  auth: {
    login: (req: AuthLoginRequest): Promise<AuthLoginResponse> => invoke('auth_login', { req }),
    logout: (reason?: SessionEndReason): Promise<AuthLogoutResponse> => invoke('auth_logout', { reason }),
    status: (): Promise<AuthStatusResponse> => invoke('auth_status'),
    qrStart: (): Promise<QrLoginStart> => invoke('auth_qr_start'),
  },
  session: {
    get: (): Promise<Session | null> => invoke('session_get'),
    start: (req: SessionStartRequest): Promise<Session> => invoke('session_start', { req }),
    pause: (reason?: string): Promise<Session> => invoke('session_pause', { reason }),
    resume: (): Promise<Session> => invoke('session_resume'),
    end: (reason?: SessionEndReason): Promise<SessionEndResult> => invoke('session_end', { reason }),
    extend: (minutes: number, tariffId?: string): Promise<Session> => invoke('session_extend', { minutes, tariffId }),
    lock: (reason?: string): Promise<Session> => invoke('session_lock', { reason }),
    unlock: (secret: { password?: string; pin?: string }): Promise<Session> =>
      invoke('session_unlock', { password: secret.password, pin: secret.pin }),
    timeLeft: (): Promise<SessionTimeLeftResponse> => invoke('session_time_left'),
  },
  games: {
    list: (q?: GamesListRequest): Promise<GamesListResponse> => invoke('games_list', { q }),
    get: (gameId: string): Promise<Game> => invoke('games_get', { gameId }),
    launch: (req: GamesLaunchRequest): Promise<LaunchResult> => invoke('games_launch', { req }),
    kill: (target?: GamesKillRequest): Promise<GamesKillResponse> =>
      invoke('games_kill', {
        gameId: target?.gameId ?? undefined,
        pid: target?.pid ?? undefined,
        force: target?.force ?? undefined,
      }),
    running: (): Promise<RunningGame[]> => invoke('games_running'),
    installStatus: (gameId: string): Promise<GameInstallStatus> => invoke('games_install_status', { gameId }),
  },
  apps: {
    list: (): Promise<App[]> => invoke('apps_list'),
    launch: (appId: string, args?: string): Promise<AppsLaunchResponse> => invoke('apps_launch', { appId, args }),
  },
  wallet: {
    balance: (): Promise<Balance> => invoke('wallet_balance'),
    tariffs: (zone?: string): Promise<WalletTariffsResponse> => invoke('wallet_tariffs', { zone }),
    history: (q?: WalletHistoryRequest): Promise<WalletHistoryResponse> => invoke('wallet_history', { q }),
    topupIntent: (amount: Money, provider: TopupProvider): Promise<TopupIntent> =>
      invoke('wallet_topup_intent', { amount, provider }),
  },
  shop: {
    products: (q?: ShopProductsRequest): Promise<Product[]> => invoke('shop_products', { q }),
    order: (req: ShopOrderRequest): Promise<Order> => invoke('shop_order', { req }),
    orderStatus: (orderId: string): Promise<Order> => invoke('shop_order_status', { orderId }),
    orders: (q?: ShopOrdersRequest): Promise<ShopOrdersResponse> => invoke('shop_orders', { q }),
  },
  chat: {
    history: (q?: ChatHistoryRequest): Promise<ChatHistoryResponse> => invoke('chat_history', { q }),
    send: (text: string, roomId?: string): Promise<ChatMessage> =>
      invoke('chat_send', { text, roomId, idempotencyKey: uuid() }),
    markRead: (upToMessageId: string, roomId?: string): Promise<ChatMarkReadResponse> =>
      invoke('chat_mark_read', { upToMessageId, roomId }),
  },
  booking: {
    seats: (date: string): Promise<BookingSeatsResponse> => invoke('booking_seats', { date }),
    reserve: (pcId: string, from: string, to: string): Promise<Booking> =>
      invoke('booking_reserve', { pcId, from, to }),
    cancel: (bookingId: string): Promise<Booking> => invoke('booking_cancel', { bookingId }),
  },
  tournaments: {
    list: (q?: TournamentsListRequest): Promise<Tournament[]> => invoke('tournaments_list', { q }),
    join: (tournamentId: string): Promise<Tournament> => invoke('tournaments_join', { tournamentId }),
    leaderboard: (tournamentId: string, limit?: number): Promise<TournamentsLeaderboardResponse> =>
      invoke('tournaments_leaderboard', { tournamentId, limit }),
  },
  profile: {
    get: (): Promise<User> => invoke('profile_get'),
    update: (patch: ProfileUpdateRequest): Promise<User> => invoke('profile_update', { patch }),
    stats: (): Promise<UserStats> => invoke('profile_stats'),
    achievements: (): Promise<Achievement[]> => invoke('profile_achievements'),
    loyalty: (): Promise<Loyalty> => invoke('profile_loyalty'),
  },
  settings: {
    get: (): Promise<ShellSettings> => invoke('settings_get'),
    set: (patch: SettingsSetRequest): Promise<ShellSettings> => invoke('settings_set', { patch }),
    getTheme: (name?: string): Promise<Theme> => invoke('settings_get_theme', { name }),
    listThemes: (): Promise<string[]> => invoke('settings_list_themes'),
    getShellConfig: (): Promise<ShellConfig> => invoke('settings_get_shell_config'),
  },
  policy: {
    get: (): Promise<Policy> => invoke('policy_get'),
    reload: (force?: boolean): Promise<PolicyReloadResponse> => invoke('policy_reload', { force }),
  },
  system: {
    pcInfo: (): Promise<PcInfo> => invoke('sys_pc_info'),
    hardware: (refresh?: boolean): Promise<HardwareInfo> => invoke('sys_hardware', { refresh }),
    metrics: (): Promise<PcMetrics> => invoke('sys_metrics'),
    callAdmin: (category: CallAdminCategory, message?: string): Promise<SysCallAdminResponse> =>
      invoke('sys_call_admin', { category, message }),
    reboot: (delaySec?: number, reason?: string): Promise<ScheduledResult> =>
      invoke('sys_reboot', { delaySec, reason }),
    shutdown: (delaySec?: number, reason?: string): Promise<ScheduledResult> =>
      invoke('sys_shutdown', { delaySec, reason }),
    lockScreen: (reason?: string): Promise<OkResponse> => invoke('sys_lock_screen', { reason }),
    setVolume: (level: number, muted?: boolean): Promise<VolumeState> => invoke('sys_set_volume', { level, muted }),
    setLocale: (locale: Locale): Promise<SysSetLocaleResponse> => invoke('sys_set_locale', { locale }),
    unlockAdmin: (pin: string): Promise<SysUnlockAdminResponse> => invoke('sys_unlock_admin', { pin }),
    ackAdminMessage: (id: string): Promise<OkResponse> => invoke('sys_ack_admin_message', { id }),
    logClientError: (e: SysLogClientErrorRequest): Promise<void> =>
      invoke<null>('sys_log_client_error', { e }).then(() => undefined),
    updateCheck: (): Promise<UpdateCheckResponse> => invoke('update_check'),
    updateApply: (component: UpdateComponent): Promise<UpdateApplyResponse> => invoke('update_apply', { component }),
  },
  kiosk: {
    state: (): Promise<KioskState> => invoke('kiosk_state'),
    setGuard: (active: boolean): Promise<void> => invoke<null>('kiosk_set_guard', { active }).then(() => undefined),
    setFullscreen: (on: boolean): Promise<void> => invoke<null>('kiosk_set_fullscreen', { on }).then(() => undefined),
    showOverlay: (kind: OverlayKind, payload?: Record<string, unknown>): Promise<void> =>
      invoke<null>('kiosk_show_overlay', { kind, payload }).then(() => undefined),
    monitors: (): Promise<KioskMonitor[]> => invoke('kiosk_monitors'),
    moveToMonitor: (index: number): Promise<void> =>
      invoke<null>('kiosk_move_to_monitor', { index }).then(() => undefined),
    virtualKeyboard: (show: boolean): Promise<void> =>
      invoke<null>('kiosk_virtual_keyboard', { show }).then(() => undefined),
    focus: (): Promise<void> => invoke<null>('kiosk_focus').then(() => undefined),
    exit: (adminToken: string, action: 'explorer' | 'quit'): Promise<void> =>
      invoke<null>('kiosk_exit', { adminToken, action }).then(() => undefined),
    reload: (): Promise<void> => invoke<null>('kiosk_reload').then(() => undefined),
    openDevtools: (): Promise<void> => invoke<null>('kiosk_open_devtools').then(() => undefined),
    gamepadState: (): Promise<GamepadState[]> => invoke('kiosk_gamepad_state'),
    idleReset: (): Promise<void> => invoke<null>('kiosk_idle_reset').then(() => undefined),
    i18nBundle: (locale: Locale): Promise<Record<string, string>> => invoke('kiosk_i18n_bundle', { locale }),
    assetUrl: (path: string): Promise<string> => invoke('kiosk_asset_url', { path }),
  },
};

/** Type of the {@link api} object. */
export type ShellApi = typeof api;

// ---------------------------------------------------------------------------------------------------------------------
// Typed events
// ---------------------------------------------------------------------------------------------------------------------

/** Payload per `kiosk://<name>` event as delivered to the UI (normalized from the native payloads, §3.2). */
export interface KioskEventMap {
  idle: { idle: boolean; seconds: number; stage?: 'active' | 'dim' | 'idle' | 'screensaver' };
  gamepad: {
    kind: 'button' | 'axis';
    name: string;
    value: number;
    gamepadId: number;
    pressed?: boolean;
    connected?: boolean;
  };
  hotkey: { name: string; combo?: string };
  monitorChanged: MonitorInfo[];
  connectivity: { connected: boolean; state: string; attempts?: number; since?: string };
  themeChanged: { name: string; theme?: Theme };
  localeChanged: { locale: Locale };
  focus: { focused: boolean; foregroundProcess?: string };
  overlay: { kind: string; payload?: unknown };
}

/** Kiosk event name. */
export type KioskEventName = keyof KioskEventMap;

/** Raw shapes emitted by the Rust side (`kiosk/*.rs`, `gamepad.rs`, `agent/events.rs`). */
interface RawKioskPayloads {
  idle: { idle: boolean; idleSec: number; stage: 'active' | 'dim' | 'idle' | 'screensaver' };
  gamepad: {
    index: number;
    button?: GamepadButtonName;
    axis?: GamepadAxisName;
    value: number;
    pressed?: boolean;
    connected?: boolean;
  };
  hotkey: { name: string; combo: string };
  monitorChanged: { monitors: KioskMonitor[]; primaryIndex: number; reason: string };
  connectivity: { agent: 'connected' | 'disconnected' | 'connecting'; attempts: number; since: string };
  themeChanged: Theme;
  localeChanged: { locale: Locale };
  focus: { hasFocus: boolean; foregroundProcess?: string };
  overlay: { kind: string; payload?: unknown };
}

type KioskNormalizers = { [K in KioskEventName]: (raw: RawKioskPayloads[K]) => KioskEventMap[K] };

const normalizeKiosk: KioskNormalizers = {
  idle: (raw) => ({ idle: raw.idle, seconds: raw.idleSec, stage: raw.stage }),
  gamepad: (raw) => {
    if (raw.button !== undefined) {
      const pressed = raw.pressed ?? raw.value > 0.5;
      return { kind: 'button', name: raw.button, value: pressed ? 1 : 0, gamepadId: raw.index, pressed };
    }
    if (raw.axis !== undefined) {
      return { kind: 'axis', name: raw.axis, value: raw.value, gamepadId: raw.index };
    }
    return {
      kind: 'button',
      name: 'connection',
      value: raw.connected ? 1 : 0,
      gamepadId: raw.index,
      connected: raw.connected ?? false,
    };
  },
  hotkey: (raw) => ({ name: raw.name, combo: raw.combo }),
  monitorChanged: (raw) => raw.monitors,
  connectivity: (raw) => ({
    connected: raw.agent === 'connected',
    state: raw.agent,
    attempts: raw.attempts,
    since: raw.since,
  }),
  themeChanged: (raw) => ({ name: raw.name, theme: raw }),
  localeChanged: (raw) => ({ locale: raw.locale }),
  focus: (raw) => ({ focused: raw.hasFocus, foregroundProcess: raw.foregroundProcess }),
  overlay: (raw) => ({ kind: raw.kind, payload: raw.payload }),
};

/** Event name prefix helpers. */
export const agentEventName = <K extends keyof AgentEventMap>(name: K): `agent://${K}` => `agent://${name}`;
export const kioskEventName = <K extends KioskEventName>(name: K): `kiosk://${K}` => `kiosk://${name}`;

export const events = {
  /** Subscribes to `agent://<name>` with the payload type from the contracts. */
  on<K extends keyof AgentEventMap>(name: K, h: (p: AgentEventMap[K]) => void): () => void {
    return listen<AgentEventMap[K]>(agentEventName(name), h);
  },
  /** Subscribes to `kiosk://<name>`; native payloads are normalized to {@link KioskEventMap}. */
  onKiosk<K extends KioskEventName>(name: K, h: (p: KioskEventMap[K]) => void): () => void {
    const normalize = normalizeKiosk[name] as (raw: RawKioskPayloads[K]) => KioskEventMap[K];
    return listen<RawKioskPayloads[K]>(kioskEventName(name), (raw) => h(normalize(raw)));
  },
};
