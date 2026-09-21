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
  CallAdminCategory,
  PcStatus,
  TELEMETRY_MAX_SAMPLES,
  UpdateChannel,
  UpdateComponent,
  isIpcError,
  type AgentRegisterResponse,
  type HardwareInfo,
  type HeartbeatResponse,
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
  optObj,
  optStr,
  publicPc,
  requireAgent,
  sendCached,
  sid,
  str,
  uuid,
  viewSession,
  type PcRecord,
} from '../db.js';
import { pendingCommands, pushToPc, resolveAck } from '../ws.js';

const PC_STATUSES = Object.values(PcStatus);
const CALL_CATEGORIES = Object.values(CallAdminCategory);
const AC_KINDS = Object.values(AntiCheatKind);
const AC_SEVERITIES = Object.values(AntiCheatSeverity);
const AC_ACTIONS = Object.values(AntiCheatAction);
const CHANNELS = Object.values(UpdateChannel);
const COMPONENTS = Object.values(UpdateComponent);

function assignPc(hwid: string, previousPcId: string | null, machineName: string): PcRecord {
  const byHwid = db.pcs.find((p) => p.hwid === hwid);
  if (byHwid) return byHwid;
  const previous = previousPcId ? db.pcs.find((p) => p.id === previousPcId) : undefined;
  if (previous && (previous.hwid === null || db.pcs.every((p) => p.hwid !== previous.hwid || p === previous))) return previous;
  const free = process.env['MOCK_STRICT_REGISTER'] === '1' ? undefined : db.pcs.find((p) => p.hwid === null && p.status !== 'maintenance' && !p.currentSessionId);
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
  if (!db.pcs.some((p) => p.id === pending.id)) db.pcs.push(pending);
  markDirty();
  throw new ApiError('forbidden', 'PC is pending admin approval', { reason: 'pendingApproval', pcId: pending.id });
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
    if (pc.status === 'offline') pc.status = 'free';
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
    arr(b, 'runningGames', 64);
    int(b, 'offlineQueue', 0);
    bool(b, 'shellConnected');
    pc.lastHeartbeatAt = now();
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
    for (const e of events) {
      if (isObject(e)) console.warn(`[telemetry] ${pc.name}: ${String(e['kind'])} ${JSON.stringify(e['data'] ?? null)}`);
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

  app.post<{ Params: { pcId: string; commandId: string } }>('/agents/:pcId/commands/:commandId/ack', async (req, reply) => {
    const pc = requireAgent(req, req.params.pcId);
    const b = body(req);
    const ok = bool(b, 'ok');
    const rec = db.commands.find((c) => c.envelope.id === req.params.commandId);
    if (!rec || rec.pcId !== pc.id) throw errors.notFound('command');
    resolveAck(req.params.commandId, { ok, error: isIpcError(b['error']) ? b['error'] : null, result: optObj(b, 'result') });
    return reply.code(204).send();
  });

  app.get<{ Querystring: { zone?: string } }>('/pcs', async (req) => {
    const me = requireAgent(req);
    const zone = req.query.zone?.toLowerCase();
    return { items: db.pcs.filter((p) => !zone || p.zone.toLowerCase() === zone).map((p) => publicPc(p, p.id === me.id)) };
  });

  app.get<{ Params: { pcId: string } }>('/pcs/:pcId', async (req) => {
    const me = requireAgent(req);
    const pc = findPc(req.params.pcId);
    if (!pc) throw errors.notFound('pc');
    return publicPc(pc, pc.id === me.id);
  });

  app.post('/support/call-admin', async (req, reply) => {
    const pc = requireAgent(req);
    return idempotent(req, reply, async () => {
      const b = body(req);
      const pcId = str(b, 'pcId', 64);
      if (pcId !== pc.id) throw errors.forbidden('pcMismatch');
      const category = oneOf(b, 'category', CALL_CATEGORIES);
      const message = optStr(b, 'message', 500);
      isoDate(b, 'at');
      const ticket = { ticketId: uuid(), pcId, userId: optStr(b, 'userId', 64), category, message, createdAt: now() };
      db.tickets.push(ticket);
      if (db.tickets.length > 200) db.tickets.splice(0, db.tickets.length - 200);
      markDirty();
      console.log(`[support] ${pc.name} calls admin (${category}): ${message ?? ''}`);
      const ack = setTimeout(() => {
        pushToPc(pcId, 'notification', {
          id: uuid(),
          title: 'Support',
          body: 'An administrator is on the way to your seat.',
          level: 'info',
          ttlSec: 10,
          action: null,
        });
      }, 3000);
      ack.unref();
      return { status: 201, body: { ticketId: ticket.ticketId, createdAt: ticket.createdAt, queuePosition: db.tickets.length } };
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
      const channel = req.params.channel as UpdateChannel;
      const component = req.query.component as UpdateComponent | undefined;
      if (!CHANNELS.includes(channel)) throw errors.notFound('channel');
      if (!component || !COMPONENTS.includes(component)) throw errors.notFound('component');
      if (!req.query.current) throw errors.validation('current', 'required');
      const manifest: UpdateManifest = db.updateManifests[channel][component];
      if (compareSemver(manifest.version, req.query.current) <= 0) return reply.code(204).send();
      return manifest;
    },
  );
}
