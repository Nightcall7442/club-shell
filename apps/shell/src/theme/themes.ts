/**
 * Theme loading and application. A `Theme` (contracts, `themes\<name>.json`) is written to CSS variables on
 * `:root`: `--c-<name>: R G B` (so Tailwind opacity modifiers work), `--radius`, `--font`, `--blur`, plus
 * `data-theme` / `data-animations` attributes that `animations.css` keys off and `data-glass` (blur > 0) for
 * translucent panels.
 */
import type { Theme, ThemeColors } from '@clubshell/contracts';
import { api } from '@/lib/tauri';
import { hexToRgb, isLight, onColor, shade, solidFill } from './colors';

export { contrastRatio, hexToRgb, isLight, luminance, onColor, shade, solidFill } from './colors';

/** Alias kept for the module contract. */
export type ThemeDef = Theme;

/** `config/themes/default.json`. */
export const DEFAULT_THEME: Theme = {
  version: 1,
  name: 'default',
  displayName: 'ClubShell Obsidian',
  colors: {
    bg: '#07090C',
    surface: '#0D1117',
    primary: '#F4F4F5',
    accent: '#9ADFFF',
    text: '#E8F1F6',
    muted: '#8B98A5',
    danger: '#EF4444',
    success: '#22C55E',
  },
  radius: 10,
  font: 'Inter',
  backgroundVideo: null,
  // No wallpaper: every screen sits on the plain background (see Background). A club that wants its own picture
  // behind every screen still sets one here.
  wallpaper: null,
  blur: 0,
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
    muted: '#9A92BE',
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

/** The club's accent from the admin console (`ShellSettings.club.accent`); wins over every theme's own accent. */
let accentOverride: string | null = null;

/** Sets (or clears with `null`) the club accent and re-applies the current theme. Invalid colours are ignored. */
export function setAccentOverride(hex: string | null | undefined): void {
  const next = hex && hexToRgb(hex) ? hex : null;
  if (next === accentOverride) {
    return;
  }
  accentOverride = next;
  if (current) {
    applyTheme(current);
  }
}

/** The theme last passed to {@link applyTheme}. */
export function currentTheme(): Theme | null {
  return current;
}

/** Writes the theme to `:root` CSS variables and data attributes. Safe to call repeatedly. */
export function applyTheme(theme: Theme): void {
  const base = normalizeTheme(theme);
  current = base;
  const t = accentOverride ? { ...base, colors: { ...base.colors, accent: accentOverride } } : base;
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
    root.style.setProperty(`--c-on-${key}`, hexToRgb(onColor(t.colors[key], t.colors.bg)) ?? '255 255 255');
  }
  root.style.setProperty('--c-danger-solid', hexToRgb(solidFill(t.colors.danger)) ?? '');
  root.style.setProperty('--radius', `${t.radius}px`);
  root.style.setProperty('--font', `"${t.font.replace(/"/g, '')}"`);
  root.style.setProperty('--blur', `${t.blur}px`);
  root.dataset['theme'] = t.name;
  // Blurred, translucent panels only when the theme asks for them (see `.glass` in tokens.css).
  root.toggleAttribute('data-glass', t.blur > 0);
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
