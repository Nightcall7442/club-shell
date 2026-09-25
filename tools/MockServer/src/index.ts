/**
 * ClubShell mock central server — Fastify 4 app: CORS, raw-body JSON parsing, tracing, chaos (latency / fail-rate),
 * Bearer + `X-User-Token` auth context, optional HMAC signature verification, a rate-limit stub, the SERVER_API.md §3
 * error envelope, `/health`, all `/api/v1` routes, `/ws/agent`, the 1 s wall-clock tick and graceful shutdown.
 *
 * Usage: `tsx src/index.ts [--port 8080] [--reset] [--latency <ms>] [--fail-rate <0..1>]`
 * Env:   MOCK_SERVER_PORT, MOCK_VERIFY_SIGNATURE=1 (enforce HMAC), MOCK_SKIP_SIGNATURE=1 (never check),
 *        MOCK_STRICT_REGISTER=1, MOCK_QR_AUTOCONFIRM_SEC, MOCK_GUEST_DISABLED=1, LOG_LEVEL
 */
import { existsSync, readFileSync } from 'node:fs';
import cors from '@fastify/cors';
import websocket from '@fastify/websocket';
import Fastify, { type FastifyError, type FastifyInstance, type FastifyReply, type FastifyRequest } from 'fastify';
import { WS_MAX_FRAME_BYTES, type ErrorCode, type ServerErrorEnvelope } from '@clubshell/contracts';
import {
  ApiError,
  DB_PATH,
  MOCK_ART_DIR,
  db,
  errors,
  flushDb,
  hmacHex,
  now,
  resolveAgentToken,
  resolveUserToken,
  sha256Hex,
  uuid,
  type PcRecord,
} from './db.js';
import { adminRoutes } from './routes/admin.js';
import { clubRoutes } from './routes/club.js';
import { tickClub } from './club.js';
import { tickHealth } from './health.js';
import { authRoutes } from './routes/auth.js';
import { chatRoutes, tickChat } from './routes/chat.js';
import { gamesRoutes } from './routes/games.js';
import { playerSettingsRoutes } from './routes/playerSettings.js';
import { pcsRoutes } from './routes/pcs.js';
import { sessionRoutes, tickSessions } from './routes/session.js';
import { shopRoutes, tickShop } from './routes/shop.js';
import { tickWallet, walletRoutes } from './routes/wallet.js';
import { connections, registerWs } from './ws.js';

// ---------------------------------------------------------------------------------------------------------------------
// CLI / env
// ---------------------------------------------------------------------------------------------------------------------

interface Options {
  port: number;
  latencyMs: number;
  failRate: number;
}

function parseArgs(argv: string[]): Options {
  const opts: Options = {
    port: Number.parseInt(process.env['MOCK_SERVER_PORT'] ?? '8080', 10) || 8080,
    latencyMs: 0,
    failRate: 0,
  };
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    const next = argv[i + 1];
    if (arg === '--port' && next) opts.port = Number.parseInt(next, 10) || opts.port;
    if (arg === '--latency' && next) opts.latencyMs = Math.max(0, Number.parseInt(next, 10) || 0);
    if (arg === '--fail-rate' && next) opts.failRate = Math.min(1, Math.max(0, Number.parseFloat(next) || 0));
  }
  return opts;
}

const SIGNATURE_MODE: 'skip' | 'enforce' | 'log' =
  process.env['MOCK_SKIP_SIGNATURE'] === '1'
    ? 'skip'
    : process.env['MOCK_VERIFY_SIGNATURE'] === '1'
      ? 'enforce'
      : 'log';
const CLOCK_SKEW_SEC = 300;
const RATE_LIMIT_PER_MIN = 600;
const STARTED_AT = Date.now();

const isExempt = (url: string): boolean =>
  url === '/health' ||
  url.startsWith('/api/v1/admin') ||
  url.startsWith('/mock') ||
  url.startsWith('/_mock') ||
  url.startsWith('/ws/') ||
  url.includes('/mock/');

// ---------------------------------------------------------------------------------------------------------------------
// Auth: Bearer → pc, X-User-Token → user, HMAC signature, rate limit
// ---------------------------------------------------------------------------------------------------------------------

const rateBuckets = new Map<string, { count: number; resetAt: number }>();

function rateLimit(token: string): void {
  const t = Date.now();
  let bucket = rateBuckets.get(token);
  if (!bucket || bucket.resetAt <= t) {
    bucket = { count: 0, resetAt: t + 60_000 };
    rateBuckets.set(token, bucket);
    if (rateBuckets.size > 1000) {
      for (const [k, v] of rateBuckets) if (v.resetAt <= t) rateBuckets.delete(k);
    }
  }
  bucket.count += 1;
  if (bucket.count > RATE_LIMIT_PER_MIN) {
    const retryAfterSec = Math.max(1, Math.ceil((bucket.resetAt - t) / 1000));
    throw new ApiError('rateLimited', 'Too many requests', { retryAfterSec });
  }
}

function verifySignature(req: FastifyRequest, pc: PcRecord): void {
  if (SIGNATURE_MODE === 'skip') return;
  const ts = req.headers['x-timestamp'];
  const sig = req.headers['x-signature'];
  const fail = (problem: string, extra: Record<string, unknown> = {}): void => {
    if (SIGNATURE_MODE === 'enforce')
      throw errors.unauthorized(problem === 'clockSkew' ? 'clockSkew' : 'signature', { problem, ...extra });
    console.warn(
      `[sig] ${pc.name} ${req.method} ${req.url}: ${problem} (log-only; set MOCK_VERIFY_SIGNATURE=1 to enforce)`,
    );
  };
  if (typeof ts !== 'string' || typeof sig !== 'string') {
    // Agents with signing disabled send neither header; only complain when one of the two is present.
    if (typeof ts === 'string' || typeof sig === 'string') fail('missing');
    else if (SIGNATURE_MODE === 'enforce') fail('missing');
    return;
  }
  const tsNum = Number.parseInt(ts, 10);
  if (!Number.isFinite(tsNum) || Math.abs(Date.now() / 1000 - tsNum) > CLOCK_SKEW_SEC) {
    fail('clockSkew', { serverTime: now() });
    return;
  }
  const path = req.raw.url ?? req.url;
  const expected = hmacHex(
    Buffer.from(pc.signingSecret ?? '', 'base64'),
    `${ts}${req.method.toUpperCase()}${path}${sha256Hex(req.rawBody)}`,
  );
  if (expected !== sig.trim().toLowerCase()) fail('mismatch');
  // ponytail: no replay window; add a (pcId, ts, sig) LRU if replay tests are ever needed.
}

async function authenticate(req: FastifyRequest): Promise<void> {
  req.auth = { pc: null, user: null, userTokenProblem: null };
  if (!req.url.startsWith('/api/v1/') || isExempt(req.url)) return;
  const authz = req.headers.authorization;
  if (typeof authz !== 'string' || !authz.startsWith('Bearer ')) return;
  const token = authz.slice(7).trim();
  const { pc, problem } = resolveAgentToken(token);
  if (!pc) throw errors.unauthorized(problem ?? 'invalid');
  req.auth.pc = pc;
  rateLimit(token);
  verifySignature(req, pc);
  const userToken = req.headers['x-user-token'];
  if (typeof userToken === 'string' && userToken.length > 0) {
    const { user, problem: userProblem } = resolveUserToken(userToken, pc.id);
    req.auth.user = user;
    req.auth.userTokenProblem = userProblem;
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Errors
// ---------------------------------------------------------------------------------------------------------------------

const STATUS_TO_CODE: Record<number, ErrorCode> = {
  400: 'validation',
  401: 'unauthorized',
  403: 'forbidden',
  404: 'notFound',
  409: 'conflict',
  413: 'validation',
  415: 'validation',
  422: 'validation',
  429: 'rateLimited',
};

function toApiError(err: unknown): ApiError {
  if (err instanceof ApiError) return err;
  const fe = err as Partial<FastifyError> & { validation?: unknown };
  if (fe.validation)
    return new ApiError('validation', fe.message ?? 'Validation failed', { field: 'body', reason: 'schema' });
  if (typeof fe.code === 'string' && fe.code.startsWith('FST_ERR_CTP'))
    return new ApiError('validation', fe.message ?? 'Bad body', { field: 'body', reason: 'parse' });
  if (typeof fe.statusCode === 'number' && STATUS_TO_CODE[fe.statusCode]) {
    const code = STATUS_TO_CODE[fe.statusCode] as ErrorCode;
    return new ApiError(code, fe.message ?? code, null, fe.statusCode);
  }
  console.error('[error]', err);
  return new ApiError('internal', 'Internal server error', null);
}

function sendError(err: ApiError, req: FastifyRequest, reply: FastifyReply): FastifyReply {
  const envelope: ServerErrorEnvelope = {
    error: { code: err.code, message: err.message, details: err.details, traceId: req.traceId },
  };
  if (err.code === 'rateLimited' && err.details && typeof err.details['retryAfterSec'] === 'number') {
    reply.header('Retry-After', String(err.details['retryAfterSec']));
  }
  return reply.code(err.status).send(envelope);
}

// ---------------------------------------------------------------------------------------------------------------------
// App
// ---------------------------------------------------------------------------------------------------------------------

export async function buildApp(opts: Options): Promise<FastifyInstance> {
  const app = Fastify({
    logger: { level: process.env['LOG_LEVEL'] ?? 'warn' },
    bodyLimit: 64 * 1024 * 1024,
    disableRequestLogging: true,
    trustProxy: true,
  });

  app.decorateRequest('auth', null);
  app.decorateRequest('rawBody', '');
  app.decorateRequest('traceId', '');

  // Keep the raw body for HMAC verification; an empty JSON body is allowed (`null`).
  app.addContentTypeParser('application/json', { parseAs: 'string' }, (req, payload, done) => {
    const text = payload as string;
    req.rawBody = text;
    if (text.trim().length === 0) {
      done(null, null);
      return;
    }
    try {
      done(null, JSON.parse(text));
    } catch {
      done(new ApiError('validation', 'Malformed JSON body', { field: 'body', reason: 'json' }), undefined);
    }
  });
  app.addContentTypeParser('*', { parseAs: 'buffer' }, (req, payload, done) => {
    req.rawBody = (payload as Buffer).toString('utf8');
    done(null, payload);
  });

  await app.register(cors, {
    origin: true,
    exposedHeaders: ['ETag', 'X-Trace-Id', 'X-Server-Time', 'Retry-After'],
    allowedHeaders: ['*'],
  });
  await app.register(websocket, { options: { maxPayload: WS_MAX_FRAME_BYTES } });

  app.addHook('onRequest', async (req, reply) => {
    const incoming = req.headers['x-trace-id'];
    req.traceId = typeof incoming === 'string' && incoming.length > 0 ? incoming : uuid();
    req.rawBody = '';
    reply.header('X-Trace-Id', req.traceId);
    reply.header('X-Server-Time', now());
    if (isExempt(req.url)) return;
    if (opts.latencyMs > 0) await new Promise<void>((resolve) => setTimeout(resolve, opts.latencyMs));
    if (opts.failRate > 0 && Math.random() < opts.failRate) {
      throw new ApiError('serverUnavailable', 'Chaos: simulated outage', { chaos: true });
    }
  });

  app.addHook('preHandler', authenticate);

  app.addHook('onResponse', async (req, reply) => {
    const ms = reply.elapsedTime.toFixed(0);
    const who = req.auth?.pc?.name ?? '-';
    console.log(
      `${new Date().toISOString().slice(11, 23)} ${req.method.padEnd(6)} ${req.url} → ${reply.statusCode} ${ms}ms [${who}]`,
    );
  });

  app.setErrorHandler((err, req, reply) => sendError(toApiError(err), req, reply));
  app.setNotFoundHandler((req, reply) =>
    sendError(
      new ApiError('notFound', `Route ${req.method} ${req.url} not found`, { route: req.url }, 404),
      req,
      reply,
    ),
  );

  app.get('/health', async () => ({
    ok: true,
    serverTime: now(),
    uptimeSec: Math.floor((Date.now() - STARTED_AT) / 1000),
    pcs: db.pcs.length,
    registered: db.pcs.filter((p) => p.hwid !== null).length,
    connections: connections().length,
    sessions: db.sessions.filter((s) => s.state !== 'ended').length,
    signature: SIGNATURE_MODE,
    chaos: { latencyMs: opts.latencyMs, failRate: opts.failRate },
    db: DB_PATH,
  }));

  // The kiosk's demo art, so images work without internet (see `localArt` in db.ts).
  app.get<{ Params: { file: string } }>('/mock-art/:file', async (req, reply) => {
    const file = req.params.file;
    if (!/^[\w.-]+\.(jpg|png|svg|webp|mp4)$/.test(file) || !existsSync(`${MOCK_ART_DIR}${file}`)) {
      return reply.code(404).send();
    }
    const type = file.endsWith('.svg')
      ? 'image/svg+xml'
      : file.endsWith('.mp4')
        ? 'video/mp4'
        : file.endsWith('.png')
          ? 'image/png'
          : 'image/jpeg';
    return reply
      .type(type)
      .header('cache-control', 'public, max-age=86400')
      .send(readFileSync(`${MOCK_ART_DIR}${file}`));
  });

  await app.register(
    async (api) => {
      pcsRoutes(api);
      authRoutes(api);
      adminRoutes(api);
      clubRoutes(api);
      sessionRoutes(api);
      gamesRoutes(api);
      playerSettingsRoutes(api);
      walletRoutes(api);
      shopRoutes(api);
      chatRoutes(api);
    },
    { prefix: '/api/v1' },
  );
  registerWs(app);

  const tick = setInterval(() => {
    const t = Date.now();
    try {
      tickSessions(t);
      tickShop(t);
      tickWallet(t);
      tickChat(t);
      tickClub(t);
      tickHealth(t);
    } catch (err) {
      console.error('[tick]', err);
    }
  }, 1000);
  app.addHook('onClose', async () => {
    clearInterval(tick);
    flushDb();
  });
  return app;
}

async function main(): Promise<void> {
  const opts = parseArgs(process.argv.slice(2));
  const app = await buildApp(opts);
  await app.listen({ port: opts.port, host: '0.0.0.0' });
  console.log(
    [
      `ClubShell mock server listening on http://localhost:${opts.port}`,
      `  REST  http://localhost:${opts.port}/api/v1   WS ws://localhost:${opts.port}/ws/agent   health /health`,
      `  db    ${DB_PATH} (${process.argv.includes('--reset') ? 'reseeded' : 'loaded'}; ${db.pcs.length} PCs, ${db.users.length} users, ${db.games.length} games)`,
      `  sig   ${SIGNATURE_MODE}   chaos latency=${opts.latencyMs}ms failRate=${opts.failRate}`,
      `  demo  users: ${db.users.map((u) => u.username).join(', ')} (password "demo"); POST /mock/pcs/:pcId/command {type,payload}`,
    ].join('\n'),
  );

  let closing = false;
  const shutdown = (signal: string): void => {
    if (closing) return;
    closing = true;
    console.log(`\n[${signal}] shutting down…`);
    const force = setTimeout(() => process.exit(1), 5000);
    force.unref();
    app
      .close()
      .then(() => {
        flushDb();
        process.exit(0);
      })
      .catch((err: unknown) => {
        console.error(err);
        process.exit(1);
      });
  };
  process.once('SIGINT', () => shutdown('SIGINT'));
  process.once('SIGTERM', () => shutdown('SIGTERM'));
}

main().catch((err: unknown) => {
  console.error(err);
  process.exit(1);
});
