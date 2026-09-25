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
}

/** Error carrying the server's `ErrorCode` so screens can map `insufficientFunds` and friends to copy. */
export class AdminError extends Error {
  constructor(
    readonly code: string,
    message: string,
    readonly details: Record<string, unknown> | null,
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
    throw new AdminError(err?.code ?? 'internal', err?.message ?? res.statusText, err?.details ?? null);
  }
  return (await res.json()) as T;
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
    post<SessionResult>('/admin/sessions', input),
  extend: (input: { pcId: string; minutes: number; tariffId?: string }): Promise<SessionResult> =>
    post<SessionResult>('/admin/sessions/extend', input),
  end: (input: { pcId: string }): Promise<SessionResult> => post<SessionResult>('/admin/sessions/end', input),
  topUp: (input: {
    userId: string;
    amount: number;
    method?: string;
  }): Promise<{
    balance: Money;
    transaction: Transaction;
  }> => post('/admin/wallet/topup', input),
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
  limits: { minorAge: number; minorCurfew: string };
  catalog: { order: string[]; hidden: string[]; featured: string[] };
  banners: Banner[];
  rulesText: { ru: string; uz: string; en: string };
  stock: { lowAt: number };
  automation: AutomationRule[];
  notifications: {
    telegramBotToken: string;
    telegramChatId: string;
    events: Record<ClubEvent, boolean>;
    bigTopupAt: number;
  };
  webhooks: Webhook[];
  control: ControlSettings;
  apiKey: string;
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
  | 'pcCommand';

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
  telegram: string;
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
  login: (pin: string): Promise<{ token: string; staff: StaffMember; shift: Shift | null }> =>
    post('/admin/login', { pin }),
  me: (): Promise<{ staff: StaffMember; shift: Shift | null }> => call('/admin/me'),

  staff: (): Promise<{ items: StaffMember[] }> => call('/admin/staff'),
  addStaff: (input: { name: string; role: StaffRole; pin: string }): Promise<{ id: string }> =>
    post('/admin/staff', input),
  updateStaff: (id: string, input: Partial<{ name: string; active: boolean; pin: string }>): Promise<unknown> =>
    patch(`/admin/staff/${id}`, input),

  shift: (): Promise<{ shift: Shift | null; x: ShiftTotals | null; history: Shift[] }> => call('/admin/shift'),
  openShift: (openingCash: number): Promise<{ shift: Shift }> => post('/admin/shift/open', { openingCash }),
  closeShift: (closingCash: number): Promise<{ shift: Shift; expectedCash: number }> =>
    post('/admin/shift/close', { closingCash }),

  settings: (): Promise<ClubSettings> => call('/admin/club'),
  saveSettings: (partial: Partial<ClubSettings>): Promise<unknown> => patch('/admin/club', partial),
  rotateApiKey: (): Promise<{ apiKey: string }> => post('/admin/club/api-key', {}),
  testNotification: (): Promise<unknown> => post('/admin/notifications/test', {}),

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
    telegram?: string;
  }): Promise<{ client: Client }> => post('/admin/clients', input),
  updateClient: (id: string, input: Partial<Client>): Promise<{ client: Client }> =>
    patch(`/admin/clients/${id}`, input),
  clientTransactions: (id: string): Promise<{ items: Transaction[] }> => call(`/admin/clients/${id}/transactions`),
  redeemPromo: (userId: string, code: string): Promise<{ balance: Money }> =>
    post('/admin/promo/redeem', { userId, code }),

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
  receiveProduct: (id: string, qty: number): Promise<unknown> => post(`/admin/products/${id}/receive`, { qty }),

  games: (): Promise<{ items: AdminGame[]; order: string[] }> => call('/admin/games'),
  saveGameSettingsPaths: (id: string, settingsPaths: string[]): Promise<{ settingsPaths: string[] }> =>
    patch(`/admin/games/${id}`, { settingsPaths }),

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
