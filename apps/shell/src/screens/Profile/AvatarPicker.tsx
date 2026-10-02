/**
 * The player picks a preset avatar (lib/avatars.ts): a grid of pictures, the current one marked; a pick is saved at
 * once through `profile_update` and shows everywhere the player appears (session bar, lock screen, leaderboards).
 */
import { useState } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { Modal } from '@/components/ui/Modal';
import { AVATAR_IDS, presetAvatarUrl, type AvatarId } from '@/lib/avatars';
import { api } from '@/lib/tauri';
import { useAuthStore, useNotificationsStore } from '@/store';

export interface AvatarPickerProps {
  open: boolean;
  onClose: () => void;
  /** The avatar the player has now (`user.avatarUrl`). */
  current: string | null | undefined;
}

export function AvatarPicker({ open, onClose, current }: AvatarPickerProps): JSX.Element {
  const { t } = useTranslation();
  const updateUser = useAuthStore((s) => s.updateUser);
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const [saving, setSaving] = useState<AvatarId | null>(null);

  const pick = async (id: AvatarId): Promise<void> => {
    const url = presetAvatarUrl(id);
    if (url === current) {
      onClose();
      return;
    }
    setSaving(id);
    try {
      const updated = await api.profile.update({ avatarUrl: url });
      updateUser({ avatarUrl: updated.avatarUrl ?? url });
      push({ title: t('profile.avatarSaved'), level: 'success', ttlSec: 3 });
      onClose();
    } catch (e) {
      pushError(e, t('profile.avatarTitle'));
    } finally {
      setSaving(null);
    }
  };

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={t('profile.avatarTitle')}
      description={t('profile.avatarHint')}
      size="lg"
      showClose
    >
      <ul role="list" className="grid grid-cols-6 gap-3">
        {AVATAR_IDS.map((id) => {
          const url = presetAvatarUrl(id);
          const active = url === current;
          return (
            <li key={id}>
              <button
                type="button"
                data-nav="true"
                aria-pressed={active}
                aria-label={t(`profile.avatars.${id}`)}
                disabled={saving !== null}
                onClick={() => void pick(id)}
                className={clsx(
                  'focus-ring group relative block aspect-square w-full overflow-hidden rounded-full transition-transform duration-[var(--dur-fast)] hover:scale-105 disabled:cursor-wait',
                  active ? 'ring-2 ring-primary ring-offset-2 ring-offset-bg' : 'ring-1 ring-text/10',
                  saving === id && 'animate-pulse',
                )}
              >
                <img src={url} alt="" draggable={false} className="h-full w-full object-cover" />
              </button>
            </li>
          );
        })}
      </ul>
    </Modal>
  );
}

export default AvatarPicker;
