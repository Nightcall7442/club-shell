/**
 * Loyalty tab: level ring, points, progress to the next level and the localized perk list.
 */
import type { Loyalty as LoyaltyInfo } from '@clubshell/contracts';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { ProgressBar } from '@/components/ui/ProgressBar';
import { Skeleton } from '@/components/ui/Skeleton';
import { useLocale } from '@/hooks/useLocale';
import { formatNumber, formatPercent } from '@/lib/format';

/** Loyalty levels are 0-based on the wire; people count from 1. */
export function displayLevel(level: number): number {
  return level + 1;
}

/** Progress towards the next level in `[0, 1]`. */
export function levelProgress(points: number, nextLevelAt: number): number {
  if (nextLevelAt <= 0) {
    return 1;
  }
  return Math.min(1, Math.max(0, points / nextLevelAt));
}

export interface LevelRingProps {
  /** Level number shown in the centre (already 1-based). */
  level: number;
  /** `[0, 1]`. */
  progress: number;
  /** Accessible description of the ring. */
  label: string;
  className?: string;
}

const RING_R = 52;
const RING_C = 2 * Math.PI * RING_R;

/** SVG progress ring with the level number inside. */
export function LevelRing({ level, progress, label, className }: LevelRingProps): JSX.Element {
  const { t } = useTranslation();
  const offset = RING_C * (1 - Math.min(1, Math.max(0, progress)));
  return (
    <div role="img" aria-label={label} className={clsx('relative aspect-square w-[clamp(11rem,14vw,16rem)]', className)}>
      <svg viewBox="0 0 120 120" className="h-full w-full -rotate-90" aria-hidden="true">
        <defs>
          <linearGradient id="loyalty-ring-gradient" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0%" stopColor="rgb(var(--c-primary))" />
            <stop offset="100%" stopColor="rgb(var(--c-accent))" />
          </linearGradient>
        </defs>
        <circle cx="60" cy="60" r={RING_R} fill="none" stroke="rgb(var(--c-text) / 0.1)" strokeWidth="10" />
        <circle
          cx="60"
          cy="60"
          r={RING_R}
          fill="none"
          stroke="url(#loyalty-ring-gradient)"
          strokeWidth="10"
          strokeLinecap="round"
          strokeDasharray={RING_C}
          strokeDashoffset={offset}
          className="transition-[stroke-dashoffset] duration-700 ease-out"
          style={{ filter: 'drop-shadow(0 0 8px rgb(var(--c-primary) / 0.6))' }}
        />
      </svg>
      <div className="absolute inset-0 flex flex-col items-center justify-center" aria-hidden="true">
        <span className="tnum text-glow text-[3.25rem] font-black leading-none">{level}</span>
        <span className="mt-1 text-sm uppercase tracking-wide text-muted">{t('profile.loyaltyLevel')}</span>
      </div>
    </div>
  );
}

function CheckIcon(): JSX.Element {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M5 12.5l4.5 4.5L19 7.5" />
    </svg>
  );
}

export interface LoyaltyProps {
  loyalty: LoyaltyInfo | null;
  loading: boolean;
}

export function Loyalty({ loyalty, loading }: LoyaltyProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();

  if (loading) {
    return (
      <div className="grid gap-[var(--gap)] xl:grid-cols-[minmax(0,2fr)_minmax(0,3fr)]">
        <div className="glass flex flex-col items-center gap-4 rounded-xl p-[var(--gap)]">
          <Skeleton variant="circle" className="w-[clamp(11rem,14vw,16rem)]" style={{ aspectRatio: '1' }} />
          <Skeleton variant="text" width="40%" />
          <Skeleton variant="text" width="60%" />
        </div>
        <div className="glass rounded-xl p-[var(--gap)]">
          <Skeleton variant="text" width="30%" className="mb-4" />
          <Skeleton variant="text" lines={3} />
        </div>
      </div>
    );
  }

  if (!loyalty) {
    return (
      <div className="glass flex min-h-[16rem] items-center justify-center rounded-xl p-[var(--gap)] text-lg text-muted">
        {t('common.unavailable')}
      </div>
    );
  }

  const progress = levelProgress(loyalty.points, loyalty.nextLevelAt);
  const toNext = Math.max(0, loyalty.nextLevelAt - loyalty.points);
  const percent = formatPercent(progress * 100, locale);

  return (
    <div className="grid gap-[var(--gap)] xl:grid-cols-[minmax(0,2fr)_minmax(0,3fr)]">
      <section className="glass flex flex-col items-center gap-3 rounded-xl p-[var(--gap)] text-center" aria-label={t('profile.loyalty')}>
        <LevelRing level={displayLevel(loyalty.level)} progress={progress} label={`${t('profile.levelProgress')}: ${percent}`} />
        <h3 className="text-2xl font-bold">{t('profile.level', { level: displayLevel(loyalty.level) })}</h3>
        <p className="tnum text-4xl font-black text-accent">{formatNumber(loyalty.points, locale)}</p>
        <p className="-mt-2 text-muted">{t('profile.points')}</p>
        <ProgressBar
          value={loyalty.points}
          max={loyalty.nextLevelAt}
          tone="accent"
          size="lg"
          label={t('profile.levelProgress')}
          valueText={percent}
          className="mt-2"
        />
        <p className="text-base text-muted">{t('profile.nextLevel', { points: formatNumber(loyalty.nextLevelAt, locale) })}</p>
        {toNext > 0 && <p className="text-sm text-muted">{t('profile.pointsToNext', { points: formatNumber(toNext, locale) })}</p>}
      </section>

      <section className="glass rounded-xl p-[var(--gap)]" aria-label={t('profile.perks')}>
        <h3 className="text-xl font-bold">{t('profile.perks')}</h3>
        {loyalty.perks.length === 0 ? (
          <p className="mt-4 text-muted">{t('profile.noPerks')}</p>
        ) : (
          <ul role="list" className="mt-4 flex flex-col gap-3">
            {loyalty.perks.map((perk, i) => (
              <li key={`${i}-${perk}`} className="flex items-start gap-3 rounded-lg bg-surface/40 p-4 text-lg">
                <span aria-hidden="true" className="mt-0.5 inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-success/20 text-success [&>svg]:h-4 [&>svg]:w-4">
                  <CheckIcon />
                </span>
                <span>{perk}</span>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

export default Loyalty;
