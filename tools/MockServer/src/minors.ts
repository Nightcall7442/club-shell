/**
 * The date of birth (D-107), the birthday gift and the minors' night curfew (D-108) — the server's rules on the mock's
 * clock (the machine's local time stands for the club's, as everywhere in the mock):
 * - a new account needs `birthDate` (`YYYY-MM-DD`, an age of 5–100, not in the future: `400 birthDate
 *   required|format|future|min|max`); the player gives a missing one once (`PATCH /users/{id}`), then `403
 *   birthDateLocked`;
 * - the player's own purchase on the PC needs it (`403 policyDenied birthDateRequired`); a minor — or a member without
 *   one — in the forbidden hours (`limits.minorCurfew`…`limits.minorCurfewEnd`, on unless `minorCurfewEnabled` is
 *   false) gets `403 policyDenied minorCurfew {from, to}`; prepaid time that would still run at their start is refused
 *   with `400 validation minutes curfew {maxMinutes, from, to}`;
 * - a minor's open session is warned 10 and 5 minutes before and ended at the start (`admin`: unused time refunded,
 *   the bonus account first), the player signed out of the PC; the desk's seat shows `curfew`;
 * - `settings.birthdayGift`: once per birthday year, the bonus on the bonus account and a message to the PC.
 */
import { randomUUID as uuid } from 'node:crypto';
import { ApiError, applyTransaction, balanceOf, db, errors, findUser, markDirty, revokeUserTokens, uzs } from './db.js';
import type { SessionRecord, UserRecord } from './db.js';
import { club } from './club.js';
import { pushToPc, pushToUser, sendCommand } from './ws.js';

export const BIRTH_MIN_AGE = 5;
export const BIRTH_MAX_AGE = 100;

/** `YYYY-MM-DD` of a local date. */
export function isoDay(d: Date): string {
  const p = (n: number): string => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

function parts(day: string): [number, number, number] | null {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(day);
  if (!m) return null;
  const [y, mo, d] = [Number(m[1]), Number(m[2]), Number(m[3])];
  const check = new Date(y, mo - 1, d);
  return check.getFullYear() === y && check.getMonth() === mo - 1 && check.getDate() === d ? [y, mo, d] : null;
}

/** Whole years on `at`'s local date; null when `birth` is not a date. */
export function ageOn(birth: string, at = new Date()): number | null {
  const b = parts(birth);
  if (!b) return null;
  const [ty, tm, td] = [at.getFullYear(), at.getMonth() + 1, at.getDate()];
  return ty - b[0] - (tm < b[1] || (tm === b[1] && td < b[2]) ? 1 : 0);
}

/** The given date of birth, else `400 birthDate required|format|future|min|max`. */
export function birthDateOf(b: Record<string, unknown>, at = new Date()): string {
  const v = b['birthDate'];
  if (v === undefined || v === null || (typeof v === 'string' && v.trim() === ''))
    throw errors.validation('birthDate', 'required');
  if (typeof v !== 'string') throw errors.validation('birthDate', 'format');
  const day = v.trim();
  const age = ageOn(day, at);
  if (age === null) throw errors.validation('birthDate', 'format');
  if (day > isoDay(at)) throw errors.validation('birthDate', 'future');
  if (age < BIRTH_MIN_AGE) throw errors.validation('birthDate', 'min');
  if (age > BIRTH_MAX_AGE) throw errors.validation('birthDate', 'max');
  return day;
}

// ---------------------------------------------------------------------------------------------------------------------
// The curfew
// ---------------------------------------------------------------------------------------------------------------------

const minutesOf = (hhmm: string | undefined, fallback: number): number => {
  const m = /^(\d{2}):(\d{2})$/.exec(hhmm ?? '');
  return m ? Number(m[1]) * 60 + Number(m[2]) : fallback;
};

export function curfewHours(): { from: string; to: string } {
  const l = club().limits;
  return { from: l.minorCurfew || '22:00', to: l.minorCurfewEnd || '06:00' };
}

export function curfewText(): string {
  const { from, to } = curfewHours();
  return `Несовершеннолетним нельзя играть с ${from} до ${to}`;
}

/** `minor`, `noBirthDate` (never taken for an adult) or null — an adult, a guest, a staff account, the curfew off. */
export function curfewHolds(user: UserRecord, at = new Date()): 'minor' | 'noBirthDate' | null {
  if (club().limits.minorCurfewEnabled === false || user.transient || (user.role !== 'member' && user.role !== 'vip'))
    return null;
  if (!user.birthDate) return 'noBirthDate';
  const age = ageOn(user.birthDate, at);
  return age !== null && age < club().limits.minorAge ? 'minor' : null;
}

export function isMinorUser(user: UserRecord, at = new Date()): boolean {
  if (!user.birthDate || user.transient || (user.role !== 'member' && user.role !== 'vip')) return false;
  const age = ageOn(user.birthDate, at);
  return age !== null && age < club().limits.minorAge;
}

export function inCurfewHours(at = new Date()): boolean {
  const { from, to } = curfewHours();
  const [f, t] = [minutesOf(from, 22 * 60), minutesOf(to, 6 * 60)];
  const m = at.getHours() * 60 + at.getMinutes();
  return f !== t && (f < t ? m >= f && m < t : m >= f || m < t);
}

/** The next start of the forbidden hours (`at` itself inside them); null without a window. */
export function nextCurfewStart(at = new Date()): Date | null {
  if (inCurfewHours(at)) return at;
  const { from, to } = curfewHours();
  const f = minutesOf(from, 22 * 60);
  if (f === minutesOf(to, 6 * 60)) return null;
  const start = new Date(at.getFullYear(), at.getMonth(), at.getDate(), Math.floor(f / 60), f % 60);
  return start > at ? start : new Date(start.getTime() + 86400_000);
}

/** The start of the forbidden hours `at` falls in; null outside them. */
export function curfewStartedAt(at = new Date()): Date | null {
  if (!inCurfewHours(at)) return null;
  const f = minutesOf(curfewHours().from, 22 * 60);
  const start = new Date(at.getFullYear(), at.getMonth(), at.getDate(), Math.floor(f / 60), f % 60);
  return start > at ? new Date(start.getTime() - 86400_000) : start;
}

export function birthDateRequired(): ApiError {
  return new ApiError('policyDenied', 'Укажите дату рождения', {
    rule: 'birthDateRequired',
    reason: 'birthDateRequired',
  });
}

export function curfewDenied(): ApiError {
  return new ApiError('policyDenied', curfewText(), { rule: 'minorCurfew', reason: 'minorCurfew', ...curfewHours() });
}

/** Rule 5 of a purchase (D-108): `kiosk` — the player's own on the PC, which needs the date at any hour (D-107). */
export function checkCurfew(user: UserRecord, kiosk: boolean, at = new Date()): void {
  if (kiosk && !user.transient && (user.role === 'member' || user.role === 'vip') && !user.birthDate)
    throw birthDateRequired();
  if (curfewHolds(user, at) && inCurfewHours(at)) throw user.birthDate ? curfewDenied() : birthDateRequired();
}

/** Prepaid time that would still run at the next start of the forbidden hours: `400 minutes curfew {maxMinutes}`. */
export function checkCurfewEnd(user: UserRecord, leftSec: number, minutes: number, at = new Date()): void {
  const holds = curfewHolds(user, at);
  const start = nextCurfewStart(at);
  if (!holds || !start || at.getTime() + (leftSec + minutes * 60) * 1000 <= start.getTime()) return;
  const maxMinutes = Math.max(0, Math.floor((start.getTime() - at.getTime()) / 60_000 - leftSec / 60));
  throw new ApiError('validation', `${curfewText()}: не больше ${maxMinutes} мин`, {
    field: 'minutes',
    reason: 'curfew',
    maxMinutes,
    ...curfewHours(),
    ...(holds === 'noBirthDate' ? { noBirthDate: true } : {}),
  });
}

/** Sessions the curfew ended (D-108): the desk's seat note for 30 minutes. */
const curfewEnded = new Map<string, { at: string; userName: string }>();
const warned = new Map<string, Set<number>>();

/** The seat's curfew note: the minor's open session the curfew ends within the hour, or one it ended there lately. */
export function seatCurfew(
  pcId: string,
  rec: SessionRecord | undefined,
  user: UserRecord | undefined,
  at = new Date(),
) {
  if (rec && user && curfewHolds(user, at)) {
    const start = nextCurfewStart(at);
    if (start && start.getTime() - at.getTime() <= 3600_000)
      return { at: start.toISOString(), ended: false, userName: user.displayName };
  }
  const ended = rec ? undefined : curfewEnded.get(pcId);
  return ended && at.getTime() - Date.parse(ended.at) <= 30 * 60_000 ? { ...ended, ended: true } : null;
}

/**
 * The tick's curfew pass (the server's `SessionTickWorker.CurfewAsync`): warnings 10 and 5 minutes before, then the end at
 * the start — the desk's end (`admin`, the refund), the PC told and the player signed out.
 */
export function tickCurfew(nowMs: number, end: (rec: SessionRecord, reason: 'admin') => unknown): void {
  const at = new Date(nowMs);
  const next = nextCurfewStart(at);
  if (club().limits.minorCurfewEnabled === false || !next) return;
  const started = curfewStartedAt(at);
  if (!started && next.getTime() - nowMs > 10 * 60_000) return;
  for (const rec of db.sessions) {
    if (rec.state === 'ended' || rec.state === 'idle') continue;
    const user = findUser(rec.userId);
    if (!user || !curfewHolds(user, at)) continue;
    if (started) {
      end(rec, 'admin');
      revokeUserTokens(user.id, rec.pcId);
      pushToPc(rec.pcId, 'userRevoked', { userId: user.id, reason: 'sessionEnded' });
      curfewEnded.set(rec.pcId, { at: started.toISOString(), userName: user.displayName });
      pushToUser(user.id, 'notification', {
        id: uuid(),
        title: 'Сеанс завершён',
        body: `${curfewText()}. Неиспользованное время возвращено на баланс.`,
        level: 'warning',
        ttlSec: 120,
        action: null,
      });
      continue;
    }
    const left = next.getTime() - nowMs;
    const marks = warned.get(rec.id) ?? new Set<number>();
    const due = [10, 5].filter((m) => left <= m * 60_000 && !marks.has(m));
    if (due.length === 0) continue;
    due.forEach((m) => marks.add(m));
    warned.set(rec.id, marks);
    const minutes = Math.max(1, Math.ceil(left / 60_000));
    pushToPc(rec.pcId, 'notification', {
      id: uuid(),
      title: `Через ${minutes} мин сеанс завершится`,
      body: user.birthDate
        ? `${curfewText()}: в ${curfewHours().from} сеанс завершится, неиспользованное время вернётся на баланс.`
        : `В аккаунте не указана дата рождения: в ${curfewHours().from} сеанс завершится. Укажите её у администратора.`,
      level: 'warning',
      ttlSec: 60,
      action: null,
    });
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// The birthday gift
// ---------------------------------------------------------------------------------------------------------------------

export interface BirthdayGift {
  enabled: boolean;
  amount: number;
  windowDays: number;
  text: string;
}

export const DEFAULT_BIRTHDAY_GIFT: BirthdayGift = { enabled: false, amount: 2_000_000, windowDays: 3, text: '' };

export function birthdayGift(): BirthdayGift {
  return { ...DEFAULT_BIRTHDAY_GIFT, ...(club().birthdayGift ?? {}) };
}

/** The owner's patch, checked as the server: `400 birthdayGift.* min|max|format`. */
export function parseBirthdayGift(v: unknown): BirthdayGift {
  if (typeof v !== 'object' || v === null || Array.isArray(v)) throw errors.validation('birthdayGift', 'schema');
  const o = v as Record<string, unknown>;
  if ('enabled' in o && typeof o['enabled'] !== 'boolean') throw errors.validation('birthdayGift.enabled', 'format');
  const amount = typeof o['amount'] === 'number' ? Math.round(o['amount']) : DEFAULT_BIRTHDAY_GIFT.amount;
  if (amount < 0 || amount > 1_000_000_000) throw errors.validation('birthdayGift.amount', amount < 0 ? 'min' : 'max');
  const windowDays =
    typeof o['windowDays'] === 'number' ? Math.round(o['windowDays']) : DEFAULT_BIRTHDAY_GIFT.windowDays;
  if (windowDays < 0 || windowDays > 30)
    throw errors.validation('birthdayGift.windowDays', windowDays < 0 ? 'min' : 'max');
  const text = typeof o['text'] === 'string' ? o['text'].trim() : '';
  if (text.length > 200) throw errors.validation('birthdayGift.text', 'max');
  return { enabled: o['enabled'] === true, amount, windowDays, text };
}

/** The year whose birthday is within `windowDays` of `at` (29 February — the 28th in a common year); null — none. */
export function birthdayYear(birth: string, at: Date, windowDays: number): number | null {
  const b = parts(birth);
  if (!b) return null;
  const today = Date.UTC(at.getFullYear(), at.getMonth(), at.getDate());
  for (const year of [at.getFullYear(), at.getFullYear() - 1, at.getFullYear() + 1]) {
    if (year <= b[0]) continue;
    const leap = (year % 4 === 0 && year % 100 !== 0) || year % 400 === 0;
    const day = Date.UTC(year, b[1] - 1, b[1] === 2 && b[2] === 29 && !leap ? 28 : b[2]);
    if (Math.abs(day - today) <= windowDays * 86400_000) return year;
  }
  return null;
}

/** «С днём рождения! +20 000 бонусов», or the owner's text with `{bonus}`. */
export function birthdayMessage(g: BirthdayGift): string {
  const sum = Math.floor(g.amount / 100)
    .toString()
    .replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
  return g.text ? g.text.split('{bonus}').join(sum) : `С днём рождения! +${sum} бонусов`;
}

/** Counted for the console: gifts of the last 365 days and the last one. */
export function birthdayStats(): { fired: number; lastFiredAt: string | null } {
  const since = Date.now() - 365 * 86400_000;
  const recent = Object.values(club().birthdayGiven ?? {}).filter((at) => Date.parse(at) > since);
  return { fired: recent.length, lastFiredAt: recent.sort().at(-1) ?? null };
}

/** The gift of a player on a PC when it is due; false — nothing given. Once per birthday year, kept in the mock's state. */
export function giveBirthday(user: UserRecord, pcId: string, at = new Date()): boolean {
  const g = birthdayGift();
  if (!g.enabled || user.transient || (user.role !== 'member' && user.role !== 'vip') || !user.birthDate) return false;
  const year = birthdayYear(user.birthDate, at, g.windowDays);
  if (year === null) return false;
  const c = club();
  const given = (c.birthdayGiven ??= {});
  const key = `${user.id}:${year}`;
  if (given[key]) return false;
  given[key] = at.toISOString();
  if (g.amount > 0) {
    applyTransaction(user, 'bonus', uzs(g.amount), 'Подарок на день рождения', null);
    pushToUser(user.id, 'walletUpdated', balanceOf(user));
  }
  markDirty();
  void sendCommand(pcId, 'message', {
    id: uuid(),
    from: c.branding.clubName,
    text: birthdayMessage(g),
    level: 'info',
    requiresAck: false,
  });
  return true;
}

/** The club minute's birthday pass: players of open sessions. */
export function tickBirthdays(nowMs: number): void {
  if (!birthdayGift().enabled) return;
  for (const rec of db.sessions) {
    if (rec.state === 'ended' || rec.state === 'idle') continue;
    const user = findUser(rec.userId);
    if (user) giveBirthday(user, rec.pcId, new Date(nowMs));
  }
}

/** The view a seat's player needs (D-108): `minor`, `noBirthDate`. */
export function seatUserMarks(user: UserRecord): { minor?: true; noBirthDate?: true } {
  if (user.transient || (user.role !== 'member' && user.role !== 'vip')) return {};
  if (!user.birthDate) return { noBirthDate: true };
  return isMinorUser(user) ? { minor: true } : {};
}
