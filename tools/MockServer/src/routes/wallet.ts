/**
 * Wallet & tariffs (SERVER_API.md §4.8): balance, filtered/paged ledger, tariffs with ETag, top-up intents that
 * settle automatically 10 s after creation (pushes `walletUpdated` + a notification), intent polling.
 */
import type { FastifyInstance } from 'fastify';
import { TOPUP_MIN_AMOUNT_MINOR, TopupProvider, TransactionType, type TopupIntent } from '@clubshell/contracts';
import {
  applyTransaction,
  balanceOf,
  body,
  db,
  errors,
  findUser,
  idempotent,
  inSec,
  markDirty,
  moneyOf,
  now,
  oneOf,
  paginate,
  requireAgent,
  requireUser,
  sendCached,
  str,
  uuid,
  uzs,
  type TopupIntentRecord,
} from '../db.js';
import { pushToUser } from '../ws.js';

const PROVIDERS = Object.values(TopupProvider);
const TX_TYPES = Object.values(TransactionType);
const AUTO_PAY_AFTER_MS = 10_000;
const INTENT_TTL_SEC = 900;

function publicIntent(i: TopupIntentRecord): TopupIntent {
  return {
    id: i.id,
    provider: i.provider,
    amount: i.amount,
    status: i.status,
    qrUrl: i.qrUrl ?? null,
    deepLink: i.deepLink ?? null,
    paymentUrl: i.paymentUrl ?? null,
    expiresAt: i.expiresAt,
    createdAt: i.createdAt,
  };
}

/** Wall-clock tick: settle pending intents after 10 s, expire stale ones. */
export function tickWallet(nowMs: number): void {
  for (const intent of db.topupIntents) {
    if (intent.status !== 'pending') continue;
    if (nowMs - Date.parse(intent.createdAt) >= AUTO_PAY_AFTER_MS) {
      const user = findUser(intent.userId);
      intent.status = user ? 'paid' : 'cancelled';
      if (user) {
        applyTransaction(user, 'topUp', intent.amount, `Top-up via ${intent.provider}`, intent.id);
        const bonusPct = user.loyaltyLevel >= 3 ? 15 : user.loyaltyLevel >= 2 ? 10 : 0;
        if (bonusPct > 0) {
          const bonus = uzs(Math.floor((intent.amount.amount * bonusPct) / 100));
          user.bonus = uzs(user.bonus.amount + bonus.amount);
          applyTransaction(user, 'bonus', uzs(0), `Loyalty bonus ${bonusPct}% (bonus balance)`, intent.id);
        }
        pushToUser(user.id, 'walletUpdated', balanceOf(user));
        pushToUser(user.id, 'notification', {
          id: uuid(),
          title: 'Top-up received',
          body: `${(intent.amount.amount / 100).toLocaleString('ru-RU')} UZS added to your balance.`,
          level: 'success',
          ttlSec: 8,
          action: { label: 'Wallet', command: '/wallet', args: null },
        });
      }
      markDirty();
    } else if (Date.parse(intent.expiresAt) <= nowMs) {
      intent.status = 'expired';
      markDirty();
    }
  }
}

export function walletRoutes(app: FastifyInstance): void {
  app.get<{ Params: { userId: string } }>('/wallet/:userId/balance', async (req) => {
    const { user } = requireUser(req, req.params.userId);
    return balanceOf(user);
  });

  app.get<{
    Params: { userId: string };
    Querystring: { page?: string; pageSize?: string; from?: string; to?: string; type?: string };
  }>('/wallet/:userId/transactions', async (req) => {
    const { user } = requireUser(req, req.params.userId);
    const { from, to, type } = req.query;
    if (type && !(TX_TYPES as string[]).includes(type)) throw errors.validation('type', 'enum');
    if (from && Number.isNaN(Date.parse(from))) throw errors.validation('from', 'format');
    if (to && Number.isNaN(Date.parse(to))) throw errors.validation('to', 'format');
    const items = db.transactions.filter(
      (t) =>
        t.userId === user.id &&
        (!type || t.type === type) &&
        (!from || Date.parse(t.createdAt) >= Date.parse(from)) &&
        (!to || Date.parse(t.createdAt) < Date.parse(to)),
    );
    return paginate(items, req.query);
  });

  app.get<{ Querystring: { zone?: string } }>('/tariffs', async (req, reply) => {
    requireAgent(req);
    const zone = req.query.zone?.toLowerCase();
    const items = db.tariffs.filter(
      (t) => !zone || t.zones.length === 0 || t.zones.some((z) => z.toLowerCase() === zone),
    );
    return sendCached(req, reply, { items, serverTime: now() }, items);
  });

  app.post<{ Params: { userId: string } }>('/wallet/:userId/topup-intent', async (req, reply) => {
    const { pc, user } = requireUser(req, req.params.userId);
    return idempotent(req, reply, async () => {
      const b = body(req);
      const amount = moneyOf(b, 'amount');
      if (amount.amount < TOPUP_MIN_AMOUNT_MINOR) throw errors.validation('amount', 'min');
      if (amount.amount > 100_000_000_00) throw errors.validation('amount', 'max');
      const provider = oneOf(b, 'provider', PROVIDERS);
      if (str(b, 'pcId', 64) !== pc.id) throw errors.forbidden('pcMismatch');
      if (user.role === 'guest' && provider !== 'cash') throw errors.policyDenied('guestTopup');
      const id = uuid();
      const deepLink =
        provider === 'cash' ? null : `${provider}://pay?merchant=clubshell&intent=${id}&amount=${amount.amount}`;
      const intent: TopupIntentRecord = {
        id,
        userId: user.id,
        provider,
        amount,
        status: 'pending',
        qrUrl: deepLink
          ? `https://api.qrserver.com/v1/create-qr-code/?size=320x320&data=${encodeURIComponent(deepLink)}`
          : null,
        deepLink,
        paymentUrl: provider === 'cash' ? null : `https://checkout.${provider}.uz/mock/${id}`,
        expiresAt: inSec(INTENT_TTL_SEC),
        createdAt: now(),
      };
      db.topupIntents.push(intent);
      if (db.topupIntents.length > 200) db.topupIntents.splice(0, db.topupIntents.length - 200);
      if (provider === 'cash') {
        db.tickets.push({
          ticketId: uuid(),
          pcId: pc.id,
          userId: user.id,
          category: 'other',
          message: `Cash top-up ${amount.amount / 100} UZS`,
          createdAt: now(),
        });
      }
      markDirty();
      return { status: 201, body: publicIntent(intent) };
    });
  });

  app.get<{ Params: { userId: string; id: string } }>('/wallet/:userId/topup-intent/:id', async (req) => {
    const { user } = requireUser(req, req.params.userId);
    const intent = db.topupIntents.find((i) => i.id === req.params.id);
    if (!intent) throw errors.notFound('topupIntent');
    if (intent.userId !== user.id) throw errors.forbidden('notOwner');
    return publicIntent(intent);
  });
}
