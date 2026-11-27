/**
 * The bonus account (D-106): bonus money pays only for play time, and before the balance — a prepaid start, an extension,
 * a package, a postpaid bill. The bar and everything else take the balance only. The server decides; this is what the
 * screens show before asking it.
 */
import type { Money } from '@clubshell/contracts';

export interface TimePayment {
  /** What the bonus account pays of the price. */
  fromBonus: Money;
  /** What the balance pays of the price. */
  fromBalance: Money;
  /** The balance after the purchase (below zero — it cannot be bought). */
  balanceAfter: Money;
  /** The bonus balance after the purchase. */
  bonusAfter: Money;
  /** Whether balance + bonus cover the price. */
  enough: boolean;
}

/** How `price` of play time is paid from `balance` and `bonus` (null — none): the bonus first, then the balance. */
export function payForTime(balance: Money, bonus: Money | null | undefined, price: Money): TimePayment {
  const currency = price.currency;
  const available = Math.max(0, bonus?.amount ?? 0);
  const fromBonus = Math.min(available, Math.max(0, price.amount));
  const fromBalance = Math.max(0, price.amount) - fromBonus;
  return {
    fromBonus: { amount: fromBonus, currency },
    fromBalance: { amount: fromBalance, currency },
    balanceAfter: { amount: balance.amount - fromBalance, currency },
    bonusAfter: { amount: available - fromBonus, currency },
    enough: balance.amount - fromBalance >= 0,
  };
}

/** What play time can be paid with: the balance plus the bonus account. */
export function timeFunds(balance: Money, bonus: Money | null | undefined): Money {
  return { amount: balance.amount + Math.max(0, bonus?.amount ?? 0), currency: balance.currency };
}
