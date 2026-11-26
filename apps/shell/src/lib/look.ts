/**
 * The owner's layout of the player screens (D-104, `shell.club.look`, «Интерфейс и логотипы» → designer): where the club
 * logo stands on the lock and idle screens and in the top bar, how large, which of each screen's own elements show, the
 * picture behind it with its dimming and blur, the owner's texts, the headings font and the plates' tint.
 *
 * Pure (no React, no stores): `look.test.ts` runs it under `node --test`. Without a look every helper answers today's
 * layout exactly, so a club that never opened the designer sees no change.
 */
import type { Locale, ShellLook, ShellScreenLook, ShellText } from '@clubshell/contracts';
import { contrastRatio, hexToRgb, isLight, shade } from '../theme/colors.ts';

export type LookScreen = 'lock' | 'idle' | 'desktop';
export type LogoPosition = 'topLeft' | 'topCenter' | 'topRight' | 'center' | 'bottomLeft' | 'bottomRight';
export type LogoSize = 's' | 'm' | 'l' | 'xl';
export type DisplayFont = 'unbounded' | 'inter' | 'mono';

/**
 * Where the logo may stand on each screen, so it never lands on a fixed element: on the lock screen not in the footer
 * (the power and «Позвать администратора» buttons) and `center` sits above the sign-in card; on the idle screen `center`
 * sits above the clock; the top bar has its tabs in the middle and its controls on the right.
 */
export const LOGO_POSITIONS: Record<LookScreen, readonly LogoPosition[]> = {
  lock: ['topLeft', 'topCenter', 'topRight', 'center'],
  idle: ['topLeft', 'topCenter', 'topRight', 'center', 'bottomLeft', 'bottomRight'],
  desktop: ['topLeft'],
};

/** The top bar is too low for the largest logo. */
export const LOGO_SIZES: Record<LookScreen, readonly LogoSize[]> = {
  lock: ['s', 'm', 'l', 'xl'],
  idle: ['s', 'm', 'l', 'xl'],
  desktop: ['s', 'm', 'l'],
};

/**
 * The logo's box per screen and size. `m` is today's size on that screen (lock 1.75rem, idle 2rem, top bar 1.5rem); the
 * larger ones follow the screen height so a 1366×768 lock screen still fits its sign-in card.
 */
const LOGO_BOX: Record<LookScreen, Record<LogoSize, string>> = {
  lock: { s: '1.25rem', m: '1.75rem', l: 'clamp(2.25rem, 4.6vh, 3.25rem)', xl: 'clamp(3rem, 8vh, 5.5rem)' },
  idle: { s: '1.5rem', m: '2rem', l: 'clamp(2.75rem, 5.5vh, 4rem)', xl: 'clamp(4rem, 10vh, 7rem)' },
  desktop: { s: '1.25rem', m: '1.5rem', l: '2rem', xl: '2rem' },
};

export function logoBox(screen: LookScreen, size: LogoSize): string {
  return LOGO_BOX[screen][size];
}

/** The club name's type next to the logo: today's at `m`, a step down or up with the logo. */
const NAME_CLASS: Record<LookScreen, Record<LogoSize, string>> = {
  lock: { s: 'text-xl', m: 'text-2xl', l: 'text-3xl', xl: 'text-[clamp(1.875rem,3.6vh,2.5rem)]' },
  idle: {
    s: 'text-2xl',
    m: 'text-3xl',
    l: 'text-[clamp(1.875rem,3.6vh,2.5rem)]',
    xl: 'text-[clamp(2.25rem,4.6vh,3.25rem)]',
  },
  desktop: { s: 'text-sm', m: 'text-sm', l: 'text-base', xl: 'text-base' },
};

export function nameClass(screen: LookScreen, size: LogoSize): string {
  return NAME_CLASS[screen][size];
}

/** One screen's layout with every default filled in. */
export interface ScreenLayout {
  logo: { show: boolean; position: LogoPosition; size: LogoSize; name: boolean };
  /** The picture behind the screen; `null` — none (the theme's background, or the lock screen's club art). */
  wallpaperUrl: string | null;
  /** Dimming 0–0.8; `null` — the screen's own default veil. */
  dim: number | null;
  /** `true`/`false` — the owner's blur; `null` — the theme's. */
  blur: boolean | null;
  clock: boolean;
  language: boolean;
  prices: boolean;
  freeBadge: boolean;
  title: ShellText | null;
  subtitle: ShellText | null;
  cta: ShellText | null;
}

const DEFAULT_LOGO: ScreenLayout['logo'] = { show: true, position: 'topLeft', size: 'm', name: true };

function oneOf<T extends string>(value: string | null | undefined, allowed: readonly T[], fallback: T): T {
  return allowed.includes(value as T) ? (value as T) : fallback;
}

/**
 * The layout of `screen`. Without a look: today's (logo top left, today's size, name on, every element on, the club
 * wallpaper `fallbackWallpaper` — the idle screen passes none). A position the screen cannot take, or one that would
 * collide with what is on it (bottom left over the idle price list), falls back to the top left.
 */
export function resolveScreen(
  look: ShellLook | null | undefined,
  screen: LookScreen,
  fallbackWallpaper: string | null = null,
): ScreenLayout {
  const s: ShellScreenLook | undefined = look?.[screen];
  if (!s) {
    return {
      logo: DEFAULT_LOGO,
      wallpaperUrl: fallbackWallpaper,
      dim: null,
      blur: null,
      clock: true,
      language: true,
      prices: true,
      freeBadge: true,
      title: null,
      subtitle: null,
      cta: null,
    };
  }
  const prices = s.prices !== false;
  let position = oneOf(s.logo?.position, LOGO_POSITIONS[screen], 'topLeft');
  if (screen === 'idle' && position === 'bottomLeft' && prices) {
    position = 'topLeft';
  }
  const dim = typeof s.dim === 'number' && Number.isFinite(s.dim) ? Math.min(80, Math.max(0, s.dim)) / 100 : null;
  return {
    logo: {
      show: s.logo?.show !== false,
      position,
      size: oneOf(s.logo?.size, LOGO_SIZES[screen], 'm'),
      name: s.logo?.name !== false,
    },
    wallpaperUrl: clean(s.wallpaperUrl),
    dim,
    blur: typeof s.blur === 'boolean' ? s.blur : null,
    clock: s.clock !== false,
    language: s.language !== false,
    prices,
    freeBadge: s.freeBadge !== false,
    title: s.title ?? null,
    subtitle: s.subtitle ?? null,
    cta: s.cta ?? null,
  };
}

function clean(value: string | null | undefined): string | null {
  const v = value?.trim();
  return v ? v : null;
}

/** The owner's line for `locale`, or `null` — then the shell's bundled one for that language. */
export function lookText(text: ShellText | null | undefined, locale: Locale): string | null {
  if (!text || locale === 'unknown') {
    return null;
  }
  return clean(text[locale]);
}

/**
 * A picture of the look on the club's server comes as a path (`/api/v1/media/{id}`): the Agent puts the server's
 * address in front of the club's own logo and wallpaper only, so the look takes it from them. A full URL, a local path
 * with no server known, or nothing stays as it is.
 */
export function onServer(url: string | null, anchors: readonly (string | null | undefined)[]): string | null {
  if (!url || url.length < 2 || url[0] !== '/' || url[1] === '/') {
    return url;
  }
  for (const anchor of anchors) {
    const m = anchor ? /^(https?:\/\/[^/]+)\/api\//i.exec(anchor) : null;
    if (m?.[1]) {
      return m[1] + url;
    }
  }
  return url;
}

/** Contrast the theme's text keeps on the tinted plates (WCAG AA for body text). */
export const TINT_CONTRAST = 4.5;

/**
 * The owner's plate tint made safe for the theme's text: unchanged when the text already reads on it at
 * {@link TINT_CONTRAST}, else darkened (light text) or lightened (dark text) just enough. `null` for no or a malformed
 * tint — the theme's background stays.
 */
export function safeTint(tint: string | null | undefined, text: string): string | null {
  if (!tint || !/^#[0-9a-f]{6}$/i.test(tint) || !hexToRgb(text)) {
    return null;
  }
  const toward = isLight(text) ? -1 : 1;
  for (let i = 0; i <= 40; i += 1) {
    const candidate = shade(tint, (toward * i) / 40);
    if (contrastRatio(text, candidate) >= TINT_CONTRAST) {
      return candidate.toUpperCase();
    }
  }
  return toward < 0 ? '#000000' : '#FFFFFF';
}

/** The CSS font stack of the headings (`--font-display`); only fonts bundled with the shell, never a remote one. */
export function displayFontStack(font: string | null | undefined): string {
  switch (font) {
    case 'inter':
      return '"Inter Variable", "Inter", sans-serif';
    case 'mono':
      return '"JetBrains Mono Variable", "JetBrains Mono", ui-monospace, monospace';
    default:
      return '"Unbounded Variable", "Unbounded", var(--font), sans-serif';
  }
}

/**
 * The veil over the picture as a CSS gradient: `dim` darkens the middle; the top and bottom never go under 55%/80%,
 * so the header and footer (the clock, the prices, the buttons) read at any dimming; `null` — `fallback`, today's.
 */
export function veil(dim: number | null, fallback: string): string {
  if (dim === null) {
    return fallback;
  }
  const top = Math.min(1, Math.max(0.55, dim + 0.15));
  const bottom = Math.min(1, Math.max(0.8, dim + 0.3));
  return `linear-gradient(180deg, rgb(var(--c-bg) / ${top.toFixed(2)}) 0%, rgb(var(--c-bg) / ${dim.toFixed(2)}) 35%, rgb(var(--c-bg) / ${dim.toFixed(2)}) 60%, rgb(var(--c-bg) / ${bottom.toFixed(2)}) 100%)`;
}

/** The blur over the picture: the owner's (16px or none), else the theme's own (`fallback`). */
export function blurFilter(blur: boolean | null, fallback: string): string {
  if (blur === null) {
    return fallback;
  }
  return blur ? 'blur(16px)' : 'none';
}

/**
 * The look of the admin console's storage shape (`settings.branding` fields `lock`, `idle`, `desktop`, `font`, `tint`)
 * as the server sends it to the PCs (`BrandingLook.ToShell`): the shell mock's `?look=` switch uses it.
 */
export function lookFromBranding(branding: Record<string, unknown>, clubWallpaper: string | null): ShellLook | null {
  const keys = ['lock', 'idle', 'desktop', 'font', 'tint'];
  if (!keys.some((k) => branding[k] !== undefined && branding[k] !== null)) {
    return null;
  }
  const obj = (v: unknown): Record<string, unknown> =>
    v && typeof v === 'object' && !Array.isArray(v) ? (v as Record<string, unknown>) : {};
  const str = (v: unknown): string | null => (typeof v === 'string' && v.trim() ? v.trim() : null);
  const on = (v: unknown): boolean => v !== false;
  const text = (v: unknown): ShellText | null => {
    const t = obj(v);
    const out = { ru: str(t['ru']), uz: str(t['uz']), en: str(t['en']) };
    return out.ru || out.uz || out.en ? out : null;
  };
  const screen = (name: LookScreen, fallback: string | null): ShellScreenLook => {
    const s = obj(branding[name]);
    const logo = obj(s['logo']);
    const dim = typeof s['dim'] === 'number' ? Math.min(80, Math.max(0, Math.round(s['dim']))) : null;
    return {
      logo: {
        show: on(logo['show']),
        position: oneOf(str(logo['position']), LOGO_POSITIONS[name], 'topLeft'),
        size: oneOf(str(logo['size']), LOGO_SIZES[name], 'm'),
        name: on(logo['name']),
      },
      wallpaperUrl: str(s['wallpaperUrl']) ?? fallback,
      dim,
      blur: typeof s['blur'] === 'boolean' ? s['blur'] : null,
      clock: on(s['clock']),
      language: on(s['language']),
      prices: on(s['prices']),
      freeBadge: on(s['freeBadge']),
      title: text(s['title']),
      subtitle: text(s['subtitle']),
      cta: text(s['cta']),
    };
  };
  const font = str(branding['font']);
  const tint = str(branding['tint']);
  return {
    lock: screen('lock', clubWallpaper),
    idle: screen('idle', null),
    desktop: screen('desktop', clubWallpaper),
    font: font === 'inter' || font === 'mono' ? font : 'unbounded',
    tint: tint && /^#[0-9a-f]{6}$/i.test(tint) ? tint.toUpperCase() : null,
  };
}
