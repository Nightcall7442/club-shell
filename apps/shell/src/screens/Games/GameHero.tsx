/**
 * Featured/selected game banner: hero artwork behind a gradient, title, meta chips and the primary actions
 * (Play, or Close game while running, plus Details). Owns the "close game?" confirmation. Reused as the header of
 * the details page (`showDetails={false}`, `size="lg"`).
 */
import type { AntiCheatKind, Game } from '@clubshell/contracts';
import { useEffect, useRef, useState } from 'react';
import clsx from 'clsx';
import { motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { GameArtwork } from '@/components/media/GameArtwork';
import { Badge } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { Skeleton } from '@/components/ui/Skeleton';
import { useLocale } from '@/hooks/useLocale';
import { formatGb } from '@/lib/format';
import { useThemeStore } from '@/store/theme';
import { categoryLabel } from './Categories';
import { launcherLabelKey } from './GameCard';

export type HeroSize = 'md' | 'lg';

export interface GameHeroProps {
  game: Game | null;
  loading?: boolean;
  running?: boolean;
  launching?: boolean;
  /** Show the "Details" button (default `true`). */
  showDetails?: boolean;
  size?: HeroSize;
  /** Focus the primary action once, when the first game is shown (initial focus of the screen). */
  autoFocus?: boolean;
  /** Catalogue banner: launcher + one genre, no description (the details page carries the rest). */
  compact?: boolean;
  onPlay: (game: Game) => void;
  onDetails?: (game: Game) => void;
  /** Called after the user confirms closing the running game. */
  onKill?: (game: Game) => void;
  className?: string;
}

const HEIGHT: Record<HeroSize, string> = {
  md: 'h-[clamp(300px,36vh,480px)]',
  lg: 'h-[clamp(340px,44vh,600px)]',
};

const ANTI_CHEAT_LABEL: Readonly<Record<AntiCheatKind, string>> = {
  none: '',
  eac: 'EAC',
  battlEye: 'BattlEye',
  vanguard: 'Vanguard',
  faceit: 'FACEIT',
  ricochet: 'Ricochet',
};

/** Human label of an anti-cheat subsystem (`''` for none). */
export function antiCheatLabel(kind: AntiCheatKind): string {
  return ANTI_CHEAT_LABEL[kind] ?? kind;
}

function PlayIcon(): JSX.Element {
  return (
    <svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
      <path d="M8 5.5v13a1 1 0 001.5.86l11-6.5a1 1 0 000-1.72l-11-6.5A1 1 0 008 5.5z" />
    </svg>
  );
}

function StopIcon(): JSX.Element {
  return (
    <svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
      <rect x="6" y="6" width="12" height="12" rx="2" />
    </svg>
  );
}

function InfoIcon(): JSX.Element {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" aria-hidden="true">
      <circle cx="12" cy="12" r="9" />
      <path d="M12 11v5M12 8h.01" />
    </svg>
  );
}

export function GameHero({
  game,
  loading = false,
  running = false,
  launching = false,
  showDetails = true,
  size = 'md',
  autoFocus = false,
  compact = false,
  onPlay,
  onDetails,
  onKill,
  className,
}: GameHeroProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const animations = useThemeStore((s) => s.theme.animations);
  const [confirmKill, setConfirmKill] = useState(false);
  const primary = useRef<HTMLButtonElement>(null);
  const focusedOnce = useRef(false);
  const duration = animations ? 0.25 : 0;
  const hasGame = game !== null && !loading;

  useEffect(() => {
    if (autoFocus && hasGame && !focusedOnce.current && primary.current) {
      focusedOnce.current = true;
      primary.current.focus({ preventScroll: true });
    }
  }, [autoFocus, hasGame]);

  if (loading) {
    return <Skeleton variant="rect" className={clsx('w-full rounded-xl', HEIGHT[size], className)} />;
  }

  if (!game) {
    return (
      <div
        className={clsx(
          'glass flex w-full items-center justify-center rounded-xl text-xl text-muted',
          HEIGHT[size],
          className,
        )}
      >
        {t('games.selectGame')}
      </div>
    );
  }

  const chips: { key: string; label: string; tone: 'neutral' | 'primary' | 'accent' | 'muted' }[] = compact
    ? [
        { key: 'launcher', label: t(launcherLabelKey(game.launcher)), tone: 'neutral' },
        ...game.category
          .slice(0, 1)
          .map((c) => ({ key: `cat-${c}`, label: categoryLabel(t, c), tone: 'neutral' as const })),
      ]
    : [
        { key: 'launcher', label: t(launcherLabelKey(game.launcher)), tone: 'neutral' },
        ...(game.antiCheat !== 'none'
          ? [
              {
                key: 'ac',
                label: `${t('games.antiCheat')}: ${antiCheatLabel(game.antiCheat)}`,
                tone: 'accent' as const,
              },
            ]
          : []),
        { key: 'size', label: formatGb(game.sizeGb, locale), tone: 'muted' },
        {
          key: 'age',
          label: game.ageRating > 0 ? t('games.ageRating', { age: game.ageRating }) : t('games.ageRatingAll'),
          tone: game.ageRating >= 18 ? 'primary' : 'muted',
        },
        ...game.category
          .slice(0, 2)
          .map((c) => ({ key: `cat-${c}`, label: categoryLabel(t, c), tone: 'neutral' as const })),
      ];

  return (
    <section
      aria-label={game.title}
      className={clsx('glass relative w-full overflow-hidden rounded-xl', HEIGHT[size], className)}
    >
      <GameArtwork
        key={`art-${game.id}`}
        src={game.heroUrl ?? game.coverUrl}
        title={game.title}
        kind="hero"
        priority
        aria-hidden="true"
        className="rounded-none"
        style={{ position: 'absolute', inset: 0, height: '100%', aspectRatio: 'auto' }}
      />
      <div
        aria-hidden="true"
        className="pointer-events-none absolute inset-0 bg-gradient-to-r from-bg/95 via-bg/70 to-bg/10"
      />
      <div
        aria-hidden="true"
        className="pointer-events-none absolute inset-x-0 bottom-0 h-1/2 bg-gradient-to-t from-bg/90 to-transparent"
      />

      <div className="relative flex h-full max-w-[min(70%,60rem)] flex-col justify-end gap-4 p-[var(--gutter)]">
        {/* Only the text re-mounts per game: the action row keeps its DOM so keyboard focus survives a selection change. */}
        <motion.div
          key={`content-${game.id}`}
          initial={{ opacity: 0, x: 16 }}
          animate={{ opacity: 1, x: 0 }}
          transition={{ duration, ease: 'easeOut' }}
          className="flex flex-col gap-3"
        >
          <h1
            className={clsx(
              'text-glow line-clamp-2 font-black leading-none tracking-tight text-text',
              size === 'lg' ? 'text-[length:var(--fs-display)]' : 'text-[length:var(--fs-3xl)]',
            )}
          >
            {game.title}
          </h1>
          <ul className="flex flex-wrap items-center gap-2" aria-label={t('common.details')}>
            {running && (
              <li>
                <Badge tone="primary" solid live>
                  {t('games.nowPlaying')}
                </Badge>
              </li>
            )}
            {!game.installed && (
              <li>
                <Badge tone="muted" solid>
                  {t('games.notInstalled')}
                </Badge>
              </li>
            )}
            {game.requiresAccount && (
              <li>
                <Badge tone="accent" solid title={t('games.requiresAccountHint')}>
                  {t('games.requiresAccount')}
                </Badge>
              </li>
            )}
            {chips.map((c) => (
              <li key={c.key}>
                <Badge
                  tone={c.tone}
                  size="md"
                  className={c.tone === 'neutral' ? 'bg-text/[0.08] text-text/85' : undefined}
                >
                  {c.label}
                </Badge>
              </li>
            ))}
          </ul>
          {!compact && game.description && (
            <p className="line-clamp-2 max-w-[48rem] text-base text-text/80">{game.description}</p>
          )}
        </motion.div>
        <div className="mt-1 flex flex-wrap items-center gap-3">
          {running ? (
            <Button ref={primary} variant="danger" size="xl" icon={<StopIcon />} onClick={() => setConfirmKill(true)}>
              {t('games.kill')}
            </Button>
          ) : (
            <Button
              ref={primary}
              variant="primary"
              size="xl"
              icon={<PlayIcon />}
              loading={launching}
              disabled={!game.installed}
              title={game.installed ? undefined : t('errors.gameNotInstalled')}
              onClick={() => onPlay(game)}
            >
              {launching ? t('games.launching') : t('games.playNow')}
            </Button>
          )}
          {showDetails && onDetails && (
            <Button variant="secondary" size="xl" icon={<InfoIcon />} onClick={() => onDetails(game)}>
              {t('games.details')}
            </Button>
          )}
        </div>
      </div>

      <Modal
        open={confirmKill}
        onClose={() => setConfirmKill(false)}
        title={t('games.killTitle')}
        description={t('games.killConfirm', { title: game.title })}
        size="sm"
        danger
        footer={
          <>
            <Button variant="secondary" size="lg" onClick={() => setConfirmKill(false)}>
              {t('common.cancel')}
            </Button>
            <Button
              variant="danger"
              size="lg"
              onClick={() => {
                setConfirmKill(false);
                onKill?.(game);
              }}
            >
              {t('games.kill')}
            </Button>
          </>
        }
      />
    </section>
  );
}

export default GameHero;
