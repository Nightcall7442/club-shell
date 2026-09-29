/**
 * Admin console (`apps/admin`) end-to-end against a throwaway mock server started with `--reset` on :8091
 * (playwright.config.ts, project `admin`). Seeded staff: owner PIN `0000`, cashier PIN `1111`. Tests share one
 * database and run in file order, so each one sets up what it checks instead of relying on another's leftovers.
 */
import { createHash, createHmac } from 'node:crypto';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

const API = 'http://localhost:8091/api/v1';
const OWNER_PIN = '0000';
const CASHIER_PIN = '1111';
/** ADMIN_SERVER=real: the console runs against the central server (playwright.config.ts), not the mock. */
const REAL = process.env['ADMIN_SERVER'] === 'real';

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

/** The Agent's request signature (docs/SERVER_API.md §2.2): HMAC-SHA256 over timestamp + METHOD + target + sha256(body). The mock ignores it. */
function signature(
  secretB64: string | undefined,
  method: string,
  target: string,
  body: string,
): Record<string, string> {
  if (!secretB64) return {};
  const timestamp = String(Math.floor(Date.now() / 1000));
  const bodyHash = createHash('sha256').update(body).digest('hex');
  const value = createHmac('sha256', Buffer.from(secretB64, 'base64'))
    .update(timestamp + method + target + bodyHash)
    .digest('hex');
  return { 'X-Timestamp': timestamp, 'X-Signature': value };
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
  test.skip(
    REAL,
    "The tickets come from the mock's telemetry simulator; the server derives them from Agent telemetry (HealthTests).",
  );
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
  test.skip(REAL, 'The club network answers 501 on the central server (DESIGN D-19).');
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

test('a money action retried after a lost answer is applied once (Idempotency-Key)', async ({ page, request }) => {
  const owner = await tokenFor(request, OWNER_PIN);
  const username = `e2e-promo-${Date.now()}`;
  const created = await request.post(`${API}/admin/clients`, {
    headers: auth(owner),
    data: { username, displayName: 'E2E Промокод' },
  });
  expect(created.ok()).toBeTruthy();

  await signIn(page, CASHIER_PIN);
  await page.goto('/#/clients');
  await page.getByRole('row').filter({ hasText: username }).click();
  await page.getByLabel('Промокод').fill('WELCOME');

  // The server applies the code, but its answer is lost on the way back; the cashier presses again.
  const keys: string[] = [];
  await page.route('**/admin/promo/redeem', async (route) => {
    if (route.request().method() !== 'POST') return route.continue();
    keys.push(route.request().headers()['idempotency-key'] ?? '');
    if (keys.length > 1) return route.continue();
    await route.fetch();
    return route.abort('failed');
  });
  const apply = page.getByRole('button', { name: 'Применить' });
  await apply.click();
  await expect(page.getByText(/Нет связи с сервером/)).toBeVisible();
  await apply.click();
  await expect(page.getByText(/Промокод применён/)).toBeVisible();

  expect(keys).toHaveLength(2);
  expect(keys[0]).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
  expect(keys[1]).toBe(keys[0]);
  // WELCOME is 10 000 sum, credited once.
  const found = (await (await request.get(`${API}/admin/clients?q=${username}`, { headers: auth(owner) })).json()) as {
    items: { balance: { amount: number } }[];
  };
  expect(found.items[0]?.balance.amount).toBe(1_000_000);
});

test('only the owner reads the API key, Telegram is gone, and signing out revokes the token', async ({
  page,
  request,
}) => {
  const cashier = await tokenFor(request, CASHIER_PIN);
  const settings = (await (await request.get(`${API}/admin/club`, { headers: auth(cashier) })).json()) as Record<
    string,
    unknown
  >;
  expect(settings['apiKey']).toBeUndefined();
  expect(JSON.stringify(settings)).not.toMatch(/telegram/i);
  expect((await request.get(`${API}/admin/club/api-key`, { headers: auth(cashier) })).status()).toBe(403);

  await signIn(page, OWNER_PIN);
  await page.goto('/#/integrations');
  await expect(page.getByLabel('Ключ API')).toHaveValue(/^ck_/);
  await expect(page.getByText('Telegram')).toHaveCount(0);

  const token = await page.evaluate(() => localStorage.getItem('clubshell.admin.token'));
  expect(token).toBeTruthy();
  const loggedOut = page.waitForResponse((r) => r.url().endsWith('/admin/logout') && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Выйти' }).click();
  expect((await loggedOut).ok()).toBeTruthy();
  await expect(page.getByText('Введите PIN')).toBeVisible();
  expect((await request.get(`${API}/admin/me`, { headers: auth(token ?? '') })).status()).toBe(401);
});

test('a client registered at the counter signs in on a PC with the issued password and the bound card', async ({
  page,
  request,
}) => {
  const username = `e2e-login-${Date.now()}`;
  await signIn(page, CASHIER_PIN);
  await page.goto('/#/clients');
  await page.getByRole('button', { name: 'Новый клиент' }).click();
  await page.getByLabel('Имя').fill('E2E Вход');
  await page.getByLabel('Логин').fill(username);
  await page.getByLabel('Номер карты').fill(`CARD-${username}`);
  await page.getByRole('button', { name: 'Создать' }).click();
  const issued = page.getByRole('status').filter({ hasText: 'Временный пароль' }).locator('.font-mono');
  await expect(issued).toHaveText(/^[a-z2-9]{8}$/);
  const first = (await issued.textContent()) ?? '';

  // The PC side: an agent registers, then signs the client in (POST /auth/login).
  const hwid = `e2e-hwid-${username}`;
  const reg = await request.post(`${API}/agents/register`, {
    headers: { 'X-Club-Key': 'e2e' },
    data: {
      hwid,
      machineName: 'E2E-PC',
      agentVersion: '1.0.0',
      hardware: {
        cpu: { model: 'E2E CPU', cores: 4, threads: 8 },
        gpu: [],
        ramMb: 8192,
        disks: [],
        monitors: [],
        network: { mac: '00:00:00:00:00:99', ip: '10.0.0.99', adapter: 'Ethernet' },
        os: { version: '10.0.22631', build: '22631' },
        peripherals: [],
      },
      ipAddress: '10.0.0.99',
      macAddress: '00:00:00:00:00:99',
    },
  });
  expect(reg.ok()).toBeTruthy();
  const { pcId, accessToken, signingSecret } = (await reg.json()) as {
    pcId: string;
    accessToken: string;
    signingSecret?: string;
  };
  const loginTarget = new URL(`${API}/auth/login`).pathname;
  const login = async (data: Record<string, string>): Promise<number> => {
    const body = JSON.stringify({ pcId, hwid, ...data });
    return (
      await request.post(`${API}/auth/login`, {
        headers: {
          Authorization: `Bearer ${accessToken}`,
          'Content-Type': 'application/json',
          ...signature(signingSecret, 'POST', loginTarget, body),
        },
        data: body,
      })
    ).status();
  };
  expect(await login({ kind: 'password', username, password: first })).toBe(200);
  expect(await login({ kind: 'password', username, password: 'not-the-one' })).toBe(401);
  expect(await login({ kind: 'card', cardId: `CARD-${username}` })).toBe(200);

  // A reset issues a new temporary password; the old one stops working.
  await page.getByRole('button', { name: 'Сбросить пароль' }).click();
  await expect(issued).not.toHaveText(first);
  const second = (await issued.textContent()) ?? '';
  expect(await login({ kind: 'password', username, password: first })).toBe(401);
  expect(await login({ kind: 'password', username, password: second })).toBe(200);
});
