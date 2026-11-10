/**
 * What a price on a tariff card or in a buy dialog shows: the server's quote (`session.quote`: day rate and the
 * player's discount applied) when it answered, else the list price marked approximate (offline, an older Agent). Pure
 * (type imports only) so the rules are unit-tested; `usePrice` feeds it.
 */
import type { Money, SessionQuote, Tariff } from '@clubshell/contracts';

/** Where a quote request stands. */
export type QuoteState =
  | { status: 'loading' }
  | { status: 'ok'; quote: SessionQuote }
  | { status: 'failed'; error?: unknown };

export interface PriceView {
  /** The amount to show (and to check the balance against). */
  amount: Money;
  /** The list price to strike through next to a discounted amount; `null` without a discount. */
  base: Money | null;
  /** Player discount in percent (0 = none). */
  discountPct: number;
  /** `group` | `loyalty` | `happyHour` (or a club's own name), when the server says which. */
  discountReason: string | null;
  /** The quote failed: `amount` is the list price, shown as approximate. */
  approximate: boolean;
  /** The quote has not answered yet: `amount` is the list price for now. */
  loading: boolean;
}

/** List price of `minutes` (`null` = one hour) on `tariff`; a package costs its price whatever the minutes. */
export function listPrice(tariff: Tariff, minutes: number | null): Money {
  if (tariff.isPackage && tariff.packagePrice) {
    return tariff.packagePrice;
  }
  const m = minutes ?? 60;
  const total = tariff.pricePerHour.amount * m;
  // As the contracts' `tariffPriceFor`: pro rata per minute, rounded up to the minor unit.
  return { amount: Math.floor(total / 60) + (total % 60 === 0 ? 0 : 1), currency: tariff.pricePerHour.currency };
}

export function priceView(state: QuoteState, list: Money): PriceView {
  if (state.status === 'ok') {
    const q = state.quote;
    const discounted = q.discountPct > 0 && q.base.amount > q.total.amount;
    return {
      amount: q.total,
      base: discounted ? q.base : null,
      discountPct: q.discountPct > 0 ? q.discountPct : 0,
      discountReason: q.discountPct > 0 ? (q.discountReason ?? null) : null,
      approximate: false,
      loading: false,
    };
  }
  return {
    amount: list,
    base: null,
    discountPct: 0,
    discountReason: null,
    approximate: state.status === 'failed',
    loading: state.status === 'loading',
  };
}
