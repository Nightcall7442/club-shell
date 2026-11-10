/**
 * A sign-out the Agent refused (`auth.logout` failed): the player is still signed in and the session still runs, so the
 * screen says it and offers to try again instead of showing the lock screen as if they had left. One dialog for every
 * way out (the leave button, the idle prompt, the profile, the tariff picker); rendered at the router root.
 */
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { useAuthStore } from '@/store/auth';
import { describeError } from '@/store/notifications';

export function LogoutFailedDialog(): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const failure = useAuthStore((s) => s.logoutFailure);
  const retry = useAuthStore((s) => s.retryLogout);
  const dismiss = useAuthStore((s) => s.dismissLogoutFailure);
  const [busy, setBusy] = useState(false);

  const again = async (): Promise<void> => {
    setBusy(true);
    try {
      if (await retry()) {
        navigate('/lock', { replace: true });
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal
      open={failure !== null}
      onClose={() => !busy && dismiss()}
      title={t('session.logoutFailed.title')}
      description={failure ? t('session.logoutFailed.body', { error: describeError(failure.error) }) : undefined}
      size="sm"
      danger
      footer={
        <>
          <Button variant="ghost" size="lg" onClick={dismiss} disabled={busy}>
            {t('common.close')}
          </Button>
          <Button variant="danger" size="lg" loading={busy} onClick={() => void again()}>
            {t('session.logoutFailed.retry')}
          </Button>
        </>
      }
    />
  );
}

export default LogoutFailedDialog;
