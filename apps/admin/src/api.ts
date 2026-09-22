/**
 * Thin client of the mock server's cashier routes (`/api/v1/admin/*`, `tools/MockServer/src/routes/admin.ts`).
 * A real deployment points `VITE_ADMIN_API` at the operator's own server and replaces the static token with a
 * staff login; the shapes below are what the console renders.
 */
import type { Money, Session, ServerErrorEnvelope, Tariff, Transaction } from '@clubshell/contracts';

const BASE = (import.meta.env['VITE_ADMIN_API'] as string | undefined) ?? 'http://localhost:8080/api/v1';
const TOKEN = (import.meta.env['VITE_ADMIN_TOKEN'] as string | undefined) ?? 'admin-dev-token';

export interface SeatUser {
  id: string;
  displayName: string;
  role: string;
  balance: Money;
}

export interface Seat {
  pc: {
    id: string;
    name: string;
    zone: string;
    number: number;
    status: 'free' | 'busy' | 'locked' | 'maintenance' | 'booked' | 'offline';
  };
  session: Session | null;
  user: SeatUser | null;
}

export interface Member extends SeatUser {
  username: string;
}

export interface Overview {
  at: string;
  club: { free: number; total: number };
  seats: Seat[];
  tariffs: Tariff[];
  users: Member[];
}

/** Error carrying the server's `ErrorCode` so screens can map `insufficientFunds` and friends to copy. */
export class AdminError extends Error {
  constructor(
    readonly code: string,
    message: string,
    readonly details: Record<string, unknown> | null,
  ) {
    super(message);
    this.name = 'AdminError';
  }
}

async function call<T>(path: string, init?: RequestInit): Promise<T> {
  let res: Response;
  try {
    res = await fetch(`${BASE}${path}`, {
      ...init,
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${TOKEN}`, ...(init?.headers ?? {}) },
    });
  } catch (e) {
    throw new AdminError('network', e instanceof Error ? e.message : 'Network error', null);
  }
  if (!res.ok) {
    const envelope = (await res.json().catch(() => null)) as ServerErrorEnvelope | null;
    const err = envelope?.error;
    throw new AdminError(err?.code ?? 'internal', err?.message ?? res.statusText, err?.details ?? null);
  }
  return (await res.json()) as T;
}

const post = <T>(path: string, payload: unknown): Promise<T> =>
  call<T>(path, { method: 'POST', body: JSON.stringify(payload) });

export interface SessionResult {
  session: Session;
  charged: Money;
  balance?: Money;
  refunded?: Money;
}

export const adminApi = {
  overview: (): Promise<Overview> => call<Overview>('/admin/overview'),
  openSession: (input: { pcId: string; userId: string; tariffId: string; minutes: number }): Promise<SessionResult> =>
    post<SessionResult>('/admin/sessions', input),
  extend: (input: { pcId: string; minutes: number; tariffId?: string }): Promise<SessionResult> =>
    post<SessionResult>('/admin/sessions/extend', input),
  end: (input: { pcId: string }): Promise<SessionResult> => post<SessionResult>('/admin/sessions/end', input),
  topUp: (input: {
    userId: string;
    amount: number;
    method?: string;
  }): Promise<{
    balance: Money;
    transaction: Transaction;
  }> => post('/admin/wallet/topup', input),
  command: (
    pcId: string,
    input: { kind: 'message' | 'lock' | 'unlock' | 'reboot' | 'shutdown'; text?: string },
  ): Promise<{ ack: { ok: boolean; error?: { code: string; message: string } | null } }> =>
    post(`/admin/pcs/${pcId}/command`, input),
};
