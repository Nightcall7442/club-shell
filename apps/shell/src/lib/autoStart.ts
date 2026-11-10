/**
 * The owner's auto-start (SHELL_CHANGES: a member with money plays at once, without the tariff picker): pay as you go on
 * the cheapest hourly tariff open now on this PC, charged per minute from the balance; the server stops the session when
 * the balance runs out. Pure (type imports only) so the conditions are unit-tested.
 *
 * It used to fire whenever the picker opened with money on the balance — also right after the prepaid time ran out or
 * the desk ended the session, turning an ended visit into an open-ended bill, and on a tariff closed at this hour.
 */
import type { Tariff } from '@clubshell/contracts';

export interface AutoStartInput {
  /** A fresh sign-in that brought no session, not used yet (`auth.autoStartArmed`). */
  armed: boolean;
  isGuest: boolean;
  /** Main balance in minor units; `null` while unknown. */
  balance: number | null;
  tariffs: readonly Tariff[];
  /** The tariff applies to this PC's zone and to one of its time windows right now (club clock). */
  availableNow: (tariff: Tariff) => boolean;
}

/** The tariff to start pay as you go on, or `null` when the picker must show instead. */
export function pickAutoStartTariff(input: AutoStartInput): Tariff | null {
  if (!input.armed || input.isGuest || input.balance === null || input.balance <= 0) {
    return null;
  }
  const open = input.tariffs.filter((t) => !t.isPackage && input.availableNow(t));
  return [...open].sort((a, b) => a.pricePerHour.amount - b.pricePerHour.amount)[0] ?? null;
}
