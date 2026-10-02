/**
 * Top-up at the counter: how the club takes money while online payment is unavailable (no payment provider on the
 * server, `features.topup` off, or the server out of reach). The counter's top-up reaches this PC as `wallet.updated`,
 * so the player only has to say who they are; the balance on screen follows by itself.
 */
import { forwardRef, useState } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { Button, type ButtonProps } from '@/components/ui/Button';
import { Spinner } from '@/components/ui/Spinner';
import { track } from '@/lib/analytics';
import { api } from '@/lib/tauri';
import { useAuthStore } from '@/store/auth';
import { useNotificationsStore } from '@/store/notifications';
import { selectFeature, useSettingsStore } from '@/store/settings';

const CounterIcon = (): JSX.Element => (
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="1.8"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <path d="M4 10h16v9a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1v-9zM2.5 10h19M7 10V5.5A1.5 1.5 0 0 1 8.5 4h7A1.5 1.5 0 0 1 17 5.5V10" />
    <path d="M9.5 7h5M8 14h2M14 14h2M8 17h8" />
  </svg>
);

const BellIcon = (): JSX.Element => (
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="2"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <path d="M6 16V11a6 6 0 0 1 12 0v5l1.5 2h-15L6 16Z" />
    <path d="M10 20a2 2 0 0 0 4 0" />
  </svg>
);

const CheckIcon = (): JSX.Element => (
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="2.5"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <path d="M5 12.5l4.5 4.5L19 7.5" />
  </svg>
);

export interface CashDeskStepsProps {
  className?: string;
}

/** The three steps at the counter, with the login and PC to name read out large, and a live "waiting" line. */
export function CashDeskSteps({ className }: CashDeskStepsProps): JSX.Element {
  const { t } = useTranslation();
  const login = useAuthStore((s) => s.user?.username ?? null);
  const pcName = useSettingsStore((s) => s.pcInfo?.pc.name ?? null);

  return (
    <div className={clsx('flex flex-col gap-5', className)}>
      <div className="flex items-center gap-4 rounded-lg border border-accent/25 bg-accent/[0.06] p-4">
        <span
          aria-hidden="true"
          className="inline-flex h-12 w-12 shrink-0 items-center justify-center rounded-full bg-accent/15 text-accent [&>svg]:h-7 [&>svg]:w-7"
        >
          <CounterIcon />
        </span>
        <div className="min-w-0">
          <p className="text-lg font-semibold leading-snug text-text">{t('wallet.deskLead')}</p>
          <p className="mt-0.5 text-sm text-muted">{t('wallet.onlineOff')}</p>
        </div>
      </div>

      <ol className="flex flex-col gap-4">
        {[t('wallet.deskStepGo'), t('wallet.deskStepName'), t('wallet.deskStepPay')].map((step, i) => (
          <li key={i} className="flex items-start gap-3">
            <span
              aria-hidden="true"
              className="num-dot inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-full border border-accent/40 text-base text-accent"
            >
              {i + 1}
            </span>
            <div className="flex min-w-0 flex-1 flex-col gap-3 pt-1">
              <p className="text-base text-text">{step}</p>
              {i === 1 && (login || pcName) && (
                <dl className="grid grid-cols-2 gap-2">
                  {login && (
                    <div className="min-w-0 rounded-md bg-text/[0.05] px-3 py-2">
                      <dt className="hud-label">{t('wallet.deskLogin')}</dt>
                      <dd translate="no" className="truncate font-mono text-lg font-semibold text-text">
                        {login}
                      </dd>
                    </div>
                  )}
                  {pcName && (
                    <div className="min-w-0 rounded-md bg-text/[0.05] px-3 py-2">
                      <dt className="hud-label">{t('wallet.deskPc')}</dt>
                      <dd translate="no" className="truncate font-mono text-lg font-semibold text-text">
                        {pcName}
                      </dd>
                    </div>
                  )}
                </dl>
              )}
            </div>
          </li>
        ))}
      </ol>

      <p className="flex items-center gap-2 text-sm text-muted" aria-live="polite">
        <Spinner size="sm" aria-hidden="true" />
        {t('wallet.deskWaiting')}
      </p>
    </div>
  );
}

export type CallAdminForTopUpProps = Pick<ButtonProps, 'variant' | 'size' | 'block' | 'className'>;

/**
 * "Call an administrator" with the top-up reason, through the shell's one call-admin path (`sys_call_admin`, the
 * Agent's 30 s rate limit and offline queue included). Answers in place instead of a toast: the dialog stays on top.
 * Renders nothing when the club turned `callAdmin` off.
 */
export const CallAdminForTopUp = forwardRef<HTMLButtonElement, CallAdminForTopUpProps>(function CallAdminForTopUp(
  { variant = 'primary', size = 'lg', block, className },
  ref,
) {
  const { t } = useTranslation();
  const enabled = useSettingsStore(selectFeature('callAdmin'));
  const pushError = useNotificationsStore((s) => s.pushError);
  const [state, setState] = useState<'idle' | 'calling' | 'called'>('idle');

  if (!enabled) {
    return null;
  }

  const call = async (): Promise<void> => {
    setState('calling');
    track('wallet.callAdmin');
    try {
      await api.system.callAdmin('help', t('wallet.callAdminMessage'));
      setState('called');
    } catch (e) {
      setState('idle');
      pushError(e, t('wallet.callAdmin'));
    }
  };

  return (
    <Button
      ref={ref}
      variant={state === 'called' ? 'secondary' : variant}
      size={size}
      block={block}
      className={clsx(state === 'called' && 'text-success', className)}
      icon={state === 'called' ? <CheckIcon /> : <BellIcon />}
      loading={state === 'calling'}
      disabled={state === 'called'}
      onClick={() => void call()}
    >
      {state === 'called' ? t('wallet.deskCalled') : t('wallet.callAdmin')}
    </Button>
  );
});
