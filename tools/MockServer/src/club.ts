/**
 * Club customisation: everything a club owner configures for their own venue from the admin console — staff and
 * roles, cashier shifts, pricing (weekday / holiday multipliers, client groups, happy hours, top-up bonus tiers, promo
 * codes, loyalty levels), branding and the player-facing features, the game catalogue, banners, club rules, stock
 * thresholds, "if → then" automation rules and outbound webhooks (Telegram is banned in the product).
 *
 * Stored as `db.club` next to the rest of the mock store (persisted with it). {@link ensureClub} fills a missing or
 * older document with defaults, so an existing `.mock-db.json` keeps working. The pricing and rule engines live here
 * too so the admin routes, the kiosk routes and the tick share one implementation.
 */
import {
  tariffIsValidFor,
  tariffPriceFor,
  weekdayFromJsDay,
  type Money,
  type ShellFeatures,
  type Tariff,
} from '@clubshell/contracts';
import {
  applyTransaction,
  balanceOf,
  db,
  errors,
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
  /** The owner's mark: may sign in to their own Windows account on the club's PCs (an owner always may). */
  pcService?: boolean;
  /** The owner's mark: may write a debt off (SHELL_CHANGES 90; an owner always may). */
  debtWriteOff?: boolean;
}

/** `pcService` as the server answers it: an owner always, a cashier with the owner's mark. */
export function mayServicePcs(s: StaffRecord): boolean {
  return s.role === 'owner' || s.pcService === true;
}

/** Payment methods of a counter top-up; `other` is any top-up the console did not book under one of them. */
export const PAY_METHODS = ['cash', 'card', 'payme', 'click', 'uzum'] as const;
export type PayMethod = (typeof PAY_METHODS)[number];

export interface ShiftTotals {
  topUpCash: number;
  topUpOther: number;
  /** The same top-ups by method; they add up to `topUpCash + topUpOther`. */
  topUpByMethod: Record<PayMethod | 'other', number>;
  sessions: number;
  shop: number;
  refunds: number;
  bonuses: number;
  count: number;
  /** Cash put into / taken out of the drawer (`cashMoves`). */
  cashIn: number;
  cashOut: number;
  /** Cash given back to walk-in guests (cash `adjustment` rows). */
  payouts: number;
  /** Debts written off, without money (SHELL_CHANGES 90), and how many. */
  writeOffs: number;
  writeOffCount: number;
  /** Cash top-ups booked with the club API key: in `topUpCash`, but never in the drawer. */
  apiCash: number;
  /**
   * Bar sales of the shift by method, net of the voids booked in it (D-57); `balance` may be negative in a shift that
   * voids an earlier shift's balance sale. Absent from a Z saved before cash desk part 3.
   */
  shopByMethod: Record<ShopMethod, number>;
  /** What the shift's voids gave back, and how many there were. */
  shopVoids: number;
  shopVoidCount: number;
  /** Desk top-ups taken back in the shift (D-91), already netted out of the top-up sums, and how many. */
  topUpCancels?: number;
  topUpCancelCount?: number;
  /** D-106: the play time the bonus account paid, net of the bonus its refunds gave back (inside `sessions`, never cash). */
  bonusSpent?: number;
}

/** How a bar sale was paid: a counter method, or the client's balance. */
export type ShopMethod = PayMethod | 'balance';
export const SHOP_METHODS: readonly ShopMethod[] = ['cash', 'card', 'payme', 'click', 'uzum', 'balance'];

/** Why cash went into or out of the drawer; the note is the cashier's own words (required for `other`). */
export type CashReason = 'change' | 'collection' | 'expenses' | 'other';
export const CASH_REASONS: readonly CashReason[] = ['change', 'collection', 'expenses', 'other'];

export interface CashMove {
  id: string;
  kind: 'in' | 'out';
  amount: number;
  reasonCode: CashReason;
  note: string | null;
  at: string;
  staffId: string;
  staffName: string;
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
  /** Append-only, like the server's `cash_movements`. */
  cashMoves: CashMove[];
  /** What the drawer should have held at the close; null while open. */
  expectedCash: number | null;
  closedBy: string | null;
}

/** A shift as the routes send it: the moves stay inside (the feed and the totals carry them). */
export function shiftView(s: ShiftRecord): Omit<ShiftRecord, 'cashMoves'> {
  const { cashMoves: _moves, ...rest } = s;
  return rest;
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
}

export interface BonusTier {
  /** Minimum top-up, minor units. */
  minAmount: number;
  bonusPct: number;
}

/**
 * The top-up promotion «x2» (D-84, beyond the contract) as the server keeps it: on, a desk top-up of at least
 * `minAmount` gets `bonusPct` % on top (100 = x2) on `days` (0 = Sunday; empty = every day), `from`–`to` of the club's
 * clock (both null = all day; `to` < `from` runs past midnight), until `until` (null = no end). Not added to the tier
 * bonus: the larger of the two wins.
 */
export interface TopUpPromo {
  enabled: boolean;
  bonusPct: number;
  minAmount: number;
  days: number[];
  from: string | null;
  to: string | null;
  until: string | null;
}

/** The free PCs' screensaver (D-87) as the server keeps it. */
export interface ScreensaverSettings {
  enabled: boolean;
  afterMinutes: number;
  items: { url: string; type: 'image' | 'video'; durationSec: number }[];
}

export const DEFAULT_SCREENSAVER: ScreensaverSettings = { enabled: false, afterMinutes: 5, items: [] };

export const DEFAULT_TOPUP_PROMO: TopUpPromo = {
  enabled: false,
  bonusPct: 100,
  minAmount: 0,
  days: [],
  from: null,
  to: null,
  until: null,
};

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

export type ClubEvent =
  | 'shiftClosed'
  | 'pcOffline'
  | 'bigTopup'
  | 'lowStock'
  | 'ruleFired'
  | 'sessionOpened'
  | 'suspicious'
  | 'hardware';

export const CLUB_EVENTS: readonly ClubEvent[] = [
  'shiftClosed',
  'pcOffline',
  'bigTopup',
  'lowStock',
  'ruleFired',
  'sessionOpened',
  'suspicious',
  'hardware',
];

/** What a staff member did at the counter; the journal the owner's "Контроль" page reads. */
export type AuditAction =
  | 'shiftOpen'
  | 'shiftClose'
  | 'topUp'
  | 'sessionOpen'
  | 'sessionExtend'
  | 'sessionEnd'
  | 'promoRedeem'
  | 'clientGroup'
  | 'blacklist'
  | 'stockReceive'
  | 'stockEdit'
  | 'pcCommand'
  | 'clientPassword'
  | 'clientCard'
  // Beyond the contract's enum (D-44, D-68): in the feed and the Z, not in /admin/control.
  | 'cashIn'
  | 'cashOut'
  | 'payout'
  | 'debtWriteOff'
  | 'shopSale'
  | 'shopVoid'
  | 'sessionMove'
  | 'callAck'
  | 'callResolve'
  | 'stockCreate'
  | 'stockArchive'
  // D-91: a desk top-up taken back; listed by /admin/control apart (`corrections`), not in its journal.
  | 'topUpCancel';

export interface AuditEntry {
  id: string;
  at: string;
  staffId: string;
  staffName: string;
  /** Shift open at the time, `null` when the operation happened with no shift open. */
  shiftId: string | null;
  action: AuditAction;
  userId: string | null;
  pcId: string | null;
  /** Money moved, minor units (refunds and top-ups positive); 0 when none. */
  amount: number;
  /** Short human-readable detail in Russian (the console translates the fixed parts). */
  detail: string;
  /** Extra facts the rules and the feed read (e.g. `sessionMinutes`, `discountPct`, `method`, a session's `quote`). */
  meta: Record<string, string | number | boolean | null | Record<string, number>>;
  /** The owner's own money operation (D-101): in no shift. */
  owner?: boolean;
}

/** Thresholds of the cashier-control rules, tuned by the owner. */
export interface ControlSettings {
  /** A session ended with a refund within this many minutes of opening counts as an early end. */
  earlyEndMinutes: number;
  /** This many early ends by one cashier in one shift is a red flag. */
  earlyEndsPerShift: number;
  /** Moving a client into a group with at least this discount is flagged. */
  discountPct: number;
  /** Topping up the same client this many times in one shift is flagged. */
  sameClientTopups: number;
  /** Cash short at shift close by more than this (minor units) is a red flag. */
  shortfallFrom: number;
}

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
  /** The hall plan drawn in the hall editor (SHELL_CHANGES 94); absent or null — none. */
  hallPlan?: import('./hall.js').HallPlan | null;
  pricing: {
    /** Percent of the base price per weekday, 0 = Sunday. */
    weekdayPct: number[];
    holidays: string[];
    holidayPct: number;
  };
  groups: ClientGroup[];
  clients: Record<string, ClientProfile>;
  bonusTiers: BonusTier[];
  topUpPromo?: TopUpPromo;
  screensaver?: ScreensaverSettings;
  promoCodes: PromoCode[];
  happyHours: HappyHour[];
  loyalty: LoyaltyLevel[];
  /**
   * Desk postpaid: `guestPostpaid` (absent = on) lets the desk seat guests postpaid; `guestDebtLimit` / `memberDebtLimit`
   * (null or 0 — no cap) cap a desk postpaid seat's bill / how far below zero a member may go — desk sessions only, the
   * kiosk never opens postpaid on them. `cashOutOwnerOnly` keeps cash-outs to the owner.
   */
  limits: {
    minorAge: number;
    minorCurfew: string;
    guestPostpaid?: boolean;
    guestDebtLimit?: number | null;
    memberDebtLimit?: number | null;
    cashOutOwnerOnly?: boolean;
    autoExtendMinutes?: number;
    /** Players create their own account on the lock screen (D-92); absent — off. */
    selfSignup?: boolean;
  };
  catalog: { order: string[]; hidden: string[]; featured: string[] };
  banners: Banner[];
  rulesText: { ru: string; uz: string; en: string };
  stock: { lowAt: number };
  automation: AutomationRule[];
  /** A top-up of at least this much (minor units) raises the `bigTopup` event. */
  notifications: { bigTopupAt: number };
  webhooks: Webhook[];
  apiKey: string;
  staff: StaffRecord[];
  shifts: ShiftRecord[];
  /** Tokens of signed-in staff → staff id. */
  staffTokens: Record<string, string>;
  /** Staff actions, newest first (capped). */
  audit: AuditEntry[];
  control: ControlSettings;
}

const CONFIG_VERSION = 5;

export const DEFAULT_CONTROL: ControlSettings = {
  earlyEndMinutes: 10,
  earlyEndsPerShift: 3,
  discountPct: 30,
  sameClientTopups: 3,
  shortfallFrom: 500_000,
};

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
      qrLogin: true,
      gpuPanel: false,
      lockScreenPower: true,
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
    notifications: { bigTopupAt: 20_000_000 },
    webhooks: [],
    apiKey: `ck_${uuid().replace(/-/g, '')}`,
    staff: [
      { id: 'owner', name: 'Владелец', role: 'owner', pin: '0000', active: true },
      { id: 'cashier-1', name: 'Кассир Азиз', role: 'cashier', pin: '1111', active: true },
    ],
    shifts: [],
    staffTokens: {},
    audit: [],
    control: { ...DEFAULT_CONTROL },
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
    const base = defaults();
    const old = store.club;
    store.club = {
      ...base,
      ...old,
      // Sections added later keep the stored values and gain the new keys; v4 drops the Telegram settings, v5 gives
      // every shift its drawer moves, expected cash and closer.
      notifications: { bigTopupAt: old.notifications?.bigTopupAt ?? base.notifications.bigTopupAt },
      control: { ...base.control, ...old.control },
      audit: old.audit ?? [],
      shifts: (old.shifts ?? []).map((s) => ({
        ...s,
        cashMoves: s.cashMoves ?? [],
        expectedCash: s.expectedCash ?? null,
        closedBy: s.closedBy ?? null,
      })),
      version: CONFIG_VERSION,
    };
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
  return c.clients[userId] ?? { groupId: null, note: '', blacklisted: false, phone: '', birthYear: null };
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
 * worst case). Without `userId` it is a walk-in: no group, and the loyalty level of zero spend, as the server prices it —
 * what a new guest account has, so a walk-in guest's seat costs exactly its quote (D-48).
 */
export function quote(
  tariff: Tariff,
  minutes: number,
  userId: string | null,
  zone: string,
  at = new Date(),
): PriceQuote {
  const c = club();
  const base = tariffPriceFor(tariff, minutes);
  const dateKey = at.toISOString().slice(0, 10);
  const dayPct = c.pricing.holidays.includes(dateKey)
    ? c.pricing.holidayPct
    : (c.pricing.weekdayPct[at.getDay()] ?? 100);
  const candidates: { pct: number; reason: string }[] = [];
  if (userId) {
    const group = c.groups.find((g) => g.id === profileOf(userId).groupId);
    if (group && group.discountPct > 0) candidates.push({ pct: group.discountPct, reason: group.name });
  }
  // A walk-in has spent nothing: only a level reached from 0 (the server's `LevelOf(0)`).
  const level = userId
    ? loyaltyOf(userId)
    : [...c.loyalty]
        .sort((a, b) => a.minSpent - b.minSpent)
        .filter((l) => l.minSpent <= 0)
        .at(-1);
  if (level && level.discountPct > 0) candidates.push({ pct: level.discountPct, reason: level.name });
  const hh = activeHappyHour(zone, at);
  if (hh) candidates.push({ pct: hh.discountPct, reason: hh.name });
  const best = candidates.sort((a, b) => b.pct - a.pct)[0] ?? null;
  const discountPct = best?.pct ?? 0;
  const total = Math.round((base.amount * dayPct * (100 - discountPct)) / 10000 / 100) * 100;
  return { base, dayPct, discountPct, discountReason: best?.reason ?? null, total: uzs(Math.max(0, total)) };
}

/**
 * D-90, time «по сумме»: the quote of the most whole minutes of an hourly tariff the cashier's `amount` buys now, as the
 * server sells it — from the shortest purchase (5, or the tariff's minimum; 5 for an extension) up to 24 h and the
 * tariff's maximum less `purchasedMinutes` — so the price is never above the amount. A package sells only itself
 * (`400 amount package`); too little for the shortest purchase is `409 amountTooLow {minutes, total}`. The price never
 * falls as minutes grow, so a binary search finds it.
 */
export function minutesFor(
  tariff: Tariff,
  amount: number,
  userId: string | null,
  zone: string,
  purchasedMinutes = 0,
): { minutes: number; priced: PriceQuote } {
  if (tariff.isPackage) throw errors.validation('amount', 'package');
  const shortest = purchasedMinutes > 0 ? 5 : Math.max(5, tariff.minMinutes);
  const longest = Math.max(
    shortest,
    Math.min(1440, tariff.maxMinutes != null ? tariff.maxMinutes - purchasedMinutes : 1440),
  );
  let best = quote(tariff, shortest, userId, zone);
  if (best.total.amount > amount) throw errors.conflict('amountTooLow', { minutes: shortest, total: best.total });
  let lo = shortest;
  let hi = longest;
  while (lo < hi) {
    const mid = lo + Math.ceil((hi - lo) / 2);
    const q = quote(tariff, mid, userId, zone);
    if (q.total.amount <= amount) {
      lo = mid;
      best = q;
    } else hi = mid - 1;
  }
  return { minutes: lo, priced: best };
}

/** D-90: the `amount` of a purchase «по сумме» (1 tiyin … 1 000 000 сум), prepaid only; null — none sent. */
export function budgetOf(b: Record<string, unknown>, prepaid: boolean): number | null {
  const raw = b['amount'];
  if (raw === undefined || raw === null) return null;
  if (!prepaid) throw errors.validation('amount', 'postpaid');
  if (typeof raw !== 'number' || !Number.isInteger(raw)) throw errors.validation('amount', 'format');
  if (raw < 1) throw errors.validation('amount', 'min');
  if (raw > 100_000_000) throw errors.validation('amount', 'max');
  return raw;
}

function localClock(d: Date): string {
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

/**
 * Why `tariff` cannot be sold on a PC of `zone` now — its zones, its time windows (the mock's local clock; the server
 * uses the club's time zone) — or null. The quote names it; opening and extending refuse with it (403 `policyDenied`).
 */
export function tariffRule(tariff: Tariff, zone: string, at = new Date()): 'tariffZone' | 'tariffTime' | null {
  if (tariff.zones.length > 0 && !tariff.zones.some((z) => z.toLowerCase() === zone.toLowerCase())) return 'tariffZone';
  if (!tariffIsValidFor(tariff, zone, weekdayFromJsDay(at.getDay()), localClock(at))) return 'tariffTime';
  return null;
}

const promoMinutes = (hhmm: string | null): number | null =>
  hhmm && /^\d{2}:\d{2}$/.test(hhmm) ? Number(hhmm.slice(0, 2)) * 60 + Number(hhmm.slice(3)) : null;

/** Whether the «x2» promotion runs at `at` (the mock's clock is the club's). */
export function topUpPromoActive(p: TopUpPromo | undefined, at = new Date()): boolean {
  if (!p?.enabled || (p.until && at.getTime() >= Date.parse(p.until))) return false;
  const from = promoMinutes(p.from);
  const to = promoMinutes(p.to);
  const minute = at.getHours() * 60 + at.getMinutes();
  if (from === null || to === null || from === to) return p.days.length === 0 || p.days.includes(at.getDay());
  const overnight = to < from;
  const inside = overnight ? minute >= from || minute < to : minute >= from && minute < to;
  const day = overnight && minute < to ? (at.getDay() + 6) % 7 : at.getDay();
  return inside && (p.days.length === 0 || p.days.includes(day));
}

const pctOf = (amount: number, pct: number): number => Math.round((amount * pct) / 100 / 100) * 100;

/** Bonus credited for a top-up of `amount` (minor units): the tier table's or the «x2» promotion's, the larger. */
export function topupBonus(amount: number): number {
  const tier = [...club().bonusTiers].sort((a, b) => b.minAmount - a.minAmount).find((t) => amount >= t.minAmount);
  const promo = club().topUpPromo;
  const promoPct = amount > 0 && promo && amount >= promo.minAmount && topUpPromoActive(promo) ? promo.bonusPct : 0;
  return Math.max(tier ? pctOf(amount, tier.bonusPct) : 0, pctOf(amount, promoPct));
}

// ---------------------------------------------------------------------------------------------------------------------
// Notifications and webhooks
// ---------------------------------------------------------------------------------------------------------------------

/** Fire-and-forget POSTs to the webhooks subscribed to `event`. Never throws. */
export function emit(event: ClubEvent, text: string, data: Record<string, unknown> = {}): void {
  for (const hook of club().webhooks) {
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
        await sendCommand(pcId, 'message', {
          id: uuid(),
          from: club().branding.clubName,
          text: a.text,
          level: 'info',
          requiresAck: false,
        });
      break;
    case 'bonus':
      // A walk-in guest's throwaway account never gets bonus money (D-36); the rule's other actions still fire.
      if (user && !user.transient) {
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
      if (r.trigger.kind === 'visitCount' && r.trigger.value > 0 && visits % r.trigger.value === 0)
        fire(r, { pcId: rec.pcId, user });
    }
    emit('sessionOpened', `${findPc(rec.pcId)?.name ?? rec.pcId} · ${user?.displayName ?? ''}`, { sessionId: rec.id });
  },
  topUp(user: UserRecord, amount: number): void {
    for (const r of club().automation) {
      if (r.enabled && r.trigger.kind === 'topupAtLeast' && amount >= r.trigger.value) fire(r, { user, pcId: null });
    }
    if (amount >= club().notifications.bigTopupAt) {
      emit('bigTopup', `${user.displayName}: ${Math.round(amount / 100).toLocaleString('ru-RU')} сум`, {
        userId: user.id,
        amount,
      });
    }
  },
  /** A tracked quantity changed: `lowStock` once per downward crossing of the threshold (`before` unknown — any low). */
  stockChanged(title: string, qty: number, before?: number | null): void {
    const lowAt = club().stock.lowAt;
    if (qty > lowAt) return;
    if (before !== undefined && before !== null && before <= lowAt) return;
    emit('lowStock', `${title}: осталось ${qty}`, { title, qty });
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
        if (left >= 0 && left <= limit && once(r.id, rec.id))
          fire(r, { pcId: pc.id, user: findUser(rec.userId) ?? null });
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

/**
 * The method a top-up was booked under, read from its description: the counter writes `(card)` and friends, the kiosk
 * `via Payme`, the seed `in cash`.
 */
/** How a counter top-up was paid, in the server's wording: «наличными», «картой», «через Payme». */
export function counterMethod(method: string): string {
  if (method === 'cash') return 'наличными';
  if (method === 'card') return 'картой';
  return `через ${method.charAt(0).toUpperCase()}${method.slice(1)}`;
}

export function topUpMethod(description: string): PayMethod | 'other' {
  if (/cash|налич/i.test(description)) return 'cash';
  if (/карт/i.test(description)) return 'card';
  return PAY_METHODS.find((m) => new RegExp(`\\b${m}\\b`, 'i').test(description)) ?? 'other';
}

/** Description of a cash payout to a walk-in guest (an `adjustment` row). */
export const PAYOUT_DESCRIPTION = 'Выдано наличными на кассе';
/** The row that takes a desk top-up back (D-91): a `topUp` of −amount, its method still in the description. */
export const TOPUP_CANCEL_DESCRIPTION = 'Отмена: пополнение';
/** A cash top-up booked with the club API key says so in its description. */
export const API_TOPUP = '(через API клуба)';
/**
 * Ledger rows of the owner's own operations (D-101: top-ups and their cancels, seat payments, payouts, write-offs, bar
 * balance sales and their voids): in no shift, so no X/Z,
 * drawer or «Принято за смену» counts them.
 */
export const OWNER_TRANSACTIONS = new Set<string>();

/**
 * Money movements since `from` — the X report of an open shift, the Z report when it closes (with its drawer moves).
 * `shop` is the bar net of voids: the balance sales through their `purchase` rows, the method sales from the shift's
 * own sales and voids (D-57).
 */
export function totalsSince(from: string, to: string = now(), shift: ShiftRecord | null = null): ShiftTotals {
  const t: ShiftTotals = {
    topUpCash: 0,
    topUpOther: 0,
    topUpByMethod: { cash: 0, card: 0, payme: 0, click: 0, uzum: 0, other: 0 },
    sessions: 0,
    shop: 0,
    refunds: 0,
    bonuses: 0,
    count: 0,
    cashIn: 0,
    cashOut: 0,
    payouts: 0,
    writeOffs: 0,
    writeOffCount: 0,
    apiCash: 0,
    shopByMethod: { cash: 0, card: 0, payme: 0, click: 0, uzum: 0, balance: 0 },
    shopVoids: 0,
    shopVoidCount: 0,
    topUpCancels: 0,
    topUpCancelCount: 0,
    bonusSpent: 0,
  };
  for (const m of shift?.cashMoves ?? []) {
    if (m.kind === 'in') t.cashIn += m.amount;
    else t.cashOut += m.amount;
  }
  for (const s of db.shopSales) {
    if (shift ? s.shiftId !== shift.id : s.createdAt < from || s.createdAt > to) continue;
    const signed = s.kind === 'sale' ? s.total : -s.total;
    t.shopByMethod[s.method] += signed;
    if (s.method !== 'balance') t.shop += signed;
    if (s.kind === 'void') {
      t.shopVoids += s.total;
      t.shopVoidCount += 1;
    }
  }
  for (const tx of db.transactions) {
    if (tx.createdAt < from || tx.createdAt > to || OWNER_TRANSACTIONS.has(tx.id)) continue;
    t.count += 1;
    const a = tx.amount.amount;
    if (tx.bonus === true && (tx.type === 'charge' || tx.type === 'refund')) t.bonusSpent = (t.bonusSpent ?? 0) - a;
    switch (tx.type) {
      case 'topUp': {
        const method = topUpMethod(tx.description);
        if (method === 'cash') t.topUpCash += a;
        else t.topUpOther += a;
        t.topUpByMethod[method] += a;
        if (method === 'cash' && tx.description.includes(API_TOPUP)) t.apiCash += a;
        if (tx.description.startsWith(TOPUP_CANCEL_DESCRIPTION)) {
          t.topUpCancels = (t.topUpCancels ?? 0) - a;
          t.topUpCancelCount = (t.topUpCancelCount ?? 0) + 1;
        }
        break;
      }
      case 'adjustment':
        if (tx.description === PAYOUT_DESCRIPTION) t.payouts += -a;
        if (tx.description.startsWith(WRITE_OFF_DESCRIPTION)) {
          t.writeOffs += a;
          t.writeOffCount += 1;
        }
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

/** The ledger description of a debt written off (SHELL_CHANGES 90), followed by the reason. */
export const WRITE_OFF_DESCRIPTION = 'Списание долга: ';

export function mayWriteOff(s: StaffRecord): boolean {
  return s.role === 'owner' || s.debtWriteOff === true;
}

export function openShift(): ShiftRecord | null {
  return club().shifts.find((s) => s.closedAt === null) ?? null;
}

/**
 * Cash the drawer should hold (D-41, D-57): the float + the desk's cash top-ups (not the API's) + cash in − cash out −
 * cash given back to guests + the bar's cash sales net of their voids. Only the server computes it; the console shows it.
 */
export function expectedCashOf(openingCash: number, t: ShiftTotals): number {
  return openingCash + (t.topUpCash - t.apiCash) + t.cashIn - t.cashOut - t.payouts + (t.shopByMethod?.cash ?? 0);
}

/** What the open shift's drawer should hold right now. */
export function drawerNow(shift: ShiftRecord): number {
  return expectedCashOf(shift.openingCash, totalsSince(shift.openedAt, now(), shift));
}
