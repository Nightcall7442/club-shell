/**
 * Leaving the club in two taps, and a seat nobody uses giving itself back.
 *
 * - {@link LeaveButton}: "Завершить и выйти" at the right end of the status line. Before it, signing out took Profile →
 *   Settings → the bottom of the page, so players walked away instead: the meter kept running on a postpaid session,
 *   and whoever sat down next played on their account and balance.
 * - {@link IdleLeavePrompt}: after {@link IDLE_LEAVE_SEC} without any input, with no game running and the Shell in front,
 *   "Вы ещё здесь?" counts down {@link IDLE_LEAVE_COUNTDOWN_SEC} seconds and then signs the player out (`idle`). It stays
 *   out of the way while a game or a program from the dock is in front: a cutscene or a film has no input either, and
 *   the player would never see the prompt behind it.
 *
 * Both end the session through `auth.logout`; the lock screen then shows what the visit cost (`auth.receipt`).
 */
import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { useIdle } from '@/hooks/useIdle';
import { useLocale } from '@/hooks/useLocale';
import { useSession } from '@/hooks/useSession';
import { formatDurationSec, formatMoney } from '@/lib/format';
import { useAuthStore } from '@/store/auth';
import { useGamesStore } from '@/store/games';

/** No input for this long, outside games, and the Shell asks whether anyone is still there. */
export const IDLE_LEAVE_SEC = 10 * 60;
/** Seconds the question stays up before the player is signed out. */
export const IDLE_LEAVE_COUNTDOWN_SEC = 60;

const IconLeave = (): JSX.Element => (
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth={2}
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <path d="M15 4h3a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-3" />
    <path d="M10 17l-5-5 5-5M5 12h11" />
  </svg>
);

/** Signs out (ending the session) and goes to the lock screen. */
function useLeave(): (reason: 'user' | 'idle') => Promise<void> {
  const logout = useAuthStore((s) => s.logout);
  const navigate = useNavigate();
  return useCallback(
    async (reason) => {
      await logout(reason);
      navigate('/lock', { replace: true });
    },
    [logout, navigate],
  );
}

export function LeaveButton(): JSX.Element | null {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const s = useSession();
  const leave = useLeave();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);

  if (!s.user) {
    return null;
  }

  // What ending now means for the player's money: a postpaid bill is taken now, unused prepaid time goes back.
  const detail = !s.isOpen
    ? t('session.leaveNoSession')
    : s.isOpenEnded
      ? t('session.leavePostpaid', {
          duration: formatDurationSec(s.secondsUsed, { compact: true }),
          amount: s.cost ? formatMoney(s.cost, locale) : '—',
        })
      : t('session.leavePrepaid', { duration: formatDurationSec(s.secondsUsed, { compact: true }) });

  const confirm = async (): Promise<void> => {
    setBusy(true);
    try {
      await leave('user');
    } finally {
      setBusy(false);
      setOpen(false);
    }
  };

  return (
    <>
      <button
        type="button"
        data-nav="true"
        onClick={() => setOpen(true)}
        className="focus-ring flex h-12 items-center gap-2 rounded-md px-3 text-sm font-medium text-muted transition-colors duration-[var(--dur-fast)] hover:bg-danger/10 hover:text-danger [&>svg]:h-5 [&>svg]:w-5"
      >
        <IconLeave />
        {t('session.leave')}
      </button>
      <Modal
        open={open}
        onClose={() => !busy && setOpen(false)}
        title={t('session.leaveTitle')}
        description={detail}
        size="sm"
        danger
        footer={
          <>
            <Button variant="ghost" size="lg" onClick={() => setOpen(false)} disabled={busy}>
              {t('common.cancel')}
            </Button>
            <Button variant="danger" size="lg" loading={busy} onClick={() => void confirm()}>
              {t('session.leaveConfirm')}
            </Button>
          </>
        }
      />
    </>
  );
}

export function IdleLeavePrompt(): JSX.Element | null {
  const { t } = useTranslation();
  const s = useSession();
  const { idle, reset } = useIdle(IDLE_LEAVE_SEC);
  const gameRunning = useGamesStore((g) => g.running.some((r) => r.state === 'running' || r.state === 'launching'));
  const leave = useLeave();
  const leaveRef = useRef(leave);
  leaveRef.current = leave;
  // A deadline rather than a decrementing counter: the screen re-renders every second while idle, which must not
  // restart the countdown.
  const [deadline, setDeadline] = useState<number | null>(null);
  const [now, setNow] = useState(() => Date.now());

  const due = idle && s.isOpen && !gameRunning;
  useEffect(() => {
    // Only when the Shell itself is what the player looks at (not a game or a program from the dock).
    if (due && deadline === null && document.hasFocus()) {
      setNow(Date.now());
      setDeadline(Date.now() + IDLE_LEAVE_COUNTDOWN_SEC * 1000);
    }
  }, [due, deadline]);

  useEffect(() => {
    if (deadline === null) {
      return;
    }
    const timer = window.setInterval(() => setNow(Date.now()), 250);
    return () => window.clearInterval(timer);
  }, [deadline]);

  useEffect(() => {
    if (deadline !== null && now >= deadline) {
      setDeadline(null);
      void leaveRef.current('idle');
    }
  }, [now, deadline]);

  const stay = (): void => {
    setDeadline(null);
    reset();
  };

  if (deadline === null || !s.isOpen) {
    return null;
  }

  return (
    <Modal
      open
      onClose={stay}
      title={t('session.idleTitle')}
      description={t('session.idleBody', { seconds: Math.max(0, Math.ceil((deadline - now) / 1000)) })}
      size="sm"
      footer={
        <>
          <Button variant="ghost" size="lg" onClick={() => void leave('user')}>
            {t('session.leaveConfirm')}
          </Button>
          <Button variant="cta" size="lg" onClick={stay}>
            {t('session.idleStay')}
          </Button>
        </>
      }
    />
  );
}
