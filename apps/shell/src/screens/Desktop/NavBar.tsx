/**
 * Section tabs of the top HUD bar, the way a game's pause menu lays them out: `LB` · labels · `RB`. One `NavLink`
 * per feature-enabled screen, the active one underlined by an accent bar that slides between tabs, a chat unread
 * count. Gamepad LB/RB (and the two keycaps, by click) cycle the routes unless a tab list is on screen (Tabs owns
 * LB/RB then); B/Escape go home.
 */
import { useCallback, useMemo } from 'react';
import clsx from 'clsx';
import { motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { NavLink, useLocation, useNavigate } from 'react-router-dom';
import type { ShellFeatures } from '@clubshell/contracts';
import { useGamepad } from '@/hooks/useGamepad';
import { selectUnreadTotal, useChatStore } from '@/store/chat';
import { selectFeatures, useSettingsStore } from '@/store/settings';
import { selectAnimationsEnabled, useThemeStore } from '@/store/theme';

// ---------------------------------------------------------------------------------------------------------------------
// Items
// ---------------------------------------------------------------------------------------------------------------------

export interface NavItemDef {
  key: 'home' | 'games' | 'apps' | 'shop' | 'wallet' | 'chat' | 'booking' | 'tournaments' | 'support';
  to: string;
  /** Feature toggle that hides the item when off. */
  feature?: keyof ShellFeatures;
}

export const NAV_ITEMS: readonly NavItemDef[] = [
  { key: 'home', to: '/home' },
  { key: 'games', to: '/games' },
  { key: 'apps', to: '/apps', feature: 'apps' },
  { key: 'shop', to: '/shop', feature: 'shop' },
  { key: 'wallet', to: '/wallet' },
  { key: 'chat', to: '/chat', feature: 'chat' },
  { key: 'booking', to: '/booking', feature: 'booking' },
  { key: 'tournaments', to: '/tournaments', feature: 'tournaments' },
  { key: 'support', to: '/support' },
];

/** Items visible under the current feature toggles. */
export function visibleNavItems(features: ShellFeatures): NavItemDef[] {
  return NAV_ITEMS.filter((i) => !i.feature || features[i.feature]);
}

// ---------------------------------------------------------------------------------------------------------------------
// Nav bar
// ---------------------------------------------------------------------------------------------------------------------

export function NavBar({ className }: { className?: string }): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const location = useLocation();
  const features = useSettingsStore(selectFeatures);
  const defaultRoute = useSettingsStore((s) => s.shellConfig?.ui.defaultRoute ?? '/home');
  const unread = useChatStore(selectUnreadTotal);
  const items = useMemo(() => visibleNavItems(features), [features]);

  const currentIndex = items.findIndex((i) => location.pathname === i.to || location.pathname.startsWith(`${i.to}/`));

  const cycle = useCallback(
    (dir: 'prev' | 'next') => {
      // ponytail: Tabs owns LB/RB when a tab list is on screen; DOM check beats a shared registry.
      if (document.querySelector('#main [role="tablist"], [role="dialog"] [role="tablist"]')) {
        return;
      }
      if (items.length === 0) {
        return;
      }
      const from = currentIndex < 0 ? 0 : currentIndex;
      const next = items[(from + (dir === 'next' ? 1 : -1) + items.length) % items.length];
      if (next) {
        navigate(next.to);
      }
    },
    [items, currentIndex, navigate],
  );

  const goHome = useCallback(() => {
    if (location.pathname !== defaultRoute && !document.querySelector('[role="dialog"]')) {
      navigate(defaultRoute);
    }
  }, [location.pathname, defaultRoute, navigate]);

  useGamepad({ onTab: cycle, onBack: goHome });

  return (
    <nav aria-label={t('common.menu')} className={clsx('flex min-w-0 items-center gap-3', className)}>
      <Keycap label="LB" title={t('desktop.prevSection')} onClick={() => cycle('prev')} />
      <ul role="list" className="flex min-w-0 items-center">
        {items.map((item) => {
          const label = t(`desktop.nav.${item.key}`);
          const badge = item.key === 'chat' && unread > 0 ? (unread > 99 ? '99+' : String(unread)) : null;
          return (
            <li key={item.key} className="shrink-0">
              <NavLink
                to={item.to}
                data-nav="true"
                aria-label={badge ? `${label}, ${t('chat.unread', { count: unread })}` : label}
                className={({ isActive }) =>
                  clsx(
                    'focus-ring relative flex h-11 items-center gap-1.5 rounded-sm px-[clamp(0.55rem,0.8vw,1rem)] font-display text-[0.72rem] uppercase tracking-[0.08em] transition-colors duration-[var(--dur-fast)]',
                    isActive ? 'text-text' : 'text-muted hover:text-text',
                  )
                }
              >
                {({ isActive }) => (
                  <>
                    <span className="whitespace-nowrap">{label}</span>
                    {badge && (
                      <span aria-hidden="true" className="-mt-2 font-mono text-[0.6rem] font-medium text-accent">
                        {badge}
                      </span>
                    )}
                    {isActive && <ActiveBar />}
                  </>
                )}
              </NavLink>
            </li>
          );
        })}
      </ul>
      <Keycap label="RB" title={t('desktop.nextSection')} onClick={() => cycle('next')} />
    </nav>
  );
}

/** Accent bar under the active tab; slides to the next tab instead of jumping. */
function ActiveBar(): JSX.Element {
  const animations = useThemeStore(selectAnimationsEnabled);
  return (
    <motion.span
      layoutId="nav-active-bar"
      aria-hidden="true"
      className="absolute inset-x-[clamp(0.55rem,0.8vw,1rem)] -bottom-px h-0.5 bg-accent"
      transition={animations ? { type: 'spring', stiffness: 520, damping: 44 } : { duration: 0 }}
    />
  );
}

/** A controller shoulder-button keycap; clicking it does what the button does. */
export function Keycap({ label, title, onClick }: { label: string; title: string; onClick?: () => void }): JSX.Element {
  return (
    <button
      type="button"
      data-nav="true"
      aria-label={title}
      title={title}
      onClick={onClick}
      className="focus-ring inline-flex h-7 min-w-[2.4rem] shrink-0 items-center justify-center rounded-md border border-text/20 px-2 font-mono text-[0.65rem] font-medium text-muted transition-colors duration-[var(--dur-fast)] hover:border-accent/60 hover:text-accent"
    >
      {label}
    </button>
  );
}
