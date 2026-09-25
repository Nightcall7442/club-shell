/**
 * "Sit at any PC — it plays like home": a player's own game settings (binds, sensitivity, graphics) follow them
 * between PCs. In mock mode the demo player already carries CS2 and Fortnite (`PLAYER_GAME_SETTINGS`).
 */
import { expect, test } from '@playwright/test';

test('the profile lists the games whose settings follow the player and can reset one', async ({ page }) => {
  await page.goto('/?mock=auth#/profile?tab=games');
  const list = page.getByRole('region', { name: 'Настройки игр' });
  await expect(list.getByText('Counter-Strike 2')).toBeVisible();
  await expect(list.getByText('Fortnite')).toBeVisible();

  await list.getByRole('button', { name: 'Сбросить настройки Counter-Strike 2' }).click();
  await expect(list.getByText('Counter-Strike 2')).toHaveCount(0);
  await expect(list.getByText('Fortnite')).toBeVisible();
});

test('a game that carries settings says so on its page', async ({ page }) => {
  await page.goto('/?mock=auth#/games');
  await page.locator('[data-game-id]').getByText('Counter-Strike 2', { exact: true }).first().click();
  await expect(page).toHaveURL(/#\/games\/[0-9a-f-]{36}$/);
  await expect(page.getByText('Ваши бинды, чувствительность и графика подтянутся на этом ПК')).toBeVisible();
});
