/**
 * Home hero: a full-width stage with the selected game's art under the HUD bar — title and Play / Close / Details
 * bottom left, a rail of recent covers bottom right that switches it (time left and balance are in
 * the status line, on every screen).
 * Selection is the games-store `selectedId`, so the pick carries over to `/games`; the running game is always shown.
 */
import type { Game } from '@clubshell/contracts';
import { useEffect, useMemo, useRef, useState } from 'react';
import clsx from 'clsx';
import { AnimatePresence, motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { GameArtwork } from '@/components/media/GameArtwork';
import { Badge } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { Skeleton } from '@/components/ui/Skeleton';
import { categoryLabel } from '@/screens/Games/Categories';
import { launcherLabelKey } from '@/screens/Games/GameCard';
import { launchGame } from '@/screens/Games/LaunchOverlay';
import { selectFeaturedGames, selectRecentGames, useGamesStore } from '@/store/games';
import { useNotificationsStore } from '@/store/notifications';
import { selectAnimationsEnabled, useThemeStore } from '@/store/theme';

const STRIP_MAX = 6;

// ---------------------------------------------------------------------------------------------------------------------
// Icons
// ---------------------------------------------------------------------------------------------------------------------

const svgProps = {
  viewBox: '0 0 24 24',
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 2,
  strokeLinecap: 'round',
  strokeLinejoin: 'round',
  'aria-hidden': true,
} as const;

const PlayIcon = (): JSX.Element => (
  <svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
    <path d="M8 5.5v13a1 1 0 001.5.86l11-6.5a1 1 0 000-1.72l-11-6.5A1 1 0 008 5.5z" />
  </svg>
);
const StopIcon = (): JSX.Element => (
  <svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
    <rect x="6" y="6" width="12" height="12" rx="2" />
  </svg>
);
const InfoIcon = (): JSX.Element => (
  <svg {...svgProps}>
    <circle cx="12" cy="12" r="9" />
    <path d="M12 11v5M12 8h.01" />
  </svg>
);
const ArrowIcon = (): JSX.Element => (
  <svg {...svgProps}>
    <path d="M5 12h14M13 6l6 6-6 6" />
  </svg>
);
const GridIcon = (): JSX.Element => (
  <svg {...svgProps}>
    <rect x="3" y="3" width="7" height="7" rx="1.5" />
    <rect x="14" y="3" width="7" height="7" rx="1.5" />
    <rect x="3" y="14" width="7" height="7" rx="1.5" />
    <rect x="14" y="14" width="7" height="7" rx="1.5" />
  </svg>
);

// ---------------------------------------------------------------------------------------------------------------------
// Recent covers
// ---------------------------------------------------------------------------------------------------------------------

function PosterTile({
  game,
  selected,
  running,
  onSelect,
}: {
  game: Game;
  selected: boolean;
  running: boolean;
  onSelect: (game: Game) => void;
}): JSX.Element {
  return (
    <button
      type="button"
      role="listitem"
      data-nav="true"
      aria-label={game.title}
      aria-pressed={selected}
      onClick={() => onSelect(game)}
      onFocus={() => onSelect(game)}
      className="focus-ring group flex w-[clamp(5.5rem,6.2vw,8rem)] min-w-0 flex-col gap-2 rounded-lg text-left focus-visible:shadow-none"
    >
      <span
        className={clsx(
          'hud-focus relative block rounded-lg transition-opacity duration-[var(--dur-base)] [--brk-inset:-5px] group-focus-visible:[--brk-inset:-5px]',
          selected ? 'opacity-100' : 'opacity-60 group-hover:opacity-100',
        )}
        aria-current={selected ? 'true' : undefined}
      >
        <GameArtwork src={game.coverUrl} title={game.title} kind="cover" priority className="rounded-lg" />
        {running && <span aria-hidden="true" className="absolute right-2 top-2 h-2 w-2 rounded-full bg-success" />}
      </span>
      <span className={clsx('hud-label truncate', selected && 'text-accent')}>{game.title}</span>
    </button>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Hero
// ---------------------------------------------------------------------------------------------------------------------

export function HomeHero(): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const animations = useThemeStore(selectAnimationsEnabled);
  const status = useGamesStore((s) => s.status);
  const byId = useGamesStore((s) => s.byId);
  const recent = useGamesStore(selectRecentGames);
  const featured = useGamesStore(selectFeaturedGames);
  const selectedId = useGamesStore((s) => s.selectedId);
  const select = useGamesStore((s) => s.select);
  const running = useGamesStore((s) => s.running);
  const launching = useGamesStore((s) => s.launching);
  const kill = useGamesStore((s) => s.kill);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [confirmKill, setConfirmKill] = useState(false);
  const primary = useRef<HTMLButtonElement>(null);
  const focusedOnce = useRef(false);

  const runningGame = running[0] ? (byId.get(running[0].gameId) ?? null) : null;
  const strip = useMemo(() => {
    const seen = new Set<string>();
    const out: Game[] = [];
    for (const g of [...(runningGame ? [runningGame] : []), ...recent, ...featured]) {
      if (!seen.has(g.id)) {
        seen.add(g.id);
        out.push(g);
      }
    }
    return out.slice(0, STRIP_MAX);
  }, [runningGame, recent, featured]);

  const hero = strip.find((g) => g.id === selectedId) ?? runningGame ?? strip[0] ?? null;
  const isRunning = hero !== null && running.some((r) => r.gameId === hero.id);
  const isLaunching = hero !== null && launching?.gameId === hero.id;
  const loading = status === 'loading' && strip.length === 0;
  const fade = { duration: animations ? 0.25 : 0 };

  // Initial focus for keyboard/gamepad users: Play, unless something else already holds focus.
  useEffect(() => {
    if (focusedOnce.current || loading || !primary.current) {
      return;
    }
    const active = document.activeElement;
    if (active === null || active === document.body || active.id === 'main') {
      focusedOnce.current = true;
      primary.current.focus({ preventScroll: true });
    }
  }, [loading, hero]);

  const play = (g: Game): void => {
    select(g.id);
    void launchGame(g.id);
  };
  const closeGame = async (g: Game): Promise<void> => {
    setConfirmKill(false);
    try {
      await kill(g.id);
    } catch (e) {
      pushError(e, t('games.killTitle'));
    }
  };

  const chips = hero
    ? [
        { key: 'launcher', label: t(launcherLabelKey(hero.launcher)) },
        ...hero.category.slice(0, 1).map((c) => ({ key: `cat-${c}`, label: categoryLabel(t, c) })),
      ]
    : [];

  return (
    <section aria-label={t('desktop.title')} className="flex flex-col">
      {/* Stage: the selected game's art across the whole screen, under the HUD bar, melting into the page. */}
      <div className="relative -mx-[var(--gutter)] -mt-[calc(var(--topbar-h)+var(--gap))] h-[clamp(32rem,68vh,50rem)] overflow-hidden">
        <div
          aria-hidden="true"
          className="absolute inset-0 [mask-image:linear-gradient(to_bottom,black_65%,transparent)]"
        >
          {loading && <Skeleton variant="rect" className="absolute inset-0 h-full w-full rounded-none" />}
          <AnimatePresence initial={false}>
            {hero && (
              <motion.div
                key={hero.id}
                className="absolute inset-0"
                initial={{ opacity: 0 }}
                animate={{ opacity: 1 }}
                exit={{ opacity: 0 }}
                transition={fade}
              >
                <GameArtwork
                  src={hero.heroUrl ?? hero.coverUrl}
                  title={hero.title}
                  kind="hero"
                  priority
                  className="rounded-none"
                  style={{ position: 'absolute', inset: 0, height: '100%', aspectRatio: 'auto' }}
                />
              </motion.div>
            )}
          </AnimatePresence>
          <div className="absolute inset-0 bg-gradient-to-r from-bg via-bg/55 to-transparent" />
          <div className="absolute inset-x-0 bottom-0 h-1/2 bg-gradient-to-t from-bg to-transparent" />
        </div>

        {/* Title block, bottom left */}
        <div className="absolute bottom-[calc(var(--gap)*2)] left-[var(--gutter)] z-10 flex max-w-[min(52%,56rem)] flex-col gap-5">
          {loading && (
            <div className="flex flex-col gap-3">
              <Skeleton variant="text" width="70%" height="2.5rem" />
              <Skeleton variant="text" width="40%" />
            </div>
          )}

          {!loading && !hero && (
            <div className="flex flex-col items-start gap-4">
              <p className="text-[length:var(--fs-xl)] font-semibold text-text">{t('games.empty')}</p>
              <Button ref={primary} variant="primary" size="lg" icon={<GridIcon />} onClick={() => navigate('/games')}>
                {t('desktop.allGames')}
              </Button>
            </div>
          )}

          {hero && (
            <>
              <AnimatePresence mode="wait" initial={false}>
                <motion.div
                  key={hero.id}
                  initial={{ opacity: 0 }}
                  animate={{ opacity: 1 }}
                  exit={{ opacity: 0 }}
                  transition={fade}
                  className="flex flex-col gap-3"
                >
                  {isRunning && (
                    <span className="flex items-center gap-2 text-sm font-medium text-success">
                      <span aria-hidden="true" className="h-2 w-2 rounded-full bg-success" />
                      {t('games.nowPlaying')}
                    </span>
                  )}
                  <h1 className="line-clamp-2 font-display text-[clamp(2.75rem,4.6vw,5.5rem)] font-normal leading-[1.05] tracking-[-0.02em] text-text">
                    {hero.title}
                  </h1>
                  <ul className="flex flex-wrap items-center gap-2" aria-label={t('common.details')}>
                    {!hero.installed && (
                      <li>
                        <Badge tone="muted">{t('games.notInstalled')}</Badge>
                      </li>
                    )}
                    {hero.requiresAccount && (
                      <li>
                        <Badge tone="accent" title={t('games.requiresAccountHint')}>
                          {t('games.requiresAccount')}
                        </Badge>
                      </li>
                    )}
                    {chips.map((c) => (
                      <li key={c.key}>
                        <Badge tone="neutral">{c.label}</Badge>
                      </li>
                    ))}
                  </ul>
                </motion.div>
              </AnimatePresence>

              <div className="flex flex-wrap items-center gap-2">
                {isRunning ? (
                  <Button
                    ref={primary}
                    variant="danger"
                    size="lg"
                    icon={<StopIcon />}
                    onClick={() => setConfirmKill(true)}
                  >
                    {t('games.kill')}
                  </Button>
                ) : (
                  <Button
                    ref={primary}
                    variant="cta"
                    size="lg"
                    icon={<PlayIcon />}
                    loading={isLaunching}
                    disabled={!hero.installed}
                    title={hero.installed ? undefined : t('errors.gameNotInstalled')}
                    onClick={() => play(hero)}
                    className="min-w-[11rem]"
                  >
                    {isLaunching ? t('games.launching') : t('games.playNow')}
                  </Button>
                )}
                <Button variant="secondary" size="lg" icon={<InfoIcon />} onClick={() => navigate(`/games/${hero.id}`)}>
                  {t('games.details')}
                </Button>
              </div>
            </>
          )}
        </div>

        {/* Recent games rail, bottom right: switches the stage */}
        {strip.length > 0 && (
          <div className="absolute bottom-[calc(var(--gap)*2)] right-[var(--gutter)] z-10 flex flex-col items-end gap-3">
            <div className="flex items-center gap-4">
              <span className="hud-label">{t('desktop.recentlyPlayed')}</span>
              <Button variant="ghost" size="md" iconRight={<ArrowIcon />} onClick={() => navigate('/games')}>
                {t('desktop.allGames')}
              </Button>
            </div>
            <div role="list" aria-label={t('desktop.continuePlaying')} className="flex gap-3">
              {strip.slice(0, 6).map((g) => (
                <PosterTile
                  key={g.id}
                  game={g}
                  selected={hero?.id === g.id}
                  running={running.some((r) => r.gameId === g.id)}
                  onSelect={(x) => select(x.id)}
                />
              ))}
            </div>
          </div>
        )}
      </div>

      <Modal
        open={confirmKill}
        onClose={() => setConfirmKill(false)}
        title={t('games.killTitle')}
        description={t('games.killConfirm', { title: hero?.title ?? '' })}
        size="sm"
        danger
        footer={
          <>
            <Button variant="secondary" size="lg" onClick={() => setConfirmKill(false)}>
              {t('common.cancel')}
            </Button>
            <Button variant="danger" size="lg" onClick={() => hero && void closeGame(hero)}>
              {t('games.kill')}
            </Button>
          </>
        }
      />
    </section>
  );
}
