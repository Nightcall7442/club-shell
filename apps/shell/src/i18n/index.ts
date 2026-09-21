/**
 * i18next bootstrap. Bundled resources (`en`/`ru`/`uz`, namespace `translation`) plus an optional flat
 * override bundle from the Agent data dir (`kiosk_i18n_bundle`) when running inside Tauri.
 */
import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import type { Locale } from '@clubshell/contracts';
import { Locale as LocaleValues } from '@clubshell/contracts';
import { api, isTauri } from '@/lib/tauri';
import en from './en.json';
import ru from './ru.json';
import uz from './uz.json';

/** All UI locales. */
export const LOCALES: readonly Locale[] = [LocaleValues.En, LocaleValues.Ru, LocaleValues.Uz];

/** Native display name per locale (for the language switcher). */
export const LOCALE_NAMES: Readonly<Record<Locale, string>> = { en: 'English', ru: 'Русский', uz: "O'zbekcha" };

/** BCP-47 tag per UI locale (Intl formatting). */
export const LOCALE_TAGS: Readonly<Record<Locale, string>> = { en: 'en-US', ru: 'ru-RU', uz: 'uz-UZ' };

/** Narrows any string to a supported {@link Locale}, defaulting to `en`. */
export function toLocale(value: string | null | undefined): Locale {
  return value === 'ru' || value === 'uz' || value === 'en' ? value : 'en';
}

export const resources = {
  en: { translation: en },
  ru: { translation: ru },
  uz: { translation: uz },
} as const;

const overlaysLoaded = new Set<Locale>();

async function loadOverlay(locale: Locale): Promise<void> {
  if (!isTauri() || overlaysLoaded.has(locale)) {
    return;
  }
  try {
    const bundle = await api.kiosk.i18nBundle(locale);
    if (bundle && Object.keys(bundle).length > 0) {
      // Flat `a.b.c` keys are expanded by i18next's keySeparator.
      i18n.addResources(locale, 'translation', bundle);
    }
    overlaysLoaded.add(locale);
  } catch {
    // Overrides are optional; the embedded bundle is authoritative.
  }
}

function applyDocumentLocale(locale: Locale): void {
  if (typeof document !== 'undefined') {
    document.documentElement.lang = locale;
    document.documentElement.dataset['locale'] = locale;
  }
}

/** Initializes i18next once; safe to call again (switches the locale). */
export async function initI18n(locale: Locale): Promise<void> {
  if (i18n.isInitialized) {
    await changeLocale(locale);
    return;
  }
  await i18n.use(initReactI18next).init({
    resources,
    lng: locale,
    fallbackLng: 'en',
    supportedLngs: [...LOCALES],
    ns: ['translation'],
    defaultNS: 'translation',
    keySeparator: '.',
    nsSeparator: false,
    returnNull: false,
    returnEmptyString: false,
    interpolation: { escapeValue: false },
    react: { useSuspense: false },
  });
  applyDocumentLocale(locale);
  await loadOverlay(locale);
}

/** Switches the UI language (bundled resources + Agent overrides). */
export async function changeLocale(locale: Locale): Promise<void> {
  if (!i18n.isInitialized) {
    await initI18n(locale);
    return;
  }
  await loadOverlay(locale);
  if (i18n.language !== locale) {
    await i18n.changeLanguage(locale);
  }
  applyDocumentLocale(locale);
}

/** Current UI locale (narrowed). */
export function currentLocale(): Locale {
  return toLocale(i18n.language);
}

export default i18n;
