/**
 * The small print of a quoted price: the player's discount (list price struck through, "Discount 10% · loyalty") or,
 * when the server could not be asked, that the amount is the approximate list price.
 */
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import type { Locale } from '@clubshell/contracts';
import { useLocale } from '@/hooks/useLocale';
import { formatMoney } from '@/lib/format';
import type { PriceView } from '@/lib/pricing';

/** `12 000 сум`, or `≈ 12 000 сум` for the approximate list price. */
export function formatPrice(view: PriceView, locale: Locale): string {
  const text = formatMoney(view.amount, locale);
  return view.approximate ? `≈ ${text}` : text;
}

export interface PriceNoteProps {
  view: PriceView | null;
  className?: string;
}

export function PriceNote({ view, className }: PriceNoteProps): JSX.Element | null {
  const { t, i18n } = useTranslation();
  const { locale } = useLocale();
  if (!view) {
    return null;
  }
  if (view.base && view.discountPct > 0) {
    const reasonKey = view.discountReason ? `wallet.discountReason.${view.discountReason}` : null;
    const reason = reasonKey && i18n.exists(reasonKey) ? t(reasonKey) : null;
    return (
      <span className={clsx('tnum text-sm', className)}>
        <s className="text-muted">{formatMoney(view.base, locale)}</s>{' '}
        <span className="font-semibold text-success">{t('wallet.discountPct', { pct: view.discountPct })}</span>
        {reason && <span className="text-muted"> · {reason}</span>}
      </span>
    );
  }
  if (view.approximate) {
    return (
      <span className={clsx('text-sm text-accent', className)} title={t('wallet.approxHint')}>
        {t('wallet.approx')} · {t('wallet.approxHint')}
      </span>
    );
  }
  return null;
}

export default PriceNote;
