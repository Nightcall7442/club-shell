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
import { tiltHandlers } from '@/hooks/useTilt';
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
      {...tiltHandlers(6)}
      className={clsx('focus-ring tilt group relative block w-full rounded-lg text-left outline-none', className)}
      {...rest}
    >
      <GameArtwork
        src={game.coverUrl}
        title={game.title}
        kind="cover"
        priority={priority}
        className={clsx(
          'rounded-lg shadow-[var(--shadow-card)] transition-[box-shadow,filter] duration-[var(--dur-base)]',
          selected && 'border-glow',
          !game.installed && 'opacity-60 saturate-50',
        )}
        overlay={
          <>
            <div
              aria-hidden="true"
              className="pointer-events-none absolute inset-x-0 bottom-0 h-1/2 bg-gradient-to-t from-bg/95 via-bg/45 to-transparent"
            />
            <span aria-hidden="true" className="tilt-sheen rounded-lg" />
            {(running || !game.installed) && (
              <div className="absolute left-2 top-2">
                {running ? (
                  <Badge tone="primary" size="sm" solid live>
                    {t('games.running')}
                  </Badge>
                ) : (
                  <Badge tone="muted" size="sm">
                    {t('games.notInstalled')}
                  </Badge>
                )}
              </div>
            )}
            <div className="absolute inset-x-0 bottom-0 flex flex-col gap-1 p-3">
              <span className="line-clamp-2 text-base font-bold leading-tight text-text [text-shadow:0_1px_12px_rgb(0_0_0/0.7)]">
                {game.title}
              </span>
              <span
                className={clsx(
                  'flex items-center gap-2 text-xs text-text/75 transition-[opacity,transform] duration-[var(--dur-base)] ease-[var(--ease-out)]',
                  'translate-y-1 opacity-0 group-hover:translate-y-0 group-hover:opacity-100 group-focus-visible:translate-y-0 group-focus-visible:opacity-100 group-data-[focused=true]:translate-y-0 group-data-[focused=true]:opacity-100',
                  selected && 'translate-y-0 opacity-100',
                )}
              >
                <span
                  aria-label={t(launcherLabelKey(game.launcher))}
                  className="inline-flex h-5 min-w-5 items-center justify-center rounded bg-text/15 px-1 text-[0.65rem] font-bold uppercase tracking-wide"
                >
                  {launcherGlyph(game.launcher)}
                </span>
                <span className="truncate">{subline}</span>
                {game.ageRating > 0 && (
                  <span className="shrink-0 tnum">{t('games.ageRating', { age: game.ageRating })}</span>
                )}
              </span>
            </div>
          </>
        }
      />
    </button>
  );
});

export default GameCard;
