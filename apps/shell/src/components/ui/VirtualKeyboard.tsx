import { useEffect, useLayoutEffect, useRef, useState, type PointerEvent } from 'react';
import clsx from 'clsx';
import { AnimatePresence, motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { create } from 'zustand';
import { currentLocale } from '@/i18n';

export type KeyboardLayout = 'en' | 'ru' | 'uz' | 'symbols';
export type KeyboardTarget = HTMLInputElement | HTMLTextAreaElement;
export type ShiftState = 'off' | 'once' | 'lock';

export interface VirtualKeyboardState {
  visible: boolean;
  target: KeyboardTarget | null;
  layout: KeyboardLayout;
  shift: ShiftState;
  open(el: KeyboardTarget): void;
  close(): void;
  setLayout(layout: KeyboardLayout): void;
  /** Tap: `off → once → off`; double tap (`once → lock`) locks caps. */
  toggleShift(): void;
}

function layoutForLocale(): KeyboardLayout {
  const l = currentLocale();
  return l === 'ru' || l === 'uz' ? l : 'en';
}

/** Singleton keyboard state; `Input` opens it on focus when `settings.allowVirtualKeyboard` is on. */
export const useVirtualKeyboard = create<VirtualKeyboardState>()((set) => ({
  visible: false,
  target: null,
  layout: 'en',
  shift: 'off',
  open: (el) =>
    set((s) => ({ visible: true, target: el, layout: s.visible ? s.layout : layoutForLocale(), shift: 'off' })),
  close: () => set({ visible: false, target: null, shift: 'off' }),
  setLayout: (layout) => set({ layout, shift: 'off' }),
  toggleShift: () => set((s) => ({ shift: s.shift === 'off' ? 'once' : s.shift === 'once' ? 'lock' : 'off' })),
}));

// ---------------------------------------------------------------------------------------------------------------------
// Text editing against a (possibly React-controlled) input
// ---------------------------------------------------------------------------------------------------------------------

function setNativeValue(el: KeyboardTarget, value: string): void {
  const proto = el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
  const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
  if (setter) {
    setter.call(el, value);
  } else {
    el.value = value;
  }
  el.dispatchEvent(new Event('input', { bubbles: true }));
}

function selectionOf(el: KeyboardTarget): [number, number] {
  const start = el.selectionStart;
  const end = el.selectionEnd;
  if (start === null || end === null) {
    return [el.value.length, el.value.length];
  }
  return [start, end];
}

function setCaret(el: KeyboardTarget, pos: number): void {
  try {
    el.setSelectionRange(pos, pos);
  } catch {
    // input types without selection support (number, email): caret stays at the end
  }
}

/** Inserts `text` at the caret (replacing any selection) and fires `input` so React sees the change. */
export function insertText(el: KeyboardTarget, text: string): void {
  const [start, end] = selectionOf(el);
  const v = el.value;
  setNativeValue(el, v.slice(0, start) + text + v.slice(end));
  setCaret(el, start + text.length);
}

/** Deletes the selection or the character before the caret. */
export function deleteBackward(el: KeyboardTarget): void {
  const [start, end] = selectionOf(el);
  const v = el.value;
  if (start !== end) {
    setNativeValue(el, v.slice(0, start) + v.slice(end));
    setCaret(el, start);
    return;
  }
  if (start === 0) {
    return;
  }
  // Drop a whole surrogate pair when the previous char is the low half of one.
  const step =
    start >= 2 && /[\uDC00-\uDFFF]/.test(v.charAt(start - 1)) && /[\uD800-\uDBFF]/.test(v.charAt(start - 2)) ? 2 : 1;
  setNativeValue(el, v.slice(0, start - step) + v.slice(start));
  setCaret(el, start - step);
}

// ---------------------------------------------------------------------------------------------------------------------
// Layouts
// ---------------------------------------------------------------------------------------------------------------------

const DIGITS = ['1', '2', '3', '4', '5', '6', '7', '8', '9', '0'];

const LETTERS: Record<Exclude<KeyboardLayout, 'symbols'>, string[][]> = {
  en: [
    ['q', 'w', 'e', 'r', 't', 'y', 'u', 'i', 'o', 'p'],
    ['a', 's', 'd', 'f', 'g', 'h', 'j', 'k', 'l'],
    ['z', 'x', 'c', 'v', 'b', 'n', 'm'],
  ],
  ru: [
    ['й', 'ц', 'у', 'к', 'е', 'н', 'г', 'ш', 'щ', 'з', 'х', 'ъ'],
    ['ф', 'ы', 'в', 'а', 'п', 'р', 'о', 'л', 'д', 'ж', 'э'],
    ['я', 'ч', 'с', 'м', 'и', 'т', 'ь', 'б', 'ю', 'ё'],
  ],
  uz: [
    ['q', 'w', 'e', 'r', 't', 'y', 'u', 'i', 'o', 'p', 'oʻ', 'gʻ'],
    ['a', 's', 'd', 'f', 'g', 'h', 'j', 'k', 'l', 'ʼ'],
    ['z', 'x', 'c', 'v', 'b', 'n', 'm'],
  ],
};

const SYMBOLS: string[][] = [
  DIGITS,
  ['@', '#', '$', '%', '&', '-', '_', '+', '(', ')'],
  ['.', ',', '?', '!', ':', ';', "'", '"', '/', '*'],
];

const LAYOUT_LABEL: Record<Exclude<KeyboardLayout, 'symbols'>, string> = {
  en: 'layoutEn',
  ru: 'layoutRu',
  uz: 'layoutUz',
};

const preventFocusSteal = (e: PointerEvent): void => e.preventDefault();

let instances = 0;

interface KeyProps {
  label: string;
  onPress: () => void;
  wide?: boolean;
  accent?: boolean;
  active?: boolean;
  ariaLabel?: string;
}

function Key({ label, onPress, wide = false, accent = false, active = false, ariaLabel }: KeyProps): JSX.Element {
  return (
    <button
      type="button"
      tabIndex={-1}
      aria-label={ariaLabel}
      aria-pressed={active || undefined}
      onPointerDown={preventFocusSteal}
      onClick={onPress}
      className={clsx(
        'inline-flex h-[3.2rem] select-none items-center justify-center rounded-md text-lg font-semibold transition-colors duration-[var(--dur-fast)] active:scale-95',
        wide ? 'min-w-[5.5rem] px-4 text-base' : 'min-w-[3.2rem] px-2',
        accent ? 'bg-primary/25 text-text hover:bg-primary/40' : 'bg-text/10 text-text hover:bg-text/20',
        active && 'bg-primary text-on-primary hover:bg-primary',
      )}
    >
      {label}
    </button>
  );
}

/** On-screen keyboard (QWERTY / ЙЦУКЕН / Uzbek Latin / symbols). Render once near the app root. */
export function VirtualKeyboard(): JSX.Element | null {
  const { t } = useTranslation();
  const { visible, target, layout, shift, close, setLayout, toggleShift } = useVirtualKeyboard();
  const [primary, setPrimary] = useState(false);
  const panel = useRef<HTMLDivElement>(null);

  // Publish the keyboard height so layouts can keep the focused control (and chat composer) above it.
  useLayoutEffect(() => {
    const root = document.documentElement;
    if (visible && panel.current) {
      root.style.setProperty('--vk-h', `${panel.current.offsetHeight}px`);
      return () => {
        root.style.removeProperty('--vk-h');
      };
    }
    return undefined;
  }, [visible, layout, primary]);

  useEffect(() => {
    instances += 1;
    setPrimary(instances === 1);
    return () => {
      instances -= 1;
    };
  }, []);

  useEffect(() => {
    if (visible && target) {
      target.scrollIntoView({ block: 'center', behavior: 'smooth' });
    }
  }, [visible, target, primary]);

  // A focused input removed from the DOM (route change, modal step) fires no blur: close when the target is gone.
  useEffect(() => {
    if (!visible || !target || typeof MutationObserver === 'undefined') {
      return undefined;
    }
    const mo = new MutationObserver(() => {
      if (!target.isConnected) {
        close();
      }
    });
    mo.observe(document.body, { childList: true, subtree: true });
    return () => mo.disconnect();
  }, [visible, target, close]);

  if (!primary) {
    return null;
  }

  const live = (): KeyboardTarget | null => {
    if (!target || !target.isConnected) {
      close();
      return null;
    }
    return target;
  };

  const type = (ch: string): void => {
    const el = live();
    if (!el) {
      return;
    }
    insertText(el, shift !== 'off' ? ch.toUpperCase() : ch);
    if (shift === 'once') {
      useVirtualKeyboard.setState({ shift: 'off' });
    }
  };

  const backspace = (): void => {
    const el = live();
    if (el) {
      deleteBackward(el);
    }
  };

  const enter = (): void => {
    const el = live();
    if (!el) {
      return;
    }
    if (el instanceof HTMLTextAreaElement) {
      insertText(el, '\n');
      return;
    }
    const ev = new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', bubbles: true, cancelable: true });
    const proceed = el.dispatchEvent(ev);
    if (proceed && el.form) {
      el.form.requestSubmit();
    }
    close();
  };

  const rows = layout === 'symbols' ? SYMBOLS : [DIGITS, ...LETTERS[layout]];
  const upper = shift !== 'off';

  return (
    <AnimatePresence>
      {visible && (
        <motion.div
          ref={panel}
          role="group"
          aria-label={t('kiosk.virtualKeyboard.title')}
          data-nav-scope="keyboard"
          className="glass-strong fixed inset-x-0 bottom-0 z-[80] rounded-t-2xl px-[var(--gutter)] pb-4 pt-3"
          initial={{ y: '100%' }}
          animate={{ y: 0 }}
          exit={{ y: '100%' }}
          transition={{ duration: 0.2, ease: 'easeOut' }}
        >
          <div className="mx-auto flex max-w-[1100px] flex-col gap-2">
            <div className="flex items-center justify-between gap-2">
              <div className="flex gap-1">
                {(Object.keys(LETTERS) as Exclude<KeyboardLayout, 'symbols'>[]).map((l) => (
                  <Key
                    key={l}
                    label={t(`kiosk.virtualKeyboard.${LAYOUT_LABEL[l]}`)}
                    onPress={() => setLayout(l)}
                    active={layout === l}
                  />
                ))}
                <Key
                  label={layout === 'symbols' ? t('kiosk.virtualKeyboard.letters') : t('kiosk.virtualKeyboard.symbols')}
                  onPress={() => setLayout(layout === 'symbols' ? layoutForLocale() : 'symbols')}
                  wide
                />
              </div>
              <Key
                label={t('kiosk.virtualKeyboard.hide')}
                ariaLabel={t('kiosk.virtualKeyboard.close')}
                onPress={close}
                wide
              />
            </div>
            {rows.map((row, i) => (
              <div key={i} className="flex justify-center gap-1.5">
                {row.map((ch) => (
                  <Key
                    key={ch}
                    label={upper && layout !== 'symbols' ? ch.toUpperCase() : ch}
                    onPress={() => type(ch)}
                  />
                ))}
              </div>
            ))}
            <div className="flex justify-center gap-1.5">
              {layout !== 'symbols' && (
                <Key
                  label={shift === 'lock' ? t('kiosk.virtualKeyboard.capsLock') : t('kiosk.virtualKeyboard.shift')}
                  onPress={toggleShift}
                  active={shift !== 'off'}
                  accent
                  wide
                />
              )}
              <button
                type="button"
                tabIndex={-1}
                aria-label={t('kiosk.virtualKeyboard.space')}
                onPointerDown={preventFocusSteal}
                onClick={() => type(' ')}
                className="inline-flex h-[3.2rem] min-w-[18rem] flex-1 items-center justify-center rounded-md bg-text/10 text-base font-semibold text-muted hover:bg-text/20 active:scale-[0.98]"
              >
                {t('kiosk.virtualKeyboard.space')}
              </button>
              <Key label="⌫" ariaLabel={t('kiosk.virtualKeyboard.backspace')} onPress={backspace} accent wide />
              <Key label={t('kiosk.virtualKeyboard.enter')} onPress={enter} accent wide />
            </div>
          </div>
        </motion.div>
      )}
    </AnimatePresence>
  );
}
