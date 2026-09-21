import { useEffect, useRef, useState } from 'react';
import clsx from 'clsx';
import { QRCodeSVG } from 'qrcode.react';
import { useTranslation } from 'react-i18next';
import type { AuthLoginResponse, QrLoginStart } from '@clubshell/contracts';
import { Button } from '@/components/ui/Button';
import { ProgressBar } from '@/components/ui/ProgressBar';
import { Skeleton } from '@/components/ui/Skeleton';
import { Spinner } from '@/components/ui/Spinner';
import { api, toShellApiError } from '@/lib/tauri';
import { mmss, secondsUntil } from '@/lib/time';
import { useAuthStore } from '@/store/auth';
import { describeError } from '@/store/notifications';

export interface QrLoginProps {
  onSuccess?: (res: AuthLoginResponse) => void;
  className?: string;
}

export type QrPhase = 'loading' | 'pending' | 'scanned' | 'confirmed' | 'expired' | 'error';

const QR_SIZE = 224;

function reasonOf(e: unknown): string | null {
  const err = toShellApiError(e);
  const details = typeof err.details === 'object' && err.details !== null ? (err.details as Record<string, unknown>) : {};
  return typeof details['reason'] === 'string' ? details['reason'] : null;
}

/**
 * QR sign-in: `auth_qr_start` → QR of `qrUrl` + expiry countdown; the token is exchanged every `pollIntervalSec`
 * through `auth_login{kind:'qr'}` until the app confirms it (`unauthorized/pending` means "not yet").
 */
export function QrLogin({ onSuccess, className }: QrLoginProps): JSX.Element {
  const { t } = useTranslation();
  const login = useAuthStore((s) => s.login);
  const clearError = useAuthStore((s) => s.clearError);
  const [start, setStart] = useState<QrLoginStart | null>(null);
  const [phase, setPhase] = useState<QrPhase>('loading');
  const [message, setMessage] = useState<string | null>(null);
  const [left, setLeft] = useState(0);
  const [total, setTotal] = useState(1);
  const [attempt, setAttempt] = useState(0);
  const onSuccessRef = useRef(onSuccess);
  onSuccessRef.current = onSuccess;

  // New handshake per attempt.
  useEffect(() => {
    let active = true;
    setPhase('loading');
    setStart(null);
    setMessage(null);
    api.auth.qrStart().then(
      (s) => {
        if (active) {
          const secs = Math.max(1, Math.floor(secondsUntil(s.expiresAt) || 0));
          setStart(s);
          setTotal(secs);
          setLeft(secs);
          setPhase('pending');
        }
      },
      (e: unknown) => {
        if (active) {
          setPhase('error');
          setMessage(describeError(e));
        }
      },
    );
    return () => {
      active = false;
    };
  }, [attempt]);

  const waiting = phase === 'pending' || phase === 'scanned';

  // Expiry countdown.
  useEffect(() => {
    if (!start || !waiting) {
      return undefined;
    }
    const tick = (): void => {
      const s = Math.max(0, Math.floor(secondsUntil(start.expiresAt) || 0));
      setLeft(s);
      if (s <= 0) {
        setPhase('expired');
      }
    };
    tick();
    const id = setInterval(tick, 1000);
    return () => clearInterval(id);
  }, [start, waiting]);

  // Token exchange poll.
  useEffect(() => {
    if (!start || !waiting) {
      return undefined;
    }
    let active = true;
    let inFlight = false;
    const poll = async (): Promise<void> => {
      if (inFlight) {
        return;
      }
      inFlight = true;
      try {
        const res = await login('qr', { qrToken: start.qrToken });
        if (active) {
          setPhase('confirmed');
          onSuccessRef.current?.(res);
        }
      } catch (e) {
        if (!active) {
          return;
        }
        const err = toShellApiError(e);
        const reason = reasonOf(err);
        if (err.code === 'unauthorized' && (reason === 'pending' || reason === 'scanned')) {
          clearError();
          if (reason === 'scanned') {
            setPhase('scanned');
          }
        } else if (err.code === 'unauthorized' && reason === 'expired') {
          clearError();
          setPhase('expired');
        } else {
          setPhase('error');
          setMessage(describeError(err));
        }
      } finally {
        inFlight = false;
      }
    };
    const id = setInterval(() => void poll(), Math.max(1, start.pollIntervalSec) * 1000);
    return () => {
      active = false;
      clearInterval(id);
    };
  }, [start, waiting, login, clearError]);

  const stale = phase === 'expired' || phase === 'error';

  return (
    <div className={clsx('flex flex-col items-center gap-5 text-center', className)}>
      <p className="text-base text-muted">{t('lock.scanQr')}</p>

      <div className="relative">
        {phase === 'loading' || !start ? (
          <Skeleton variant="rect" width={QR_SIZE + 32} height={QR_SIZE + 32} className="rounded-xl" />
        ) : (
          <div className={clsx('rounded-xl bg-white p-4 shadow-[var(--shadow-card)] transition-opacity duration-[var(--dur-base)]', stale && 'opacity-20')}>
            <QRCodeSVG value={start.qrUrl} size={QR_SIZE} level="M" marginSize={0} bgColor="#ffffff" fgColor="#0b0f1a" title={t('lock.qr')} />
          </div>
        )}
        {stale && (
          <div className="absolute inset-0 flex flex-col items-center justify-center gap-3 p-4">
            <p role="alert" className="text-lg font-semibold text-danger">
              {phase === 'expired' ? t('lock.qrExpired') : message}
            </p>
            <Button size="lg" onClick={() => setAttempt((a) => a + 1)}>
              {t('lock.qrRefresh')}
            </Button>
          </div>
        )}
        {phase === 'confirmed' && (
          <div className="absolute inset-0 flex items-center justify-center rounded-xl bg-success/80">
            <svg viewBox="0 0 24 24" className="h-20 w-20 text-white" fill="none" stroke="currentColor" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
              <path d="M5 13l4 4L19 7" />
            </svg>
          </div>
        )}
      </div>

      <div role="status" aria-live="polite" className="flex min-h-[1.75rem] items-center justify-center gap-3 text-base">
        {waiting && <Spinner size="sm" />}
        <span className={clsx(phase === 'scanned' && 'text-accent', phase === 'confirmed' && 'text-success', 'text-text')}>
          {phase === 'pending' && t('lock.qrWaiting')}
          {phase === 'scanned' && t('lock.qrScanned')}
          {phase === 'confirmed' && t('lock.qrConfirmed')}
          {phase === 'loading' && t('common.loading')}
        </span>
      </div>

      {waiting && start && (
        <div className="w-full max-w-[20rem]">
          <ProgressBar
            value={left}
            max={total}
            size="sm"
            tone={left <= 20 ? 'accent' : 'primary'}
            label={t('lock.qrExpiresIn', { time: mmss(left) })}
            valueText={t('lock.qrExpiresIn', { time: mmss(left) })}
          />
        </div>
      )}

      {waiting && (
        <Button variant="ghost" size="md" onClick={() => setAttempt((a) => a + 1)}>
          {t('lock.qrRefresh')}
        </Button>
      )}
    </div>
  );
}

export default QrLogin;
