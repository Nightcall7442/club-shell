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

/**
 * A request of the Agent on `pc` (bearer and signature), as the signed-in player when `userToken` is given; `headers`
 * adds an `Idempotency-Key` where the route needs one.
 */
async function agentCall(
  request: APIRequestContext,
  pc: AgentPc,
  method: 'GET' | 'POST',
  path: string,
  data?: Record<string, unknown>,
  userToken?: string,
  headers: Record<string, string> = {},
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
      ...headers,
    },
    ...(body ? { data: body } : {}),
  });
}

/**
 * The Agent's heartbeat (a full `HeartbeatRequest`): a registered PC is offline until its first one, on the server and
 * on the mock. `currentSessionId` and `offlineQueue` are what a move onto the PC is checked against (D-59);
 * `runningGames` with `currentSessionId` is the game the map shows on the seat (D-71); `gamesVolume` the games disk
 * «Состояние ПК» shows (D-73) and `antiCheat` the Vanguard state `GET /games` follows (D-74), left out when not given
 * (an older Agent).
 */
async function heartbeat(
  request: APIRequestContext,
  pc: AgentPc,
  {
    currentSessionId = null,
    offlineQueue = 0,
    runningGames = [],
    gamesVolume,
    antiCheat,
  }: {
    currentSessionId?: string | null;
    offlineQueue?: number;
    runningGames?: { gameId: string; pid: number; startedAt: string }[];
    gamesVolume?: { owner: string; mounted?: boolean | null; driveLetter?: string | null; since?: string | null };
    antiCheat?: { vanguardInstalled?: boolean | null; vanguardLoaded?: boolean | null };
  } = {},
): Promise<void> {
  const res = await agentCall(request, pc, 'POST', `/agents/${pc.pcId}/heartbeat`, {
    status: 'free',
    currentSessionId,
    agentVersion: '1.0.16',
    shellVersion: '1.0.16',
    uptimeSec: 600,
    ipAddress: '10.0.0.10',
    policyVersion: 0,
    runningGames,
    offlineQueue,
    shellConnected: true,
    ...(gamesVolume ? { gamesVolume } : {}),
    ...(antiCheat ? { antiCheat } : {}),
  });
  expect(res.ok(), await res.text()).toBeTruthy();
}

interface OverviewSeat {
  pc: { id: string; name: string; number: number; status: string };
  session: { id: string; cost: { amount: number }; secondsLeft: number; isPrepaid: boolean } | null;
  user: { id: string; displayName: string; role: string; balance: { amount: number } } | null;
  signedIn?: boolean | null;
  game?: { id: string; title: string; coverUrl: string | null; heroUrl: string | null } | null;
}

interface CallRow {
  id: string;
  pcId: string;
  category: string;
  message: string | null;
  status: string;
  repeat: boolean;
}

async function overview(request: APIRequestContext): Promise<{
  seats: OverviewSeat[];
  tariffs: { id: string; name: string; isPackage: boolean }[];
  guestDebts?: { userId: string; displayName: string; debt: { amount: number } }[];
  calls?: CallRow[];
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
  x: {
    topUpByMethod: Record<string, number>;
    cashIn?: number;
    cashOut?: number;
    payouts?: number;
    shop?: number;
    shopByMethod?: Record<string, number>;
    shopVoidCount?: number;
  } | null;
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

interface ProductRow {
  id: string;
  title: string;
  price: { amount: number };
  stockQty?: number | null;
  inStock: boolean;
}

async function products(request: APIRequestContext): Promise<ProductRow[]> {
  const headers = auth(await tokenFor(request, CASHIER_PIN));
  return ((await (await request.get(`${API}/admin/products`, { headers })).json()) as { items: ProductRow[] }).items;
}

async function productOf(request: APIRequestContext, id: string): Promise<ProductRow | undefined> {
  return (await products(request)).find((p) => p.id === id);
}

/** A product of the test's own, added by the owner at the desk: tracked stock, a fixed price. */
async function newProduct(
  request: APIRequestContext,
  tag: string,
  { price = 500_000, stockQty = 10 }: { price?: number; stockQty?: number | null } = {},
): Promise<ProductRow> {
  const headers = auth(await tokenFor(request, OWNER_PIN));
  const res = await request.post(`${API}/admin/products`, {
    headers,
    data: { title: `E2E ${tag} ${Date.now()}`, category: 'drink', price, stockQty },
  });
  expect(res.status(), await res.text()).toBe(201);
  return ((await res.json()) as { product: ProductRow }).product;
}

/** A bar sale through the API (D-52): paid by `method` (cash by default), or from `userId`'s balance. */
async function barSale(
  request: APIRequestContext,
  lines: { product: ProductRow; qty: number }[],
  {
    method = 'cash',
    userId,
    key,
    saleId = randomUUID(),
  }: { method?: string; userId?: string; key?: string; saleId?: string } = {},
): Promise<APIResponse> {
  const total = lines.reduce((sum, l) => sum + l.product.price.amount * l.qty, 0);
  return moneyPost(
    request,
    '/admin/shop/sales',
    {
      saleId,
      items: lines.map((l) => ({ productId: l.product.id, qty: l.qty })),
      total,
      ...(userId ? { userId } : {}),
      ...(method === 'balance' ? {} : { payment: { method, amount: total } }),
    },
    key ? { key } : {},
  );
}

/** Today's money of the feed (the club's local day). */
async function today(request: APIRequestContext): Promise<{ taken: number }> {
  const headers = auth(await tokenFor(request, CASHIER_PIN));
  const feed = (await (await request.get(`${API}/admin/shift/operations?limit=1`, { headers })).json()) as {
    today: { taken: number };
  };
  return feed.today;
}

interface OperationRow {
  id: string;
  kind: string;
  drawer: number;
  sessionId: string | null;
  saleId?: string | null;
  client: { id: string } | null;
  lines?: { title: string; qty: number }[] | null;
  voided?: boolean | null;
}

/** Every row of the open shift's feed (of `kinds`, a comma list), newest first. */
async function shiftOperations(request: APIRequestContext, kinds?: string): Promise<OperationRow[]> {
  const headers = auth(await tokenFor(request, CASHIER_PIN));
  const out: OperationRow[] = [];
  let before: string | null = null;
  for (let i = 0; i < 50; i += 1) {
    const qs = new URLSearchParams({ limit: '200' });
    if (kinds) qs.set('kinds', kinds);
    if (before) qs.set('before', before);
    const feed = (await (await request.get(`${API}/admin/shift/operations?${qs}`, { headers })).json()) as {
      items: OperationRow[];
      next: string | null;
    };
    out.push(...feed.items);
    if (!feed.next) break;
    before = feed.next;
  }
  return out;
}

/** A client seated by the desk on `pc` for an hour, paid in cash: the session's id. */
async function seatMember(request: APIRequestContext, pc: AgentPc, userId: string): Promise<string> {
  const tariffId = await standardTariff(request);
  const quote = (await (
    await request.post(`${API}/admin/quote`, {
      headers: auth(await tokenFor(request, CASHIER_PIN)),
      data: { tariffId, pcId: pc.pcId, minutes: 60, userId },
    })
  ).json()) as { total: { amount: number } };
  const open = await moneyPost(request, '/admin/sessions', {
    pcId: pc.pcId,
    userId,
    tariffId,
    minutes: 60,
    payment: { amount: quote.total.amount, method: 'cash' },
  });
  expect(open.status(), await open.text()).toBe(201);
  return ((await open.json()) as { session: { id: string } }).session.id;
}

/** Ends the open session of `pcId` at the desk, when there is one. */
async function endAt(request: APIRequestContext, pcId: string): Promise<void> {
  if (!(await seatOf(request, pcId)).session) return;
  const res = await moneyPost(request, '/admin/sessions/end', { pcId });
  expect(res.ok(), await res.text()).toBeTruthy();
}

/** A move through the API (D-59), with its own key. */
async function moveSession(
  request: APIRequestContext,
  data: { fromPcId: string; toPcId: string; sessionId?: string; tariffId?: string },
): Promise<APIResponse> {
  return moneyPost(request, '/admin/sessions/move', data);
}

/** The reason of a refusal: `details.reason`, else `details.rule`. */
async function reasonOf(res: APIResponse): Promise<string | undefined> {
  const body = (await res.json()) as { error?: { details?: { reason?: string; rule?: string } } };
  return body.error?.details?.reason ?? body.error?.details?.rule;
}

/**
 * Replaces Web Audio with a counter: every oscillator the console starts is one beep in `window.__beeps`. The context
 * reports itself running, as after the sign-in's key presses.
 */
async function stubAudio(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const w = window as unknown as { __beeps: number; AudioContext: unknown };
    w.__beeps = 0;
    const param = (): Record<string, unknown> => {
      const p: Record<string, unknown> = { value: 0 };
      for (const name of ['setValueAtTime', 'linearRampToValueAtTime', 'exponentialRampToValueAtTime'])
        p[name] = () => p;
      return p;
    };
    class FakeAudioContext {
      state = 'running';
      currentTime = 0;
      destination = {};
      onstatechange: (() => void) | null = null;
      resume(): Promise<void> {
        return Promise.resolve();
      }
      createGain(): Record<string, unknown> {
        return { gain: param(), connect: () => undefined };
      }
      createOscillator(): Record<string, unknown> {
        return {
          type: 'sine',
          frequency: param(),
          connect: () => undefined,
          start: () => {
            w.__beeps += 1;
          },
          stop: () => undefined,
        };
      }
    }
    w.AudioContext = FakeAudioContext;
  });
}

const beeps = (page: Page): Promise<number> => page.evaluate(() => (window as unknown as { __beeps: number }).__beeps);

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
  // A bar sale for cash and its void (cash desk part 3): one row each.
  const drink = await newProduct(request, 'feed');
  const sold = await barSale(request, [{ product: drink, qty: 1 }]);
  expect(sold.status(), await sold.text()).toBe(201);
  const saleId = ((await sold.json()) as { sale: { id: string } }).sale.id;
  const voided = await moneyPost(request, `/admin/shop/sales/${saleId}/void`, { reasonCode: 'mistake' });
  expect(voided.ok(), await voided.text()).toBeTruthy();

  await stubPrint(page);
  await signIn(page, CASHIER_PIN);
  // 1920 px: the feed is a column of its own; today's money is the «Сегодня принято» tile of the KPI strip above it.
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
  const barRows = feed.getByRole('listitem').filter({ hasText: drink.title });
  await expect(barRows.filter({ hasText: 'Продажа бара' })).toHaveCount(1);
  await expect(barRows.filter({ hasText: 'Продажа бара' })).toContainText('аннулирован');
  await expect(barRows.filter({ hasText: 'Аннулирование' })).toHaveCount(1);
  // The feed's drawer column adds up to the cash the drawer should hold (DESIGN:1732).
  const rows = await shiftOperations(request);
  expect(rows.filter((r) => r.saleId === saleId)).toHaveLength(2);
  expect(rows.reduce((sum, r) => sum + r.drawer, 0)).toBe((await shiftState(request)).expectedCash);

  // Today's money (the KPI tile, which opens the split by method) covers at least these two payments.
  const headline = (await page.getByRole('button', { name: /^Сегодня принято/ }).textContent()) ?? '';
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

// ---------------------------------------------------------------------------------------------------------------------
// Cash desk part 3: the bar, desk products, moving a session, the players' calls, commands to several PCs
// ---------------------------------------------------------------------------------------------------------------------

/** A seeded product by the start of its title, with at least `qty` on the shelf (the owner receives more if needed). */
async function shelf(request: APIRequestContext, title: string, qty: number): Promise<ProductRow> {
  const found = (await products(request)).find((p) => p.title.startsWith(title));
  expect(found, title).toBeTruthy();
  if (found!.stockQty != null && found!.stockQty < qty) {
    const owner = auth(await tokenFor(request, OWNER_PIN));
    const res = await request.post(`${API}/admin/products/${found!.id}/receive`, { headers: owner, data: { qty: 20 } });
    expect(res.ok(), await res.text()).toBeTruthy();
  }
  return (await productOf(request, found!.id))!;
}

const cartOf = (page: Page) => page.getByRole('complementary', { name: 'Корзина' });
const goods = (page: Page) => page.getByRole('list', { name: 'Товары' });

test('the bar sells to a walk-in for cash: stock goes down, X, expected cash and «Сегодня принято» go up, the receipt lists the goods', async ({
  page,
  request,
}) => {
  await ensureShift(request);
  const cola = await shelf(request, 'Coca-Cola', 3);
  const lays = await shelf(request, "Lay's", 3);
  const total = cola.price.amount * 2 + lays.price.amount;
  const before = await shiftState(request);
  const takenBefore = (await today(request)).taken;

  await stubPrint(page);
  await signIn(page, CASHIER_PIN);
  await nav(page).getByRole('button', { name: 'Бар', exact: true }).click();
  const cart = cartOf(page);
  await goods(page)
    .getByRole('button', { name: /^Coca-Cola/ })
    .click();
  await goods(page)
    .getByRole('button', { name: /^Coca-Cola/ })
    .click();
  // Typing in the search after the first item takes no payment.
  await page.getByPlaceholder('Поиск товара').fill("Lay's");
  await goods(page)
    .getByRole('button', { name: /^Lay's/ })
    .click();
  await expect(cart).toContainText(sumPattern(total));
  await expect(cart.getByRole('status')).toHaveCount(0);
  await cart.getByRole('button', { name: /^Продать · Наличные/ }).click();
  await expect(cart.getByRole('status')).toContainText('Продано');
  // The next walk-in starts at once: the buyer is «Гость» again.
  await expect(cart.getByRole('group', { name: 'Покупатель' }).getByRole('button', { name: 'Гость' })).toHaveAttribute(
    'aria-pressed',
    'true',
  );

  // The slip prints from «Чек» (printing right after a sale is a console setting, off by default); it lists the goods
  // and says it is not fiscal.
  await cart.getByRole('status').getByRole('button', { name: 'Чек', exact: true }).click();
  await expect.poll(() => printed(page)).toContain('Не является фискальным чеком');
  const slip = await printed(page);
  expect(slip).toContain(`${cola.title} ×2`);
  expect(slip).toContain(lays.title);

  expect((await productOf(request, cola.id))?.stockQty).toBe((cola.stockQty ?? 0) - 2);
  expect((await productOf(request, lays.id))?.stockQty).toBe((lays.stockQty ?? 0) - 1);
  const after = await shiftState(request);
  expect((after.x?.shopByMethod?.cash ?? 0) - (before.x?.shopByMethod?.cash ?? 0)).toBe(total);
  expect((after.expectedCash ?? 0) - (before.expectedCash ?? 0)).toBe(total);
  expect((await today(request)).taken - takenBefore).toBe(total);

  // One «Продажа бара» row, and its cash is in the drawer.
  expect((await shiftOperations(request, 'shopSale'))[0]?.drawer).toBe(total);
  await nav(page).getByRole('button', { name: 'Карта', exact: true }).click();
  const row = page.locator('[data-feed="column"]').getByRole('listitem').filter({ hasText: 'Продажа бара' }).first();
  await expect(row).toContainText(`${cola.title} ×2`);
});

test("the bar charges a seated client's balance from the seat panel; a short balance pays the cart by card under the client's name", async ({
  page,
  request,
}) => {
  await ensureShift(request);
  const pc = await registerAgent(request, 'bar-seat');
  const member = await newClient(request, 'bar-member');
  const topUp = await moneyPost(request, '/admin/wallet/topup', {
    userId: member.id,
    amount: 3_000_000,
    method: 'cash',
  });
  expect(topUp.ok(), await topUp.text()).toBeTruthy();
  await seatMember(request, pc, member.id);
  const water = await shelf(request, 'Still water', 2);
  const shirt = await shelf(request, 'Club T-shirt', 2);
  const balance = async (): Promise<number> => (await seatOf(request, pc.pcId)).user?.balance.amount ?? 0;
  const balanceBefore = await balance();
  expect(balanceBefore).toBeGreaterThanOrEqual(water.price.amount);
  expect(balanceBefore - water.price.amount).toBeLessThan(shirt.price.amount);

  await stubPrint(page);
  await signIn(page, CASHIER_PIN);
  await page.locator(`#seat-${pc.pcId}`).click();
  await page.locator('main').getByRole('button', { name: 'Бар', exact: true }).click();
  const cart = cartOf(page);
  // The seat's player is the buyer.
  await expect(cart).toContainText(member.displayName);
  await goods(page)
    .getByRole('button', { name: /^Still water/ })
    .click();
  await cart.getByRole('button', { name: /^Списать с баланса/ }).click();
  await expect(cart.getByRole('status')).toContainText('с баланса');
  expect(await balance()).toBe(balanceBefore - water.price.amount);

  // The same client with a cart the balance does not cover: paid by card, under their name, the balance untouched.
  const x0 = await shiftState(request);
  await cart.getByRole('group', { name: 'Покупатель' }).getByRole('button', { name: 'Клиент' }).click();
  await cart.getByRole('combobox', { name: 'Клиент' }).fill(member.username);
  await page.getByRole('option').filter({ hasText: member.username }).click();
  await expect(cart).toContainText(member.displayName);
  await goods(page)
    .getByRole('button', { name: /^Club T-shirt/ })
    .click();
  await expect(cart).toContainText('Не хватает');
  await cart.getByRole('button', { name: /^Продать · Карта/ }).click();
  await expect(cart.getByRole('status')).toContainText('Продано');
  expect(await balance()).toBe(balanceBefore - water.price.amount);
  const x1 = await shiftState(request);
  expect(x1.x?.topUpByMethod).toEqual(x0.x?.topUpByMethod);
  expect((x1.x?.shopByMethod?.card ?? 0) - (x0.x?.shopByMethod?.card ?? 0)).toBe(shirt.price.amount);
  expect((await shiftOperations(request, 'shopSale'))[0]?.client?.id).toBe(member.id);
  await endAt(request, pc.pcId);
});

test('the bar refuses more than is in stock and a changed price, and books nothing; the same cart is booked once', async ({
  page,
  request,
}) => {
  await ensureShift(request);
  const owner = auth(await tokenFor(request, OWNER_PIN));
  const item = await newProduct(request, 'stock', { price: 500_000, stockQty: 5 });
  const x0 = await shiftState(request);

  await signIn(page, CASHIER_PIN);
  await page.goto('/#/bar');
  const tile = goods(page).getByRole('button', { name: new RegExp(`^${item.title}`) });
  await tile.click();
  await tile.click();
  // The owner counts the shelf meanwhile: one left.
  const counted = await request.patch(`${API}/admin/products/${item.id}`, { headers: owner, data: { stockQty: 1 } });
  expect(counted.ok(), await counted.text()).toBeTruthy();
  const cart = cartOf(page);
  await cart.getByRole('button', { name: /^Продать · Наличные/ }).click();
  await expect(cart.getByText(/осталось 1/).first()).toBeVisible();
  await expect(cart.locator(`[data-line="${item.id}"] [data-qty]`)).toHaveText('1');
  expect((await shiftState(request)).x?.shopByMethod?.cash ?? 0).toBe(x0.x?.shopByMethod?.cash ?? 0);
  expect((await productOf(request, item.id))?.stockQty).toBe(1);

  // A total the server does not price so books nothing.
  const stale = await moneyPost(request, '/admin/shop/sales', {
    saleId: randomUUID(),
    items: [{ productId: item.id, qty: 1 }],
    total: item.price.amount + 100,
    payment: { method: 'cash', amount: item.price.amount + 100 },
  });
  expect(stale.status()).toBe(409);
  expect(await reasonOf(stale)).toBe('priceChanged');

  // One cart is booked once: its saleId under a new key is `saleExists`, its key with another body is refused.
  const saleId = randomUUID();
  const key = randomUUID();
  const first = await barSale(request, [{ product: item, qty: 1 }], { saleId, key });
  expect(first.status(), await first.text()).toBe(201);
  const again = await barSale(request, [{ product: item, qty: 1 }], { saleId });
  expect(again.status()).toBe(409);
  expect(await reasonOf(again)).toBe('saleExists');
  const reused = await barSale(request, [{ product: item, qty: 1 }], { key });
  expect(reused.status()).toBe(409);
  expect(await reasonOf(reused)).toBe('idempotencyKeyReused');
  expect((await productOf(request, item.id))?.stockQty).toBe(0);
});

test('a bar sale is voided with a reason in its shift: stock and expected cash come back, a second void is refused', async ({
  page,
  request,
}) => {
  await ensureShift(request);
  const item = await newProduct(request, 'void', { price: 600_000, stockQty: 5 });
  const sold = await barSale(request, [{ product: item, qty: 2 }]);
  expect(sold.status(), await sold.text()).toBe(201);
  const saleId = ((await sold.json()) as { sale: { id: string } }).sale.id;
  const before = await shiftState(request);

  await stubPrint(page);
  await signIn(page, CASHIER_PIN);
  const row = page
    .locator('[data-feed="column"]')
    .getByRole('listitem')
    .filter({ hasText: item.title })
    .filter({ hasText: 'Продажа бара' });
  await row.getByRole('button', { name: 'Аннулировать…' }).click();
  const sheet = page.getByRole('dialog', { name: 'Аннулировать продажу' });
  await sheet.getByRole('button', { name: 'Ошибка кассира' }).click();
  await sheet.getByRole('button', { name: 'Аннулировать', exact: true }).click();
  await expect(sheet.getByRole('status')).toContainText('Аннулировано');
  await sheet.getByRole('button', { name: 'Готово' }).click();
  await expect(row).toContainText('аннулирован');
  await expect(row.getByRole('button', { name: 'Аннулировать…' })).toHaveCount(0);

  expect((await productOf(request, item.id))?.stockQty).toBe(5);
  const after = await shiftState(request);
  expect((before.expectedCash ?? 0) - (after.expectedCash ?? 0)).toBe(item.price.amount * 2);
  expect((after.x?.shopVoidCount ?? 0) - (before.x?.shopVoidCount ?? 0)).toBe(1);
  const second = await moneyPost(request, `/admin/shop/sales/${saleId}/void`, { reasonCode: 'mistake' });
  expect(second.status()).toBe(409);
  expect(await reasonOf(second)).toBe('alreadyVoided');
});

test('the owner adds a product at the desk and it sells at the bar; a price change keeps the stock; archived it is gone', async ({
  page,
  request,
}) => {
  await ensureShift(request);
  await stubPrint(page);
  // A cashier sees no «Новый товар».
  await signIn(page, CASHIER_PIN);
  await page.goto('/#/shop');
  await expect(page.getByRole('heading', { name: 'Магазин и склад' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Новый товар' })).toHaveCount(0);

  await signIn(page, OWNER_PIN);
  await page.goto('/#/shop');
  await page.getByRole('button', { name: 'Новый товар' }).click();
  const sheet = page.getByRole('dialog', { name: 'Новый товар' });
  const title = `E2E чай ${Date.now()}`;
  await sheet.getByLabel('Название').fill(title);
  await sheet.getByLabel('Цена').fill('7000');
  await sheet.getByLabel('Остаток').fill('4');
  await sheet.getByRole('button', { name: 'Добавить' }).click();
  await expect(page.getByText(`Товар добавлен · ${title}`)).toBeVisible();
  const created = (await products(request)).find((p) => p.title === title);
  expect(created?.price.amount).toBe(700_000);
  expect(created?.stockQty).toBe(4);

  // Sold at the bar.
  await nav(page).getByRole('button', { name: 'Бар', exact: true }).click();
  await goods(page)
    .getByRole('button', { name: new RegExp(`^${title}`) })
    .click();
  await cartOf(page)
    .getByRole('button', { name: /^Продать · Наличные/ })
    .click();
  await expect(cartOf(page).getByRole('status')).toContainText('Продано');
  expect((await productOf(request, created!.id))?.stockQty).toBe(3);

  // The bar sells one more while the owner edits the price: the save does not write the old quantity back.
  await nav(page).getByRole('button', { name: 'Магазин и склад', exact: true }).click();
  await page.getByRole('row').filter({ hasText: title }).click();
  const panel = page.locator('section').filter({ has: page.getByRole('heading', { name: title }) });
  await expect(panel.getByLabel('Остаток')).toHaveValue('3');
  const sold = await barSale(request, [{ product: created!, qty: 1 }]);
  expect(sold.status(), await sold.text()).toBe(201);
  await panel.getByLabel('Цена').fill('8000');
  await panel.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await expect(panel.getByText('Сохранено')).toBeVisible();
  const repriced = await productOf(request, created!.id);
  expect(repriced?.price.amount).toBe(800_000);
  expect(repriced?.stockQty).toBe(2);

  // Archived: gone from the list and from the bar.
  await panel.getByRole('button', { name: 'В архив' }).click();
  await page
    .getByRole('dialog', { name: `В архив · ${title}` })
    .getByRole('button', { name: 'В архив' })
    .click();
  await expect(page.getByRole('row').filter({ hasText: title })).toHaveCount(0);
  expect(await productOf(request, created!.id)).toBeUndefined();
  await nav(page).getByRole('button', { name: 'Бар', exact: true }).click();
  await expect(goods(page).getByRole('button').first()).toBeVisible();
  await expect(goods(page).getByRole('button', { name: new RegExp(`^${title}`) })).toHaveCount(0);
});

test('a session is moved to another PC with its time and money, and the old PC is told it ended', async ({
  page,
  request,
}) => {
  await ensureShift(request);
  const a = await registerAgent(request, 'move-a');
  const b = await registerAgent(request, 'move-b');
  await heartbeat(request, b);
  const member = await newClient(request, 'move');
  const sessionId = await seatMember(request, a, member.id);
  const before = await seatOf(request, a.pcId);
  const t0 = Date.now();

  await signIn(page, CASHIER_PIN);
  await page.locator(`#seat-${a.pcId}`).click();
  await page.getByRole('button', { name: 'Пересадить на другой ПК…' }).click();
  await expect(page.getByText(/^Пересадка с .+: нажмите свободный ПК/)).toBeVisible();
  await page.locator(`#seat-${b.pcId}`).click();
  const sheet = page.getByRole('dialog', { name: /^Пересадить · / });
  await expect(sheet).toContainText('Часы идут');
  await sheet.getByRole('button', { name: 'Пересадить', exact: true }).click();
  await expect(sheet).toHaveCount(0);
  await expect(page.getByText(/^Пересажен на/)).toBeVisible();
  await expect(page.locator(`#seat-${b.pcId}`)).toContainText('ждёт входа');

  const onB = await seatOf(request, b.pcId);
  const elapsed = Math.round((Date.now() - t0) / 1000);
  expect(onB.session?.id).toBe(sessionId);
  expect(onB.session?.cost.amount).toBe(before.session?.cost.amount);
  expect(
    Math.abs((onB.session?.secondsLeft ?? 0) - ((before.session?.secondsLeft ?? 0) - elapsed)),
  ).toBeLessThanOrEqual(5);
  expect(onB.signedIn).toBe(false);
  expect((await seatOf(request, a.pcId)).session).toBeNull();

  // The old PC: its own end is a clean «not active» with the session ended there; its events change nothing.
  const end = await agentCall(request, a, 'POST', `/sessions/${sessionId}/end`, {
    reason: 'user',
    secondsUsed: 60,
    endedAt: new Date().toISOString(),
  });
  expect(end.status()).toBe(409);
  const ended = (await end.json()) as { error: { code: string; details: { session: { id: string; state: string } } } };
  expect(ended.error.code).toBe('sessionNotActive');
  expect(ended.error.details.session.state).toBe('ended');
  const events = await agentCall(
    request,
    a,
    'POST',
    `/sessions/${sessionId}/events`,
    { events: [{ sessionId, type: 'ended', at: new Date().toISOString(), data: { reason: 'user' } }] },
    undefined,
    { 'Idempotency-Key': randomUUID() },
  );
  expect(events.status(), await events.text()).toBe(204);
  expect((await seatOf(request, b.pcId)).session?.id).toBe(sessionId);

  // The player signs in on the new PC; the old one points elsewhere.
  const login = (pc: AgentPc): Promise<APIResponse> =>
    agentCall(request, pc, 'POST', '/auth/login', {
      kind: 'password',
      pcId: pc.pcId,
      hwid: pc.hwid,
      username: member.username,
      password: member.password,
    });
  const onNew = await login(b);
  expect(onNew.status(), await onNew.text()).toBe(200);
  const onOld = await login(a);
  expect(onOld.status()).toBe(409);
  expect(await reasonOf(onOld)).toBe('activeSessionElsewhere');

  // The feed tells the move.
  expect((await shiftOperations(request, 'sessionMove')).some((r) => r.sessionId === sessionId)).toBe(true);
  await expect(
    page.locator('[data-feed="column"]').getByRole('listitem').filter({ hasText: 'Пересадка' }).first(),
  ).toBeVisible();
  await endAt(request, b.pcId);
});

test('a move to a busy, maintenance or still-playing PC is refused with the reason', async ({ page, request }) => {
  await ensureShift(request);
  const owner = auth(await tokenFor(request, OWNER_PIN));
  const a = await registerAgent(request, 'mv-a');
  const b = await registerAgent(request, 'mv-b');
  const c = await registerAgent(request, 'mv-c');
  await heartbeat(request, b);
  await heartbeat(request, c);
  const sessionId = await seatMember(request, a, (await newClient(request, 'mv-a')).id);
  await seatMember(request, c, (await newClient(request, 'mv-c')).id);

  const busy = await moveSession(request, { fromPcId: a.pcId, sessionId, toPcId: c.pcId });
  expect(busy.status()).toBe(409);
  expect(await reasonOf(busy)).toBe('pcBusy');

  const maintenance = async (on: boolean): Promise<void> => {
    const res = await request.patch(`${API}/admin/pcs/${b.pcId}`, { headers: owner, data: { maintenance: on } });
    expect(res.ok(), await res.text()).toBeTruthy();
  };
  await maintenance(true);
  const inService = await moveSession(request, { fromPcId: a.pcId, sessionId, toPcId: b.pcId });
  expect(inService.status()).toBe(403);
  expect(await reasonOf(inService)).toBe('pcMaintenance');

  // Picking the target, the map dims the PCs that cannot take the session.
  await signIn(page, CASHIER_PIN);
  await page.locator(`#seat-${a.pcId}`).click();
  await page.getByRole('button', { name: 'Пересадить на другой ПК…' }).click();
  await expect(page.locator(`#seat-${b.pcId}`)).toHaveAttribute('aria-disabled', 'true');
  await expect(page.locator(`#seat-${c.pcId}`)).toHaveAttribute('aria-disabled', 'true');
  await page.keyboard.press('Escape');
  await expect(page.getByText(/^Пересадка с/)).toHaveCount(0);

  // A PC still holding a session of its own it has not sent.
  await maintenance(false);
  await heartbeat(request, b, { offlineQueue: 1 });
  const local = await moveSession(request, { fromPcId: a.pcId, sessionId, toPcId: b.pcId });
  expect(local.status()).toBe(409);
  expect(await reasonOf(local)).toBe('targetHasLocalSession');

  await heartbeat(request, b);
  const moved = await moveSession(request, { fromPcId: a.pcId, sessionId, toPcId: b.pcId });
  expect(moved.status(), await moved.text()).toBe(200);
  // The map's old view (the session on A) moves nothing a second time.
  const stale = await moveSession(request, { fromPcId: a.pcId, sessionId, toPcId: c.pcId });
  expect(stale.status()).toBe(409);
  expect(await reasonOf(stale)).toBe('sessionMoved');
  await endAt(request, b.pcId);
  await endAt(request, c.pcId);
});

test('a walk-in guest and a kiosk guest moved to another PC sign in there with «Гость»', async ({ request }) => {
  await ensureShift(request);
  const a = await registerAgent(request, 'g-a');
  const b = await registerAgent(request, 'g-b');
  const c = await registerAgent(request, 'g-c');
  const d = await registerAgent(request, 'g-d');
  await heartbeat(request, b);
  await heartbeat(request, d);
  const tariffId = await standardTariff(request);
  const cashier = auth(await tokenFor(request, CASHIER_PIN));
  const guestSession = async (res: APIResponse): Promise<string | undefined> =>
    ((await res.json()) as { session: { id: string } | null }).session?.id;

  // A walk-in guest the desk seated.
  const quote = (await (
    await request.post(`${API}/admin/quote`, { headers: cashier, data: { tariffId, pcId: a.pcId, minutes: 60 } })
  ).json()) as { total: { amount: number } };
  const seated = await moneyPost(request, '/admin/sessions/guest', {
    pcId: a.pcId,
    tariffId,
    minutes: 60,
    prepaid: true,
    payment: { amount: quote.total.amount, method: 'cash' },
  });
  expect(seated.status(), await seated.text()).toBe(201);
  const deskSession = ((await seated.json()) as { session: { id: string } }).session.id;
  const movedDesk = await moveSession(request, { fromPcId: a.pcId, sessionId: deskSession, toPcId: b.pcId });
  expect(movedDesk.status(), await movedDesk.text()).toBe(200);
  const onB = await agentCall(request, b, 'POST', '/auth/guest', { pcId: b.pcId, hwid: b.hwid });
  expect(onB.status(), await onB.text()).toBe(200);
  expect(await guestSession(onB)).toBe(deskSession);

  // A guest who signed in at the kiosk and bought time there.
  const kiosk = await agentCall(request, c, 'POST', '/auth/guest', { pcId: c.pcId, hwid: c.hwid });
  expect(kiosk.status(), await kiosk.text()).toBe(200);
  const guest = (await kiosk.json()) as { accessToken: string; user: { id: string } };
  const paid = await moneyPost(request, '/admin/wallet/topup', {
    userId: guest.user.id,
    amount: 5_000_000,
    method: 'cash',
  });
  expect(paid.ok(), await paid.text()).toBeTruthy();
  const own = await agentCall(
    request,
    c,
    'POST',
    '/sessions',
    { pcId: c.pcId, userId: guest.user.id, tariffId, minutes: 60, prepaid: true },
    guest.accessToken,
    { 'Idempotency-Key': randomUUID() },
  );
  expect(own.status(), await own.text()).toBe(201);
  const kioskSession = ((await own.json()) as { id: string }).id;
  const movedKiosk = await moveSession(request, { fromPcId: c.pcId, sessionId: kioskSession, toPcId: d.pcId });
  expect(movedKiosk.status(), await movedKiosk.text()).toBe(200);
  const onD = await agentCall(request, d, 'POST', '/auth/guest', { pcId: d.pcId, hwid: d.hwid });
  expect(onD.status(), await onD.text()).toBe(200);
  expect(await guestSession(onD)).toBe(kioskSession);

  await endAt(request, b.pcId);
  await endAt(request, d.pcId);
});

test("a player's call rings at the desk until «Иду», and repeats do not ring again", async ({ page, request }) => {
  // Two beeps 5 s apart and two quiet windows longer than a ring, on top of the sign-in and the polls.
  test.setTimeout(60_000);
  const pc = await registerAgent(request, 'call');
  const call = (at: string): Promise<APIResponse> =>
    agentCall(
      request,
      pc,
      'POST',
      '/support/call-admin',
      { pcId: pc.pcId, category: 'technical', message: 'E2E мышь не работает', at },
      undefined,
      { 'Idempotency-Key': randomUUID() },
    );
  await stubAudio(page);
  await signIn(page, CASHIER_PIN);
  await expect(page.getByRole('heading', { name: 'Карта зала' })).toBeVisible();

  const first = await call(new Date().toISOString());
  expect(first.status(), await first.text()).toBe(201);
  expect(((await first.json()) as { queuePosition: number }).queuePosition).toBeGreaterThanOrEqual(1);
  const bell = page.getByRole('button', { name: /^Вызовы \d+$/ });
  await expect(bell).toBeVisible();
  await expect(page.locator(`#seat-${pc.pcId} [aria-label="Вызов администратора"]`)).toBeVisible();
  // It rings every 5 s while nobody answers.
  await expect.poll(() => beeps(page), { timeout: 15_000 }).toBeGreaterThanOrEqual(2);

  await bell.click();
  const row = page.getByRole('dialog', { name: 'Вызовы игроков' }).locator(`[data-call-pc="${pc.pcId}"]`);
  await expect(row).toContainText('E2E мышь не работает');
  await row.getByRole('button', { name: 'Иду' }).click();
  // A registered PC has no socket here: the player is not told, and the inbox says so.
  await expect(row).toContainText('ПК не на связи — игрок не получил сообщение');
  const answered = await beeps(page);
  await page.waitForTimeout(6_000);
  expect(await beeps(page)).toBe(answered);

  // Pressed again a second later: one more call of the same PC, shown, not ringing.
  const at = new Date(Date.now() + 1000).toISOString();
  expect((await call(at)).status()).toBe(201);
  await expect(row).toContainText('×2');
  const repeated = await beeps(page);
  await page.waitForTimeout(6_000);
  expect(await beeps(page)).toBeLessThanOrEqual(repeated + 1);
  // The same press again (its telemetry copy, a replay) is still that one call.
  expect((await call(at)).status()).toBe(201);
  expect(((await overview(request)).calls ?? []).filter((c) => c.pcId === pc.pcId)).toHaveLength(2);

  await row.getByRole('button', { name: 'Закрыть вызов' }).click();
  await expect(page.getByRole('button', { name: /^Вызовы \d+$/ })).toHaveCount(0);
});

test('a call that came only through telemetry and a «report a problem» text reach the inbox; a bad event does not break telemetry', async ({
  request,
}) => {
  const pc = await registerAgent(request, 'tele');
  const now = Date.now();
  const at = (ms: number): string => new Date(now - ms).toISOString();
  const res = await agentCall(request, pc, 'POST', `/agents/${pc.pcId}/telemetry`, {
    samples: [
      {
        cpuPct: 37,
        gpuPct: 12,
        ramUsedMb: 4096,
        temps: { cpu: 55, gpu: 48 },
        fps: null,
        netMbps: { up: 1, down: 5 },
        uptimeSec: 600,
        at: at(0),
      },
    ],
    events: [
      {
        kind: 'callAdmin',
        at: at(3000),
        data: { pcId: pc.pcId, userId: null, category: 'help', message: 'E2E телеметрия', at: at(3000) },
      },
      {
        kind: 'shellClientError',
        at: at(2000),
        data: { level: 'warn', message: '[user report] мышь не работает', stack: null, route: '/support' },
      },
      { kind: 'callAdmin', at: at(1000), data: { pcId: pc.pcId, category: 'x', message: null, at: at(1000) } },
    ],
  });
  expect(res.status(), await res.text()).toBe(204);

  const calls = ((await overview(request)).calls ?? []).filter((c) => c.pcId === pc.pcId);
  expect(calls.map((c) => c.category).sort()).toEqual(['help', 'problem']);
  expect(calls.find((c) => c.category === 'problem')?.message).toBe('мышь не работает');
  // The batch still counted: the PC's metrics are the sample.
  const hall = (await (
    await request.get(`${API}/admin/pcs`, { headers: auth(await tokenFor(request, OWNER_PIN)) })
  ).json()) as { items: { id: string; metrics: { cpuPct: number } | null }[] };
  expect(hall.items.find((p) => p.id === pc.pcId)?.metrics?.cpuPct).toBe(37);

  // Closed, so the inbox of the next tests starts empty.
  const headers = auth(await tokenFor(request, CASHIER_PIN));
  for (const c of calls) {
    const closed = await request.post(`${API}/admin/calls/${c.id}/resolve`, { headers, data: {} });
    expect(closed.ok(), await closed.text()).toBeTruthy();
  }
  expect(((await overview(request)).calls ?? []).filter((c) => c.pcId === pc.pcId)).toHaveLength(0);
});

test('bulk actions report each PC: offline PCs are skipped for power, a busy PC is ended first after asking', async ({
  page,
  request,
}) => {
  await ensureShift(request);
  const a = await registerAgent(request, 'bulk-a');
  const b = await registerAgent(request, 'bulk-b');
  const c = await registerAgent(request, 'bulk-c');
  // A never heartbeats: it is offline.
  await heartbeat(request, b);
  await heartbeat(request, c);
  const member = await newClient(request, 'bulk');
  const sessionId = await seatMember(request, c, member.id);

  await signIn(page, CASHIER_PIN);
  for (const pc of [a, b, c]) await page.locator(`#seat-${pc.pcId}`).click({ modifiers: ['Control'] });
  await expect(page.getByRole('heading', { name: 'Выбрано 3' })).toBeVisible();
  const panel = page.locator('aside').filter({ has: page.getByRole('heading', { name: 'Выбрано 3' }) });
  const result = (pc: AgentPc) =>
    panel.getByRole('list', { name: 'Результат по ПК' }).locator(`[data-pc-result="${pc.pcId}"]`);

  // A message reaches every PC, the offline one when it comes back.
  await panel.getByLabel('Сообщение на экран').fill('E2E: закрываемся');
  await panel.getByRole('button', { name: /^Сообщение \(3\)/ }).click();
  for (const pc of [a, b, c]) await expect(result(pc)).toContainText('в очереди');

  // A reboot asks first, naming the player it would interrupt.
  await panel.getByRole('button', { name: /^Перезагрузить/ }).click();
  const confirm = page.getByRole('dialog', { name: /^Перезагрузить/ });
  await expect(confirm).toContainText(member.displayName);
  await expect(confirm).toContainText('Сеанс будет завершён, неиспользованное время вернётся на баланс');
  await confirm.getByRole('button', { name: 'Перезагрузить', exact: true }).click();
  await expect(result(c)).toContainText('сеанс завершён');
  await expect(result(c)).toContainText('в очереди');
  await expect(result(a)).toContainText('пропущен: офлайн');
  await expect(result(b)).toContainText('в очереди');
  expect((await seatOf(request, c.pcId)).session).toBeNull();
  expect((await shiftOperations(request, 'sessionEnd')).some((r) => r.sessionId === sessionId)).toBe(true);

  // A plain click is one PC again.
  await page.locator(`#seat-${b.pcId}`).click();
  await expect(page.getByRole('heading', { name: 'Выбрано 3' })).toHaveCount(0);
  await expect(page.getByRole('heading', { name: /Посадить на/ })).toBeVisible();
});

test('a single PC command says when the PC is offline or busy instead of claiming success', async ({
  page,
  request,
}) => {
  await ensureShift(request);
  const offline = await registerAgent(request, 'one-off');
  const seated = await registerAgent(request, 'one-busy');
  await heartbeat(request, seated);
  const member = await newClient(request, 'one-busy');
  await seatMember(request, seated, member.id);

  await signIn(page, CASHIER_PIN);
  await page.locator(`#seat-${offline.pcId}`).click();
  await page.getByRole('button', { name: /^Ещё ⋯/ }).click();
  await page.getByRole('button', { name: 'Заблокировать', exact: true }).click();
  await expect(page.getByText(/офлайн — команда в очереди/)).toBeVisible();

  // Rebooting a busy PC asks first and offers to move the player instead.
  await page.locator(`#seat-${seated.pcId}`).click();
  await page.getByRole('button', { name: /^Ещё ⋯/ }).click();
  await page.getByRole('button', { name: 'Перезагрузить', exact: true }).click();
  const confirm = page.getByRole('dialog', { name: /^Перезагрузить/ });
  await expect(confirm).toContainText(member.displayName);
  await expect(confirm.getByRole('button', { name: 'Пересадить' })).toBeVisible();
  await confirm.getByRole('button', { name: 'Отмена' }).click();
  expect((await seatOf(request, seated.pcId)).session).not.toBeNull();
  await endAt(request, seated.pcId);
});

// ---------------------------------------------------------------------------------------------------------------------
// Cash desk F («Командный центр»): the game on a busy seat
// ---------------------------------------------------------------------------------------------------------------------

test('a busy seat shows the game its PC reports', async ({ page, request }) => {
  await ensureShift(request);
  const pc = await registerAgent(request, 'game');
  await heartbeat(request, pc);
  const member = await newClient(request, 'game');
  const sessionId = await seatMember(request, pc, member.id);
  // «Counter-Strike 2» is in the mock's catalog and in games.e2e.json (the real server's seed).
  const games = (await (
    await request.get(`${API}/admin/games`, { headers: auth(await tokenFor(request, OWNER_PIN)) })
  ).json()) as { items: { id: string; title: string }[] };
  const cs2 = games.items.find((g) => g.title === 'Counter-Strike 2');
  expect(cs2, 'Counter-Strike 2 in the catalog').toBeTruthy();
  await heartbeat(request, pc, {
    currentSessionId: sessionId,
    runningGames: [{ gameId: cs2!.id, pid: 4242, startedAt: new Date().toISOString() }],
  });
  expect((await seatOf(request, pc.pcId)).game?.title).toBe('Counter-Strike 2');

  // The tile names the game, with its cover art or without (a catalog game may have none).
  await signIn(page, CASHIER_PIN);
  const tile = page.locator(`#seat-${pc.pcId}`);
  await expect(tile).toContainText('Counter-Strike 2');

  // The game closed: the next heartbeat lists none, and the tile forgets it.
  await heartbeat(request, pc, { currentSessionId: sessionId, runningGames: [] });
  expect((await seatOf(request, pc.pcId)).game ?? null).toBeNull();
  await expect(tile).not.toContainText('Counter-Strike 2');
  await endAt(request, pc.pcId);
});

// ---------------------------------------------------------------------------------------------------------------------
// The machine: games disk and hardware (D-73)
// ---------------------------------------------------------------------------------------------------------------------

test('«Состояние ПК» shows the games disk a PC reports, and the hall list carries its hardware', async ({
  page,
  request,
}) => {
  const pc = await registerAgent(request, 'disk');
  await heartbeat(request, pc, {
    gamesVolume: { owner: 'agent', mounted: false, driveLetter: 'G', since: new Date().toISOString() },
  });
  const hall = (await (
    await request.get(`${API}/admin/pcs`, { headers: auth(await tokenFor(request, OWNER_PIN)) })
  ).json()) as {
    items: {
      id: string;
      name: string;
      gamesVolume?: { mounted?: boolean } | null;
      hardware: { cpu: { model: string } } | null;
    }[];
  };
  const mine = hall.items.find((p) => p.id === pc.pcId);
  expect(mine, 'the PC in the hall list').toBeTruthy();
  expect(mine!.gamesVolume?.mounted).toBe(false);
  // What registerAgent sent at registration.
  expect(mine!.hardware?.cpu.model).toBe('E2E CPU');

  await signIn(page, CASHIER_PIN);
  await page.goto('/#/health');
  const row = page.locator('tbody tr').filter({ has: page.locator(`[title="${mine!.name}"]`) });
  await expect(row).toContainText(/не подключён с \d{2}:\d{2}/);

  // Back: the next heartbeat says it is connected again.
  await heartbeat(request, pc, {
    gamesVolume: { owner: 'agent', mounted: true, driveLetter: 'G', since: new Date().toISOString() },
  });
  await page.reload();
  await expect(row).toContainText(/(?<!не )подключён с \d{2}:\d{2}/);
});

test('a PC without a loaded Vanguard is not offered the games it could not start (D-74)', async ({ request }) => {
  const pc = await registerAgent(request, 'vgk');
  const listed = async (): Promise<{ id: string; launcher: string; antiCheat: string }[]> => {
    const res = await agentCall(request, pc, 'GET', '/games');
    expect(res.ok(), await res.text()).toBeTruthy();
    return ((await res.json()) as { items: { id: string; launcher: string; antiCheat: string }[] }).items;
  };
  await heartbeat(request, pc, { antiCheat: { vanguardInstalled: true, vanguardLoaded: true } });
  const all = await listed();

  // Installed but not loaded until a reboot. The seed policy requires Vanguard for every game with an anti-cheat, so
  // only the games without one (and not Riot's, which the Agent treats as Vanguard) are left.
  await heartbeat(request, pc, { antiCheat: { vanguardInstalled: true, vanguardLoaded: false } });
  expect((await listed()).map((g) => g.id)).toEqual(
    all.filter((g) => g.antiCheat === 'none' && g.launcher !== 'riot').map((g) => g.id),
  );

  // Loaded after the reboot: the whole catalog again.
  await heartbeat(request, pc, { antiCheat: { vanguardInstalled: true, vanguardLoaded: true } });
  expect((await listed()).map((g) => g.id)).toEqual(all.map((g) => g.id));
});
