/**
 * Application root: the hash router plus the window-level behaviour that must outlive any single screen —
 * session/auth driven redirects, idle → attract screen, `shell.command{showAds}` fullscreen ads, the kiosk exit
 * hotkey (admin PIN panel) and screen-view analytics. Store-level reactions (toasts for connectivity, staff
 * messages, update banners, remote-control indicator, lock/unlock/reboot/showMessage commands) are wired once in
 * `bootstrapStores()` and rendered by `NotificationCenter` inside the shell and lock screens.
 */
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AnimatePresence, motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { RouterProvider, useLocation, useNavigate } from 'react-router-dom';
import type { ShowAdsArgs } from '@clubshell/contracts';
import { Button } from '@/components/ui/Button';
import { useHotkeys } from '@/hooks/useHotkeys';
import { useAgentEvent, useKioskEvent } from '@/hooks/useTauriEvent';
import { trackScreen } from '@/lib/analytics';
import { createAppRouter, isBareRoute, type WindowRole } from '@/router';
import { AdsCarousel } from '@/screens/Idle/AdsCarousel';
import { AdminPanel } from '@/screens/Profile/Settings';
import { useAuthStore } from '@/store/auth';
import { useGamesStore } from '@/store/games';
import { useSessionStore } from '@/store/session';
import { useSettingsStore } from '@/store/settings';
import { useThemeStore } from '@/store/theme';

/** `shell.json → kiosk.exitHotkey` fallback. */
export const DEFAULT_EXIT_HOTKEY = 'ctrl+alt+shift+f12';
const ADS_FALLBACK_SEC = 15;

// ---------------------------------------------------------------------------------------------------------------------
// Fullscreen ads (`shell.command{showAds}` while the shell is visible; the Rust overlay handles it during a game)
// ---------------------------------------------------------------------------------------------------------------------

export interface AdsOverlayProps {
  args: ShowAdsArgs | null;
  onClose: () => void;
}

/** Plays the playlist once (sum of item durations, `ads.durationSec` fallback) with an optional skip. */
export function AdsOverlay({ args, onClose }: AdsOverlayProps): JSX.Element {
  const { t } = useTranslation();
  const animations = useThemeStore((s) => s.theme.animations);
  const fallbackSec = useSettingsStore((s) => s.shellConfig?.ads.durationSec ?? ADS_FALLBACK_SEC);
  const open = args !== null;
  const skippable = args?.skippable ?? true;

  const totalSec = useMemo(() => {
    if (!args) {
      return 0;
    }
    const sum = args.items.reduce((acc, i) => acc + (i.durationSec > 0 ? i.durationSec : fallbackSec), 0);
    return Math.max(3, sum || fallbackSec);
  }, [args, fallbackSec]);

  useEffect(() => {
    if (!open) {
      return undefined;
    }
    const timer = setTimeout(onClose, totalSec * 1000);
    return () => clearTimeout(timer);
  }, [open, totalSec, onClose]);

  useHotkeys({ escape: onClose, 'kiosk:exit': onClose }, [onClose], { enabled: open && skippable });

  const duration = animations ? 0.3 : 0;
  return (
    <AnimatePresence>
      {open ? (
        <motion.div
          key="ads"
          role="dialog"
          aria-modal="true"
          aria-label={t('idle.ads')}
          className="fixed inset-0 z-[70] bg-bg"
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0 }}
          transition={{ duration }}
        >
          <AdsCarousel
            items={args.items.length > 0 ? args.items : undefined}
            fullscreen
            showCounter={args.items.length > 1}
          />
          <div className="pointer-events-none absolute inset-x-0 top-0 flex items-start justify-between p-[var(--gap)]">
            <span className="glass rounded-full px-4 py-1.5 text-[length:var(--fs-sm)] text-muted">
              {t('idle.ads')}
            </span>
            {skippable ? (
              <Button variant="secondary" size="lg" className="pointer-events-auto" onClick={onClose} autoFocus>
                {t('common.skip')}
              </Button>
            ) : null}
          </div>
        </motion.div>
      ) : null}
    </AnimatePresence>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Global listeners (inside the router context)
// ---------------------------------------------------------------------------------------------------------------------

/** Window-level behaviour of the main webview. Rendered by the router's root layout. */
export function GlobalListeners(): JSX.Element {
  const navigate = useNavigate();
  const { pathname } = useLocation();
  const pathRef = useRef(pathname);
  pathRef.current = pathname;
  const exitHotkey = useSettingsStore((s) => s.shellConfig?.kiosk.exitHotkey || DEFAULT_EXIT_HOTKEY);
  const [adminOpen, setAdminOpen] = useState(false);
  const [ads, setAds] = useState<ShowAdsArgs | null>(null);

  // Screen views (deduped per route inside `trackScreen`).
  useEffect(() => {
    trackScreen(pathname);
  }, [pathname]);

  // Session closed / locked (agent event, admin command, timer) → lock screen.
  useEffect(
    () =>
      useSessionStore.subscribe(
        (s) => s.state,
        (state) => {
          if ((state === 'locked' || state === 'ended' || state === 'idle') && !isBareRoute(pathRef.current)) {
            navigate('/lock', { replace: true });
          }
        },
      ),
    [navigate],
  );

  // Logout / `auth.expired` (the store already cleared the user-scoped slices) → lock screen.
  useEffect(
    () =>
      useAuthStore.subscribe(
        (s) => s.user,
        (user, prev) => {
          if (prev && !user && !isBareRoute(pathRef.current)) {
            navigate('/lock', { replace: true });
          }
        },
      ),
    [navigate],
  );

  // Native idle with nobody logged in → attract screen (the lock screen covers the local fallback).
  useKioskEvent('idle', (p) => {
    const current = pathRef.current;
    if (
      p.idle &&
      useAuthStore.getState().user === null &&
      current !== '/idle' &&
      current !== '/overlay' &&
      current !== '/ads'
    ) {
      navigate('/idle', { replace: true });
    }
  });

  // Ads while the shell is in front; during a game the Rust overlay window plays them instead.
  useAgentEvent('shell.command', (c) => {
    if (c.command === 'showAds' && useGamesStore.getState().running.length === 0 && pathRef.current !== '/ads') {
      setAds(c.args);
    }
  });

  const openAdmin = useCallback(() => setAdminOpen(true), []);
  const closeAdmin = useCallback(() => setAdminOpen(false), []);
  const closeAds = useCallback(() => setAds(null), []);
  useHotkeys({ [exitHotkey]: openAdmin, 'kiosk:exit': openAdmin }, [exitHotkey]);

  return (
    <>
      <AdsOverlay args={ads} onClose={closeAds} />
      <AdminPanel open={adminOpen} onClose={closeAdmin} />
    </>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// App
// ---------------------------------------------------------------------------------------------------------------------

export interface AppProps {
  /** Which webview this document runs in (`main` by default; `overlay` / `ads` get a single-route router). */
  role?: WindowRole;
}

/** Router provider for the given window role. Requires `initI18n()` and the store bootstrap to have run. */
export function App({ role = 'main' }: AppProps): JSX.Element {
  const router = useMemo(
    () => createAppRouter({ role, extras: role === 'main' ? <GlobalListeners /> : undefined }),
    [role],
  );
  return <RouterProvider router={router} />;
}

export default App;
