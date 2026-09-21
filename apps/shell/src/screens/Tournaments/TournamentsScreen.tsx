import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import clsx from 'clsx';
import { AnimatePresence, motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import type { Tournament, TournamentState, TournamentsLeaderboardResponse } from '@clubshell/contracts';
import { GameArtwork } from '@/components/media/GameArtwork';
import { Badge, type BadgeTone } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { ProgressBar } from '@/components/ui/ProgressBar';
import { Skeleton } from '@/components/ui/Skeleton';
import { Tabs } from '@/components/ui/Tabs';
import { useGamepad } from '@/hooks/useGamepad';
import { useLocale } from '@/hooks/useLocale';
import { formatDateTime, formatDurationSec, formatMoney } from '@/lib/format';
import { api } from '@/lib/tauri';
import { getServerTimeOffset } from '@/lib/time';
import { useAuthStore } from '@/store/auth';
import { useGamesStore } from '@/store/games';
import { useNotificationsStore } from '@/store/notifications';
import { useSettingsStore } from '@/store/settings';
import { useThemeStore } from '@/store/theme';
import { Bracket } from './Bracket';
import { Leaderboard } from './Leaderboard';

const LEADERBOARD_REFRESH_MS = 30_000;
const STATE_ORDER: Record<TournamentState, number> = { live: 0, registration: 1, upcoming: 2, finished: 3 };
const STATE_TONE: Record<TournamentState, BadgeTone> = {
  live: 'danger',
  registration: 'success',
  upcoming: 'primary',
  finished: 'muted',
};

type DetailTab = 'bracket' | 'leaderboard';

function TrophyIcon(): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <path d="M8 21h8M12 17v4M7 4h10v5a5 5 0 0 1-10 0zM7 6H4v2a3 3 0 0 0 3 3M17 6h3v2a3 3 0 0 1-3 3" />
    </svg>
  );
}

/** Localized "starts in / started / ended" line for a tournament; `nowMs` is the ticking local clock. */
export function tournamentTimeLabel(
  tr: Tournament,
  nowMs: number,
  locale: Parameters<typeof formatDateTime>[1],
  t: (key: string, vars?: Record<string, unknown>) => string,
): string {
  const at = formatDateTime(tr.startsAt, locale);
  if (tr.state === 'finished') {
    return t('tournaments.endedAt', { time: at });
  }
  if (tr.state === 'live') {
    return t('tournaments.startedAt', { time: at });
  }
  const sec = Math.round((Date.parse(tr.startsAt) - (nowMs + getServerTimeOffset())) / 1000);
  if (Number.isNaN(sec) || sec <= 0) {
    return t('tournaments.startsAt', { time: at });
  }
  return t('tournaments.startsIn', { time: formatDurationSec(sec, { compact: true, seconds: sec < 3600 }) });
}

interface CardProps {
  tr: Tournament;
  gameTitle: string;
  cover: string | null;
  selected: boolean;
  joining: boolean;
  nowMs: number;
  onSelect: () => void;
  onJoin: () => void;
}

export function TournamentCard({
  tr,
  gameTitle,
  cover,
  selected,
  joining,
  nowMs,
  onSelect,
  onJoin,
}: CardProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const full = tr.players >= tr.maxPlayers;
  const canJoin = tr.state === 'registration' && !tr.joined && !full;
  return (
    <article
      className={clsx(
        'glass relative flex gap-4 rounded-2xl p-3 transition-[box-shadow,transform] duration-[var(--dur-fast)]',
        selected && 'shadow-[var(--shadow-glow)] ring-2 ring-primary',
      )}
      aria-current={selected ? 'true' : undefined}
    >
      <button
        type="button"
        data-nav="true"
        onClick={onSelect}
        aria-label={`${tr.title} — ${t(`tournaments.state.${tr.state}`)}`}
        className="focus-ring absolute inset-0 rounded-2xl"
      />
      <div className="w-[clamp(5rem,6.5vw,7.5rem)] shrink-0 self-start">
        <GameArtwork src={cover} title={gameTitle} kind="cover" className="rounded-xl" />
      </div>
      <div className="relative z-10 flex min-w-0 flex-1 flex-col gap-2 pointer-events-none">
        <div className="flex items-start justify-between gap-2">
          <div className="min-w-0">
            <h3 className="truncate text-lg font-bold text-text">{tr.title}</h3>
            <p className="truncate text-sm text-muted">{gameTitle}</p>
          </div>
          <Badge tone={STATE_TONE[tr.state]} size="sm" solid={tr.state === 'live'} live={tr.state === 'live'}>
            {tr.state === 'live' ? t('tournaments.liveBadge') : t(`tournaments.state.${tr.state}`)}
          </Badge>
        </div>
        <p className="tnum text-sm text-muted">{tournamentTimeLabel(tr, nowMs, locale, t)}</p>
        <div className="flex items-center justify-between gap-3 text-sm">
          <span className="flex items-center gap-1.5 font-semibold text-accent">
            <span className="inline-flex h-4 w-4 [&>svg]:h-full [&>svg]:w-full" aria-hidden="true">
              <TrophyIcon />
            </span>
            {formatMoney(tr.prizePool, locale)}
          </span>
          <span className="tnum text-muted">
            {t('tournaments.playersOf', { players: tr.players, max: tr.maxPlayers })}
          </span>
        </div>
        <ProgressBar
          value={tr.players}
          max={tr.maxPlayers}
          size="sm"
          tone={full ? 'accent' : 'primary'}
          label={t('tournaments.players')}
        />
        <div className="pointer-events-auto flex items-center gap-2">
          {tr.joined ? (
            <Badge tone="success" size="md" dot>
              {t('tournaments.joined')}
            </Badge>
          ) : canJoin ? (
            <Button size="md" loading={joining} onClick={onJoin}>
              {t('tournaments.join')}
            </Button>
          ) : (
            <span className="text-sm text-muted">
              {full ? t('tournaments.full') : t('tournaments.registrationClosed')}
            </span>
          )}
        </div>
      </div>
    </article>
  );
}

/** Tournament list with join, plus a details panel (bracket / leaderboard, auto-refreshed while live). */
export default function TournamentsScreen(): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const navigate = useNavigate();
  const animations = useThemeStore((s) => s.theme.animations);
  const me = useAuthStore((s) => s.user);
  const games = useGamesStore((s) => s.byId);
  const defaultRoute = useSettingsStore((s) => s.shellConfig?.ui.defaultRoute ?? '/home');
  const pushError = useNotificationsStore((s) => s.pushError);
  const push = useNotificationsStore((s) => s.push);

  const [items, setItems] = useState<Tournament[]>([]);
  const [loading, setLoading] = useState(true);
  const [failed, setFailed] = useState(false);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [tab, setTab] = useState<DetailTab>('bracket');
  const [joiningId, setJoiningId] = useState<string | null>(null);
  const [board, setBoard] = useState<TournamentsLeaderboardResponse | null>(null);
  const [boardLoading, setBoardLoading] = useState(false);
  const [nowMs, setNowMs] = useState(() => Date.now());
  const firstCardRef = useRef<HTMLDivElement | null>(null);

  const load = useCallback(async (): Promise<void> => {
    setLoading(true);
    setFailed(false);
    try {
      const list = await api.tournaments.list();
      list.sort(
        (a, b) => STATE_ORDER[a.state] - STATE_ORDER[b.state] || Date.parse(a.startsAt) - Date.parse(b.startsAt),
      );
      setItems(list);
      setSelectedId((prev) => (prev && list.some((x) => x.id === prev) ? prev : (list[0]?.id ?? null)));
    } catch (e) {
      setFailed(true);
      pushError(e, t('tournaments.title'));
    } finally {
      setLoading(false);
    }
  }, [pushError, t]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    const id = window.setInterval(() => setNowMs(Date.now()), 1000);
    return () => window.clearInterval(id);
  }, []);

  const selected = useMemo(() => items.find((x) => x.id === selectedId) ?? null, [items, selectedId]);

  // Leaderboard: load on selection, refresh every 30 s while live.
  useEffect(() => {
    if (!selectedId) {
      setBoard(null);
      return undefined;
    }
    let cancelled = false;
    const fetchBoard = async (): Promise<void> => {
      setBoardLoading(true);
      try {
        const res = await api.tournaments.leaderboard(selectedId, 50);
        if (!cancelled) {
          setBoard(res);
        }
      } catch (e) {
        if (!cancelled) {
          pushError(e, t('tournaments.leaderboard'));
        }
      } finally {
        if (!cancelled) {
          setBoardLoading(false);
        }
      }
    };
    setBoard(null);
    void fetchBoard();
    const live = items.find((x) => x.id === selectedId)?.state === 'live';
    const timer = live ? window.setInterval(() => void fetchBoard(), LEADERBOARD_REFRESH_MS) : null;
    return () => {
      cancelled = true;
      if (timer !== null) {
        window.clearInterval(timer);
      }
    };
    // `items` only matters through the live flag; re-running on every list refresh would restart the timer.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedId, pushError, t]);

  useEffect(() => {
    if (
      selected &&
      !selected.bracket &&
      tab === 'bracket' &&
      selected.state !== 'upcoming' &&
      selected.state !== 'registration'
    ) {
      setTab('leaderboard');
    }
  }, [selected, tab]);

  useEffect(() => {
    if (!loading) {
      firstCardRef.current?.querySelector<HTMLElement>('[data-nav="true"]')?.focus();
    }
  }, [loading]);

  const join = async (tr: Tournament): Promise<void> => {
    setJoiningId(tr.id);
    try {
      const updated = await api.tournaments.join(tr.id);
      setItems((list) => list.map((x) => (x.id === updated.id ? updated : x)));
      push({
        title: t('tournaments.joined'),
        body: t('tournaments.youJoined', { title: updated.title }),
        level: 'success',
      });
    } catch (e) {
      pushError(e, t('tournaments.join'));
    } finally {
      setJoiningId(null);
    }
  };

  const nameOf = useCallback(
    (userId: string): string => {
      if (me && userId === me.id) {
        return me.displayName;
      }
      const row = board?.entries.find((e) => e.userId === userId) ?? (board?.me?.userId === userId ? board.me : null);
      return row?.name ?? `${t('tournaments.player')} ${userId.slice(0, 4).toUpperCase()}`;
    },
    [me, board, t],
  );

  useGamepad({
    onBack: () => navigate(defaultRoute),
    onTab: (dir) =>
      setTab((cur) =>
        dir === 'next'
          ? cur === 'bracket'
            ? 'leaderboard'
            : 'bracket'
          : cur === 'bracket'
            ? 'leaderboard'
            : 'bracket',
      ),
  });

  const gameOf = (gameId: string): { title: string; cover: string | null; hero: string | null } => {
    const g = games.get(gameId);
    return {
      title: g?.title ?? t('tournaments.game'),
      cover: g?.coverUrl ?? null,
      hero: g?.heroUrl ?? g?.coverUrl ?? null,
    };
  };

  const tabs = [
    { key: 'bracket' as const, label: t('tournaments.bracket') },
    { key: 'leaderboard' as const, label: t('tournaments.leaderboard') },
  ];

  return (
    <motion.div
      initial={{ opacity: 0, y: 12 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: animations ? 0.2 : 0, ease: 'easeOut' }}
      className="grid h-full min-h-0 grid-cols-[clamp(24rem,32vw,36rem)_1fr] gap-[var(--gap)]"
    >
      <section className="flex min-h-0 flex-col gap-4">
        <header>
          <h1 className="text-3xl font-bold text-text">{t('tournaments.title')}</h1>
          <p className="text-muted">{t('tournaments.subtitle')}</p>
        </header>
        <div ref={firstCardRef} className="no-scrollbar flex min-h-0 flex-1 flex-col gap-3 overflow-y-auto pb-2 pr-1">
          {loading ? (
            [0, 1, 2].map((i) => (
              <div key={i} className="glass flex gap-4 rounded-2xl p-3" aria-hidden="true">
                <Skeleton variant="cover" width="6.5rem" className="rounded-xl" />
                <div className="flex flex-1 flex-col gap-2 py-1">
                  <Skeleton variant="text" lines={2} />
                  <Skeleton variant="rect" height={8} />
                  <Skeleton variant="rect" width={96} height={40} className="rounded-md" />
                </div>
              </div>
            ))
          ) : failed ? (
            <div className="glass flex flex-col items-center gap-4 rounded-2xl p-8 text-center">
              <p className="text-lg text-muted">{t('errors.generic')}</p>
              <Button variant="secondary" onClick={() => void load()}>
                {t('common.retry')}
              </Button>
            </div>
          ) : items.length === 0 ? (
            <div className="glass flex flex-col items-center gap-3 rounded-2xl p-10 text-center text-muted">
              <span className="inline-flex h-12 w-12 text-primary [&>svg]:h-full [&>svg]:w-full" aria-hidden="true">
                <TrophyIcon />
              </span>
              <p className="text-lg">{t('tournaments.noTournaments')}</p>
            </div>
          ) : (
            items.map((tr) => {
              const g = gameOf(tr.gameId);
              return (
                <TournamentCard
                  key={tr.id}
                  tr={tr}
                  gameTitle={g.title}
                  cover={g.cover}
                  selected={tr.id === selectedId}
                  joining={joiningId === tr.id}
                  nowMs={nowMs}
                  onSelect={() => setSelectedId(tr.id)}
                  onJoin={() => void join(tr)}
                />
              );
            })
          )}
        </div>
      </section>

      <section className="glass flex min-h-0 flex-col overflow-hidden rounded-2xl">
        <AnimatePresence mode="wait" initial={false}>
          {selected ? (
            <motion.div
              key={selected.id}
              initial={{ opacity: 0, x: 16 }}
              animate={{ opacity: 1, x: 0 }}
              exit={{ opacity: 0, x: -10 }}
              transition={{ duration: animations ? 0.2 : 0, ease: 'easeOut' }}
              className="flex min-h-0 flex-1 flex-col"
            >
              <GameArtwork
                src={gameOf(selected.gameId).hero}
                title={gameOf(selected.gameId).title}
                kind="hero"
                aspect="4:1"
                priority
                className="w-full shrink-0 rounded-none"
                overlay={
                  <div className="absolute inset-0 flex flex-col justify-end gap-2 bg-gradient-to-t from-bg/95 via-bg/40 to-transparent p-6">
                    <div className="flex flex-wrap items-center gap-3">
                      <Badge
                        tone={STATE_TONE[selected.state]}
                        size="md"
                        solid={selected.state === 'live'}
                        live={selected.state === 'live'}
                      >
                        {selected.state === 'live'
                          ? t('tournaments.liveBadge')
                          : t(`tournaments.state.${selected.state}`)}
                      </Badge>
                      <span className="tnum text-sm text-muted">{tournamentTimeLabel(selected, nowMs, locale, t)}</span>
                    </div>
                    <h2 className="text-glow text-3xl font-bold text-text">{selected.title}</h2>
                    <div className="flex flex-wrap items-center gap-x-6 gap-y-1 text-base">
                      <span className="text-muted">
                        {t('tournaments.game')}: <span className="text-text">{gameOf(selected.gameId).title}</span>
                      </span>
                      <span className="text-muted">
                        {t('tournaments.prizePool')}:{' '}
                        <span className="font-semibold text-accent">{formatMoney(selected.prizePool, locale)}</span>
                      </span>
                      <span className="tnum text-muted">
                        {t('tournaments.playersOf', { players: selected.players, max: selected.maxPlayers })}
                      </span>
                    </div>
                  </div>
                }
              />
              <div className="flex items-center justify-between gap-4 px-6 pt-4">
                <Tabs
                  items={tabs}
                  value={tab}
                  onChange={setTab}
                  label={t('common.details')}
                  idPrefix="tournament"
                  variant="underline"
                />
                {selected.joined && (
                  <Badge tone="success" size="md" dot>
                    {t('tournaments.joined')}
                  </Badge>
                )}
              </div>
              <div
                id={`tournament-panel-${tab}`}
                role="tabpanel"
                aria-labelledby={`tournament-tab-${tab}`}
                className="no-scrollbar min-h-0 flex-1 overflow-y-auto p-6"
              >
                {tab === 'bracket' ? (
                  selected.bracket ? (
                    <Bracket bracket={selected.bracket} meId={me?.id ?? null} nameOf={nameOf} className="max-h-full" />
                  ) : (
                    <p className="py-8 text-center text-muted">{t('tournaments.noBracket')}</p>
                  )
                ) : (
                  <Leaderboard
                    entries={board?.entries ?? []}
                    me={board?.me ?? null}
                    meId={me?.id ?? null}
                    updatedAt={board?.updatedAt ?? null}
                    loading={boardLoading && board === null}
                    refreshing={boardLoading && board !== null}
                  />
                )}
              </div>
            </motion.div>
          ) : (
            <motion.div
              key="empty"
              initial={{ opacity: 0 }}
              animate={{ opacity: 1 }}
              exit={{ opacity: 0 }}
              className="flex flex-1 items-center justify-center p-10 text-center text-muted"
            >
              {loading ? (
                <Skeleton variant="hero" className="w-full rounded-xl" />
              ) : (
                <p className="text-lg">{t('tournaments.selectHint')}</p>
              )}
            </motion.div>
          )}
        </AnimatePresence>
      </section>
    </motion.div>
  );
}
