/** `pnpm --filter @clubshell/shell test` (Node's own runner, which strips the types itself). */
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import type { SessionQuote, Tariff } from '@clubshell/contracts';
import { listPrice, priceView } from './pricing.ts';

const sum = (n: number) => ({ amount: n * 100, currency: 'UZS' });
const STANDARD: Tariff = {
  id: 's',
  name: 'Standard',
  pricePerHour: sum(12_000),
  minMinutes: 30,
  maxMinutes: null,
  zones: [],
  timeWindows: [],
  isPackage: false,
  packageMinutes: null,
  packagePrice: null,
};
const PACK: Tariff = { ...STANDARD, id: 'p', isPackage: true, packageMinutes: 300, packagePrice: sum(40_000) };
const quote = (patch: Partial<SessionQuote>): SessionQuote => ({
  minutes: 60,
  base: sum(12_000),
  total: sum(12_000),
  dayPct: 100,
  discountPct: 0,
  package: false,
  ...patch,
});

describe('listPrice', () => {
  it('prices an hour by default, minutes pro rata, a package whole', () => {
    assert.deepEqual(listPrice(STANDARD, null), sum(12_000));
    assert.deepEqual(listPrice(STANDARD, 90), sum(18_000));
    assert.deepEqual(listPrice(PACK, 30), sum(40_000));
  });
});

describe('priceView', () => {
  it("shows the server's total, with the list price struck through when discounted", () => {
    const v = priceView(
      { status: 'ok', quote: quote({ total: sum(10_800), discountPct: 10, discountReason: 'loyalty' }) },
      sum(12_000),
    );
    assert.deepEqual(v.amount, sum(10_800));
    assert.deepEqual(v.base, sum(12_000));
    assert.equal(v.discountPct, 10);
    assert.equal(v.discountReason, 'loyalty');
    assert.equal(v.approximate, false);
  });

  it('shows a day rate without a discount as the plain price', () => {
    const v = priceView({ status: 'ok', quote: quote({ total: sum(14_400), dayPct: 120 }) }, sum(12_000));
    assert.deepEqual(v.amount, sum(14_400));
    assert.equal(v.base, null);
    assert.equal(v.discountPct, 0);
  });

  it('falls back to the list price, labelled approximate, when the quote fails', () => {
    const v = priceView({ status: 'failed' }, sum(12_000));
    assert.deepEqual(v.amount, sum(12_000));
    assert.equal(v.approximate, true);
    assert.equal(v.loading, false);
    const loading = priceView({ status: 'loading' }, sum(12_000));
    assert.equal(loading.loading, true);
    assert.equal(loading.approximate, false);
  });
});
