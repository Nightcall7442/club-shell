/**
 * Authentication: current user, connectivity mode of the login, login/logout/refresh. A login response may carry
 * an existing session for this PC; it is handed to the session store here so screens need a single call.
 * `bootstrapStores()` resets every user-scoped slice when `user` drops to null (logout, `auth.expired`).
 */
import {
  isSessionOpen,
  type AuthExpiredReason,
  type AuthKind,
  type AuthLoginRequest,
  type AuthLoginResponse,
  type AuthStatusResponse,
  type ConnectivityState,
  type Money,
  type PcReadiness,
  type SessionEndReason,
  type User,
} from '@clubshell/contracts';
import { create } from 'zustand';
import { subscribeWithSelector } from 'zustand/middleware';
import { resetAnalyticsSession, track } from '@/lib/analytics';
import { log } from '@/lib/logger';
import {
  buildLoginRequest,
  createElsewhereRetry,
  elsewhereOf,
  type AuthCredentials,
  type ElsewhereInfo,
} from '@/lib/signIn';
import { api, toShellApiError, type ShellError } from '@/lib/tauri';
import { useSessionStore } from './session';
import { asShellError, type AsyncStatus } from './settings';

export type { AuthCredentials } from '@/lib/signIn';

/** What the lock screen shows the player who just left: time played and what the session cost. */
export interface LeaveReceipt {
  secondsUsed: number;
  cost: Money;
  /** `Date.now()` when it was taken; the lock screen shows it for a few seconds. */
  at: number;
}

/** Where the player's session is open after `409 activeSessionElsewhere` on a sign-in. */
export type ElsewhereSession = ElsewhereInfo;

/** The player whose sign-in expired (`tokenExpired`) while their session was open: the lock screen offers them back in. */
export interface ReloginUser {
  userId: string;
  username: string;
  displayName: string;
  avatarUrl: string | null;
}

/** A sign-out the Agent refused: the player is still signed in and the session still runs. */
export interface LogoutFailure {
  reason: SessionEndReason;
  error: ShellError;
}

export interface AuthState {
  user: User | null;
  mode: ConnectivityState;
  expiresAt: string | null;
  /** Set by `auth.expired`; cleared on the next login. */
  expiredReason: AuthExpiredReason | null;
  /** `true` once `auth_status` answered (router guards wait for it). */
  ready: boolean;
  status: AsyncStatus;
  error: ShellError | null;
  /** Set by `logout` when it ended a session; cleared on the next login or by `dismissReceipt`. */
  receipt: LeaveReceipt | null;
  /**
   * Whether the PC lets a player in yet (`auth.status` / `sys.readinessChanged`, SHELL_CHANGES 86): the sign-in screen
   * stays closed until the games disks are attached. `null` from an Agent that does not report it: ready. A property of
   * the PC, not of the player: it survives logout and `auth.expired`.
   */
  readiness: PcReadiness | null;
  /** The last sign-in was refused because the player's session is open on another PC (the dialog's data). */
  elsewhere: ElsewhereSession | null;
  /** Set by `auth.expired {tokenExpired}` with an open session: sign the same player in again and continue. */
  relogin: ReloginUser | null;
  /** The session is locked from the cash desk (`auth.status.lockedByStaff`): only the desk unlocks it. */
  lockedByStaff: boolean;
  /** The player has an unlock PIN (`auth.status.pinSet`); `null` until known. */
  pinSet: boolean | null;
  /** The last `logout` failed: shown with a retry instead of pretending the player left. */
  logoutFailure: LogoutFailure | null;
  /**
   * A fresh sign-in without a session: the tariff picker may start the owner's pay-as-you-go session once, right
   * now. Never set by `auth.status`, by a sign-in that brought its session back, or after a session ended.
   */
  autoStartArmed: boolean;
}

/** Options of {@link AuthActions.login}. */
export interface LoginOptions {
  /** The player confirmed «end my session on the other PC and sign in here». */
  endElsewhere?: boolean;
}

export interface AuthActions {
  login(kind: AuthKind, creds?: AuthCredentials, opts?: LoginOptions): Promise<AuthLoginResponse>;
  loginGuest(displayName?: string): Promise<AuthLoginResponse>;
  /**
   * Repeats the sign-in refused with `activeSessionElsewhere`, with `endElsewhere: true`. Its secrets were kept in
   * memory only for this and are forgotten as the retry is sent. `null` when there is nothing to repeat.
   */
  endElsewhereAndLogin(): Promise<AuthLoginResponse | null>;
  /** Drops the refused sign-in and its secrets. */
  cancelElsewhere(): void;
  /**
   * Ends the session (if any) and clears the user. `false` when the Agent refused: nothing is cleared and the failure
   * is in `logoutFailure` for the screen to show with a retry.
   */
  logout(reason?: SessionEndReason): Promise<boolean>;
  /** Tries the failed sign-out again (same reason). */
  retryLogout(): Promise<boolean>;
  dismissLogoutFailure(): void;
  /** `auth_status` → user + session; errors leave the current state untouched. */
  refresh(): Promise<AuthStatusResponse | null>;
  /** `auth_status` for the lock screen: who locked the session (`lockedByStaff`) and whether a PIN unlocks it. */
  refreshLock(): Promise<void>;
  setUser(user: User | null): void;
  /** Merges a partial user update (profile edits, `wallet.updated` balance mirror). */
  updateUser(patch: Partial<User>): void;
  setBalance(balance: Money): void;
  setLockedByStaff(locked: boolean): void;
  onExpired(reason: AuthExpiredReason): void;
  onReadiness(readiness: PcReadiness): void;
  /** The picker used (or passed on) the one auto-start a fresh sign-in allows. */
  disarmAutoStart(): void;
  clearRelogin(): void;
  clearError(): void;
  dismissReceipt(): void;
  reset(): void;
}

export type AuthStore = AuthState & AuthActions;

const initialState: AuthState = {
  user: null,
  mode: 'online',
  expiresAt: null,
  expiredReason: null,
  ready: false,
  status: 'idle',
  error: null,
  receipt: null,
  readiness: null,
  elsewhere: null,
  relogin: null,
  lockedByStaff: false,
  pinSet: null,
  logoutFailure: null,
  autoStartArmed: false,
};

/**
 * The secrets of a sign-in refused with `activeSessionElsewhere`, for the one retry with `endElsewhere`. Closure memory
 * only: never in the store (devtools, snapshots), never persisted; dropped on cancel, success, logout, expiry or the retry.
 */
const elsewhereRetry = createElsewhereRetry();

export const useAuthStore = create<AuthStore>()(
  subscribeWithSelector((set, get) => {
    /** Sends a sign-in; a refusal with `activeSessionElsewhere` opens the lock screen's dialog. */
    const signIn = async (req: AuthLoginRequest): Promise<AuthLoginResponse> => {
      set({ status: 'loading', error: null });
      try {
        const res = await elsewhereRetry.send(req, (r) => api.auth.login(r));
        resetAnalyticsSession();
        set({
          user: res.user,
          mode: res.mode,
          expiresAt: res.expiresAt,
          expiredReason: null,
          receipt: null,
          ready: true,
          status: 'ready',
          elsewhere: null,
          relogin: null,
          logoutFailure: null,
          lockedByStaff: false,
          pinSet: null,
          autoStartArmed: !res.session,
        });
        useSessionStore.getState().setSession(res.session ?? null);
        track('auth.login', {
          kind: req.kind,
          role: res.user.role,
          mode: res.mode,
          endElsewhere: req.endElsewhere === true,
        });
        return res;
      } catch (e) {
        const err = toShellApiError(e);
        set({ status: 'error', error: err.toJSON(), elsewhere: elsewhereRetry.armed ? elsewhereOf(err) : null });
        throw err;
      }
    };

    return {
      ...initialState,

      login(kind, creds = {}, opts = {}) {
        return signIn(buildLoginRequest(kind, creds, opts.endElsewhere === true));
      },

      loginGuest(displayName) {
        return get().login('guest', displayName ? { username: displayName } : {});
      },

      async endElsewhereAndLogin() {
        const req = elsewhereRetry.take();
        set({ elsewhere: null });
        return req ? signIn(req) : null;
      },

      cancelElsewhere() {
        elsewhereRetry.clear();
        set({ elsewhere: null });
      },

      async logout(reason = 'user') {
        set({ status: 'loading', error: null, logoutFailure: null });
        let receipt: LeaveReceipt | null = null;
        try {
          const res = await api.auth.logout(reason);
          if (res.sessionEnded && res.session) {
            receipt = { secondsUsed: res.session.secondsUsed, cost: res.session.cost, at: Date.now() };
          }
        } catch (e) {
          // The Agent kept the player signed in (and the meter running): say so, never pretend they left.
          const error = asShellError(e);
          log.warn('auth.logout failed', error);
          track('auth.logoutFailed', { reason, code: error.code });
          set({ status: 'error', error, logoutFailure: { reason, error } });
          return false;
        }
        elsewhereRetry.clear();
        track('auth.logout', { reason });
        useSessionStore.getState().reset();
        set({ ...initialState, ready: true, status: 'ready', receipt, readiness: get().readiness });
        return true;
      },

      retryLogout() {
        return get().logout(get().logoutFailure?.reason ?? 'user');
      },

      dismissLogoutFailure() {
        set({ logoutFailure: null });
      },

      dismissReceipt() {
        set({ receipt: null });
      },

      async refresh() {
        set({ status: 'loading', error: null });
        try {
          const res = await api.auth.status();
          set({
            user: res.authenticated ? (res.user ?? null) : null,
            mode: res.mode,
            expiresAt: res.expiresAt ?? null,
            ready: true,
            status: 'ready',
            readiness: res.readiness ?? null,
            lockedByStaff: res.lockedByStaff === true,
            pinSet: res.pinSet ?? null,
          });
          useSessionStore.getState().setSession(res.authenticated ? (res.session ?? null) : null);
          return res;
        } catch (e) {
          const error = asShellError(e);
          log.warn('auth.status failed', error);
          set({ ready: true, status: 'error', error });
          return null;
        }
      },

      async refreshLock() {
        try {
          const res = await api.auth.status();
          set({ lockedByStaff: res.lockedByStaff === true, pinSet: res.pinSet ?? null });
        } catch (e) {
          log.debug('auth.status (lock) failed', asShellError(e));
        }
      },

      setUser(user) {
        set({ user });
      },

      updateUser(patch) {
        set((s) => (s.user ? { user: { ...s.user, ...patch } } : {}));
      },

      setBalance(balance) {
        get().updateUser({ balance });
      },

      setLockedByStaff(locked) {
        set({ lockedByStaff: locked });
      },

      onExpired(reason) {
        track('auth.expired', { reason });
        const { user, readiness } = get();
        const session = useSessionStore.getState().session;
        // The Agent locked the open session when the token ran out: the same member signs in again and plays on.
        const relogin: ReloginUser | null =
          reason === 'tokenExpired' && user && user.role !== 'guest' && session && isSessionOpen(session.state)
            ? {
                userId: user.id,
                username: user.username,
                displayName: user.displayName,
                avatarUrl: user.avatarUrl ?? null,
              }
            : null;
        elsewhereRetry.clear();
        useSessionStore.getState().reset();
        set({ ...initialState, ready: true, status: 'ready', expiredReason: reason, readiness, relogin });
      },

      onReadiness(readiness) {
        set({ readiness });
      },

      disarmAutoStart() {
        set({ autoStartArmed: false });
      },

      clearRelogin() {
        set({ relogin: null });
      },

      clearError() {
        set({ error: null, status: get().status === 'error' ? 'idle' : get().status });
      },

      reset() {
        elsewhereRetry.clear();
        set({ ...initialState, ready: get().ready, readiness: get().readiness });
      },
    };
  }),
);

/** Selector: the PC lets a player in (no readiness reported counts as ready, SHELL_CHANGES 86). */
export const selectPcReady = (s: AuthStore): boolean => s.readiness === null || s.readiness.state === 'ready';
/** Selector: logged in. */
export const selectIsAuthenticated = (s: AuthStore): boolean => s.user !== null;
/** Selector: guest account. */
export const selectIsGuest = (s: AuthStore): boolean => s.user?.role === 'guest';
