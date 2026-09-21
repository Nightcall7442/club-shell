/**
 * Home hero: full-bleed key art of the selected game (slow Ken Burns, crossfade on change), a poster strip to switch
 * it, the title block with Play / Close / Details, quick-action pills and floating session / wallet / booking widgets.
 * Selection is the games-store `selectedId`, so the pick carries over to `/games`; the running game is always shown.
 */
import type { Game } from '@clubshell/contracts';
import { useEffect, useMemo, useRef, useState, type CSSProperties, type PointerEvent, type ReactNode } from 'react';
import clsx from 'clsx';
import { AnimatePresence, motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { GameArtwork, useResolvedAsset } from '@/components/media/GameArtwork';
import { Badge } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { Skeleton } from '@/components/ui/Skeleton';
import { useImageTint } from '@/hooks/useImageTint';
import { useLocale } from '@/hooks/useLocale';
import { useSession } from '@/hooks/useSession';
import { tiltHandlers } from '@/hooks/useTilt';
import { whoosh } from '@/lib/sound';
import { formatGb, formatMoney } from '@/lib/format';
import { log } from '@/lib/logger';
import { api } from '@/lib/tauri';
import { serverNow, toDateKey } from '@/lib/time';
import { categoryLabel } from '@/screens/Games/Categories';
import { launcherLabelKey } from '@/screens/Games/GameCard';
import { antiCheatLabel } from '@/screens/Games/GameHero';
import { launchGame } from '@/screens/Games/LaunchOverlay';
import { QuickActions } from '@/screens/Desktop/QuickActions';
import { ExtendSessionModal, Ring, timerLabel, type RingProps } from '@/screens/Desktop/SessionTimer';
import { selectFeaturedGames, selectRecentGames, useGamesStore } from '@/store/games';
import { useNotificationsStore } from '@/store/notifications';
import { selectFeatures, useSettingsStore } from '@/store/settings';
import { selectAnimationsEnabled, useThemeStore } from '@/store/theme';
import { selectTariff, useWalletStore } from '@/store/wallet';

const STRIP_MAX = 6;

/** Greeting key by local hour. */
export function greetingKey(
  hour: number,
): 'desktop.greetingMorning' | 'desktop.greetingDay' | 'desktop.greetingEvening' | 'desktop.greetingNight' {
  if (hour < 5) return 'desktop.greetingNight';
  if (hour < 12) return 'desktop.greetingMorning';
  if (hour < 18) return 'desktop.greetingDay';
  if (hour < 23) return 'desktop.greetingEvening';
  return 'desktop.greetingNight';
}

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
const GridIcon = (): JSX.Element => (
  <svg {...svgProps}>
    <rect x="3" y="3" width="7" height="7" rx="1.5" />
    <rect x="14" y="3" width="7" height="7" rx="1.5" />
    <rect x="3" y="14" width="7" height="7" rx="1.5" />
    <rect x="14" y="14" width="7" height="7" rx="1.5" />
  </svg>
);
const ArrowIcon = (): JSX.Element => (
  <svg {...svgProps}>
    <path d="M5 12h14M13 6l6 6-6 6" />
  </svg>
);

// ---------------------------------------------------------------------------------------------------------------------
// Poster strip
// ---------------------------------------------------------------------------------------------------------------------

const TILE_W = 'w-[clamp(4.25rem,5vw,5.75rem)]';

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
  const animations = useThemeStore(selectAnimationsEnabled);
  return (
    <button
      type="button"
      role="listitem"
      data-nav="true"
      aria-label={game.title}
      aria-pressed={selected}
      onClick={() => onSelect(game)}
      onFocus={() => onSelect(game)}
      onMouseEnter={() => onSelect(game)}
      {...tiltHandlers(9)}
      className={clsx(
        'focus-ring tilt relative shrink-0 overflow-hidden rounded-lg transition-[opacity,box-shadow] duration-[var(--dur-base)] ease-[var(--ease-out)]',
        TILE_W,
        selected ? 'opacity-100 [--zoom:1.06]' : 'opacity-55 hover:opacity-100',
      )}
    >
      <GameArtwork src={game.coverUrl} title={game.title} kind="cover" priority className="rounded-lg" />
      <span aria-hidden="true" className="tilt-sheen rounded-lg" />
      {selected && (
        <motion.span
          layoutId="home-strip-ring"
          aria-hidden="true"
          className="pointer-events-none absolute inset-0 rounded-lg border-glow"
          transition={animations ? { type: 'spring', stiffness: 480, damping: 40, mass: 0.7 } : { duration: 0 }}
        />
      )}
      {running && (
        <span
          aria-hidden="true"
          className="anim-live-dot absolute right-1.5 top-1.5 h-2.5 w-2.5 rounded-full bg-success shadow-[0_0_10px_rgb(var(--c-success))]"
        />
      )}
    </button>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Widgets
// ---------------------------------------------------------------------------------------------------------------------

function Widget({ children, className }: { children: ReactNode; className?: string }): JSX.Element {
  return <div className={clsx('glass-strong rounded-2xl p-4', className)}>{children}</div>;
}

function SessionWidget(): JSX.Element {
  const { t } = useTranslation();
  const s = useSession();
  const tariff = useWalletStore(selectTariff(s.tariffId));
  const [open, setOpen] = useState(false);

  const total = s.secondsUsed + Math.max(0, s.secondsLeft);
  const left = s.isOpenEnded ? 1 : total > 0 ? Math.max(0, s.secondsLeft) / total : 0;
  const label = timerLabel(s.isOpen, s.secondsLeft, s.secondsUsed);
  const tone: RingProps['tone'] =
    !s.isOpen || s.isOpenEnded ? 'muted' : s.isCritical ? 'danger' : s.isWarning ? 'accent' : 'primary';
  const caption = !s.isOpen ? t('session.noSession') : s.isOpenEnded ? t('session.timeUsed') : t('session.timeLeft');
  const canExtend = s.isOpen && !s.isOpenEnded;

  return (
    <Widget>
      <div className="flex items-center gap-4">
        <div className="relative flex items-center justify-center">
          <Ring progress={left} size={72} stroke={6} tone={tone} label={t('session.timerLabel')} valueText={label} />
          <span className="tnum absolute text-xs font-bold text-muted" aria-hidden="true">
            {s.isOpen && !s.isOpenEnded ? `${Math.round(left * 100)}%` : '∞'}
          </span>
        </div>
        <div className="min-w-0 flex-1">
          <div className="text-xs font-semibold uppercase tracking-wide text-muted">{caption}</div>
          <div
            className={clsx(
              'tnum text-3xl font-bold leading-tight',
              s.isCritical ? 'timer-critical' : s.isWarning ? 'timer-warning' : 'text-text',
            )}
          >
            {label}
          </div>
          {tariff && <div className="truncate text-sm text-muted">{tariff.name}</div>}
        </div>
      </div>
      {canExtend && (
        <Button variant="secondary" size="md" block className="mt-3" onClick={() => setOpen(true)}>
          {t('session.extend')}
        </Button>
      )}
      <ExtendSessionModal open={open} onClose={() => setOpen(false)} />
    </Widget>
  );
}

function WalletWidget(): JSX.Element | null {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const navigate = useNavigate();
  const { user } = useSession();
  const wallet = useWalletStore((s) => s.balance?.amount ?? null);
  const balance = wallet ?? user?.balance ?? null;
  if (!balance) {
    return null;
  }
  return (
    <Widget>
      <div className="text-xs font-semibold uppercase tracking-wide text-muted">{t('desktop.balance')}</div>
      <div className="tnum truncate text-3xl font-bold leading-tight text-text">{formatMoney(balance, locale)}</div>
      <Button
        variant="primary"
        size="md"
        block
        iconRight={<ArrowIcon />}
        className="mt-3"
        onClick={() => navigate('/wallet')}
      >
        {t('desktop.topUp')}
      </Button>
    </Widget>
  );
}

const SEAT_DOT: Record<string, string> = {
  free: 'bg-accent shadow-[0_0_6px_rgb(var(--c-accent)/0.8)]',
  booked: 'bg-primary',
  busy: 'bg-text/25',
  locked: 'bg-text/25',
  maintenance: 'bg-danger/60',
  offline: 'bg-text/15',
};

function BookingWidget(): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [seats, setSeats] = useState<{ status: string }[] | null>(null);

  useEffect(() => {
    let active = true;
    api.booking.seats(toDateKey(serverNow())).then(
      (r) => active && setSeats(r.seats),
      (e: unknown) => {
        log.warn('booking widget failed', e);
        if (active) {
          setSeats([]);
        }
      },
    );
    return () => {
      active = false;
    };
  }, []);

  const free = seats?.filter((s) => s.status === 'free').length ?? 0;

  return (
    <Widget>
      <div className="text-xs font-semibold uppercase tracking-wide text-muted">{t('desktop.nav.booking')}</div>
      <div className="text-xl font-bold leading-tight text-text">
        {seats === null ? <Skeleton variant="text" width="8rem" /> : t('booking.freeSeats', { count: free })}
      </div>
      {seats && seats.length > 0 && (
        <div aria-hidden="true" className="mt-3 grid grid-cols-12 gap-1.5">
          {seats.slice(0, 48).map((s, i) => (
            <span key={i} className={clsx('h-2 rounded-sm', SEAT_DOT[s.status] ?? SEAT_DOT.busy)} />
          ))}
        </div>
      )}
      <Button
        variant="secondary"
        size="md"
        block
        iconRight={<ArrowIcon />}
        className="mt-3"
        onClick={() => navigate('/booking')}
      >
        {t('booking.reserve')}
      </Button>
    </Widget>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Hero
// ---------------------------------------------------------------------------------------------------------------------

export function HomeHero(): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const navigate = useNavigate();
  const { user } = useSession();
  const features = useSettingsStore(selectFeatures);
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
  const duration = animations ? 0.25 : 0;
  const { url: artUrl } = useResolvedAsset(hero ? (hero.heroUrl ?? hero.coverUrl) : null);
  const tint = useImageTint(artUrl);

  // Parallax: pointer position → --px/--py in −1..1 on the section (CSS moves art and text in opposite directions).
  const onPointerMove = (e: PointerEvent<HTMLElement>): void => {
    if (!animations || e.pointerType === 'touch') {
      return;
    }
    const r = e.currentTarget.getBoundingClientRect();
    e.currentTarget.style.setProperty('--px', (((e.clientX - r.left) / r.width) * 2 - 1).toFixed(3));
    e.currentTarget.style.setProperty('--py', (((e.clientY - r.top) / r.height) * 2 - 1).toFixed(3));
  };
  const onPointerLeave = (e: PointerEvent<HTMLElement>): void => {
    e.currentTarget.style.setProperty('--px', '0');
    e.currentTarget.style.setProperty('--py', '0');
  };

  // Entrance choreography of the title block: each line arrives a beat after the previous, out of a soft blur.
  const stagger = {
    hidden: {},
    show: { transition: { staggerChildren: animations ? 0.07 : 0 } },
    exit: { opacity: 0, y: -8, transition: { duration: animations ? 0.18 : 0 } },
  };
  const line = {
    hidden: { opacity: 0, y: 18, filter: 'blur(8px)' },
    show: {
      opacity: 1,
      y: 0,
      filter: 'blur(0px)',
      transition: { duration: animations ? 0.5 : 0, ease: [0.16, 1, 0.3, 1] },
    },
  };

  // A whoosh when the hero changes by hand (not on first paint).
  const lastHeroId = useRef<string | null>(null);
  useEffect(() => {
    const id = hero?.id ?? null;
    if (lastHeroId.current !== null && id !== null && id !== lastHeroId.current) {
      whoosh();
    }
    lastHeroId.current = id;
  }, [hero?.id]);

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

  const greeting = t(greetingKey(new Date().getHours()), { name: user?.displayName ?? '' });

  const chips = hero
    ? [
        { key: 'launcher', label: t(launcherLabelKey(hero.launcher)) },
        ...(hero.antiCheat !== 'none' ? [{ key: 'ac', label: antiCheatLabel(hero.antiCheat) }] : []),
        { key: 'size', label: formatGb(hero.sizeGb, locale) },
        ...hero.category.slice(0, 2).map((c) => ({ key: `cat-${c}`, label: categoryLabel(t, c) })),
      ]
    : [];

  return (
    <section
      aria-label={t('desktop.title')}
      onPointerMove={onPointerMove}
      onPointerLeave={onPointerLeave}
      style={tint ? ({ '--hero-tint': tint } as CSSProperties) : undefined}
      className="relative -mx-[var(--gutter)] -mt-[calc(var(--topbar-h)+var(--gap))] h-[calc(100vh-4.5rem)] min-h-[38rem] overflow-hidden [--px:0] [--py:0]"
    >
      {/* Art + veils, faded out at the bottom so the hero melts into the page. */}
      <div
        aria-hidden="true"
        className="absolute inset-0 [mask-image:linear-gradient(to_bottom,black_70%,transparent)]"
      >
        {loading && <Skeleton variant="rect" className="absolute inset-0 h-full w-full rounded-none" />}
        {/* Oversized so the parallax drift never shows an edge; drifts against the pointer for depth. */}
        <div className="absolute -inset-[2%] transition-transform duration-[900ms] ease-[var(--ease-out)] [transform:translate3d(calc(var(--px)*-1.1%),calc(var(--py)*-0.7%),0)]">
          <AnimatePresence initial={false}>
            {hero && (
              <motion.div
                key={hero.id}
                className="absolute inset-0"
                style={{ transformOrigin: '65% 40%' }}
                initial={{ opacity: 0, scale: 1.02, filter: 'blur(14px)' }}
                animate={{ opacity: 1, scale: animations ? 1.08 : 1, filter: 'blur(0px)' }}
                exit={{ opacity: 0, scale: 1.04, filter: 'blur(10px)' }}
                transition={{
                  opacity: { duration: animations ? 0.8 : 0 },
                  filter: { duration: animations ? 1.1 : 0, ease: 'easeOut' },
                  scale: { duration: 34, ease: 'linear' },
                }}
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
        </div>
        <div className="film-grain" />
        <div className="film-vignette" />
        <div className="absolute inset-0 bg-gradient-to-r from-bg/95 via-bg/40 to-bg/5" />
        <div className="absolute inset-x-0 bottom-0 h-3/5 bg-gradient-to-t from-bg/95 via-bg/45 to-transparent" />
        <div className="absolute inset-x-0 top-0 h-64 bg-gradient-to-b from-bg/80 via-bg/30 to-transparent" />
        {/* Stage light: the artwork's own colour, bleeding into the dark corner under the title. */}
        {tint && (
          <div className="absolute inset-0 transition-opacity duration-[1200ms] [background:radial-gradient(70%_60%_at_18%_100%,rgb(var(--hero-tint)/0.28),transparent_70%)]" />
        )}
      </div>

      {/* Poster strip */}
      {strip.length > 0 && (
        <div
          role="list"
          aria-label={t('desktop.continuePlaying')}
          className="absolute left-[var(--gutter)] top-[calc(var(--topbar-h)+var(--gap))] z-10 flex items-stretch gap-2.5"
        >
          {strip.map((g) => (
            <PosterTile
              key={g.id}
              game={g}
              selected={hero?.id === g.id}
              running={running.some((r) => r.gameId === g.id)}
              onSelect={(x) => select(x.id)}
            />
          ))}
          <button
            type="button"
            role="listitem"
            data-nav="true"
            onClick={() => navigate('/games')}
            className={clsx(
              'focus-ring glass flex shrink-0 flex-col items-center justify-center gap-1.5 rounded-lg px-1 text-center text-xs font-semibold leading-tight text-muted transition-colors duration-[var(--dur-fast)] hover:bg-surface/80 hover:text-text [&>svg]:h-6 [&>svg]:w-6',
              TILE_W,
            )}
          >
            <GridIcon />
            {t('desktop.allGames')}
          </button>
        </div>
      )}

      {/* Floating widgets */}
      <aside
        aria-label={t('desktop.sessionBar')}
        className="absolute right-[var(--gutter)] top-[calc(var(--topbar-h)+var(--gap))] z-10 hidden w-[clamp(17rem,19vw,22rem)] flex-col gap-3 2xl:flex"
      >
        <SessionWidget />
        {features.topup && <WalletWidget />}
        {features.booking && <BookingWidget />}
      </aside>

      {/* Title block; the pill row below it may run wider than the text column. */}
      <div className="absolute inset-x-[var(--gutter)] bottom-[var(--gap)] z-10 flex flex-col gap-4 transition-transform duration-[900ms] ease-[var(--ease-out)] [transform:translate3d(calc(var(--px)*0.35%),calc(var(--py)*0.25%),0)] [&>*:not(:last-child)]:max-w-[min(64%,64rem)] 2xl:[&>*:not(:last-child)]:max-w-[min(56%,64rem)]">
        <p className="text-base font-medium text-text/70">
          {greeting}
          <span className="mx-2 text-text/30">·</span>
          {t('desktop.welcomeBack')}
        </p>

        {loading && (
          <div className="flex flex-col gap-3">
            <Skeleton variant="text" width="60%" height="3.5rem" />
            <Skeleton variant="text" width="40%" />
          </div>
        )}

        {!loading && !hero && (
          <div className="flex flex-col items-start gap-4">
            <h1 className="text-[length:var(--fs-3xl)] font-black leading-none tracking-tight text-text">
              {t('games.empty')}
            </h1>
            <Button ref={primary} variant="primary" size="xl" icon={<GridIcon />} onClick={() => navigate('/games')}>
              {t('desktop.allGames')}
            </Button>
          </div>
        )}

        {hero && (
          <>
            <AnimatePresence mode="wait" initial={false}>
              <motion.div
                key={hero.id}
                variants={stagger}
                initial="hidden"
                animate="show"
                exit="exit"
                className="flex flex-col gap-3"
              >
                <motion.h1
                  variants={line}
                  className="text-glow line-clamp-2 text-[length:var(--fs-display)] font-black leading-[0.95] tracking-[-0.03em] text-text"
                >
                  {hero.title}
                </motion.h1>
                <motion.ul
                  variants={line}
                  className="flex flex-wrap items-center gap-2"
                  aria-label={t('common.details')}
                >
                  {isRunning && (
                    <li>
                      <Badge tone="primary" solid live>
                        {t('games.nowPlaying')}
                      </Badge>
                    </li>
                  )}
                  {!hero.installed && (
                    <li>
                      <Badge tone="muted" solid>
                        {t('games.notInstalled')}
                      </Badge>
                    </li>
                  )}
                  {hero.requiresAccount && (
                    <li>
                      <Badge tone="accent" solid title={t('games.requiresAccountHint')}>
                        {t('games.requiresAccount')}
                      </Badge>
                    </li>
                  )}
                  {chips.map((c) => (
                    <li key={c.key}>
                      <Badge tone="neutral" size="md" className="bg-text/[0.08] text-text/85">
                        {c.label}
                      </Badge>
                    </li>
                  ))}
                </motion.ul>
                {hero.description && (
                  <motion.p variants={line} className="line-clamp-2 max-w-[44rem] text-lg leading-snug text-text/80">
                    {hero.description}
                  </motion.p>
                )}
              </motion.div>
            </AnimatePresence>

            <div className="mt-1 flex flex-wrap items-center gap-3">
              {isRunning ? (
                <Button
                  ref={primary}
                  variant="danger"
                  size="xl"
                  icon={<StopIcon />}
                  onClick={() => setConfirmKill(true)}
                >
                  {t('games.kill')}
                </Button>
              ) : (
                <Button
                  ref={primary}
                  variant="primary"
                  size="xl"
                  icon={<PlayIcon />}
                  loading={isLaunching}
                  disabled={!hero.installed}
                  title={hero.installed ? undefined : t('errors.gameNotInstalled')}
                  onClick={() => play(hero)}
                  className="min-w-[14rem] [box-shadow:0_14px_44px_-12px_rgb(var(--hero-tint,var(--c-primary))/0.65)]"
                >
                  {isLaunching ? t('games.launching') : t('games.playNow')}
                </Button>
              )}
              <Button variant="secondary" size="xl" icon={<InfoIcon />} onClick={() => navigate(`/games/${hero.id}`)}>
                {t('games.details')}
              </Button>
            </div>
          </>
        )}

        <QuickActions compact className="mt-2" />
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
