/**
 * Players' calls to the desk (cash desk part 3, D-62/D-63), as the server keeps them in `admin_calls`: from the PC's
 * «Позвать администратора» (`POST /support/call-admin`), from its telemetry copy when the call could not be sent (the
 * same `at`, so one call), and from a «report a problem» text (`shellClientError` starting `[user report] `, at most five
 * per PC an hour). A call of a PC whose last call was answered («Иду») in the last ten minutes, with none open, is kept
 * as answered (`repeat`): it shows in the inbox without ringing again. One older than 30 minutes when it arrives is kept
 * resolved. The desk's inbox (`overview.calls`) lists the open and answered ones of the last 12 hours.
 */
import { db, findUser, markDirty, now, uuid, type CallRecord, type PcRecord } from './db.js';

export const CALL_CATEGORIES = ['help', 'technical', 'order', 'other'] as const;
export type CallCategory = CallRecord['category'];

const REPEAT_AFTER_ACK_MS = 10 * 60_000;
const STALE_MS = 30 * 60_000;
const REPORTS_PER_HOUR = 5;
const LIVE_MS = 12 * 3600_000;
const LIVE_MAX = 50;
const KEEP = 1000;
/** What the shell prefixes a «report a problem» text with. */
export const USER_REPORT = '[user report] ';

/** A call as the desk reads it. */
export interface CallView {
  id: string;
  pcId: string;
  pcName: string;
  pcNumber: number;
  user: { id: string; displayName: string } | null;
  category: CallCategory;
  message: string | null;
  source: CallRecord['source'];
  at: string;
  status: CallRecord['status'];
  repeat: boolean;
  ackedBy: string | null;
  ackedAt: string | null;
}

export function callView(c: CallRecord): CallView {
  return {
    id: c.id,
    pcId: c.pcId,
    pcName: c.pcName,
    pcNumber: c.pcNumber,
    user: c.userId ? { id: c.userId, displayName: c.userName ?? '' } : null,
    category: c.category,
    message: c.message,
    source: c.source,
    at: c.receivedAt,
    status: c.status,
    repeat: c.repeat,
    ackedBy: c.ackedBy,
    ackedAt: c.ackedAt,
  };
}

/** Open and answered calls of the last 12 hours, newest first: the desk's inbox. */
export function liveCalls(t = Date.now()): CallView[] {
  return db.calls
    .filter((c) => c.status !== 'resolved' && t - Date.parse(c.receivedAt) < LIVE_MS)
    .sort((a, b) => b.receivedAt.localeCompare(a.receivedAt) || b.id.localeCompare(a.id))
    .slice(0, LIVE_MAX)
    .map(callView);
}

/** 1 + the club's open calls received before this one. */
export function queuePosition(call: CallRecord): number {
  return 1 + db.calls.filter((c) => c.status === 'open' && c.receivedAt < call.receivedAt).length;
}

/** A message as stored: trimmed, empty → none, at most 500 characters. */
export function callMessage(v: unknown): string | null {
  if (typeof v !== 'string') return null;
  const text = v.trim();
  return text ? text.slice(0, 500) : null;
}

/**
 * Stores one call, once per (PC, at): an existing one is returned as it is (`created` false). Problem reports past five
 * an hour per PC are dropped (null). The status follows the repeat and stale rules above.
 */
export function insertCall(o: {
  pc: PcRecord;
  userId: string | null;
  category: CallCategory;
  message: string | null;
  source: CallRecord['source'];
  at: string;
}): { call: CallRecord; created: boolean } | null {
  const existing = db.calls.find((c) => c.pcId === o.pc.id && c.at === o.at);
  if (existing) return { call: existing, created: false };
  const t = Date.now();
  if (o.category === 'problem') {
    const lastHour = db.calls.filter(
      (c) => c.pcId === o.pc.id && c.category === 'problem' && t - Date.parse(c.receivedAt) < 3600_000,
    ).length;
    if (lastHour >= REPORTS_PER_HOUR) {
      console.warn(`[calls] ${o.pc.name}: problem report dropped (${REPORTS_PER_HOUR} an hour)`);
      return null;
    }
  }
  const mine = db.calls.filter((c) => c.pcId === o.pc.id);
  const open = mine.some((c) => c.status === 'open');
  const answered = mine.some((c) => c.ackedAt !== null && t - Date.parse(c.ackedAt) < REPEAT_AFTER_ACK_MS);
  const receivedAt = now();
  const repeat = o.category !== 'problem' && !open && answered;
  const stale = t - Date.parse(o.at) > STALE_MS;
  const last = [...mine].reverse().find((c) => c.ackedAt !== null);
  const user = o.userId ? findUser(o.userId) : undefined;
  const call: CallRecord = {
    id: uuid(),
    pcId: o.pc.id,
    pcName: o.pc.name,
    pcNumber: o.pc.number,
    userId: user?.id ?? null,
    userName: user?.displayName ?? null,
    category: o.category,
    message: o.message,
    source: o.source,
    at: o.at,
    receivedAt,
    status: stale ? 'resolved' : repeat ? 'acked' : 'open',
    repeat,
    // A repeat carries the answer it repeats: it never stretches the ten minutes.
    ackedAt: repeat ? (last?.ackedAt ?? receivedAt) : null,
    ackedBy: repeat ? (last?.ackedBy ?? null) : null,
    resolvedAt: stale ? receivedAt : null,
    resolvedBy: stale ? 'auto' : null,
  };
  db.calls.push(call);
  if (db.calls.length > KEEP) db.calls.splice(0, db.calls.length - KEEP);
  markDirty();
  console.log(`[calls] ${o.pc.name} (${o.category}, ${o.source}${repeat ? ', repeat' : ''}): ${o.message ?? ''}`);
  return { call, created: true };
}

/**
 * «Иду» / «Закрыть» of `call`: it and every older call of the same PC that is still open («Иду») or not yet resolved
 * («Закрыть») move on. Returns whether anything changed (an answered call answered again changes nothing).
 */
export function moveCalls(call: CallRecord, to: 'acked' | 'resolved', staffName: string): boolean {
  const at = now();
  let changed = false;
  for (const c of db.calls) {
    if (c.pcId !== call.pcId || c.receivedAt > call.receivedAt) continue;
    if (to === 'acked' && c.status === 'open') {
      c.status = 'acked';
      c.ackedAt = at;
      c.ackedBy = staffName;
      changed = true;
    } else if (to === 'resolved' && c.status !== 'resolved') {
      c.status = 'resolved';
      c.resolvedAt = at;
      c.resolvedBy = staffName;
      changed = true;
    }
  }
  if (changed) markDirty();
  return changed;
}

/** The player a call is from: the signed-in user, else the open session's player of the PC, else nobody. */
export function callerOf(pcId: string, tokenUserId: string | null, bodyUserId: string | null): string | null {
  if (tokenUserId) return tokenUserId;
  if (bodyUserId && Object.values(db.userTokens).some((x) => x.userId === bodyUserId && x.pcId === pcId))
    return bodyUserId;
  return db.sessions.find((s) => s.pcId === pcId && s.state !== 'ended' && s.state !== 'idle')?.userId ?? null;
}
