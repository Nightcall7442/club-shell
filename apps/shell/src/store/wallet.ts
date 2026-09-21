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
import { syncServerTime } from '@/lib/time';
import { asShellError, type AsyncStatus } from './settings';

export interface WalletState {
  balance: Balance | null;
  tariffs: Tariff[];
  tariffZone: string | null;
  history: Transaction[];
  historyTotal: number;
  historyPage: number;
  historyType: TransactionType | null;
  historyStatus: AsyncStatus;
  topupIntent: TopupIntent | null;
  status: AsyncStatus;
  error: ShellError | null;
}

export interface WalletActions {
  /** Balance + tariffs; errors swallowed into `error`. */
  load(): Promise<void>;
  loadBalance(): Promise<Balance | null>;
  loadTariffs(zone?: string): Promise<Tariff[]>;
  /** Ledger page (1-based); `type` filters by transaction kind. */
  loadHistory(page?: number, type?: TransactionType | null): Promise<Transaction[]>;
  /** Creates a top-up intent (`amount` ≥ 1 000 UZS); rethrows validation errors. */
  createTopup(amount: Money, provider: TopupProvider): Promise<TopupIntent>;
  clearTopup(): void;
  onUpdated(balance: Balance): void;
  reset(): void;
}

export type WalletStore = WalletState & WalletActions;

const HISTORY_PAGE_SIZE = 20;

const initialState: WalletState = {
  balance: null,
  tariffs: [],
  tariffZone: null,
  history: [],
  historyTotal: 0,
  historyPage: 1,
  historyType: null,
  historyStatus: 'idle',
  topupIntent: null,
  status: 'idle',
  error: null,
};

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

    async loadHistory(page = 1, type = null) {
      set({ historyStatus: 'loading', historyPage: page, historyType: type });
      try {
        const res = await api.wallet.history({ page, pageSize: HISTORY_PAGE_SIZE, type });
        set((s) => ({
          history: page > 1 ? [...s.history, ...res.items] : res.items,
          historyTotal: res.total,
          historyStatus: 'ready',
        }));
        return res.items;
      } catch (e) {
        set({ historyStatus: 'error', error: asShellError(e) });
        return [];
      }
    },

    async createTopup(amount, provider) {
      set({ status: 'loading', error: null });
      try {
        const intent = await api.wallet.topupIntent(amount, provider);
        set({ topupIntent: intent, status: 'ready' });
        track('wallet.topupIntent', { provider, amount: amount.amount });
        return intent;
      } catch (e) {
        const err = toShellApiError(e);
        set({ status: 'error', error: err.toJSON() });
        throw err;
      }
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
      if (get().history.length > 0) {
        void get().loadHistory(1, get().historyType);
      }
    },

    reset() {
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
