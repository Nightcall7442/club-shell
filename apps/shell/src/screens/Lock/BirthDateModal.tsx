/**
 * «Укажите дату рождения» (D-107): a signed-in member whose account has no date of birth is asked once, before any time
 * is bought — the server refuses the purchase without it (`birthDateRequired`). The date is saved with `profile.update
 * {birthDate}` and cannot be changed from the PC afterwards (only the club's desk does), so the minors' curfew (D-108)
 * cannot be edited around; the modal says so. «Выйти» signs out instead.
 */
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { BirthDatePicker, EMPTY_BIRTH_DATE, birthDateOf, type BirthDateParts } from '@/components/BirthDatePicker';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { birthDateProblem, birthDateProblemOf, isoDay } from '@/lib/birthDate';
import { errorDetails, errorReason } from '@/lib/errorText';
import { api, toShellApiError } from '@/lib/tauri';
import { useAuthStore } from '@/store/auth';
import { describeError } from '@/store/notifications';

export interface BirthDateModalProps {
  open: boolean;
  /** Sign out instead (the lock screen's picker); absent — the modal can be closed (the wallet). */
  onLogout?: () => void;
  onClose?: () => void;
}

export function BirthDateModal({ open, onLogout, onClose }: BirthDateModalProps): JSX.Element {
  const { t } = useTranslation();
  const updateUser = useAuthStore((s) => s.updateUser);
  const [parts, setParts] = useState<BirthDateParts>(EMPTY_BIRTH_DATE);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const save = async (): Promise<void> => {
    const birthDate = birthDateOf(parts);
    const problem = birthDateProblem(birthDate, isoDay(new Date()));
    if (problem) {
      setError(t(`birthDate.problem.${problem}`));
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const updated = await api.profile.update({ birthDate });
      updateUser({ birthDate: updated.birthDate ?? birthDate });
    } catch (e) {
      const err = toShellApiError(e);
      if (err.code === 'validation' && errorDetails(err.details)['field'] === 'birthDate') {
        setError(t(`birthDate.problem.${birthDateProblemOf(errorReason(err.details))}`));
      } else if (err.code === 'forbidden' && errorReason(err.details) === 'birthDateLocked') {
        setError(t('birthDate.locked'));
      } else {
        setError(describeError(e));
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal
      open={open}
      onClose={() => onClose?.()}
      closeOnBackdrop={false}
      closeOnEscape={!onLogout}
      showClose={!onLogout}
      size="md"
      title={t('birthDate.askTitle')}
      description={t('birthDate.askBody')}
      footer={
        <>
          {onLogout && (
            <Button variant="ghost" size="lg" disabled={busy} onClick={onLogout}>
              {t('lock.logoutInstead')}
            </Button>
          )}
          <Button variant="cta" size="lg" loading={busy} onClick={() => void save()}>
            {t('birthDate.save')}
          </Button>
        </>
      }
    >
      <div data-birth-date-ask className="flex flex-col gap-3">
        <BirthDatePicker
          value={parts}
          disabled={busy}
          error={error}
          onChange={(p) => {
            setParts(p);
            setError(null);
          }}
        />
        <p className="text-sm text-muted">{t('birthDate.onceHint')}</p>
      </div>
    </Modal>
  );
}
