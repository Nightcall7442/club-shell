/**
 * The bar (cash desk part 3, beyond the contract, D-52..D-57), as the server books it: `POST /admin/shop/sales` sells
 * goods at the desk — a method sale (cash, card, Payme, Click, Uzum, `payment.amount` = `total`) moves no wallet and only
 * names the buyer, a balance sale (no payment, a `userId`) is a `purchase` row of −total, never into debt and never past
 * a running postpaid bill — and `POST /admin/shop/sales/{id}/void` takes one back with a reason (the stock comes back
 * unless it was a defect; a cashier within 15 minutes, the owner later; cash and other method sales only in their own
 * shift). The desk makes the sale's id (`saleId`, once per cart): a booked one answers 409 `saleExists {sale}` under any
 * key, and both routes refuse a key reused with another body (D-70). Goods sell at list price: the desk's `total` must be
 * the server's (409 `priceChanged {total, prices}`). A sale never writes `inStock` (the owner's switch). Every refusal is
 * found before anything changes, because `idempotent` has no rollback.
 */
import type { FastifyInstance } from 'fastify';
import { tariffPriceFor, type JsonObject, type Money, type Product } from '@clubshell/contracts';
import {
  ApiError,
  applyTransaction,
  balanceOf,
  body,
  db,
  enumOf,
  errors,
  findPc,
  findTariff,
  findUser,
  idempotent,
  isObject,
  markDirty,
  now,
  openSessionForUser,
  optStr,
  uuid,
  uzs,
  viewSession,
  type ProductRecord,
  type ShopSaleLine,
  type ShopSaleRecord,
  type UserRecord,
} from '../db.js';
import { PAY_METHODS, clubHooks, drawerNow, openShift, type PayMethod, type StaffRecord } from '../club.js';
import { record } from '../control.js';
import { pushToUser } from '../ws.js';
import { isApiKey, requireStaff } from './club.js';

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const MAX_LINES = 20;
const MAX_QTY = 99;
const MAX_TOTAL = 100_000_000;
/** A cashier voids a sale within this many minutes of it; the owner at any time (D-56). */
export const VOID_WINDOW_MIN = 15;
export const VOID_REASONS = ['mistake', 'returned', 'defect', 'other'] as const;
export const PRODUCT_CATEGORIES = ['food', 'drink', 'snack', 'service', 'merch', 'time'] as const;

/** Products still on sale lists: not archived. */
export function liveProducts(): ProductRecord[] {
  return db.products.filter((p) => !p.deletedAt);
}

/** The wire `Product`, without the desk's own fields. */
export function productView(p: ProductRecord): Product {
  const { source: _source, deletedAt: _deletedAt, deletedBy: _deletedBy, ...product } = p;
  return { ...product };
}

/** A sale as the desk reads it: who bought, on which PC, the lines as sold. */
export function saleView(s: ShopSaleRecord): JsonObject {
  const user = s.userId ? findUser(s.userId) : undefined;
  const pc = s.pcId ? findPc(s.pcId) : undefined;
  return {
    id: s.id,
    at: s.createdAt,
    shiftId: s.shiftId,
    method: s.method,
    total: s.total,
    staffName: s.staffName,
    user: user ? { id: user.id, displayName: user.displayName, role: user.role } : null,
    pc: pc ? { id: pc.id, name: pc.name } : null,
    lines: s.lines.map((l) => ({ ...l, amount: l.price * l.qty })),
  } as unknown as JsonObject;
}

/** The void of `saleId`, if any. */
export function voidOf(saleId: string): ShopSaleRecord | undefined {
  return db.shopSales.find((s) => s.kind === 'void' && s.voidOf === saleId);
}

/** `Coca-Cola ×2, Lay's` — the journal's detail of a sale. */
function linesDetail(lines: readonly ShopSaleLine[]): string {
  return lines.map((l) => (l.qty > 1 ? `${l.title} ×${l.qty}` : l.title)).join(', ');
}

function uuidField(v: unknown, field: string, required: boolean): string | null {
  if (v === undefined || v === null || v === '') {
    if (required) throw errors.validation(field, 'required');
    return null;
  }
  if (typeof v !== 'string' || !UUID.test(v)) throw errors.validation(field, 'format');
  return v.toLowerCase();
}

function intField(v: unknown, field: string, min: number, max: number): number {
  if (v === undefined || v === null) throw errors.validation(field, 'required');
  if (typeof v !== 'number' || !Number.isInteger(v)) throw errors.validation(field, 'format');
  if (v < min) throw errors.validation(field, 'min');
  if (v > max) throw errors.validation(field, 'max');
  return v;
}

interface SaleInput {
  saleId: string;
  items: { productId: string; qty: number }[];
  total: number;
  userId: string | null;
  pcId: string | null;
  payment: { method: PayMethod; amount: number } | null;
}

/** The body of a sale, with the server's field names and reasons. */
function readSale(b: Record<string, unknown>): SaleInput {
  const saleId = uuidField(b['saleId'], 'saleId', true) as string;
  const raw = b['items'];
  if (!Array.isArray(raw) || raw.length === 0) throw errors.validation('items', 'required');
  if (raw.length > MAX_LINES) throw errors.validation('items', 'max');
  const items = raw.map((x, i) => {
    if (!isObject(x)) throw errors.validation(`items[${i}]`, 'format');
    const productId = uuidField(x['productId'], `items[${i}].productId`, true) as string;
    return { productId, qty: intField(x['qty'], `items[${i}].qty`, 1, MAX_QTY) };
  });
  if (new Set(items.map((x) => x.productId)).size !== items.length) throw errors.validation('items', 'duplicate');
  const total = intField(b['total'], 'total', 1, MAX_TOTAL);
  let payment: SaleInput['payment'] = null;
  const p = b['payment'];
  if (p !== undefined && p !== null) {
    if (!isObject(p)) throw errors.validation('payment', 'format');
    const method = p['method'];
    if (typeof method !== 'string' || !(PAY_METHODS as readonly string[]).includes(method))
      throw errors.validation('payment.method', 'enum');
    const amount = intField(p['amount'], 'payment.amount', 1, MAX_TOTAL);
    if (amount !== total) throw errors.validation('payment.amount', 'total');
    payment = { method: method as PayMethod, amount };
  }
  const userId = uuidField(b['userId'], 'userId', payment === null);
  const pcId = uuidField(b['pcId'], 'pcId', false);
  return { saleId, items, total, userId, pcId, payment };
}

/**
 * What a balance sale may take (D-55): the balance, less what a running postpaid session has played up to a minute from
 * now. Never into debt: the member debt limit does not apply to the bar.
 */
export function spendableOf(user: UserRecord): number {
  const open = openSessionForUser(user.id);
  let reserve = 0;
  if (open && !open.isPrepaid) {
    const tariff = findTariff(open.tariffId);
    const used = viewSession(open).secondsUsed + 60;
    // What the end will charge for the time played so far, as `endSession` prices it.
    if (tariff) reserve = tariffPriceFor(tariff, Math.ceil(used / 60)).amount;
  }
  return Math.max(0, user.balance.amount - Math.max(0, reserve));
}

/** 403 `forbidden` with the void window (D-56). */
function voidWindow(): ApiError {
  return new ApiError('forbidden', 'Forbidden (voidWindow)', { reason: 'voidWindow', minutes: VOID_WINDOW_MIN });
}

export function barRoutes(app: FastifyInstance): void {
  app.post('/admin/shop/sales', async (req, reply) => {
    const staff: StaffRecord = requireStaff(req);
    // Before the key check, as the server: the club API key is not a person at the desk.
    if (isApiKey(req)) throw errors.forbidden('staffOnly');
    return idempotent(
      req,
      reply,
      async () => {
        const input = readSale(body(req));
        const shift = openShift();
        if (!shift) throw errors.conflict('shiftClosed');
        const booked = db.shopSales.find((s) => s.kind === 'sale' && s.id === input.saleId);
        if (booked) throw errors.conflict('saleExists', { sale: saleView(booked) });
        const user = input.userId ? findUser(input.userId) : null;
        if (input.userId && !user) throw errors.notFound('user');
        const pc = input.pcId ? findPc(input.pcId) : null;
        if (input.pcId && !pc) throw errors.notFound('pc');
        // Every line is checked before any stock moves, in productId order as the server decrements them.
        const ordered = [...input.items].sort((a, b) => a.productId.localeCompare(b.productId));
        const lines: { product: ProductRecord; qty: number }[] = [];
        for (const item of ordered) {
          const product = liveProducts().find((p) => p.id === item.productId);
          if (!product) throw errors.notFound('product');
          if (product.category === 'time') throw errors.conflict('notSellable', { productId: product.id });
          const tracked = product.stockQty !== null && product.stockQty !== undefined;
          if (!product.inStock || (tracked && (product.stockQty as number) < item.qty)) {
            throw errors.conflict('outOfStock', {
              productId: product.id,
              available: product.inStock ? (product.stockQty ?? 0) : 0,
            });
          }
          lines.push({ product, qty: item.qty });
        }
        const total = lines.reduce((sum, l) => sum + l.product.price.amount * l.qty, 0);
        if (total !== input.total) {
          throw errors.conflict('priceChanged', {
            total: uzs(total),
            prices: lines.map((l) => ({ productId: l.product.id, price: l.product.price })) as unknown as JsonObject[],
          });
        }
        const balanceSale = input.payment === null;
        if (balanceSale && user) {
          const available = spendableOf(user);
          if (available < total) throw errors.insufficientFunds(uzs(total), uzs(available));
        }

        // Book: the lines in the desk's order, as the cart showed them.
        const sold: ShopSaleLine[] = input.items.map((item) => {
          const product = lines.find((l) => l.product.id === item.productId)?.product as ProductRecord;
          return { productId: product.id, title: product.title, qty: item.qty, price: product.price.amount };
        });
        const before = new Map<string, number | null>();
        for (const { product, qty } of lines) {
          before.set(product.id, product.stockQty ?? null);
          if (product.stockQty !== null && product.stockQty !== undefined) product.stockQty -= qty;
        }
        let ledgerId: string | null = null;
        if (balanceSale && user) {
          ledgerId = applyTransaction(user, 'purchase', uzs(-total), `Бар: ${linesDetail(sold)}`, input.saleId).id;
          pushToUser(user.id, 'walletUpdated', balanceOf(user));
        }
        const sale: ShopSaleRecord = {
          id: input.saleId,
          kind: 'sale',
          voidOf: null,
          shiftId: shift.id,
          staffId: staff.id,
          staffName: staff.name,
          userId: user?.id ?? null,
          pcId: pc?.id ?? null,
          method: input.payment?.method ?? 'balance',
          total,
          ledgerId,
          reasonCode: null,
          note: null,
          createdAt: now(),
          lines: sold,
        };
        db.shopSales.push(sale);
        markDirty();
        record(staff, 'shopSale', {
          userId: sale.userId,
          pcId: sale.pcId,
          amount: total,
          detail: linesDetail(sold),
          meta: {
            saleId: sale.id,
            method: sale.method,
            ...(user && balanceSale ? { balance: user.balance.amount } : {}),
          },
        });
        for (const { product } of lines) {
          if (product.stockQty !== null && product.stockQty !== undefined)
            clubHooks.stockChanged(product.title, product.stockQty, before.get(product.id));
        }
        return {
          status: 201,
          body: {
            sale: saleView(sale),
            balance: balanceSale && user ? user.balance : null,
            // Copies: a replay answers with the stock right after this sale; in the cart's order, as the server.
            products: input.items.map((item) =>
              productView({ ...(lines.find((l) => l.product.id === item.productId)?.product as ProductRecord) }),
            ),
            expectedCash: drawerNow(shift),
          },
        };
      },
      { keyRequired: true, strictBody: true },
    );
  });

  app.post<{ Params: { id: string } }>('/admin/shop/sales/:id/void', async (req, reply) => {
    const staff: StaffRecord = requireStaff(req);
    if (isApiKey(req)) throw errors.forbidden('staffOnly');
    return idempotent(
      req,
      reply,
      async () => {
        const b = body(req);
        const reasonCode = enumOf(b, 'reasonCode', VOID_REASONS);
        const note = optStr(b, 'note', 10_000)?.trim() || null;
        if (reasonCode === 'other' && !note) throw errors.validation('note', 'required');
        if (note && note.length < 3 && reasonCode === 'other') throw errors.validation('note', 'min');
        if (note && note.length > 200) throw errors.validation('note', 'max');
        // The open shift first, then the sale: the server's order.
        const shift = openShift();
        if (!shift) throw errors.conflict('shiftClosed');
        const sale = db.shopSales.find((s) => s.kind === 'sale' && s.id === req.params.id.toLowerCase());
        if (!sale) throw errors.notFound('sale');
        if (voidOf(sale.id)) throw errors.conflict('alreadyVoided');
        if (staff.role !== 'owner' && Date.now() - Date.parse(sale.createdAt) > VOID_WINDOW_MIN * 60_000)
          throw voidWindow();
        // Cash and other method money goes back only in the shift that took it; a balance sale in any open shift.
        if (sale.method !== 'balance' && sale.shiftId !== shift.id) throw errors.conflict('saleShiftClosed');
        if (sale.method === 'cash') {
          const available = drawerNow(shift);
          if (available < sale.total) throw errors.conflict('cashShort', { available: uzs(available) });
        }
        const user = sale.userId ? findUser(sale.userId) : undefined;
        if (sale.method === 'balance' && !user) throw errors.notFound('user');

        const id = uuid();
        const restocked: ProductRecord[] = [];
        if (reasonCode !== 'defect') {
          for (const line of [...sale.lines].sort((a, b) => a.productId.localeCompare(b.productId))) {
            const product = liveProducts().find((p) => p.id === line.productId);
            if (product && product.stockQty !== null && product.stockQty !== undefined) {
              product.stockQty += line.qty;
              restocked.push(product);
            }
          }
        }
        let ledgerId: string | null = null;
        let balance: Money | null = null;
        if (sale.method === 'balance' && user) {
          // A `purchase` row of +total, never a `refund` (refunds count as time and raise a guest's payable).
          ledgerId = applyTransaction(user, 'purchase', uzs(sale.total), 'Бар: аннулирование', id).id;
          balance = user.balance;
          pushToUser(user.id, 'walletUpdated', balanceOf(user));
        }
        const at = now();
        const voided: ShopSaleRecord = {
          ...sale,
          id,
          kind: 'void',
          voidOf: sale.id,
          shiftId: shift.id,
          staffId: staff.id,
          staffName: staff.name,
          ledgerId,
          reasonCode,
          note,
          createdAt: at,
        };
        db.shopSales.push(voided);
        markDirty();
        record(staff, 'shopVoid', {
          userId: sale.userId,
          pcId: sale.pcId,
          amount: sale.total,
          detail: linesDetail(sale.lines),
          meta: { saleId: sale.id, voidId: id, method: sale.method, reasonCode, note, saleAt: sale.createdAt },
        });
        return {
          status: 200,
          body: {
            void: {
              id,
              at,
              saleId: sale.id,
              saleAt: sale.createdAt,
              method: sale.method,
              total: sale.total,
              reasonCode,
              note,
              staffName: staff.name,
            },
            balance,
            products: restocked.map((p) => productView({ ...p })),
            expectedCash: drawerNow(shift),
          },
        };
      },
      { keyRequired: true, strictBody: true },
    );
  });
}
