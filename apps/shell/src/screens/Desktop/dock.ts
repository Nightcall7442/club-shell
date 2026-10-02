/**
 * Model of the dock in the status bar: the programs open in the player's session (`kiosk_open_windows`) → icons, a
 * running catalogue game shown by its cover, in a stable order so the icons do not jump around as the windows change
 * Z order. Pure (type-only imports), so `dock.test.ts` runs it under `node --test`.
 */
import type { Game, RunningGame } from '@clubshell/contracts';
import type { OpenWindow } from '@/lib/tauri';

/** One icon of the dock. */
export interface DockItem {
  /** React key, stable while the program runs: the game id for a game (its window may change), else the pid. */
  key: string;
  pid: number;
  /** Window raised on activation. */
  hwnd: number;
  /** Tooltip and accessible name: the game's title, else the window's. */
  title: string;
  /** Running catalogue game behind the window. */
  gameId: string | null;
  /** That game's cover (URL or ProgramData-relative path, resolved through `assetUrl`). */
  cover: string | null;
  /** Exe icon (`data:image/png`), the fallback for a game without a cover. */
  icon: string | null;
}

/** `C:/Games/CS2/` → `c:\games\cs2`: one spelling for comparing Windows paths. */
export function normalizePath(path: string): string {
  return path.trim().replace(/\//g, '\\').replace(/\\+$/, '').toLowerCase();
}

const ABSOLUTE = /^([a-z]:\\|\\\\)/;

/** Full exe of a game: `exePath` when absolute, else under `installPath`. */
function gameExe(game: Game): string | null {
  const exe = game.exePath ? normalizePath(game.exePath) : '';
  if (!exe) {
    return null;
  }
  if (ABSOLUTE.test(exe)) {
    return exe;
  }
  const dir = game.installPath ? normalizePath(game.installPath) : '';
  return dir ? `${dir}\\${exe.replace(/^\.?\\+/, '')}` : null;
}

/**
 * The running game `window` belongs to: the pid the Agent tracks, the game's exe, or any exe under its install
 * directory (launchers hand over to the real game process, so the pid alone misses it).
 */
export function matchRunningGame(
  window: OpenWindow,
  running: readonly RunningGame[],
  games: ReadonlyMap<string, Game>,
): RunningGame | null {
  const byPid = running.find((r) => r.pid === window.pid);
  if (byPid) {
    return byPid;
  }
  const exe = normalizePath(window.exePath);
  if (!exe) {
    return null;
  }
  return (
    running.find((r) => {
      const game = games.get(r.gameId);
      if (!game) {
        return false;
      }
      const dir = game.installPath ? normalizePath(game.installPath) : '';
      return gameExe(game) === exe || (dir !== '' && exe.startsWith(`${dir}\\`));
    }) ?? null
  );
}

/**
 * Dock icons for `windows` (front-most first): the running game first, then every other program where it was in the
 * previous render (`order`: its keys), newcomers after them. One icon per game even when several of its processes
 * have windows (launcher and game).
 */
export function toDockItems(
  windows: readonly OpenWindow[],
  running: readonly RunningGame[],
  games: ReadonlyMap<string, Game>,
  order: readonly string[] = [],
): DockItem[] {
  const items: DockItem[] = [];
  const keys = new Set<string>();
  for (const w of windows) {
    const run = matchRunningGame(w, running, games);
    const game = run ? games.get(run.gameId) : undefined;
    const key = run ? `game:${run.gameId}` : `pid:${w.pid}`;
    if (keys.has(key)) {
      continue;
    }
    keys.add(key);
    items.push({
      key,
      pid: w.pid,
      hwnd: w.hwnd,
      title: game?.title || run?.title || w.title,
      gameId: run?.gameId ?? null,
      cover: game?.coverUrl || null,
      icon: w.icon,
    });
  }
  const rank = (item: DockItem, arrival: number): number => {
    const known = order.indexOf(item.key);
    return known >= 0 ? known : order.length + arrival;
  };
  return items
    .map((item, arrival) => ({ item, rank: rank(item, arrival) }))
    .sort((a, b) => Number(b.item.gameId !== null) - Number(a.item.gameId !== null) || a.rank - b.rank)
    .map(({ item }) => item);
}

/** `true` when two listings would render the same dock (lets a poll keep the previous array and skip a render). */
export function sameWindows(a: readonly OpenWindow[], b: readonly OpenWindow[]): boolean {
  return (
    a.length === b.length &&
    a.every((w, i) => {
      const o = b[i];
      return (
        o !== undefined &&
        w.pid === o.pid &&
        w.hwnd === o.hwnd &&
        w.title === o.title &&
        w.exePath === o.exePath &&
        w.icon === o.icon
      );
    })
  );
}
