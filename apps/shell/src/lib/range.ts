/**
 * Sliders under a gamepad: left / right on a focused `<input type="range">` moves it instead of leaving it
 * (`useGamepad`). A short scale (a 1–20 mouse speed, a volume in 5 % steps) moves one position per press, a long one
 * five, so a 0–100 slider is crossed in twenty presses rather than a hundred.
 */

/** Scales up to this many positions move one position per press. */
const SHORT_SCALE = 30;

/** Positions one press moves on a range of `min`…`max` in `step`s. */
export function rangeStepsPerPress(min: number, max: number, step: number): number {
  const positions = Math.round((max - min) / (step > 0 ? step : 1));
  return positions <= SHORT_SCALE ? 1 : 5;
}

/** Moves a range input by `delta` steps (gamepad left/right) and notifies React. */
export function nudgeRange(el: HTMLInputElement, delta: number): void {
  const step = Number(el.step) || 1;
  const min = Number(el.min) || 0;
  const max = Number(el.max) || 100;
  const next = Math.min(max, Math.max(min, Number(el.value) + delta * step));
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
  setter?.call(el, String(next));
  el.dispatchEvent(new Event('input', { bubbles: true }));
  el.dispatchEvent(new Event('change', { bubbles: true }));
}
