/**
 * «Вход для персонала» on the lock screen: a staff member the owner allowed to service PCs types their PIN from the admin
 * console, and the Agent signs the kiosk out of Windows so they can sign in to their own Windows account — to install
 * games, launchers and programs with an administrator's rights (`sys.staffSignIn`). The PIN is checked on the server;
 * the kiosk comes back when they sign out of Windows, when the time runs out, after a restart, or when nobody signs in.
 */
import { useEffect, useId, useRef, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { Modal } from '@/components/ui/Modal';
import { useLocale } from '@/hooks/useLocale';
import { errorDetails, errorReason } from '@/lib/errorText';
import { formatTime } from '@/lib/format';
import { api, toShellApiError } from '@/lib/tauri';
import { describeError } from '@/store';

const PIN_RE = /^\d{4,8}$/;

interface Done {
  name: string;
  until: string;
  minutes: number;
}

export function StaffSignInButton(): JSX.Element {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button variant="ghost" size="md" onClick={() => setOpen(true)}>
        {t('lock.staff.button')}
      </Button>
      <StaffSignInDialog open={open} onClose={() => setOpen(false)} />
    </>
  );
}

function StaffSignInDialog({ open, onClose }: { open: boolean; onClose: () => void }): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const formId = useId();
  const pinRef = useRef<HTMLInputElement>(null);
  const [pin, setPin] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [checking, setChecking] = useState(false);
  const [done, setDone] = useState<Done | null>(null);

  useEffect(() => {
    if (open) {
      setPin('');
      setError(null);
      setChecking(false);
      setDone(null);
    }
  }, [open]);

  const refusal = (err: unknown): string => {
    const e = toShellApiError(err);
    const reason = errorReason(e.details);
    switch (e.code) {
      case 'unauthorized':
        return t('lock.staff.wrongPin');
      case 'forbidden':
        return reason === 'pcService' ? t('lock.staff.notAllowed') : describeError(e);
      case 'conflict':
        if (reason === 'sessionActive') return t('lock.staff.sessionActive');
        if (reason === 'kioskOff') return t('lock.staff.kioskOff');
        return describeError(e);
      case 'agentOffline':
      case 'serverUnavailable':
      case 'timeout':
        return t('lock.staff.offline');
      case 'rateLimited': {
        const seconds = errorDetails(e.details)['retryAfterSec'];
        return t('lock.staff.tooMany', { seconds: typeof seconds === 'number' ? seconds : 60 });
      }
      case 'validation':
        return t('lock.staff.pinFormat');
      default:
        return describeError(e);
    }
  };

  const submit = async (e: FormEvent<HTMLFormElement>): Promise<void> => {
    e.preventDefault();
    if (!PIN_RE.test(pin)) {
      setError(t('lock.staff.pinFormat'));
      return;
    }
    setChecking(true);
    try {
      const res = await api.system.staffSignIn(pin);
      setDone({ name: res.staffName, until: res.until, minutes: Math.max(1, Math.round(res.signInWithinSec / 60)) });
    } catch (err) {
      setError(refusal(err));
      setPin('');
      pinRef.current?.focus();
    } finally {
      setChecking(false);
    }
  };

  if (done) {
    // The Agent signs the kiosk out in a moment; until then this is what the staff member reads.
    return (
      <Modal
        open={open}
        onClose={() => undefined}
        closeOnBackdrop={false}
        closeOnEscape={false}
        showClose={false}
        size="sm"
        title={t('lock.staff.doneTitle', { name: done.name })}
      >
        <div role="status" className="space-y-3 text-base text-muted">
          <p>{t('lock.staff.doneBody', { minutes: done.minutes })}</p>
          <p>{t('lock.staff.doneBack', { time: formatTime(done.until, locale) })}</p>
        </div>
      </Modal>
    );
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={t('lock.staff.title')}
      description={t('lock.staff.hint')}
      size="sm"
      initialFocusRef={pinRef}
      footer={
        <>
          <Button variant="ghost" size="lg" onClick={onClose} disabled={checking}>
            {t('common.cancel')}
          </Button>
          <Button type="submit" form={formId} size="lg" loading={checking}>
            {t('lock.staff.submit')}
          </Button>
        </>
      }
    >
      <form id={formId} onSubmit={(e) => void submit(e)} noValidate>
        <Input
          ref={pinRef}
          label={t('lock.staff.pin')}
          type="password"
          inputMode="numeric"
          autoComplete="off"
          maxLength={8}
          size="lg"
          value={pin}
          onChange={(e) => {
            setPin(e.target.value.replace(/\D/g, ''));
            setError(null);
          }}
          placeholder={t('lock.staff.pinPlaceholder')}
          error={error}
        />
      </form>
    </Modal>
  );
}
