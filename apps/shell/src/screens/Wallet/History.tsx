import { useEffect, useMemo } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { TransactionType, type Transaction, type TransactionType as TransactionTypeValue } from '@clubshell/contracts';
import { Button } from '@/components/ui/Button';
import { EmptyState } from '@/components/ui/EmptyState';
import { Skeleton } from '@/components/ui/Skeleton';
import { Tabs, type TabItem } from '@/components/ui/Tabs';
import { useLocale } from '@/hooks/useLocale';
import { formatMoney, formatMoneySigned, formatRelativeDay, formatTime } from '@/lib/format';
import { describeError } from '@/store/notifications';
import { selectHistoryHasMore, useWalletStore, type WalletHistoryRange } from '@/store/wallet';

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

export const HISTORY_RANGES: readonly WalletHistoryRange[] = ['all', 'today', 'week', 'month'];

const ADJUSTMENT_ICON = (
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="2"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <path d="M4 7h10M4 17h6M14 7h6M10 17h10M14 4v6M10 14v6" />
  </svg>
);

const ICONS: Record<TransactionTypeValue, JSX.Element> = {
  topUp: (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M12 19V5M5 12l7-7 7 7" />
    </svg>
  ),
  charge: (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <circle cx="12" cy="12" r="9" />
      <path d="M12 7v5l3 2" />
    </svg>
  ),
  refund: (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M9 14l-4-4 4-4M5 10h9a5 5 0 0 1 0 10h-3" />
    </svg>
  ),
  bonus: (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M12 3l2.7 5.6 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.9 1-6.1L3.2 9.5l6.1-.9L12 3z" />
    </svg>
  ),
  purchase: (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M6 8h12l1 13H5L6 8zM9 8V6a3 3 0 0 1 6 0v2" />
    </svg>
  ),
  adjustment: ADJUSTMENT_ICON,
  unknown: ADJUSTMENT_ICON,
};

const LEDGER_ICON = (
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="1.6"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <path d="M6 3h12a1 1 0 0 1 1 1v16l-3-2-2 2-2-2-2 2-2-2-3 2V4a1 1 0 0 1 1-1z" />
    <path d="M9 8h6M9 12h6" />
  </svg>
);

const TONE: Record<TransactionTypeValue, string> = {
  topUp: 'bg-success/15 text-success',
  charge: 'bg-primary/15 text-primary',
  refund: 'bg-accent/15 text-accent',
  bonus: 'bg-accent/15 text-accent',
  purchase: 'bg-text/10 text-text',
  adjustment: 'bg-muted/15 text-muted',
  unknown: 'bg-muted/15 text-muted',
};

/** Local calendar day of a timestamp, the history's group key. */
function dayKey(iso: string): string {
  const d = new Date(iso);
  return `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`;
}

/** Consecutive transactions of one local day (the list is newest first, so a day is one run). */
export function groupByDay(items: readonly Transaction[]): { key: string; items: Transaction[] }[] {
  const groups: { key: string; items: Transaction[] }[] = [];
  for (const tx of items) {
    const key = dayKey(tx.createdAt);
    const last = groups[groups.length - 1];
    if (last && last.key === key) {
      last.items.push(tx);
    } else {
      groups.push({ key, items: [tx] });
    }
  }
  return groups;
}

export interface TransactionRowProps {
  transaction: Transaction;
}

/** One ledger row: what (the server's description), the kind and the time, the signed amount and the balance after. */
export function TransactionRow({ transaction: tx }: TransactionRowProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const positive = tx.amount.amount > 0;
  return (
    <li className="flex items-center gap-4 border-b border-text/10 py-3 last:border-b-0">
      <span
        className={clsx(
          'inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-full [&>svg]:h-6 [&>svg]:w-6',
          TONE[tx.type],
        )}
        aria-hidden="true"
      >
        {ICONS[tx.type]}
      </span>
      <div className="min-w-0 flex-1">
        <p className="truncate text-base font-semibold text-text">
          {tx.description || t(`wallet.transaction.${tx.type}`)}
        </p>
        <p className="tnum text-sm text-muted">
          {t(`wallet.transaction.${tx.type}`)} · {formatTime(tx.createdAt, locale)}
        </p>
      </div>
      <div className="shrink-0 text-right">
        <p
          className={clsx(
            'tnum text-lg font-bold',
            positive ? 'text-success' : tx.amount.amount < 0 ? 'text-text' : 'text-muted',
          )}
        >
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

/**
 * The ledger: kind tabs and period chips (both server filters), days as sticky headings, "load more". Once every row of
 * the filter is loaded the header sums what came in and went out. Errors stay in the panel with a retry, never a toast.
 */
export function History({ className }: HistoryProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const history = useWalletStore((s) => s.history);
  const historyStatus = useWalletStore((s) => s.historyStatus);
  const historyPage = useWalletStore((s) => s.historyPage);
  const historyType = useWalletStore((s) => s.historyType);
  const historyRange = useWalletStore((s) => s.historyRange);
  const historyError = useWalletStore((s) => s.historyError);
  const hasMore = useWalletStore(selectHistoryHasMore);
  const loadHistory = useWalletStore((s) => s.loadHistory);

  // Every visit starts from the newest page of the filter the player left on.
  useEffect(() => {
    const s = useWalletStore.getState();
    void s.loadHistory(1, s.historyType);
  }, []);

  const typeTabs = useMemo<TabItem<TypeKey>[]>(
    () => [
      { key: 'all', label: t('wallet.allTypes') },
      ...HISTORY_TYPES.map((k) => ({ key: k, label: t(`wallet.transaction.${k}`) })),
    ],
    [t],
  );

  const groups = useMemo(() => groupByDay(history), [history]);

  const totals = useMemo(() => {
    if (hasMore || history.length === 0) {
      return null;
    }
    const currency = history[0]?.amount.currency ?? 'UZS';
    let income = 0;
    let spent = 0;
    for (const tx of history) {
      if (tx.amount.amount > 0) {
        income += tx.amount.amount;
      } else {
        spent -= tx.amount.amount;
      }
    }
    return { income: { amount: income, currency }, spent: { amount: spent, currency } };
  }, [history, hasMore]);

  const loading = historyStatus === 'loading';
  const failed = historyStatus === 'error';
  const initialLoading = loading && historyPage === 1 && history.length === 0;
  const filtered = historyType !== null || historyRange !== 'all';
  const retry = (): void => void loadHistory(failed && history.length > 0 ? historyPage : 1, historyType);

  return (
    <section aria-label={t('wallet.history')} className={clsx('glass flex min-h-0 flex-col rounded-xl', className)}>
      <header className="flex flex-col gap-3 px-5 pt-5">
        <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
          <h2 className="font-display text-2xl font-normal tracking-tight text-text">{t('wallet.history')}</h2>
          {totals && (
            <dl className="tnum flex items-baseline gap-4 text-sm">
              {totals.income.amount > 0 && (
                <div className="flex items-baseline gap-1.5">
                  <dt className="text-muted">{t('wallet.periodIn')}</dt>
                  <dd className="font-semibold text-success">{formatMoney(totals.income, locale)}</dd>
                </div>
              )}
              {totals.spent.amount > 0 && (
                <div className="flex items-baseline gap-1.5">
                  <dt className="text-muted">{t('wallet.periodOut')}</dt>
                  <dd className="font-semibold text-text">{formatMoney(totals.spent, locale)}</dd>
                </div>
              )}
            </dl>
          )}
        </div>
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
              aria-pressed={historyRange === r}
              onClick={() => void loadHistory(1, historyType, r)}
              className={clsx(
                'focus-ring h-9 rounded-full px-3 font-semibold transition-colors duration-[var(--dur-fast)]',
                historyRange === r ? 'bg-accent/20 text-accent' : 'text-muted hover:bg-text/10 hover:text-text',
              )}
            >
              {t(`wallet.range.${r}`)}
            </button>
          ))}
        </div>
      </header>

      <div
        id="wallet-history-panel"
        role="tabpanel"
        aria-busy={loading || undefined}
        className="themed-scrollbar mt-3 min-h-0 flex-1 overflow-y-auto px-5 pb-3"
      >
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
        ) : failed && history.length === 0 ? (
          <EmptyState
            icon={LEDGER_ICON}
            title={t('wallet.historyError')}
            hint={historyError ? describeError(historyError) : undefined}
            action={
              <Button variant="secondary" onClick={retry}>
                {t('common.retry')}
              </Button>
            }
            className="h-full min-h-[14rem]"
          />
        ) : history.length === 0 ? (
          <EmptyState
            icon={LEDGER_ICON}
            title={filtered ? t('wallet.noFilteredHistory') : t('wallet.noHistory')}
            hint={filtered ? undefined : t('wallet.noHistoryHint')}
            className="h-full min-h-[14rem]"
          />
        ) : (
          groups.map((group) => (
            <section key={group.key}>
              <h3 className="hud-label sticky top-0 z-[1] bg-surface py-2">
                {formatRelativeDay(group.items[0]?.createdAt ?? '', locale)}
              </h3>
              <ul role="list">
                {group.items.map((tx) => (
                  <TransactionRow key={tx.id} transaction={tx} />
                ))}
              </ul>
            </section>
          ))
        )}
      </div>

      {(hasMore || (failed && history.length > 0)) && !initialLoading && (
        <footer className="flex flex-col gap-2 border-t border-text/10 px-5 py-3">
          {failed && history.length > 0 && (
            <p className="text-center text-sm text-danger">
              {historyError ? describeError(historyError) : t('wallet.historyError')}
            </p>
          )}
          <Button
            variant="secondary"
            block
            loading={loading}
            onClick={() => (failed ? retry() : void loadHistory(historyPage + 1, historyType))}
          >
            {failed ? t('common.retry') : t('wallet.loadMore')}
          </Button>
        </footer>
      )}
    </section>
  );
}
