/**
 * Device-level slice: `ShellSettings` (+ feature toggles) persisted through `settings_set`, plus the read-only
 * system context the UI needs everywhere (`shell.json`, `PcInfo`, hardware inventory, live metrics, kiosk state,
 * policy). Loaded first by `bootstrapStores()`; never reset on logout, except the live metrics (`clearMetrics`).
 */
import type {
  GpuInfo,
  HardwareInfo,
  Locale,
  MonitorInfo,
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
import { primaryGpu } from '@/screens/Pc/devices/gpuVendor';

/**
 * How often the Shell asks for a metrics sample while none arrives: the Agent's own sampling period
 * (`telemetry.metricsIntervalSec`, 5 s by default).
 */
export const METRICS_POLL_MS = 5_000;
/** A sample older than this (two missed periods) no longer passes for live. */
const METRICS_STALE_MS = 2 * METRICS_POLL_MS;

/** Lifecycle of an async store action. */
export type AsyncStatus = 'idle' | 'loading' | 'ready' | 'error';

/** Normalizes any rejection into the `ShellError` plain shape stored in `error` fields. */
export function asShellError(e: unknown): ShellError {
  return toShellApiError(e).toJSON();
}

/**
 * Mirrors the Agent's fallbacks: server-backed sections (shop, chat, booking, tournaments, top-up) stay off until the
 * server's config turns them on, so a club whose server lacks them never shows a screen that calls a missing endpoint.
 */
export const DEFAULT_FEATURES: ShellFeatures = {
  shop: false,
  chat: false,
  booking: false,
  tournaments: false,
  profile: true,
  topup: false,
  apps: true,
  callAdmin: true,
  qrLogin: false,
  gpuPanel: false,
};

/**
 * Mirrors `config/shell.default.json` until `settings_get` answers — except the on-screen keyboard, off until the Agent
 * says the club allows it: the Shell often starts before the Agent link is up.
 */
export const DEFAULT_SETTINGS: ShellSettings = {
  locale: 'ru',
  theme: 'default',
  availableThemes: ['default', 'neon'],
  volume: 60,
  muted: false,
  idleTimeoutSec: 300,
  showMetricsOverlay: false,
  allowVirtualKeyboard: false,
  uiSounds: true,
  features: DEFAULT_FEATURES,
};

export interface SettingsState {
  settings: ShellSettings;
  /** Same object as `settings.features`, exposed for cheap selectors. */
  features: ShellFeatures;
  shellConfig: ShellConfig | null;
  pcInfo: PcInfo | null;
  /** `sys_hardware`: read once per app run for the top bar and Home, read again when the full spec sheet opens. */
  hardware: HardwareInfo | null;
  hardwareStatus: AsyncStatus;
  metrics: PcMetrics | null;
  /** `Date.now()` when `metrics` arrived; 0 while there is none. */
  metricsAt: number;
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
  /**
   * One `sys_hardware` for the app's lifetime (the graphics card and monitors the top bar names do not change); calls
   * while it runs share it, a failed one can be retried. The first call on a cold PC is a WMI scan of a few seconds, so
   * nothing waits on it.
   */
  loadHardware(): Promise<void>;
  /**
   * `sys_hardware` again although it is known: free disk space, peripherals and the IP change while the Shell runs for
   * days. The Agent answers from its own cache (rescanned hourly), so this is cheap; the known inventory stays on
   * screen meanwhile and when the read fails.
   */
  refreshHardware(): Promise<void>;
  /**
   * One `sys_metrics` read while the latest sample is missing or stale. The Agent pushes `sys.metrics` only during a
   * session (or with the metrics overlay on), so between sessions `AppShell` asks every {@link METRICS_POLL_MS}. A
   * failed read drops a stale sample: an old reading must not pass for a live one.
   */
  ensureMetrics(): Promise<void>;
  /** Forgets the last sample on logout: it may carry the previous player's game (FPS). */
  clearMetrics(): void;
  /** Asks `sys_pc_info` again while it is still unknown (the Agent link may have been down at boot). */
  ensurePcInfo(): Promise<void>;
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
  hardware: null,
  hardwareStatus: 'idle',
  metrics: null,
  metricsAt: 0,
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

// In-flight reads shared by concurrent callers (the top bar and the home block ask at the same moment).
let hardwareLoad: Promise<void> | null = null;
let metricsLoad: Promise<void> | null = null;
let pcInfoLoad: Promise<void> | null = null;

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

    loadHardware() {
      return get().hardware ? Promise.resolve() : get().refreshHardware();
    },

    refreshHardware() {
      if (!hardwareLoad) {
        // A known inventory stays as it is until the new one is in, and after a failed read.
        set((s) => (s.hardware ? {} : { hardwareStatus: 'loading' }));
        hardwareLoad = api.system
          .hardware()
          .then(
            (hardware) => set({ hardware, hardwareStatus: 'ready' }),
            (e: unknown) => {
              log.warn('system.hardware failed', asShellError(e));
              set((s) => (s.hardware ? {} : { hardwareStatus: 'error' }));
            },
          )
          .finally(() => {
            hardwareLoad = null;
          });
      }
      return hardwareLoad;
    },

    ensureMetrics() {
      if (get().metrics && Date.now() - get().metricsAt < METRICS_STALE_MS) {
        return Promise.resolve();
      }
      const asked = Date.now();
      metricsLoad ??= api.system
        .metrics()
        .then(
          // A `sys.metrics` event may have landed meanwhile: it is the newer sample.
          (metrics) => set((s) => (s.metricsAt > asked ? {} : { metrics, metricsAt: Date.now() })),
          (e: unknown) => {
            if (get().metrics) {
              log.warn('system.metrics failed', asShellError(e));
            }
            set((s) => (s.metricsAt > asked ? {} : { metrics: null, metricsAt: 0 }));
          },
        )
        .finally(() => {
          metricsLoad = null;
        });
      return metricsLoad;
    },

    clearMetrics() {
      set({ metrics: null, metricsAt: 0 });
    },

    ensurePcInfo() {
      if (get().pcInfo) {
        return Promise.resolve();
      }
      pcInfoLoad ??= api.system
        .pcInfo()
        .then(
          (pcInfo) => {
            syncServerTime(pcInfo.serverTime);
            set({ pcInfo });
          },
          (e: unknown) => log.warn('system.pcInfo failed', asShellError(e)),
        )
        .finally(() => {
          pcInfoLoad = null;
        });
      return pcInfoLoad;
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
      set({ metrics, metricsAt: Date.now() });
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
/**
 * Selector: the primary display, live from the kiosk layer (a refresh rate switched a minute ago shows) and from the
 * hardware inventory until the kiosk state is known.
 */
export const selectPrimaryMonitor = (s: SettingsStore): MonitorInfo | null => {
  const live = s.kiosk?.monitors;
  const monitors: readonly MonitorInfo[] = live && live.length > 0 ? live : (s.hardware?.monitors ?? []);
  return monitors.find((m) => m.primary) ?? monitors[0] ?? null;
};
/** Selector: the graphics card games run on (a discrete card before the iGPU next to it). */
export const selectPrimaryGpu = (s: SettingsStore): GpuInfo | null => primaryGpu(s.hardware?.gpu ?? []);
