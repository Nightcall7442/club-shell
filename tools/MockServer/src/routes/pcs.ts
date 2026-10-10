/**
 * Agent lifecycle (SERVER_API.md §4.1, §4.2, §4.13, §4.14): register/refresh, heartbeat, telemetry, config and
 * policies with ETags, REST command fallback + ack, PC lists, call-admin tickets, anti-cheat reports, update manifests.
 */
import { randomBytes } from 'node:crypto';
import type { FastifyInstance } from 'fastify';
import {
  AntiCheatAction,
  AntiCheatKind,
  AntiCheatSeverity,
  GamesVolumeOwner,
  PcStatus,
  TELEMETRY_MAX_SAMPLES,
  UpdateChannel,
  UpdateComponent,
  isIpcError,
  type AgentRegisterResponse,
  type HardwareInfo,
  type HeartbeatGamesVolume,
  type HeartbeatResponse,
  type JsonObject,
  type PcMetrics,
  type UpdateManifest,
} from '@clubshell/contracts';
import {
  ApiError,
  agentConfigFor,
  arr,
  body,
  bool,
  compareSemver,
  createAgentTokens,
  db,
  errors,
  findPc,
  idempotent,
  int,
  isObject,
  isoDate,
  markDirty,
  now,
  obj,
  oneOf,
  openSessionForPc,
  optBool,
  optObj,
  optStr,
  optionalUser,
  publicPc,
  requireAgent,
  settleStatus,
  sendCached,
  sid,
  str,
  viewSession,
  type CallRecord,
  type PcRecord,
  knownValues,
  type Known,
} from '../db.js';
import { CALL_CATEGORIES, USER_REPORT, callMessage, callerOf, insertCall, queuePosition } from '../calls.js';
import { pendingCommands, resolveAck } from '../ws.js';
import { realTelemetry } from '../health.js';

const PC_STATUSES = knownValues(PcStatus);
const VOLUME_OWNERS = Object.values(GamesVolumeOwner);
const AC_KINDS = knownValues(AntiCheatKind);
const AC_SEVERITIES = Object.values(AntiCheatSeverity);
const AC_ACTIONS = Object.values(AntiCheatAction);
const CHANNELS = knownValues(UpdateChannel);
const COMPONENTS = knownValues(UpdateComponent);

/**
 * The PC a registering Agent becomes: its own again (by hwid or `previousPcId`), else a free seeded PC of the demo hall,
 * else a new PC pending the owner's approval (403 `pendingApproval`). `MOCK_AUTO_APPROVE_PCS=1` (the admin e2e tests,
 * as the server's `Club:AutoApprovePcs`) makes every new Agent a new approved PC instead, never a seeded one.
 */
function assignPc(hwid: string, previousPcId: string | null, machineName: string): PcRecord {
  const byHwid = db.pcs.find((p) => p.hwid === hwid);
  if (byHwid) return byHwid;
  const previous = previousPcId ? db.pcs.find((p) => p.id === previousPcId) : undefined;
  if (previous && (previous.hwid === null || db.pcs.every((p) => p.hwid !== previous.hwid || p === previous)))
    return previous;
  const autoApprove = process.env['MOCK_AUTO_APPROVE_PCS'] === '1';
  const free =
    process.env['MOCK_STRICT_REGISTER'] === '1' || autoApprove
      ? undefined
      : db.pcs.find((p) => p.hwid === null && p.status !== 'maintenance' && !p.currentSessionId);
  if (free) return free;
  const number = Math.max(...db.pcs.map((p) => p.number)) + 1;
  const pending: PcRecord = {
    id: sid(`pc:pending:${hwid}`),
    name: `PC-${String(number).padStart(2, '0')}`,
    zone: 'Standard',
    number,
    hwid,
    x: (number - 1) % 6,
    y: 8 + Math.floor((number - 19) / 6),
    ipAddress: '0.0.0.0',
    status: 'maintenance',
    currentSessionId: null,
    agentVersion: '0.0.0',
    shellVersion: '0.0.0',
    lastHeartbeatAt: now(),
    machineName,
    macAddress: null,
    signingSecret: null,
    hardware: null,
    metrics: [],
  };
  if (autoApprove) {
    const approved: PcRecord = { ...pending, id: sid(`pc:${hwid}`), status: 'offline', registered: true };
    db.pcs.push(approved);
    markDirty();
    return approved;
  }
  if (!db.pcs.some((p) => p.id === pending.id)) db.pcs.push(pending);
  markDirty();
  throw new ApiError('forbidden', 'PC is pending admin approval', { reason: 'pendingApproval', pcId: pending.id });
}

/**
 * The inbox call a telemetry event carries, as the server copies it: `callAdmin` (its data is the ticket the PC could not
 * send: the category of the contract, a message of at most 500, a parsable `at`, this PC) or a «report a problem» text
 * (`shellClientError` starting `[user report] `). Null — not a call; `invalid` — a call that is skipped.
 */
function callOfEvent(pc: PcRecord, e: Record<string, unknown>): Parameters<typeof insertCall>[0] | 'invalid' | null {
  const data = isObject(e['data']) ? e['data'] : null;
  if (e['kind'] === 'callAdmin') {
    if (!data) return 'invalid';
    const category = data['category'];
    const at = typeof data['at'] === 'string' ? Date.parse(data['at']) : Number.NaN;
    if (typeof category !== 'string' || !(CALL_CATEGORIES as readonly string[]).includes(category)) return 'invalid';
    if (Number.isNaN(at) || data['pcId'] !== pc.id) return 'invalid';
    // As the route: a message over 500 characters is not a valid call (the server skips it, it does not cut it).
    if (typeof data['message'] === 'string' && data['message'].trim().length > 500) return 'invalid';
    const userId = typeof data['userId'] === 'string' ? data['userId'] : null;
    return {
      pc,
      userId: callerOf(pc.id, null, userId),
      category: category as (typeof CALL_CATEGORIES)[number],
      message: callMessage(data['message']),
      source: 'telemetry',
      at: new Date(at).toISOString(),
    };
  }
  if (e['kind'] === 'shellClientError' && data && typeof data['message'] === 'string') {
    if (!data['message'].startsWith(USER_REPORT)) return null;
    const message = callMessage(data['message'].slice(USER_REPORT.length));
    const at = typeof e['at'] === 'string' ? Date.parse(e['at']) : Number.NaN;
    if (!message || Number.isNaN(at)) return 'invalid';
    return {
      pc,
      userId: callerOf(pc.id, null, null),
      category: 'problem',
      message,
      source: 'report',
      at: new Date(at).toISOString(),
    };
  }
  return null;
}

/**
 * A heartbeat's `gamesVolume` as the server keeps it (D-73): null when the Agent sent none (an older one), its null fields
 * left out; a bad one is refused as the server's binder would.
 */
function gamesVolumeOf(b: JsonObject): HeartbeatGamesVolume | null {
  const v = optObj(b, 'gamesVolume');
  if (!v) return null;
  const mounted = optBool(v, 'mounted');
  const driveLetter = optStr(v, 'driveLetter', 1);
  const since = optStr(v, 'since', 64);
  if (since !== null && Number.isNaN(Date.parse(since))) throw errors.validation('gamesVolume.since', 'format');
  return {
    owner: oneOf(v, 'owner', VOLUME_OWNERS),
    ...(mounted !== null ? { mounted } : {}),
    ...(driveLetter !== null ? { driveLetter } : {}),
    ...(since !== null ? { since: new Date(since).toISOString() } : {}),
  };
}

export function pcsRoutes(app: FastifyInstance): void {
  app.post('/agents/register', async (req): Promise<AgentRegisterResponse> => {
    const clubKey = req.headers['x-club-key'];
    if (typeof clubKey !== 'string' || clubKey.length === 0) throw errors.unauthorized('clubKey');
    const b = body(req);
    const hwid = str(b, 'hwid', 128);
    const machineName = str(b, 'machineName', 64);
    const agentVersion = str(b, 'agentVersion', 32);
    const hardware = obj(b, 'hardware') as unknown as HardwareInfo;
    const ipAddress = str(b, 'ipAddress', 64);
    const macAddress = str(b, 'macAddress', 32);
    const pc = assignPc(hwid, optStr(b, 'previousPcId', 64), machineName);
    pc.hwid = hwid;
    pc.machineName = machineName;
    pc.agentVersion = agentVersion;
    pc.ipAddress = ipAddress;
    pc.macAddress = macAddress;
    pc.hardware = hardware;
    pc.lastHeartbeatAt = now();
    // As on the server: a registered PC is offline until its first heartbeat or socket (the seeded hall is not).
    pc.registered = true;
    settleStatus(pc);
    pc.signingSecret = randomBytes(32).toString('base64');
    const tokens = createAgentTokens(pc);
    markDirty();
    console.log(`[agents] ${pc.name} registered (${machineName}, ${hwid.slice(0, 12)}…)`);
    return {
      pcId: pc.id,
      pc: publicPc(pc, true),
      accessToken: tokens.accessToken,
      refreshToken: tokens.refreshToken,
      signingSecret: pc.signingSecret,
      expiresAt: tokens.expiresAt,
      serverTime: now(),
      config: agentConfigFor(pc),
    };
  });

  app.post('/agents/refresh', async (req) => {
    const b = body(req);
    const refreshToken = str(b, 'refreshToken', 256);
    const hwid = str(b, 'hwid', 128);
    const rec = db.refreshTokens[refreshToken];
    if (!rec) throw errors.unauthorized('revoked');
    if (rec.used) throw errors.unauthorized('reused');
    if (Date.parse(rec.expiresAt) <= Date.now()) throw errors.unauthorized('expired');
    const pc = findPc(rec.pcId);
    if (!pc || pc.hwid !== hwid) throw errors.unauthorized('revoked');
    rec.used = true;
    const tokens = createAgentTokens(pc);
    return { accessToken: tokens.accessToken, refreshToken: tokens.refreshToken, expiresAt: tokens.expiresAt };
  });

  app.post<{ Params: { pcId: string } }>('/agents/:pcId/heartbeat', async (req): Promise<HeartbeatResponse> => {
    const pc = requireAgent(req, req.params.pcId);
    const b = body(req);
    const status = oneOf(b, 'status', PC_STATUSES);
    pc.agentVersion = str(b, 'agentVersion', 32);
    pc.shellVersion = str(b, 'shellVersion', 32);
    int(b, 'uptimeSec', 0);
    pc.ipAddress = str(b, 'ipAddress', 64);
    int(b, 'policyVersion', 0);
    // Kept for the hall map (D-71): the game on the seat. Each entry must be whole, as the server's contract types it.
    pc.runningGames = arr(b, 'runningGames', 64).map((g, i) => {
      if (!isObject(g)) throw errors.validation(`runningGames[${i}]`, 'format');
      return { gameId: str(g, 'gameId', 64), pid: int(g, 'pid', 0), startedAt: isoDate(g, 'startedAt') };
    });
    // What a move onto this PC is checked against (D-59): an unsent offline session refuses it.
    pc.offlineQueue = int(b, 'offlineQueue', 0);
    pc.reportedSessionId = optStr(b, 'currentSessionId', 64);
    bool(b, 'shellConnected');
    pc.gamesVolume = gamesVolumeOf(b);
    pc.lastHeartbeatAt = now();
    pc.seen = true;
    const session = openSessionForPc(pc.id);
    if (pc.status !== 'maintenance') {
      if (session) pc.status = session.state === 'locked' ? 'locked' : 'busy';
      else pc.status = status === 'locked' ? 'locked' : 'free';
    }
    pc.currentSessionId = session?.id ?? null;
    markDirty();
    return {
      serverTime: now(),
      pcStatus: pc.status,
      policyVersion: db.policy.version,
      configVersion: db.configVersion,
      catalogVersion: db.catalogVersion,
      pendingCommands: pendingCommands(pc.id).length,
      session: session ? viewSession(session) : null,
    };
  });

  app.post<{ Params: { pcId: string } }>('/agents/:pcId/telemetry', async (req, reply) => {
    const pc = requireAgent(req, req.params.pcId);
    const b = body(req);
    const samples = arr(b, 'samples', TELEMETRY_MAX_SAMPLES);
    const events = arr(b, 'events', 1000);
    samples.forEach((s, i) => {
      if (!isObject(s)) throw errors.validation(`samples[${i}]`, 'format');
    });
    const hardware = optObj(b, 'hardware');
    if (hardware) pc.hardware = hardware as unknown as HardwareInfo;
    pc.metrics = [...pc.metrics, ...(samples as unknown as PcMetrics[])].slice(-TELEMETRY_MAX_SAMPLES);
    realTelemetry(pc, samples as unknown as PcMetrics[]);
    for (const e of events) {
      if (!isObject(e)) continue;
      console.warn(`[telemetry] ${pc.name}: ${String(e['kind'])} ${JSON.stringify(e['data'] ?? null)}`);
      // Calls and problem reports reach the desk's inbox; an invalid one is skipped, the rest of the batch still counts.
      const call = callOfEvent(pc, e);
      if (call === 'invalid') console.warn(`[telemetry] ${pc.name}: invalid ${String(e['kind'])} skipped`);
      else if (call) insertCall(call);
    }
    markDirty();
    return reply.code(204).send();
  });

  app.get<{ Params: { pcId: string } }>('/agents/:pcId/config', async (req, reply) => {
    const pc = requireAgent(req, req.params.pcId);
    return sendCached(req, reply, agentConfigFor(pc));
  });

  app.get<{ Params: { pcId: string } }>('/agents/:pcId/policies', async (req, reply) => {
    requireAgent(req, req.params.pcId);
    return sendCached(req, reply, db.policy);
  });

  app.get<{ Params: { pcId: string } }>('/agents/:pcId/commands', async (req) => {
    const pc = requireAgent(req, req.params.pcId);
    return { items: pendingCommands(pc.id).map((c) => c.envelope) };
  });

  app.post<{ Params: { pcId: string; commandId: string } }>(
    '/agents/:pcId/commands/:commandId/ack',
    async (req, reply) => {
      const pc = requireAgent(req, req.params.pcId);
      const b = body(req);
      const ok = bool(b, 'ok');
      const rec = db.commands.find((c) => c.envelope.id === req.params.commandId);
      if (!rec || rec.pcId !== pc.id) throw errors.notFound('command');
      resolveAck(req.params.commandId, {
        ok,
        error: isIpcError(b['error']) ? b['error'] : null,
        result: optObj(b, 'result'),
      });
      return reply.code(204).send();
    },
  );

  app.get<{ Querystring: { zone?: string } }>('/pcs', async (req) => {
    const me = requireAgent(req);
    const zone = req.query.zone?.toLowerCase();
    return {
      items: db.pcs.filter((p) => !zone || p.zone.toLowerCase() === zone).map((p) => publicPc(p, p.id === me.id)),
    };
  });

  app.get<{ Params: { pcId: string } }>('/pcs/:pcId', async (req) => {
    const me = requireAgent(req);
    const pc = findPc(req.params.pcId);
    if (!pc) throw errors.notFound('pc');
    return publicPc(pc, pc.id === me.id);
  });

  /**
   * «Позвать администратора» (contract `callAdmin`, D-62): one call per (PC, `at`) — the agent sends the same `at` here and
   * in its telemetry copy. The player is the signed-in user, else a body `userId` holding a sign-in on this PC, else the
   * open session's player. 201 `{ticketId, createdAt, queuePosition}`; no other 4xx for a valid call (the agent would
   * show it to the player).
   */
  app.post('/support/call-admin', async (req, reply) => {
    const pc = requireAgent(req);
    return idempotent(req, reply, async () => {
      const b = body(req);
      // The body first, then whose PC it is: the server's order.
      const pcId = str(b, 'pcId', 64);
      const category = oneOf(b, 'category', CALL_CATEGORIES);
      const message = optStr(b, 'message', 10_000)?.trim() || null;
      if (message && message.length > 500) throw errors.validation('message', 'max');
      const at = isoDate(b, 'at');
      if (pcId !== pc.id) throw errors.forbidden('pcMismatch');
      const userId = callerOf(pc.id, optionalUser(req)?.id ?? null, optStr(b, 'userId', 64));
      const stored = insertCall({ pc, userId, category, message: callMessage(message), source: 'direct', at });
      const call = stored?.call as CallRecord;
      return {
        status: 201,
        body: { ticketId: call.id, createdAt: call.receivedAt, queuePosition: queuePosition(call) },
      };
    });
  });

  app.post('/anticheat/report', async (req, reply) => {
    const pc = requireAgent(req);
    const b = body(req);
    const report = {
      pcId: str(b, 'pcId', 64),
      sessionId: optStr(b, 'sessionId', 64),
      userId: optStr(b, 'userId', 64),
      gameId: optStr(b, 'gameId', 64),
      kind: oneOf(b, 'kind', AC_KINDS),
      check: str(b, 'check', 64),
      severity: oneOf(b, 'severity', AC_SEVERITIES),
      details: obj(b, 'details'),
      at: isoDate(b, 'at'),
      actionTaken: oneOf(b, 'actionTaken', AC_ACTIONS),
    };
    if (report.pcId !== pc.id) throw errors.forbidden('pcMismatch');
    db.anticheatReports.push(report);
    if (db.anticheatReports.length > 200) db.anticheatReports.splice(0, db.anticheatReports.length - 200);
    markDirty();
    console.warn(`[anticheat] ${pc.name}: ${report.kind}/${report.check} (${report.severity}) → ${report.actionTaken}`);
    return reply.code(204).send();
  });

  app.get<{ Params: { channel: string }; Querystring: { component?: string; current?: string; arch?: string } }>(
    '/updates/:channel/manifest',
    async (req, reply) => {
      requireAgent(req);
      const channel = req.params.channel as Known<UpdateChannel>;
      const component = req.query.component as Known<UpdateComponent> | undefined;
      if (!CHANNELS.includes(channel)) throw errors.notFound('channel');
      if (!component || !COMPONENTS.includes(component)) throw errors.notFound('component');
      if (!req.query.current) throw errors.validation('current', 'required');
      const manifest: UpdateManifest = db.updateManifests[channel][component];
      if (compareSemver(manifest.version, req.query.current) <= 0) return reply.code(204).send();
      return manifest;
    },
  );
}
