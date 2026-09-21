/**
 * User auth (SERVER_API.md §4.3) and users (§4.4): password/card/token/qr login, QR handshake with demo auto-confirm,
 * guests, logout, profile, stats, achievements, loyalty. Demo credentials: any seeded user with password `demo`
 * (admin also `admin`), cards `CARD-0001..0003`, one-time tokens `tok-alisher` / `tok-dilnoza`.
 */
import { randomBytes } from 'node:crypto';
import type { FastifyInstance } from 'fastify';
import { AuthKind, Locale, SessionEndReason, type AuthResponse, type QrLoginStatus } from '@clubshell/contracts';
import {
  body,
  createUserToken,
  db,
  errors,
  findPc,
  findUser,
  inSec,
  loyaltyOf,
  markDirty,
  now,
  oneOf,
  openSessionForPc,
  openSessionForUser,
  optOneOf,
  optStr,
  publicUser,
  requireAgent,
  requireUser,
  revokeUserTokens,
  sha256Hex,
  str,
  uuid,
  uzs,
  viewSession,
  zero,
  type PcRecord,
  type UserRecord,
} from '../db.js';
import { endSession } from './session.js';

const AUTH_KINDS = Object.values(AuthKind);
const LOCALES = Object.values(Locale);
const END_REASONS = Object.values(SessionEndReason);
const QR_TTL_SEC = 120;
const QR_SCANNED_AFTER_MS = 4_000;
const qrAutoConfirmMs = Number.parseInt(process.env['MOCK_QR_AUTOCONFIRM_SEC'] ?? '8', 10) * 1000;
const failedAttempts = new Map<string, number>();

/** Argon2id-shaped PHC string so the Agent can store it; not a real Argon2 hash (mock). */
function offlineHash(user: UserRecord, password: string): string {
  const salt = Buffer.from(sha256Hex(user.id).slice(0, 16)).toString('base64').replace(/=+$/, '');
  const hash = Buffer.from(sha256Hex(`${salt}:${password}`), 'hex')
    .toString('base64')
    .replace(/=+$/, '');
  return `$argon2id$v=19$m=65536,t=3,p=4$${salt}$${hash}`;
}

/** Binds the user to the PC and builds the `AuthResponse`. */
function authResponse(user: UserRecord, pc: PcRecord, extra: { offlineHash?: string | null } = {}): AuthResponse {
  if (user.flags.includes('banned')) throw errors.forbidden('banned');
  const elsewhere = openSessionForUser(user.id);
  if (elsewhere && elsewhere.pcId !== pc.id) {
    throw errors.conflict('activeSessionElsewhere', {
      pcId: elsewhere.pcId,
      pcName: findPc(elsewhere.pcId)?.name ?? null,
    });
  }
  const tokens = createUserToken(user, pc.id);
  const own = openSessionForPc(pc.id);
  return {
    user: publicUser(user),
    session: own && own.userId === user.id ? viewSession(own) : null,
    accessToken: tokens.accessToken,
    refreshToken: tokens.refreshToken,
    expiresAt: tokens.expiresAt,
    offlineHash: extra.offlineHash ?? null,
  };
}

function createGuest(pc: PcRecord, displayName: string | null, locale: UserRecord['locale'] | null): UserRecord {
  if (process.env['MOCK_GUEST_DISABLED'] === '1') throw errors.forbidden('guestDisabled');
  db.guestCounter += 1;
  const guest: UserRecord = {
    id: uuid(),
    username: `guest-${pc.number}-${db.guestCounter}`,
    displayName: displayName ?? `Guest ${pc.number}`,
    avatarUrl: null,
    role: 'guest',
    balance: zero(),
    loyaltyLevel: 0,
    loyaltyPoints: 0,
    createdAt: now(),
    lastSeenAt: now(),
    locale: locale ?? 'ru',
    flags: [],
    password: randomBytes(8).toString('hex'),
    pin: null,
    cardId: null,
    loginToken: null,
    bonus: uzs(0),
    muted: false,
    transient: true,
  };
  db.users.push(guest);
  markDirty();
  return guest;
}

function demoUser(): UserRecord {
  const u = db.users.find((x) => x.role === 'member') ?? db.users[0];
  if (!u) throw errors.notFound('user');
  return u;
}

export function authRoutes(app: FastifyInstance): void {
  app.post('/auth/login', async (req) => {
    const pc = requireAgent(req);
    const b = body(req);
    const kind = oneOf(b, 'kind', AUTH_KINDS);
    if (str(b, 'pcId', 64) !== pc.id) throw errors.forbidden('pcMismatch');
    str(b, 'hwid', 128);
    let user: UserRecord | undefined;
    let hash: string | null = null;
    switch (kind) {
      case 'password': {
        const username = str(b, 'username', 32).toLowerCase();
        const password = str(b, 'password', 128);
        const candidate = db.users.find((u) => u.username.toLowerCase() === username && !u.transient);
        if (!candidate || (password !== candidate.password && password !== 'demo')) {
          const attempts = (failedAttempts.get(username) ?? 0) + 1;
          failedAttempts.set(username, attempts);
          throw errors.unauthorized('badCredentials', { attemptsLeft: Math.max(0, 5 - attempts) });
        }
        failedAttempts.delete(username);
        user = candidate;
        hash = offlineHash(candidate, password);
        break;
      }
      case 'card': {
        const cardId = str(b, 'cardId', 64);
        user = db.users.find((u) => u.cardId !== null && u.cardId.toLowerCase() === cardId.toLowerCase());
        if (!user) throw errors.unauthorized('badCredentials', { attemptsLeft: 5 });
        break;
      }
      case 'token': {
        const token = str(b, 'token', 256);
        user = db.users.find((u) => u.loginToken === token);
        if (!user) throw errors.unauthorized('badCredentials', { attemptsLeft: 5 });
        break;
      }
      case 'qr': {
        const rec = db.qrLogins[str(b, 'qrToken', 128)];
        user = rec?.confirmedAt && rec.userId ? findUser(rec.userId) : undefined;
        if (!user) throw errors.unauthorized('qrNotConfirmed');
        break;
      }
      case 'guest':
        user = createGuest(pc, optStr(b, 'username', 32), null);
        break;
    }
    return authResponse(user, pc, { offlineHash: hash });
  });

  app.post('/auth/qr/start', async (req) => {
    const pc = requireAgent(req);
    const pcId = optStr(body(req), 'pcId', 64) ?? pc.id;
    if (pcId !== pc.id) throw errors.forbidden('pcMismatch');
    const token = randomBytes(18).toString('base64url');
    const rec = {
      token,
      pcId,
      createdAt: now(),
      expiresAt: inSec(QR_TTL_SEC),
      userId: null,
      confirmedAt: null,
      consumed: false,
    };
    db.qrLogins[token] = rec;
    markDirty();
    return {
      qrToken: token,
      qrUrl: `https://club.example.uz/q/${token}`,
      expiresAt: rec.expiresAt,
      pollIntervalSec: 2,
    };
  });

  app.get<{ Params: { token: string } }>('/auth/qr/:token', async (req): Promise<QrLoginStatus> => {
    const pc = requireAgent(req);
    const rec = db.qrLogins[req.params.token];
    if (!rec) throw errors.notFound('qrToken');
    if (rec.pcId !== pc.id) throw errors.forbidden('pcMismatch');
    const age = Date.now() - Date.parse(rec.createdAt);
    if (rec.consumed || Date.now() >= Date.parse(rec.expiresAt)) return { status: 'expired', auth: null };
    if (!rec.confirmedAt && qrAutoConfirmMs > 0 && age >= qrAutoConfirmMs) {
      rec.confirmedAt = now();
      rec.userId = demoUser().id;
    }
    if (rec.confirmedAt && rec.userId) {
      const user = findUser(rec.userId);
      rec.consumed = true;
      markDirty();
      if (!user) return { status: 'expired', auth: null };
      return { status: 'confirmed', auth: authResponse(user, pc) };
    }
    markDirty();
    return { status: age >= QR_SCANNED_AFTER_MS ? 'scanned' : 'pending', auth: null };
  });

  app.post<{ Params: { token: string } }>('/mock/qr/:token/confirm', async (req) => {
    const rec = db.qrLogins[req.params.token];
    if (!rec) throw errors.notFound('qrToken');
    const b = (req.body ?? {}) as Record<string, unknown>;
    const wanted =
      typeof b['userId'] === 'string' ? b['userId'] : typeof b['username'] === 'string' ? b['username'] : null;
    const user = wanted ? db.users.find((u) => u.id === wanted || u.username === wanted) : demoUser();
    if (!user) throw errors.notFound('user');
    rec.confirmedAt = now();
    rec.userId = user.id;
    markDirty();
    return { ok: true, userId: user.id, status: 'confirmed' };
  });

  app.post('/auth/guest', async (req) => {
    const pc = requireAgent(req);
    const b = body(req);
    if (str(b, 'pcId', 64) !== pc.id) throw errors.forbidden('pcMismatch');
    str(b, 'hwid', 128);
    const guest = createGuest(pc, optStr(b, 'displayName', 32), optOneOf(b, 'locale', LOCALES));
    return authResponse(guest, pc);
  });

  app.post('/auth/logout', async (req, reply) => {
    const { pc, user } = requireUser(req);
    const reason = oneOf(body(req), 'reason', END_REASONS);
    const s = openSessionForUser(user.id);
    if (s && s.pcId === pc.id) endSession(s, reason);
    revokeUserTokens(user.id, pc.id);
    user.lastSeenAt = now();
    markDirty();
    return reply.code(204).send();
  });

  app.get<{ Params: { userId: string } }>('/users/:userId', async (req) => {
    const { user } = requireUser(req);
    const target =
      user.role === 'admin' ? findUser(req.params.userId) : user.id === req.params.userId ? user : undefined;
    if (!target) throw user.role === 'admin' ? errors.notFound('user') : errors.forbidden('notOwner');
    return publicUser(target);
  });

  app.patch<{ Params: { userId: string } }>('/users/:userId', async (req) => {
    const { user } = requireUser(req, req.params.userId);
    if (user.role === 'guest') throw errors.forbidden('guest');
    const b = body(req);
    const displayName = optStr(b, 'displayName', 32);
    if (displayName !== null && displayName.trim().length < 2) throw errors.validation('displayName', 'min');
    const avatarUrl = optStr(b, 'avatarUrl', 512);
    if (avatarUrl !== null && avatarUrl.length > 0 && !/^https?:\/\//.test(avatarUrl))
      throw errors.validation('avatarUrl', 'format');
    const locale = optOneOf(b, 'locale', LOCALES);
    const pin = optStr(b, 'pin', 6);
    if (pin !== null && !/^\d{4,6}$/.test(pin)) throw errors.validation('pin', 'format');
    if (displayName !== null) user.displayName = displayName.trim();
    if (avatarUrl !== null) user.avatarUrl = avatarUrl.length > 0 ? avatarUrl : null;
    if (locale !== null) user.locale = locale;
    if (pin !== null) user.pin = pin;
    markDirty();
    return publicUser(user);
  });

  app.get<{ Params: { userId: string } }>('/users/:userId/stats', async (req) => {
    const { user } = requireUser(req, req.params.userId);
    return db.stats[user.id] ?? { totalHours: 0, sessionsCount: 0, favoriteGames: [], spent: zero(), rank: 0 };
  });

  app.get<{ Params: { userId: string } }>('/users/:userId/achievements', async (req) => {
    const { user } = requireUser(req, req.params.userId);
    return { items: db.achievements[user.id] ?? [] };
  });

  app.get<{ Params: { userId: string } }>('/users/:userId/loyalty', async (req) => {
    const { user } = requireUser(req, req.params.userId);
    return loyaltyOf(user);
  });
}
