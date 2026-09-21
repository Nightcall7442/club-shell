/**
 * Wallet contracts — mirror of `ClubShell.Contracts.Wallet` (Balance.cs, Tariff.cs, Transaction.cs).
 * Money is integer minor units (tiyin for UZS); timestamps are ISO-8601 UTC strings; times are `HH:mm`.
 */

/** Monetary amount in integer minor units; `{ amount, currency }` on the wire. */
export interface Money {
  /** Signed minor units (negative for charges/purchases where noted). */
  amount: number;
  /** ISO-4217 code, upper-case; `UZS` by default. */
  currency: string;
}

/** Currency assumed when none is given. */
export const DEFAULT_CURRENCY = 'UZS';

// ---- BEGIN MANUAL ----
/** Zero in the default currency. */
export const MONEY_ZERO: Readonly<Money> = Object.freeze({ amount: 0, currency: DEFAULT_CURRENCY });

/** Creates a Money value; the currency is trimmed and upper-cased, empty → `UZS`. Throws on a non-integer amount. */
export function money(amount: number, currency: string = DEFAULT_CURRENCY): Money {
  if (!Number.isSafeInteger(amount)) {
    throw new RangeError(`Money.amount must be a safe integer, got ${amount}`);
  }
  const code = currency.trim().toUpperCase();
  return { amount, currency: code.length === 0 ? DEFAULT_CURRENCY : code };
}

/** `true` when both amounts are denominated in the same currency (ordinal, case-sensitive). */
export function isSameCurrency(a: Money, b: Money): boolean {
  return a.currency === b.currency;
}

function ensureSameCurrency(a: Money, b: Money): void {
  if (!isSameCurrency(a, b)) {
    throw new Error(`Currency mismatch: ${a.currency} vs ${b.currency}`);
  }
}

/** Sum of two amounts; throws when currencies differ. */
export function addMoney(a: Money, b: Money): Money {
  ensureSameCurrency(a, b);
  return money(a.amount + b.amount, a.currency);
}

/** Difference of two amounts; throws when currencies differ. */
export function subtractMoney(a: Money, b: Money): Money {
  ensureSameCurrency(a, b);
  return money(a.amount - b.amount, a.currency);
}

/** Scales an amount by an integer factor (unit price × quantity). */
export function multiplyMoney(value: Money, factor: number): Money {
  if (!Number.isSafeInteger(factor)) {
    throw new RangeError(`Money factor must be a safe integer, got ${factor}`);
  }
  return money(value.amount * factor, value.currency);
}

/** Sums a list of amounts; an empty list yields {@link MONEY_ZERO}. */
export function sumMoney(values: readonly Money[]): Money {
  return values.length === 0 ? { ...MONEY_ZERO } : values.reduce((acc, v) => addMoney(acc, v));
}

/** Compares two amounts of the same currency (negative / 0 / positive); throws when currencies differ. */
export function compareMoney(a: Money, b: Money): number {
  ensureSameCurrency(a, b);
  return a.amount === b.amount ? 0 : a.amount < b.amount ? -1 : 1;
}

/** BCP-47 tags used for currency formatting per UI locale. */
const LOCALE_TAGS: Readonly<Record<string, string>> = { en: 'en-US', ru: 'ru-RU', uz: 'uz-UZ' };

/**
 * Formats an amount for display (`shell.json → ui.currencyFormat`): minor units are converted to major units
 * (÷100) and rendered with `Intl.NumberFormat`; `locale` is a UI locale (`en`/`ru`/`uz`) or any BCP-47 tag.
 * Falls back to `<major> <currency>` when the currency is unknown to the runtime.
 */
export function formatMoney(value: Money, locale: string, minorDigits = 0): string {
  const tag = LOCALE_TAGS[locale] ?? locale;
  const major = value.amount / 100;
  try {
    return new Intl.NumberFormat(tag, {
      style: 'currency',
      currency: value.currency,
      minimumFractionDigits: minorDigits,
      maximumFractionDigits: minorDigits,
    }).format(major);
  } catch {
    return `${major.toFixed(minorDigits)} ${value.currency}`;
  }
}
// ---- END MANUAL ----

/** Wallet balance of a user; payload of `wallet.balance` and the `wallet.updated` event. */
export interface Balance {
  /** Owner. */
  userId: string;
  /** Main (real money) balance. */
  amount: Money;
  /** Bonus balance (promotional, non-withdrawable). */
  bonus: Money;
  /** ISO-4217 code of both balances. */
  currency: string;
  /** Last change time; stale when served from cache while offline. */
  updatedAt: string;
}

// ---- BEGIN MANUAL ----
/** Main + bonus of a balance. */
export function balanceTotal(balance: Balance): Money {
  return addMoney(balance.amount, balance.bonus);
}
// ---- END MANUAL ----

/** Day of week used by tariff time windows. */
export const Weekday = {
  Mon: 'mon',
  Tue: 'tue',
  Wed: 'wed',
  Thu: 'thu',
  Fri: 'fri',
  Sat: 'sat',
  Sun: 'sun',
} as const;
/** Day of week used by tariff time windows. */
export type Weekday = (typeof Weekday)[keyof typeof Weekday];

// ---- BEGIN MANUAL ----
/** Weekdays in order Monday → Sunday. */
export const WEEKDAYS: readonly Weekday[] = [
  Weekday.Mon,
  Weekday.Tue,
  Weekday.Wed,
  Weekday.Thu,
  Weekday.Fri,
  Weekday.Sat,
  Weekday.Sun,
];

/** Maps a JS `Date.getDay()` value (0 = Sunday) to a {@link Weekday}. */
export function weekdayFromJsDay(day: number): Weekday {
  const w = WEEKDAYS[(day + 6) % 7];
  if (w === undefined) {
    throw new RangeError(`Invalid JS day ${day}`);
  }
  return w;
}
// ---- END MANUAL ----

/** Weekly time window (club local time, `HH:mm`); `to` < `from` wraps midnight. */
export interface TariffTimeWindow {
  /** Days the window applies to. */
  days: Weekday[];
  /** Start time, inclusive (`HH:mm`). */
  from: string;
  /** End time, exclusive (`HH:mm`). */
  to: string;
}

// ---- BEGIN MANUAL ----
/** Parses `HH:mm` (also `H:mm`, `HH:mm:ss`) to minutes since midnight; throws on malformed input. */
export function parseClubTime(text: string): number {
  const m = /^(\d{1,2}):(\d{2})(?::\d{2}(?:\.\d+)?)?$/.exec(text);
  const hours = m ? Number(m[1]) : NaN;
  const minutes = m ? Number(m[2]) : NaN;
  if (!m || hours > 23 || minutes > 59) {
    throw new RangeError(`Invalid time '${text}', expected HH:mm`);
  }
  return hours * 60 + minutes;
}

/** `true` when `localTime` (`HH:mm`) on `day` falls inside the window (handles midnight wrap). */
export function tariffWindowContains(window: TariffTimeWindow, day: Weekday, localTime: string): boolean {
  const from = parseClubTime(window.from);
  const to = parseClubTime(window.to);
  const t = parseClubTime(localTime);
  if (from === to) {
    return window.days.includes(day);
  }
  if (from < to) {
    return window.days.includes(day) && t >= from && t < to;
  }
  if (t >= from) {
    return window.days.includes(day);
  }
  const previous = WEEKDAYS[(WEEKDAYS.indexOf(day) + 6) % 7] as Weekday;
  return t < to && window.days.includes(previous);
}
// ---- END MANUAL ----

/** Billing tariff. */
export interface Tariff {
  /** Tariff id. */
  id: string;
  /** Display name. */
  name: string;
  /** Hourly price. */
  pricePerHour: Money;
  /** Minimum purchasable minutes. */
  minMinutes: number;
  /** Maximum purchasable minutes; null = unlimited. */
  maxMinutes?: number | null;
  /** Zones the tariff applies to; empty = all. */
  zones: string[];
  /** Validity windows; empty = always. */
  timeWindows: TariffTimeWindow[];
  /** Fixed package (`packageMinutes` for `packagePrice`) instead of hourly billing. */
  isPackage: boolean;
  /** Minutes included in the package; required when `isPackage`. */
  packageMinutes?: number | null;
  /** Package price; required when `isPackage`. */
  packagePrice?: Money | null;
}

// ---- BEGIN MANUAL ----
/** Price for `minutes` under a tariff: package price when `isPackage`, otherwise pro-rata per minute rounded up to the minor unit. */
export function tariffPriceFor(tariff: Tariff, minutes: number): Money {
  if (!Number.isSafeInteger(minutes) || minutes < 0) {
    throw new RangeError(`minutes must be a non-negative integer, got ${minutes}`);
  }
  if (tariff.isPackage && tariff.packagePrice) {
    return tariff.packagePrice;
  }
  const total = tariff.pricePerHour.amount * minutes;
  const rounded = Math.floor(total / 60) + (total % 60 === 0 ? 0 : 1);
  return money(rounded, tariff.pricePerHour.currency);
}

/** `true` when the tariff is valid for `zone` at the given local day/time (`HH:mm`). */
export function tariffIsValidFor(tariff: Tariff, zone: string, day: Weekday, localTime: string): boolean {
  if (tariff.zones.length > 0 && !tariff.zones.some((z) => z.toLowerCase() === zone.toLowerCase())) {
    return false;
  }
  return tariff.timeWindows.length === 0 || tariff.timeWindows.some((w) => tariffWindowContains(w, day, localTime));
}
// ---- END MANUAL ----

/** Kind of wallet transaction. */
export const TransactionType = {
  TopUp: 'topUp',
  Charge: 'charge',
  Refund: 'refund',
  Bonus: 'bonus',
  Purchase: 'purchase',
  Adjustment: 'adjustment',
} as const;
/** Kind of wallet transaction. */
export type TransactionType = (typeof TransactionType)[keyof typeof TransactionType];

/** Top-up payment provider. */
export const TopupProvider = {
  Payme: 'payme',
  Click: 'click',
  Uzum: 'uzum',
  Cash: 'cash',
} as const;
/** Top-up payment provider. */
export type TopupProvider = (typeof TopupProvider)[keyof typeof TopupProvider];

/** Lifecycle of a {@link TopupIntent}. */
export const TopupStatus = {
  Pending: 'pending',
  Paid: 'paid',
  Expired: 'expired',
  Cancelled: 'cancelled',
} as const;
/** Lifecycle of a {@link TopupIntent}. */
export type TopupStatus = (typeof TopupStatus)[keyof typeof TopupStatus];

/** Wallet ledger entry. */
export interface Transaction {
  /** Transaction id. */
  id: string;
  /** Wallet owner. */
  userId: string;
  /** Kind. */
  type: TransactionType;
  /** Signed amount: negative for `charge` and `purchase`. */
  amount: Money;
  /** Main balance after this entry. */
  balanceAfter: Money;
  /** Human-readable description (localized). */
  description: string;
  /** Creation time. */
  createdAt: string;
  /** External reference: order id, session id or payment id. */
  ref?: string | null;
}

/** Pending or settled top-up; payload of `wallet.topupIntent`. */
export interface TopupIntent {
  /** Intent id. */
  id: string;
  /** Payment provider. */
  provider: TopupProvider;
  /** Requested amount. */
  amount: Money;
  /** Current status. */
  status: TopupStatus;
  /** QR image URL to render, when the provider supports QR payment. */
  qrUrl?: string | null;
  /** Mobile app deep link, when available. */
  deepLink?: string | null;
  /** Web payment page, when available. */
  paymentUrl?: string | null;
  /** When the intent expires unpaid. */
  expiresAt: string;
  /** Creation time. */
  createdAt: string;
}

/** Body of `POST /wallet/{userId}/topup-intent`. */
export interface TopupIntentCreateRequest {
  /** Requested amount; at least 1 000 UZS (100 000 minor units). */
  amount: Money;
  /** Payment provider. */
  provider: TopupProvider;
  /** PC the request originates from. */
  pcId: string;
}

/** Minimum top-up amount in minor units (1 000 UZS). */
export const TOPUP_MIN_AMOUNT_MINOR = 100_000;
