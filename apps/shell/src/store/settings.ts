/**
 * Device-level slice: `ShellSettings` (+ feature toggles) persisted through `settings_set`, plus the read-only
 * system context the UI needs everywhere (`shell.json`, `PcInfo`, live metrics, kiosk state, policy).
 * Loaded first by `bootstrapStores()`; never reset on logout.
 */
import type {
  Locale,
  PcInfo,
  PcMetrics,
  Policy,
  SettingsSetRequest,
  ShellFeatures,
  ShellSettings,
} from '@clubshell/contracts';
import { create } from 'zustand';
import { subscribeWithSelector } from 'zustand/middleware';
import { changeLocale, toLocale } from '@/i18n';
import { log } from '@/lib/logger';
import { api, isTauri, toShellApiError, type KioskState, type ShellConfig, type ShellError } from '@/lib/tauri';
import { syncServerTime } from '@/lib/time';

/** Lifecycle of an async store action. */
export type AsyncStatus = 'idle' | 'loading' | 'ready' | 'error';

/** Normalizes any rejection into the `ShellError` plain shape stored in `error` fields. */
export function asShellError(e: unknown): ShellError {
  return toShellApiError(e).toJSON();
}

/** Every feature on until the Agent says otherwise (avoids a flash of hidden navigation). */
export const DEFAULT_FEATURES: ShellFeatures = {
  shop: true,
  chat: true,
  booking: true,
  tournaments: true,
  profile: true,
  topup: true,
  apps: true,
  callAdmin: true,
};

/** Mirrors `config/shell.default.json` until `settings_get` answers. */
export const DEFAULT_SETTINGS: ShellSettings = {
  locale: 'ru',
  theme: 'default',
  availableThemes: ['default', 'neon'],
  volume: 60,
  muted: false,
  idleTimeoutSec: 300,
  showMetricsOverlay: false,
  allowVirtualKeyboard: true,
  uiSounds: true,
  features: DEFAULT_FEATURES,
};

export interface SettingsState {
  settings: ShellSettings;
  /** Same object as `settings.features`, exposed for cheap selectors. */
  features: ShellFeatures;
  shellConfig: ShellConfig | null;
  pcInfo: PcInfo | null;
  metrics: PcMetrics | null;
  kiosk: KioskState | null;
  policy: Policy | null;
  /** `true` once `settings_get` succeeded at least once. */
  loaded: boolean;
  status: AsyncStatus;
  error: ShellError | null;
}

export interface SettingsActions {
  /** Loads `ShellSettings`; errors are swallowed (defaults stay in place) and reported in `error`. */
  load(): Promise<ShellSettings>;
  /** Loads shell.json, PcInfo (syncs the server clock), kiosk state and policy; each part fails independently. */
  loadSystem(): Promise<void>;
  /** Optimistic patch persisted via `settings_set`; rolls back and rethrows on failure. */
  set(patch: SettingsSetRequest): Promise<ShellSettings>;
  setLocale(locale: Locale): Promise<void>;
  /** Native volume (`sys_set_volume`) mirrored into settings. */
  setVolume(level: number, muted?: boolean): Promise<void>;
  toggleMute(): Promise<void>;
  setTheme(name: string): Promise<void>;
  setIdleTimeout(sec: number): Promise<void>;
  /** Applies a locale pushed by the Agent (`kiosk://localeChanged`) without a round trip. */
  applyLocale(locale: Locale): Promise<void>;
  setMetrics(metrics: PcMetrics): void;
  setKiosk(kiosk: KioskState): void;
  setPolicy(policy: Policy): void;
  refreshKiosk(): Promise<void>;
  isFeatureEnabled(feature: keyof ShellFeatures): boolean;
}

export type SettingsStore = SettingsState & SettingsActions;

const initialState: SettingsState = {
  settings: DEFAULT_SETTINGS,
  features: DEFAULT_FEATURES,
  shellConfig: null,
  pcInfo: null,
  metrics: null,
  kiosk: null,
  policy: null,
  loaded: false,
  status: 'idle',
  error: null,
};

function normalize(s: ShellSettings): ShellSettings {
  return {
    ...DEFAULT_SETTINGS,
    ...s,
    locale: toLocale(s.locale),
    volume: Math.min(100, Math.max(0, Math.round(s.volume))),
    features: { ...DEFAULT_FEATURES, ...s.features },
  };
}

export const useSettingsStore = create<SettingsStore>()(
  subscribeWithSelector((set, get) => ({
    ...initialState,

    async load() {
      set({ status: 'loading', error: null });
      try {
        const settings = normalize(await api.settings.get());
        set({ settings, features: settings.features, loaded: true, status: 'ready' });
        return settings;
      } catch (e) {
        const error = asShellError(e);
        log.warn('settings.load failed, using defaults', error);
        set({ status: 'error', error });
        return get().settings;
      }
    },

    async loadSystem() {
      const [config, pcInfo, kiosk, policy] = await Promise.allSettled([
        api.settings.getShellConfig(),
        api.system.pcInfo(),
        api.kiosk.state(),
        api.policy.get(),
      ]);
      if (config.status === 'fulfilled') {
        set({ shellConfig: config.value });
      } else {
        log.warn('settings.getShellConfig failed', asShellError(config.reason));
      }
      if (pcInfo.status === 'fulfilled') {
        syncServerTime(pcInfo.value.serverTime);
        set({ pcInfo: pcInfo.value });
      } else {
        log.warn('system.pcInfo failed', asShellError(pcInfo.reason));
      }
      if (kiosk.status === 'fulfilled') {
        set({ kiosk: kiosk.value });
      }
      if (policy.status === 'fulfilled') {
        set({ policy: policy.value });
      }
    },

    async set(patch) {
      const before = get().settings;
      const optimistic = normalize({
        ...before,
        ...(Object.fromEntries(
          Object.entries(patch).filter(([, v]) => v !== null && v !== undefined),
        ) as Partial<ShellSettings>),
      });
      set({ settings: optimistic, features: optimistic.features, status: 'loading', error: null });
      try {
        const saved = normalize(await api.settings.set(patch));
        set({ settings: saved, features: saved.features, loaded: true, status: 'ready' });
        return saved;
      } catch (e) {
        const error = asShellError(e);
        set({ settings: before, features: before.features, status: 'error', error });
        throw toShellApiError(e);
      }
    },

    async setLocale(locale) {
      await get().set({ locale });
      await changeLocale(locale);
    },

    async setVolume(level, muted) {
      const clamped = Math.min(100, Math.max(0, Math.round(level)));
      const before = get().settings;
      set({ settings: { ...before, volume: clamped, muted: muted ?? before.muted } });
      try {
        const v = await api.system.setVolume(clamped, muted);
        set((s) => ({ settings: { ...s.settings, volume: v.level, muted: v.muted } }));
      } catch (e) {
        set({ settings: before });
        throw toShellApiError(e);
      }
    },

    async toggleMute() {
      const { volume, muted } = get().settings;
      await get().setVolume(volume, !muted);
    },

    async setTheme(name) {
      if (get().settings.theme !== name) {
        await get().set({ theme: name });
      }
    },

    async setIdleTimeout(sec) {
      await get().set({ idleTimeoutSec: Math.max(0, Math.round(sec)) });
    },

    async applyLocale(locale) {
      const l = toLocale(locale);
      set((s) => (s.settings.locale === l ? {} : { settings: { ...s.settings, locale: l } }));
      await changeLocale(l);
    },

    setMetrics(metrics) {
      set({ metrics });
    },

    setKiosk(kiosk) {
      set({ kiosk });
    },

    setPolicy(policy) {
      set({ policy });
    },

    async refreshKiosk() {
      try {
        set({ kiosk: await api.kiosk.state() });
      } catch (e) {
        if (isTauri()) {
          log.warn('kiosk.state failed', asShellError(e));
        }
      }
    },

    isFeatureEnabled(feature) {
      return get().features[feature];
    },
  })),
);

/** Selector: current UI locale. */
export const selectLocale = (s: SettingsStore): Locale => s.settings.locale;
/** Selector: feature toggles. */
export const selectFeatures = (s: SettingsStore): ShellFeatures => s.features;
/** Selector factory: one feature flag. */
export const selectFeature =
  (feature: keyof ShellFeatures) =>
  (s: SettingsStore): boolean =>
    s.features[feature];
