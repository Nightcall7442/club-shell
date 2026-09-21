import { useId, useState, type FormEvent } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import type { AuthLoginResponse } from '@clubshell/contracts';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { useAuthStore } from '@/store/auth';
import { describeError } from '@/store/notifications';

export interface GuestLoginProps {
  onSuccess?: (res: AuthLoginResponse) => void;
  className?: string;
}

const NAME_MAX = 32;

/** Guest sign-in: optional display name, mandatory rules checkbox, postpaid time. */
export function GuestLogin({ onSuccess, className }: GuestLoginProps): JSX.Element {
  const { t } = useTranslation();
  const loginGuest = useAuthStore((s) => s.loginGuest);
  const termsId = useId();
  const [name, setName] = useState('');
  const [accepted, setAccepted] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async (e: FormEvent<HTMLFormElement>): Promise<void> => {
    e.preventDefault();
    if (!accepted || busy) {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const trimmed = name.trim().slice(0, NAME_MAX);
      const res = await loginGuest(trimmed.length > 0 ? trimmed : undefined);
      onSuccess?.(res);
    } catch (err) {
      setError(describeError(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} noValidate className={clsx('flex flex-col gap-5', className)}>
      <p className="text-base text-muted">{t('lock.guestHint')}</p>
      <Input
        name="displayName"
        size="lg"
        autoComplete="nickname"
        maxLength={NAME_MAX}
        label={t('lock.guestName')}
        placeholder={t('lock.guestNamePlaceholder')}
        value={name}
        onChange={(e) => setName(e.target.value)}
        disabled={busy}
      />
      <label htmlFor={termsId} className="flex cursor-pointer select-none items-center gap-3 text-base text-text">
        <input
          id={termsId}
          type="checkbox"
          data-nav="true"
          checked={accepted}
          onChange={(e) => setAccepted(e.target.checked)}
          disabled={busy}
          className="focus-ring h-6 w-6 shrink-0 cursor-pointer rounded-sm accent-primary"
        />
        <span>{t('lock.guestTerms')}</span>
      </label>
      {error && (
        <p role="alert" className="rounded-md bg-danger/15 px-4 py-3 text-base font-medium text-danger">
          {error}
        </p>
      )}
      <Button type="submit" size="xl" block loading={busy} disabled={!accepted}>
        {busy ? t('lock.loggingIn') : t('lock.guest')}
      </Button>
    </form>
  );
}

export default GuestLogin;
