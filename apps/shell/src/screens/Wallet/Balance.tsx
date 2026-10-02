import { forwardRef, useMemo } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { balanceTotal, type Money, type Tariff } from '@clubshell/contracts';
import { Badge } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { Skeleton } from '@/components/ui/Skeleton';
import { DotAmount } from '@/components/ui/DotAmount';
import { useLocale } from '@/hooks/useLocale';
import { useSession } from '@/hooks/useSession';
import { formatDurationSec, formatMoney, formatRelativeDay, formatTime } from '@/lib/format';
import { serverNow } from '@/lib/time';
import { useNotificationsStore } from '@/store/notifications';
import { selectFeature, useSettingsStore } from '@/store/settings';
import { useWalletStore } from '@/store/wallet';
import { tariffAvailableNow } from './Tariffs';

export interface BalanceProps {
  /** Opens the top-up flow (online payment, or the counter's steps when the club has none). */
  onTopUp: () => void;
  className?: string;
}

/**
 * The hourly tariff a balance is measured in: the running session's, else the cheapest one open to this PC now.
 * Packages have no hourly price, so they never count.
 */
export function playTariff(
  tariffs: readonly Tariff[],
  sessionTariffId: string | null,
  zone: string,
  at: Date,
): Tariff | null {
  const hourly = tariffs.filter((t) => !t.isPackage && t.pricePerHour.amount > 0);
  return (
    hourly.find((t) => t.id === sessionTariffId) ??
    hourly
      .filter((t) => tariffAvailableNow(t, zone, at))
      .sort((a, b) => a.pricePerHour.amount - b.pricePerHour.amount)[0] ??
    null
  );
}

/** Whole minutes `money` buys at the tariff's base hourly rate (discounts left out, hence "about"). */
export function playMinutes(money: Money, tariff: Tariff): number {
  return tariff.pricePerHour.amount > 0 ? Math.floor((Math.max(0, money.amount) * 60) / tariff.pricePerHour.amount) : 0;
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

const GamepadIcon = (): JSX.Element => (
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="1.8"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <path d="M7 8h10a4 4 0 0 1 3.9 4.9l-.8 3.5a2.3 2.3 0 0 1-4 .9L14.5 15h-5l-1.6 2.3a2.3 2.3 0 0 1-4-.9l-.8-3.5A4 4 0 0 1 7 8z" />
    <path d="M8 11v3M6.5 12.5h3M15.5 12h.01M17.5 13.5h.01" />
  </svg>
);

/**
 * Balance card: the main amount in the dot face, bonus and total when there is a bonus, the last update, how much play
 * it buys, and the Top up button — always there: without online payment it explains the counter instead.
 */
export const Balance = forwardRef<HTMLButtonElement, BalanceProps>(function Balance({ onTopUp, className }, ref) {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const balance = useWalletStore((s) => s.balance);
  const status = useWalletStore((s) => s.status);
  const tariffs = useWalletStore((s) => s.tariffs);
  const tariffZone = useWalletStore((s) => s.tariffZone);
  const pcZone = useSettingsStore((s) => s.pcInfo?.pc.zone ?? null);
  const onlineTopup = useSettingsStore(selectFeature('topup'));
  const offline = useNotificationsStore((s) => s.serverConnectivity !== 'online');
  const session = useSession();
  const loading = balance === null && status !== 'error';

  const play = useMemo(() => {
    if (!balance) {
      return null;
    }
    const tariff = playTariff(tariffs, session.tariffId, pcZone ?? tariffZone ?? '', serverNow());
    const minutes = tariff ? playMinutes(balanceTotal(balance), tariff) : 0;
    return tariff && minutes > 0 ? { tariff, minutes } : null;
  }, [balance, tariffs, session.tariffId, pcZone, tariffZone]);

  return (
    <section
      aria-label={t('wallet.balance')}
      className={clsx('glass relative shrink-0 overflow-hidden rounded-xl', className)}
    >
      <div aria-hidden="true" className="hud-grid pointer-events-none absolute inset-0" />
      <div
        aria-hidden="true"
        className="pointer-events-none absolute -right-24 -top-32 h-80 w-80 rounded-full bg-accent/[0.09] blur-3xl"
      />
      <div className="relative flex flex-wrap items-end justify-between gap-6 p-6">
        <div className="min-w-0 flex-1">
          <p className="hud-label flex items-center gap-2">
            <span className="inline-flex h-4 w-4 text-accent" aria-hidden="true">
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
              <p
                className={clsx(
                  'tnum mt-3 text-[clamp(2.6rem,4vw,4.5rem)] leading-none',
                  balance.amount.amount < 0 ? 'text-danger' : 'text-text',
                )}
              >
                <DotAmount value={formatMoney(balance.amount, locale)} />
              </p>
              {balance.bonus.amount > 0 && (
                <dl className="mt-4 flex flex-wrap items-baseline gap-x-8 gap-y-2 text-base">
                  <div className="flex items-baseline gap-2">
                    <dt className="text-muted">{t('wallet.bonus')}</dt>
                    <dd className="tnum font-semibold text-accent">{formatMoney(balance.bonus, locale)}</dd>
                  </div>
                  <div className="flex items-baseline gap-2">
                    <dt className="text-muted">{t('wallet.total')}</dt>
                    <dd className="tnum font-semibold text-text">{formatMoney(balanceTotal(balance), locale)}</dd>
                  </div>
                </dl>
              )}
              <p className="tnum mt-3 text-sm text-muted">
                {t('wallet.updatedAt', {
                  time: `${formatRelativeDay(balance.updatedAt, locale)}, ${formatTime(balance.updatedAt, locale)}`,
                })}
              </p>
              {play && (
                <p className="mt-4 inline-flex items-center gap-2 rounded-full bg-text/[0.05] py-1.5 pl-2.5 pr-4 text-sm text-text">
                  <span className="inline-flex h-5 w-5 text-accent" aria-hidden="true">
                    <GamepadIcon />
                  </span>
                  {t('wallet.enoughFor', {
                    time: formatDurationSec(play.minutes * 60, { compact: true }),
                    tariff: play.tariff.name,
                  })}
                </p>
              )}
              {offline && <p className="mt-2 text-sm text-accent">{t('wallet.cachedHint')}</p>}
            </>
          ) : (
            <p className="mt-3 text-lg text-danger">{t('errors.generic')}</p>
          )}
        </div>
        <div className="flex shrink-0 flex-col items-end gap-2">
          <Button ref={ref} variant="cta" size="xl" icon={<PlusIcon />} onClick={onTopUp}>
            {t('wallet.topUp')}
          </Button>
          {!onlineTopup && <p className="text-sm text-muted">{t('wallet.topUpAtDesk')}</p>}
        </div>
      </div>
    </section>
  );
});
