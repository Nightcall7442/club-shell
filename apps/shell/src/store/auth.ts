/**
 * Authentication: current user, connectivity mode of the login, login/logout/refresh. A login response may carry
 * an existing session for this PC; it is handed to the session store here so screens need a single call.
 * `bootstrapStores()` resets every user-scoped slice when `user` drops to null (logout, `auth.expired`).
 */
import type {
  AuthExpiredReason,
  AuthKind,
  AuthLoginResponse,
  AuthStatusResponse,
  ConnectivityState,
  Money,
  SessionEndReason,
  User,
} from '@clubshell/contracts';
import { create } from 'zustand';
import { subscribeWithSelector } from 'zustand/middleware';
import { resetAnalyticsSession, track } from '@/lib/analytics';
import { log } from '@/lib/logger';
import { api, toShellApiError, type ShellError } from '@/lib/tauri';
import { useSessionStore } from './session';
import { asShellError, type AsyncStatus } from './settings';

/** Secrets per `AuthKind`; only the field matching `kind` is used. */
export interface AuthCredentials {
  username?: string;
  password?: string;
  qrToken?: string;
  cardId?: string;
  token?: string;
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
}

export interface AuthActions {
  login(kind: AuthKind, creds?: AuthCredentials): Promise<AuthLoginResponse>;
  loginGuest(displayName?: string): Promise<AuthLoginResponse>;
  /** Ends the session (if any) and clears the user. Errors are logged; the local state is cleared regardless. */
  logout(reason?: SessionEndReason): Promise<void>;
  /** `auth_status` → user + session; errors leave the current state untouched. */
  refresh(): Promise<AuthStatusResponse | null>;
  setUser(user: User | null): void;
  /** Merges a partial user update (profile edits, `wallet.updated` balance mirror). */
  updateUser(patch: Partial<User>): void;
  setBalance(balance: Money): void;
  onExpired(reason: AuthExpiredReason): void;
  clearError(): void;
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
};

export const useAuthStore = create<AuthStore>()(
  subscribeWithSelector((set, get) => ({
    ...initialState,

    async login(kind, creds = {}) {
      set({ status: 'loading', error: null });
      try {
        const res = await api.auth.login({
          kind,
          username: creds.username ?? null,
          password: creds.password ?? null,
          qrToken: creds.qrToken ?? null,
          cardId: creds.cardId ?? null,
          token: creds.token ?? null,
        });
        resetAnalyticsSession();
        set({
          user: res.user,
          mode: res.mode,
          expiresAt: res.expiresAt,
          expiredReason: null,
          ready: true,
          status: 'ready',
        });
        useSessionStore.getState().setSession(res.session ?? null);
        track('auth.login', { kind, role: res.user.role, mode: res.mode });
        return res;
      } catch (e) {
        const err = toShellApiError(e);
        set({ status: 'error', error: err.toJSON() });
        throw err;
      }
    },

    loginGuest(displayName) {
      return get().login('guest', displayName ? { username: displayName } : {});
    },

    async logout(reason = 'user') {
      set({ status: 'loading', error: null });
      try {
        await api.auth.logout(reason);
      } catch (e) {
        log.warn('auth.logout failed', asShellError(e));
      }
      track('auth.logout', { reason });
      useSessionStore.getState().reset();
      set({ ...initialState, ready: true, status: 'ready' });
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

    setUser(user) {
      set({ user });
    },

    updateUser(patch) {
      set((s) => (s.user ? { user: { ...s.user, ...patch } } : {}));
    },

    setBalance(balance) {
      get().updateUser({ balance });
    },

    onExpired(reason) {
      track('auth.expired', { reason });
      useSessionStore.getState().reset();
      set({ ...initialState, ready: true, status: 'ready', expiredReason: reason });
    },

    clearError() {
      set({ error: null, status: get().status === 'error' ? 'idle' : get().status });
    },

    reset() {
      set({ ...initialState, ready: get().ready });
    },
  })),
);

/** Selector: logged in. */
export const selectIsAuthenticated = (s: AuthStore): boolean => s.user !== null;
/** Selector: guest account. */
export const selectIsGuest = (s: AuthStore): boolean => s.user?.role === 'guest';
