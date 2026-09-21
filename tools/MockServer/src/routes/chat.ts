/**
 * Chat (SERVER_API.md §4.10), booking (§4.11) and tournaments (§4.12). Chat rooms are `club`, `zone:<zone>`,
 * `pc:<pcId>` and `dm:<a>:<b>`; a mock administrator answers every message in `pc:`/`dm:`/`club` rooms 2 s later via
 * the `chatMessage` push.
 */
import type { FastifyInstance } from 'fastify';
import {
  ANONYMOUS_USER_ID,
  CHAT_HISTORY_DEFAULT_LIMIT,
  CHAT_HISTORY_MAX_LIMIT,
  ChatRooms,
  TournamentState,
  type Booking,
  type BookingSeatsResponse,
  type ChatHistoryResponse,
  type ChatMessage,
  type Seat,
} from '@clubshell/contracts';
import {
  ApiError,
  body,
  db,
  errors,
  findPc,
  idempotent,
  isoDate,
  markDirty,
  now,
  optionalUser,
  requireAgent,
  requireUser,
  roomFor,
  str,
  unreadCount,
  uuid,
  viewTournament,
  type ChatRoomRecord,
  type PcRecord,
  type UserRecord,
} from '../db.js';
import { broadcast, pushToPc, pushToUser } from '../ws.js';

const TOURNAMENT_STATES = Object.values(TournamentState);
const SLOT_MINUTES = 30;
const OPEN_FROM = '10:00';
const OPEN_TO = '02:00';
const MAX_BOOKING_MINUTES = 240;
const ADMIN_REPLY_MS = 2_000;
const ADMIN_REPLIES = [
  'Got it, I will be there in a minute.',
  'Thanks for reaching out! Anything else?',
  'Sure, checking that for you now.',
  'Noted. Have a good game!',
  'On my way to your seat.',
];

interface PendingReply {
  roomId: string;
  dueAt: number;
  index: number;
}

const pendingReplies: PendingReply[] = [];
let replyCounter = 0;

function assertMember(roomId: string, pc: PcRecord, user: UserRecord): void {
  if (user.role === 'admin' || roomId === ChatRooms.Club) return;
  if (roomId.startsWith('pc:')) {
    if (roomId !== ChatRooms.forPc(pc.id)) throw errors.forbidden('notMember');
    return;
  }
  if (roomId.startsWith('zone:')) {
    if (roomId.slice(5).toLowerCase() !== pc.zone.toLowerCase()) throw errors.forbidden('notMember');
    return;
  }
  if (roomId.startsWith('dm:')) {
    const [, a, b] = roomId.split(':');
    if (a !== user.id && b !== user.id) throw errors.forbidden('notMember');
    return;
  }
  throw errors.notFound('room');
}

function withReadAt(room: ChatRoomRecord, userId: string): ChatMessage[] {
  const marker = room.reads[userId];
  const idx = marker ? room.messages.findIndex((m) => m.id === marker.upTo) : -1;
  return room.messages.map((m, i) => ({ ...m, readAt: m.senderId === userId ? m.createdAt : i <= idx && marker ? marker.at : null }));
}

function deliver(room: ChatRoomRecord, msg: ChatMessage, exceptPcId: string | null): void {
  if (room.id === ChatRooms.Club) {
    broadcast('chatMessage', msg, (pcId) => pcId !== exceptPcId);
  } else if (room.id.startsWith('zone:')) {
    const zone = room.id.slice(5).toLowerCase();
    broadcast('chatMessage', msg, (pcId) => pcId !== exceptPcId && findPc(pcId)?.zone.toLowerCase() === zone);
  } else if (room.id.startsWith('dm:')) {
    const [, a, b] = room.id.split(':');
    for (const uid of [a, b]) if (uid && uid !== msg.senderId) pushToUser(uid, 'chatMessage', msg);
  } else if (room.id.startsWith('pc:')) {
    const pcId = room.id.slice(3);
    if (pcId !== exceptPcId) pushToPc(pcId, 'chatMessage', msg);
  }
}

/** Wall-clock tick: send the queued admin auto-replies. */
export function tickChat(nowMs: number): void {
  const admin = db.users.find((u) => u.role === 'admin');
  while (pendingReplies.length > 0 && (pendingReplies[0]?.dueAt ?? Infinity) <= nowMs) {
    const pending = pendingReplies.shift();
    if (!pending || !admin) continue;
    const room = roomFor(pending.roomId);
    const msg: ChatMessage = {
      id: uuid(),
      roomId: room.id,
      senderId: admin.id,
      senderName: admin.displayName,
      senderRole: admin.role,
      text: ADMIN_REPLIES[pending.index % ADMIN_REPLIES.length] ?? ADMIN_REPLIES[0] ?? '',
      createdAt: now(),
      readAt: null,
      kind: 'admin',
    };
    room.messages.push(msg);
    if (room.messages.length > 500) room.messages.splice(0, room.messages.length - 500);
    markDirty();
    deliver(room, msg, null);
  }
}

function seatOf(p: PcRecord): Seat {
  return { pcId: p.id, name: p.name, zone: p.zone, x: p.x, y: p.y, status: p.status };
}

function isSlotAligned(iso: string): boolean {
  const d = new Date(iso);
  return d.getUTCSeconds() === 0 && d.getUTCMilliseconds() === 0 && d.getUTCMinutes() % SLOT_MINUTES === 0;
}

export function chatRoutes(app: FastifyInstance): void {
  // ---- chat --------------------------------------------------------------------------------------------------------

  app.get<{ Params: { roomId: string }; Querystring: { before?: string; limit?: string } }>('/chat/:roomId/messages', async (req): Promise<ChatHistoryResponse> => {
    const { pc, user } = requireUser(req);
    const roomId = decodeURIComponent(req.params.roomId);
    assertMember(roomId, pc, user);
    const room = roomFor(roomId);
    const limit = Math.min(CHAT_HISTORY_MAX_LIMIT, Math.max(1, Number.parseInt(req.query.limit ?? String(CHAT_HISTORY_DEFAULT_LIMIT), 10) || CHAT_HISTORY_DEFAULT_LIMIT));
    const all = withReadAt(room, user.id);
    const end = req.query.before ? all.findIndex((m) => m.id === req.query.before) : all.length;
    if (end < 0) throw errors.notFound('message');
    const start = Math.max(0, end - limit);
    return { roomId, items: all.slice(start, end), hasMore: start > 0, unread: unreadCount(room, user.id) };
  });

  app.post<{ Params: { roomId: string } }>('/chat/:roomId/messages', async (req, reply) => {
    const { pc, user } = requireUser(req);
    const roomId = decodeURIComponent(req.params.roomId);
    assertMember(roomId, pc, user);
    return idempotent(req, reply, async () => {
      const text = str(body(req), 'text', ChatRooms.MaxTextLength).trim();
      if (text.length === 0) throw errors.validation('text', 'required');
      if (user.muted) throw errors.policyDenied('muted');
      const room = roomFor(roomId);
      const msg: ChatMessage = {
        id: uuid(),
        roomId,
        senderId: user.id,
        senderName: user.displayName,
        senderRole: user.role,
        text,
        createdAt: now(),
        readAt: now(),
        kind: 'text',
      };
      room.messages.push(msg);
      if (room.messages.length > 500) room.messages.splice(0, room.messages.length - 500);
      room.reads[user.id] = { upTo: msg.id, at: msg.createdAt };
      markDirty();
      deliver(room, msg, pc.id);
      if (user.role !== 'admin' && !roomId.startsWith('zone:')) {
        replyCounter += 1;
        pendingReplies.push({ roomId, dueAt: Date.now() + ADMIN_REPLY_MS, index: replyCounter });
      }
      return { status: 201, body: msg };
    });
  });

  app.post<{ Params: { roomId: string } }>('/chat/:roomId/read', async (req) => {
    const { pc, user } = requireUser(req);
    const roomId = decodeURIComponent(req.params.roomId);
    assertMember(roomId, pc, user);
    const upToMessageId = str(body(req), 'upToMessageId', 64);
    const room = roomFor(roomId);
    if (!room.messages.some((m) => m.id === upToMessageId)) throw errors.notFound('message');
    room.reads[user.id] = { upTo: upToMessageId, at: now() };
    markDirty();
    return { roomId, unread: unreadCount(room, user.id) };
  });

  // ---- booking -----------------------------------------------------------------------------------------------------

  app.get<{ Querystring: { date?: string } }>('/booking/seats', async (req): Promise<BookingSeatsResponse> => {
    requireAgent(req);
    const date = req.query.date ?? now().slice(0, 10);
    if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) throw errors.validation('date', 'format');
    const me = optionalUser(req)?.id ?? null;
    const bookings = db.bookings
      .filter((b) => b.from.slice(0, 10) === date)
      .map((b) => ({ ...b, userId: b.userId === me ? b.userId : ANONYMOUS_USER_ID }));
    return { date, seats: db.pcs.map(seatOf), bookings, slotMinutes: SLOT_MINUTES, openFrom: OPEN_FROM, openTo: OPEN_TO };
  });

  app.post('/booking/reserve', async (req, reply) => {
    const { pc, user } = requireUser(req);
    return idempotent(req, reply, async () => {
      const b = body(req);
      if (str(b, 'userId', 64) !== user.id) throw errors.forbidden('notOwner');
      const pcId = str(b, 'pcId', 64);
      const from = isoDate(b, 'from');
      const to = isoDate(b, 'to');
      const target = findPc(pcId);
      if (!target) throw errors.notFound('pc');
      if (target.status === 'maintenance') throw errors.policyDenied('pcMaintenance');
      if (Date.parse(to) <= Date.parse(from)) throw errors.validation('to', 'range');
      if (!isSlotAligned(from) || !isSlotAligned(to)) throw errors.validation('from', 'alignment');
      if (Date.parse(from) < Date.now() - 60_000) throw errors.validation('from', 'past');
      if (Date.parse(to) - Date.parse(from) > MAX_BOOKING_MINUTES * 60_000) {
        throw new ApiError('validation', 'Booking too long', { field: 'to', reason: 'maxDuration', maxMinutes: MAX_BOOKING_MINUTES });
      }
      const taken = db.bookings.find(
        (x) => x.pcId === pcId && x.status !== 'cancelled' && x.status !== 'expired' && Date.parse(x.from) < Date.parse(to) && Date.parse(from) < Date.parse(x.to),
      );
      if (taken) throw errors.conflict('slotTaken', { bookingId: taken.id, from: taken.from, to: taken.to });
      if (user.role === 'guest') throw errors.policyDenied('guestBooking');
      const booking: Booking = { id: uuid(), userId: user.id, pcId, from, to, status: 'reserved' };
      db.bookings.push(booking);
      markDirty();
      pushToUser(user.id, 'bookingUpdated', booking);
      console.log(`[booking] ${user.username} reserved ${target.name} ${from} → ${to} (from ${pc.name})`);
      return { status: 201, body: booking };
    });
  });

  app.delete<{ Params: { id: string } }>('/booking/:id', async (req) => {
    const { user } = requireUser(req);
    const booking = db.bookings.find((b) => b.id === req.params.id);
    if (!booking) throw errors.notFound('booking');
    if (booking.userId !== user.id && user.role !== 'admin') throw errors.forbidden('notOwner');
    if (booking.status === 'cancelled') return booking;
    if (Date.parse(booking.from) <= Date.now()) throw errors.conflict('alreadyStarted');
    booking.status = 'cancelled';
    markDirty();
    pushToUser(booking.userId, 'bookingUpdated', booking);
    return booking;
  });

  // ---- tournaments -------------------------------------------------------------------------------------------------

  app.get<{ Querystring: { state?: string; gameId?: string } }>('/tournaments', async (req) => {
    requireAgent(req);
    const { state, gameId } = req.query;
    if (state && !(TOURNAMENT_STATES as string[]).includes(state)) throw errors.validation('state', 'enum');
    const me = optionalUser(req)?.id ?? null;
    const items = db.tournaments.filter((t) => (!state || t.state === state) && (!gameId || t.gameId === gameId)).map((t) => viewTournament(t, me));
    return { items };
  });

  app.post<{ Params: { id: string } }>('/tournaments/:id/join', async (req) => {
    const { user } = requireUser(req);
    const t = db.tournaments.find((x) => x.id === req.params.id);
    if (!t) throw errors.notFound('tournament');
    if (t.state !== 'registration') throw errors.conflict('notOpen', { state: t.state });
    if (t.participants.includes(user.id)) throw errors.conflict('alreadyJoined');
    if (t.participants.length >= t.maxPlayers) throw errors.conflict('full', { maxPlayers: t.maxPlayers });
    if (user.role === 'guest') throw errors.policyDenied('guestTournament');
    t.participants.push(user.id);
    markDirty();
    const view = viewTournament(t, user.id);
    pushToUser(user.id, 'tournamentUpdated', view);
    return view;
  });

  app.get<{ Params: { id: string }; Querystring: { limit?: string } }>('/tournaments/:id/leaderboard', async (req) => {
    requireAgent(req);
    const t = db.tournaments.find((x) => x.id === req.params.id);
    if (!t) throw errors.notFound('tournament');
    const limit = Math.min(100, Math.max(1, Number.parseInt(req.query.limit ?? '50', 10) || 50));
    const me = optionalUser(req)?.id ?? null;
    const entries = t.leaderboard.slice(0, limit);
    const mine = me ? t.leaderboard.find((e) => e.userId === me) ?? null : null;
    return { tournamentId: t.id, entries, me: mine && !entries.includes(mine) ? mine : null, updatedAt: t.leaderboardUpdatedAt };
  });
}
