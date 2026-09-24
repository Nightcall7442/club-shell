import { useCallback, useEffect, useRef, useState } from 'react';
import clsx from 'clsx';
import { QRCodeSVG } from 'qrcode.react';
import { useTranslation } from 'react-i18next';
import {
  TOPUP_MIN_AMOUNT_MINOR,
  TopupProvider,
  type Money,
  type TopupIntent,
  type TopupProvider as TopupProviderValue,
} from '@clubshell/contracts';
import { Badge } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { Modal } from '@/components/ui/Modal';
import { Spinner } from '@/components/ui/Spinner';
import { useGamepad } from '@/hooks/useGamepad';
import { useLocale } from '@/hooks/useLocale';
import { formatMoney } from '@/lib/format';
import { isShellApiError } from '@/lib/tauri';
import { mmss, secondsUntil } from '@/lib/time';
import { useNotificationsStore } from '@/store/notifications';
import { useWalletStore } from '@/store/wallet';

export interface TopUpModalProps {
  open: boolean;
  onClose: () => void;
  /** Called once the intent settles (after the success toast). */
  onPaid?: (intent: TopupIntent) => void;
}

/** Quick amounts in UZS (major units). */
export const TOPUP_PRESETS_UZS: readonly number[] = [10_000, 20_000, 50_000, 100_000];

/** Provider chips in display order. */
export const TOPUP_PROVIDERS: readonly TopupProviderValue[] = [
  TopupProvider.Payme,
  TopupProvider.Click,
  TopupProvider.Uzum,
  TopupProvider.Cash,
];

const POLL_MS = 4000;
const CLOSE_AFTER_PAID_MS = 2500;

type Step = 'form' | 'pending' | 'paid' | 'expired';

/** Parses a user-typed UZS amount (digits, spaces, separators) to minor units; `null` when not a number. */
export function parseUzsInput(text: string): number | null {
  const digits = text.replace(/[^\d]/g, '');
  if (digits.length === 0) {
    return null;
  }
  const major = Number(digits);
  return Number.isSafeInteger(major) ? major * 100 : null;
}

/**
 * Routes gamepad `B` to `onBack` for a dialog. Rendered *inside* the dialog body so it mounts after the screen's
 * own `useGamepad` instance and therefore shadows it while the dialog is open.
 */
export function ModalBackHandler({ onBack }: { onBack: () => void }): null {
  useGamepad({ onBack });
  return null;
}

const CheckIcon = (): JSX.Element => (
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="3"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <path d="M5 13l4 4L19 7" />
  </svg>
);

/**
 * Top-up flow: amount presets / custom amount + provider chips → `wallet.topupIntent` → QR (or "pay at the desk"
 * for cash) with an expiry countdown; settles from `agent://wallet.updated` (store marks the intent `paid`) or the
 * balance poll fallback, then closes itself.
 */
export function TopUpModal({ open, onClose, onPaid }: TopUpModalProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const balance = useWalletStore((s) => s.balance);
  const storeIntent = useWalletStore((s) => s.topupIntent);
  const createTopup = useWalletStore((s) => s.createTopup);
  const clearTopup = useWalletStore((s) => s.clearTopup);
  const loadBalance = useWalletStore((s) => s.loadBalance);
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);

  const [step, setStep] = useState<Step>('form');
  const [preset, setPreset] = useState<number | null>(TOPUP_PRESETS_UZS[1] ?? null);
  const [custom, setCustom] = useState('');
  const [provider, setProvider] = useState<TopupProviderValue>(TopupProvider.Payme);
  const [creating, setCreating] = useState(false);
  const [intent, setIntent] = useState<TopupIntent | null>(null);
  const [secondsLeft, setSecondsLeft] = useState(0);
  const baseline = useRef<number | null>(null);
  const settled = useRef(false);
  const firstFocus = useRef<HTMLButtonElement>(null);

  const currency = balance?.currency ?? 'UZS';
  const amountMinor = custom.trim().length > 0 ? parseUzsInput(custom) : preset !== null ? preset * 100 : null;
  const amountError =
    custom.trim().length > 0 && amountMinor === null
      ? t('wallet.amountInvalid')
      : amountMinor !== null && amountMinor < TOPUP_MIN_AMOUNT_MINOR
        ? t('wallet.minAmount', { amount: formatMoney({ amount: TOPUP_MIN_AMOUNT_MINOR, currency }, locale) })
        : null;
  const canContinue = amountMinor !== null && amountError === null && !creating;

  // Reset when (re)opened.
  useEffect(() => {
    if (open) {
      setStep('form');
      setCustom('');
      setCreating(false);
      setIntent(null);
      baseline.current = null;
      settled.current = false;
    }
  }, [open]);

  const finishPaid = useCallback(
    (paid: TopupIntent) => {
      if (settled.current) {
        return;
      }
      settled.current = true;
      setStep('paid');
      push({
        title: t('notifications.topUpSuccess'),
        body: t('wallet.topUpSuccess', { amount: formatMoney(paid.amount, locale) }),
        level: 'success',
      });
      onPaid?.(paid);
    },
    [push, t, locale, onPaid],
  );

  // Settlement via the store (`wallet.updated` raised the balance by the intent amount).
  useEffect(() => {
    if (step === 'pending' && intent && storeIntent?.id === intent.id && storeIntent.status === 'paid') {
      finishPaid(storeIntent);
    }
  }, [step, intent, storeIntent, finishPaid]);

  // Countdown + balance poll while pending.
  useEffect(() => {
    if (step !== 'pending' || !intent) {
      return undefined;
    }
    const tick = (): void => {
      const left = Math.max(0, Math.floor(secondsUntil(intent.expiresAt) || 0));
      setSecondsLeft(left);
      if (left <= 0) {
        setStep('expired');
      }
    };
    tick();
    const timer = setInterval(tick, 1000);
    // ponytail: no `topupStatus` command in the protocol; the balance poll is the fallback when the push event is missed.
    const poll = setInterval(() => {
      void (async () => {
        const b = await loadBalance();
        if (b && baseline.current !== null && b.amount.amount >= baseline.current + intent.amount.amount) {
          finishPaid({ ...intent, status: 'paid' });
        }
      })();
    }, POLL_MS);
    return () => {
      clearInterval(timer);
      clearInterval(poll);
    };
  }, [step, intent, loadBalance, finishPaid]);

  // Auto-close after success.
  useEffect(() => {
    if (step !== 'paid') {
      return undefined;
    }
    const timer = setTimeout(() => {
      clearTopup();
      onClose();
    }, CLOSE_AFTER_PAID_MS);
    return () => clearTimeout(timer);
  }, [step, onClose, clearTopup]);

  const close = (): void => {
    clearTopup();
    onClose();
  };

  const submit = async (): Promise<void> => {
    if (amountMinor === null || amountError) {
      return;
    }
    const amount: Money = { amount: amountMinor, currency };
    setCreating(true);
    try {
      baseline.current = useWalletStore.getState().balance?.amount.amount ?? null;
      const created = await createTopup(amount, provider);
      setIntent(created);
      setStep('pending');
    } catch (e) {
      if (isShellApiError(e) && e.code === 'validation') {
        push({
          title: t('wallet.topUpTitle'),
          body: t('wallet.minAmount', { amount: formatMoney({ amount: TOPUP_MIN_AMOUNT_MINOR, currency }, locale) }),
          level: 'error',
        });
      } else {
        pushError(e, t('wallet.topUpTitle'));
      }
    } finally {
      setCreating(false);
    }
  };

  const qrValue = intent ? (intent.paymentUrl ?? intent.deepLink ?? null) : null;
  const providerName = intent ? t(`wallet.provider.${intent.provider}`) : '';

  return (
    <Modal
      open={open}
      onClose={close}
      title={t('wallet.topUpTitle')}
      description={step === 'form' ? t('wallet.topUpHint') : undefined}
      size="md"
      initialFocusRef={firstFocus}
      closeOnBackdrop={step !== 'pending'}
      footer={
        step === 'form' ? (
          <>
            <Button variant="secondary" size="lg" onClick={close}>
              {t('common.cancel')}
            </Button>
            <Button variant="cta" size="lg" loading={creating} disabled={!canContinue} onClick={() => void submit()}>
              {creating ? t('wallet.creating') : t('wallet.createIntent')}
            </Button>
          </>
        ) : step === 'expired' ? (
          <>
            <Button variant="secondary" size="lg" onClick={close}>
              {t('common.close')}
            </Button>
            <Button size="lg" onClick={() => setStep('form')}>
              {t('common.retry')}
            </Button>
          </>
        ) : (
          <Button variant="secondary" size="lg" onClick={close}>
            {step === 'paid' ? t('common.done') : t('common.close')}
          </Button>
        )
      }
    >
      <ModalBackHandler onBack={close} />
      {step === 'form' && (
        <div className="flex flex-col gap-5">
          <fieldset className="flex flex-col gap-2">
            <legend className="mb-2 text-sm font-medium text-muted">{t('wallet.quickAmounts')}</legend>
            <div className="grid grid-cols-4 gap-2">
              {TOPUP_PRESETS_UZS.map((value, i) => {
                const active = custom.trim().length === 0 && preset === value;
                return (
                  <button
                    key={value}
                    ref={i === 0 ? firstFocus : undefined}
                    type="button"
                    data-nav="true"
                    aria-pressed={active}
                    onClick={() => {
                      setPreset(value);
                      setCustom('');
                    }}
                    className={clsx(
                      'focus-ring tnum h-14 rounded-lg text-base font-bold transition-colors duration-[var(--dur-fast)]',
                      active ? 'choice choice-on' : 'choice',
                    )}
                  >
                    {formatMoney({ amount: value * 100, currency }, locale)}
                  </button>
                );
              })}
            </div>
          </fieldset>

          <Input
            label={t('wallet.customAmount')}
            placeholder={t('wallet.amountPlaceholder')}
            inputMode="numeric"
            autoComplete="off"
            value={custom}
            error={amountError}
            hint={
              amountError
                ? undefined
                : t('wallet.minAmount', { amount: formatMoney({ amount: TOPUP_MIN_AMOUNT_MINOR, currency }, locale) })
            }
            trailing={<span className="text-sm font-semibold">{t('common.currency')}</span>}
            onChange={(e) => setCustom(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && canContinue) {
                e.preventDefault();
                void submit();
              }
            }}
          />

          <fieldset className="flex flex-col gap-2">
            <legend className="mb-2 text-sm font-medium text-muted">{t('wallet.chooseProvider')}</legend>
            <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
              {TOPUP_PROVIDERS.map((p) => {
                const active = provider === p;
                return (
                  <button
                    key={p}
                    type="button"
                    data-nav="true"
                    role="radio"
                    aria-checked={active}
                    onClick={() => setProvider(p)}
                    className={clsx(
                      'focus-ring h-12 rounded-md text-base font-semibold',
                      active ? 'choice choice-on' : 'choice',
                    )}
                  >
                    {t(`wallet.provider.${p}`)}
                  </button>
                );
              })}
            </div>
            {provider === TopupProvider.Cash && <p className="text-sm text-muted">{t('wallet.cashHint')}</p>}
          </fieldset>

          <div className="flex items-center justify-between rounded-lg bg-text/5 px-4 py-3 text-base">
            <span className="text-muted">{t('wallet.amount')}</span>
            <span className="tnum text-xl font-semibold text-text">
              {amountMinor !== null && !amountError ? formatMoney({ amount: amountMinor, currency }, locale) : '—'}
            </span>
          </div>
        </div>
      )}

      {step === 'pending' && intent && (
        <div className="flex flex-col items-center gap-4 text-center" aria-live="polite">
          <Badge tone="primary" size="lg" live>
            {t('wallet.waitingPayment')}
          </Badge>
          <p className="tnum text-3xl font-semibold text-text">{formatMoney(intent.amount, locale)}</p>
          {intent.provider === TopupProvider.Cash ? (
            <p className="max-w-[28rem] text-lg text-text">{t('wallet.cashHint')}</p>
          ) : qrValue ? (
            <>
              <div className="rounded-xl bg-white p-4 shadow-[var(--shadow-glow)]">
                <QRCodeSVG
                  value={qrValue}
                  size={240}
                  level="M"
                  marginSize={0}
                  aria-label={t('wallet.scanToPay', { provider: providerName })}
                />
              </div>
              <p className="text-base text-text">{t('wallet.scanToPay', { provider: providerName })}</p>
              {intent.deepLink && (
                <p
                  className="max-w-full truncate rounded-md bg-text/5 px-3 py-1.5 font-mono text-sm text-muted"
                  title={intent.deepLink}
                >
                  {t('wallet.openInApp')}: {intent.deepLink}
                </p>
              )}
              {intent.paymentUrl && (
                <p
                  className="max-w-full truncate rounded-md bg-text/5 px-3 py-1.5 font-mono text-sm text-muted"
                  title={intent.paymentUrl}
                >
                  {t('wallet.payOnWeb')}: {intent.paymentUrl}
                </p>
              )}
            </>
          ) : intent.qrUrl ? (
            <>
              <img
                src={intent.qrUrl}
                alt={t('wallet.scanToPay', { provider: providerName })}
                width={240}
                height={240}
                className="rounded-xl bg-white p-3"
              />
              <p className="text-base text-text">{t('wallet.scanToPay', { provider: providerName })}</p>
            </>
          ) : (
            <Spinner size="lg" />
          )}
          <p className={clsx('tnum text-sm', secondsLeft <= 60 ? 'text-danger' : 'text-muted')}>
            {t('wallet.intentExpiresIn', { time: mmss(secondsLeft) })}
          </p>
        </div>
      )}

      {step === 'paid' && intent && (
        <div className="flex flex-col items-center gap-3 py-4 text-center">
          <span
            className="inline-flex h-16 w-16 items-center justify-center rounded-full bg-success text-bg anim-pop"
            aria-hidden="true"
          >
            <span className="h-9 w-9">
              <CheckIcon />
            </span>
          </span>
          <p className="text-2xl font-bold text-text">{t('wallet.paid')}</p>
          <p className="tnum text-lg text-success">
            {t('wallet.topUpSuccess', { amount: formatMoney(intent.amount, locale) })}
          </p>
          <p className="text-sm text-muted">{t('wallet.closeAfterPaid')}</p>
        </div>
      )}

      {step === 'expired' && (
        <div className="flex flex-col items-center gap-2 py-4 text-center">
          <p className="text-2xl font-bold text-danger">{t('wallet.expired')}</p>
          <p className="text-base text-muted">{t('common.tryAgain')}</p>
        </div>
      )}
    </Modal>
  );
}

export default TopUpModal;
