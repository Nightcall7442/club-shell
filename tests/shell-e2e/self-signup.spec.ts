/**
 * «Создать аккаунт» on the lock screen (D-92), in mock mode: offered only where the owner allows it (`?signup=on`,
 * `?signup=rules` with the club rules to accept), checks the two passwords and the rules, says why the server refused,
 * and signs the new player in like a password login — the tariff picker, then home once the time starts.
 */
import { readFileSync } from 'node:fs';
import { expect, test, type Locator, type Page } from '@playwright/test';

type Strings = Record<string, unknown>;

const ru = (
  (bundle: Strings) =>
  (key: string, vars: Record<string, string> = {}): string => {
    const value = key
      .split('.')
      .reduce<unknown>(
        (node, part) => (typeof node === 'object' && node !== null ? (node as Strings)[part] : undefined),
        bundle,
      );
    if (typeof value !== 'string') throw new Error(`missing i18n key: ${key}`);
    return value.replace(/\{\{\s*(\w+)\s*\}\}/g, (_, name: string) => vars[name] ?? '');
  }
)(JSON.parse(readFileSync(new URL('../../apps/shell/src/i18n/ru.json', import.meta.url), 'utf8')) as Strings);

function field(page: Page, label: string): Locator {
  return page.getByLabel(label, { exact: true }).and(page.locator('input'));
}

async function openRegistration(page: Page, signup: 'on' | 'rules'): Promise<Locator> {
  await page.goto(`/?signup=${signup}#/lock`);
  await page.getByRole('tab', { name: ru('lock.methodPassword') }).click();
  await page.getByRole('button', { name: ru('lock.register.open') }).click();
  const form = page.getByRole('form', { name: ru('lock.register.title') });
  await expect(form).toBeVisible();
  return form;
}

async function simulate(page: Page, name: string, ...args: unknown[]): Promise<void> {
  await page.waitForFunction(() => Boolean((window as unknown as { __clubshellMock?: unknown }).__clubshellMock));
  await page.evaluate(
    ([fn, rest]) =>
      (
        window as unknown as { __clubshellMock: { simulate: Record<string, (...a: unknown[]) => void> } }
      ).__clubshellMock.simulate[fn as string]?.(...(rest as unknown[])),
    [name, args] as const,
  );
}

test.describe('self-signup on the lock screen', () => {
  test('a club without the switch keeps «ask the staff» and offers no registration', async ({ page }) => {
    await page.goto('/#/lock');
    await page.getByRole('tab', { name: ru('lock.methodPassword') }).click();
    await expect(page.getByText(ru('lock.askStaff'))).toBeVisible();
    await expect(page.getByRole('button', { name: ru('lock.register.open') })).toHaveCount(0);
  });

  test('a player creates an account, is signed in, and plays once the time starts', async ({ page }) => {
    const form = await openRegistration(page, 'on');
    const submit = form.getByRole('button', { name: ru('lock.register.submit') });

    // The form's own checks first: the name and the gender (D-104: none picked at first), then two different passwords.
    await submit.click();
    await expect(form.getByText(ru('lock.register.nameRequired'))).toBeVisible();
    await expect(form.getByText(ru('lock.register.genderRequired'))).toBeVisible();
    const genders = form.getByRole('radiogroup', { name: ru('lock.register.gender') });
    await expect(genders.getByRole('radio', { checked: true })).toHaveCount(0);
    await genders.getByRole('radio', { name: ru('lock.register.genderMale') }).click();
    await expect(genders.getByRole('radio', { name: ru('lock.register.genderMale') })).toHaveAttribute(
      'aria-checked',
      'true',
    );
    await expect(form.getByText(ru('lock.register.genderRequired'))).toHaveCount(0);
    await field(page, ru('lock.register.name')).fill('Самир');
    await field(page, ru('lock.register.login')).fill('samir');
    await field(page, ru('lock.register.password')).fill('4321');
    await field(page, ru('lock.register.passwordAgain')).fill('4322');
    await submit.click();
    await expect(form.getByText(ru('lock.register.passwordMismatch'))).toBeVisible();

    // A login someone has is the server's refusal, on the login field.
    await field(page, ru('lock.register.login')).fill('demo');
    await field(page, ru('lock.register.password')).fill('4321');
    await field(page, ru('lock.register.passwordAgain')).fill('4321');
    await submit.click();
    await expect(form.getByText(ru('lock.register.loginTaken'))).toBeVisible();

    await field(page, ru('lock.register.login')).fill('Samir');
    await field(page, ru('lock.register.password')).fill('4321');
    await field(page, ru('lock.register.passwordAgain')).fill('4321');
    await submit.click();

    // Signed in like after a password login: a new account has no time yet, so the tariff picker asks.
    await expect(page.getByText(ru('lock.register.welcome', { login: 'samir' }))).toBeVisible();
    await expect(form).toHaveCount(0);
    await expect(page.getByRole('dialog', { name: ru('wallet.chooseTariff') })).toBeVisible();

    // The desk opens their time: home.
    await simulate(page, 'deskStart', 60);
    await expect(page).toHaveURL(/#\/home$/);
  });

  test('the club rules are accepted before the account is made', async ({ page }) => {
    const form = await openRegistration(page, 'rules');
    await expect(form.getByText('Беречь технику')).toBeVisible();
    await field(page, ru('lock.register.name')).fill('Нодира');
    await form.getByRole('radio', { name: ru('lock.register.genderFemale') }).click();
    await field(page, ru('lock.register.login')).fill('+998 90 765 43 21');
    await field(page, ru('lock.register.password')).fill('pass');
    await field(page, ru('lock.register.passwordAgain')).fill('pass');
    await form.getByRole('button', { name: ru('lock.register.submit') }).click();
    await expect(form.getByText(ru('lock.register.rulesRequired'))).toBeVisible();

    await form.getByLabel(ru('lock.register.acceptRules')).check();
    await form.getByRole('button', { name: ru('lock.register.submit') }).click();
    // The phone is the login, kept as its digits.
    await expect(page.getByText(ru('lock.register.welcome', { login: '998907654321' }))).toBeVisible();
  });

  test('«I already have an account» goes back to the sign-in methods', async ({ page }) => {
    const form = await openRegistration(page, 'on');
    await form.getByRole('button', { name: ru('lock.register.back') }).click();
    await expect(page.getByRole('heading', { level: 1, name: ru('lock.title') })).toBeVisible();
    await expect(field(page, ru('lock.username'))).toBeVisible();
  });
});
