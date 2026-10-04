/**
 * Account tab of the profile (`?tab=settings`): change the session-unlock PIN (not for guests) and log out. The PC's
 * own settings — sound, mouse, monitor, theme — live in the PC block on Home; a line here points there.
 */
import { useEffect, useId, useRef, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { SettingsSection } from '@/components/settings/SettingsSection';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { Modal } from '@/components/ui/Modal';
import { api, toShellApiError } from '@/lib/tauri';
import { THIS_PC_STATE } from '@/screens/Pc/PcBadge';
import { useAuthStore, useNotificationsStore } from '@/store';

// ---------------------------------------------------------------------------------------------------------------------
// PIN
// ---------------------------------------------------------------------------------------------------------------------

const PIN_RE = /^\d{4,6}$/;

export interface PinModalProps {
  open: boolean;
  onClose: () => void;
}

/** Sets / changes the session-unlock PIN (`profile_update { pin }`). */
export function PinModal({ open, onClose }: PinModalProps): JSX.Element {
  const { t } = useTranslation();
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const formId = useId();
  const firstRef = useRef<HTMLInputElement>(null);
  const [pin, setPin] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (open) {
      setPin('');
      setConfirm('');
      setError(null);
      setSaving(false);
    }
  }, [open]);

  const submit = async (e: FormEvent<HTMLFormElement>): Promise<void> => {
    e.preventDefault();
    if (!PIN_RE.test(pin)) {
      setError(t('lock.pinInvalid'));
      return;
    }
    if (pin !== confirm) {
      setError(t('profile.pinMismatch'));
      return;
    }
    setSaving(true);
    try {
      await api.profile.update({ pin });
      push({ title: t('profile.pinSaved'), level: 'success', ttlSec: 4 });
      onClose();
    } catch (err) {
      const shell = toShellApiError(err);
      if (shell.code === 'validation') {
        setError(t('lock.pinInvalid'));
      } else {
        pushError(err, t('profile.changePin'));
      }
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={t('profile.changePin')}
      description={t('profile.pinHint')}
      size="sm"
      initialFocusRef={firstRef}
      footer={
        <>
          <Button variant="ghost" size="lg" onClick={onClose} disabled={saving}>
            {t('common.cancel')}
          </Button>
          <Button type="submit" form={formId} size="lg" loading={saving}>
            {t('profile.save')}
          </Button>
        </>
      }
    >
      <form id={formId} onSubmit={(e) => void submit(e)} className="flex flex-col gap-4" noValidate>
        <Input
          ref={firstRef}
          label={t('profile.newPin')}
          type="password"
          inputMode="numeric"
          autoComplete="off"
          maxLength={6}
          size="lg"
          value={pin}
          onChange={(e) => {
            setPin(e.target.value.replace(/\D/g, ''));
            setError(null);
          }}
          placeholder={t('lock.pinPlaceholder')}
        />
        <Input
          label={t('profile.confirmPin')}
          type="password"
          inputMode="numeric"
          autoComplete="off"
          maxLength={6}
          size="lg"
          value={confirm}
          onChange={(e) => {
            setConfirm(e.target.value.replace(/\D/g, ''));
            setError(null);
          }}
          placeholder={t('lock.pinPlaceholder')}
          error={error}
        />
      </form>
    </Modal>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Account tab
// ---------------------------------------------------------------------------------------------------------------------

export function Account(): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const user = useAuthStore((s) => s.user);
  const logout = useAuthStore((s) => s.logout);
  const [pinOpen, setPinOpen] = useState(false);
  const [logoutOpen, setLogoutOpen] = useState(false);
  const [loggingOut, setLoggingOut] = useState(false);

  const confirmLogout = async (): Promise<void> => {
    setLoggingOut(true);
    await logout('user');
    setLoggingOut(false);
    setLogoutOpen(false);
    navigate('/lock', { replace: true });
  };

  const isGuest = user?.role === 'guest';

  return (
    <div className="flex flex-col gap-[var(--gap)]">
      <div className="grid items-start gap-[var(--gap)] xl:grid-cols-2">
        <SettingsSection
          title={t('profile.account')}
          description={isGuest ? t('profile.guestHint') : t('profile.pinHint')}
        >
          <div className="flex flex-wrap gap-3">
            {!isGuest && (
              <Button variant="secondary" size="lg" onClick={() => setPinOpen(true)}>
                {t('profile.changePin')}
              </Button>
            )}
            <Button variant="danger" size="lg" onClick={() => setLogoutOpen(true)}>
              {t('profile.logout')}
            </Button>
          </div>
        </SettingsSection>

        {/* Players used to find the PC's settings here: say where they went. */}
        <div className="glass flex flex-wrap items-center justify-between gap-4 rounded-xl p-[var(--gap)]">
          <p className="min-w-0 flex-1 text-base text-muted">{t('profile.pcMoved')}</p>
          <Button variant="secondary" size="lg" onClick={() => navigate('/home', { state: THIS_PC_STATE })}>
            {t('profile.openPcSettings')}
          </Button>
        </div>
      </div>

      <PinModal open={pinOpen} onClose={() => setPinOpen(false)} />

      <Modal
        open={logoutOpen}
        onClose={() => !loggingOut && setLogoutOpen(false)}
        title={t('profile.logoutTitle')}
        description={t('profile.logoutConfirm')}
        size="sm"
        danger
        footer={
          <>
            <Button variant="ghost" size="lg" onClick={() => setLogoutOpen(false)} disabled={loggingOut}>
              {t('common.cancel')}
            </Button>
            <Button variant="danger" size="lg" loading={loggingOut} onClick={() => void confirmLogout()}>
              {t('profile.logout')}
            </Button>
          </>
        }
      />
    </div>
  );
}

export default Account;
