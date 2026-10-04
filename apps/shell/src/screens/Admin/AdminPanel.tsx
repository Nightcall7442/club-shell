/**
 * The hidden administrator panel (exit hotkey from shell.json or `kiosk://hotkey exit`, mounted once by
 * `GlobalListeners`): admin PIN → reboot / shutdown / exit to the desktop / quit / devtools / reload. Errors surface as
 * toasts.
 */
import { useEffect, useId, useRef, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { Modal } from '@/components/ui/Modal';
import { useLocale } from '@/hooks/useLocale';
import { formatTime } from '@/lib/format';
import { api, toShellApiError } from '@/lib/tauri';
import { secondsUntil } from '@/lib/time';
import { describeError, useNotificationsStore, useSettingsStore } from '@/store';

const PIN_RE = /^\d{4,6}$/;

export interface AdminPanelProps {
  open: boolean;
  onClose: () => void;
}

interface AdminToken {
  token: string;
  expiresAt: string;
}

type AdminAction = 'reboot' | 'shutdown' | 'explorer' | 'quit' | 'devtools' | 'reload';

const POWER_DELAY_SEC = 30;

/** PIN prompt → admin actions. The token is short-lived; an expired one drops back to the PIN prompt. */
export function AdminPanel({ open, onClose }: AdminPanelProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const setScheduledPower = useNotificationsStore((s) => s.setScheduledPower);
  const devtoolsAllowed = useSettingsStore((s) => s.shellConfig?.devtools ?? s.kiosk?.devtools ?? false);
  const formId = useId();
  const pinRef = useRef<HTMLInputElement>(null);
  const [pin, setPin] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [checking, setChecking] = useState(false);
  const [admin, setAdmin] = useState<AdminToken | null>(null);
  const [busy, setBusy] = useState<AdminAction | null>(null);

  useEffect(() => {
    if (open) {
      setPin('');
      setError(null);
      setChecking(false);
      setBusy(null);
      setAdmin((a) => (a && Date.parse(a.expiresAt) > Date.now() ? a : null));
    }
  }, [open]);

  const unlock = async (e: FormEvent<HTMLFormElement>): Promise<void> => {
    e.preventDefault();
    if (!PIN_RE.test(pin)) {
      setError(t('lock.pinInvalid'));
      return;
    }
    setChecking(true);
    try {
      const res = await api.system.unlockAdmin(pin);
      setAdmin({ token: res.adminToken, expiresAt: res.expiresAt });
      setPin('');
    } catch (err) {
      const shell = toShellApiError(err);
      setError(shell.code === 'unauthorized' ? t('settings.adminInvalidPin') : describeError(shell));
      setPin('');
    } finally {
      setChecking(false);
    }
  };

  const run = async (action: AdminAction): Promise<void> => {
    if (!admin || busy) {
      return;
    }
    if (Date.parse(admin.expiresAt) <= Date.now()) {
      setAdmin(null);
      return;
    }
    setBusy(action);
    try {
      switch (action) {
        case 'reboot':
        case 'shutdown': {
          const res =
            action === 'reboot'
              ? await api.system.reboot(POWER_DELAY_SEC, 'admin')
              : await api.system.shutdown(POWER_DELAY_SEC, 'admin');
          const seconds = Math.max(0, Math.round(secondsUntil(res.scheduledAt) || POWER_DELAY_SEC));
          setScheduledPower({
            kind: action,
            at: Date.parse(res.scheduledAt) || Date.now() + seconds * 1000,
            message: null,
          });
          push({
            id: action,
            title: action === 'reboot' ? t('admin.rebootScheduled') : t('admin.shutdownScheduled'),
            body: action === 'reboot' ? t('admin.rebootIn', { seconds }) : t('admin.shutdownIn', { seconds }),
            level: 'warning',
            ttlSec: null,
            source: 'system',
          });
          onClose();
          break;
        }
        case 'explorer':
        case 'quit':
          await api.kiosk.exit(admin.token, action);
          onClose();
          break;
        case 'devtools':
          await api.kiosk.openDevtools();
          break;
        case 'reload':
          await api.kiosk.reload();
          break;
      }
    } catch (err) {
      pushError(err, t('settings.adminPanel'));
    } finally {
      setBusy(null);
    }
  };

  const actions: { key: AdminAction; label: string; variant: 'primary' | 'secondary' | 'danger'; hidden?: boolean }[] =
    [
      { key: 'reload', label: t('settings.reload'), variant: 'secondary' },
      { key: 'devtools', label: t('settings.devtools'), variant: 'secondary', hidden: !devtoolsAllowed },
      { key: 'explorer', label: t('settings.exitExplorer'), variant: 'primary' },
      { key: 'quit', label: t('settings.exitQuit'), variant: 'primary' },
      { key: 'reboot', label: t('settings.reboot'), variant: 'danger' },
      { key: 'shutdown', label: t('settings.shutdown'), variant: 'danger' },
    ];

  if (!admin) {
    return (
      <Modal
        open={open}
        onClose={onClose}
        title={t('kiosk.exitTitle')}
        description={t('kiosk.exitHint')}
        size="sm"
        initialFocusRef={pinRef}
        footer={
          <>
            <Button variant="ghost" size="lg" onClick={onClose} disabled={checking}>
              {t('common.cancel')}
            </Button>
            <Button type="submit" form={formId} size="lg" loading={checking}>
              {t('settings.adminUnlock')}
            </Button>
          </>
        }
      >
        <form id={formId} onSubmit={(e) => void unlock(e)} noValidate>
          <Input
            ref={pinRef}
            label={t('settings.adminPin')}
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
            error={error}
          />
        </form>
      </Modal>
    );
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={t('settings.adminPanel')}
      description={t('settings.adminUnlocked', { time: formatTime(admin.expiresAt, locale) })}
      size="md"
      footer={
        <Button variant="ghost" size="lg" onClick={onClose} disabled={busy !== null}>
          {t('common.close')}
        </Button>
      }
    >
      <div className="grid grid-cols-2 gap-3">
        {actions
          .filter((a) => !a.hidden)
          .map((a) => (
            <Button
              key={a.key}
              variant={a.variant}
              size="lg"
              block
              loading={busy === a.key}
              disabled={busy !== null && busy !== a.key}
              onClick={() => void run(a.key)}
            >
              {a.label}
            </Button>
          ))}
      </div>
    </Modal>
  );
}
