/**
 * `GET /ws/agent?token=` (subprotocol `clubshell.v1`, SERVER_API.md §6): per-PC connection registry, server pings,
 * command delivery with ack correlation (at-least-once: un-acked commands are redelivered on reconnect), pushes,
 * an agent event log, and the `/mock` control endpoints used by demos and e2e tests.
 */
import type { FastifyInstance } from 'fastify';
import type { WebSocket } from 'ws';
import {
  WS_MAX_FRAME_BYTES,
  WS_SUBPROTOCOL,
  isServerCommandType,
  isWsPushKind,
  type CommandAck,
  type JsonObject,
  type ServerCommandEnvelope,
  type ServerCommandPayloadMap,
  type ServerCommandType,
  type WsAck,
  type WsFrame,
  type WsPushKind,
  type WsPushPayloadMap,
} from '@clubshell/contracts';
import {
  body,
  db,
  errors,
  findPc,
  isObject,
  markDirty,
  now,
  optInt,
  optObj,
  optStr,
  pcIdsForUser,
  resolveAgentToken,
  str,
  uuid,
  type CommandRecord,
} from './db.js';

interface Conn {
  pcId: string;
  socket: WebSocket;
  connectedAt: string;
  pongTimer: NodeJS.Timeout | null;
}

interface EventLogEntry {
  at: string;
  pcId: string;
  kind: 'connect' | 'disconnect' | 'event' | 'ack' | 'command' | 'push';
  name: string | null;
  payload: unknown;
}

const PING_INTERVAL_MS = 20_000;
const PONG_TIMEOUT_MS = 10_000;
const ACK_TIMEOUT_MS = 30_000;
const EVENT_LOG_MAX = 500;

const conns = new Map<string, Conn>();
const pendingAcks = new Map<string, { resolve: (ack: CommandAck) => void; timer: NodeJS.Timeout }>();
const eventLog: EventLogEntry[] = [];

function logEvent(pcId: string, kind: EventLogEntry['kind'], name: string | null, payload: unknown): void {
  eventLog.push({ at: now(), pcId, kind, name, payload });
  if (eventLog.length > EVENT_LOG_MAX) eventLog.shift();
}

function send(conn: Conn, frame: WsFrame<unknown>): boolean {
  if (conn.socket.readyState !== conn.socket.OPEN) return false;
  conn.socket.send(JSON.stringify(frame));
  return true;
}

export function isConnected(pcId: string): boolean {
  return conns.has(pcId);
}

export function connections(): { pcId: string; name: string; connectedAt: string }[] {
  return [...conns.values()].map((c) => ({
    pcId: c.pcId,
    name: findPc(c.pcId)?.name ?? '?',
    connectedAt: c.connectedAt,
  }));
}

export function pushToPc<K extends WsPushKind>(pcId: string, name: K, payload: WsPushPayloadMap[K]): boolean {
  const conn = conns.get(pcId);
  if (!conn) return false;
  logEvent(pcId, 'push', name, payload);
  return send(conn, { type: 'push', id: uuid(), ts: now(), name, payload });
}

export function pushToUser<K extends WsPushKind>(userId: string, name: K, payload: WsPushPayloadMap[K]): number {
  return pcIdsForUser(userId).filter((pcId) => pushToPc(pcId, name, payload)).length;
}

export function broadcast<K extends WsPushKind>(
  name: K,
  payload: WsPushPayloadMap[K],
  filter?: (pcId: string) => boolean,
): number {
  let n = 0;
  for (const pcId of conns.keys()) {
    if (filter && !filter(pcId)) continue;
    if (pushToPc(pcId, name, payload)) n += 1;
  }
  return n;
}

/** Un-acked, non-expired commands queued for a PC (oldest first). */
export function pendingCommands(pcId: string): CommandRecord[] {
  const t = Date.now();
  return db.commands.filter(
    (c) => c.pcId === pcId && c.ack === null && (c.envelope.expiresAt == null || Date.parse(c.envelope.expiresAt) > t),
  );
}

/** Records an ack for a command (WS ack frame or REST `/commands/{id}/ack`) and resolves any waiter. */
export function resolveAck(commandId: string, ack: CommandAck): boolean {
  const rec = db.commands.find((c) => c.envelope.id === commandId);
  if (!rec) return false;
  rec.ack = ack;
  rec.ackedAt = now();
  markDirty();
  logEvent(rec.pcId, 'ack', rec.envelope.name, ack);
  const waiter = pendingAcks.get(commandId);
  if (waiter) {
    clearTimeout(waiter.timer);
    pendingAcks.delete(commandId);
    waiter.resolve(ack);
  }
  return true;
}

function commandFrame(env: ServerCommandEnvelope): WsFrame<unknown> {
  return {
    type: 'command',
    id: env.id,
    ts: env.ts,
    name: env.name,
    payload: env.payload ?? null,
    supersedes: env.supersedes ?? null,
    expiresAt: env.expiresAt ?? null,
  };
}

/**
 * Queues a command for a PC and, when it is connected, sends it and waits for the ack (30 s). Offline PCs get the
 * command on reconnect / via `GET /agents/{pcId}/commands`; the returned ack then reports `agentOffline`.
 */
export function sendCommand<T extends ServerCommandType>(
  pcId: string,
  name: T,
  payload: ServerCommandPayloadMap[T] | null,
  opts: { expiresInSec?: number | null; supersedes?: string | null; issuedBy?: string | null } = {},
): Promise<CommandAck> {
  const envelope: ServerCommandEnvelope = {
    id: uuid(),
    ts: now(),
    name,
    payload,
    issuedBy: opts.issuedBy ?? 'mock-admin',
    supersedes: opts.supersedes ?? null,
    expiresAt: opts.expiresInSec ? new Date(Date.now() + opts.expiresInSec * 1000).toISOString() : null,
  };
  db.commands.push({ pcId, envelope, ack: null, ackedAt: null });
  if (db.commands.length > 1000) db.commands.splice(0, db.commands.length - 1000);
  markDirty();
  logEvent(pcId, 'command', name, payload);
  const conn = conns.get(pcId);
  if (!conn || !send(conn, commandFrame(envelope))) {
    return Promise.resolve({
      ok: false,
      error: {
        code: 'agentOffline',
        message: 'PC is not connected; command queued',
        details: { commandId: envelope.id },
      },
      result: null,
    });
  }
  return new Promise<CommandAck>((resolve) => {
    const timer = setTimeout(() => {
      pendingAcks.delete(envelope.id);
      resolve({
        ok: false,
        error: {
          code: 'timeout',
          message: 'No ack within 30 s; command stays queued',
          details: { commandId: envelope.id },
        },
        result: null,
      });
    }, ACK_TIMEOUT_MS);
    pendingAcks.set(envelope.id, { resolve, timer });
  });
}

function handleFrame(conn: Conn, raw: unknown): void {
  if (!isObject(raw) || typeof raw['type'] !== 'string') return;
  const frame = raw as unknown as WsFrame<unknown>;
  switch (frame.type) {
    case 'pong':
      if (conn.pongTimer) {
        clearTimeout(conn.pongTimer);
        conn.pongTimer = null;
      }
      return;
    case 'ping':
      send(conn, { type: 'pong', id: frame.id, ts: now(), payload: null });
      return;
    case 'ack': {
      const ack = frame.ack;
      if (!isObject(ack) || typeof ack['id'] !== 'string') return;
      const a = ack as unknown as WsAck;
      resolveAck(a.id, { ok: a.ok === true, error: a.error ?? null, result: a.result ?? null });
      return;
    }
    case 'event':
      logEvent(conn.pcId, 'event', frame.name ?? null, frame.payload);
      return;
    default:
      return;
  }
}

function pingAll(): void {
  for (const conn of conns.values()) {
    if (conn.pongTimer) continue; // still waiting for the previous pong
    if (!send(conn, { type: 'ping', id: uuid(), ts: now(), payload: null })) continue;
    conn.pongTimer = setTimeout(() => {
      conn.pongTimer = null;
      console.warn(`[ws] ${findPc(conn.pcId)?.name ?? conn.pcId}: no pong within ${PONG_TIMEOUT_MS} ms, terminating`);
      conn.socket.terminate();
    }, PONG_TIMEOUT_MS);
  }
}

function detach(conn: Conn): void {
  if (conn.pongTimer) clearTimeout(conn.pongTimer);
  if (conns.get(conn.pcId) === conn) conns.delete(conn.pcId);
}

export function registerWs(app: FastifyInstance): void {
  app.get('/ws/agent', { websocket: true }, (socket, req) => {
    const token = (req.query as { token?: string }).token ?? '';
    const { pc, problem } = resolveAgentToken(token);
    if (!pc) {
      socket.close(4401, `unauthorized: ${problem ?? 'invalid'}`);
      return;
    }
    if (socket.protocol && socket.protocol !== WS_SUBPROTOCOL) {
      socket.close(4426, `unsupported subprotocol '${socket.protocol}', expected ${WS_SUBPROTOCOL}`);
      return;
    }
    const previous = conns.get(pc.id);
    if (previous) {
      detach(previous);
      previous.socket.close(1000, 'replaced by a new connection');
    }
    const conn: Conn = { pcId: pc.id, socket, connectedAt: now(), pongTimer: null };
    conns.set(pc.id, conn);
    logEvent(pc.id, 'connect', null, { protocol: socket.protocol || null });
    console.log(`[ws] ${pc.name} connected`);
    if (pc.status === 'offline') {
      pc.status = 'free';
      markDirty();
    }

    socket.on('message', (data, isBinary) => {
      if (isBinary) return;
      const text = data.toString();
      if (text.length > WS_MAX_FRAME_BYTES) {
        socket.close(1009, 'frame too large');
        return;
      }
      try {
        handleFrame(conn, JSON.parse(text));
      } catch {
        console.warn(`[ws] ${pc.name}: malformed frame ignored`);
      }
    });
    socket.on('close', (code, reason) => {
      detach(conn);
      logEvent(pc.id, 'disconnect', null, { code, reason: reason.toString() });
      console.log(`[ws] ${pc.name} disconnected (${code})`);
    });
    socket.on('error', (err) => console.warn(`[ws] ${pc.name}: ${err.message}`));

    // At-least-once delivery: replay everything that was queued while the PC was away.
    for (const rec of pendingCommands(pc.id)) send(conn, commandFrame(rec.envelope));
  });

  const pingTimer = setInterval(pingAll, PING_INTERVAL_MS);
  app.addHook('onClose', async () => {
    clearInterval(pingTimer);
    for (const conn of conns.values()) conn.socket.close(1001, 'server shutting down');
    for (const waiter of pendingAcks.values()) clearTimeout(waiter.timer);
  });

  // ---- mock control endpoints ---------------------------------------------------------------------------------------

  const injectCommand = async (pcId: string, b: JsonObject): Promise<unknown> => {
    if (!findPc(pcId)) throw errors.notFound('pc');
    const name = typeof b['type'] === 'string' ? b['type'] : typeof b['name'] === 'string' ? b['name'] : '';
    if (!isServerCommandType(name)) throw errors.validation('type', 'enum');
    const payload = optObj(b, 'payload') as ServerCommandPayloadMap[typeof name] | null;
    const delivered = isConnected(pcId);
    const ack = await sendCommand(pcId, name, payload, {
      expiresInSec: optInt(b, 'expiresInSec', 1),
      supersedes: optStr(b, 'supersedes'),
      issuedBy: optStr(b, 'issuedBy'),
    });
    const command = db.commands[db.commands.length - 1]?.envelope ?? null;
    return { delivered, command, ack };
  };

  app.post<{ Params: { pcId: string } }>('/mock/pcs/:pcId/command', async (req) =>
    injectCommand(req.params.pcId, body(req)),
  );
  app.post('/_mock/command', async (req) => {
    const b = body(req);
    return injectCommand(str(b, 'pcId', 64), b);
  });

  app.post('/_mock/push', async (req) => {
    const b = body(req);
    const name = str(b, 'name', 64);
    if (!isWsPushKind(name)) throw errors.validation('name', 'enum');
    const payload = optObj(b, 'payload');
    if (!payload) throw errors.validation('payload', 'required');
    const pcId = optStr(b, 'pcId');
    const userId = optStr(b, 'userId');
    const typed = payload as unknown as WsPushPayloadMap[typeof name];
    let delivered = 0;
    if (pcId) delivered = pushToPc(pcId, name, typed) ? 1 : 0;
    else if (userId) delivered = pushToUser(userId, name, typed);
    else delivered = broadcast(name, typed);
    return { delivered };
  });

  app.get<{ Querystring: { pcId?: string; limit?: string } }>('/mock/events', async (req) => {
    const limit = Math.min(EVENT_LOG_MAX, Number.parseInt(req.query.limit ?? '100', 10) || 100);
    const items = eventLog.filter((e) => !req.query.pcId || e.pcId === req.query.pcId).slice(-limit);
    return { items, total: eventLog.length };
  });

  app.get('/mock/connections', async () => ({ items: connections() }));

  app.get<{ Querystring: { pcId?: string } }>('/mock/commands', async (req) => ({
    items: db.commands.filter((c) => !req.query.pcId || c.pcId === req.query.pcId).slice(-100),
  }));

  app.post('/mock/reset-tokens', async () => {
    db.agentTokens = {};
    db.refreshTokens = {};
    db.userTokens = {};
    markDirty();
    for (const conn of conns.values()) conn.socket.close(4401, 'tokens reset');
    return { ok: true };
  });
}
