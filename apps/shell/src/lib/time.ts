/**
 * Clock helpers: server-time offset, countdown formatting and a tiny `formatClock` token formatter.
 * Durations are integer seconds (wire convention); timestamps are ISO-8601 UTC strings.
 */

let serverOffsetMs = 0;

/** Local wall clock in ms. */
export function nowMs(): number {
  return Date.now();
}

/** Sets the (server − local) clock offset in ms. */
export function setServerTimeOffset(ms: number): void {
  serverOffsetMs = Number.isFinite(ms) ? ms : 0;
}

/** Current (server − local) clock offset in ms. */
export function getServerTimeOffset(): number {
  return serverOffsetMs;
}

/** Updates the offset from a server timestamp (`serverTime` of `session.timeLeft`, `sys.pcInfo`, …). */
export function syncServerTime(serverTimeIso: string): void {
  const t = Date.parse(serverTimeIso);
  if (!Number.isNaN(t)) {
    setServerTimeOffset(t - Date.now());
  }
}

/** Server-corrected "now". */
export function serverNow(): Date {
  return new Date(Date.now() + serverOffsetMs);
}

/** Server-corrected "now" in ms. */
export function serverNowMs(): number {
  return Date.now() + serverOffsetMs;
}

/** Whole seconds from server-now until `iso`; negative when in the past, `NaN` when unparsable. */
export function secondsUntil(iso: string): number {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) {
    return Number.NaN;
  }
  return Math.round((t - serverNowMs()) / 1000);
}

/** Whole seconds since `iso` (server-corrected). */
export function secondsSince(iso: string): number {
  return -secondsUntil(iso);
}

const pad2 = (n: number): string => String(n).padStart(2, '0');

function clampSec(sec: number): number {
  return Number.isFinite(sec) ? Math.max(0, Math.floor(sec)) : 0;
}

/** `mm:ss` (minutes may exceed 59: `125:07`). */
export function mmss(sec: number): string {
  const s = clampSec(sec);
  return `${pad2(Math.floor(s / 60))}:${pad2(s % 60)}`;
}

/** `hh:mm:ss` (hours may exceed 23). */
export function hhmmss(sec: number): string {
  const s = clampSec(sec);
  return `${pad2(Math.floor(s / 3600))}:${pad2(Math.floor((s % 3600) / 60))}:${pad2(s % 60)}`;
}

/** `h:mm` style (e.g. `1:27`), used by the session bar when ≥ 10 minutes remain. */
export function hmm(sec: number): string {
  const s = clampSec(sec);
  return `${Math.floor(s / 3600)}:${pad2(Math.floor((s % 3600) / 60))}`;
}

/**
 * Formats a date with a token pattern: `YYYY`, `MM`, `DD`, `HH`, `H`, `hh`, `h`, `mm`, `ss`, `a` (am/pm).
 * Anything else is copied verbatim. Default pattern is `HH:mm` (`shell.json → ui.clockFormat`).
 */
export function formatClock(date: Date, fmt = 'HH:mm'): string {
  const h24 = date.getHours();
  const h12 = h24 % 12 === 0 ? 12 : h24 % 12;
  const tokens: Record<string, string> = {
    YYYY: String(date.getFullYear()),
    MM: pad2(date.getMonth() + 1),
    DD: pad2(date.getDate()),
    HH: pad2(h24),
    H: String(h24),
    hh: pad2(h12),
    h: String(h12),
    mm: pad2(date.getMinutes()),
    ss: pad2(date.getSeconds()),
    a: h24 < 12 ? 'am' : 'pm',
  };
  return fmt.replace(/YYYY|MM|DD|HH|H|hh|h|mm|ss|a/g, (m) => tokens[m] ?? m);
}

/** `YYYY-MM-DD` of a date in local time (booking dates). */
export function toDateKey(date: Date): string {
  return formatClock(date, 'YYYY-MM-DD');
}

/** ISO string of a `Date` (UTC, ms precision). */
export function toIso(date: Date | number): string {
  return new Date(date).toISOString();
}

/** Rounds `date` down to the nearest `slotMinutes` boundary. */
export function floorToSlot(date: Date, slotMinutes: number): Date {
  const ms = slotMinutes * 60_000;
  return new Date(Math.floor(date.getTime() / ms) * ms);
}
