/**
 * Opening and closing apps (the owner's report: an app opened behind the Shell, there was no way back, «Закрыть» did
 * not close it, and a second tap opened a second copy). Against the Vite dev server in mock mode: the mock agent keeps
 * the apps started through `apps_launch` (`state.apps`), answers a second launch with `alreadyRunning`, lists them in
 * `apps_running` and ends them in `apps_kill`. The window work itself (bringing a window in front of the Shell) is
 * Rust-side and is not exercised here. `?mock=auth` signs in `demo` / `1234` with a session.
 */
import { readFileSync } from 'node:fs';
import { expect, test, type Page } from '@playwright/test';

type Strings = Record<string, unknown>;
type Translate = (key: string, vars?: Record<string, string | number>) => string;

function translator(locale: string): Translate {
  const url = new URL(`../../apps/shell/src/i18n/${locale}.json`, import.meta.url);
  const bundle = JSON.parse(readFileSync(url, 'utf8')) as Strings;
  return (key, vars = {}) => {
    const value = key
      .split('.')
      .reduce<unknown>(
        (node, part) => (typeof node === 'object' && node !== null ? (node as Strings)[part] : undefined),
        bundle,
      );
    if (typeof value !== 'string') {
      throw new Error(`missing i18n key: ${key}`);
    }
    return value.replace(/\{\{\s*(\w+)\s*\}\}/g, (_, name: string) => String(vars[name] ?? ''));
  };
}

interface MockApps {
  apps: { appId: string; title: string; pid: number; pids: number[]; startedAt: string }[];
}

function mockApps(page: Page): Promise<MockApps['apps']> {
  return page.evaluate(() => (window as unknown as { __clubshellMock: { state: MockApps } }).__clubshellMock.state.apps);
}

async function open(page: Page, hash: string): Promise<Translate> {
  await page.goto(`/?mock=auth${hash}`);
  await page.waitForFunction(() => Boolean((window as unknown as { __clubshellMock?: unknown }).__clubshellMock));
  return translator((await page.locator('html').getAttribute('lang')) ?? 'ru');
}

function strip(page: Page, t: Translate) {
  return page.getByRole('region', { name: t('running.title') });
}

test.describe('running apps', () => {
  test('a launched app is listed as running, and a second tap switches to it instead of a second copy', async ({
    page,
  }) => {
    const t = await open(page, '#/apps');
    // How to come back is on the screen before anything is opened.
    await expect(page.getByText(t('running.backHint')).first()).toBeVisible();
    await expect(strip(page, t)).toHaveCount(0);

    await page.getByRole('button', { name: `${t('apps.launch')} Discord` }).click();
    await expect(page.getByText(t('apps.launched', { title: 'Discord' }))).toBeVisible();
    const running = strip(page, t);
    await expect(running.getByRole('listitem')).toHaveCount(1);
    await expect(running.getByRole('listitem', { name: new RegExp(`^Discord`) })).toBeVisible();

    // The tile now says it runs; tapping it again brings it forward.
    const tile = page
      .getByRole('region', { name: t('apps.category.voice') })
      .getByRole('button', { name: `${t('running.switchTo')} Discord` });
    await expect(tile).toBeVisible();
    await tile.click();
    await expect(page.getByText(t('apps.alreadyRunning', { title: 'Discord' }))).toBeVisible();
    expect(await mockApps(page)).toHaveLength(1);
    await expect(running.getByRole('listitem')).toHaveCount(1);

    // A second app joins the list.
    await page.getByRole('button', { name: `${t('apps.launch')} Google Chrome` }).click();
    await expect(running.getByRole('listitem')).toHaveCount(2);
  });

  test('«Закрыть» asks once more, then closes exactly that app', async ({ page }) => {
    const t = await open(page, '#/apps');
    await page.getByRole('button', { name: `${t('apps.launch')} Discord` }).click();
    await page.getByRole('button', { name: `${t('apps.launch')} Google Chrome` }).click();
    const running = strip(page, t);
    await expect(running.getByRole('listitem')).toHaveCount(2);

    const discord = running.getByRole('listitem', { name: /^Discord/ });
    await discord.getByRole('button', { name: `${t('running.close')} Discord` }).click();
    // Nothing closed on the first press.
    expect(await mockApps(page)).toHaveLength(2);
    await discord.getByRole('button', { name: `${t('running.confirmClose')} Discord` }).click();

    await expect(running.getByRole('listitem')).toHaveCount(1);
    await expect(running.getByRole('listitem', { name: /^Google Chrome/ })).toBeVisible();
    expect((await mockApps(page)).map((a) => a.title)).toEqual(['Google Chrome']);
    // Discord's tile is a launch tile again.
    await expect(page.getByRole('button', { name: `${t('apps.launch')} Discord` })).toBeVisible();
  });

  test('Home lists what runs, and «Перейти» brings it forward', async ({ page }) => {
    const t = await open(page, '#/apps');
    await page.getByRole('button', { name: `${t('apps.launch')} Discord` }).click();
    await expect(strip(page, t).getByRole('listitem')).toHaveCount(1);

    await page.evaluate(() => {
      location.hash = '#/home';
    });
    const running = strip(page, t);
    await expect(running.getByRole('listitem', { name: /^Discord/ })).toBeVisible();
    const switched = page.waitForEvent('console', (m) => m.text().includes('apps_launch(Discord): already running'));
    await running.getByRole('button', { name: `${t('running.switchTo')} Discord` }).click();
    await switched;
    expect(await mockApps(page)).toHaveLength(1);
    await expect(running.getByRole('listitem')).toHaveCount(1);
  });

  test('the HUD lists what runs and its × says it only hides the panel', async ({ page }) => {
    const t = await open(page, '#/overlay');
    await page.evaluate(() => {
      const mock = (window as unknown as { __clubshellMock: { state: MockApps } }).__clubshellMock;
      mock.state.apps.push({
        appId: '60000000-0000-4000-8000-000000000002',
        title: 'Discord',
        pid: 8101,
        pids: [8101],
        startedAt: new Date().toISOString(),
      });
    });
    const hud = page.getByRole('dialog', { name: t('kiosk.hudTitle') });
    await expect
      .poll(
        async () => {
          await page.evaluate(() =>
            (window as unknown as { __clubshellMock: { emit: (e: string, p: unknown) => void } }).__clubshellMock.emit(
              'kiosk://overlay',
              { kind: 'hud' },
            ),
          );
          await page.waitForTimeout(250);
          return hud.isVisible();
        },
        { timeout: 15_000 },
      )
      .toBe(true);
    await expect(hud.getByRole('region', { name: t('running.title') }).getByRole('listitem')).toHaveCount(1);
    await expect(hud.getByRole('button', { name: t('kiosk.hudHide') })).toBeVisible();
    await expect(hud.getByRole('button', { name: t('kiosk.returnToShell') })).toBeVisible();
  });

  test('a running game offers «Перейти к игре», which brings its window forward', async ({ page }) => {
    const t = await open(page, '#/home');
    const title = await page.evaluate(() => {
      const mock = (
        window as unknown as {
          __clubshellMock: {
            state: { games: { id: string; title: string; installed: boolean }[]; running: unknown[] };
            emit: (e: string, p: unknown) => void;
          };
        }
      ).__clubshellMock;
      const game = mock.state.games.find((g) => g.installed);
      if (!game) {
        throw new Error('no installed mock game');
      }
      const at = new Date().toISOString();
      mock.state.running.push({ gameId: game.id, title: game.title, pid: 4321, startedAt: at, accountLeaseId: null, state: 'running' });
      mock.emit('agent://game.stateChanged', { gameId: game.id, title: game.title, pid: 4321, state: 'running', at });
      return game.title;
    });

    await expect(strip(page, t).getByRole('listitem', { name: new RegExp(`^${title}`) })).toBeVisible();
    const switched = page.waitForEvent('console', (m) => m.text().includes(`kiosk_focus_window(${title})`));
    await page.getByRole('button', { name: t('games.switchTo') }).click();
    await switched;
  });
});
