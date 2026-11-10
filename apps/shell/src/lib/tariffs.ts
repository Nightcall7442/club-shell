/** Tariff helpers shared by the lock screen's picker and the wallet. */
import { tariffIsValidFor, weekdayFromJsDay, type Tariff } from '@clubshell/contracts';
import { formatClock } from './time';

/** `true` when the tariff applies to `zone` (unknown zone = any) at `at` (club local time). */
export function tariffAvailableNow(tariff: Tariff, zone: string, at: Date): boolean {
  const z = zone || tariff.zones[0] || '';
  return tariffIsValidFor(tariff, z, weekdayFromJsDay(at.getDay()), formatClock(at, 'HH:mm'));
}
