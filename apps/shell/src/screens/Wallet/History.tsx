import { useEffect, useMemo, useState } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { TransactionType, type Transaction, type TransactionType as TransactionTypeValue } from '@clubshell/contracts';
import { Button } from '@/components/ui/Button';
import { Skeleton } from '@/components/ui/Skeleton';
import { Tabs, type TabItem } from '@/components/ui/Tabs';
import { useLocale } from '@/hooks/useLocale';
import { formatMoney, formatMoneySigned, formatRelativeDay, formatTime } from '@/lib/format';
import { useNotificationsStore } from '@/store/notifications';
import { selectHistoryHasMore, useWalletStore } from '@/store/wallet';

export type HistoryRange = 'all' | 'today' | 'week' | 'month';
type TypeKey = TransactionTypeValue | 'all';

/** Transaction type tabs in display order. */
export const HISTORY_TYPES: readonly TransactionTypeValue[] = [
  TransactionType.TopUp,
  TransactionType.Charge,
  TransactionType.Purchase,
  TransactionType.Refund,
  TransactionType.Bonus,
  TransactionType.Adjustment,
];

export const HISTORY_RANGES: readonly HistoryRange[] = ['all', 'today', 'week', 'month'];

const DAY_MS = 86_400_000;

/** Lower bound (epoch ms) of a range, `null` for `all`. */
export function rangeStart(range: HistoryRange, now: number = Date.now()): number | null {
  switch (range) {
    case 'today': {
      const d = new Date(now);
      d.setHours(0, 0, 0, 0);
      return d.getTime();
    }
    case 'week':
      return now - 7 * DAY_MS;
    case 'month':
      return now - 30 * DAY_MS;
    default:
      return null;
  }
}

const ICONS: Record<TransactionTypeValue, JSX.Element> = {
  topUp: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M12 19V5M5 12l7-7 7 7" />
    </svg>
  ),
  charge: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <circle cx="12" cy="12" r="9" />
      <path d="M12 7v5l3 2" />
    </svg>
  ),
  refund: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M9 14l-4-4 4-4M5 10h9a5 5 0 0 1 0 10h-3" />
    </svg>
  ),
  bonus: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M12 3l2.7 5.6 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.9 1-6.1L3.2 9.5l6.1-.9L12 3z" />
    </svg>
  ),
  purchase: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M6 8h12l1 13H5L6 8zM9 8V6a3 3 0 0 1 6 0v2" />
    </svg>
  ),
  adjustment: (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M4 7h10M4 17h6M14 7h6M10 17h10M14 4v6M10 14v6" />
    </svg>
  ),
};

const TONE: Record<TransactionTypeValue, string> = {
  topUp: 'bg-success/15 text-success',
  charge: 'bg-primary/15 text-primary',
  refund: 'bg-accent/15 text-accent',
  bonus: 'bg-accent/15 text-accent',
  purchase: 'bg-text/10 text-text',
  adjustment: 'bg-muted/15 text-muted',
};

export interface TransactionRowProps {
  transaction: Transaction;
}

export function TransactionRow({ transaction: tx }: TransactionRowProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const positive = tx.amount.amount > 0;
  return (
    <li className="flex items-center gap-4 border-b border-text/10 py-3 last:border-b-0">
      <span className={clsx('inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-full [&>svg]:h-6 [&>svg]:w-6', TONE[tx.type])} aria-hidden="true">
        {ICONS[tx.type]}
      </span>
      <div className="min-w-0 flex-1">
        <p className="truncate text-base font-semibold text-text">{tx.description || t(`wallet.transaction.${tx.type}`)}</p>
        <p className="tnum text-sm text-muted">
          {t(`wallet.transaction.${tx.type}`)} · {formatRelativeDay(tx.createdAt, locale)}, {formatTime(tx.createdAt, locale)}
        </p>
      </div>
      <div className="shrink-0 text-right">
        <p className={clsx('tnum text-lg font-bold', positive ? 'text-success' : tx.amount.amount < 0 ? 'text-text' : 'text-muted')}>
          {formatMoneySigned(tx.amount, locale)}
        </p>
        <p className="tnum text-xs text-muted">
          {t('wallet.balanceAfter')}: {formatMoney(tx.balanceAfter, locale)}
        </p>
      </div>
    </li>
  );
}

export interface HistoryProps {
  className?: string;
}

/** Paged ledger with type tabs (server filter) and a period filter, plus "load more". */
export function History({ className }: HistoryProps): JSX.Element {
  const { t } = useTranslation();
  const history = useWalletStore((s) => s.history);
  const historyStatus = useWalletStore((s) => s.historyStatus);
  const historyPage = useWalletStore((s) => s.historyPage);
  const historyType = useWalletStore((s) => s.historyType);
  const error = useWalletStore((s) => s.error);
  const hasMore = useWalletStore(selectHistoryHasMore);
  const loadHistory = useWalletStore((s) => s.loadHistory);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [range, setRange] = useState<HistoryRange>('all');

  useEffect(() => {
    if (historyStatus === 'idle') {
      void loadHistory(1, null);
    }
  }, [historyStatus, loadHistory]);

  useEffect(() => {
    if (historyStatus === 'error' && error) {
      pushError(error, t('wallet.history'));
    }
  }, [historyStatus, error, pushError, t]);

  const typeTabs = useMemo<TabItem<TypeKey>[]>(
    () => [{ key: 'all', label: t('wallet.allTypes') }, ...HISTORY_TYPES.map((k) => ({ key: k, label: t(`wallet.transaction.${k}`) }))],
    [t],
  );

  // ponytail: period is a client-side filter on the loaded pages; the store's loadHistory has no from/to. Move it
  // server-side (WalletHistoryRequest.from/to) when ledgers grow past a few pages.
  const visible = useMemo(() => {
    const start = rangeStart(range);
    return start === null ? history : history.filter((tx) => Date.parse(tx.createdAt) >= start);
  }, [history, range]);

  const loading = historyStatus === 'loading';
  const initialLoading = loading && historyPage === 1 && history.length === 0;

  return (
    <section aria-label={t('wallet.history')} className={clsx('glass flex min-h-0 flex-col rounded-xl', className)}>
      <header className="flex flex-col gap-3 px-5 pt-5">
        <h2 className="text-2xl font-bold text-text">{t('wallet.history')}</h2>
        <Tabs<TypeKey>
          items={typeTabs}
          value={historyType ?? 'all'}
          onChange={(key) => void loadHistory(1, key === 'all' ? null : key)}
          label={t('wallet.filter')}
          idPrefix="wallet-history"
          className="flex-wrap rounded-[1.75rem]"
        />
        <div role="group" aria-label={t('wallet.period')} className="flex flex-wrap items-center gap-2 text-sm">
          <span className="text-muted">{t('wallet.period')}:</span>
          {HISTORY_RANGES.map((r) => (
            <button
              key={r}
              type="button"
              data-nav="true"
              aria-pressed={range === r}
              onClick={() => setRange(r)}
              className={clsx(
                'focus-ring h-9 rounded-full px-3 font-semibold transition-colors duration-[var(--dur-fast)]',
                range === r ? 'bg-accent/20 text-accent' : 'text-muted hover:bg-text/10 hover:text-text',
              )}
            >
              {t(`wallet.range.${r}`)}
            </button>
          ))}
        </div>
      </header>

      <div id="wallet-history-panel" role="tabpanel" className="themed-scrollbar min-h-0 flex-1 overflow-y-auto px-5 py-3">
        {initialLoading ? (
          <div className="flex flex-col gap-3 py-2">
            {Array.from({ length: 6 }, (_, i) => (
              <div key={i} className="flex items-center gap-4">
                <Skeleton variant="circle" width="2.75rem" height="2.75rem" />
                <Skeleton variant="text" lines={2} className="flex-1" />
                <Skeleton width="6rem" height="1.5rem" />
              </div>
            ))}
          </div>
        ) : visible.length === 0 ? (
          <div className="flex h-full min-h-[12rem] flex-col items-center justify-center gap-2 text-center">
            <p className="text-lg font-semibold text-text">{history.length === 0 ? t('wallet.noHistory') : t('common.noResults')}</p>
            {historyStatus === 'error' && (
              <Button variant="secondary" onClick={() => void loadHistory(1, historyType)}>
                {t('common.retry')}
              </Button>
            )}
          </div>
        ) : (
          <ul role="list">
            {visible.map((tx) => (
              <TransactionRow key={tx.id} transaction={tx} />
            ))}
          </ul>
        )}
      </div>

      {hasMore && !initialLoading && (
        <footer className="border-t border-text/10 px-5 py-3">
          <Button variant="secondary" block loading={loading} onClick={() => void loadHistory(historyPage + 1, historyType)}>
            {t('wallet.loadMore')}
          </Button>
        </footer>
      )}
    </section>
  );
}
