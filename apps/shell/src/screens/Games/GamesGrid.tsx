/**
 * Library grid: fixed column count (`shell.json → ui.gridColumns`), roving tab index (the selected card is the
 * single tab stop), spatial navigation through `data-nav` cards, and chunked rendering — rows are appended when a
 * sentinel under the grid scrolls into view, so a 500-game catalogue never mounts 500 covers at once.
 */
import type { Game } from '@clubshell/contracts';
import { useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { Skeleton } from '@/components/ui/Skeleton';
import { focusElement } from '@/hooks/useGamepad';
import { GameCard } from './GameCard';

export interface GamesGridProps {
  games: Game[];
  /** Columns (default 7). */
  columns?: number;
  selectedId: string | null;
  /** Ids of games whose process is alive. */
  runningIds?: ReadonlySet<string>;
  loading?: boolean;
  /** Text of the empty state (default `games.empty`). */
  emptyText?: string;
  /** Focus the roving card once the grid has games (initial focus of the screen). */
  autoFocus?: boolean;
  onSelect: (game: Game) => void;
  onActivate: (game: Game) => void;
  /** Launch from the card's hover play disc; omitted = cards only open details. */
  onPlay?: (game: Game) => void;
  className?: string;
}

const INITIAL_ROWS = 4;
const CHUNK_ROWS = 3;

/** Focuses the card of `gameId` (or the first card) inside `root`; returns the focused element. */
export function focusGameCard(gameId: string | null, root: ParentNode = document): HTMLElement | null {
  const el =
    (gameId ? root.querySelector<HTMLElement>(`[data-game-id="${CSS.escape(gameId)}"]`) : null) ??
    root.querySelector<HTMLElement>('[data-game-id]');
  if (el) {
    focusElement(el);
  }
  return el;
}

function EmptyIcon(): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      className="h-14 w-14"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <rect x="3" y="5" width="18" height="14" rx="3" />
      <path d="M8 12h.01M12 12h.01M16 12h.01" />
    </svg>
  );
}

export function GamesGrid({
  games,
  columns = 7,
  selectedId,
  runningIds,
  loading = false,
  emptyText,
  autoFocus = false,
  onSelect,
  onActivate,
  onPlay,
  className,
}: GamesGridProps): JSX.Element {
  const { t } = useTranslation();
  const cols = Math.max(2, Math.min(10, Math.round(columns)));
  const [limit, setLimit] = useState(cols * INITIAL_ROWS);
  const sentinel = useRef<HTMLDivElement>(null);
  const list = useRef<HTMLUListElement>(null);
  const focusedOnce = useRef(false);

  useEffect(() => {
    setLimit(cols * INITIAL_ROWS);
  }, [games, cols]);

  useEffect(() => {
    const el = sentinel.current;
    if (!el || limit >= games.length || typeof IntersectionObserver === 'undefined') {
      return undefined;
    }
    const io = new IntersectionObserver((entries) => {
      if (entries.some((e) => e.isIntersecting)) {
        setLimit((l) => Math.min(games.length, l + cols * CHUNK_ROWS));
      }
    });
    io.observe(el);
    return () => io.disconnect();
  }, [limit, games.length, cols]);

  // Roving tab stop: the selected card when visible, else the first one.
  const visible = useMemo(() => games.slice(0, limit), [games, limit]);
  const rovingId = useMemo(
    () => (selectedId && visible.some((g) => g.id === selectedId) ? selectedId : (visible[0]?.id ?? null)),
    [visible, selectedId],
  );

  useEffect(() => {
    if (!autoFocus || loading || focusedOnce.current || visible.length === 0 || !list.current) {
      return;
    }
    focusedOnce.current = true;
    focusGameCard(rovingId, list.current);
  }, [autoFocus, loading, visible.length, rovingId]);

  const style: CSSProperties = { gridTemplateColumns: `repeat(${cols}, minmax(0, 1fr))`, gap: 'var(--gap)' };

  if (loading) {
    return (
      <div className={clsx('grid', className)} style={style} aria-busy="true" aria-label={t('common.loading')}>
        {Array.from({ length: cols * 2 }, (_, i) => (
          <div key={i} className="flex flex-col gap-2">
            <Skeleton variant="cover" />
            <Skeleton variant="text" lines={2} className="px-1" />
          </div>
        ))}
      </div>
    );
  }

  if (games.length === 0) {
    return (
      <div
        className={clsx(
          'glass flex flex-col items-center justify-center gap-3 rounded-xl px-6 py-16 text-center text-muted',
          className,
        )}
      >
        <EmptyIcon />
        <p className="text-xl font-semibold text-text">{emptyText ?? t('games.empty')}</p>
      </div>
    );
  }

  return (
    <div className={className}>
      <ul ref={list} role="list" aria-label={t('games.gridLabel')} className="grid" style={style}>
        {visible.map((game, i) => (
          <li key={game.id} className="min-w-0">
            <GameCard
              game={game}
              selected={game.id === selectedId}
              running={runningIds?.has(game.id) ?? false}
              priority={i < cols * 2}
              tabIndex={game.id === rovingId ? 0 : -1}
              onSelect={onSelect}
              onActivate={onActivate}
              onLaunch={onPlay}
            />
          </li>
        ))}
      </ul>
      {limit < games.length && <div ref={sentinel} aria-hidden="true" className="h-px w-full" />}
    </div>
  );
}

export default GamesGrid;
