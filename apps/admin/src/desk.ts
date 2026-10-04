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
