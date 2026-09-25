/**
 * The owner's club set-up from the admin console reaching the player screen: name, accent colour, home banner and house
 * rules (`ShellSettings.club`, written into shell.json by the Agent from the server config). In mock mode `?club=demo`
 * starts with `DEMO_CLUB` (apps/shell/src/mocks/data.ts); without it the stock look must stay untouched.
 */
import { expect, test, type Page } from '@playwright/test';

const CLUB_NAME = 'CyberArena Tashkent';
/** `#FF8A3D` as the `r g b` triplet the theme writes into `--c-accent`. */
const CLUB_ACCENT = '255 138 61';

async function accent(page: Page): Promise<string> {
  return page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--c-accent').trim());
}

test('without a club block the lock screen keeps the stock name and accent', async ({ page }) => {
  await page.goto('/#/lock');
  await expect(page.getByText(CLUB_NAME)).toHaveCount(0);
  await expect.poll(() => accent(page)).not.toBe(CLUB_ACCENT);
});

test('the lock screen shows the club name and accent from the admin console', async ({ page }) => {
  await page.goto('/?club=demo#/lock');
  await expect(page.getByText(CLUB_NAME).first()).toBeVisible();
  await expect.poll(() => accent(page)).toBe(CLUB_ACCENT);
});

test('the home screen carries the club name and the owner banner', async ({ page }) => {
  await page.goto('/?club=demo&mock=auth#/home');
  await expect(page.getByRole('banner').getByText(CLUB_NAME)).toBeVisible();
  const banner = page.getByText('Ночной пакет: 5 часов за 40 000 сум');
  await banner.scrollIntoViewIfNeeded();
  await expect(banner).toBeVisible();
});

test('the support screen lists the owner rules in the UI language', async ({ page }) => {
  await page.goto('/?club=demo&mock=auth#/support');
  await expect(page.getByText('Читы запрещены.', { exact: true })).toBeVisible();
  // The owner's list replaces the bundled one rather than adding to it.
  await expect(page.getByText('Уважайте других игроков и персонал.')).toHaveCount(0);
});
