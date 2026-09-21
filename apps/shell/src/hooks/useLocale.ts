/**
 * Locale facade: current UI locale (from i18next, re-rendering on change), the persisted switcher, and `t`.
 */
import type { Locale } from '@clubshell/contracts';
import { useCallback } from 'react';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { LOCALE_NAMES, LOCALE_TAGS, LOCALES, toLocale } from '@/i18n';
import { useSettingsStore } from '@/store/settings';

export interface LocaleFacade {
  locale: Locale;
  /** BCP-47 tag for `Intl` (`ru-RU`). */
  tag: string;
  /** Persists via `settings_set` and switches i18next. */
  setLocale: (locale: Locale) => Promise<void>;
  t: TFunction;
  locales: readonly Locale[];
  names: Readonly<Record<Locale, string>>;
}

export function useLocale(): LocaleFacade {
  const { t, i18n } = useTranslation();
  const persist = useSettingsStore((s) => s.setLocale);
  const locale = toLocale(i18n.resolvedLanguage ?? i18n.language);
  const setLocale = useCallback((next: Locale) => persist(next), [persist]);
  return { locale, tag: LOCALE_TAGS[locale], setLocale, t, locales: LOCALES, names: LOCALE_NAMES };
}
