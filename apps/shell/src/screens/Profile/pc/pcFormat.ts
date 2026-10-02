/**
 * Pure helpers of the PC settings panels (unit-tested by the e2e package without a browser): the double-click slider
 * scale, the double-click test and the split of a Windows audio endpoint name.
 */

/** The double-click slider runs from slow (900 ms, left) to fast (200 ms, right) in 50 ms steps, like Windows'. */
export const DOUBLE_CLICK_SLOWEST_MS = 900;
export const DOUBLE_CLICK_STEP_MS = 50;
export const DOUBLE_CLICK_POSITIONS = 14;

/** Slider position (0…14) of a double-click time; a time off the 50 ms grid snaps to the nearest step. */
export function doubleClickPosition(ms: number): number {
  const position = Math.round((DOUBLE_CLICK_SLOWEST_MS - ms) / DOUBLE_CLICK_STEP_MS);
  return Math.min(DOUBLE_CLICK_POSITIONS, Math.max(0, position));
}

/** Double-click time (ms) at a slider position. */
export function doubleClickMsAt(position: number): number {
  const p = Math.min(DOUBLE_CLICK_POSITIONS, Math.max(0, Math.round(position)));
  return DOUBLE_CLICK_SLOWEST_MS - p * DOUBLE_CLICK_STEP_MS;
}

/** Outcome of one click on the double-click test pad. */
export type DoubleClickResult = 'armed' | 'ok' | 'slow';

/**
 * A second click within `thresholdMs` of the first is `ok` (what Windows counts as a double click); one that came a bit
 * too late is `slow` and starts a new attempt; a click long after the previous one only arms.
 */
export function judgeClick(previousAt: number | null, now: number, thresholdMs: number): DoubleClickResult {
  if (previousAt === null) {
    return 'armed';
  }
  const gap = now - previousAt;
  if (gap <= thresholdMs) {
    return 'ok';
  }
  return gap <= thresholdMs * 3 ? 'slow' : 'armed';
}

/**
 * Windows names an output `Endpoint (Adapter)` — "Speakers (Realtek(R) Audio)". The endpoint part is the title, the
 * adapter the detail line; a name without an adapter is all title.
 */
export function splitDeviceName(name: string): { title: string; detail: string | null } {
  const trimmed = name.trim();
  const open = trimmed.indexOf(' (');
  if (open <= 0 || !trimmed.endsWith(')')) {
    return { title: trimmed, detail: null };
  }
  const detail = trimmed.slice(open + 2, -1).trim();
  return { title: trimmed.slice(0, open).trim(), detail: detail.length > 0 ? detail : null };
}
