/**
 * Console copy in three languages. Russian is the source text and the key: `t('Касса')` returns the Uzbek or English
 * line from the tables below, or the Russian text itself when a line is missing (never an empty label).
 * `{name}` placeholders are filled from `vars`.
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

export function t(ru: string, vars?: Record<string, string | number>): string {
  const table = lang === 'uz' ? UZ : lang === 'en' ? EN : null;
  let out = (table && table[ru]) || ru;
  if (vars) {
    for (const [k, v] of Object.entries(vars)) out = out.split(`{${k}}`).join(String(v));
  }
  return out;
}
