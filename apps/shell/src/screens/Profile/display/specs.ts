/**
 * Formatting for the "Мой компьютер" card and the refresh-rate picker: readable CPU names, binary units turned into
 * the ГБ / ТБ / ГГц / Гц players read on a box, and the rate worth recommending.
 */
import type { Locale } from '@clubshell/contracts';
import type { TFunction } from 'i18next';
import { formatNumber } from '@/lib/format';

/** `Intel(R) Core(TM) i5-10400F CPU @ 2.90GHz` → `Intel Core i5-10400F`; `AMD Ryzen 5 5600X 6-Core Processor` → `AMD Ryzen 5 5600X`. */
export function cpuName(model: string): string {
  return model
    .replace(/\((R|TM|C)\)/gi, '')
    .replace(/@.*$/, '')
    .replace(/\b\d+-Core\b/gi, '')
    .replace(/\b(CPU|Processor)\b/gi, '')
    .replace(/\s+/g, ' ')
    .trim();
}

/** Base clock from an Intel-style model string (`… @ 2.90GHz` → 2.9); `null` when the model does not say. */
export function cpuGhz(model: string): number | null {
  const m = /@\s*(\d+(?:\.\d+)?)\s*GHz/i.exec(model);
  const ghz = m ? Number(m[1]) : Number.NaN;
  return Number.isFinite(ghz) && ghz > 0 ? ghz : null;
}

export type SizeUnit = 'gb' | 'tb';

/** Size of `gib` GiB the way Windows shows it: whole ГБ below 1 ТБ, ТБ with one decimal above. */
export function sizeParts(gib: number): { value: number; unit: SizeUnit } {
  return gib >= 1024
    ? { value: Math.round((gib / 1024) * 10) / 10, unit: 'tb' }
    : { value: Math.round(gib), unit: 'gb' };
}

/** `12 288` MiB → `12 ГБ` (one decimal when it is not whole). */
export function formatMib(mib: number, locale: Locale, t: TFunction): string {
  return formatUnit('gb', Math.round((mib / 1024) * 10) / 10, locale, t);
}

/** `953.9` GiB → `954 ГБ`, `1863` GiB → `1,8 ТБ`. */
export function formatGib(gib: number, locale: Locale, t: TFunction): string {
  const { value, unit } = sizeParts(gib);
  return formatUnit(unit, value, locale, t);
}

/** A number with its unit word from `pcDisplay.units` (`240 Гц`, `2,9 ГГц`, `32 ГБ`). */
export function formatUnit(unit: SizeUnit | 'ghz' | 'hz', value: number, locale: Locale, t: TFunction): string {
  return t(`pcDisplay.units.${unit}`, { value: formatNumber(value, locale, { maximumFractionDigits: 1 }) });
}

/** The display's best rate when it is above the current one: what a player should be nudged to switch to. */
export function betterRate(hz: number, rates: readonly number[]): number | null {
  const best = rates.length > 0 ? Math.max(...rates) : 0;
  return best > hz ? best : null;
}
