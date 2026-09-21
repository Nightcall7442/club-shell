/**
 * Theme loading and application. A `Theme` (contracts, `themes\<name>.json`) is written to CSS variables on
 * `:root`: `--c-<name>: R G B` (so Tailwind opacity modifiers work), `--radius`, `--font`, `--blur`, plus
 * `data-theme` / `data-animations` attributes that `animations.css` keys off.
 */
import type { Theme, ThemeColors } from '@clubshell/contracts';
import { api } from '@/lib/tauri';

/** Alias kept for the module contract. */
export type ThemeDef = Theme;

/** `config/themes/default.json`. */
export const DEFAULT_THEME: Theme = {
  version: 1,
  name: 'default',
  displayName: 'ClubShell Onyx',
  colors: {
    bg: '#09090B',
    surface: '#151518',
    primary: '#F4F4F5',
    accent: '#F2B84B',
    text: '#FAFAFA',
    muted: '#8E8E96',
    danger: '#EF4444',
    success: '#22C55E',
  },
  radius: 12,
  font: 'Inter',
  backgroundVideo: null,
  wallpaper: 'themes/assets/default-wallpaper.jpg',
  blur: 12,
  animations: true,
};

/** `config/themes/neon.json`. */
export const NEON_THEME: Theme = {
  version: 1,
  name: 'neon',
  displayName: 'Neon Night',
  colors: {
    bg: '#07060F',
    surface: '#120F24',
    primary: '#A855F7',
    accent: '#F0ABFC',
    text: '#FAF5FF',
    muted: '#8E85B3',
    danger: '#FB7185',
    success: '#34D399',
  },
  radius: 16,
  font: 'Rajdhani',
  backgroundVideo: 'themes/assets/neon-loop.mp4',
  wallpaper: 'themes/assets/neon-wallpaper.jpg',
  blur: 20,
  animations: true,
};

/** Themes bundled with the shell (fallback when the Agent data dir has none). */
export const builtinThemes: Readonly<Record<string, Theme>> = {
  default: DEFAULT_THEME,
  neon: NEON_THEME,
};

/** Names of the bundled themes. */
export const BUILTIN_THEME_NAMES: readonly string[] = Object.keys(builtinThemes);

/** Colour keys in the order they are written to CSS. */
export const THEME_COLOR_KEYS: readonly (keyof ThemeColors)[] = [
  'bg',
  'surface',
  'primary',
  'accent',
  'text',
  'muted',
  'danger',
  'success',
];

/**
 * `#RGB`, `#RRGGBB` or `#RRGGBBAA` → `"R G B"` (alpha ignored, Tailwind supplies it).
 * Returns `null` for anything else so a malformed theme cannot blank the UI.
 */
export function hexToRgb(hex: string): string | null {
  const m = /^#?([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$/i.exec(hex.trim());
  if (!m) {
    return null;
  }
  let h = m[1] ?? '';
  if (h.length === 3) {
    h = h
      .split('')
      .map((c) => c + c)
      .join('');
  }
  const r = parseInt(h.slice(0, 2), 16);
  const g = parseInt(h.slice(2, 4), 16);
  const b = parseInt(h.slice(4, 6), 16);
  return `${r} ${g} ${b}`;
}

/** Mixes a theme hex colour towards black/white (for hover/active shades); `amount` −1..1. */
export function shade(hex: string, amount: number): string {
  const rgb = hexToRgb(hex);
  if (!rgb) {
    return hex;
  }
  const [r, g, b] = rgb.split(' ').map(Number) as [number, number, number];
  const target = amount < 0 ? 0 : 255;
  const k = Math.min(1, Math.abs(amount));
  const mix = (c: number): number => Math.round(c + (target - c) * k);
  const to = (c: number): string => mix(c).toString(16).padStart(2, '0');
  return `#${to(r)}${to(g)}${to(b)}`;
}

/** WCAG relative luminance 0..1 of a hex colour (`0` for malformed input). */
export function luminance(hex: string): number {
  const rgb = hexToRgb(hex);
  if (!rgb) {
    return 0;
  }
  const [r, g, b] = rgb.split(' ').map((c) => {
    const v = Number(c) / 255;
    return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
  }) as [number, number, number];
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/** `true` when text on this colour should be dark (light primaries such as white buttons). */
export function isLight(hex: string): boolean {
  return luminance(hex) > 0.4;
}

/** Type guard for a `Theme` object read from JSON. */
export function isTheme(value: unknown): value is Theme {
  if (typeof value !== 'object' || value === null) {
    return false;
  }
  const t = value as { name?: unknown; colors?: unknown };
  if (typeof t.name !== 'string' || typeof t.colors !== 'object' || t.colors === null) {
    return false;
  }
  const colors = t.colors as Record<string, unknown>;
  return THEME_COLOR_KEYS.every((k) => typeof colors[k] === 'string');
}

/** Fills missing optional fields from {@link DEFAULT_THEME}. */
export function normalizeTheme(theme: Theme): Theme {
  return {
    ...DEFAULT_THEME,
    ...theme,
    colors: { ...DEFAULT_THEME.colors, ...theme.colors },
    radius: Number.isFinite(theme.radius) ? theme.radius : DEFAULT_THEME.radius,
    blur: Number.isFinite(theme.blur) ? theme.blur : DEFAULT_THEME.blur,
    font: theme.font.trim().length > 0 ? theme.font : DEFAULT_THEME.font,
    animations: theme.animations !== false,
  };
}

/** Theme currently applied to the document (or `null` before the first `applyTheme`). */
let current: Theme | null = null;

/** The theme last passed to {@link applyTheme}. */
export function currentTheme(): Theme | null {
  return current;
}

/** Writes the theme to `:root` CSS variables and data attributes. Safe to call repeatedly. */
export function applyTheme(theme: Theme): void {
  const t = normalizeTheme(theme);
  current = t;
  if (typeof document === 'undefined') {
    return;
  }
  const root = document.documentElement;
  for (const key of THEME_COLOR_KEYS) {
    const rgb = hexToRgb(t.colors[key]) ?? hexToRgb(DEFAULT_THEME.colors[key]);
    if (rgb) {
      root.style.setProperty(`--c-${key}`, rgb);
    }
  }
  // Light primaries (white buttons) darken on hover; dark ones lighten. Text on them flips likewise.
  const dir = isLight(t.colors.primary) ? -1 : 1;
  root.style.setProperty('--c-primary-hover', hexToRgb(shade(t.colors.primary, 0.1 * dir)) ?? '');
  root.style.setProperty('--c-primary-active', hexToRgb(shade(t.colors.primary, 0.2 * dir)) ?? '');
  for (const key of ['primary', 'accent'] as const) {
    const on = isLight(t.colors[key]) ? t.colors.bg : '#FFFFFF';
    root.style.setProperty(`--c-on-${key}`, hexToRgb(on) ?? '255 255 255');
  }
  root.style.setProperty('--radius', `${t.radius}px`);
  root.style.setProperty('--font', `"${t.font.replace(/"/g, '')}"`);
  root.style.setProperty('--blur', `${t.blur}px`);
  root.dataset['theme'] = t.name;
  root.dataset['animations'] = t.animations ? 'true' : 'false';
  root.classList.add('dark');
  const meta = document.querySelector<HTMLMetaElement>('meta[name="theme-color"]');
  if (meta) {
    meta.content = t.colors.bg;
  }
}

/**
 * Loads a theme by name via `settings_get_theme` (Agent data dir, falls back to the embedded default on the
 * Rust side); if that fails, the bundled theme of the same name, else {@link DEFAULT_THEME}.
 */
export async function loadTheme(name: string): Promise<Theme> {
  try {
    const theme = await api.settings.getTheme(name);
    if (isTheme(theme)) {
      return normalizeTheme(theme);
    }
  } catch {
    // fall through to the bundled copy
  }
  return builtinThemes[name] ?? DEFAULT_THEME;
}

/** Lists installed theme names (`settings_list_themes`), falling back to the bundled ones. */
export async function listThemes(): Promise<string[]> {
  try {
    const names = await api.settings.listThemes();
    return names.length > 0 ? names : [...BUILTIN_THEME_NAMES];
  } catch {
    return [...BUILTIN_THEME_NAMES];
  }
}

/** Loads and applies a theme in one step; returns the theme applied. */
export async function switchTheme(name: string): Promise<Theme> {
  const theme = await loadTheme(name);
  applyTheme(theme);
  return theme;
}
