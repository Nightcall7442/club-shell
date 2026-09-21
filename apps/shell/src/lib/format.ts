/**
 * Display formatting: money (UZS, integer minor units ÷ 100, 0 fraction digits by default), numbers,
 * percentages, bytes and durations. Locale-aware via `Intl`; unit words come from the i18n bundle.
 */
import type { Locale, Money } from '@clubshell/contracts';
import i18n, { LOCALE_TAGS, toLocale } from '@/i18n';

/** Display label for currencies where the ISO code is not what people say. */
const CURRENCY_LABELS: Readonly<Record<string, Readonly<Record<Locale, string>>>> = {
  UZS: { en: 'UZS', ru: 'сум', uz: "so'm" },
};

function tag(locale: Locale | string): string {
  return LOCALE_TAGS[toLocale(locale)] ?? 'en-US';
}

/** Formats a number with locale grouping (`45 000` / `45,000`). */
export function formatNumber(value: number, locale: Locale, options?: Intl.NumberFormatOptions): string {
  return new Intl.NumberFormat(tag(locale), { maximumFractionDigits: 0, ...options }).format(value);
}

/**
 * Formats a Money value: `45 000 сум` / `45 000 so'm` / `UZS 45,000`. Minor units are converted to major
 * units (÷100); `minorDigits` defaults to 0 (`shell.json → ui.currencyFormat`). Negative amounts keep the sign.
 */
export function formatMoney(m: Money, locale: Locale, minorDigits = 0): string {
  const major = m.amount / 100;
  const label = CURRENCY_LABELS[m.currency]?.[locale];
  if (label !== undefined) {
    const num = formatNumber(major, locale, { minimumFractionDigits: minorDigits, maximumFractionDigits: minorDigits });
    return locale === 'en' ? `${label} ${num}` : `${num} ${label}`;
  }
  try {
    return new Intl.NumberFormat(tag(locale), {
      style: 'currency',
      currency: m.currency,
      minimumFractionDigits: minorDigits,
      maximumFractionDigits: minorDigits,
    }).format(major);
  } catch {
    return `${major.toFixed(minorDigits)} ${m.currency}`;
  }
}

/** Signed money for ledgers: `+45 000 сум` / `−12 000 сум`. */
export function formatMoneySigned(m: Money, locale: Locale, minorDigits = 0): string {
  const abs = formatMoney({ amount: Math.abs(m.amount), currency: m.currency }, locale, minorDigits);
  return m.amount < 0 ? `−${abs}` : `+${abs}`;
}

/** Formats a 0–100 value as a percentage (`73%`). */
export function formatPercent(value: number, locale: Locale, fractionDigits = 0): string {
  return new Intl.NumberFormat(tag(locale), {
    style: 'percent',
    minimumFractionDigits: fractionDigits,
    maximumFractionDigits: fractionDigits,
  }).format(value / 100);
}

const BYTE_UNITS = ['B', 'KB', 'MB', 'GB', 'TB'] as const;

/** Formats a byte count (`1.5 GB`). */
export function formatBytes(bytes: number, locale: Locale, fractionDigits = 1): string {
  if (!Number.isFinite(bytes) || bytes < 0) {
    return '—';
  }
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < BYTE_UNITS.length - 1) {
    value /= 1024;
    unit += 1;
  }
  const digits = unit === 0 ? 0 : fractionDigits;
  return `${formatNumber(value, locale, { minimumFractionDigits: digits, maximumFractionDigits: digits })} ${BYTE_UNITS[unit]}`;
}

/** Formats gigabytes with one decimal (`sizeGb` fields). */
export function formatGb(gb: number, locale: Locale): string {
  return `${formatNumber(gb, locale, { maximumFractionDigits: 1 })} GB`;
}

/** Formats megabit/s throughput (`12.5 Mbps`). */
export function formatMbps(mbps: number, locale: Locale): string {
  return `${formatNumber(mbps, locale, { maximumFractionDigits: 1 })} Mbps`;
}

/** Translates `format.<key>` with a count (i18next plural forms `_one/_few/_many/_other`). */
export function pluralize(
  key: 'minutes' | 'hours' | 'seconds' | 'days' | 'items' | 'players' | 'games' | 'sessions' | 'points' | 'messages',
  count: number,
): string {
  return i18n.t(`format.${key}`, { count });
}

export interface DurationOptions {
  /** `1h 27m` instead of `1 hour 27 minutes`. */
  compact?: boolean;
  /** Include seconds (default: only when under one hour in compact mode, never otherwise). */
  seconds?: boolean;
}

/**
 * Formats a duration in seconds: `1 hour 27 minutes` (plural-aware) or compact `1h 27m` / `4m 12s`.
 * Negative or non-finite input renders as zero.
 */
export function formatDurationSec(sec: number, opts: DurationOptions = {}): string {
  const total = Number.isFinite(sec) ? Math.max(0, Math.floor(sec)) : 0;
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const withSeconds = opts.seconds ?? (opts.compact === true && h === 0);
  if (opts.compact) {
    if (h > 0) {
      return withSeconds ? i18n.t('format.durationHms', { h, m, s }) : i18n.t('format.durationHm', { h, m });
    }
    if (m > 0 || !withSeconds) {
      return withSeconds ? i18n.t('format.durationMs', { m, s }) : i18n.t('format.durationM', { m });
    }
    return i18n.t('format.durationS', { s });
  }
  const parts: string[] = [];
  if (h > 0) {
    parts.push(pluralize('hours', h));
  }
  if (m > 0 || (h === 0 && !withSeconds)) {
    parts.push(pluralize('minutes', m));
  }
  if (withSeconds && (s > 0 || parts.length === 0)) {
    parts.push(pluralize('seconds', s));
  }
  return parts.join(' ');
}

/** Formats an ISO timestamp as a localized date (`21 Sep 2026`). */
export function formatDate(iso: string, locale: Locale, options?: Intl.DateTimeFormatOptions): string {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) {
    return '—';
  }
  return new Intl.DateTimeFormat(tag(locale), options ?? { day: 'numeric', month: 'short', year: 'numeric' }).format(t);
}

/** Formats an ISO timestamp as a localized time (`14:05`). */
export function formatTime(iso: string, locale: Locale): string {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) {
    return '—';
  }
  return new Intl.DateTimeFormat(tag(locale), { hour: '2-digit', minute: '2-digit', hour12: false }).format(t);
}

/** Formats an ISO timestamp as date + time (`21 Sep, 14:05`). */
export function formatDateTime(iso: string, locale: Locale): string {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) {
    return '—';
  }
  return new Intl.DateTimeFormat(tag(locale), {
    day: 'numeric',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(t);
}

/** Relative day label: `Today` / `Yesterday` / `Tomorrow` / localized date. */
export function formatRelativeDay(iso: string, locale: Locale): string {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) {
    return '—';
  }
  const startOf = (d: Date): number => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  const diffDays = Math.round((startOf(new Date(t)) - startOf(new Date())) / 86_400_000);
  if (diffDays === 0) {
    return i18n.t('common.today');
  }
  if (diffDays === -1) {
    return i18n.t('common.yesterday');
  }
  if (diffDays === 1) {
    return i18n.t('common.tomorrow');
  }
  return formatDate(iso, locale, { day: 'numeric', month: 'short' });
}

/** Initials for avatar fallbacks (`Bobur Yusupov` → `BY`). */
export function initials(name: string): string {
  const parts = name
    .trim()
    .split(/\s+/)
    .filter((p) => p.length > 0);
  if (parts.length === 0) {
    return '?';
  }
  const first = parts[0]?.charAt(0) ?? '';
  const last = parts.length > 1 ? (parts[parts.length - 1]?.charAt(0) ?? '') : '';
  return (first + last).toUpperCase();
}

/** Truncates with an ellipsis. */
export function truncate(text: string, max: number): string {
  return text.length <= max ? text : `${text.slice(0, Math.max(0, max - 1)).trimEnd()}…`;
}
