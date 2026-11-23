/**
 * The player's starred games and apps (D-86): `ShellSettings.favorites` holds them as `game:<id>` / `app:<id>`, newest
 * first, on the member's account (the Agent saves them to the server), so the same row greets them on any PC.
 */
import type { App, Game } from '@clubshell/contracts';

export type FavoriteKind = 'game' | 'app';

/** Most favourites one account keeps (the server refuses more). */
export const MAX_FAVORITES = 60;

/** The stored key of a game or an app. */
export function favoriteKey(kind: FavoriteKind, id: string): string {
  return `${kind}:${id}`;
}

/** The kind and id of a stored key; `null` for one this Shell does not know (a newer Shell may star other things). */
export function parseFavoriteKey(key: string): { kind: FavoriteKind; id: string } | null {
  const at = key.indexOf(':');
  const kind = key.slice(0, at);
  const id = key.slice(at + 1);
  return at > 0 && id.length > 0 && (kind === 'game' || kind === 'app') ? { kind, id } : null;
}

/**
 * The list with `key` starred (in front: the newest is the first on Home) or unstarred. Starring past
 * {@link MAX_FAVORITES} drops the oldest: the player's latest choice always counts.
 */
export function toggleFavorite(list: readonly string[], key: string): string[] {
  if (list.includes(key)) {
    return list.filter((k) => k !== key);
  }
  return [key, ...list].slice(0, MAX_FAVORITES);
}

export type FavoriteItem = { kind: 'game'; key: string; game: Game } | { kind: 'app'; key: string; app: App };

/**
 * The starred games and apps this PC has, in the stored order. A game missing from this PC's catalogue and an app the
 * club does not allow here are left out (they stay starred for the PCs that have them).
 */
export function resolveFavorites(
  keys: readonly string[],
  games: readonly Game[],
  apps: readonly App[],
): FavoriteItem[] {
  const gameById = new Map(games.map((g) => [g.id, g]));
  const appById = new Map(apps.map((a) => [a.id, a]));
  const items: FavoriteItem[] = [];
  for (const key of keys) {
    const parsed = parseFavoriteKey(key);
    if (parsed?.kind === 'game') {
      const game = gameById.get(parsed.id);
      if (game) {
        items.push({ kind: 'game', key, game });
      }
    } else if (parsed?.kind === 'app') {
      const app = appById.get(parsed.id);
      if (app?.allowed) {
        items.push({ kind: 'app', key, app });
      }
    }
  }
  return items;
}
