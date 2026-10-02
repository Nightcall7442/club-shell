/**
 * Game catalogue (Map by id + server order), client-side filters, running processes and launch state.
 * Derived arrays are memoized outside React: the `select*` functions return the same reference while their
 * inputs are unchanged, so `useGamesStore(selectFilteredGames)` never re-renders spuriously.
 */
import {
  isGameStateTerminal,
  type Game,
  type GameStateChanged,
  type GamesKillResponse,
  type GamesLaunchRequest,
  type GamesSort,
  type LaunchResult,
  type RunningGame,
} from '@clubshell/contracts';
import { create } from 'zustand';
import { subscribeWithSelector } from 'zustand/middleware';
import { track } from '@/lib/analytics';
import { log } from '@/lib/logger';
import { api, toShellApiError, type ShellError } from '@/lib/tauri';
import { asShellError, type AsyncStatus } from './settings';

export interface GamesFilters {
  category: string | null;
  search: string;
  installedOnly: boolean;
  sort: GamesSort;
}

export interface LaunchingState {
  gameId: string;
  title: string;
  startedAt: number;
  pid: number | null;
}

export interface GamesState {
  byId: ReadonlyMap<string, Game>;
  /** Ids in server (popularity) order. */
  ids: string[];
  categories: string[];
  total: number;
  catalogVersion: string | null;
  filters: GamesFilters;
  selectedId: string | null;
  running: RunningGame[];
  /** Launch in flight until `game.stateChanged{running|failed|exited}` (or the command fails). */
  launching: LaunchingState | null;
  launchError: ShellError | null;
  lastStateChange: GameStateChanged | null;
  status: AsyncStatus;
  error: ShellError | null;
}

export interface GamesActions {
  /** Loads the whole catalogue (≤ 500) once; `force` reloads. Errors are swallowed into `error`. */
  load(force?: boolean): Promise<void>;
  /** Fetches one game (cache first unless `force`). */
  get(gameId: string, force?: boolean): Promise<Game | null>;
  refreshRunning(): Promise<void>;
  select(gameId: string | null): void;
  setFilters(patch: Partial<GamesFilters>): void;
  resetFilters(): void;
  launch(gameId: string, opts?: Omit<GamesLaunchRequest, 'gameId'>): Promise<LaunchResult>;
  /** Kills one game (or every running game when omitted). */
  kill(gameId?: string, force?: boolean): Promise<GamesKillResponse>;
  onStateChanged(e: GameStateChanged): void;
  upsert(game: Game): void;
  /** Clears user-scoped state (running, launching, selection, filters); keeps the catalogue. */
  reset(): void;
}

export type GamesStore = GamesState & GamesActions;

export const DEFAULT_FILTERS: GamesFilters = { category: null, search: '', installedOnly: false, sort: 'popularity' };

const initialState: GamesState = {
  byId: new Map(),
  ids: [],
  categories: [],
  total: 0,
  catalogVersion: null,
  filters: DEFAULT_FILTERS,
  selectedId: null,
  running: [],
  launching: null,
  launchError: null,
  lastStateChange: null,
  status: 'idle',
  error: null,
};

/**
 * One key per category, whichever way the catalogue spells it: `battle-royale`, `battle_royale` and `battleRoyale` are
 * all `battleRoyale` (the `games.cat.*` keys), so a category is one chip with one translated name.
 */
export function categoryKey(category: string): string {
  const words = category
    .trim()
    .split(/[-_\s]+/)
    .filter(Boolean);
  return words
    .map((w, i) => (i === 0 ? w.charAt(0).toLowerCase() + w.slice(1) : w.charAt(0).toUpperCase() + w.slice(1)))
    .join('');
}

function indexGames(games: Game[]): Pick<GamesState, 'byId' | 'ids' | 'categories'> {
  const byId = new Map<string, Game>();
  const categories = new Set<string>();
  for (const g of games) {
    byId.set(g.id, g);
    for (const c of g.category) {
      categories.add(categoryKey(c));
    }
  }
  return { byId, ids: games.map((g) => g.id), categories: Array.from(categories).sort((a, b) => a.localeCompare(b)) };
}

export const useGamesStore = create<GamesStore>()(
  subscribeWithSelector((set, get) => ({
    ...initialState,

    async load(force = false) {
      if (get().status === 'loading' || (!force && get().ids.length > 0)) {
        return;
      }
      // A forced reload over a loaded list (the Games screen opening, the lock screen between players) is quiet: the
      // list stays on screen and a failure keeps it. That is how a game the owner added reaches a running Shell — the
      // Agent refreshes its catalogue on the server's catalogVersion, but sends no event for it.
      const quiet = get().ids.length > 0;
      if (!quiet) {
        set({ status: 'loading', error: null });
      }
      try {
        const res = await api.games.list({ pageSize: 500, sort: 'popularity' });
        set({ ...indexGames(res.items), total: res.total, catalogVersion: res.catalogVersion, status: 'ready' });
      } catch (e) {
        const error = asShellError(e);
        log.warn('games.load failed', error);
        if (!quiet) {
          set({ status: 'error', error });
        }
      }
    },

    async get(gameId, force = false) {
      const cached = get().byId.get(gameId);
      if (cached && !force) {
        return cached;
      }
      try {
        const game = await api.games.get(gameId);
        get().upsert(game);
        return game;
      } catch (e) {
        log.warn(`games.get(${gameId}) failed`, asShellError(e));
        return cached ?? null;
      }
    },

    async refreshRunning() {
      try {
        set({ running: await api.games.running() });
      } catch (e) {
        log.debug('games.running failed', asShellError(e));
      }
    },

    select(gameId) {
      set({ selectedId: gameId });
    },

    setFilters(patch) {
      set((s) => ({ filters: { ...s.filters, ...patch } }));
    },

    resetFilters() {
      set({ filters: DEFAULT_FILTERS });
    },

    async launch(gameId, opts) {
      const game = get().byId.get(gameId);
      set({ launching: { gameId, title: game?.title ?? gameId, startedAt: Date.now(), pid: null }, launchError: null });
      track('game.launch', { gameId });
      try {
        const result = await api.games.launch({ gameId, ...opts });
        set((s) => ({
          launching: s.launching?.gameId === gameId ? { ...s.launching, pid: result.pid ?? null } : s.launching,
        }));
        void get().refreshRunning();
        settleLaunch(gameId);
        return result;
      } catch (e) {
        const err = toShellApiError(e);
        set({ launching: null, launchError: isLaunchCancelled(err) ? null : err.toJSON() });
        track('game.launchFailed', { gameId, code: err.code });
        throw err;
      }
    },

    async kill(gameId, force) {
      try {
        const res = await api.games.kill(gameId ? { gameId, force: force ?? null } : force ? { force } : undefined);
        set((s) => ({
          running: gameId ? s.running.filter((r) => r.gameId !== gameId && !res.pids.includes(r.pid)) : [],
          launching: gameId && s.launching?.gameId !== gameId ? s.launching : null,
        }));
        track('game.kill', { gameId: gameId ?? '*', killed: res.killed });
        return res;
      } catch (e) {
        const error = toShellApiError(e);
        // The Agent no longer tracks it: the game is gone (or its launcher handed over and exited). Drop the stale
        // "running" entry, so the player is not stuck on a game that cannot be closed or replaced.
        if (gameId && error.code === 'notFound') {
          set((s) => ({
            running: s.running.filter((r) => r.gameId !== gameId),
            launching: s.launching?.gameId === gameId ? null : s.launching,
          }));
          void get().refreshRunning();
          return { killed: 0, pids: [] };
        }
        throw error;
      }
    },

    onStateChanged(e) {
      set((s) => {
        const running = s.running.filter((r) => r.gameId !== e.gameId);
        if (!isGameStateTerminal(e.state)) {
          const prev = s.running.find((r) => r.gameId === e.gameId);
          running.push({
            gameId: e.gameId,
            title: e.title,
            pid: e.pid ?? prev?.pid ?? 0,
            startedAt: prev?.startedAt ?? e.at,
            accountLeaseId: prev?.accountLeaseId ?? null,
            state: e.state,
          });
        }
        const launching = s.launching?.gameId === e.gameId && e.state !== 'launching' ? null : s.launching;
        const launchError =
          e.state === 'failed' && e.error && !isLaunchCancelled(e.error)
            ? { code: e.error.code, message: e.error.message, details: e.error.details ?? undefined }
            : s.launchError;
        const game = s.byId.get(e.gameId);
        const byId =
          game && e.state === 'running' ? new Map(s.byId).set(e.gameId, { ...game, lastPlayedAt: e.at }) : s.byId;
        return { running, launching, launchError, lastStateChange: e, byId };
      });
    },

    upsert(game) {
      set((s) => {
        const byId = new Map(s.byId);
        byId.set(game.id, game);
        const ids = s.ids.includes(game.id) ? s.ids : [...s.ids, game.id];
        const keys = game.category.map(categoryKey);
        const categories = keys.some((c) => !s.categories.includes(c))
          ? Array.from(new Set([...s.categories, ...keys])).sort((a, b) => a.localeCompare(b))
          : s.categories;
        return { byId, ids, categories };
      });
    },

    reset() {
      set({
        running: [],
        launching: null,
        launchError: null,
        selectedId: null,
        filters: DEFAULT_FILTERS,
        lastStateChange: null,
      });
    },
  })),
);

/** How often (and how many times) a launch the Agent has finished is checked against its list of running games. */
const LAUNCH_SETTLE_MS = 3000;
const LAUNCH_SETTLE_TRIES = 3;

/**
 * Safety net for the launch screen. `game.stateChanged{running}` ends a launch; should that event get lost, the screen
 * stayed at "waiting for the game window" for good, its "cancel" the only way out. Once the Agent has returned the
 * game's pid, its `games.running` settles the launch instead: the game is there (running), or after a few checks it
 * is not (gone already).
 */
function settleLaunch(gameId: string, attempt = 1): void {
  setTimeout(() => {
    const before = useGamesStore.getState().launching;
    if (before?.gameId !== gameId || before.pid == null) {
      return;
    }
    void useGamesStore
      .getState()
      .refreshRunning()
      .then(() => {
        const s = useGamesStore.getState();
        if (s.launching?.gameId !== gameId) {
          return;
        }
        if (s.running.some((r) => r.gameId === gameId) || attempt >= LAUNCH_SETTLE_TRIES) {
          log.warn(`games.launch(${gameId}): no running event; settled from games.running (attempt ${attempt})`);
          useGamesStore.setState({ launching: null });
        } else {
          settleLaunch(gameId, attempt + 1);
        }
      });
  }, LAUNCH_SETTLE_MS);
}

/** The player cancelled the launch ("cancel launch"): `gameLaunchFailed{stage: cancelled}`, not worth an error. */
export function isLaunchCancelled(error: { code?: string; details?: unknown } | null | undefined): boolean {
  const details = error?.details;
  return (
    error?.code === 'gameLaunchFailed' &&
    typeof details === 'object' &&
    details !== null &&
    (details as Record<string, unknown>)['stage'] === 'cancelled'
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Memoized selectors (reference-stable while inputs are unchanged)
// ---------------------------------------------------------------------------------------------------------------------

function memo1<A, R>(fn: (a: A) => R): (a: A) => R {
  let lastA: A | undefined;
  let lastR: R | undefined;
  let primed = false;
  return (a) => {
    if (primed && a === lastA) {
      return lastR as R;
    }
    primed = true;
    lastA = a;
    lastR = fn(a);
    return lastR;
  };
}

function memo2<A, B, R>(fn: (a: A, b: B) => R): (a: A, b: B) => R {
  let lastA: A | undefined;
  let lastB: B | undefined;
  let lastR: R | undefined;
  let primed = false;
  return (a, b) => {
    if (primed && a === lastA && b === lastB) {
      return lastR as R;
    }
    primed = true;
    lastA = a;
    lastB = b;
    lastR = fn(a, b);
    return lastR;
  };
}

const allGames = memo2((byId: ReadonlyMap<string, Game>, ids: string[]): Game[] =>
  ids.flatMap((id) => (byId.has(id) ? [byId.get(id) as Game] : [])),
);

const filteredGames = memo2((games: Game[], f: GamesFilters): Game[] => {
  const q = f.search.trim().toLowerCase();
  const out = games.filter(
    (g) =>
      (!f.category || g.category.some((c) => categoryKey(c) === f.category)) &&
      (!f.installedOnly || g.installed) &&
      (q.length === 0 || g.title.toLowerCase().includes(q) || g.tags.some((t) => t.toLowerCase().includes(q))),
  );
  if (f.sort === 'title') {
    out.sort((a, b) => a.title.localeCompare(b.title));
  } else if (f.sort === 'lastPlayed') {
    out.sort((a, b) => Date.parse(b.lastPlayedAt ?? '1970-01-01') - Date.parse(a.lastPlayedAt ?? '1970-01-01'));
  } else {
    out.sort((a, b) => b.popularity - a.popularity);
  }
  return out;
});

const recentGames = memo1((games: Game[]): Game[] =>
  games
    .filter((g) => g.lastPlayedAt)
    .sort((a, b) => Date.parse(b.lastPlayedAt ?? '') - Date.parse(a.lastPlayedAt ?? ''))
    .slice(0, 8),
);

const featuredGames = memo1((games: Game[]): Game[] => games.filter((g) => g.installed).slice(0, 6));

/** All catalogue games in server order. */
export const selectAllGames = (s: GamesStore): Game[] => allGames(s.byId, s.ids);
/** Games matching the current filters. */
export const selectFilteredGames = (s: GamesStore): Game[] => filteredGames(selectAllGames(s), s.filters);
/** Recently played games (≤ 8). */
export const selectRecentGames = (s: GamesStore): Game[] => recentGames(selectAllGames(s));
/** Installed top-popularity games for the desktop hero row. */
export const selectFeaturedGames = (s: GamesStore): Game[] => featuredGames(selectAllGames(s));
/** The selected game, if any. */
export const selectSelectedGame = (s: GamesStore): Game | null =>
  s.selectedId ? (s.byId.get(s.selectedId) ?? null) : null;
/** The first running game (kiosk is single-game), if any. */
export const selectRunningGame = (s: GamesStore): RunningGame | null => s.running[0] ?? null;
/** `true` while any game process is alive or launching. */
export const selectGameMode = (s: GamesStore): boolean => s.running.length > 0 || s.launching !== null;
/** Selector factory: one game by id. */
export const selectGame =
  (gameId: string) =>
  (s: GamesStore): Game | null =>
    s.byId.get(gameId) ?? null;
