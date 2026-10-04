/**
 * The shift's operations feed (`GET /admin/shift/operations`, beyond the contract, D-43), built from the staff journal
 * (`club().audit`) as the server builds it from `audit_entries`: newest first by (at, id), a keyset cursor
 * (base64url `at|id`), the feed's kinds only. A paid seat or extension is one row: its top-up entry is marked
 * `forSession` and left out, the session entry carries the payment. A debt payment is a top-up marked `debt`. Each row
 * says what it did to the drawer. `today` is the club's local day (Asia/Tashkent here; the server uses the club's time
 * zone) by payment method, with the cash given back to guests.
 */
import { isObject, db, errors, findPc, findUser } from './db.js';
import {
  PAYOUT_DESCRIPTION,
  club,
  openShift,
  topUpMethod,
  type AuditEntry,
  type PayMethod,
  type ShiftRecord,
  type StaffRecord,
} from './club.js';

const TIME_ZONE = 'Asia/Tashkent';
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export const OPERATION_KINDS = [
  'topUp',
  'debtPaid',
  'sessionOpen',
  'sessionExtend',
  'sessionEnd',
  'payout',
  'cashIn',
  'cashOut',
  'shiftOpen',
  'shiftClose',
  'promoRedeem',
  'shopSale',
  'shopVoid',
  'sessionMove',
] as const;
export type OperationKind = (typeof OPERATION_KINDS)[number];

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
  drawer: number;
  reasonCode: string | null;
  note: string | null;
  sessionId: string | null;
  /** A seat or an extension sold a package (null for an entry that does not say, and for other kinds). */
  package: boolean | null;
  /** A cash move's own id (the № of its slip). */
  movementId: string | null;
  /** Cash desk part 3 (D-68): the bar sale (of a void: the sale it voided), how it was paid, its lines. */
  saleId?: string | null;
  method?: string | null;
  lines?: { title: string; qty: number; price: number }[] | null;
  /** A sale that was voided since. */
  voided?: boolean | null;
  /** A void: when the sale was. */
  voidOfAt?: string | null;
  /** A move: the PC the session came from (`pc` is where it went). */
  fromPc?: { id: string; name: string } | null;
}

/** The journal entry as a feed row; null for what the feed does not show. */
function toOperation(e: AuditEntry): Operation | null {
  const m = e.meta;
  const s = (k: string): string | null => (typeof m[k] === 'string' ? (m[k] as string) : null);
  const n = (k: string): number | null => (typeof m[k] === 'number' ? (m[k] as number) : null);
  let kind: OperationKind;
  if (e.action === 'topUp') {
    if (m['forSession'] === true) return null;
    kind = m['debt'] === true ? 'debtPaid' : 'topUp';
  } else if ((OPERATION_KINDS as readonly string[]).includes(e.action)) {
    kind = e.action as OperationKind;
  } else {
    return null;
  }
  const user = e.userId ? findUser(e.userId) : undefined;
  const pc = e.pcId ? findPc(e.pcId) : undefined;
  const q = m['quote'];
  let paid: Operation['paid'] = null;
  let drawer = 0;
  if (kind === 'topUp' || kind === 'debtPaid') {
    const method = s('method') ?? 'cash';
    paid = { amount: e.amount, method, transactionId: s('transactionId') };
    // A top-up with the club API key is booked as cash but never reached the drawer.
    if (method === 'cash' && m['api'] !== true) drawer = e.amount;
  } else if (kind === 'sessionOpen' || kind === 'sessionExtend') {
    const amount = n('paidAmount');
    if (amount !== null) {
      paid = { amount, method: s('paidMethod') ?? 'cash', transactionId: s('transactionId') };
      if (paid.method === 'cash') drawer = amount;
    }
  } else if (kind === 'payout' || kind === 'cashOut') {
    drawer = -e.amount;
  } else if (kind === 'cashIn' || kind === 'shiftOpen') {
    // The opening float is the drawer's first money: a shift's rows add up to its expected cash.
    drawer = e.amount;
  }
  let charged: number | null =
    kind === 'sessionOpen' || kind === 'sessionExtend' ? e.amount : kind === 'sessionEnd' ? n('charged') : null;
  const sale = kind === 'shopSale' || kind === 'shopVoid' ? db.shopSales.find((x) => x.id === s('saleId')) : undefined;
  const method = s('method');
  if (kind === 'shopSale') {
    // A method sale's money as a payment; a balance sale is what the balance paid.
    if (method && method !== 'balance') paid = { amount: e.amount, method, transactionId: null };
    else charged = e.amount;
    if (method === 'cash') drawer = e.amount;
  } else if (kind === 'shopVoid') {
    if (method && method !== 'balance') paid = { amount: e.amount, method, transactionId: null };
    if (method === 'cash') drawer = -e.amount;
  }
  const from = kind === 'sessionMove' ? findPc(s('fromPcId') ?? '') : undefined;
  return {
    id: e.id,
    at: e.at,
    kind,
    staffName: e.staffName,
    client: user ? { id: user.id, displayName: user.displayName, role: user.role } : null,
    pc: pc ? { id: pc.id, name: pc.name } : null,
    tariff: s('tariff'),
    minutes: n('minutes'),
    prepaid: typeof m['prepaid'] === 'boolean' ? m['prepaid'] : null,
    amount: e.amount,
    charged,
    quote:
      isObject(q) && typeof q['base'] === 'number'
        ? { base: q['base'], dayPct: Number(q['dayPct'] ?? 100), discountPct: Number(q['discountPct'] ?? 0) }
        : null,
    paid,
    drawer,
    reasonCode: s('reasonCode'),
    note: s('note'),
    sessionId: s('sessionId'),
    package:
      (kind === 'sessionOpen' || kind === 'sessionExtend') && typeof m['package'] === 'boolean' ? m['package'] : null,
    movementId: s('movementId'),
    ...(kind === 'shopSale' || kind === 'shopVoid'
      ? {
          saleId: s('saleId'),
          method,
          lines: sale ? sale.lines.map((l) => ({ title: l.title, qty: l.qty, price: l.price })) : null,
          voided: kind === 'shopSale' ? db.shopSales.some((x) => x.kind === 'void' && x.voidOf === s('saleId')) : null,
          voidOfAt: kind === 'shopVoid' ? s('saleAt') : null,
        }
      : {}),
    ...(kind === 'sessionMove'
      ? { fromPc: from ? { id: from.id, name: from.name } : { id: s('fromPcId') ?? '', name: s('fromPc') ?? '' } }
      : {}),
  };
}

const encode = (at: string, id: string): string => Buffer.from(`${at}|${id}`).toString('base64url');

function decode(cursor: string): { at: string; id: string } {
  const [at, id] = Buffer.from(cursor, 'base64url').toString('utf8').split('|');
  if (!at || !id || Number.isNaN(Date.parse(at))) throw errors.validation('before', 'format');
  return { at, id };
}

/** Milliseconds `TIME_ZONE` is ahead of UTC at `at`. */
function offsetMs(at: Date): number {
  const p = Object.fromEntries(
    new Intl.DateTimeFormat('en-US', {
      timeZone: TIME_ZONE,
      hourCycle: 'h23',
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    })
      .formatToParts(at)
      .map((x) => [x.type, x.value]),
  );
  const local = Date.UTC(+p['year']!, +p['month']! - 1, +p['day']!, +p['hour']!, +p['minute']!, +p['second']!);
  return local - Math.floor(at.getTime() / 1000) * 1000;
}

/**
 * Money of the club's local day so far, by method, minus the cash given back to guests in the headline. `byMethod` is
 * top-ups only; the bar's method sales net of their voids are `shopByMethod` and in `taken`; `shop` is the goods sold
 * both ways (D-57).
 */
function today(): {
  date: string;
  from: string;
  byMethod: Record<PayMethod | 'other', number>;
  taken: number;
  payouts: number;
  sessions: number;
  shop: number;
  shopByMethod: Record<PayMethod, number>;
} {
  const at = new Date();
  const offset = offsetMs(at);
  const date = new Date(at.getTime() + offset).toISOString().slice(0, 10);
  const from = new Date(Date.parse(`${date}T00:00:00Z`) - offset).toISOString();
  const byMethod: Record<PayMethod | 'other', number> = { cash: 0, card: 0, payme: 0, click: 0, uzum: 0, other: 0 };
  let taken = 0;
  let payouts = 0;
  let sessions = 0;
  let shop = 0;
  for (const tx of db.transactions) {
    if (tx.createdAt < from) continue;
    const a = tx.amount.amount;
    if (tx.type === 'topUp') {
      byMethod[topUpMethod(tx.description)] += a;
      taken += a;
    } else if (tx.type === 'adjustment' && tx.description === PAYOUT_DESCRIPTION) payouts += -a;
    // Time sold net of what was given back, as the server counts it (charges − refunds).
    else if (tx.type === 'charge' || tx.type === 'refund') sessions += -a;
    else if (tx.type === 'purchase') shop += -a;
  }
  const shopByMethod: Record<PayMethod, number> = { cash: 0, card: 0, payme: 0, click: 0, uzum: 0 };
  for (const sale of db.shopSales) {
    if (sale.createdAt < from || sale.method === 'balance') continue;
    const signed = sale.kind === 'sale' ? sale.total : -sale.total;
    shopByMethod[sale.method] += signed;
    taken += signed;
    shop += signed;
  }
  return { date, from, byMethod, taken, payouts, sessions, shop, shopByMethod };
}

/**
 * One page of the feed. `shiftId` — the open shift by default; a cashier may read only the open and the last closed
 * one (403 `ownerOnly`), an unknown shift is 404. `limit` 1..200 (50), `kinds` a comma list of {@link OPERATION_KINDS}.
 */
export function operationsPage(
  me: StaffRecord,
  q: { shiftId?: string; before?: string; limit?: string; kinds?: string },
): {
  shift: { id: string; staffName: string; openedAt: string; closedAt: string | null; closedBy: string | null } | null;
  items: Operation[];
  next: string | null;
  today: ReturnType<typeof today>;
} {
  // The server's answers: digits only (`format`), then 1..200 (`min` / `max`); a shift id that is no UUID is `format`.
  if (q.limit !== undefined && q.limit !== '' && !/^\d+$/.test(q.limit)) throw errors.validation('limit', 'format');
  const limit = q.limit === undefined || q.limit === '' ? 50 : Number(q.limit);
  if (limit < 1) throw errors.validation('limit', 'min');
  if (limit > 200) throw errors.validation('limit', 'max');
  if (q.shiftId && !UUID.test(q.shiftId)) throw errors.validation('shiftId', 'format');
  let kinds: Set<string> | null = null;
  if (q.kinds) {
    const list = q.kinds
      .split(',')
      .map((k) => k.trim())
      .filter(Boolean);
    if (list.some((k) => !(OPERATION_KINDS as readonly string[]).includes(k))) throw errors.validation('kinds', 'enum');
    kinds = new Set(list);
  }
  const cursor = q.before ? decode(q.before) : null;
  const c = club();
  let shift: ShiftRecord | null;
  if (q.shiftId) {
    shift = c.shifts.find((s) => s.id === q.shiftId) ?? null;
    if (!shift) throw errors.notFound('shift');
    const lastClosed = c.shifts.filter((s) => s.closedAt !== null).at(-1);
    if (me.role !== 'owner' && shift.closedAt !== null && shift.id !== lastClosed?.id)
      throw errors.forbidden('ownerOnly');
  } else {
    shift = openShift();
  }
  const rows = shift
    ? c.audit
        .filter((e) => e.shiftId === shift.id)
        .map(toOperation)
        .filter((o): o is Operation => o !== null && (kinds === null || kinds.has(o.kind)))
        .filter((o) => !cursor || o.at < cursor.at || (o.at === cursor.at && o.id < cursor.id))
        .sort((a, b) => b.at.localeCompare(a.at) || b.id.localeCompare(a.id))
    : [];
  const items = rows.slice(0, limit);
  const last = items.at(-1);
  return {
    shift: shift
      ? {
          id: shift.id,
          staffName: shift.staffName,
          openedAt: shift.openedAt,
          closedAt: shift.closedAt,
          closedBy: shift.closedBy ?? null,
        }
      : null,
    items,
    next: rows.length > limit && last ? encode(last.at, last.id) : null,
    today: today(),
  };
}
