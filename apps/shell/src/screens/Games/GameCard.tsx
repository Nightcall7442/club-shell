/**
 * 2:3 cover tile of the library grid. A native `<button>` (Enter/Space/click open details) with `data-nav` for
 * spatial navigation; focus and pointer-enter select the game (Home's hero follows the selection). With `onLaunch`, a
 * play disc appears on hover and launches without the detour through details.
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
  /** Launch straight from the card (mouse shortcut on hover); omitted = the card only opens details. */
  onLaunch?: (game: Game) => void;
}

function PlayIcon(): JSX.Element {
  return (
    <svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
      <path d="M8 5.5v13a1 1 0 0 0 1.5.86l10.5-6.5a1 1 0 0 0 0-1.72L9.5 4.64A1 1 0 0 0 8 5.5Z" />
    </svg>
  );
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
    onLaunch,
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
        'focus-ring hud-focus group relative block w-full rounded-lg text-left outline-none [--brk-inset:-7px]',
        className,
      )}
      {...rest}
    >
      <GameArtwork
        src={game.coverUrl}
        title={game.title}
        kind="cover"
        priority={priority}
        className="rounded-lg transition-[filter] duration-[var(--dur-base)] group-hover:brightness-110"
        overlay={
          <>
            {/* Dims and greys only the art under it: fading the whole tile also faded the "not installed" badge
                that explains why. */}
            {!game.installed && (
              <div aria-hidden="true" className="pointer-events-none absolute inset-0 bg-bg/55 backdrop-saturate-0" />
            )}
            <div
              aria-hidden="true"
              className="pointer-events-none absolute inset-x-0 bottom-0 h-1/2 bg-gradient-to-t from-bg/95 via-bg/45 to-transparent"
            />
            {onLaunch && game.installed && !running && (
              // A mouse shortcut, not a second control: a button inside the card's button is invalid HTML, and
              // keyboard and gamepad reach Play in one step anyway — Enter opens details, where Play has focus.
              <span
                aria-hidden="true"
                title={t('games.playNow')}
                data-card-play="true"
                onClick={(e) => {
                  e.stopPropagation();
                  onLaunch(game);
                }}
                className="absolute right-3 top-3 inline-flex h-12 w-12 scale-90 cursor-pointer items-center justify-center rounded-full bg-accent pl-0.5 text-on-accent opacity-0 transition-[opacity,transform] duration-[var(--dur-base)] ease-[var(--ease-out)] hover:!scale-110 group-hover:scale-100 group-hover:opacity-100 [&>svg]:h-5 [&>svg]:w-5"
              >
                <PlayIcon />
              </span>
            )}
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
