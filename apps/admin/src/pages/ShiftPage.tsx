/**
 * Cash shift: open with the drawer float, watch the X-report while the shift runs, put cash into the drawer or take it
 * out, close with the counted cash and get the Z-report; both split the top-ups and the bar by payment method (with the
 * bar's voids) and show the drawer line (float + desk cash + bar cash + in − out − payouts = expected, the server's figure). X and Z print on the report paper; the
 * print settings of this console (paper per kind, the receipt language) are set here. Below, the shift's operations
 * (the owner may pick an older shift) and the closed shifts with who closed them and a reprint of their Z. Polls
 * `/admin/shift` every 5 s and tells the console's shift state (the top-bar chip, the money buttons) when it changes.
 *
 * Variant F: the X / Z report as groups of compact KPI cards (Doto figures, zeros quiet, voids and any difference
 * amber); below, the shift's operations beside a 400 px column with the drawer card (close: the float and the expected
 * cash, how it adds up, the counted cash and the difference; or open) and the cash moves; then the history and the
 * print settings.
 */
import { useCallback, useEffect, useState } from 'react';
import clsx from 'clsx';
import { adminApi, clubApi, expectedOf, type GuestDebt, type OperationKind, type Shift, type ShiftTotals } from '@/api';
import { useClub } from '@/club';
import { describe } from '@/errors';
import { LANGS, dateLocale, t, type Lang } from '@/i18n';
import { money, moneyExact } from '@/format';
import { PrintIcon } from '@/icons';
import { FEED_FILTERS, OperationsFeed } from '@/operations';
import { PAY_METHODS } from '@/paybox';
import {
  PAPERS,
  ShiftReport,
  paperOf,
  printDocument,
  setPaper,
  setReceiptLang,
  barAutoReceipt,
  setBarAutoReceipt,
  storedReceiptLang,
  type Paper,
  type PrintKind,
} from '@/print';
import { drawerMoves, printX, useShift } from '@/shift';
import { Button, Chip, Field, KpiCard, MoneyInput, Note, PageHeader, Section, Sum, Table, Well, inputCls } from '@/ui';

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

/**
 * One stat of the X / Z report: a compact KPI card (mono label, Doto 26 figure, «сум»), a `group` named by its label (the
 * report's figures are found that way). A zero is quiet and bare (no «сум» brighter than its «0»), so the figures that
 * moved stand out; `warn` is amber (a drawer difference). The cards sit inside the report's glass panel, which already
 * blurs the wallpaper: they add no blur of their own.
 */
function Stat({
  label,
  value,
  unit,
  tone,
}: {
  label: string;
  value: string;
  unit?: string;
  tone?: 'warn';
}): JSX.Element {
  return (
    <KpiCard
      compact
      groupLabel={label}
      label={label}
      value={value}
      unit={value === '0' ? undefined : unit}
      tone={value === '0' && tone !== 'warn' ? 'quiet' : 'default'}
      valueClassName={tone === 'warn' ? '!text-warning' : undefined}
      className="[-webkit-backdrop-filter:none] [backdrop-filter:none]"
    />
  );
}

/**
 * A group of the report: a mono head on a fading rule (as the hall's zones), then its cards, six to a row on a wide
 * screen; `foot` is a muted line under them (the methods that took nothing, the bar's voids).
 */
function StatGroup({
  label,
  aside,
  foot,
  children,
}: {
  label: string;
  aside?: React.ReactNode;
  foot?: React.ReactNode;
  children?: React.ReactNode;
}): JSX.Element {
  return (
    <div role="group" aria-label={label} className="flex flex-col gap-2.5">
      <div className="flex h-4 items-center gap-3">
        <span className="font-mono text-[10.5px] font-semibold uppercase leading-none tracking-[0.2em] text-text">
          {label}
        </span>
        <span aria-hidden="true" className="h-px min-w-6 flex-1 bg-gradient-to-r from-accent/[0.18] to-accent/[0.04]" />
        {aside && <span className="label tracking-[0.14em]">{aside}</span>}
      </div>
      {children && <div className="grid grid-cols-2 gap-2.5 md:grid-cols-3 xl:grid-cols-6">{children}</div>}
      {foot && <p className="text-xs leading-5 text-muted">{foot}</p>}
    </div>
  );
}

/** «Карта · Payme · Click — 0 сум»: the methods of a group that took nothing, as one muted line instead of cards. */
function zeroLine(labels: string[]): string | null {
  return labels.length > 0 ? `${labels.join(' · ')} — 0 ${t('сум')}` : null;
}

/** The shift's money and the drawer's own moves (cash in, cash out, payouts, API cash) with the count of operations. */
function TotalsGrid({ x }: { x: ShiftTotals }): JSX.Element {
  const money: [string, number][] = [
    [t('Пополнения наличными'), x.topUpCash],
    [t('Пополнения картой/онлайн'), x.topUpOther],
    [t('Сеансы'), x.sessions],
    [t('Магазин'), x.shop],
    [t('Возвраты'), x.refunds],
    [t('Бонусы'), x.bonuses],
  ];
  const drawer: [string, number][] = [
    [t('Внесения'), x.cashIn ?? 0],
    [t('Изъятия'), x.cashOut ?? 0],
    [t('Выдачи гостям'), x.payouts ?? 0],
    [t('Через API (нал.)'), x.apiCash ?? 0],
  ];
  return (
    <>
      <StatGroup label={t('Итоги смены')}>
        {money.map(([label, v]) => (
          <Stat key={label} label={label} value={sum(v)} unit={t('сум')} />
        ))}
      </StatGroup>
      <StatGroup label={t('Касса')}>
        {drawer.map(([label, v]) => (
          <Stat key={label} label={label} value={sum(v)} unit={t('сум')} />
        ))}
        <Stat label={t('Операций')} value={String(x.count)} />
      </StatGroup>
    </>
  );
}

/**
 * Top-ups by payment method: what the drawer, the terminal and each wallet app should each add up to. A method that
 * took nothing is a name in the muted line under the cards.
 */
function MethodsGrid({ byMethod }: { byMethod: NonNullable<ShiftTotals['topUpByMethod']> }): JSX.Element {
  const all = [
    ...PAY_METHODS.map((m) => ({ id: m.id, label: t(m.label) })),
    { id: 'other' as const, label: t('Другое') },
  ];
  const moved = all.filter((m) => byMethod[m.id] !== 0);
  return (
    <StatGroup
      label={t('Пополнения по способам оплаты')}
      foot={zeroLine(all.filter((m) => byMethod[m.id] === 0).map((m) => m.label))}
    >
      {moved.length > 0 &&
        moved.map((m) => <Stat key={m.id} label={m.label} value={sum(byMethod[m.id])} unit={t('сум')} />)}
    </StatGroup>
  );
}

/**
 * The bar of the shift by method, net of its voids (D-57): the drawer's cash, each terminal and wallet app, what the
 * balances paid, and how many sales were taken back for how much (amber once there is one). Nothing from a server or a
 * Z before cash desk part 3.
 */
function ShopGrid({ x }: { x: ShiftTotals }): JSX.Element | null {
  const shop = x.shopByMethod;
  if (!shop) return null;
  const all = [
    ...PAY_METHODS.map((m) => ({ id: m.id, label: `${t('Бар')} · ${t(m.label)}`, short: t(m.label) })),
    { id: 'balance' as const, label: t('Бар · с баланса'), short: t('с баланса') },
  ];
  const moved = all.filter((m) => shop[m.id] !== 0);
  const zero = zeroLine(all.filter((m) => shop[m.id] === 0).map((m) => m.short));
  const voids = x.shopVoidCount ?? 0;
  return (
    <StatGroup
      label={t('Бар по способам оплаты')}
      foot={
        <>
          {zero && (
            <>
              {zero}
              <br />
            </>
          )}
          {/* The voids under the cards (the group stays one row); amber once there is one. */}
          <span className={clsx(voids > 0 && 'text-warning')}>
            {t('Аннулировано: {n}', { n: voids })} · {sum(x.shopVoids ?? 0)} {t('сум')}
          </span>
        </>
      }
    >
      {moved.length > 0 &&
        moved.map((m) => <Stat key={m.id} label={m.label} value={sum(shop[m.id])} unit={t('сум')} />)}
    </StatGroup>
  );
}

/** Float + the desk's cash top-ups + the bar's cash + cash in − cash out − payouts = what the drawer should hold. */
function DrawerLine({ opening, x, expected }: { opening: number; x: ShiftTotals; expected: number }): JSX.Element {
  const rows: [string, number][] = [
    [t('На начало смены'), opening],
    [t('+ наличные пополнения'), x.topUpCash - (x.apiCash ?? 0)],
    ...(x.shopByMethod ? ([[t('+ наличные продажи бара'), x.shopByMethod.cash]] as [string, number][]) : []),
    [t('+ внесения'), x.cashIn ?? 0],
    [t('− изъятия'), x.cashOut ?? 0],
    [t('− выдачи гостям'), x.payouts ?? 0],
  ];
  return (
    <dl aria-label={t('Наличные в кассе')} className="well flex flex-col gap-1.5 px-3.5 py-3 text-[13px] leading-5">
      {rows.map(([label, v]) => (
        <div key={label} className="flex justify-between gap-3">
          <dt className="min-w-0 text-dim">{label}</dt>
          <dd className="tnum shrink-0 whitespace-nowrap font-mono text-[12.5px] text-text">
            <Sum minor={v} />
          </dd>
        </div>
      ))}
      <div className="mt-0.5 flex justify-between gap-3 border-t border-accent/[0.08] pt-2 font-semibold text-hi">
        <dt>{t('= ожидается')}</dt>
        <dd className="tnum whitespace-nowrap font-mono text-[12.5px]">
          <Sum minor={expected} />
        </dd>
      </div>
      {(x.apiCash ?? 0) > 0 && (
        <p className="pt-1 text-xs text-muted">
          {t('Пополнения через API ({sum}) в кассу не попадают', { sum: uzs(x.apiCash ?? 0) })}
        </p>
      )}
    </dl>
  );
}

/** A difference of the drawer: none reads plainly, any other amber (short or over, the sign says which). */
function diffTone(d: number): 'warn' | undefined {
  return d === 0 ? undefined : 'warn';
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
  const [auto, setAuto] = useState<boolean>(barAutoReceipt);
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
      <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
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
        <Field label={t('Чек в баре')}>
          <select
            className={inputCls}
            value={auto ? 'auto' : 'button'}
            onChange={(e) => {
              const on = e.target.value === 'auto';
              setBarAutoReceipt(on);
              setAuto(on);
            }}
          >
            <option value="button">{t('По кнопке «Чек»')}</option>
            <option value="auto">{t('Сразу после продажи')}</option>
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

  const hasFeed = Boolean(feedId && desk.cashDesk2);
  const hasMoves = Boolean(shift && x && desk.cashDesk2);
  // The drawer card: close the open shift, or open one; nothing until the first answer.
  const drawerCard = shift && x ? 'close' : loaded && !shift ? 'open' : null;
  const twoColumns = hasFeed && (hasMoves || drawerCard !== null);

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
                variant="utility"
                size="sm"
                onClick={() => print(() => printZ(zReport.shift, zReport.expectedCash, club.clubName, false))}
              >
                <PrintIcon size={16} />
                {t('Печать Z')}
              </Button>
              <Button variant="ghost" size="sm" onClick={() => setZReport(null)}>
                {t('Скрыть')}
              </Button>
            </>
          }
          bodyClassName="gap-5"
        >
          <StatGroup label={t('Наличные в кассе')}>
            <Stat label={t('Ожидалось в кассе')} value={sum(zReport.expectedCash)} unit={t('сум')} />
            <Stat label={t('Посчитано')} value={sum(zReport.shift.closingCash ?? 0)} unit={t('сум')} />
            <Stat
              label={t('Расхождение')}
              value={signed((zReport.shift.closingCash ?? 0) - zReport.expectedCash)}
              unit={t('сум')}
              tone={diffTone((zReport.shift.closingCash ?? 0) - zReport.expectedCash)}
            />
          </StatGroup>
          <TotalsGrid x={zReport.shift.totals} />
          {zReport.shift.totals.topUpByMethod && <MethodsGrid byMethod={zReport.shift.totals.topUpByMethod} />}
          <ShopGrid x={zReport.shift.totals} />
        </Section>
      )}

      {shift && x && (
        <Section
          title={t('X-отчёт')}
          actions={
            <Button variant="utility" size="sm" onClick={() => print(() => printX(club.clubName))}>
              <PrintIcon size={16} />
              {t('Печать X')}
            </Button>
          }
          bodyClassName="gap-5"
        >
          <TotalsGrid x={x} />
          {x.topUpByMethod && <MethodsGrid byMethod={x.topUpByMethod} />}
          <ShopGrid x={x} />
        </Section>
      )}

      {(hasFeed || hasMoves || drawerCard) && (
        <div
          className={clsx(
            'grid items-start gap-4',
            // Two columns: the feed's panel runs as tall as the drawer column beside it (no hole under a short feed).
            twoColumns && 'xl:grid-cols-[minmax(0,1fr)_400px] xl:items-stretch',
            // An older server has no feed: the drawer card keeps its column's width instead of the page's.
            !hasFeed && 'max-w-[480px]',
          )}
        >
          {hasFeed && (
            <Section
              title={t('Операции смены')}
              className={clsx(twoColumns && 'xl:h-full')}
              actions={
                feedChoices.length > 1 ? (
                  <select
                    aria-label={t('Смена')}
                    className={clsx(inputCls, '!h-9 w-60 !text-[13px]')}
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
              bodyClassName="min-h-0 flex-1 gap-3"
            >
              <div role="group" aria-label={t('Фильтр')} className="-ml-3 flex flex-wrap gap-1">
                {FEED_FILTERS.map((f) => (
                  <Chip key={f.id} pressed={feedFilter === f.id} onClick={() => setFeedFilter(f.id)}>
                    {t(f.label)}
                  </Chip>
                ))}
              </div>
              {/* Beside the drawer column the feed fills what is left of its panel (and scrolls); alone it is capped. */}
              <div className={clsx('flex min-h-0 flex-1 flex-col', twoColumns && 'xl:relative xl:min-h-[24rem]')}>
                <OperationsFeed
                  placement="page"
                  shiftId={feedId}
                  kinds={feedKinds}
                  className={clsx('max-h-[32rem]', twoColumns && 'xl:absolute xl:inset-0 xl:max-h-none')}
                />
              </div>
            </Section>
          )}

          {(hasMoves || drawerCard) && (
            <div className="flex min-w-0 flex-col gap-4">
              {drawerCard === 'close' && shift && x && (
                <Section title={t('Закрыть смену')} variant="solid" className="edge-top">
                  <div className="grid grid-cols-2 gap-2">
                    <Well
                      groupLabel={t('На начало смены')}
                      label={t('На начало смены')}
                      value={sum(shift.openingCash)}
                      unit={t('сум')}
                    />
                    <Well
                      groupLabel={t('Ожидается в кассе')}
                      label={t('Ожидается в кассе')}
                      value={sum(expected)}
                      unit={t('сум')}
                    />
                  </div>
                  <DrawerLine opening={shift.openingCash} x={x} expected={expected} />
                  {debts.length > 0 && (
                    <Note tone="warn">
                      {t(
                        'Не оплачено долгов: {n} на {sum}. Смену можно закрыть — они останутся в «Расчёт с гостями и долги».',
                        {
                          n: debts.length,
                          sum: moneyExact(debtTotal),
                        },
                      )}
                    </Note>
                  )}
                  <Field label={t('Посчитано в кассе')}>
                    <MoneyInput value={counted} onChange={setCounted} disabled={busy} />
                  </Field>
                  <Well
                    groupLabel={t('Расхождение')}
                    label={t('Расхождение')}
                    value={signed(diff)}
                    unit={t('сум')}
                    size={26}
                    tone={diffTone(diff)}
                  />
                  <Button variant="primary" size="lg" className="w-full" disabled={busy} onClick={() => void close()}>
                    {t('Закрыть смену')}
                  </Button>
                </Section>
              )}

              {drawerCard === 'open' && (
                <Section title={t('Открыть смену')} variant="solid" className="edge-top">
                  <Field label={t('Наличные в кассе на начало')}>
                    <MoneyInput value={opening} onChange={setOpening} disabled={busy} />
                  </Field>
                  <Button variant="primary" size="lg" className="w-full" disabled={busy} onClick={() => void open()}>
                    {t('Открыть смену')}
                  </Button>
                </Section>
              )}

              {hasMoves && shift && (
                <Section title={t('Внесение / изъятие')} variant="side" bodyClassName="gap-3">
                  <div className="grid grid-cols-2 gap-2">
                    <Button onClick={() => desk.requestCashMove('in')}>{t('Внесение')}</Button>
                    <Button onClick={() => desk.requestCashMove('out')}>{t('Изъятие')}</Button>
                  </div>
                  <OperationsFeed
                    placement="page"
                    shiftId={shift.id}
                    kinds={['cashIn', 'cashOut', 'payout']}
                    className="max-h-[20rem]"
                  />
                </Section>
              )}
            </div>
          )}
        </div>
      )}

      <Section title={t('История смен')} bodyClassName="p-2">
        <Table
          rows={history}
          rowKey={(s) => s.id}
          empty={t('Закрытых смен пока нет')}
          columns={[
            { key: 'who', title: t('Кассир'), render: (s) => <span className="font-medium">{s.staffName}</span> },
            {
              key: 'open',
              title: t('Открыта'),
              render: (s) => <span className="tnum font-mono text-xs text-dim">{dateTime(s.openedAt)}</span>,
            },
            {
              key: 'close',
              title: t('Закрыта'),
              render: (s) => <span className="tnum font-mono text-xs text-dim">{dateTime(s.closedAt)}</span>,
            },
            {
              key: 'closer',
              title: t('Закрыл'),
              render: (s) => <span className="text-dim">{s.closedBy ?? '—'}</span>,
            },
            { key: 'sessions', title: t('Сеансы'), num: true, render: (s) => <Sum minor={s.totals?.sessions} /> },
            { key: 'shop', title: t('Магазин'), num: true, render: (s) => <Sum minor={s.totals?.shop} /> },
            { key: 'cash', title: t('Наличные в кассе'), num: true, render: (s) => <Sum minor={s.closingCash} /> },
            {
              key: 'diff',
              title: t('Расхождение'),
              num: true,
              render: (s) => {
                const want = expectedOfClosed(s);
                if (s.closingCash === null || want === null) return '—';
                const d = s.closingCash - want;
                return (
                  <span className={clsx('whitespace-nowrap', d !== 0 ? 'font-semibold text-warning' : 'text-muted')}>
                    {signed(d)} <span className="font-sans font-medium text-muted">{t('сум')}</span>
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
                    className="focus-ring inline-flex h-7 w-7 items-center justify-center rounded-sm text-muted hover:bg-text/[0.06] hover:text-text"
                    onClick={() => print(() => printZ(s, expectedOfClosed(s), club.clubName, true))}
                  >
                    <PrintIcon size={15} />
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
