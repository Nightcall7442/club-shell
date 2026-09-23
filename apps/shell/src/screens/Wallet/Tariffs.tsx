import { useEffect, useMemo, useState } from 'react';
import clsx from 'clsx';
import { motion } from 'framer-motion';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import {
  tariffIsValidFor,
  tariffPriceFor,
  WEEKDAYS,
  weekdayFromJsDay,
  type Tariff,
  type TariffTimeWindow,
} from '@clubshell/contracts';
import { Badge } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { Skeleton } from '@/components/ui/Skeleton';
import { useLocale } from '@/hooks/useLocale';
import { useSession } from '@/hooks/useSession';
import { formatMoney } from '@/lib/format';
import { isShellApiError } from '@/lib/tauri';
import { formatClock, serverNow } from '@/lib/time';
import { describeError, useNotificationsStore } from '@/store/notifications';
import { useSettingsStore } from '@/store/settings';
import { selectAnimationsEnabled, useThemeStore } from '@/store/theme';
import { selectBalanceAmount, useWalletStore } from '@/store/wallet';
import { ModalBackHandler } from './TopUpModal';

export type TariffAction = 'start' | 'extend';

/** Minute presets offered for hourly tariffs (filtered by `minMinutes`/`maxMinutes`). */
export const MINUTE_PRESETS: readonly number[] = [30, 60, 90, 120, 180, 240, 300, 480];

/** `true` when the tariff applies to `zone` (unknown zone = any) at `at` (club local time). */
export function tariffAvailableNow(tariff: Tariff, zone: string, at: Date): boolean {
  const z = zone || tariff.zones[0] || '';
  return tariffIsValidFor(tariff, z, weekdayFromJsDay(at.getDay()), formatClock(at, 'HH:mm'));
}

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
          {pkg && (
            <p className="tnum text-base text-muted">
              {t('wallet.packageMinutes', { minutes: tariff.packageMinutes, price: formatMoney(pkg, locale) })}
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

      <p className="tnum text-[clamp(1.6rem,2.2vw,2.4rem)] font-black leading-none text-text">
        {formatMoney(pkg ?? tariff.pricePerHour, locale)}
        {!pkg && <span className="ml-1 text-base font-semibold text-muted">/ {t('common.hourShort')}</span>}
      </p>

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
  const [prepaid, setPrepaid] = useState(true);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (open) {
      setMinutes(options.includes(60) ? 60 : (options[0] ?? 60));
      setPrepaid(true);
      setBusy(false);
    }
  }, [open, options]);

  const isPackage = Boolean(tariff?.isPackage);
  const charged = action === 'extend' || prepaid || isPackage;
  const cost = tariff ? tariffPriceFor(tariff, minutes) : null;
  const notEnough = charged && cost !== null && cost.amount > balance.amount;

  const submit = async (): Promise<void> => {
    if (!tariff) {
      return;
    }
    setBusy(true);
    try {
      if (action === 'start') {
        await session.start(tariff.id, prepaid || isPackage, prepaid || isPackage ? minutes : undefined);
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
          {action === 'start' && !isPackage && (
            <fieldset className="grid grid-cols-2 gap-2">
              {(
                [
                  { key: true, label: t('wallet.prepaid'), hint: t('wallet.prepaidHint') },
                  { key: false, label: t('wallet.postpaid'), hint: t('wallet.postpaidHint') },
                ] as const
              ).map((opt) => (
                <button
                  key={String(opt.key)}
                  type="button"
                  role="radio"
                  aria-checked={prepaid === opt.key}
                  data-nav="true"
                  onClick={() => setPrepaid(opt.key)}
                  className={clsx(
                    'focus-ring flex flex-col items-start gap-1 rounded-lg p-4 text-left transition-colors duration-[var(--dur-fast)]',
                    prepaid === opt.key ? 'bg-primary/20 border-glow' : 'glass hover:bg-surface/80',
                  )}
                >
                  <span className="text-base font-bold text-text">{opt.label}</span>
                  <span className="text-sm text-muted">{opt.hint}</span>
                </button>
              ))}
            </fieldset>
          )}

          {charged && (
            <fieldset className="flex flex-col gap-2">
              <legend className="mb-2 text-sm font-medium text-muted">{t('wallet.chooseMinutes')}</legend>
              <div className="grid grid-cols-4 gap-2">
                {options.map((m) => {
                  const active = minutes === m;
                  return (
                    <button
                      key={m}
                      type="button"
                      role="radio"
                      aria-checked={active}
                      data-nav="true"
                      onClick={() => setMinutes(m)}
                      className={clsx(
                        'focus-ring tnum flex h-16 flex-col items-center justify-center rounded-lg text-base font-bold transition-colors duration-[var(--dur-fast)]',
                        active
                          ? 'bg-primary text-on-primary shadow-[0_8px_24px_-8px_rgb(var(--c-primary)/0.4)]'
                          : 'glass text-text hover:bg-surface/80',
                      )}
                    >
                      <span>
                        {m} {t('common.min')}
                      </span>
                      <span className={clsx('text-xs font-medium', active ? 'text-on-primary/70' : 'text-muted')}>
                        {formatMoney(tariffPriceFor(tariff, m), locale)}
                      </span>
                    </button>
                  );
                })}
              </div>
            </fieldset>
          )}

          <dl className="flex flex-col gap-1 rounded-lg bg-text/5 px-4 py-3 text-base">
            <div className="flex items-center justify-between">
              <dt className="text-muted">{t('wallet.estimatedCost')}</dt>
              <dd className="tnum text-xl font-black text-text">
                {charged && cost ? formatMoney(cost, locale) : t('wallet.postpaid')}
              </dd>
            </div>
            {charged && cost && (
              <div className="flex items-center justify-between text-sm">
                <dt className="text-muted">{t('wallet.balanceAfter')}</dt>
                <dd className={clsx('tnum font-semibold', notEnough ? 'text-danger' : 'text-muted')}>
                  {formatMoney({ amount: balance.amount - cost.amount, currency: cost.currency }, locale)}
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

/** Tariff cards grouped by zone with live availability and the start / extend action for the current session. */
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
  const action: TariffAction | null = !session.isOpen
    ? 'start'
    : session.isLocked || session.isOpenEnded
      ? null
      : 'extend';

  const groups = useMemo(() => {
    const map = new Map<string, Tariff[]>();
    for (const tariff of tariffs) {
      const key = tariff.zones.length === 0 ? t('common.all') : tariff.zones.join(', ');
      map.set(key, [...(map.get(key) ?? []), tariff]);
    }
    // This PC's zone first, then the rest alphabetically.
    return Array.from(map, ([key, items]) => ({ key, items })).sort((a, b) => {
      const az = a.items[0]?.zones.some((z) => z.toLowerCase() === zone.toLowerCase()) ? 0 : 1;
      const bz = b.items[0]?.zones.some((z) => z.toLowerCase() === zone.toLowerCase()) ? 0 : 1;
      return az - bz || a.key.localeCompare(b.key);
    });
  }, [tariffs, zone, t]);

  const loading = status === 'loading' && tariffs.length === 0;

  return (
    <section aria-label={t('wallet.tariffs')} className={clsx('flex flex-col gap-4', className)}>
      <h2 className="text-2xl font-bold text-text">{t('wallet.tariffs')}</h2>
      {loading ? (
        <div className="grid grid-cols-[repeat(auto-fill,minmax(clamp(15rem,17vw,20rem),1fr))] gap-[var(--gap)]">
          {Array.from({ length: 3 }, (_, i) => (
            <Skeleton key={i} height="16rem" />
          ))}
        </div>
      ) : tariffs.length === 0 ? (
        <p className="glass rounded-lg p-6 text-center text-lg text-muted">{t('wallet.noTariffs')}</p>
      ) : (
        groups.map((group, gi) => (
          <div key={group.key} className="flex flex-col gap-3">
            {groups.length > 1 && (
              <h3 className="text-base font-semibold uppercase tracking-wide text-muted">{group.key}</h3>
            )}
            <motion.ul
              role="list"
              className="grid grid-cols-[repeat(auto-fill,minmax(clamp(15rem,17vw,20rem),1fr))] gap-[var(--gap)]"
              initial={animations ? 'hidden' : false}
              animate="show"
              variants={{ hidden: {}, show: { transition: { staggerChildren: 0.05, delayChildren: gi * 0.05 } } }}
            >
              {group.items.map((tariff) => (
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
          </div>
        ))
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
