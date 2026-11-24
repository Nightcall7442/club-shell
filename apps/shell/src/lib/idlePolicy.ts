/**
 * What a seat nobody uses does (the «Вы ещё здесь?» prompt, `IdleLeavePrompt`), and when the Shell asks the Agent again
 * who is signed in (D-100). Pure: no stores, no IPC.
 */

/** What the prompt does when its countdown ends. */
export type IdleOutcome = 'callAdmin' | 'keep' | 'lock' | 'signOut' | 'none';

export interface IdleSeat {
  isOpen: boolean;
  isOpenEnded: boolean;
  isGuest: boolean;
  isPayAtDesk: boolean;
}

/**
 * - A postpaid seat the desk opened is never ended by idling (the bill would go unpaid): the administrator is called.
 * - A guest's prepaid time is paid and a guest can neither lock (`guestLock`) nor sign in again: it is kept (D-100).
 * - A member's prepaid time is theirs: the screen locks and the minutes wait.
 * - The player's own open-ended time (pay as you go from the balance) is signed out, so it stops costing.
 */
export function idleOutcome(seat: IdleSeat): IdleOutcome {
  if (!seat.isOpen) {
    return 'none';
  }
  if (seat.isPayAtDesk) {
    return 'callAdmin';
  }
  if (!seat.isOpenEnded) {
    return seat.isGuest ? 'keep' : 'lock';
  }
  return 'signOut';
}

/** The prompt only asks when its countdown would do something the player should be warned about. */
export function idlePromptDue(idle: boolean, seat: IdleSeat, programsRunning: boolean): boolean {
  const outcome = idleOutcome(seat);
  return idle && !programsRunning && outcome !== 'none' && outcome !== 'keep';
}

/** Session states with a player at the PC. */
const OPEN_STATES: ReadonlySet<string> = new Set(['active', 'paused', 'locked', 'ending']);

/**
 * A `session.updated` of an open session while the Shell holds no player (D-100: the Agent restarted mid-session and
 * restored its player after the Shell's reconnect read «nobody»): ask `auth.status` again, at most every `everyMs`.
 */
export function shouldAdoptSignIn(
  hasUser: boolean,
  state: string,
  now: number,
  lastReadAt: number,
  everyMs: number,
): boolean {
  return !hasUser && OPEN_STATES.has(state) && now - lastReadAt >= everyMs;
}
