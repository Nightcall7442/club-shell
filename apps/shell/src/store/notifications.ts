/**
 * Notification queue: toasts (local + `notification.push`), admin messages that need an ack, the session warning
 * banner, remote-control indicator, update banners, connectivity and scheduled power. One shared interval
 * expires toasts. Error codes never reach the screen raw: {@link describeError} maps them to i18n.
 */
import type {
  AdminMessage,
  ConnectivityState,
  Money,
  Notification,
  NotificationAction,
  NotificationLevel,
  RemoteControlEvent,
  SessionWarning,
  UpdateProgress,
  UpdateReady,
} from '@clubshell/contracts';
import { create } from 'zustand';
import { subscribeWithSelector } from 'zustand/middleware';
import i18n, { currentLocale } from '@/i18n';
import { trackError } from '@/lib/analytics';
import { formatMoney } from '@/lib/format';
import { log } from '@/lib/logger';
import { api, toShellApiError, uuid } from '@/lib/tauri';

export type ToastSource = 'local' | 'agent' | 'system';

export interface Toast {
  id: string;
  title: string;
  body: string;
  level: NotificationLevel;
  /** Seconds until auto-dismiss; null = sticky. */
  ttlSec: number | null;
  action: NotificationAction | null;
  createdAt: number;
  expiresAt: number | null;
  source: ToastSource;
}

export interface PushInput {
  id?: string;
  title: string;
  body?: string;
  level?: NotificationLevel;
  /** Defaults per level: info/success 6 s, warning 10 s, error 12 s. `null` = sticky. */
  ttlSec?: number | null;
  action?: NotificationAction | null;
  source?: ToastSource;
}

export interface ScheduledPower {
  kind: 'reboot' | 'shutdown';
  /** Epoch ms. */
  at: number;
  message: string | null;
}

export interface NotificationsState {
  toasts: Toast[];
  /** Admin messages waiting for an acknowledgement (oldest first). */
  adminQueue: AdminMessage[];
  sessionWarning: SessionWarning | null;
  remoteControl: RemoteControlEvent | null;
  updateReady: UpdateReady | null;
  updateProgress: UpdateProgress | null;
  agentConnected: boolean;
  agentAttempts: number;
  serverConnectivity: ConnectivityState;
  scheduledPower: ScheduledPower | null;
}

export interface NotificationsActions {
  /** Adds a toast and returns its id (reusing `id` replaces an existing toast). */
  push(input: PushInput): string;
  /** Toast from a caught error: title = `context` (already translated) or a generic one, body = mapped message. */
  pushError(e: unknown, context?: string): string;
  /** Toast from an Agent `Notification`. */
  pushNotification(n: Notification): string;
  dismiss(id: string): void;
  dismissAll(): void;
  /** Admin message: queued when it requires an ack, otherwise shown as a toast. */
  pushAdmin(m: AdminMessage): void;
  ackAdmin(id: string): Promise<void>;
  /** Session warning banner + toast (ignored at 0 minutes: `session.ended` follows). */
  notifySessionWarning(w: SessionWarning): void;
  clearSessionWarning(): void;
  setRemoteControl(e: RemoteControlEvent): void;
  setUpdateReady(u: UpdateReady | null): void;
  setUpdateProgress(p: UpdateProgress | null): void;
  setAgentConnectivity(connected: boolean, attempts?: number): void;
  setServerConnectivity(state: ConnectivityState): void;
  setScheduledPower(p: ScheduledPower | null): void;
  /** Clears user-scoped items (toasts, admin queue, session warning); keeps device banners. */
  reset(): void;
}

export type NotificationsStore = NotificationsState & NotificationsActions;

const MAX_TOASTS = 5;
const DEFAULT_TTL: Readonly<Record<NotificationLevel, number>> = { info: 6, success: 6, warning: 10, error: 12 };

const initialState: NotificationsState = {
  toasts: [],
  adminQueue: [],
  sessionWarning: null,
  remoteControl: null,
  updateReady: null,
  updateProgress: null,
  agentConnected: true,
  agentAttempts: 0,
  serverConnectivity: 'online',
  scheduledPower: null,
};

function isMoney(v: unknown): v is Money {
  return (
    typeof v === 'object' &&
    v !== null &&
    typeof (v as Money).amount === 'number' &&
    typeof (v as Money).currency === 'string'
  );
}

/** Human message for any error (`ShellApiError`, plain `{code}` or unknown) via `errors.<code>`. */
export function describeError(e: unknown): string {
  const err = toShellApiError(e);
  const details = (typeof err.details === 'object' && err.details !== null ? err.details : {}) as Record<
    string,
    unknown
  >;
  const locale = currentLocale();
  const vars: Record<string, unknown> = {};
  if (err.code === 'insufficientFunds') {
    vars['required'] = isMoney(details['required']) ? formatMoney(details['required'], locale) : '—';
    vars['available'] = isMoney(details['available']) ? formatMoney(details['available'], locale) : '—';
  }
  if (err.code === 'rateLimited') {
    vars['seconds'] = typeof details['retryAfterSec'] === 'number' ? details['retryAfterSec'] : 30;
  }
  const key = `errors.${err.code}`;
  return i18n.exists(key) ? i18n.t(key, vars) : i18n.t('errors.generic');
}

// ponytail: one interval for every toast; a per-toast timeout is more code for no gain at ≤5 toasts.
let expiryTimer: ReturnType<typeof setInterval> | null = null;

function ensureExpiryTimer(): void {
  if (expiryTimer !== null) {
    return;
  }
  expiryTimer = setInterval(() => {
    const now = Date.now();
    const { toasts } = useNotificationsStore.getState();
    const alive = toasts.filter((t) => t.expiresAt === null || t.expiresAt > now);
    if (alive.length !== toasts.length) {
      useNotificationsStore.setState({ toasts: alive });
    }
    if (!alive.some((t) => t.expiresAt !== null)) {
      stopToastTimer();
    }
  }, 500);
}

/** Stops the expiry interval (teardown / tests). */
export function stopToastTimer(): void {
  if (expiryTimer !== null) {
    clearInterval(expiryTimer);
    expiryTimer = null;
  }
}

export const useNotificationsStore = create<NotificationsStore>()(
  subscribeWithSelector((set, get) => ({
    ...initialState,

    push(input) {
      const level = input.level ?? 'info';
      const ttlSec = input.ttlSec === undefined ? DEFAULT_TTL[level] : input.ttlSec;
      const now = Date.now();
      const toast: Toast = {
        id: input.id ?? uuid(),
        title: input.title,
        body: input.body ?? '',
        level,
        ttlSec,
        action: input.action ?? null,
        createdAt: now,
        expiresAt: ttlSec === null ? null : now + ttlSec * 1000,
        source: input.source ?? 'local',
      };
      set((s) => ({ toasts: [...s.toasts.filter((t) => t.id !== toast.id), toast].slice(-MAX_TOASTS) }));
      if (toast.expiresAt !== null) {
        ensureExpiryTimer();
      }
      return toast.id;
    },

    pushError(e, context) {
      const err = toShellApiError(e);
      trackError(err.code, context);
      log.warn(`ui error${context ? ` (${context})` : ''}: ${err.code} ${err.message}`);
      return get().push({
        title: context ?? i18n.t('notifications.error'),
        body: describeError(err),
        level: 'error',
        source: 'system',
      });
    },

    pushNotification(n) {
      return get().push({
        id: n.id,
        title: n.title,
        body: n.body,
        level: n.level,
        ttlSec: n.ttlSec ?? undefined,
        action: n.action ?? null,
        source: 'agent',
      });
    },

    dismiss(id) {
      set((s) => ({ toasts: s.toasts.filter((t) => t.id !== id) }));
    },

    dismissAll() {
      set({ toasts: [] });
    },

    pushAdmin(m) {
      if (m.requiresAck) {
        set((s) => (s.adminQueue.some((q) => q.id === m.id) ? {} : { adminQueue: [...s.adminQueue, m] }));
        return;
      }
      get().push({
        id: m.id,
        title: i18n.t('admin.messageTitle', { from: m.from }),
        body: m.text,
        level: m.level,
        ttlSec: 12,
        source: 'agent',
      });
    },

    async ackAdmin(id) {
      set((s) => ({ adminQueue: s.adminQueue.filter((m) => m.id !== id) }));
      try {
        await api.system.ackAdminMessage(id);
      } catch (e) {
        log.warn('ackAdminMessage failed', toShellApiError(e).toJSON());
      }
    },

    notifySessionWarning(w) {
      if (w.minutesLeft <= 0) {
        return;
      }
      set({ sessionWarning: w });
      get().push({
        id: `session-warning-${w.minutesLeft}`,
        title: i18n.t('session.warningTitle'),
        body:
          w.minutesLeft === 1
            ? i18n.t('session.lastMinute')
            : i18n.t('session.warningMinutes', { minutes: w.minutesLeft }),
        level: w.minutesLeft <= 5 ? 'error' : 'warning',
        ttlSec: 15,
        action: { label: i18n.t('session.addTime'), command: '/wallet', args: null },
        source: 'system',
      });
    },

    clearSessionWarning() {
      set({ sessionWarning: null });
    },

    setRemoteControl(e) {
      set({ remoteControl: e.state === 'started' && e.showIndicator ? e : null });
    },

    setUpdateReady(u) {
      set({ updateReady: u, updateProgress: u ? null : get().updateProgress });
    },

    setUpdateProgress(p) {
      set({ updateProgress: p });
    },

    setAgentConnectivity(connected, attempts = 0) {
      set({ agentConnected: connected, agentAttempts: attempts });
    },

    setServerConnectivity(state) {
      set({ serverConnectivity: state });
    },

    setScheduledPower(p) {
      set({ scheduledPower: p });
    },

    reset() {
      set({ toasts: [], adminQueue: [], sessionWarning: null, scheduledPower: null });
    },
  })),
);

/** Selector: the admin message currently requiring an ack (modal), if any. */
export const selectCurrentAdminMessage = (s: NotificationsStore): AdminMessage | null => s.adminQueue[0] ?? null;
