/**
 * Admin console (`apps/admin`) end-to-end against a throwaway mock server started with `--reset` on :8091
 * (playwright.config.ts, project `admin`). Seeded staff: owner PIN `0000`, cashier PIN `1111`. Tests share one
 * database and run in file order, so each one sets up what it checks instead of relying on another's leftovers.
 * Money needs an open cash shift and the console opens with a shift gate without one, so {@link signIn} opens a shift
 * through the API first unless the test is about the gate itself. Tests that need a PC of their own register an Agent
 * ({@link registerAgent}) and act as it with signed requests ({@link agentCall}); {@link stubPrint} records what the
 * console prints instead of opening the print dialog. Amounts are read from the API, never written as literals: the
 * mock and the server price and refund the same way, but the day's price and discounts depend on earlier tests.
 */
import { createHash, createHmac, randomBytes, randomUUID } from 'node:crypto';
import { expect, test, type APIRequestContext, type APIResponse, type Page } from '@playwright/test';

const API = 'http://localhost:8091/api/v1';
const OWNER_PIN = '0000';
const CASHIER_PIN = '1111';
/** ADMIN_SERVER=real: the console runs against the central server (playwright.config.ts), not the mock. */
const REAL = process.env['ADMIN_SERVER'] === 'real';

/** Opens a shift through the API when none is open, so the console's shift gate does not cover the page. */
async function ensureShift(request: APIRequestContext): Promise<void> {
  const headers = auth(await tokenFor(request, OWNER_PIN));
  const state = (await (await request.get(`${API}/admin/shift`, { headers })).json()) as { shift: unknown };
  if (state.shift) return;
  const res = await request.post(`${API}/admin/shift/open`, { headers, data: { openingCash: 0 } });
  expect(res.ok(), await res.text()).toBeTruthy();
}

/**
 * Closes the open shift, if any, with exactly the cash it expects (so no shortfall is flagged): the server's
 * `expectedCash`, which counts cash moves and payouts too.
 */
async function closeShift(request: APIRequestContext): Promise<void> {
  const headers = auth(await tokenFor(request, OWNER_PIN));
  const state = (await (await request.get(`${API}/admin/shift`, { headers })).json()) as {
    shift: { openingCash: number } | null;
    x: { topUpCash: number } | null;
    expectedCash?: number | null;
  };
  if (!state.shift) return;
  const res = await request.post(`${API}/admin/shift/close`, {
    headers,
    data: { closingCash: state.expectedCash ?? state.shift.openingCash + (state.x?.topUpCash ?? 0) },
  });
  expect(res.ok(), await res.text()).toBeTruthy();
}

/** A money POST with its own `Idempotency-Key` (required by the routes beyond the contract). */
async function moneyPost(
  request: APIRequestContext,
  path: string,
  data: unknown,
  { token, key = randomUUID() }: { token?: string; key?: string } = {},
): Promise<APIResponse> {
  return request.post(`${API}${path}`, {
    headers: { ...auth(token ?? (await tokenFor(request, CASHIER_PIN))), 'Idempotency-Key': key },
    data,
  });
}

/** Replaces `window.print`: what `#print-root` held at that moment is pushed to `window.__printed`. */
async function stubPrint(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const w = window as unknown as { __printed: string[]; print: () => void };
    w.__printed = [];
    w.print = () => {
      w.__printed.push(document.getElementById('print-root')?.innerText ?? '');
    };
  });
}

const printed = (page: Page): Promise<string> =>
  page.evaluate(() => (window as unknown as { __printed: string[] }).__printed.join('\n---\n'));

/** A PC of the test's own: registered as an Agent does and taken out of maintenance by the owner. */
interface AgentPc {
  pcId: string;
  hwid: string;
  accessToken: string;
  signingSecret?: string;
}

async function registerAgent(request: APIRequestContext, tag: string): Promise<AgentPc> {
  const hwid = `e2e-hwid-${tag}-${Date.now()}`;
  const mac = ['02', ...randomBytes(5).toString('hex').match(/../g)!].join(':').toUpperCase();
  const ip = `10.0.${1 + (randomBytes(1)[0]! % 200)}.${1 + (randomBytes(1)[0]! % 250)}`;
  const reg = await request.post(`${API}/agents/register`, {
    headers: { 'X-Club-Key': 'e2e' },
    data: {
      hwid,
      machineName: `E2E-${tag}`.slice(0, 15),
      agentVersion: '1.0.0',
      hardware: {
        cpu: { model: 'E2E CPU', cores: 4, threads: 8 },
        gpu: [],
        ramMb: 8192,
        disks: [],
        monitors: [],
        network: { mac, ip, adapter: 'Ethernet' },
        os: { version: '10.0.22631', build: '22631' },
        peripherals: [],
      },
      ipAddress: ip,
      macAddress: mac,
    },
  });
  expect(reg.ok(), await reg.text()).toBeTruthy();
  const { pcId, accessToken, signingSecret } = (await reg.json()) as AgentPc;
  const owner = auth(await tokenFor(request, OWNER_PIN));
  const freed = await request.patch(`${API}/admin/pcs/${pcId}`, { headers: owner, data: { maintenance: false } });
  expect(freed.ok(), await freed.text()).toBeTruthy();
  return { pcId, hwid, accessToken, signingSecret };
}

/** A request of the Agent on `pc` (bearer and signature), as the signed-in player when `userToken` is given. */
async function agentCall(
  request: APIRequestContext,
  pc: AgentPc,
  method: 'GET' | 'POST',
  path: string,
  data?: Record<string, unknown>,
  userToken?: string,
): Promise<APIResponse> {
  const body = data === undefined ? '' : JSON.stringify(data);
  const target = new URL(`${API}${path}`).pathname;
  return request.fetch(`${API}${path}`, {
    method,
    headers: {
      Authorization: `Bearer ${pc.accessToken}`,
      ...(body ? { 'Content-Type': 'application/json' } : {}),
      ...(userToken ? { 'X-User-Token': userToken } : {}),
      ...signature(pc.signingSecret, method, target, body),
    },
    ...(body ? { data: body } : {}),
  });
}

interface OverviewSeat {
  pc: { id: string; number: number; status: string };
  session: { id: string; cost: { amount: number }; secondsLeft: number; isPrepaid: boolean } | null;
  user: { id: string; displayName: string; role: string } | null;
  signedIn?: boolean | null;
}

async function overview(request: APIRequestContext): Promise<{
  seats: OverviewSeat[];
  tariffs: { id: string; name: string; isPackage: boolean }[];
  guestDebts?: { userId: string; displayName: string; debt: { amount: number } }[];
}> {
  const headers = auth(await tokenFor(request, CASHIER_PIN));
  return (await request.get(`${API}/admin/overview`, { headers })).json();
}

async function seatOf(request: APIRequestContext, pcId: string): Promise<OverviewSeat> {
  const seat = (await overview(request)).seats.find((s) => s.pc.id === pcId);
  expect(seat, `seat ${pcId}`).toBeTruthy();
  return seat!;
}

async function shiftState(request: APIRequestContext): Promise<{
  shift: { id: string; openingCash: number } | null;
  x: { topUpByMethod: Record<string, number>; cashIn?: number; cashOut?: number; payouts?: number } | null;
  expectedCash?: number | null;
}> {
  const headers = auth(await tokenFor(request, OWNER_PIN));
  return (await request.get(`${API}/admin/shift`, { headers })).json();
}

/** A client of the club (no balance unless topped up), with a password to sign in on a PC. */
async function newClient(
  request: APIRequestContext,
  tag: string,
): Promise<{ id: string; username: string; displayName: string; password: string }> {
  const headers = auth(await tokenFor(request, OWNER_PIN));
  const stamp = `${Date.now()}${randomBytes(2).toString('hex')}`;
  const username = `e2e-${tag}-${stamp}`.slice(0, 32);
  const displayName = `E2E ${tag} ${stamp.slice(-6)}`;
  const password = `pw-${stamp}`;
  const res = await request.post(`${API}/admin/clients`, { headers, data: { username, displayName, password } });
  expect(res.ok(), await res.text()).toBeTruthy();
  const { client } = (await res.json()) as { client: { id: string } };
  return { id: client.id, username, displayName, password };
}

async function standardTariff(request: APIRequestContext): Promise<string> {
  const standard = (await overview(request)).tariffs.find((t) => t.name === 'Standard');
  expect(standard).toBeTruthy();
  return standard!.id;
}

/** `2100000` minor units → a pattern for `21 000 сум` with any space the formatter uses. */
function sumPattern(minor: number): RegExp {
  const whole = String(Math.round(minor / 100)).replace(/\B(?=(\d{3})+(?!\d))/g, '\\s');
  return new RegExp(`(^|[^\\d\\s])${whole}\\sсум`);
}

async function signIn(page: Page, pin: string, { shift = true }: { shift?: boolean } = {}): Promise<void> {
  if (shift) await ensureShift(page.request);
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
  await closeShift(page.request);
  await signIn(page, OWNER_PIN, { shift: false });
  // No shift: the console asks for one at once; the owner may put it off and open it on the Смена page.
  const gate = page.getByRole('dialog', { name: 'Открыть смену' });
  await expect(gate).toBeVisible();
  await gate.getByRole('button', { name: 'Позже' }).click();
  await expect(gate).toHaveCount(0);
  await nav(page).getByRole('button', { name: 'Смена', exact: true }).click();
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

test('a cashier without an open shift has to open one, with the float the last shift closed with', async ({
  page,
  request,
}) => {
  const headers = auth(await tokenFor(request, OWNER_PIN));
  // The last closed shift counted 77 000 in the drawer.
  await closeShift(request);
  expect(
    (await request.post(`${API}/admin/shift/open`, { headers, data: { openingCash: 7_700_000 } })).ok(),
  ).toBeTruthy();
  expect(
    (await request.post(`${API}/admin/shift/close`, { headers, data: { closingCash: 7_700_000 } })).ok(),
  ).toBeTruthy();

  // Without a shift the counter takes no money.
  const overview = (await (await request.get(`${API}/admin/overview`, { headers })).json()) as {
    users: { id: string }[];
  };
  const refused = await request.post(`${API}/admin/wallet/topup`, {
    headers,
    data: { userId: overview.users[0]!.id, amount: 1_000_000, method: 'cash' },
  });
  expect(refused.status()).toBe(409);
  expect(((await refused.json()) as { error: { details: { reason: string } } }).error.details.reason).toBe(
    'shiftClosed',
  );

  await signIn(page, CASHIER_PIN, { shift: false });
  const gate = page.getByRole('dialog', { name: 'Открыть смену' });
  await expect(gate).toBeVisible();
  // A cashier cannot put it off, only sign out.
  await expect(gate.getByRole('button', { name: 'Позже' })).toHaveCount(0);
  await expect(gate.getByRole('button', { name: 'Выйти' })).toBeVisible();
  await expect(gate.getByLabel('Наличные в кассе на начало')).toHaveValue('77000');
  await gate.getByRole('button', { name: 'Открыть смену' }).click();
  await expect(gate).toHaveCount(0);
  await expect(page.getByRole('button', { name: /Смена · Кассир Азиз/ })).toBeVisible();

  const state = (await (await request.get(`${API}/admin/shift`, { headers })).json()) as {
    shift: { staffName: string; openingCash: number } | null;
  };
  expect(state.shift?.staffName).toBe('Кассир Азиз');
  expect(state.shift?.openingCash).toBe(7_700_000);
});

test('a typed amount topped up by card from the client search lands in the X-report as card', async ({
  page,
  request,
}) => {
  const headers = auth(await tokenFor(request, OWNER_PIN));
  const username = `e2e-card-${Date.now()}`;
  const created = await request.post(`${API}/admin/clients`, {
    headers,
    data: { username, displayName: 'E2E Карта' },
  });
  expect(created.ok()).toBeTruthy();
  // A fresh shift: its X-report holds only this top-up.
  await closeShift(request);
  await signIn(page, CASHIER_PIN);

  // "/" jumps to the client search; the row's "Пополнить" opens the pay box without any PC.
  await expect(page.getByRole('heading', { name: 'Карта зала' })).toBeVisible();
  await page.keyboard.press('/');
  await expect(page.getByRole('combobox', { name: 'Поиск клиента' })).toBeFocused();
  await page.keyboard.type(username);
  await page.getByRole('option').filter({ hasText: username }).getByRole('button', { name: 'Пополнить' }).click();

  const sheet = page.getByRole('dialog', { name: 'Пополнить · E2E Карта' });
  const amount = sheet.getByLabel('Сумма');
  await amount.fill('45000');
  await expect(amount).toHaveValue('45 000');
  // The method button is the confirmation.
  await sheet.getByRole('button', { name: /^Карта/ }).click();
  await expect(sheet.getByRole('status')).toContainText(/Баланс пополнен · 45\s000 сум · Карта/);

  const x = (await (await request.get(`${API}/admin/shift`, { headers })).json()) as {
    x: { topUpCash: number; topUpOther: number; topUpByMethod: Record<string, number> };
  };
  expect(x.x.topUpByMethod['card']).toBe(4_500_000);
  expect(x.x.topUpByMethod['cash']).toBe(0);
  expect(x.x.topUpCash).toBe(0);
  expect(x.x.topUpOther).toBe(4_500_000);
  // The top-bar chip counts it as cashless (its name starts with «Смена ·»: the feed's «Сегодня принято» headline says
  // «безнал …» too, and at 1920 px it is on the map next to the chip).
  await expect(page.getByRole('button', { name: /^Смена · .*безнал 45\s000/ })).toBeVisible();
});

test('a client is seated from the map: the pay box takes what the balance lacks, and ending asks first', async ({
  page,
  request,
}) => {
  const headers = auth(await tokenFor(request, OWNER_PIN));
  const username = `e2e-seat-${Date.now()}`;
  expect(
    (await request.post(`${API}/admin/clients`, { headers, data: { username, displayName: 'E2E Посадка' } })).ok(),
  ).toBeTruthy();
  const overview = (await (await request.get(`${API}/admin/overview`, { headers })).json()) as {
    seats: { pc: { id: string; number: number; status: string }; session: unknown }[];
  };
  const free = overview.seats.find((s) => s.pc.status === 'free' && !s.session)!;
  await signIn(page, CASHIER_PIN);

  // A PC by its number: digits, then Enter.
  await expect(page.locator(`#seat-${free.pc.id}`)).toBeVisible();
  await page.keyboard.type(String(free.pc.number));
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: /Посадить на/ })).toBeVisible();

  await page.getByRole('combobox', { name: 'Кто' }).fill(username);
  await page.getByRole('option').filter({ hasText: username }).click();
  // A new client has nothing on the balance: the pay box holds the price and its button seats the client.
  await page.getByRole('button', { name: /^Посадить · Наличные/ }).click();
  await expect(page.getByText(/Сеанс открыт/)).toBeVisible();

  await page.getByRole('button', { name: 'Завершить сеанс', exact: true }).click();
  const confirm = page.getByRole('dialog', { name: /Завершить сеанс/ });
  await expect(confirm.getByText('Неиспользованное время вернётся на баланс')).toBeVisible();
  await confirm.getByRole('button', { name: 'Завершить сеанс', exact: true }).click();
  await expect(confirm).toHaveCount(0);
  // What the server returned stays in the panel after the PC frees up.
  await expect(page.getByText(/Сеанс завершён/)).toBeVisible();
  await expect(page.getByRole('heading', { name: /Посадить на/ })).toBeVisible();
});

// ---------------------------------------------------------------------------------------------------------------------
// Cash desk, part 2: walk-in guests, postpaid, packages, settling, drawer moves, the operations feed, printing
// ---------------------------------------------------------------------------------------------------------------------

test('a walk-in guest is seated from the map with one exact payment and the «Гость» button on that PC signs in to it', async ({
  page,
  request,
}) => {
  const pc = await registerAgent(request, 'guest');
  await stubPrint(page);
  await signIn(page, CASHIER_PIN);
  const tile = page.locator(`#seat-${pc.pcId}`);
  await tile.click();
  await expect(page.getByRole('heading', { name: /Посадить на/ })).toBeVisible();
  await page.getByRole('group', { name: 'Кто садится' }).getByRole('button', { name: 'Гость' }).click();
  await page.getByRole('button', { name: '1 ч', exact: true }).click();
  // A guest pays exactly the price: the box holds it read-only and the method button seats the guest.
  await expect(page.getByLabel('Сумма')).toHaveAttribute('readonly', '');
  await page.getByRole('button', { name: /^Посадить гостя · Наличные/ }).click();
  await expect(page.getByText(/Сеанс открыт/)).toBeVisible();
  // Nobody has pressed «Гость» on the PC yet, and the clock already runs.
  await expect(tile).toContainText('ждёт входа');

  const seat = await seatOf(request, pc.pcId);
  expect(seat.user?.role).toBe('guest');
  const login = await agentCall(request, pc, 'POST', '/auth/guest', { pcId: pc.pcId, hwid: pc.hwid });
  expect(login.status(), await login.text()).toBe(200);
  const guest = (await login.json()) as {
    accessToken: string;
    user: { id: string; role: string };
    session: { id: string } | null;
  };
  expect(guest.session?.id).toBe(seat.session?.id);
  expect(guest.user.id).toBe(seat.user?.id);
  expect(guest.user.role).toBe('guest');
  await expect(tile).not.toContainText('ждёт входа');

  // The slip of the seat names the guest and the method, and says it is not a fiscal receipt.
  await page.getByRole('button', { name: 'Чек', exact: true }).click();
  await expect.poll(() => printed(page)).toContain('Не является фискальным чеком');
  const slip = await printed(page);
  expect(slip).toContain('Гость');
  expect(slip).toContain('Наличные');

  // Ended at the desk at once: the unused time goes back in cash, from the settle sheet over the map.
  const payoutsBefore = (await shiftState(request)).x?.payouts ?? 0;
  await page.getByRole('button', { name: 'Завершить сеанс', exact: true }).click();
  const confirm = page.getByRole('dialog', { name: /Завершить сеанс/ });
  await expect(confirm.getByText('Остаток времени выдаётся наличными только при завершении на кассе')).toBeVisible();
  await confirm.getByRole('button', { name: 'Завершить сеанс', exact: true }).click();
  const settle = page.getByRole('dialog', { name: /^Выдать наличными/ });
  await settle.getByRole('button', { name: /^Выдать .+ наличными$/ }).click();
  await expect(settle.getByRole('status')).toContainText('Выдано');
  await settle.getByRole('button', { name: 'Готово' }).click();
  await expect(settle).toHaveCount(0);
  expect((await shiftState(request)).x?.payouts ?? 0).toBeGreaterThan(payoutsBefore);

  // The desk end signed the guest out of the PC.
  const after = await agentCall(request, pc, 'GET', `/wallet/${guest.user.id}/balance`, undefined, guest.accessToken);
  expect(after.status()).toBe(401);
});

test("a password login on a PC holding someone else's desk session is refused", async ({ request }) => {
  await ensureShift(request);
  const pc = await registerAgent(request, 'occupied');
  const tariffId = await standardTariff(request);
  const seated = await newClient(request, 'occ-a');
  const other = await newClient(request, 'occ-b');
  const cashier = auth(await tokenFor(request, CASHIER_PIN));
  const quote = (await (
    await request.post(`${API}/admin/quote`, {
      headers: cashier,
      data: { tariffId, pcId: pc.pcId, minutes: 60, userId: seated.id },
    })
  ).json()) as { total: { amount: number } };
  const open = await moneyPost(request, '/admin/sessions', {
    pcId: pc.pcId,
    userId: seated.id,
    tariffId,
    minutes: 60,
    payment: { amount: quote.total.amount, method: 'cash' },
  });
  expect(open.status(), await open.text()).toBe(201);
  const sessionId = ((await open.json()) as { session: { id: string } }).session.id;

  const login = (who: { username: string; password: string }): Promise<APIResponse> =>
    agentCall(request, pc, 'POST', '/auth/login', {
      kind: 'password',
      pcId: pc.pcId,
      hwid: pc.hwid,
      username: who.username,
      password: who.password,
    });
  const refused = await login(other);
  expect(refused.status()).toBe(403);
  expect(((await refused.json()) as { error: { details: { reason: string } } }).error.details.reason).toBe(
    'pcOccupied',
  );
  // The seated player still signs in to their own session.
  const own = await login(seated);
  expect(own.status(), await own.text()).toBe(200);
  expect(((await own.json()) as { session: { id: string } | null }).session?.id).toBe(sessionId);

  expect((await moneyPost(request, '/admin/sessions/end', { pcId: pc.pcId })).ok()).toBeTruthy();
});

test("a postpaid guest's bill is taken at the end with the exact pay box, and an unpaid one waits in the debts panel", async ({
  page,
  request,
}) => {
  const owner = auth(await tokenFor(request, OWNER_PIN));
  const club = (await (await request.get(`${API}/admin/club`, { headers: owner })).json()) as {
    limits: Record<string, unknown>;
  };
  const setLimits = async (extra: Record<string, unknown>): Promise<void> => {
    const res = await request.patch(`${API}/admin/club`, {
      headers: owner,
      data: { limits: { ...club.limits, ...extra } },
    });
    expect(res.ok(), await res.text()).toBeTruthy();
  };
  const first = await registerAgent(request, 'pp1');
  const second = await registerAgent(request, 'pp2');
  try {
    // Off in the settings: a guest cannot be seated postpaid.
    await setLimits({ guestPostpaid: false });
    await signIn(page, CASHIER_PIN);
    const guestMode = page.getByRole('group', { name: 'Кто садится' }).getByRole('button', { name: 'Гость' });
    const postpaid = page.getByRole('group', { name: 'Оплата' }).getByRole('button', { name: 'Постоплата' });
    await page.locator(`#seat-${first.pcId}`).click();
    await guestMode.click();
    await expect(postpaid).toBeDisabled();
    await expect(page.getByText('Постоплата для гостей выключена в настройках клуба')).toBeVisible();

    await setLimits({ guestPostpaid: true });
    await page.reload();
    const seatGuest = async (pc: AgentPc, name: string): Promise<void> => {
      await page.locator(`#seat-${pc.pcId}`).click();
      await expect(page.getByRole('heading', { name: /Посадить на/ })).toBeVisible();
      await guestMode.click();
      await page.getByLabel('Имя').fill(name);
      await postpaid.click();
      await page.getByRole('button', { name: 'Посадить · постоплата' }).click();
      await expect(page.getByText('Сеанс открыт · постоплата')).toBeVisible();
      // The bill runs from the first minute.
      await expect.poll(async () => (await seatOf(request, pc.pcId)).session?.cost.amount ?? 0).toBeGreaterThan(0);
    };
    const endSeat = async (): Promise<void> => {
      await page.getByRole('button', { name: 'Завершить сеанс', exact: true }).click();
      const confirm = page.getByRole('dialog', { name: /Завершить сеанс/ });
      await expect(confirm.getByText(/К оплате ≈/)).toBeVisible();
      await confirm.getByRole('button', { name: 'Завершить сеанс', exact: true }).click();
      await expect(confirm).toHaveCount(0);
    };
    const debtOf = async (name: string): Promise<number> =>
      (await overview(request)).guestDebts?.find((d) => d.displayName === name)?.debt.amount ?? 0;

    // At the end the settle sheet takes exactly the bill, to the tiyin.
    const name1 = `E2E Постоплата ${Date.now()}`;
    await seatGuest(first, name1);
    await endSeat();
    const settle = page.getByRole('dialog', { name: `Долг · ${name1}` });
    await expect(settle.getByLabel('Сумма')).toHaveAttribute('readonly', '');
    const debt = await debtOf(name1);
    expect(debt).toBeGreaterThan(0);
    const cashBefore = (await shiftState(request)).x?.topUpByMethod['cash'] ?? 0;
    await settle.getByRole('button', { name: /^Принять · Наличные/ }).click();
    await expect(settle.getByRole('status')).toContainText('Долг оплачен');
    await settle.getByRole('button', { name: 'Готово' }).click();
    expect(((await shiftState(request)).x?.topUpByMethod['cash'] ?? 0) - cashBefore).toBe(debt);
    expect(await debtOf(name1)).toBe(0);

    // A bill not taken at the end waits under the map and is taken from there.
    const name2 = `E2E Постоплата ${Date.now()}`;
    await seatGuest(second, name2);
    await endSeat();
    await page
      .getByRole('dialog', { name: `Долг · ${name2}` })
      .getByRole('button', { name: 'Закрыть' })
      .click();
    const row = page
      .getByRole('region', { name: 'Расчёт с гостями и долги' })
      .getByRole('listitem')
      .filter({ hasText: name2 });
    await row.getByRole('button', { name: /^Принять/ }).click();
    const later = page.getByRole('dialog', { name: `Долг · ${name2}` });
    await later.getByRole('button', { name: /^Принять · Наличные/ }).click();
    await expect(later.getByRole('status')).toContainText('Долг оплачен');
    await later.getByRole('button', { name: 'Готово' }).click();
    await expect(row).toHaveCount(0);
  } finally {
    await setLimits({});
  }
});

test('a member plays postpaid from the balance and is signed out by the desk end', async ({ page, request }) => {
  await ensureShift(request);
  const pc = await registerAgent(request, 'member-pp');
  const member = await newClient(request, 'pp-member');
  const topUp = await moneyPost(request, '/admin/wallet/topup', {
    userId: member.id,
    amount: 5_000_000,
    method: 'card',
  });
  expect(topUp.ok(), await topUp.text()).toBeTruthy();
  const balanceBefore = ((await topUp.json()) as { balance: { amount: number } }).balance.amount;
  // The player has signed in on that PC before the desk seats them.
  const login = await agentCall(request, pc, 'POST', '/auth/login', {
    kind: 'password',
    pcId: pc.pcId,
    hwid: pc.hwid,
    username: member.username,
    password: member.password,
  });
  expect(login.status(), await login.text()).toBe(200);
  const { accessToken } = (await login.json()) as { accessToken: string };

  await signIn(page, CASHIER_PIN);
  const tile = page.locator(`#seat-${pc.pcId}`);
  await tile.click();
  await page.getByRole('combobox', { name: 'Кто' }).fill(member.username);
  await page.getByRole('option').filter({ hasText: member.username }).click();
  await page.getByRole('group', { name: 'Оплата' }).getByRole('button', { name: 'Постоплата' }).click();
  await page.getByRole('button', { name: 'Посадить · постоплата' }).click();
  await expect(page.getByText('Сеанс открыт · постоплата')).toBeVisible();
  await expect(tile).toContainText('∞');
  await expect.poll(async () => (await seatOf(request, pc.pcId)).session?.cost.amount ?? 0).toBeGreaterThan(0);

  await page.getByRole('button', { name: 'Завершить сеанс', exact: true }).click();
  const confirm = page.getByRole('dialog', { name: /Завершить сеанс/ });
  await expect(confirm.getByText(/спишется с баланса/)).toBeVisible();
  await confirm.getByRole('button', { name: 'Завершить сеанс', exact: true }).click();
  await expect(page.getByText(/Сеанс завершён · списано/)).toBeVisible();
  const owner = auth(await tokenFor(request, OWNER_PIN));
  const found = (await (await request.get(`${API}/admin/clients?q=${member.username}`, { headers: owner })).json()) as {
    items: { balance: { amount: number } }[];
  };
  expect(found.items[0]?.balance.amount).toBeLessThan(balanceBefore);
  // The desk end signed the player out of that PC.
  const after = await agentCall(request, pc, 'GET', `/wallet/${member.id}/balance`, undefined, accessToken);
  expect(after.status()).toBe(401);

  // With nothing on the balance and no member debt allowed, postpaid is off for the client.
  const broke = await newClient(request, 'pp-broke');
  await page.getByRole('combobox', { name: 'Кто' }).fill(broke.username);
  await page.getByRole('option').filter({ hasText: broke.username }).click();
  await expect(page.getByRole('group', { name: 'Оплата' }).getByRole('button', { name: 'Постоплата' })).toBeDisabled();
  await expect(page.getByText('Постоплата — только с баланса, а на нём нет даже на минуту')).toBeVisible();
});

test('package tariffs are cards priced by the quote and seat for the package minutes', async ({ page, request }) => {
  await ensureShift(request);
  const owner = auth(await tokenFor(request, OWNER_PIN));
  const name = `E2E пакет ${Date.now()}`;
  const created = await request.post(`${API}/admin/tariffs`, {
    headers: owner,
    data: {
      name,
      pricePerHour: 0,
      zones: [],
      timeWindows: [],
      isPackage: true,
      packageMinutes: 180,
      packagePrice: 2_500_000,
    },
  });
  expect(created.ok(), await created.text()).toBeTruthy();
  const pkg = ((await created.json()) as { tariff: { id: string } }).tariff;
  const pc = await registerAgent(request, 'pkg');
  const client = await newClient(request, 'pkg');
  const cashier = auth(await tokenFor(request, CASHIER_PIN));
  const quoteOf = async (
    tariffId: string,
  ): Promise<{ total: { amount: number }; rule?: string | null; minutes?: number }> =>
    (
      await request.post(`${API}/admin/quote`, {
        headers: cashier,
        data: { tariffId, pcId: pc.pcId, minutes: 60, userId: client.id },
      })
    ).json();
  // Always compared with the server's own quote: an earlier test left a day rate and an all-day happy hour.
  const quote = await quoteOf(pkg.id);
  expect(quote.minutes).toBe(180);
  const night = (await overview(request)).tariffs.find((t) => t.name === 'Night Pack (5h)');
  expect(night).toBeTruthy();
  const nightQuote = await quoteOf(night!.id);

  await signIn(page, CASHIER_PIN);
  await page.locator(`#seat-${pc.pcId}`).click();
  await page.getByRole('combobox', { name: 'Кто' }).fill(client.username);
  await page.getByRole('option').filter({ hasText: client.username }).click();
  const card = page.getByRole('button', { name: new RegExp(name) });
  await expect(card).toContainText('3 ч');
  await expect(card).toContainText(sumPattern(quote.total.amount));
  // Outside its hours (or zone) a package card is off, exactly when the quote names the rule.
  const nightCard = page.getByRole('button', { name: /Night Pack/ });
  if (nightQuote.rule) await expect(nightCard).toBeDisabled();
  else await expect(nightCard).toBeEnabled();

  await card.click();
  await page.getByRole('button', { name: /^Посадить · Наличные/ }).click();
  await expect(page.getByText(/Сеанс открыт/)).toBeVisible();
  expect((await seatOf(request, pc.pcId)).session?.secondsLeft ?? 0).toBeGreaterThan(10_700);

  await page.getByRole('button', { name: 'Продлить', exact: true }).click();
  const extend = page.getByRole('dialog', { name: /^Продлить/ });
  await expect(extend.getByRole('button', { name: /Ещё пакет/ })).toBeVisible();
  await extend.getByRole('button', { name: 'Закрыть' }).click();

  await page.getByRole('button', { name: 'Завершить сеанс', exact: true }).click();
  const confirm = page.getByRole('dialog', { name: /Завершить сеанс/ });
  await expect(confirm.getByText('Время пакета не возвращается')).toBeVisible();
  await confirm.getByRole('button', { name: 'Завершить сеанс', exact: true }).click();
  await expect(confirm).toHaveCount(0);
});

test('cash in and out move the expected cash, need a reason, and land in the Z report', async ({ page, request }) => {
  const owner = auth(await tokenFor(request, OWNER_PIN));
  // A fresh shift with nothing in the drawer.
  await closeShift(request);
  await stubPrint(page);
  await signIn(page, CASHIER_PIN);
  const chip = page.getByRole('button', { name: /^Смена · .* · в кассе/ });
  await expect(chip).toHaveAccessibleName(/в кассе 0 ·/);
  const menu = page.getByRole('button', { name: 'Внесение и изъятие' });

  await menu.click();
  await page.getByRole('menuitem', { name: 'Внесение' }).click();
  const cashIn = page.getByRole('dialog', { name: 'Внесение' });
  await cashIn.getByLabel('Сумма').fill('50000');
  await cashIn.getByRole('button', { name: 'Размен' }).click();
  await cashIn.getByRole('button', { name: 'Внести' }).click();
  await expect(cashIn.getByRole('status')).toContainText('Внесено');
  await cashIn.getByRole('button', { name: 'Готово' }).click();
  await expect(chip).toHaveAccessibleName(/в кассе 50\s000/);

  await menu.click();
  await page.getByRole('menuitem', { name: 'Изъятие' }).click();
  const cashOut = page.getByRole('dialog', { name: 'Изъятие' });
  await cashOut.getByLabel('Сумма').fill('60000');
  await cashOut.getByRole('button', { name: 'Инкассация' }).click();
  await cashOut.getByRole('button', { name: 'Изъять' }).click();
  // No more than the drawer holds.
  await expect(cashOut.getByText(/В кассе только 50\s000/)).toBeVisible();
  await cashOut.getByLabel('Сумма').fill('30000');
  await cashOut.getByRole('button', { name: 'Изъять' }).click();
  await expect(cashOut.getByRole('status')).toContainText('Изъято');
  await cashOut.getByRole('button', { name: 'Готово' }).click();
  await expect(chip).toHaveAccessibleName(/в кассе 20\s000/);

  // The route: «Другое» needs a note, and every move needs its Idempotency-Key.
  expect(
    (await moneyPost(request, '/admin/shift/cash', { kind: 'in', amount: 100_000, reasonCode: 'other' })).status(),
  ).toBe(400);
  const keyless = await request.post(`${API}/admin/shift/cash`, {
    headers: auth(await tokenFor(request, CASHIER_PIN)),
    data: { kind: 'in', amount: 100_000, reasonCode: 'change' },
  });
  expect(keyless.status()).toBe(400);

  // Closed with exactly what the drawer should hold: the Z has the moves and no difference.
  const state = await shiftState(request);
  expect(state.expectedCash).toBe((state.shift?.openingCash ?? 0) + 2_000_000);
  await nav(page).getByRole('button', { name: 'Смена', exact: true }).click();
  await page.getByLabel('Посчитано в кассе').fill(String((state.expectedCash ?? 0) / 100));
  await page.getByRole('button', { name: 'Закрыть смену' }).click();
  await expect(page.getByText('Смена закрыта')).toBeVisible();
  const z = page.locator('section').filter({ has: page.getByRole('heading', { name: /^Z-отчёт/ }) });
  await expect(z.getByRole('group', { name: 'Внесения' })).toContainText(/50\s000/);
  await expect(z.getByRole('group', { name: 'Изъятия' })).toContainText(/30\s000/);
  await expect(z.getByRole('group', { name: 'Расхождение' }).locator('.num-dot')).toHaveText('0');
  await z.getByRole('button', { name: 'Печать Z' }).click();
  await expect.poll(() => printed(page)).toContain('Z-отчёт');
  expect(await printed(page)).toContain('Инкассация');

  // A move sent again under its key is the same move.
  await ensureShift(request);
  const key = randomUUID();
  const move = { kind: 'in', amount: 100_000, reasonCode: 'change' };
  const once = await moneyPost(request, '/admin/shift/cash', move, { key });
  expect(once.ok(), await once.text()).toBeTruthy();
  const twice = await moneyPost(request, '/admin/shift/cash', move, { key });
  expect(twice.ok()).toBeTruthy();
  expect(twice.headers()['idempotent-replayed']).toBe('true');
  expect((await shiftState(request)).x?.cashIn).toBe(100_000);

  // The owner can keep cash-outs to himself.
  const club = (await (await request.get(`${API}/admin/club`, { headers: owner })).json()) as {
    limits: Record<string, unknown>;
  };
  const patched = await request.patch(`${API}/admin/club`, {
    headers: owner,
    data: { limits: { ...club.limits, cashOutOwnerOnly: true } },
  });
  expect(patched.ok(), await patched.text()).toBeTruthy();
  try {
    await page.reload();
    await menu.click();
    await page.getByRole('menuitem', { name: 'Изъятие' }).click();
    const refused = page.getByRole('dialog', { name: 'Изъятие' });
    await refused.getByLabel('Сумма').fill('500');
    await refused.getByRole('button', { name: 'Инкассация' }).click();
    await refused.getByRole('button', { name: 'Изъять' }).click();
    await expect(refused.getByText(/Только владелец/)).toBeVisible();
  } finally {
    await request.patch(`${API}/admin/club`, { headers: owner, data: { limits: club.limits } });
  }
});

test("the operations feed shows each desk operation once with who, what and how it was paid, and today's total", async ({
  page,
  request,
}) => {
  await ensureShift(request);
  const pc = await registerAgent(request, 'feed');
  const tariffId = await standardTariff(request);
  const seated = await newClient(request, 'feed-seat');
  const topped = await newClient(request, 'feed-card');
  const cashier = auth(await tokenFor(request, CASHIER_PIN));
  const quote = (await (
    await request.post(`${API}/admin/quote`, {
      headers: cashier,
      data: { tariffId, pcId: pc.pcId, minutes: 60, userId: seated.id },
    })
  ).json()) as { total: { amount: number } };
  // A seat paid in cash, a card top-up of another client, cash put into the drawer.
  const open = await moneyPost(request, '/admin/sessions', {
    pcId: pc.pcId,
    userId: seated.id,
    tariffId,
    minutes: 60,
    payment: { amount: quote.total.amount, method: 'cash' },
  });
  expect(open.status(), await open.text()).toBe(201);
  const card = await moneyPost(request, '/admin/wallet/topup', {
    userId: topped.id,
    amount: 4_500_000,
    method: 'card',
  });
  expect(card.ok(), await card.text()).toBeTruthy();
  const note = `E2E лента ${Date.now()}`;
  const moved = await moneyPost(request, '/admin/shift/cash', {
    kind: 'in',
    amount: 2_000_000,
    reasonCode: 'change',
    note,
  });
  expect(moved.ok(), await moved.text()).toBeTruthy();

  await stubPrint(page);
  await signIn(page, CASHIER_PIN);
  // 1920 px: the feed is a column of its own.
  const feed = page.locator('[data-feed="column"]');
  await expect(feed).toBeVisible();
  const seatRow = feed.getByRole('listitem').filter({ hasText: seated.displayName });
  await expect(seatRow).toHaveCount(1);
  await expect(seatRow).toContainText('Посадка');
  await expect(seatRow).toContainText('Наличные');
  await expect(seatRow).not.toContainText('Пополнение');
  const cardRow = feed.getByRole('listitem').filter({ hasText: topped.displayName });
  await expect(cardRow).toContainText('Пополнение');
  await expect(cardRow).toContainText('Карта');
  await expect(feed.getByRole('listitem').filter({ hasText: note })).toContainText('Размен');

  // Today's money covers at least these two payments.
  const headline = (await feed.getByRole('button', { name: /Сегодня принято/ }).textContent()) ?? '';
  const taken = Number(/Сегодня принято ([\d\s]+)/.exec(headline)?.[1]?.replace(/\s/g, '') ?? '0');
  expect(taken * 100).toBeGreaterThanOrEqual(quote.total.amount + 4_500_000);

  // A reprint is a «Копия».
  await seatRow.getByRole('button', { name: 'Печать копии' }).click();
  await expect.poll(() => printed(page)).toContain('Копия');

  // Below 1800 px the feed fills the right panel while no seat is picked.
  await page.setViewportSize({ width: 1600, height: 900 });
  await expect(page.locator('[data-feed="column"]')).toHaveCount(0);
  await expect(page.locator('[data-feed="panel"]')).toBeVisible();

  expect((await moneyPost(request, '/admin/sessions/end', { pcId: pc.pcId })).ok()).toBeTruthy();
});
