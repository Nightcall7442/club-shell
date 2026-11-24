/**
 * Player input the browser does not see as a DOM event — the gamepad (`kiosk://gamepad`, the browser Gamepad API) — still
 * counts as input (D-100): the idle prompt («Вы ещё здесь?») must not count down on a player navigating with a pad, and
 * the kiosk's own idle timer (`kiosk://idle`, fed by the native keyboard/mouse hooks only) is told too
 * (`kiosk_idle_reset`). At most one mark per {@link ACTIVITY_THROTTLE_MS}.
 */
import { api, isTauri } from '@/lib/tauri';

/** Window event `useIdle` treats as input, like a key press. */
export const ACTIVITY_EVENT = 'clubshell:activity';

export const ACTIVITY_THROTTLE_MS = 1000;

let lastMark = 0;

/** Marks player input that is not a DOM event (gamepad). */
export function markActivity(now: number = Date.now()): void {
  if (now - lastMark < ACTIVITY_THROTTLE_MS) {
    return;
  }
  lastMark = now;
  if (typeof window !== 'undefined') {
    window.dispatchEvent(new Event(ACTIVITY_EVENT));
  }
  if (isTauri()) {
    void api.kiosk.idleReset().catch(() => undefined);
  }
}

/** Tests: forget the throttle. */
export function resetActivityThrottle(): void {
  lastMark = 0;
}
