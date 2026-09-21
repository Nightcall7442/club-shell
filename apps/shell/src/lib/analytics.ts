/**
 * Lightweight UI analytics. No `sys.track` command exists in TAURI_COMMANDS.md, so events are kept in a
 * local ring buffer (inspectable from devtools / Playwright via `getAnalyticsBuffer()`) and echoed to the
 * console in dev. Screen views are deduplicated per route.
 */

/** One recorded event. */
export interface AnalyticsEvent {
  /** Event name, e.g. `game.launch`. */
  event: string;
  /** Free-form properties (no secrets). */
  props?: Record<string, unknown>;
  /** Wall-clock time in ms. */
  at: number;
  /** Route at the time of the event. */
  route?: string;
}

const BUFFER_SIZE = 200;
const buffer: AnalyticsEvent[] = [];
let lastScreen: string | null = null;
let sessionStartedAt = Date.now();

function currentRoute(): string | undefined {
  if (typeof window === 'undefined') {
    return undefined;
  }
  const hash = window.location.hash;
  return hash.startsWith('#') ? hash.slice(1) : window.location.pathname;
}

/** Records an event. Never throws. */
export function track(event: string, props?: Record<string, unknown>): void {
  const entry: AnalyticsEvent = { event, props, at: Date.now(), route: currentRoute() };
  buffer.push(entry);
  if (buffer.length > BUFFER_SIZE) {
    buffer.splice(0, buffer.length - BUFFER_SIZE);
  }
  if (import.meta.env.DEV) {
    console.debug('[analytics]', event, props ?? '');
  }
}

/** Records a screen view once per route change (`screen.view { route }`). */
export function trackScreen(route: string): void {
  if (route === lastScreen) {
    return;
  }
  lastScreen = route;
  track('screen.view', { route });
}

/** Records an error occurrence (code only, never the message with user data). */
export function trackError(code: string, context?: string): void {
  track('error', { code, context });
}

/** Marks the start of a UI session (login) so durations can be derived. */
export function resetAnalyticsSession(): void {
  sessionStartedAt = Date.now();
  lastScreen = null;
  track('ui.session.start');
}

/** Seconds since the UI session started. */
export function uiSessionSeconds(): number {
  return Math.floor((Date.now() - sessionStartedAt) / 1000);
}

/** Snapshot of the ring buffer, oldest first. */
export function getAnalyticsBuffer(): readonly AnalyticsEvent[] {
  return buffer.slice();
}

/** Clears the ring buffer (tests). */
export function clearAnalyticsBuffer(): void {
  buffer.length = 0;
  lastScreen = null;
}
