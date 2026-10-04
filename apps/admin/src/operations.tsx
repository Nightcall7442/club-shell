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
import { AdminError, clubApi, type Operation, type OperationKind, type OperationsPage, type Today } from '@/api';
import { pcLabel } from '@/clientSearch';
import { useClub } from '@/club';
import { describe } from '@/errors';
import { exactDigits, minutesLabel, moneyExact } from '@/format';
import { dateLocale, t } from '@/i18n';
import { OPERATION_LABEL, REASON_LABEL } from '@/labels';
import { methodName } from '@/paybox';
import { CashSlip, Receipt, printDocument, type ReceiptData } from '@/print';
import { useShift } from '@/shift';
import { Button } from '@/ui';

const POLL_MS = 5000;

/** Kinds that have a slip to reprint. */
const RECEIPT_KIND: Partial<Record<OperationKind, ReceiptData['kind']>> = {
  sessionOpen: 'seat',
  sessionExtend: 'extend',
  sessionEnd: 'end',
  topUp: 'topup',
  debtPaid: 'debt',
  payout: 'payout',
};

/** The filters of the full feed (the Смена page): a set of kinds each. */
export const FEED_FILTERS: { id: string; label: string; kinds: OperationKind[] }[] = [
  { id: 'all', label: 'Все', kinds: [] },
  { id: 'money', label: 'Оплаты', kinds: ['topUp', 'debtPaid', 'sessionOpen', 'sessionExtend', 'promoRedeem'] },
  { id: 'sessions', label: 'Сеансы', kinds: ['sessionOpen', 'sessionExtend', 'sessionEnd'] },
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
  }
  return parts.join(' · ');
}

/** How it was paid: the payment with its method, or what the balance paid. */
function operationPaid(op: Operation): string | null {
  if (op.paid) return `${moneyExact(op.paid.amount)} · ${methodName(op.paid.method)}`;
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
  const cash = today.byMethod.cash - today.payouts;
  const cashless = today.taken - today.byMethod.cash;
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

function Row({ op, club }: { op: Operation; club: string | null }): JSX.Element {
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
  const { version, cashDesk2 } = useShift();
  const club = useClub();
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
          <Row key={op.id} op={op} club={club.clubName} />
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
    </section>
  );
}
