/**
 * 2:3 cover tile of the library grid. A native `<button>` (Enter/Space/click activate for free) with
 * `data-nav` for spatial navigation; focus and pointer-enter select the game (the hero follows the selection).
 */
import type { Game, LauncherType } from '@clubshell/contracts';
import { forwardRef, type ButtonHTMLAttributes } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { GameArtwork } from '@/components/media/GameArtwork';
import { Badge } from '@/components/ui/Badge';
import { useLocale } from '@/hooks/useLocale';
import { formatRelativeDay } from '@/lib/format';
import { categoryLabel } from './Categories';

export interface GameCardProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'onSelect'> {
  game: Game;
  /** Highlighted as the current hero game. */
  selected?: boolean;
  /** The game process is alive. */
  running?: boolean;
  /** Eager-load the cover (first rows). */
  priority?: boolean;
  onSelect?: (game: Game) => void;
  onActivate?: (game: Game) => void;
}

/** i18n label of a launcher (`games.launcherSteam`, …). */
export function launcherLabelKey(launcher: LauncherType): string {
  return `games.launcher${launcher.charAt(0).toUpperCase()}${launcher.slice(1)}`;
}

/** Two-letter monogram used as the launcher glyph on cards (no icon library in the bundle). */
export function launcherGlyph(launcher: LauncherType): string {
  switch (launcher) {
    case 'steam':
      return 'St';
    case 'epic':
      return 'Ep';
    case 'battleNet':
      return 'Bn';
    case 'riot':
      return 'Ri';
    case 'ea':
      return 'EA';
    case 'ubisoft':
      return 'Ub';
    default:
      return 'Ex';
  }
}

export const GameCard = forwardRef<HTMLButtonElement, GameCardProps>(function GameCard(
  {
    game,
    selected = false,
    running = false,
    priority = false,
    onSelect,
    onActivate,
    className,
    onFocus,
    onPointerEnter,
    onClick,
    ...rest
  },
  ref,
) {
  const { t } = useTranslation();
  const { locale } = useLocale();

  const subline = game.lastPlayedAt
    ? `${t('games.lastPlayed')}: ${formatRelativeDay(game.lastPlayedAt, locale)}`
    : game.category.length > 0
      ? game.category.map((c) => categoryLabel(t, c)).join(' · ')
      : t('games.neverPlayed');

  return (
    <button
      ref={ref}
      type="button"
      data-nav="true"
      data-game-id={game.id}
      aria-current={selected ? 'true' : undefined}
      aria-label={game.title}
      title={game.title}
      onFocus={(e) => {
        onSelect?.(game);
        onFocus?.(e);
      }}
      onPointerEnter={(e) => {
        onSelect?.(game);
        onPointerEnter?.(e);
      }}
      onClick={(e) => {
        onActivate?.(game);
        onClick?.(e);
      }}
      className={clsx(
        'focus-ring anim-cover-hover group flex w-full flex-col gap-2 rounded-lg text-left outline-none',
        'transition-[transform,box-shadow] duration-[var(--dur-base)] ease-[var(--ease-out)]',
        className,
      )}
      {...rest}
    >
      <GameArtwork
        src={game.coverUrl}
        title={game.title}
        kind="cover"
        priority={priority}
        className={clsx(
          'rounded-lg transition-[box-shadow] duration-[var(--dur-base)]',
          selected && 'border-glow',
          !game.installed && 'opacity-60 saturate-50',
        )}
        overlay={
          <>
            <div
              aria-hidden="true"
              className="pointer-events-none absolute inset-x-0 bottom-0 h-1/3 bg-gradient-to-t from-bg/90 to-transparent"
            />
            <div className="absolute left-2 top-2 flex flex-col items-start gap-1">
              {running ? (
                <Badge tone="primary" size="sm" solid live>
                  {t('games.running')}
                </Badge>
              ) : (
                <Badge tone={game.installed ? 'success' : 'muted'} size="sm" solid={game.installed}>
                  {game.installed ? t('games.installed') : t('games.notInstalled')}
                </Badge>
              )}
            </div>
            <div className="absolute bottom-2 left-2 right-2 flex items-end justify-between gap-2">
              <span
                aria-label={t(launcherLabelKey(game.launcher))}
                className="inline-flex h-7 min-w-7 items-center justify-center rounded-md bg-bg/70 px-1.5 text-xs font-bold uppercase tracking-wide text-text backdrop-blur-sm"
              >
                {launcherGlyph(game.launcher)}
              </span>
              {game.ageRating > 0 && (
                <span className="inline-flex h-7 items-center rounded-md bg-bg/70 px-1.5 text-xs font-bold text-text backdrop-blur-sm">
                  {t('games.ageRating', { age: game.ageRating })}
                </span>
              )}
            </div>
          </>
        }
      />
      <span className="flex min-w-0 flex-col gap-0.5 px-1">
        <span
          className={clsx(
            'line-clamp-2 text-base font-semibold leading-tight',
            selected ? 'text-primary' : 'text-text group-hover:text-primary',
          )}
        >
          {game.title}
        </span>
        <span className="truncate text-sm text-muted">{subline}</span>
      </span>
    </button>
  );
});

export default GameCard;
