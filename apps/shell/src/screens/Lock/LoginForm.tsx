import { useState, type FormEvent, type KeyboardEvent } from 'react';
import clsx from 'clsx';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import type { AuthLoginResponse } from '@clubshell/contracts';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { isTauri, toShellApiError } from '@/lib/tauri';
import { useAuthStore } from '@/store/auth';
import { describeError } from '@/store/notifications';

export interface LoginFormProps {
  onSuccess?: (res: AuthLoginResponse) => void;
  className?: string;
}

/** Human message for a failed password / PIN check; anything unexpected falls back to `describeError`. */
export function loginErrorMessage(e: unknown, t: TFunction): string {
  const err = toShellApiError(e);
  const details = (typeof err.details === 'object' && err.details !== null ? err.details : {}) as Record<
    string,
    unknown
  >;
  switch (err.code) {
    case 'unauthorized':
      if (details['reason'] === 'wrongPin') {
        return t('lock.wrongPin');
      }
      if (details['reason'] === 'wrongPassword') {
        return t('lock.wrongPassword');
      }
      return t('lock.invalidCredentials');
    case 'forbidden':
      return t('lock.banned');
    case 'rateLimited':
      return t('lock.tooManyAttempts');
    case 'validation':
      if (details['field'] === 'username') {
        return t('lock.usernameRequired');
      }
      if (details['field'] === 'password') {
        return t('lock.passwordRequired');
      }
      if (details['field'] === 'pin') {
        return t('lock.pinRequired');
      }
      return describeError(err);
    default:
      return describeError(err);
  }
}

const EyeIcon = ({ off }: { off: boolean }): JSX.Element => (
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
    <path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z" />
    <circle cx="12" cy="12" r="3" />
    {off && <path d="M4 4l16 16" />}
  </svg>
);

/** Username + password login; submits on Enter, maps server errors to i18n, warns about Caps Lock. */
export function LoginForm({ onSuccess, className }: LoginFormProps): JSX.Element {
  const { t } = useTranslation();
  const login = useAuthStore((s) => s.login);
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [show, setShow] = useState(false);
  const [caps, setCaps] = useState(false);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<{ username?: string; password?: string; form?: string }>({});

  const onKey = (e: KeyboardEvent<HTMLInputElement>): void => {
    setCaps(e.getModifierState('CapsLock'));
  };

  const submit = async (e: FormEvent<HTMLFormElement>): Promise<void> => {
    e.preventDefault();
    const name = username.trim();
    const next: typeof errors = {};
    if (name.length === 0) {
      next.username = t('lock.usernameRequired');
    }
    if (password.length === 0) {
      next.password = t('lock.passwordRequired');
    }
    setErrors(next);
    if (next.username || next.password) {
      return;
    }
    setBusy(true);
    try {
      const res = await login('password', { username: name, password });
      setPassword('');
      onSuccess?.(res);
    } catch (err) {
      setErrors({ form: loginErrorMessage(err, t) });
      setPassword('');
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} noValidate className={clsx('flex flex-col gap-5', className)}>
      <Input
        name="username"
        size="lg"
        autoComplete="username"
        autoCapitalize="none"
        spellCheck={false}
        label={t('lock.username')}
        placeholder={t('lock.usernamePlaceholder')}
        value={username}
        onChange={(e) => setUsername(e.target.value)}
        error={errors.username}
        hint={isTauri() ? undefined : t('lock.demoHint')}
        disabled={busy}
      />
      <Input
        name="password"
        size="lg"
        type={show ? 'text' : 'password'}
        autoComplete="current-password"
        label={t('lock.password')}
        placeholder={t('lock.passwordPlaceholder')}
        value={password}
        onChange={(e) => setPassword(e.target.value)}
        onKeyDown={onKey}
        onKeyUp={onKey}
        error={errors.password}
        hint={caps ? <span className="text-accent">{t('lock.capsLockOn')}</span> : undefined}
        disabled={busy}
        trailing={
          <button
            type="button"
            data-nav="true"
            aria-label={show ? t('lock.hidePassword') : t('lock.showPassword')}
            aria-pressed={show}
            onClick={() => setShow((v) => !v)}
            className="focus-ring inline-flex h-10 w-10 items-center justify-center rounded-md text-muted hover:text-text"
          >
            <EyeIcon off={show} />
          </button>
        }
      />
      {errors.form && (
        <p role="alert" className="rounded-md bg-danger/15 px-4 py-3 text-base font-medium text-danger">
          {errors.form}
        </p>
      )}
      <Button type="submit" variant="cta" size="xl" block loading={busy}>
        {busy ? t('lock.loggingIn') : t('lock.login')}
      </Button>
      <p className="text-center text-sm text-muted">
        {t('lock.noAccount')} {t('lock.askStaff')}
      </p>
    </form>
  );
}

export default LoginForm;
