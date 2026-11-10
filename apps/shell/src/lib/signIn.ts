/**
 * The sign-in request and its one retry after `409 activeSessionElsewhere` («end my session on PC N and sign in
 * here»). Pure (type imports only) so the retry is unit-tested; `store/auth.ts` sends the requests.
 *
 * The refused sign-in's secrets (password, card number, QR token) are kept in this closure only — never in a store, never
 * persisted — and forgotten when the retry is taken, cancelled, or another sign-in succeeds.
 */
import type { AuthKind, AuthLoginRequest } from '@clubshell/contracts';

/** Secrets per `AuthKind`; only the field matching `kind` is used. */
export interface AuthCredentials {
  username?: string;
  password?: string;
  qrToken?: string;
  cardId?: string;
  token?: string;
}

/** `auth.login` request; `endElsewhere` only when the player confirmed it. */
export function buildLoginRequest(kind: AuthKind, creds: AuthCredentials, endElsewhere = false): AuthLoginRequest {
  return {
    kind,
    username: creds.username ?? null,
    password: creds.password ?? null,
    qrToken: creds.qrToken ?? null,
    cardId: creds.cardId ?? null,
    token: creds.token ?? null,
    ...(endElsewhere ? { endElsewhere: true } : {}),
  };
}

/** Where the player's session is open, from the refusal's `details`. */
export interface ElsewhereInfo {
  pcId: string | null;
  pcName: string | null;
}

/** `409 activeSessionElsewhere` (any shape with `code` and `details.reason`) → where; anything else → `null`. */
export function elsewhereOf(e: unknown): ElsewhereInfo | null {
  if (typeof e !== 'object' || e === null) {
    return null;
  }
  const { code, details } = e as { code?: unknown; details?: unknown };
  const d = typeof details === 'object' && details !== null ? (details as Record<string, unknown>) : {};
  if (code !== 'conflict' || d['reason'] !== 'activeSessionElsewhere') {
    return null;
  }
  return {
    pcId: typeof d['pcId'] === 'string' ? d['pcId'] : null,
    pcName: typeof d['pcName'] === 'string' ? d['pcName'] : null,
  };
}

export interface ElsewhereRetry {
  /** A refused sign-in is waiting for the player's answer. */
  readonly armed: boolean;
  /**
   * Sends `req`. A first attempt (not a retry, not a guest) refused with `activeSessionElsewhere` is kept for
   * {@link take}; a success forgets any kept one. Every error is rethrown.
   */
  send<R>(req: AuthLoginRequest, transport: (req: AuthLoginRequest) => Promise<R>): Promise<R>;
  /** The kept sign-in as an `endElsewhere: true` request, forgotten at once: one retry. `null` when none. */
  take(): AuthLoginRequest | null;
  /** Forgets the kept sign-in (cancel, logout, expiry). */
  clear(): void;
}

export function createElsewhereRetry(): ElsewhereRetry {
  let pending: AuthLoginRequest | null = null;
  return {
    get armed() {
      return pending !== null;
    },
    async send(req, transport) {
      try {
        const res = await transport(req);
        pending = null;
        return res;
      } catch (e) {
        pending = elsewhereOf(e) && !req.endElsewhere && req.kind !== 'guest' ? { ...req } : null;
        throw e;
      }
    },
    take() {
      const req = pending;
      pending = null;
      return req ? { ...req, endElsewhere: true } : null;
    },
    clear() {
      pending = null;
    },
  };
}
