/**
 * Games (SERVER_API.md §4.6) and apps (§4.7): catalogue with ETag/paging, account-pool leases (3 accounts per
 * launcher, `accountPoolExhausted` when empty, AES-GCM secrets the Agent can decrypt), release, save-upload target
 * (with a mock `PUT` sink), launch reports.
 */
import type { FastifyInstance } from 'fastify';
import {
  AccountLeaseReleaseReason,
  LaunchReportPhase,
  LauncherType,
  type AccountLease,
  type Game,
} from '@clubshell/contracts';
import {
  ApiError,
  body,
  db,
  encryptPoolSecret,
  errors,
  findGame,
  findSession,
  inSec,
  int,
  markDirty,
  now,
  obj,
  oneOf,
  optInt,
  optionalUser,
  paginate,
  requireAgent,
  requireUser,
  sendCached,
  str,
  uuid,
  type LeaseRecord,
} from '../db.js';

const LEASE_TTL_SEC = 14_400;
const RELEASE_REASONS = Object.values(AccountLeaseReleaseReason);
const PHASES = Object.values(LaunchReportPhase);
const LAUNCHERS = Object.values(LauncherType);

function withLastPlayed(game: Game, userId: string | null): Game {
  return { ...game, lastPlayedAt: userId ? db.lastPlayed[userId]?.[game.id] ?? null : null };
}

function expireLeases(nowMs: number): void {
  for (const l of db.leases) {
    if (l.releasedAt === null && Date.parse(l.expiresAt) <= nowMs) l.releasedAt = new Date(nowMs).toISOString();
  }
}

function findLease(gameId: string, leaseId: string): LeaseRecord | undefined {
  return db.leases.find((l) => l.leaseId === leaseId && l.gameId === gameId);
}

export function gamesRoutes(app: FastifyInstance): void {
  app.get<{ Querystring: { zone?: string; page?: string; pageSize?: string } }>('/games', async (req, reply) => {
    requireAgent(req);
    const userId = optionalUser(req)?.id ?? null;
    const all = db.games.map((g) => withLastPlayed(g, userId));
    const paged = req.query.page || req.query.pageSize
      ? paginate(all, { page: req.query.page, pageSize: req.query.pageSize ?? '1000' }, 1000)
      : { items: all, total: all.length, page: 1, pageSize: all.length };
    return sendCached(req, reply, { ...paged, catalogVersion: db.catalogVersion });
  });

  app.get<{ Params: { id: string } }>('/games/:id', async (req) => {
    requireAgent(req);
    const game = findGame(req.params.id);
    if (!game) throw errors.notFound('game');
    return withLastPlayed(game, optionalUser(req)?.id ?? null);
  });

  app.get<{ Params: { id: string }; Querystring: { sessionId?: string } }>('/games/:id/accounts/lease', async (req): Promise<AccountLease> => {
    const { pc, user } = requireUser(req);
    const game = findGame(req.params.id);
    if (!game) throw errors.notFound('game');
    if (!req.query.sessionId) throw errors.validation('sessionId', 'required');
    const session = findSession(req.query.sessionId);
    if (!session || session.userId !== user.id || session.state === 'ended' || session.state === 'idle') throw errors.sessionNotActive(null);
    if (game.ageRating >= 18 && user.role === 'guest') throw errors.policyDenied('ageRating');
    const t = Date.now();
    expireLeases(t);
    let lease = db.leases.find((l) => l.gameId === game.id && l.userId === user.id && l.releasedAt === null);
    if (!lease) {
      const inUse = new Set(db.leases.filter((l) => l.releasedAt === null && l.launcher === game.launcher).map((l) => l.username));
      const account = db.accountPool.find((a) => a.launcher === game.launcher && !inUse.has(a.username));
      if (!account) throw new ApiError('accountPoolExhausted', `No free ${game.launcher} account`, { launcher: game.launcher });
      lease = {
        leaseId: uuid(),
        gameId: game.id,
        userId: user.id,
        sessionId: session.id,
        pcId: pc.id,
        launcher: game.launcher,
        username: account.username,
        createdAt: now(),
        expiresAt: inSec(LEASE_TTL_SEC),
        releasedAt: null,
      };
      db.leases.push(lease);
      if (db.leases.length > 500) db.leases.splice(0, db.leases.length - 500);
    }
    markDirty();
    const account = db.accountPool.find((a) => a.launcher === lease.launcher && a.username === lease.username);
    const secret = encryptPoolSecret(pc.signingSecret ?? Buffer.alloc(32).toString('base64'), account?.password ?? '');
    return {
      leaseId: lease.leaseId,
      launcher: lease.launcher,
      username: lease.username,
      secret,
      extra: account?.extra ?? null,
      expiresAt: lease.expiresAt,
      cloudSave: null,
    };
  });

  app.post<{ Params: { id: string; leaseId: string } }>('/games/:id/accounts/:leaseId/release', async (req, reply) => {
    requireAgent(req);
    const b = body(req);
    oneOf(b, 'reason', RELEASE_REASONS);
    const lease = findLease(req.params.id, req.params.leaseId);
    if (!lease) throw errors.notFound('lease');
    if (lease.releasedAt === null) {
      lease.releasedAt = now();
      markDirty();
    }
    return reply.code(204).send();
  });

  app.get<{ Params: { id: string; leaseId: string } }>('/games/:id/accounts/:leaseId/save-upload', async (req) => {
    requireAgent(req);
    const lease = findLease(req.params.id, req.params.leaseId);
    if (!lease) throw errors.notFound('lease');
    return {
      uploadUrl: `${req.protocol}://${req.hostname}/api/v1/mock/upload/${lease.leaseId}`,
      expiresAt: inSec(600),
      maxBytes: 512 * 1024 * 1024,
    };
  });

  // Sink for pre-signed save-bundle uploads (any body, any size up to the server limit).
  app.put<{ Params: { id: string } }>('/mock/upload/:id', async (req) => {
    const bytes = Buffer.isBuffer(req.body) ? req.body.length : req.rawBody.length;
    console.log(`[saves] received ${bytes} bytes for ${req.params.id}`);
    return { ok: true, bytes };
  });

  app.post<{ Params: { id: string } }>('/games/:id/launch-report', async (req, reply) => {
    const pc = requireAgent(req);
    const game = findGame(req.params.id);
    if (!game) throw errors.notFound('game');
    const b = body(req);
    const report = {
      gameId: game.id,
      pcId: pc.id,
      sessionId: str(b, 'sessionId', 64),
      userId: str(b, 'userId', 64),
      result: obj(b, 'result'),
      durationMs: int(b, 'durationMs', 0),
      launcher: oneOf(b, 'launcher', LAUNCHERS),
      antiCheat: obj(b, 'antiCheat'),
      phase: oneOf(b, 'phase', PHASES),
      exitCode: optInt(b, 'exitCode'),
      playedSec: optInt(b, 'playedSec', 0),
      at: now(),
    };
    db.launchReports.push(report);
    if (db.launchReports.length > 200) db.launchReports.splice(0, db.launchReports.length - 200);
    if (report.phase === 'launch' && report.result['ok'] === true) {
      const perUser = db.lastPlayed[report.userId] ?? {};
      perUser[game.id] = now();
      db.lastPlayed[report.userId] = perUser;
    }
    markDirty();
    console.log(`[games] ${pc.name}: ${game.title} ${report.phase} ok=${String(report.result['ok'])} in ${report.durationMs} ms`);
    return reply.code(204).send();
  });

  app.get('/apps', async (req, reply) => {
    requireAgent(req);
    return sendCached(req, reply, { items: db.apps });
  });
}
