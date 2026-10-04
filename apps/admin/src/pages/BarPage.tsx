/**
 * «Бар» (cash desk part 3, D-67): goods sold at the desk. On the left the club's products by category with a search,
 * each with its price and what is left; on the right the cart, the buyer and the payment. The buyer is «Гость» (a
 * walk-in: the product clicks plus the payment) and comes back to it after every sale; «Клиент» names a client — picked
 * from the search or handed over by the seat panel's «Бар» with the PC — who pays from the balance when it covers the
 * cart («Списать с баланса»), else by a method under their name. Goods sell at list price, no discounts.
 *
 * The cart gets its sale's id (`saleId`) with its first item, so one cart is never booked twice whatever key goes out
 * (D-52). A lost answer freezes the cart and the buyer: only the same sale can be sent again, under the same key (the pay
 * box's «Повторить»); «Сбросить» asks first, because the sale may already be in the feed. The cart outlives a trip to the
 * map and back. A refusal is shown and fixed where it can be: too few left sets the line to what is left, a new price
 * reloads the prices, a short balance switches to paying by a method. After a sale the slip prints.
 *
 * Keys: the search is focused; Enter in it adds the first product found; arrows move between the products and Enter adds
 * one; «+», «−» and Delete change the last line; Esc clears the search, then the buyer. There is no pay hotkey: the pay
 * box's own keys work only inside it.
 */
import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react';
import clsx from 'clsx';
import type { Product } from '@clubshell/contracts';
import {
  AdminError,
  adminApi,
  clubApi,
  newKey,
  type ClientHit,
  type PayMethod,
  type SaleInput,
  type SaleResponse,
  type Sale,
} from '@/api';
import { ClientPicker, pcLabel } from '@/clientSearch';
import { useClub } from '@/club';
import { isTyping, onShowBar, sheetOpen } from '@/desk';
import { amountOf, describe, isLostAnswer, reasonOf } from '@/errors';
import { money, moneyExact } from '@/format';
import { t } from '@/i18n';
import { PRODUCT_CATEGORY_LABEL } from '@/labels';
import {
  PayBox,
  ReceiptButton,
  ShiftClosedNote,
  TopUpSheet,
  methodName,
  useHeldKey,
  useShiftClosed,
  type Payment,
} from '@/paybox';
import { Receipt, printDocument, type ReceiptData } from '@/print';
import { useShift } from '@/shift';
import { Button, Kbd, Note, PageHeader, Sheet, inputCls } from '@/ui';

/** Most lines in one sale, most units of one line (the server's limits). */
const MAX_LINES = 20;
const MAX_QTY = 99;
const CATEGORIES = ['drink', 'food', 'snack', 'service', 'merch'] as const;

interface Line {
  productId: string;
  qty: number;
}

/** Who buys: a walk-in, or a client (with their PC and a running postpaid bill, which a balance purchase shortens). */
type Buyer =
  | { kind: 'guest' }
  | {
      kind: 'client';
      id: string;
      displayName: string;
      role: string;
      balance: number;
      pcId: string | null;
      pcName: string | null;
      /** The running postpaid bill of their session: the balance it holds back (D-55). */
      postpaid: number | null;
    };

/** What was sent: after a lost answer only this goes again (D-46). */
interface Sent {
  saleId: string;
  items: Line[];
  total: number;
  buyer: Buyer;
  /** `balance` — «Списать с баланса»; a method — the pay box. */
  via: 'balance' | 'method';
}

/** The bar's state outlives the page: a trip to the map keeps the cart, and a frozen sale stays frozen. */
interface Kept {
  lines: Line[];
  saleId: string | null;
  buyer: Buyer;
  frozen: Sent | null;
}

let kept: Kept | null = null;

const GUEST: Buyer = { kind: 'guest' };

/** The buyer of a seat or a client found, with what their PC's session holds back. */
async function buyerOf(id: string, fallback: ClientHit | null): Promise<Buyer | null> {
  const o = await adminApi.overview();
  const seat = o.seats.find((s) => s.user?.id === id && s.session);
  const user = seat?.user ?? null;
  if (!user && !fallback) return null;
  return {
    kind: 'client',
    id,
    displayName: user?.displayName ?? fallback?.displayName ?? '',
    role: user?.role ?? 'member',
    balance: user?.balance.amount ?? fallback?.balance.amount ?? 0,
    pcId: seat?.pc.id ?? fallback?.playing?.pcId ?? null,
    pcName: seat?.pc.name ?? fallback?.playing?.pcName ?? null,
    postpaid: seat?.session && !seat.session.isPrepaid ? seat.session.cost.amount : null,
  };
}

/** A product can be put in the cart: on sale, and some left when tracked. */
function sellable(p: Product): boolean {
  return p.inStock && (p.stockQty == null || p.stockQty > 0) && p.category !== 'time';
}

function linesOf(sale: Sale): ReceiptData['lines'] {
  return sale.lines.map((l) => ({ title: l.title, qty: l.qty, price: l.price }));
}

export default function BarPage(): JSX.Element {
  const club = useClub();
  const shift = useShift();
  const closed = useShiftClosed();
  const [products, setProducts] = useState<Product[]>([]);
  const [lowAt, setLowAt] = useState(0);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [category, setCategory] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [lines, setLines] = useState<Line[]>(kept?.lines ?? []);
  const [saleId, setSaleId] = useState<string | null>(kept?.saleId ?? null);
  const [buyer, setBuyer] = useState<Buyer>(kept?.buyer ?? GUEST);
  const [clientMode, setClientMode] = useState(kept?.buyer.kind === 'client');
  const [frozen, setFrozen] = useState<Sent | null>(kept?.frozen ?? null);
  /** The server said the balance is short (402): pay by a method, with what it said is available. */
  const [short, setShort] = useState<number | null>(null);
  const [payByMethod, setPayByMethod] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [done, setDone] = useState<{ text: string; change: number | null; receipt: ReceiptData } | null>(null);
  const [confirmReset, setConfirmReset] = useState(false);
  const [topUp, setTopUp] = useState(false);
  const balanceKey = useHeldKey();
  const searchRef = useRef<HTMLInputElement>(null);
  const gridRef = useRef<HTMLUListElement>(null);
  const searchId = useId();

  useEffect(() => {
    kept = { lines, saleId, buyer, frozen };
  }, [lines, saleId, buyer, frozen]);

  const load = useCallback(async (): Promise<void> => {
    try {
      const r = await clubApi.products();
      setProducts(r.items);
      setLowAt(r.lowAt);
      setLoadError(null);
    } catch (e) {
      setLoadError(describe(e));
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  // «Бар» from the seat panel: the seat's player and PC, once.
  useEffect(
    () =>
      onShowBar(({ userId }) => {
        if (kept?.frozen) return;
        buyerOf(userId, null)
          .then((b) => {
            if (!b) return;
            setBuyer(b);
            setClientMode(true);
            setShort(null);
            setPayByMethod(false);
          })
          .catch((e: unknown) => setError(describe(e)));
      }),
    [],
  );

  const byId = useMemo(() => new Map(products.map((p) => [p.id, p])), [products]);
  const total = lines.reduce((sum, l) => sum + (byId.get(l.productId)?.price.amount ?? 0) * l.qty, 0);
  const shown = useMemo(() => {
    const q = search.trim().toLowerCase();
    return products.filter(
      (p) =>
        p.category !== 'time' &&
        (category === null || p.category === category) &&
        (q === '' || p.title.toLowerCase().includes(q)),
    );
  }, [products, category, search]);
  const locked = frozen !== null || busy;

  /** Most of one product the cart may hold: what is left (tracked), at most 99. */
  const capOf = (p: Product | undefined): number =>
    p && p.stockQty != null ? Math.min(MAX_QTY, Math.max(0, p.stockQty)) : MAX_QTY;

  const add = (p: Product): void => {
    if (locked || !sellable(p)) return;
    setDone(null);
    setNotice(null);
    setError(null);
    setLines((list) => {
      const line = list.find((l) => l.productId === p.id);
      if (line) return list.map((l) => (l === line ? { ...l, qty: Math.min(capOf(p), l.qty + 1) } : l));
      if (list.length >= MAX_LINES) return list;
      return [...list, { productId: p.id, qty: 1 }];
    });
    if (!saleId) setSaleId(newKey());
  };

  const setQty = (productId: string, qty: number): void => {
    if (locked) return;
    setLines((list) =>
      qty <= 0
        ? list.filter((l) => l.productId !== productId)
        : list.map((l) => (l.productId === productId ? { ...l, qty: Math.min(capOf(byId.get(productId)), qty) } : l)),
    );
  };

  // An empty cart is a new sale next time.
  useEffect(() => {
    if (lines.length === 0 && !frozen) setSaleId(null);
  }, [lines.length, frozen]);

  const clearBuyer = (): void => {
    setBuyer(GUEST);
    setClientMode(false);
    setShort(null);
    setPayByMethod(false);
    balanceKey.reset();
  };

  const resetCart = (): void => {
    setLines([]);
    setSaleId(null);
    setFrozen(null);
    setError(null);
    setNotice(null);
    clearBuyer();
  };

  /** The sale of the cart as it is now (or the frozen one). */
  const snapshot = (via: Sent['via']): Sent | null =>
    frozen ?? (saleId && lines.length > 0 ? { saleId, items: lines.map((l) => ({ ...l })), total, buyer, via } : null);

  const send = (
    sent: Sent,
    payment: { method: PayMethod; amount: number } | null,
    key: string,
  ): Promise<SaleResponse> => {
    const input: SaleInput = {
      saleId: sent.saleId,
      items: sent.items,
      total: sent.total,
      ...(sent.buyer.kind === 'client' ? { userId: sent.buyer.id } : {}),
      ...(sent.buyer.kind === 'client' && sent.buyer.pcId ? { pcId: sent.buyer.pcId } : {}),
      ...(payment ? { payment } : {}),
    };
    return adminApi.shopSale(input, key);
  };

  /** A booked sale (a new one, or one the server already had): the slip, the stock, a fresh cart for a walk-in. */
  const finish = (sale: Sale, r: SaleResponse | null, sent: Sent, p: Payment | null): void => {
    const client = sent.buyer.kind === 'client' ? sent.buyer : null;
    const guest = !client || client.role === 'guest';
    const receipt: ReceiptData = {
      kind: 'sale',
      at: sale.at,
      ref: sale.id,
      club: club.clubName,
      cashier: sale.staffName,
      client: guest ? null : (client?.displayName ?? null),
      guest,
      pc: sale.pc?.name ?? null,
      lines: linesOf(sale),
      method: sale.method,
      total: sale.total,
      paid: sale.method !== 'balance' ? { amount: sale.total, method: sale.method } : null,
      received: p?.received ?? null,
      balance: r?.balance?.amount ?? null,
    };
    if (r) setProducts((list) => list.map((x) => r.products.find((y) => y.id === x.id) ?? x));
    else void load();
    setDone({
      text:
        sale.method === 'balance'
          ? t('Продано · {sum} · с баланса', { sum: moneyExact(sale.total) })
          : t('Продано · {sum} · {method}', { sum: moneyExact(sale.total), method: methodName(sale.method) }),
      change: p?.received != null ? p.received - sale.total : null,
      receipt,
    });
    setLines([]);
    setSaleId(null);
    setFrozen(null);
    setError(null);
    setNotice(null);
    clearBuyer();
    shift.refresh();
    void printDocument(<Receipt r={receipt} />, 'receipt');
    window.setTimeout(() => searchRef.current?.focus(), 0);
  };

  /** A definite refusal: fix the cart where the server says how; a lost answer freezes the sale. */
  const refused = (e: unknown, sent: Sent): void => {
    if (isLostAnswer(e)) {
      setFrozen(sent);
      return;
    }
    setFrozen(null);
    const reason = reasonOf(e);
    const d = e instanceof AdminError ? (e.details ?? {}) : {};
    if (reason === 'outOfStock' && typeof d['productId'] === 'string') {
      const available = typeof d['available'] === 'number' ? d['available'] : 0;
      const title = byId.get(d['productId'])?.title ?? '';
      setLines((list) =>
        available > 0
          ? list.map((l) => (l.productId === d['productId'] ? { ...l, qty: Math.min(l.qty, available) } : l))
          : list.filter((l) => l.productId !== d['productId']),
      );
      setNotice(
        available > 0
          ? t('{title}: осталось {n} — в корзине теперь {n}', { title, n: available })
          : t('{title}: закончился — убран из корзины', { title }),
      );
      void load();
    } else if (reason === 'priceChanged') {
      const prices = Array.isArray(d['prices']) ? (d['prices'] as { productId?: unknown; price?: unknown }[]) : [];
      setProducts((list) =>
        list.map((x) => {
          const hit = prices.find((y) => y.productId === x.id);
          const amount = hit ? amountOf(hit.price) : null;
          return amount === null ? x : { ...x, price: { ...x.price, amount } };
        }),
      );
      setNotice(t('Цены изменились — проверьте сумму и примите оплату снова'));
      void load();
    } else if (reason === 'notSellable' || (e instanceof AdminError && e.status === 404)) {
      void load();
    }
  };

  /** A sale the server already booked (`saleExists`) is done, whatever this try sent. */
  const bookedSale = (e: unknown): Sale | null => {
    if (reasonOf(e) !== 'saleExists' || !(e instanceof AdminError)) return null;
    const sale = e.details?.['sale'];
    return sale && typeof sale === 'object' ? (sale as Sale) : null;
  };

  const payMethod = async (p: Payment): Promise<void> => {
    const sent = snapshot('method');
    if (!sent) throw new Error(t('Корзина пуста'));
    try {
      const r = await send(sent, { method: p.method, amount: p.amount }, p.key);
      finish(r.sale, r, sent, p);
    } catch (e) {
      const booked = bookedSale(e);
      if (booked) {
        finish(booked, null, sent, p);
        return;
      }
      refused(e, sent);
      throw e;
    }
  };

  const payBalance = async (): Promise<void> => {
    const sent = snapshot('balance');
    if (!sent || busy) return;
    setBusy(true);
    setError(null);
    try {
      const r = await send(sent, null, balanceKey.take());
      balanceKey.settle();
      finish(r.sale, r, sent, null);
    } catch (e) {
      balanceKey.settle(e);
      const booked = bookedSale(e);
      if (booked) {
        finish(booked, null, sent, null);
        return;
      }
      refused(e, sent);
      if (e instanceof AdminError && e.code === 'insufficientFunds') {
        setShort(amountOf(e.details?.['available']) ?? 0);
        setPayByMethod(true);
      }
      setError(
        isLostAnswer(e)
          ? t('Ответ сервера не пришёл: продажа могла пройти. Повторите её — дважды она не проведётся.')
          : describe(e),
      );
    } finally {
      setBusy(false);
      shift.refresh();
    }
  };

  // Keys of the page: +/−/Delete on the last line, Esc clears the search, then the buyer. The listener is installed
  // once and reads this render's state and actions through the ref.
  const keys = useRef({ lines, search, clientMode, locked, setQty, clearBuyer });
  keys.current = { lines, search, clientMode, locked, setQty, clearBuyer };
  useEffect(() => {
    const on = (e: KeyboardEvent): void => {
      if (sheetOpen() || e.ctrlKey || e.metaKey || e.altKey) return;
      const k = keys.current;
      if (e.key === 'Escape') {
        if (k.search) setSearch('');
        else if (k.clientMode && !k.locked) k.clearBuyer();
        return;
      }
      if (isTyping(e) || k.locked) return;
      const last = k.lines.at(-1);
      if (!last) return;
      if (e.key === '+' || e.key === '=') {
        e.preventDefault();
        k.setQty(last.productId, last.qty + 1);
      } else if (e.key === '-') {
        e.preventDefault();
        k.setQty(last.productId, last.qty - 1);
      } else if (e.key === 'Delete') {
        e.preventDefault();
        k.setQty(last.productId, 0);
      }
    };
    window.addEventListener('keydown', on);
    return () => window.removeEventListener('keydown', on);
  }, []);

  /** Arrows move between the products of the grid (a row is the tiles with the same top). */
  const moveFocus = (e: React.KeyboardEvent<HTMLUListElement>): void => {
    const tiles = [...(gridRef.current?.querySelectorAll<HTMLButtonElement>('button[data-product]') ?? [])];
    const at = tiles.indexOf(document.activeElement as HTMLButtonElement);
    if (at < 0) return;
    const top = (el: HTMLElement): number => el.getBoundingClientRect().top;
    const perRow = Math.max(1, tiles.filter((x) => top(x) === top(tiles[0] as HTMLElement)).length);
    const step: Record<string, number> = { ArrowRight: 1, ArrowLeft: -1, ArrowDown: perRow, ArrowUp: -perRow };
    const d = step[e.key];
    if (d === undefined) return;
    e.preventDefault();
    tiles[Math.min(tiles.length - 1, Math.max(0, at + d))]?.focus();
  };

  const client = buyer.kind === 'client' ? buyer : null;
  const spendable = client ? Math.max(0, client.balance - (client.postpaid ?? 0)) : 0;
  const balanceCovers = client !== null && total > 0 && spendable >= total && short === null;
  const viaMethod = !client || !balanceCovers || payByMethod;
  const empty = lines.length === 0;

  return (
    <div className="grid h-full min-h-0 grid-cols-1 gap-5 lg:grid-cols-[minmax(0,1fr)_24rem]">
      <div className="flex min-h-0 flex-col gap-4">
        <PageHeader title={t('Бар')} />
        {loadError && <Note note={{ text: loadError, tone: 'err' }} />}
        <div className="flex flex-wrap items-center gap-2">
          <div role="group" aria-label={t('Категории')} className="flex flex-wrap gap-1.5">
            {[null, ...CATEGORIES].map((c) => (
              <Button
                key={c ?? 'all'}
                size="sm"
                aria-pressed={category === c}
                className={clsx(category === c && 'choice-on')}
                onClick={() => setCategory(c)}
              >
                {c === null ? t('Все') : t(PRODUCT_CATEGORY_LABEL[c] ?? c)}
              </Button>
            ))}
          </div>
          <label htmlFor={searchId} className="sr-only">
            {t('Поиск товара')}
          </label>
          <input
            id={searchId}
            ref={searchRef}
            type="search"
            autoFocus
            autoComplete="off"
            className={clsx(inputCls, 'ml-auto h-9 w-64')}
            placeholder={t('Поиск товара')}
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                e.preventDefault();
                if (e.repeat) return;
                const first = shown.find(sellable);
                if (first) add(first);
              } else if (e.key === 'ArrowDown') {
                e.preventDefault();
                gridRef.current?.querySelector<HTMLButtonElement>('button[data-product]:not(:disabled)')?.focus();
              }
            }}
          />
        </div>
        <ul
          ref={gridRef}
          aria-label={t('Товары')}
          onKeyDown={moveFocus}
          className="grid min-h-0 grid-cols-[repeat(auto-fill,minmax(9.5rem,1fr))] content-start gap-2 overflow-y-auto pr-1"
        >
          {shown.map((p) => {
            const can = sellable(p);
            const low = p.stockQty != null && p.stockQty <= lowAt;
            return (
              <li key={p.id}>
                <button
                  type="button"
                  data-product={p.id}
                  disabled={!can || locked}
                  aria-label={`${p.title} · ${money(p.price)}`}
                  onClick={() => add(p)}
                  className="focus-ring choice flex h-[5.5rem] w-full flex-col justify-between rounded-md px-3 py-2 text-left disabled:cursor-not-allowed disabled:opacity-40"
                >
                  <span className="line-clamp-2 text-sm font-medium leading-tight">{p.title}</span>
                  <span className="flex items-baseline justify-between gap-2">
                    <span className="tnum text-sm font-semibold">{money(p.price)}</span>
                    {!can ? (
                      <span className="text-xs text-danger">{t('нет')}</span>
                    ) : p.stockQty != null ? (
                      <span className={clsx('tnum text-xs', low ? 'text-warning' : 'text-muted')}>
                        {t('осталось {n}', { n: p.stockQty })}
                      </span>
                    ) : null}
                  </span>
                </button>
              </li>
            );
          })}
          {shown.length === 0 && !loadError && (
            <li className="col-span-full py-8 text-center text-sm text-muted">
              {products.length === 0
                ? t('Товаров нет — владелец добавит их в «Магазин и склад»')
                : t('Ничего не нашли')}
            </li>
          )}
        </ul>
      </div>

      <aside aria-label={t('Корзина')} className="panel flex min-h-0 flex-col gap-4 overflow-y-auto p-5">
        <header className="flex items-center justify-between gap-2">
          <h2 className="label text-text">{t('Корзина')}</h2>
          {!empty && (
            <Button
              variant="ghost"
              size="sm"
              disabled={busy}
              onClick={() => (frozen ? setConfirmReset(true) : resetCart())}
            >
              {t('Сбросить')}
            </Button>
          )}
        </header>

        {done && (
          <div role="status" className="flex flex-col gap-2 rounded-md bg-success/10 px-3 py-2 text-sm text-success">
            <div className="flex items-start justify-between gap-3">
              <p>{done.text}</p>
              <ReceiptButton receipt={done.receipt} className="h-8 shrink-0 px-2.5 text-xs" />
            </div>
            {done.change !== null && done.change > 0 && (
              <p className="font-semibold">{t('Сдача: {sum}', { sum: moneyExact(done.change) })}</p>
            )}
          </div>
        )}

        {empty ? (
          <p className="text-sm text-muted">{t('Нажмите на товар, чтобы добавить его в корзину.')}</p>
        ) : (
          <ul aria-label={t('Позиции')} className="flex flex-col divide-y divide-line">
            {lines.map((l) => {
              const p = byId.get(l.productId);
              const title = p?.title ?? '…';
              return (
                <li key={l.productId} data-line={l.productId} className="flex items-center gap-2 py-2">
                  <span className="min-w-0 flex-1 truncate text-sm">{title}</span>
                  <span className="flex items-center gap-1">
                    <button
                      type="button"
                      aria-label={t('Меньше: {title}', { title })}
                      disabled={locked}
                      onClick={() => setQty(l.productId, l.qty - 1)}
                      className="focus-ring choice h-7 w-7 rounded-md text-sm disabled:opacity-40"
                    >
                      −
                    </button>
                    <span data-qty className="tnum w-6 text-center text-sm font-semibold">
                      {l.qty}
                    </span>
                    <button
                      type="button"
                      aria-label={t('Больше: {title}', { title })}
                      disabled={locked || l.qty >= capOf(p)}
                      onClick={() => setQty(l.productId, l.qty + 1)}
                      className="focus-ring choice h-7 w-7 rounded-md text-sm disabled:opacity-40"
                    >
                      +
                    </button>
                  </span>
                  <span className="tnum w-24 text-right text-sm">{moneyExact((p?.price.amount ?? 0) * l.qty)}</span>
                  <button
                    type="button"
                    aria-label={t('Убрать: {title}', { title })}
                    disabled={locked}
                    onClick={() => setQty(l.productId, 0)}
                    className="focus-ring h-7 w-7 rounded-md text-muted hover:bg-white/[0.06] hover:text-text disabled:opacity-40"
                  >
                    ×
                  </button>
                </li>
              );
            })}
          </ul>
        )}

        {!empty && (
          <div className="flex items-baseline justify-between border-t border-line pt-3">
            <span className="label">{t('Итого')}</span>
            <span className="tnum text-xl font-semibold">{moneyExact(frozen?.total ?? total)}</span>
          </div>
        )}

        <div className="flex flex-col gap-2">
          <div role="group" aria-label={t('Покупатель')} className="grid grid-cols-2 gap-1.5">
            {(['guest', 'client'] as const).map((k) => {
              const on = k === 'client' ? clientMode : !clientMode;
              return (
                <Button
                  key={k}
                  aria-pressed={on}
                  disabled={locked}
                  className={clsx(on && 'choice-on')}
                  onClick={() => {
                    if (k === 'guest') clearBuyer();
                    else setClientMode(true);
                  }}
                >
                  {k === 'guest' ? t('Гость') : t('Клиент')}
                </Button>
              );
            })}
          </div>
          {clientMode && !client && (
            <ClientPicker
              label={t('Клиент')}
              value={null}
              allowPlaying
              autoFocus
              onChange={(hit) => {
                if (!hit) return;
                buyerOf(hit.id, hit)
                  .then((b) => {
                    if (b) setBuyer(b);
                  })
                  .catch((e: unknown) => setError(describe(e)));
              }}
            />
          )}
          {client && (
            <div className="flex items-center gap-2 rounded-md border border-accent/40 bg-accent/[0.05] px-3 py-2">
              <span className="flex min-w-0 flex-1 flex-col">
                <span className="truncate text-sm font-medium">
                  {client.role === 'guest' ? t('Гость') : client.displayName}
                </span>
                <span className="tnum font-mono text-[0.7rem] text-muted">
                  {[
                    t('баланс {sum}', { sum: moneyExact(client.balance) }),
                    client.pcName ? pcLabel(client.pcName) : null,
                  ]
                    .filter(Boolean)
                    .join(' · ')}
                </span>
              </span>
              <button
                type="button"
                aria-label={t('Сменить клиента')}
                title={t('Сменить клиента')}
                disabled={locked}
                className="focus-ring h-7 w-7 shrink-0 rounded-md text-muted hover:bg-white/[0.06] hover:text-text disabled:opacity-40"
                onClick={() => {
                  setBuyer(GUEST);
                  setShort(null);
                  setPayByMethod(false);
                  balanceKey.reset();
                }}
              >
                ×
              </button>
            </div>
          )}
          {client?.postpaid != null && (
            <p className="rounded-md bg-warning/10 px-3 py-1.5 text-xs text-warning">
              {t('Идёт постоплата: покупка с баланса сократит время игры')}
            </p>
          )}
        </div>

        {notice && <p className="rounded-md bg-warning/10 px-3 py-2 text-sm text-warning">{notice}</p>}

        {!empty && (clientMode ? client !== null : true) && (
          <section aria-label={t('Оплата')} className="flex flex-col gap-3">
            {client && (!viaMethod || frozen?.via === 'balance') && (
              <>
                <ShiftClosedNote />
                <Button
                  variant="primary"
                  className="h-11"
                  disabled={busy || closed || (frozen !== null && frozen.via !== 'balance')}
                  onClick={() => void payBalance()}
                >
                  {busy
                    ? '…'
                    : frozen
                      ? t('Повторить списание · {sum}', { sum: moneyExact(frozen.total) })
                      : t('Списать с баланса · {sum}', { sum: moneyExact(total) })}
                </Button>
                {!frozen && (
                  <Button variant="ghost" size="sm" onClick={() => setPayByMethod(true)}>
                    {t('Оплатить деньгами')}
                  </Button>
                )}
              </>
            )}
            {client && viaMethod && !payByMethod && (
              <p className="text-sm text-warning">
                {short !== null
                  ? t('На балансе доступно {sum} — оплата деньгами', { sum: moneyExact(short) })
                  : t('Не хватает {sum} на балансе', { sum: moneyExact(Math.max(0, total - spendable)) })}
              </p>
            )}
            {client && payByMethod && short !== null && (
              <p className="text-sm text-warning">
                {t('На балансе доступно {sum} — оплата деньгами', { sum: moneyExact(short) })}
              </p>
            )}
            {viaMethod && frozen?.via !== 'balance' && (
              <PayBox
                key={saleId ?? 'none'}
                exact={frozen?.total ?? total}
                verb={t('Продать')}
                autoFocus={false}
                disabled={total <= 0 ? t('Корзина пуста') : null}
                onPay={payMethod}
              />
            )}
            {client && client.role !== 'guest' && viaMethod && !frozen && (
              <Button variant="ghost" size="sm" onClick={() => setTopUp(true)}>
                {t('Пополнить баланс')}
              </Button>
            )}
            {error && (
              <p role="alert" className="rounded-md bg-danger/10 px-3 py-2 text-sm text-danger">
                {error}
              </p>
            )}
          </section>
        )}
        {frozen && (
          <p className="text-xs text-muted">
            {t('Корзина заморожена, пока не ясно, прошла ли продажа. Повторите её — дважды она не проведётся.')}
          </p>
        )}
        <p className="mt-auto text-xs text-muted">
          <Kbd>+</Kbd> <Kbd>−</Kbd> <Kbd>Del</Kbd> {t('— последняя позиция')} · <Kbd>Esc</Kbd> {t('— очистить поиск')}
        </p>
      </aside>

      {confirmReset && (
        <Sheet title={t('Сбросить корзину?')} onClose={() => setConfirmReset(false)}>
          <p className="text-sm">
            {t(
              'Ответ на эту продажу не пришёл: она может уже быть в ленте операций. Проверьте ленту перед тем, как продать снова.',
            )}
          </p>
          <div className="flex justify-end gap-2 border-t border-line pt-4">
            <Button variant="ghost" autoFocus onClick={() => setConfirmReset(false)}>
              {t('Отмена')}
            </Button>
            <Button
              variant="danger"
              className="border border-danger/50"
              onClick={() => {
                setConfirmReset(false);
                resetCart();
              }}
            >
              {t('Сбросить')}
            </Button>
          </div>
        </Sheet>
      )}
      {topUp && client && (
        <TopUpSheet
          payee={{
            id: client.id,
            displayName: client.displayName,
            balance: { amount: client.balance, currency: 'UZS' },
          }}
          initial={Math.max(0, total - spendable)}
          onClose={() => setTopUp(false)}
          onDone={() => {
            // The new balance: the lookup knows it (and the seat, when the client plays).
            adminApi
              .lookupClients(client.displayName)
              .then((r) => buyerOf(client.id, r.items.find((x) => x.id === client.id) ?? null))
              .then((b) => {
                if (b) setBuyer(b);
                setShort(null);
                setPayByMethod(false);
              })
              .catch(() => undefined);
          }}
        />
      )}
    </div>
  );
}
