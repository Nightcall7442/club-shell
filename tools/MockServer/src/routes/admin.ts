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
 * Cash desk part 2, as on the server: a seat may be postpaid (`prepaid: false`; guests by `limits.guestPostpaid`,
 * members while the balance and `limits.memberDebtLimit` cover the first minute); a walk-in guest is seated with
 * `POST /admin/sessions/guest` and signs in with «Гость» on that PC; a desk open signs out anyone else on the PC
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
  revokeUserTokens,
  settleStatus,
  signedInOn,
  str,
  uuid,
  uzs,
  viewSession,
  addPurchase,
  zero,
  type PcRecord,
  type SessionRecord,
  type UserRecord,
} from '../db.js';
import { endSession } from './session.js';
import { createGuest } from './auth.js';
import { isApiKey, requireStaff, topUpWithBonus } from './club.js';
import {
  PAYOUT_DESCRIPTION,
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

function requireAdmin(req: FastifyRequest): StaffRecord {
  return requireStaff(req);
}

/** One row of the hall map: the PC, who is on it, how much time is left, and whether that player has signed in. */
interface SeatView {
  pc: ReturnType<typeof publicPc>;
  session: Session | null;
  user: { id: string; displayName: string; role: string; balance: Money } | null;
  signedIn: boolean | null;
}

function seatOf(pc: PcRecord): SeatView {
  const rec = openSessionForPc(pc.id);
  const session = rec ? viewSession(rec) : null;
  const user = rec ? findUser(rec.userId) : undefined;
  return {
    pc: publicPc(pc),
    session,
    user: user ? { id: user.id, displayName: user.displayName, role: user.role, balance: user.balance } : null,
    // A desk session nobody has signed in to yet: its clock already runs (D-49).
    signedIn: rec ? signedInOn(rec.userId, pc.id) : null,
  };
}

function userView(u: UserRecord): { id: string; displayName: string; username: string; role: string; balance: Money } {
  return { id: u.id, displayName: u.displayName, username: u.username, role: u.role, balance: u.balance };
}

/** Money is taken only in an open shift, so the X / Z reports account for every сум. */
function requireShift(): void {
  if (!openShift()) throw errors.conflict('shiftClosed');
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
  if (user.balance.amount + incoming < cost.amount)
    throw errors.insufficientFunds(cost, uzs(user.balance.amount + incoming));
}

/**
 * Books the payment of a seat or an extension as a counter top-up; its journal entry is `forSession` (the feed shows
 * the session's row with the payment merged in, D-43).
 */
function bookPayment(
  staff: StaffRecord,
  user: UserRecord,
  payment: Payment | null,
): { transaction: Transaction; bonus: number } | null {
  if (!payment) return null;
  const { bonus, transaction } = topUpWithBonus(user, payment.amount, payment.method);
  record(staff, 'topUp', {
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

/**
 * Postpaid's first minute must be affordable (the server's Rule 10): a guest within `limits.guestDebtLimit` (none — no
 * limit), anyone else within the balance plus `limits.memberDebtLimit` (0 or absent — no debt).
 */
function checkPostpaid(user: UserRecord, tariff: Tariff, zone: string): void {
  const limits = club().limits;
  const guestLimit = limits.guestDebtLimit ?? 0;
  const limit = user.role === 'guest' ? (guestLimit > 0 ? guestLimit : null) : Math.max(0, limits.memberDebtLimit ?? 0);
  if (limit === null) return;
  const first = quote(tariff, 1, user.role === 'guest' ? null : user.id, zone).total;
  if (first.amount > user.balance.amount + limit) throw errors.insufficientFunds(first, user.balance);
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
}): { status: number; body: unknown } {
  const { staff, pc, user, tariff, mins, prepaid, payment, priced } = o;
  const cost = prepaid ? priced.total : zero();
  const paid = bookPayment(staff, user, payment);
  const id = uuid();
  if (cost.amount > 0)
    applyTransaction(user, 'charge', uzs(-cost.amount), `Session ${mins} min · ${tariff.name} (staff)`, id);
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
  return {
    status: 201,
    body: {
      session,
      charged: cost,
      balance: user.balance,
      user: { id: user.id, displayName: user.displayName, role: user.role },
      // As the server: a key without a value is left out.
      ...(paid ? { payment: { transaction: paid.transaction, bonus: uzs(paid.bonus) } } : {}),
    },
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
    if (tx.type === 'refund') refunds += tx.amount.amount;
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
      const pc = findPc(pcId);
      const user = findUser(userId);
      const tariff = findTariff(str(b, 'tariffId', 64));
      if (!pc) throw errors.notFound('pc');
      requireShift();
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
      if (!prepaid && user.role === 'guest' && !club().limits.guestPostpaid)
        throw errors.policyDenied('postpaidNotAllowed');
      const mins = tariff.isPackage ? (tariff.packageMinutes ?? minutes) : prepaid ? minutes : 0;
      const priced = quote(tariff, prepaid ? mins : 60, userId, pc.zone);
      if (prepaid) checkFunds(user, payment, priced.total);
      else checkPostpaid(user, tariff, pc.zone);
      return seat({ staff, pc, user, tariff, mins, prepaid, payment, priced, guest: false });
    });
  });

  /**
   * A walk-in guest's seat (beyond the contract, D-24): the transient guest account and its session in one step, the
   * account made only after every check passed. Prepaid takes exactly the price now (409 `priceChanged {total}`, D-48),
   * postpaid follows `limits.guestPostpaid` / `guestDebtLimit`. The guest presses «Гость» on that PC to sign in.
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
        const pc = findPc(pcId);
        if (!pc) throw errors.notFound('pc');
        requireShift();
        const tariff = findTariff(tariffId);
        if (!tariff) throw errors.notFound('tariff');
        const mins = tariff.isPackage ? (tariff.packageMinutes ?? minutes) : prepaid ? minutes : 0;
        // A new guest is priced as a walk-in (no group, the loyalty level of zero spend). The price is checked before
        // the seat, in the server's order: a wrong price on a busy PC is `priceChanged` there too.
        const priced = quote(tariff, prepaid ? mins : 60, null, pc.zone);
        if (payment && payment.amount !== priced.total.amount)
          throw errors.conflict('priceChanged', { total: priced.total });
        checkSeat(pc, tariff, prepaid);
        if (!prepaid && !club().limits.guestPostpaid) throw errors.policyDenied('postpaidNotAllowed');
        if (!prepaid) {
          const limit = club().limits.guestDebtLimit ?? 0;
          const first = quote(tariff, 1, null, pc.zone).total;
          if (limit > 0 && first.amount > limit) throw errors.insufficientFunds(first, zero());
        }
        const guest = createGuest(pc, name, null, true);
        return seat({ staff, pc, user: guest, tariff, mins, prepaid, payment, priced, guest: true });
      },
      { keyRequired: true },
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
      requireShift();
      const rec = targetSession(b);
      const tariff = findTariff(optStr(b, 'tariffId', 64) ?? rec.tariffId);
      const user = findUser(rec.userId);
      const pc = findPc(rec.pcId);
      if (!tariff) throw errors.notFound('tariff');
      if (!user) throw errors.notFound('user');
      if (!rec.isPrepaid) throw errors.conflict('postpaidSession');
      const rule = tariffRule(tariff, pc?.zone ?? '');
      if (rule) throw errors.policyDenied(rule);
      const mins = tariff.isPackage ? (tariff.packageMinutes ?? minutes) : minutes;
      const priced = quote(tariff, mins, user.id, pc?.zone ?? '');
      const cost = priced.total;
      if (user.transient && payment && payment.amount !== cost.amount)
        throw errors.conflict('priceChanged', { total: cost });
      checkFunds(user, payment, cost);
      const paid = bookPayment(staff, user, payment);
      if (cost.amount > 0)
        applyTransaction(user, 'charge', uzs(-cost.amount), `Extension +${mins} min · ${tariff.name} (staff)`, rec.id);
      addPurchase(rec, { paid: cost.amount, sec: mins * 60, pkg: tariff.isPackage });
      rec.purchasedSec += mins * 60;
      rec.paidAmount = uzs(rec.paidAmount.amount + cost.amount);
      rec.tariffId = tariff.id;
      rec.warningsSent = [];
      const session = viewSession(rec);
      pushToPc(rec.pcId, 'sessionUpdated', session);
      pushToUser(user.id, 'walletUpdated', balanceOf(user));
      record(staff, 'sessionExtend', {
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
        return {
          status: 200,
          body: {
            session,
            from: { pcId: from.id, name: from.name },
            to: { pcId: to.id, name: to.name },
            tariffChanged,
            user: { id: user.id, displayName: user.displayName, role: user.role },
            signedIn: false,
          },
        };
      },
      { keyRequired: true, strictBody: true },
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
      requireShift();
      if (!user) throw errors.notFound('user');
      if (settleDebt) {
        if (user.balance.amount >= 0) throw errors.conflict('noDebt');
        if (amount !== -user.balance.amount) throw errors.conflict('debtChanged', { debt: uzs(-user.balance.amount) });
      }
      const { bonus, transaction } = topUpWithBonus(user, amount, method, { settleDebt, viaApi });
      record(staff, 'topUp', {
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
        const shift = openShift();
        if (!shift) throw errors.conflict('shiftClosed');
        const available = drawerNow(shift);
        if (available < amount) throw errors.conflict('cashShort', { available: uzs(available) });
        const transaction = applyTransaction(user, 'adjustment', uzs(-amount), PAYOUT_DESCRIPTION, null);
        pushToUser(user.id, 'walletUpdated', balanceOf(user));
        record(staff, 'payout', {
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
        for (const pcId of pcIds) {
          const pc = findPc(pcId);
          if (!pc) {
            results.push({ pcId, pcName: null, outcome: 'skipped', skipped: 'notFound', ack: null, ended: null });
            continue;
          }
          const row: BulkResult = { pcId, pcName: pc.name, outcome: 'queued', skipped: null, ack: null, ended: null };
          results.push(row);
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

const BULK_KINDS = ['message', 'lock', 'unlock', 'reboot', 'shutdown'] as const;
type BulkKind = (typeof BULK_KINDS)[number];

interface BulkResult {
  pcId: string;
  pcName: string | null;
  outcome: 'done' | 'queued' | 'noAnswer' | 'failed' | 'skipped';
  skipped: 'sessionOpen' | 'offline' | 'notFound' | null;
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
