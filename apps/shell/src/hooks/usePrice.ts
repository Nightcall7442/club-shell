/**
 * The price a tariff card or a buy dialog shows: the server's quote for this PC and player (day rate and discount
 * applied), the list price labelled approximate when the quote fails (offline). See `lib/pricing.ts`.
 */
import { useEffect, useMemo, useState } from 'react';
import type { Tariff } from '@clubshell/contracts';
import { listPrice, priceView, type PriceView, type QuoteState } from '@/lib/pricing';
import { fetchQuote } from '@/lib/quotes';

/** `session.quote` of `minutes` (`null` = one hour, or the package) on `tariffId`; idle while `tariffId` is null. */
export function useQuote(tariffId: string | null, minutes: number | null): QuoteState {
  const key = tariffId ? `${tariffId}|${minutes ?? ''}` : null;
  const [state, setState] = useState<{ key: string | null; value: QuoteState }>({
    key: null,
    value: { status: 'loading' },
  });
  useEffect(() => {
    if (!tariffId) {
      return undefined;
    }
    let alive = true;
    fetchQuote(tariffId, minutes).then(
      (quote) => alive && setState({ key, value: { status: 'ok', quote } }),
      (error: unknown) => alive && setState({ key, value: { status: 'failed', error } }),
    );
    return () => {
      alive = false;
    };
  }, [tariffId, minutes, key]);
  return state.key === key ? state.value : { status: 'loading' };
}

/** {@link PriceView} of `minutes` on `tariff`; `null` without a tariff. */
export function usePrice(tariff: Tariff | null, minutes: number | null): PriceView | null {
  const quote = useQuote(tariff?.id ?? null, tariff?.isPackage ? null : minutes);
  return useMemo(() => (tariff ? priceView(quote, listPrice(tariff, minutes)) : null), [tariff, minutes, quote]);
}
