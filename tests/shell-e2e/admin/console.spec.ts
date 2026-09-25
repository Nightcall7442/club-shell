/**
 * Admin console (`apps/admin`) end-to-end against a throwaway mock server started with `--reset` on :8091
 * (playwright.config.ts, project `admin`). Seeded staff: owner PIN `0000`, cashier PIN `1111`. Tests share one
 * database and run in file order, so each one sets up what it checks instead of relying on another's leftovers.
 */
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

const API = 'http://localhost:8091/api/v1';
const OWNER_PIN = '0000';
const CASHIER_PIN = '1111';

async function signIn(page: Page, pin: string): Promise<void> {
  await page.goto('/');
  await page.evaluate(() => localStorage.clear());
  await page.reload();
  await expect(page.getByText('Введите PIN')).toBeVisible();
  for (const d of pin) await page.keyboard.press(d);
  await page.keyboard.press('Enter');
}

async function tokenFor(request: APIRequestContext, pin: string): Promise<string> {
  const res = await request.post(`${API}/admin/login`, { data: { pin } });
  expect(res.ok()).toBeTruthy();
  return ((await res.json()) as { token: string }).token;
}

function auth(token: string): Record<string, string> {
  return { Authorization: `Bearer ${token}` };
}

const nav = (page: Page) => page.getByRole('navigation');

test('a wrong PIN is refused with a clear message', async ({ page }) => {
  await signIn(page, '9999');
  await expect(page.getByText('Неверный PIN или сессия истекла')).toBeVisible();
});

test('the owner sees every section', async ({ page }) => {
  await signIn(page, OWNER_PIN);
  for (const label of ['Касса', 'Настройка клуба', 'Бизнес', 'Тарифы и цены', 'Отчёты', 'Персонал']) {
    await expect(nav(page).getByText(label, { exact: true })).toBeVisible();
  }
});

test('a cashier sees only the counter and cannot change club settings', async ({ page, request }) => {
  await signIn(page, CASHIER_PIN);
  await expect(nav(page).getByText('Касса', { exact: true })).toBeVisible();
  await expect(nav(page).getByText('Настройка клуба', { exact: true })).toHaveCount(0);
  await expect(nav(page).getByText('Отчёты', { exact: true })).toHaveCount(0);

  // Hiding the menu is not the guard: the server refuses the owner-only write too.
  const token = await tokenFor(request, CASHIER_PIN);
  const res = await request.patch(`${API}/admin/club`, {
    headers: auth(token),
    data: { branding: { clubName: 'Hijacked', accent: '#FF0000', logoUrl: null, wallpaperUrl: null } },
  });
  expect(res.status()).toBe(403);
});

test('the language switch translates the console and survives a reload', async ({ page }) => {
  await signIn(page, OWNER_PIN);
  await page.getByRole('button', { name: /^uz$/i }).click();
  await expect(nav(page).getByText('Xarita', { exact: true })).toBeVisible();
  await page.reload();
  await expect(nav(page).getByText('Xarita', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: /^ru$/i }).click();
  await expect(nav(page).getByText('Карта', { exact: true })).toBeVisible();
});

test('a shift opens with starting cash and closes with the counted difference', async ({ page }) => {
  await signIn(page, OWNER_PIN);
  await page.goto('/#/shift');
  await page.getByLabel('Наличные в кассе на начало').fill('100000');
  await page.getByRole('button', { name: 'Открыть смену' }).click();
  await expect(page.getByText('Смена открыта')).toBeVisible();

  await page.getByLabel('Посчитано в кассе').fill('90000');
  await page.getByRole('button', { name: 'Закрыть смену' }).click();
  await expect(page.getByText('Смена закрыта')).toBeVisible();
  // Z-report: 100 000 expected, 90 000 counted.
  await expect(page.getByText('−10 000').first()).toBeVisible();
});

test('the price takes the day rate and only the single best discount', async ({ request }) => {
  const owner = await tokenFor(request, OWNER_PIN);
  const headers = auth(owner);

  // Every day +20 %, and a happy hour (−30 %) that covers the whole clock in two halves.
  const everyDay = [0, 1, 2, 3, 4, 5, 6];
  const patch = await request.patch(`${API}/admin/club`, {
    headers,
    data: {
      pricing: { weekdayPct: everyDay.map(() => 120), holidays: [], holidayPct: 120 },
      happyHours: [
        { id: 'hh-am', name: 'Весь день', days: everyDay, from: '00:00', to: '12:00', discountPct: 30, zones: [] },
        { id: 'hh-pm', name: 'Весь день', days: everyDay, from: '12:00', to: '00:00', discountPct: 30, zones: [] },
      ],
    },
  });
  expect(patch.ok()).toBeTruthy();

  const tariffs = (await (await request.get(`${API}/admin/tariffs`, { headers })).json()) as {
    items: { id: string; name: string }[];
  };
  const standard = tariffs.items.find((t) => t.name === 'Standard');
  const pcs = (await (await request.get(`${API}/admin/pcs`, { headers })).json()) as { items: { id: string }[] };
  expect(standard).toBeTruthy();
  const pcId = pcs.items[0]?.id;

  async function quoteFor(groupId: string | null): Promise<{
    dayPct: number;
    discountPct: number;
    discountReason: string | null;
    total: { amount: number };
  }> {
    let userId: string | null = null;
    if (groupId) {
      const created = await request.post(`${API}/admin/clients`, {
        headers,
        data: { username: `e2e-${groupId}-${Date.now()}`, displayName: `E2E ${groupId}`, groupId },
      });
      expect(created.ok()).toBeTruthy();
      userId = ((await created.json()) as { client: { id: string } }).client.id;
    }
    const res = await request.post(`${API}/admin/quote`, {
      headers,
      data: { tariffId: standard!.id, pcId, minutes: 60, userId },
    });
    expect(res.ok()).toBeTruthy();
    return res.json();
  }

  // 12 000 sum/h × 120 % × (100 − 30) % = 10 080 sum.
  const walkIn = await quoteFor(null);
  expect(walkIn.dayPct).toBe(120);
  expect(walkIn.discountPct).toBe(30);
  expect(walkIn.total.amount).toBe(1_008_000);

  // Staff group (−50 %) beats the happy hour; the discounts do not stack.
  const staff = await quoteFor('staff');
  expect(staff.discountPct).toBe(50);
  expect(staff.discountReason).toBe('Сотрудник');
  expect(staff.total.amount).toBe(720_000);

  // Student group (−15 %) loses to the happy hour.
  const student = await quoteFor('student');
  expect(student.discountPct).toBe(30);
});

test('the player-screen settings are saved on the server', async ({ page, request }) => {
  await signIn(page, OWNER_PIN);
  await page.goto('/#/club');
  const name = page.getByLabel('Название клуба');
  await name.fill('E2E Arena');
  const save = page.getByRole('button', { name: 'Сохранить', exact: true });
  await save.click();
  // The save bar gives way to a short confirmation.
  await expect(page.getByRole('status').filter({ hasText: 'Сохранено' })).toBeVisible();
  await expect(save).toHaveCount(0);

  const owner = await tokenFor(request, OWNER_PIN);
  const club = (await (await request.get(`${API}/admin/club`, { headers: auth(owner) })).json()) as {
    branding: { clubName: string };
  };
  expect(club.branding.clubName).toBe('E2E Arena');
});

test('cashier control flags a cash shortfall and quick refunds, and only the owner sees it', async ({
  page,
  request,
}) => {
  const cashier = await tokenFor(request, CASHIER_PIN);
  const headers = auth(cashier);
  const call = async (method: 'GET' | 'POST', path: string, data?: unknown): Promise<any> => {
    const res = await request.fetch(`${API}${path}`, { method, headers, data });
    expect(res.ok(), `${method} ${path}: ${await res.text()}`).toBeTruthy();
    return res.json();
  };

  const current = (await call('GET', '/admin/shift')) as { shift: { openingCash: number } | null };
  if (current.shift) await call('POST', '/admin/shift/close', { closingCash: current.shift.openingCash });

  const overview = (await call('GET', '/admin/overview')) as {
    users: { id: string }[];
    tariffs: { id: string; name: string }[];
    seats: { pc: { id: string; status: string }; session: unknown }[];
  };
  const user = overview.users[0]!;
  const standard = overview.tariffs.find((t) => t.name === 'Standard')!;
  const free = overview.seats.filter((s) => s.pc.status === 'free' && !s.session).map((s) => s.pc.id);

  await call('POST', '/admin/shift/open', { openingCash: 20_000_000 });
  await call('POST', '/admin/wallet/topup', { userId: user.id, amount: 10_000_000, method: 'cash' });
  // Three sessions opened and ended with a refund at once: the classic "sell time, refund it, pocket the cash".
  for (const pcId of free.slice(0, 3)) {
    await call('POST', '/admin/sessions', { pcId, userId: user.id, tariffId: standard.id, minutes: 120 });
    await call('POST', '/admin/sessions/end', { pcId });
  }
  // 300 000 expected in the drawer, 250 000 counted.
  await call('POST', '/admin/shift/close', { closingCash: 25_000_000 });

  expect((await request.get(`${API}/admin/control`, { headers })).status()).toBe(403);

  await signIn(page, OWNER_PIN);
  await page.goto('/#/control');
  await expect(page.getByRole('heading', { name: 'Контроль кассиров' })).toBeVisible();
  await expect(page.getByText('Недостача в кассе при закрытии смены: 50 000 сум').first()).toBeVisible();
  await expect(page.getByText('3 сеанса за смену закрыты с возвратом вскоре после открытия').first()).toBeVisible();
  await expect(page.getByRole('cell', { name: 'Кассир Азиз' }).first()).toBeVisible();
});

test('PC health opens repair tickets from telemetry; staff take them and close them', async ({ page }) => {
  // The mock plays the Agents' telemetry: PC-15's GPU runs hot, PC-07 heats up day by day, PC-19 lost frames.
  await signIn(page, CASHIER_PIN);
  await expect(page.locator('[aria-label="Нужен ремонт"]').first()).toBeVisible();

  await page.goto('/#/health');
  await expect(page.getByRole('heading', { name: 'Состояние ПК' })).toBeVisible();
  const tickets = page.getByRole('list', { name: 'Заявки на ремонт' });
  const hot = tickets.getByRole('listitem').filter({ hasText: /Видеокарта \d+ °C — перегрев/ });
  await expect(hot).toHaveCount(1);
  await hot.getByRole('button', { name: 'Взять в работу' }).click();
  await expect(hot.getByText('В работе')).toBeVisible();
  await hot.getByRole('button', { name: 'Решено' }).click();
  await expect(tickets.getByRole('listitem').filter({ hasText: /Видеокарта \d+ °C — перегрев/ })).toHaveCount(0);
  // Thresholds are the owner's.
  await expect(page.getByText('Пороги')).toHaveCount(0);
});

test('the owner sees and edits where a game keeps player settings', async ({ page }) => {
  await signIn(page, OWNER_PIN);
  await page.goto('/#/catalog');
  const cs2 = page.getByRole('row').filter({ hasText: 'Counter-Strike 2' });
  await expect(cs2.getByRole('button', { name: 'Переносятся' })).toBeVisible();

  const rust = page.getByRole('row').filter({ hasText: 'Rust' });
  await rust.getByRole('button', { name: 'Не заданы' }).click();
  await page.getByLabel('Пути к настройкам игрока').fill('%APPDATA%\\Rust\\cfg');
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await expect(rust.getByRole('button', { name: 'Переносятся' })).toBeVisible();
});

test('the owner sees every club of the network and adds one', async ({ page, request }) => {
  const cashier = await tokenFor(request, CASHIER_PIN);
  expect((await request.get(`${API}/admin/network`, { headers: auth(cashier) })).status()).toBe(403);

  await signIn(page, OWNER_PIN);
  await page.goto('/#/network');
  await expect(page.getByRole('heading', { name: /Сеть клубов/ })).toBeVisible();
  // The local club (renamed by an earlier test) plus the two demo neighbours.
  await expect(page.getByRole('article')).toHaveCount(3);
  await expect(page.getByText('этот сервер')).toHaveCount(1);
  for (const name of ['CyberArena Чиланзар', 'CyberArena Самарканд']) {
    await expect(page.getByRole('article', { name })).toBeVisible();
  }

  await page.getByLabel('Название').fill('CyberArena Бухара');
  await page.getByLabel('Город').fill('Бухара');
  await page.getByRole('button', { name: 'Добавить', exact: true }).click();
  const added = page.getByRole('article', { name: 'CyberArena Бухара' });
  await expect(added).toBeVisible();
  await expect(added.getByText('играют сейчас')).toBeVisible();
});

test('insights turn the club data into actions — a happy hour in one click, a dismissed one stays hidden', async ({
  page,
  request,
}) => {
  // Start from a club with no happy hours (an earlier test sets its own).
  const owner = await tokenFor(request, OWNER_PIN);
  expect(
    (await request.patch(`${API}/admin/club`, { headers: auth(owner), data: { happyHours: [] } })).ok(),
  ).toBeTruthy();
  const cashier = await tokenFor(request, CASHIER_PIN);
  expect((await request.get(`${API}/admin/insights`, { headers: auth(cashier) })).status()).toBe(403);

  await signIn(page, OWNER_PIN);
  await page.goto('/#/insights');
  await expect(page.getByRole('heading', { name: 'Подсказки' })).toBeVisible();

  // Demo sales: Red Bull runs out before the next delivery, nobody buys the club T-shirt.
  const redBull = page.getByRole('article', { name: /Red Bull 0\.25L закончится/ });
  await expect(redBull).toBeVisible();
  await expect(redBull.getByText(/Закажите \d+ шт\./)).toBeVisible();
  await expect(page.getByRole('article', { name: 'Club T-shirt не продаётся' })).toBeVisible();

  // An empty zone gets its happy hour straight from the card, and the card is gone once it exists.
  const idle = page.getByRole('article', { name: /пустует/ }).first();
  const title = (await idle.getByRole('heading').textContent()) ?? '';
  await idle.getByRole('button', { name: /Создать счастливый час/ }).click();
  await expect(page.getByText(/Счастливый час «.+» создан/)).toBeVisible();
  await expect(page.getByRole('article', { name: title })).toHaveCount(0);

  const shirt = page.getByRole('article', { name: 'Club T-shirt не продаётся' });
  await shirt.getByRole('button', { name: 'Скрыть' }).click();
  await expect(shirt).toHaveCount(0);
  await page.reload();
  await expect(page.getByRole('article', { name: /Red Bull 0\.25L закончится/ })).toBeVisible();
  await expect(page.getByRole('article', { name: 'Club T-shirt не продаётся' })).toHaveCount(0);
});
