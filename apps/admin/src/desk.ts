/**
 * Hand-off to the hall map from outside it: the top-bar search's "Показать ПК" switches to the map page and asks it to
 * select a PC. The request waits here until the map is mounted (it takes it on mount) or, when the map is already open,
 * reaches it through the event.
 */
const EVENT = 'desk:show-pc';
let pending: string | null = null;

export function showPc(pcId: string): void {
  pending = pcId;
  window.dispatchEvent(new Event(EVENT));
}

/** Calls `select` with a waiting request now and with every later one; returns the unsubscribe. */
export function onShowPc(select: (pcId: string) => void): () => void {
  const take = (): void => {
    if (pending === null) return;
    const id = pending;
    pending = null;
    select(id);
  };
  take();
  window.addEventListener(EVENT, take);
  return () => window.removeEventListener(EVENT, take);
}

/**
 * Hand-off to the bar from the busy-seat panel («Бар»): the buyer (the seat's player) and the PC are prefilled once. The
 * request waits until the bar page is mounted, like {@link showPc}.
 */
const BAR_EVENT = 'desk:show-bar';
let pendingBar: { pcId: string; userId: string } | null = null;

export function showBar(request: { pcId: string; userId: string }): void {
  pendingBar = request;
  window.dispatchEvent(new Event(BAR_EVENT));
}

/** Calls `take` with a waiting request now and with every later one; returns the unsubscribe. */
export function onShowBar(take: (request: { pcId: string; userId: string }) => void): () => void {
  const run = (): void => {
    if (pendingBar === null) return;
    const request = pendingBar;
    pendingBar = null;
    take(request);
  };
  run();
  window.addEventListener(BAR_EVENT, run);
  return () => window.removeEventListener(BAR_EVENT, run);
}

/** True when the key went to a field the cashier is typing in (hotkeys leave it alone). */
export function isTyping(e: KeyboardEvent): boolean {
  const el = e.target;
  return (
    el instanceof HTMLElement &&
    (el.isContentEditable || el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT')
  );
}

/** True while a modal sheet is open: the map's hotkeys (digits, F2) wait until it closes. */
export function sheetOpen(): boolean {
  return document.querySelector('[role="dialog"][aria-modal="true"]') !== null;
}
