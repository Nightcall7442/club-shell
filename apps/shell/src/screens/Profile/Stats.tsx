/**
 * Stats tab: headline numbers (hours, sessions, spent, rank) and the favourite-games list with hour bars.
 * Covers come from the games store (loaded on demand; the list still renders without them).
 */
import { useEffect, type ReactNode } from 'react';
import type { FavoriteGame, UserStats } from '@clubshell/contracts';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { GameArtwork } from '@/components/media/GameArtwork';
import { DotAmount } from '@/components/ui/DotAmount';
import { ProgressBar } from '@/components/ui/ProgressBar';
import { Skeleton } from '@/components/ui/Skeleton';
import { useLocale } from '@/hooks/useLocale';
import { formatMoney, formatNumber, pluralize } from '@/lib/format';
import { useGamesStore } from '@/store/games';

export type StatTone = 'primary' | 'accent' | 'success' | 'neutral';

export interface StatCardProps {
  label: string;
  value: string;
  hint?: string;
  icon?: ReactNode;
  tone?: StatTone;
  className?: string;
}

const TONE: Record<StatTone, string> = {
  primary: 'bg-primary/15 text-primary',
  accent: 'bg-accent/15 text-accent',
  success: 'bg-success/15 text-success',
  neutral: 'bg-text/10 text-text',
};

/** One headline number. */
export function StatCard({ label, value, hint, icon, tone = 'primary', className }: StatCardProps): JSX.Element {
  return (
    <div className={clsx('glass flex items-center gap-4 rounded-xl p-5', className)}>
      {icon && (
        <span
          aria-hidden="true"
          className={clsx(
            'inline-flex h-14 w-14 shrink-0 items-center justify-center rounded-lg [&>svg]:h-7 [&>svg]:w-7',
            TONE[tone],
          )}
        >
          {icon}
        </span>
      )}
      <div className="min-w-0">
        <p className="text-sm text-muted">{label}</p>
        <p className="truncate text-3xl leading-tight">
          <DotAmount value={value} />
        </p>
        {hint && <p className="text-sm text-muted">{hint}</p>}
      </div>
    </div>
  );
}

const ICON_PROPS = {
  viewBox: '0 0 24 24',
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 2,
  strokeLinecap: 'round',
  strokeLinejoin: 'round',
} as const;

function ClockIcon(): JSX.Element {
  return (
    <svg {...ICON_PROPS} aria-hidden="true">
      <circle cx="12" cy="12" r="9" />
      <path d="M12 7v5l3 2" />
    </svg>
  );
}

function PlayIcon(): JSX.Element {
  return (
    <svg {...ICON_PROPS} aria-hidden="true">
      <path d="M7 5v14l11-7z" />
    </svg>
  );
}

function WalletIcon(): JSX.Element {
  return (
    <svg {...ICON_PROPS} aria-hidden="true">
      <rect x="3" y="6" width="18" height="13" rx="2" />
      <path d="M3 10h18M16 14h2" />
    </svg>
  );
}

function TrophyIcon(): JSX.Element {
  return (
    <svg {...ICON_PROPS} aria-hidden="true">
      <path d="M8 4h8v5a4 4 0 0 1-8 0zM8 6H5a3 3 0 0 0 3 5M16 6h3a3 3 0 0 1-3 5M12 13v4M8 20h8" />
    </svg>
  );
}

export interface FavoriteGamesProps {
  games: FavoriteGame[];
}

/** Most-played games with hour bars relative to the top entry; each row opens the game page. */
export function FavoriteGames({ games }: FavoriteGamesProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const navigate = useNavigate();
  const byId = useGamesStore((s) => s.byId);
  const load = useGamesStore((s) => s.load);

  useEffect(() => {
    void load();
  }, [load]);

  const sorted = [...games].sort((a, b) => b.hours - a.hours);
  const max = sorted[0]?.hours ?? 0;

  return (
    <ul role="list" className="flex flex-col gap-2">
      {sorted.map((f) => {
        const game = byId.get(f.gameId);
        const title = game?.title ?? t('common.unknown');
        return (
          <li key={f.gameId}>
            <button
              type="button"
              data-nav="true"
              onClick={() => navigate(`/games/${f.gameId}`)}
              className="focus-ring flex w-full items-center gap-4 rounded-lg p-2 text-left transition-colors duration-[var(--dur-fast)] hover:bg-text/5"
            >
              <div className="w-14 shrink-0">
                <GameArtwork src={game?.coverUrl} title={title} className="rounded-md" />
              </div>
              <div className="min-w-0 flex-1">
                <p className="mb-1.5 truncate text-lg font-semibold">{title}</p>
                <ProgressBar
                  value={f.hours}
                  max={max}
                  size="sm"
                  label={title}
                  valueText={t('profile.hoursPlayed', {
                    hours: formatNumber(f.hours, locale, { maximumFractionDigits: 1 }),
                  })}
                />
              </div>
            </button>
          </li>
        );
      })}
    </ul>
  );
}

export interface StatsProps {
  stats: UserStats | null;
  loading: boolean;
}

export function Stats({ stats, loading }: StatsProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();

  if (loading) {
    return (
      <div className="flex flex-col gap-[var(--gap)]">
        <div className="grid grid-cols-2 gap-[var(--gap)] xl:grid-cols-4">
          {Array.from({ length: 4 }, (_, i) => (
            <Skeleton key={i} variant="rect" height="6.5rem" className="rounded-xl" />
          ))}
        </div>
        <div className="glass rounded-xl p-[var(--gap)]">
          <Skeleton variant="text" width="30%" className="mb-4" />
          <Skeleton variant="text" lines={4} />
        </div>
      </div>
    );
  }

  if (!stats || stats.sessionsCount === 0) {
    return (
      <div className="glass flex min-h-[16rem] flex-col items-center justify-center gap-2 rounded-xl p-[var(--gap)] text-center">
        <span
          aria-hidden="true"
          className="inline-flex h-16 w-16 items-center justify-center rounded-full bg-primary/15 text-primary [&>svg]:h-8 [&>svg]:w-8"
        >
          <ClockIcon />
        </span>
        <p className="text-lg text-muted">{stats ? t('profile.statsEmpty') : t('common.unavailable')}</p>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-[var(--gap)]">
      <div className="grid grid-cols-2 gap-[var(--gap)] xl:grid-cols-4">
        <StatCard
          label={t('profile.totalHours')}
          value={t('profile.hoursPlayed', {
            hours: formatNumber(stats.totalHours, locale, { maximumFractionDigits: 1 }),
          })}
          icon={<ClockIcon />}
          tone="primary"
        />
        <StatCard
          label={t('profile.sessions')}
          value={pluralize('sessions', stats.sessionsCount)}
          icon={<PlayIcon />}
          tone="accent"
        />
        <StatCard
          label={t('profile.spent')}
          value={formatMoney(stats.spent, locale)}
          icon={<WalletIcon />}
          tone="success"
        />
        <StatCard
          label={t('profile.rank')}
          value={stats.rank > 0 ? t('profile.rankValue', { rank: formatNumber(stats.rank, locale) }) : '—'}
          icon={<TrophyIcon />}
          tone="neutral"
        />
      </div>

      <section className="glass rounded-xl p-[var(--gap)]" aria-label={t('profile.favoriteGames')}>
        <h3 className="mb-4 font-display text-xl font-normal tracking-tight">{t('profile.favoriteGames')}</h3>
        {stats.favoriteGames.length === 0 ? (
          <p className="text-muted">{t('common.empty')}</p>
        ) : (
          <FavoriteGames games={stats.favoriteGames} />
        )}
      </section>
    </div>
  );
}

export default Stats;
