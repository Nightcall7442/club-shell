import { useCallback, useState } from 'react';
import clsx from 'clsx';
import { AnimatePresence, motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import type { NotificationAction } from '@clubshell/contracts';
import { Badge, levelTone } from '@/components/ui/Badge';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { ToastViewport } from '@/components/ui/Toast';
import { api } from '@/lib/tauri';
import { log } from '@/lib/logger';
import { selectCurrentAdminMessage, useNotificationsStore } from '@/store/notifications';
import { useThemeStore } from '@/store/theme';

/**
 * Overlay layer: toasts (incl. the store's session-time warnings), must-acknowledge staff messages,
 * the remote-control indicator and the update-ready banner.
 */
export function NotificationCenter(): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const animations = useThemeStore((s) => s.theme.animations);
  const toasts = useNotificationsStore((s) => s.toasts);
  const dismiss = useNotificationsStore((s) => s.dismiss);
  const push = useNotificationsStore((s) => s.push);
  const adminMessage = useNotificationsStore(selectCurrentAdminMessage);
  const ackAdmin = useNotificationsStore((s) => s.ackAdmin);
  const remoteControl = useNotificationsStore((s) => s.remoteControl);
  const updateReady = useNotificationsStore((s) => s.updateReady);

  const [acking, setAcking] = useState(false);
  const [applying, setApplying] = useState(false);
  const [updateDismissed, setUpdateDismissed] = useState<string | null>(null);

  const remoteActive = remoteControl?.state === 'started' && remoteControl.showIndicator;
  const updateKey = updateReady ? `${updateReady.component}@${updateReady.version}` : null;
  const showUpdate = updateReady !== null && (updateReady.mandatory || updateDismissed !== updateKey);
  const duration = animations ? 0.2 : 0;

  /** Route actions navigate; IPC-command actions are not executable from the UI layer. */
  const onToastAction = useCallback(
    (action: NotificationAction): void => {
      if (action.command.startsWith('/')) {
        navigate(action.command);
      } else {
        log.debug('toast action ignored (not a route)', action.command);
      }
    },
    [navigate],
  );

  const onAck = async (): Promise<void> => {
    if (!adminMessage) {
      return;
    }
    setAcking(true);
    try {
      await ackAdmin(adminMessage.id);
    } finally {
      setAcking(false);
    }
  };

  const onApplyUpdate = async (): Promise<void> => {
    if (!updateReady) {
      return;
    }
    setApplying(true);
    try {
      await api.system.updateApply(updateReady.component);
    } catch (e) {
      log.error('update apply failed', e);
      push({ title: t('update.failed'), body: t('update.failedHint'), level: 'error', source: 'system' });
      setApplying(false);
    }
  };

  return (
    <>
      <ToastViewport items={toasts} onDismiss={dismiss} onAction={onToastAction} />

      {/* Remote-control indicator (bottom centre). */}
      <AnimatePresence>
        {remoteActive && (
          <motion.div
            key="remote"
            role="status"
            className="pointer-events-none fixed bottom-[var(--gap)] left-1/2 z-[85] -translate-x-1/2"
            initial={{ opacity: 0, y: 12 }}
            animate={{ opacity: 1, y: 0 }}
            exit={{ opacity: 0, y: 12 }}
            transition={{ duration, ease: 'easeOut' }}
          >
            <Badge tone="danger" size="lg" live className="glass-strong normal-case tracking-normal">
              {remoteControl?.adminName ? t('admin.remoteControlBy', { admin: remoteControl.adminName }) : t('admin.remoteControlActive')}
            </Badge>
          </motion.div>
        )}
      </AnimatePresence>

      {/* Update-ready banner (bottom right). */}
      <AnimatePresence>
        {showUpdate && updateReady && (
          <motion.div
            key="update"
            role="status"
            className="glass-strong pointer-events-auto fixed bottom-[var(--gap)] right-[var(--gutter)] z-[85] flex w-[min(92vw,30rem)] items-start gap-4 rounded-xl p-4"
            initial={{ opacity: 0, x: 24 }}
            animate={{ opacity: 1, x: 0 }}
            exit={{ opacity: 0, x: 24 }}
            transition={{ duration, ease: 'easeOut' }}
          >
            <span className="mt-0.5 inline-flex h-7 w-7 shrink-0 items-center justify-center text-primary">
              <svg viewBox="0 0 24 24" className="h-full w-full" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                <path d="M12 3v12M7 10l5 5 5-5M4 19h16" />
              </svg>
            </span>
            <div className="min-w-0 flex-1">
              <p className="text-base font-bold leading-tight text-text">{t('update.readyTitle')}</p>
              <p className="mt-1 text-sm text-muted">
                {t('update.readyHint', { component: t(`update.component.${updateReady.component}`), version: updateReady.version })}
              </p>
              {updateReady.mandatory && <p className="mt-1 text-sm font-semibold text-danger">{t('update.mandatory')}</p>}
              <div className="mt-3 flex flex-wrap gap-2">
                <Button size="md" loading={applying} onClick={() => void onApplyUpdate()}>
                  {applying ? t('update.applying') : t('update.applyNow')}
                </Button>
                {!updateReady.mandatory && (
                  <Button size="md" variant="ghost" disabled={applying} onClick={() => setUpdateDismissed(updateKey)}>
                    {t('update.later')}
                  </Button>
                )}
              </div>
            </div>
          </motion.div>
        )}
      </AnimatePresence>

      {/* Staff message requiring an explicit acknowledgement (the store toasts the others). */}
      <Modal
        open={adminMessage !== null}
        onClose={() => undefined}
        closeOnBackdrop={false}
        closeOnEscape={false}
        showClose={false}
        title={adminMessage ? t('admin.messageTitle', { from: adminMessage.from }) : t('admin.message')}
        size="md"
        danger={adminMessage?.level === 'error'}
        footer={
          <Button size="lg" loading={acking} onClick={() => void onAck()}>
            {t('admin.acknowledge')}
          </Button>
        }
      >
        {adminMessage && (
          <div className="flex flex-col gap-3">
            <Badge tone={levelTone(adminMessage.level)} dot>
              {t(`notifications.${adminMessage.level}`)}
            </Badge>
            <p className={clsx('whitespace-pre-wrap text-lg leading-relaxed text-text', adminMessage.level === 'error' && 'font-semibold')}>
              {adminMessage.text}
            </p>
          </div>
        )}
      </Modal>
    </>
  );
}
