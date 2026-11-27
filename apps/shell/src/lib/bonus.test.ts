/** `pnpm --filter @clubshell/shell test` (Node's own runner, which strips the types itself). */
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { payForTime, timeFunds } from './bonus.ts';

const uzs = (amount: number) => ({ amount, currency: 'UZS' });

describe('payForTime (D-106: the bonus account pays play time first)', () => {
  it('takes the bonus first and the balance for the rest', () => {
    const pay = payForTime(uzs(1_000_000), uzs(500_000), uzs(600_000));
    assert.deepEqual(
      [pay.fromBonus.amount, pay.fromBalance.amount, pay.balanceAfter.amount, pay.bonusAfter.amount, pay.enough],
      [500_000, 100_000, 900_000, 0, true],
    );
  });

  it('leaves the balance alone when the bonus covers the price', () => {
    const pay = payForTime(uzs(0), uzs(800_000), uzs(600_000));
    assert.deepEqual(
      [pay.fromBonus.amount, pay.balanceAfter.amount, pay.bonusAfter.amount, pay.enough],
      [600_000, 0, 200_000, true],
    );
  });

  it('is not enough when the balance and the bonus together fall short; no bonus is none', () => {
    assert.equal(payForTime(uzs(100_000), uzs(400_000), uzs(600_000)).enough, false);
    assert.equal(payForTime(uzs(700_000), null, uzs(600_000)).fromBonus.amount, 0);
    assert.equal(timeFunds(uzs(100), uzs(50)).amount, 150);
    assert.equal(timeFunds(uzs(-300), undefined).amount, -300);
  });
});
