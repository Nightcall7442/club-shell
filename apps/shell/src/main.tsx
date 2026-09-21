/**
 * Entry point. Boot order: global error handlers → kiosk input guards → i18n (default locale) → stores
 * (settings → theme + locale + system → listeners → auth/session → catalogue) → theme applied → `<App/>`.
 *
 * The same bundle serves three webviews, told apart by the hash: the main window, the transparent always-on-top
 * `#/overlay` window and the secondary-monitor `#/ads` windows. The latter two only need settings, theme and
 * i18n, so they skip the session-level bootstrap.
 */
import { Component, StrictMode, type ErrorInfo, type ReactNode } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import './index.css';
import i18n, { initI18n } from '@/i18n';
import { installGlobalErrorHandlers, log } from '@/lib/logger';
import { api, events, isTauri } from '@/lib/tauri';
import { App } from '@/App';
import { getWindowRole, type WindowRole } from '@/router';
import { bootstrapStores, DEFAULT_SETTINGS, useSettingsStore, useThemeStore } from '@/store';
import { applyTheme, currentTheme } from '@/theme/themes';

// ---------------------------------------------------------------------------------------------------------------------
// Error boundary
// ---------------------------------------------------------------------------------------------------------------------

interface ErrorBoundaryProps {
  children: ReactNode;
}

interface ErrorBoundaryState {
  error: Error | null;
}

/** Last-resort boundary: logs the crash and offers "try again" (re-mount) or a webview reload. */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  override state: ErrorBoundaryState = { error: null };

  static getDerivedStateFromError(error: Error): ErrorBoundaryState {
    return { error };
  }

  override componentDidCatch(error: Error, info: ErrorInfo): void {
    log.error('Render crash', error, info.componentStack ?? '');
  }

  private readonly retry = (): void => {
    this.setState({ error: null });
  };

  private readonly reload = (): void => {
    if (isTauri()) {
      api.kiosk.reload().catch(() => window.location.reload());
    } else {
      window.location.reload();
    }
  };

  override render(): ReactNode {
    if (!this.state.error) {
      return this.props.children;
    }
    const t = (key: string): string => i18n.t(key);
    return (
      <div role="alert" className="flex h-full w-full items-center justify-center bg-bg p-[var(--gutter)] text-text">
        <div className="glass-strong flex w-[min(40rem,90vw)] flex-col items-center gap-5 rounded-[calc(var(--radius)*2)] px-10 py-9 text-center">
          <svg aria-hidden="true" viewBox="0 0 24 24" className="h-14 w-14 text-danger" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
            <path d="M12 9v4M12 17h.01" />
            <path d="M10.3 3.9 2.6 17.2A2 2 0 0 0 4.3 20h15.4a2 2 0 0 0 1.7-2.8L13.7 3.9a2 2 0 0 0-3.4 0Z" />
          </svg>
          <p className="text-[var(--fs-2xl)] font-bold">{t('common.error')}</p>
          <p className="text-[var(--fs-base)] text-muted">{t('errors.generic')}</p>
          <div className="mt-2 flex flex-wrap justify-center gap-3">
            <button
              type="button"
              autoFocus
              onClick={this.retry}
              className="focus-ring rounded-[var(--radius)] bg-primary px-6 py-3 text-[var(--fs-base)] font-semibold text-text shadow-glow hover:bg-primary/90"
            >
              {t('common.retry')}
            </button>
            <button
              type="button"
              onClick={this.reload}
              className="focus-ring rounded-[var(--radius)] bg-surface/80 px-6 py-3 text-[var(--fs-base)] font-semibold text-text hover:bg-surface"
            >
              {t('settings.reload')}
            </button>
          </div>
        </div>
      </div>
    );
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Kiosk guards
// ---------------------------------------------------------------------------------------------------------------------

const ZOOM_KEYS = new Set(['+', '-', '=', '0', 'add', 'subtract']);

function devtoolsAllowed(): boolean {
  const s = useSettingsStore.getState();
  return s.shellConfig?.devtools ?? s.kiosk?.devtools ?? false;
}

/** Blocks drag, browser zoom (keys, ctrl+wheel, pinch) and — inside the webview — reload/devtools chords. */
function installKioskGuards(): void {
  document.addEventListener('dragstart', (e) => e.preventDefault());
  document.addEventListener('gesturestart', (e) => e.preventDefault());
  document.addEventListener(
    'wheel',
    (e) => {
      if (e.ctrlKey || e.metaKey) {
        e.preventDefault();
      }
    },
    { passive: false },
  );
  document.addEventListener(
    'keydown',
    (e) => {
      const mod = e.ctrlKey || e.metaKey;
      const key = e.key.toLowerCase();
      if (mod && ZOOM_KEYS.has(key)) {
        e.preventDefault();
        return;
      }
      if (!isTauri()) {
        return;
      }
      const reload = key === 'f5' || (mod && key === 'r');
      const fullscreen = key === 'f11';
      const devtools = key === 'f12' || (mod && e.shiftKey && (key === 'i' || key === 'j' || key === 'c')) || (mod && key === 'u');
      const browserUi = mod && (key === 'p' || key === 's' || key === 'o' || key === 'f' || key === 'g' || key === 'h' || key === 'j' || key === 'd');
      if (reload || fullscreen || browserUi || (devtools && !devtoolsAllowed())) {
        e.preventDefault();
        e.stopPropagation();
      }
    },
    { capture: true },
  );
}

/** Hides the cursor after `shell.json → kiosk.hideCursorAfterSec` seconds without pointer movement (0 = never). */
function installCursorHider(): void {
  const root = document.documentElement;
  let timer: ReturnType<typeof setTimeout> | null = null;
  const arm = (): void => {
    if (timer) {
      clearTimeout(timer);
      timer = null;
    }
    delete root.dataset['cursor'];
    const sec = useSettingsStore.getState().shellConfig?.kiosk.hideCursorAfterSec ?? 0;
    if (sec > 0) {
      timer = setTimeout(() => {
        root.dataset['cursor'] = 'hidden';
      }, sec * 1000);
    }
  };
  window.addEventListener('pointermove', arm, { passive: true });
  window.addEventListener('pointerdown', arm, { passive: true });
  arm();
}

// ---------------------------------------------------------------------------------------------------------------------
// Boot
// ---------------------------------------------------------------------------------------------------------------------

/** Pre-i18n splash (plain markup: no `t()` before `initI18n`). */
function BootSplash(): JSX.Element {
  return (
    <div aria-busy="true" className="flex h-full w-full items-center justify-center bg-bg">
      <span className="anim-spin inline-block h-14 w-14 rounded-full border-4 border-solid border-primary border-r-transparent" />
    </div>
  );
}

/** Overlay / ads windows: settings → i18n + theme + shell config, plus theme/locale sync from the kiosk layer. */
async function bootstrapWindow(): Promise<void> {
  const settings = useSettingsStore.getState();
  await settings.load();
  const { locale, theme } = useSettingsStore.getState().settings;
  await Promise.all([initI18n(locale), useThemeStore.getState().load(theme), settings.loadSystem()]);
  events.onKiosk('themeChanged', (p) => {
    if (p.theme) {
      useThemeStore.getState().apply(p.theme);
    } else {
      void useThemeStore.getState().load(p.name);
    }
  });
  events.onKiosk('localeChanged', (p) => void useSettingsStore.getState().applyLocale(p.locale));
}

async function boot(role: WindowRole): Promise<void> {
  const container = document.getElementById('root');
  if (!container) {
    throw new Error('#root missing');
  }
  installGlobalErrorHandlers();
  installKioskGuards();
  document.documentElement.dataset['window'] = role;
  if (role === 'overlay') {
    // The native overlay window is transparent; only what the route draws may be visible.
    document.documentElement.style.background = 'transparent';
    document.body.style.background = 'transparent';
  }

  // Vite HMR re-executes this entry (its exports look like components); reuse the root instead of creating a second one.
  const w = window as unknown as { __clubshellRoot?: Root };
  const root = w.__clubshellRoot ?? createRoot(container);
  w.__clubshellRoot = root;
  root.render(
    <StrictMode>
      <BootSplash />
    </StrictMode>,
  );

  try {
    await initI18n(DEFAULT_SETTINGS.locale);
    if (role === 'main') {
      await bootstrapStores();
    } else {
      await bootstrapWindow();
    }
  } catch (e) {
    // The UI still renders (lock screen shows the connectivity state); the stores retry on the next event.
    log.error('boot failed', e);
  }
  applyTheme(currentTheme() ?? useThemeStore.getState().theme);
  if (role === 'main') {
    installCursorHider();
  }

  root.render(
    <StrictMode>
      <ErrorBoundary>
        <App role={role} />
      </ErrorBoundary>
    </StrictMode>,
  );
  log.info(`shell ready (${role}, ${isTauri() ? 'tauri' : 'mock'})`);
}

void boot(getWindowRole());
