import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import type { LeaderboardEntry } from '@clubshell/contracts';
import { Avatar } from '@/components/ui/Avatar';
import { Badge } from '@/components/ui/Badge';
import { Skeleton } from '@/components/ui/Skeleton';
import { Spinner } from '@/components/ui/Spinner';
import { useLocale } from '@/hooks/useLocale';
import { formatNumber, formatTime } from '@/lib/format';

export interface LeaderboardProps {
  entries: LeaderboardEntry[];
  /** Current user's row when outside the top list. */
  me?: LeaderboardEntry | null;
  meId: string | null;
  updatedAt?: string | null;
  /** First load: skeleton rows. */
  loading?: boolean;
  /** Background refresh: small spinner in the header. */
  refreshing?: boolean;
  className?: string;
}

const MEDAL: Record<number, string> = {
  1: 'bg-[#f5c542] text-black shadow-[0_0_14px_rgba(245,197,66,0.6)]',
  2: 'bg-[#c9ced6] text-black',
  3: 'bg-[#cd7f32] text-black',
};

function Row({ e, mine }: { e: LeaderboardEntry; mine: boolean }): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  return (
    <tr
      data-nav="true"
      tabIndex={0}
      aria-current={mine ? 'true' : undefined}
      className={clsx(
        'focus-ring rounded-xl transition-colors duration-[var(--dur-fast)] [&>td]:py-2.5 [&>td:first-child]:rounded-l-xl [&>td:last-child]:rounded-r-xl',
        mine ? 'bg-primary/20 shadow-[inset_0_0_0_1px_rgb(var(--c-primary)/0.6)]' : 'hover:bg-text/5',
      )}
    >
      <td className="w-16 pl-3 text-center">
        <span
          className={clsx(
            'tnum inline-flex h-9 w-9 items-center justify-center rounded-full text-sm font-bold',
            MEDAL[e.rank] ?? 'bg-surface/70 text-muted',
          )}
        >
          {e.rank}
        </span>
      </td>
      <td>
        <span className="flex items-center gap-3">
          <Avatar name={e.name} src={e.avatarUrl} size="md" ring={mine} />
          <span className="min-w-0">
            <span className="block truncate font-semibold text-text">{e.name}</span>
            {mine && (
              <Badge tone="primary" size="sm" solid>
                {t('tournaments.you')}
              </Badge>
            )}
          </span>
        </span>
      </td>
      <td className="tnum pr-4 text-right text-lg font-bold text-text">{formatNumber(e.score, locale)}</td>
    </tr>
  );
}

/** Ranked table with medals for the top 3 and the current user's row highlighted (appended when outside the list). */
export function Leaderboard({
  entries,
  me = null,
  meId,
  updatedAt = null,
  loading = false,
  refreshing = false,
  className,
}: LeaderboardProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const isMe = (e: LeaderboardEntry): boolean => meId !== null && e.userId === meId;
  const extra = me && !entries.some((e) => e.userId === me.userId) ? me : null;

  return (
    <div className={clsx('flex flex-col gap-3', className)}>
      <div className="flex items-center justify-between px-1 text-sm text-muted">
        <span className="flex items-center gap-2">
          {t('tournaments.leaderboard')}
          {refreshing && <Spinner size="sm" label={t('tournaments.refreshing')} />}
        </span>
        {updatedAt && (
          <span className="tnum">{t('tournaments.updatedAt', { time: formatTime(updatedAt, locale) })}</span>
        )}
      </div>
      {loading ? (
        <div className="flex flex-col gap-2" aria-hidden="true">
          {[0, 1, 2, 3, 4, 5].map((i) => (
            <Skeleton key={i} variant="rect" height={56} className="rounded-xl" />
          ))}
        </div>
      ) : entries.length === 0 && !extra ? (
        <p className="py-8 text-center text-muted">{t('tournaments.noLeaderboard')}</p>
      ) : (
        <table className="w-full border-separate border-spacing-y-1 text-base">
          <thead>
            <tr className="text-left text-xs uppercase tracking-wide text-muted">
              <th scope="col" className="pl-3 text-center font-medium">
                {t('tournaments.rank')}
              </th>
              <th scope="col" className="font-medium">
                {t('tournaments.player')}
              </th>
              <th scope="col" className="pr-4 text-right font-medium">
                {t('tournaments.score')}
              </th>
            </tr>
          </thead>
          <tbody>
            {entries.map((e) => (
              <Row key={e.userId} e={e} mine={isMe(e)} />
            ))}
            {extra && (
              <>
                <tr aria-hidden="true">
                  <td colSpan={3} className="py-1 text-center text-muted">
                    ···
                  </td>
                </tr>
                <Row e={extra} mine />
              </>
            )}
          </tbody>
        </table>
      )}
    </div>
  );
}
