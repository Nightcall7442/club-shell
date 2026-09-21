/**
 * Active theme. `setTheme` applies the theme to `:root` immediately and persists the choice via the settings
 * store; `bootstrapStores()` also re-applies whenever `settings.theme` changes from elsewhere (Agent push).
 */
import type { Theme } from '@clubshell/contracts';
import { create } from 'zustand';
import { subscribeWithSelector } from 'zustand/middleware';
import { log } from '@/lib/logger';
import { toShellApiError, type ShellError } from '@/lib/tauri';
import { applyTheme, BUILTIN_THEME_NAMES, DEFAULT_THEME, listThemes, loadTheme, normalizeTheme } from '@/theme/themes';
import { asShellError, useSettingsStore, type AsyncStatus } from './settings';

export interface ThemeState {
  theme: Theme;
  name: string;
  themes: string[];
  status: AsyncStatus;
  error: ShellError | null;
}

export interface ThemeActions {
  /** Loads and applies `name` (defaults to `settings.theme`); falls back to the bundled theme on failure. */
  load(name?: string): Promise<Theme>;
  /** Applies + persists (`settings_set { theme }`). Rethrows when persisting fails; the theme stays applied. */
  setTheme(name: string): Promise<Theme>;
  /** Applies an already-resolved theme object (e.g. from `kiosk://themeChanged`). */
  apply(theme: Theme): void;
  loadList(): Promise<string[]>;
}

export type ThemeStore = ThemeState & ThemeActions;

export const useThemeStore = create<ThemeStore>()(
  subscribeWithSelector((set, get) => ({
    theme: DEFAULT_THEME,
    name: DEFAULT_THEME.name,
    themes: [...BUILTIN_THEME_NAMES],
    status: 'idle',
    error: null,

    async load(name) {
      const target = name ?? useSettingsStore.getState().settings.theme;
      set({ status: 'loading', error: null });
      try {
        const theme = await loadTheme(target);
        get().apply(theme);
        set({ status: 'ready' });
        return theme;
      } catch (e) {
        const error = asShellError(e);
        log.warn(`theme.load(${target}) failed`, error);
        get().apply(DEFAULT_THEME);
        set({ status: 'error', error });
        return DEFAULT_THEME;
      }
    },

    async setTheme(name) {
      const theme = await get().load(name);
      try {
        await useSettingsStore.getState().setTheme(theme.name);
      } catch (e) {
        set({ status: 'error', error: asShellError(e) });
        throw toShellApiError(e);
      }
      return theme;
    },

    apply(theme) {
      const t = normalizeTheme(theme);
      applyTheme(t);
      set({ theme: t, name: t.name });
    },

    async loadList() {
      const themes = await listThemes();
      set({ themes });
      return themes;
    },
  })),
);

/** Selector: whether motion should be reduced (`theme.animations === false`). */
export const selectAnimationsEnabled = (s: ThemeStore): boolean => s.theme.animations;
