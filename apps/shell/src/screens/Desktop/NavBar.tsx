/**
 * Vertical navigation of the sidebar: one `NavLink` per feature-enabled screen (icon + label, the active one on a
 * quiet fill), a chat unread count. Gamepad LB/RB cycle the routes unless a tab list is on screen (Tabs owns LB/RB
 * then); B/Escape go home.
 */
import { useCallback, useMemo } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { NavLink, useLocation, useNavigate } from 'react-router-dom';
import type { ShellFeatures } from '@clubshell/contracts';
import { useGamepad } from '@/hooks/useGamepad';
import { selectUnreadTotal, useChatStore } from '@/store/chat';
import { selectFeatures, useSettingsStore } from '@/store/settings';

// ---------------------------------------------------------------------------------------------------------------------
// Icons
// ---------------------------------------------------------------------------------------------------------------------

const svgProps = {
  viewBox: '0 0 24 24',
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 1.6,
  strokeLinecap: 'round',
  strokeLinejoin: 'round',
  'aria-hidden': true,
} as const;

const ICONS = {
  home: (
    <svg {...svgProps}>
      <path d="M3 11l9-7 9 7v9a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1v-9Z" />
    </svg>
  ),
  games: (
    <svg {...svgProps}>
      <path d="M6 8h12a4 4 0 0 1 4 4v3a3 3 0 0 1-5.4 1.8L15 15H9l-1.6 1.8A3 3 0 0 1 2 15v-3a4 4 0 0 1 4-4Z" />
      <path d="M7 11v3M5.5 12.5h3" />
      <circle cx="16.5" cy="11.5" r="0.8" fill="currentColor" stroke="none" />
      <circle cx="18.5" cy="13.5" r="0.8" fill="currentColor" stroke="none" />
    </svg>
  ),
  apps: (
    <svg {...svgProps}>
      <rect x="3" y="3" width="7" height="7" rx="1.5" />
      <rect x="14" y="3" width="7" height="7" rx="1.5" />
      <rect x="3" y="14" width="7" height="7" rx="1.5" />
      <rect x="14" y="14" width="7" height="7" rx="1.5" />
    </svg>
  ),
  shop: (
    <svg {...svgProps}>
      <path d="M4 7h16l-1.2 11a2 2 0 0 1-2 1.8H7.2a2 2 0 0 1-2-1.8L4 7Z" />
      <path d="M8 10V6a4 4 0 0 1 8 0v4" />
    </svg>
  ),
  wallet: (
    <svg {...svgProps}>
      <path d="M3 7a2 2 0 0 1 2-2h13a1 1 0 0 1 1 1v2H5a2 2 0 0 1-2-2Zm0 0v10a2 2 0 0 0 2 2h15a1 1 0 0 0 1-1v-7a1 1 0 0 0-1-1H5" />
      <circle cx="16" cy="14" r="1.2" fill="currentColor" stroke="none" />
    </svg>
  ),
  chat: (
    <svg {...svgProps}>
      <path d="M4 6a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H9l-5 4V6Z" />
      <path d="M8 9h8M8 12h5" />
    </svg>
  ),
  booking: (
    <svg {...svgProps}>
      <rect x="3" y="5" width="18" height="16" rx="2" />
      <path d="M3 10h18M8 3v4M16 3v4" />
      <path d="M8 15l2.5 2.5L16 12" />
    </svg>
  ),
  tournaments: (
    <svg {...svgProps}>
      <path d="M8 4h8v5a4 4 0 0 1-8 0V4Z" />
      <path d="M8 6H5a1 1 0 0 0-1 1v1a3 3 0 0 0 3 3M16 6h3a1 1 0 0 1 1 1v1a3 3 0 0 1-3 3" />
      <path d="M12 13v4M8 21h8M9 17h6" />
    </svg>
  ),
  support: (
    <svg {...svgProps}>
      <circle cx="12" cy="12" r="9" />
      <path d="M9.5 9.5a2.5 2.5 0 1 1 3.5 2.3c-.7.3-1 .8-1 1.5v.2" />
      <circle cx="12" cy="17" r="0.8" fill="currentColor" stroke="none" />
    </svg>
  ),
} as const;

// ---------------------------------------------------------------------------------------------------------------------
// Items
// ---------------------------------------------------------------------------------------------------------------------

export interface NavItemDef {
  key: keyof typeof ICONS;
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
    <nav aria-label={t('common.menu')} className={clsx('min-w-0', className)}>
      <ul role="list" className="flex flex-col gap-0.5">
        {items.map((item) => {
          const label = t(`desktop.nav.${item.key}`);
          const badge = item.key === 'chat' && unread > 0 ? (unread > 99 ? '99+' : String(unread)) : null;
          return (
            <li key={item.key}>
              <NavLink
                to={item.to}
                data-nav="true"
                aria-label={badge ? `${label}, ${t('chat.unread', { count: unread })}` : label}
                className={({ isActive }) =>
                  clsx(
                    'focus-ring flex h-11 items-center gap-3 rounded-md px-3 text-[0.95rem] font-medium transition-colors duration-[var(--dur-fast)]',
                    // Active: an accent rail on the left edge and a light that fades out to the right.
                    isActive
                      ? 'rounded-l-none bg-gradient-to-r from-accent/[0.13] to-transparent text-text shadow-[inset_2px_0_0_rgb(var(--c-accent))]'
                      : 'text-muted hover:bg-text/[0.04] hover:text-text',
                  )
                }
              >
                <span
                  aria-hidden="true"
                  className="inline-flex h-5 w-5 shrink-0 items-center justify-center [&>svg]:h-full [&>svg]:w-full"
                >
                  {ICONS[item.key]}
                </span>
                <span className="min-w-0 flex-1 truncate">{label}</span>
                {badge && (
                  <span aria-hidden="true" className="font-mono text-xs font-medium text-accent">
                    {badge}
                  </span>
                )}
              </NavLink>
            </li>
          );
        })}
      </ul>
    </nav>
  );
}
