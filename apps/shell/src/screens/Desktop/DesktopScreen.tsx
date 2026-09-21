/**
 * Home (`/home`): greeting, running-game banner, "continue playing" cover row, quick actions, promo strip
 * (ads playlist or tariffs), tournaments teaser and recent staff chat. Sections fade in with a small stagger.
 */
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import clsx from 'clsx';
import { AnimatePresence, motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import type { Game, Tournament } from '@clubshell/contracts';
import { GameArtwork, useResolvedAsset } from '@/components/media/GameArtwork';
import { Avatar } from '@/components/ui/Avatar';
import { Badge } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { Skeleton } from '@/components/ui/Skeleton';
import { Spinner } from '@/components/ui/Spinner';
import { collectNavigables, focusElement } from '@/hooks/useGamepad';
import { useLocale } from '@/hooks/useLocale';
import { useSession } from '@/hooks/useSession';
import { formatDateTime, formatDurationSec, formatMoney, formatTime } from '@/lib/format';
import { log } from '@/lib/logger';
import { api } from '@/lib/tauri';
import { secondsSince } from '@/lib/time';
import { QuickActions } from '@/screens/Desktop/QuickActions';
import { isPendingMessage, selectActiveRoom, useChatStore } from '@/store/chat';
import { selectFeaturedGames, selectRecentGames, selectRunningGame, useGamesStore } from '@/store/games';
import { useNotificationsStore } from '@/store/notifications';
import { selectFeatures, useSettingsStore } from '@/store/settings';
import { selectAnimationsEnabled, useThemeStore } from '@/store/theme';
import { useWalletStore } from '@/store/wallet';

// ---------------------------------------------------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------------------------------------------------

/** Greeting key by local hour. */
export function greetingKey(hour: number): 'desktop.greetingMorning' | 'desktop.greetingDay' | 'desktop.greetingEvening' | 'desktop.greetingNight' {
  if (hour < 5) return 'desktop.greetingNight';
  if (hour < 12) return 'desktop.greetingMorning';
  if (hour < 18) return 'desktop.greetingDay';
  if (hour < 23) return 'desktop.greetingEvening';
  return 'desktop.greetingNight';
}

interface SectionProps {
  title: string;
  action?: { label: string; onClick: () => void };
  children: ReactNode;
  className?: string;
  index?: number;
}

function Section({ title, action, children, className, index = 0 }: SectionProps): JSX.Element {
  const animations = useThemeStore(selectAnimationsEnabled);
  return (
    <motion.section
      aria-label={title}
      className={className}
      initial={animations ? { opacity: 0, y: 12 } : false}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: animations ? 0.25 : 0, delay: animations ? index * 0.05 : 0, ease: 'easeOut' }}
    >
      <div className="mb-3 flex items-center justify-between gap-4">
        <h2 className="text-xl font-bold text-text">{title}</h2>
        {action && (
          <Button variant="ghost" size="md" onClick={action.onClick}>
            {action.label}
          </Button>
        )}
      </div>
      {children}
    </motion.section>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Running game banner
// ---------------------------------------------------------------------------------------------------------------------

export function RunningGameBanner(): JSX.Element | null {
  const { t } = useTranslation();
  const running = useGamesStore(selectRunningGame);
  const launching = useGamesStore((s) => s.launching);
  const kill = useGamesStore((s) => s.kill);
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [confirm, setConfirm] = useState(false);
  const [killing, setKilling] = useState(false);
  const [, setTick] = useState(0);

  useEffect(() => {
    if (!running) {
      return undefined;
    }
    const id = setInterval(() => setTick((n) => n + 1), 1000);
    return () => clearInterval(id);
  }, [running]);

  if (!running && !launching) {
    return null;
  }

  const onKill = async (): Promise<void> => {
    if (!running) {
      return;
    }
    setKilling(true);
    try {
      await kill(running.gameId);
      push({ title: t('games.killed'), level: 'info', ttlSec: 4, source: 'local' });
      setConfirm(false);
    } catch (e) {
      pushError(e, t('games.killTitle'));
    } finally {
      setKilling(false);
    }
  };

  return (
    <div role="status" className="glass flex items-center gap-4 rounded-xl px-5 py-3">
      {running ? <Badge tone="success" live>{t('games.running')}</Badge> : <Spinner size="sm" />}
      <div className="min-w-0 flex-1">
        <div className="truncate text-lg font-bold text-text">{running ? t('desktop.gameRunning', { title: running.title }) : t('games.launchTitle', { title: launching?.title ?? '' })}</div>
        <div className="truncate text-sm text-muted">
          {running ? t('desktop.runningSince', { duration: formatDurationSec(secondsSince(running.startedAt), { compact: true }) }) : t('games.launching')}
        </div>
      </div>
      {running && (
        <Button variant="secondary" onClick={() => setConfirm(true)}>
          {t('games.kill')}
        </Button>
      )}
      <Modal
        open={confirm}
        onClose={() => setConfirm(false)}
        title={t('games.killTitle')}
        description={t('games.killConfirm', { title: running?.title ?? '' })}
        size="sm"
        danger
        footer={
          <>
            <Button variant="ghost" size="lg" onClick={() => setConfirm(false)} disabled={killing}>
              {t('common.cancel')}
            </Button>
            <Button variant="danger" size="lg" loading={killing} onClick={() => void onKill()}>
              {t('games.kill')}
            </Button>
          </>
        }
      />
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Continue playing (cover row)
// ---------------------------------------------------------------------------------------------------------------------

export function GameCoverCard({ game, priority = false }: { game: Game; priority?: boolean }): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { locale } = useLocale();
  return (
    <button
      type="button"
      data-nav="true"
      aria-label={game.title}
      onClick={() => navigate(`/games/${game.id}`)}
      className="focus-ring group flex w-[var(--card-cover-w)] shrink-0 flex-col gap-2 rounded-lg text-left"
    >
      <GameArtwork
        src={game.coverUrl}
        title={game.title}
        priority={priority}
        className="shadow-[var(--shadow-card)] transition-transform duration-[var(--dur-base)] group-hover:scale-[1.03] group-focus-visible:scale-[1.03]"
        overlay={
          <div className="absolute inset-x-0 bottom-0 flex items-end justify-between gap-2 bg-gradient-to-t from-bg/90 to-transparent p-3">
            {game.installed ? (
              <Badge tone="primary" size="sm" solid>
                {t('games.launch')}
              </Badge>
            ) : (
              <Badge tone="muted" size="sm">
                {t('games.notInstalled')}
              </Badge>
            )}
          </div>
        }
      />
      <span className="truncate text-base font-semibold text-text">{game.title}</span>
      <span className="truncate text-sm text-muted">{game.lastPlayedAt ? formatDateTime(game.lastPlayedAt, locale) : t('games.neverPlayed')}</span>
    </button>
  );
}

export function ContinuePlaying({ index }: { index: number }): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const status = useGamesStore((s) => s.status);
  const recent = useGamesStore(selectRecentGames);
  const featured = useGamesStore(selectFeaturedGames);
  const games = recent.length > 0 ? recent : featured;
  const title = recent.length > 0 ? t('desktop.continuePlaying') : t('desktop.popular');

  return (
    <Section title={title} index={index} action={{ label: t('desktop.seeAll'), onClick: () => navigate('/games') }}>
      <div role="list" aria-label={title} className="no-scrollbar -mx-1 flex gap-[var(--gap)] overflow-x-auto px-1 pb-2">
        {status === 'loading' && games.length === 0
          ? Array.from({ length: 6 }, (_, i) => (
              <div key={i} role="listitem" className="w-[var(--card-cover-w)] shrink-0">
                <Skeleton variant="cover" />
                <Skeleton variant="text" className="mt-2" width="80%" />
              </div>
            ))
          : games.map((g, i) => (
              <div key={g.id} role="listitem">
                <GameCoverCard game={g} priority={i < 4} />
              </div>
            ))}
        {status !== 'loading' && games.length === 0 && (
          <div className="glass flex w-full flex-col items-start gap-3 rounded-xl p-6">
            <p className="text-base text-muted">{t('games.empty')}</p>
            <Button variant="secondary" onClick={() => navigate('/games')}>
              {t('desktop.allGames')}
            </Button>
          </div>
        )}
      </div>
    </Section>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Promo strip
// ---------------------------------------------------------------------------------------------------------------------

function PromoImage({ url }: { url: string }): JSX.Element {
  const { t } = useTranslation();
  const { url: resolved } = useResolvedAsset(url);
  return resolved ? (
    <img src={resolved} alt={t('idle.ads')} draggable={false} className="absolute inset-0 h-full w-full object-cover" />
  ) : (
    <Skeleton variant="rect" className="absolute inset-0 h-full w-full rounded-none" />
  );
}

export function PromoStrip({ index }: { index: number }): JSX.Element | null {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const navigate = useNavigate();
  const ads = useSettingsStore((s) => s.shellConfig?.ads ?? null);
  const tariffs = useWalletStore((s) => s.tariffs);
  const animations = useThemeStore(selectAnimationsEnabled);
  const items = useMemo(() => (ads?.enabled ? ads.playlist.filter((p) => p.type === 'image') : []), [ads]);
  const [idx, setIdx] = useState(0);

  useEffect(() => {
    if (items.length < 2) {
      return undefined;
    }
    const current = items[idx % items.length];
    const id = setTimeout(() => setIdx((i) => (i + 1) % items.length), Math.max(3, current?.durationSec ?? 8) * 1000);
    return () => clearTimeout(id);
  }, [idx, items]);

  if (items.length === 0 && tariffs.length === 0) {
    return null;
  }

  const current = items[idx % Math.max(1, items.length)];

  return (
    <Section title={t('desktop.promo')} index={index}>
      {current ? (
        <div className="glass relative aspect-[6/1] w-full overflow-hidden rounded-xl">
          <AnimatePresence initial={false}>
            <motion.div
              key={current.url}
              className="absolute inset-0"
              initial={animations ? { opacity: 0 } : false}
              animate={{ opacity: 1 }}
              exit={{ opacity: 0 }}
              transition={{ duration: animations ? 0.5 : 0 }}
            >
              <PromoImage url={current.url} />
            </motion.div>
          </AnimatePresence>
          {items.length > 1 && (
            <span className="absolute bottom-3 right-4 rounded-full bg-bg/70 px-3 py-1 text-xs text-muted">{t('idle.adOf', { index: (idx % items.length) + 1, total: items.length })}</span>
          )}
        </div>
      ) : (
        <div role="list" className="grid grid-cols-[repeat(auto-fit,minmax(14rem,1fr))] gap-[var(--gap)]">
          {tariffs.map((tf) => (
            <button
              key={tf.id}
              type="button"
              role="listitem"
              data-nav="true"
              onClick={() => navigate('/wallet')}
              className="focus-ring glass flex flex-col gap-1 rounded-xl p-5 text-left transition-colors duration-[var(--dur-fast)] hover:bg-surface/80"
            >
              <span className="flex items-center justify-between gap-2">
                <span className="truncate text-lg font-bold text-text">{tf.name}</span>
                <Badge tone={tf.isPackage ? 'accent' : 'primary'} size="sm">
                  {tf.isPackage ? t('wallet.package') : t('wallet.hourlyRate')}
                </Badge>
              </span>
              <span className="tnum text-base text-muted">
                {tf.isPackage && tf.packagePrice && tf.packageMinutes
                  ? t('wallet.packageMinutes', { minutes: tf.packageMinutes, price: formatMoney(tf.packagePrice, locale) })
                  : t('wallet.perHour', { price: formatMoney(tf.pricePerHour, locale) })}
              </span>
            </button>
          ))}
        </div>
      )}
    </Section>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Tournaments teaser
// ---------------------------------------------------------------------------------------------------------------------

export function TournamentsTeaser({ index }: { index: number }): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const navigate = useNavigate();
  const byId = useGamesStore((s) => s.byId);
  const [items, setItems] = useState<Tournament[] | null>(null);

  useEffect(() => {
    let active = true;
    api.tournaments.list().then(
      (list) => {
        if (active) {
          const order: Record<Tournament['state'], number> = { live: 0, registration: 1, upcoming: 2, finished: 3 };
          setItems([...list].sort((a, b) => order[a.state] - order[b.state] || Date.parse(a.startsAt) - Date.parse(b.startsAt)).slice(0, 3));
        }
      },
      (e: unknown) => {
        log.warn('tournaments teaser failed', e);
        if (active) {
          setItems([]);
        }
      },
    );
    return () => {
      active = false;
    };
  }, []);

  return (
    <Section title={t('desktop.nav.tournaments')} index={index} action={{ label: t('desktop.seeAll'), onClick: () => navigate('/tournaments') }}>
      <div role="list" className="flex flex-col gap-2">
        {items === null && Array.from({ length: 3 }, (_, i) => <Skeleton key={i} variant="rect" height="4.5rem" />)}
        {items !== null && items.length === 0 && <p className="glass rounded-xl p-5 text-base text-muted">{t('desktop.noTournaments')}</p>}
        {items?.map((tr) => (
          <button
            key={tr.id}
            type="button"
            role="listitem"
            data-nav="true"
            onClick={() => navigate('/tournaments')}
            className="focus-ring glass flex items-center gap-4 rounded-xl px-5 py-3 text-left transition-colors duration-[var(--dur-fast)] hover:bg-surface/80"
          >
            <div className="min-w-0 flex-1">
              <div className="flex items-center gap-2">
                <span className="truncate text-base font-bold text-text">{tr.title}</span>
                {tr.state === 'live' ? (
                  <Badge tone="danger" size="sm" live>
                    {t('tournaments.liveBadge')}
                  </Badge>
                ) : (
                  <Badge tone={tr.state === 'registration' ? 'success' : 'muted'} size="sm">
                    {t(`tournaments.state.${tr.state}`)}
                  </Badge>
                )}
                {tr.joined && (
                  <Badge tone="primary" size="sm">
                    {t('tournaments.joined')}
                  </Badge>
                )}
              </div>
              <div className="truncate text-sm text-muted">
                {byId.get(tr.gameId)?.title ?? t('tournaments.game')} · {t('tournaments.playersOf', { players: tr.players, max: tr.maxPlayers })} · {t('tournaments.startsAt', { time: formatDateTime(tr.startsAt, locale) })}
              </div>
            </div>
            <div className="shrink-0 text-right">
              <div className="text-xs uppercase tracking-wide text-muted">{t('tournaments.prizePool')}</div>
              <div className="tnum text-base font-bold text-accent">{formatMoney(tr.prizePool, locale)}</div>
            </div>
          </button>
        ))}
      </div>
    </Section>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Recent chat
// ---------------------------------------------------------------------------------------------------------------------

export function RecentChat({ index }: { index: number }): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const navigate = useNavigate();
  const { user } = useSession();
  const room = useChatStore(selectActiveRoom);
  const status = useChatStore((s) => s.status);
  const messages = room.messages.slice(-3);

  return (
    <Section title={t('desktop.recentChat')} index={index} action={{ label: t('notifications.open'), onClick: () => navigate('/chat') }}>
      <button
        type="button"
        data-nav="true"
        aria-label={room.unread > 0 ? `${t('desktop.recentChat')}, ${t('chat.unread', { count: room.unread })}` : t('desktop.recentChat')}
        onClick={() => navigate('/chat')}
        className="focus-ring glass flex w-full flex-col gap-3 rounded-xl p-5 text-left transition-colors duration-[var(--dur-fast)] hover:bg-surface/80"
      >
        {status === 'loading' && messages.length === 0 && <Skeleton variant="text" lines={3} />}
        {status !== 'loading' && messages.length === 0 && <p className="text-base text-muted">{t('chat.empty')}</p>}
        {messages.map((m) => {
          const mine = isPendingMessage(m) || (user !== null && m.senderId === user.id);
          return (
            <div key={m.id} className="flex items-start gap-3">
              <Avatar name={mine ? t('chat.you') : m.senderName || t('chat.admin')} size="sm" />
              <div className="min-w-0 flex-1">
                <div className="flex items-baseline justify-between gap-2">
                  <span className={clsx('truncate text-sm font-semibold', mine ? 'text-muted' : 'text-primary')}>{mine ? t('chat.you') : m.senderName || t('chat.admin')}</span>
                  <span className="tnum shrink-0 text-xs text-muted">{formatTime(m.createdAt, locale)}</span>
                </div>
                <p className="line-clamp-2 text-base text-text">{m.text}</p>
              </div>
            </div>
          );
        })}
        {room.unread > 0 && (
          <Badge tone="danger" size="sm" solid className="self-start">
            {t('chat.unread', { count: room.unread })}
          </Badge>
        )}
      </button>
    </Section>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Screen
// ---------------------------------------------------------------------------------------------------------------------

export default function DesktopScreen(): JSX.Element {
  const { t } = useTranslation();
  const { user } = useSession();
  const features = useSettingsStore(selectFeatures);
  const root = useRef<HTMLDivElement>(null);

  // Initial focus for keyboard/gamepad users: the first navigable tile when nothing else holds focus.
  useEffect(() => {
    const frame = requestAnimationFrame(() => {
      const active = document.activeElement;
      if (root.current && (active === null || active === document.body || active.id === 'main')) {
        const first = collectNavigables(root.current)[0];
        if (first) {
          focusElement(first);
        }
      }
    });
    return () => cancelAnimationFrame(frame);
  }, []);

  const name = user?.displayName ?? '';
  const greeting = t(greetingKey(new Date().getHours()), { name });

  return (
    <div ref={root} className="mx-auto flex w-full max-w-[1800px] flex-col gap-[calc(var(--gap)*1.5)]">
      <header className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="text-3xl font-bold leading-tight text-text">{greeting}</h1>
          <p className="text-base text-muted">{t('desktop.welcomeBack')}</p>
        </div>
      </header>

      <RunningGameBanner />
      <ContinuePlaying index={1} />
      <QuickActions />
      <PromoStrip index={3} />
      <div className={clsx('grid gap-[calc(var(--gap)*1.5)]', features.tournaments && features.chat ? 'grid-cols-1 2xl:grid-cols-2' : 'grid-cols-1')}>
        {features.tournaments && <TournamentsTeaser index={4} />}
        {features.chat && <RecentChat index={5} />}
      </div>
    </div>
  );
}
