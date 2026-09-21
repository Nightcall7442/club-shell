/**
 * Home-screen tile grid: launch the last played game, top up, order food, call admin, add time, lock the PC.
 * Tiles hide with their feature toggle; dialogs are reused from SessionTimer (extend) and Support (call admin).
 */
import { useState, type ReactNode } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { useSession } from '@/hooks/useSession';
import { track } from '@/lib/analytics';
import { ExtendSessionModal } from '@/screens/Desktop/SessionTimer';
import { CallAdminModal } from '@/screens/Support/CallAdminButton';
import { selectRecentGames, useGamesStore } from '@/store/games';
import { useNotificationsStore } from '@/store/notifications';
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

const ICONS: Record<QuickActionKey, JSX.Element> = {
  launch: (
    <svg {...svgProps}>
      <path d="M8 5.5v13l11-6.5-11-6.5Z" />
    </svg>
  ),
  topup: (
    <svg {...svgProps}>
      <path d="M3 7a2 2 0 0 1 2-2h13a1 1 0 0 1 1 1v2H5a2 2 0 0 1-2-2Zm0 0v10a2 2 0 0 0 2 2h15a1 1 0 0 0 1-1v-7a1 1 0 0 0-1-1H5" />
      <path d="M12 11v6M9 14h6" />
    </svg>
  ),
  shop: (
    <svg {...svgProps}>
      <path d="M4 12h16l-1 8H5l-1-8Z" />
      <path d="M6 12a6 6 0 0 1 12 0M12 4v2" />
    </svg>
  ),
  callAdmin: (
    <svg {...svgProps}>
      <path d="M6 16V11a6 6 0 0 1 12 0v5l1.5 2h-15L6 16Z" />
      <path d="M10 20a2 2 0 0 0 4 0" />
    </svg>
  ),
  extend: (
    <svg {...svgProps}>
      <circle cx="12" cy="13" r="8" />
      <path d="M12 9v4l3 2M9 2h6" />
    </svg>
  ),
  lock: (
    <svg {...svgProps}>
      <rect x="5" y="11" width="14" height="10" rx="2" />
      <path d="M8 11V7a4 4 0 0 1 8 0v4" />
    </svg>
  ),
};

// ---------------------------------------------------------------------------------------------------------------------
// Tile
// ---------------------------------------------------------------------------------------------------------------------

export type QuickActionKey = 'launch' | 'topup' | 'shop' | 'callAdmin' | 'extend' | 'lock';

export interface QuickActionTileProps {
  icon: ReactNode;
  label: string;
  hint?: string;
  onClick: () => void;
  disabled?: boolean;
  loading?: boolean;
  tone?: 'primary' | 'accent' | 'danger';
  className?: string;
}

const TONE: Record<NonNullable<QuickActionTileProps['tone']>, string> = {
  primary: 'text-primary bg-primary/15',
  accent: 'text-accent bg-accent/15',
  danger: 'text-danger bg-danger/15',
};

export function QuickActionTile({
  icon,
  label,
  hint,
  onClick,
  disabled = false,
  loading = false,
  tone = 'primary',
  className,
}: QuickActionTileProps): JSX.Element {
  return (
    <button
      type="button"
      data-nav="true"
      disabled={disabled || loading}
      aria-busy={loading || undefined}
      aria-label={hint ? `${label}. ${hint}` : label}
      onClick={onClick}
      className={clsx(
        'focus-ring glass group flex min-h-[9rem] flex-col items-start justify-between gap-3 rounded-xl p-5 text-left transition-[transform,background-color] duration-[var(--dur-fast)] ease-[var(--ease-out)]',
        'hover:bg-surface/80 active:scale-[0.98] disabled:cursor-not-allowed disabled:opacity-50 disabled:active:scale-100',
        className,
      )}
    >
      <span
        aria-hidden="true"
        className={clsx(
          'inline-flex h-14 w-14 items-center justify-center rounded-lg [&>svg]:h-8 [&>svg]:w-8',
          TONE[tone],
          loading && 'anim-glow',
        )}
      >
        {icon}
      </span>
      <span className="flex w-full min-w-0 flex-col gap-0.5">
        <span className="truncate text-lg font-bold leading-tight text-text">{label}</span>
        {hint && <span className="line-clamp-2 text-sm text-muted">{hint}</span>}
      </span>
    </button>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Grid
// ---------------------------------------------------------------------------------------------------------------------

export function QuickActions({ className }: { className?: string }): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const features = useSettingsStore(selectFeatures);
  const recent = useGamesStore(selectRecentGames);
  const launching = useGamesStore((s) => s.launching);
  const launch = useGamesStore((s) => s.launch);
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const { isOpen, isOpenEnded, isActive, lock, busy } = useSession();

  const [extendOpen, setExtendOpen] = useState(false);
  const [callOpen, setCallOpen] = useState(false);
  const [lockOpen, setLockOpen] = useState(false);
  const [locking, setLocking] = useState(false);

  const last = recent[0] ?? null;

  const onLaunch = async (): Promise<void> => {
    if (!last) {
      navigate('/games');
      return;
    }
    track('quickAction', { key: 'launch', gameId: last.id });
    if (!last.installed) {
      navigate(`/games/${last.id}`);
      return;
    }
    try {
      await launch(last.id);
      push({
        id: `launch-${last.id}`,
        title: t('games.launchTitle', { title: last.title }),
        body: t('games.launchHint'),
        level: 'info',
        ttlSec: 8,
        source: 'local',
      });
    } catch (e) {
      pushError(e, t('games.launchFailed'));
    }
  };

  const go = (key: QuickActionKey, to: string): void => {
    track('quickAction', { key });
    navigate(to);
  };

  const onLock = async (): Promise<void> => {
    setLocking(true);
    track('quickAction', { key: 'lock' });
    try {
      await lock('user');
      setLockOpen(false);
      navigate('/lock');
    } catch (e) {
      pushError(e, t('session.lockTitle'));
    } finally {
      setLocking(false);
    }
  };

  return (
    <section aria-label={t('desktop.quickActions')} className={className}>
      <h2 className="mb-3 text-xl font-bold text-text">{t('desktop.quickActions')}</h2>
      <div className="grid grid-cols-[repeat(auto-fit,minmax(clamp(160px,12vw,240px),1fr))] gap-[var(--gap)]">
        <QuickActionTile
          icon={ICONS.launch}
          label={t('desktop.launchLast')}
          hint={last ? last.title : t('desktop.noRecent')}
          loading={launching !== null && last !== null && launching.gameId === last.id}
          onClick={() => void onLaunch()}
        />
        {features.topup && (
          <QuickActionTile
            icon={ICONS.topup}
            label={t('desktop.topUp')}
            hint={t('wallet.topUpHint')}
            tone="accent"
            onClick={() => go('topup', '/wallet')}
          />
        )}
        {features.shop && (
          <QuickActionTile
            icon={ICONS.shop}
            label={t('desktop.shop')}
            hint={t('shop.subtitle')}
            tone="accent"
            onClick={() => go('shop', '/shop')}
          />
        )}
        {features.callAdmin && (
          <QuickActionTile
            icon={ICONS.callAdmin}
            label={t('desktop.callAdmin')}
            hint={t('support.callHint')}
            onClick={() => {
              track('quickAction', { key: 'callAdmin' });
              setCallOpen(true);
            }}
          />
        )}
        <QuickActionTile
          icon={ICONS.extend}
          label={t('desktop.extend')}
          hint={
            isOpen && !isOpenEnded
              ? t('session.extendHint')
              : isOpenEnded
                ? t('session.openEnded')
                : t('session.noSession')
          }
          disabled={!isOpen || isOpenEnded}
          onClick={() => {
            track('quickAction', { key: 'extend' });
            setExtendOpen(true);
          }}
        />
        <QuickActionTile
          icon={ICONS.lock}
          label={t('desktop.lockPc')}
          hint={t('session.lockedHint')}
          tone="danger"
          disabled={!isActive}
          onClick={() => setLockOpen(true)}
        />
      </div>

      <ExtendSessionModal open={extendOpen} onClose={() => setExtendOpen(false)} />
      <CallAdminModal open={callOpen} onClose={() => setCallOpen(false)} />
      <Modal
        open={lockOpen}
        onClose={() => setLockOpen(false)}
        title={t('session.lockTitle')}
        description={t('session.lockConfirm')}
        size="sm"
        footer={
          <>
            <Button variant="ghost" size="lg" onClick={() => setLockOpen(false)} disabled={locking}>
              {t('common.cancel')}
            </Button>
            <Button
              variant="danger"
              size="lg"
              loading={locking || busy}
              icon={ICONS.lock}
              onClick={() => void onLock()}
            >
              {locking ? t('session.locking') : t('session.lock')}
            </Button>
          </>
        }
      />
    </section>
  );
}
