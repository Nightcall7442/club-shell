/**
 * Cashier/administrator surface (`/api/v1/admin/*`) — the club-side counterpart of the kiosk API, used by
 * `apps/admin`. The central server product is out of scope for this repository (`COMPETITORS.md` §4), so these
 * routes exist to make the demo whole: open time on a seat, top up a wallet, extend or end someone's session and
 * push the existing `ServerCommand`s to a PC.
 *
 * Auth is a static bearer token (`MOCK_ADMIN_TOKEN`, default `admin-dev-token`) rather than a staff login: the
 * real console authenticates against the operator's own server. These routes are exempt from the agent Bearer /
 * HMAC gate (`isExempt` in `index.ts`). The money routes honour `Idempotency-Key` ({@link idempotent}): the console
 * sends one per cashier action and reuses it when the cashier retries after a lost answer; the routes beyond the
 * contract (a walk-in guest's seat, a payout) require one. Taking money (a top-up, opening or extending a session)
 * needs an open cash shift: 409 `conflict` / `shiftClosed` otherwise, checked after the body as the server does; ending
 * a session only settles the balance and does not. Opening and extending take an optional `payment {amount, method}`:
 * the top-up and the session in one step, every refusal checked before either, and the journal written last.
 *
 * Cash desk part 2, as on the server: a seat may be postpaid (`prepaid: false`; guests unless `limits.guestPostpaid` is
 * false, members always; a positive `guestDebtLimit` / `memberDebtLimit` caps it); a walk-in guest is seated with
 * `POST /admin/sessions/guest` and signs in on that PC with the seat's code (D-96); a desk open signs out anyone else on the PC
 * (`seatTaken`), a desk end signs the player out (`sessionEnded`); a debt is taken exactly (`settleDebt`) and a guest's
 * refund given back in cash (`/admin/wallet/payout`), never more than the guest paid in cash and got back.
 */
import type { FastifyInstance, FastifyRequest } from 'fastify';
import type { CommandAck, Money, Session, Tariff, Transaction } from '@clubshell/contracts';
import {
  ApiError,
  applyTransaction,
  balanceOf,
  body,
  db,
  endedView,
  errors,
  findGame,
  findPc,
  findSession,
  findTariff,
  findUser,
  idempotent,
  int,
  isObject,
  markDirty,
  now,
  openSessionForPc,
  openSessionForUser,
  optBool,
  optStr,
  othersSignedInOn,
  publicPc,
  replayGuestCode,
  revokeUserTokens,
  setGuestCode,
  settleStatus,
  signedInOn,
  str,
  timeFunds,
  uuid,
  uzs,
  viewSession,
  addPurchase,
  zero,
  type PcRecord,
  type SessionRecord,
  type UserRecord,
} from '../db.js';
import { hallPlan } from '../hall.js';
import { endSession } from './session.js';
import { createGuest } from './auth.js';
import { isApiKey, requireStaff, topUpWithBonus } from './club.js';
import {
  OWNER_TRANSACTIONS,
  PAYOUT_DESCRIPTION,
  TOPUP_CANCEL_DESCRIPTION,
  counterMethod,
  WRITE_OFF_DESCRIPTION,
  budgetOf,
  minutesFor,
  mayWriteOff,
  PAY_METHODS,
  club,
  clubHooks,
  drawerNow,
  inCurfew,
  isMinor,
  openShift,
  profileOf,
  quote,
  tariffRule,
  topUpMethod,
  topupBonus,
  type PayMethod,
  type PriceQuote,
  type StaffRecord,
} from '../club.js';
import { record } from '../control.js';
import { openTicketMarks } from '../health.js';
import { broadcast, isConnected, pendingCommands, pushToPc, pushToUser, sendCommand } from '../ws.js';
import { callView, liveCalls, moveCalls } from '../calls.js';

const STAFF_NAME = 'Администратор';

/** A cashier takes back only their own desk top-up, this many minutes after it; the owner any of the open shift (D-91). */
const CANCEL_WINDOW_MIN = 15;
const CANCEL_REASONS = ['wrongAmount', 'wrongClient', 'other'];

function requireAdmin(req: FastifyRequest): StaffRecord {
  return requireStaff(req);
}

/** One row of the hall map: the PC, who is on it, how much time is left, and whether that player has signed in. */
interface SeatView {
  pc: ReturnType<typeof publicPc>;
  session: Session | null;
  user: { id: string; displayName: string; role: string; balance: Money; bonus: Money } | null;
  signedIn: boolean | null;
  game: SeatGame | null;
  place: { x: number; y: number; rotation: number };
}

/** The game on a busy seat (D-71): the catalog title and art; an empty cover is null. */
interface SeatGame {
  id: string;
  title: string;
  coverUrl: string | null;
  heroUrl: string | null;
}

function seatGame(id: string | undefined): SeatGame | null {
  const g = id ? findGame(id) : undefined;
  return g ? { id: g.id, title: g.title, coverUrl: g.coverUrl || null, heroUrl: g.heroUrl || null } : null;
}

/**
 * What the seat shows as being played (D-71), as the server derives it: only on a busy or locked PC, the newest game its
 * Agent reports in the last heartbeat for this very session (its id, or the PC's own id of an offline session).
 *
 * Mock only: the seeded demo hall has no Agent, so its desk sessions get a demo game picked from the catalog by the
 * session id (stable across polls) once their player has signed in (nobody plays before that: «ждёт входа»), and the
 * dev desk looks like the F design. The real server never invents one; an E2E test that seats a seeded (agentless) PC,
 * signs its player in and expects no game would see this demo game.
 */
function gameOf(pc: PcRecord, rec: SessionRecord | undefined): SeatGame | null {
  if (!rec || (pc.status !== 'busy' && pc.status !== 'locked')) return null;
  if (!pc.registered) {
    if (!signedInOn(rec.userId, pc.id)) return null;
    const art = db.games.filter((g) => g.coverUrl.includes('/mock-art/'));
    if (art.length === 0) return null;
    let hash = 0;
    for (const ch of rec.id) hash = (hash * 31 + ch.charCodeAt(0)) >>> 0;
    return seatGame(art[hash % art.length]?.id);
  }
  if (!pc.reportedSessionId || (pc.reportedSessionId !== rec.id && pc.reportedSessionId !== rec.clientSessionId))
    return null;
  const newest = [...(pc.runningGames ?? [])].sort((a, b) => Date.parse(b.startedAt) - Date.parse(a.startedAt))[0];
  return seatGame(newest?.gameId);
}

function seatOf(pc: PcRecord): SeatView {
  const rec = openSessionForPc(pc.id);
  const session = rec ? viewSession(rec) : null;
  const user = rec ? findUser(rec.userId) : undefined;
  return {
    pc: publicPc(pc),
    session,
    // D-106: the bonus account apart (beyond the contract).
    user: user
      ? { id: user.id, displayName: user.displayName, role: user.role, balance: user.balance, bonus: user.bonus }
      : null,
    // A desk session nobody has signed in to yet: its clock already runs (D-49).
    signedIn: rec ? signedInOn(rec.userId, pc.id) : null,
    game: gameOf(pc, rec),
    place: { x: pc.x, y: pc.y, rotation: pc.rotation ?? 0 },
  };
}

function userView(u: UserRecord): {
  id: string;
  displayName: string;
  username: string;
  role: string;
  balance: Money;
  bonus: Money;
} {
  return {
    id: u.id,
    displayName: u.displayName,
    username: u.username,
    role: u.role,
    balance: u.balance,
    bonus: u.bonus,
  };
}

/**
 * Money is taken only in an open shift, so the X / Z reports account for every сум — except the owner's own money
 * (D-101, `owner`), which joins no shift.
 */
function requireShift(owner = false): void {
  if (!owner && !openShift()) throw errors.conflict('shiftClosed');
}

/** The owner at the desk in person (not the club API key, which acts as the owner): an owner operation (D-101). */
function ownerOp(req: FastifyRequest, staff: StaffRecord): boolean {
  return !isApiKey(req) && staff.role === 'owner';
}

/**
 * Runs `book` and, for an owner operation, marks every ledger row it wrote as the owner's (a charge may write two: the
 * bonus part and the main part, D-106) — as the server marks the charge of the owner's seat or extension `owner_op`.
 */
function asOwner<T>(owner: boolean, book: () => T): T {
  const before = db.transactions.length;
  const out = book();
  if (owner)
    for (const row of db.transactions.slice(0, db.transactions.length - before)) OWNER_TRANSACTIONS.add(row.id);
  return out;
}

/** The journal fields of an owner operation: no shift, marked `owner`; nothing otherwise. */
function ownerFields(owner: boolean): { shiftId?: null; owner?: true } {
  return owner ? { shiftId: null, owner: true } : {};
}

interface Payment {
  amount: number;
  method: PayMethod;
}

/** A counter payment's method, else 400 `enum` (the server's reason). */
function payMethod(v: unknown, field: string): PayMethod {
  if (typeof v !== 'string' || !(PAY_METHODS as readonly string[]).includes(v)) throw errors.validation(field, 'enum');
  return v as PayMethod;
}

/** `payment` of an open or extend (method required), else none. */
function paymentOf(b: Record<string, unknown>): Payment | null {
  const p = b['payment'];
  if (p === undefined || p === null) return null;
  if (!isObject(p)) throw errors.validation('payment', 'type');
  const amount = p['amount'];
  if (amount === undefined || amount === null) throw errors.validation('payment.amount', 'required');
  if (typeof amount !== 'number' || !Number.isInteger(amount)) throw errors.validation('payment.amount', 'type');
  if (amount < 1) throw errors.validation('payment.amount', 'min');
  if (amount > 100_000_000) throw errors.validation('payment.amount', 'max');
  if (p['method'] === undefined || p['method'] === null) throw errors.validation('payment.method', 'required');
  return { amount, method: payMethod(p['method'], 'payment.method') };
}

/** Funds for `cost` once `payment` (and the bonus it would earn) is in, else 402 before anything is booked. */
function checkFunds(user: UserRecord, payment: Payment | null, cost: Money): void {
  const debt = Math.max(0, -user.balance.amount);
  const bonus = payment && !user.transient ? topupBonus(Math.max(0, payment.amount - debt)) : 0;
  const incoming = payment ? payment.amount + bonus : 0;
  // Play time: the bonus account pays first (D-106).
  if (timeFunds(user) + incoming < cost.amount) throw errors.insufficientFunds(cost, uzs(timeFunds(user) + incoming));
}

/**
 * Books the payment of a seat or an extension as a counter top-up; its journal entry is `forSession` (the feed shows
 * the session's row with the payment merged in, D-43).
 */
function bookPayment(
  staff: StaffRecord,
  user: UserRecord,
  payment: Payment | null,
  owner: boolean,
): { transaction: Transaction; bonus: number } | null {
  if (!payment) return null;
  const { bonus, transaction } = topUpWithBonus(user, payment.amount, payment.method, { owner });
  record(staff, 'topUp', {
    ...ownerFields(owner),
    userId: user.id,
    amount: payment.amount,
    detail: user.displayName,
    meta: { method: payment.method, bonus, forSession: true, transactionId: transaction.id },
  });
  return { transaction, bonus };
}

/** The session the cashier is acting on, by session id or by seat. */
function targetSession(b: Record<string, unknown>): SessionRecord {
  const sessionId = optStr(b, 'sessionId', 64);
  const rec = sessionId ? findSession(sessionId) : openSessionForPc(str(b, 'pcId', 64));
  if (!rec || rec.state === 'ended' || rec.state === 'idle') throw errors.notFound('session');
  return rec;
}

/** `prepaid` of an open: true when absent. */
function prepaidOf(b: Record<string, unknown>): boolean {
  return optBool(b, 'prepaid') ?? true;
}

/** Desk postpaid for guests: on unless the club turned it off (absent = on, the server's default). */
const guestPostpaidOn = (): boolean => club().limits.guestPostpaid ?? true;

/**
 * A desk postpaid seat under a debt cap (the server's Rule 10): the first minute must fit within the balance plus
 * `limits.guestDebtLimit` (guests) or `limits.memberDebtLimit` (members). `null` or 0 is no cap at all: desk postpaid
 * works by default for both. The caps are the desk's only.
 */
function checkPostpaid(user: UserRecord, tariff: Tariff, zone: string): void {
  const limits = club().limits;
  const cap = user.role === 'guest' ? (limits.guestDebtLimit ?? 0) : (limits.memberDebtLimit ?? 0);
  const limit = cap > 0 ? cap : null;
  if (limit === null) return;
  const first = quote(tariff, 1, user.role === 'guest' ? null : user.id, zone).total;
  if (first.amount > timeFunds(user) + limit) throw errors.insufficientFunds(first, uzs(timeFunds(user)));
}

/** What a seat of `tariff` is checked for before anything is booked: the PC, the tariff's zone and hours. */
function checkSeat(pc: PcRecord, tariff: Tariff, prepaid: boolean): void {
  if (pc.status === 'maintenance') throw errors.policyDenied('pcMaintenance');
  const busy = openSessionForPc(pc.id);
  if (busy)
    throw new ApiError('sessionAlreadyActive', 'PC already has an open session', { sessionId: busy.id, pcId: pc.id });
  const rule = tariffRule(tariff, pc.zone);
  if (rule) throw errors.policyDenied(rule);
  // A package is sold prepaid only (D-38); the kiosk already forces it.
  if (!prepaid && tariff.isPackage) throw errors.validation('prepaid', 'package');
}

const quoteMeta = (q: PriceQuote): Record<string, number> => ({
  base: q.base.amount,
  dayPct: q.dayPct,
  discountPct: q.discountPct,
});

/**
 * Seats `user` on `pc` once every check passed: books the payment, charges the price (prepaid), signs out anyone else
 * on the PC (`seatTaken`, before the session push), journals the seat with its payment and price.
 */
function seat(o: {
  staff: StaffRecord;
  pc: PcRecord;
  user: UserRecord;
  tariff: Tariff;
  mins: number;
  prepaid: boolean;
  payment: Payment | null;
  priced: PriceQuote;
  guest: boolean;
  /** The owner's own seat (D-101): its payment and journal entry join no shift. */
  owner: boolean;
}): { status: number; body: unknown; stored: unknown } {
  const { staff, pc, user, tariff, mins, prepaid, payment, priced } = o;
  const cost = prepaid ? priced.total : zero();
  const paid = bookPayment(staff, user, payment, o.owner);
  const id = uuid();
  if (cost.amount > 0)
    asOwner(o.owner, () =>
      applyTransaction(user, 'charge', uzs(-cost.amount), `Сеанс ${mins} мин · ${tariff.name}`, id),
    );
  const startedAt = now();
  const rec: SessionRecord = {
    id,
    userId: user.id,
    pcId: pc.id,
    tariffId: tariff.id,
    state: 'active',
    startedAt,
    isPrepaid: prepaid,
    purchasedSec: prepaid ? mins * 60 : 0,
    paidAmount: cost,
    usedBeforeSec: 0,
    runningSince: startedAt,
    pausedAt: null,
    endedAt: null,
    endReason: null,
    warningsSent: [],
    lastPushAt: Date.now(),
    origin: 'cashier',
    createdByStaffId: staff.id,
    purchases: prepaid ? [{ paid: cost.amount, sec: mins * 60, pkg: tariff.isPackage }] : [],
  };
  // No code (D-98): the PC signs the desk's guest in at once.
  const guestCode: string | null = null;
  db.sessions.push(rec);
  settleStatus(pc);
  // Whoever was signed in on this PC must not get this session under their name (D-29).
  for (const other of othersSignedInOn(pc.id, user.id)) {
    revokeUserTokens(other, pc.id);
    pushToPc(pc.id, 'userRevoked', { userId: other, reason: 'seatTaken' });
  }
  const session = viewSession(rec);
  pushToPc(pc.id, 'sessionUpdated', session);
  broadcast('pcStatusChanged', { pcId: pc.id, status: pc.status });
  pushToUser(user.id, 'walletUpdated', balanceOf(user));
  clubHooks.sessionOpened(rec);
  record(staff, 'sessionOpen', {
    ...ownerFields(o.owner),
    userId: user.id,
    pcId: pc.id,
    amount: cost.amount,
    detail: `${user.displayName} · ${pc.name} · ${tariff.name}`,
    meta: {
      minutes: prepaid ? mins : 0,
      prepaid,
      tariff: tariff.name,
      package: tariff.isPackage,
      discountPct: priced.discountPct,
      sessionId: id,
      quote: quoteMeta(priced),
      ...(payment && paid
        ? { paidAmount: payment.amount, paidMethod: payment.method, transactionId: paid.transaction.id }
        : {}),
      ...(o.guest ? { guest: true } : {}),
    },
  });
  const answer = {
    session,
    charged: cost,
    balance: user.balance,
    user: { id: user.id, displayName: user.displayName, role: user.role },
    // As the server: a key without a value is left out.
    ...(paid ? { payment: { transaction: paid.transaction, bonus: uzs(paid.bonus) } } : {}),
  };
  return {
    status: 201,
    // The code is in this answer only, never in the stored one (a replay answers a new code).
    body: guestCode ? { ...answer, guestCode } : answer,
    stored: answer,
  };
}

/**
 * Cash that may go back to a walk-in guest now (D-37): no more than the balance, than what desk ends refunded, and than
 * what the guest paid in cash — each less what was already given back. A card payer and bonus money get none.
 */
function payableOf(user: UserRecord): number {
  let refunds = 0;
  let cash = 0;
  let payouts = 0;
  for (const tx of db.transactions) {
    if (tx.userId !== user.id) continue;
    if (tx.type === 'refund' && tx.bonus !== true) refunds += tx.amount.amount;
    else if (tx.type === 'topUp' && topUpMethod(tx.description) === 'cash') cash += tx.amount.amount;
    else if (tx.type === 'adjustment' && tx.description === PAYOUT_DESCRIPTION) payouts += -tx.amount.amount;
  }
  return Math.max(0, Math.min(user.balance.amount, refunds - payouts, cash - payouts));
}

/** The PC and the end of a player's last session in the club (for the settle list). */
function lastSessionOf(userId: string): { pc: string | null; pcId: string | null; endedAt: string | null } | null {
  let last: SessionRecord | undefined;
  for (const s of db.sessions) {
    if (s.userId === userId && (!last || s.startedAt > last.startedAt)) last = s;
  }
  if (!last) return null;
  return { pc: findPc(last.pcId)?.name ?? null, pcId: last.pcId, endedAt: last.endedAt };
}

/** Newest end first, the ones still open (no end) on top, as the server orders them. */
function byEnd(a: { endedAt: string | null }, b: { endedAt: string | null }): number {
  if (a.endedAt === b.endedAt) return 0;
  if (a.endedAt === null) return -1;
  if (b.endedAt === null) return 1;
  return b.endedAt.localeCompare(a.endedAt);
}

export function adminRoutes(app: FastifyInstance): void {
  /**
   * Hall map + tariffs + staff-visible users, i.e. everything the cashier screen renders; plus what is left to settle:
   * negative balances of guests and members who played here (`guestDebts`), walk-in guests with money on the account
   * and what of it may go back in cash (`guestRefunds`).
   */
  app.get('/admin/overview', async (req) => {
    requireAdmin(req);
    const seats = db.pcs.map(seatOf);
    const debts = db.users
      .filter((u) => (u.role === 'guest' || !u.transient) && u.role !== 'admin' && u.balance.amount < 0)
      .flatMap((u) => {
        const last = lastSessionOf(u.id);
        return last
          ? [
              {
                userId: u.id,
                displayName: u.displayName,
                debt: uzs(-u.balance.amount),
                pc: last.pc,
                endedAt: last.endedAt,
                role: u.role,
              },
            ]
          : [];
      })
      .sort(byEnd)
      .slice(0, 100);
    const refunds = db.users
      .filter((u) => u.transient && u.balance.amount > 0 && !openSessionForUser(u.id))
      .flatMap((u) => {
        const last = lastSessionOf(u.id);
        return last
          ? [
              {
                userId: u.id,
                displayName: u.displayName,
                balance: u.balance,
                payable: uzs(payableOf(u)),
                pc: last.pc,
                endedAt: last.endedAt,
              },
            ]
          : [];
      })
      .sort(byEnd)
      .slice(0, 100);
    return {
      at: now(),
      club: { free: seats.filter((s) => s.pc.status === 'free').length, total: seats.length },
      seats,
      tariffs: db.tariffs,
      users: db.users.filter((u) => !u.transient && u.role !== 'admin' && !profileOf(u.id).blacklisted).map(userView),
      zones: club().zones,
      repairs: openTicketMarks(),
      guestDebts: debts,
      guestRefunds: refunds,
      hallPlan: hallPlan(),
      // Cash desk part 3: the players' calls the desk has not closed (D-63).
      calls: liveCalls(),
    };
  });

  /**
   * Opens a session on a seat for an existing member — the "add time" of a cashier. Prepaid by default; `prepaid:
   * false` is postpaid (no payment with it, no package). `minutes` stays required (the console sends 60 where it is
   * ignored: a package, postpaid).
   */
  app.post('/admin/sessions', async (req, reply) => {
    const staff = requireAdmin(req);
    return idempotent(req, reply, async () => {
      const b = body(req);
      const pcId = str(b, 'pcId', 64);
      const userId = str(b, 'userId', 64);
      const minutes = int(b, 'minutes', 5, 1440);
      const prepaid = prepaidOf(b);
      const payment = paymentOf(b);
      if (!prepaid && payment) throw errors.validation('payment', 'postpaid');
      const amount = budgetOf(b, prepaid);
      const pc = findPc(pcId);
      const user = findUser(userId);
      const tariff = findTariff(str(b, 'tariffId', 64));
      if (!pc) throw errors.notFound('pc');
      const owner = ownerOp(req, staff);
      requireShift(owner);
      if (!user) throw errors.notFound('user');
      if (!tariff) throw errors.notFound('tariff');
      checkSeat(pc, tariff, prepaid);
      const mine = openSessionForUser(userId);
      if (mine)
        throw new ApiError('sessionAlreadyActive', 'User already has an open session', {
          sessionId: mine.id,
          pcId: mine.pcId,
        });
      if (profileOf(userId).blacklisted) throw errors.policyDenied('blacklisted');
      if (isMinor(userId) && inCurfew()) throw errors.policyDenied('minorCurfew');
      if (!prepaid && user.role === 'guest' && !guestPostpaidOn()) throw errors.policyDenied('postpaidNotAllowed');
      // D-90: «по сумме» the server sells the minutes the amount buys, whatever `minutes` says.
      const byAmount = amount === null ? null : minutesFor(tariff, amount, userId, pc.zone);
      const mins = byAmount?.minutes ?? (tariff.isPackage ? (tariff.packageMinutes ?? minutes) : prepaid ? minutes : 0);
      const priced = byAmount?.priced ?? quote(tariff, prepaid ? mins : 60, userId, pc.zone);
      if (prepaid) checkFunds(user, payment, priced.total);
      else checkPostpaid(user, tariff, pc.zone);
      return seat({ staff, pc, user, tariff, mins, prepaid, payment, priced, guest: false, owner });
    });
  });

  /**
   * A walk-in guest's seat (beyond the contract, D-24): the transient guest account and its session in one step, the
   * account made only after every check passed. Prepaid takes exactly the price now (409 `priceChanged {total}`, D-48),
   * postpaid follows `limits.guestPostpaid` / `guestDebtLimit`. The answer carries the seat's four-digit code once
   * (`guestCode`, D-96): the guest types it on that PC to sign in.
   */
  app.post('/admin/sessions/guest', async (req, reply) => {
    const staff = requireAdmin(req);
    // Before the key check, as the server: the club API key is refused whatever it sends.
    if (isApiKey(req)) throw errors.forbidden('staffOnly');
    return idempotent(
      req,
      reply,
      async () => {
        const b = body(req);
        const pcId = str(b, 'pcId', 64);
        const tariffId = str(b, 'tariffId', 64);
        const minutes = int(b, 'minutes', 5, 1440);
        const prepaid = prepaidOf(b);
        const name = optStr(b, 'displayName', 200)?.trim() || null;
        if (name && name.length > 32) throw errors.validation('displayName', 'max');
        const payment = paymentOf(b);
        if (prepaid && !payment) throw errors.validation('payment', 'required');
        if (!prepaid && payment) throw errors.validation('payment', 'postpaid');
        const amount = budgetOf(b, prepaid);
        const pc = findPc(pcId);
        if (!pc) throw errors.notFound('pc');
        const owner = ownerOp(req, staff);
        requireShift(owner);
        const tariff = findTariff(tariffId);
        if (!tariff) throw errors.notFound('tariff');
        // A new guest is priced as a walk-in (no group, the loyalty level of zero spend). The price is checked before
        // the seat, in the server's order: a wrong price on a busy PC is `priceChanged` there too.
        const byAmount = amount === null ? null : minutesFor(tariff, amount, null, pc.zone);
        const mins =
          byAmount?.minutes ?? (tariff.isPackage ? (tariff.packageMinutes ?? minutes) : prepaid ? minutes : 0);
        const priced = byAmount?.priced ?? quote(tariff, prepaid ? mins : 60, null, pc.zone);
        if (payment && payment.amount !== priced.total.amount)
          throw errors.conflict('priceChanged', { total: priced.total });
        checkSeat(pc, tariff, prepaid);
        if (!prepaid && !guestPostpaidOn()) throw errors.policyDenied('postpaidNotAllowed');
        if (!prepaid) {
          const limit = club().limits.guestDebtLimit ?? 0;
          const first = quote(tariff, 1, null, pc.zone).total;
          if (limit > 0 && first.amount > limit) throw errors.insufficientFunds(first, zero());
        }
        const guest = createGuest(pc, name, null, true);
        return seat({ staff, pc, user: guest, tariff, mins, prepaid, payment, priced, guest: true, owner });
      },
      { keyRequired: true, replayed: replayGuestCode },
    );
  });

  /**
   * Adds paid minutes to a running session (by seat or by session id): the minutes sent on an hourly tariff, the
   * package's own on a package (the server overrides `minutes`). A walk-in guest pays exactly the price.
   */
  app.post('/admin/sessions/extend', async (req, reply) => {
    const staff = requireAdmin(req);
    return idempotent(req, reply, async () => {
      const b = body(req);
      const minutes = int(b, 'minutes', 5, 1440);
      const payment = paymentOf(b);
      const amount = budgetOf(b, true);
      const owner = ownerOp(req, staff);
      requireShift(owner);
      const rec = targetSession(b);
      const tariff = findTariff(optStr(b, 'tariffId', 64) ?? rec.tariffId);
      const user = findUser(rec.userId);
      const pc = findPc(rec.pcId);
      if (!tariff) throw errors.notFound('tariff');
      if (!user) throw errors.notFound('user');
      if (!rec.isPrepaid) throw errors.conflict('postpaidSession');
      const rule = tariffRule(tariff, pc?.zone ?? '');
      if (rule) throw errors.policyDenied(rule);
      const byAmount =
        amount === null ? null : minutesFor(tariff, amount, user.id, pc?.zone ?? '', Math.floor(rec.purchasedSec / 60));
      const mins = byAmount?.minutes ?? (tariff.isPackage ? (tariff.packageMinutes ?? minutes) : minutes);
      const priced = byAmount?.priced ?? quote(tariff, mins, user.id, pc?.zone ?? '');
      const cost = priced.total;
      if (user.transient && payment && payment.amount !== cost.amount)
        throw errors.conflict('priceChanged', { total: cost });
      checkFunds(user, payment, cost);
      const paid = bookPayment(staff, user, payment, owner);
      if (cost.amount > 0)
        asOwner(owner, () =>
          applyTransaction(user, 'charge', uzs(-cost.amount), `Продление +${mins} мин · ${tariff.name}`, rec.id),
        );
      addPurchase(rec, { paid: cost.amount, sec: mins * 60, pkg: tariff.isPackage });
      rec.purchasedSec += mins * 60;
      rec.paidAmount = uzs(rec.paidAmount.amount + cost.amount);
      rec.tariffId = tariff.id;
      rec.warningsSent = [];
      const session = viewSession(rec);
      pushToPc(rec.pcId, 'sessionUpdated', session);
      pushToUser(user.id, 'walletUpdated', balanceOf(user));
      record(staff, 'sessionExtend', {
        ...ownerFields(owner),
        userId: user.id,
        pcId: rec.pcId,
        amount: cost.amount,
        detail: `${user.displayName} · ${pc?.name ?? rec.pcId} · +${mins}`,
        meta: {
          minutes: mins,
          tariff: tariff.name,
          package: tariff.isPackage,
          sessionId: rec.id,
          quote: quoteMeta(priced),
          ...(payment && paid
            ? { paidAmount: payment.amount, paidMethod: payment.method, transactionId: paid.transaction.id }
            : {}),
        },
      });
      return {
        status: 200,
        body: {
          session,
          charged: cost,
          balance: user.balance,
          ...(paid ? { payment: { transaction: paid.transaction, bonus: uzs(paid.bonus) } } : {}),
        },
      };
    });
  });

  /**
   * Ends a session from the counter; unused prepaid time is refunded by `endSession`, postpaid charged. The player is
   * signed out of that PC whatever the role (D-28). The answer adds the settled balance (negative — a debt), whose it is
   * and, for a walk-in guest, the cash that may go back now (`payable`). A session that moved to another PC than the one
   * named is refused (409 `sessionMoved`): the desk reads the map again.
   */
  app.post('/admin/sessions/end', async (req, reply) => {
    const staff = requireAdmin(req);
    return idempotent(req, reply, async () => {
      const b = body(req);
      const rec = targetSession(b);
      const pcId = optStr(b, 'pcId', 64);
      if (pcId && rec.pcId !== pcId) throw errors.conflict('sessionMoved');
      const { result, user, guest } = deskEnd(staff, rec);
      return {
        status: 200,
        body: {
          session: result.session,
          charged: result.charged,
          refunded: result.refunded,
          balance: user?.balance ?? zero(),
          ...(user ? { user: { id: user.id, displayName: user.displayName, role: user.role } } : {}),
          // A walk-in guest's only, as the server: a member's end has no `payable` key.
          ...(user && guest ? { payable: uzs(payableOf(user)) } : {}),
        },
      };
    });
  });

  /**
   * Moves an open session to another PC (beyond the contract, D-59..D-61): time and money carry over, nothing is
   * booked. `fromPcId` is the PC the desk sees it on — a retry after the move, or a stale map, is 409 `sessionMoved`.
   * The target must be live, not in maintenance (403), not offline, free (`pcBusy`) and hold no session of its own the
   * server does not know (`targetHasLocalSession`: an outbox not empty, or an unknown `currentSessionId` in its last
   * heartbeat). Prepaid keeps its tariff while it is sold in the target's zone, else the desk names an hourly one sold
   * there (409 `tariffZone {zone}`); postpaid keeps its price. Paused resumes, locked runs again. The target's other
   * sign-ins go (`seatTaken`), the player's sign-in on the old PC goes; the old PC is told the session ended there (the
   * «ended view») and `userRevoked {seatMoved}`.
   */
  app.post('/admin/sessions/move', async (req, reply) => {
    const staff = requireAdmin(req);
    if (isApiKey(req)) throw errors.forbidden('staffOnly');
    return idempotent(
      req,
      reply,
      async () => {
        const b = body(req);
        const fromPcId = uuidOf(b['fromPcId'], 'fromPcId', true) as string;
        const toPcId = uuidOf(b['toPcId'], 'toPcId', true) as string;
        if (fromPcId === toPcId) throw errors.validation('toPcId', 'same');
        const sessionId = uuidOf(b['sessionId'], 'sessionId', false);
        const tariffId = uuidOf(b['tariffId'], 'tariffId', false);
        const from = findPc(fromPcId);
        const to = findPc(toPcId);
        if (!from || !to) throw errors.notFound('pc');
        let rec: SessionRecord | undefined;
        if (sessionId) {
          rec = findSession(sessionId);
          if (!rec) throw errors.notFound('session');
        } else {
          rec = openSessionForPc(from.id);
        }
        if (!rec || rec.pcId !== from.id || rec.state === 'ended' || rec.state === 'idle')
          throw errors.conflict('sessionMoved');
        if (rec.state === 'ending') throw errors.conflict('sessionEnding');
        if (to.status === 'maintenance') throw errors.policyDenied('pcMaintenance');
        if (to.status === 'offline') throw errors.conflict('targetOffline');
        if (openSessionForPc(to.id)) throw errors.conflict('pcBusy', { pcId: to.id });
        // As the server: a session the server knows by its id or by the PC's own id of it is not the PC's own.
        const reported = to.reportedSessionId ?? null;
        const known = (id: string): boolean =>
          findSession(id) !== undefined || db.sessions.some((s) => s.clientSessionId === id);
        if ((to.offlineQueue ?? 0) > 0 || (reported && !known(reported)))
          throw errors.conflict('targetHasLocalSession');
        const user = findUser(rec.userId);
        if (!user) throw errors.notFound('user');
        const current = findTariff(rec.tariffId);
        const soldIn = (t: Tariff): boolean =>
          t.zones.length === 0 || t.zones.some((z) => z.toLowerCase() === to.zone.toLowerCase());
        let next = current;
        if (tariffId) {
          if (!rec.isPrepaid) throw errors.validation('tariffId', 'postpaid');
          next = findTariff(tariffId);
          if (!next) throw errors.notFound('tariff');
          if (next.isPackage) throw errors.validation('tariffId', 'package');
          // Its zones and its time windows, as the quote and the server judge it (403 tariffZone | tariffTime).
          const rule = tariffRule(next, to.zone);
          if (rule) throw errors.policyDenied(rule);
        } else if (rec.isPrepaid && current && !soldIn(current)) {
          throw errors.conflict('tariffZone', { zone: to.zone });
        }
        if (rec.state === 'paused' && !rec.isPrepaid && current) checkPostpaid(user, current, to.zone);

        const at = now();
        const tariffFrom = current?.name ?? null;
        if (rec.state === 'paused') {
          rec.runningSince = at;
          rec.pausedAt = null;
        }
        if (rec.state === 'paused' || rec.state === 'locked') rec.state = 'active';
        const tariffChanged = next !== undefined && next.id !== rec.tariffId;
        if (next) rec.tariffId = next.id;
        rec.pcId = to.id;
        rec.moves = [...(rec.moves ?? []), { fromPcId: from.id, toPcId: to.id, at, staffId: staff.id }];
        // A walk-in guest signs in on the target at once (D-98): a code from before goes with the move.
        rec.guestCodeHash = null;
        const guestCode: string | null = null;
        markDirty();
        settleStatus(from);
        settleStatus(to);
        // Whoever was signed in on the target must not get this session under their name (D-29); the player signs in
        // on the target, and the sign-in on the old PC goes.
        for (const other of othersSignedInOn(to.id, user.id)) {
          revokeUserTokens(other, to.id);
          pushToPc(to.id, 'userRevoked', { userId: other, reason: 'seatTaken' });
        }
        revokeUserTokens(user.id, from.id);
        const session = viewSession(rec);
        pushToPc(to.id, 'sessionUpdated', session);
        pushToPc(from.id, 'sessionUpdated', endedView(rec, from.id));
        pushToPc(from.id, 'userRevoked', { userId: user.id, reason: 'seatMoved' });
        broadcast('pcStatusChanged', { pcId: from.id, status: from.status });
        broadcast('pcStatusChanged', { pcId: to.id, status: to.status });
        record(staff, 'sessionMove', {
          userId: user.id,
          pcId: to.id,
          detail: `${from.name} → ${to.name}`,
          meta: {
            sessionId: rec.id,
            fromPcId: from.id,
            fromPc: from.name,
            toPcId: to.id,
            toPc: to.name,
            tariffFrom,
            tariffTo: next?.name ?? null,
            prepaid: rec.isPrepaid,
            secondsLeft: session.secondsLeft,
          },
        });
        const answer = {
          session,
          from: { pcId: from.id, name: from.name },
          to: { pcId: to.id, name: to.name },
          tariffChanged,
          user: { id: user.id, displayName: user.displayName, role: user.role },
          signedIn: false,
        };
        return { status: 200, body: guestCode ? { ...answer, guestCode } : answer, stored: answer };
      },
      { keyRequired: true, strictBody: true, replayed: replayGuestCode },
    );
  });

  /**
   * Top-up at the counter, booked under its payment method (`cash` when none is sent). `settleDebt` takes a debt to the
   * tiyin: the amount must be exactly the debt (409 `debtChanged {debt}`; `noDebt` when there is none), with no bonus.
   */
  app.post('/admin/wallet/topup', async (req, reply) => {
    const staff = requireAdmin(req);
    const viaApi = isApiKey(req);
    return idempotent(req, reply, async () => {
      const b = body(req);
      const user = findUser(str(b, 'userId', 64));
      const amount = int(b, 'amount', 1, 100_000_000);
      const method = payMethod(optStr(b, 'method', 16) ?? 'cash', 'method');
      const settleDebt = optBool(b, 'settleDebt') ?? false;
      // D-101: the owner's top-up is the owner's own — no shift needed, none joined.
      const owner = !viaApi && staff.role === 'owner';
      if (!owner) requireShift();
      if (!user) throw errors.notFound('user');
      if (settleDebt) {
        if (user.balance.amount >= 0) throw errors.conflict('noDebt');
        if (amount !== -user.balance.amount) throw errors.conflict('debtChanged', { debt: uzs(-user.balance.amount) });
      }
      const { bonus, transaction } = topUpWithBonus(user, amount, method, { settleDebt, viaApi, owner });
      record(staff, 'topUp', {
        ...(owner ? { shiftId: null, owner: true } : {}),
        userId: user.id,
        amount,
        detail: user.displayName,
        meta: {
          method,
          bonus,
          transactionId: transaction.id,
          ...(settleDebt ? { debt: true } : {}),
          ...(viaApi ? { api: true } : {}),
        },
      });
      return { status: 200, body: { balance: user.balance, transaction, bonus: uzs(bonus) } };
    });
  });

  /**
   * A desk top-up taken back (beyond the contract, D-91), as the server: the money back by the same method with the tier
   * bonus it gave, and with `amount` the right top-up booked in the same step. Only a plain desk top-up (`404
   * transaction`; a seat's payment, a debt payment or the API key's — `409 notCorrectable`), once (`alreadyCancelled`),
   * in the shift that took it, still open (`topUpShiftClosed`); a cashier their own (`403 notOwnTopUp`) within 15
   * minutes (`403 cancelWindow {minutes}`) and no cash back under `limits.cashOutOwnerOnly` (`403 ownerOnly`). The player
   * must still have it (`409 topUpSpent {spendable, required}`), the drawer the cash (`409 cashShort {available}`).
   */
  app.post('/admin/wallet/topup/cancel', async (req, reply) => {
    const staff = requireAdmin(req);
    if (isApiKey(req)) throw errors.forbidden('staffOnly');
    return idempotent(
      req,
      reply,
      async () => {
        const b = body(req);
        const transactionId = str(b, 'transactionId', 64);
        const reasonCode = str(b, 'reasonCode', 32);
        if (!CANCEL_REASONS.includes(reasonCode)) throw errors.validation('reasonCode', 'enum');
        const note = optStr(b, 'note', 500)?.trim() || null;
        if (note && note.length > 200) throw errors.validation('note', 'max');
        if (reasonCode === 'other' && (!note || note.length < 3))
          throw errors.validation('note', note ? 'min' : 'required');
        const corrected = b['amount'] === undefined || b['amount'] === null ? null : int(b, 'amount', 1, 100_000_000);
        const tx = db.transactions.find((x) => x.id === transactionId && x.type === 'topUp');
        if (!tx) throw errors.notFound('transaction');
        const user = findUser(tx.userId);
        if (!user) throw errors.notFound('transaction');
        if (
          db.transactions.some(
            (x) => x.type === 'topUp' && x.ref === tx.id && x.description.startsWith(TOPUP_CANCEL_DESCRIPTION),
          )
        )
          throw errors.conflict('alreadyCancelled');
        const entry = club().audit.find((e) => e.action === 'topUp' && e.meta['transactionId'] === tx.id);
        if (
          !entry ||
          tx.amount.amount <= 0 ||
          entry.meta['forSession'] === true ||
          entry.meta['debt'] === true ||
          entry.meta['api'] === true
        )
          throw errors.conflict('notCorrectable');
        // The owner's own top-up (D-101) has no shift: only the owner takes it back, at any time, with no drawer.
        const ownerTop = entry.owner === true;
        if (ownerTop && staff.role !== 'owner') throw errors.forbidden('ownerOnly');
        if (staff.role !== 'owner' && entry.staffId !== staff.id) throw errors.forbidden('notOwnTopUp');
        if (staff.role !== 'owner' && Date.now() - Date.parse(tx.createdAt) > CANCEL_WINDOW_MIN * 60_000)
          throw new ApiError('forbidden', 'Forbidden (cancelWindow)', {
            reason: 'cancelWindow',
            minutes: CANCEL_WINDOW_MIN,
          });
        const amount = tx.amount.amount;
        const method = topUpMethod(tx.description);
        const cashBack = method === 'cash' ? Math.max(0, amount - (corrected ?? 0)) : 0;
        if (cashBack > 0 && staff.role !== 'owner' && club().limits.cashOutOwnerOnly)
          throw errors.forbidden('ownerOnly');
        const shift = ownerTop ? null : openShift();
        if (!ownerTop && (!shift || entry.shiftId !== shift.id)) throw errors.conflict('topUpShiftClosed');
        if (shift && cashBack > 0) {
          const available = drawerNow(shift);
          if (available < cashBack) throw errors.conflict('cashShort', { available: uzs(available) });
        }
        if (corrected === amount) throw errors.validation('amount', 'same');
        const bonus = typeof entry.meta['bonus'] === 'number' ? entry.meta['bonus'] : 0;
        // What the balance can spend: an open postpaid session's bill so far counts against it, less what the bonus
        // account covers of it (D-106). The bonus it gave must still be on the bonus account; nothing spent goes back.
        const open = openSessionForUser(user.id);
        const owed = open && !open.isPrepaid ? (viewSession(open).cost?.amount ?? 0) : 0;
        const incomingBonus = corrected === null || user.transient ? 0 : topupBonus(corrected);
        const incoming = corrected === null ? 0 : corrected;
        const bonusLeft = Math.max(0, user.bonus.amount) + incomingBonus;
        const spendable = user.balance.amount - Math.max(0, owed - Math.max(0, bonusLeft - bonus));
        if (spendable + incoming < amount)
          throw errors.conflict('topUpSpent', {
            spendable: uzs(Math.max(0, spendable)),
            required: uzs(amount - incoming),
          });
        if (bonusLeft < bonus)
          throw errors.conflict('topUpSpent', {
            account: 'bonus',
            spendable: uzs(Math.max(0, user.bonus.amount)),
            required: uzs(bonus - incomingBonus),
          });
        const payMethod = method === 'other' ? 'cash' : method;
        let fresh: { bonus: number; transaction: Transaction } | null = null;
        if (corrected !== null) {
          fresh = topUpWithBonus(user, corrected, payMethod, { owner: ownerTop });
          record(staff, 'topUp', {
            ...ownerFields(ownerTop),
            userId: user.id,
            amount: corrected,
            detail: user.displayName,
            meta: { method: payMethod, bonus: fresh.bonus, transactionId: fresh.transaction.id, correctionOf: tx.id },
          });
        }
        const back = applyTransaction(
          user,
          'topUp',
          uzs(-amount),
          `${TOPUP_CANCEL_DESCRIPTION} на кассе ${counterMethod(payMethod)}`,
          tx.id,
        );
        if (ownerTop) OWNER_TRANSACTIONS.add(back.id);
        if (bonus > 0) {
          const row = applyTransaction(user, 'bonus', uzs(-bonus), 'Отмена бонуса за пополнение', tx.id);
          if (ownerTop) OWNER_TRANSACTIONS.add(row.id);
        }
        pushToUser(user.id, 'walletUpdated', balanceOf(user));
        record(staff, 'topUpCancel', {
          ...ownerFields(ownerTop),
          userId: user.id,
          amount,
          detail: user.displayName,
          meta: {
            method: payMethod,
            bonus,
            transactionId: back.id,
            cancelOf: tx.id,
            topUpAt: tx.createdAt,
            reasonCode,
            note,
            ...(fresh && corrected !== null ? { correctedTo: corrected, correctedId: fresh.transaction.id } : {}),
          },
        });
        return {
          status: 200,
          body: {
            cancelled: {
              id: back.id,
              at: back.createdAt,
              transactionId: tx.id,
              topUpAt: tx.createdAt,
              amount,
              bonus,
              method: payMethod,
              reasonCode,
              note,
              staffName: staff.name,
            },
            balance: user.balance,
            topUp: fresh ? { balance: user.balance, transaction: fresh.transaction, bonus: uzs(fresh.bonus) } : null,
            expectedCash: ((open) => (open ? drawerNow(open) : 0))(openShift()),
          },
        };
      },
      { keyRequired: true, strictBody: true },
    );
  });

  /**
   * Gives a walk-in guest's money back in cash (beyond the contract, D-37): exactly what is payable now (409
   * `payableChanged {payable}`), never to a member (`notGuest`) or while the guest plays (`guestPlaying`), only from an
   * open shift and a drawer that holds it (`cashShort {available}`). An `adjustment` row, counted as payouts in X/Z.
   */
  app.post('/admin/wallet/payout', async (req, reply) => {
    const staff = requireAdmin(req);
    // Before the key check, as the server: the club API key is refused whatever it sends.
    if (isApiKey(req)) throw errors.forbidden('staffOnly');
    return idempotent(
      req,
      reply,
      async () => {
        const b = body(req);
        const user = findUser(str(b, 'userId', 64));
        const amount = int(b, 'amount', 1, 100_000_000);
        if ((optStr(b, 'method', 16) ?? 'cash') !== 'cash') throw errors.validation('method', 'enum');
        if (!user) throw errors.notFound('user');
        if (!user.transient || user.role !== 'guest') throw errors.conflict('notGuest');
        if (openSessionForUser(user.id)) throw errors.conflict('guestPlaying');
        const payable = payableOf(user);
        if (payable <= 0 || amount !== payable) throw errors.conflict('payableChanged', { payable: uzs(payable) });
        // The owner pays the guest out in person (D-101): no shift, no drawer.
        const owner = staff.role === 'owner';
        if (!owner) {
          const shift = openShift();
          if (!shift) throw errors.conflict('shiftClosed');
          const available = drawerNow(shift);
          if (available < amount) throw errors.conflict('cashShort', { available: uzs(available) });
        }
        const transaction = applyTransaction(user, 'adjustment', uzs(-amount), PAYOUT_DESCRIPTION, null);
        if (owner) OWNER_TRANSACTIONS.add(transaction.id);
        pushToUser(user.id, 'walletUpdated', balanceOf(user));
        record(staff, 'payout', {
          ...ownerFields(owner),
          userId: user.id,
          amount,
          detail: user.displayName,
          meta: { method: 'cash', transactionId: transaction.id },
        });
        return { status: 200, body: { balance: user.balance, payable: uzs(payableOf(user)), transaction } };
      },
      { keyRequired: true },
    );
  });

  /**
   * Debt write-off (SHELL_CHANGES 90), as the server: exactly the debt (`409 debtChanged {debt}`, none — `noDebt`), not in
   * a session (`409 sessionOpen`), a reason; the owner or a cashier with the mark (`403 writeOffNotAllowed`). An
   * `adjustment` without money; the journal has `debtWriteOff`; X counts a cashier's as `writeOffs` (the owner's stays out, D-101).
   */
  app.post('/admin/wallet/write-off', async (req, reply) => {
    const staff = requireAdmin(req);
    return idempotent(
      req,
      reply,
      async () => {
        if (!mayWriteOff(staff)) throw errors.forbidden('writeOffNotAllowed');
        const b = body(req);
        const user = findUser(str(b, 'userId', 64));
        const amount = int(b, 'amount', 1, 100_000_000_000);
        const reason = (optStr(b, 'reason', 200) ?? '').trim();
        if (!reason) throw errors.validation('reason', 'required');
        if (!user) throw errors.notFound('user');
        if (openSessionForUser(user.id)) throw errors.conflict('sessionOpen');
        const debt = Math.max(0, -user.balance.amount);
        if (debt === 0) throw errors.conflict('noDebt');
        if (amount !== debt) throw errors.conflict('debtChanged', { debt: uzs(debt) });
        const transaction = applyTransaction(user, 'adjustment', uzs(amount), WRITE_OFF_DESCRIPTION + reason, null);
        // D-101: the owner's write-off is the owner's own — out of the cashier's shift and X.
        const owner = !isApiKey(req) && staff.role === 'owner';
        if (owner) OWNER_TRANSACTIONS.add(transaction.id);
        pushToUser(user.id, 'walletUpdated', balanceOf(user));
        record(staff, 'debtWriteOff', {
          ...(owner ? { shiftId: null, owner: true } : {}),
          userId: user.id,
          amount,
          detail: `${user.displayName}: ${reason}`,
          meta: { reason, transactionId: transaction.id },
        });
        return { status: 200, body: { balance: user.balance, writtenOff: uzs(amount), transactionId: transaction.id } };
      },
      { keyRequired: true },
    );
  });

  /** Staff message, lock/unlock and power commands — the existing `ServerCommand` set. */
  app.post('/admin/pcs/:pcId/command', async (req) => {
    const staff = requireAdmin(req);
    const { pcId } = req.params as { pcId: string };
    const pc = findPc(pcId);
    if (!pc) throw errors.notFound('pc');
    const b = body(req);
    const kind = str(b, 'kind', 32);
    const text = optStr(b, 'text', 500);
    record(staff, 'pcCommand', { pcId, detail: `${pc.name} · ${kind}`, meta: { kind } });
    switch (kind) {
      case 'message': {
        if (!text) throw errors.validation('text', 'required');
        const ack = await sendCommand(pcId, 'message', {
          id: uuid(),
          from: STAFF_NAME,
          text,
          level: 'info',
          requiresAck: true,
        });
        return { ack };
      }
      case 'lock':
        return { ack: await sendCommand(pcId, 'lock', { reason: 'staff', message: text ?? null }) };
      case 'unlock':
        return { ack: await sendCommand(pcId, 'unlock', null) };
      case 'reboot':
        return { ack: await sendCommand(pcId, 'reboot', { delaySec: 5, force: false, message: text ?? null }) };
      case 'shutdown':
        return { ack: await sendCommand(pcId, 'shutdown', { delaySec: 5, force: false, message: text ?? null }) };
      default:
        throw errors.validation('kind', 'unknown');
    }
  });

  /**
   * «Иду» (beyond the contract, D-63): this call and every older open call of its PC are answered. With `notify` (the
   * default) a PC on its socket gets the `message` «Администратор идёт к вам» in the caller's language, for two minutes,
   * no ack asked; an offline one is not told (`notified` false), so the next player never sees it. Answering an
   * answered or closed call changes and sends nothing (`notified` null).
   */
  app.post<{ Params: { id: string } }>('/admin/calls/:id/ack', async (req) => {
    const staff = requireAdmin(req);
    const call = db.calls.find((c) => c.id === req.params.id);
    if (!call) throw errors.notFound('call');
    const b = isObject(req.body) ? req.body : {};
    const notify = optBool(b, 'notify') ?? true;
    // null — this request answered nothing (someone else did): not the same as «the PC is offline».
    let notified: boolean | null = null;
    if (call.status === 'open' && moveCalls(call, 'acked', staff.name)) {
      notified = false;
      record(staff, 'callAck', {
        userId: call.userId,
        pcId: call.pcId,
        detail: call.pcName,
        meta: { callId: call.id, category: call.category },
      });
      if (notify && isConnected(call.pcId)) {
        const locale = (call.userId ? findUser(call.userId)?.locale : null) ?? 'ru';
        void sendCommand(
          call.pcId,
          'message',
          {
            id: uuid(),
            from: STAFF_NAME,
            text: ON_THE_WAY[locale] ?? (ON_THE_WAY['ru'] as string),
            level: 'info',
            requiresAck: false,
          },
          { expiresInSec: 120, issuedBy: staff.name },
        );
        notified = true;
      }
    }
    return { call: callView(call), notified };
  });

  /** «Закрыть»: this call and every older call of its PC not yet closed. */
  app.post<{ Params: { id: string } }>('/admin/calls/:id/resolve', async (req) => {
    const staff = requireAdmin(req);
    const call = db.calls.find((c) => c.id === req.params.id);
    if (!call) throw errors.notFound('call');
    if (moveCalls(call, 'resolved', staff.name)) {
      record(staff, 'callResolve', {
        userId: call.userId,
        pcId: call.pcId,
        detail: call.pcName,
        meta: { callId: call.id, category: call.category },
      });
    }
    return { call: callView(call) };
  });

  /**
   * One command to several PCs (beyond the contract, D-65), each PC on its own: offline PCs are skipped for lock, reboot
   * and shutdown (a queued one would hit the next player); a busy PC is skipped for reboot and shutdown unless
   * `includeBusy`, which ends its session through the desk end first (journal, refund, sign-out) — with `sessionIds`
   * only the sessions the desk's confirm listed (another one is skipped `sessionOpen`). Every PC gets a
   * `pcCommand` entry with the batch. Results per PC: done (acked), queued (not connected), noAnswer (no ack in time),
   * failed, skipped (`sessionOpen` | `offline` | `notFound`). A replay answers the results as they were before the acks
   * (sent commands read `queued`).
   */
  app.post('/admin/pcs/commands', async (req, reply) => {
    const staff = requireAdmin(req);
    return idempotent(
      req,
      reply,
      async () => {
        const b = body(req);
        const raw = b['pcIds'];
        if (!Array.isArray(raw) || raw.length === 0) throw errors.validation('pcIds', 'required');
        if (raw.length > 100) throw errors.validation('pcIds', 'max');
        const pcIds = raw.map((v, i) => uuidOf(v, `pcIds[${i}]`, true) as string);
        if (new Set(pcIds).size !== pcIds.length) throw errors.validation('pcIds', 'duplicate');
        const kind = b['kind'];
        if (typeof kind !== 'string' || !(BULK_KINDS as readonly string[]).includes(kind))
          throw errors.validation('kind', 'unknown');
        const text = optStr(b, 'text', 10_000)?.trim() || null;
        if (text && text.length > 500) throw errors.validation('text', 'max');
        if (kind === 'message' && !text) throw errors.validation('text', 'required');
        const level = b['level'] ?? 'info';
        if (level !== 'info' && level !== 'warning') throw errors.validation('level', 'enum');
        const includeBusy = optBool(b, 'includeBusy') ?? false;
        if (includeBusy && kind !== 'reboot' && kind !== 'shutdown') throw errors.validation('includeBusy', 'kind');
        // The sessions the desk's confirm listed: only those are ended (a player who sat down after it is skipped).
        const rawSessions = b['sessionIds'];
        let endable: Set<string> | null = null;
        if (rawSessions !== undefined && rawSessions !== null) {
          if (!Array.isArray(rawSessions)) throw errors.validation('sessionIds', 'format');
          if (!includeBusy) throw errors.validation('sessionIds', 'includeBusy');
          if (rawSessions.length > 100) throw errors.validation('sessionIds', 'max');
          endable = new Set(rawSessions.map((v, i) => uuidOf(v, `sessionIds[${i}]`, true) as string));
        }
        const batchId = uuid();
        const results: BulkResult[] = [];
        const sent: { row: BulkResult; ack: Promise<CommandAck> }[] = [];
        const relayUse = new Map<string, number>();
        for (const pcId of pcIds) {
          const pc = findPc(pcId);
          if (!pc) {
            results.push({ pcId, pcName: null, outcome: 'skipped', skipped: 'notFound', ack: null, ended: null });
            continue;
          }
          const row: BulkResult = { pcId, pcName: pc.name, outcome: 'queued', skipped: null, ack: null, ended: null };
          results.push(row);
          if (kind === 'wake') {
            // As the server: a PC that is on sends the Wake-on-LAN packet — one in the target's /24 first, the least used.
            if (pc.status !== 'offline') {
              row.outcome = 'skipped';
              row.skipped = 'online';
              continue;
            }
            if (!pc.macAddress) {
              row.outcome = 'skipped';
              row.skipped = 'noMac';
              continue;
            }
            const net = (ip: string | null | undefined): string => (ip ?? '').split('.').slice(0, 3).join('.');
            const relay = db.pcs
              .filter((p) => p.id !== pc.id && p.status !== 'offline' && p.status !== 'maintenance')
              .sort(
                (a, b) =>
                  Number(net(a.ipAddress) !== net(pc.ipAddress)) - Number(net(b.ipAddress) !== net(pc.ipAddress)) ||
                  (relayUse.get(a.id) ?? 0) - (relayUse.get(b.id) ?? 0) ||
                  a.number - b.number,
              )[0];
            if (!relay) {
              row.outcome = 'skipped';
              row.skipped = 'noRelay';
              continue;
            }
            relayUse.set(relay.id, (relayUse.get(relay.id) ?? 0) + 1);
            row.via = relay.name;
            record(staff, 'pcCommand', {
              pcId: pc.id,
              detail: `${pc.name} · wake · ${relay.name}`,
              meta: { kind, batchId, relayPcId: relay.id },
            });
            sent.push({ row, ack: sendCommand(relay.id, 'wake', { targetMac: pc.macAddress.toLowerCase() }) });
            continue;
          }
          const power = kind === 'reboot' || kind === 'shutdown';
          if ((power || kind === 'lock') && pc.status === 'offline') {
            row.outcome = 'skipped';
            row.skipped = 'offline';
            continue;
          }
          const open = openSessionForPc(pc.id);
          if (power && open) {
            if (!includeBusy || (endable && !endable.has(open.id))) {
              row.outcome = 'skipped';
              row.skipped = 'sessionOpen';
              continue;
            }
            const { result, user } = deskEnd(staff, open);
            row.ended = {
              sessionId: open.id,
              // As the server: never null (a session always has its player).
              user: user
                ? { id: user.id, displayName: user.displayName, role: user.role }
                : { id: open.userId, displayName: '', role: 'member' },
              charged: result.charged,
              refunded: result.refunded,
            };
          }
          const supersedes =
            kind === 'unlock'
              ? (pendingCommands(pc.id).find((c) => c.envelope.name === 'lock')?.envelope.id ?? null)
              : null;
          record(staff, 'pcCommand', { pcId: pc.id, detail: `${pc.name} · ${kind}`, meta: { kind, batchId } });
          sent.push({ row, ack: sendBulk(pc.id, kind as BulkKind, text, level, supersedes) });
        }
        // What a replay answers: before the acks, every sent command still queued.
        const stored = { batchId, results: results.map((r) => ({ ...r })) };
        const acks = await Promise.all(sent.map((x) => x.ack));
        sent.forEach(({ row }, i) => {
          const a = acks[i] as CommandAck;
          row.outcome = a.ok
            ? 'done'
            : a.error?.code === 'agentOffline'
              ? 'queued'
              : a.error?.code === 'timeout'
                ? 'noAnswer'
                : 'failed';
          // A PC not connected answered nothing: the server sends no ack for it.
          row.ack = row.outcome === 'queued' ? null : a;
        });
        return { status: 200, body: { batchId, results }, stored };
      },
      { keyRequired: true, strictBody: true },
    );
  });
}

const BULK_KINDS = ['message', 'lock', 'unlock', 'reboot', 'shutdown', 'wake'] as const;
type BulkKind = (typeof BULK_KINDS)[number];

interface BulkResult {
  pcId: string;
  pcName: string | null;
  outcome: 'done' | 'queued' | 'noAnswer' | 'failed' | 'skipped';
  skipped: 'sessionOpen' | 'offline' | 'notFound' | 'online' | 'noMac' | 'noRelay' | null;
  /** `wake`: the PC that sent the packet. */
  via?: string;
  ack: CommandAck | null;
  ended: {
    sessionId: string;
    user: { id: string; displayName: string; role: string };
    charged: Money;
    refunded: Money;
  } | null;
}

/** «Администратор идёт к вам» in the player's language. */
const ON_THE_WAY: Record<string, string> = {
  ru: 'Администратор идёт к вам',
  uz: 'Administrator yoningizga kelmoqda',
  en: 'An administrator is on the way',
};

/** The command of a bulk action, built as the single-PC route builds it; their acks are awaited together. */
function sendBulk(
  pcId: string,
  kind: BulkKind,
  text: string | null,
  level: 'info' | 'warning',
  supersedes: string | null,
): Promise<CommandAck> {
  switch (kind) {
    case 'message':
      return sendCommand(pcId, 'message', { id: uuid(), from: STAFF_NAME, text: text ?? '', level, requiresAck: true });
    case 'lock':
      return sendCommand(pcId, 'lock', { reason: 'staff', message: text });
    case 'unlock':
      return sendCommand(pcId, 'unlock', null, { supersedes });
    case 'reboot':
      return sendCommand(pcId, 'reboot', { delaySec: 5, force: false, message: text });
    case 'shutdown':
      return sendCommand(pcId, 'shutdown', { delaySec: 5, force: false, message: text });
    case 'wake':
      throw new Error('wake goes to a relay, not through sendBulk');
  }
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** A UUID field of a desk body: missing → `required` (when it must be there), not a UUID → `format`. */
function uuidOf(v: unknown, field: string, required: boolean): string | null {
  if (v === undefined || v === null || v === '') {
    if (required) throw errors.validation(field, 'required');
    return null;
  }
  if (typeof v !== 'string' || !UUID.test(v)) throw errors.validation(field, 'format');
  return v.toLowerCase();
}

/**
 * The desk end of a session (the body of `/admin/sessions/end`, shared with bulk power commands, D-65): settled with
 * reason `admin` (unused prepaid time back to the balance, postpaid charged), the player signed out of the PC, the
 * `sessionEnd` journal entry.
 */
function deskEnd(
  staff: StaffRecord,
  rec: SessionRecord,
): { result: ReturnType<typeof endSession>; user: UserRecord | undefined; guest: boolean } {
  const sessionMinutes = Math.floor((Date.now() - Date.parse(rec.startedAt)) / 60_000);
  const result = endSession(rec, 'admin');
  const user = findUser(rec.userId);
  if (user) {
    revokeUserTokens(user.id, rec.pcId);
    pushToPc(rec.pcId, 'userRevoked', { userId: user.id, reason: 'sessionEnded' });
  }
  const guest = user?.transient ?? false;
  record(staff, 'sessionEnd', {
    userId: rec.userId,
    pcId: rec.pcId,
    amount: result.refunded?.amount ?? 0,
    detail: `${user?.displayName ?? rec.userId} · ${findPc(rec.pcId)?.name ?? rec.pcId}`,
    meta: {
      sessionMinutes,
      sessionId: rec.id,
      charged: result.charged.amount,
      prepaid: rec.isPrepaid,
      guest,
    },
  });
  return { result, user, guest };
}
