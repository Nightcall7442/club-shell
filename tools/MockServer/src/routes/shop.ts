/**
 * Shop (SERVER_API.md §4.9): products with ETag, idempotent order creation charging the wallet, order polling and
 * listing, cancel with refund, and an automatic status progression pending → accepted → preparing → delivering → done
 * every 15 s that pushes `orderUpdated` (+ a notification when done).
 */
import type { FastifyInstance } from 'fastify';
import {
  ORDER_MAX_LINES,
  ORDER_MAX_QTY,
  ProductCategory,
  type Order,
  type OrderItem,
  type OrderStatus,
} from '@clubshell/contracts';
import {
  applyTransaction,
  arr,
  balanceOf,
  body,
  db,
  errors,
  findUser,
  idempotent,
  int,
  isObject,
  markDirty,
  now,
  optStr,
  paginate,
  requireAgent,
  requireUser,
  sendCached,
  str,
  uuid,
  uzs,
} from '../db.js';
import { pushToUser } from '../ws.js';

const CATEGORIES = Object.values(ProductCategory);
const PROGRESS_EVERY_MS = 15_000;
const NEXT: Partial<Record<OrderStatus, OrderStatus>> = {
  pending: 'accepted',
  accepted: 'preparing',
  preparing: 'delivering',
  delivering: 'done',
};

/** Wall-clock tick: advance active orders one step every 15 s. */
export function tickShop(nowMs: number): void {
  for (const order of db.orders) {
    const next = NEXT[order.status];
    if (!next || nowMs - Date.parse(order.updatedAt) < PROGRESS_EVERY_MS) continue;
    order.status = next;
    order.updatedAt = new Date(nowMs).toISOString();
    markDirty();
    pushToUser(order.userId, 'orderUpdated', order);
    if (next === 'done') {
      pushToUser(order.userId, 'notification', {
        id: uuid(),
        title: 'Order delivered',
        body: `Your order (${order.items.map((i) => `${i.title} ×${i.qty}`).join(', ')}) has been delivered. Enjoy!`,
        level: 'success',
        ttlSec: 8,
        action: null,
      });
    }
  }
}

export function shopRoutes(app: FastifyInstance): void {
  app.get<{ Querystring: { category?: string } }>('/shop/products', async (req, reply) => {
    requireAgent(req);
    const category = req.query.category;
    if (category && !(CATEGORIES as string[]).includes(category)) throw errors.validation('category', 'enum');
    return sendCached(req, reply, { items: db.products.filter((p) => !category || p.category === category) });
  });

  app.post('/shop/orders', async (req, reply) => {
    const { pc, user } = requireUser(req);
    return idempotent(req, reply, async () => {
      const b = body(req);
      if (str(b, 'userId', 64) !== user.id) throw errors.forbidden('notOwner');
      if (str(b, 'pcId', 64) !== pc.id) throw errors.forbidden('pcMismatch');
      optStr(b, 'sessionId', 64);
      const note = optStr(b, 'note', 300);
      if (user.flags.includes('noShop')) throw errors.policyDenied('noShop');
      const lines = arr(b, 'items', ORDER_MAX_LINES);
      if (lines.length === 0) throw errors.validation('items', 'required');
      const items: OrderItem[] = lines.map((line, i) => {
        if (!isObject(line)) throw errors.validation(`items[${i}]`, 'format');
        const productId = str(line, 'productId', 64);
        const qty = int(line, 'qty', 1, ORDER_MAX_QTY);
        const product = db.products.find((p) => p.id === productId);
        if (!product) throw errors.notFound('product');
        if (
          !product.inStock ||
          (product.stockQty !== null && product.stockQty !== undefined && product.stockQty < qty)
        ) {
          throw errors.conflict('outOfStock', { productId, available: product.stockQty ?? 0 });
        }
        return { productId, title: product.title, qty, price: product.price };
      });
      const total = uzs(items.reduce((sum, i) => sum + i.price.amount * i.qty, 0));
      if (user.balance.amount < total.amount) throw errors.insufficientFunds(total, user.balance);
      for (const line of items) {
        const product = db.products.find((p) => p.id === line.productId);
        if (product && product.stockQty !== null && product.stockQty !== undefined) {
          product.stockQty -= line.qty;
          product.inStock = product.stockQty > 0;
        }
      }
      const order: Order = {
        id: uuid(),
        userId: user.id,
        pcId: pc.id,
        items,
        total,
        status: 'pending',
        createdAt: now(),
        updatedAt: now(),
        note,
      };
      applyTransaction(
        user,
        'purchase',
        uzs(-total.amount),
        `Shop order · ${items.map((i) => i.title).join(', ')}`,
        order.id,
      );
      db.orders.unshift(order);
      if (db.orders.length > 300) db.orders.splice(300);
      markDirty();
      pushToUser(user.id, 'walletUpdated', balanceOf(user));
      pushToUser(user.id, 'orderUpdated', order);
      console.log(`[shop] ${pc.name}: order ${order.id.slice(0, 8)} for ${total.amount / 100} UZS`);
      return { status: 201, body: order };
    });
  });

  app.get<{ Querystring: { userId?: string; page?: string; pageSize?: string; activeOnly?: string } }>(
    '/shop/orders',
    async (req) => {
      const { user } = requireUser(req, req.query.userId ?? undefined);
      const activeOnly = req.query.activeOnly === 'true';
      const items = db.orders.filter(
        (o) => o.userId === user.id && (!activeOnly || (o.status !== 'done' && o.status !== 'cancelled')),
      );
      return paginate(items, req.query);
    },
  );

  app.get<{ Params: { id: string } }>('/shop/orders/:id', async (req) => {
    const { user } = requireUser(req);
    const order = db.orders.find((o) => o.id === req.params.id);
    if (!order) throw errors.notFound('order');
    if (order.userId !== user.id && user.role !== 'admin') throw errors.forbidden('notOwner');
    return order;
  });

  app.post<{ Params: { id: string } }>('/shop/orders/:id/cancel', async (req) => {
    const { user } = requireUser(req);
    const order = db.orders.find((o) => o.id === req.params.id);
    if (!order) throw errors.notFound('order');
    if (order.userId !== user.id) throw errors.forbidden('notOwner');
    if (order.status !== 'pending') throw errors.conflict('notPending', { status: order.status });
    order.status = 'cancelled';
    order.updatedAt = now();
    for (const line of order.items) {
      const product = db.products.find((p) => p.id === line.productId);
      if (product && product.stockQty !== null && product.stockQty !== undefined) {
        product.stockQty += line.qty;
        product.inStock = true;
      }
    }
    const owner = findUser(order.userId);
    if (owner) {
      applyTransaction(owner, 'refund', order.total, 'Refund: cancelled order', order.id);
      pushToUser(owner.id, 'walletUpdated', balanceOf(owner));
    }
    markDirty();
    pushToUser(order.userId, 'orderUpdated', order);
    return order;
  });
}
