import { useRef, useState, type KeyboardEvent } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import type { AuthLoginResponse } from '@clubshell/contracts';
import { Button } from '@/components/ui/Button';
import { errorDetails } from '@/lib/errorText';
import { toShellApiError } from '@/lib/tauri';
import { useAuthStore } from '@/store/auth';
import { isElsewhereError, loginErrorMessage } from './LoginForm';

/** The desk's code is four digits (D-96). */
export const GUEST_CODE_LENGTH = 4;

export interface GuestCodeFormProps {
  onSuccess?: (res: AuthLoginResponse) => void;
  className?: string;
}

const KEYS = ['1', '2', '3', '4', '5', '6', '7', '8', '9'] as const;

const EraseIcon = (
  <svg
    viewBox="0 0 24 24"
    className="h-6 w-6"
    fill="none"
    stroke="currentColor"
    strokeWidth="2"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <path d="M21 5H9l-6 7 6 7h12z" />
    <path d="M17 9l-6 6M11 9l6 6" />
  </svg>
);

/**
 * A walk-in guest's seat the cash desk opened on this PC (`session.guestCode`, D-96): no PC signs anyone in on the
 * server's word, so the guest types the four digits the cashier named. Keyboard or the on-screen digits; the fourth
 * digit signs in. A wrong code says how many tries are left (the server counts them per PC: 5 in 15 minutes), the
 * digits are cleared after every attempt and never logged.
 */
export function GuestCodeForm({ onSuccess, className }: GuestCodeFormProps): JSX.Element {
  const { t } = useTranslation();
  const loginGuestCode = useAuthStore((s) => s.loginGuestCode);
  const field = useRef<HTMLInputElement>(null);
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const signIn = async (value: string): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      const res = await loginGuestCode(value);
      onSuccess?.(res);
    } catch (e) {
      const err = toShellApiError(e);
      if (!isElsewhereError(err)) {
        const left = errorDetails(err.details)['attemptsLeft'];
        setError(
          err.code !== 'unauthorized'
            ? loginErrorMessage(err)
            : left === 0
              ? t('lock.guestCode.lockedOut')
              : typeof left === 'number'
                ? t('lock.guestCode.wrongLeft', { attempts: left })
                : t('lock.guestCode.wrong'),
        );
      }
      setCode('');
      field.current?.focus();
    } finally {
      setBusy(false);
    }
  };

  const enter = (next: string): void => {
    if (busy) {
      return;
    }
    const digits = next.replace(/\D/g, '').slice(0, GUEST_CODE_LENGTH);
    setCode(digits);
    if (digits.length > 0) {
      setError(null);
    }
    if (digits.length === GUEST_CODE_LENGTH) {
      void signIn(digits);
    }
  };

  // Digits typed while the on-screen keypad has the focus (a gamepad, a click) go to the code as well.
  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>): void => {
    if (e.target === field.current || e.ctrlKey || e.altKey || e.metaKey) {
      return;
    }
    if (/^\d$/.test(e.key)) {
      e.preventDefault();
      enter(code + e.key);
    } else if (e.key === 'Backspace') {
      e.preventDefault();
      enter(code.slice(0, -1));
    }
  };

  return (
    <div
      onKeyDown={onKeyDown}
      className={clsx('flex flex-col items-center gap-[clamp(0.75rem,1.9vh,1.25rem)] text-center', className)}
    >
      <div>
        <h1 className="font-display text-3xl font-light leading-tight tracking-tight text-text">
          {t('lock.guestCode.title')}
        </h1>
        <p className="mt-1 text-balance text-base text-muted">{t('lock.guestCode.hint')}</p>
      </div>

      {/* Four cells over one real field: the keyboard, the on-screen keypad and a screen reader all write to it. */}
      <label className="group/code relative block cursor-text" onClick={() => field.current?.focus()}>
        <input
          ref={field}
          name="guestCode"
          type="text"
          inputMode="numeric"
          autoComplete="one-time-code"
          pattern="[0-9]*"
          maxLength={GUEST_CODE_LENGTH}
          autoFocus
          aria-label={t('lock.guestCode.label')}
          aria-invalid={error !== null}
          aria-describedby={error ? 'guest-code-error' : undefined}
          value={code}
          readOnly={busy}
          onChange={(e) => enter(e.target.value)}
          className="absolute inset-0 h-full w-full cursor-text opacity-0"
        />
        <span aria-hidden="true" className="flex gap-3">
          {Array.from({ length: GUEST_CODE_LENGTH }, (_, i) => (
            <span
              key={i}
              className={clsx(
                'field tnum flex h-[clamp(4rem,10vh,5.5rem)] w-[clamp(3.25rem,8vh,4.5rem)] items-center justify-center rounded-md font-mono text-[clamp(2rem,5vh,3rem)] font-light text-text transition-[box-shadow,border-color] duration-[var(--dur-fast)]',
                error
                  ? 'border-danger/70'
                  : i === Math.min(code.length, GUEST_CODE_LENGTH - 1) &&
                      'group-focus-within/code:border-accent/60 group-focus-within/code:[box-shadow:var(--shadow-glow)]',
              )}
            >
              {code[i] ?? ''}
            </span>
          ))}
        </span>
      </label>

      <p
        id="guest-code-error"
        role={error ? 'alert' : undefined}
        className={clsx('min-h-[1.5rem] text-base', error ? 'text-danger' : 'text-muted')}
      >
        {error ?? (busy ? t('lock.guestCode.signingIn') : '')}
      </p>

      <div className="grid w-[min(100%,18rem)] grid-cols-3 gap-2" role="group" aria-label={t('lock.guestCode.keypad')}>
        {KEYS.map((k) => (
          <Button key={k} variant="secondary" size="lg" disabled={busy} onClick={() => enter(code + k)}>
            <span className="tnum text-2xl">{k}</span>
          </Button>
        ))}
        <span aria-hidden="true" />
        <Button variant="secondary" size="lg" disabled={busy} onClick={() => enter(code + '0')}>
          <span className="tnum text-2xl">0</span>
        </Button>
        <Button
          variant="ghost"
          size="lg"
          iconOnly
          icon={EraseIcon}
          aria-label={t('lock.guestCode.erase')}
          disabled={busy || code.length === 0}
          onClick={() => enter(code.slice(0, -1))}
        />
      </div>
    </div>
  );
}
