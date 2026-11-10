/**
 * In-game HUD (`/#/overlay` + `kiosk://overlay { kind: "hud" }`) against the Vite dev server in mock mode. The
 * native hotkey and window are Rust-side; here the overlay route is driven through `window.__clubshellMock.emit`.
 * The overlay window has no language switch and renders in the persisted locale, so strings come from `<html lang>`.
 */
import { readFileSync } from 'node:fs';
import { expect, test, type Page } from '@playwright/test';

type Strings = Record<string, unknown>;
type Translate = (key: string, vars?: Record<string, string | number>) => string;

function loadStrings(locale: string): Strings {
  const url = new URL(`../../apps/shell/src/i18n/${locale}.json`, import.meta.url);
  return JSON.parse(readFileSync(url, 'utf8')) as Strings;
}

function translator(bundle: Strings): Translate {
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

function emitHud(page: Page): Promise<void> {
  return page.evaluate(() =>
    (window as unknown as { __clubshellMock: { emit: (e: string, p: unknown) => void } }).__clubshellMock.emit(
      'kiosk://overlay',
      { kind: 'hud' },
    ),
  );
}

/** The mock installs before the overlay route subscribes, so the first emits can land on nobody: retry until it shows. */
async function showHud(page: Page): Promise<Translate> {
  await page.goto('/?mock=auth#/overlay');
  await page.waitForFunction(() => Boolean((window as unknown as { __clubshellMock?: unknown }).__clubshellMock));
  const lang = (await page.locator('html').getAttribute('lang')) ?? 'ru';
  const dialog = page.getByRole('dialog');
  await expect
    .poll(
      async () => {
        await emitHud(page);
        await page.waitForTimeout(250);
        return dialog.isVisible();
      },
      { timeout: 15_000 },
    )
    .toBe(true);
  return translator(loadStrings(lang));
}

function simulate(page: Page, name: string): Promise<void> {
  return page.evaluate(
    (fn) =>
      (window as unknown as { __clubshellMock: { simulate: Record<string, () => void> } }).__clubshellMock.simulate[
        fn
      ]?.(),
    name,
  );
}

/** 6 000 UZS (Standard, 12 000 an hour, for 30 minutes) in any locale's grouping. */
const HALF_HOUR_PRICE = /6[\s,.  ]?000/;

test.describe('in-game HUD', () => {
  test('shows time, balance and actions; +30 min shows its price, asks, then extends and charges', async ({ page }) => {
    const t = await showHud(page);
    const hud = page.getByRole('dialog', { name: t('kiosk.hudTitle') });
    await expect(hud).toBeVisible();
    await expect(hud.getByText(t('session.timeLeft'))).toBeVisible();
    await expect(hud.getByText(t('desktop.balance'))).toBeVisible();

    const extend = hud.getByRole('button', { name: t('kiosk.hudExtend') });
    await expect(extend).toBeEnabled();
    await expect(extend).toBeFocused();
    // The server's price is on the button before anything is charged.
    await expect(extend).toContainText(HALF_HOUR_PRICE);
    const before = await hud.locator('.tnum').first().textContent();
    await extend.click();

    // Nothing charged yet: a confirm with the price.
    const confirm = hud.getByRole('group', { name: t('kiosk.hudExtend') });
    await expect(confirm).toBeVisible();
    await expect(confirm).toContainText(HALF_HOUR_PRICE);
    expect(await hud.locator('.tnum').first().textContent()).toBe(before);
    // Cancel keeps everything as it was …
    await confirm.getByRole('button', { name: t('common.cancel') }).click();
    await expect(confirm).toHaveCount(0);
    await expect(page.getByText(t('session.extended', { minutes: 30 }))).toHaveCount(0);

    // … confirm extends.
    await extend.click();
    await confirm.getByRole('button', { name: t('common.confirm') }).click();
    await expect(page.getByText(t('session.extended', { minutes: 30 }))).toBeVisible();
    await expect.poll(async () => hud.locator('.tnum').first().textContent()).not.toBe(before);
  });

  test('+30 min is not offered on a package session', async ({ page }) => {
    const t = await showHud(page);
    const hud = page.getByRole('dialog', { name: t('kiosk.hudTitle') });
    await expect(hud.getByRole('button', { name: t('kiosk.hudExtend') })).toBeVisible();
    await simulate(page, 'packageSession');
    await expect(hud.getByRole('button', { name: t('kiosk.hudExtend') })).toHaveCount(0);
    // The rest of the panel stays.
    await expect(hud.getByRole('button', { name: t('desktop.callAdmin') })).toBeVisible();
  });

  test('Escape and a click outside hide it', async ({ page }) => {
    const t = await showHud(page);
    const hud = page.getByRole('dialog', { name: t('kiosk.hudTitle') });
    await expect(hud).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(hud).toBeHidden();

    await emitHud(page);
    await expect(hud).toBeVisible();
    await page.mouse.click(200, 200);
    await expect(hud).toBeHidden();
  });
});
