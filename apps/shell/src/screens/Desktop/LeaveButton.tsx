/**
 * Leaving the club in two taps, and a seat nobody uses giving itself back.
 *
 * - {@link LeaveButton}: "Завершить и выйти" at the right end of the status line. Before it, signing out took Profile →
 *   Settings → the bottom of the page, so players walked away instead: the meter kept running on a postpaid session,
 *   and whoever sat down next played on their account and balance. A postpaid session is never ended by the player:
 *   the button becomes «Оплатить на кассе» (what is owed now, and a call to the administrator) — ending it here let
 *   players walk out unpaid and left their accounts in the red. Only the cash desk ends it, taking the payment.
 * - {@link IdleLeavePrompt}: after {@link IDLE_LEAVE_SEC} without any input (keyboard, mouse or gamepad), with nothing
 *   running and the Shell in front, "Вы ещё здесь?" counts down {@link IDLE_LEAVE_COUNTDOWN_SEC} seconds; any input
 *   cancels it. A member's prepaid time is theirs, so it then locks the screen (`session.lock {idle}`: the paid minutes
 *   wait; the Agent itself only ever locks on idle). A guest's prepaid time is paid as well, and a guest cannot lock
 *   (`guestLock`) nor sign in again: it is never ended on idle (D-100) — no prompt, the time runs as paid.
 *   Open-ended time of the player's own (pay as you go) is signed out (`idle`). A postpaid seat is never ended by
 *   idling: the administrator is called instead (once per idle spell), who closes the bill at the desk. It stays out of
 *   the way while a game or any program is running (from the catalogue or the dock): a cutscene or a film has no input
 *   either, and the player would never see the prompt behind it.
 *
 * Signing out goes through `auth.logout`; the lock screen then shows what the visit cost (`auth.receipt`).
 */
import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { Button } from '@/components/ui/Button';
import { Modal } from '@/components/ui/Modal';
import { useIdle } from '@/hooks/useIdle';
import { useLocale } from '@/hooks/useLocale';
import { useRunningPrograms } from '@/hooks/useRunningPrograms';
import { useSession } from '@/hooks/useSession';
import { formatDurationSec, formatMoney } from '@/lib/format';
import { idleOutcome, idlePromptDue } from '@/lib/idlePolicy';
import { api } from '@/lib/tauri';
import { useAuthStore } from '@/store/auth';
import { useGamesStore } from '@/store/games';
import { useNotificationsStore } from '@/store/notifications';

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

/**
 * Signs out (ending the session) and goes to the lock screen. A refused sign-out stays where it is: the root's
 * `LogoutFailedDialog` says the player is still signed in and offers a retry.
 */
function useLeave(): (reason: 'user' | 'idle') => Promise<void> {
  const logout = useAuthStore((s) => s.logout);
  const navigate = useNavigate();
  return useCallback(
    async (reason) => {
      if (await logout(reason)) {
        navigate('/lock', { replace: true });
      }
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

  // Postpaid: the bill is paid at the desk, where the session is ended; the player can only call the administrator.
  if (s.isPayAtDesk) {
    return <PayAtDeskButton />;
  }

  // What ending now means for the player's money, honestly: the server does not refund a player's own end, so unused
  // prepaid time is lost; pay as you go shows what it cost; a guest's bill is paid at the desk.
  const duration = formatDurationSec(s.secondsUsed, { compact: true });
  const amount = s.cost ? formatMoney(s.cost, locale) : '—';
  const detail = !s.isOpen
    ? t('session.leaveNoSession')
    : s.isOpenEnded
      ? t(s.isGuest ? 'session.leaveGuest' : 'session.leavePostpaid', { duration, amount })
      : t('session.leavePrepaid', {
          duration,
          left: formatDurationSec(Math.max(0, s.secondsLeft), { compact: true }),
        });

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
  // Any program the player runs counts, not only catalogue games (D-100).
  const { programs } = useRunningPrograms();
  const busy = gameRunning || programs.length > 0;
  const navigate = useNavigate();
  const pushError = useNotificationsStore((n) => n.pushError);
  const leave = useLeave();
  // lib/idlePolicy: a postpaid seat calls the administrator, a member's prepaid minutes are kept behind the lock, a
  // guest's prepaid minutes are never ended (D-100), the player's own open-ended time is signed out.
  const outcome = idleOutcome(s);
  const lockInstead = outcome === 'lock';
  const postpaid = outcome === 'callAdmin';
  const idleOut = async (): Promise<void> => {
    if (outcome === 'none' || outcome === 'keep') {
      reset();
      return;
    }
    if (postpaid) {
      try {
        await api.system.callAdmin('help', t('session.idlePostpaidCall'));
      } catch (e) {
        pushError(e, t('admin.callAdminTitle'));
      }
      reset();
      return;
    }
    if (!lockInstead) {
      await leave('idle');
      return;
    }
    try {
      await s.lock('idle');
      navigate('/lock', { replace: true });
    } catch (e) {
      // Never end paid time instead: the Agent's own idle lock follows anyway.
      pushError(e, t('session.lockTitle'));
    }
  };
  const idleOutRef = useRef(idleOut);
  idleOutRef.current = idleOut;
  // A deadline rather than a decrementing counter: the screen re-renders every second while idle, which must not
  // restart the countdown.
  const [deadline, setDeadline] = useState<number | null>(null);
  const [now, setNow] = useState(() => Date.now());

  // A guest's prepaid time runs as paid: nothing to ask (D-100).
  const due = idlePromptDue(idle, s, busy);
  useEffect(() => {
    // Only when the Shell itself is what the player looks at (not a game or a program from the dock).
    if (due && deadline === null && document.hasFocus()) {
      setNow(Date.now());
      setDeadline(Date.now() + IDLE_LEAVE_COUNTDOWN_SEC * 1000);
    }
  }, [due, deadline]);

  // Any input (the hook's idle ends) or a program starting cancels the countdown (D-100).
  useEffect(() => {
    if (deadline !== null && !due) {
      setDeadline(null);
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
      void idleOutRef.current();
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
      description={t(
        postpaid ? 'session.idleBodyPostpaid' : lockInstead ? 'session.idleBodyLock' : 'session.idleBody',
        {
          seconds: Math.max(0, Math.ceil((deadline - now) / 1000)),
        },
      )}
      size="sm"
      footer={
        <>
          {!postpaid && (
            <Button variant="ghost" size="lg" onClick={() => void leave('user')}>
              {t('session.leaveConfirm')}
            </Button>
          )}
          <Button variant="cta" size="lg" onClick={stay}>
            {t('session.idleStay')}
          </Button>
        </>
      }
    />
  );
}

/**
 * «Оплатить на кассе» in place of «Завершить и выйти» on a postpaid session: what the visit costs so far, and a call to
 * the administrator, who ends the session at the desk when it is paid.
 */
function PayAtDeskButton(): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const s = useSession();
  const pushError = useNotificationsStore((n) => n.pushError);
  const push = useNotificationsStore((n) => n.push);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const duration = formatDurationSec(s.secondsUsed, { compact: true });
  const amount = s.cost ? formatMoney(s.cost, locale) : '—';
  const call = async (): Promise<void> => {
    setBusy(true);
    try {
      await api.system.callAdmin('help', t('session.payAtDeskCall', { duration, amount }));
      push({
        id: 'call-admin',
        title: t('notifications.callAdminSent'),
        body: t('admin.adminOnWay'),
        level: 'success',
        source: 'system',
      });
      setOpen(false);
    } catch (e) {
      pushError(e, t('admin.callAdminTitle'));
    } finally {
      setBusy(false);
    }
  };
  return (
    <>
      <button
        type="button"
        data-nav="true"
        onClick={() => setOpen(true)}
        className="focus-ring flex h-12 items-center gap-2 rounded-md px-3 text-sm font-medium text-muted transition-colors duration-[var(--dur-fast)] hover:bg-text/[0.06] hover:text-text [&>svg]:h-5 [&>svg]:w-5"
      >
        <IconLeave />
        {t('session.payAtDesk')}
      </button>
      <Modal
        open={open}
        onClose={() => !busy && setOpen(false)}
        title={t('session.payAtDeskTitle')}
        description={t('session.payAtDeskBody', { duration, amount })}
        size="sm"
        footer={
          <>
            <Button variant="ghost" size="lg" onClick={() => setOpen(false)} disabled={busy}>
              {t('common.cancel')}
            </Button>
            <Button variant="cta" size="lg" loading={busy} onClick={() => void call()}>
              {t('session.payAtDeskCallButton')}
            </Button>
          </>
        }
      />
    </>
  );
}
