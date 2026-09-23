/**
 * Games library (`/#/games`) end-to-end tests against the Vite dev server in mock mode. `?mock=auth` boots
 * the shell already signed in (`demo`) with an active session, so the guarded routes render directly. The
 * catalogue is the mock data of apps/shell/src/mocks/data.ts (15 titles, e.g. "Counter-Strike 2",
 * "Dota 2", "Rocket League"); launches go "launching" → "running" after ~2.4 s on the mock clock.
 */
import { readFileSync } from 'node:fs';
import { expect, test, type Locator, type Page } from '@playwright/test';

type Strings = Record<string, unknown>;

function loadStrings(locale: string): Strings {
  const url = new URL(`../../apps/shell/src/i18n/${locale}.json`, import.meta.url);
  return JSON.parse(readFileSync(url, 'utf8')) as Strings;
}

/** `t('games.title')` over the raw bundle, with `{{var}}` interpolation. */
function translator(bundle: Strings): (key: string, vars?: Record<string, string | number>) => string {
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

const en = translator(loadStrings('en'));

/** Lower bound on the mock catalogue size; the exact number is cross-checked against the on-screen count. */
const MIN_GAMES = 10;
const CS2 = 'Counter-Strike 2';
const DOTA = 'Dota 2';
const VALORANT = 'VALORANT';
const ROCKET_LEAGUE = 'Rocket League';
const FORZA = 'Forza Horizon 5';

function grid(page: Page): Locator {
  return page.getByRole('list', { name: en('games.gridLabel') });
}

function cards(page: Page): Locator {
  return grid(page).locator('[data-game-id]');
}

function card(page: Page, title: string): Locator {
  return grid(page).getByRole('button', { name: title, exact: true });
}

/** Selected-game banner (`<section aria-label={title}>`). */
function hero(page: Page, title: string): Locator {
  return page.getByRole('region', { name: title, exact: true });
}

function categoryChip(page: Page, label: string): Locator {
  return page.getByRole('group', { name: en('games.categories') }).getByRole('button', { name: label, exact: true });
}

function searchBox(page: Page): Locator {
  return page.getByRole('searchbox', { name: en('games.search') });
}

/**
 * The shell boots in Russian (mock settings); switch to English through the top bar's sound-and-language menu (its
 * only popover). The option is activated with Enter (kiosk keyboard/gamepad navigation): the popover drops below
 * the hero artwork on the library screen, so a pointer click there is intercepted by the page.
 */
async function useEnglish(page: Page): Promise<void> {
  const trigger = page.locator('header [data-popover-trigger]');
  await expect(trigger).toBeVisible();
  if ((await page.locator('html').getAttribute('lang')) === 'en') {
    return;
  }
  await trigger.click();
  const option = page.getByRole('option', { name: 'English' });
  await expect(option).toBeVisible();
  await option.press('Enter');
  await expect(page.locator('html')).toHaveAttribute('lang', 'en');
}

async function openLibrary(page: Page): Promise<void> {
  await page.goto('/?mock=auth#/games');
  await useEnglish(page);
  await expect(page).toHaveURL(/#\/games$/);
  await expect(grid(page)).toBeVisible();
  await expect(cards(page).first()).toBeVisible();
}

/** Number of games in the "N games" caption next to the library heading. */
async function shownCount(page: Page): Promise<number> {
  const heading = page.getByRole('heading', { level: 2, name: en('games.title') });
  const text = await heading.textContent();
  const match = /(\d+)\s+\S+/.exec(text ?? '');
  expect(match, `count caption in "${text}"`).not.toBeNull();
  return Number(match?.[1]);
}

test.describe('games library', () => {
  test('shows the catalogue grid with a card per mock game', async ({ page }) => {
    await openLibrary(page);

    const total = await shownCount(page);
    expect(total).toBeGreaterThanOrEqual(MIN_GAMES);
    await expect(cards(page)).toHaveCount(total);
    for (const title of [CS2, DOTA, VALORANT, ROCKET_LEAGUE]) {
      await expect(card(page, title)).toBeVisible();
    }

    // Most popular title is the default hero; exactly one card is the roving tab stop.
    await expect(hero(page, CS2)).toBeVisible();
    await expect(hero(page, CS2).getByRole('heading', { level: 1 })).toHaveText(CS2);
    await expect(hero(page, CS2).getByRole('button', { name: en('games.playNow') })).toBeEnabled();
    await expect(grid(page).locator('[data-game-id][tabindex="0"]')).toHaveCount(1);
    await expect(card(page, CS2)).toHaveAttribute('aria-current', 'true');

    // Installed is the default state (no badge); only "not installed" and "running" are marked.
    await expect(card(page, CS2).getByText(en('games.installed'), { exact: true })).toHaveCount(0);
    await expect(card(page, FORZA).getByText(en('games.notInstalled'), { exact: true })).toBeVisible();
    await expect(hero(page, CS2).getByRole('button', { name: en('games.details') })).toBeVisible();
  });

  test('search filters the grid', async ({ page }) => {
    await openLibrary(page);
    const total = await shownCount(page);

    await searchBox(page).fill('rocket');
    await expect(cards(page)).toHaveCount(1);
    await expect(card(page, ROCKET_LEAGUE)).toBeVisible();
    await expect(hero(page, ROCKET_LEAGUE)).toBeVisible();
    await expect(page.getByRole('button', { name: en('games.clearFilters') })).toBeVisible();

    // Tags are searched too ("esports" is shared by several titles), and case does not matter.
    await searchBox(page).fill('ESPORTS');
    await expect(card(page, CS2)).toBeVisible();
    await expect(card(page, DOTA)).toBeVisible();
    await expect(card(page, ROCKET_LEAGUE)).toHaveCount(0);
    expect(await cards(page).count()).toBeGreaterThanOrEqual(2);
    expect(await cards(page).count()).toBeLessThan(total);

    await searchBox(page).fill('zzz-no-such-game');
    await expect(cards(page)).toHaveCount(0);
    await expect(page.getByText(en('games.noResults'))).toBeVisible();

    await page.getByRole('button', { name: en('common.clear'), exact: true }).click();
    await expect(searchBox(page)).toHaveValue('');
    await expect(cards(page)).toHaveCount(total);
    await expect(page.getByRole('button', { name: en('games.clearFilters') })).toHaveCount(0);
  });

  test('category chips filter the grid', async ({ page }) => {
    await openLibrary(page);
    const total = await shownCount(page);
    const all = categoryChip(page, en('games.allCategories'));
    const racing = categoryChip(page, en('games.cat.racing'));
    await expect(all).toHaveAttribute('aria-pressed', 'true');

    await racing.click();
    await expect(racing).toHaveAttribute('aria-pressed', 'true');
    await expect(all).toHaveAttribute('aria-pressed', 'false');
    await expect(cards(page)).toHaveCount(2);
    await expect(card(page, ROCKET_LEAGUE)).toBeVisible();
    await expect(card(page, FORZA)).toBeVisible();
    await expect(hero(page, ROCKET_LEAGUE)).toBeVisible();

    // Filters combine: installed-only hides the uninstalled racer.
    await page.getByRole('button', { name: en('games.installedOnly') }).click();
    await expect(cards(page)).toHaveCount(1);
    await expect(card(page, FORZA)).toHaveCount(0);

    await page.getByRole('button', { name: en('games.clearFilters') }).click();
    await expect(all).toHaveAttribute('aria-pressed', 'true');
    await expect(cards(page)).toHaveCount(total);
  });

  test('selecting a card updates the hero', async ({ page }) => {
    await openLibrary(page);
    await expect(hero(page, CS2)).toBeVisible();

    await card(page, VALORANT).hover();
    await expect(hero(page, VALORANT)).toBeVisible();
    await expect(hero(page, VALORANT).getByRole('heading', { level: 1 })).toHaveText(VALORANT);
    await expect(hero(page, CS2)).toHaveCount(0);
    await expect(card(page, VALORANT)).toHaveAttribute('aria-current', 'true');
    await expect(card(page, CS2)).not.toHaveAttribute('aria-current', 'true');

    // Keyboard focus selects as well.
    await card(page, DOTA).focus();
    await expect(hero(page, DOTA)).toBeVisible();
    await expect(card(page, DOTA)).toHaveAttribute('aria-current', 'true');
  });

  test('opens the details route from a card and from the hero', async ({ page }) => {
    await openLibrary(page);

    await card(page, DOTA).click();
    await expect(page).toHaveURL(/#\/games\/[0-9a-f-]{36}$/);
    await expect(grid(page)).toHaveCount(0);
    await expect(page.getByRole('heading', { level: 1, name: DOTA })).toBeVisible();
    await expect(page.getByRole('heading', { level: 2, name: en('games.description'), exact: true })).toBeVisible();

    await page
      .getByRole('button', { name: en('games.backToGames') })
      .first()
      .click();
    await expect(page).toHaveURL(/#\/games$/);
    await expect(grid(page)).toBeVisible();

    await hero(page, DOTA)
      .getByRole('button', { name: en('games.details') })
      .click();
    await expect(page).toHaveURL(/#\/games\/[0-9a-f-]{36}$/);
    await expect(page.getByRole('heading', { level: 1, name: DOTA })).toBeVisible();
  });

  test('launches a game, sees it running and closes it', async ({ page }) => {
    await openLibrary(page);
    const banner = hero(page, CS2);
    await banner.getByRole('button', { name: en('games.playNow') }).click();

    // Launch overlay: progress steps, then the "running" state, then it goes away on its own.
    const overlay = page.getByRole('dialog', { name: en('games.launchTitle', { title: CS2 }) });
    await expect(overlay).toBeVisible();
    await expect(overlay.getByText(en('games.launchStep.antiCheat'))).toBeVisible();
    await expect(overlay.getByRole('button', { name: en('games.launchCancel') })).toBeVisible();
    const ready = page.getByRole('dialog', { name: en('games.launchReady') });
    await expect(ready).toBeVisible({ timeout: 10_000 });
    await expect(ready).toBeHidden({ timeout: 10_000 });

    // Running badge on the card, "Close game" in the hero.
    await expect(card(page, CS2).getByText(en('games.running'), { exact: true })).toBeVisible();
    await expect(banner.getByText(en('games.nowPlaying'), { exact: true })).toBeVisible();
    const kill = banner.getByRole('button', { name: en('games.kill') });
    await expect(kill).toBeVisible();
    await expect(banner.getByRole('button', { name: en('games.playNow') })).toHaveCount(0);

    // Kill: confirm dialog → the process is gone and the card drops its "running" badge.
    await kill.click();
    const confirm = page.getByRole('dialog', { name: en('games.killTitle') });
    await expect(confirm).toBeVisible();
    await expect(confirm.getByText(en('games.killConfirm', { title: CS2 }))).toBeVisible();
    await confirm.getByRole('button', { name: en('games.kill') }).click();
    await expect(confirm).toBeHidden();
    await expect(banner.getByRole('button', { name: en('games.playNow') })).toBeEnabled();
    await expect(card(page, CS2).getByText(en('games.running'), { exact: true })).toHaveCount(0);
  });

  test('keyboard: ArrowRight moves between cards, Enter opens details', async ({ page }) => {
    await openLibrary(page);
    const first = cards(page).nth(0);
    const second = cards(page).nth(1);
    const secondTitle = await second.getAttribute('aria-label');
    expect(secondTitle).toBeTruthy();

    await first.focus();
    await expect(first).toBeFocused();
    await page.keyboard.press('ArrowRight');
    await expect(second).toBeFocused();
    await expect(hero(page, secondTitle ?? '')).toBeVisible();
    await expect(second).toHaveAttribute('aria-current', 'true');

    await page.keyboard.press('ArrowLeft');
    await expect(first).toBeFocused();
    await page.keyboard.press('ArrowRight');
    await expect(second).toBeFocused();

    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(/#\/games\/[0-9a-f-]{36}$/);
    // The library keeps rendering during its exit transition (its hero already shows the same title), so
    // wait for a details-only control before asserting the page.
    await expect(page.getByRole('button', { name: en('games.backToGames') }).first()).toBeVisible();
    await expect(grid(page)).toHaveCount(0);
    await expect(page.getByRole('heading', { level: 1, name: secondTitle ?? '' })).toBeVisible();

    // Escape returns to the library (B / Esc back binding of the details page).
    await page.keyboard.press('Escape');
    await expect(page).toHaveURL(/#\/games$/);
    await expect(grid(page)).toBeVisible();
  });
});
