/**
 * Client of the console routes (`/api/v1/admin/*`: `tools/MockServer/src/routes/admin.ts` for the counter,
 * `routes/club.ts` for everything the owner configures). Staff sign in by PIN; the token is kept in localStorage.
 * A real deployment points `VITE_ADMIN_API` at the operator's own server.
 */
import type {
  Money,
  PcStatus,
  Product,
  Session,
  ServerErrorEnvelope,
  ShellFeatures,
  Tariff,
  TariffTimeWindow,
  Transaction,
} from '@clubshell/contracts';

const BASE = (import.meta.env['VITE_ADMIN_API'] as string | undefined) ?? 'http://localhost:8080/api/v1';
const TOKEN_KEY = 'clubshell.admin.token';
const CLUB_CODE_KEY = 'clubshell.admin.clubCode';

let token: string | null = (() => {
  try {
    return localStorage.getItem(TOKEN_KEY);
  } catch {
    return null;
  }
})();

export function setToken(value: string | null): void {
  token = value;
  try {
    if (value) localStorage.setItem(TOKEN_KEY, value);
    else localStorage.removeItem(TOKEN_KEY);
  } catch {
    // private mode: the session just does not survive a reload
  }
}

/** The club code of this console (a server may hold several clubs; the PIN is looked up in the club of the code). */
export function getClubCode(): string {
  try {
    return localStorage.getItem(CLUB_CODE_KEY) ?? '';
  } catch {
    return '';
  }
}

export function setClubCode(value: string): void {
  try {
    if (value) localStorage.setItem(CLUB_CODE_KEY, value);
    else localStorage.removeItem(CLUB_CODE_KEY);
  } catch {
    // private mode: the code is typed again next time
  }
}

export function hasToken(): boolean {
  return token !== null;
}

export interface SeatUser {
  id: string;
  displayName: string;
  role: string;
  balance: Money;
}

export interface Seat {
  pc: {
    id: string;
    name: string;
    zone: string;
    number: number;
    status: 'free' | 'busy' | 'locked' | 'maintenance' | 'booked' | 'offline';
  };
  session: Session | null;
  user: SeatUser | null;
}

export interface Member extends SeatUser {
  username: string;
}

export interface Overview {
  at: string;
  club: { free: number; total: number };
  seats: Seat[];
  tariffs: Tariff[];
  users: Member[];
  zones: Zone[];
  /** PCs with an open repair ticket and its worst severity. */
  repairs?: { pcId: string; severity: 'high' | 'medium' }[];
  /** Guests left with a postpaid bill (`limits.guestPostpaid`); a top-up of `debt` clears one. */
  guestDebts?: { userId: string; displayName: string; debt: Money; pc: string | null; endedAt: string | null }[];
}

/** Error carrying the server's `ErrorCode` so screens can map `insufficientFunds` and friends to copy. */
export class AdminError extends Error {
  constructor(
    readonly code: string,
    message: string,
    readonly details: Record<string, unknown> | null,
    /** HTTP status; 0 when no answer came back. */
    readonly status = 0,
  ) {
    super(message);
    this.name = 'AdminError';
  }
}

async function call<T>(path: string, init?: RequestInit): Promise<T> {
  let res: Response;
  try {
    res = await fetch(`${BASE}${path}`, {
      ...init,
      headers: {
        'Content-Type': 'application/json',
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        ...(init?.headers ?? {}),
      },
    });
  } catch (e) {
    throw new AdminError('network', e instanceof Error ? e.message : 'Network error', null);
  }
  if (!res.ok) {
    const envelope = (await res.json().catch(() => null)) as ServerErrorEnvelope | null;
    const err = envelope?.error;
    if (res.status === 401 && path !== '/admin/login') {
      setToken(null);
      window.dispatchEvent(new Event('admin:signed-out'));
    }
    throw new AdminError(err?.code ?? 'internal', err?.message ?? res.statusText, err?.details ?? null, res.status);
  }
  return (await res.json()) as T;
}

/** A UUID for `Idempotency-Key`; `crypto.randomUUID` exists only on https/localhost, a LAN console may be plain http. */
function newKey(): string {
  if (typeof crypto.randomUUID === 'function') return crypto.randomUUID();
  const b = crypto.getRandomValues(new Uint8Array(16));
  b[6] = ((b[6] ?? 0) & 0x0f) | 0x40;
  b[8] = ((b[8] ?? 0) & 0x3f) | 0x80;
  const h = Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('');
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}

/** Keys of money actions still waiting for a definite answer, by path + body. */
const pendingKeys = new Map<string, { key: string; at: number }>();

/**
 * How long a lost answer may be retried under the same key. Later, an identical body (the same top-up, `end` of the
 * same PC) is a new cashier action: reusing the key would replay the old result and silently skip it.
 */
const RETRY_WINDOW_MS = 2 * 60_000;

/**
 * POST of a money action with an `Idempotency-Key`: one key per cashier action, reused when the cashier repeats the
 * same action shortly after a lost answer (no response or 5xx), so the server replays the first result instead of
 * charging twice. A success, a refusal (4xx) or {@link RETRY_WINDOW_MS} ends the action: the next identical request is
 * a new one.
 */
async function postMoney<T>(path: string, payload: unknown): Promise<T> {
  const body = JSON.stringify(payload);
  const action = `${path} ${body}`;
  const now = Date.now();
  const pending = pendingKeys.get(action);
  const key = pending && now - pending.at < RETRY_WINDOW_MS ? pending.key : newKey();
  pendingKeys.set(action, { key, at: now });
  try {
    const r = await call<T>(path, { method: 'POST', body, headers: { 'Idempotency-Key': key } });
    pendingKeys.delete(action);
    return r;
  } catch (e) {
    if (e instanceof AdminError && e.status >= 400 && e.status < 500) pendingKeys.delete(action);
    throw e;
  }
}

const post = <T>(path: string, payload: unknown): Promise<T> =>
  call<T>(path, { method: 'POST', body: JSON.stringify(payload) });
const patch = <T>(path: string, payload: unknown): Promise<T> =>
  call<T>(path, { method: 'PATCH', body: JSON.stringify(payload) });
const put = <T>(path: string, payload: unknown): Promise<T> =>
  call<T>(path, { method: 'PUT', body: JSON.stringify(payload) });
const del = <T>(path: string): Promise<T> => call<T>(path, { method: 'DELETE' });

export interface SessionResult {
  session: Session;
  charged: Money;
  balance?: Money;
  refunded?: Money;
}

export const adminApi = {
  overview: (): Promise<Overview> => call<Overview>('/admin/overview'),
  openSession: (input: { pcId: string; userId: string; tariffId: string; minutes: number }): Promise<SessionResult> =>
    postMoney<SessionResult>('/admin/sessions', input),
  extend: (input: { pcId: string; minutes: number; tariffId?: string }): Promise<SessionResult> =>
    postMoney<SessionResult>('/admin/sessions/extend', input),
  end: (input: { pcId: string }): Promise<SessionResult> => postMoney<SessionResult>('/admin/sessions/end', input),
  topUp: (input: {
    userId: string;
    amount: number;
    method?: string;
  }): Promise<{
    balance: Money;
    transaction: Transaction;
  }> => postMoney('/admin/wallet/topup', input),
  command: (
    pcId: string,
    input: { kind: 'message' | 'lock' | 'unlock' | 'reboot' | 'shutdown'; text?: string },
  ): Promise<{ ack: { ok: boolean; error?: { code: string; message: string } | null } }> =>
    post(`/admin/pcs/${pcId}/command`, input),
};

// ---------------------------------------------------------------------------------------------------------------------
// Club configuration (routes/club.ts)
// ---------------------------------------------------------------------------------------------------------------------

export type StaffRole = 'owner' | 'cashier';
export interface StaffMember {
  id: string;
  name: string;
  role: StaffRole;
  active: boolean;
}

export interface ShiftTotals {
  topUpCash: number;
  topUpOther: number;
  sessions: number;
  shop: number;
  refunds: number;
  bonuses: number;
  count: number;
}
export interface Shift {
  id: string;
  staffId: string;
  staffName: string;
  openedAt: string;
  closedAt: string | null;
  openingCash: number;
  closingCash: number | null;
  totals: ShiftTotals | null;
}

export interface Zone {
  name: string;
  color: string;
}
export interface ClientGroup {
  id: string;
  name: string;
  discountPct: number;
  color: string;
}
export interface BonusTier {
  minAmount: number;
  bonusPct: number;
}
export interface PromoCode {
  code: string;
  kind: 'bonus' | 'discountPct';
  value: number;
  usesLeft: number | null;
  expiresAt: string | null;
  used: number;
}
export interface HappyHour {
  id: string;
  name: string;
  days: number[];
  from: string;
  to: string;
  discountPct: number;
  zones: string[];
}
export interface LoyaltyLevel {
  level: number;
  name: string;
  minSpent: number;
  discountPct: number;
}
export type RuleTrigger =
  | { kind: 'minutesLeft'; value: number }
  | { kind: 'pcIdleMinutes'; value: number }
  | { kind: 'visitCount'; value: number }
  | { kind: 'topupAtLeast'; value: number }
  | { kind: 'sessionStarted' };
export type RuleAction =
  | { kind: 'message'; text: string }
  | { kind: 'bonus'; amount: number }
  | { kind: 'lockPc' }
  | { kind: 'shutdownPc' }
  | { kind: 'notifyOwner'; text: string };
export interface AutomationRule {
  id: string;
  name: string;
  enabled: boolean;
  trigger: RuleTrigger;
  action: RuleAction;
  fired: number;
  lastFiredAt: string | null;
}
export interface Banner {
  id: string;
  title: string;
  imageUrl: string;
  from: string | null;
  to: string | null;
  enabled: boolean;
}
export type ClubEvent =
  | 'shiftClosed'
  | 'pcOffline'
  | 'bigTopup'
  | 'lowStock'
  | 'ruleFired'
  | 'sessionOpened'
  | 'suspicious'
  | 'hardware';
export interface Webhook {
  id: string;
  url: string;
  events: ClubEvent[];
  enabled: boolean;
  lastStatus: number | null;
  lastAt: string | null;
}

/** The settings document the owner edits (`GET/PATCH /admin/club`). Amounts are minor units (tiyin). */
export interface ClubSettings {
  branding: { clubName: string; accent: string; logoUrl: string | null; wallpaperUrl: string | null };
  features: ShellFeatures;
  zones: Zone[];
  pricing: { weekdayPct: number[]; holidays: string[]; holidayPct: number };
  groups: ClientGroup[];
  bonusTiers: BonusTier[];
  promoCodes: PromoCode[];
  happyHours: HappyHour[];
  loyalty: LoyaltyLevel[];
  /**
   * `guestPostpaid` / `guestDebtLimit` (tiyin, null or 0 — no limit) are beyond the contract: guests may play postpaid and
   * pay at the counter afterwards; their unpaid bills come back in `Overview.guestDebts`.
   */
  limits: {
    minorAge: number;
    minorCurfew: string;
    guestPostpaid?: boolean;
    guestDebtLimit?: number | null;
    /** Minutes the server extends a prepaid session by when it runs out and the balance pays; 0 — off. */
    autoExtendMinutes?: number;
  };
  catalog: { order: string[]; hidden: string[]; featured: string[] };
  banners: Banner[];
  rulesText: { ru: string; uz: string; en: string };
  stock: { lowAt: number };
  automation: AutomationRule[];
  /** Threshold of the `bigTopup` event, minor units. Telegram fields an older server still sends are ignored. */
  notifications?: { bigTopupAt: number };
  webhooks: Webhook[];
  control: ControlSettings;
  /** Only from an older server; the key is read by the owner from `GET /admin/club/api-key`. */
  apiKey?: string;
  events: ClubEvent[];
}

/** Thresholds of the cashier-control rules. Money in minor units. */
export interface ControlSettings {
  earlyEndMinutes: number;
  earlyEndsPerShift: number;
  discountPct: number;
  sameClientTopups: number;
  shortfallFrom: number;
}

export type AuditAction =
  | 'shiftOpen'
  | 'shiftClose'
  | 'topUp'
  | 'sessionOpen'
  | 'sessionExtend'
  | 'sessionEnd'
  | 'promoRedeem'
  | 'clientGroup'
  | 'blacklist'
  | 'stockReceive'
  | 'stockEdit'
  | 'pcCommand'
  | 'clientPassword'
  | 'clientCard';

export interface AuditEntry {
  id: string;
  at: string;
  staffId: string;
  staffName: string;
  shiftId: string | null;
  action: AuditAction;
  userId: string | null;
  pcId: string | null;
  amount: number;
  detail: string;
  meta: Record<string, string | number | boolean | null>;
}

export type FlagKind = 'shortfall' | 'earlyEnds' | 'earlyEnd' | 'discount' | 'sameClient' | 'noShift' | 'bigCash';
export type Severity = 'high' | 'medium' | 'low';

export interface ControlFlag {
  id: string;
  kind: FlagKind;
  severity: Severity;
  at: string;
  staffId: string;
  staffName: string;
  shiftId: string | null;
  userId: string | null;
  pcId: string | null;
  amount: number;
  params: Record<string, string | number>;
}

export interface StaffSummary {
  staffId: string;
  staffName: string;
  operations: number;
  topUps: number;
  refunds: number;
  earlyEnds: number;
  discounts: number;
  shortfall: number;
  flags: Record<Severity, number>;
}

export type HealthKind = 'cpuHot' | 'gpuHot' | 'cpuTrend' | 'gpuTrend' | 'fpsDrop' | 'unstable';
export type HealthSeverity = 'high' | 'medium';
export type TicketStatus = 'open' | 'inWork' | 'resolved';

export interface HealthIssue {
  kind: HealthKind;
  severity: HealthSeverity;
  params: Record<string, number>;
}

export interface HealthTicket {
  id: string;
  pcId: string;
  pcName: string;
  kind: HealthKind;
  severity: HealthSeverity;
  params: Record<string, number>;
  status: TicketStatus;
  openedAt: string;
  updatedAt: string;
  resolvedAt: string | null;
  note: string;
  autoMaintenance: boolean;
}

export interface HealthSettings {
  cpuHotC: number;
  gpuHotC: number;
  trendC: number;
  fpsDropPct: number;
  offlinePerDay: number;
  autoMaintenance: boolean;
}

export interface PcHealth {
  id: string;
  name: string;
  zone: string;
  status: PcStatus;
  score: number;
  live: { cpu: number | null; gpu: number | null; fps: number | null };
  baseline: { cpu: number | null; gpu: number | null; fps: number | null };
  hourly: { cpu: (number | null)[]; gpu: (number | null)[]; fps: (number | null)[] };
  issues: HealthIssue[];
  ticket: HealthTicket | null;
}

export interface HealthReport {
  settings: HealthSettings;
  pcs: PcHealth[];
  tickets: HealthTicket[];
}

export interface NetworkClubReport {
  id: string;
  name: string;
  city: string;
  address: string;
  /** The club this server runs (real numbers). */
  local: boolean;
  /** Played by the demo simulator, not by real Agents. */
  simulated: boolean;
  pcs: number;
  busyNow: number;
  revenueToday: number;
  revenue: number;
  sessions: number;
  avgCheck: number;
  byDay: number[];
  hourly: number[];
  repairs: number;
  signals: number;
  shift: { staffName: string; since: string } | null;
}

export interface NetworkReport {
  name: string;
  days: number;
  clubs: NetworkClubReport[];
  totals: { clubs: number; pcs: number; busyNow: number; revenue: number; revenueToday: number; sessions: number };
  players: { total: number; balance: number; multiClub: number };
}

export interface ControlReport {
  from: string;
  to: string;
  settings: ControlSettings;
  staff: StaffSummary[];
  flags: ControlFlag[];
  log: AuditEntry[];
}

export interface Client {
  id: string;
  username: string;
  displayName: string;
  role: string;
  balance: Money;
  bonus: Money;
  groupId: string | null;
  note: string;
  blacklisted: boolean;
  phone: string;
  birthYear: number | null;
  /** Club card the client signs in with; absent from an older server. */
  cardId?: string | null;
  spent: number;
  visits: number;
  level: number;
  levelName: string;
}

export type DeviceKind = 'pc' | 'console' | 'vr' | 'other';
export interface HallPc {
  id: string;
  name: string;
  zone: string;
  number: number;
  status: Seat['pc']['status'];
  ipAddress: string;
  x: number;
  y: number;
  device: DeviceKind;
  hardware: Record<string, unknown> | null;
  metrics: {
    cpuPct: number;
    gpuPct: number;
    ramUsedMb: number;
    temps: { cpu?: number | null; gpu?: number | null };
    fps?: number | null;
  } | null;
}

export interface AdminGame {
  id: string;
  title: string;
  coverUrl: string | null;
  installed: boolean;
  launcher: string;
  category: string[];
  hidden: boolean;
  featured: boolean;
  /** Where the game keeps a player's own settings (carried from PC to PC per player). */
  settingsPaths: string[];
  /** Steam app id (the number in the store link) or the game's code in its launcher; null for an exe game. */
  launcherAppId?: string | null;
  /** Full path of the game's .exe on the PCs; null for a launcher game. */
  exePath?: string | null;
  args?: string | null;
  description?: string;
  /** The club's own game (added or changed here): server updates of the starter catalogue leave it alone. */
  custom?: boolean;
  /** Trailer the player shell plays behind the game's art: a direct https link to an .mp4/.webm file. */
  videoUrl?: string | null;
}

/** What the owner sets for a game of the club (`POST /admin/games`, `PUT /admin/games/{id}`). */
export interface GameInput {
  title: string;
  launcher: string;
  exePath?: string | null;
  args?: string | null;
  launcherAppId?: string | null;
  coverUrl?: string | null;
  category: string[];
  description?: string | null;
  /** https only; null — no trailer (a save replaces the game's card, so send the current one to keep it). */
  videoUrl?: string | null;
}

export interface PriceQuote {
  base: Money;
  dayPct: number;
  discountPct: number;
  discountReason: string | null;
  total: Money;
}

export interface Reports {
  days: number;
  totals: { sessions: number; shop: number; topUps: number; sessionsCount: number };
  byDay: { date: string; sessions: number; shop: number; topUps: number }[];
  /** Seat-hours per weekday (0 = Sunday) × hour. */
  heat: number[][];
  topGames: { id: string; title: string; players: number }[];
  topProducts: { title: string; qty: number; amount: number }[];
  shifts: Shift[];
}

export type TariffInput = Omit<Tariff, 'id' | 'pricePerHour' | 'packagePrice'> & {
  pricePerHour: number;
  packagePrice: number | null;
  timeWindows: TariffTimeWindow[];
};

export const clubApi = {
  login: (pin: string, clubCode?: string): Promise<{ token: string; staff: StaffMember; shift: Shift | null }> =>
    post('/admin/login', clubCode ? { pin, clubCode } : { pin }),
  me: (): Promise<{ staff: StaffMember; shift: Shift | null }> => call('/admin/me'),
  /** Revokes the staff token on the server; the caller drops it locally. */
  logout: (): Promise<unknown> => post('/admin/logout', {}),

  staff: (): Promise<{ items: StaffMember[] }> => call('/admin/staff'),
  addStaff: (input: { name: string; role: StaffRole; pin: string }): Promise<{ id: string }> =>
    post('/admin/staff', input),
  updateStaff: (id: string, input: Partial<{ name: string; active: boolean; pin: string }>): Promise<unknown> =>
    patch(`/admin/staff/${id}`, input),

  shift: (): Promise<{ shift: Shift | null; x: ShiftTotals | null; history: Shift[] }> => call('/admin/shift'),
  openShift: (openingCash: number): Promise<{ shift: Shift }> => post('/admin/shift/open', { openingCash }),
  closeShift: (closingCash: number): Promise<{ shift: Shift; expectedCash: number }> =>
    postMoney('/admin/shift/close', { closingCash }),

  settings: (): Promise<ClubSettings> => call('/admin/club'),
  saveSettings: (partial: Partial<ClubSettings>): Promise<unknown> => patch('/admin/club', partial),
  apiKey: (): Promise<{ apiKey: string }> => call('/admin/club/api-key'),
  rotateApiKey: (): Promise<{ apiKey: string }> => post('/admin/club/api-key', {}),

  tariffs: (): Promise<{ items: Tariff[] }> => call('/admin/tariffs'),
  addTariff: (t: TariffInput): Promise<{ tariff: Tariff }> => post('/admin/tariffs', t),
  saveTariff: (id: string, t: TariffInput): Promise<{ tariff: Tariff }> => put(`/admin/tariffs/${id}`, t),
  deleteTariff: (id: string): Promise<unknown> => del(`/admin/tariffs/${id}`),
  quote: (input: { tariffId: string; pcId: string; minutes: number; userId?: string | null }): Promise<PriceQuote> =>
    post('/admin/quote', input),

  clients: (q = ''): Promise<{ items: Client[] }> => call(`/admin/clients?q=${encodeURIComponent(q)}`),
  addClient: (input: {
    displayName: string;
    username: string;
    phone?: string;
    birthYear?: number | null;
    groupId?: string | null;
    password?: string;
    cardId?: string | null;
  }): Promise<{ client: Client }> => post('/admin/clients', input),
  setClientPassword: (id: string, password: string): Promise<unknown> =>
    post(`/admin/clients/${id}/password`, { password }),
  bindCard: (id: string, cardId: string | null): Promise<{ client: Client }> =>
    post(`/admin/clients/${id}/card`, { cardId }),
  updateClient: (id: string, input: Partial<Client>): Promise<{ client: Client }> =>
    patch(`/admin/clients/${id}`, input),
  clientTransactions: (id: string): Promise<{ items: Transaction[] }> => call(`/admin/clients/${id}/transactions`),
  redeemPromo: (userId: string, code: string): Promise<{ balance: Money }> =>
    postMoney('/admin/promo/redeem', { userId, code }),

  pcs: (): Promise<{ items: HallPc[]; zones: Zone[] }> => call('/admin/pcs'),
  updatePc: (
    id: string,
    input: Partial<{
      zone: string;
      name: string;
      number: number;
      x: number;
      y: number;
      device: DeviceKind;
      maintenance: boolean;
    }>,
  ): Promise<unknown> => patch(`/admin/pcs/${id}`, input),
  addPc: (input: {
    zone: string;
    number: number;
    device: DeviceKind;
    name?: string;
    x?: number;
    y?: number;
  }): Promise<unknown> => post('/admin/pcs', input),
  deletePc: (id: string): Promise<unknown> => del(`/admin/pcs/${id}`),

  products: (): Promise<{ items: Product[]; lowAt: number }> => call('/admin/products'),
  updateProduct: (
    id: string,
    input: Partial<{ title: string; price: number; inStock: boolean; stockQty: number | null }>,
  ): Promise<unknown> => patch(`/admin/products/${id}`, input),
  receiveProduct: (id: string, qty: number): Promise<unknown> => postMoney(`/admin/products/${id}/receive`, { qty }),

  games: (): Promise<{ items: AdminGame[]; order: string[] }> => call('/admin/games'),
  saveGameSettingsPaths: (id: string, settingsPaths: string[]): Promise<{ settingsPaths: string[] }> =>
    patch(`/admin/games/${id}`, { settingsPaths }),
  addGame: (input: GameInput): Promise<{ game: AdminGame }> => post('/admin/games', input),
  saveGame: (id: string, input: GameInput): Promise<{ game: AdminGame }> => put(`/admin/games/${id}`, input),
  deleteGame: (id: string): Promise<unknown> => del(`/admin/games/${id}`),

  reports: (days: number): Promise<Reports> => call(`/admin/reports?days=${days}`),
  network: (days: number): Promise<NetworkReport> => call(`/admin/network?days=${days}`),
  addNetworkClub: (input: { name: string; city: string; address: string; pcs: number }): Promise<unknown> =>
    post('/admin/network/clubs', input),
  health: (): Promise<HealthReport> => call('/admin/health'),
  updateTicket: (id: string, status: TicketStatus, note?: string): Promise<{ ticket: HealthTicket }> =>
    patch(`/admin/health/tickets/${id}`, { status, note: note ?? null }),
  saveHealthSettings: (settings: HealthSettings): Promise<{ settings: HealthSettings }> =>
    patch('/admin/health/settings', settings),
  control: (days: number, staffId: string | null): Promise<ControlReport> =>
    call(`/admin/control?days=${days}${staffId ? `&staffId=${encodeURIComponent(staffId)}` : ''}`),
};
