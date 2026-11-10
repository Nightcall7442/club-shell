/**
 * `session.quote` with a short cache: a tariff grid, a buy dialog and its minute chips ask for the same few prices, and
 * a price changes only with the day rate, a happy hour or the player. Answers live a minute; a failure is not kept, so
 * the next screen asks again (it shows the list price as approximate meanwhile). Cleared when the player changes.
 */
import type { SessionQuote } from '@clubshell/contracts';
import { api } from './tauri';

const TTL_MS = 60_000;

interface Entry {
  at: number;
  promise: Promise<SessionQuote>;
}

const cache = new Map<string, Entry>();

const keyOf = (tariffId: string, minutes: number | null): string => `${tariffId}|${minutes ?? ''}`;

/** The server's price of `minutes` (`null` = one hour, or the package) on `tariffId` for the signed-in player here. */
export function fetchQuote(tariffId: string, minutes: number | null): Promise<SessionQuote> {
  const key = keyOf(tariffId, minutes);
  const hit = cache.get(key);
  if (hit && Date.now() - hit.at < TTL_MS) {
    return hit.promise;
  }
  const promise = api.session.quote({ tariffId, minutes });
  const entry: Entry = { at: Date.now(), promise };
  cache.set(key, entry);
  promise.catch(() => {
    if (cache.get(key) === entry) {
      cache.delete(key);
    }
  });
  return promise;
}

/** Forgets every quote (another player, a new day rate). */
export function clearQuoteCache(): void {
  cache.clear();
}
