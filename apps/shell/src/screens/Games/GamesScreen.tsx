/**
 * `/games` — library: filter row (categories / installed / sort + search) over the cover grid. No hero here: Home
 * already opens on one, and on this screen it pushed the grid down to a single row above the fold. A card opens
 * details (where Play and Close live); its hover play disc launches directly. Selection lives in the games store,
 * so the last card looked at is Home's hero on the way back; the launch dialog is mounted here so a launch started
 * from a card shows progress in place.
 */
import type { Game } from '@clubshell/contracts';
import { useEffect, useMemo, useRef } from 'react';
import { motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { Button } from '@/components/ui/Button';
import { useGamepad } from '@/hooks/useGamepad';
import { useHotkeys } from '@/hooks/useHotkeys';
import { pluralize } from '@/lib/format';
import { DEFAULT_FILTERS, selectFilteredGames, useGamesStore } from '@/store/games';
import { useNotificationsStore } from '@/store/notifications';
import { useSettingsStore } from '@/store/settings';
import { useThemeStore } from '@/store/theme';
import { Categories, cycleCategory } from './Categories';
import { GamesGrid, focusGameCard } from './GamesGrid';
import { LaunchOverlay, launchGame } from './LaunchOverlay';
import { SearchBar } from './SearchBar';

export default function GamesScreen(): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const load = useGamesStore((s) => s.load);
  const status = useGamesStore((s) => s.status);
  const error = useGamesStore((s) => s.error);
  const total = useGamesStore((s) => s.ids.length);
  const categories = useGamesStore((s) => s.categories);
  const filters = useGamesStore((s) => s.filters);
  const setFilters = useGamesStore((s) => s.setFilters);
  const resetFilters = useGamesStore((s) => s.resetFilters);
  const games = useGamesStore(selectFilteredGames);
  const selectedId = useGamesStore((s) => s.selectedId);
  const select = useGamesStore((s) => s.select);
  const running = useGamesStore((s) => s.running);
  const pushError = useNotificationsStore((s) => s.pushError);
  const columns = useSettingsStore((s) => s.shellConfig?.ui.gridColumns ?? 7);
  const homeRoute = useSettingsStore((s) => s.shellConfig?.ui.defaultRoute ?? '/home');
  const animations = useThemeStore((s) => s.theme.animations);
  const searchRef = useRef<HTMLInputElement>(null);
  const gridRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    void load();
    document.getElementById('main')?.scrollTo({ top: 0 });
  }, [load]);

  const reportedError = useRef<unknown>(null);
  useEffect(() => {
    if (status === 'error' && error && error !== reportedError.current) {
      reportedError.current = error;
      pushError(error, t('games.title'));
    }
  }, [status, error, pushError, t]);

  const loading = status === 'loading' || (status === 'idle' && total === 0);
  const runningIds = useMemo(() => new Set(running.map((r) => r.gameId)), [running]);
  const filtersActive =
    filters.category !== DEFAULT_FILTERS.category ||
    filters.installedOnly !== DEFAULT_FILTERS.installedOnly ||
    filters.sort !== DEFAULT_FILTERS.sort ||
    filters.search.trim().length > 0;

  const openDetails = (game: Game): void => {
    select(game.id);
    navigate(`/games/${game.id}`);
  };

  const play = (game: Game): void => {
    select(game.id);
    void launchGame(game.id);
  };

  useGamepad({
    onBack: () => navigate(homeRoute),
    onTab: (dir) => setFilters({ category: cycleCategory(categories, filters.category, dir) }),
  });
  useHotkeys({
    'ctrl+f': () => searchRef.current?.focus(),
    '/': () => searchRef.current?.focus(),
  });

  const duration = animations ? 0.25 : 0;

  return (
    <motion.div
      initial={{ opacity: 0, y: 12 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration, ease: 'easeOut' }}
      className="flex min-h-full w-full flex-col gap-[var(--gap)]"
    >
      <div className="flex items-center gap-[var(--gap)]">
        <Categories
          className="min-w-0 flex-1"
          categories={categories}
          value={filters.category}
          onChange={(category) => setFilters({ category })}
          installedOnly={filters.installedOnly}
          onInstalledOnlyChange={(installedOnly) => setFilters({ installedOnly })}
          sort={filters.sort}
          onSortChange={(sort) => setFilters({ sort })}
        />
        <SearchBar
          ref={searchRef}
          className="!w-[clamp(220px,18vw,360px)]"
          value={filters.search}
          onChange={(search) => setFilters({ search })}
          onSubmit={() => focusGameCard(null, gridRef.current ?? document)}
        />
      </div>

      <div className="flex flex-wrap items-baseline justify-between gap-3">
        <h2 className="text-base font-semibold text-muted">
          {t('games.title')}
          <span className="ml-2 font-normal">{loading ? '' : pluralize('games', games.length)}</span>
        </h2>
        <div className="flex items-center gap-3 text-sm text-muted">
          {filters.installedOnly && <span>{t('games.installedFilterOn')}</span>}
          {filtersActive && (
            <Button variant="ghost" size="md" onClick={resetFilters}>
              {t('games.clearFilters')}
            </Button>
          )}
        </div>
      </div>

      <div ref={gridRef} className="pb-[var(--gap)]">
        <GamesGrid
          games={games}
          columns={columns}
          selectedId={selectedId}
          runningIds={runningIds}
          loading={loading}
          emptyText={filtersActive ? t('games.noResults') : t('games.empty')}
          autoFocus
          onSelect={(g) => select(g.id)}
          onActivate={openDetails}
          onPlay={play}
        />
      </div>

      <LaunchOverlay />
    </motion.div>
  );
}
