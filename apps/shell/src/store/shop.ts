/**
 * Shop: product catalogue with category/search, a cart (`productId → qty`), placed orders and their live status
 * (`agent://shop.orderUpdated`). `placeOrder` is idempotent per cart snapshot.
 */
import {
  isOrderActive,
  ORDER_MAX_LINES,
  ORDER_MAX_QTY,
  type Money,
  type Order,
  type Product,
  type ProductCategory,
} from '@clubshell/contracts';
import { create } from 'zustand';
import { subscribeWithSelector } from 'zustand/middleware';
import { track } from '@/lib/analytics';
import { log } from '@/lib/logger';
import { api, toShellApiError, uuid, type ShellError } from '@/lib/tauri';
import { asShellError, type AsyncStatus } from './settings';

export interface CartLine {
  product: Product;
  qty: number;
}

export interface ShopState {
  products: Product[];
  category: ProductCategory | null;
  search: string;
  cart: ReadonlyMap<string, number>;
  orders: Order[];
  /** Order placed most recently in this UI session (status card). */
  lastOrder: Order | null;
  placing: boolean;
  ordersStatus: AsyncStatus;
  status: AsyncStatus;
  error: ShellError | null;
}

export interface ShopActions {
  /** Loads the full product list once (filters are client-side); `force` reloads. */
  load(force?: boolean): Promise<void>;
  loadOrders(activeOnly?: boolean): Promise<Order[]>;
  setCategory(category: ProductCategory | null): void;
  setSearch(search: string): void;
  addToCart(productId: string, qty?: number): void;
  removeFromCart(productId: string): void;
  setQty(productId: string, qty: number): void;
  clearCart(): void;
  /** Places the cart as one order; clears the cart on success; rethrows (`insufficientFunds`, …). */
  placeOrder(note?: string): Promise<Order>;
  onOrderUpdated(order: Order): void;
  reset(): void;
}

export type ShopStore = ShopState & ShopActions;

const initialState: ShopState = {
  products: [],
  category: null,
  search: '',
  cart: new Map(),
  orders: [],
  lastOrder: null,
  placing: false,
  ordersStatus: 'idle',
  status: 'idle',
  error: null,
};

let idempotencyKey: string | null = null;

export const useShopStore = create<ShopStore>()(
  subscribeWithSelector((set, get) => ({
    ...initialState,

    async load(force = false) {
      if (!force && (get().status === 'loading' || get().products.length > 0)) {
        return;
      }
      set({ status: 'loading', error: null });
      try {
        set({ products: await api.shop.products(), status: 'ready' });
      } catch (e) {
        const error = asShellError(e);
        log.warn('shop.load failed', error);
        set({ status: 'error', error });
      }
    },

    async loadOrders(activeOnly = false) {
      set({ ordersStatus: 'loading' });
      try {
        const res = await api.shop.orders({ activeOnly, pageSize: 20 });
        set({ orders: res.items, ordersStatus: 'ready' });
        return res.items;
      } catch (e) {
        log.warn('shop.orders failed', asShellError(e));
        set({ ordersStatus: 'error' });
        return get().orders;
      }
    },

    setCategory(category) {
      set({ category });
    },

    setSearch(search) {
      set({ search });
    },

    addToCart(productId, qty = 1) {
      set((s) => {
        const cart = new Map(s.cart);
        const next = Math.min(ORDER_MAX_QTY, (cart.get(productId) ?? 0) + qty);
        if (!cart.has(productId) && cart.size >= ORDER_MAX_LINES) {
          return {};
        }
        cart.set(productId, next);
        idempotencyKey = null;
        return { cart };
      });
    },

    removeFromCart(productId) {
      set((s) => {
        const cart = new Map(s.cart);
        cart.delete(productId);
        idempotencyKey = null;
        return { cart };
      });
    },

    setQty(productId, qty) {
      if (qty <= 0) {
        get().removeFromCart(productId);
        return;
      }
      set((s) => {
        const cart = new Map(s.cart);
        cart.set(productId, Math.min(ORDER_MAX_QTY, Math.round(qty)));
        idempotencyKey = null;
        return { cart };
      });
    },

    clearCart() {
      idempotencyKey = null;
      set({ cart: new Map() });
    },

    async placeOrder(note) {
      const items = Array.from(get().cart, ([productId, qty]) => ({ productId, qty }));
      if (items.length === 0) {
        throw toShellApiError({
          code: 'validation',
          message: 'Cart is empty',
          details: { field: 'items', reason: 'required' },
        });
      }
      idempotencyKey ??= uuid();
      set({ placing: true, error: null });
      try {
        const order = await api.shop.order({ items, idempotencyKey, note: note ?? null });
        idempotencyKey = null;
        set((s) => ({
          cart: new Map(),
          placing: false,
          lastOrder: order,
          orders: [order, ...s.orders.filter((o) => o.id !== order.id)],
        }));
        track('shop.order', { lines: items.length, total: order.total.amount });
        return order;
      } catch (e) {
        const err = toShellApiError(e);
        set({ placing: false, error: err.toJSON() });
        throw err;
      }
    },

    onOrderUpdated(order) {
      set((s) => ({
        orders: s.orders.some((o) => o.id === order.id)
          ? s.orders.map((o) => (o.id === order.id ? order : o))
          : [order, ...s.orders],
        lastOrder: s.lastOrder?.id === order.id ? order : s.lastOrder,
      }));
    },

    reset() {
      idempotencyKey = null;
      set(initialState);
    },
  })),
);

// ---------------------------------------------------------------------------------------------------------------------
// Selectors (memoized on their inputs)
// ---------------------------------------------------------------------------------------------------------------------

let filteredKey: { products: Product[]; category: ProductCategory | null; search: string } | null = null;
let filteredValue: Product[] = [];

/** Products matching category + search. */
export function selectFilteredProducts(s: ShopStore): Product[] {
  if (
    filteredKey &&
    filteredKey.products === s.products &&
    filteredKey.category === s.category &&
    filteredKey.search === s.search
  ) {
    return filteredValue;
  }
  const q = s.search.trim().toLowerCase();
  filteredKey = { products: s.products, category: s.category, search: s.search };
  filteredValue = s.products.filter(
    (p) =>
      (!s.category || p.category === s.category) &&
      (q.length === 0 || p.title.toLowerCase().includes(q) || p.tags.some((t) => t.toLowerCase().includes(q))),
  );
  return filteredValue;
}

let cartKey: { products: Product[]; cart: ReadonlyMap<string, number> } | null = null;
let cartValue: CartLine[] = [];

/** Cart as product lines (products missing from the catalogue are skipped). */
export function selectCartLines(s: ShopStore): CartLine[] {
  if (cartKey && cartKey.products === s.products && cartKey.cart === s.cart) {
    return cartValue;
  }
  cartKey = { products: s.products, cart: s.cart };
  cartValue = Array.from(s.cart, ([id, qty]) => {
    const product = s.products.find((p) => p.id === id);
    return product ? [{ product, qty }] : [];
  }).flat();
  return cartValue;
}

/** Cart total. */
export const selectCartTotal = (s: ShopStore): Money => ({
  amount: selectCartLines(s).reduce((sum, l) => sum + l.product.price.amount * l.qty, 0),
  currency: 'UZS',
});
/** Number of items in the cart (sum of quantities). */
export const selectCartCount = (s: ShopStore): number => Array.from(s.cart.values()).reduce((a, b) => a + b, 0);
/** Product categories present in the catalogue. */
export const selectCategories = (s: ShopStore): ProductCategory[] =>
  Array.from(new Set(s.products.map((p) => p.category)));
/** Orders still in progress. */
export const selectActiveOrders = (s: ShopStore): Order[] => s.orders.filter((o) => isOrderActive(o.status));
