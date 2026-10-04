/**
 * The shift's operations as the counter did them (D-43), newest first: who, for whom and on which PC, what (a seat, a
 * top-up, a cash move…), how it was paid and what it did to the drawer, with a ⎙ that reprints the slip as a «Копия».
 * A paid seat is one row with its payment merged in (the server leaves the session's top-up out). Above the rows,
 * today's money (the club's local day) by method, minus the cash given back to guests.
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
  type Today,
  type VoidReason,
} from '@/api';
import { pcLabel } from '@/clientSearch';
import { useClub } from '@/club';
import { describe, isLostAnswer } from '@/errors';
import { exactDigits, minutesLabel, moneyExact } from '@/format';
import { dateLocale, t } from '@/i18n';
import { OPERATION_LABEL, REASON_LABEL, VOID_REASONS, VOID_REASON_LABEL } from '@/labels';
import { methodName, useHeldKey } from '@/paybox';
import { CashSlip, Receipt, printDocument, type ReceiptData } from '@/print';
import { useShift } from '@/shift';
import { Button, Field, Note, Sheet, inputCls } from '@/ui';

const POLL_MS = 5000;
/** A cashier takes a bar sale back within this many minutes of it; the owner later (D-56). */
const VOID_WINDOW_MIN = 15;

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
    kinds: ['topUp', 'debtPaid', 'sessionOpen', 'sessionExtend', 'promoRedeem', 'shopSale'],
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
  }
  return parts.join(' · ');
}

/** `Coca-Cola ×2, Lay's`: the goods of a bar row. */
function linesSummary(lines: Operation['lines']): string {
  return (lines ?? []).map((l) => (l.qty > 1 ? `${l.title} ×${l.qty}` : l.title)).join(', ');
}

/** How it was paid: the payment with its method, or what the balance paid. */
function operationPaid(op: Operation): string | null {
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

/** «Сегодня принято …»: the headline, opening to every method, the payouts and the sessions. */
function TodayLine({ today }: { today: Today }): JSX.Element {
  const [open, setOpen] = useState(false);
  // The bar's cash is cash taken too (D-57): `taken` holds both, `byMethod` only the top-ups.
  const shopCash = today.shopByMethod?.cash ?? 0;
  const shop = today.shopByMethod ? Object.values(today.shopByMethod).reduce((a, b) => a + b, 0) : 0;
  const cash = today.byMethod.cash + shopCash - today.payouts;
  const cashless = today.taken - today.byMethod.cash - shopCash;
  return (
    <div className="flex flex-col gap-1.5">
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
        className="focus-ring flex flex-wrap items-baseline gap-x-2 gap-y-0.5 rounded-md text-left text-sm hover:text-text"
      >
        <span className="text-text">
          {t('Сегодня принято {sum}', { sum: moneyExact(today.taken - today.payouts) })}
        </span>
        <span className="tnum text-xs text-muted">
          {t('нал {sum}', { sum: exactDigits(cash) })} · {t('безнал {sum}', { sum: exactDigits(cashless) })}
        </span>
      </button>
      {open && (
        <dl className="grid grid-cols-2 gap-x-4 gap-y-1 rounded-md border border-line bg-bg px-3 py-2 text-xs">
          {(['cash', 'card', 'payme', 'click', 'uzum'] as const).map((m) => (
            <div key={m} className="flex justify-between gap-2">
              <dt className="text-muted">{methodName(m)}</dt>
              <dd className="tnum">{exactDigits(today.byMethod[m])}</dd>
            </div>
          ))}
          {today.byMethod.other > 0 && (
            <div className="flex justify-between gap-2">
              <dt className="text-muted">{t('Другое')}</dt>
              <dd className="tnum">{exactDigits(today.byMethod.other)}</dd>
            </div>
          )}
          {today.shopByMethod && (
            <div className="flex justify-between gap-2">
              <dt className="text-muted">{t('Бар')}</dt>
              <dd className="tnum">{exactDigits(shop)}</dd>
            </div>
          )}
          <div className="flex justify-between gap-2">
            <dt className="text-muted">{t('Выдано гостям')}</dt>
            <dd className="tnum">{exactDigits(today.payouts)}</dd>
          </div>
          <div className="flex justify-between gap-2">
            <dt className="text-muted">{t('Сеансы')}</dt>
            <dd className="tnum">{exactDigits(today.sessions)}</dd>
          </div>
        </dl>
      )}
    </div>
  );
}

function Row({
  op,
  club,
  onVoid,
}: {
  op: Operation;
  club: string | null;
  /** «Аннулировать…» of a bar sale this staff member may still take back. */
  onVoid?: () => void;
}): JSX.Element {
  const who = [
    op.client ? (op.client.role === 'guest' ? t('Гость') : op.client.displayName) : null,
    op.pc ? pcLabel(op.pc.name) : null,
  ]
    .filter(Boolean)
    .join(' · ');
  const paid = operationPaid(op);
  const printable = op.kind === 'cashIn' || op.kind === 'cashOut' || RECEIPT_KIND[op.kind] !== undefined;
  return (
    <li className="flex flex-col gap-0.5 py-2">
      <div className="flex items-baseline justify-between gap-2">
        <span className="min-w-0 truncate text-sm">
          <span className="tnum mr-2 font-mono text-xs text-muted">{time(op.at)}</span>
          {op.voided && (
            <span className="mr-1.5 rounded border border-danger/50 px-1 py-px text-[0.65rem] font-semibold text-danger">
              {t('аннулирован')}
            </span>
          )}
          {operationWhat(op)}
        </span>
        {op.drawer !== 0 && (
          <span className={clsx('tnum shrink-0 font-mono text-xs', op.drawer > 0 ? 'text-success' : 'text-danger')}>
            {signedSum(op.drawer)}
          </span>
        )}
      </div>
      <div className="flex items-center justify-between gap-2">
        <span className="min-w-0 truncate text-xs text-muted">
          {[who, paid, op.staffName].filter(Boolean).join(' · ')}
        </span>
        {onVoid && (
          <button
            type="button"
            onClick={onVoid}
            className="focus-ring h-6 shrink-0 rounded px-1.5 text-xs font-semibold text-danger hover:bg-danger/10"
          >
            {t('Аннулировать…')}
          </button>
        )}
        {printable && (
          <button
            type="button"
            aria-label={t('Печать копии')}
            title={t('Печать копии')}
            onClick={() => reprint(op, club)}
            className="focus-ring h-6 w-6 shrink-0 rounded text-sm leading-none text-muted hover:bg-white/[0.06] hover:text-text"
          >
            ⎙
          </button>
        )}
      </div>
    </li>
  );
}

/**
 * The feed. `shiftId` — another shift than the open one (the owner's pick on the Смена page); `kinds` — a filter;
 * `placement` tells the map's column from the panel and the page (a data attribute, for the layout and the tests).
 */
export function OperationsFeed({
  shiftId = null,
  kinds,
  placement,
  className,
  showToday = true,
}: {
  shiftId?: string | null;
  kinds?: OperationKind[];
  placement: 'column' | 'panel' | 'page';
  className?: string;
  showToday?: boolean;
}): JSX.Element | null {
  const { version, cashDesk2, shift: openShift, refresh } = useShift();
  const club = useClub();
  const owner = club.staff?.role === 'owner';
  const [voiding, setVoiding] = useState<Operation | null>(null);
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
  const query = useRef({ shiftId, kinds, paged });
  query.current = { shiftId, kinds, paged };
  // Answers of a previous shift / filter that arrive late are dropped.
  const generation = useRef(0);
  // The shift the rows are of (undefined: none answered yet). Following the open shift, the next one (or none, after a
  // close on another console) starts the list over instead of mixing its rows with the old shift's.
  const shown = useRef<string | null | undefined>(undefined);

  const load = useCallback(async (): Promise<void> => {
    const gen = generation.current;
    try {
      const r = await clubApi.operations({ shiftId: query.current.shiftId, kinds: query.current.kinds });
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
  }, [shiftId, kindsKey, load]);

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
      const r = await clubApi.operations({ shiftId, kinds, before: next });
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

  return (
    <section
      aria-label={t('Операции')}
      data-feed={placement}
      className={clsx('flex min-h-0 flex-col gap-3', className)}
    >
      <header className="flex flex-col gap-2">
        <h2 className="label text-text">{t('Операции смены')}</h2>
        {showToday && page?.today && <TodayLine today={page.today} />}
      </header>
      {error && <p className="rounded-md bg-danger/10 px-3 py-1.5 text-xs text-danger">{error}</p>}
      <ol aria-label={t('Операции смены')} className="flex min-h-0 flex-col divide-y divide-line overflow-y-auto pr-1">
        {items.map((op) => (
          <Row
            key={op.id}
            op={op}
            club={club.clubName}
            onVoid={
              voidable(op, { owner, openShiftId: openShift?.id ?? null, feedShiftId: page?.shift?.id ?? null })
                ? () => setVoiding(op)
                : undefined
            }
          />
        ))}
      </ol>
      {page && items.length === 0 && (
        <p className="text-sm text-muted">{page.shift ? t('Операций пока нет') : t('Смена не открыта')}</p>
      )}
      {next && (
        <Button variant="ghost" size="sm" disabled={loadingMore} onClick={() => void more()}>
          {loadingMore ? '…' : t('Ещё')}
        </Button>
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
  if (op.kind !== 'shopSale' || op.voided || !op.saleId || !openShiftId) return false;
  if (op.method !== 'balance' && feedShiftId !== openShiftId) return false;
  return owner || Date.now() - Date.parse(op.at) <= VOID_WINDOW_MIN * 60_000;
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
      <p className="text-sm">
        {time(op.at)} · {linesSummary(op.lines) || t('Продажа бара')} · {moneyExact(op.amount)}
      </p>
      {done ? (
        <>
          <p role="status" className="rounded-md bg-success/10 px-3 py-2 text-sm text-success">
            {t('Аннулировано · {sum}', { sum: moneyExact(done.total) })}
          </p>
          <div className="grid grid-cols-2 gap-2">
            <Button onClick={() => void printDocument(<Receipt r={slip(done)} />, 'receipt')}>{t('Печать')}</Button>
            <Button variant="primary" autoFocus onClick={onClose}>
              {t('Готово')}
            </Button>
          </div>
        </>
      ) : (
        <>
          <div className="flex flex-col gap-1.5">
            <span className="label">{t('Причина')}</span>
            <div role="group" aria-label={t('Причина')} className="grid grid-cols-2 gap-1.5">
              {VOID_REASONS.map((r) => (
                <Button
                  key={r}
                  size="sm"
                  aria-pressed={reason === r}
                  disabled={busy || lost}
                  className={clsx(reason === r && 'choice-on')}
                  onClick={() => setReason(r)}
                >
                  {t(VOID_REASON_LABEL[r])}
                </Button>
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
          <p className="rounded-md bg-white/[0.04] px-3 py-2 text-sm">
            {back}
            <span className="block text-xs text-muted">
              {reason === 'defect' ? t('Брак не возвращается на склад') : t('Товар вернётся на склад')}
            </span>
          </p>
          <Note note={error ? { text: error, tone: 'err' } : null} />
          <div className="flex justify-end gap-2 border-t border-line pt-4">
            <Button variant="ghost" onClick={onClose}>
              {t('Отмена')}
            </Button>
            <Button
              variant="danger"
              className="border border-danger/50"
              disabled={!ready}
              onClick={() => void submit()}
            >
              {busy ? '…' : lost ? t('Повторить') : t('Аннулировать')}
            </Button>
          </div>
        </>
      )}
    </Sheet>
  );
}
