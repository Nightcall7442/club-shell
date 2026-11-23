/**
 * What the player has running — catalogue games (`games_running`) and apps (`apps_running`) — for the «Запущено» strip
 * on Home, the Apps screen and the in-game HUD, and the two things the strip does with one: bring it to the front and
 * close it. Reads through `api` only (the HUD's overlay webview has no session stores). Polls every
 * {@link RUNNING_POLL_MS} while the page is visible, at once on `game.stateChanged` and after a launch
 * ({@link notifyProgramsChanged}).
 */
import { useCallback, useSyncExternalStore } from 'react';
import type { App, RunningApp, RunningGame } from '@clubshell/contracts';
import { log } from '@/lib/logger';
import { api, events, isShellApiError } from '@/lib/tauri';
import { pickWindow } from '@/screens/Desktop/dock';

export const RUNNING_POLL_MS = 3000;

/** Window event after an `apps_launch` / a close: the strips refresh without waiting for the next poll. */
const CHANGED_EVENT = 'clubshell:programs-changed';

export function notifyProgramsChanged(): void {
  if (typeof window !== 'undefined') {
    window.dispatchEvent(new Event(CHANGED_EVENT));
  }
}

/** One running game or app. */
export interface RunningProgram {
  /** `game:<id>` / `app:<id>`. */
  key: string;
  kind: 'game' | 'app';
  /** Game or app id. */
  id: string;
  title: string;
  /** Every process the Agent knows of; the first is the main one. */
  pids: number[];
  /** App icon from the catalogue (resolved through `assetUrl`). */
  iconUrl: string | null;
  /** The game is still starting: nothing to switch to yet. */
  starting: boolean;
}

/** Games first (the kiosk runs one at a time), then apps oldest first. */
export function toPrograms(
  games: readonly RunningGame[],
  apps: readonly RunningApp[],
  catalogue: readonly App[],
): RunningProgram[] {
  const icons = new Map(catalogue.map((a) => [a.id, a.iconUrl || null]));
  return [
    ...games.map(
      (g): RunningProgram => ({
        key: `game:${g.gameId}`,
        kind: 'game',
        id: g.gameId,
        title: g.title,
        pids: [g.pid],
        iconUrl: null,
        starting: g.state === 'launching',
      }),
    ),
    ...apps.map(
      (a): RunningProgram => ({
        key: `app:${a.appId}`,
        kind: 'app',
        id: a.appId,
        title: a.title,
        pids: [a.pid, ...a.pids.filter((p) => p !== a.pid)],
        iconUrl: icons.get(a.appId) ?? null,
        starting: false,
      }),
    ),
  ];
}

/** The apps catalogue for the icons, read once per page. */
let catalogue: Promise<App[]> | null = null;
function appsCatalogue(): Promise<App[]> {
  catalogue ??= api.apps.list().catch((e: unknown) => {
    log.debug('apps_list for the running strip failed', e);
    catalogue = null;
    return [];
  });
  return catalogue;
}

// One poller per page, shared by every strip and tile on it (the Apps screen has both): each `apps_running` has the
// Agent walk the session's processes, so two consumers must not mean two polls.
let current: RunningProgram[] = [];
const listeners = new Set<() => void>();
let stopPoller: (() => void) | null = null;
let pollNow: (() => void) | null = null;

function startPoller(): () => void {
  let alive = true;
  let timer: number | undefined;
  let inFlight = false;
  let again = false;
  const poll = async (): Promise<void> => {
    if (inFlight) {
      again = true;
      return;
    }
    inFlight = true;
    window.clearTimeout(timer);
    if (!document.hidden) {
      const [games, apps, list] = await Promise.all([
        api.games.running().catch((e: unknown) => {
          log.debug('games_running failed', e);
          return null;
        }),
        api.apps.running().catch((e: unknown) => {
          log.debug('apps_running failed', e);
          return null;
        }),
        appsCatalogue(),
      ]);
      if (alive && games !== null && apps !== null) {
        const next = toPrograms(games, apps, list);
        if (!samePrograms(current, next)) {
          current = next;
          listeners.forEach((l) => l());
        }
      }
    }
    inFlight = false;
    if (alive) {
      if (again) {
        again = false;
        void poll();
      } else {
        timer = window.setTimeout(() => void poll(), RUNNING_POLL_MS);
      }
    }
  };
  const nudge = (): void => void poll();
  pollNow = nudge;
  document.addEventListener('visibilitychange', nudge);
  window.addEventListener(CHANGED_EVENT, nudge);
  const off = events.on('game.stateChanged', nudge);
  void poll();
  return () => {
    alive = false;
    window.clearTimeout(timer);
    document.removeEventListener('visibilitychange', nudge);
    window.removeEventListener(CHANGED_EVENT, nudge);
    off();
    pollNow = null;
  };
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  stopPoller ??= startPoller();
  return () => {
    listeners.delete(listener);
    if (listeners.size === 0 && stopPoller) {
      stopPoller();
      stopPoller = null;
    }
  };
}

export function useRunningPrograms(): { programs: RunningProgram[]; refresh: () => void } {
  const programs = useSyncExternalStore(subscribe, () => current);
  const refresh = useCallback(() => pollNow?.(), []);
  return { programs, refresh };
}

function samePrograms(a: readonly RunningProgram[], b: readonly RunningProgram[]): boolean {
  return (
    a.length === b.length &&
    a.every((p, i) => {
      const o = b[i];
      return (
        o !== undefined &&
        p.key === o.key &&
        p.title === o.title &&
        p.iconUrl === o.iconUrl &&
        p.starting === o.starting &&
        p.pids.join() === o.pids.join()
      );
    })
  );
}

/**
 * Brings `program` to the front; the Shell goes behind it until the player comes back. An app goes through
 * `apps_launch`, which brings a running app's window forward — and starts an app that lives in the tray again, so it
 * shows one. A game's window is found among the open programs (its pid, else its install folder). `false` when it
 * could not be brought forward.
 */
export async function focusProgram(program: RunningProgram): Promise<boolean> {
  if (program.kind === 'app') {
    await api.apps.launch(program.id);
    return true;
  }
  const windows = await api.kiosk.openWindows();
  let target = pickWindow(windows, program.pids);
  if (!target) {
    const game = await api.games.get(program.id).catch(() => null);
    target = pickWindow(windows, program.pids, game?.installPath ?? null);
  }
  if (!target) {
    return false;
  }
  try {
    return await api.kiosk.focusWindow(target.hwnd);
  } catch (e) {
    if (isShellApiError(e) && e.code === 'notFound') {
      return false;
    }
    throw e;
  }
}

/** Closes `program`: a game through `games_kill`, an app through `apps_kill` (its windows first, then the rest). */
export async function closeProgram(program: RunningProgram): Promise<void> {
  try {
    if (program.kind === 'game') {
      await api.games.kill({ gameId: program.id });
    } else {
      await api.apps.kill(program.id);
    }
  } catch (e) {
    // Gone already: nothing left to close.
    if (!(isShellApiError(e) && e.code === 'notFound')) {
      throw e;
    }
  } finally {
    notifyProgramsChanged();
  }
}
