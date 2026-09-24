/**
 * In-memory store of the mock server: typed records seeded with realistic club data, shared helpers (ids, time,
 * money, paging, validation, ETag, idempotency, auth context) and debounced persistence to `./.mock-db.json`.
 * `--reset` on the command line discards the persisted file and reseeds.
 */
import { createCipheriv, createHash, createHmac, hkdfSync, randomBytes, randomUUID } from 'node:crypto';
import { existsSync, readFileSync, unlinkSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import type { FastifyReply, FastifyRequest } from 'fastify';
import {
  ERROR_CODE_HTTP_STATUS,
  SESSION_OPEN_ENDED,
  tariffPriceFor,
  type Achievement,
  type AgentServerConfig,
  type App,
  type Balance,
  type Booking,
  type ChatMessage,
  type CommandAck,
  type ErrorCode,
  type Game,
  type HardwareInfo,
  type JsonObject,
  type LauncherType,
  type LeaderboardEntry,
  type Loyalty,
  type Money,
  type Order,
  type PagedResult,
  type Pc,
  type PcMetrics,
  type Policy,
  type Product,
  type ServerCommandEnvelope,
  type Session,
  type SessionEndReason,
  type SessionEvent,
  type SessionState,
  type Tariff,
  type TopupIntent,
  type Tournament,
  type Transaction,
  type TransactionType,
  type UpdateChannel,
  type UpdateComponent,
  type UpdateManifest,
  type User,
  type UserStats,
} from '@clubshell/contracts';

// ---------------------------------------------------------------------------------------------------------------------
// Errors
// ---------------------------------------------------------------------------------------------------------------------

/** Error that the global handler renders as the SERVER_API.md §3 envelope. */
export class ApiError extends Error {
  readonly status: number;

  constructor(
    readonly code: ErrorCode,
    message: string,
    readonly details: JsonObject | null = null,
    status?: number,
  ) {
    super(message);
    this.name = 'ApiError';
    this.status = status ?? ERROR_CODE_HTTP_STATUS[code];
  }
}

export const errors = {
  validation: (field: string, reason: string): ApiError =>
    new ApiError('validation', `Invalid ${field}: ${reason}`, { field, reason }),
  notFound: (what: string): ApiError => new ApiError('notFound', `${what} not found`, { what }),
  unauthorized: (reason: string, extra: JsonObject = {}): ApiError =>
    new ApiError('unauthorized', `Unauthorized (${reason})`, { reason, ...extra }),
  forbidden: (reason: string): ApiError => new ApiError('forbidden', `Forbidden (${reason})`, { reason }),
  conflict: (reason: string, extra: JsonObject = {}): ApiError =>
    new ApiError('conflict', `Conflict (${reason})`, { reason, ...extra }),
  policyDenied: (rule: string): ApiError => new ApiError('policyDenied', `Denied by policy rule '${rule}'`, { rule }),
  insufficientFunds: (required: Money, available: Money): ApiError =>
    new ApiError('insufficientFunds', 'Balance too low', { required, available }),
  sessionNotActive: (session: Session | null): ApiError =>
    new ApiError('sessionNotActive', 'Session is not active', session ? { session } : null),
};

// ---------------------------------------------------------------------------------------------------------------------
// Records
// ---------------------------------------------------------------------------------------------------------------------

export interface UserRecord extends User {
  password: string;
  pin: string | null;
  cardId: string | null;
  loginToken: string | null;
  bonus: Money;
  muted: boolean;
  transient: boolean;
}

export interface PcRecord extends Pc {
  hwid: string | null;
  x: number;
  y: number;
  machineName: string | null;
  macAddress: string | null;
  signingSecret: string | null;
  hardware: HardwareInfo | null;
  metrics: PcMetrics[];
}

export interface SessionRecord {
  id: string;
  userId: string;
  pcId: string;
  tariffId: string;
  state: SessionState;
  startedAt: string;
  isPrepaid: boolean;
  /** Seconds bought (prepaid); 0 for postpaid. */
  purchasedSec: number;
  /** Amount charged up front (prepaid) or at settlement (postpaid). */
  paidAmount: Money;
  /** Seconds consumed before the current run segment. */
  usedBeforeSec: number;
  /** Start of the current run segment; null while paused/ended. */
  runningSince: string | null;
  pausedAt: string | null;
  endedAt: string | null;
  endReason: SessionEndReason | null;
  warningsSent: number[];
  lastPushAt: number;
}

export interface AgentTokenRecord {
  pcId: string;
  expiresAt: string;
}

export interface RefreshTokenRecord {
  pcId: string;
  expiresAt: string;
  used: boolean;
}

export interface UserTokenRecord {
  userId: string;
  pcId: string;
  expiresAt: string;
}

export interface QrRecord {
  token: string;
  pcId: string;
  createdAt: string;
  expiresAt: string;
  userId: string | null;
  confirmedAt: string | null;
  consumed: boolean;
}

export interface CommandRecord {
  pcId: string;
  envelope: ServerCommandEnvelope;
  ack: CommandAck | null;
  ackedAt: string | null;
}

export interface PoolAccount {
  launcher: LauncherType;
  username: string;
  password: string;
  extra: JsonObject | null;
}

export interface LeaseRecord {
  leaseId: string;
  gameId: string;
  userId: string;
  sessionId: string;
  pcId: string;
  launcher: LauncherType;
  username: string;
  createdAt: string;
  expiresAt: string;
  releasedAt: string | null;
}

export interface TopupIntentRecord extends TopupIntent {
  userId: string;
}

export interface ChatReadMarker {
  upTo: string;
  at: string;
}

export interface ChatRoomRecord {
  id: string;
  messages: ChatMessage[];
  /** Last read message id (and when) per user. */
  reads: Record<string, ChatReadMarker>;
}

export interface TournamentRecord extends Omit<Tournament, 'joined' | 'players'> {
  participants: string[];
  leaderboard: LeaderboardEntry[];
  leaderboardUpdatedAt: string;
}

export interface TicketRecord {
  ticketId: string;
  pcId: string;
  userId: string | null;
  category: string;
  message: string | null;
  createdAt: string;
}

export interface IdempotencyRecord {
  status: number;
  body: unknown;
  at: string;
}

export interface Db {
  seedVersion: number;
  configVersion: number;
  catalogVersion: string;
  policy: Policy;
  pcs: PcRecord[];
  users: UserRecord[];
  games: Game[];
  apps: App[];
  tariffs: Tariff[];
  products: Product[];
  orders: Order[];
  transactions: Transaction[];
  topupIntents: TopupIntentRecord[];
  sessions: SessionRecord[];
  sessionEvents: SessionEvent[];
  chatRooms: ChatRoomRecord[];
  bookings: Booking[];
  tournaments: TournamentRecord[];
  achievements: Record<string, Achievement[]>;
  stats: Record<string, UserStats>;
  lastPlayed: Record<string, Record<string, string>>;
  updateManifests: Record<UpdateChannel, Record<UpdateComponent, UpdateManifest>>;
  agentTokens: Record<string, AgentTokenRecord>;
  refreshTokens: Record<string, RefreshTokenRecord>;
  userTokens: Record<string, UserTokenRecord>;
  qrLogins: Record<string, QrRecord>;
  commands: CommandRecord[];
  accountPool: PoolAccount[];
  leases: LeaseRecord[];
  tickets: TicketRecord[];
  anticheatReports: JsonObject[];
  launchReports: JsonObject[];
  idempotency: Record<string, IdempotencyRecord>;
  guestCounter: number;
}

// ---------------------------------------------------------------------------------------------------------------------
// Primitive helpers
// ---------------------------------------------------------------------------------------------------------------------

export const uuid = (): string => randomUUID();
export const now = (): string => new Date().toISOString();
export const addSec = (iso: string, sec: number): string => new Date(Date.parse(iso) + sec * 1000).toISOString();
export const inSec = (sec: number): string => new Date(Date.now() + sec * 1000).toISOString();
export const secondsBetween = (fromIso: string, toMs: number): number =>
  Math.max(0, Math.floor((toMs - Date.parse(fromIso)) / 1000));

/** Deterministic UUID v4-shaped id derived from a seed name (stable across reseeds). */
export function sid(name: string): string {
  const h = createHash('sha256').update(name).digest('hex');
  const variant = ((parseInt(h[16] ?? '0', 16) & 0x3) | 0x8).toString(16);
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-4${h.slice(13, 16)}-${variant}${h.slice(17, 20)}-${h.slice(20, 32)}`;
}

export const uzs = (amount: number): Money => ({ amount, currency: 'UZS' });
export const addMoney = (a: Money, b: Money): Money => uzs(a.amount + b.amount);
export const subMoney = (a: Money, b: Money): Money => uzs(a.amount - b.amount);
export const mulMoney = (a: Money, factor: number): Money => uzs(a.amount * factor);
export const zero = (): Money => uzs(0);

export function paginate<T>(items: T[], q: { page?: string; pageSize?: string }, max = 200): PagedResult<T> {
  const page = Math.max(1, Number.parseInt(q.page ?? '1', 10) || 1);
  const pageSize = Math.min(max, Math.max(1, Number.parseInt(q.pageSize ?? '50', 10) || 50));
  return { items: items.slice((page - 1) * pageSize, page * pageSize), total: items.length, page, pageSize };
}

export function compareSemver(a: string, b: string): number {
  const pa =
    a
      .split('-')[0]
      ?.split('.')
      .map((n) => Number.parseInt(n, 10) || 0) ?? [];
  const pb =
    b
      .split('-')[0]
      ?.split('.')
      .map((n) => Number.parseInt(n, 10) || 0) ?? [];
  for (let i = 0; i < 3; i += 1) {
    const d = (pa[i] ?? 0) - (pb[i] ?? 0);
    if (d !== 0) return d;
  }
  return 0;
}

export const sha256Hex = (data: string | Buffer): string => createHash('sha256').update(data).digest('hex');
export const hmacHex = (key: Buffer, message: string): string =>
  createHmac('sha256', key).update(message).digest('hex');

/** AES-256-GCM encryption of an account-pool secret with the per-agent key (HKDF-SHA256(signingSecret, "account-pool")). */
export function encryptPoolSecret(signingSecretBase64: string, plaintext: string): string {
  const key = Buffer.from(
    hkdfSync('sha256', Buffer.from(signingSecretBase64, 'base64'), Buffer.alloc(0), 'account-pool', 32),
  );
  const nonce = randomBytes(12);
  const cipher = createCipheriv('aes-256-gcm', key, nonce);
  const body = Buffer.concat([cipher.update(plaintext, 'utf8'), cipher.final()]);
  return Buffer.concat([nonce, body, cipher.getAuthTag()]).toString('base64');
}

/** Compact HS256 JWT (the Agent only reads `expiresAt`; the token is looked up as opaque). */
export function makeJwt(claims: JsonObject): string {
  const b64 = (v: unknown): string => Buffer.from(JSON.stringify(v)).toString('base64url');
  const head = `${b64({ alg: 'HS256', typ: 'JWT' })}.${b64(claims)}`;
  return `${head}.${createHmac('sha256', 'mock-server-key').update(head).digest('base64url')}`;
}

// ---------------------------------------------------------------------------------------------------------------------
// Validation helpers (throw `validation` errors)
// ---------------------------------------------------------------------------------------------------------------------

export const isObject = (v: unknown): v is JsonObject => typeof v === 'object' && v !== null && !Array.isArray(v);

export function body(req: FastifyRequest): JsonObject {
  if (!isObject(req.body)) throw errors.validation('body', 'required');
  return req.body;
}

export function str(o: JsonObject, key: string, max = 4096): string {
  const v = o[key];
  if (typeof v !== 'string' || v.length === 0) throw errors.validation(key, 'required');
  if (v.length > max) throw errors.validation(key, 'max');
  return v;
}

export function optStr(o: JsonObject, key: string, max = 4096): string | null {
  const v = o[key];
  if (v === undefined || v === null) return null;
  if (typeof v !== 'string') throw errors.validation(key, 'format');
  if (v.length > max) throw errors.validation(key, 'max');
  return v;
}

export function int(o: JsonObject, key: string, min = Number.MIN_SAFE_INTEGER, max = Number.MAX_SAFE_INTEGER): number {
  const v = o[key];
  if (typeof v !== 'number' || !Number.isInteger(v)) throw errors.validation(key, 'required');
  if (v < min) throw errors.validation(key, 'min');
  if (v > max) throw errors.validation(key, 'max');
  return v;
}

export function optInt(o: JsonObject, key: string, min?: number, max?: number): number | null {
  return o[key] === undefined || o[key] === null ? null : int(o, key, min, max);
}

export function bool(o: JsonObject, key: string): boolean {
  const v = o[key];
  if (typeof v !== 'boolean') throw errors.validation(key, 'required');
  return v;
}

export function oneOf<T extends string>(o: JsonObject, key: string, values: readonly T[]): T {
  const v = o[key];
  if (typeof v !== 'string' || !(values as readonly string[]).includes(v)) throw errors.validation(key, 'enum');
  return v as T;
}

export function optOneOf<T extends string>(o: JsonObject, key: string, values: readonly T[]): T | null {
  return o[key] === undefined || o[key] === null ? null : oneOf(o, key, values);
}

export function obj(o: JsonObject, key: string): JsonObject {
  const v = o[key];
  if (!isObject(v)) throw errors.validation(key, 'required');
  return v;
}

export function optObj(o: JsonObject, key: string): JsonObject | null {
  return o[key] === undefined || o[key] === null ? null : obj(o, key);
}

export function arr(o: JsonObject, key: string, max = 1000): unknown[] {
  const v = o[key];
  if (!Array.isArray(v)) throw errors.validation(key, 'required');
  if (v.length > max) throw errors.validation(key, 'max');
  return v;
}

export function moneyOf(o: JsonObject, key: string): Money {
  const m = obj(o, key);
  const amount = int(m, 'amount', 0);
  const currency = typeof m['currency'] === 'string' ? m['currency'] : 'UZS';
  if (currency !== 'UZS') throw errors.validation(`${key}.currency`, 'unsupported');
  return uzs(amount);
}

export function isoDate(o: JsonObject, key: string): string {
  const v = str(o, key, 64);
  if (Number.isNaN(Date.parse(v))) throw errors.validation(key, 'format');
  return new Date(v).toISOString();
}

// ---------------------------------------------------------------------------------------------------------------------
// Request context, auth, ETag, idempotency
// ---------------------------------------------------------------------------------------------------------------------

export interface AuthContext {
  pc: PcRecord | null;
  user: UserRecord | null;
  /** Why the user token was rejected, when one was sent. */
  userTokenProblem: string | null;
}

declare module 'fastify' {
  interface FastifyRequest {
    auth: AuthContext;
    rawBody: string;
    traceId: string;
  }
}

export function requireAgent(req: FastifyRequest, pcId?: string): PcRecord {
  const pc = req.auth.pc;
  if (!pc) throw errors.unauthorized('missing');
  if (pcId !== undefined && pcId !== pc.id) throw errors.forbidden('pcMismatch');
  return pc;
}

export function requireUser(req: FastifyRequest, userId?: string): { pc: PcRecord; user: UserRecord } {
  const pc = requireAgent(req);
  const user = req.auth.user;
  if (!user)
    throw errors.unauthorized('userToken', req.auth.userTokenProblem ? { problem: req.auth.userTokenProblem } : {});
  if (userId !== undefined && userId !== user.id) throw errors.forbidden('notOwner');
  return { pc, user };
}

/** Optional user context (endpoints where `X-User-Token` only enriches the response). */
export function optionalUser(req: FastifyRequest): UserRecord | null {
  return req.auth.user;
}

/** Sends `body` with an ETag computed over `key ?? body`; replies 304 when `If-None-Match` matches. */
export function sendCached(req: FastifyRequest, reply: FastifyReply, payload: unknown, key?: unknown): FastifyReply {
  const tag = `"${createHash('sha1')
    .update(JSON.stringify(key ?? payload))
    .digest('hex')}"`;
  reply.header('ETag', tag);
  reply.header('Cache-Control', 'private, must-revalidate');
  const sent = req.headers['if-none-match'];
  const list: string[] = Array.isArray(sent) ? sent : sent ? [sent] : [];
  const tags = list.flatMap((h) => h.split(',').map((t) => t.trim()));
  if (tags.includes(tag) || tags.includes(`W/${tag}`)) return reply.code(304).send();
  return reply.send(payload);
}

/**
 * Replays the stored response when the request carries an `Idempotency-Key` seen in the last 24 h (scoped by
 * method+path), otherwise runs `run` and stores its result. Errors are not stored, so a retry re-executes.
 */
export async function idempotent(
  req: FastifyRequest,
  reply: FastifyReply,
  run: () => Promise<{ status: number; body: unknown }>,
): Promise<unknown> {
  const header = req.headers['idempotency-key'];
  const key = typeof header === 'string' && header.length > 0 ? `${req.method} ${req.url} ${header}` : null;
  if (key) {
    const hit = db.idempotency[key];
    if (hit && Date.now() - Date.parse(hit.at) < 24 * 3600 * 1000) {
      reply.header('Idempotent-Replayed', 'true');
      return reply.code(hit.status).send(hit.body);
    }
  }
  const result = await run();
  if (key) {
    db.idempotency[key] = { status: result.status, body: result.body, at: now() };
    markDirty();
  }
  return reply.code(result.status).send(result.body);
}

// ---------------------------------------------------------------------------------------------------------------------
// Domain helpers
// ---------------------------------------------------------------------------------------------------------------------

export function findUser(id: string): UserRecord | undefined {
  return db.users.find((u) => u.id === id);
}

export function findPc(id: string): PcRecord | undefined {
  return db.pcs.find((p) => p.id === id);
}

export function findTariff(id: string): Tariff | undefined {
  return db.tariffs.find((t) => t.id === id);
}

export function findGame(id: string): Game | undefined {
  return db.games.find((g) => g.id === id);
}

export function findSession(id: string): SessionRecord | undefined {
  return db.sessions.find((s) => s.id === id);
}

export function openSessionForPc(pcId: string): SessionRecord | undefined {
  return db.sessions.find((s) => s.pcId === pcId && s.state !== 'ended' && s.state !== 'idle');
}

export function openSessionForUser(userId: string): SessionRecord | undefined {
  return db.sessions.find((s) => s.userId === userId && s.state !== 'ended' && s.state !== 'idle');
}

/** Public `User` view (strips credentials and internal fields). */
export function publicUser(u: UserRecord): User {
  return {
    id: u.id,
    username: u.username,
    displayName: u.displayName,
    avatarUrl: u.avatarUrl ?? null,
    role: u.role,
    balance: u.balance,
    loyaltyLevel: u.loyaltyLevel,
    loyaltyPoints: u.loyaltyPoints,
    createdAt: u.createdAt,
    lastSeenAt: u.lastSeenAt ?? null,
    locale: u.locale,
    flags: u.flags,
  };
}

/** Public `Pc` view; `hwid` only for the caller's own PC. */
export function publicPc(p: PcRecord, own = false): Pc {
  return {
    id: p.id,
    name: p.name,
    zone: p.zone,
    number: p.number,
    hwid: own ? p.hwid : null,
    ipAddress: p.ipAddress,
    status: p.status,
    currentSessionId: p.currentSessionId ?? null,
    agentVersion: p.agentVersion,
    shellVersion: p.shellVersion,
    lastHeartbeatAt: p.lastHeartbeatAt,
  };
}

export function balanceOf(u: UserRecord): Balance {
  return { userId: u.id, amount: u.balance, bonus: u.bonus, currency: 'UZS', updatedAt: now() };
}

/** Applies a signed ledger entry to the user's main balance and records it. */
export function applyTransaction(
  user: UserRecord,
  type: TransactionType,
  amount: Money,
  description: string,
  ref: string | null,
): Transaction {
  user.balance = addMoney(user.balance, amount);
  const tx: Transaction = {
    id: uuid(),
    userId: user.id,
    type,
    amount,
    balanceAfter: user.balance,
    description,
    createdAt: now(),
    ref,
  };
  db.transactions.unshift(tx);
  markDirty();
  return tx;
}

/** Wall-clock view of a session record. */
export function viewSession(s: SessionRecord, nowMs = Date.now()): Session {
  const running = s.runningSince !== null && (s.state === 'active' || s.state === 'locked' || s.state === 'ending');
  const used = s.usedBeforeSec + (running && s.runningSince ? secondsBetween(s.runningSince, nowMs) : 0);
  const left = s.isPrepaid ? Math.max(0, s.purchasedSec - used) : SESSION_OPEN_ENDED;
  let endsAt: string | null = null;
  if (s.state === 'ended') endsAt = s.endedAt;
  else if (s.isPrepaid) endsAt = new Date(nowMs + left * 1000).toISOString();
  const tariff = findTariff(s.tariffId);
  const cost =
    s.isPrepaid || s.state === 'ended' || !tariff ? s.paidAmount : tariffPriceFor(tariff, Math.ceil(used / 60));
  return {
    id: s.id,
    userId: s.userId,
    pcId: s.pcId,
    state: s.state,
    startedAt: s.startedAt,
    endsAt,
    pausedAt: s.pausedAt,
    tariffId: s.tariffId,
    secondsLeft: s.state === 'ended' ? 0 : left,
    secondsUsed: used,
    cost,
    isPrepaid: s.isPrepaid,
    warningsSent: s.warningsSent,
  };
}

/** Player-facing features set in the admin console, read without importing the club module (it imports this one). */
function clubFeatures(): Record<string, boolean> | null {
  const c = (db as unknown as { club?: { features?: Record<string, boolean> } }).club;
  return c?.features ?? null;
}

export function agentConfigFor(pc: PcRecord): AgentServerConfig {
  return {
    version: db.configVersion,
    pcName: pc.name,
    zone: pc.zone,
    number: pc.number,
    session: { warningMinutes: [15, 5, 1], heartbeatSec: 30, graceSec: 60 },
    offline: { allowNewSessions: true, maxOfflineMinutes: 240 },
    games: {
      accountPool: { enabled: true, leaseTtlSec: 14400, releaseOnExit: true },
      cloudSave: { enabled: true, maxMb: 512 },
    },
    storage: null,
    updates: { channel: 'stable', checkIntervalSec: 3600, applyWindow: { from: '04:00', to: '07:00' } },
    telemetry: { metricsIntervalSec: 5, uploadIntervalSec: 60 },
    remoteAdmin: { allowScreenCapture: true, allowRemoteInput: true, showIndicator: true },
    shell: {
      locale: 'ru',
      theme: pc.zone === 'VIP' ? 'neon' : 'default',
      // The owner's switches from the admin console (`db.club.features`) win over the defaults.
      features: {
        shop: clubFeatures()?.shop ?? true,
        chat: clubFeatures()?.chat ?? true,
        booking: clubFeatures()?.booking ?? true,
        tournaments: clubFeatures()?.tournaments ?? true,
        ads: pc.zone !== 'VIP',
      },
      ads: { enabled: pc.zone !== 'VIP', intervalSec: 900 },
      idle: { timeoutSec: 300 },
    },
    themes: [],
    wsUrl: null,
  };
}

export function createAgentTokens(pc: PcRecord): { accessToken: string; refreshToken: string; expiresAt: string } {
  const expiresAt = inSec(3600);
  const accessToken = makeJwt({
    sub: pc.id,
    club: sid('club:demo'),
    hwid: pc.hwid,
    role: 'agent',
    iat: Math.floor(Date.now() / 1000),
    exp: Math.floor(Date.parse(expiresAt) / 1000),
    jti: uuid(),
  });
  const refreshToken = `rt_${randomBytes(24).toString('base64url')}`;
  db.agentTokens[accessToken] = { pcId: pc.id, expiresAt };
  db.refreshTokens[refreshToken] = { pcId: pc.id, expiresAt: inSec(30 * 24 * 3600), used: false };
  markDirty();
  return { accessToken, refreshToken, expiresAt };
}

export function createUserToken(
  user: UserRecord,
  pcId: string,
): { accessToken: string; refreshToken: string; expiresAt: string } {
  // One user binding per PC: drop older tokens bound to this PC.
  for (const [token, rec] of Object.entries(db.userTokens)) {
    if (rec.pcId === pcId) delete db.userTokens[token];
  }
  const expiresAt = inSec(12 * 3600);
  const accessToken = `ut_${randomBytes(24).toString('base64url')}`;
  db.userTokens[accessToken] = { userId: user.id, pcId, expiresAt };
  user.lastSeenAt = now();
  markDirty();
  return { accessToken, refreshToken: `ur_${randomBytes(24).toString('base64url')}`, expiresAt };
}

export function resolveAgentToken(token: string): { pc: PcRecord | null; problem: string | null } {
  const rec = db.agentTokens[token];
  if (!rec) return { pc: null, problem: 'invalid' };
  if (Date.parse(rec.expiresAt) <= Date.now()) return { pc: null, problem: 'expired' };
  const pc = findPc(rec.pcId);
  return pc ? { pc, problem: null } : { pc: null, problem: 'revoked' };
}

export function resolveUserToken(token: string, pcId: string): { user: UserRecord | null; problem: string | null } {
  const rec = db.userTokens[token];
  if (!rec) return { user: null, problem: 'invalid' };
  if (Date.parse(rec.expiresAt) <= Date.now()) return { user: null, problem: 'expired' };
  if (rec.pcId !== pcId) return { user: null, problem: 'boundElsewhere' };
  const user = findUser(rec.userId);
  return user ? { user, problem: null } : { user: null, problem: 'revoked' };
}

/** PCs a user is currently bound to (targets of user-scoped pushes). */
export function pcIdsForUser(userId: string): string[] {
  return Object.values(db.userTokens)
    .filter((t) => t.userId === userId && Date.parse(t.expiresAt) > Date.now())
    .map((t) => t.pcId);
}

export function revokeUserTokens(userId: string, pcId?: string): void {
  for (const [token, rec] of Object.entries(db.userTokens)) {
    if (rec.userId === userId && (pcId === undefined || rec.pcId === pcId)) delete db.userTokens[token];
  }
  markDirty();
}

export function viewTournament(t: TournamentRecord, userId: string | null): Tournament {
  return {
    id: t.id,
    title: t.title,
    gameId: t.gameId,
    startsAt: t.startsAt,
    state: t.state,
    prizePool: t.prizePool,
    maxPlayers: t.maxPlayers,
    players: t.participants.length,
    joined: userId !== null && t.participants.includes(userId),
    bracket: t.bracket ?? null,
  };
}

export function roomFor(roomId: string): ChatRoomRecord {
  let room = db.chatRooms.find((r) => r.id === roomId);
  if (!room) {
    room = { id: roomId, messages: [], reads: {} };
    db.chatRooms.push(room);
    markDirty();
  }
  return room;
}

export function unreadCount(room: ChatRoomRecord, userId: string): number {
  const marker = room.reads[userId];
  const idx = marker ? room.messages.findIndex((m) => m.id === marker.upTo) : -1;
  return room.messages.slice(idx + 1).filter((m) => m.senderId !== userId).length;
}

export function loyaltyOf(u: UserRecord): Loyalty {
  const levels = [0, 500, 1500, 4000, 10000];
  const nextLevelAt = levels[u.loyaltyLevel + 1] ?? levels[levels.length - 1] ?? 0;
  const perks = [
    ['Welcome bonus 5%'],
    ['Welcome bonus 5%', 'Free drink every 10 hours'],
    ['Bonus 10% on top-ups', 'Free drink every 10 hours', 'Priority booking'],
    ['Bonus 15% on top-ups', 'VIP zone at Standard price on weekdays', 'Priority booking'],
    ['Bonus 20% on top-ups', 'Personal locker', 'Tournament seed'],
  ];
  return { level: u.loyaltyLevel, points: u.loyaltyPoints, nextLevelAt, perks: perks[u.loyaltyLevel] ?? [] };
}

// ---------------------------------------------------------------------------------------------------------------------
// Seed
// ---------------------------------------------------------------------------------------------------------------------

const SEED_VERSION = 3;

const ZONES: { zone: string; from: number; to: number; cols: number; x0: number; y0: number }[] = [
  { zone: 'Standard', from: 1, to: 12, cols: 6, x0: 0, y0: 0 },
  { zone: 'VIP', from: 13, to: 18, cols: 3, x0: 0, y0: 3 },
  { zone: 'Bootcamp', from: 19, to: 24, cols: 6, x0: 0, y0: 6 },
];

function seedPcs(t0: number): PcRecord[] {
  const list: PcRecord[] = [];
  for (const z of ZONES) {
    for (let n = z.from; n <= z.to; n += 1) {
      const idx = n - z.from;
      const name = `PC-${String(n).padStart(2, '0')}`;
      let status: PcRecord['status'] = 'free';
      if (n === 3) status = 'maintenance';
      if (n === 20 || n === 22) status = 'offline';
      if (n === 8) status = 'locked';
      if (n === 11 || n === 16) status = 'booked';
      if (n === 14) status = 'busy';
      list.push({
        id: sid(`pc:${name}`),
        name,
        zone: z.zone,
        number: n,
        hwid: null,
        x: z.x0 + (idx % z.cols),
        y: z.y0 + Math.floor(idx / z.cols),
        ipAddress: `10.0.1.${10 + n}`,
        status,
        currentSessionId: n === 14 ? sid('session:seed-dilnoza') : null,
        agentVersion: '1.4.2',
        shellVersion: '1.4.2',
        lastHeartbeatAt: new Date(t0 - (status === 'offline' ? 3600_000 : 15_000)).toISOString(),
        machineName: null,
        macAddress: `02:42:AC:11:00:${(n + 16).toString(16).padStart(2, '0').toUpperCase()}`,
        signingSecret: null,
        hardware: null,
        metrics: [],
      });
    }
  }
  return list;
}

function seedUsers(t0: number): UserRecord[] {
  const mk = (
    key: string,
    username: string,
    displayName: string,
    role: UserRecord['role'],
    balance: number,
    extra: Partial<UserRecord> = {},
  ): UserRecord => ({
    id: sid(`user:${key}`),
    username,
    displayName,
    avatarUrl: `https://picsum.photos/seed/avatar-${key}/200/200`,
    role,
    balance: uzs(balance),
    loyaltyLevel: 1,
    loyaltyPoints: 620,
    createdAt: new Date(t0 - 120 * 86400_000).toISOString(),
    lastSeenAt: new Date(t0 - 86400_000).toISOString(),
    locale: 'ru',
    flags: [],
    password: 'demo',
    pin: '1234',
    cardId: null,
    loginToken: null,
    bonus: zero(),
    muted: false,
    transient: false,
    ...extra,
  });
  return [
    mk('admin', 'admin', 'Администратор', 'admin', 0, {
      password: 'admin',
      flags: ['staff'],
      loyaltyLevel: 0,
      loyaltyPoints: 0,
      locale: 'ru',
    }),
    mk('alisher', 'alisher', 'Alisher K.', 'member', 4_500_000, {
      cardId: 'CARD-0001',
      loginToken: 'tok-alisher',
      bonus: uzs(50_000),
      loyaltyPoints: 620,
    }),
    mk('dilnoza', 'dilnoza', 'Dilnoza R.', 'vip', 12_000_000, {
      cardId: 'CARD-0002',
      loginToken: 'tok-dilnoza',
      loyaltyLevel: 3,
      loyaltyPoints: 4_820,
      locale: 'uz',
      bonus: uzs(200_000),
    }),
    mk('bekzod', 'bekzod', 'Bekzod T.', 'member', 300_000, {
      cardId: 'CARD-0003',
      loyaltyLevel: 0,
      loyaltyPoints: 140,
      locale: 'en',
    }),
    mk('guest', 'guest', 'Guest', 'guest', 0, { pin: null, loyaltyLevel: 0, loyaltyPoints: 0, password: 'guest' }),
  ];
}

function seedGames(): Game[] {
  type G = [
    slug: string,
    title: string,
    launcher: LauncherType,
    appId: string | null,
    ac: Game['antiCheat'],
    account: boolean,
    age: number,
    cats: string[],
    tags: string[],
    size: number,
    pop: number,
    desc: string,
  ];
  const rows: G[] = [
    [
      'cs2',
      'Counter-Strike 2',
      'steam',
      '730',
      'none',
      false,
      16,
      ['shooter', 'competitive'],
      ['fps', 'esports', 'team'],
      35.2,
      98,
      'The definitive competitive tactical shooter. 5v5 bomb defusal on the Source 2 engine.',
    ],
    [
      'dota2',
      'Dota 2',
      'steam',
      '570',
      'none',
      false,
      12,
      ['moba', 'competitive'],
      ['moba', 'esports', 'strategy'],
      48.7,
      95,
      'Every day, millions of players worldwide enter battle as one of over a hundred Dota heroes.',
    ],
    [
      'valorant',
      'VALORANT',
      'riot',
      'valorant',
      'vanguard',
      true,
      16,
      ['shooter', 'competitive'],
      ['fps', 'hero', 'esports'],
      30.1,
      94,
      'A 5v5 character-based tactical shooter where precise gunplay meets unique agent abilities.',
    ],
    [
      'lol',
      'League of Legends',
      'riot',
      'league_of_legends',
      'vanguard',
      true,
      12,
      ['moba', 'competitive'],
      ['moba', 'esports'],
      22.4,
      88,
      'Team up with friends and test your skills in 5v5 MOBA combat.',
    ],
    [
      'fortnite',
      'Fortnite',
      'epic',
      'fn',
      'eac',
      true,
      12,
      ['battle-royale', 'shooter'],
      ['br', 'building', 'crossplay'],
      42.0,
      91,
      'Drop in, build up, and be the last one standing in the battle royale that started it all.',
    ],
    [
      'apex',
      'Apex Legends',
      'steam',
      '1172470',
      'eac',
      true,
      16,
      ['battle-royale', 'shooter'],
      ['br', 'hero', 'squad'],
      75.3,
      86,
      'Master an ever-growing roster of legendary characters in a squad-based battle royale.',
    ],
    [
      'pubg',
      'PUBG: BATTLEGROUNDS',
      'steam',
      '578080',
      'battlEye',
      false,
      16,
      ['battle-royale', 'shooter'],
      ['br', 'realistic'],
      40.0,
      80,
      'Land on strategic locations, loot weapons and supplies, and survive to become the last team standing.',
    ],
    [
      'gta5',
      'Grand Theft Auto V',
      'steam',
      '271590',
      'battlEye',
      true,
      18,
      ['action', 'open-world'],
      ['open-world', 'story', 'online'],
      105.0,
      84,
      'Explore the sprawling city of Los Santos and Blaine County in the ultimate open-world experience.',
    ],
    [
      'minecraft',
      'Minecraft',
      'exe',
      null,
      'none',
      true,
      6,
      ['sandbox', 'survival'],
      ['sandbox', 'creative', 'family'],
      1.2,
      82,
      'Build, explore and survive in a blocky, procedurally generated 3D world.',
    ],
    [
      'rocketleague',
      'Rocket League',
      'epic',
      'rl',
      'none',
      false,
      6,
      ['sports', 'arcade'],
      ['cars', 'football', 'crossplay'],
      20.5,
      76,
      'Soccer meets driving in this high-octane hybrid of arcade-style sports and vehicular mayhem.',
    ],
    [
      'overwatch2',
      'Overwatch 2',
      'battleNet',
      'pro',
      'none',
      true,
      12,
      ['shooter', 'hero'],
      ['fps', 'hero', 'team'],
      50.0,
      78,
      'Team-based action with a diverse cast of heroes, each with unique abilities.',
    ],
    [
      'fc24',
      'EA SPORTS FC 24',
      'ea',
      'fc24',
      'eac',
      true,
      3,
      ['sports'],
      ['football', 'sim'],
      100.0,
      79,
      "The next chapter in the world's game with HyperMotionV technology and over 19 000 players.",
    ],
    [
      'warzone',
      'Call of Duty: Warzone',
      'battleNet',
      'auks',
      'ricochet',
      true,
      18,
      ['battle-royale', 'shooter'],
      ['br', 'fps'],
      125.0,
      83,
      'Massive combat arenas, Resurgence and Plunder modes in the free-to-play battle royale.',
    ],
    [
      'rust',
      'Rust',
      'steam',
      '252490',
      'eac',
      false,
      18,
      ['survival', 'open-world'],
      ['survival', 'pvp', 'crafting'],
      25.0,
      70,
      'The only aim in Rust is to survive. Overcome struggles such as hunger, thirst and cold.',
    ],
    [
      'thefinals',
      'THE FINALS',
      'steam',
      '2073850',
      'eac',
      false,
      16,
      ['shooter', 'competitive'],
      ['fps', 'destruction', 'team'],
      32.0,
      72,
      'Fight alongside your teammates in a virtual game show with fully destructible arenas.',
    ],
  ];
  return rows.map(
    (
      [
        slug,
        title,
        launcher,
        launcherAppId,
        antiCheat,
        requiresAccount,
        ageRating,
        category,
        tags,
        sizeGb,
        popularity,
        description,
      ],
      i,
    ) => ({
      id: sid(`game:${slug}`),
      title,
      launcher,
      launcherAppId,
      exePath: launcher === 'exe' ? 'D:\\Games\\Minecraft\\MinecraftLauncher.exe' : null,
      args: null,
      installPath: null,
      installed: false,
      category,
      tags,
      coverUrl: `https://picsum.photos/seed/${slug}/600/900`,
      heroUrl: `https://picsum.photos/seed/${slug}-hero/1600/900`,
      videoUrl: slug === 'cs2' ? 'https://cdn.example.uz/trailers/cs2.mp4' : null,
      description,
      ageRating,
      popularity,
      lastPlayedAt: null,
      requiresAccount,
      antiCheat,
      minSpec: { cpu: 'Intel Core i5-9400F', gpu: 'NVIDIA GeForce GTX 1660', ramMb: 8192 },
      sizeGb,
      version: `1.${(i * 7) % 40}.${(i * 13) % 100}`,
    }),
  );
}

function seedApps(): App[] {
  const rows: [slug: string, title: string, exe: string, category: string][] = [
    ['chrome', 'Google Chrome', 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', 'browser'],
    ['discord', 'Discord', 'C:\\Users\\club\\AppData\\Local\\Discord\\Update.exe', 'voice'],
    ['telegram', 'Telegram Desktop', 'C:\\Program Files\\Telegram Desktop\\Telegram.exe', 'tool'],
    ['spotify', 'Spotify', 'C:\\Users\\club\\AppData\\Roaming\\Spotify\\Spotify.exe', 'media'],
    ['obs', 'OBS Studio', 'C:\\Program Files\\obs-studio\\bin\\64bit\\obs64.exe', 'tool'],
    ['vlc', 'VLC media player', 'C:\\Program Files\\VideoLAN\\VLC\\vlc.exe', 'media'],
  ];
  return rows.map(([slug, title, exePath, category]) => ({
    id: sid(`app:${slug}`),
    title,
    exePath,
    iconUrl: `https://picsum.photos/seed/app-${slug}/128/128`,
    category,
    allowed: slug !== 'obs',
  }));
}

function seedTariffs(): Tariff[] {
  const allDays: Tariff['timeWindows'][number]['days'] = ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'];
  return [
    {
      id: sid('tariff:standard'),
      name: 'Standard',
      pricePerHour: uzs(1_200_000),
      minMinutes: 30,
      maxMinutes: 720,
      zones: ['Standard', 'Bootcamp'],
      timeWindows: [],
      isPackage: false,
      packageMinutes: null,
      packagePrice: null,
    },
    {
      id: sid('tariff:vip'),
      name: 'VIP',
      pricePerHour: uzs(2_000_000),
      minMinutes: 30,
      maxMinutes: 720,
      zones: ['VIP'],
      timeWindows: [],
      isPackage: false,
      packageMinutes: null,
      packagePrice: null,
    },
    {
      id: sid('tariff:night'),
      name: 'Night Pack (5h)',
      pricePerHour: uzs(800_000),
      minMinutes: 300,
      maxMinutes: 300,
      zones: [],
      timeWindows: [{ days: allDays, from: '22:00', to: '08:00' }],
      isPackage: true,
      packageMinutes: 300,
      packagePrice: uzs(4_000_000),
    },
  ];
}

function seedProducts(): Product[] {
  const rows: [
    slug: string,
    title: string,
    category: Product['category'],
    price: number,
    stock: number | null,
    tags: string[],
  ][] = [
    ['cola', 'Coca-Cola 0.5L', 'drink', 800_000, 48, ['cold', 'popular']],
    ['redbull', 'Red Bull 0.25L', 'drink', 1_800_000, 20, ['energy']],
    ['water', 'Still water 0.5L', 'drink', 400_000, 100, ['cold']],
    ['americano', 'Americano', 'drink', 1_200_000, null, ['hot', 'coffee']],
    ['lays', "Lay's Crab 90g", 'snack', 1_000_000, 30, ['chips']],
    ['snickers', 'Snickers', 'snack', 700_000, 0, ['chocolate']],
    ['popcorn', 'Popcorn (salted)', 'snack', 900_000, 15, ['popular']],
    ['lavash', 'Lavash with chicken', 'food', 2_800_000, 12, ['hot', 'popular']],
    ['burger', 'Club Burger', 'food', 3_200_000, 8, ['hot']],
    ['somsa', 'Somsa (beef)', 'food', 800_000, 25, ['hot', 'local']],
    ['headset', 'Headset rental (session)', 'service', 1_000_000, null, ['rental']],
    ['tshirt', 'Club T-shirt', 'merch', 12_000_000, 5, ['merch']],
  ];
  return rows.map(([slug, title, category, price, stockQty, tags]) => ({
    id: sid(`product:${slug}`),
    title,
    category,
    price: uzs(price),
    imageUrl: `https://picsum.photos/seed/product-${slug}/400/400`,
    inStock: stockQty === null || stockQty > 0,
    stockQty,
    tags,
  }));
}

function seedTournaments(t0: number, users: UserRecord[]): TournamentRecord[] {
  const ids = users.map((u) => u.id);
  const names = users.map((u) => u.displayName);
  const entry = (rank: number, i: number, score: number): LeaderboardEntry => ({
    rank,
    userId: ids[i] ?? uuid(),
    name: names[i] ?? `Player ${i}`,
    score,
    avatarUrl: users[i]?.avatarUrl ?? null,
  });
  return [
    {
      id: sid('tournament:cs2-cup'),
      title: 'CS2 Weekend Cup',
      gameId: sid('game:cs2'),
      startsAt: new Date(t0 - 3600_000).toISOString(),
      state: 'live',
      prizePool: uzs(150_000_000),
      maxPlayers: 8,
      participants: [ids[1] ?? '', ids[2] ?? '', ids[3] ?? '', ids[4] ?? ''].filter(Boolean),
      bracket: {
        rounds: [
          {
            matches: [
              { id: sid('match:1'), a: ids[1], b: ids[3], winner: ids[1], score: '2-0' },
              { id: sid('match:2'), a: ids[2], b: ids[4], winner: ids[2], score: '2-1' },
            ],
          },
          { matches: [{ id: sid('match:3'), a: ids[1], b: ids[2], winner: null, score: '1-1' }] },
        ],
      },
      leaderboard: [entry(1, 1, 2450), entry(2, 2, 2380), entry(3, 3, 1910), entry(4, 4, 1500)],
      leaderboardUpdatedAt: new Date(t0 - 60_000).toISOString(),
    },
    {
      id: sid('tournament:dota-open'),
      title: 'Dota 2 Open Qualifier',
      gameId: sid('game:dota2'),
      startsAt: new Date(t0 + 2 * 86400_000).toISOString(),
      state: 'registration',
      prizePool: uzs(300_000_000),
      maxPlayers: 40,
      participants: [ids[2] ?? ''].filter(Boolean),
      bracket: null,
      leaderboard: [],
      leaderboardUpdatedAt: new Date(t0).toISOString(),
    },
    {
      id: sid('tournament:valorant-night'),
      title: 'VALORANT Night Clash',
      gameId: sid('game:valorant'),
      startsAt: new Date(t0 + 7 * 86400_000).toISOString(),
      state: 'upcoming',
      prizePool: uzs(80_000_000),
      maxPlayers: 20,
      participants: [],
      bracket: null,
      leaderboard: [],
      leaderboardUpdatedAt: new Date(t0).toISOString(),
    },
  ];
}

function seedManifests(t0: number): Db['updateManifests'] {
  const mk = (channel: UpdateChannel, component: UpdateComponent, version: string): UpdateManifest => ({
    channel,
    component,
    version,
    url: `http://localhost:8080/mock/packages/${component}-${version}.zip`,
    sha256: sha256Hex(`${channel}:${component}:${version}`),
    size: component === 'agent' ? 18_450_112 : 42_118_400,
    signature: Buffer.from(`sig:${component}:${version}`).toString('base64'),
    releaseNotes: `## ${component} ${version}\n\n- Mock release for the ${channel} channel.\n- No functional changes.`,
    mandatory: false,
    publishedAt: new Date(t0 - 5 * 86400_000).toISOString(),
    minAgentVersion: component === 'shell' ? '1.4.0' : null,
  });
  return {
    stable: { agent: mk('stable', 'agent', '1.4.3'), shell: mk('stable', 'shell', '1.4.3') },
    beta: { agent: mk('beta', 'agent', '1.5.0-beta.2'), shell: mk('beta', 'shell', '1.5.0-beta.2') },
  };
}

function seedChat(t0: number, users: UserRecord[], pcs: PcRecord[]): ChatRoomRecord[] {
  const admin = users[0] as UserRecord;
  const alisher = users[1] as UserRecord;
  const dilnoza = users[2] as UserRecord;
  const msg = (
    roomId: string,
    sender: UserRecord,
    text: string,
    minutesAgo: number,
    kind: ChatMessage['kind'] = 'text',
  ): ChatMessage => ({
    id: sid(`msg:${roomId}:${text}`),
    roomId,
    senderId: sender.id,
    senderName: sender.displayName,
    senderRole: sender.role,
    text,
    createdAt: new Date(t0 - minutesAgo * 60_000).toISOString(),
    readAt: null,
    kind,
  });
  const pc01 = pcs[0] as PcRecord;
  return [
    {
      id: 'club',
      messages: [
        msg('club', admin, 'Welcome to the club chat! Be nice.', 240, 'system'),
        msg('club', dilnoza, 'Anyone up for a CS2 5v5 tonight?', 95),
        msg('club', alisher, 'Count me in, PC-05 after 20:00', 90),
        msg('club', admin, 'Reminder: the Weekend Cup finals start at 21:00 in the VIP zone.', 60, 'admin'),
        msg('club', dilnoza, 'GL HF everyone', 30),
      ],
      reads: {},
    },
    {
      id: 'zone:VIP',
      messages: [msg('zone:VIP', admin, 'VIP zone: new headsets installed today.', 120, 'admin')],
      reads: {},
    },
    {
      id: `pc:${pc01.id}`,
      messages: [
        msg(`pc:${pc01.id}`, admin, 'Hi! This is the support chat of your seat. Ask anything.', 180, 'admin'),
        msg(`pc:${pc01.id}`, alisher, 'Hello, can I get a headset?', 170),
        msg(`pc:${pc01.id}`, admin, 'Sure, bringing it over.', 168, 'admin'),
      ],
      reads: {},
    },
  ];
}

function seedAchievements(t0: number, users: UserRecord[]): Record<string, Achievement[]> {
  const defs: [slug: string, title: string, description: string, target: number][] = [
    ['first-blood', 'First Blood', 'Play your first session', 1],
    ['night-owl', 'Night Owl', 'Play 5 sessions after midnight', 5],
    ['marathon', 'Marathon', 'Play 100 hours in total', 100],
    ['big-spender', 'Big Spender', 'Spend 1 000 000 UZS in the shop', 1_000_000],
    ['team-player', 'Team Player', 'Join 3 tournaments', 3],
    ['regular', 'Regular', 'Visit the club 30 days', 30],
  ];
  const out: Record<string, Achievement[]> = {};
  users.forEach((u, ui) => {
    out[u.id] = defs.map(([slug, title, description, target], i) => {
      const current = Math.min(target, Math.floor((target * ((ui + 1) * (i + 2))) / 12));
      return {
        id: sid(`ach:${slug}`),
        title,
        description,
        iconUrl: `https://picsum.photos/seed/ach-${slug}/96/96`,
        unlockedAt: current >= target ? new Date(t0 - (i + 1) * 86400_000).toISOString() : null,
        progress: { current, target },
      };
    });
  });
  return out;
}

function seedPolicy(): Policy {
  const file = new URL('../../../config/policies.example.json', import.meta.url);
  return JSON.parse(readFileSync(file, 'utf8')) as Policy;
}

function seed(): Db {
  const t0 = Date.now();
  const pcs = seedPcs(t0);
  const users = seedUsers(t0);
  const games = seedGames();
  const products = seedProducts();
  const alisher = users[1] as UserRecord;
  const dilnoza = users[2] as UserRecord;
  const bekzod = users[3] as UserRecord;
  const pc05 = pcs[4] as PcRecord;
  const pc14 = pcs[13] as PcRecord;
  const pc11 = pcs[10] as PcRecord;
  const pc16 = pcs[15] as PcRecord;
  const cola = products[0] as Product;
  const lavash = products[7] as Product;
  const tx = (
    userId: string,
    type: TransactionType,
    amount: number,
    after: number,
    description: string,
    hoursAgo: number,
    ref: string | null = null,
  ): Transaction => ({
    id: sid(`tx:${userId}:${hoursAgo}:${description}`),
    userId,
    type,
    amount: uzs(amount),
    balanceAfter: uzs(after),
    description,
    createdAt: new Date(t0 - hoursAgo * 3600_000).toISOString(),
    ref,
  });
  const orderDone: Order = {
    id: sid('order:seed-1'),
    userId: alisher.id,
    pcId: pc05.id,
    items: [
      { productId: cola.id, title: cola.title, qty: 2, price: cola.price },
      { productId: lavash.id, title: lavash.title, qty: 1, price: lavash.price },
    ],
    total: uzs(cola.price.amount * 2 + lavash.price.amount),
    status: 'done',
    createdAt: new Date(t0 - 26 * 3600_000).toISOString(),
    updatedAt: new Date(t0 - 25.5 * 3600_000).toISOString(),
    note: null,
  };
  const orderActive: Order = {
    id: sid('order:seed-2'),
    userId: dilnoza.id,
    pcId: pc14.id,
    items: [{ productId: cola.id, title: cola.title, qty: 1, price: cola.price }],
    total: cola.price,
    status: 'preparing',
    createdAt: new Date(t0 - 4 * 60_000).toISOString(),
    updatedAt: new Date(t0 - 10_000).toISOString(),
    note: 'No ice please',
  };
  const seededSession: SessionRecord = {
    id: sid('session:seed-dilnoza'),
    userId: dilnoza.id,
    pcId: pc14.id,
    tariffId: sid('tariff:vip'),
    state: 'active',
    startedAt: new Date(t0 - 93 * 60_000).toISOString(),
    isPrepaid: true,
    purchasedSec: 180 * 60,
    paidAmount: uzs(6_000_000),
    usedBeforeSec: 0,
    runningSince: new Date(t0 - 93 * 60_000).toISOString(),
    pausedAt: null,
    endedAt: null,
    endReason: null,
    warningsSent: [],
    lastPushAt: 0,
  };
  const day = new Date(t0);
  const slot = (h: number, m = 0): string =>
    new Date(Date.UTC(day.getUTCFullYear(), day.getUTCMonth(), day.getUTCDate(), h, m)).toISOString();
  const pool: PoolAccount[] = [];
  for (const launcher of ['steam', 'epic', 'battleNet', 'riot', 'ea', 'ubisoft', 'exe'] as LauncherType[]) {
    for (let i = 1; i <= 3; i += 1) {
      pool.push({
        launcher,
        username: `club_${launcher}_${String(i).padStart(2, '0')}`,
        password: `P@ss-${launcher}-${i}!`,
        extra:
          launcher === 'steam'
            ? { steamGuardSecret: `SG${i}MOCKSECRET` }
            : launcher === 'riot'
              ? { region: 'EUNE' }
              : null,
      });
    }
  }
  const stats: Record<string, UserStats> = {};
  users.forEach((u, i) => {
    stats[u.id] = {
      totalHours: [0, 312.5, 980.2, 41.0, 0][i] ?? 0,
      sessionsCount: [0, 140, 410, 22, 0][i] ?? 0,
      favoriteGames: [
        { gameId: sid('game:cs2'), hours: [0, 120, 400, 10, 0][i] ?? 0 },
        { gameId: sid('game:dota2'), hours: [0, 80, 210, 12, 0][i] ?? 0 },
        { gameId: sid('game:valorant'), hours: [0, 45, 90, 5, 0][i] ?? 0 },
      ],
      spent: uzs([0, 380_000_000, 1_250_000_000, 42_000_000, 0][i] ?? 0),
      rank: [0, 12, 2, 97, 0][i] ?? 0,
    };
  });
  return {
    seedVersion: SEED_VERSION,
    configVersion: 7,
    catalogVersion: sha256Hex(`catalog:${SEED_VERSION}`).slice(0, 12),
    policy: seedPolicy(),
    pcs,
    users,
    games,
    apps: seedApps(),
    tariffs: seedTariffs(),
    products,
    orders: [orderActive, orderDone],
    transactions: [
      tx(alisher.id, 'purchase', -orderDone.total.amount, 4_500_000, 'Shop order', 26, orderDone.id),
      tx(
        alisher.id,
        'charge',
        -2_400_000,
        4_500_000 + orderDone.total.amount,
        'Session 2h Standard',
        27,
        sid('session:alisher-1'),
      ),
      tx(alisher.id, 'bonus', 50_000, 6_900_000 + orderDone.total.amount, 'Loyalty bonus', 30),
      tx(
        alisher.id,
        'topUp',
        5_000_000,
        6_850_000 + orderDone.total.amount,
        'Top-up via Payme',
        31,
        sid('topup:alisher-1'),
      ),
      tx(
        alisher.id,
        'charge',
        -1_200_000,
        1_850_000 + orderDone.total.amount,
        'Session 1h Standard',
        50,
        sid('session:alisher-0'),
      ),
      tx(
        alisher.id,
        'refund',
        600_000,
        3_050_000 + orderDone.total.amount,
        'Refund: unused 30 min',
        72,
        sid('session:alisher-r'),
      ),
      tx(dilnoza.id, 'charge', -6_000_000, 12_000_000, 'Session 3h VIP', 1.6, seededSession.id),
      tx(dilnoza.id, 'purchase', -cola.price.amount, 18_000_000, 'Shop order', 0.07, orderActive.id),
      tx(
        dilnoza.id,
        'topUp',
        10_000_000,
        18_000_000 + cola.price.amount,
        'Top-up via Click',
        20,
        sid('topup:dilnoza-1'),
      ),
      tx(bekzod.id, 'topUp', 2_000_000, 300_000, 'Top-up in cash', 48, sid('topup:bekzod-1')),
    ],
    topupIntents: [],
    sessions: [seededSession],
    sessionEvents: [],
    chatRooms: seedChat(t0, users, pcs),
    bookings: [
      { id: sid('booking:1'), userId: alisher.id, pcId: pc11.id, from: slot(15), to: slot(17), status: 'reserved' },
      { id: sid('booking:2'), userId: bekzod.id, pcId: pc16.id, from: slot(18), to: slot(20), status: 'confirmed' },
    ],
    tournaments: seedTournaments(t0, users),
    achievements: seedAchievements(t0, users),
    stats,
    lastPlayed: {
      [alisher.id]: {
        [sid('game:cs2')]: new Date(t0 - 86400_000).toISOString(),
        [sid('game:dota2')]: new Date(t0 - 3 * 86400_000).toISOString(),
      },
      [dilnoza.id]: { [sid('game:valorant')]: new Date(t0 - 2 * 3600_000).toISOString() },
    },
    updateManifests: seedManifests(t0),
    agentTokens: {},
    refreshTokens: {},
    userTokens: {},
    qrLogins: {},
    commands: [],
    accountPool: pool,
    leases: [],
    tickets: [],
    anticheatReports: [],
    launchReports: [],
    idempotency: {},
    guestCounter: 0,
  };
}

// ---------------------------------------------------------------------------------------------------------------------
// Persistence
// ---------------------------------------------------------------------------------------------------------------------

const DB_FILE = fileURLToPath(new URL('../.mock-db.json', import.meta.url));
let dirty = false;
let saveTimer: NodeJS.Timeout | null = null;

function load(): Db {
  const reset = process.argv.includes('--reset');
  if (reset && existsSync(DB_FILE)) unlinkSync(DB_FILE);
  if (!reset && existsSync(DB_FILE)) {
    try {
      const parsed = JSON.parse(readFileSync(DB_FILE, 'utf8')) as Partial<Db>;
      if (parsed.seedVersion === SEED_VERSION) {
        // Policy is always taken from the config file so edits there show up without --reset.
        return { ...(parsed as Db), policy: seedPolicy() };
      }
      console.warn(`[db] ${DB_FILE} has seedVersion ${parsed.seedVersion}, expected ${SEED_VERSION}; reseeding`);
    } catch (err) {
      console.warn(`[db] could not read ${DB_FILE}: ${(err as Error).message}; reseeding`);
    }
  }
  const fresh = seed();
  writeFileSync(DB_FILE, JSON.stringify(fresh));
  return fresh;
}

/** The kiosk's demo art (`apps/shell/public/mock-art`), served by this server at `/mock-art/*`. */
export const MOCK_ART_DIR = fileURLToPath(new URL('../../../apps/shell/public/mock-art/', import.meta.url));
const PUBLIC_URL = process.env['MOCK_PUBLIC_URL'] ?? 'http://localhost:8080';
const ART_ALIASES: Record<string, string> = { americano: 'coffee', lays: 'chips', popcorn: 'chips' };

/**
 * Swaps the seed's placeholder image hosts (picsum.photos, unreachable offline) for the local demo art when a matching
 * file exists: `seed/cs2/…` → `cs2-cover.jpg`, `seed/cs2-hero/…` → `cs2-hero.jpg`, `seed/product-cola/…` → `p-cola.jpg`,
 * `seed/app-discord/…` → `app-discord.svg`, `seed/ach-owl/…` → `ach-owl.svg`. Runs on every load, so an existing
 * `.mock-db.json` is fixed too.
 */
function localArt(url: string | null | undefined): string | null | undefined {
  const m = typeof url === 'string' ? /picsum\.photos\/seed\/([^/]+)\//.exec(url) : null;
  if (!m) return url;
  const seed = m[1] as string;
  const candidates: string[] = [];
  if (seed.startsWith('product-')) {
    const slug = seed.slice(8);
    candidates.push(`p-${ART_ALIASES[slug] ?? slug}.jpg`, `p-${slug}.svg`);
  } else if (seed.startsWith('app-') || seed.startsWith('ach-')) candidates.push(`${seed}.svg`);
  else if (seed.endsWith('-hero')) candidates.push(`${seed}.jpg`);
  else candidates.push(`${seed}-cover.jpg`);
  const hit = candidates.find((f) => existsSync(`${MOCK_ART_DIR}${f}`));
  return hit ? `${PUBLIC_URL}/mock-art/${hit}` : url;
}

function withLocalArt(store: Db): Db {
  for (const g of store.games) {
    g.coverUrl = localArt(g.coverUrl) ?? g.coverUrl;
    g.heroUrl = localArt(g.heroUrl) ?? g.heroUrl;
  }
  for (const p of store.products) p.imageUrl = localArt(p.imageUrl) ?? p.imageUrl;
  for (const a of store.apps) a.iconUrl = localArt(a.iconUrl) ?? a.iconUrl;
  for (const list of Object.values(store.achievements))
    for (const a of list) a.iconUrl = localArt(a.iconUrl) ?? a.iconUrl;
  return store;
}

/** The store. Mutate freely, then call {@link markDirty}. */
export const db: Db = withLocalArt(load());

/** Schedules a debounced write of the store to disk. */
export function markDirty(): void {
  dirty = true;
  if (saveTimer) return;
  saveTimer = setTimeout(flushDb, 500);
  saveTimer.unref();
}

/** Writes the store to disk immediately when dirty. */
export function flushDb(): void {
  if (saveTimer) {
    clearTimeout(saveTimer);
    saveTimer = null;
  }
  if (!dirty) return;
  dirty = false;
  try {
    // ponytail: full rewrite on every change; switch to SQLite if the file grows past a few MB.
    writeFileSync(DB_FILE, JSON.stringify(db));
  } catch (err) {
    console.error(`[db] persist failed: ${(err as Error).message}`);
  }
}

export const DB_PATH = DB_FILE;
