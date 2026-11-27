/**
 * The bonus account on the PC (D-106), in mock mode: the signed-in member's bonus (the mock's 5 000 сум) shows apart from the
 * balance — on the wallet's balance card with its rule «Бонусы — только на время», and next to the balance in the status
 * line — and a promo code credits the bonus, not the balance.
 */
import { expect, test } from '@playwright/test';

test('the wallet and the status line show the bonus apart from the balance, with its rule; a promo code credits the bonus', async ({
  page,
}) => {
  await page.goto('/?mock=auth#/wallet');
  const card = page.getByRole('region', { name: 'Баланс' }).locator('[data-bonus]');
  await expect(card).toContainText('Бонусы');
  await expect(card).toContainText('Бонусы — только на время');
  await expect(card).toContainText(/(^|\D)5\s?000\s?сум/);

  const status = page.locator('button[data-nav] [data-bonus]');
  await expect(status).toContainText(/(^|\D)5\s?000\s?сум/);
  await expect(page.locator('button[data-nav]:has([data-bonus])')).toHaveAttribute('title', /Бонусы — только на время/);

  // BONUS10 (10 000 сум) goes to the bonus account: the balance stays, the bonus grows.
  const balanceBefore = await page.getByRole('region', { name: 'Баланс' }).locator('.tnum').first().textContent();
  const promo = page.getByRole('region', { name: 'Промокод' });
  await promo.getByLabel('Промокод').fill('BONUS10');
  await promo.getByRole('button', { name: 'Активировать' }).click();
  await expect(promo).toContainText('по коду BONUS10');
  await expect(card).toContainText(/15\s?000\s?сум/);
  await expect(status).toContainText(/15\s?000\s?сум/);
  expect(await page.getByRole('region', { name: 'Баланс' }).locator('.tnum').first().textContent()).toBe(balanceBefore);
});
