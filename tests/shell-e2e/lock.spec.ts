/**
 * Lock screen (`/#/lock`) end-to-end tests against the Vite dev server in mock mode (`VITE_MOCK=1`,
 * started by playwright.config.ts). Copy is asserted through the shipped i18n bundles so a wording change
 * updates the tests for free. Mock accounts: `demo` / `1234`, user PIN `1234` (apps/shell/src/mocks).
 */
import { readFileSync } from 'node:fs';
import { expect, test, type Locator, type Page } from '@playwright/test';

type Strings = Record<string, unknown>;

function loadStrings(locale: string): Strings {
  const url = new URL(`../../apps/shell/src/i18n/${locale}.json`, import.meta.url);
  return JSON.parse(readFileSync(url, 'utf8')) as Strings;
}

/** `t('lock.title')` over a raw bundle (no i18next in the test runner). */
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
const ru = translator(loadStrings('ru'));

const DEMO_USER = 'demo';
const DEMO_PASSWORD = '1234';
const USER_PIN = '1234';

function escapeRegExp(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/** Labelled form control (the tab panel shares the "Password" label with its tab, hence the `input` filter). */
function field(page: Page, label: string): Locator {
  return page.getByLabel(label, { exact: true }).and(page.locator('input'));
}

/**
 * Focusing an input opens the on-screen keyboard (mock settings allow it), which shifts the layout; blur
 * the field and wait for the panel to leave before clicking anything below it.
 */
async function dismissKeyboard(page: Page): Promise<void> {
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await expect(page.getByRole('group', { name: en('kiosk.virtualKeyboard.title') })).toBeHidden();
}

/** Top-bar session countdown (`aria-label="Session timer: HH:MM:SS"`). */
function sessionTimer(page: Page): Locator {
  return page.getByRole('button', { name: new RegExp(`^${escapeRegExp(en('session.timerLabel'))}: \\d`) });
}

/** The app boots in Russian (mock settings); every test forces English through the visible switcher. */
async function useEnglish(page: Page): Promise<void> {
  const english = page.getByRole('button', { name: 'English', exact: true });
  await expect(english).toBeVisible();
  if ((await english.getAttribute('aria-pressed')) !== 'true') {
    await english.click();
  }
  await expect(page.locator('html')).toHaveAttribute('lang', 'en');
}

async function openLock(page: Page): Promise<void> {
  await page.goto('/#/lock');
  await useEnglish(page);
  await expect(page.getByRole('heading', { level: 1, name: en('lock.title') })).toBeVisible();
}

/** Fills the password form and submits it with Enter (the form's documented shortcut). */
async function signIn(page: Page, username: string, password: string): Promise<void> {
  await page.getByRole('tab', { name: en('lock.methodPassword') }).click();
  await field(page, en('lock.username')).fill(username);
  const secret = field(page, en('lock.password'));
  await secret.fill(password);
  await secret.press('Enter');
}

/** Tariff picker shown after login (no session yet) → "Start playing" → `/home`. */
async function startSession(page: Page): Promise<void> {
  const picker = page.getByRole('dialog', { name: en('wallet.chooseTariff') });
  await expect(picker).toBeVisible();
  await picker.getByRole('button', { name: en('lock.startPlaying') }).click();
  await expect(page).toHaveURL(/#\/home$/);
  await expect(sessionTimer(page)).toBeVisible();
}

test.describe('lock screen', () => {
  test('renders the sign-in card with method tabs', async ({ page }) => {
    await openLock(page);

    await expect(page.getByText(en('lock.subtitle'))).toBeVisible();
    const tabs = page.getByRole('tablist', { name: en('lock.chooseMethod') });
    await expect(tabs).toBeVisible();
    await expect(tabs.getByRole('tab')).toHaveCount(3);
    for (const key of ['lock.methodQr', 'lock.methodPassword', 'lock.methodGuest']) {
      await expect(tabs.getByRole('tab', { name: en(key) })).toBeVisible();
    }
    // QR is the default: no keyboard needed and no password typed on a shared screen.
    await expect(tabs.getByRole('tab', { name: en('lock.methodQr') })).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByText(en('lock.scanQr'))).toBeVisible();
    await page.getByRole('tab', { name: en('lock.methodPassword') }).click();

    await expect(field(page, en('lock.username'))).toBeVisible();
    await expect(field(page, en('lock.password'))).toBeVisible();
    await expect(page.getByRole('button', { name: en('lock.login'), exact: true })).toBeEnabled();
    await expect(page.getByText(en('lock.demoHint'))).toBeVisible();

    await expect(page.getByRole('group', { name: en('lock.language') }).getByRole('button')).toHaveCount(3);
    await expect(page.getByLabel(en('lock.clock'))).toHaveText(/\d{1,2}:\d{2}/);
    await expect(page.getByRole('button', { name: en('lock.callAdmin') })).toBeVisible();
  });

  test('wrong password shows a localized error', async ({ page }) => {
    await openLock(page);
    await page.getByRole('tab', { name: en('lock.methodPassword') }).click();

    // Client-side validation first: nothing typed.
    await page.getByRole('button', { name: en('lock.login'), exact: true }).click();
    await expect(page.getByRole('alert').filter({ hasText: en('lock.usernameRequired') })).toBeVisible();
    await expect(page.getByRole('alert').filter({ hasText: en('lock.passwordRequired') })).toBeVisible();

    await signIn(page, DEMO_USER, 'definitely-wrong');
    const error = page.getByRole('alert').filter({ hasText: en('lock.invalidCredentials') });
    await expect(error).toBeVisible();
    await expect(page).toHaveURL(/#\/lock$/);
    await expect(field(page, en('lock.password'))).toHaveValue('');
    await expect(sessionTimer(page)).toHaveCount(0);
  });

  test('correct credentials sign in and land on /home with the session timer', async ({ page }) => {
    await openLock(page);
    await signIn(page, DEMO_USER, DEMO_PASSWORD);
    await startSession(page);

    await expect(sessionTimer(page)).toHaveAttribute('aria-label', /\d{1,2}:\d{2}(:\d{2})?/);
    await expect(page.getByRole('button', { name: en('desktop.lock'), exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { level: 1, name: en('lock.title') })).toHaveCount(0);
  });

  test('switching the language changes the copy (ru → en)', async ({ page }) => {
    await page.goto('/#/lock');

    // Mock settings boot the shell in Russian.
    await expect(page.getByRole('heading', { level: 1, name: ru('lock.title') })).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('lang', 'ru');
    await expect(page.getByRole('tab', { name: ru('lock.methodGuest') })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Русский', exact: true })).toHaveAttribute('aria-pressed', 'true');

    await page.getByRole('button', { name: 'English', exact: true }).click();

    await expect(page.locator('html')).toHaveAttribute('lang', 'en');
    await expect(page.getByRole('heading', { level: 1, name: en('lock.title') })).toBeVisible();
    await expect(page.getByRole('heading', { level: 1, name: ru('lock.title') })).toHaveCount(0);
    await expect(page.getByRole('tab', { name: en('lock.methodGuest') })).toBeVisible();
    await expect(page.getByText(en('lock.scanQr'))).toBeVisible();
    await expect(page.getByRole('button', { name: 'English', exact: true })).toHaveAttribute('aria-pressed', 'true');

    // The choice is persisted through settings_set: it survives a tab change within the session.
    await page.getByRole('tab', { name: en('lock.methodPassword') }).click();
    await expect(page.getByRole('button', { name: en('lock.login'), exact: true })).toBeVisible();
  });

  test('QR sign-in completes when the phone confirms the code', async ({ page }) => {
    await page.goto('/?mock=qr#/lock');
    await useEnglish(page);
    // QR is the default method: the code is on screen without touching anything.
    await expect(page.getByText(en('lock.scanQr'))).toBeVisible();
    await expect(page.getByRole('img', { name: en('lock.qr') })).toBeVisible();

    // The mock phone confirms a few seconds later; the shell signs in and asks for a tariff.
    await expect(page.getByRole('dialog', { name: en('wallet.chooseTariff') })).toBeVisible({ timeout: 20_000 });
  });

  test('QR tab renders an SVG QR code with a countdown', async ({ page }) => {
    await openLock(page);
    await page.getByRole('tab', { name: en('lock.methodQr') }).click();

    const panel = page.getByRole('tabpanel');
    await expect(panel.getByText(en('lock.scanQr'))).toBeVisible();
    const qr = panel.getByRole('img', { name: en('lock.qr') });
    await expect(qr).toBeVisible();
    expect(await qr.evaluate((el) => el.tagName.toLowerCase())).toBe('svg');
    expect(await qr.locator('path').count()).toBeGreaterThan(0);

    await expect(panel.getByRole('status').filter({ hasText: en('lock.qrWaiting') })).toBeVisible();
    await expect(panel.getByRole('progressbar')).toBeVisible();
    await expect(panel.getByRole('button', { name: en('lock.qrRefresh') })).toBeVisible();
  });

  test('guest login works', async ({ page }) => {
    await openLock(page);
    await page.getByRole('tab', { name: en('lock.methodGuest') }).click();

    await expect(page.getByText(en('lock.guestHint'))).toBeVisible();
    const submit = page.getByRole('button', { name: en('lock.guest'), exact: true });
    await expect(submit).toBeDisabled();
    await field(page, en('lock.guestName')).fill('Playwright');
    await dismissKeyboard(page);
    await page.getByRole('checkbox', { name: en('lock.guestTerms') }).check();
    await expect(submit).toBeEnabled();
    await submit.click();

    await startSession(page);
    await expect(page.getByText(en('desktop.guestBadge'), { exact: true })).toBeVisible();
    await expect(
      page.getByRole('button', { name: new RegExp(`^${escapeRegExp(en('desktop.userMenu'))}: Playwright`) }),
    ).toBeVisible();
  });

  test('locking from the top bar returns to /lock and unlocking restores the session', async ({ page }) => {
    await openLock(page);
    await signIn(page, DEMO_USER, DEMO_PASSWORD);
    await startSession(page);

    await page.getByRole('button', { name: en('desktop.lock'), exact: true }).click();
    await expect(page).toHaveURL(/#\/lock$/);
    await expect(page.getByText(en('lock.locked'), { exact: true })).toBeVisible();
    await expect(page.getByText(en('lock.lockedHint'))).toBeVisible();
    await expect(page.getByRole('tablist', { name: en('lock.chooseMethod') }).getByRole('tab')).toHaveCount(2);
    await expect(page.getByRole('heading', { level: 1, name: en('lock.title') })).toHaveCount(0);

    // Wrong PIN is rejected in place …
    const pin = field(page, en('lock.pin'));
    await expect(page.getByRole('button', { name: en('lock.unlockWithPin') })).toBeVisible();
    await pin.fill('9999');
    await pin.press('Enter');
    await expect(page.getByRole('alert').filter({ hasText: en('lock.wrongPin') })).toBeVisible();
    await expect(page).toHaveURL(/#\/lock$/);

    // … the right one restores the running session.
    await pin.fill(USER_PIN);
    await pin.press('Enter');
    await expect(page).toHaveURL(/#\/home$/);
    await expect(sessionTimer(page)).toBeVisible();
    await expect(page.getByRole('button', { name: en('desktop.lock'), exact: true })).toBeVisible();
  });
});
