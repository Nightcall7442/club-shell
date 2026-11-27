/**
 * Favourites and the player's own Shell settings follow the account, not the PC (D-86): a member stars a game or an
 * app and it waits on Home under «Избранное»; their theme comes back when they sign in again and the PC's own returns
 * when they leave. In mock mode the mock agent keeps each account's preferences (`state.accountPrefs`) as the server
 * does — in memory, so the tests move inside the app (`location.hash`) rather than reload it. `?mock=auth` signs in
 * `demo` / `1234` with a session.
 */
import { expect, test, type Page } from '@playwright/test';

interface MockPrefsState {
  settings: { theme: string; favorites?: string[]; accountPrefs?: boolean };
  prefsSaves: { userId: string; prefs: { theme?: string; favorites?: string[] } }[];
}

function mockState(page: Page): Promise<MockPrefsState> {
  return page.evaluate(
    () => (window as unknown as { __clubshellMock: { state: MockPrefsState } }).__clubshellMock.state,
  );
}

/** The last save of the member's preferences to the (mock) server. */
async function lastSave(page: Page): Promise<MockPrefsState['prefsSaves'][number]['prefs'] | undefined> {
  return (await mockState(page)).prefsSaves.at(-1)?.prefs;
}

function favourites(page: Page) {
  return page.getByRole('region', { name: 'Избранное' });
}

/** Navigates inside the running app: a reload would start the mock agent afresh. */
async function go(page: Page, hash: string): Promise<void> {
  await page.evaluate((h) => {
    window.location.hash = h;
  }, hash);
}

async function openGame(page: Page, title: string): Promise<void> {
  await page.locator('[data-game-id]').getByText(title, { exact: true }).first().click();
  await expect(page).toHaveURL(/#\/games\/[0-9a-f-]{36}$/);
}

test('a starred game shows in Favourites on Home and goes when unstarred', async ({ page }) => {
  await page.goto('/?mock=auth#/home');
  await expect(page.getByRole('heading', { name: 'Избранное' })).toHaveCount(0);
  await go(page, '#/games');
  await openGame(page, 'Counter-Strike 2');
  const star = page.getByRole('button', { name: 'Добавить Counter-Strike 2 в избранное' });
  await expect(star).toHaveAttribute('aria-pressed', 'false');
  await star.click();
  const unstar = page.getByRole('button', { name: 'Убрать Counter-Strike 2 из избранного' });
  await expect(unstar).toHaveAttribute('aria-pressed', 'true');
  // Saved to the member's account, not to this PC.
  await expect
    .poll(async () => (await lastSave(page))?.favorites)
    .toEqual([expect.stringMatching(/^game:[0-9a-f-]{36}$/)]);

  await go(page, '#/home');
  const row = favourites(page);
  await expect(row.locator('[data-game-id]').getByText('Counter-Strike 2', { exact: true })).toBeVisible();

  await row.getByRole('button', { name: 'Убрать Counter-Strike 2 из избранного' }).click();
  await expect(favourites(page)).toHaveCount(0);
  await expect.poll(async () => (await lastSave(page))?.favorites).toEqual([]);
});

test('a starred app opens from Favourites on Home', async ({ page }) => {
  await page.goto('/?mock=auth#/apps');
  await page.getByRole('button', { name: 'Добавить Discord в избранное' }).click();
  await expect(page.getByRole('button', { name: 'Убрать Discord из избранного' })).toHaveAttribute(
    'aria-pressed',
    'true',
  );

  await go(page, '#/home');
  const open = favourites(page)
    .getByRole('button', { name: /Discord$/ })
    .first();
  await expect(open).toBeVisible();
  await open.click();
  await expect(page.getByText('Discord открыто')).toBeVisible();
});

test('the theme follows the member: the PC gets its own back at sign-out and theirs returns at sign-in', async ({
  page,
}) => {
  await page.goto('/?mock=auth#/home');
  const html = page.locator('html');
  await expect(html).toHaveAttribute('data-theme', 'default');
  await page.getByRole('button', { name: /Неоновая ночь/ }).click();
  await expect(html).toHaveAttribute('data-theme', 'neon');
  await expect.poll(async () => (await lastSave(page))?.theme).toBe('neon');

  await page.getByRole('button', { name: 'Завершить и выйти', exact: true }).click();
  await page
    .getByRole('dialog', { name: 'Завершить игру и выйти?' })
    .getByRole('button', { name: 'Завершить и выйти' })
    .click();
  await expect(page).toHaveURL(/#\/lock$/);
  // The next player sits down at the club's PC, not at the last one's.
  await expect(html).toHaveAttribute('data-theme', 'default');
  expect((await mockState(page)).settings.accountPrefs).toBe(false);

  await page.getByRole('tab', { name: 'Пароль' }).click();
  await page.getByLabel('Логин', { exact: true }).and(page.locator('input')).fill('demo');
  const password = page.getByLabel('Пароль', { exact: true }).and(page.locator('input'));
  await password.fill('1234');
  await password.press('Enter');
  await expect(html).toHaveAttribute('data-theme', 'neon');
});

test('a guest has no account to star anything on', async ({ page }) => {
  await page.goto('/#/lock');
  await expect(page.getByRole('heading', { level: 1, name: 'Добро пожаловать' })).toBeVisible();
  // The cashier seats a walk-in guest on this PC.
  await page.evaluate(() =>
    (
      window as unknown as { __clubshellMock: { simulate: { deskGuest: (name: string) => void } } }
    ).__clubshellMock.simulate.deskGuest('Гость 5'),
  );
  await expect(page).toHaveURL(/#\/home$/, { timeout: 15_000 });
  await go(page, '#/games');
  await openGame(page, 'Counter-Strike 2');
  await expect(page.getByRole('button', { name: 'Играть сейчас' })).toBeVisible();
  await expect(page.getByRole('button', { name: /избранно/ })).toHaveCount(0);
  expect((await mockState(page)).settings.accountPrefs).toBe(false);
});
