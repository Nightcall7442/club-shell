/**
 * Console copy in three languages. Russian is the source text and the key: `t('Касса')` returns the Uzbek or English
 * line from the tables below, or the Russian text itself when a line is missing (never an empty label;
 * `scripts/i18n-check.mjs` keeps the tables complete). `{name}` placeholders are filled from `vars`. A Russian word with
 * two meanings carries its sense before a bar: `t('оплата|Карта')` is the bank card ("Karta", "Card"), `t('Карта')` the
 * hall map; Russian shows the text after it. {@link inLang} renders a slip in its own language (the receipt language of
 * the console may differ from the screen's).
 */
import { useSyncExternalStore } from 'react';
import { UZ, EN } from '@/i18n.tables';

export type Lang = 'ru' | 'uz' | 'en';
export const LANGS: readonly Lang[] = ['ru', 'uz', 'en'];
const KEY = 'clubshell.admin.lang';

let lang: Lang = (() => {
  try {
    const v = localStorage.getItem(KEY);
    return v === 'uz' || v === 'en' ? v : 'ru';
  } catch {
    return 'ru';
  }
})();
const listeners = new Set<() => void>();

export function setLang(next: Lang): void {
  lang = next;
  try {
    localStorage.setItem(KEY, next);
  } catch {
    // keeps working for this session
  }
  document.documentElement.lang = next;
  listeners.forEach((l) => l());
}

export function useLang(): Lang {
  return useSyncExternalStore(
    (l) => {
      listeners.add(l);
      return () => listeners.delete(l);
    },
    () => lang,
  );
}

/** The language {@link inLang} forces while a slip renders; null — the console's own. */
let forced: Lang | null = null;

export function t(ru: string, vars?: Record<string, string | number>): string {
  const l = forced ?? lang;
  const table = l === 'uz' ? UZ : l === 'en' ? EN : null;
  let out = (table && table[ru]) || ru.slice(ru.indexOf('|') + 1);
  if (vars) {
    for (const [k, v] of Object.entries(vars)) out = out.split(`{${k}}`).join(String(v));
  }
  return out;
}

/**
 * Runs `fn` with {@link t} and {@link dateLocale} answering in `l`: a synchronous render (`flushSync`) of a printed slip
 * comes out in the receipt language without touching the screen's.
 */
export function inLang<T>(l: Lang, fn: () => T): T {
  const before = forced;
  forced = l;
  try {
    return fn();
  } finally {
    forced = before;
  }
}

/** The console's own language (not a forced one). */
export function currentLang(): Lang {
  return lang;
}

/** BCP 47 locale for dates and month names in the current console language. */
export function dateLocale(): string {
  const l = forced ?? lang;
  return l === 'en' ? 'en-GB' : l === 'uz' ? 'uz-Latn' : 'ru-RU';
}
