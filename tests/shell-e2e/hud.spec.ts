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

test.describe('in-game HUD', () => {
  test('shows time, balance and actions; +30 min extends the session and charges the wallet', async ({ page }) => {
    const t = await showHud(page);
    const hud = page.getByRole('dialog', { name: t('kiosk.hudTitle') });
    await expect(hud).toBeVisible();
    await expect(hud.getByText(t('session.timeLeft'))).toBeVisible();
    await expect(hud.getByText(t('desktop.balance'))).toBeVisible();

    const extend = hud.getByRole('button', { name: t('kiosk.hudExtend') });
    await expect(extend).toBeEnabled();
    await expect(extend).toBeFocused();
    const before = await hud.locator('.tnum').first().textContent();
    await extend.click();
    await expect(page.getByText(t('session.extended', { minutes: 30 }))).toBeVisible();
    await expect.poll(async () => hud.locator('.tnum').first().textContent()).not.toBe(before);
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
