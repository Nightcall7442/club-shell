/**
 * "Компьютер и настройки" on Home and the PC data the top bar keeps on every screen, in mock mode (demo PC:
 * PC-12 in the Standard zone, NVIDIA GeForce RTX 4070, primary monitor at 240 Гц of 60–240, a second one at 144 Гц,
 * Windows 11 Pro on C:, volume 60; `sys.metrics` reports CPU ~47 °C — apps/shell/src/mocks/data.ts). At 1920 px the
 * mock's nine section tabs make the vitals chip keep one reading: the group's text still holds all of them.
 */
import { expect, test, type Locator, type Page } from '@playwright/test';

test.use({ viewport: { width: 1920, height: 1080 } });

function topBar(page: Page): Locator {
  return page.getByRole('banner');
}

/** The PC line under the club name (`PC-12 · Standard · RTX 4070 · 240 Гц`); caps come from CSS, hence `i`. */
function pcBadge(page: Page): Locator {
  return topBar(page).getByRole('button', { name: /^PC-12 · /i });
}

/** The sound-and-language button, which shows the volume. */
function soundButton(page: Page): Locator {
  return topBar(page).locator('[data-popover-trigger]');
}

test('the home screen shows this PC and opens its full specs', async ({ page }) => {
  await page.goto('/?mock=auth#/home');
  await expect(page.getByRole('heading', { level: 2, name: 'Компьютер и настройки' })).toBeVisible();

  const pc = page.getByRole('region', { name: 'Мой компьютер', exact: true });
  await expect(pc.getByText('PC-12', { exact: true })).toBeVisible();
  await expect(pc.getByText('NVIDIA GeForce RTX 4070', { exact: true })).toBeVisible();
  await expect(pc.getByText('240 Гц', { exact: true })).toBeVisible();

  await pc.getByRole('button', { name: 'Все характеристики', exact: true }).click();
  const specs = page.getByRole('dialog', { name: 'Мой компьютер' });
  await expect(specs).toBeVisible();
  await expect(specs.getByText('C:', { exact: true })).toBeVisible();
  await expect(specs.getByText('Windows 11 Pro', { exact: true })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(specs).toHaveCount(0);
});

for (const route of ['/home', '/games', '/wallet', '/profile']) {
  test(`the top bar keeps the PC, its vitals and the volume on ${route}`, async ({ page }) => {
    await page.goto(`/?mock=auth#${route}`);
    await expect(pcBadge(page)).toHaveAccessibleName(/^PC-12 · Standard · RTX 4070 · 240 Гц$/i);
    const vitals = topBar(page).getByRole('group', { name: 'Состояние ПК' });
    await expect(vitals).toContainText('ЦП');
    // A temperature, never the 0° a missing sensor reports.
    await expect(vitals).toContainText(/ЦП\s*[1-9]\d*°/);
    await expect(soundButton(page)).toHaveText('60');
  });
}

test('the volume slider on Home moves the number in the top bar', async ({ page }) => {
  await page.goto('/?mock=auth#/home');
  await expect(soundButton(page)).toHaveText('60');

  const slider = page
    .getByRole('region', { name: 'Звук', exact: true })
    .getByRole('slider', { name: 'Громкость', exact: true });
  await slider.focus();
  for (let i = 0; i < 5; i += 1) {
    await slider.press('ArrowRight');
  }
  await expect(slider).toHaveValue('65');
  await expect(soundButton(page)).toHaveText('65');
});

test('the PC line in the top bar opens the PC block on Home', async ({ page }) => {
  await page.goto('/?mock=auth#/games');
  await pcBadge(page).click();

  await expect(page).toHaveURL(/#\/home$/);
  const block = page.locator('#this-pc');
  await expect(block).toBeInViewport();
  // Scrolled up to just under the top bar, not merely peeking in under the hero.
  await expect.poll(async () => (await block.boundingBox())?.y ?? Number.POSITIVE_INFINITY).toBeLessThan(300);
  await expect(block.getByRole('button', { name: 'Все характеристики', exact: true })).toBeFocused();
});

test('a new refresh rate asks "keep these settings?" once, and the top bar follows it', async ({ page }) => {
  await page.goto('/?mock=auth#/home');
  const monitor = page.getByRole('region', { name: 'Монитор', exact: true });
  const primary = monitor.getByRole('radiogroup', { name: 'Частота обновления' }).first();
  await primary.getByRole('radio', { name: '144 Гц', exact: true }).click();

  const confirm = page.getByRole('dialog', { name: 'Оставить эти настройки?' });
  await expect(confirm).toBeVisible();
  await expect(confirm).toHaveCount(1);
  await confirm.getByRole('button', { name: 'Оставить', exact: true }).click();
  await expect(confirm).toHaveCount(0);
  await expect(pcBadge(page)).toHaveAccessibleName(/· 144 Гц$/i);
});
