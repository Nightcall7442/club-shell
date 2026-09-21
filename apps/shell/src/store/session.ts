/**
 * Play session. The Agent is authoritative (`agent://session.updated` every second); a local 1 s ticker keeps
 * the countdown smooth between events (derived from `endsAt` against the server-corrected clock) and
 * `session_time_left` re-syncs the clock every 30 s.
 */
import {
  isSessionOpen,
  isSessionTimerRunning,
  SESSION_OPEN_ENDED,
  type Session,
  type SessionEndReason,
  type SessionEndResult,
  type SessionEndedEvent,
  type SessionState,
  type SessionTimeLeftResponse,
  type SessionWarning,
} from '@clubshell/contracts';
import { create } from 'zustand';
import { subscribeWithSelector } from 'zustand/middleware';
import { track } from '@/lib/analytics';
import { log } from '@/lib/logger';
import { api, toShellApiError, type ShellError } from '@/lib/tauri';
import { secondsUntil, syncServerTime } from '@/lib/time';
import { asShellError, type AsyncStatus } from './settings';

export interface SessionStoreState {
  session: Session | null;
  /** `idle` when there is no session; `ended` right after `session.ended` until the next session. */
  state: SessionState;
  /** Remaining seconds; −1 for open-ended postpaid sessions. */
  secondsLeft: number;
  secondsUsed: number;
  /** Minute marks already warned about (from the session + `session.warning`). */
  warnings: number[];
  lastWarning: SessionWarning | null;
  /** Last `session.ended` payload (lock screen shows the reason / settlement). */
  lastEnded: SessionEndedEvent | null;
  /** Last successful `session.end` settlement. */
  lastResult: SessionEndResult | null;
  status: AsyncStatus;
  error: ShellError | null;
}

export interface SessionStoreActions {
  /** `session_get`; errors are swallowed (no session assumed). */
  load(): Promise<Session | null>;
  start(tariffId: string, prepaid: boolean, minutes?: number): Promise<Session>;
  pause(reason?: string): Promise<Session>;
  resume(): Promise<Session>;
  extend(minutes: number, tariffId?: string): Promise<Session>;
  end(reason?: SessionEndReason): Promise<SessionEndResult>;
  lock(reason?: string): Promise<Session>;
  unlock(secret: { password?: string; pin?: string }): Promise<Session>;
  /** Replaces the session (events, login response). */
  setSession(session: Session | null): void;
  /** Local state override for `shell.command{lock|unlock}` before the Agent's `session.updated` arrives. */
  applyState(state: SessionState): void;
  applyTimeLeft(r: SessionTimeLeftResponse): void;
  onWarning(w: SessionWarning): void;
  onEnded(e: SessionEndedEvent): void;
  /** One local second; called by the ticker. */
  tick(): void;
  /** `session_time_left` round trip (clock sync + drift correction). */
  reconcile(): Promise<void>;
  reset(): void;
}

export type SessionStore = SessionStoreState & SessionStoreActions;

const RECONCILE_MS = 30_000;

const initialState: SessionStoreState = {
  session: null,
  state: 'idle',
  secondsLeft: 0,
  secondsUsed: 0,
  warnings: [],
  lastWarning: null,
  lastEnded: null,
  lastResult: null,
  status: 'idle',
  error: null,
};

function fromSession(
  session: Session | null,
): Pick<SessionStoreState, 'session' | 'state' | 'secondsLeft' | 'secondsUsed' | 'warnings'> {
  return {
    session,
    state: session?.state ?? 'idle',
    secondsLeft: session?.secondsLeft ?? 0,
    secondsUsed: session?.secondsUsed ?? 0,
    warnings: session?.warningsSent ?? [],
  };
}

let lastReconcileAt = 0;

export const useSessionStore = create<SessionStore>()(
  subscribeWithSelector((set, get) => {
    /** Runs a session command with status bookkeeping; rethrows a `ShellApiError`. */
    const run = async <T>(name: string, work: () => Promise<T>, apply: (v: T) => void): Promise<T> => {
      set({ status: 'loading', error: null });
      try {
        const v = await work();
        apply(v);
        set({ status: 'ready' });
        track(`session.${name}`);
        return v;
      } catch (e) {
        const err = toShellApiError(e);
        set({ status: 'error', error: err.toJSON() });
        throw err;
      }
    };
    const applySession = (s: Session): void => {
      set({ ...fromSession(s), lastEnded: null });
    };

    return {
      ...initialState,

      async load() {
        set({ status: 'loading', error: null });
        try {
          const session = await api.session.get();
          set({ ...fromSession(session && isSessionOpen(session.state) ? session : null), status: 'ready' });
          return session;
        } catch (e) {
          const error = asShellError(e);
          log.warn('session.load failed', error);
          set({ status: 'error', error });
          return null;
        }
      },

      start(tariffId, prepaid, minutes) {
        return run('start', () => api.session.start({ tariffId, prepaid, minutes: minutes ?? null }), applySession);
      },

      pause(reason) {
        return run('pause', () => api.session.pause(reason), applySession);
      },

      resume() {
        return run('resume', () => api.session.resume(), applySession);
      },

      extend(minutes, tariffId) {
        return run('extend', () => api.session.extend(minutes, tariffId), applySession);
      },

      end(reason) {
        return run(
          'end',
          () => api.session.end(reason),
          (r) =>
            set({
              ...fromSession(null),
              state: 'ended',
              lastResult: r,
              lastEnded: { session: r.session, reason: reason ?? 'user', charged: r.charged },
            }),
        );
      },

      lock(reason) {
        return run('lock', () => api.session.lock(reason), applySession);
      },

      unlock(secret) {
        return run('unlock', () => api.session.unlock(secret), applySession);
      },

      setSession(session) {
        if (session && !isSessionOpen(session.state)) {
          // A closed session is only a "no session" with a reason; `onEnded` carries the settlement.
          set({ ...fromSession(null), state: session.state });
          return;
        }
        set(fromSession(session));
      },

      applyState(state) {
        const s = get().session;
        if (!s || s.state === state) {
          return;
        }
        set({ session: { ...s, state }, state });
      },

      applyTimeLeft(r) {
        syncServerTime(r.serverTime);
        const s = get().session;
        if (r.state === 'idle' || r.state === 'ended') {
          if (s) {
            set({ ...fromSession(null), state: r.state });
          }
          return;
        }
        if (!s || (r.sessionId && r.sessionId !== s.id)) {
          return;
        }
        const session: Session = {
          ...s,
          state: r.state,
          secondsLeft: r.secondsLeft,
          secondsUsed: r.secondsUsed,
          endsAt: r.endsAt ?? s.endsAt ?? null,
        };
        set({ session, state: r.state, secondsLeft: r.secondsLeft, secondsUsed: r.secondsUsed });
      },

      onWarning(w) {
        set((st) => ({
          lastWarning: w,
          warnings: st.warnings.includes(w.minutesLeft) ? st.warnings : [...st.warnings, w.minutesLeft],
        }));
      },

      onEnded(e) {
        set({ ...fromSession(null), state: 'ended', lastEnded: e, lastWarning: null, status: 'ready' });
        track('session.ended', { reason: e.reason });
      },

      tick() {
        const { session, secondsLeft, secondsUsed } = get();
        if (!session || !isSessionTimerRunning(session.state)) {
          return;
        }
        let next = secondsLeft;
        if (secondsLeft !== SESSION_OPEN_ENDED) {
          const untilEnd = session.endsAt ? secondsUntil(session.endsAt) : Number.NaN;
          next = Number.isNaN(untilEnd) ? Math.max(0, secondsLeft - 1) : Math.max(0, untilEnd);
        }
        set({ secondsLeft: next, secondsUsed: secondsUsed + 1 });
        if (Date.now() - lastReconcileAt >= RECONCILE_MS) {
          void get().reconcile();
        }
      },

      async reconcile() {
        lastReconcileAt = Date.now();
        try {
          get().applyTimeLeft(await api.session.timeLeft());
        } catch (e) {
          log.debug('session.timeLeft failed', asShellError(e));
        }
      },

      reset() {
        set(initialState);
      },
    };
  }),
);

let ticker: ReturnType<typeof setInterval> | null = null;

/** Starts the 1 s countdown ticker (idempotent). */
export function startSessionTicker(): void {
  if (ticker === null) {
    lastReconcileAt = Date.now();
    ticker = setInterval(() => useSessionStore.getState().tick(), 1000);
  }
}

/** Stops the ticker (teardown / tests). */
export function stopSessionTicker(): void {
  if (ticker !== null) {
    clearInterval(ticker);
    ticker = null;
  }
}

/** Selector: session open (`starting`..`ending`). */
export const selectHasSession = (s: SessionStore): boolean => s.session !== null && isSessionOpen(s.session.state);
/** Selector: session is `locked`. */
export const selectIsLocked = (s: SessionStore): boolean => s.state === 'locked';
