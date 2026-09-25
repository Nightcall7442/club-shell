/**
 * The club's own look from the admin console (`ShellSettings.club`, pushed by the server into `shell.json → club`):
 * name, logo, wallpaper, home banners and rules. Every field falls back to the bundled defaults when the owner left it
 * empty or the server sent no club block at all.
 */
import type { ClubBanner, ClubRules, Locale } from '@clubshell/contracts';
import { useTranslation } from 'react-i18next';
import { toLocale } from '@/i18n';
import { useSettingsStore } from '@/store/settings';

export interface ClubView {
  /** Club name, or the bundled `idle.clubName`. */
  name: string;
  logoUrl: string | null;
  wallpaperUrl: string | null;
  banners: ClubBanner[];
  /** Rules for the current locale, one per line; `null` when the owner set none (the bundled list is shown). */
  rules: string[] | null;
}

const EMPTY_BANNERS: ClubBanner[] = [];

function clean(value: string | null | undefined): string | null {
  const v = value?.trim();
  return v ? v : null;
}

/** Rules text for `locale` split into lines, falling back to Russian (the owner's source language). */
export function clubRules(rules: ClubRules | null | undefined, locale: Locale): string[] | null {
  const text = clean(rules?.[locale]) ?? clean(rules?.ru);
  if (!text) {
    return null;
  }
  const lines = text
    .split('\n')
    .map((l) => l.trim())
    .filter(Boolean);
  return lines.length > 0 ? lines : null;
}

export function useClub(): ClubView {
  const { t, i18n } = useTranslation();
  const club = useSettingsStore((s) => s.settings.club ?? null);
  const locale = toLocale(i18n.resolvedLanguage ?? i18n.language);
  return {
    name: clean(club?.name) ?? t('idle.clubName'),
    logoUrl: clean(club?.logoUrl),
    wallpaperUrl: clean(club?.wallpaperUrl),
    banners: club?.banners ?? EMPTY_BANNERS,
    rules: clubRules(club?.rules, locale),
  };
}
