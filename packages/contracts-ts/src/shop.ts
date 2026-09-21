/**
 * Shop contracts — mirror of `ClubShell.Contracts.Shop` (Product.cs, Order.cs).
 */
import type { Money } from './wallet.js';

// ---- BEGIN MANUAL ----
import { multiplyMoney } from './wallet.js';
// ---- END MANUAL ----

/** Shop product category. */
export const ProductCategory = {
  Food: 'food',
  Drink: 'drink',
  Snack: 'snack',
  Service: 'service',
  Merch: 'merch',
  Time: 'time',
} as const;
/** Shop product category. */
export type ProductCategory = (typeof ProductCategory)[keyof typeof ProductCategory];

/** Shop product (IPC_PROTOCOL.md §6.13). */
export interface Product {
  /** Product id. */
  id: string;
  /** Localized title. */
  title: string;
  /** Category. */
  category: ProductCategory;
  /** Unit price. */
  price: Money;
  /** Image URL. */
  imageUrl: string;
  /** Availability; unknown when served from cache offline (shown as available). */
  inStock: boolean;
  /** Remaining quantity, when tracked. */
  stockQty?: number | null;
  /** Free-form tags. */
  tags: string[];
}

/** Order lifecycle. */
export const OrderStatus = {
  Pending: 'pending',
  Accepted: 'accepted',
  Preparing: 'preparing',
  Delivering: 'delivering',
  Done: 'done',
  Cancelled: 'cancelled',
} as const;
/** Order lifecycle. */
export type OrderStatus = (typeof OrderStatus)[keyof typeof OrderStatus];

// ---- BEGIN MANUAL ----
/** `true` while the order is still in progress (not `done` / `cancelled`). */
export function isOrderActive(status: OrderStatus): boolean {
  return status !== OrderStatus.Done && status !== OrderStatus.Cancelled;
}
// ---- END MANUAL ----

/** Line of a placed order; `price` is the unit price at order time. */
export interface OrderItem {
  /** Product id. */
  productId: string;
  /** Product title at order time. */
  title: string;
  /** Quantity (1–99). */
  qty: number;
  /** Unit price at order time. */
  price: Money;
}

// ---- BEGIN MANUAL ----
/** `price × qty` of an order line. */
export function orderLineTotal(item: OrderItem): Money {
  return multiplyMoney(item.price, item.qty);
}
// ---- END MANUAL ----

/** Line of an order being placed (`shop.order` / `POST /shop/orders`). */
export interface OrderLineRequest {
  /** Product id. */
  productId: string;
  /** Quantity (1–99). */
  qty: number;
}

/** Maximum quantity per order line. */
export const ORDER_MAX_QTY = 99;

/** Maximum lines per order. */
export const ORDER_MAX_LINES = 20;

/** Shop order (IPC_PROTOCOL.md §6.13). */
export interface Order {
  /** Order id. */
  id: string;
  /** Buyer. */
  userId: string;
  /** Seat to deliver to. */
  pcId: string;
  /** Lines. */
  items: OrderItem[];
  /** Total charged. */
  total: Money;
  /** Current status. */
  status: OrderStatus;
  /** Creation time. */
  createdAt: string;
  /** Last status change. */
  updatedAt: string;
  /** Free-text note for staff. */
  note?: string | null;
}

/** Body of `POST /shop/orders` (sent with an `Idempotency-Key`). */
export interface OrderCreateRequest {
  /** Buyer. */
  userId: string;
  /** Seat to deliver to. */
  pcId: string;
  /** Current session, when any. */
  sessionId?: string | null;
  /** Lines (1–20). */
  items: OrderLineRequest[];
  /** Free-text note for staff. */
  note?: string | null;
}
