/**
 * A USB card reader is a keyboard: it types the card number a few milliseconds a key and presses Enter. The card tab's
 * own field takes those keys while it is showing; this catches a card tapped while any other screen of the lock is up
 * (the QR or password tab, the attract screen), so the number is not lost and the player does not tap twice.
 */

/** A key that can be part of a card number: readers type digits, some hex or a dashed id such as `CARD-0001`. */
export const CARD_KEY = /^[\p{L}\p{N}-]$/u;

/** The longest card the cash desk can bind (`users.card_id`). */
export const CARD_MAX = 64;

/** Keys of one burst come at most this far apart: a reader types every few ms, a person rarely under 50 ms. */
export const BURST_GAP_MS = 50;

/** The shortest burst taken for a card: readers send 8–10 characters or more, a person may roll a few keys at once. */
export const BURST_MIN = 6;

/** How long a card read while no screen takes it (the attract screen giving way to the lock screen) waits for one. */
const HANDOFF_MS = 1_500;

/**
 * The character a key types on a Latin layout. A USB reader sends key codes, so under the Russian layout the letters
 * of a hex card id would arrive as Cyrillic (`0A1B` → `0Ф1И`) and match no card; digits are the same on every layout.
 * Cards are compared without regard to case.
 */
export function readerKey(e: { key: string; code: string }): string {
  const latin = /^Key([A-Z])$/.exec(e.code)?.[1];
  return latin !== undefined && /^\p{L}$/u.test(e.key) && !/^[a-z]$/i.test(e.key) ? latin : e.key;
}

/** What the burst detector reads of a `keydown`. */
export interface ReaderKeyEvent {
  key: string;
  code: string;
  /** `Event.timeStamp`, in ms. */
  timeStamp: number;
  repeat?: boolean;
  ctrlKey?: boolean;
  altKey?: boolean;
  metaKey?: boolean;
}

/**
 * A detector of reader bursts: fed every `keydown`, it returns the card number on the Enter that ends a burst — at
 * least `BURST_MIN` card characters, each within `BURST_GAP_MS` of the one before, and the Enter as close — and
 * `null` for every other key. Shift is passed over (readers type capitals with it); any other key, a held key's
 * repeats, a chord or a pause starts over.
 */
export function createBurstDetector(): (e: ReaderKeyEvent) => string | null {
  let chars = '';
  let lastAt = Number.NEGATIVE_INFINITY;
  return (e) => {
    if (e.key === 'Shift') {
      return null;
    }
    if (e.timeStamp - lastAt > BURST_GAP_MS) {
      chars = '';
    }
    lastAt = e.timeStamp;
    if (e.key === 'Enter') {
      const cardId = chars;
      chars = '';
      return cardId.length >= BURST_MIN && cardId.length <= CARD_MAX ? cardId : null;
    }
    if (e.repeat || e.ctrlKey || e.altKey || e.metaKey || !CARD_KEY.test(e.key)) {
      chars = '';
      return null;
    }
    // One past the limit is enough to refuse it at the Enter.
    chars = (chars + readerKey(e)).slice(0, CARD_MAX + 1);
    return null;
  };
}

/** A person is typing in a text field (or pointed the reader at it): the field keeps its keys. */
function inTextField(target: EventTarget | null): boolean {
  return target instanceof Element && target.closest('input, textarea, select, [contenteditable]') !== null;
}

let installed = false;
let reader: ((cardId: string) => void) | null = null;
let pending: { cardId: string; at: number } | null = null;

/**
 * Watches the main window's keys for reader bursts from boot on, across the change of screens: the attract screen
 * goes to the lock screen on the reader's first key, and the rest of the card arrives meanwhile. `enabled` says
 * whether a card may sign in now (nobody signed in). A burst ended outside a text field is taken: its Enter does not
 * press the focused tab or button, and the number goes to the screen that subscribed with `onCardRead`, or waits up
 * to 1.5 s for one.
 */
export function installCardReader(enabled: () => boolean): void {
  if (installed) {
    return;
  }
  installed = true;
  const detect = createBurstDetector();
  window.addEventListener(
    'keydown',
    (e) => {
      const cardId = detect(e);
      if (cardId === null || !enabled() || inTextField(e.target)) {
        return;
      }
      e.preventDefault();
      e.stopPropagation();
      if (reader) {
        reader(cardId);
      } else {
        pending = { cardId, at: performance.now() };
      }
    },
    { capture: true },
  );
}

/** Takes the cards read from now on, and one read just before that no screen took; returns the unsubscribe. */
export function onCardRead(take: (cardId: string) => void): () => void {
  reader = take;
  const waiting = pending;
  pending = null;
  if (waiting && performance.now() - waiting.at < HANDOFF_MS) {
    take(waiting.cardId);
  }
  return () => {
    if (reader === take) {
      reader = null;
    }
  };
}
