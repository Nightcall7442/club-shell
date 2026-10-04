import type { Money } from '@clubshell/contracts';
import { t } from './i18n';

const nf = new Intl.NumberFormat('ru-RU');

/** UZS in minor units → `45 000 сум` (the club has no coins, so minor digits are dropped). */
export function money(m: Money | null | undefined): string {
  if (!m) {
    return '—';
  }
  return t('{n} сум', { n: nf.format(Math.round(m.amount / 100)) });
}

/**
 * UZS to the tiyin: `45 000 сум`, or `45 000,50 сум` when the amount has a minor part. For amounts that must be taken
 * exactly (a debt, a guest's price): a rounded figure would be refused by the server.
 */
export function moneyExact(minor: number): string {
  return t('{n} сум', { n: exactDigits(minor) });
}

/** `4500050` → `45 000,50`; `4500000` → `45 000`. */
export function exactDigits(minor: number): string {
  const sign = minor < 0 ? '−' : '';
  const abs = Math.abs(minor);
  const whole = nf.format(Math.floor(abs / 100));
  const frac = abs % 100;
  return frac === 0 ? `${sign}${whole}` : `${sign}${whole},${String(frac).padStart(2, '0')}`;
}

/** Seconds → `1:26:05` (hours:minutes:seconds), `05:12` under an hour. */
export function duration(sec: number): string {
  if (sec < 0) {
    return '∞';
  }
  // Always with seconds: "1:27:40", never "1:27", which reads as 1 min 27 s next to "58:02".
  const h = Math.floor(sec / 3600);
  const m = Math.floor((sec % 3600) / 60);
  const mmss = `${String(m).padStart(2, '0')}:${String(sec % 60).padStart(2, '0')}`;
  return h > 0 ? `${h}:${mmss}` : mmss;
}

export function minutesLabel(min: number): string {
  if (min % 60 === 0) {
    return t('{n} ч', { n: min / 60 });
  }
  return t('{n} мин', { n: min });
}
