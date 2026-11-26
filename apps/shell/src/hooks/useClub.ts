/**
 * The club's own look from the admin console (`ShellSettings.club`, pushed by the server into `shell.json → club`):
 * name, logo, wallpaper, home banners and rules. Every field falls back to the bundled defaults when the owner left it
 * empty or the server sent no club block at all.
 */
import { useMemo, type CSSProperties } from 'react';
import type { ClubBanner, ClubRules, Locale, ShellText } from '@clubshell/contracts';
import { useTranslation } from 'react-i18next';
import { toLocale } from '@/i18n';
import { lookText, onServer, resolveScreen, safeTint, type LookScreen, type ScreenLayout } from '@/lib/look';
import { useSettingsStore } from '@/store/settings';
import { useThemeStore } from '@/store/theme';
import { hexToRgb } from '@/theme/colors';

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
  const text = (locale !== 'unknown' ? clean(rules?.[locale]) : null) ?? clean(rules?.ru);
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

export interface ScreenLook extends ScreenLayout {
  /** The owner's line for the player's language, else `fallback` (the bundled one). */
  text(line: ShellText | null, fallback: string): string;
}

/**
 * The owner's layout of `screen` (D-105, `shell.club.look`), every default filled in; without a look, today's layout
 * and the club wallpaper on the lock screen and the desktop. Pictures on the club server get its address.
 */
export function useScreenLook(screen: LookScreen): ScreenLook {
  const { i18n } = useTranslation();
  const club = useSettingsStore((s) => s.settings.club ?? null);
  const locale = toLocale(i18n.resolvedLanguage ?? i18n.language);
  return useMemo(() => {
    const layout = resolveScreen(club?.look, screen, screen === 'idle' ? null : clean(club?.wallpaperUrl));
    const anchors = [
      club?.wallpaperUrl,
      club?.logoUrl,
      ...(club?.banners ?? []).map((b) => b.imageUrl),
      ...(club?.screensaver?.items ?? []).map((i) => i.url),
    ];
    return {
      ...layout,
      wallpaperUrl: onServer(layout.wallpaperUrl, anchors),
      text: (line, fallback) => lookText(line, locale) ?? fallback,
    };
  }, [club, screen, locale]);
}

/**
 * The owner's plate tint (D-105) as a scoped `--c-bg` for the lock and idle screens, made readable for the theme's
 * text; `undefined` — the theme's background.
 */
export function useTintStyle(): CSSProperties | undefined {
  const tint = useSettingsStore((s) => s.settings.club?.look?.tint ?? null);
  const text = useThemeStore((s) => s.theme.colors.text);
  return useMemo(() => {
    const safe = safeTint(tint, text);
    const rgb = safe ? hexToRgb(safe) : null;
    return rgb ? ({ '--c-bg': rgb } as CSSProperties) : undefined;
  }, [tint, text]);
}
