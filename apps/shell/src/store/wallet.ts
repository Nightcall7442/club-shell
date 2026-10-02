/**
 * Wallet: balance (kept in sync by `agent://wallet.updated`), tariffs of this PC's zone, paged ledger and the
 * current top-up intent (QR / deep link) until it settles.
 */
import type {
  Balance,
  Money,
  Tariff,
  TopupIntent,
  TopupProvider,
  Transaction,
  TransactionType,
} from '@clubshell/contracts';
import { create } from 'zustand';
import { subscribeWithSelector } from 'zustand/middleware';
import { track } from '@/lib/analytics';
import { log } from '@/lib/logger';
import { api, toShellApiError, type ShellError } from '@/lib/tauri';
import { serverNowMs, syncServerTime } from '@/lib/time';
import { asShellError, type AsyncStatus } from './settings';

/** History period; sent as `from`, so the server pages only that period. */
export type WalletHistoryRange = 'all' | 'today' | 'week' | 'month';

export interface WalletState {
  balance: Balance | null;
  tariffs: Tariff[];
  tariffZone: string | null;
  history: Transaction[];
  historyTotal: number;
  historyPage: number;
  historyType: TransactionType | null;
  historyRange: WalletHistoryRange;
  historyStatus: AsyncStatus;
  /** Why the last ledger page failed; shown in the history panel, never as a toast. */
  historyError: ShellError | null;
  topupIntent: TopupIntent | null;
  status: AsyncStatus;
  error: ShellError | null;
}

export interface WalletActions {
  /** Balance + tariffs; errors swallowed into `error`. */
  load(): Promise<void>;
  loadBalance(): Promise<Balance | null>;
  loadTariffs(zone?: string): Promise<Tariff[]>;
  /** Ledger page (1-based); `type` filters by transaction kind, `range` by period (default: the current one). */
  loadHistory(page?: number, type?: TransactionType | null, range?: WalletHistoryRange): Promise<Transaction[]>;
  /** Creates a top-up intent (`amount` ≥ 1 000 UZS); rethrows every error for the dialog to explain. */
  createTopup(amount: Money, provider: TopupProvider): Promise<TopupIntent>;
  clearTopup(): void;
  onUpdated(balance: Balance): void;
  reset(): void;
}

export type WalletStore = WalletState & WalletActions;

const HISTORY_PAGE_SIZE = 20;
const DAY_MS = 86_400_000;

/** Lower bound (epoch ms) of a period, `null` for `all`; `today` starts at local midnight. */
export function rangeStart(range: WalletHistoryRange, now: number = serverNowMs()): number | null {
  switch (range) {
    case 'today': {
      const d = new Date(now);
      d.setHours(0, 0, 0, 0);
      return d.getTime();
    }
    case 'week':
      return now - 7 * DAY_MS;
    case 'month':
      return now - 30 * DAY_MS;
    default:
      return null;
  }
}

/**
 * Errors after which the club can still take the money at the counter: the server has no payment provider (501), the
 * club turned online top-up off (`policyDenied features.topup`), or the server or Agent cannot be reached.
 */
export function isTopupUnavailable(e: unknown): boolean {
  const { code } = toShellApiError(e);
  return (
    code === 'notImplemented' || code === 'policyDenied' || code === 'serverUnavailable' || code === 'agentOffline'
  );
}

const initialState: WalletState = {
  balance: null,
  tariffs: [],
  tariffZone: null,
  history: [],
  historyTotal: 0,
  historyPage: 1,
  historyType: null,
  historyRange: 'all',
  historyStatus: 'idle',
  historyError: null,
  topupIntent: null,
  status: 'idle',
  error: null,
};

// Only the newest ledger request may write: a filter tapped twice quickly must not end on the older answer.
let historySeq = 0;

export const useWalletStore = create<WalletStore>()(
  subscribeWithSelector((set, get) => ({
    ...initialState,

    async load() {
      set({ status: 'loading', error: null });
      const [balance, tariffs] = await Promise.allSettled([api.wallet.balance(), api.wallet.tariffs()]);
      const patch: Partial<WalletState> = { status: 'ready' };
      if (balance.status === 'fulfilled') {
        patch.balance = balance.value;
      } else {
        patch.status = 'error';
        patch.error = asShellError(balance.reason);
        log.warn('wallet.balance failed', patch.error);
      }
      if (tariffs.status === 'fulfilled') {
        syncServerTime(tariffs.value.serverTime);
        patch.tariffs = tariffs.value.items;
        patch.tariffZone = tariffs.value.zone;
      } else {
        log.warn('wallet.tariffs failed', asShellError(tariffs.reason));
      }
      set(patch);
    },

    async loadBalance() {
      try {
        const balance = await api.wallet.balance();
        set({ balance, error: null });
        return balance;
      } catch (e) {
        set({ error: asShellError(e) });
        return get().balance;
      }
    },

    async loadTariffs(zone) {
      try {
        const res = await api.wallet.tariffs(zone);
        syncServerTime(res.serverTime);
        set({ tariffs: res.items, tariffZone: res.zone });
        return res.items;
      } catch (e) {
        log.warn('wallet.tariffs failed', asShellError(e));
        return get().tariffs;
      }
    },

    async loadHistory(page = 1, type = null, range = get().historyRange) {
      const seq = ++historySeq;
      const start = rangeStart(range);
      set((s) => ({
        historyStatus: 'loading',
        historyPage: page,
        historyType: type,
        historyRange: range,
        historyError: null,
        // A new filter starts from an empty list instead of showing the old one under the new tab.
        history: page === 1 && (type !== s.historyType || range !== s.historyRange) ? [] : s.history,
      }));
      try {
        const res = await api.wallet.history({
          page,
          pageSize: HISTORY_PAGE_SIZE,
          type,
          from: start === null ? null : new Date(start).toISOString(),
        });
        if (seq !== historySeq) {
          return res.items;
        }
        set((s) => ({
          history: page > 1 ? [...s.history, ...res.items] : res.items,
          historyTotal: res.total,
          historyStatus: 'ready',
        }));
        return res.items;
      } catch (e) {
        if (seq === historySeq) {
          const error = asShellError(e);
          log.warn('wallet.history failed', error);
          set({ historyStatus: 'error', historyError: error });
        }
        return [];
      }
    },

    async createTopup(amount, provider) {
      // `status`/`error` belong to the balance load: a refused top-up must not blank the balance card.
      const intent = await api.wallet.topupIntent(amount, provider);
      set({ topupIntent: intent });
      track('wallet.topupIntent', { provider, amount: amount.amount });
      return intent;
    },

    clearTopup() {
      set({ topupIntent: null });
    },

    onUpdated(balance) {
      set((s) => {
        const intent = s.topupIntent;
        const settled = intent && s.balance && balance.amount.amount >= s.balance.amount.amount + intent.amount.amount;
        return { balance, topupIntent: settled ? { ...intent, status: 'paid' } : intent };
      });
      if (get().history.length > 0 || get().historyStatus !== 'idle') {
        void get().loadHistory(1, get().historyType);
      }
    },

    reset() {
      historySeq += 1;
      set(initialState);
    },
  })),
);

/** Selector: main balance amount (0 UZS before load). */
export const selectBalanceAmount = (s: WalletStore): Money => s.balance?.amount ?? { amount: 0, currency: 'UZS' };
/** Selector: more ledger pages available. */
export const selectHistoryHasMore = (s: WalletStore): boolean => s.history.length < s.historyTotal;
/** Selector factory: tariff by id. */
export const selectTariff =
  (tariffId: string | null | undefined) =>
  (s: WalletStore): Tariff | null =>
    (tariffId ? s.tariffs.find((t) => t.id === tariffId) : undefined) ?? null;
