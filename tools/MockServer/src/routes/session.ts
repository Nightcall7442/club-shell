/**
 * Sessions (SERVER_API.md §4.5): create (idempotent), pause/resume, end with settlement, extend, event batches, and
 * the server-side wall-clock timer that ends prepaid sessions on time-up and pushes `sessionUpdated` for reconciliation.
 */
import type { FastifyInstance } from 'fastify';
import {
  SESSION_EVENTS_MAX,
  SessionEndReason,
  SessionEventType,
  tariffIsValidFor,
  tariffPriceFor,
  weekdayFromJsDay,
  type SessionEndResult,
  type SessionEvent,
} from '@clubshell/contracts';
import {
  ApiError,
  applyTransaction,
  arr,
  balanceOf,
  body,
  bool,
  db,
  errors,
  findPc,
  findSession,
  findTariff,
  findUser,
  idempotent,
  int,
  isObject,
  isoDate,
  markDirty,
  now,
  oneOf,
  openSessionForPc,
  openSessionForUser,
  optInt,
  optStr,
  requireAgent,
  requireUser,
  str,
  uuid,
  uzs,
  viewSession,
  zero,
  type SessionRecord,
} from '../db.js';
import { broadcast, pushToPc, pushToUser } from '../ws.js';

const END_REASONS = Object.values(SessionEndReason);
const EVENT_TYPES = Object.values(SessionEventType);
const RECONCILE_PUSH_MS = 30_000;
const MAX_OFFLINE_START_MIN = 240;

function record(sessionId: string, type: SessionEvent['type'], data: SessionEvent['data'] = null): void {
  db.sessionEvents.push({ sessionId, type, at: now(), data });
  if (db.sessionEvents.length > 2000) db.sessionEvents.splice(0, db.sessionEvents.length - 2000);
}

function localClock(d: Date): string {
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

/** Ends a session, settles the wallet (postpaid charge / admin refund), frees the PC and pushes updates. */
export function endSession(s: SessionRecord, reason: SessionEndReason, nowMs = Date.now()): SessionEndResult {
  const used = viewSession(s, nowMs).secondsUsed;
  s.usedBeforeSec = used;
  s.runningSince = null;
  s.pausedAt = null;
  s.state = 'ended';
  s.endedAt = new Date(nowMs).toISOString();
  s.endReason = reason;
  const user = findUser(s.userId);
  const tariff = findTariff(s.tariffId);
  let charged = zero();
  let refunded = zero();
  if (s.isPrepaid) {
    // Unused prepaid time is refunded only when the club ends the session (admin/error), never on user/idle/timeUp.
    if ((reason === 'admin' || reason === 'error') && tariff && !tariff.isPackage && user) {
      refunded = tariffPriceFor(tariff, Math.floor(Math.max(0, s.purchasedSec - used) / 60));
      if (refunded.amount > 0) applyTransaction(user, 'refund', refunded, 'Refund: unused session time', s.id);
    }
  } else if (tariff && user) {
    charged = tariffPriceFor(tariff, Math.ceil(used / 60));
    s.paidAmount = charged;
    if (charged.amount > 0) applyTransaction(user, 'charge', uzs(-charged.amount), `Session ${tariff.name} (postpaid)`, s.id);
    record(s.id, 'charged', { amount: charged });
  }
  const pc = findPc(s.pcId);
  if (pc) {
    if (pc.status !== 'maintenance') pc.status = 'free';
    pc.currentSessionId = null;
    broadcast('pcStatusChanged', { pcId: pc.id, status: pc.status });
  }
  record(s.id, 'ended', { reason });
  markDirty();
  const session = viewSession(s, nowMs);
  pushToPc(s.pcId, 'sessionUpdated', session);
  if (user && (charged.amount > 0 || refunded.amount > 0)) pushToUser(user.id, 'walletUpdated', balanceOf(user));
  return { session, charged, refunded };
}

/** Wall-clock tick: time-up handling and periodic reconciliation pushes. */
export function tickSessions(nowMs: number): void {
  for (const s of db.sessions) {
    if (s.state === 'ended' || s.state === 'idle') continue;
    const view = viewSession(s, nowMs);
    if (s.isPrepaid && s.runningSince && view.secondsLeft <= 0) {
      endSession(s, 'timeUp', nowMs);
      continue;
    }
    if (nowMs - s.lastPushAt >= RECONCILE_PUSH_MS) {
      s.lastPushAt = nowMs;
      pushToPc(s.pcId, 'sessionUpdated', view);
    }
  }
}

function ownedOpenSession(id: string, userId: string): SessionRecord {
  const s = findSession(id);
  if (!s) throw errors.notFound('session');
  if (s.userId !== userId) throw errors.forbidden('notOwner');
  return s;
}

export function sessionRoutes(app: FastifyInstance): void {
  app.get<{ Querystring: { pcId?: string } }>('/sessions/current', async (req, reply) => {
    const pc = requireAgent(req);
    const pcId = req.query.pcId ?? pc.id;
    const s = openSessionForPc(pcId);
    return s ? viewSession(s) : reply.code(204).send();
  });

  app.post('/sessions', async (req, reply) => {
    const { pc, user } = requireUser(req);
    return idempotent(req, reply, async () => {
      const b = body(req);
      const pcId = str(b, 'pcId', 64);
      const userId = str(b, 'userId', 64);
      const tariff = findTariff(str(b, 'tariffId', 64));
      const minutes = optInt(b, 'minutes', 1);
      const prepaid = bool(b, 'prepaid');
      const startedAt = optStr(b, 'startedAt') ? isoDate(b, 'startedAt') : now();
      const clientSessionId = optStr(b, 'clientSessionId', 64);
      if (userId !== user.id) throw errors.forbidden('notOwner');
      if (pcId !== pc.id) throw errors.forbidden('pcMismatch');
      const targetPc = findPc(pcId);
      if (!targetPc) throw errors.notFound('pc');
      if (!tariff) throw errors.notFound('tariff');
      if (Date.now() - Date.parse(startedAt) > MAX_OFFLINE_START_MIN * 60_000) throw errors.validation('startedAt', 'tooOld');
      const mine = openSessionForUser(user.id);
      if (mine) throw new ApiError('sessionAlreadyActive', 'User already has an open session', { sessionId: mine.id, pcId: mine.pcId });
      const busy = openSessionForPc(pcId);
      if (busy) throw new ApiError('sessionAlreadyActive', 'PC already has an open session', { sessionId: busy.id, pcId });
      if (targetPc.status === 'maintenance') throw errors.policyDenied('pcMaintenance');
      const t = Date.now();
      const booked = db.bookings.find(
        (bk) => bk.pcId === pcId && bk.userId !== user.id && (bk.status === 'reserved' || bk.status === 'confirmed') && Date.parse(bk.from) <= t && t < Date.parse(bk.to),
      );
      if (booked) throw errors.policyDenied('pcBooked');
      if (tariff.zones.length > 0 && !tariff.zones.some((z) => z.toLowerCase() === targetPc.zone.toLowerCase())) throw errors.policyDenied('tariffZone');
      const local = new Date();
      if (!tariffIsValidFor(tariff, targetPc.zone, weekdayFromJsDay(local.getDay()), localClock(local))) throw errors.policyDenied('tariffTime');
      if (!prepaid && user.role === 'guest') throw errors.policyDenied('postpaidNotAllowed');
      let mins: number;
      if (tariff.isPackage) {
        mins = tariff.packageMinutes ?? minutes ?? 0;
      } else {
        if (minutes === null) throw errors.validation('minutes', 'required');
        if (minutes < tariff.minMinutes) throw errors.validation('minutes', 'min');
        if (tariff.maxMinutes != null && minutes > tariff.maxMinutes) throw errors.validation('minutes', 'max');
        mins = minutes;
      }
      const cost = prepaid ? tariffPriceFor(tariff, mins) : zero();
      if (prepaid && user.balance.amount < cost.amount) throw errors.insufficientFunds(cost, user.balance);
      const id = clientSessionId && !findSession(clientSessionId) ? clientSessionId : uuid();
      if (prepaid && cost.amount > 0) applyTransaction(user, 'charge', uzs(-cost.amount), `Session ${mins} min · ${tariff.name}`, id);
      const rec: SessionRecord = {
        id,
        userId: user.id,
        pcId,
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
      };
      db.sessions.push(rec);
      if (db.sessions.length > 500) db.sessions.splice(0, db.sessions.length - 500);
      targetPc.status = 'busy';
      targetPc.currentSessionId = id;
      record(id, 'started');
      markDirty();
      const session = viewSession(rec);
      pushToPc(pcId, 'sessionUpdated', session);
      broadcast('pcStatusChanged', { pcId, status: 'busy' });
      if (cost.amount > 0) pushToUser(user.id, 'walletUpdated', balanceOf(user));
      return { status: 201, body: session };
    });
  });

  app.post<{ Params: { id: string } }>('/sessions/:id/pause', async (req) => {
    const { user } = requireUser(req);
    const s = ownedOpenSession(req.params.id, user.id);
    if (s.state === 'paused') throw errors.conflict('alreadyPaused');
    if (s.state !== 'active') throw errors.sessionNotActive(viewSession(s));
    const t = Date.now();
    s.usedBeforeSec = viewSession(s, t).secondsUsed;
    s.runningSince = null;
    s.pausedAt = new Date(t).toISOString();
    s.state = 'paused';
    record(s.id, 'paused');
    markDirty();
    const view = viewSession(s, t);
    pushToPc(s.pcId, 'sessionUpdated', view);
    return view;
  });

  app.post<{ Params: { id: string } }>('/sessions/:id/resume', async (req) => {
    const { user } = requireUser(req);
    const s = ownedOpenSession(req.params.id, user.id);
    if (s.state === 'active') throw errors.conflict('notPaused');
    if (s.state !== 'paused') throw errors.sessionNotActive(viewSession(s));
    if (s.isPrepaid && s.purchasedSec - s.usedBeforeSec <= 0) throw errors.sessionNotActive(viewSession(s));
    s.runningSince = now();
    s.pausedAt = null;
    s.state = 'active';
    record(s.id, 'resumed');
    markDirty();
    const view = viewSession(s);
    pushToPc(s.pcId, 'sessionUpdated', view);
    return view;
  });

  app.post<{ Params: { id: string } }>('/sessions/:id/end', async (req) => {
    const pc = requireAgent(req);
    const s = findSession(req.params.id);
    if (!s) throw errors.notFound('session');
    if (s.pcId !== pc.id) throw errors.forbidden('pcMismatch');
    const b = body(req);
    const reason = oneOf(b, 'reason', END_REASONS);
    int(b, 'secondsUsed', 0);
    const endedAt = optStr(b, 'endedAt') ? Date.parse(isoDate(b, 'endedAt')) : Date.now();
    if (s.state === 'ended') throw errors.sessionNotActive(viewSession(s));
    return endSession(s, reason, Math.min(endedAt, Date.now()));
  });

  app.post<{ Params: { id: string } }>('/sessions/:id/extend', async (req, reply) => {
    const { user } = requireUser(req);
    const s = ownedOpenSession(req.params.id, user.id);
    return idempotent(req, reply, async () => {
      const b = body(req);
      const minutes = int(b, 'minutes', 1, 1440);
      const tariff = findTariff(optStr(b, 'tariffId', 64) ?? s.tariffId);
      if (!tariff) throw errors.notFound('tariff');
      if (s.state !== 'active' && s.state !== 'paused' && s.state !== 'locked') throw errors.sessionNotActive(viewSession(s));
      if (!s.isPrepaid) throw errors.conflict('postpaidSession');
      if (tariff.maxMinutes != null && s.purchasedSec / 60 + minutes > tariff.maxMinutes) throw errors.validation('minutes', 'max');
      const cost = tariffPriceFor(tariff, minutes);
      if (user.balance.amount < cost.amount) throw errors.insufficientFunds(cost, user.balance);
      if (cost.amount > 0) applyTransaction(user, 'charge', uzs(-cost.amount), `Extension +${minutes} min · ${tariff.name}`, s.id);
      s.purchasedSec += minutes * 60;
      s.paidAmount = uzs(s.paidAmount.amount + cost.amount);
      s.tariffId = tariff.id;
      s.warningsSent = [];
      record(s.id, 'extended', { minutes, cost });
      markDirty();
      const view = viewSession(s);
      pushToPc(s.pcId, 'sessionUpdated', view);
      pushToUser(user.id, 'walletUpdated', balanceOf(user));
      return { status: 200, body: view };
    });
  });

  app.post<{ Params: { id: string } }>('/sessions/:id/events', async (req, reply) => {
    const pc = requireAgent(req);
    const s = findSession(req.params.id);
    if (!s) throw errors.notFound('session');
    if (s.pcId !== pc.id) throw errors.forbidden('pcMismatch');
    return idempotent(req, reply, async () => {
      const events = arr(body(req), 'events', SESSION_EVENTS_MAX);
      events.forEach((e, i) => {
        if (!isObject(e)) throw errors.validation(`events[${i}]`, 'format');
        const type = oneOf(e, 'type', EVENT_TYPES);
        const at = isoDate(e, 'at');
        const data = isObject(e['data']) ? e['data'] : null;
        db.sessionEvents.push({ sessionId: s.id, type, at, data } as SessionEvent);
        if (type === 'locked' && s.state === 'active') s.state = 'locked';
        if (type === 'unlocked' && s.state === 'locked') s.state = 'active';
        if (type === 'warning' && data && typeof data['minutesLeft'] === 'number' && !s.warningsSent.includes(data['minutesLeft'])) {
          s.warningsSent.push(data['minutesLeft']);
        }
      });
      if (db.sessionEvents.length > 2000) db.sessionEvents.splice(0, db.sessionEvents.length - 2000);
      markDirty();
      return { status: 204, body: undefined };
    });
  });
}
