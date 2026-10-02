/** `pnpm --filter @clubshell/shell test` (Node's own runner, which strips the types itself). */
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import type { Game, RunningGame } from '@clubshell/contracts';
import type { OpenWindow } from '@/lib/tauri';
import { matchRunningGame, normalizePath, sameWindows, toDockItems } from './dock.ts';

const win = (pid: number, title: string, exePath: string, hwnd = pid * 10): OpenWindow => ({
  pid,
  hwnd,
  title,
  exePath,
  icon: `data:image/png;base64,${pid}`,
});

// Only the fields the dock reads; the rest of a catalogue game does not matter here.
const game = (id: string, title: string, installPath: string | null, exePath: string | null): Game =>
  ({ id, title, installPath, exePath, coverUrl: `cache\\media\\${id}.jpg` }) as Game;

const run = (gameId: string, pid: number): RunningGame => ({
  gameId,
  title: `${gameId} (agent)`,
  pid,
  startedAt: '2026-10-02T10:00:00Z',
  accountLeaseId: null,
  state: 'running',
});

const CS2 = game('cs2', 'Counter-Strike 2', 'D:/SteamLibrary/steamapps/common/Counter-Strike Global Offensive/', null);
const DOTA = game('dota', 'Dota 2', 'D:\\Games\\Dota', 'game\\bin\\win64\\dota2.exe');
const GAMES = new Map([CS2, DOTA].map((g) => [g.id, g]));

const DISCORD = win(7, 'Discord', 'C:\\Users\\club\\AppData\\Local\\Discord\\app-1.0\\Discord.exe');
const CHROME = win(8, 'News - Google Chrome', 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe');
const CS2_WINDOW = win(
  20,
  'Counter-Strike 2',
  'd:\\steamlibrary\\steamapps\\common\\counter-strike global offensive\\game\\bin\\win64\\cs2.exe',
);

describe('normalizePath', () => {
  it('folds case, separators and a trailing slash', () => {
    assert.equal(normalizePath(' C:/Games/CS2/ '), 'c:\\games\\cs2');
    assert.equal(normalizePath('D:\\Games\\Dota\\\\'), 'd:\\games\\dota');
  });
});

describe('matchRunningGame', () => {
  it('matches the pid the Agent tracks first', () => {
    assert.equal(matchRunningGame(win(42, 'x', ''), [run('dota', 42)], GAMES)?.gameId, 'dota');
  });

  it('matches a launcher-spawned process under the install directory', () => {
    assert.equal(matchRunningGame(CS2_WINDOW, [run('cs2', 5)], GAMES)?.gameId, 'cs2');
  });

  it('matches the exe relative to the install path', () => {
    const dota = win(31, 'Dota 2', 'D:/GAMES/dota/game/bin/win64/dota2.exe');
    assert.equal(matchRunningGame(dota, [run('dota', 30)], GAMES)?.gameId, 'dota');
  });

  it('leaves other programs, sibling folders and stopped games alone', () => {
    assert.equal(matchRunningGame(DISCORD, [run('cs2', 5)], GAMES), null);
    assert.equal(matchRunningGame(win(9, 'x', 'D:\\Games\\Dota 2 Tools\\tool.exe'), [run('dota', 5)], GAMES), null);
    assert.equal(matchRunningGame(CS2_WINDOW, [], GAMES), null);
    assert.equal(matchRunningGame(CS2_WINDOW, [run('unknown', 5)], GAMES), null);
  });
});

describe('toDockItems', () => {
  it('puts the running game first with its catalogue title and cover', () => {
    const items = toDockItems([DISCORD, CS2_WINDOW, CHROME], [run('cs2', 5)], GAMES);
    assert.deepEqual(
      items.map((i) => i.key),
      ['game:cs2', 'pid:7', 'pid:8'],
    );
    assert.deepEqual(items[0], {
      key: 'game:cs2',
      pid: 20,
      hwnd: 200,
      title: 'Counter-Strike 2',
      gameId: 'cs2',
      cover: 'cache\\media\\cs2.jpg',
      icon: 'data:image/png;base64,20',
    });
    assert.equal(items[1]?.title, 'Discord');
    assert.equal(items[1]?.cover, null);
  });

  it('keeps the previous order when the windows change Z order, newcomers last', () => {
    const order = ['pid:8', 'pid:7'];
    const steam = win(9, 'Steam', 'C:\\Program Files (x86)\\Steam\\steam.exe');
    const items = toDockItems([steam, DISCORD, CHROME], [], GAMES, order);
    assert.deepEqual(
      items.map((i) => i.key),
      ['pid:8', 'pid:7', 'pid:9'],
    );
  });

  it('shows one icon per game when its launcher has a window too', () => {
    const launcher = win(21, 'Launcher', 'D:\\SteamLibrary\\steamapps\\common\\Counter-Strike Global Offensive\\l.exe');
    const items = toDockItems([CS2_WINDOW, launcher], [run('cs2', 5)], GAMES);
    assert.deepEqual(
      items.map((i) => [i.key, i.pid]),
      [['game:cs2', 20]],
    );
  });

  it('falls back to the Agent title without a catalogue entry', () => {
    const items = toDockItems([win(5, 'GAME_WINDOW', '')], [run('missing', 5)], GAMES);
    assert.equal(items[0]?.title, 'missing (agent)');
    assert.equal(items[0]?.cover, null);
  });

  it('is empty when nothing is open', () => {
    assert.deepEqual(toDockItems([], [run('cs2', 5)], GAMES), []);
  });
});

describe('sameWindows', () => {
  it('compares what the dock renders', () => {
    assert.ok(sameWindows([DISCORD, CHROME], [{ ...DISCORD }, { ...CHROME }]));
    assert.ok(!sameWindows([DISCORD, CHROME], [CHROME, DISCORD]));
    assert.ok(!sameWindows([DISCORD], [{ ...DISCORD, title: 'Discord | #general' }]));
    assert.ok(!sameWindows([DISCORD], []));
  });
});
