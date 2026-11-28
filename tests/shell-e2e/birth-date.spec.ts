/**
 * The date of birth and the minors' night curfew on the PC (D-107, D-108), in mock mode: a signed-in member without a
 * date (`?birth=none`) gives it once before any time is bought; a minor (`?birth=minor`) in the forbidden hours
 * (`?curfew=now`) is told «Несовершеннолетним нельзя играть с 22:00 до 06:00», and a purchase that would run into them
 * (`?curfew=soon`, 30 minutes away) is told how many minutes still fit.
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

/** The desk tops the new account up until the picker's «start» can pay (the wallet follows the push). */
async function topUpForPicker(page: Page, picker: Locator): Promise<Locator> {
  const start = picker.getByRole('button', { name: ru('lock.startPlaying') });
  await expect(async () => {
    if (await start.isDisabled()) await simulate(page, 'counterTopUp', 50_000);
    await expect(start).toBeEnabled({ timeout: 1_000 });
  }).toPass({ timeout: 15_000 });
  return start;
}

/**
 * A new account made on the lock screen (`?signup=on`): no money, so nothing starts by itself and the tariff picker
 * (or the date-of-birth question) follows. `?birth=` in the query replaces the date the mock account gets.
 */
async function signUp(page: Page, query: string): Promise<void> {
  await page.goto(`/?signup=on&${query}#/lock`);
  await page.getByRole('tab', { name: ru('lock.methodPassword') }).click();
  await page.getByRole('button', { name: ru('lock.register.open') }).click();
  const form = page.getByRole('form', { name: ru('lock.register.title') });
  await field(page, ru('lock.register.name')).fill('Тимур');
  await form.getByRole('radio', { name: ru('lock.register.genderMale') }).click();
  await form.getByLabel(ru('birthDate.day'), { exact: true }).selectOption('1');
  await form.getByLabel(ru('birthDate.month'), { exact: true }).selectOption('2');
  await form.getByLabel(ru('birthDate.year'), { exact: true }).selectOption('2001');
  await field(page, ru('lock.register.login')).fill(`timur${Date.now() % 100000}`);
  await field(page, ru('lock.register.password')).fill('4321');
  await field(page, ru('lock.register.passwordAgain')).fill('4321');
  await form.getByRole('button', { name: ru('lock.register.submit') }).click();
  await expect(form).toHaveCount(0);
}

test.describe('the date of birth and the minors’ curfew on the PC', () => {
  test('a member without a date of birth gives it once before choosing the time', async ({ page }) => {
    await signUp(page, 'birth=none');
    const ask = page.getByRole('dialog', { name: ru('birthDate.askTitle') });
    await expect(ask).toBeVisible();
    await expect(page.getByRole('dialog', { name: ru('wallet.chooseTariff') })).toHaveCount(0);
    await expect(ask.getByText(ru('birthDate.onceHint'))).toBeVisible();

    await ask.getByRole('button', { name: ru('birthDate.save') }).click();
    await expect(ask.getByText(ru('birthDate.problem.required'))).toBeVisible();
    await ask.getByLabel(ru('birthDate.day'), { exact: true }).selectOption('17');
    await ask.getByLabel(ru('birthDate.month'), { exact: true }).selectOption('5');
    await ask.getByLabel(ru('birthDate.year'), { exact: true }).selectOption('2001');
    await ask.getByRole('button', { name: ru('birthDate.save') }).click();

    await expect(ask).toHaveCount(0);
    await expect(page.getByRole('dialog', { name: ru('wallet.chooseTariff') })).toBeVisible();
  });

  test('a minor in the forbidden hours is told the club’s hours', async ({ page }) => {
    await signUp(page, 'birth=minor&curfew=now');
    const picker = page.getByRole('dialog', { name: ru('wallet.chooseTariff') });
    await expect(picker).toBeVisible();
    await (await topUpForPicker(page, picker)).click();
    await expect(page.getByText(ru('errors.policy.minorCurfewHours', { from: '22:00', to: '06:00' }))).toBeVisible();
  });

  test('a minor’s purchase that would run into the curfew says how many minutes still fit', async ({ page }) => {
    await signUp(page, 'birth=minor&curfew=soon');
    const picker = page.getByRole('dialog', { name: ru('wallet.chooseTariff') });
    await expect(picker).toBeVisible();
    await (await topUpForPicker(page, picker)).click();
    await expect(
      page.getByText(ru('errors.policy.minorCurfewUntil', { from: '22:00', to: '06:00', minutes: '30' })),
    ).toBeVisible();
  });
});
