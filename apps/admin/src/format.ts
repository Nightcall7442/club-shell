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
