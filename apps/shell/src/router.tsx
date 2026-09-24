/**
 * Hash router (Tauri serves `index.html`; the overlay and ads windows open `#/overlay` and `#/ads`).
 *
 * - `/lock`, `/idle` render without the shell; every other screen sits under {@link AppShell} behind
 *   {@link RequireSession} (user + open, unlocked session) and, per route, {@link RequireFeature}.
 * - Screens are code-split with `React.lazy`; {@link RouteFallback} shows a spinner while a chunk loads.
 * - Each route carries a {@link RouteHandle} (`titleKey`) that {@link DocumentTitle} writes to `document.title`.
 * - Unknown paths go to the configured default route (`shell.json → ui.defaultRoute`).
 */
import { lazy, Suspense, useEffect, useMemo, useState, type ReactNode } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { createHashRouter, Navigate, Outlet, useLocation, useMatches, type RouteObject } from 'react-router-dom';
import type { NotificationLevel, ShellFeatures } from '@clubshell/contracts';
import { AppShell } from '@/components/layout/AppShell';
import { Badge, levelTone } from '@/components/ui/Badge';
import { Spinner } from '@/components/ui/Spinner';
import { useKioskEvent } from '@/hooks/useTauriEvent';
import { api, type OverlayKind } from '@/lib/tauri';
import { AdsCarousel, type AdItem } from '@/screens/Idle/AdsCarousel';
import { HudScreen } from '@/screens/Overlay/HudScreen';
import { useAuthStore } from '@/store/auth';
import { selectHasSession, selectIsLocked, useSessionStore } from '@/store/session';
import { useSettingsStore } from '@/store/settings';

// ---------------------------------------------------------------------------------------------------------------------
// Lazy screens (default exports)
// ---------------------------------------------------------------------------------------------------------------------

const LockScreen = lazy(() => import('@/screens/Lock/LockScreen'));
const IdleScreen = lazy(() => import('@/screens/Idle/IdleScreen'));
const DesktopScreen = lazy(() => import('@/screens/Desktop/DesktopScreen'));
const GamesScreen = lazy(() => import('@/screens/Games/GamesScreen'));
const GameDetails = lazy(() => import('@/screens/Games/GameDetails'));
const AppsScreen = lazy(() => import('@/screens/Apps/AppsScreen'));
const ShopScreen = lazy(() => import('@/screens/Shop/ShopScreen'));
const WalletScreen = lazy(() => import('@/screens/Wallet/WalletScreen'));
const ChatScreen = lazy(() => import('@/screens/Chat/ChatScreen'));
const BookingScreen = lazy(() => import('@/screens/Booking/BookingScreen'));
const TournamentsScreen = lazy(() => import('@/screens/Tournaments/TournamentsScreen'));
const ProfileScreen = lazy(() => import('@/screens/Profile/ProfileScreen'));
const SupportScreen = lazy(() => import('@/screens/Support/SupportScreen'));

// ---------------------------------------------------------------------------------------------------------------------
// Types & helpers
// ---------------------------------------------------------------------------------------------------------------------

/** Which webview this document runs in, derived from the hash (`#/overlay`, `#/ads?monitor=N`, else main). */
export type WindowRole = 'main' | 'overlay' | 'ads';

/** Per-route metadata (`useMatches()[i].handle`). */
export interface RouteHandle {
  /** i18n key of the screen title (`document.title`). */
  titleKey: string;
  /** Feature toggle that must be on for the route. */
  feature?: keyof ShellFeatures;
}

/** Fallback when `shell.json` has not loaded yet. */
export const FALLBACK_DEFAULT_ROUTE = '/home';

/** Routes that render outside the authenticated shell (no guard, no redirect on session end). */
export const BARE_ROUTES: readonly string[] = ['/lock', '/idle', '/overlay', '/ads'];

export function getWindowRole(hash: string = typeof window === 'undefined' ? '' : window.location.hash): WindowRole {
  const path = hash.replace(/^#/, '').split('?')[0] ?? '';
  if (path === '/overlay' || path.startsWith('/overlay/')) {
    return 'overlay';
  }
  if (path === '/ads' || path.startsWith('/ads/')) {
    return 'ads';
  }
  return 'main';
}

/** `true` for {@link BARE_ROUTES}. */
export function isBareRoute(pathname: string): boolean {
  return BARE_ROUTES.some((r) => pathname === r || pathname.startsWith(`${r}/`));
}

function isRouteHandle(h: unknown): h is RouteHandle {
  return typeof h === 'object' && h !== null && typeof (h as { titleKey?: unknown }).titleKey === 'string';
}

/** Default authenticated route from `shell.json → ui.defaultRoute`. */
export function useDefaultRoute(): string {
  return useSettingsStore((s) => s.shellConfig?.ui.defaultRoute || FALLBACK_DEFAULT_ROUTE);
}

// ---------------------------------------------------------------------------------------------------------------------
// Building blocks
// ---------------------------------------------------------------------------------------------------------------------

/** Full-cell spinner used as the `Suspense` fallback and while auth status is unknown. */
export function RouteFallback({ className }: { className?: string }): JSX.Element {
  return (
    <div className={clsx('flex h-full min-h-[40vh] w-full items-center justify-center', className)}>
      <Spinner size="xl" />
    </div>
  );
}

/** Redirects to `/lock` unless a user is logged in with an open, unlocked session. */
export function RequireSession({ children }: { children: ReactNode }): JSX.Element {
  const location = useLocation();
  const ready = useAuthStore((s) => s.ready);
  const hasUser = useAuthStore((s) => s.user !== null);
  const hasSession = useSessionStore(selectHasSession);
  const locked = useSessionStore(selectIsLocked);

  if (!ready) {
    return <RouteFallback className="min-h-full" />;
  }
  if (!hasUser || !hasSession || locked) {
    return <Navigate to="/lock" replace state={{ from: location.pathname }} />;
  }
  return <>{children}</>;
}

/** Renders `children` only when `settings.features[feature]` is on; otherwise goes to the default route. */
export function RequireFeature({
  feature,
  children,
}: {
  feature: keyof ShellFeatures;
  children: ReactNode;
}): JSX.Element {
  const enabled = useSettingsStore((s) => s.features[feature]);
  const defaultRoute = useDefaultRoute();
  if (!enabled) {
    return <Navigate to={defaultRoute} replace />;
  }
  return <>{children}</>;
}

/** Unknown path → default route (the session guard then decides between shell and `/lock`). */
export function NotFound(): JSX.Element {
  const defaultRoute = useDefaultRoute();
  return <Navigate to={defaultRoute} replace />;
}

/** `document.title` = "<screen> · ClubShell" from the deepest matched {@link RouteHandle}. */
export function DocumentTitle(): null {
  const { t, i18n } = useTranslation();
  const matches = useMatches();
  const titleKey = useMemo(() => {
    for (let i = matches.length - 1; i >= 0; i -= 1) {
      const h = matches[i]?.handle;
      if (isRouteHandle(h)) {
        return h.titleKey;
      }
    }
    return null;
  }, [matches]);

  useEffect(() => {
    const screen = titleKey ? t(titleKey) : '';
    document.title = screen && screen !== 'ClubShell' ? `${screen} · ClubShell` : 'ClubShell';
  }, [titleKey, t, i18n.language]);

  return null;
}

/** Root layout: suspense boundary, title sync and any window-level `extras` (global listeners, admin modal). */
function RootLayout({ extras }: { extras?: ReactNode }): JSX.Element {
  return (
    <>
      <DocumentTitle />
      <Suspense fallback={<RouteFallback className="min-h-full" />}>
        <Outlet />
      </Suspense>
      {extras}
    </>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Overlay window (`#/overlay`): transparent always-on-top webview driven by `kiosk://overlay`
// ---------------------------------------------------------------------------------------------------------------------

interface OverlayState {
  kind: OverlayKind;
  payload: unknown;
}

interface OverlayMessage {
  title: string;
  body: string;
  level: NotificationLevel;
}

const LEVELS: readonly NotificationLevel[] = ['info', 'success', 'warning', 'error'];

function asRecord(value: unknown): Record<string, unknown> {
  return typeof value === 'object' && value !== null ? (value as Record<string, unknown>) : {};
}

function str(rec: Record<string, unknown>, key: string): string | null {
  const v = rec[key];
  return typeof v === 'string' && v.length > 0 ? v : null;
}

/**
 * The Rust layer forwards heterogeneous payloads under `kind: "message"`: `ShowMessageArgs`, `AdminMessage`,
 * `SessionWarning` or `RemoteControlEvent`. Reduce them to a title/body/level triple.
 */
export function overlayMessage(
  payload: unknown,
  t: (key: string, vars?: Record<string, unknown>) => string,
): OverlayMessage {
  const p = asRecord(payload);
  const rawLevel = str(p, 'level');
  const level: NotificationLevel =
    rawLevel && (LEVELS as readonly string[]).includes(rawLevel) ? (rawLevel as NotificationLevel) : 'info';
  const title = str(p, 'title');
  if (title) {
    return { title, body: str(p, 'body') ?? '', level };
  }
  const text = str(p, 'text');
  if (text) {
    const from = str(p, 'from');
    return { title: from ? t('admin.messageTitle', { from }) : t('admin.messageFromStaff'), body: text, level };
  }
  if (typeof p['minutesLeft'] === 'number') {
    const minutes = p['minutesLeft'];
    return {
      title: t('session.warningTitle'),
      body: minutes > 0 ? t('session.warningMinutes', { minutes }) : t('session.lastMinute'),
      level: minutes <= 1 ? 'error' : 'warning',
    };
  }
  const adminName = str(p, 'adminName');
  if (adminName) {
    return { title: t('admin.remoteControl'), body: t('admin.remoteControlBy', { admin: adminName }), level: 'info' };
  }
  return { title: t('kiosk.overlayMessage'), body: '', level };
}

/** Ads playlist carried by `shell.command{showAds}` args (`{ items, skippable }`) or a bare item list. */
export function overlayAds(payload: unknown): AdItem[] {
  const p = asRecord(payload);
  const list = Array.isArray(p['items']) ? p['items'] : Array.isArray(payload) ? payload : [];
  const out: AdItem[] = [];
  for (const raw of list) {
    const item = asRecord(raw);
    const url = str(item, 'url');
    if (!url) {
      continue;
    }
    const type = item['type'] === 'video' ? 'video' : 'image';
    const durationSec = typeof item['durationSec'] === 'number' ? item['durationSec'] : 0;
    out.push({ url, type, durationSec });
  }
  return out;
}

/** Lock veil / staff message / ads for the native overlay window. Renders nothing for `none`. */
export function OverlayScreen(): JSX.Element | null {
  const { t } = useTranslation();
  const [state, setState] = useState<OverlayState>({ kind: 'none', payload: undefined });

  useEffect(() => {
    let active = true;
    api.kiosk
      .state()
      .then((s) => {
        if (active) {
          setState((prev) => (prev.kind === 'none' ? { kind: s.overlay, payload: undefined } : prev));
        }
      })
      .catch(() => undefined);
    return () => {
      active = false;
    };
  }, []);

  useKioskEvent('overlay', (e) => {
    const kind: OverlayKind =
      e.kind === 'lock' || e.kind === 'ads' || e.kind === 'message' || e.kind === 'hud' ? e.kind : 'none';
    setState({ kind, payload: e.payload });
  });

  if (state.kind === 'none') {
    return null;
  }

  if (state.kind === 'lock') {
    return (
      <div
        role="dialog"
        aria-modal="true"
        aria-label={t('lock.locked')}
        className="fixed inset-0 flex items-center justify-center bg-bg/90 backdrop-blur-xl"
      >
        <div className="glass-strong border-glow flex max-w-[40rem] flex-col items-center gap-4 rounded-[calc(var(--radius)*2)] px-12 py-10 text-center">
          <svg
            aria-hidden="true"
            viewBox="0 0 24 24"
            className="h-16 w-16 text-primary"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.8"
            strokeLinecap="round"
            strokeLinejoin="round"
          >
            <rect x="4" y="10" width="16" height="11" rx="2" />
            <path d="M8 10V7a4 4 0 0 1 8 0v3" />
            <circle cx="12" cy="15.5" r="1.3" />
          </svg>
          <p className="text-[length:var(--fs-2xl)] font-bold text-text">{t('lock.locked')}</p>
          <p className="text-[length:var(--fs-lg)] text-muted">{t('admin.lockedByAdmin')}</p>
        </div>
      </div>
    );
  }

  if (state.kind === 'hud') {
    return <HudScreen onClose={() => void api.kiosk.showOverlay('none')} />;
  }

  if (state.kind === 'ads') {
    const items = overlayAds(state.payload);
    return (
      <div className="fixed inset-0 bg-bg" aria-label={t('kiosk.overlayAds')}>
        <AdsCarousel items={items.length > 0 ? items : undefined} fullscreen showCounter={false} />
      </div>
    );
  }

  // Broadcast lower-third over the running game: a colour bar, the minutes left as the hero number, one line of copy.
  const m = overlayMessage(state.payload, t);
  const rawMinutes = asRecord(state.payload)['minutesLeft'];
  const minutes = typeof rawMinutes === 'number' ? rawMinutes : null;
  const tone =
    m.level === 'info' ? 'primary' : m.level === 'warning' ? 'accent' : m.level === 'error' ? 'danger' : 'success';
  return (
    <div className="pointer-events-none fixed inset-x-0 bottom-[7vh] flex justify-center px-[var(--gutter)]">
      <div
        role={m.level === 'error' ? 'alert' : 'status'}
        className="anim-toast-in glass-strong relative flex w-[min(56rem,80vw)] items-center gap-6 overflow-hidden rounded-2xl py-5 pl-8 pr-7"
        style={{
          boxShadow: `inset 0 1px 0 rgb(var(--c-text) / 0.08), 0 30px 80px -24px rgb(0 0 0 / 0.9), 0 0 0 1px rgb(var(--c-${tone}) / 0.25)`,
        }}
      >
        <span
          aria-hidden="true"
          className="absolute inset-y-0 left-0 w-1.5"
          style={{ background: `rgb(var(--c-${tone}))` }}
        />
        {minutes !== null && (
          <span
            aria-hidden="true"
            className={clsx(
              'tnum shrink-0 text-[4.5rem] font-semibold leading-none tracking-tight',
              minutes <= 1 && 'timer-critical',
            )}
            style={{ color: `rgb(var(--c-${tone}))` }}
          >
            {Math.max(0, minutes)}
          </span>
        )}
        <div className="min-w-0 flex-1">
          <p className="text-xs font-semibold text-muted">{m.title}</p>
          <p className="mt-1 truncate text-[length:var(--fs-xl)] font-bold leading-tight text-text">
            {m.body || m.title}
          </p>
        </div>
        <Badge tone={levelTone(m.level)} size="sm" className="shrink-0">
          {t(`notifications.${m.level}`)}
        </Badge>
      </div>
    </div>
  );
}

/** Secondary-monitor ads window (`#/ads?monitor=N&mode=ads|black`). */
export function AdsScreen(): JSX.Element {
  const { t } = useTranslation();
  const { search } = useLocation();
  const mode = useMemo(() => new URLSearchParams(search).get('mode') ?? 'ads', [search]);
  if (mode === 'black') {
    return <div className="fixed inset-0 bg-black" aria-hidden="true" />;
  }
  return (
    <div className="fixed inset-0 bg-bg" aria-label={t('kiosk.adsTitle')}>
      <AdsCarousel fullscreen showCounter={false} />
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Router
// ---------------------------------------------------------------------------------------------------------------------

export interface AppRouterOptions {
  /** Which window this router serves (default `main`). */
  role?: WindowRole;
  /** Rendered inside the router context at the root (global listeners, window-level modals). */
  extras?: ReactNode;
}

function gated(feature: keyof ShellFeatures, element: ReactNode): JSX.Element {
  return <RequireFeature feature={feature}>{element}</RequireFeature>;
}

/** Route table of the main window. */
export function mainRoutes(): RouteObject[] {
  return [
    { index: true, element: <NotFound /> },
    { path: 'lock', element: <LockScreen />, handle: { titleKey: 'lock.title' } satisfies RouteHandle },
    { path: 'idle', element: <IdleScreen />, handle: { titleKey: 'idle.title' } satisfies RouteHandle },
    { path: 'overlay', element: <OverlayScreen />, handle: { titleKey: 'kiosk.overlayTitle' } satisfies RouteHandle },
    { path: 'ads', element: <AdsScreen />, handle: { titleKey: 'kiosk.adsTitle' } satisfies RouteHandle },
    {
      element: (
        <RequireSession>
          <AppShell />
        </RequireSession>
      ),
      children: [
        { path: 'home', element: <DesktopScreen />, handle: { titleKey: 'desktop.title' } satisfies RouteHandle },
        { path: 'games', element: <GamesScreen />, handle: { titleKey: 'games.title' } satisfies RouteHandle },
        { path: 'games/:id', element: <GameDetails />, handle: { titleKey: 'games.title' } satisfies RouteHandle },
        {
          path: 'apps',
          element: gated('apps', <AppsScreen />),
          handle: { titleKey: 'apps.title', feature: 'apps' } satisfies RouteHandle,
        },
        {
          path: 'shop',
          element: gated('shop', <ShopScreen />),
          handle: { titleKey: 'shop.title', feature: 'shop' } satisfies RouteHandle,
        },
        { path: 'wallet', element: <WalletScreen />, handle: { titleKey: 'wallet.title' } satisfies RouteHandle },
        {
          path: 'chat',
          element: gated('chat', <ChatScreen />),
          handle: { titleKey: 'chat.title', feature: 'chat' } satisfies RouteHandle,
        },
        {
          path: 'booking',
          element: gated('booking', <BookingScreen />),
          handle: { titleKey: 'booking.title', feature: 'booking' } satisfies RouteHandle,
        },
        {
          path: 'tournaments',
          element: gated('tournaments', <TournamentsScreen />),
          handle: { titleKey: 'tournaments.title', feature: 'tournaments' } satisfies RouteHandle,
        },
        {
          path: 'profile',
          element: gated('profile', <ProfileScreen />),
          handle: { titleKey: 'profile.title', feature: 'profile' } satisfies RouteHandle,
        },
        { path: 'support', element: <SupportScreen />, handle: { titleKey: 'support.title' } satisfies RouteHandle },
      ],
    },
    { path: '*', element: <NotFound /> },
  ];
}

/** Creates the hash router for `role`; overlay/ads windows get a single-route table. */
export function createAppRouter(options: AppRouterOptions = {}): ReturnType<typeof createHashRouter> {
  const role = options.role ?? 'main';
  let children: RouteObject[];
  if (role === 'overlay') {
    children = [
      { path: '*', element: <OverlayScreen />, handle: { titleKey: 'kiosk.overlayTitle' } satisfies RouteHandle },
    ];
  } else if (role === 'ads') {
    children = [{ path: '*', element: <AdsScreen />, handle: { titleKey: 'kiosk.adsTitle' } satisfies RouteHandle }];
  } else {
    children = mainRoutes();
  }
  return createHashRouter([{ path: '/', element: <RootLayout extras={options.extras} />, children }]);
}
