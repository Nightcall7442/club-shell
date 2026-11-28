/**
 * Low-time warnings (D-109) against the Vite dev server in mock mode: the mock Agent warns at 10, 7 and 5 minutes left
 * (and the last minute), once each, as the real Agent and server do. `simulate.timeLeft(seconds)` moves the prepaid time
 * left, so the next tick of the mock's one-second clock crosses the mark. On the desktop the Shell's own toast shows;
 * over a game the native overlay window (`/#/overlay`, `kiosk://overlay { kind: "message" }`, which the mock emits as
 * the Rust kiosk does while a game runs) shows the passive toast. Screenshots go to `test-results/low-time-*.png`.
 */
import { readFileSync } from 'node:fs';
import { expect, test, type Page } from '@playwright/test';

type Strings = Record<string, unknown>;
type Translate = (key: string, vars?: Record<string, string | number>) => string;

function loadStrings(locale: string): Strings {
  const url = new URL(`../../apps/shell/src/i18n/${locale}.json`, import.meta.url);
  return JSON.parse(readFileSync(url, 'utf8')) as Strings;
}

/** `t()` over a raw bundle with i18next's plural keys (`_one`, `_few`, `_many`, `_other`) for `count`. */
function translator(bundle: Strings, locale: string): Translate {
  const plural = new Intl.PluralRules(locale);
  const lookup = (key: string): unknown =>
    key
      .split('.')
      .reduce<unknown>(
        (node, part) => (typeof node === 'object' && node !== null ? (node as Strings)[part] : undefined),
        bundle,
      );
  return (key, vars = {}) => {
    let value = lookup(key);
    if (value === undefined && typeof vars['count'] === 'number') {
      value = lookup(`${key}_${plural.select(vars['count'])}`) ?? lookup(`${key}_other`);
    }
    if (typeof value !== 'string') {
      throw new Error(`missing i18n key: ${key}`);
    }
    return value.replace(/\{\{\s*(\w+)\s*\}\}/g, (_, name: string) => String(vars[name] ?? ''));
  };
}

async function mockReady(page: Page): Promise<void> {
  await page.waitForFunction(() => Boolean((window as unknown as { __clubshellMock?: unknown }).__clubshellMock));
}

async function simulate(page: Page, name: string, ...args: unknown[]): Promise<void> {
  await mockReady(page);
  await page.evaluate(
    ([fn, rest]) =>
      (
        window as unknown as { __clubshellMock: { simulate: Record<string, (...a: unknown[]) => void> } }
      ).__clubshellMock.simulate[fn as string]?.(...(rest as unknown[])),
    [name, args] as const,
  );
}

/** Signed in as the mock's `demo` with a prepaid hour running, on the desktop. */
async function playing(page: Page): Promise<void> {
  await expect(page).toHaveURL(/#\/home$/);
  await page.waitForFunction(
    () => (window as unknown as { __clubshellMock: { state: { session: unknown } } }).__clubshellMock.state.session,
  );
}

function warningsSent(page: Page): Promise<number[]> {
  return page.evaluate(
    () =>
      (window as unknown as { __clubshellMock: { state: { session: { warningsSent: number[] } | null } } })
        .__clubshellMock.state.session?.warningsSent ?? [],
  );
}

async function strings(page: Page): Promise<Translate> {
  const lang = (await page.locator('html').getAttribute('lang')) ?? 'ru';
  return translator(loadStrings(lang), lang);
}

const SHOTS = process.env['LOW_TIME_SHOTS'];

async function shot(page: Page, name: string): Promise<void> {
  await page.screenshot({ path: `${SHOTS ?? 'test-results'}/low-time-${name}.png` });
}

test.describe('low-time warnings (D-109)', () => {
  test('on the desktop: a toast at 10, 7 and 5 minutes left, once each, with «extend» and re-armed by an extension', async ({
    page,
  }) => {
    await page.goto('/?mock=auth#/home');
    await mockReady(page);
    await playing(page);
    const t = await strings(page);

    for (const minutes of [10, 7, 5]) {
      await simulate(page, 'timeLeft', minutes * 60 + 2);
      const toast = page.getByText(t('session.lowTime', { count: minutes }), { exact: true });
      await expect(toast).toBeVisible({ timeout: 10_000 });
      if (minutes === 10) await shot(page, 'desktop');
    }
    expect(await warningsSent(page)).toEqual([10, 7, 5]);
    await expect(page.getByRole('button', { name: t('session.addTime') }).first()).toBeVisible();

    // Ticks past a mark warn nothing twice.
    await page.waitForTimeout(2_500);
    expect(await warningsSent(page)).toEqual([10, 7, 5]);

    // An extension to 8 minutes left: 7 and 5 re-arm, 10 stays sent (the time is below it).
    await simulate(page, 'timeLeft', 8 * 60);
    expect(await warningsSent(page)).toEqual([10]);
    await simulate(page, 'timeLeft', 7 * 60 + 2);
    await expect.poll(() => warningsSent(page), { timeout: 10_000 }).toEqual([10, 7]);
    await expect(page.getByText(t('session.lowTime', { count: 7 }), { exact: true })).toBeVisible();
  });

  test('pay as you go: warned when the balance pays for 10 minutes or less (the money, not a clock)', async ({
    page,
  }) => {
    await page.goto('/?mock=auth#/home');
    await mockReady(page);
    await playing(page);
    const t = await strings(page);
    // 45 000 sum at 12 000 an hour pays hours: nothing yet.
    await simulate(page, 'payAsYouGoSession');
    await page.waitForTimeout(1_500);
    expect(await warningsSent(page)).toEqual([]);
    // The money left drops to 8 minutes beyond what the session has cost so far (Standard: 200 sum, 20 000 tiyin a
    // minute), 600 sum of it in bonuses, which pay play time too (D-106): the 10-minute mark.
    await page.evaluate(() => {
      const mock = (
        window as unknown as {
          __clubshellMock: {
            state: {
              user: { balance: { amount: number } };
              balance: { bonus: { amount: number } };
              session: { cost: { amount: number } };
            };
          };
        }
      ).__clubshellMock;
      mock.state.user.balance.amount = mock.state.session.cost.amount + 100_000;
      mock.state.balance.bonus.amount = 60_000;
    });
    await expect(page.getByText(t('session.lowTime', { count: 10 }), { exact: true })).toBeVisible({ timeout: 10_000 });
    expect(await warningsSent(page)).toEqual([10]);
  });

  test('over a game: the passive overlay toast at 10, 7 and 5 minutes with the hero number and the HUD hint', async ({
    page,
  }) => {
    await page.goto('/?mock=auth#/overlay');
    await mockReady(page);
    const t = await strings(page);
    await page.evaluate(() => {
      (
        window as unknown as { __clubshellMock: { state: { kiosk: { gameMode: boolean } } } }
      ).__clubshellMock.state.kiosk.gameMode = true;
      // A stand-in for the game behind the transparent overlay window, for the screenshot.
      document.body.style.background =
        'radial-gradient(circle at 30% 40%, #3b4a3f 0%, #1b2420 45%, #0b0f0d 100%), #0b0f0d';
    });

    for (const minutes of [10, 7, 5]) {
      await simulate(page, 'timeLeft', minutes * 60 + 2);
      const toast = page.getByRole(minutes <= 5 ? 'alert' : 'status').filter({
        hasText: t('session.lowTime', { count: minutes }),
      });
      await expect(toast).toBeVisible({ timeout: 10_000 });
      await expect(toast).toContainText(String(minutes));
      await expect(toast).toContainText(t('session.lowTimeHud'));
      if (minutes === 10) await shot(page, 'in-game');
    }
    expect(await warningsSent(page)).toEqual([10, 7, 5]);
  });
});
