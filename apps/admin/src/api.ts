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
    /** Versions the PC reported in its last heartbeat (absent from an older server). */
    agentVersion?: string;
    shellVersion?: string;
  };
  session: Session | null;
  user: SeatUser | null;
  /**
   * The session's player holds a live sign-in on this PC; false — a desk session nobody has signed in to yet (its clock
   * already runs); null — no session (absent from an older server).
   */
  signedIn?: boolean | null;
  /**
   * The game the PC's agent reports running for this session (D-71, from its heartbeat, up to 30 s late): its catalog
   * title and art (an empty cover is null). null — none, no session, or the PC is not busy or locked; absent from an
   * older server — the map shows no game.
   */
  game?: SeatGame | null;
}

/** A running game on a seat (`Seat.game`). */
export interface SeatGame {
  id: string;
  title: string;
  coverUrl: string | null;
  heroUrl: string | null;
}

export interface Member extends SeatUser {
  username: string;
}

/** A player left with a negative balance (a postpaid bill): an exact `settleDebt` top-up of `debt` clears it. */
export interface GuestDebt {
  userId: string;
  displayName: string;
  debt: Money;
  pc: string | null;
  endedAt: string | null;
  /** `guest` | `member` | `vip`; absent from an older server, which lists guests only. */
  role?: string;
}

/** A walk-in guest with money left on the account: `payable` of it may be given back in cash (`payout`). */
export interface GuestRefund {
  userId: string;
  displayName: string;
  balance: Money;
  payable: Money;
  pc: string | null;
  endedAt: string | null;
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
  /** Unpaid postpaid bills of guests and members («Расчёт с гостями и долги»). */
  guestDebts?: GuestDebt[];
  /** Walk-in guests with an unused refund on the account (beyond the contract). */
  guestRefunds?: GuestRefund[];
  /**
   * Players' calls the desk has not closed: open and answered ones of the last 12 hours, newest first (cash desk part 3).
   * Absent from an older server: no inbox then.
   */
  calls?: Call[];
}

export type CallCategory = 'help' | 'technical' | 'order' | 'other' | 'problem';

/** A player's call to the desk: the PC's «Позвать администратора», its telemetry copy, or a «report a problem» text. */
export interface Call {
  id: string;
  pcId: string;
  pcName: string;
  pcNumber: number;
  user: { id: string; displayName: string } | null;
  category: CallCategory;
  message: string | null;
  source: 'direct' | 'telemetry' | 'report';
  /** When the server got it. */
  at: string;
  status: 'open' | 'acked' | 'resolved';
  /** Came within 10 minutes of an answered call of the same PC: shown, never rung again. */
  repeat: boolean;
  ackedBy: string | null;
  ackedAt: string | null;
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

/**
 * A money request with no answer in this time is a lost answer (status 0): a dropped club link can otherwise hold a
 * payment "in progress" for minutes. The money screens then offer the same payment again under the same key. Other
 * requests wait as long as the browser does (a PC command waits up to 30 s for the PC's answer).
 */
const MONEY_TIMEOUT_MS = 20_000;

async function call<T>(path: string, init?: RequestInit, timeoutMs?: number): Promise<T> {
  let res: Response;
  const abort = new AbortController();
  const timer = timeoutMs ? window.setTimeout(() => abort.abort(), timeoutMs) : 0;
  try {
    res = await fetch(`${BASE}${path}`, {
      ...init,
      signal: abort.signal,
      headers: {
        'Content-Type': 'application/json',
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        ...(init?.headers ?? {}),
      },
    });
  } catch (e) {
    throw new AdminError('network', e instanceof Error ? e.message : 'Network error', null);
  } finally {
    window.clearTimeout(timer);
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

/**
 * A UUID for `Idempotency-Key`; `crypto.randomUUID` exists only on https/localhost, a LAN console may be plain http.
 * A money sheet takes one when the cashier starts an action and keeps it until a definite answer (D-46).
 */
export function newKey(): string {
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
 * POST of a money action with an `Idempotency-Key`. A sheet that holds its own key for the action (`held`) keeps it
 * frozen until a definite answer, however late the cashier retries (D-46). Either way the key of a lost answer (no
 * response or 5xx) is also remembered by path + body: when the cashier closes the frozen sheet and enters the same
 * action again shortly after, the same key goes out, so the server replays the first result instead of charging twice;
 * a success, a refusal (4xx) or {@link RETRY_WINDOW_MS} ends the action, and the next identical request is a new one.
 */
async function postMoney<T>(path: string, payload: unknown, held?: string, timeoutMs = MONEY_TIMEOUT_MS): Promise<T> {
  const body = JSON.stringify(payload);
  const action = `${path} ${body}`;
  const now = Date.now();
  const pending = pendingKeys.get(action);
  const key = pending && now - pending.at < RETRY_WINDOW_MS ? pending.key : (held ?? newKey());
  pendingKeys.set(action, { key, at: now });
  try {
    const r = await call<T>(path, { method: 'POST', body, headers: { 'Idempotency-Key': key } }, timeoutMs);
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

/** The top-up booked with an open or extend (for the receipt); `bonus` is 0 for a guest. */
export interface SessionPaid {
  transaction: Transaction;
  bonus: Money;
}

export interface SessionResult {
  session: Session;
  charged: Money;
  /** After the action; after an end it is the settled balance (negative — a debt). */
  balance?: Money;
  refunded?: Money;
  /** Whose session it is (absent from an older server). */
  user?: { id: string; displayName: string; role: string };
  payment?: SessionPaid | null;
  /** End of a walk-in guest's session: the cash that may be given back now (D-37); null for members. */
  payable?: Money | null;
}

export interface TopUpResult {
  balance: Money;
  transaction: Transaction;
  bonus?: Money;
}

export interface PayoutResult {
  balance: Money;
  /** What is still payable after this payout (normally 0). */
  payable: Money;
  transaction: Transaction;
}

/** How the client paid at the counter; the server books the top-up under it and splits the X / Z reports by it. */
export type PayMethod = 'cash' | 'card' | 'payme' | 'click' | 'uzum';

/** One row of the counter's client search (`GET /admin/clients/lookup`): the 8 best matches, recent clients when empty. */
export interface ClientHit {
  id: string;
  displayName: string;
  username: string;
  /** Last 4 digits of the phone; null when the client left none. */
  phoneTail: string | null;
  balance: Money;
  bonus: Money;
  cardId: string | null;
  /** The PC the client is playing on right now. */
  playing: { pcId: string; pcName: string } | null;
  /** In this club's blacklist: the club refuses them a session. */
  blacklisted?: boolean;
}

/** Money taken with opening or extending: the server tops up and opens in one transaction (a refusal books nothing). */
export interface SessionPayment {
  amount: number;
  method: PayMethod;
}

// ---------------------------------------------------------------------------------------------------------------------
// Cash desk part 3 (beyond the contract): the bar, moves, bulk commands
// ---------------------------------------------------------------------------------------------------------------------

/** How a bar sale was paid: a counter method, or the buyer's balance (D-52). */
export type SaleMethod = PayMethod | 'balance';

/**
 * A bar sale as the desk sends it (D-52): `saleId` is made once per cart and becomes the sale's id, so one cart is never
 * booked twice; `total` is what the desk shows (409 `priceChanged` when the server's differs). With `payment` (exactly
 * the total) a method sale — `userId` only names the buyer; without it a balance sale of `userId`.
 */
export interface SaleInput {
  saleId: string;
  items: { productId: string; qty: number }[];
  total: number;
  userId?: string;
  pcId?: string;
  payment?: { method: PayMethod; amount: number };
}

/** One line as sold: the title and the price of that moment (tiyin). */
export interface SaleLine {
  productId: string;
  title: string;
  qty: number;
  price: number;
  amount: number;
}

export interface Sale {
  id: string;
  at: string;
  shiftId: string;
  method: SaleMethod;
  total: number;
  staffName: string;
  user: { id: string; displayName: string; role: string } | null;
  pc: { id: string; name: string } | null;
  lines: SaleLine[];
}

export interface SaleResponse {
  sale: Sale;
  /** The buyer's balance after a balance sale; null for a method sale. */
  balance: Money | null;
  /** Each line's product after the sale. */
  products: Product[];
  expectedCash: number;
}

/** Why a sale is taken back (D-56): a defect does not go back on the shelf; «other» needs a note. */
export type VoidReason = 'mistake' | 'returned' | 'defect' | 'other';

export interface SaleVoid {
  id: string;
  at: string;
  saleId: string;
  saleAt: string;
  method: SaleMethod;
  total: number;
  reasonCode: VoidReason;
  note: string | null;
  staffName: string;
}

export interface SaleVoidResponse {
  void: SaleVoid;
  balance: Money | null;
  products: Product[];
  expectedCash: number;
}

/** The PC the desk sees the session on (`fromPcId`), the target, and an hourly tariff when the target's zone needs one. */
export interface MoveInput {
  fromPcId: string;
  sessionId?: string;
  toPcId: string;
  tariffId?: string;
}

export interface MoveResponse {
  session: Session;
  from: { pcId: string; name: string };
  to: { pcId: string; name: string };
  tariffChanged: boolean;
  user: { id: string; displayName: string; role: string };
  /** False: the player signs in on the target. */
  signedIn: boolean;
}

export type PcCommandKind = 'message' | 'lock' | 'unlock' | 'reboot' | 'shutdown';

/** A PC's answer to a command; `agentOffline` — not connected (queued), `timeout` — no answer in time. */
export interface CommandAck {
  ok: boolean;
  error?: { code: string; message: string } | null;
}

/** What one PC of a bulk command did (D-65). Amounts of `ended` are minor units, a `Money` from some servers. */
export interface BulkResult {
  pcId: string;
  pcName: string | null;
  outcome: 'done' | 'queued' | 'noAnswer' | 'failed' | 'skipped';
  skipped: 'sessionOpen' | 'offline' | 'notFound' | null;
  ack: CommandAck | null;
  ended: {
    sessionId: string;
    user: { id: string; displayName: string; role: string } | null;
    charged: Money | number | null;
    refunded: Money | number | null;
  } | null;
}

/**
 * One command to several PCs (D-65). `includeBusy` (reboot, shutdown) ends busy PCs' sessions first — only those in
 * `sessionIds`, the ones the confirm listed: a player who sat down after it is left alone (`skipped sessionOpen`).
 */
export interface BulkInput {
  pcIds: string[];
  kind: PcCommandKind;
  text?: string;
  level?: 'info' | 'warning';
  includeBusy?: boolean;
  sessionIds?: string[];
}

export interface BulkResponse {
  batchId: string;
  results: BulkResult[];
}

/** A bulk command may wait for every PC's answer (30 s) before it answers. */
const BULK_TIMEOUT_MS = 45_000;

export const adminApi = {
  overview: (): Promise<Overview> => call<Overview>('/admin/overview'),
  /**
   * A member's seat. `minutes` stays required by the contract: 60 for a package or postpaid, which the server ignores
   * there. `prepaid: false` — postpaid (no payment with it).
   */
  openSession: (
    input: {
      pcId: string;
      userId: string;
      tariffId: string;
      minutes: number;
      prepaid?: boolean;
      payment?: SessionPayment;
    },
    key?: string,
  ): Promise<SessionResult> => postMoney<SessionResult>('/admin/sessions', input, key),
  /**
   * A walk-in guest's seat (beyond the contract): the server creates the guest account with the session; the guest
   * presses «Гость» on that PC. Prepaid needs a payment of exactly the price; the key is required.
   */
  openGuestSession: (
    input: {
      pcId: string;
      tariffId: string;
      minutes: number;
      prepaid: boolean;
      displayName?: string;
      payment?: SessionPayment;
    },
    key: string,
  ): Promise<SessionResult> => postMoney<SessionResult>('/admin/sessions/guest', input, key),
  extend: (
    input: {
      pcId: string;
      minutes: number;
      tariffId?: string;
      payment?: SessionPayment;
    },
    key?: string,
  ): Promise<SessionResult> => postMoney<SessionResult>('/admin/sessions/extend', input, key),
  end: (input: { pcId: string }): Promise<SessionResult> => postMoney<SessionResult>('/admin/sessions/end', input),
  topUp: (
    input: {
      userId: string;
      amount: number;
      method: PayMethod;
    },
    key?: string,
  ): Promise<TopUpResult> => postMoney('/admin/wallet/topup', input, key),
  /** Takes a debt to the tiyin: `amount` must equal the debt (409 `debtChanged {debt}` otherwise); no bonus. */
  settle: (input: { userId: string; amount: number; method: PayMethod }, key: string): Promise<TopUpResult> =>
    postMoney('/admin/wallet/topup', { ...input, settleDebt: true }, key),
  /** Gives a walk-in guest's refund back in cash: `amount` must equal what is payable now (beyond the contract). */
  payout: (input: { userId: string; amount: number }, key: string): Promise<PayoutResult> =>
    postMoney('/admin/wallet/payout', { ...input, method: 'cash' }, key),
  command: (pcId: string, input: { kind: PcCommandKind; text?: string }): Promise<{ ack: CommandAck }> =>
    post(`/admin/pcs/${pcId}/command`, input),
  /** Name or login substring, any part of the phone digits, or the exact card; under 2 characters — recent clients. */
  lookupClients: (q: string): Promise<{ items: ClientHit[] }> =>
    call(`/admin/clients/lookup?q=${encodeURIComponent(q)}`),
  /** A bar sale (D-52); the key is required and never goes out with another body (D-70). */
  shopSale: (input: SaleInput, key: string): Promise<SaleResponse> => postMoney('/admin/shop/sales', input, key),
  /** Takes a bar sale back with a reason (D-56). */
  shopVoid: (id: string, input: { reasonCode: VoidReason; note?: string }, key: string): Promise<SaleVoidResponse> =>
    postMoney(`/admin/shop/sales/${id}/void`, input, key),
  /** «Пересадить»: the open session of `fromPcId` to `toPcId`, with its time and money (D-59). */
  moveSession: (input: MoveInput, key: string): Promise<MoveResponse> => postMoney('/admin/sessions/move', input, key),
  /**
   * «Иду»: answers the call and the older open calls of its PC; `notified` — the player got «Администратор идёт к вам»
   * (false: the PC is not connected); null — someone had answered it already, nothing was sent.
   */
  callAck: (id: string, notify = true): Promise<{ call: Call; notified: boolean | null }> =>
    post(`/admin/calls/${id}/ack`, { notify }),
  /** «Закрыть»: closes the call and the older ones of its PC. */
  callResolve: (id: string): Promise<{ call: Call }> => post(`/admin/calls/${id}/resolve`, {}),
  /** One command to several PCs, a result per PC (D-65); `includeBusy` ends busy PCs' sessions before a reboot. */
  pcCommands: (input: BulkInput, key: string): Promise<BulkResponse> =>
    postMoney('/admin/pcs/commands', input, key, BULK_TIMEOUT_MS),
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
  /** Top-ups by payment method (absent from an older server, which has only cash / other). */
  topUpByMethod?: Record<PayMethod | 'other', number>;
  sessions: number;
  shop: number;
  refunds: number;
  bonuses: number;
  count: number;
  /** Cash put into / taken out of the drawer (beyond the contract; absent from an older server and old Z reports). */
  cashIn?: number;
  cashOut?: number;
  /** Cash given back to walk-in guests. */
  payouts?: number;
  /** Cash top-ups booked with the club API key: not in the drawer, so not in the expected cash. */
  apiCash?: number;
  /**
   * The bar by method, net of the voids booked in the shift (D-57); `balance` may be negative when the shift voids an
   * earlier shift's balance sale. Absent from an older server and from a Z saved before cash desk part 3.
   */
  shopByMethod?: Record<SaleMethod, number>;
  /** What the shift's voids gave back, and how many there were. */
  shopVoids?: number;
  shopVoidCount?: number;
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
  /** What the drawer should have held at the close (the server's formula); null while open, absent from an older server. */
  expectedCash?: number | null;
  /** Who closed it. */
  closedBy?: string | null;
}

/** Cash expected in the drawer of a shift, the server's formula (D-41, D-57); for old rows without the server's figure. */
export function expectedOf(openingCash: number, x: ShiftTotals): number {
  return (
    openingCash +
    x.topUpCash -
    (x.apiCash ?? 0) +
    (x.cashIn ?? 0) -
    (x.cashOut ?? 0) -
    (x.payouts ?? 0) +
    (x.shopByMethod?.cash ?? 0)
  );
}

export type CashMoveKind = 'in' | 'out';
export type CashReason = 'change' | 'collection' | 'expenses' | 'other';

export interface CashMovement {
  id: string;
  kind: CashMoveKind;
  amount: number;
  reasonCode: CashReason;
  note: string | null;
  at: string;
  staffName: string;
}

export type OperationKind =
  | 'topUp'
  | 'debtPaid'
  | 'sessionOpen'
  | 'sessionExtend'
  | 'sessionEnd'
  | 'payout'
  | 'cashIn'
  | 'cashOut'
  | 'shiftOpen'
  | 'shiftClose'
  | 'promoRedeem'
  // Cash desk part 3 (D-68)
  | 'shopSale'
  | 'shopVoid'
  | 'sessionMove';

/** One row of the shift's operations feed: a paid open is one row with its payment merged in (D-43). */
export interface Operation {
  id: string;
  at: string;
  kind: OperationKind;
  staffName: string;
  client: { id: string; displayName: string; role: string } | null;
  pc: { id: string; name: string } | null;
  tariff: string | null;
  minutes: number | null;
  prepaid: boolean | null;
  amount: number;
  charged: number | null;
  quote: { base: number; dayPct: number; discountPct: number } | null;
  paid: { amount: number; method: string; transactionId: string | null } | null;
  /** Signed effect on the drawer (cash in +, cash out −). */
  drawer: number;
  reasonCode: string | null;
  note: string | null;
  sessionId: string | null;
  /** A seat or an extension sold a package (its time is not refunded); absent from an older server. */
  package?: boolean | null;
  /** A cash move's own id, the № of its slip (the entry's `id` is the journal's). */
  movementId?: string | null;
  /** A bar sale (of a void: the sale it took back), how it was paid and its lines as sold (cash desk part 3). */
  saleId?: string | null;
  method?: SaleMethod | null;
  lines?: { title: string; qty: number; price: number }[] | null;
  /** A bar sale taken back since. */
  voided?: boolean | null;
  /** A void: when the sale was. */
  voidOfAt?: string | null;
  /** A move: the PC the session came from (`pc` is where it went). */
  fromPc?: { id: string; name: string } | null;
}

/**
 * Money taken today (the club's local day): top-ups by method, and the bar's method sales net of voids (`shopByMethod`,
 * absent from an older server); `taken` is both, `taken − payouts` the headline; `shop` the goods sold either way.
 */
export interface Today {
  date: string;
  from: string;
  byMethod: Record<PayMethod | 'other', number>;
  taken: number;
  payouts: number;
  sessions: number;
  shop: number;
  shopByMethod?: Record<PayMethod, number>;
}

export interface OperationsPage {
  shift: { id: string; staffName: string; openedAt: string; closedAt: string | null; closedBy: string | null } | null;
  items: Operation[];
  next: string | null;
  today: Today;
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
    /**
     * How far below zero a member's postpaid session may run (tiyin); 0 or absent — no member debt: a member plays
     * postpaid only while the balance lasts (D-31).
     */
    memberDebtLimit?: number | null;
    /** Only the owner takes cash out of the drawer (D-40). */
    cashOutOwnerOnly?: boolean;
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
  | 'clientCard'
  // Beyond the contract enum (D-44): the Control page has their labels, the server does not send them there yet.
  | 'cashIn'
  | 'cashOut'
  | 'payout';

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
  /** Facts of the entry; a session's `quote` is an object. */
  meta: Record<string, unknown>;
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
  /** Why the tariff cannot be sold on this PC now (its zone, its time window); null — it can (absent: older server). */
  rule?: 'tariffZone' | 'tariffTime' | null;
  /** Minutes priced: the package's own for a package. */
  minutes?: number;
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

  /** `expectedCash` — what the open shift's drawer should hold now (absent from an older server). */
  shift: (): Promise<{ shift: Shift | null; x: ShiftTotals | null; history: Shift[]; expectedCash?: number | null }> =>
    call('/admin/shift'),
  openShift: (openingCash: number): Promise<{ shift: Shift }> => post('/admin/shift/open', { openingCash }),
  closeShift: (closingCash: number): Promise<{ shift: Shift; expectedCash: number }> =>
    postMoney('/admin/shift/close', { closingCash }),
  /** Cash put into or taken out of the drawer (beyond the contract); the key is required. */
  cashMove: (
    input: { kind: CashMoveKind; amount: number; reasonCode: CashReason; note?: string | null },
    key: string,
  ): Promise<{ movement: CashMovement; expectedCash: number }> => postMoney('/admin/shift/cash', input, key),
  /** The shift's journal, newest first (the open shift by default), and today's money by method. */
  operations: (
    q: { shiftId?: string | null; before?: string | null; kinds?: OperationKind[]; limit?: number } = {},
  ): Promise<OperationsPage> => {
    const params = new URLSearchParams();
    if (q.shiftId) params.set('shiftId', q.shiftId);
    if (q.before) params.set('before', q.before);
    if (q.kinds && q.kinds.length > 0) params.set('kinds', q.kinds.join(','));
    if (q.limit) params.set('limit', String(q.limit));
    const qs = params.toString();
    return call(`/admin/shift/operations${qs ? `?${qs}` : ''}`);
  },

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
  /**
   * Only the keys that changed (D-54): a `stockQty` goes with the quantity the panel read (`expectedStockQty`), so a bar
   * sale meanwhile is not overwritten (409 `stockChanged {stockQty}`).
   */
  updateProduct: (
    id: string,
    input: Partial<{
      title: string;
      price: number;
      inStock: boolean;
      stockQty: number | null;
      expectedStockQty: number | null;
    }>,
  ): Promise<unknown> => patch(`/admin/products/${id}`, input),
  /** A product the owner adds at the desk (beyond the contract, D-58); `stockQty` null — not tracked. */
  createProduct: (input: {
    title: string;
    category: string;
    price: number;
    stockQty: number | null;
    inStock?: boolean;
  }): Promise<{ product: Product }> => post('/admin/products', input),
  /** Archives a product: gone from every list; past sales keep their lines. */
  archiveProduct: (id: string): Promise<unknown> => del(`/admin/products/${id}`),
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
