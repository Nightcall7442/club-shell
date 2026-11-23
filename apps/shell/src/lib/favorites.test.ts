/** `pnpm --filter @clubshell/shell test` (Node's own runner, which strips the types itself). */
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import type { App, Game } from '@clubshell/contracts';
import { favoriteKey, MAX_FAVORITES, parseFavoriteKey, resolveFavorites, toggleFavorite } from './favorites.ts';

// Only the fields the helpers read.
const game = (id: string): Game => ({ id, title: id }) as Game;
const app = (id: string, allowed = true): App => ({ id, title: id, allowed }) as App;

describe('favorites', () => {
  it('keys a game and an app apart and reads them back', () => {
    assert.equal(favoriteKey('game', 'g1'), 'game:g1');
    assert.deepEqual(parseFavoriteKey('app:discord'), { kind: 'app', id: 'discord' });
    assert.equal(parseFavoriteKey('song:1'), null);
    assert.equal(parseFavoriteKey('game:'), null);
    assert.equal(parseFavoriteKey('nokind'), null);
  });

  it('stars in front, unstars, and keeps the newest when full', () => {
    assert.deepEqual(toggleFavorite(['game:a'], 'app:b'), ['app:b', 'game:a']);
    assert.deepEqual(toggleFavorite(['app:b', 'game:a'], 'app:b'), ['game:a']);
    const full = Array.from({ length: MAX_FAVORITES }, (_, i) => `game:${i}`);
    const next = toggleFavorite(full, 'game:new');
    assert.equal(next.length, MAX_FAVORITES);
    assert.equal(next[0], 'game:new');
    assert.ok(!next.includes(`game:${MAX_FAVORITES - 1}`));
  });

  it('resolves what this PC has, in order, without disallowed apps', () => {
    const items = resolveFavorites(
      ['app:discord', 'game:g2', 'game:gone', 'app:blocked', 'game:g1'],
      [game('g1'), game('g2')],
      [app('discord'), app('blocked', false)],
    );
    assert.deepEqual(
      items.map((i) => i.key),
      ['app:discord', 'game:g2', 'game:g1'],
    );
  });
});
