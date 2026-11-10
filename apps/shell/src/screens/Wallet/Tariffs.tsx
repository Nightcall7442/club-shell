import { useEffect, useMemo, useState } from 'react';
import clsx from 'clsx';
import { motion } from 'framer-motion';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { WEEKDAYS, type Tariff, type TariffTimeWindow } from '@clubshell/contracts';
import { Badge } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { formatPrice, PriceNote } from '@/components/ui/PriceNote';
import { Skeleton } from '@/components/ui/Skeleton';
import { DotAmount } from '@/components/ui/DotAmount';
import { useLocale } from '@/hooks/useLocale';
import { usePrice } from '@/hooks/usePrice';
import { useSession } from '@/hooks/useSession';
import { formatMoney } from '@/lib/format';
import { isShellApiError } from '@/lib/tauri';
import { tariffAvailableNow } from '@/lib/tariffs';
import { serverNow } from '@/lib/time';
import { describeError, useNotificationsStore } from '@/store/notifications';
import { useSettingsStore } from '@/store/settings';
import { selectAnimationsEnabled, useThemeStore } from '@/store/theme';
import { selectBalanceAmount, useWalletStore } from '@/store/wallet';
import { ModalBackHandler } from './TopUpModal';

export type TariffAction = 'start' | 'extend';

/** Minute presets offered for hourly tariffs (filtered by `minMinutes`/`maxMinutes`). */
export const MINUTE_PRESETS: readonly number[] = [30, 60, 90, 120, 180, 240, 300, 480];

export { tariffAvailableNow };

/** Minutes the user may buy under a tariff (package → its fixed length). */
export function tariffMinuteOptions(tariff: Tariff): number[] {
  if (tariff.isPackage && tariff.packageMinutes) {
    return [tariff.packageMinutes];
  }
  const max = tariff.maxMinutes ?? Number.POSITIVE_INFINITY;
  const opts = MINUTE_PRESETS.filter((m) => m >= tariff.minMinutes && m <= max);
  return opts.length > 0 ? opts : [tariff.minMinutes];
}

/** Human form of a time window: `Valid Mon–Fri 22:00–06:00` / `Valid every day …`. */
export function describeTimeWindow(w: TariffTimeWindow, t: TFunction): string {
  const days = WEEKDAYS.filter((d) => w.days.includes(d));
  const first = days[0];
  const last = days[days.length - 1];
  const contiguous =
    first !== undefined && last !== undefined && WEEKDAYS.indexOf(last) - WEEKDAYS.indexOf(first) === days.length - 1;
  let label: string;
  if (days.length >= 7) {
    label = t('wallet.everyDay');
  } else if (days.length > 2 && contiguous && first && last) {
    label = `${t(`wallet.weekday.${first}`)}–${t(`wallet.weekday.${last}`)}`;
  } else {
    label = days.map((d) => t(`wallet.weekday.${d}`)).join(', ');
  }
  return t('wallet.timeWindow', { days: label, from: w.from, to: w.to });
}

const ClockIcon = (): JSX.Element => (
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
);

// ---------------------------------------------------------------------------------------------------------------------
// Card
// ---------------------------------------------------------------------------------------------------------------------

export interface TariffCardProps {
  tariff: Tariff;
  available: boolean;
  current: boolean;
  action: TariffAction | null;
  onAction: (tariff: Tariff, action: TariffAction) => void;
}

export function TariffCard({ tariff, available, current, action, onAction }: TariffCardProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const pkg = tariff.isPackage && tariff.packagePrice && tariff.packageMinutes ? tariff.packagePrice : null;
  // The server's price for this player now (an hour, or the package): day rate and discount applied.
  const price = usePrice(tariff, null);

  return (
    <article
      aria-label={tariff.name}
      className={clsx(
        'glass relative flex w-full flex-col gap-3 rounded-lg p-5 transition-[opacity,box-shadow] duration-[var(--dur-fast)] focus-within:border-glow',
        current && 'border-glow',
        !available && 'opacity-60',
      )}
    >
      <header className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h3 className="truncate text-xl font-bold text-text">{tariff.name}</h3>
          {/* The hourly rate is the big number below; only a package needs a line here, for its minutes. */}
          {pkg && price && (
            <p className="tnum text-base text-muted">
              {t('wallet.packageMinutes', { minutes: tariff.packageMinutes, price: formatPrice(price, locale) })}
            </p>
          )}
        </div>
        <div className="flex shrink-0 flex-col items-end gap-1">
          {current && (
            <Badge tone="primary" solid size="sm">
              {t('wallet.currentTariff')}
            </Badge>
          )}
          {pkg ? (
            <Badge tone="accent" size="sm">
              {t('wallet.package')}
            </Badge>
          ) : (
            <Badge tone={available ? 'success' : 'muted'} size="sm" dot>
              {available ? t('wallet.availableNow') : t('wallet.notAvailableNow')}
            </Badge>
          )}
        </div>
      </header>

      <p
        className={clsx(
          'tnum text-[clamp(1.6rem,2.2vw,2.4rem)] leading-none text-text',
          price?.loading && 'opacity-60',
        )}
      >
        <DotAmount value={price ? formatPrice(price, locale) : formatMoney(pkg ?? tariff.pricePerHour, locale)} />
        {!pkg && <span className="ml-1 text-base font-semibold text-muted">/ {t('common.hourShort')}</span>}
      </p>
      <PriceNote view={price} className="-mt-1" />

      <ul className="flex flex-col gap-1 text-sm text-muted">
        {tariff.timeWindows.length === 0 ? (
          <li className="flex items-center gap-2">
            <span className="inline-flex h-4 w-4 shrink-0" aria-hidden="true">
              <ClockIcon />
            </span>
            {t('wallet.alwaysValid')}
          </li>
        ) : (
          tariff.timeWindows.map((w, i) => (
            <li key={i} className="flex items-center gap-2">
              <span className="inline-flex h-4 w-4 shrink-0" aria-hidden="true">
                <ClockIcon />
              </span>
              {describeTimeWindow(w, t)}
            </li>
          ))
        )}
        {!pkg && (
          <li className="tnum">
            {t('wallet.minMinutes', { minutes: tariff.minMinutes })}
            {tariff.maxMinutes != null && ` · ${t('wallet.maxMinutes', { minutes: tariff.maxMinutes })}`}
          </li>
        )}
        {tariff.zones.length > 0 && <li>{t('wallet.zoneOnly', { zones: tariff.zones.join(', ') })}</li>}
      </ul>

      {action && (
        <Button
          size="lg"
          variant={action === 'start' ? 'primary' : 'secondary'}
          block
          disabled={!available}
          onClick={() => onAction(tariff, action)}
          className="mt-auto"
        >
          {action === 'start' ? t('wallet.useTariff') : t('wallet.extendWith')}
        </Button>
      )}
    </article>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Start / extend dialog
// ---------------------------------------------------------------------------------------------------------------------

export interface TariffActionModalProps {
  tariff: Tariff | null;
  action: TariffAction;
  open: boolean;
  onClose: () => void;
  onInsufficientFunds?: () => void;
}

/** A minute preset with its quoted price. */
function MinuteChip({
  tariff,
  minutes,
  active,
  onSelect,
}: {
  tariff: Tariff;
  minutes: number;
  active: boolean;
  onSelect: () => void;
}): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const price = usePrice(tariff, minutes);
  return (
    <button
      type="button"
      role="radio"
      aria-checked={active}
      data-nav="true"
      onClick={onSelect}
      className={clsx(
        'focus-ring tnum flex h-16 flex-col items-center justify-center rounded-lg text-base font-bold transition-colors duration-[var(--dur-fast)]',
        active ? 'choice choice-on' : 'choice',
      )}
    >
      <span>
        {minutes} {t('common.min')}
      </span>
      {price && (
        <span className={clsx('text-xs font-medium', active ? 'text-on-primary/70' : 'text-muted')}>
          {formatPrice(price, locale)}
        </span>
      )}
    </button>
  );
}

/**
 * Buying time with a tariff: the player pays in advance from the balance, at the server's price (`session.quote`).
 * Pay as you go is the cash desk's (and the owner's auto-start after a sign-in), never a choice here.
 */
export function TariffActionModal({
  tariff,
  action,
  open,
  onClose,
  onInsufficientFunds,
}: TariffActionModalProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const session = useSession();
  const balance = useWalletStore(selectBalanceAmount);
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const options = useMemo(() => (tariff ? tariffMinuteOptions(tariff) : []), [tariff]);
  const [minutes, setMinutes] = useState<number>(options[0] ?? 60);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (open) {
      setMinutes(options.includes(60) ? 60 : (options[0] ?? 60));
      setBusy(false);
    }
  }, [open, options]);

  const cost = usePrice(open ? tariff : null, minutes);
  const notEnough = cost !== null && cost.amount.amount > balance.amount;

  const submit = async (): Promise<void> => {
    if (!tariff) {
      return;
    }
    setBusy(true);
    try {
      if (action === 'start') {
        await session.start(tariff.id, true, minutes);
        push({ title: t('session.started'), body: tariff.name, level: 'success' });
      } else {
        await session.extend(minutes, tariff.id);
        push({ title: t('notifications.sessionExtended', { minutes }), body: tariff.name, level: 'success' });
      }
      onClose();
    } catch (e) {
      if (isShellApiError(e) && e.code === 'insufficientFunds') {
        push({ title: t('wallet.notEnough'), body: describeError(e), level: 'error' });
        onInsufficientFunds?.();
        onClose();
      } else {
        pushError(e, action === 'start' ? t('wallet.startSession') : t('session.extend'));
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal
      open={open && tariff !== null}
      onClose={onClose}
      title={action === 'start' ? t('wallet.startSession') : t('session.extendTitle')}
      description={tariff?.name}
      size="md"
      footer={
        <>
          <Button variant="secondary" size="lg" onClick={onClose} disabled={busy}>
            {t('common.cancel')}
          </Button>
          <Button size="lg" loading={busy} onClick={() => void submit()}>
            {action === 'start'
              ? busy
                ? t('wallet.starting')
                : t('wallet.startSession')
              : busy
                ? t('session.extending')
                : t('session.extend')}
          </Button>
        </>
      }
    >
      <ModalBackHandler onBack={onClose} />
      {tariff && (
        <div className="flex flex-col gap-5">
          {action === 'start' && <p className="text-sm text-muted">{t('session.prepaidHint')}</p>}
          <fieldset className="flex flex-col gap-2">
            <legend className="mb-2 text-sm font-medium text-muted">{t('wallet.chooseMinutes')}</legend>
            <div role="radiogroup" aria-label={t('wallet.chooseMinutes')} className="grid grid-cols-4 gap-2">
              {options.map((m) => (
                <MinuteChip key={m} tariff={tariff} minutes={m} active={minutes === m} onSelect={() => setMinutes(m)} />
              ))}
            </div>
          </fieldset>

          <dl className="flex flex-col gap-1 rounded-lg bg-text/5 px-4 py-3 text-base">
            <div className="flex items-center justify-between">
              <dt className="text-muted">{t('wallet.estimatedCost')}</dt>
              <dd className="tnum text-xl font-semibold text-text">{cost ? formatPrice(cost, locale) : '—'}</dd>
            </div>
            {cost && <PriceNote view={cost} className="self-end" />}
            {cost && (
              <div className="flex items-center justify-between text-sm">
                <dt className="text-muted">{t('wallet.balanceAfter')}</dt>
                <dd className={clsx('tnum font-semibold', notEnough ? 'text-danger' : 'text-muted')}>
                  {formatMoney({ amount: balance.amount - cost.amount.amount, currency: cost.amount.currency }, locale)}
                </dd>
              </div>
            )}
          </dl>
          {notEnough && <p className="text-sm text-danger">{t('wallet.notEnough')}</p>}
        </div>
      )}
    </Modal>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// List
// ---------------------------------------------------------------------------------------------------------------------

export interface TariffsProps {
  onInsufficientFunds?: () => void;
  className?: string;
}

/** Tariff cards with live availability and the start / extend action for the current session. */
export function Tariffs({ onInsufficientFunds, className }: TariffsProps): JSX.Element {
  const { t } = useTranslation();
  const animations = useThemeStore(selectAnimationsEnabled);
  const tariffs = useWalletStore((s) => s.tariffs);
  const tariffZone = useWalletStore((s) => s.tariffZone);
  const status = useWalletStore((s) => s.status);
  const pcZone = useSettingsStore((s) => s.pcInfo?.pc.zone ?? null);
  const session = useSession();
  const [dialog, setDialog] = useState<{ tariff: Tariff; action: TariffAction } | null>(null);
  const [now, setNow] = useState(() => serverNow());

  // Availability follows the club clock; a minute resolution is plenty for time windows.
  useEffect(() => {
    const timer = setInterval(() => setNow(serverNow()), 60_000);
    return () => clearInterval(timer);
  }, []);

  const zone = pcZone ?? tariffZone ?? '';
  // A guest's time is opened and paid at the desk; pay as you go needs no extension.
  const action: TariffAction | null = !session.isOpen
    ? session.isGuest
      ? null
      : 'start'
    : session.isLocked || session.isOpenEnded
      ? null
      : 'extend';

  // One grid (the card names its zones): the current tariff, then what is open now, this PC's zone before the rest,
  // hourly before packages, cheaper first. Zone groups left a heading over a single card each.
  const sorted = useMemo(() => {
    const own = (x: Tariff): boolean => x.zones.some((z) => z.toLowerCase() === zone.toLowerCase());
    const rank = (x: Tariff): number =>
      (session.tariffId === x.id ? 0 : 8) +
      (tariffAvailableNow(x, zone, now) ? 0 : 4) +
      (own(x) ? 0 : 2) +
      (x.isPackage ? 1 : 0);
    const price = (x: Tariff): number => (x.isPackage ? (x.packagePrice?.amount ?? 0) : x.pricePerHour.amount);
    return [...tariffs].sort((a, b) => rank(a) - rank(b) || price(a) - price(b));
  }, [tariffs, zone, now, session.tariffId]);

  const loading = status === 'loading' && tariffs.length === 0;

  return (
    <section aria-label={t('wallet.tariffs')} className={clsx('flex flex-col gap-4', className)}>
      <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
        <h2 className="font-display text-2xl font-normal text-text tracking-tight">{t('wallet.tariffs')}</h2>
        {zone && <p className="hud-label">{t('wallet.zoneOfPc', { zone })}</p>}
      </div>
      {loading ? (
        <div className="grid grid-cols-[repeat(auto-fill,minmax(clamp(16.5rem,18vw,21rem),1fr))] gap-[var(--gap)]">
          {Array.from({ length: 3 }, (_, i) => (
            <Skeleton key={i} height="16rem" />
          ))}
        </div>
      ) : tariffs.length === 0 ? (
        <p className="glass rounded-lg p-6 text-center text-lg text-muted">{t('wallet.noTariffs')}</p>
      ) : (
        <motion.ul
          role="list"
          className="grid grid-cols-[repeat(auto-fill,minmax(clamp(16.5rem,18vw,21rem),1fr))] gap-[var(--gap)]"
          initial={animations ? 'hidden' : false}
          animate="show"
          variants={{ hidden: {}, show: { transition: { staggerChildren: 0.05 } } }}
        >
          {sorted.map((tariff) => (
            <motion.li
              key={tariff.id}
              variants={{ hidden: { opacity: 0, y: 12 }, show: { opacity: 1, y: 0 } }}
              className="flex"
            >
              <TariffCard
                tariff={tariff}
                available={tariffAvailableNow(tariff, zone, now)}
                current={session.tariffId === tariff.id}
                action={action}
                onAction={(tf, a) => setDialog({ tariff: tf, action: a })}
              />
            </motion.li>
          ))}
        </motion.ul>
      )}
      <TariffActionModal
        tariff={dialog?.tariff ?? null}
        action={dialog?.action ?? 'start'}
        open={dialog !== null}
        onClose={() => setDialog(null)}
        onInsufficientFunds={onInsufficientFunds}
      />
    </section>
  );
}
