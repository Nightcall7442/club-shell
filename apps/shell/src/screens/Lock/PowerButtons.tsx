/**
 * «Выключить» and «Перезагрузить» on the lock screen (SHELL_CHANGES 93): without a session and without the desk's lock,
 * the PC can be switched off or restarted without the administrator's PIN — a PC stuck on «Подготовка ПК…» too. Each asks
 * first. The Agent refuses during a session (`conflict sessionActive`) or where its local `power.allowShellReboot` is off
 * (`policyDenied`); the refusal is said, never swallowed. The club turns the buttons off with `features.lockScreenPower`.
 */
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { errorReason } from '@/lib/errorText';
import { api, toShellApiError } from '@/lib/tauri';
import { describeError, useNotificationsStore } from '@/store';

type PowerKind = 'shutdown' | 'reboot';

/** Seconds the Agent waits before the power action: the answer and the toast get to the screen first. */
const POWER_DELAY_SEC = 3;

export function PowerButtons(): JSX.Element {
  const { t } = useTranslation();
  const push = useNotificationsStore((s) => s.push);
  const [asking, setAsking] = useState<PowerKind | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refusal = (err: unknown): string => {
    const e = toShellApiError(err);
    if (e.code === 'conflict' && errorReason(e.details) === 'sessionActive') return t('lock.power.sessionActive');
    if (e.code === 'policyDenied') return t('lock.power.denied');
    return describeError(e);
  };

  const run = async (kind: PowerKind): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      if (kind === 'shutdown') {
        await api.system.shutdown(POWER_DELAY_SEC, 'lockScreen');
      } else {
        await api.system.reboot(POWER_DELAY_SEC, 'lockScreen');
      }
      setAsking(null);
      push({
        id: `lock-${kind}`,
        title: kind === 'shutdown' ? t('lock.power.shuttingDown') : t('lock.power.rebooting'),
        level: 'info',
        ttlSec: null,
        source: 'system',
      });
    } catch (e) {
      setError(refusal(e));
    } finally {
      setBusy(false);
    }
  };

  const open = (kind: PowerKind): void => {
    setError(null);
    setAsking(kind);
  };

  return (
    <>
      <Button variant="ghost" size="lg" onClick={() => open('reboot')}>
        {t('lock.power.reboot')}
      </Button>
      <Button variant="ghost" size="lg" onClick={() => open('shutdown')}>
        {t('lock.power.shutdown')}
      </Button>
      <Modal
        open={asking !== null}
        onClose={() => (busy ? undefined : setAsking(null))}
        size="sm"
        title={asking === 'reboot' ? t('lock.power.confirmReboot') : t('lock.power.confirmShutdown')}
        footer={
          <>
            <Button variant="ghost" size="lg" disabled={busy} onClick={() => setAsking(null)}>
              {t('common.cancel')}
            </Button>
            <Button variant="danger" size="lg" loading={busy} onClick={() => asking && void run(asking)}>
              {t('lock.power.yes')}
            </Button>
          </>
        }
      >
        {error && (
          <p role="alert" className="text-base font-medium text-danger">
            {error}
          </p>
        )}
      </Modal>
    </>
  );
}
