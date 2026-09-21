/**
 * "Call administrator": a button that opens {@link CallAdminModal} — category chips (help / technical / order /
 * other), an optional message and a confirmation → `sys_call_admin`. Success shows the ticket and queue position
 * as a toast; errors (incl. the 30 s rate limit) go through `pushError` so codes never reach the screen raw.
 */
import { useEffect, useRef, useState } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { CallAdminCategory, type CallAdminCategory as CallAdminCategoryType } from '@clubshell/contracts';
import { Button, type ButtonProps } from '@/components/ui/Button';
import { Input } from '@/components/ui/Input';
import { Modal } from '@/components/ui/Modal';
import { track } from '@/lib/analytics';
import { api } from '@/lib/tauri';
import { useNotificationsStore } from '@/store/notifications';
import { selectFeature, useSettingsStore } from '@/store/settings';

/** Categories in display order. */
export const CALL_ADMIN_CATEGORIES: readonly CallAdminCategoryType[] = [
  CallAdminCategory.Help,
  CallAdminCategory.Technical,
  CallAdminCategory.Order,
  CallAdminCategory.Other,
];

const MESSAGE_MAX = 200;

const IconBell = (): JSX.Element => (
  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <path d="M6 16V11a6 6 0 0 1 12 0v5l1.5 2h-15L6 16Z" />
    <path d="M10 20a2 2 0 0 0 4 0" />
  </svg>
);

// ---------------------------------------------------------------------------------------------------------------------
// Modal
// ---------------------------------------------------------------------------------------------------------------------

export interface CallAdminModalProps {
  open: boolean;
  onClose: () => void;
  initialCategory?: CallAdminCategoryType;
}

export function CallAdminModal({ open, onClose, initialCategory = CallAdminCategory.Help }: CallAdminModalProps): JSX.Element {
  const { t } = useTranslation();
  const pcName = useSettingsStore((s) => s.pcInfo?.pc.name ?? t('desktop.pc'));
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [category, setCategory] = useState<CallAdminCategoryType>(initialCategory);
  const [message, setMessage] = useState('');
  const [sending, setSending] = useState(false);
  const confirmRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (open) {
      setCategory(initialCategory);
      setMessage('');
      setSending(false);
    }
  }, [open, initialCategory]);

  const submit = async (): Promise<void> => {
    if (sending) {
      return;
    }
    setSending(true);
    track('support.callAdmin', { category });
    try {
      const res = await api.system.callAdmin(category, message.trim().length > 0 ? message.trim() : undefined);
      const lines = [t('support.calledHint', { pc: pcName }), t('support.ticket', { id: res.ticketId.slice(0, 8).toUpperCase() })];
      if (res.queuePosition != null) {
        lines.push(t('support.queuePosition', { position: res.queuePosition }));
      }
      push({ id: 'call-admin', title: t('support.called'), body: lines.join(' · '), level: 'success', source: 'local' });
      onClose();
    } catch (e) {
      pushError(e, t('support.callAdmin'));
    } finally {
      setSending(false);
    }
  };

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={t('support.callAdmin')}
      description={t('support.callHint')}
      size="md"
      initialFocusRef={confirmRef}
      footer={
        <>
          <Button variant="ghost" size="lg" onClick={onClose} disabled={sending}>
            {t('common.cancel')}
          </Button>
          <Button ref={confirmRef} size="lg" icon={<IconBell />} loading={sending} onClick={() => void submit()}>
            {sending ? t('support.calling') : t('support.callAdmin')}
          </Button>
        </>
      }
    >
      <form
        className="flex flex-col gap-5"
        onSubmit={(e) => {
          e.preventDefault();
          void submit();
        }}
      >
        <div role="radiogroup" aria-label={t('common.category')} className="grid grid-cols-2 gap-3">
          {CALL_ADMIN_CATEGORIES.map((c) => {
            const active = c === category;
            return (
              <button
                key={c}
                type="button"
                role="radio"
                aria-checked={active}
                data-nav="true"
                onClick={() => setCategory(c)}
                className={clsx(
                  'focus-ring glass rounded-lg px-4 py-3 text-left text-base font-semibold transition-colors duration-[var(--dur-fast)]',
                  active ? 'border-glow bg-primary/15 text-text' : 'text-muted hover:bg-surface/80 hover:text-text',
                )}
              >
                {t(`support.category.${c}`)}
              </button>
            );
          })}
        </div>
        <Input
          label={`${t('support.message')} (${t('common.optional').toLowerCase()})`}
          placeholder={t('support.messagePlaceholder')}
          value={message}
          maxLength={MESSAGE_MAX}
          onChange={(e) => setMessage(e.currentTarget.value)}
          hint={`${message.length} / ${MESSAGE_MAX}`}
          autoComplete="off"
        />
        <p className="text-base text-text">{t('support.confirmCall', { pc: pcName })}</p>
      </form>
    </Modal>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Button
// ---------------------------------------------------------------------------------------------------------------------

export interface CallAdminButtonProps extends Pick<ButtonProps, 'variant' | 'size' | 'block' | 'className'> {
  /** Preselected category. */
  category?: CallAdminCategoryType;
  /** Custom label (default `support.callAdmin`). */
  label?: string;
}

/** Renders nothing when the `callAdmin` feature is off. */
export function CallAdminButton({ category, label, variant = 'primary', size = 'lg', block, className }: CallAdminButtonProps): JSX.Element | null {
  const { t } = useTranslation();
  const enabled = useSettingsStore(selectFeature('callAdmin'));
  const [open, setOpen] = useState(false);
  if (!enabled) {
    return null;
  }
  return (
    <>
      <Button variant={variant} size={size} block={block} className={className} icon={<IconBell />} onClick={() => setOpen(true)}>
        {label ?? t('support.callAdmin')}
      </Button>
      <CallAdminModal open={open} onClose={() => setOpen(false)} initialCategory={category} />
    </>
  );
}
