/**
 * Keyboard chords (`'ctrl+alt+shift+f12'`, `'escape'`, `'ctrl+k'`) on `window`, plus kiosk hotkeys forwarded by the
 * Rust layer under a `'kiosk:<name>'` key (`'kiosk:exit'`, `'kiosk:callAdmin'`, …). Handlers live in a ref, so
 * passing a fresh object every render is fine; `deps` re-binds only when given.
 */
import { useEffect, useRef, type DependencyList } from 'react';
import { events } from '@/lib/tauri';

export type HotkeyMap = Record<string, () => void>;

export interface Chord {
  ctrl: boolean;
  alt: boolean;
  shift: boolean;
  meta: boolean;
  /** `KeyboardEvent.key`, lower-cased. */
  key: string;
}

const KEY_ALIASES: Readonly<Record<string, string>> = {
  esc: 'escape',
  space: ' ',
  spacebar: ' ',
  del: 'delete',
  return: 'enter',
  up: 'arrowup',
  down: 'arrowdown',
  left: 'arrowleft',
  right: 'arrowright',
  plus: '+',
  cmd: 'meta',
  win: 'meta',
  control: 'ctrl',
  option: 'alt',
};

/** Parses `'ctrl+alt+shift+f12'` (case-insensitive, any order; `'+'` alone via `'plus'`). */
export function parseChord(chord: string): Chord {
  const out: Chord = { ctrl: false, alt: false, shift: false, meta: false, key: '' };
  for (const raw of chord.toLowerCase().split('+')) {
    const part = KEY_ALIASES[raw.trim()] ?? raw.trim();
    if (part === 'ctrl' || part === 'alt' || part === 'shift' || part === 'meta') {
      out[part] = true;
    } else if (part.length > 0) {
      out.key = part;
    }
  }
  return out;
}

/** `true` when the event matches the chord exactly (modifiers included). */
export function matchesChord(e: KeyboardEvent, c: Chord): boolean {
  return (
    e.ctrlKey === c.ctrl &&
    e.altKey === c.alt &&
    e.shiftKey === c.shift &&
    e.metaKey === c.meta &&
    e.key.toLowerCase() === c.key
  );
}

/** `true` when typing into an editable control (plain printable chords are not intercepted there). */
export function isEditableTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) {
    return false;
  }
  const tag = target.tagName;
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || target.isContentEditable;
}

export function useHotkeys(map: HotkeyMap, deps: DependencyList = [], options: { enabled?: boolean } = {}): void {
  const mapRef = useRef(map);
  mapRef.current = map;
  const enabled = options.enabled ?? true;

  useEffect(() => {
    if (!enabled) {
      return undefined;
    }
    const chords = Object.keys(mapRef.current)
      .filter((k) => !k.startsWith('kiosk:'))
      .map((k) => ({ name: k, chord: parseChord(k) }));
    const onKey = (e: KeyboardEvent): void => {
      if (e.repeat) {
        return;
      }
      for (const { name, chord } of chords) {
        if (!matchesChord(e, chord)) {
          continue;
        }
        const plain = !chord.ctrl && !chord.alt && !chord.meta;
        if (plain && chord.key.length === 1 && isEditableTarget(e.target)) {
          continue;
        }
        const handler = mapRef.current[name];
        if (handler) {
          e.preventDefault();
          e.stopPropagation();
          handler();
          return;
        }
      }
    };
    window.addEventListener('keydown', onKey);
    const offKiosk = events.onKiosk('hotkey', (h) => mapRef.current[`kiosk:${h.name}`]?.());
    return () => {
      window.removeEventListener('keydown', onKey);
      offKiosk();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [enabled, ...deps]);
}
