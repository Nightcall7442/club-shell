/**
 * Club customisation: everything a club owner configures for their own venue from the admin console — staff and
 * roles, cashier shifts, pricing (weekday / holiday multipliers, client groups, happy hours, top-up bonus tiers, promo
 * codes, loyalty levels), branding and the player-facing features, the game catalogue, banners, club rules, stock
 * thresholds, "if → then" automation rules, Telegram notifications and outbound webhooks.
 *
 * Stored as `db.club` next to the rest of the mock store (persisted with it). {@link ensureClub} fills a missing or
 * older document with defaults, so an existing `.mock-db.json` keeps working. The pricing and rule engines live here
 * too so the admin routes, the kiosk routes and the tick share one implementation.
 */
import { tariffPriceFor, type Money, type ShellFeatures, type Tariff } from '@clubshell/contracts';
import {
  applyTransaction,
  balanceOf,
  db,
  findPc,
  findUser,
  markDirty,
  now,
  openSessionForPc,
  uuid,
  uzs,
  viewSession,
  type SessionRecord,
  type UserRecord,
} from './db.js';
import { pushToUser, sendCommand } from './ws.js';

// ---------------------------------------------------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------------------------------------------------

export type StaffRole = 'owner' | 'cashier';

export interface StaffRecord {
  id: string;
  name: string;
  role: StaffRole;
  pin: string;
  active: boolean;
}

export interface ShiftTotals {
  topUpCash: number;
  topUpOther: number;
  sessions: number;
  shop: number;
  refunds: number;
  bonuses: number;
  count: number;
}

export interface ShiftRecord {
  id: string;
  staffId: string;
  staffName: string;
  openedAt: string;
  closedAt: string | null;
  openingCash: number;
  closingCash: number | null;
  totals: ShiftTotals | null;
}

export interface ClientGroup {
  id: string;
  name: string;
  discountPct: number;
  color: string;
}

export interface ClientProfile {
  groupId: string | null;
  note: string;
  blacklisted: boolean;
  phone: string;
  birthYear: number | null;
  telegram: string;
}

export interface BonusTier {
  /** Minimum top-up, minor units. */
  minAmount: number;
  bonusPct: number;
}

export interface PromoCode {
  code: string;
  kind: 'bonus' | 'discountPct';
  /** Bonus amount in minor units, or a discount percentage. */
  value: number;
  usesLeft: number | null;
  expiresAt: string | null;
  used: number;
}

export interface HappyHour {
  id: string;
  name: string;
  /** 0 = Sunday … 6 = Saturday. */
  days: number[];
  from: string;
  to: string;
  discountPct: number;
  zones: string[];
}

export interface LoyaltyLevel {
  level: number;
  name: string;
  /** Lifetime spend needed, minor units. */
  minSpent: number;
  discountPct: number;
}

export type RuleTrigger =
  | { kind: 'minutesLeft'; value: number }
  | { kind: 'pcIdleMinutes'; value: number }
  | { kind: 'visitCount'; value: number }
  | { kind: 'topupAtLeast'; value: number }
  | { kind: 'sessionStarted' };

export type RuleAction =
  | { kind: 'message'; text: string }
  | { kind: 'bonus'; amount: number }
  | { kind: 'lockPc' }
  | { kind: 'shutdownPc' }
  | { kind: 'notifyOwner'; text: string };

export interface AutomationRule {
  id: string;
  name: string;
  enabled: boolean;
  trigger: RuleTrigger;
  action: RuleAction;
  fired: number;
  lastFiredAt: string | null;
}

export interface Banner {
  id: string;
  title: string;
  imageUrl: string;
  from: string | null;
  to: string | null;
  enabled: boolean;
}

export interface Zone {
  name: string;
  color: string;
}

export type DeviceKind = 'pc' | 'console' | 'vr' | 'other';

export type ClubEvent = 'shiftClosed' | 'pcOffline' | 'bigTopup' | 'lowStock' | 'ruleFired' | 'sessionOpened';

export const CLUB_EVENTS: readonly ClubEvent[] = [
  'shiftClosed',
  'pcOffline',
  'bigTopup',
  'lowStock',
  'ruleFired',
  'sessionOpened',
];

export interface Webhook {
  id: string;
  url: string;
  events: ClubEvent[];
  enabled: boolean;
  lastStatus: number | null;
  lastAt: string | null;
}

export interface ClubConfig {
  version: number;
  branding: { clubName: string; accent: string; logoUrl: string | null; wallpaperUrl: string | null };
  features: ShellFeatures;
  zones: Zone[];
  devices: Record<string, DeviceKind>;
  pricing: {
    /** Percent of the base price per weekday, 0 = Sunday. */
    weekdayPct: number[];
    holidays: string[];
    holidayPct: number;
  };
  groups: ClientGroup[];
  clients: Record<string, ClientProfile>;
  bonusTiers: BonusTier[];
  promoCodes: PromoCode[];
  happyHours: HappyHour[];
  loyalty: LoyaltyLevel[];
  limits: { minorAge: number; minorCurfew: string };
  catalog: { order: string[]; hidden: string[]; featured: string[] };
  banners: Banner[];
  rulesText: { ru: string; uz: string; en: string };
  stock: { lowAt: number };
  automation: AutomationRule[];
  notifications: {
    telegramBotToken: string;
    telegramChatId: string;
    events: Record<ClubEvent, boolean>;
    bigTopupAt: number;
  };
  webhooks: Webhook[];
  apiKey: string;
  staff: StaffRecord[];
  shifts: ShiftRecord[];
  /** Tokens of signed-in staff → staff id. */
  staffTokens: Record<string, string>;
}

const CONFIG_VERSION = 1;

// ---------------------------------------------------------------------------------------------------------------------
// Defaults
// ---------------------------------------------------------------------------------------------------------------------

function defaults(): ClubConfig {
  return {
    version: CONFIG_VERSION,
    branding: { clubName: 'CyberArena Tashkent', accent: '#9ADFFF', logoUrl: null, wallpaperUrl: null },
    features: {
      shop: true,
      chat: true,
      booking: true,
      tournaments: true,
      profile: true,
      topup: true,
      apps: true,
      callAdmin: true,
    },
    zones: [
      { name: 'Standard', color: '#22C55E' },
      { name: 'VIP', color: '#F2B84B' },
      { name: 'Bootcamp', color: '#9ADFFF' },
    ],
    devices: {},
    pricing: { weekdayPct: [110, 100, 100, 100, 100, 100, 110], holidays: [], holidayPct: 120 },
    groups: [
      { id: 'guest', name: 'Гость', discountPct: 0, color: '#F97316' },
      { id: 'regular', name: 'Постоянный', discountPct: 10, color: '#22C55E' },
      { id: 'student', name: 'Школьник', discountPct: 15, color: '#A855F7' },
      { id: 'staff', name: 'Сотрудник', discountPct: 50, color: '#3B82F6' },
    ],
    clients: {},
    bonusTiers: [
      { minAmount: 5_000_000, bonusPct: 5 },
      { minAmount: 10_000_000, bonusPct: 10 },
      { minAmount: 20_000_000, bonusPct: 15 },
    ],
    promoCodes: [{ code: 'WELCOME', kind: 'bonus', value: 1_000_000, usesLeft: 100, expiresAt: null, used: 0 }],
    happyHours: [
      { id: uuid(), name: 'Утро', days: [1, 2, 3, 4, 5], from: '10:00', to: '14:00', discountPct: 30, zones: [] },
    ],
    loyalty: [
      { level: 1, name: 'Новичок', minSpent: 0, discountPct: 0 },
      { level: 2, name: 'Игрок', minSpent: 50_000_000, discountPct: 3 },
      { level: 3, name: 'Про', minSpent: 150_000_000, discountPct: 5 },
      { level: 4, name: 'Ветеран', minSpent: 400_000_000, discountPct: 8 },
      { level: 5, name: 'Легенда', minSpent: 1_000_000_000, discountPct: 12 },
    ],
    limits: { minorAge: 18, minorCurfew: '22:00' },
    catalog: { order: [], hidden: [], featured: [] },
    banners: [],
    rulesText: {
      ru: 'Бережно относитесь к оборудованию.\nЕда и напитки только на приставном столике.\nЧиты запрещены.',
      uz: 'Uskunalarga ehtiyotkorlik bilan munosabatda boʻling.\nOvqat va ichimliklar faqat yon stolchada.\nChitlar taqiqlangan.',
      en: 'Treat the equipment with care.\nFood and drinks on the side table only.\nNo cheats.',
    },
    stock: { lowAt: 5 },
    automation: [
      {
        id: uuid(),
        name: '5 минут до конца — предложить продлить',
        enabled: true,
        trigger: { kind: 'minutesLeft', value: 5 },
        action: { kind: 'message', text: 'Осталось 5 минут. Добавьте время в кошельке, чтобы не прерывать игру.' },
        fired: 0,
        lastFiredAt: null,
      },
      {
        id: uuid(),
        name: 'Свободный ПК 30 минут — выключить',
        enabled: false,
        trigger: { kind: 'pcIdleMinutes', value: 30 },
        action: { kind: 'shutdownPc' },
        fired: 0,
        lastFiredAt: null,
      },
      {
        id: uuid(),
        name: 'Каждый 10-й визит — бонус 10 000',
        enabled: true,
        trigger: { kind: 'visitCount', value: 10 },
        action: { kind: 'bonus', amount: 1_000_000 },
        fired: 0,
        lastFiredAt: null,
      },
    ],
    notifications: {
      telegramBotToken: '',
      telegramChatId: '',
      events: {
        shiftClosed: true,
        pcOffline: true,
        bigTopup: true,
        lowStock: true,
        ruleFired: false,
        sessionOpened: false,
      },
      bigTopupAt: 20_000_000,
    },
    webhooks: [],
    apiKey: `ck_${uuid().replace(/-/g, '')}`,
    staff: [
      { id: 'owner', name: 'Владелец', role: 'owner', pin: '0000', active: true },
      { id: 'cashier-1', name: 'Кассир Азиз', role: 'cashier', pin: '1111', active: true },
    ],
    shifts: [],
    staffTokens: {},
  };
}

type WithClub = typeof db & { club?: ClubConfig };

/** The club document, created with defaults on first use (and topped up when fields were added later). */
export function club(): ClubConfig {
  const store = db as WithClub;
  if (!store.club) {
    store.club = defaults();
    markDirty();
  } else if (store.club.version !== CONFIG_VERSION) {
    store.club = { ...defaults(), ...store.club, version: CONFIG_VERSION };
    markDirty();
  }
  return store.club;
}

export function saveClub(): void {
  markDirty();
}

// ---------------------------------------------------------------------------------------------------------------------
// Clients
// ---------------------------------------------------------------------------------------------------------------------

export function profileOf(userId: string): ClientProfile {
  const c = club();
  return (
    c.clients[userId] ?? { groupId: null, note: '', blacklisted: false, phone: '', birthYear: null, telegram: '' }
  );
}

/** Lifetime spend on time and the shop, minor units (positive number). */
export function spentOf(userId: string): number {
  let sum = 0;
  for (const tx of db.transactions) {
    if (tx.userId === userId && (tx.type === 'charge' || tx.type === 'purchase') && tx.amount.amount < 0) {
      sum += -tx.amount.amount;
    }
  }
  return sum;
}

export function loyaltyOf(userId: string): LoyaltyLevel {
  const spent = spentOf(userId);
  const levels = [...club().loyalty].sort((a, b) => a.minSpent - b.minSpent);
  let current = levels[0] ?? { level: 1, name: '', minSpent: 0, discountPct: 0 };
  for (const l of levels) {
    if (spent >= l.minSpent) current = l;
  }
  return current;
}

export function visitsOf(userId: string): number {
  return db.sessions.filter((s) => s.userId === userId).length;
}

export function isMinor(userId: string, at = new Date()): boolean {
  const year = profileOf(userId).birthYear;
  return year !== null && at.getFullYear() - year < club().limits.minorAge;
}

/** `true` when the curfew for minors is in effect at this local time. */
export function inCurfew(at = new Date()): boolean {
  const [h, m] = club().limits.minorCurfew.split(':').map(Number) as [number, number];
  const mins = at.getHours() * 60 + at.getMinutes();
  return mins >= h * 60 + m || mins < 6 * 60;
}

// ---------------------------------------------------------------------------------------------------------------------
// Pricing
// ---------------------------------------------------------------------------------------------------------------------

function minutesOf(hhmm: string): number {
  const [h, m] = hhmm.split(':').map(Number) as [number, number];
  return h * 60 + m;
}

export function activeHappyHour(zone: string, at = new Date()): HappyHour | null {
  const mins = at.getHours() * 60 + at.getMinutes();
  for (const hh of club().happyHours) {
    if (!hh.days.includes(at.getDay())) continue;
    if (hh.zones.length > 0 && !hh.zones.some((z) => z.toLowerCase() === zone.toLowerCase())) continue;
    const from = minutesOf(hh.from);
    const to = minutesOf(hh.to);
    const inside = from <= to ? mins >= from && mins < to : mins >= from || mins < to;
    if (inside) return hh;
  }
  return null;
}

export interface PriceQuote {
  base: Money;
  dayPct: number;
  discountPct: number;
  discountReason: string | null;
  total: Money;
}

/**
 * Price of `minutes` on `tariff` for `userId` in `zone` now: base × weekday/holiday percent, minus the best single
 * discount among client group, loyalty level and a happy hour (discounts do not stack — the club always knows the
 * worst case).
 */
export function quote(tariff: Tariff, minutes: number, userId: string | null, zone: string, at = new Date()): PriceQuote {
  const c = club();
  const base = tariffPriceFor(tariff, minutes);
  const dateKey = at.toISOString().slice(0, 10);
  const dayPct = c.pricing.holidays.includes(dateKey) ? c.pricing.holidayPct : (c.pricing.weekdayPct[at.getDay()] ?? 100);
  const candidates: { pct: number; reason: string }[] = [];
  if (userId) {
    const group = c.groups.find((g) => g.id === profileOf(userId).groupId);
    if (group && group.discountPct > 0) candidates.push({ pct: group.discountPct, reason: group.name });
    const level = loyaltyOf(userId);
    if (level.discountPct > 0) candidates.push({ pct: level.discountPct, reason: level.name });
  }
  const hh = activeHappyHour(zone, at);
  if (hh) candidates.push({ pct: hh.discountPct, reason: hh.name });
  const best = candidates.sort((a, b) => b.pct - a.pct)[0] ?? null;
  const discountPct = best?.pct ?? 0;
  const total = Math.round((base.amount * dayPct * (100 - discountPct)) / 10000 / 100) * 100;
  return { base, dayPct, discountPct, discountReason: best?.reason ?? null, total: uzs(Math.max(0, total)) };
}

/** Bonus credited for a top-up of `amount` (minor units) by the tier table. */
export function topupBonus(amount: number): number {
  const tier = [...club().bonusTiers].sort((a, b) => b.minAmount - a.minAmount).find((t) => amount >= t.minAmount);
  return tier ? Math.round((amount * tier.bonusPct) / 100 / 100) * 100 : 0;
}

// ---------------------------------------------------------------------------------------------------------------------
// Notifications and webhooks
// ---------------------------------------------------------------------------------------------------------------------

const EVENT_TITLE: Record<ClubEvent, string> = {
  shiftClosed: 'Смена закрыта',
  pcOffline: 'ПК не в сети',
  bigTopup: 'Крупное пополнение',
  lowStock: 'Товар заканчивается',
  ruleFired: 'Сработало правило',
  sessionOpened: 'Открыт сеанс',
};

/** Fire-and-forget: Telegram message to the owner and POSTs to subscribed webhooks. Never throws. */
export function emit(event: ClubEvent, text: string, data: Record<string, unknown> = {}): void {
  const c = club();
  const n = c.notifications;
  if (n.events[event] && n.telegramBotToken && n.telegramChatId) {
    void fetch(`https://api.telegram.org/bot${n.telegramBotToken}/sendMessage`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ chat_id: n.telegramChatId, text: `${EVENT_TITLE[event]}\n${text}` }),
    }).catch(() => undefined);
  }
  for (const hook of c.webhooks) {
    if (!hook.enabled || !hook.events.includes(event)) continue;
    void fetch(hook.url, {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-clubshell-event': event },
      body: JSON.stringify({ event, at: now(), text, data }),
    })
      .then((r) => {
        hook.lastStatus = r.status;
        hook.lastAt = now();
        markDirty();
      })
      .catch(() => {
        hook.lastStatus = 0;
        hook.lastAt = now();
        markDirty();
      });
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Automation
// ---------------------------------------------------------------------------------------------------------------------

async function run(rule: AutomationRule, target: { pcId?: string | null; user?: UserRecord | null }): Promise<void> {
  const a = rule.action;
  const pcId = target.pcId ?? null;
  const user = target.user ?? null;
  switch (a.kind) {
    case 'message':
      if (pcId)
        await sendCommand(pcId, 'message', { id: uuid(), from: club().branding.clubName, text: a.text, level: 'info', requiresAck: false });
      break;
    case 'bonus':
      if (user) {
        applyTransaction(user, 'bonus', uzs(a.amount), `Бонус: ${rule.name}`, rule.id);
        pushToUser(user.id, 'walletUpdated', balanceOf(user));
      }
      break;
    case 'lockPc':
      if (pcId) await sendCommand(pcId, 'lock', { reason: 'staff', message: rule.name });
      break;
    case 'shutdownPc':
      if (pcId) await sendCommand(pcId, 'shutdown', { delaySec: 30, force: false, message: rule.name });
      break;
    case 'notifyOwner':
      break;
  }
  rule.fired += 1;
  rule.lastFiredAt = now();
  markDirty();
  const who = [pcId ? findPc(pcId)?.name : null, user?.displayName].filter(Boolean).join(' · ');
  emit('ruleFired', `${rule.name}${who ? ` — ${who}` : ''}${a.kind === 'notifyOwner' ? `\n${a.text}` : ''}`, {
    ruleId: rule.id,
    pcId,
    userId: user?.id ?? null,
  });
}

function fire(rule: AutomationRule, target: { pcId?: string | null; user?: UserRecord | null }): void {
  run(rule, target).catch((e: unknown) => console.warn(`[rules] ${rule.name}: ${(e as Error).message}`));
}

/** Event hooks called by the routes. */
export const clubHooks = {
  sessionOpened(rec: SessionRecord): void {
    const user = findUser(rec.userId) ?? null;
    const visits = visitsOf(rec.userId);
    for (const r of club().automation) {
      if (!r.enabled) continue;
      if (r.trigger.kind === 'sessionStarted') fire(r, { pcId: rec.pcId, user });
      if (r.trigger.kind === 'visitCount' && r.trigger.value > 0 && visits % r.trigger.value === 0) fire(r, { pcId: rec.pcId, user });
    }
    emit('sessionOpened', `${findPc(rec.pcId)?.name ?? rec.pcId} · ${user?.displayName ?? ''}`, { sessionId: rec.id });
  },
  topUp(user: UserRecord, amount: number): void {
    for (const r of club().automation) {
      if (r.enabled && r.trigger.kind === 'topupAtLeast' && amount >= r.trigger.value) fire(r, { user, pcId: null });
    }
    if (amount >= club().notifications.bigTopupAt) {
      emit('bigTopup', `${user.displayName}: ${Math.round(amount / 100).toLocaleString('ru-RU')} сум`, { userId: user.id, amount });
    }
  },
  stockChanged(title: string, qty: number): void {
    if (qty <= club().stock.lowAt) emit('lowStock', `${title}: осталось ${qty}`, { title, qty });
  },
};

const firedFor = new Map<string, Set<string>>();
const freeSince = new Map<string, number>();
const offlineSince = new Map<string, boolean>();

function once(ruleId: string, key: string): boolean {
  let set = firedFor.get(ruleId);
  if (!set) {
    set = new Set();
    firedFor.set(ruleId, set);
  }
  if (set.has(key)) return false;
  set.add(key);
  return true;
}

/** Called every second by the server tick: time-based rules and PC-offline notifications. */
export function tickClub(t: number): void {
  const c = club();
  for (const pc of db.pcs) {
    if (pc.status === 'free') {
      if (!freeSince.has(pc.id)) freeSince.set(pc.id, t);
    } else {
      freeSince.delete(pc.id);
    }
    const offline = pc.status === 'offline';
    if (offline && !offlineSince.get(pc.id)) emit('pcOffline', pc.name, { pcId: pc.id });
    offlineSince.set(pc.id, offline);
  }
  for (const r of c.automation) {
    if (!r.enabled) continue;
    if (r.trigger.kind === 'minutesLeft') {
      const limit = r.trigger.value * 60;
      for (const pc of db.pcs) {
        const rec = openSessionForPc(pc.id);
        if (!rec || rec.state !== 'active') continue;
        const left = viewSession(rec).secondsLeft;
        if (left >= 0 && left <= limit && once(r.id, rec.id)) fire(r, { pcId: pc.id, user: findUser(rec.userId) ?? null });
      }
    } else if (r.trigger.kind === 'pcIdleMinutes') {
      const limit = r.trigger.value * 60_000;
      for (const [pcId, since] of freeSince) {
        if (t - since >= limit && once(r.id, `${pcId}:${since}`)) fire(r, { pcId, user: null });
      }
    }
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Shifts
// ---------------------------------------------------------------------------------------------------------------------

/** Money movements since `from` — the X report of an open shift, the Z report when it closes. */
export function totalsSince(from: string, to: string = now()): ShiftTotals {
  const t: ShiftTotals = { topUpCash: 0, topUpOther: 0, sessions: 0, shop: 0, refunds: 0, bonuses: 0, count: 0 };
  for (const tx of db.transactions) {
    if (tx.createdAt < from || tx.createdAt > to) continue;
    t.count += 1;
    const a = tx.amount.amount;
    switch (tx.type) {
      case 'topUp':
        if (/cash|налич/i.test(tx.description)) t.topUpCash += a;
        else t.topUpOther += a;
        break;
      case 'charge':
        t.sessions += -a;
        break;
      case 'purchase':
        t.shop += -a;
        break;
      case 'refund':
        t.refunds += a;
        break;
      case 'bonus':
        t.bonuses += a;
        break;
      default:
        break;
    }
  }
  return t;
}

export function openShift(): ShiftRecord | null {
  return club().shifts.find((s) => s.closedAt === null) ?? null;
}
