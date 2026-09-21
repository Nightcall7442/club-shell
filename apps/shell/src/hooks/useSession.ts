/**
 * Selector facade over the session + auth stores with the derived flags every screen needs (time label,
 * warning/critical thresholds, open-ended, …). Renders only when one of the selected values changes.
 */
import {
  isSessionOpen,
  SESSION_OPEN_ENDED,
  type Money,
  type Session,
  type SessionEndReason,
  type SessionEndResult,
  type SessionEndedEvent,
  type SessionState,
  type User,
} from '@clubshell/contracts';
import { useMemo } from 'react';
import { useShallow } from 'zustand/react/shallow';
import { hmm, mmss } from '@/lib/time';
import { useAuthStore } from '@/store/auth';
import { useSessionStore } from '@/store/session';

/** Minute thresholds of the session bar (`timer-warning` / `timer-critical`). */
export const WARNING_MINUTES = 15;
export const CRITICAL_MINUTES = 5;
/** Below this the top bar switches from `h:mm` to `mm:ss`. */
export const MMSS_BELOW_SEC = 10 * 60;

export interface SessionFacade {
  user: User | null;
  isAuthenticated: boolean;
  isGuest: boolean;
  session: Session | null;
  state: SessionState;
  /** −1 for open-ended sessions. */
  secondsLeft: number;
  secondsUsed: number;
  /** Whole minutes left (0 for open-ended). */
  minutesLeft: number;
  /** Session exists and is not ended. */
  isOpen: boolean;
  isActive: boolean;
  isPaused: boolean;
  isLocked: boolean;
  isEnded: boolean;
  isOpenEnded: boolean;
  /** ≤ 15 minutes left. */
  isWarning: boolean;
  /** ≤ 5 minutes left. */
  isCritical: boolean;
  /** `mm:ss` under 10 minutes, `h:mm` otherwise; time used for open-ended sessions. */
  timeLabel: string;
  cost: Money | null;
  tariffId: string | null;
  warnings: number[];
  lastEnded: SessionEndedEvent | null;
  lastResult: SessionEndResult | null;
  busy: boolean;
  start: (tariffId: string, prepaid: boolean, minutes?: number) => Promise<Session>;
  pause: (reason?: string) => Promise<Session>;
  resume: () => Promise<Session>;
  extend: (minutes: number, tariffId?: string) => Promise<Session>;
  end: (reason?: SessionEndReason) => Promise<SessionEndResult>;
  lock: (reason?: string) => Promise<Session>;
  unlock: (secret: { password?: string; pin?: string }) => Promise<Session>;
}

/** Time label used by the top bar and lock screen. */
export function sessionTimeLabel(secondsLeft: number, secondsUsed: number): string {
  if (secondsLeft === SESSION_OPEN_ENDED) {
    return hmm(secondsUsed);
  }
  return secondsLeft < MMSS_BELOW_SEC ? mmss(secondsLeft) : hmm(secondsLeft);
}

export function useSession(): SessionFacade {
  const user = useAuthStore((s) => s.user);
  const s = useSessionStore(
    useShallow((st) => ({
      session: st.session,
      state: st.state,
      secondsLeft: st.secondsLeft,
      secondsUsed: st.secondsUsed,
      warnings: st.warnings,
      lastEnded: st.lastEnded,
      lastResult: st.lastResult,
      status: st.status,
      start: st.start,
      pause: st.pause,
      resume: st.resume,
      extend: st.extend,
      end: st.end,
      lock: st.lock,
      unlock: st.unlock,
    })),
  );

  return useMemo<SessionFacade>(() => {
    const isOpenEnded = s.session !== null && s.secondsLeft === SESSION_OPEN_ENDED;
    const isOpen = s.session !== null && isSessionOpen(s.state);
    const minutesLeft = isOpenEnded || !isOpen ? 0 : Math.floor(Math.max(0, s.secondsLeft) / 60);
    const timed = isOpen && !isOpenEnded;
    return {
      user,
      isAuthenticated: user !== null,
      isGuest: user?.role === 'guest',
      session: s.session,
      state: s.state,
      secondsLeft: s.secondsLeft,
      secondsUsed: s.secondsUsed,
      minutesLeft,
      isOpen,
      isActive: s.state === 'active',
      isPaused: s.state === 'paused',
      isLocked: s.state === 'locked',
      isEnded: s.state === 'ended',
      isOpenEnded,
      isWarning: timed && s.secondsLeft <= WARNING_MINUTES * 60,
      isCritical: timed && s.secondsLeft <= CRITICAL_MINUTES * 60,
      timeLabel: isOpen ? sessionTimeLabel(s.secondsLeft, s.secondsUsed) : '--:--',
      cost: s.session?.cost ?? null,
      tariffId: s.session?.tariffId ?? null,
      warnings: s.warnings,
      lastEnded: s.lastEnded,
      lastResult: s.lastResult,
      busy: s.status === 'loading',
      start: s.start,
      pause: s.pause,
      resume: s.resume,
      extend: s.extend,
      end: s.end,
      lock: s.lock,
      unlock: s.unlock,
    };
  }, [user, s]);
}
