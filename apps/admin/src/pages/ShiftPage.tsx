/**
 * Cash shift: open with the drawer float, watch the X-report while the shift runs, put cash into the drawer or take it
 * out, close with the counted cash and get the Z-report; both split the top-ups by payment method and show the drawer
 * line (float + desk cash + in − out − payouts = expected, the server's figure). X and Z print on the report paper; the
 * print settings of this console (paper per kind, the receipt language) are set here. Below, the shift's operations
 * (the owner may pick an older shift) and the closed shifts with who closed them and a reprint of their Z. Polls
 * `/admin/shift` every 5 s and tells the console's shift state (the top-bar chip, the money buttons) when it changes.
 */
import { useCallback, useEffect, useState } from 'react';
import clsx from 'clsx';
import { adminApi, clubApi, expectedOf, type GuestDebt, type OperationKind, type Shift, type ShiftTotals } from '@/api';
import { useClub } from '@/club';
import { describe } from '@/errors';
import { LANGS, dateLocale, t, type Lang } from '@/i18n';
import { money, moneyExact } from '@/format';
import { FEED_FILTERS, OperationsFeed } from '@/operations';
import { PAY_METHODS } from '@/paybox';
import {
  PAPERS,
  ShiftReport,
  paperOf,
  printDocument,
  setPaper,
  setReceiptLang,
  storedReceiptLang,
  type Paper,
  type PrintKind,
} from '@/print';
import { drawerMoves, printX, useShift } from '@/shift';
import { Button, Field, MoneyInput, Note, PageHeader, Section, Table, inputCls } from '@/ui';

const POLL_MS = 5000;
/** The unpaid bills the close form warns about are read this often (the overview is heavier than the shift). */
const DEBTS_POLL_MS = 30_000;
const nf = new Intl.NumberFormat('ru-RU');

type NoteState = { text: string; tone: 'ok' | 'err' } | null;

const sum = (minor: number): string => nf.format(Math.round(minor / 100));
const uzs = (minor: number | null | undefined): string =>
  minor === null || minor === undefined ? '—' : money({ amount: minor, currency: 'UZS' });

/** Each language by its own name, as language pickers do. */
const LANG_NAME: Record<Lang, string> = { ru: 'Русский', uz: 'Oʻzbekcha', en: 'English' }; // i18n-ignore

function dateTime(iso: string | null): string {
  if (!iso) return '—';
  return new Date(iso).toLocaleString(dateLocale(), {
    day: '2-digit',
    month: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function time(iso: string): string {
  return new Date(iso).toLocaleTimeString(dateLocale(), { hour: '2-digit', minute: '2-digit' });
}

/** One stat cell: mono label, dot-matrix figure, small unit; the label names the cell for assistive tech. */
function Stat({
  label,
  value,
  unit,
  tone,
}: {
  label: string;
  value: string;
  unit?: string;
  tone?: 'ok' | 'err';
}): JSX.Element {
  return (
    <div role="group" aria-label={label} className="flex min-w-0 flex-col justify-between gap-2 bg-surface px-5 py-4">
      <span className="label">{label}</span>
      <span className="flex items-baseline gap-1.5">
        <span
          className={clsx(
            'num-dot text-[1.7rem] leading-none',
            tone === 'ok' && 'text-success',
            tone === 'err' && 'text-danger',
          )}
        >
          {value}
        </span>
        {unit && <span className="text-xs text-muted">{unit}</span>}
      </span>
    </div>
  );
}

function StatGrid({ children, cols }: { children: React.ReactNode; cols: string }): JSX.Element {
  return (
    <div className={clsx('grid gap-px overflow-hidden rounded-xl border border-line bg-line', cols)}>{children}</div>
  );
}

function TotalsGrid({ x }: { x: ShiftTotals }): JSX.Element {
  const cells: [string, number][] = [
    [t('Пополнения наличными'), x.topUpCash],
    [t('Пополнения картой/онлайн'), x.topUpOther],
    [t('Сеансы'), x.sessions],
    [t('Магазин'), x.shop],
    [t('Возвраты'), x.refunds],
    [t('Бонусы'), x.bonuses],
    [t('Внесения'), x.cashIn ?? 0],
    [t('Изъятия'), x.cashOut ?? 0],
    [t('Выдачи гостям'), x.payouts ?? 0],
    [t('Через API (нал.)'), x.apiCash ?? 0],
  ];
  return (
    <StatGrid cols="grid-cols-2 md:grid-cols-4 xl:grid-cols-6">
      {cells.map(([label, v]) => (
        <Stat key={label} label={label} value={sum(v)} unit={t('сум')} />
      ))}
      <Stat label={t('Операций')} value={String(x.count)} />
    </StatGrid>
  );
}

/** Top-ups by payment method: what the drawer, the terminal and each wallet app should each add up to. */
function MethodsGrid({ byMethod }: { byMethod: NonNullable<ShiftTotals['topUpByMethod']> }): JSX.Element {
  return (
    <StatGrid cols="grid-cols-2 md:grid-cols-3 xl:grid-cols-6">
      {PAY_METHODS.map((m) => (
        <Stat key={m.id} label={t(m.label)} value={sum(byMethod[m.id])} unit={t('сум')} />
      ))}
      <Stat label={t('Другое')} value={sum(byMethod.other)} unit={t('сум')} />
    </StatGrid>
  );
}

/** Float + the desk's cash top-ups + cash in − cash out − payouts = what the drawer should hold. */
function DrawerLine({ opening, x, expected }: { opening: number; x: ShiftTotals; expected: number }): JSX.Element {
  const rows: [string, string][] = [
    [t('На начало смены'), uzs(opening)],
    [t('+ наличные пополнения'), uzs(x.topUpCash - (x.apiCash ?? 0))],
    [t('+ внесения'), uzs(x.cashIn ?? 0)],
    [t('− изъятия'), uzs(x.cashOut ?? 0)],
    [t('− выдачи гостям'), uzs(x.payouts ?? 0)],
  ];
  return (
    <dl
      aria-label={t('Наличные в кассе')}
      className="flex flex-col gap-1 rounded-md border border-line bg-bg px-4 py-3 text-sm"
    >
      {rows.map(([label, v]) => (
        <div key={label} className="flex justify-between gap-3">
          <dt className="text-muted">{label}</dt>
          <dd className="tnum">{v}</dd>
        </div>
      ))}
      <div className="flex justify-between gap-3 border-t border-line pt-1 font-semibold">
        <dt>{t('= ожидается')}</dt>
        <dd className="tnum">{uzs(expected)}</dd>
      </div>
      {(x.apiCash ?? 0) > 0 && (
        <p className="pt-1 text-xs text-muted">
          {t('Пополнения через API ({sum}) в кассу не попадают', { sum: uzs(x.apiCash ?? 0) })}
        </p>
      )}
    </dl>
  );
}

function diffTone(d: number): 'ok' | 'err' | undefined {
  if (d === 0) return 'ok';
  return d > 0 ? 'ok' : 'err';
}

function signed(minor: number): string {
  return `${minor > 0 ? '+' : minor < 0 ? '−' : ''}${sum(Math.abs(minor))}`;
}

/** Expected cash of a closed shift: the server's, or the old formula for rows without it. */
function expectedOfClosed(s: Shift): number | null {
  if (s.expectedCash !== undefined && s.expectedCash !== null) return s.expectedCash;
  return s.totals ? expectedOf(s.openingCash, s.totals) : null;
}

/** Prints a closed shift's Z with its drawer moves; a reprint says «Копия». */
async function printZ(shift: Shift, expected: number | null, club: string | null, copy: boolean): Promise<void> {
  if (!shift.totals) return;
  const moves = await drawerMoves(shift.id);
  await printDocument(
    <ShiftReport
      r={{ type: 'Z', club, shift, x: shift.totals, expected, moves, at: new Date().toISOString(), copy }}
    />,
    'report',
  );
}

/** Paper per kind and the receipt language: settings of this console, kept in the browser. */
function PrintSettings(): JSX.Element {
  const [receipt, setReceipt] = useState<Paper>(() => paperOf('receipt'));
  const [report, setReport] = useState<Paper>(() => paperOf('report'));
  const [lang, setLangState] = useState<Lang | null>(storedReceiptLang);
  const paperSelect = (kind: PrintKind, value: Paper, set: (p: Paper) => void, label: string): JSX.Element => (
    <Field label={label}>
      <select
        className={inputCls}
        value={value}
        onChange={(e) => {
          const p = e.target.value as Paper;
          setPaper(kind, p);
          set(p);
        }}
      >
        {PAPERS.map((p) => (
          <option key={p.id} value={p.id}>
            {t(p.label)}
          </option>
        ))}
      </select>
    </Field>
  );
  return (
    <Section title={t('Печать')}>
      <div className="grid gap-3 md:grid-cols-3">
        {paperSelect('receipt', receipt, setReceipt, t('Бумага чеков'))}
        {paperSelect('report', report, setReport, t('Бумага отчётов X/Z'))}
        <Field label={t('Язык чеков')}>
          <select
            className={inputCls}
            value={lang ?? ''}
            onChange={(e) => {
              const v = (e.target.value || null) as Lang | null;
              setReceiptLang(v);
              setLangState(v);
            }}
          >
            <option value="">{t('Как в консоли')}</option>
            {LANGS.map((l) => (
              <option key={l} value={l}>
                {LANG_NAME[l]}
              </option>
            ))}
          </select>
        </Field>
      </div>
      <p className="text-xs text-muted">
        {t(
          'Печать идёт через браузер на принтер по умолчанию. Без окна печати — отдельный ярлык Chrome с --kiosk-printing и своим --user-data-dir; тогда всё, и отчёты, печатается на чековый принтер. Чеки нефискальные.',
        )}
      </p>
    </Section>
  );
}

export default function ShiftPage({ isOwner = false }: { isOwner?: boolean }): JSX.Element {
  const [shift, setShift] = useState<Shift | null>(null);
  const [x, setX] = useState<ShiftTotals | null>(null);
  const [history, setHistory] = useState<Shift[]>([]);
  const [expectedCash, setExpectedCash] = useState<number | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [opening, setOpening] = useState(0);
  const [counted, setCounted] = useState(0);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<NoteState>(null);
  const [zReport, setZReport] = useState<{ shift: Shift; expectedCash: number } | null>(null);
  const [debts, setDebts] = useState<GuestDebt[]>([]);
  const [feedShift, setFeedShift] = useState<string>('');
  const [feedFilter, setFeedFilter] = useState('all');
  const desk = useShift();
  const club = useClub();

  const load = useCallback(async (): Promise<void> => {
    try {
      const r = await clubApi.shift();
      setShift(r.shift);
      setX(r.x);
      setHistory(r.history);
      setExpectedCash(r.expectedCash ?? null);
      setLoaded(true);
    } catch (e) {
      setNote({ text: describe(e), tone: 'err' });
    }
  }, []);

  useEffect(() => {
    void load();
    const id = window.setInterval(() => void load(), POLL_MS);
    return () => window.clearInterval(id);
  }, [load]);

  // Unpaid bills: closing the shift is not blocked by them, but the cashier is told.
  useEffect(() => {
    let alive = true;
    const read = (): void => {
      adminApi
        .overview()
        .then((o) => alive && setDebts(o.guestDebts ?? []))
        .catch(() => undefined);
    };
    read();
    const id = window.setInterval(read, DEBTS_POLL_MS);
    return () => {
      alive = false;
      window.clearInterval(id);
    };
  }, []);

  // The drawer changed elsewhere (a cash move from the top bar, a payment on the map): show it now.
  useEffect(() => {
    void load();
  }, [desk.version, load]);

  const open = async (): Promise<void> => {
    setBusy(true);
    setNote(null);
    try {
      await clubApi.openShift(opening);
      setZReport(null);
      setNote({ text: t('Смена открыта'), tone: 'ok' });
      desk.refresh();
      await load();
    } catch (e) {
      setNote({ text: describe(e), tone: 'err' });
    } finally {
      setBusy(false);
    }
  };

  const close = async (): Promise<void> => {
    setBusy(true);
    setNote(null);
    try {
      const r = await clubApi.closeShift(counted);
      setZReport(r);
      setCounted(0);
      setNote({ text: t('Смена закрыта'), tone: 'ok' });
      desk.refresh();
      await load();
    } catch (e) {
      setNote({ text: describe(e), tone: 'err' });
    } finally {
      setBusy(false);
    }
  };

  const print = (fn: () => Promise<void>): void => {
    fn().catch((e: unknown) => setNote({ text: describe(e), tone: 'err' }));
  };

  // The server's figure; the old formula only for an older server that sends none.
  const expected = shift && x ? (expectedCash ?? expectedOf(shift.openingCash, x)) : 0;
  const diff = counted - expected;
  const debtTotal = debts.reduce((a, d) => a + d.debt.amount, 0);

  // The feed: the open shift by default; a cashier may also read the last closed one, the owner any.
  const feedChoices = [
    ...(shift ? [{ id: shift.id, label: t('Текущая смена') }] : []),
    ...(isOwner ? history : history.slice(0, 1)).map((s) => ({
      id: s.id,
      label: `${dateTime(s.openedAt)} · ${s.staffName}`,
    })),
  ];
  const feedId = feedShift && feedChoices.some((c) => c.id === feedShift) ? feedShift : (feedChoices[0]?.id ?? '');
  const feedKinds: OperationKind[] = FEED_FILTERS.find((f) => f.id === feedFilter)?.kinds ?? [];

  const title = shift
    ? t('Смена · {name} с {time}', { name: shift.staffName, time: time(shift.openedAt) })
    : t('Смена');

  return (
    <div className="flex flex-col gap-5">
      <PageHeader title={title} />
      <Note note={note} />

      {zReport && zReport.shift.totals && (
        <Section
          title={t('Z-отчёт · {name}', { name: zReport.shift.staffName })}
          actions={
            <>
              <Button
                size="sm"
                onClick={() => print(() => printZ(zReport.shift, zReport.expectedCash, club.clubName, false))}
              >
                {t('Печать Z')}
              </Button>
              <Button variant="ghost" size="sm" onClick={() => setZReport(null)}>
                {t('Скрыть')}
              </Button>
            </>
          }
        >
          <TotalsGrid x={zReport.shift.totals} />
          {zReport.shift.totals.topUpByMethod && <MethodsGrid byMethod={zReport.shift.totals.topUpByMethod} />}
          <StatGrid cols="grid-cols-1 md:grid-cols-3">
            <Stat label={t('Ожидалось в кассе')} value={sum(zReport.expectedCash)} unit={t('сум')} />
            <Stat label={t('Посчитано')} value={sum(zReport.shift.closingCash ?? 0)} unit={t('сум')} />
            <Stat
              label={t('Расхождение')}
              value={signed((zReport.shift.closingCash ?? 0) - zReport.expectedCash)}
              unit={t('сум')}
              tone={diffTone((zReport.shift.closingCash ?? 0) - zReport.expectedCash)}
            />
          </StatGrid>
        </Section>
      )}

      {loaded && !shift && (
        <Section title={t('Открыть смену')}>
          <div className="flex flex-wrap items-end gap-3">
            <Field label={t('Наличные в кассе на начало')} className="w-64">
              <MoneyInput value={opening} onChange={setOpening} disabled={busy} />
            </Field>
            <Button variant="primary" disabled={busy} onClick={() => void open()}>
              {t('Открыть смену')}
            </Button>
          </div>
        </Section>
      )}

      {shift && x && (
        <>
          <Section
            title={t('X-отчёт')}
            actions={
              <Button size="sm" onClick={() => print(() => printX(club.clubName))}>
                {t('Печать X')}
              </Button>
            }
          >
            <TotalsGrid x={x} />
            {x.topUpByMethod && <MethodsGrid byMethod={x.topUpByMethod} />}
          </Section>

          {desk.cashDesk2 && (
            <Section
              title={t('Внесение / изъятие')}
              actions={
                <>
                  <Button size="sm" onClick={() => desk.requestCashMove('in')}>
                    {t('Внесение')}
                  </Button>
                  <Button size="sm" onClick={() => desk.requestCashMove('out')}>
                    {t('Изъятие')}
                  </Button>
                </>
              }
            >
              <OperationsFeed
                placement="page"
                shiftId={shift.id}
                kinds={['cashIn', 'cashOut', 'payout']}
                showToday={false}
              />
            </Section>
          )}

          <Section title={t('Закрыть смену')}>
            <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_22rem]">
              <StatGrid cols="grid-cols-1 md:grid-cols-3">
                <Stat label={t('На начало смены')} value={sum(shift.openingCash)} unit={t('сум')} />
                <Stat label={t('Ожидается в кассе')} value={sum(expected)} unit={t('сум')} />
                <Stat label={t('Расхождение')} value={signed(diff)} unit={t('сум')} tone={diffTone(diff)} />
              </StatGrid>
              <DrawerLine opening={shift.openingCash} x={x} expected={expected} />
            </div>
            {debts.length > 0 && (
              <p className="rounded-md bg-warning/10 px-3 py-2 text-sm text-warning">
                {t(
                  'Не оплачено долгов: {n} на {sum}. Смену можно закрыть — они останутся в «Расчёт с гостями и долги».',
                  {
                    n: debts.length,
                    sum: moneyExact(debtTotal),
                  },
                )}
              </p>
            )}
            <div className="flex flex-wrap items-end gap-3">
              <Field label={t('Посчитано в кассе')} className="w-64">
                <MoneyInput value={counted} onChange={setCounted} disabled={busy} />
              </Field>
              <Button variant="primary" disabled={busy} onClick={() => void close()}>
                {t('Закрыть смену')}
              </Button>
            </div>
          </Section>
        </>
      )}

      {feedId && desk.cashDesk2 && (
        <Section
          title={t('Операции смены')}
          actions={
            feedChoices.length > 1 ? (
              <select
                aria-label={t('Смена')}
                className={clsx(inputCls, 'h-8 w-56 text-xs')}
                value={feedId}
                onChange={(e) => setFeedShift(e.target.value)}
              >
                {feedChoices.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.label}
                  </option>
                ))}
              </select>
            ) : undefined
          }
        >
          <div role="group" aria-label={t('Фильтр')} className="flex flex-wrap gap-1.5">
            {FEED_FILTERS.map((f) => (
              <Button
                key={f.id}
                size="sm"
                aria-pressed={feedFilter === f.id}
                className={clsx(feedFilter === f.id && 'choice-on')}
                onClick={() => setFeedFilter(f.id)}
              >
                {t(f.label)}
              </Button>
            ))}
          </div>
          <OperationsFeed placement="page" shiftId={feedId} kinds={feedKinds} className="max-h-[32rem]" />
        </Section>
      )}

      <Section title={t('История смен')} bodyClassName="p-2">
        <Table
          rows={history}
          rowKey={(s) => s.id}
          empty={t('Закрытых смен пока нет')}
          columns={[
            { key: 'who', title: t('Кассир'), render: (s) => s.staffName },
            { key: 'open', title: t('Открыта'), render: (s) => <span className="tnum">{dateTime(s.openedAt)}</span> },
            {
              key: 'close',
              title: t('Закрыта'),
              render: (s) => <span className="tnum">{dateTime(s.closedAt)}</span>,
            },
            { key: 'closer', title: t('Закрыл'), render: (s) => s.closedBy ?? '—' },
            { key: 'sessions', title: t('Сеансы'), num: true, render: (s) => uzs(s.totals?.sessions) },
            { key: 'shop', title: t('Магазин'), num: true, render: (s) => uzs(s.totals?.shop) },
            { key: 'cash', title: t('Наличные в кассе'), num: true, render: (s) => uzs(s.closingCash) },
            {
              key: 'diff',
              title: t('Расхождение'),
              num: true,
              render: (s) => {
                const want = expectedOfClosed(s);
                if (s.closingCash === null || want === null) return '—';
                const d = s.closingCash - want;
                return (
                  <span className={d < 0 ? 'text-danger' : d > 0 ? 'text-success' : 'text-muted'}>
                    {`${signed(d)} ${t('сум')}`}
                  </span>
                );
              },
            },
            {
              key: 'print',
              title: '',
              width: '3rem',
              render: (s) =>
                s.totals ? (
                  <button
                    type="button"
                    aria-label={t('Печать копии Z')}
                    title={t('Печать копии Z')}
                    className="focus-ring h-7 w-7 rounded text-muted hover:bg-white/[0.06] hover:text-text"
                    onClick={() => print(() => printZ(s, expectedOfClosed(s), club.clubName, true))}
                  >
                    ⎙
                  </button>
                ) : null,
            },
          ]}
        />
      </Section>

      <PrintSettings />
    </div>
  );
}
