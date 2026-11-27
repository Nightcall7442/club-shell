/** `pnpm --filter @clubshell/shell test`: wallet rows are named by their type, not by the server's Russian description. */
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { txView } from './txView.ts';

const row = (type: string, amount: number, description: string, bonus = false) => ({
  type,
  amount: { amount },
  description,
  bonus,
});

describe('txView', () => {
  it('names a desk top-up and how it was paid', () => {
    const v = txView(row('topUp', 5_000_000, 'Пополнение на кассе наличными'));
    assert.deepEqual(
      [v.title, v.method],
      [{ key: 'wallet.tx.topUp', vars: undefined }, { key: 'wallet.tx.method.cash' }],
    );
    assert.deepEqual(txView(row('topUp', 5_000_000, 'Пополнение через Payme')).method, { text: 'Payme' });
    assert.equal(
      (txView(row('topUp', -5_000_000, 'Отмена: пополнение на кассе картой')).title as { key: string }).key,
      'wallet.tx.topUpCancel',
    );
  });

  it('reads the minutes and the tariff of play time, and the bonus part', () => {
    const v = txView(row('charge', -1_200_000, 'Продление +60 мин · Standard · бонусы', true));
    assert.equal((v.title as { key: string }).key, 'wallet.tx.extend');
    assert.deepEqual(v.detail, [
      { key: 'wallet.tx.minutes', vars: { n: 60 } },
      { text: 'Standard' },
      { key: 'wallet.tx.fromBonus', vars: undefined },
    ]);
  });

  it('keeps what a description adds: the promo code, the goods, the reason', () => {
    assert.deepEqual(txView(row('bonus', 1_000_000, 'Промокод BONUS10', true)).detail, [
      { key: 'wallet.tx.promo', vars: { code: 'BONUS10' } },
    ]);
    assert.deepEqual(txView(row('purchase', -900_000, 'Покупка в баре: Coca-Cola ×2')).detail, [
      { text: 'Coca-Cola ×2' },
    ]);
    assert.deepEqual(txView(row('adjustment', 500_000, 'Списание долга: тестовый сеанс')).detail, [
      { text: 'тестовый сеанс' },
    ]);
  });
});
