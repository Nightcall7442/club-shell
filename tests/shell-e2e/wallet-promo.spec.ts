/**
 * The member's own promo code on the wallet (D-88), in mock mode: `BONUS10` credits 10 000 сум once, `OLD` has
 * expired, an unknown code says so, and the field explains each refusal.
 */
import { expect, test } from '@playwright/test';

test('a member redeems a promo code on the wallet once; an expired or unknown code says why', async ({ page }) => {
  await page.goto('/?mock=auth#/wallet');
  const promo = page.getByRole('region', { name: 'Промокод' });
  await expect(promo).toBeVisible();
  const field = promo.getByLabel('Промокод');
  const apply = promo.getByRole('button', { name: 'Активировать' });
  await expect(apply).toBeDisabled();

  await field.fill('bonus10');
  await expect(field).toHaveValue('BONUS10');
  await apply.click();
  await expect(promo).toContainText('по коду BONUS10');
  await expect(field).toHaveValue('');

  await field.fill('BONUS10');
  await apply.click();
  await expect(promo).toContainText('Код уже использован.');

  await field.fill('OLD');
  await apply.click();
  await expect(promo).toContainText('Срок действия кода закончился.');

  await field.fill('NOPE');
  await field.press('Enter');
  await expect(promo).toContainText('Такого кода нет');
});
