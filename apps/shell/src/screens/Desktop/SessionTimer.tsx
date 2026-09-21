/**
 * Session countdown: SVG ring (used / total), big `mm:ss` under 10 minutes or `hh:mm:ss` otherwise, paused/locked
 * badges and a pulsing danger state under 5 minutes. Clicking a timed session opens {@link ExtendSessionModal}
 * (30/60/120 minute presets priced by the session tariff → `session_extend`).
 */
import { useEffect, useMemo, useState } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { SESSION_OPEN_ENDED, tariffPriceFor, type Money, type Tariff } from '@clubshell/contracts';
import { Badge } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { useLocale } from '@/hooks/useLocale';
import { MMSS_BELOW_SEC, useSession } from '@/hooks/useSession';
import { formatMoney } from '@/lib/format';
import { hhmmss, mmss } from '@/lib/time';
import { useNotificationsStore } from '@/store/notifications';
import { selectBalanceAmount, selectTariff, useWalletStore } from '@/store/wallet';

/** Minute presets of the extend dialog. */
export const EXTEND_PRESETS: readonly number[] = [30, 60, 120];

/** `mm:ss` under 10 minutes, `hh:mm:ss` otherwise; time used for open-ended sessions; `--:--` without one. */
export function timerLabel(isOpen: boolean, secondsLeft: number, secondsUsed: number): string {
  if (!isOpen) {
    return '--:--';
  }
  if (secondsLeft === SESSION_OPEN_ENDED) {
    return hhmmss(secondsUsed);
  }
  return secondsLeft < MMSS_BELOW_SEC ? mmss(secondsLeft) : hhmmss(secondsLeft);
}

/** Presets allowed by the tariff (`minMinutes`/`maxMinutes`); every preset when the tariff is unknown or too strict. */
export function allowedPresets(tariff: Tariff | null): number[] {
  if (!tariff) {
    return [...EXTEND_PRESETS];
  }
  const ok = EXTEND_PRESETS.filter(
    (m) => m >= tariff.minMinutes && (tariff.maxMinutes == null || m <= tariff.maxMinutes),
  );
  return ok.length > 0 ? ok : [...EXTEND_PRESETS];
}

// ---------------------------------------------------------------------------------------------------------------------
// Ring
// ---------------------------------------------------------------------------------------------------------------------

interface RingProps {
  /** 0–1. */
  progress: number;
  size: number;
  stroke: number;
  tone: 'primary' | 'accent' | 'danger' | 'muted';
  label: string;
  valueText: string;
}

const RING_STROKE: Record<RingProps['tone'], string> = {
  primary: 'stroke-primary',
  accent: 'stroke-accent',
  danger: 'stroke-danger',
  muted: 'stroke-muted',
};

function Ring({ progress, size, stroke, tone, label, valueText }: RingProps): JSX.Element {
  const r = (size - stroke) / 2;
  const c = 2 * Math.PI * r;
  const pct = Math.min(1, Math.max(0, progress));
  return (
    <svg
      role="progressbar"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={Math.round(pct * 100)}
      aria-valuetext={valueText}
      width={size}
      height={size}
      viewBox={`0 0 ${size} ${size}`}
      className="shrink-0 -rotate-90"
    >
      <circle cx={size / 2} cy={size / 2} r={r} fill="none" strokeWidth={stroke} className="stroke-text/10" />
      <circle
        cx={size / 2}
        cy={size / 2}
        r={r}
        fill="none"
        strokeWidth={stroke}
        strokeLinecap="round"
        strokeDasharray={c}
        strokeDashoffset={c * (1 - pct)}
        className={clsx('transition-[stroke-dashoffset] duration-1000 ease-linear', RING_STROKE[tone])}
      />
    </svg>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Timer
// ---------------------------------------------------------------------------------------------------------------------

export interface SessionTimerProps {
  /** Top-bar size (44 px ring, 2xl text) instead of the large card. */
  compact?: boolean;
  className?: string;
}

export function SessionTimer({ compact = false, className }: SessionTimerProps): JSX.Element {
  const { t } = useTranslation();
  const s = useSession();
  const [extendOpen, setExtendOpen] = useState(false);

  const total = s.secondsUsed + Math.max(0, s.secondsLeft);
  const progress = s.isOpenEnded ? 1 : total > 0 ? s.secondsUsed / total : 0;
  const label = timerLabel(s.isOpen, s.secondsLeft, s.secondsUsed);
  const tone: RingProps['tone'] = !s.isOpen
    ? 'muted'
    : s.isCritical
      ? 'danger'
      : s.isWarning
        ? 'accent'
        : s.isOpenEnded
          ? 'muted'
          : 'primary';
  const canExtend = s.isOpen && !s.isOpenEnded;

  const badge = s.isLocked ? (
    <Badge tone="danger" size="sm" dot>
      {t('session.state.locked')}
    </Badge>
  ) : s.isPaused ? (
    <Badge tone="accent" size="sm" dot>
      {t('session.state.paused')}
    </Badge>
  ) : s.isOpenEnded ? (
    <Badge tone="muted" size="sm">
      {t('session.openEnded')}
    </Badge>
  ) : null;

  const ringSize = compact ? 44 : 168;
  const ringStroke = compact ? 4 : 10;
  const caption = !s.isOpen ? t('session.noSession') : s.isOpenEnded ? t('session.timeUsed') : t('session.timeLeft');

  return (
    <>
      <button
        type="button"
        data-nav="true"
        disabled={!canExtend}
        aria-label={`${t('session.timerLabel')}: ${label}`}
        title={canExtend ? t('session.extend') : undefined}
        onClick={() => canExtend && setExtendOpen(true)}
        className={clsx(
          'focus-ring glass flex select-none items-center rounded-full text-left transition-colors duration-[var(--dur-fast)] disabled:cursor-default',
          canExtend && 'hover:bg-surface/80',
          compact ? 'h-12 gap-3 pl-1 pr-4' : 'flex-col justify-center gap-3 rounded-2xl px-8 py-6',
          className,
        )}
      >
        <div className="relative flex items-center justify-center">
          <Ring
            progress={progress}
            size={ringSize}
            stroke={ringStroke}
            tone={tone}
            label={t('session.timerLabel')}
            valueText={label}
          />
          {!compact && (
            <span
              className={clsx(
                'tnum absolute text-4xl font-bold leading-none',
                s.isCritical ? 'timer-critical' : s.isWarning ? 'timer-warning' : 'text-text',
              )}
              aria-hidden="true"
            >
              {label}
            </span>
          )}
        </div>
        {compact ? (
          <span className="flex min-w-0 items-center gap-2">
            <span
              className={clsx(
                'tnum text-2xl font-bold leading-none',
                s.isCritical ? 'timer-critical' : s.isWarning ? 'timer-warning' : 'text-text',
              )}
            >
              {label}
            </span>
            {badge}
          </span>
        ) : (
          <span className="flex flex-col items-center gap-2">
            <span className="text-sm uppercase tracking-wide text-muted">{caption}</span>
            {badge}
            {canExtend && <span className="text-sm text-primary">{t('session.extend')}</span>}
          </span>
        )}
      </button>
      <ExtendSessionModal open={extendOpen} onClose={() => setExtendOpen(false)} />
    </>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Extend dialog
// ---------------------------------------------------------------------------------------------------------------------

export interface ExtendSessionModalProps {
  open: boolean;
  onClose: () => void;
}

export function ExtendSessionModal({ open, onClose }: ExtendSessionModalProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const { session, tariffId, extend, busy, isOpen, isOpenEnded } = useSession();
  const tariff = useWalletStore(selectTariff(tariffId));
  const tariffsLoaded = useWalletStore((s) => s.tariffs.length > 0);
  const loadTariffs = useWalletStore((s) => s.loadTariffs);
  const balance = useWalletStore(selectBalanceAmount);
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);

  const presets = useMemo(() => allowedPresets(tariff), [tariff]);
  const [minutes, setMinutes] = useState<number>(60);
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    if (open && !tariffsLoaded) {
      void loadTariffs();
    }
  }, [open, tariffsLoaded, loadTariffs]);

  useEffect(() => {
    if (open) {
      setMinutes(presets.includes(60) ? 60 : (presets[0] ?? 60));
    }
  }, [open, presets]);

  const prepaid = session?.isPrepaid ?? true;
  const cost: Money | null = tariff && prepaid ? tariffPriceFor(tariff, minutes) : null;
  const after: Money | null = cost ? { amount: balance.amount - cost.amount, currency: balance.currency } : null;
  const insufficient = after !== null && after.amount < 0;
  const disabled = !isOpen || isOpenEnded || !prepaid || insufficient || submitting || busy;

  const confirm = async (): Promise<void> => {
    if (disabled) {
      return;
    }
    setSubmitting(true);
    try {
      await extend(minutes, tariffId ?? undefined);
      push({ title: t('session.extended', { minutes }), level: 'success', source: 'local' });
      onClose();
    } catch (e) {
      pushError(e, t('session.extendTitle'));
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={t('session.extendTitle')}
      description={t('session.extendHint')}
      size="md"
      footer={
        <>
          <Button variant="ghost" size="lg" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button size="lg" loading={submitting} disabled={disabled} onClick={() => void confirm()}>
            {submitting ? t('session.extending') : t('session.extendMinutes', { minutes })}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-5">
        <div role="radiogroup" aria-label={t('session.minutes')} className="grid grid-cols-3 gap-3">
          {presets.map((m) => {
            const price = tariff && prepaid ? formatMoney(tariffPriceFor(tariff, m), locale) : null;
            const active = m === minutes;
            return (
              <button
                key={m}
                type="button"
                role="radio"
                aria-checked={active}
                data-nav="true"
                onClick={() => setMinutes(m)}
                className={clsx(
                  'focus-ring glass flex flex-col items-center gap-1 rounded-lg px-3 py-4 transition-colors duration-[var(--dur-fast)]',
                  active ? 'border-glow bg-primary/15 text-text' : 'text-muted hover:bg-surface/80 hover:text-text',
                )}
              >
                <span className="text-2xl font-bold leading-none text-text">
                  {t('session.extendMinutes', { minutes: m })}
                </span>
                {price && <span className="tnum text-sm">{price}</span>}
              </button>
            );
          })}
        </div>

        <dl className="grid grid-cols-2 gap-x-4 gap-y-2 text-base">
          <dt className="text-muted">{t('session.tariff')}</dt>
          <dd className="text-right font-semibold">{tariff?.name ?? '—'}</dd>
          <dt className="text-muted">{t('wallet.balance')}</dt>
          <dd className="tnum text-right font-semibold">{formatMoney(balance, locale)}</dd>
          {cost && (
            <>
              <dt className="text-muted">{t('wallet.estimatedCost')}</dt>
              <dd className="tnum text-right font-semibold">{formatMoney(cost, locale)}</dd>
              <dt className="text-muted">{t('wallet.balanceAfter')}</dt>
              <dd className={clsx('tnum text-right font-semibold', insufficient ? 'text-danger' : 'text-success')}>
                {after ? formatMoney(after, locale) : '—'}
              </dd>
            </>
          )}
        </dl>

        {!prepaid && <p className="text-sm text-muted">{t('session.extendPostpaid')}</p>}
        {insufficient && (
          <p role="alert" className="text-sm text-danger">
            {t('wallet.notEnough')}. {t('shop.topUpFirst')}
          </p>
        )}
      </div>
    </Modal>
  );
}
