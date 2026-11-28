/**
 * Low-time warnings (D-109): the player is told at 10, 7 and 5 minutes left (the club's own list, `limits.warningMinutes`)
 * and at the last minute, once each per session. The Agent counts prepaid time (`SessionTimer`), the server counts what
 * the money still pays for on a capped postpaid (`SessionTickWorker.WarnAsync`); both reach the Shell as
 * `session.warning`. This is the same rule as pure functions: the mock Agent runs on it, and the tests pin it down.
 */

/** The marks without the owner's choice. */
export const DEFAULT_WARNING_MINUTES: readonly number[] = [10, 7, 5];

/** The final warning every session gets, whatever the club's list. */
export const LAST_MINUTE = 1;

/** The marks in force, descending and distinct: the club's list (or the default) and the last minute. */
export function warningMarks(club?: readonly number[] | null): number[] {
  const own = club ?? DEFAULT_WARNING_MINUTES;
  return [...new Set([...own, LAST_MINUTE])].filter((m) => Number.isInteger(m) && m > 0).sort((a, b) => b - a);
}

export interface WarningStep {
  /** Marks sent so far, after this step. */
  sent: number[];
  /** Minutes to warn about now (the fewest of the marks just reached), or null. */
  warn: number | null;
}

/**
 * A session starts (or is restored) with `secondsLeft`: marks it is already past count as sent, so a short session does
 * not open with a burst of stale warnings.
 */
export function startWarnings(marks: readonly number[], secondsLeft: number, sent: readonly number[] = []): number[] {
  const out = [...new Set(sent)];
  for (const m of marks) {
    if (m * 60 >= secondsLeft && !out.includes(m)) out.push(m);
  }
  return out;
}

/** One second of prepaid countdown: each mark reached (`0 < left ≤ m·60`) and not sent is sent, once. */
export function stepWarnings(marks: readonly number[], sent: readonly number[], secondsLeft: number): WarningStep {
  const fresh = marks.filter((m) => secondsLeft > 0 && secondsLeft <= m * 60 && !sent.includes(m));
  return { sent: [...sent, ...fresh], warn: fresh.length > 0 ? Math.min(...fresh) : null };
}

/** An extension (or a resync with more time): marks now above the time left are re-armed, to warn again. */
export function rearmWarnings(sent: readonly number[], secondsLeft: number): number[] {
  return sent.filter((m) => m * 60 >= secondsLeft);
}

/**
 * Postpaid with a cap: a mark is due while the money (`available` = balance + bonuses + the session's debt limit) does
 * not pay for `used + m·60 + 1` seconds, the server's «at most m minutes left». Marks reached together warn once, with
 * the fewer minutes; a top-up that makes a mark no longer due takes it back, so it warns again later.
 */
export function stepMoneyWarnings(
  marks: readonly number[],
  sent: readonly number[],
  available: number,
  secondsUsed: number,
  costOf: (seconds: number) => number,
): WarningStep {
  const due = marks.filter((m) => costOf(secondsUsed + m * 60 + 1) > available);
  const kept = sent.filter((m) => !marks.includes(m) || due.includes(m));
  const fresh = due.filter((m) => !kept.includes(m)).sort((a, b) => b - a);
  return { sent: [...kept, ...fresh], warn: fresh.length > 0 ? Math.min(...fresh) : null };
}
