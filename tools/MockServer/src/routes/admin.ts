/**
 * Cashier/administrator surface (`/api/v1/admin/*`) — the club-side counterpart of the kiosk API, used by
 * `apps/admin`. The central server product is out of scope for this repository (`COMPETITORS.md` §4), so these
 * routes exist to make the demo whole: open time on a seat, top up a wallet, extend or end someone's session and
 * push the existing `ServerCommand`s to a PC.
 *
 * Auth is a static bearer token (`MOCK_ADMIN_TOKEN`, default `admin-dev-token`) rather than a staff login: the
 * real console authenticates against the operator's own server. These routes are exempt from the agent Bearer /
 * HMAC gate (`isExempt` in `index.ts`).
 */
import type { FastifyInstance, FastifyRequest } from 'fastify';
import { tariffPriceFor, type Money, type Session } from '@clubshell/contracts';
import {
  ApiError,
  applyTransaction,
  balanceOf,
  body,
  db,
  errors,
  findPc,
  findSession,
  findTariff,
  findUser,
  int,
  now,
  openSessionForPc,
  openSessionForUser,
  optStr,
  publicPc,
  str,
  uuid,
  uzs,
  viewSession,
  type PcRecord,
  type SessionRecord,
  type UserRecord,
} from '../db.js';
import { endSession } from './session.js';
import { requireStaff, topUpWithBonus } from './club.js';
import { club, clubHooks, inCurfew, isMinor, profileOf, quote } from '../club.js';
import { broadcast, pushToPc, pushToUser, sendCommand } from '../ws.js';

const STAFF_NAME = 'Администратор';

function requireAdmin(req: FastifyRequest): void {
  requireStaff(req);
}

/** One row of the hall map: the PC, who is on it and how much time is left. */
interface SeatView {
  pc: ReturnType<typeof publicPc>;
  session: Session | null;
  user: { id: string; displayName: string; role: string; balance: Money } | null;
}

function seatOf(pc: PcRecord): SeatView {
  const rec = openSessionForPc(pc.id);
  const session = rec ? viewSession(rec) : null;
  const user = rec ? findUser(rec.userId) : undefined;
  return {
    pc: publicPc(pc),
    session,
    user: user ? { id: user.id, displayName: user.displayName, role: user.role, balance: user.balance } : null,
  };
}

function userView(u: UserRecord): { id: string; displayName: string; username: string; role: string; balance: Money } {
  return { id: u.id, displayName: u.displayName, username: u.username, role: u.role, balance: u.balance };
}

/** The session the cashier is acting on, by session id or by seat. */
function targetSession(b: Record<string, unknown>): SessionRecord {
  const sessionId = optStr(b, 'sessionId', 64);
  const rec = sessionId ? findSession(sessionId) : openSessionForPc(str(b, 'pcId', 64));
  if (!rec || rec.state === 'ended' || rec.state === 'idle') throw errors.notFound('session');
  return rec;
}

export function adminRoutes(app: FastifyInstance): void {
  /** Hall map + tariffs + staff-visible users, i.e. everything the cashier screen renders. */
  app.get('/admin/overview', async (req) => {
    requireAdmin(req);
    const seats = db.pcs.map(seatOf);
    return {
      at: now(),
      club: { free: seats.filter((s) => s.pc.status === 'free').length, total: seats.length },
      seats,
      tariffs: db.tariffs,
      users: db.users
        .filter((u) => !u.transient && u.role !== 'admin' && !profileOf(u.id).blacklisted)
        .map(userView),
      zones: club().zones,
    };
  });

  /** Opens a session on a seat for an existing member (prepaid) — the "add time" of a cashier. */
  app.post('/admin/sessions', async (req, reply) => {
    requireAdmin(req);
    const b = body(req);
    const pcId = str(b, 'pcId', 64);
    const userId = str(b, 'userId', 64);
    const minutes = int(b, 'minutes', 5, 1440);
    const pc = findPc(pcId);
    const user = findUser(userId);
    const tariff = findTariff(str(b, 'tariffId', 64));
    if (!pc) throw errors.notFound('pc');
    if (!user) throw errors.notFound('user');
    if (!tariff) throw errors.notFound('tariff');
    if (pc.status === 'maintenance') throw errors.policyDenied('pcMaintenance');
    if (openSessionForPc(pcId)) throw new ApiError('sessionAlreadyActive', 'PC already has an open session', { pcId });
    const mine = openSessionForUser(userId);
    if (mine)
      throw new ApiError('sessionAlreadyActive', 'User already has an open session', {
        sessionId: mine.id,
        pcId: mine.pcId,
      });
    if (profileOf(userId).blacklisted) throw errors.policyDenied('blacklisted');
    if (isMinor(userId) && inCurfew()) throw errors.policyDenied('minorCurfew');
    const mins = tariff.isPackage ? (tariff.packageMinutes ?? minutes) : minutes;
    const cost = quote(tariff, mins, userId, pc.zone).total;
    if (user.balance.amount < cost.amount) throw errors.insufficientFunds(cost, user.balance);
    const id = uuid();
    if (cost.amount > 0)
      applyTransaction(user, 'charge', uzs(-cost.amount), `Session ${mins} min · ${tariff.name} (staff)`, id);
    const startedAt = now();
    const rec: SessionRecord = {
      id,
      userId,
      pcId,
      tariffId: tariff.id,
      state: 'active',
      startedAt,
      isPrepaid: true,
      purchasedSec: mins * 60,
      paidAmount: cost,
      usedBeforeSec: 0,
      runningSince: startedAt,
      pausedAt: null,
      endedAt: null,
      endReason: null,
      warningsSent: [],
      lastPushAt: Date.now(),
    };
    db.sessions.push(rec);
    pc.status = 'busy';
    pc.currentSessionId = id;
    const session = viewSession(rec);
    pushToPc(pcId, 'sessionUpdated', session);
    broadcast('pcStatusChanged', { pcId, status: 'busy' });
    pushToUser(userId, 'walletUpdated', balanceOf(user));
    clubHooks.sessionOpened(rec);
    return reply.code(201).send({ session, charged: cost, balance: user.balance });
  });

  /** Adds paid minutes to a running session (by seat or by session id). */
  app.post('/admin/sessions/extend', async (req) => {
    requireAdmin(req);
    const b = body(req);
    const rec = targetSession(b);
    const minutes = int(b, 'minutes', 5, 1440);
    const tariff = findTariff(optStr(b, 'tariffId', 64) ?? rec.tariffId);
    const user = findUser(rec.userId);
    if (!tariff) throw errors.notFound('tariff');
    if (!user) throw errors.notFound('user');
    if (!rec.isPrepaid) throw errors.conflict('postpaidSession');
    const cost = quote(tariff, minutes, user.id, findPc(rec.pcId)?.zone ?? '').total;
    if (user.balance.amount < cost.amount) throw errors.insufficientFunds(cost, user.balance);
    if (cost.amount > 0)
      applyTransaction(user, 'charge', uzs(-cost.amount), `Extension +${minutes} min · ${tariff.name} (staff)`, rec.id);
    rec.purchasedSec += minutes * 60;
    rec.paidAmount = uzs(rec.paidAmount.amount + cost.amount);
    rec.tariffId = tariff.id;
    rec.warningsSent = [];
    const session = viewSession(rec);
    pushToPc(rec.pcId, 'sessionUpdated', session);
    pushToUser(user.id, 'walletUpdated', balanceOf(user));
    return { session, charged: cost, balance: user.balance };
  });

  /** Ends a session from the counter; unused prepaid time is refunded by `endSession`. */
  app.post('/admin/sessions/end', async (req) => {
    requireAdmin(req);
    const b = body(req);
    const rec = targetSession(b);
    const result = endSession(rec, 'admin');
    return { session: result.session, charged: result.charged, refunded: result.refunded };
  });

  /** Cash / card top-up at the counter. */
  app.post('/admin/wallet/topup', async (req) => {
    requireAdmin(req);
    const b = body(req);
    const user = findUser(str(b, 'userId', 64));
    const amount = int(b, 'amount', 1, 100_000_000);
    const method = optStr(b, 'method', 16) ?? 'cash';
    if (!user) throw errors.notFound('user');
    const { bonus } = topUpWithBonus(user, amount, method);
    return { balance: user.balance, bonus: uzs(bonus) };
  });

  /** Staff message, lock/unlock and power commands — the existing `ServerCommand` set. */
  app.post('/admin/pcs/:pcId/command', async (req) => {
    requireAdmin(req);
    const { pcId } = req.params as { pcId: string };
    const pc = findPc(pcId);
    if (!pc) throw errors.notFound('pc');
    const b = body(req);
    const kind = str(b, 'kind', 32);
    const text = optStr(b, 'text', 500);
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
}
