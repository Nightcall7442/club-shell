/**
 * Owner insights: conclusions, not charts. The club's own data is read for patterns an owner acts on —
 *   - a zone that stands empty in a block of hours (→ a happy hour for exactly that zone and time, one click),
 *   - a zone that is full every evening (→ raise the price there or push booking),
 *   - a product that will run out before the next delivery, a product nobody buys,
 *   - a PC that has waited for repair for more than a day (money lost per day),
 *   - players who stopped coming while money sits on their balance.
 * Each insight carries its facts (the console phrases them in the UI language), an estimated monthly effect, and an
 * action. Dismissed insights stay hidden for a week.
 *
 * Load comes from the hourly PC buckets of the health module (real telemetry, or its demo week). Sales are the real
 * orders plus a two-week demo sales history seeded on first start, so the stock insights have something to read.
 */
import { club, type HappyHour } from './club.js';
import { db, markDirty, openSessionForUser, sid, uuid } from './db.js';
import { backfill, health } from './health.js';

export type InsightKind = 'idleWindow' | 'peakWindow' | 'stockOut' | 'staleStock' | 'repairWaiting' | 'winBack';

export type InsightAction =
  | { kind: 'createHappyHour'; happyHour: Omit<HappyHour, 'id'> }
  | { kind: 'open'; section: 'pricing' | 'shop' | 'health' | 'clients' };

export interface Insight {
  id: string;
  kind: InsightKind;
  /** `money`: an opportunity with an estimated monthly effect; `risk`: something about to cost money. */
  tone: 'money' | 'risk';
  /** Estimated effect per month, minor units (0 when not estimated). */
  impact: number;
  params: Record<string, string | number>;
  action: InsightAction | null;
}

interface InsightsState {
  dismissed: Record<string, string>;
  /** Units sold per product per day, oldest first, for the last 14 days (demo history + real orders). */
  sales: Record<string, { date: string; qty: number }[]>;
  seeded: boolean;
}

type WithInsights = typeof db & { insights?: InsightsState };

function state(): InsightsState {
  const store = db as WithInsights;
  store.insights ??= { dismissed: {}, sales: {}, seeded: false };
  return store.insights;
}

const DAY = 86_400_000;
const WEEKS_PER_MONTH = 4.3;
const DISMISS_MS = 7 * DAY;

const dateKey = (ms: number): string => new Date(ms).toISOString().slice(0, 10);

function noise(seed: number): number {
  const x = Math.sin(seed * 12.9898) * 43758.5453;
  return x - Math.floor(x);
}

/** Two weeks of daily sales per product: energy drinks peak on Fridays, a merch item nobody buys. */
function seedSales(): void {
  const st = state();
  if (st.seeded) return;
  const perDay: Record<string, number> = {
    cola: 5,
    redbull: 6,
    water: 4,
    americano: 3,
    lays: 3,
    popcorn: 2,
    lavash: 2,
    burger: 2,
    somsa: 3,
    snickers: 3,
    headset: 1,
    tshirt: 0,
  };
  const today = new Date();
  today.setHours(12, 0, 0, 0);
  for (const p of db.products) {
    const slug = Object.keys(perDay).find((k) => p.id === sid(`product:${k}`)) ?? '';
    const base = perDay[slug] ?? 1;
    st.sales[p.id] = Array.from({ length: 14 }, (_, i) => {
      const d = new Date(today.getTime() - (13 - i) * DAY);
      const friday = d.getDay() === 5 && slug === 'redbull' ? 2.2 : 1;
      const qty = base === 0 ? 0 : Math.round(base * friday * (0.7 + noise(i * 31 + base) * 0.6));
      return { date: dateKey(d.getTime()), qty };
    });
  }
  st.seeded = true;
  markDirty();
}

function salesOf(productId: string, nowMs: number): { date: string; qty: number }[] {
  const days = new Map<string, number>();
  for (let i = 13; i >= 0; i -= 1) days.set(dateKey(nowMs - i * DAY), 0);
  for (const s of state().sales[productId] ?? []) if (days.has(s.date)) days.set(s.date, s.qty);
  for (const o of db.orders) {
    const d = o.createdAt.slice(0, 10);
    if (!days.has(d)) continue;
    for (const line of o.items) if (line.productId === productId) days.set(d, (days.get(d) ?? 0) + line.qty);
  }
  return [...days].map(([date, qty]) => ({ date, qty }));
}

// ---------------------------------------------------------------------------------------------------------------------
// Load windows
// ---------------------------------------------------------------------------------------------------------------------

const BLOCKS: { from: number; to: number }[] = [
  { from: 10, to: 14 },
  { from: 14, to: 18 },
  { from: 18, to: 22 },
];
const WEEKDAYS = [1, 2, 3, 4, 5];
const WEEKEND = [0, 6];

function rateFor(zone: string): number {
  const hourly = db.tariffs.filter((t) => !t.isPackage);
  const tariff =
    hourly.find((t) => t.zones.some((z) => z.toLowerCase() === zone.toLowerCase())) ??
    hourly.find((t) => t.zones.length === 0) ??
    hourly[0];
  return tariff?.pricePerHour.amount ?? 1_000_000;
}

function covered(zone: string, days: number[], from: number, to: number): boolean {
  return club().happyHours.some(
    (h) =>
      (h.zones.length === 0 || h.zones.some((z) => z.toLowerCase() === zone.toLowerCase())) &&
      days.every((d) => h.days.includes(d)) &&
      Number(h.from.slice(0, 2)) <= from &&
      (Number(h.to.slice(0, 2)) >= to || h.to === '00:00'),
  );
}

function loadWindows(nowMs: number): Insight[] {
  const out: Insight[] = [];
  const buckets = health().buckets;
  const zones = [...new Set(db.pcs.map((p) => p.zone))];
  for (const zone of zones) {
    const pcs = db.pcs.filter((p) => p.zone === zone);
    if (pcs.length < 3) continue;
    for (const [label, days] of [
      ['weekdays', WEEKDAYS],
      ['weekend', WEEKEND],
    ] as const) {
      for (const b of BLOCKS) {
        const shares: number[] = [];
        for (const pc of pcs) {
          for (const k of buckets[pc.id] ?? []) {
            if (k.h < nowMs - 7 * DAY) continue;
            const d = new Date(k.h);
            if (days.includes(d.getDay()) && d.getHours() >= b.from && d.getHours() < b.to) shares.push(k.busy);
          }
        }
        if (shares.length < pcs.length * 4) continue;
        const occ = shares.reduce((a, x) => a + x, 0) / shares.length;
        const hours = b.to - b.from;
        const rate = rateFor(zone);
        const range = `${String(b.from).padStart(2, '0')}:00–${String(b.to).padStart(2, '0')}:00`;
        const params = { zone, days: label, range, occupancy: Math.round(occ * 100), pcs: pcs.length };
        if (occ < 0.3 && !covered(zone, [...days], b.from, b.to)) {
          // Lifting an empty block to ~35 % at 30 % off.
          const extraSeatHours = pcs.length * hours * days.length * Math.max(0, 0.35 - occ) * WEEKS_PER_MONTH;
          out.push({
            id: `idle:${zone}:${label}:${b.from}`,
            kind: 'idleWindow',
            tone: 'money',
            impact: Math.round((extraSeatHours * rate * 0.7) / 10_000) * 10_000,
            params: { ...params, discount: 30 },
            action: {
              kind: 'createHappyHour',
              happyHour: {
                name: `${zone} ${range}`,
                days: [...days],
                from: `${String(b.from).padStart(2, '0')}:00`,
                to: `${String(b.to).padStart(2, '0')}:00`,
                discountPct: 30,
                zones: [zone],
              },
            },
          });
        } else if (occ > 0.85) {
          // +10 % on hours that sell out anyway.
          const seatHours = pcs.length * hours * days.length * occ * WEEKS_PER_MONTH;
          out.push({
            id: `peak:${zone}:${label}:${b.from}`,
            kind: 'peakWindow',
            tone: 'money',
            impact: Math.round((seatHours * rate * 0.1) / 10_000) * 10_000,
            params,
            action: { kind: 'open', section: 'pricing' },
          });
        }
      }
    }
  }
  // A zone full on weekdays and at the weekend in the same hours is one insight, not two.
  const merged: Insight[] = [];
  for (const i of out) {
    const twin = merged.find(
      (m) =>
        m.kind === 'peakWindow' &&
        i.kind === 'peakWindow' &&
        m.params['zone'] === i.params['zone'] &&
        m.params['range'] === i.params['range'],
    );
    if (!twin) {
      merged.push(i);
      continue;
    }
    twin.id = `peak:${i.params['zone']}:daily:${i.params['range']}`;
    twin.impact += i.impact;
    twin.params = {
      ...twin.params,
      days: 'daily',
      occupancy: Math.min(Number(twin.params['occupancy']), Number(i.params['occupancy'])),
    };
  }
  return merged;
}

// ---------------------------------------------------------------------------------------------------------------------
// Stock, repairs, players
// ---------------------------------------------------------------------------------------------------------------------

function stock(nowMs: number): Insight[] {
  const out: Insight[] = [];
  for (const p of db.products) {
    if (p.stockQty === null || p.stockQty === undefined) continue;
    const sales = salesOf(p.id, nowMs);
    const sold = sales.reduce((a, s) => a + s.qty, 0);
    const perDay = sold / sales.length;
    if (sold === 0 && p.stockQty > 0) {
      out.push({
        id: `stale:${p.id}`,
        kind: 'staleStock',
        tone: 'money',
        impact: p.stockQty * p.price.amount,
        params: { product: p.title, qty: p.stockQty, days: sales.length },
        action: { kind: 'open', section: 'shop' },
      });
      continue;
    }
    if (perDay <= 0) continue;
    const daysLeft = p.stockQty / perDay;
    if (daysLeft >= 3) continue;
    // Best weekday by average sales.
    const byWeekday = Array.from({ length: 7 }, () => ({ qty: 0, n: 0 }));
    for (const s of sales) {
      const w = new Date(`${s.date}T12:00:00`).getDay();
      byWeekday[w]!.qty += s.qty;
      byWeekday[w]!.n += 1;
    }
    const best = byWeekday.map((w, i) => ({ i, avg: w.n ? w.qty / w.n : 0 })).sort((a, b) => b.avg - a.avg)[0]!;
    const order = Math.max(1, Math.ceil(perDay * 7 - p.stockQty));
    out.push({
      id: `stock:${p.id}`,
      kind: 'stockOut',
      tone: 'risk',
      // A week of sales lost if nobody orders.
      impact: Math.round(perDay * 7 * p.price.amount),
      params: {
        product: p.title,
        qty: p.stockQty,
        perDay: Math.round(perDay * 10) / 10,
        daysLeft: Math.max(0, Math.round(daysLeft * 10) / 10),
        order,
        bestDay: best.avg > perDay * 1.4 ? best.i : -1,
      },
      action: { kind: 'open', section: 'shop' },
    });
  }
  return out;
}

function repairs(nowMs: number): Insight[] {
  const out: Insight[] = [];
  for (const t of health().tickets) {
    if (t.status === 'resolved') continue;
    const waitedH = (nowMs - Date.parse(t.openedAt)) / 3_600_000;
    if (waitedH < 24) continue;
    const pc = db.pcs.find((p) => p.id === t.pcId);
    const rate = rateFor(pc?.zone ?? 'Standard');
    out.push({
      id: `repair:${t.id}`,
      kind: 'repairWaiting',
      tone: 'risk',
      // A seat that is avoided or out of service: about 6 paid hours a day.
      impact: Math.round((6 * rate * 30) / 10_000) * 10_000,
      params: { pc: t.pcName, days: Math.floor(waitedH / 24), problem: t.kind },
      action: { kind: 'open', section: 'health' },
    });
  }
  return out;
}

function winBack(nowMs: number): Insight[] {
  const gone = db.users.filter((u) => {
    if (u.transient || u.role === 'admin' || u.role === 'guest' || u.balance.amount <= 0) return false;
    if (openSessionForUser(u.id)) return false;
    const seen = u.lastSeenAt ? Date.parse(u.lastSeenAt) : 0;
    return nowMs - seen > 14 * DAY && nowMs - seen < 90 * DAY;
  });
  if (gone.length === 0) return [];
  const balance = gone.reduce((a, u) => a + u.balance.amount, 0);
  return [
    {
      id: `winback:${dateKey(nowMs)}`,
      kind: 'winBack',
      tone: 'money',
      impact: balance,
      params: { players: gone.length, balance },
      action: { kind: 'open', section: 'clients' },
    },
  ];
}

// ---------------------------------------------------------------------------------------------------------------------

export function insights(nowMs = Date.now()): Insight[] {
  seedSales();
  backfill();
  const st = state();
  for (const [id, until] of Object.entries(st.dismissed)) if (Date.parse(until) < nowMs) delete st.dismissed[id];
  const all = [...stock(nowMs), ...repairs(nowMs), ...loadWindows(nowMs), ...winBack(nowMs)].filter(
    (i) => !st.dismissed[i.id],
  );
  // Risks first, then the biggest money.
  return all.sort((a, b) => (a.tone === b.tone ? b.impact - a.impact : a.tone === 'risk' ? -1 : 1));
}

export function dismissInsight(id: string, nowMs = Date.now()): void {
  state().dismissed[id] = new Date(nowMs + DISMISS_MS).toISOString();
  markDirty();
}

/** Applies an insight's one-click action; returns what was created. */
export function applyInsight(id: string): { happyHour: HappyHour } | null {
  const insight = insights().find((i) => i.id === id);
  if (!insight || insight.action?.kind !== 'createHappyHour') return null;
  const happyHour: HappyHour = { id: uuid(), ...insight.action.happyHour };
  club().happyHours.push(happyHour);
  markDirty();
  return { happyHour };
}
