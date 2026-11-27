/**
 * The shift's operations as the counter did them (D-43), newest first: who, for whom and on which PC, what (a seat, a
 * top-up, a cash move…), how it was paid and what it did to the drawer, with a ⎙ that reprints the slip as a «Копия».
 * A paid seat is one row with its payment merged in (the server leaves the session's top-up out). Today's money by
 * method is the KPI strip's «Принято за смену» (`kpi.tsx`).
 *
 * Variant F: a row is time | what and who | the drawer's amount (mono; money is never coloured by its sign: «+» in the
 * text colour, «−» dimmed, «∞» for a postpaid seat). The row's ⎙ and «Аннулировать…» are small icon buttons that show on
 * hover or focus (always on a touch screen). On the map the feed heads itself «Операции смены» with a «Журнал ›» link to
 * the Смена page; on the Смена page its section does.
 *
 * A desk top-up has «Исправить / отменить…» (D-91): the right amount in one step, or the whole top-up back by its method.
 *
 * The owner's own operations (D-101) are in no shift's feed: `owner` lists them instead (the Смена page's «Операции
 * владельца»), each row marked «Владелец»; the owner takes their own sale or top-up back from there at any time.
 *
 * Polls `GET /admin/shift/operations` every 5 s while the page is visible and refetches when the shift's money changes
 * ({@link useShift}`.version`); «Ещё» pages back with the server's cursor. A server without the route (404) hides it.
 */
import { useCallback, useEffect, useRef, useState } from 'react';
import clsx from 'clsx';
import {
  AdminError,
  adminApi,
  clubApi,
  type Operation,
  type OperationKind,
  type OperationsPage,
  type SaleVoid,
  type TopUpCancelReason,
  type TopUpCancelResult,
  type VoidReason,
} from '@/api';
import { pcLabel } from '@/clientSearch';
import { useClub } from '@/club';
import { describe, isLostAnswer } from '@/errors';
import { exactDigits, minutesLabel, moneyExact } from '@/format';
import { dateLocale, t } from '@/i18n';
import { ChevronRightIcon, PrintIcon, pathIcon } from '@/icons';
import {
  OPERATION_LABEL,
  REASON_LABEL,
  TOPUP_CANCEL_REASONS,
  TOPUP_CANCEL_REASON_LABEL,
  VOID_REASONS,
  VOID_REASON_LABEL,
} from '@/labels';
import { methodName, useHeldKey } from '@/paybox';
import { CashSlip, Receipt, printDocument, type ReceiptData } from '@/print';
import { ChoiceButton, useShift } from '@/shift';
import { Button, EmptyState, Field, Note, PanelHeader, Segmented, Sheet, inputCls } from '@/ui';

/** «Аннулировать…» of a row: an arrow turning back (the shared set has no undo mark). */
const UndoIcon = pathIcon('M9 14 4 9l5-5M4 9h10.5a5.5 5.5 0 0 1 0 11H11', 'UndoIcon');

const POLL_MS = 5000;
/** A cashier takes a bar sale back within this many minutes of it; the owner later (D-56). */
const VOID_WINDOW_MIN = 15;
/** A cashier takes back their own desk top-up within this many minutes of it; the owner any of the open shift (D-91). */
const CANCEL_WINDOW_MIN = 15;

/** Kinds that have a slip to reprint. */
const RECEIPT_KIND: Partial<Record<OperationKind, ReceiptData['kind']>> = {
  sessionOpen: 'seat',
  sessionExtend: 'extend',
  sessionEnd: 'end',
  topUp: 'topup',
  debtPaid: 'debt',
  payout: 'payout',
  shopSale: 'sale',
  shopVoid: 'saleVoid',
};

/** The filters of the full feed (the Смена page): a set of kinds each. */
export const FEED_FILTERS: { id: string; label: string; kinds: OperationKind[] }[] = [
  { id: 'all', label: 'Все', kinds: [] },
  {
    id: 'money',
    label: 'Оплаты',
    kinds: ['topUp', 'debtPaid', 'sessionOpen', 'sessionExtend', 'promoRedeem', 'shopSale', 'topUpCancel'],
  },
  { id: 'sessions', label: 'Сеансы', kinds: ['sessionOpen', 'sessionExtend', 'sessionEnd', 'sessionMove'] },
  { id: 'bar', label: 'Бар', kinds: ['shopSale', 'shopVoid'] },
  { id: 'drawer', label: 'Касса', kinds: ['cashIn', 'cashOut', 'payout', 'shiftOpen', 'shiftClose'] },
];

/** Two lists of rows as one, newest first, each row once. */
function merge(a: Operation[], b: Operation[]): Operation[] {
  const byId = new Map<string, Operation>();
  for (const op of [...a, ...b]) if (!byId.has(op.id)) byId.set(op.id, op);
  return [...byId.values()].sort((x, y) => y.at.localeCompare(x.at) || y.id.localeCompare(x.id));
}

function time(iso: string): string {
  return new Date(iso).toLocaleTimeString(dateLocale(), { hour: '2-digit', minute: '2-digit' });
}

const signedSum = (minor: number): string => `${minor > 0 ? '+' : '−'}${exactDigits(Math.abs(minor))}`;

/** What the row says the operation was, in the console language. */
export function operationWhat(op: Operation): string {
  const parts = [t(OPERATION_LABEL[op.kind] ?? op.kind)];
  if (op.kind === 'sessionOpen' || op.kind === 'sessionExtend') {
    if (op.tariff) parts.push(op.tariff);
    if (op.prepaid === false) parts.push(t('постоплата'));
    else if (op.minutes)
      parts.push(op.kind === 'sessionExtend' ? `+${minutesLabel(op.minutes)}` : minutesLabel(op.minutes));
  } else if (op.kind === 'sessionEnd') {
    if (op.amount > 0) parts.push(t('возврат {sum}', { sum: moneyExact(op.amount) }));
    if ((op.charged ?? 0) > 0) parts.push(t('списано {sum}', { sum: moneyExact(op.charged ?? 0) }));
  } else if (op.kind === 'cashIn' || op.kind === 'cashOut') {
    if (op.reasonCode) parts.push(t(REASON_LABEL[op.reasonCode] ?? op.reasonCode));
    if (op.note) parts.push(op.note);
  } else if (op.kind === 'shiftOpen') {
    parts.push(t('на начало {sum}', { sum: moneyExact(op.amount) }));
  } else if (op.kind === 'shiftClose') {
    parts.push(t('посчитано {sum}', { sum: moneyExact(op.amount) }));
  } else if (op.kind === 'shopSale' || op.kind === 'shopVoid') {
    const lines = linesSummary(op.lines);
    if (lines) parts.push(lines);
    if (op.kind === 'shopVoid' && op.reasonCode)
      parts.push(t(VOID_REASON_LABEL[op.reasonCode as VoidReason] ?? op.reasonCode));
    if (op.kind === 'shopVoid' && op.note) parts.push(op.note);
  } else if (op.kind === 'sessionMove') {
    if (op.fromPc && op.pc) parts.push(`${pcLabel(op.fromPc.name)} → ${pcLabel(op.pc.name)}`);
  } else if (op.kind === 'topUpCancel') {
    if (op.reasonCode) parts.push(t(TOPUP_CANCEL_REASON_LABEL[op.reasonCode as TopUpCancelReason] ?? op.reasonCode));
    if (op.note) parts.push(op.note);
  }
  return parts.join(' · ');
}

/** `Coca-Cola ×2, Lay's`: the goods of a bar row. */
function linesSummary(lines: Operation['lines']): string {
  return (lines ?? []).map((l) => (l.qty > 1 ? `${l.title} ×${l.qty}` : l.title)).join(', ');
}

/** How it was paid: the payment with its method, or what the balance paid. */
function operationPaid(op: Operation): string | null {
  if (op.kind === 'topUpCancel' && op.paid)
    return t('возврат {sum}', { sum: `${moneyExact(op.paid.amount)} · ${methodName(op.paid.method)}` });
  if (op.kind === 'shopVoid')
    return op.method === 'balance' || !op.method
      ? t('на баланс {sum}', { sum: moneyExact(op.amount) })
      : t('возврат {sum}', { sum: `${moneyExact(op.amount)} · ${methodName(op.method)}` });
  if (op.paid) return `${moneyExact(op.paid.amount)} · ${methodName(op.paid.method)}`;
  if (op.kind === 'shopSale') return t('с баланса {sum}', { sum: moneyExact(op.charged ?? op.amount) });
  if ((op.kind === 'sessionOpen' || op.kind === 'sessionExtend') && (op.charged ?? op.amount) > 0)
    return t('с баланса {sum}', { sum: moneyExact(op.charged ?? op.amount) });
  if (op.kind === 'payout') return `${moneyExact(op.amount)} · ${methodName('cash')}`;
  if (op.kind === 'cashIn' || op.kind === 'cashOut' || op.kind === 'promoRedeem') return moneyExact(op.amount);
  return null;
}

/**
 * The map's narrow feed: the row's right column already shows what the drawer took, so a payment of exactly that says
 * only its method (F: «Coca-Cola ×2, Lay's · Наличные»); anything else reads as on the Смена page.
 */
function operationPaidBrief(op: Operation): string | null {
  const drawer = Math.abs(op.drawer);
  if (drawer > 0) {
    if (op.paid && op.paid.amount === drawer) return methodName(op.paid.method);
    if (op.kind === 'shopVoid' && op.method && op.method !== 'balance' && op.amount === drawer)
      return methodName(op.method);
    if (op.kind === 'payout' && op.amount === drawer) return methodName('cash');
    if ((op.kind === 'cashIn' || op.kind === 'cashOut') && op.amount === drawer) return null;
  }
  return operationPaid(op);
}

/** Reprints the operation's slip, marked «Копия». */
export function reprint(op: Operation, club: string | null): void {
  if (op.kind === 'cashIn' || op.kind === 'cashOut') {
    void printDocument(
      <CashSlip
        s={{
          kind: op.kind === 'cashIn' ? 'in' : 'out',
          amount: op.amount,
          reasonCode: op.reasonCode ?? 'other',
          note: op.note,
          at: op.at,
          // The original slip's № is the movement's id.
          ref: op.movementId ?? op.id,
          club,
          staffName: op.staffName,
          copy: true,
        }}
      />,
      'receipt',
    );
    return;
  }
  const kind = RECEIPT_KIND[op.kind];
  if (!kind) return;
  if (kind === 'sale' || kind === 'saleVoid') {
    void printDocument(
      <Receipt
        r={{
          kind,
          at: op.at,
          // The sale's own id is the № of its slip; a void's is the journal's.
          ref: kind === 'sale' ? (op.saleId ?? op.id) : op.id,
          club,
          cashier: op.staffName,
          client: op.client && op.client.role !== 'guest' ? op.client.displayName : null,
          guest: !op.client || op.client.role === 'guest',
          pc: op.pc?.name ?? null,
          lines: op.lines ?? [],
          method: op.method ?? null,
          total: op.amount,
          paid:
            kind === 'sale' && op.method && op.method !== 'balance' ? { amount: op.amount, method: op.method } : null,
          reason: op.reasonCode,
          note: op.note,
          copy: true,
        }}
      />,
      'receipt',
    );
    return;
  }
  const r: ReceiptData = {
    kind,
    at: op.at,
    ref: op.paid?.transactionId ?? op.sessionId ?? op.id,
    club,
    cashier: op.staffName,
    client: op.client?.displayName ?? null,
    guest: op.client?.role === 'guest',
    pc: op.pc?.name ?? null,
    tariff: op.tariff,
    minutes: op.minutes,
    // Unknown (an entry from before the flag): the slip then leaves out which refund rule applies.
    pkg: op.package ?? undefined,
    prepaid: op.prepaid ?? undefined,
    quote: op.quote,
    total: kind === 'end' ? null : (op.charged ?? op.amount),
    paid: op.kind === 'payout' ? null : op.paid ? { amount: op.paid.amount, method: op.paid.method } : null,
    refunded: kind === 'end' ? op.amount : null,
    charged: kind === 'end' ? op.charged : null,
    copy: true,
  };
  if (kind === 'topup' || kind === 'debt' || kind === 'payout') r.total = op.amount;
  void printDocument(<Receipt r={r} />, 'receipt');
}

/**
 * What the row did to the drawer: «+» in the text colour, «−» dimmed (a real minus), a sale taken back since struck
 * through, «∞» a postpaid seat (nothing paid yet); nothing when the drawer did not move.
 */
function DrawerAmount({ op }: { op: Operation }): JSX.Element | null {
  if (op.drawer === 0) {
    return op.kind === 'sessionOpen' && op.prepaid === false ? (
      <span title={t('Постоплата')} className="font-medium text-muted">
        ∞
      </span>
    ) : null;
  }
  return (
    <span className={clsx(op.voided ? 'text-muted line-through' : op.drawer > 0 ? 'text-text' : 'text-dim')}>
      {signedSum(op.drawer)}
    </span>
  );
}

/** A row's 24 px icon action: on hover or focus of the row (always on a touch screen). */
const ROW_ACTION =
  'focus-ring inline-flex h-6 w-6 shrink-0 items-center justify-center rounded-sm text-muted hover:bg-text/[0.06] hover:text-text';

function Row({
  op,
  club,
  onVoid,
  onCancel,
  roomy,
  shiftStaff,
}: {
  op: Operation;
  club: string | null;
  /** «Аннулировать…» of a bar sale this staff member may still take back. */
  onVoid?: () => void;
  /** «Исправить / отменить…» of a desk top-up this staff member may still take back (D-91). */
  onCancel?: () => void;
  /** The Смена page: a little more air and type than the map's column. */
  roomy: boolean;
  /** Who runs the open shift: the map's feed leaves that name out of the row (every row would repeat it). */
  shiftStaff: string | null;
}): JSX.Element {
  const who = [
    op.client ? (op.client.role === 'guest' ? t('Гость') : op.client.displayName) : null,
    op.pc ? pcLabel(op.pc.name) : null,
  ]
    .filter(Boolean)
    .join(' · ');
  const paid = roomy ? operationPaid(op) : operationPaidBrief(op);
  const staff = roomy || op.staffName !== shiftStaff ? op.staffName : null;
  const printable = op.kind === 'cashIn' || op.kind === 'cashOut' || RECEIPT_KIND[op.kind] !== undefined;
  const sub = [who, paid, staff].filter(Boolean).join(' · ');
  return (
    <li
      className={clsx(
        'group/row grid items-start gap-x-2.5 border-t border-accent/[0.07]',
        roomy ? 'grid-cols-[44px_minmax(0,1fr)_auto] py-2' : 'grid-cols-[38px_minmax(0,1fr)_auto] py-[3px]',
      )}
    >
      <span
        className={clsx(
          'tnum font-mono font-medium text-muted',
          roomy ? 'text-[11px] leading-[18px]' : 'text-[10.5px] leading-[15px]',
        )}
      >
        {time(op.at)}
      </span>
      <span className="min-w-0">
        <span
          className={clsx(
            'flex min-w-0 items-center gap-1.5 font-medium text-text',
            roomy ? 'text-[13px] leading-[18px]' : 'text-xs leading-[15px]',
          )}
        >
          {op.owner && (
            <span
              data-owner-op
              className="inline-flex h-4 shrink-0 items-center rounded-sm border border-accent/40 bg-accent/[0.06] px-1.5 font-mono text-[8.5px] font-semibold uppercase leading-none tracking-[0.12em] text-accent"
            >
              {t('Владелец')}
            </span>
          )}
          {op.voided && (
            <span className="inline-flex h-4 shrink-0 items-center rounded-sm border border-danger/40 bg-danger/[0.06] px-1.5 font-mono text-[8.5px] font-semibold uppercase leading-none tracking-[0.12em] text-danger-ink">
              {op.kind === 'topUp' ? t('отменено') : t('аннулирован')}
            </span>
          )}
          <span className="truncate">{operationWhat(op)}</span>
        </span>
        {sub && (
          <span
            className={clsx('block truncate text-muted', roomy ? 'text-xs leading-4' : 'text-[10.5px] leading-[14px]')}
          >
            {sub}
          </span>
        )}
      </span>
      <span className="flex flex-col items-end">
        <span
          className={clsx(
            'tnum whitespace-nowrap font-mono font-semibold',
            roomy ? 'h-[18px] text-[12.5px] leading-[18px]' : 'h-[15px] text-[11.5px] leading-[15px]',
          )}
        >
          <DrawerAmount op={op} />
        </span>
        {(onVoid || onCancel || printable) && (
          <span
            className={clsx(
              '-mr-1 flex items-center gap-0.5 opacity-0 transition-opacity duration-150 group-focus-within/row:opacity-100 group-hover/row:opacity-100 [@media(hover:none)]:opacity-100',
              roomy ? 'h-4 [&>button]:-my-1' : 'h-[14px] [&>button]:-my-[5px]',
            )}
          >
            {onVoid && (
              <button
                type="button"
                aria-label={t('Аннулировать…')}
                title={t('Аннулировать…')}
                onClick={onVoid}
                className={clsx(ROW_ACTION, 'hover:!bg-danger/10 hover:!text-danger-ink')}
              >
                <UndoIcon size={14} />
              </button>
            )}
            {onCancel && (
              <button
                type="button"
                aria-label={t('Исправить / отменить…')}
                title={t('Исправить / отменить…')}
                onClick={onCancel}
                className={clsx(ROW_ACTION, 'hover:!bg-danger/10 hover:!text-danger-ink')}
              >
                <UndoIcon size={14} />
              </button>
            )}
            {printable && (
              <button
                type="button"
                aria-label={t('Печать копии')}
                title={t('Печать копии')}
                onClick={() => reprint(op, club)}
                className={ROW_ACTION}
              >
                <PrintIcon size={14} />
              </button>
            )}
          </span>
        )}
      </span>
    </li>
  );
}

/**
 * The feed. `shiftId` — another shift than the open one (the owner's pick on the Смена page); `kinds` — a filter;
 * `placement` tells the map's column from the panel and the page (a data attribute, for the layout and the tests).
 * The column and the panel head themselves («Операции смены», «Журнал ›») unless `headless` (the container already
 * says it, e.g. a fold button of the same name); the page's section heads it.
 */
export function OperationsFeed({
  shiftId = null,
  owner: ownerOps = false,
  kinds,
  placement,
  headless = false,
  className,
}: {
  shiftId?: string | null;
  /** The owner's own operations (D-101) instead of a shift's. */
  owner?: boolean;
  kinds?: OperationKind[];
  placement: 'column' | 'panel' | 'page';
  headless?: boolean;
  className?: string;
}): JSX.Element | null {
  const { version, cashDesk2, shift: openShift, refresh } = useShift();
  const club = useClub();
  const owner = club.staff?.role === 'owner';
  const [voiding, setVoiding] = useState<Operation | null>(null);
  const [cancelling, setCancelling] = useState<Operation | null>(null);
  // Every row seen since the shift / filter was picked, newest first: a poll adds the new ones on top, «Ещё» the older
  // ones below, so a row never falls out between two pages.
  const [items, setItems] = useState<Operation[]>([]);
  const [page, setPage] = useState<Pick<OperationsPage, 'shift' | 'today'> | null>(null);
  const [next, setNext] = useState<string | null>(null);
  const [paged, setPaged] = useState(false);
  const [missing, setMissing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [loadingMore, setLoadingMore] = useState(false);
  const kindsKey = (kinds ?? []).join(',');
  const query = useRef({ shiftId, kinds, paged, ownerOps });
  query.current = { shiftId, kinds, paged, ownerOps };
  // Answers of a previous shift / filter that arrive late are dropped.
  const generation = useRef(0);
  // The shift the rows are of (undefined: none answered yet). Following the open shift, the next one (or none, after a
  // close on another console) starts the list over instead of mixing its rows with the old shift's.
  const shown = useRef<string | null | undefined>(undefined);

  const load = useCallback(async (): Promise<void> => {
    const gen = generation.current;
    try {
      const r = await clubApi.operations({
        shiftId: query.current.shiftId,
        kinds: query.current.kinds,
        owner: query.current.ownerOps,
      });
      if (gen !== generation.current) return;
      const shiftNow = r.shift?.id ?? null;
      const other = shown.current !== undefined && shown.current !== shiftNow;
      shown.current = shiftNow;
      setPage({ shift: r.shift, today: r.today });
      setItems((list) => (other ? r.items : merge(r.items, list)));
      if (other) setPaged(false);
      if (other || !query.current.paged) setNext(r.next);
      setError(null);
    } catch (e) {
      if (gen !== generation.current) return;
      // A server without the feed has no route: a 404 that is not about the shift asked for.
      if (e instanceof AdminError && e.status === 404 && e.details?.['what'] !== 'shift') setMissing(true);
      else setError(describe(e));
    }
  }, []);

  // Another shift or filter: start over.
  useEffect(() => {
    generation.current += 1;
    shown.current = undefined;
    setItems([]);
    setPage(null);
    setNext(null);
    setPaged(false);
    void load();
  }, [shiftId, kindsKey, ownerOps, load]);

  useEffect(() => {
    if (version > 0) void load();
  }, [version, load]);

  useEffect(() => {
    const id = window.setInterval(() => {
      if (document.visibilityState === 'visible') void load();
    }, POLL_MS);
    return () => window.clearInterval(id);
  }, [load]);

  if (missing || !cashDesk2) return null;

  const more = async (): Promise<void> => {
    if (!next) return;
    const gen = generation.current;
    setLoadingMore(true);
    try {
      const r = await clubApi.operations({ shiftId, kinds, before: next, owner: ownerOps });
      if (gen !== generation.current) return;
      setPaged(true);
      setItems((list) => merge(list, r.items));
      setNext(r.next);
    } catch (e) {
      setError(describe(e));
    } finally {
      setLoadingMore(false);
    }
  };

  const roomy = placement === 'page';
  return (
    <section
      aria-label={t('Операции')}
      data-feed={placement}
      className={clsx('flex min-h-0 flex-col gap-2', className)}
    >
      {!roomy && !headless && (
        <PanelHeader
          size="side"
          title={t('Операции смены')}
          aside={
            <a
              href="#/shift"
              className="focus-ring label-sm -mr-1 flex h-6 items-center gap-1 rounded-sm px-1 tracking-[0.14em] hover:text-text"
            >
              {t('Журнал')}
              <ChevronRightIcon size={12} strokeWidth={1.8} />
            </a>
          }
        />
      )}
      {error && (
        <Note tone="err" className="text-xs">
          {error}
        </Note>
      )}
      <ol
        aria-label={t('Операции смены')}
        className={clsx(
          'thin-scrollbar -mr-1.5 flex min-h-0 flex-col overflow-y-auto pr-1.5',
          // The map's feed ends in a fade, not a row cut in half (the Смена page lists it all).
          !roomy && '[mask-image:linear-gradient(180deg,#000_calc(100%-18px),transparent)]',
        )}
      >
        {items.map((op) => (
          <Row
            key={op.id}
            op={op}
            club={club.clubName}
            roomy={roomy}
            shiftStaff={openShift?.staffName ?? null}
            onVoid={
              voidable(op, { owner, openShiftId: openShift?.id ?? null, feedShiftId: page?.shift?.id ?? null })
                ? () => setVoiding(op)
                : undefined
            }
            onCancel={
              cancellable(op, {
                owner,
                me: club.staff?.name ?? null,
                openShiftId: openShift?.id ?? null,
                feedShiftId: page?.shift?.id ?? null,
              })
                ? () => setCancelling(op)
                : undefined
            }
          />
        ))}
      </ol>
      {page && items.length === 0 && (
        <EmptyState
          compact
          title={page.shift || ownerOps ? t('Операций пока нет') : t('Смена не открыта')}
          className="border-t border-accent/[0.07]"
        />
      )}
      {next &&
        (roomy ? (
          <Button variant="ghost" size="sm" className="self-center" disabled={loadingMore} onClick={() => void more()}>
            {loadingMore ? '…' : t('Ещё')}
          </Button>
        ) : (
          // The map's feed: a mono caption under the fade («ЕЩЁ»), not a full button row.
          <Button
            variant="ghost"
            size="xs"
            className="mb-1 shrink-0 self-center font-mono !text-[9.5px] uppercase tracking-[0.14em] text-muted"
            disabled={loadingMore}
            onClick={() => void more()}
          >
            {loadingMore ? '…' : t('Ещё')}
          </Button>
        ))}
      {cancelling && (
        <TopUpCancelSheet
          op={cancelling}
          onClose={() => setCancelling(null)}
          onDone={() => {
            refresh();
            void load();
          }}
        />
      )}
      {voiding && (
        <VoidSheet
          op={voiding}
          onClose={() => setVoiding(null)}
          onDone={() => {
            refresh();
            void load();
          }}
        />
      )}
    </section>
  );
}

/**
 * Whether «Аннулировать…» is offered (D-56; the server decides): a sale not yet taken back, in an open shift; cash and
 * other method money only in the sale's own shift, a balance sale in any; a cashier within 15 minutes of it.
 */
function voidable(
  op: Operation,
  { owner, openShiftId, feedShiftId }: { owner: boolean; openShiftId: string | null; feedShiftId: string | null },
): boolean {
  if (op.kind !== 'shopSale' || op.voided || !op.saleId) return false;
  // The owner's own sale (D-101): the owner's to take back, with or without a shift.
  if (op.owner) return owner;
  if (!openShiftId) return false;
  if (op.method !== 'balance' && feedShiftId !== openShiftId) return false;
  return owner || Date.now() - Date.parse(op.at) <= VOID_WINDOW_MIN * 60_000;
}

/**
 * Whether «Исправить / отменить…» is offered (D-91; the server decides): a desk top-up of the open shift not taken back
 * yet; a cashier only their own (the feed names the staff member) within 15 minutes of it.
 */
function cancellable(
  op: Operation,
  {
    owner,
    me,
    openShiftId,
    feedShiftId,
  }: { owner: boolean; me: string | null; openShiftId: string | null; feedShiftId: string | null },
): boolean {
  if (op.kind !== 'topUp' || op.voided || !op.paid?.transactionId) return false;
  // The owner's own top-up (D-101): only the owner, at any time, no shift involved.
  if (op.owner) return owner;
  if (!openShiftId || feedShiftId !== openShiftId) return false;
  return owner || (op.staffName === me && Date.now() - Date.parse(op.at) <= CANCEL_WINDOW_MIN * 60_000);
}

/**
 * «Исправить сумму» or «Отменить пополнение» of a desk top-up (D-91): the right amount, which the server books in the same
 * step by the same method, or the whole top-up back; a reason (a note for «Другое»); what to hand back or take. One key
 * until a definite answer: a lost one offers only the same action again.
 */
function TopUpCancelSheet({
  op,
  onClose,
  onDone,
}: {
  op: Operation;
  onClose: () => void;
  onDone: () => void;
}): JSX.Element {
  const [mode, setMode] = useState<'fix' | 'cancel'>('fix');
  const [reason, setReason] = useState<TopUpCancelReason>('wrongAmount');
  const [digits, setDigits] = useState('');
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lost, setLost] = useState(false);
  const [done, setDone] = useState<TopUpCancelResult | null>(null);
  const key = useHeldKey();
  const paid = op.paid ?? { amount: op.amount, method: 'cash', transactionId: null };
  const method = paid.method;
  const right = mode === 'fix' ? Number(digits || '0') * 100 : 0;
  const trimmed = note.trim();
  const noteOk = reason === 'other' ? trimmed.length >= 3 && trimmed.length <= 200 : trimmed.length <= 200;
  const amountOk = mode === 'cancel' || (right > 0 && right !== paid.amount && right <= 100_000_000);
  const ready = noteOk && amountOk && !busy && paid.transactionId !== null;
  const diff = paid.amount - right;
  const cash = method === 'cash';
  const what =
    diff > 0
      ? cash
        ? t('Выдайте {sum} наличными из кассы', { sum: moneyExact(diff) })
        : t('Верните {sum} клиенту: {method}', { sum: moneyExact(diff), method: methodName(method) })
      : diff < 0
        ? t('Примите ещё {sum}: {method}', { sum: moneyExact(-diff), method: methodName(method) })
        : null;

  const submit = async (): Promise<void> => {
    if (!ready || !paid.transactionId) return;
    setBusy(true);
    setError(null);
    try {
      const r = await adminApi.cancelTopUp(
        {
          transactionId: paid.transactionId,
          reasonCode: reason,
          ...(trimmed ? { note: trimmed } : {}),
          ...(mode === 'fix' ? { amount: right } : {}),
        },
        key.take(),
      );
      key.settle();
      setLost(false);
      setDone(r);
      onDone();
    } catch (e) {
      key.settle(e);
      setLost(isLostAnswer(e));
      setError(
        isLostAnswer(e)
          ? t('Ответ сервера не пришёл: деньги могли пройти. Повторите это же действие — дважды оно не проведётся.')
          : describe(e),
      );
    } finally {
      setBusy(false);
    }
  };

  return (
    <Sheet title={t('Исправить пополнение')} onClose={onClose}>
      {/* The top-up being corrected: when, whose, how much and how. */}
      <div className="well flex items-center justify-between gap-4 px-3.5 py-3">
        <span className="min-w-0">
          <span className="label-sm tnum block">{time(op.at)}</span>
          <span className="mt-1.5 block truncate text-[13px] font-medium leading-5 text-text">
            {[op.client?.displayName, methodName(method)].filter(Boolean).join(' · ')}
          </span>
        </span>
        <span className="tnum shrink-0 whitespace-nowrap font-mono text-[15px] font-semibold text-hi">
          {moneyExact(paid.amount)}
        </span>
      </div>
      {done ? (
        <>
          <Note role="status" tone="ok">
            {done.topUp
              ? t('Исправлено · {from} → {to}', {
                  from: moneyExact(done.cancelled.amount),
                  to: moneyExact(done.topUp.transaction.amount.amount),
                })
              : t('Пополнение отменено · {sum}', { sum: moneyExact(done.cancelled.amount) })}
          </Note>
          {done.cancelled.bonus > 0 && (
            <p className="text-xs text-muted">
              {t('Бонус {sum} снят с баланса', { sum: moneyExact(done.cancelled.bonus) })}
            </p>
          )}
          <Button variant="primary" autoFocus onClick={onClose}>
            {t('Готово')}
          </Button>
        </>
      ) : (
        <>
          <Segmented
            label={t('Что сделать')}
            value={mode}
            options={[
              { id: 'fix', label: t('Исправить сумму') },
              { id: 'cancel', label: t('Отменить пополнение') },
            ]}
            onChange={(v) => {
              setMode(v as 'fix' | 'cancel');
              if (v === 'fix') setReason('wrongAmount');
            }}
          />
          {mode === 'fix' ? (
            <Field
              label={t('Правильная сумма')}
              hint={t('Пополнится этим же способом: {method}', { method: methodName(method) })}
            >
              <div className="relative">
                <input
                  className={clsx(inputCls, 'tnum pr-14')}
                  inputMode="numeric"
                  autoFocus
                  aria-label={t('Правильная сумма')}
                  value={digits.replace(/\B(?=(\d{3})+(?!\d))/g, ' ')}
                  readOnly={busy || lost}
                  onChange={(e) => setDigits(e.target.value.replace(/\D/g, '').slice(0, 9))}
                />
                <span className="pointer-events-none absolute right-3.5 top-1/2 -translate-y-1/2 text-xs font-medium text-muted">
                  {t('сум')}
                </span>
              </div>
            </Field>
          ) : (
            <div className="flex flex-col gap-2">
              <span className="label-sm">{t('Причина')}</span>
              <div role="group" aria-label={t('Причина')} className="grid grid-cols-3 gap-2">
                {TOPUP_CANCEL_REASONS.map((r) => (
                  <ChoiceButton key={r} on={reason === r} disabled={busy || lost} onClick={() => setReason(r)}>
                    {t(TOPUP_CANCEL_REASON_LABEL[r])}
                  </ChoiceButton>
                ))}
              </div>
            </div>
          )}
          <Field
            label={t('Комментарий')}
            hint={reason === 'other' ? t('Обязателен для «Другое»: от 3 до 200 символов') : t('Необязательно')}
          >
            <input
              className={inputCls}
              value={note}
              maxLength={200}
              readOnly={busy || lost}
              onChange={(e) => setNote(e.target.value)}
            />
          </Field>
          {/* Where the money goes; the bonus of the wrong top-up leaves with it. */}
          {what && (
            <p className="well px-3.5 py-3 text-[13px] font-medium leading-5 text-text">
              {what}
              <span className="mt-0.5 block text-xs font-normal text-muted">
                {t('Бонус за ошибочное пополнение снимется с баланса')}
              </span>
            </p>
          )}
          {mode === 'fix' && right === paid.amount && (
            <p className="text-xs font-medium text-warning">{t('Это та же сумма')}</p>
          )}
          <Note note={error ? { text: error, tone: 'err' } : null} />
          <div className="flex justify-end gap-2 border-t border-accent/[0.08] pt-4">
            <Button variant="ghost" onClick={onClose}>
              {t('Отмена')}
            </Button>
            <Button variant="danger" disabled={!ready} onClick={() => void submit()}>
              {busy ? '…' : lost ? t('Повторить') : mode === 'fix' ? t('Исправить') : t('Отменить пополнение')}
            </Button>
          </div>
        </>
      )}
    </Sheet>
  );
}

/**
 * Takes a bar sale back (D-56): a reason (a note for «Другое»), what happens to the goods and the money, then the slip
 * to sign. The action holds one key until a definite answer: a lost one offers only the same void again.
 */
function VoidSheet({ op, onClose, onDone }: { op: Operation; onClose: () => void; onDone: () => void }): JSX.Element {
  const club = useClub();
  const [reason, setReason] = useState<VoidReason | null>(null);
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lost, setLost] = useState(false);
  const [done, setDone] = useState<SaleVoid | null>(null);
  const key = useHeldKey();
  const trimmed = note.trim();
  const noteOk = reason === 'other' ? trimmed.length >= 3 && trimmed.length <= 200 : trimmed.length <= 200;
  const ready = reason !== null && noteOk && !busy;
  const method = op.method ?? 'cash';
  const back =
    method === 'balance'
      ? t('{sum} вернётся на баланс клиента', { sum: moneyExact(op.amount) })
      : method === 'cash'
        ? t('Выдайте {sum} наличными из кассы', { sum: moneyExact(op.amount) })
        : t('Верните {sum} клиенту: {method}', { sum: moneyExact(op.amount), method: methodName(method) });

  const submit = async (): Promise<void> => {
    if (!ready || !reason || !op.saleId) return;
    setBusy(true);
    setError(null);
    try {
      const r = await adminApi.shopVoid(
        op.saleId,
        { reasonCode: reason, ...(trimmed ? { note: trimmed } : {}) },
        key.take(),
      );
      key.settle();
      setLost(false);
      setDone(r.void);
      onDone();
    } catch (e) {
      key.settle(e);
      setLost(isLostAnswer(e));
      setError(
        isLostAnswer(e)
          ? t('Ответ сервера не пришёл: деньги могли пройти. Повторите это же действие — дважды оно не проведётся.')
          : describe(e),
      );
    } finally {
      setBusy(false);
    }
  };

  const slip = (v: SaleVoid): ReceiptData => ({
    kind: 'saleVoid',
    at: v.at,
    ref: v.id,
    club: club.clubName,
    cashier: v.staffName,
    client: op.client && op.client.role !== 'guest' ? op.client.displayName : null,
    guest: !op.client || op.client.role === 'guest',
    pc: op.pc?.name ?? null,
    lines: op.lines ?? [],
    method: v.method,
    total: v.total,
    reason: v.reasonCode,
    note: v.note,
  });

  return (
    <Sheet title={t('Аннулировать продажу')} onClose={onClose}>
      {/* The sale being taken back: when, what, how much. */}
      <div className="well flex items-center justify-between gap-4 px-3.5 py-3">
        <span className="min-w-0">
          <span className="label-sm tnum block">{time(op.at)}</span>
          <span className="mt-1.5 block truncate text-[13px] font-medium leading-5 text-text">
            {linesSummary(op.lines) || t('Продажа бара')}
          </span>
        </span>
        <span className="tnum shrink-0 whitespace-nowrap font-mono text-[15px] font-semibold text-hi">
          {moneyExact(op.amount)}
        </span>
      </div>
      {done ? (
        <>
          <Note role="status" tone="ok">
            {t('Аннулировано · {sum}', { sum: moneyExact(done.total) })}
          </Note>
          <div className="grid grid-cols-2 gap-2">
            <Button onClick={() => void printDocument(<Receipt r={slip(done)} />, 'receipt')}>{t('Печать')}</Button>
            <Button variant="primary" autoFocus onClick={onClose}>
              {t('Готово')}
            </Button>
          </div>
        </>
      ) : (
        <>
          <div className="flex flex-col gap-2">
            <span className="label-sm">{t('Причина')}</span>
            <div role="group" aria-label={t('Причина')} className="grid grid-cols-2 gap-2">
              {VOID_REASONS.map((r) => (
                <ChoiceButton key={r} on={reason === r} disabled={busy || lost} onClick={() => setReason(r)}>
                  {t(VOID_REASON_LABEL[r])}
                </ChoiceButton>
              ))}
            </div>
          </div>
          <Field
            label={t('Комментарий')}
            hint={reason === 'other' ? t('Обязателен для «Другое»: от 3 до 200 символов') : t('Необязательно')}
          >
            <input
              className={inputCls}
              value={note}
              maxLength={200}
              readOnly={busy || lost}
              onChange={(e) => setNote(e.target.value)}
            />
          </Field>
          {/* Where the money and the goods go. */}
          <p className="well px-3.5 py-3 text-[13px] font-medium leading-5 text-text">
            {back}
            <span className="mt-0.5 block text-xs font-normal text-muted">
              {reason === 'defect' ? t('Брак не возвращается на склад') : t('Товар вернётся на склад')}
            </span>
          </p>
          <Note note={error ? { text: error, tone: 'err' } : null} />
          <div className="flex justify-end gap-2 border-t border-accent/[0.08] pt-4">
            <Button variant="ghost" onClick={onClose}>
              {t('Отмена')}
            </Button>
            <Button variant="danger" disabled={!ready} onClick={() => void submit()}>
              {busy ? '…' : lost ? t('Повторить') : t('Аннулировать')}
            </Button>
          </div>
        </>
      )}
    </Sheet>
  );
}
