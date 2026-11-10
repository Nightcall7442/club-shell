/** `pnpm --filter @clubshell/shell test` (Node's own runner, which strips the types itself). */
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import type { Tariff } from '@clubshell/contracts';
// The contracts' own validity check (zones + time windows), from source: the wallet module has no imports.
import { tariffIsValidFor, type Weekday } from '../../../../packages/contracts-ts/src/wallet.ts';
import { pickAutoStartTariff, type AutoStartInput } from './autoStart.ts';

const sum = (amount: number) => ({ amount: amount * 100, currency: 'UZS' });
const hourly = (id: string, price: number, extra: Partial<Tariff> = {}): Tariff => ({
  id,
  name: id,
  pricePerHour: sum(price),
  minMinutes: 30,
  maxMinutes: null,
  zones: [],
  timeWindows: [],
  isPackage: false,
  packageMinutes: null,
  packagePrice: null,
  ...extra,
});

const EVERY_DAY: Weekday[] = ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'];
const STANDARD = hourly('standard', 12_000, { zones: ['Standard'] });
const VIP = hourly('vip', 20_000, { zones: ['VIP'] });
/** Cheapest, but only from 22:00 to 06:00. */
const NIGHT = hourly('night', 6_000, { timeWindows: [{ days: EVERY_DAY, from: '22:00', to: '06:00' }] });
/** Cheapest of all, but a package: never pay as you go. */
const PACK = hourly('pack', 1_000, { isPackage: true, packageMinutes: 180, packagePrice: sum(30_000) });
/** Weekday mornings only. */
const MORNING = hourly('morning', 9_000, {
  timeWindows: [{ days: ['mon', 'tue', 'wed', 'thu', 'fri'], from: '08:00', to: '12:00' }],
});

/** A member with 50 000 UZS who has just signed in on a Standard-zone PC on `day` at `time`. */
function input(day: Weekday, time: string, patch: Partial<AutoStartInput> = {}): AutoStartInput {
  return {
    armed: true,
    isGuest: false,
    balance: sum(50_000).amount,
    tariffs: [VIP, NIGHT, STANDARD, PACK, MORNING],
    availableNow: (t) => tariffIsValidFor(t, 'Standard', day, time),
    ...patch,
  };
}

const pick = (i: AutoStartInput): string | null => pickAutoStartTariff(i)?.id ?? null;

describe('pickAutoStartTariff', () => {
  it('takes the cheapest hourly tariff open now in this zone', () => {
    assert.equal(pick(input('wed', '15:00')), 'standard');
    // Night rate after 22:00, and still after midnight (the window started the evening before).
    assert.equal(pick(input('wed', '23:30')), 'night');
    assert.equal(pick(input('thu', '05:59')), 'night');
    assert.equal(pick(input('thu', '06:00')), 'standard');
    // Weekday mornings only.
    assert.equal(pick(input('tue', '09:00')), 'morning');
    assert.equal(pick(input('sat', '09:00')), 'standard');
  });

  it('never picks a package or a tariff of another zone', () => {
    assert.equal(pick(input('wed', '15:00', { tariffs: [PACK, VIP] })), null);
  });

  it('starts only right after a fresh sign-in', () => {
    // Time up, a session the desk ended, a shell restart (auth.status), a sign-in that brought its session back.
    assert.equal(pick(input('wed', '15:00', { armed: false })), null);
  });

  it('needs money on the balance and a member', () => {
    assert.equal(pick(input('wed', '15:00', { balance: 0 })), null);
    assert.equal(pick(input('wed', '15:00', { balance: -500 })), null);
    assert.equal(pick(input('wed', '15:00', { balance: null })), null);
    assert.equal(pick(input('wed', '15:00', { isGuest: true })), null);
  });
});
