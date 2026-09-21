/**
 * Session contracts — mirror of `ClubShell.Contracts.Sessions` (SessionState.cs, SessionEvent.cs).
 */
import type { Money } from './wallet.js';

/** Session state machine (IPC_PROTOCOL.md §6.1, §9.2). */
export const SessionState = {
  Idle: 'idle',
  Starting: 'starting',
  Active: 'active',
  Paused: 'paused',
  Locked: 'locked',
  Ending: 'ending',
  Ended: 'ended',
} as const;
/** Session state machine (IPC_PROTOCOL.md §6.1, §9.2). */
export type SessionState = (typeof SessionState)[keyof typeof SessionState];

// ---- BEGIN MANUAL ----
/** `true` for states in which a session exists and is not finished (`starting` .. `ending`). */
export function isSessionOpen(state: SessionState): boolean {
  return state !== SessionState.Idle && state !== SessionState.Ended;
}

/** `true` when session-scoped IPC requests are allowed (`active` or `paused`). */
export function sessionAllowsRequests(state: SessionState): boolean {
  return state === SessionState.Active || state === SessionState.Paused;
}

/** `true` when the timer is counting down (`active`, `locked`, `ending`). */
export function isSessionTimerRunning(state: SessionState): boolean {
  return state === SessionState.Active || state === SessionState.Locked || state === SessionState.Ending;
}
// ---- END MANUAL ----

/** Value of `Session.secondsLeft` for open-ended postpaid sessions. */
export const SESSION_OPEN_ENDED = -1;

/** Play session (IPC_PROTOCOL.md §6.4). */
export interface Session {
  /** Session id (client-generated when created offline). */
  id: string;
  /** Player. */
  userId: string;
  /** PC. */
  pcId: string;
  /** State. */
  state: SessionState;
  /** Start time. */
  startedAt: string;
  /** Scheduled end; null while `idle`/`starting` or for open-ended postpaid. */
  endsAt?: string | null;
  /** Set while paused/locked. */
  pausedAt?: string | null;
  /** Tariff in effect. */
  tariffId: string;
  /** Remaining seconds; −1 for open-ended postpaid. */
  secondsLeft: number;
  /** Consumed seconds. */
  secondsUsed: number;
  /** Cost accrued so far. */
  cost: Money;
  /** Prepaid (charged up front) vs postpaid. */
  isPrepaid: boolean;
  /** Minute marks already emitted as `session.warning`, e.g. `[10, 5]`. */
  warningsSent: number[];
}

// ---- BEGIN MANUAL ----
/** `true` when the session has no fixed end (`secondsLeft === SESSION_OPEN_ENDED`). */
export function isSessionOpenEnded(session: Session): boolean {
  return session.secondsLeft === SESSION_OPEN_ENDED;
}
// ---- END MANUAL ----

/** Payload of the `session.warning` event, emitted at each configured minute mark and once at 0. */
export interface SessionWarning {
  /** Session. */
  sessionId: string;
  /** Whole minutes remaining (0 at expiry). */
  minutesLeft: number;
  /** Seconds remaining. */
  secondsLeft: number;
  /** Scheduled end. */
  endsAt: string;
}

/** Kind of {@link SessionEvent}. */
export const SessionEventType = {
  Started: 'started',
  Paused: 'paused',
  Resumed: 'resumed',
  Extended: 'extended',
  Warning: 'warning',
  Locked: 'locked',
  Unlocked: 'unlocked',
  Ended: 'ended',
  Charged: 'charged',
} as const;
/** Kind of {@link SessionEvent}. */
export type SessionEventType = (typeof SessionEventType)[keyof typeof SessionEventType];

/** Why a session ended. */
export const SessionEndReason = {
  User: 'user',
  TimeUp: 'timeUp',
  Admin: 'admin',
  Idle: 'idle',
  AgentRestart: 'agentRestart',
  Error: 'error',
} as const;
/** Why a session ended. */
export type SessionEndReason = (typeof SessionEndReason)[keyof typeof SessionEndReason];

/** `data` of an `extended` session event. */
export interface SessionExtendedData {
  /** Minutes added. */
  minutes: number;
  /** Amount charged for the extension. */
  cost: Money;
}

/** `data` of a `warning` session event. */
export interface SessionWarningData {
  /** Minutes remaining at the time of the warning. */
  minutesLeft: number;
}

/** `data` of a `charged` session event. */
export interface SessionChargedData {
  /** Amount charged. */
  amount: Money;
}

/** `data` of an `ended` session event. */
export interface SessionEndedData {
  /** End reason. */
  reason: SessionEndReason;
}

// ---- BEGIN MANUAL ----
/** Typed `data` per {@link SessionEventType}; `null` for types without data. */
export interface SessionEventDataMap {
  started: null;
  paused: null;
  resumed: null;
  extended: SessionExtendedData;
  warning: SessionWarningData;
  locked: null;
  unlocked: null;
  ended: SessionEndedData;
  charged: SessionChargedData;
}

/** Audit/replay event of a session (IPC_PROTOCOL.md §6.21); `data` shape depends on `type`. */
export interface SessionEvent<T extends SessionEventType = SessionEventType> {
  /** Session. */
  sessionId: string;
  /** Kind. */
  type: T;
  /** Event time. */
  at: string;
  /** Typed extra data or null. */
  data?: SessionEventDataMap[T] | null;
}
// ---- END MANUAL ----

/** Settlement returned by `session.end` and `POST /sessions/{id}/end`. */
export interface SessionEndResult {
  /** Final session (state `ended`). */
  session: Session;
  /** Amount charged at settlement (postpaid). */
  charged: Money;
  /** Amount refunded for unused prepaid time. */
  refunded: Money;
}

/** Payload of the `session.ended` IPC event and the `sessionEnded` agent event. */
export interface SessionEndedEvent {
  /** Final session. */
  session: Session;
  /** End reason. */
  reason: SessionEndReason;
  /** Amount charged at settlement. */
  charged: Money;
}

/** Payload of the `sessionStarted` agent event. */
export interface SessionStartedEvent {
  /** New session. */
  session: Session;
}

/** Body of `POST /sessions` (sent with an `Idempotency-Key`). */
export interface SessionCreateRequest {
  /** PC. */
  pcId: string;
  /** Player. */
  userId: string;
  /** Tariff. */
  tariffId: string;
  /** Minutes to buy; required unless the tariff is a package. */
  minutes?: number | null;
  /** Charge now vs open-ended postpaid. */
  prepaid: boolean;
  /** Actual start for offline-created sessions (≤ `maxOfflineMinutes` in the past). */
  startedAt?: string | null;
  /** Agent-generated id used offline; adopted by the server when free. */
  clientSessionId?: string | null;
}

/** Body of `POST /sessions/{id}/end`. */
export interface SessionEndReport {
  /** End reason. */
  reason: SessionEndReason;
  /** Seconds consumed as measured by the Agent. */
  secondsUsed: number;
  /** Actual end time (offline replay). */
  endedAt?: string | null;
}

/** Body of `POST /sessions/{id}/events` (≤ 100 events). */
export interface SessionEventsBatch {
  /** Events in chronological order. */
  events: SessionEvent[];
}

/** Maximum events per {@link SessionEventsBatch}. */
export const SESSION_EVENTS_MAX = 100;
