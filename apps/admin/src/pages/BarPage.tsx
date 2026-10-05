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
 * Keys: the search is focused; Enter after a search adds the first product found (an empty search adds nothing); arrows
 * move between the products and Enter adds one; «+», «−» and Delete change the last line; Esc clears the search, then
 * the buyer. There is no pay hotkey, and the pay box shows none: its own keys work only inside it. While a sale is on its
 * way the cart cannot change.
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
import { GameArt } from '@/art';
import { ClientPicker, pcLabel } from '@/clientSearch';
import { useClub } from '@/club';
import { isTyping, onShowBar, onSignedOut, sheetOpen } from '@/desk';
import { amountOf, describe, isLostAnswer, reasonOf } from '@/errors';
import { exactDigits, money, moneyExact } from '@/format';
import { t } from '@/i18n';
import { CloseIcon, CupIcon, SearchIcon } from '@/icons';
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
import { Receipt, barAutoReceipt, printDocument, type ReceiptData } from '@/print';
import { useShift } from '@/shift';
import { Badge, Button, Chip, EmptyState, Kbd, Note, PanelHeader, Segmented, Sheet, inputCls } from '@/ui';

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

// …but not a sign-out: the next staff member never gets the last one's cart, client or frozen sale.
onSignedOut(() => {
  kept = null;
});

const GUEST: Buyer = { kind: 'guest' };

/** A cart line's − / + (32 px, quiet). */
const STEPPER =
  'focus-ring btn-tertiary inline-flex h-8 w-8 items-center justify-center rounded-md text-sm font-medium leading-none disabled:cursor-not-allowed disabled:opacity-40';

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

/**
 * An exact sum as a HUD readout: Doto digits to the tiyin (`26 000`, `26 000,50`), «сум» in Inter beside them. The
 * digits come right after whatever precedes the readout, with no space, so a line reads «Итого26 000 сум» as text.
 */
function DotoSum({
  minor,
  size,
  className,
  unitClassName,
}: {
  minor: number;
  size: number;
  className?: string;
  unitClassName?: string;
}): JSX.Element {
  return (
    <span className={clsx('inline-flex items-baseline gap-[0.3em] whitespace-nowrap', className)}>
      <span className="num-dot leading-none" style={{ fontSize: size }}>
        {exactDigits(minor)}
      </span>{' '}
      <span
        className={clsx('font-sans font-medium leading-none', unitClassName ?? 'text-muted')}
        style={{ fontSize: Math.max(10.5, Math.round(size * 0.46 * 2) / 2) }}
      >
        {t('сум')}
      </span>
    </span>
  );
}

/** Up to two initials of a name, for an avatar. */
function initialsOf(name: string): string {
  return name
    .trim()
    .split(/\s+/)
    .slice(0, 2)
    .map((w) => w.charAt(0).toUpperCase())
    .join('');
}

/**
 * One product of the grid (spec §9): its picture on the right fading in under scrims (a monogram when there is none),
 * the name, what is left in mono caps (amber when low, red «нет на складе» and dimmed when none), the price in Doto. In
 * the cart: an accent edge and «×2». The accessible name is «{title} · {price}» (the E2E finds a tile by its start), and
 * the name is the first text inside.
 */
function ProductTile({
  product: p,
  inCart,
  sellable: can,
  low,
  disabled,
  onAdd,
}: {
  product: Product;
  inCart: number;
  sellable: boolean;
  low: boolean;
  disabled: boolean;
  onAdd: () => void;
}): JSX.Element {
  return (
    <button
      type="button"
      data-product={p.id}
      disabled={disabled}
      aria-label={`${p.title} · ${money(p.price)}`}
      onClick={onAdd}
      className={clsx(
        'focus-ring group relative flex h-[120px] w-full flex-col justify-between rounded-md border bg-art pb-[13px] pl-3.5 pr-3 pt-3 text-left disabled:cursor-not-allowed',
        inCart > 0
          ? 'border-accent/50 shadow-[inset_0_0_22px_-10px_rgb(var(--c-accent)/0.55)]'
          : 'border-accent/[0.14] enabled:hover:border-accent/[0.26]',
        !can ? 'opacity-45' : 'disabled:opacity-50',
      )}
    >
      <GameArt
        src={p.imageUrl}
        variant="product"
        zoom={!disabled}
        fallback={
          <span className="absolute inset-0 bg-[linear-gradient(180deg,rgb(var(--c-accent)/0.06),rgb(var(--c-accent)/0)_70%)]">
            <span className="absolute -right-1 top-1/2 -translate-y-1/2 font-display text-[64px] font-medium leading-none text-accent/[0.09]">
              {p.title.trim().charAt(0).toUpperCase()}
            </span>
          </span>
        }
      />
      <span className="relative flex items-start justify-between gap-2">
        <span className="line-clamp-2 text-[13px] font-semibold leading-4 text-white [text-shadow:0_1px_2px_rgb(0_0_0/0.8)]">
          {p.title}
        </span>
        {inCart > 0 && (
          <Badge tone="accent" className="bg-bg/60 text-[10px]">
            ×{inCart}
          </Badge>
        )}
      </span>
      <span className="relative flex flex-col items-start gap-1.5">
        {!can ? (
          <span className="label-sm font-semibold text-danger-ink">{t('нет на складе')}</span>
        ) : p.stockQty != null ? (
          <span className={clsx('label-sm', low ? 'font-semibold text-warning' : 'text-artlabel')}>
            {t('осталось {n}', { n: p.stockQty })}
          </span>
        ) : null}
        <DotoSum
          minor={p.price.amount}
          size={16}
          className="text-white [text-shadow:0_0_8px_rgb(0_0_0/0.9),0_1px_3px_rgb(0_0_0/0.85)]"
          unitClassName="text-artlabel"
        />
      </span>
    </button>
  );
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
  /** A method sale is on its way: the cart stays as it was sent until the answer. */
  const [paying, setPaying] = useState(false);
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
  const locked = frozen !== null || busy || paying;
  // A frozen sale shows what was sent, not what the cart became.
  const cartLines = frozen?.items ?? lines;

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
    if (barAutoReceipt()) void printDocument(<Receipt r={receipt} />, 'receipt');
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
    setPaying(true);
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
    } finally {
      setPaying(false);
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
  const empty = cartLines.length === 0;
  const onSale = products.filter((p) => p.category !== 'time');
  const countOf = (c: string | null): number =>
    c === null ? onSale.length : onSale.filter((p) => p.category === c).length;
  const inCart = new Map(cartLines.map((l) => [l.productId, l.qty]));
  const units = cartLines.reduce((n, l) => n + l.qty, 0);
  // Why the buyer cannot change now (the segments' tooltip): a frozen sale, or one on its way.
  const buyerLocked = frozen
    ? t('Корзина заморожена, пока не ясно, прошла ли продажа. Повторите её — дважды она не проведётся.')
    : locked
      ? t('Идёт продажа')
      : null;

  return (
    <div className="grid h-full min-h-0 grid-cols-1 gap-4 lg:grid-cols-[minmax(0,1fr)_400px]">
      {/* The shelf: the page's primary surface, like the hall on the map. */}
      <div className="glass-panel edge-top flex min-h-0 flex-col px-5 pb-5 pt-[18px]">
        <div className="flex flex-wrap items-center justify-between gap-x-6 gap-y-3">
          <PanelHeader title={t('Бар')} level={1} />
          <div className="relative w-[300px] max-w-full">
            <label htmlFor={searchId} className="sr-only">
              {t('Поиск товара')}
            </label>
            <SearchIcon
              size={16}
              className="pointer-events-none absolute left-3.5 top-1/2 -translate-y-1/2 text-muted"
            />
            <input
              id={searchId}
              ref={searchRef}
              type="search"
              autoFocus
              autoComplete="off"
              className={clsx(inputCls, 'pl-10')}
              placeholder={t('Поиск товара')}
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  e.preventDefault();
                  // Only a search picks a product: an Enter in the empty field (out of habit, «Enter = cash») adds
                  // nothing and pays nothing.
                  if (e.repeat || search.trim() === '') return;
                  const first = shown.find(sellable);
                  if (first) add(first);
                } else if (e.key === 'ArrowDown') {
                  e.preventDefault();
                  gridRef.current?.querySelector<HTMLButtonElement>('button[data-product]:not(:disabled)')?.focus();
                }
              }}
            />
          </div>
        </div>
        {loadError && <Note note={{ text: loadError, tone: 'err' }} className="mt-3" />}
        <div role="group" aria-label={t('Категории')} className="-ml-3 mt-3 flex flex-wrap gap-1">
          {[null, ...CATEGORIES].map((c) => (
            <Chip key={c ?? 'all'} pressed={category === c} count={countOf(c)} onClick={() => setCategory(c)}>
              {c === null ? t('Все') : t(PRODUCT_CATEGORY_LABEL[c] ?? c)}
            </Chip>
          ))}
        </div>
        <ul
          ref={gridRef}
          aria-label={t('Товары')}
          onKeyDown={moveFocus}
          className="thin-scrollbar -mx-1.5 mt-2.5 grid min-h-0 flex-1 grid-cols-[repeat(auto-fill,minmax(168px,1fr))] content-start gap-2.5 overflow-y-auto p-1.5"
        >
          {shown.map((p) => {
            const can = sellable(p);
            return (
              <li key={p.id}>
                <ProductTile
                  product={p}
                  inCart={inCart.get(p.id) ?? 0}
                  sellable={can}
                  low={p.stockQty != null && p.stockQty <= lowAt}
                  disabled={!can || locked}
                  onAdd={() => add(p)}
                />
              </li>
            );
          })}
          {shown.length === 0 && !loadError && (
            <li className="col-span-full py-10">
              <EmptyState
                icon={<SearchIcon size={22} />}
                title={
                  products.length === 0
                    ? t('Товаров нет — владелец добавит их в «Магазин и склад»')
                    : t('Ничего не нашли')
                }
              />
            </li>
          )}
        </ul>
      </div>

      {/* The cart: the focused object, solid. */}
      <aside aria-label={t('Корзина')} className="panel-solid flex min-h-0 flex-col overflow-hidden">
        <div className="flex min-h-[60px] shrink-0 items-center px-4">
          <PanelHeader
            size="side"
            className="w-full"
            title={t('Корзина')}
            caption={empty ? undefined : `${units} ${t('шт')}`}
            aside={
              !empty && (
                <Button
                  variant="ghost"
                  size="sm"
                  className="-mr-2"
                  disabled={busy || paying}
                  onClick={() => (frozen ? setConfirmReset(true) : resetCart())}
                >
                  {t('Сбросить')}
                </Button>
              )
            }
          />
        </div>

        <div className="thin-scrollbar fade-y flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto px-4 pb-4 [@media(max-height:840px)]:gap-3">
          {done && (
            <Note tone="ok" role="status">
              <span className="flex items-center justify-between gap-3">
                <span>{done.text}</span>
                <ReceiptButton receipt={done.receipt} className="!h-8 shrink-0 !px-3 !text-xs" />
              </span>
              {done.change !== null && done.change > 0 && (
                <span className="mt-1 block font-semibold text-hi">
                  {t('Сдача: {sum}', { sum: moneyExact(done.change) })}
                </span>
              )}
            </Note>
          )}

          {empty ? (
            <EmptyState
              className="py-6"
              icon={<CupIcon size={22} />}
              title={t('Корзина пуста')}
              text={t('Нажмите на товар, чтобы добавить его в корзину.')}
            />
          ) : (
            <ul aria-label={t('Позиции')} className="-mt-1 flex flex-col">
              {cartLines.map((l) => {
                const p = byId.get(l.productId);
                const title = p?.title ?? '…';
                return (
                  <li
                    key={l.productId}
                    data-line={l.productId}
                    className="flex min-h-12 items-center gap-2 border-t border-accent/[0.07] py-1.5 first:border-t-0"
                  >
                    <span className="min-w-0 flex-1 truncate text-[13px] font-medium text-text">{title}</span>
                    <span className="flex items-center gap-1">
                      <button
                        type="button"
                        aria-label={t('Меньше: {title}', { title })}
                        disabled={locked}
                        onClick={() => setQty(l.productId, l.qty - 1)}
                        className={STEPPER}
                      >
                        −
                      </button>
                      <span
                        data-qty
                        className="tnum w-7 text-center font-mono text-[13px] font-semibold leading-none text-hi"
                      >
                        {l.qty}
                      </span>
                      <button
                        type="button"
                        aria-label={t('Больше: {title}', { title })}
                        disabled={locked || l.qty >= capOf(p)}
                        onClick={() => setQty(l.productId, l.qty + 1)}
                        className={STEPPER}
                      >
                        +
                      </button>
                    </span>
                    <span className="tnum w-[96px] whitespace-nowrap text-right font-mono text-[12.5px] font-semibold text-text">
                      {exactDigits((p?.price.amount ?? 0) * l.qty)}{' '}
                      <span className="font-sans font-medium text-muted">{t('сум')}</span>
                    </span>
                    <button
                      type="button"
                      aria-label={t('Убрать: {title}', { title })}
                      disabled={locked}
                      onClick={() => setQty(l.productId, 0)}
                      className="focus-ring btn-ghost -mr-1 inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-md text-muted disabled:cursor-not-allowed disabled:opacity-40"
                    >
                      <CloseIcon size={13} />
                    </button>
                  </li>
                );
              })}
            </ul>
          )}

          {!empty && (
            <div className="flex items-end justify-between gap-3 border-t border-accent/[0.08] pt-3.5">
              <span className="label pb-1">{t('Итого')}</span>
              <DotoSum minor={frozen?.total ?? total} size={30} className="text-hi" unitClassName="text-muted" />
            </div>
          )}

          <div className="flex flex-col gap-2.5 border-t border-accent/[0.08] pt-4">
            <span className="label-sm">{t('Покупатель')}</span>
            <Segmented
              label={t('Покупатель')}
              value={clientMode ? 'client' : 'guest'}
              options={[
                { id: 'guest', label: t('Гость'), disabled: buyerLocked },
                { id: 'client', label: t('Клиент'), disabled: buyerLocked },
              ]}
              onChange={(k) => {
                if (k === 'guest') clearBuyer();
                else setClientMode(true);
              }}
            />
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
              <div className="flex min-h-14 items-center gap-3 rounded-md border border-accent/40 bg-accent/[0.05] py-2 pl-3 pr-1.5">
                <span
                  aria-hidden="true"
                  className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full border border-accent/[0.22] bg-accent/[0.08] font-display text-[11px] font-semibold text-hi"
                >
                  {initialsOf(client.role === 'guest' ? t('Гость') : client.displayName)}
                </span>
                <span className="flex min-w-0 flex-1 flex-col gap-1">
                  <span className="truncate text-[13px] font-semibold leading-4 text-hi">
                    {client.role === 'guest' ? t('Гость') : client.displayName}
                  </span>
                  <span className="tnum truncate font-mono text-[11px] leading-4 text-muted">
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
                  className="focus-ring btn-ghost inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-md text-muted disabled:cursor-not-allowed disabled:opacity-40"
                  onClick={() => {
                    setBuyer(GUEST);
                    setShort(null);
                    setPayByMethod(false);
                    balanceKey.reset();
                  }}
                >
                  <CloseIcon size={14} />
                </button>
              </div>
            )}
            {client?.postpaid != null && (
              <Note tone="warn">{t('Идёт постоплата: покупка с баланса сократит время игры')}</Note>
            )}
          </div>

          {notice && (
            <Note tone="warn" role="status">
              {notice}
            </Note>
          )}

          {!empty && (clientMode ? client !== null : true) && (
            <section aria-label={t('Оплата')} className="flex flex-col gap-3 border-t border-accent/[0.08] pt-4">
              {client && (!viaMethod || frozen?.via === 'balance') && (
                <>
                  <ShiftClosedNote />
                  <Button
                    variant="primary"
                    size="lg"
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
                <p className="text-xs font-medium leading-5 text-warning">
                  {short !== null
                    ? t('На балансе доступно {sum} — оплата деньгами', { sum: moneyExact(short) })
                    : t('Не хватает {sum} на балансе', { sum: moneyExact(Math.max(0, total - spendable)) })}
                </p>
              )}
              {client && payByMethod && short !== null && (
                <p className="text-xs font-medium leading-5 text-warning">
                  {t('На балансе доступно {sum} — оплата деньгами', { sum: moneyExact(short) })}
                </p>
              )}
              {viaMethod && frozen?.via !== 'balance' && (
                <PayBox
                  key={saleId ?? 'none'}
                  exact={frozen?.total ?? total}
                  verb={t('Продать')}
                  autoFocus={false}
                  hints={false}
                  totalShown
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
                <Note tone="err" role="alert">
                  {error}
                </Note>
              )}
            </section>
          )}
          {frozen && (
            <p className="text-xs leading-5 text-muted">
              {t('Корзина заморожена, пока не ясно, прошла ли продажа. Повторите её — дважды она не проведётся.')}
            </p>
          )}
        </div>

        {/* The keys legend gives its room to the pay box up to a 960 px tall screen (1440×900 and 1366×768). */}
        <footer className="flex shrink-0 flex-wrap items-center gap-x-4 gap-y-1.5 border-t border-accent/[0.08] px-4 py-3 text-[11.5px] leading-5 text-muted [@media(max-height:960px)]:hidden">
          <span className="inline-flex items-center gap-1.5 whitespace-nowrap">
            <Kbd>+</Kbd> <Kbd>−</Kbd> <Kbd>Del</Kbd> {t('— последняя позиция')}
          </span>
          <span className="inline-flex items-center gap-1.5 whitespace-nowrap">
            <Kbd>Esc</Kbd> {t('— очистить поиск')}
          </span>
        </footer>
      </aside>

      {confirmReset && (
        <Sheet
          title={t('Сбросить корзину?')}
          onClose={() => setConfirmReset(false)}
          footer={
            <div className="ml-auto flex gap-2">
              <Button variant="ghost" autoFocus onClick={() => setConfirmReset(false)}>
                {t('Отмена')}
              </Button>
              <Button
                variant="danger"
                onClick={() => {
                  setConfirmReset(false);
                  resetCart();
                }}
              >
                {t('Сбросить')}
              </Button>
            </div>
          }
        >
          <Note tone="warn">
            {t(
              'Ответ на эту продажу не пришёл: она может уже быть в ленте операций. Проверьте ленту перед тем, как продать снова.',
            )}
          </Note>
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
