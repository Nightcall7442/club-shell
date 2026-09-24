import { forwardRef } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { balanceTotal } from '@clubshell/contracts';
import { Badge } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { Skeleton } from '@/components/ui/Skeleton';
import { DotAmount } from '@/components/ui/DotAmount';
import { useLocale } from '@/hooks/useLocale';
import { formatMoney, formatRelativeDay, formatTime } from '@/lib/format';
import { useNotificationsStore } from '@/store/notifications';
import { selectFeature, useSettingsStore } from '@/store/settings';
import { useWalletStore } from '@/store/wallet';

export interface BalanceProps {
  /** Opens the top-up flow. */
  onTopUp: () => void;
  className?: string;
}

const WalletIcon = (): JSX.Element => (
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="1.8"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <path d="M3 7a2 2 0 0 1 2-2h13v4H5a2 2 0 0 1-2-2zM3 7v10a2 2 0 0 0 2 2h15a1 1 0 0 0 1-1v-8a1 1 0 0 0-1-1H5" />
    <circle cx="16.5" cy="13.5" r="1.25" fill="currentColor" stroke="none" />
  </svg>
);

const PlusIcon = (): JSX.Element => (
  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" aria-hidden="true">
    <path d="M12 5v14M5 12h14" />
  </svg>
);

/** Balance card: main amount, bonus, total available, last update and the Top up button (feature `topup`). */
export const Balance = forwardRef<HTMLButtonElement, BalanceProps>(function Balance({ onTopUp, className }, ref) {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const balance = useWalletStore((s) => s.balance);
  const status = useWalletStore((s) => s.status);
  const topupEnabled = useSettingsStore(selectFeature('topup'));
  const offline = useNotificationsStore((s) => s.serverConnectivity !== 'online');
  const loading = balance === null && status !== 'error';

  return (
    <section
      aria-label={t('wallet.balance')}
      className={clsx('glass relative shrink-0 overflow-hidden rounded-xl p-6', className)}
    >
      <div className="relative flex flex-wrap items-start justify-between gap-6">
        <div className="min-w-0 flex-1">
          <p className="flex items-center gap-2 text-sm text-muted">
            <span className="inline-flex h-5 w-5" aria-hidden="true">
              <WalletIcon />
            </span>
            {t('wallet.balance')}
            {offline && (
              <Badge tone="accent" size="sm">
                {t('common.offline')}
              </Badge>
            )}
          </p>
          {loading ? (
            <div className="mt-3 flex flex-col gap-3">
              <Skeleton height="3.5rem" width="60%" />
              <Skeleton variant="text" width="40%" />
            </div>
          ) : balance ? (
            <>
              <p className="tnum mt-2 text-[clamp(2.4rem,3.6vw,4rem)] leading-none text-text">
                <DotAmount value={formatMoney(balance.amount, locale)} />
              </p>
              <dl className="mt-4 flex flex-wrap gap-x-8 gap-y-2 text-base">
                {balance.bonus.amount > 0 && (
                  <div className="flex items-baseline gap-2">
                    <dt className="text-muted">{t('wallet.bonus')}</dt>
                    <dd className="tnum font-semibold text-accent">{formatMoney(balance.bonus, locale)}</dd>
                  </div>
                )}
                <div className="flex items-baseline gap-2">
                  <dt className="text-muted">{t('wallet.total')}</dt>
                  <dd className="tnum font-semibold text-text">{formatMoney(balanceTotal(balance), locale)}</dd>
                </div>
                <div className="flex items-baseline gap-2 text-sm">
                  <dd className="tnum text-muted">
                    {t('wallet.updatedAt', {
                      time: `${formatRelativeDay(balance.updatedAt, locale)}, ${formatTime(balance.updatedAt, locale)}`,
                    })}
                  </dd>
                </div>
              </dl>
              {offline && <p className="mt-2 text-sm text-accent">{t('wallet.cachedHint')}</p>}
            </>
          ) : (
            <p className="mt-3 text-lg text-danger">{t('errors.generic')}</p>
          )}
        </div>
        {topupEnabled && (
          <Button ref={ref} variant="cta" size="lg" icon={<PlusIcon />} onClick={onTopUp} className="shrink-0">
            {t('wallet.topUp')}
          </Button>
        )}
      </div>
    </section>
  );
});
