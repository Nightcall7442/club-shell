/**
 * Achievements tab: grid of unlocked / locked achievements with progress bars. Cards are focusable so a
 * gamepad user can browse them; nothing on them is actionable.
 */
import { useEffect, useState } from 'react';
import type { Achievement } from '@clubshell/contracts';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { Badge } from '@/components/ui/Badge';
import { ProgressBar } from '@/components/ui/ProgressBar';
import { Skeleton } from '@/components/ui/Skeleton';
import { useLocale } from '@/hooks/useLocale';
import { formatDate, formatNumber } from '@/lib/format';

function TrophyIcon(): JSX.Element {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M8 4h8v5a4 4 0 0 1-8 0zM8 6H5a3 3 0 0 0 3 5M16 6h3a3 3 0 0 1-3 5M12 13v4M8 20h8" />
    </svg>
  );
}

function LockIcon(): JSX.Element {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <rect x="5" y="11" width="14" height="10" rx="2" />
      <path d="M8 11V7a4 4 0 0 1 8 0v4" />
    </svg>
  );
}

export interface AchievementCardProps {
  item: Achievement;
}

export function AchievementCard({ item }: AchievementCardProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const [iconFailed, setIconFailed] = useState(false);
  useEffect(() => setIconFailed(false), [item.iconUrl]);

  const unlocked = Boolean(item.unlockedAt);
  const { current, target } = item.progress;
  const progressText = t('profile.progress', { current: formatNumber(current, locale), target: formatNumber(target, locale) });

  return (
    <article
      data-nav="true"
      tabIndex={0}
      aria-label={item.title}
      className={clsx(
        'focus-ring glass flex h-full flex-col gap-3 rounded-xl p-4 transition-[opacity,transform] duration-[var(--dur-fast)]',
        unlocked ? 'border-primary/30' : 'opacity-75',
      )}
    >
      <div className="flex items-start gap-4">
        <span
          aria-hidden="true"
          className={clsx(
            'relative inline-flex h-16 w-16 shrink-0 items-center justify-center overflow-hidden rounded-lg bg-surface/60',
            unlocked ? 'border-glow text-accent' : 'text-muted grayscale',
          )}
        >
          {item.iconUrl && !iconFailed ? (
            <img
              src={item.iconUrl}
              alt=""
              loading="lazy"
              decoding="async"
              draggable={false}
              onError={() => setIconFailed(true)}
              className={clsx('h-full w-full object-cover', !unlocked && 'opacity-60')}
            />
          ) : (
            <span className="[&>svg]:h-8 [&>svg]:w-8">
              <TrophyIcon />
            </span>
          )}
          {!unlocked && (
            <span className="absolute inset-0 flex items-center justify-center bg-bg/50 text-text [&>svg]:h-6 [&>svg]:w-6">
              <LockIcon />
            </span>
          )}
        </span>
        <div className="min-w-0 flex-1">
          <h4 className="truncate text-lg font-bold">{item.title}</h4>
          <p className="line-clamp-2 text-sm text-muted">{item.description}</p>
        </div>
      </div>
      <div className="mt-auto">
        {unlocked && item.unlockedAt ? (
          <Badge tone="success" size="md" dot>
            {t('profile.unlocked', { date: formatDate(item.unlockedAt, locale) })}
          </Badge>
        ) : (
          <div className="flex flex-col gap-1.5">
            <div className="flex items-center justify-between text-sm text-muted">
              <span>{t('profile.locked')}</span>
              <span className="tnum">{progressText}</span>
            </div>
            <ProgressBar value={current} max={target} size="sm" tone="accent" label={item.title} />
          </div>
        )}
      </div>
    </article>
  );
}

export interface AchievementsProps {
  items: Achievement[] | null;
  loading: boolean;
}

export function Achievements({ items, loading }: AchievementsProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();

  if (loading) {
    return (
      <div className="grid grid-cols-2 gap-[var(--gap)] xl:grid-cols-3 2xl:grid-cols-4">
        {Array.from({ length: 6 }, (_, i) => (
          <Skeleton key={i} variant="rect" height="10rem" className="rounded-xl" />
        ))}
      </div>
    );
  }

  if (!items || items.length === 0) {
    return (
      <div className="glass flex min-h-[16rem] flex-col items-center justify-center gap-2 rounded-xl p-[var(--gap)] text-center">
        <span aria-hidden="true" className="inline-flex h-16 w-16 items-center justify-center rounded-full bg-primary/15 text-primary [&>svg]:h-8 [&>svg]:w-8">
          <TrophyIcon />
        </span>
        <p className="text-lg text-muted">{items ? t('profile.noAchievements') : t('common.unavailable')}</p>
      </div>
    );
  }

  const unlockedCount = items.filter((a) => a.unlockedAt).length;
  const sorted = [...items].sort((a, b) => {
    const au = a.unlockedAt ? 1 : 0;
    const bu = b.unlockedAt ? 1 : 0;
    if (au !== bu) {
      return bu - au;
    }
    if (au) {
      return Date.parse(b.unlockedAt ?? '') - Date.parse(a.unlockedAt ?? '');
    }
    return b.progress.current / Math.max(1, b.progress.target) - a.progress.current / Math.max(1, a.progress.target);
  });

  return (
    <div className="flex flex-col gap-[var(--gap)]">
      <div className="flex flex-wrap items-center gap-4">
        <p className="text-lg text-muted">
          {t('profile.achievementsUnlocked', { unlocked: formatNumber(unlockedCount, locale), total: formatNumber(items.length, locale) })}
        </p>
        <ProgressBar value={unlockedCount} max={items.length} size="sm" label={t('profile.achievements')} className="max-w-xs" />
      </div>
      <ul role="list" className="grid grid-cols-2 gap-[var(--gap)] xl:grid-cols-3 2xl:grid-cols-4">
        {sorted.map((item) => (
          <li key={item.id}>
            <AchievementCard item={item} />
          </li>
        ))}
      </ul>
    </div>
  );
}

export default Achievements;
