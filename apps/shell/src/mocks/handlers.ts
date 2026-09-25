/**
 * In-memory mock of every Tauri command (TAURI_COMMANDS.md §2) plus the event bus that stands in for
 * `@tauri-apps/api/event` outside the webview. Stateful: login → session, a 1 s session ticker with
 * warnings, shop orders that progress, chat echoes, top-ups that settle. Errors are plain
 * `{ code, message, details, source: 'mock' }` objects; `lib/tauri.ts` normalizes them.
 *
 * Handy accounts: `demo` / `1234`, `vip` / `1234`, guest. User PIN `1234`, admin PIN `0000`.
 * Add `?mock=auth` to the URL to boot already logged in with an active session, `?mock=qr` to have the QR
 * handshake confirm itself a few seconds after it is shown.
 */
import type {
  Achievement,
  AdminMessage,
  App,
  AppsLaunchResponse,
  AuthLoginRequest,
  AuthLoginResponse,
  AuthLogoutResponse,
  AuthStatusResponse,
  Balance,
  Booking,
  BookingSeatsResponse,
  ChatHistoryResponse,
  ChatMarkReadResponse,
  ChatMessage,
  ErrorCode,
  Game,
  GameInstallStatus,
  GameStateChanged,
  GamesKillResponse,
  GamesLaunchRequest,
  GamesListRequest,
  GamesListResponse,
  HardwareInfo,
  LaunchResult,
  Locale,
  Loyalty,
  Money,
  Notification,
  OkResponse,
  Order,
  OrderStatus,
  PcInfo,
  PcMetrics,
  Policy,
  PolicyReloadResponse,
  Product,
  ProfileUpdateRequest,
  QrLoginStart,
  RemoteControlEvent,
  RunningGame,
  ScheduledResult,
  Session,
  SessionEndReason,
  SessionEndResult,
  SessionStartRequest,
  SessionTimeLeftResponse,
  SettingsSetRequest,
  PlayerSettingsItem,
  ShellClub,
  ShellSettings,
  ShopOrderRequest,
  ShopOrdersResponse,
  SysCallAdminResponse,
  SysSetLocaleResponse,
  SysUnlockAdminResponse,
  Tariff,
  Theme,
  TopupIntent,
  TopupProvider,
  Tournament,
  TournamentsLeaderboardResponse,
  Transaction,
  UpdateApplyResponse,
  UpdateCheckResponse,
  UpdateProgress,
  UpdateReady,
  User,
  UserStats,
  VolumeState,
  WalletHistoryResponse,
  WalletTariffsResponse,
} from '@clubshell/contracts';
import { tariffPriceFor } from '@clubshell/contracts';
import type { GamepadState, KioskState, ShellConfig } from '@/lib/tauri';
import { builtinThemes, DEFAULT_THEME } from '@/theme/themes';
import {
  ACHIEVEMENTS,
  ADMIN_ID,
  ADMIN_NAME,
  ADMIN_REPLIES,
  APPS,
  BOOKINGS,
  CHAT_MESSAGES,
  GAMES,
  GUEST_TEMPLATE,
  HARDWARE,
  KIOSK_STATE,
  LEADERBOARD,
  LOYALTY,
  METRICS,
  DEMO_CLUB,
  MOCK_ADMIN_PIN,
  MOCK_CREDENTIALS,
  MOCK_USER_PIN,
  MONITORS,
  NOTIFICATIONS,
  ORDERS,
  PC,
  PC_ID,
  PC_INFO,
  PLAYER_GAME_SETTINGS,
  POLICY,
  PRODUCTS,
  QR_START,
  ROOM_ID,
  SEATS,
  SESSION,
  SETTINGS,
  SHELL_CONFIG,
  SLOT_MINUTES,
  STATS,
  TARIFFS,
  TOURNAMENTS,
  TRANSACTIONS,
  UPDATE_MANIFEST,
  USER,
  USER_ID,
  VIP_USER,
  uzs,
} from './data';

// ---------------------------------------------------------------------------------------------------------------------
// Bus / registry / errors
// ---------------------------------------------------------------------------------------------------------------------

/** A mock command implementation. */
export type MockHandler = (args: Record<string, unknown>) => unknown | Promise<unknown>;

/** Command name → handler. Filled at module load; `lib/tauri.ts` dispatches here outside Tauri. */
export const registry: Record<string, MockHandler> = {};

/** Registers (or overrides) a mock command. */
export function registerMock(cmd: string, handler: MockHandler): void {
  registry[cmd] = handler;
}

type Listener = (payload: unknown) => void;
const bus = new Map<string, Set<Listener>>();

/** Fires an event (`agent://…` / `kiosk://…`) to every mock subscriber, asynchronously like Tauri does. */
export function emitMock<T>(event: string, payload: T): void {
  const set = bus.get(event);
  if (!set || set.size === 0) {
    return;
  }
  const listeners = Array.from(set);
  queueMicrotask(() => {
    for (const l of listeners) {
      try {
        l(payload);
      } catch (e) {
        console.error(`[mock] listener for ${event} threw`, e);
      }
    }
  });
}

/** Subscribes to a mock event; returns the unsubscribe. */
export function subscribeMock<T>(event: string, handler: (payload: T) => void): () => void {
  let set = bus.get(event);
  if (!set) {
    set = new Set();
    bus.set(event, set);
  }
  const l: Listener = (p) => handler(p as T);
  set.add(l);
  return () => {
    set?.delete(l);
  };
}

/** Throws a `ShellError`-shaped object with `source: 'mock'`. */
export function mockError(code: ErrorCode, message?: string, details?: Record<string, unknown>): never {
  throw { code, message: message ?? code, details: details ?? null, source: 'mock' as const };
}

// ---------------------------------------------------------------------------------------------------------------------
// Small utilities
// ---------------------------------------------------------------------------------------------------------------------

const delay = (ms: number): Promise<void> => new Promise((r) => setTimeout(r, ms));
const latency = (): Promise<void> => delay(80 + Math.random() * 170);
const nowIso = (): string => new Date().toISOString();
const isoIn = (sec: number): string => new Date(Date.now() + sec * 1000).toISOString();

function newId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  const b = new Uint8Array(16);
  crypto.getRandomValues(b);
  const h = Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('');
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-4${h.slice(13, 16)}-8${h.slice(17, 20)}-${h.slice(20)}`;
}

const str = (args: Record<string, unknown>, key: string): string | undefined => {
  const v = args[key];
  return typeof v === 'string' ? v : undefined;
};
const num = (args: Record<string, unknown>, key: string): number | undefined => {
  const v = args[key];
  return typeof v === 'number' && Number.isFinite(v) ? v : undefined;
};
const bool = (args: Record<string, unknown>, key: string): boolean | undefined => {
  const v = args[key];
  return typeof v === 'boolean' ? v : undefined;
};
const obj = <T>(args: Record<string, unknown>, key: string): T | undefined => {
  const v = args[key];
  return typeof v === 'object' && v !== null ? (v as T) : undefined;
};

const clone = <T>(v: T): T => JSON.parse(JSON.stringify(v)) as T;
const money = (amount: number): Money => ({ amount: Math.round(amount), currency: 'UZS' });

// ---------------------------------------------------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------------------------------------------------

interface QrState {
  token: string;
  expiresAt: number;
  confirmedAt: number | null;
}

interface MockState {
  user: User | null;
  expiresAt: string | null;
  session: Session | null;
  balance: Balance;
  transactions: Transaction[];
  orders: Order[];
  messages: ChatMessage[];
  bookings: Booking[];
  tournaments: Tournament[];
  games: Game[];
  running: RunningGame[];
  settings: ShellSettings;
  gameSettings: PlayerSettingsItem[];
  policy: Policy;
  volume: VolumeState;
  qr: QrState | null;
  adminToken: { token: string; expiresAt: number } | null;
  lastCallAdminAt: number;
  adminAttempts: number[];
  idempotency: Map<string, unknown>;
  kiosk: KioskState;
  updateReady: boolean;
}

function balanceFor(user: User | null): Balance {
  return {
    userId: user?.id ?? USER_ID,
    amount: user ? { ...user.balance } : uzs(0),
    bonus: user?.role === 'guest' ? uzs(0) : uzs(5_000),
    currency: 'UZS',
    updatedAt: nowIso(),
  };
}

function freshState(): MockState {
  return {
    user: null,
    expiresAt: null,
    session: null,
    balance: balanceFor(null),
    transactions: clone(TRANSACTIONS),
    orders: clone(ORDERS),
    messages: clone(CHAT_MESSAGES),
    bookings: clone(BOOKINGS),
    tournaments: clone(TOURNAMENTS),
    games: clone(GAMES),
    running: [],
    settings: { ...clone(SETTINGS), club: demoClubRequested() ? clone(DEMO_CLUB) : null },
    gameSettings: clone(PLAYER_GAME_SETTINGS),
    policy: clone(POLICY),
    volume: { level: SETTINGS.volume, muted: SETTINGS.muted },
    qr: null,
    adminToken: null,
    lastCallAdminAt: 0,
    adminAttempts: [],
    idempotency: new Map(),
    kiosk: clone(KIOSK_STATE),
    updateReady: false,
  };
}

/** Mutable mock state (exposed for tests and `window.__clubshellMock`). */
export const mockState: MockState = freshState();

const AUTH_STORAGE_KEY = 'clubshell.mock.auth';
let started = false;
const timers = new Set<ReturnType<typeof setTimeout>>();

function later(ms: number, fn: () => void): void {
  const t = setTimeout(() => {
    timers.delete(t);
    fn();
  }, ms);
  timers.add(t);
}

// ---------------------------------------------------------------------------------------------------------------------
// Auth helpers
// ---------------------------------------------------------------------------------------------------------------------

function requireUser(): User {
  if (!mockState.user) {
    mockError('unauthorized', 'Not logged in', { reason: 'notLoggedIn' });
  }
  return mockState.user;
}

function requireSession(): Session {
  const s = mockState.session;
  if (!s || (s.state !== 'active' && s.state !== 'paused')) {
    mockError('sessionNotActive', 'No active session');
  }
  return s;
}

function persistAuth(username: string | null): void {
  try {
    if (username) {
      sessionStorage.setItem(AUTH_STORAGE_KEY, username);
    } else {
      sessionStorage.removeItem(AUTH_STORAGE_KEY);
    }
  } catch {
    // storage unavailable (private mode / thumbnail capture)
  }
}

function setUser(user: User): void {
  mockState.user = clone(user);
  mockState.expiresAt = isoIn(3600);
  mockState.balance = balanceFor(mockState.user);
  persistAuth(user.username);
}

function makeGuest(displayName?: string | null): User {
  const n = Math.floor(100 + Math.random() * 900);
  return {
    ...GUEST_TEMPLATE,
    id: newId(),
    username: `guest${n}`,
    displayName: displayName && displayName.trim().length > 0 ? displayName.trim() : `Guest ${n}`,
    createdAt: nowIso(),
  };
}

function activeSessionFor(user: User): Session {
  const s = clone(SESSION);
  s.id = newId();
  s.userId = user.id;
  s.startedAt = new Date(Date.now() - 33 * 60_000).toISOString();
  s.endsAt = isoIn(87 * 60);
  return s;
}

// ---------------------------------------------------------------------------------------------------------------------
// Wallet helpers
// ---------------------------------------------------------------------------------------------------------------------

function tariffById(id: string): Tariff {
  const t = TARIFFS.find((x) => x.id === id);
  if (!t) {
    mockError('notFound', 'Tariff not found', { name: id });
  }
  return t;
}

function pushTransaction(type: Transaction['type'], amount: Money, description: string, ref: string | null): void {
  const user = requireUser();
  user.balance = money(user.balance.amount + amount.amount);
  mockState.balance = { ...balanceFor(user), bonus: mockState.balance.bonus };
  mockState.transactions.unshift({
    id: newId(),
    userId: user.id,
    type,
    amount,
    balanceAfter: { ...user.balance },
    description,
    createdAt: nowIso(),
    ref,
  });
  emitMock('agent://wallet.updated', clone(mockState.balance));
}

function assertFunds(required: Money): void {
  const user = requireUser();
  if (user.balance.amount < required.amount) {
    mockError('insufficientFunds', 'Insufficient funds', { required, available: { ...user.balance } });
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Session engine
// ---------------------------------------------------------------------------------------------------------------------

const WARNING_MARKS = [15, 5, 1] as const;

function emitSession(): void {
  if (mockState.session) {
    emitMock('agent://session.updated', clone(mockState.session));
  }
}

function endSession(reason: SessionEndReason): SessionEndResult {
  const s = mockState.session;
  if (!s) {
    mockError('sessionNotActive', 'No active session');
  }
  const tariff = TARIFFS.find((t) => t.id === s.tariffId) ?? TARIFFS[0];
  let charged = money(0);
  let refunded = money(0);
  if (s.isPrepaid) {
    if (s.secondsLeft > 60 && tariff && !tariff.isPackage) {
      refunded = tariffPriceFor(tariff, Math.floor(s.secondsLeft / 60));
      pushTransaction('refund', refunded, `Refund: unused ${Math.floor(s.secondsLeft / 60)} min`, s.id);
    }
  } else if (tariff) {
    charged = tariffPriceFor(tariff, Math.ceil(s.secondsUsed / 60));
    if (charged.amount > 0) {
      pushTransaction(
        'charge',
        money(-charged.amount),
        `Session ${PC.name}, ${Math.ceil(s.secondsUsed / 60)} min ${tariff.name}`,
        s.id,
      );
    }
  }
  const ended: Session = {
    ...s,
    state: 'ended',
    secondsLeft: 0,
    endsAt: nowIso(),
    pausedAt: null,
    cost: s.isPrepaid ? s.cost : charged,
  };
  mockState.session = null;
  mockState.running = [];
  mockState.kiosk.gameMode = false;
  emitMock('agent://session.ended', { session: clone(ended), reason, charged });
  return { session: ended, charged, refunded };
}

function tick(): void {
  const s = mockState.session;
  if (!s) {
    return;
  }
  if (s.state === 'active' || s.state === 'locked' || s.state === 'ending') {
    s.secondsUsed += 1;
    if (s.secondsLeft > 0) {
      s.secondsLeft -= 1;
      s.endsAt = isoIn(s.secondsLeft);
      for (const m of WARNING_MARKS) {
        if (s.secondsLeft === m * 60 && !s.warningsSent.includes(m)) {
          s.warningsSent.push(m);
          emitMock('agent://session.warning', {
            sessionId: s.id,
            minutesLeft: m,
            secondsLeft: s.secondsLeft,
            endsAt: s.endsAt,
          });
        }
      }
      if (s.secondsLeft === 0) {
        emitMock('agent://session.warning', { sessionId: s.id, minutesLeft: 0, secondsLeft: 0, endsAt: s.endsAt });
        endSession('timeUp');
        return;
      }
    } else if (s.secondsLeft === -1) {
      const tariff = TARIFFS.find((t) => t.id === s.tariffId);
      if (tariff) {
        s.cost = tariffPriceFor(tariff, Math.ceil(s.secondsUsed / 60));
      }
    }
    emitSession();
  }
}

let metricsSeq = 0;
function metricsSample(): PcMetrics {
  metricsSeq += 1;
  const gameRunning = mockState.running.length > 0;
  const wobble = (base: number, amp: number): number =>
    Math.max(0, Math.round(base + Math.sin(metricsSeq / 3) * amp + (Math.random() - 0.5) * amp));
  return {
    cpuPct: wobble(gameRunning ? 62 : METRICS.cpuPct, 8),
    gpuPct: wobble(gameRunning ? 88 : METRICS.gpuPct, 6),
    ramUsedMb: wobble(gameRunning ? 16_400 : METRICS.ramUsedMb, 300),
    temps: {
      cpu: wobble(gameRunning ? 71 : METRICS.temps.cpu, 2),
      gpu: wobble(gameRunning ? 68 : METRICS.temps.gpu, 2),
    },
    fps: gameRunning ? wobble(214, 20) : null,
    netMbps: { up: Math.round(wobble(24, 10)) / 10, down: Math.round(wobble(187, 60)) / 10 },
    uptimeSec: METRICS.uptimeSec + metricsSeq * 5,
    at: nowIso(),
  };
}

function bootFromUrl(): void {
  if (typeof window === 'undefined') {
    return;
  }
  let wantAuth = false;
  try {
    wantAuth = new URLSearchParams(window.location.search).get('mock') === 'auth';
  } catch {
    wantAuth = false;
  }
  let stored: string | null = null;
  try {
    stored = sessionStorage.getItem(AUTH_STORAGE_KEY);
  } catch {
    stored = null;
  }
  const username = wantAuth ? 'demo' : stored;
  if (!username) {
    return;
  }
  const user = username === 'vip' ? VIP_USER : username.startsWith('guest') ? makeGuest(username) : USER;
  setUser(user);
  if (wantAuth || stored === 'demo' || stored === 'vip') {
    mockState.session = activeSessionFor(user);
  }
}

function ensureStarted(): void {
  if (started) {
    return;
  }
  started = true;
  bootFromUrl();
  setInterval(tick, 1000);
  setInterval(() => emitMock('agent://sys.metrics', metricsSample()), 5000);
  later(1500, () => emitMock('kiosk://connectivity', { agent: 'connected', attempts: 0, since: nowIso() }));
  later(2500, () =>
    emitMock('agent://sys.connectivity', { state: 'online', since: nowIso(), queuedEvents: 0, serverLatencyMs: 24 }),
  );
  if (typeof window !== 'undefined') {
    (window as unknown as { __clubshellMock: unknown }).__clubshellMock = {
      state: mockState,
      emit: emitMock,
      simulate,
      registry,
      reset: resetMock,
    };
  }
}

/** Resets every piece of mock state (tests). Timers keep running. */
export function resetMock(): void {
  for (const t of timers) {
    clearTimeout(t);
  }
  timers.clear();
  Object.assign(mockState, freshState());
  persistAuth(null);
}

// ---------------------------------------------------------------------------------------------------------------------
// Dev simulation helpers (also on window.__clubshellMock.simulate)
// ---------------------------------------------------------------------------------------------------------------------

/** `?club=demo` in the URL starts with the owner's branding from {@link DEMO_CLUB}. */
function demoClubRequested(): boolean {
  return typeof window !== 'undefined' && new URLSearchParams(window.location.search).get('club') === 'demo';
}

export const simulate = {
  /** Replaces the club block as if the Agent had written a new one from the server (`null` clears it). */
  club(club: ShellClub | null = DEMO_CLUB): void {
    mockState.settings = { ...mockState.settings, club: club ? clone(club) : null };
  },
  adminMessage(text = 'Please finish your match, the club closes in 20 minutes.', requiresAck = true): AdminMessage {
    const m: AdminMessage = { id: newId(), from: ADMIN_NAME, text, level: 'warning', requiresAck, at: nowIso() };
    emitMock('agent://admin.message', m);
    return m;
  },
  notification(n?: Partial<Notification>): Notification {
    const base = NOTIFICATIONS[0] ?? { id: newId(), title: 'Info', body: '', level: 'info' as const };
    const out: Notification = { ...base, ...n, id: newId() };
    emitMock('agent://notification.push', out);
    return out;
  },
  remoteControl(state: RemoteControlEvent['state'] = 'started'): void {
    emitMock('agent://admin.remoteControl', { state, adminName: ADMIN_NAME, at: nowIso(), showIndicator: true });
  },
  updateReady(): void {
    mockState.updateReady = true;
    const r: UpdateReady = {
      component: 'shell',
      version: UPDATE_MANIFEST.version,
      restartRequired: true,
      mandatory: false,
      applyAt: null,
    };
    emitMock('agent://update.ready', r);
  },
  connectivity(state: 'online' | 'offline'): void {
    emitMock('agent://sys.connectivity', {
      state,
      since: nowIso(),
      queuedEvents: state === 'offline' ? 3 : 0,
      serverLatencyMs: state === 'online' ? 24 : null,
    });
  },
  agentLink(state: 'connected' | 'disconnected' | 'connecting', attempts = 0): void {
    emitMock('kiosk://connectivity', { agent: state, attempts, since: nowIso() });
  },
  idle(idle: boolean, idleSec = idle ? 300 : 0): void {
    mockState.kiosk.idle = idle;
    mockState.kiosk.idleSec = idleSec;
    emitMock('kiosk://idle', { idle, idleSec, stage: idle ? 'idle' : 'active' });
  },
  gamepadButton(button: string, pressed = true): void {
    emitMock('kiosk://gamepad', { index: 0, button, value: pressed ? 1 : 0, pressed });
  },
  hotkey(name: string): void {
    emitMock('kiosk://hotkey', { name, combo: name === 'exit' ? 'Ctrl+Alt+Shift+F12' : name });
  },
  shellCommand(command: 'lock' | 'unlock' | 'showAds' | 'showMessage' | 'reboot'): void {
    const args =
      command === 'showAds'
        ? { items: SHELL_CONFIG.ads.playlist, skippable: true }
        : command === 'showMessage'
          ? { title: 'Staff', body: 'Free energy drink for tournament players tonight!', level: 'info', ttlSec: 10 }
          : command === 'reboot'
            ? { delaySec: 60, message: 'Scheduled maintenance' }
            : command === 'lock'
              ? { reason: 'admin', message: 'Locked by staff' }
              : null;
    if (command === 'lock' && mockState.session && mockState.session.state === 'active') {
      mockState.session.state = 'locked';
      mockState.session.pausedAt = nowIso();
      emitSession();
    }
    if (command === 'unlock' && mockState.session && mockState.session.state === 'locked') {
      mockState.session.state = 'active';
      mockState.session.pausedAt = null;
      emitSession();
    }
    emitMock('agent://shell.command', { command, args, commandId: newId() });
  },
  authExpired(reason: 'tokenExpired' | 'revoked' | 'admin' = 'admin'): void {
    mockState.user = null;
    mockState.session = null;
    persistAuth(null);
    emitMock('agent://auth.expired', { reason });
  },
  sessionSeconds(seconds: number): void {
    if (mockState.session) {
      mockState.session.secondsLeft = seconds;
      mockState.session.endsAt = isoIn(seconds);
      emitSession();
    }
  },
  policyChanged(): void {
    mockState.policy.version += 1;
    mockState.policy.updatedAt = nowIso();
    emitMock('agent://policy.changed', {
      version: mockState.policy.version,
      updatedAt: mockState.policy.updatedAt,
      changed: ['kiosk'],
      policy: clone(mockState.policy),
    });
  },
};

// ---------------------------------------------------------------------------------------------------------------------
// Command handlers — wrapper adds latency + lazy start
// ---------------------------------------------------------------------------------------------------------------------

function cmd(name: string, fn: MockHandler, opts: { fast?: boolean } = {}): void {
  registry[name] = async (args) => {
    ensureStarted();
    if (!opts.fast) {
      await latency();
    }
    return fn(args ?? {});
  };
}

// ----- auth -----------------------------------------------------------------------------------------------------------

cmd('auth_login', (args): AuthLoginResponse => {
  const req = obj<AuthLoginRequest>(args, 'req');
  if (!req) {
    mockError('validation', 'req is required', { field: 'req', reason: 'required' });
  }
  let user: User;
  switch (req.kind) {
    case 'password': {
      const username = (req.username ?? '').trim().toLowerCase();
      const password = req.password ?? '';
      if (username.length === 0) {
        mockError('validation', 'username is required', { field: 'username', reason: 'required' });
      }
      if (password.length === 0) {
        mockError('validation', 'password is required', { field: 'password', reason: 'required' });
      }
      if (username === 'banned') {
        mockError('forbidden', 'Account is banned', { reason: 'banned' });
      }
      if (MOCK_CREDENTIALS[username] !== password) {
        mockError('unauthorized', 'Invalid credentials', { reason: 'invalidCredentials' });
      }
      user = username === 'vip' ? VIP_USER : USER;
      break;
    }
    case 'guest':
      user = makeGuest(req.username ?? null);
      break;
    case 'qr': {
      const qr = mockState.qr;
      if (!qr || qr.token !== req.qrToken) {
        mockError('unauthorized', 'Unknown QR token', { reason: 'invalidToken' });
      }
      if (Date.now() > qr.expiresAt) {
        mockError('unauthorized', 'QR token expired', { reason: 'expired' });
      }
      if (qr.confirmedAt === null) {
        mockError('unauthorized', 'QR login not confirmed yet', { reason: 'pending' });
      }
      mockState.qr = null;
      user = USER;
      break;
    }
    case 'card':
      if (!req.cardId) {
        mockError('validation', 'cardId is required', { field: 'cardId', reason: 'required' });
      }
      user = req.cardId.endsWith('9') ? VIP_USER : USER;
      break;
    case 'token':
      if (!req.token) {
        mockError('validation', 'token is required', { field: 'token', reason: 'required' });
      }
      user = USER;
      break;
    default:
      mockError('validation', 'Unknown auth kind', { field: 'kind', reason: 'format' });
  }
  setUser(user);
  const session = mockState.session && mockState.session.userId === user.id ? clone(mockState.session) : null;
  if (!session) {
    mockState.session = null;
  }
  return {
    user: clone(mockState.user as User),
    session,
    expiresAt: mockState.expiresAt ?? isoIn(3600),
    mode: 'online',
  };
});

cmd('auth_logout', (args): AuthLogoutResponse => {
  const reason = (str(args, 'reason') as SessionEndReason | undefined) ?? 'user';
  let ended: Session | null = null;
  if (mockState.session) {
    ended = endSession(reason).session;
  }
  mockState.user = null;
  mockState.expiresAt = null;
  mockState.balance = balanceFor(null);
  persistAuth(null);
  return { ok: true, sessionEnded: ended !== null, session: ended };
});

cmd(
  'auth_status',
  (): AuthStatusResponse => ({
    authenticated: mockState.user !== null,
    mode: 'online',
    user: mockState.user ? clone(mockState.user) : null,
    session: mockState.session ? clone(mockState.session) : null,
    expiresAt: mockState.expiresAt,
  }),
);

/**
 * Seconds until the mock "phone" confirms a QR handshake. Off by default so the lock screen does not sign itself
 * in while someone looks at it (QR is the first method now); `?mock=qr` replays the happy path in a demo.
 */
const QR_AUTOCONFIRM_SEC = 7;

function qrAutoConfirms(): boolean {
  try {
    return new URLSearchParams(window.location.search).get('mock') === 'qr';
  } catch {
    return false;
  }
}

cmd('auth_qr_start', (): QrLoginStart => {
  const token = `qr-${newId().slice(0, 8)}`;
  const expiresAt = Date.now() + 120_000;
  mockState.qr = { token, expiresAt, confirmedAt: null };
  if (qrAutoConfirms()) {
    later(QR_AUTOCONFIRM_SEC * 1000, () => {
      if (mockState.qr?.token === token) {
        mockState.qr.confirmedAt = Date.now();
      }
    });
  }
  return {
    ...QR_START,
    qrToken: token,
    qrUrl: `https://club.example.uz/q/${token}`,
    expiresAt: new Date(expiresAt).toISOString(),
  };
});

// ----- session --------------------------------------------------------------------------------------------------------

cmd('session_get', (): Session | null => (mockState.session ? clone(mockState.session) : null));

cmd('session_start', (args): Session => {
  const user = requireUser();
  const req = obj<SessionStartRequest>(args, 'req');
  if (!req) {
    mockError('validation', 'req is required', { field: 'req', reason: 'required' });
  }
  if (mockState.session && mockState.session.state !== 'ended') {
    mockError('sessionAlreadyActive', 'A session is already active');
  }
  const tariff = tariffById(req.tariffId);
  let minutes = req.minutes ?? null;
  if (tariff.isPackage) {
    minutes = tariff.packageMinutes ?? 0;
  }
  if (req.prepaid) {
    if (minutes === null || minutes <= 0) {
      mockError('validation', 'minutes is required for prepaid sessions', { field: 'minutes', reason: 'required' });
    }
    if (minutes < tariff.minMinutes) {
      mockError('validation', `Minimum ${tariff.minMinutes} minutes`, { field: 'minutes', reason: 'min' });
    }
    if (tariff.maxMinutes != null && minutes > tariff.maxMinutes) {
      mockError('validation', `Maximum ${tariff.maxMinutes} minutes`, { field: 'minutes', reason: 'max' });
    }
  }
  const price = req.prepaid && minutes ? tariffPriceFor(tariff, minutes) : money(0);
  if (req.prepaid) {
    assertFunds(price);
  } else if (user.role === 'guest' && user.balance.amount <= 0) {
    // guests may play postpaid; nothing to check
  }
  const id = newId();
  if (req.prepaid && price.amount > 0) {
    pushTransaction('charge', money(-price.amount), `Session ${PC.name}, ${minutes} min ${tariff.name}`, id);
  }
  const seconds = req.prepaid && minutes ? minutes * 60 : -1;
  mockState.session = {
    id,
    userId: user.id,
    pcId: PC_ID,
    state: 'active',
    startedAt: nowIso(),
    endsAt: seconds > 0 ? isoIn(seconds) : null,
    pausedAt: null,
    tariffId: tariff.id,
    secondsLeft: seconds,
    secondsUsed: 0,
    cost: price,
    isPrepaid: req.prepaid,
    warningsSent: [],
  };
  emitSession();
  return clone(mockState.session);
});

cmd('session_pause', (): Session => {
  const s = requireSession();
  if (s.state !== 'active') {
    mockError('conflict', 'Session is not active', { reason: s.state });
  }
  s.state = 'paused';
  s.pausedAt = nowIso();
  emitSession();
  return clone(s);
});

cmd('session_resume', (): Session => {
  const s = requireSession();
  if (s.state !== 'paused') {
    mockError('conflict', 'Session is not paused', { reason: s.state });
  }
  s.state = 'active';
  s.pausedAt = null;
  if (s.secondsLeft > 0) {
    s.endsAt = isoIn(s.secondsLeft);
  }
  emitSession();
  return clone(s);
});

cmd('session_end', (args): SessionEndResult => {
  const reason = (str(args, 'reason') as SessionEndReason | undefined) ?? 'user';
  if (!mockState.session) {
    mockError('sessionNotActive', 'No active session');
  }
  return endSession(reason);
});

cmd('session_extend', (args): Session => {
  const s = requireSession();
  const minutes = num(args, 'minutes');
  if (minutes === undefined || minutes <= 0 || !Number.isInteger(minutes)) {
    mockError('validation', 'minutes must be a positive integer', { field: 'minutes', reason: 'min' });
  }
  const tariff = tariffById(str(args, 'tariffId') ?? s.tariffId);
  const price = tariffPriceFor(tariff, minutes);
  if (s.isPrepaid) {
    assertFunds(price);
    pushTransaction('charge', money(-price.amount), `Extend ${minutes} min ${tariff.name}`, s.id);
    s.cost = money(s.cost.amount + price.amount);
    s.secondsLeft += minutes * 60;
    s.endsAt = isoIn(s.secondsLeft);
    s.warningsSent = s.warningsSent.filter((m) => m * 60 > s.secondsLeft);
  }
  emitSession();
  return clone(s);
});

cmd('session_lock', (): Session => {
  const s = requireSession();
  s.state = 'locked';
  s.pausedAt = nowIso();
  mockState.kiosk.locked = true;
  emitSession();
  return clone(s);
});

cmd('session_unlock', (args): Session => {
  const s = mockState.session;
  if (!s || s.state !== 'locked') {
    mockError('conflict', 'Session is not locked', { reason: s?.state ?? 'idle' });
  }
  const password = str(args, 'password');
  const pin = str(args, 'pin');
  const user = requireUser();
  const passwordOk = password !== undefined && MOCK_CREDENTIALS[user.username] === password;
  const pinOk = pin !== undefined && pin === MOCK_USER_PIN;
  if (!passwordOk && !pinOk) {
    if (password === undefined && pin === undefined) {
      mockError('validation', 'password or pin is required', { field: 'pin', reason: 'required' });
    }
    mockError('unauthorized', 'Wrong secret', { reason: pin !== undefined ? 'wrongPin' : 'wrongPassword' });
  }
  s.state = 'active';
  s.pausedAt = null;
  mockState.kiosk.locked = false;
  emitSession();
  return clone(s);
});

cmd(
  'session_time_left',
  (): SessionTimeLeftResponse => {
    const s = mockState.session;
    return {
      state: s?.state ?? 'idle',
      secondsLeft: s ? s.secondsLeft : 0,
      secondsUsed: s ? s.secondsUsed : 0,
      serverTime: nowIso(),
      sessionId: s?.id ?? null,
      endsAt: s?.endsAt ?? null,
    };
  },
  { fast: true },
);

// ----- games / apps ---------------------------------------------------------------------------------------------------

cmd('games_list', (args): GamesListResponse => {
  const q = obj<GamesListRequest>(args, 'q') ?? {};
  let items = mockState.games.slice();
  if (q.category) {
    items = items.filter((g) => g.category.includes(q.category as string));
  }
  if (q.search && q.search.trim().length > 0) {
    const s = q.search.trim().toLowerCase();
    items = items.filter((g) => g.title.toLowerCase().includes(s) || g.tags.some((t) => t.toLowerCase().includes(s)));
  }
  if (q.installedOnly) {
    items = items.filter((g) => g.installed);
  }
  if (q.launcher) {
    items = items.filter((g) => g.launcher === q.launcher);
  }
  const sort = q.sort ?? 'popularity';
  items.sort((a, b) => {
    if (sort === 'title') {
      return a.title.localeCompare(b.title);
    }
    if (sort === 'lastPlayed') {
      return Date.parse(b.lastPlayedAt ?? '1970-01-01') - Date.parse(a.lastPlayedAt ?? '1970-01-01');
    }
    return b.popularity - a.popularity;
  });
  const page = Math.max(1, q.page ?? 1);
  const pageSize = Math.min(500, Math.max(1, q.pageSize ?? 100));
  const total = items.length;
  return {
    items: clone(items.slice((page - 1) * pageSize, page * pageSize)),
    total,
    page,
    pageSize,
    catalogVersion: 'v42',
  };
});

function gameById(id: string): Game {
  const g = mockState.games.find((x) => x.id === id);
  if (!g) {
    mockError('notFound', 'Game not found', { name: id });
  }
  return g;
}

cmd('games_get', (args): Game => clone(gameById(str(args, 'gameId') ?? '')));

cmd('games_launch', async (args): Promise<LaunchResult> => {
  const req = obj<GamesLaunchRequest>(args, 'req');
  if (!req) {
    mockError('validation', 'req is required', { field: 'req', reason: 'required' });
  }
  const s = requireSession();
  if (s.state !== 'active') {
    mockError('sessionNotActive', 'Session is paused');
  }
  const game = gameById(req.gameId);
  if (!game.installed) {
    mockError('gameNotInstalled', `${game.title} is not installed`, { name: game.id });
  }
  if (mockState.running.some((r) => r.gameId === game.id)) {
    mockError('policyDenied', 'Game already running', { rule: 'alreadyRunning' });
  }
  const user = requireUser();
  if (game.ageRating >= 18 && user.role === 'guest') {
    mockError('policyDenied', 'Age rating', { rule: 'ageRating' });
  }
  const pid = 4000 + Math.floor(Math.random() * 5000);
  const startedAt = nowIso();
  const leaseId = game.requiresAccount ? newId() : null;
  const running: RunningGame = {
    gameId: game.id,
    title: game.title,
    pid,
    startedAt,
    accountLeaseId: leaseId,
    state: 'launching',
  };
  mockState.running.push(running);
  mockState.kiosk.gameMode = true;
  const change = (state: GameStateChanged['state'], extra: Partial<GameStateChanged> = {}): void =>
    emitMock('agent://game.stateChanged', { gameId: game.id, title: game.title, pid, state, at: nowIso(), ...extra });
  change('launching');
  await delay(900);
  later(1500, () => {
    const r = mockState.running.find((x) => x.pid === pid);
    if (r) {
      r.state = 'running';
      game.lastPlayedAt = nowIso();
      change('running');
    }
  });
  return { ok: true, pid, startedAt, accountLeaseId: leaseId, error: null };
});

cmd('games_kill', (args): GamesKillResponse => {
  const gameId = str(args, 'gameId');
  const pid = num(args, 'pid');
  const victims = mockState.running.filter(
    (r) => (gameId ? r.gameId === gameId : true) && (pid !== undefined ? r.pid === pid : true),
  );
  if ((gameId || pid !== undefined) && victims.length === 0) {
    mockError('notFound', 'No such running game', { name: gameId ?? String(pid) });
  }
  mockState.running = mockState.running.filter((r) => !victims.includes(r));
  mockState.kiosk.gameMode = mockState.running.length > 0;
  for (const v of victims) {
    emitMock('agent://game.stateChanged', {
      gameId: v.gameId,
      title: v.title,
      pid: v.pid,
      state: 'killed',
      at: nowIso(),
      exitCode: 137,
    });
  }
  return { killed: victims.length, pids: victims.map((v) => v.pid) };
});

cmd('games_running', (): RunningGame[] => clone(mockState.running));

cmd('games_install_status', (args): GameInstallStatus => {
  const g = gameById(str(args, 'gameId') ?? '');
  return {
    gameId: g.id,
    installed: g.installed,
    installPath: g.installPath ?? null,
    sizeGb: g.installed ? g.sizeGb : 0,
    version: g.installed ? (g.version ?? null) : null,
    verifiedAt: g.installed ? new Date(Date.now() - 6 * 3600_000).toISOString() : null,
    launcherReady: g.installed && g.launcher !== 'ubisoft',
  };
});

cmd('apps_list', (): App[] => clone(APPS));

cmd('apps_launch', (args): AppsLaunchResponse => {
  requireSession();
  const app = APPS.find((a) => a.id === str(args, 'appId'));
  if (!app) {
    mockError('notFound', 'App not found', { name: str(args, 'appId') ?? '' });
  }
  if (!app.allowed) {
    mockError('policyDenied', `${app.title} is blocked`, { rule: 'processAllowlist' });
  }
  return { ok: true, pid: 6000 + Math.floor(Math.random() * 3000), startedAt: nowIso() };
});

// ----- wallet ---------------------------------------------------------------------------------------------------------

cmd('wallet_balance', (): Balance => {
  requireUser();
  return clone(mockState.balance);
});

cmd('wallet_tariffs', (args): WalletTariffsResponse => {
  const zone = str(args, 'zone') ?? PC.zone;
  const items = TARIFFS.filter(
    (t) => t.zones.length === 0 || t.zones.some((z) => z.toLowerCase() === zone.toLowerCase()),
  );
  return { items: clone(items), zone, serverTime: nowIso() };
});

cmd('wallet_history', (args): WalletHistoryResponse => {
  requireUser();
  const q =
    obj<{
      page?: number | null;
      pageSize?: number | null;
      from?: string | null;
      to?: string | null;
      type?: Transaction['type'] | null;
    }>(args, 'q') ?? {};
  let items = mockState.transactions.slice();
  if (q.type) {
    items = items.filter((t) => t.type === q.type);
  }
  if (q.from) {
    const from = Date.parse(q.from);
    items = items.filter((t) => Date.parse(t.createdAt) >= from);
  }
  if (q.to) {
    const to = Date.parse(q.to);
    items = items.filter((t) => Date.parse(t.createdAt) < to);
  }
  const page = Math.max(1, q.page ?? 1);
  const pageSize = Math.min(200, Math.max(1, q.pageSize ?? 20));
  return { items: clone(items.slice((page - 1) * pageSize, page * pageSize)), total: items.length, page, pageSize };
});

cmd('wallet_topup_intent', (args): TopupIntent => {
  const user = requireUser();
  const amount = obj<Money>(args, 'amount');
  const provider = str(args, 'provider') as TopupProvider | undefined;
  if (!amount || typeof amount.amount !== 'number') {
    mockError('validation', 'amount is required', { field: 'amount', reason: 'required' });
  }
  if (amount.amount < 100_000) {
    mockError('validation', 'Minimum top-up is 1 000 UZS', { field: 'amount', reason: 'min' });
  }
  if (!provider || !['payme', 'click', 'uzum', 'cash'].includes(provider)) {
    mockError('validation', 'Unknown provider', { field: 'provider', reason: 'format' });
  }
  const id = newId();
  const intent: TopupIntent = {
    id,
    provider,
    amount: { ...amount },
    status: 'pending',
    qrUrl:
      provider === 'cash'
        ? null
        : `https://api.qrserver.com/v1/create-qr-code/?size=320x320&data=${encodeURIComponent(`https://pay.example.uz/${provider}/${id}`)}`,
    deepLink: provider === 'cash' ? null : `${provider}://pay/${id}`,
    paymentUrl: provider === 'cash' ? null : `https://pay.example.uz/${provider}/${id}`,
    expiresAt: isoIn(600),
    createdAt: nowIso(),
  };
  const settleIn = provider === 'cash' ? 12_000 : 6_000;
  later(settleIn, () => {
    if (mockState.user?.id !== user.id) {
      return;
    }
    pushTransaction('topUp', { ...amount }, `Top-up via ${provider === 'cash' ? 'cash at the desk' : provider}`, id);
    emitMock('agent://notification.push', {
      id: newId(),
      title: 'Top-up received',
      body: `+${Math.round(amount.amount / 100).toLocaleString('ru-RU')} UZS`,
      level: 'success',
      ttlSec: 8,
      action: null,
    });
  });
  if (provider === 'cash') {
    later(2500, () =>
      emitMock('agent://admin.message', {
        id: newId(),
        from: ADMIN_NAME,
        text: 'Coming to collect the cash top-up.',
        level: 'info',
        requiresAck: false,
        at: nowIso(),
      }),
    );
  }
  return intent;
});

// ----- shop -----------------------------------------------------------------------------------------------------------

cmd('shop_products', (args): Product[] => {
  const q = obj<{ category?: Product['category'] | null; search?: string | null }>(args, 'q') ?? {};
  let items = PRODUCTS.slice();
  if (q.category) {
    items = items.filter((p) => p.category === q.category);
  }
  if (q.search && q.search.trim().length > 0) {
    const s = q.search.trim().toLowerCase();
    items = items.filter((p) => p.title.toLowerCase().includes(s) || p.tags.some((t) => t.includes(s)));
  }
  return clone(items);
});

const ORDER_FLOW: { status: OrderStatus; afterMs: number }[] = [
  { status: 'accepted', afterMs: 4000 },
  { status: 'preparing', afterMs: 10_000 },
  { status: 'delivering', afterMs: 18_000 },
  { status: 'done', afterMs: 26_000 },
];

function scheduleOrder(orderId: string): void {
  for (const step of ORDER_FLOW) {
    later(step.afterMs, () => {
      const o = mockState.orders.find((x) => x.id === orderId);
      if (!o || o.status === 'cancelled') {
        return;
      }
      o.status = step.status;
      o.updatedAt = nowIso();
      emitMock('agent://shop.orderUpdated', clone(o));
    });
  }
}

cmd('shop_order', (args): Order => {
  const user = requireUser();
  requireSession();
  const req = obj<ShopOrderRequest>(args, 'req');
  if (!req) {
    mockError('validation', 'req is required', { field: 'req', reason: 'required' });
  }
  if (req.idempotencyKey && mockState.idempotency.has(req.idempotencyKey)) {
    return clone(mockState.idempotency.get(req.idempotencyKey) as Order);
  }
  if (!Array.isArray(req.items) || req.items.length === 0 || req.items.length > 20) {
    mockError('validation', '1–20 lines required', { field: 'items', reason: req.items?.length ? 'max' : 'required' });
  }
  if (user.flags.includes('noShop')) {
    mockError('forbidden', 'Shop disabled for this account', { reason: 'noShop' });
  }
  const lines = req.items.map((line, i) => {
    const p = PRODUCTS.find((x) => x.id === line.productId);
    if (!p) {
      mockError('notFound', 'Product not found', { name: line.productId });
    }
    if (!Number.isInteger(line.qty) || line.qty < 1 || line.qty > 99) {
      mockError('validation', 'qty must be 1–99', { field: `items[${i}].qty`, reason: 'max' });
    }
    if (!p.inStock || (p.stockQty != null && p.stockQty < line.qty)) {
      mockError('conflict', `${p.title} is out of stock`, { reason: 'outOfStock' });
    }
    return { productId: p.id, title: p.title, qty: line.qty, price: { ...p.price } };
  });
  const total = money(lines.reduce((sum, l) => sum + l.price.amount * l.qty, 0));
  assertFunds(total);
  const id = newId();
  pushTransaction(
    'purchase',
    money(-total.amount),
    `Shop order: ${lines.map((l) => (l.qty > 1 ? `${l.title} ×${l.qty}` : l.title)).join(', ')}`,
    id,
  );
  for (const l of lines) {
    const p = PRODUCTS.find((x) => x.id === l.productId);
    if (p && p.stockQty != null) {
      p.stockQty -= l.qty;
      p.inStock = p.stockQty > 0;
    }
  }
  const order: Order = {
    id,
    userId: user.id,
    pcId: PC_ID,
    items: lines,
    total,
    status: 'pending',
    createdAt: nowIso(),
    updatedAt: nowIso(),
    note: req.note ?? null,
  };
  mockState.orders.unshift(order);
  if (req.idempotencyKey) {
    mockState.idempotency.set(req.idempotencyKey, order);
  }
  scheduleOrder(id);
  emitMock('agent://shop.orderUpdated', clone(order));
  return clone(order);
});

cmd('shop_order_status', (args): Order => {
  requireUser();
  const o = mockState.orders.find((x) => x.id === str(args, 'orderId'));
  if (!o) {
    mockError('notFound', 'Order not found', { name: str(args, 'orderId') ?? '' });
  }
  return clone(o);
});

cmd('shop_orders', (args): ShopOrdersResponse => {
  const user = requireUser();
  const q = obj<{ page?: number | null; pageSize?: number | null; activeOnly?: boolean | null }>(args, 'q') ?? {};
  let items = mockState.orders.filter((o) => o.userId === user.id || o.userId === USER_ID);
  if (q.activeOnly) {
    items = items.filter((o) => o.status !== 'done' && o.status !== 'cancelled');
  }
  const page = Math.max(1, q.page ?? 1);
  const pageSize = Math.min(100, Math.max(1, q.pageSize ?? 20));
  return { items: clone(items.slice((page - 1) * pageSize, page * pageSize)), total: items.length };
});

// ----- chat -----------------------------------------------------------------------------------------------------------

cmd('chat_history', (args): ChatHistoryResponse => {
  requireUser();
  const q = obj<{ roomId?: string | null; before?: string | null; limit?: number | null }>(args, 'q') ?? {};
  const roomId = q.roomId ?? ROOM_ID;
  let items = mockState.messages.filter((m) => m.roomId === roomId);
  if (q.before) {
    const idx = items.findIndex((m) => m.id === q.before);
    if (idx >= 0) {
      items = items.slice(0, idx);
    }
  }
  const limit = Math.min(200, Math.max(1, q.limit ?? 50));
  const hasMore = items.length > limit;
  const page = items.slice(-limit);
  const unread = mockState.messages.filter(
    (m) => m.roomId === roomId && m.readAt === null && m.senderId !== mockState.user?.id,
  ).length;
  return { roomId, items: clone(page), hasMore, unread };
});

cmd('chat_send', (args): ChatMessage => {
  const user = requireUser();
  const text = (str(args, 'text') ?? '').trim();
  if (text.length === 0 || text.length > 2000) {
    mockError('validation', 'text must be 1–2000 chars', {
      field: 'text',
      reason: text.length === 0 ? 'required' : 'max',
    });
  }
  const roomId = str(args, 'roomId') ?? ROOM_ID;
  const key = str(args, 'idempotencyKey');
  if (key && mockState.idempotency.has(key)) {
    return clone(mockState.idempotency.get(key) as ChatMessage);
  }
  const msg: ChatMessage = {
    id: newId(),
    roomId,
    senderId: user.id,
    senderName: user.displayName,
    senderRole: user.role,
    text,
    createdAt: nowIso(),
    readAt: nowIso(),
    kind: 'text',
  };
  mockState.messages.push(msg);
  if (key) {
    mockState.idempotency.set(key, msg);
  }
  later(2000, () => {
    const reply: ChatMessage = {
      id: newId(),
      roomId,
      senderId: ADMIN_ID,
      senderName: ADMIN_NAME,
      senderRole: 'admin',
      text: ADMIN_REPLIES[Math.floor(Math.random() * ADMIN_REPLIES.length)] ?? 'Ok',
      createdAt: nowIso(),
      readAt: null,
      kind: 'admin',
    };
    mockState.messages.push(reply);
    emitMock('agent://chat.message', clone(reply));
  });
  return clone(msg);
});

cmd('chat_mark_read', (args): ChatMarkReadResponse => {
  requireUser();
  const roomId = str(args, 'roomId') ?? ROOM_ID;
  const upTo = str(args, 'upToMessageId') ?? '';
  const items = mockState.messages.filter((m) => m.roomId === roomId);
  const idx = items.findIndex((m) => m.id === upTo);
  const limit = idx >= 0 ? idx : items.length - 1;
  for (let i = 0; i <= limit; i += 1) {
    const m = items[i];
    if (m && m.readAt === null) {
      m.readAt = nowIso();
    }
  }
  const unread = items.filter((m) => m.readAt === null && m.senderId !== mockState.user?.id).length;
  return { roomId, unread };
});

// ----- booking --------------------------------------------------------------------------------------------------------

cmd('booking_seats', (args): BookingSeatsResponse => {
  const date = str(args, 'date');
  if (!date || !/^\d{4}-\d{2}-\d{2}$/.test(date)) {
    mockError('validation', 'date must be YYYY-MM-DD', { field: 'date', reason: 'format' });
  }
  const me = mockState.user?.id;
  const bookings = mockState.bookings
    .filter((b) => b.from.slice(0, 10) === date || b.to.slice(0, 10) === date)
    .map((b) => ({
      ...b,
      userId: b.userId === me || b.userId === USER_ID ? (me ?? b.userId) : '00000000-0000-0000-0000-000000000000',
    }));
  return { date, seats: clone(SEATS), bookings, slotMinutes: SLOT_MINUTES, openFrom: '10:00', openTo: '06:00' };
});

cmd('booking_reserve', (args): Booking => {
  const user = requireUser();
  const pcId = str(args, 'pcId');
  const from = str(args, 'from');
  const to = str(args, 'to');
  if (!pcId || !from || !to) {
    mockError('validation', 'pcId, from, to are required', {
      field: !pcId ? 'pcId' : !from ? 'from' : 'to',
      reason: 'required',
    });
  }
  const seatRow = SEATS.find((s) => s.pcId === pcId);
  if (!seatRow) {
    mockError('notFound', 'Seat not found', { name: pcId });
  }
  const f = Date.parse(from);
  const t = Date.parse(to);
  if (Number.isNaN(f) || Number.isNaN(t) || t <= f) {
    mockError('validation', 'Invalid time range', { field: 'to', reason: 'format' });
  }
  if (f < Date.now() - 60_000) {
    mockError('validation', 'Start must be in the future', { field: 'from', reason: 'min' });
  }
  if (seatRow.status === 'maintenance' || seatRow.status === 'offline') {
    mockError('conflict', 'Seat unavailable', { reason: seatRow.status });
  }
  const overlap = mockState.bookings.some(
    (b) =>
      b.pcId === pcId &&
      b.status !== 'cancelled' &&
      b.status !== 'expired' &&
      Date.parse(b.from) < t &&
      Date.parse(b.to) > f,
  );
  if (overlap) {
    mockError('conflict', 'Slot already booked', { reason: 'overlap' });
  }
  const booking: Booking = {
    id: newId(),
    userId: user.id,
    pcId,
    from: new Date(f).toISOString(),
    to: new Date(t).toISOString(),
    status: 'confirmed',
  };
  mockState.bookings.push(booking);
  return clone(booking);
});

cmd('booking_cancel', (args): Booking => {
  const user = requireUser();
  const b = mockState.bookings.find((x) => x.id === str(args, 'bookingId'));
  if (!b) {
    mockError('notFound', 'Booking not found', { name: str(args, 'bookingId') ?? '' });
  }
  if (b.userId !== user.id && b.userId !== USER_ID) {
    mockError('forbidden', 'Not your booking', { reason: 'owner' });
  }
  b.status = 'cancelled';
  return clone(b);
});

// ----- tournaments ----------------------------------------------------------------------------------------------------

cmd('tournaments_list', (args): Tournament[] => {
  const q = obj<{ state?: Tournament['state'] | null; gameId?: string | null }>(args, 'q') ?? {};
  let items = mockState.tournaments.slice();
  if (q.state) {
    items = items.filter((t) => t.state === q.state);
  }
  if (q.gameId) {
    items = items.filter((t) => t.gameId === q.gameId);
  }
  return clone(items);
});

cmd('tournaments_join', (args): Tournament => {
  requireUser();
  const t = mockState.tournaments.find((x) => x.id === str(args, 'tournamentId'));
  if (!t) {
    mockError('notFound', 'Tournament not found', { name: str(args, 'tournamentId') ?? '' });
  }
  if (t.joined) {
    return clone(t);
  }
  if (t.state !== 'registration') {
    mockError('conflict', 'Registration closed', { reason: 'registrationClosed' });
  }
  if (t.players >= t.maxPlayers) {
    mockError('conflict', 'Tournament is full', { reason: 'full' });
  }
  t.players += 1;
  t.joined = true;
  return clone(t);
});

cmd('tournaments_leaderboard', (args): TournamentsLeaderboardResponse => {
  const id = str(args, 'tournamentId') ?? '';
  if (!mockState.tournaments.some((t) => t.id === id)) {
    mockError('notFound', 'Tournament not found', { name: id });
  }
  const limit = Math.min(100, Math.max(1, num(args, 'limit') ?? 50));
  const entries = LEADERBOARD.slice(0, limit);
  const me = mockState.user
    ? (LEADERBOARD.find((e) => e.userId === mockState.user?.id || e.userId === USER_ID) ?? null)
    : null;
  return {
    tournamentId: id,
    entries: clone(entries),
    updatedAt: nowIso(),
    me: me && !entries.includes(me) ? clone(me) : null,
  };
});

// ----- profile --------------------------------------------------------------------------------------------------------

cmd('profile_get', (): User => clone(requireUser()));

cmd('profile_update', (args): User => {
  const user = requireUser();
  const patch = obj<ProfileUpdateRequest>(args, 'patch') ?? {};
  if (patch.displayName !== undefined && patch.displayName !== null) {
    const name = patch.displayName.trim();
    if (name.length === 0 || name.length > 32) {
      mockError('validation', 'displayName must be 1–32 chars', {
        field: 'displayName',
        reason: name.length === 0 ? 'required' : 'max',
      });
    }
    user.displayName = name;
  }
  if (patch.avatarUrl !== undefined) {
    user.avatarUrl = patch.avatarUrl;
  }
  if (patch.locale) {
    user.locale = patch.locale;
  }
  if (patch.pin !== undefined && patch.pin !== null && !/^\d{4,6}$/.test(patch.pin)) {
    mockError('validation', 'pin must be 4–6 digits', { field: 'pin', reason: 'format' });
  }
  return clone(user);
});

cmd('profile_stats', (): UserStats => {
  const user = requireUser();
  return user.role === 'guest'
    ? { totalHours: 0, sessionsCount: 0, favoriteGames: [], spent: uzs(0), rank: 0 }
    : clone(STATS);
});

cmd('profile_achievements', (): Achievement[] => {
  const user = requireUser();
  return user.role === 'guest' ? [] : clone(ACHIEVEMENTS);
});

cmd('profile_loyalty', (): Loyalty => {
  const user = requireUser();
  return user.role === 'guest' ? { level: 0, points: 0, nextLevelAt: 500, perks: [] } : clone(LOYALTY);
});

cmd('profile_game_settings', (): PlayerSettingsItem[] => {
  const user = requireUser();
  return user.role === 'guest' ? [] : clone(mockState.gameSettings);
});

cmd('profile_game_settings_reset', (args): PlayerSettingsItem[] => {
  requireUser();
  const gameId = str(args, 'gameId');
  mockState.gameSettings = mockState.gameSettings.filter((g) => g.gameId !== gameId);
  return clone(mockState.gameSettings);
});

// ----- settings / policy ----------------------------------------------------------------------------------------------

cmd('settings_get', (): ShellSettings => clone(mockState.settings));

cmd('settings_set', (args): ShellSettings => {
  const patch = obj<SettingsSetRequest>(args, 'patch');
  if (!patch || Object.values(patch).every((v) => v === null || v === undefined)) {
    mockError('validation', 'patch must set at least one key', { field: 'patch', reason: 'required' });
  }
  const s = mockState.settings;
  if (patch.theme != null) {
    if (!s.availableThemes.includes(patch.theme)) {
      mockError('notFound', 'Theme not found', { name: patch.theme });
    }
    s.theme = patch.theme;
  }
  if (patch.locale != null) {
    s.locale = patch.locale;
  }
  if (patch.volume != null) {
    if (patch.volume < 0 || patch.volume > 100) {
      mockError('validation', 'volume must be 0–100', { field: 'volume', reason: 'max' });
    }
    s.volume = Math.round(patch.volume);
    mockState.volume.level = s.volume;
  }
  if (patch.muted != null) {
    s.muted = patch.muted;
    mockState.volume.muted = patch.muted;
  }
  if (patch.idleTimeoutSec != null) {
    s.idleTimeoutSec = Math.max(0, Math.round(patch.idleTimeoutSec));
  }
  if (patch.showMetricsOverlay != null) {
    s.showMetricsOverlay = patch.showMetricsOverlay;
  }
  if (patch.allowVirtualKeyboard != null) {
    if (!mockState.policy.kiosk.allowVirtualKeyboard && patch.allowVirtualKeyboard) {
      mockError('policyDenied', 'Virtual keyboard disabled by policy', {
        rule: 'allowVirtualKeyboard',
        field: 'allowVirtualKeyboard',
      });
    }
    s.allowVirtualKeyboard = patch.allowVirtualKeyboard;
  }
  if (patch.uiSounds != null) {
    s.uiSounds = patch.uiSounds;
  }
  if (patch.theme != null) {
    emitMock('kiosk://themeChanged', clone(builtinThemes[patch.theme] ?? DEFAULT_THEME));
  }
  if (patch.locale != null) {
    emitMock('kiosk://localeChanged', { locale: patch.locale });
  }
  return clone(s);
});

cmd(
  'settings_get_theme',
  (args): Theme => {
    const name = str(args, 'name') ?? mockState.settings.theme;
    return clone(builtinThemes[name] ?? DEFAULT_THEME);
  },
  { fast: true },
);

cmd('settings_list_themes', (): string[] => mockState.settings.availableThemes.slice(), { fast: true });

cmd('settings_get_shell_config', (): ShellConfig => clone(SHELL_CONFIG), { fast: true });

cmd('policy_get', (): Policy => clone(mockState.policy));

cmd('policy_reload', (args): PolicyReloadResponse => {
  const force = bool(args, 'force') ?? false;
  return { policy: clone(mockState.policy), source: force ? 'server' : 'cache', applied: true, changed: [] };
});

// ----- system ---------------------------------------------------------------------------------------------------------

cmd(
  'sys_pc_info',
  (): PcInfo => ({ ...clone(PC_INFO), serverTime: nowIso(), uptimeSec: METRICS.uptimeSec + metricsSeq * 5 }),
);

cmd('sys_hardware', (): HardwareInfo => clone(HARDWARE));

cmd('sys_metrics', (): PcMetrics => metricsSample(), { fast: true });

cmd('sys_call_admin', (args): SysCallAdminResponse => {
  const category = str(args, 'category');
  if (!category || !['help', 'technical', 'order', 'other'].includes(category)) {
    mockError('validation', 'Unknown category', { field: 'category', reason: 'format' });
  }
  const sinceLast = Date.now() - mockState.lastCallAdminAt;
  if (sinceLast < 30_000) {
    mockError('rateLimited', 'Already called recently', { retryAfterSec: Math.ceil((30_000 - sinceLast) / 1000) });
  }
  mockState.lastCallAdminAt = Date.now();
  const ticketId = newId();
  later(3000, () =>
    emitMock('agent://admin.message', {
      id: newId(),
      from: ADMIN_NAME,
      text: 'On my way to your seat.',
      level: 'info',
      requiresAck: false,
      at: nowIso(),
    }),
  );
  return { ticketId, createdAt: nowIso(), queuePosition: 1 };
});

const power = (args: Record<string, unknown>): ScheduledResult => ({ scheduledAt: isoIn(num(args, 'delaySec') ?? 30) });
cmd('sys_reboot', power);
cmd('sys_shutdown', power);

cmd('sys_lock_screen', (): OkResponse => {
  const s = mockState.session;
  if (s && s.state === 'active') {
    s.state = 'locked';
    s.pausedAt = nowIso();
    mockState.kiosk.locked = true;
    emitSession();
  }
  emitMock('agent://shell.command', { command: 'lock', args: { reason: 'user', message: null }, commandId: newId() });
  return { ok: true };
});

cmd(
  'sys_set_volume',
  (args): VolumeState => {
    const level = num(args, 'level');
    if (level === undefined || level < 0 || level > 100) {
      mockError('validation', 'level must be 0–100', { field: 'level', reason: 'max' });
    }
    mockState.volume = { level: Math.round(level), muted: bool(args, 'muted') ?? mockState.volume.muted };
    mockState.settings.volume = mockState.volume.level;
    mockState.settings.muted = mockState.volume.muted;
    return { ...mockState.volume };
  },
  { fast: true },
);

cmd(
  'sys_set_locale',
  (args): SysSetLocaleResponse => {
    const locale = str(args, 'locale') as Locale | undefined;
    if (!locale || !['en', 'ru', 'uz'].includes(locale)) {
      mockError('validation', 'Unknown locale', { field: 'locale', reason: 'format' });
    }
    mockState.settings.locale = locale;
    emitMock('kiosk://localeChanged', { locale });
    return { locale };
  },
  { fast: true },
);

cmd('sys_unlock_admin', (args): SysUnlockAdminResponse => {
  const now = Date.now();
  mockState.adminAttempts = mockState.adminAttempts.filter((t) => now - t < 60_000);
  if (mockState.adminAttempts.length >= 3) {
    mockError('rateLimited', 'Too many attempts', { retryAfterSec: 60 });
  }
  mockState.adminAttempts.push(now);
  if (str(args, 'pin') !== MOCK_ADMIN_PIN) {
    mockError('unauthorized', 'Wrong admin PIN', { reason: 'wrongPin' });
  }
  mockState.adminAttempts = [];
  const token = newId();
  mockState.adminToken = { token, expiresAt: now + 300_000 };
  return { ok: true, adminToken: token, expiresAt: new Date(now + 300_000).toISOString() };
});

cmd('sys_ack_admin_message', (): OkResponse => ({ ok: true }), { fast: true });

cmd(
  'sys_log_client_error',
  (args): null => {
    const e = obj<{ level: string; message: string; stack?: string | null; route?: string | null }>(args, 'e');
    if (e && import.meta.env.DEV) {
      console.debug(`[mock:logClientError] ${e.level}: ${e.message}`, e.route ?? '');
    }
    return null;
  },
  { fast: true },
);

cmd(
  'update_check',
  (): UpdateCheckResponse => ({
    current: { agent: '1.4.2', shell: '1.0.0' },
    agent: null,
    shell: clone(UPDATE_MANIFEST),
  }),
);

cmd('update_apply', (args): UpdateApplyResponse => {
  const component = str(args, 'component');
  if (component !== 'agent' && component !== 'shell') {
    mockError('validation', 'Unknown component', { field: 'component', reason: 'format' });
  }
  const version = UPDATE_MANIFEST.version;
  const total = UPDATE_MANIFEST.size;
  const phases: UpdateProgress['phase'][] = ['downloading', 'verifying', 'staging', 'applying'];
  let step = 0;
  for (let pct = 10; pct <= 100; pct += 15) {
    const percent = Math.min(100, pct);
    later(500 + step * 500, () => {
      const phase = phases[Math.min(phases.length - 1, Math.floor((percent / 100) * phases.length))] ?? 'downloading';
      emitMock('agent://update.progress', {
        component,
        version,
        phase,
        percent,
        bytesDone: Math.round((total * percent) / 100),
        bytesTotal: total,
        error: null,
      });
    });
    step += 1;
  }
  later(500 + step * 500, () => {
    mockState.updateReady = true;
    emitMock('agent://update.ready', {
      component,
      version,
      restartRequired: component === 'shell',
      mandatory: false,
      applyAt: null,
    });
  });
  return { scheduled: true, at: isoIn(step * 0.5 + 1) };
});

// ----- kiosk (local) --------------------------------------------------------------------------------------------------

function requireAdmin(): void {
  const t = mockState.adminToken;
  if (!t || t.expiresAt < Date.now()) {
    mockError('forbidden', 'Admin unlock required', { reason: 'adminToken' });
  }
}

cmd('kiosk_state', (): KioskState => ({ ...clone(mockState.kiosk), gameMode: mockState.running.length > 0 }), {
  fast: true,
});

cmd(
  'kiosk_set_guard',
  (args): null => {
    const active = bool(args, 'active') ?? true;
    if (
      !active &&
      mockState.running.length === 0 &&
      (!mockState.adminToken || mockState.adminToken.expiresAt < Date.now())
    ) {
      mockError('forbidden', 'Guard may only be released while a game runs or with an admin unlock', {
        reason: 'guard',
      });
    }
    mockState.kiosk.guardActive = active;
    return null;
  },
  { fast: true },
);

cmd(
  'kiosk_set_fullscreen',
  (args): null => {
    requireAdmin();
    mockState.kiosk.fullscreen = bool(args, 'on') ?? true;
    return null;
  },
  { fast: true },
);

cmd(
  'kiosk_show_overlay',
  (args): null => {
    const kind = str(args, 'kind');
    if (kind !== 'lock' && kind !== 'ads' && kind !== 'message' && kind !== 'hud' && kind !== 'none') {
      mockError('validation', 'Unknown overlay kind', { field: 'kind', reason: 'format' });
    }
    mockState.kiosk.overlay = kind;
    emitMock('kiosk://overlay', { kind, payload: args['payload'] });
    return null;
  },
  { fast: true },
);

cmd('kiosk_monitors', () => clone(MONITORS), { fast: true });

cmd(
  'kiosk_move_to_monitor',
  (args): null => {
    const index = num(args, 'index') ?? -1;
    if (!MONITORS.some((m) => m.index === index)) {
      mockError('notFound', 'Monitor not found', { name: 'monitor' });
    }
    return null;
  },
  { fast: true },
);

cmd(
  'kiosk_virtual_keyboard',
  (): null => {
    if (!SHELL_CONFIG.kiosk.allowVirtualKeyboard) {
      mockError('policyDenied', 'Virtual keyboard disabled', { rule: 'allowVirtualKeyboard' });
    }
    return null;
  },
  { fast: true },
);

cmd('kiosk_focus', (): null => null, { fast: true });

cmd(
  'kiosk_exit',
  (args): null => {
    const token = str(args, 'adminToken');
    const action = str(args, 'action');
    if (action !== 'explorer' && action !== 'quit') {
      mockError('validation', 'action must be explorer|quit', { field: 'action', reason: 'format' });
    }
    if (!token || mockState.adminToken?.token !== token || mockState.adminToken.expiresAt < Date.now()) {
      mockError('unauthorized', 'Invalid admin token', { reason: 'adminToken' });
    }
    console.info(`[mock] kiosk_exit(${action}) — would exit the shell`);
    return null;
  },
  { fast: true },
);

cmd(
  'kiosk_reload',
  (): null => {
    if (typeof window !== 'undefined') {
      setTimeout(() => window.location.reload(), 50);
    }
    return null;
  },
  { fast: true },
);

cmd(
  'kiosk_open_devtools',
  (): null => {
    if (!SHELL_CONFIG.devtools) {
      mockError('forbidden', 'Devtools disabled', { reason: 'devtools' });
    }
    console.info('[mock] kiosk_open_devtools — use the browser devtools');
    return null;
  },
  { fast: true },
);

cmd(
  'kiosk_gamepad_state',
  (): GamepadState[] => {
    const pads: GamepadState[] = [];
    if (typeof navigator !== 'undefined' && typeof navigator.getGamepads === 'function') {
      for (const gp of navigator.getGamepads()) {
        if (gp) {
          let mask = 0;
          gp.buttons.forEach((b, i) => {
            if (b.pressed) {
              mask |= 1 << i;
            }
          });
          pads.push({
            index: gp.index,
            connected: gp.connected,
            buttons: mask,
            leftX: gp.axes[0] ?? 0,
            leftY: gp.axes[1] ?? 0,
            rightX: gp.axes[2] ?? 0,
            rightY: gp.axes[3] ?? 0,
            lt: gp.buttons[6]?.value ?? 0,
            rt: gp.buttons[7]?.value ?? 0,
          });
        }
      }
    }
    mockState.kiosk.gamepadConnected = pads.some((p) => p.connected);
    return pads;
  },
  { fast: true },
);

cmd(
  'kiosk_idle_reset',
  (): null => {
    if (mockState.kiosk.idle) {
      mockState.kiosk.idle = false;
      mockState.kiosk.idleSec = 0;
      emitMock('kiosk://idle', { idle: false, idleSec: 0, stage: 'active' });
    }
    return null;
  },
  { fast: true },
);

cmd('kiosk_i18n_bundle', (): Record<string, string> => ({}), { fast: true });

cmd(
  'kiosk_asset_url',
  (args): string => {
    const path = (str(args, 'path') ?? '').replace(/\\/g, '/');
    if (path.length === 0) {
      mockError('validation', 'path is required', { field: 'path', reason: 'required' });
    }
    if (/^(https?:|data:|blob:)/i.test(path) || path.startsWith('/')) {
      return path;
    }
    const base = path.split('/').pop() ?? path;
    const seed = base.replace(/\.[a-z0-9]+$/i, '');
    if (path.startsWith('themes/')) {
      if (/\.(mp4|webm)$/i.test(path)) {
        return 'https://cdn.jsdelivr.net/gh/mdn/interactive-examples@main/live-examples/media/examples/flower.webm';
      }
      if (seed === 'default-wallpaper') {
        return '/mock-art/default-wallpaper.jpg';
      }
      return `https://picsum.photos/seed/${seed}/1920/1080`;
    }
    if (path.startsWith('cache/media/')) {
      return `https://picsum.photos/seed/${seed}/600/900`;
    }
    mockError('forbidden', 'Path outside themes\\ or cache\\media\\', { reason: 'path' });
  },
  { fast: true },
);

/** Every command name TAURI_COMMANDS.md §4.1 lists; `tests/unit/mock-coverage` compares against this. */
export const MOCKED_COMMANDS: readonly string[] = Object.keys(registry);
