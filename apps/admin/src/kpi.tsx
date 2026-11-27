/**
 * The KPI strip over the counter pages (variant F): four glass cards that answer "how is the shift going" at a glance.
 *
 * 1. «Занято» — seats with a session out of all, the occupancy ticks and how many are free (from App's 5 s overview).
 * 2. «Принято за смену» — the open shift's money (zero again after every close, D-83) net of the cash given back to guests, cash and cashless;
 *    the card is a button that opens the split by method, the payouts and the sessions. Polled every 15 s and whenever
 *    the shift's money changes; hidden on a server without the operations feed.
 * 3. «В кассе» — the cash the drawer should hold and the cashless taken in the shift, with «Внесение и изъятие».
 * 4. «Вызовы» — amber while a player calls the desk (the first PC, the bell's list behind the chevron), quiet with no
 *    button when nobody calls; the browser's «sound off» chip lives here.
 */
import { useEffect, useRef, useState } from 'react';
import clsx from 'clsx';
import { AdminError, clubApi, type Today } from '@/api';
import { AudioUnlockChip, CallsBell, groupCalls, useCalls } from '@/calls';
import { pcLabel } from '@/clientSearch';
import { exactDigits, moneyParts, moneyShort } from '@/format';
import { t } from '@/i18n';
import { BellIcon, ChevronDownIcon } from '@/icons';
import { methodName } from '@/paybox';
import { CashMenu, cashSplit, useShift } from '@/shift';
import { KpiBody, KpiCard, NARROW_QUERY, PHONE_QUERY, Ticks, useMedia } from '@/ui';

const TODAY_POLL_MS = 15_000;
const nf = new Intl.NumberFormat('ru-RU');

/** The hall's counts for card 1: seats with a session, free seats, all seats. */
export interface HallCounts {
  occupied: number;
  free: number;
  total: number;
  /** How many zones the hall has (the header's «Касса · 24 ПК · 3 зоны»). */
  zones: number;
}

/**
 * Today's money for the strip: `GET /admin/shift/operations?limit=1` (its `today`), every 15 s and after every shift
 * answer (a sale, a top-up). null until the first answer; `missing` — the server has no feed (an older one).
 */
export function useToday(): { today: Today | null; missing: boolean } {
  const { version, cashDesk2 } = useShift();
  const [today, setToday] = useState<Today | null>(null);
  const [missing, setMissing] = useState(false);
  const alive = useRef(true);
  useEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
    };
  }, []);
  useEffect(() => {
    if (!cashDesk2 || missing) return undefined;
    const load = (): void => {
      clubApi
        .operations({ limit: 1 })
        .then((r) => {
          if (alive.current) setToday(r.today);
        })
        .catch((e: unknown) => {
          if (alive.current && e instanceof AdminError && e.status === 404 && e.details?.['what'] !== 'shift')
            setMissing(true);
        });
    };
    load();
    const id = window.setInterval(() => {
      if (document.visibilityState === 'visible') load();
    }, TODAY_POLL_MS);
    return () => window.clearInterval(id);
  }, [version, cashDesk2, missing]);
  return { today, missing: missing || !cashDesk2 };
}

/** Cash and cashless of `today`: the bar's cash is cash taken too (D-57), the payouts go back out of the cash. */
function todaySplit(today: Today): { taken: number; cash: number; cashless: number; shop: number } {
  const shopCash = today.shopByMethod?.cash ?? 0;
  const shop = today.shopByMethod ? Object.values(today.shopByMethod).reduce((a, b) => a + b, 0) : 0;
  return {
    taken: today.taken - today.payouts,
    cash: today.byMethod.cash + shopCash - today.payouts,
    cashless: today.taken - today.byMethod.cash - shopCash,
    shop,
  };
}

function OccupancyCard({ hall, phone }: { hall: HallCounts | null; phone: boolean }): JSX.Element {
  const unavailable = hall ? Math.max(0, hall.total - hall.occupied - hall.free) : 0;
  if (phone) {
    // Half a phone's width: the count and the free seats under it, without the ticks.
    return (
      <KpiCard
        label={t('Занято')}
        big
        value={
          hall ? (
            <>
              {hall.occupied}
              <span className="ml-1 text-base text-muted">/{hall.total}</span>
            </>
          ) : (
            <span className="text-muted">—</span>
          )
        }
        sub={hall ? t('{n} свободно', { n: hall.free }) : undefined}
      />
    );
  }
  return (
    <KpiCard
      label={t('Занято')}
      big
      value={
        hall ? (
          <>
            {hall.occupied}
            <span className="ml-1.5 text-lg text-muted">/{hall.total}</span>
          </>
        ) : (
          <span className="text-muted">—</span>
        )
      }
      aside={
        hall && (
          <span className="flex flex-col items-end gap-[9px]">
            <Ticks
              busy={hall.occupied}
              free={hall.free}
              unavailable={unavailable}
              label={t('{busy} занято, {free} свободно, {off} недоступно', {
                busy: hall.occupied,
                free: hall.free,
                off: unavailable,
              })}
              className="max-[1399px]:[&>span]:h-3.5"
            />
            <span className="label whitespace-nowrap tracking-[0.14em]">{t('{n} свободно', { n: hall.free })}</span>
          </span>
        )
      }
    />
  );
}

/**
 * «Принято за смену»: the card is the button and its text reads «Принято за смену 38 400 сум нал … · безнал …» (real
 * spaces between the pieces); the split by method opens under it.
 */
function TodayCard({ today, phone }: { today: Today | null; phone: boolean }): JSX.Element {
  const [open, setOpen] = useState(false);
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return undefined;
    const close = (e: MouseEvent): void => {
      if (box.current && !box.current.contains(e.target as Node)) setOpen(false);
    };
    const leave = (): void => setOpen(false);
    window.addEventListener('mousedown', close);
    window.addEventListener('hashchange', leave);
    return () => {
      window.removeEventListener('mousedown', close);
      window.removeEventListener('hashchange', leave);
    };
  }, [open]);
  // Until the first answer the card is not a button: whoever reads the button reads a real figure.
  if (!today) return <KpiCard label={t('Принято за смену')} value="—" tone="quiet" />;
  const split = todaySplit(today);
  return (
    <div ref={box} className="relative min-w-0">
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
        onKeyDown={(e) => {
          if (e.key === 'Escape' && open) {
            e.stopPropagation();
            setOpen(false);
          }
        }}
        className={clsx(
          'focus-ring glass-kpi kpi-card flex w-full min-w-0 items-center rounded-md text-left transition-colors hover:border-accent/[0.26]',
          open && 'border-accent/[0.26]',
        )}
      >
        <KpiBody
          label={t('Принято за смену')}
          value={phone ? moneyShort(split.taken) : moneyParts(split.taken).num}
          unit={t('сум')}
          sub={`${t('нал {sum}', { sum: exactDigits(split.cash) })} · ${t('безнал {sum}', { sum: exactDigits(split.cashless) })}`}
        />
      </button>
      {open && (
        <dl className="panel-solid anim-rise absolute left-0 top-[calc(100%+8px)] z-40 grid w-[19rem] max-w-[calc(100vw-2rem)] grid-cols-2 gap-x-5 gap-y-1.5 px-4 py-3.5 text-xs">
          {(['cash', 'card', 'payme', 'click', 'uzum'] as const).map((m) => (
            <div key={m} className="flex justify-between gap-2">
              <dt className="text-muted">{methodName(m)}</dt>
              <dd className="tnum font-mono">{exactDigits(today.byMethod[m])}</dd>
            </div>
          ))}
          {today.byMethod.other > 0 && (
            <div className="flex justify-between gap-2">
              <dt className="text-muted">{t('Другое')}</dt>
              <dd className="tnum font-mono">{exactDigits(today.byMethod.other)}</dd>
            </div>
          )}
          {today.shopByMethod && (
            <div className="flex justify-between gap-2">
              <dt className="text-muted">{t('Бар')}</dt>
              <dd className="tnum font-mono">{exactDigits(split.shop)}</dd>
            </div>
          )}
          <div className="flex justify-between gap-2">
            <dt className="text-muted">{t('Выдано гостям')}</dt>
            <dd className="tnum font-mono">{exactDigits(today.payouts)}</dd>
          </div>
          <div className="flex justify-between gap-2">
            <dt className="text-muted">{t('Сеансы')}</dt>
            <dd className="tnum font-mono">{exactDigits(today.sessions)}</dd>
          </div>
        </dl>
      )}
    </div>
  );
}

function CashCard({ phone }: { phone: boolean }): JSX.Element {
  const { shift, x, expectedCash } = useShift();
  const split = x ? cashSplit(x) : null;
  const cash = shift && split ? (expectedCash ?? split.cash) : null;
  return (
    <KpiCard
      label={t('В кассе')}
      value={cash !== null ? (phone ? moneyShort(cash) : moneyParts(cash).num) : '—'}
      // Narrow: the «±» sits in the corner over the figure's line, so the figure goes without «сум».
      unit={cash !== null && !phone ? t('сум') : undefined}
      tone={cash !== null ? 'default' : 'quiet'}
      sub={shift && split ? t('безнал {sum} сум', { sum: nf.format(Math.round(split.cashless / 100)) }) : undefined}
      aside={<CashMenu variant="kpi" />}
      asideCorner={phone}
    />
  );
}

function CallsCard({ phone }: { phone: boolean }): JSX.Element {
  const calls = useCalls();
  const groups = groupCalls(calls ?? []);
  const first = groups[0];
  const ringing = groups.some((g) => g.ringing);
  if (!first) {
    return (
      <KpiCard
        label={t('Вызовы')}
        icon={<BellIcon size={13} strong />}
        value="0"
        tone="quiet"
        sub={t('Нет вызовов')}
        aside={<AudioUnlockChip variant="kpi" />}
        asideCorner={phone}
      />
    );
  }
  return (
    <KpiCard
      tone="attention"
      className={clsx(ringing && 'motion-safe:anim-warn-glow')}
      label={t('Вызовы')}
      icon={<BellIcon size={13} strong />}
      big
      value={
        <span className="flex items-center gap-3.5">
          {groups.length}
          <span className="flex min-w-0 flex-col gap-[5px] font-sans font-normal">
            <span className="truncate text-sm font-semibold leading-none text-hi">{pcLabel(first.pcName)}</span>
            <span className="truncate text-xs leading-none text-dim">{t('зовёт администратора')}</span>
          </span>
        </span>
      }
      aside={
        <>
          <AudioUnlockChip variant="kpi" />
          <CallsBell variant="kpi" />
        </>
      }
      asideCorner={phone}
    />
  );
}

const KPI_OPEN_KEY = 'clubshell.admin.kpiOpen';

/**
 * A phone's strip folded to one line (about 56 px instead of four cards): the four figures side by side, the calls
 * in amber while somebody calls. A tap unfolds the cards (with «Внесение и изъятие», the split by method and the calls'
 * list); the choice is remembered on this phone.
 */
function PhoneSummary({
  hall,
  today,
  missing,
  onOpen,
}: {
  hall: HallCounts | null;
  today: Today | null;
  missing: boolean;
  onOpen: () => void;
}): JSX.Element {
  const { shift, x, expectedCash } = useShift();
  const calls = groupCalls(useCalls() ?? []);
  const split = x ? cashSplit(x) : null;
  const cash = shift && split ? (expectedCash ?? split.cash) : null;
  const cells: { label: string; value: string; tone?: 'warn' }[] = [
    { label: t('Занято'), value: hall ? `${hall.occupied}/${hall.total}` : '—' },
    ...(missing ? [] : [{ label: t('Принято'), value: today ? moneyShort(todaySplit(today).taken) : '—' }]),
    { label: t('В кассе'), value: cash !== null ? moneyShort(cash) : '—' },
    { label: t('Вызовы'), value: String(calls.length), tone: calls.length > 0 ? ('warn' as const) : undefined },
  ];
  return (
    <button
      type="button"
      aria-expanded={false}
      aria-label={t('Сводка смены: показать подробно')}
      onClick={onOpen}
      className="glass-kpi focus-ring mb-3 flex w-full items-center gap-2 rounded-md px-3 py-2.5 text-left"
    >
      <span
        className="grid min-w-0 flex-1 gap-2"
        style={{ gridTemplateColumns: `repeat(${cells.length}, minmax(0, 1fr))` }}
      >
        {cells.map((c) => (
          <span key={c.label} className="flex min-w-0 flex-col gap-1">
            <span className={clsx('label-sm truncate', c.tone === 'warn' && 'text-warning')}>{c.label}</span>
            <span
              className={clsx(
                'tnum truncate font-mono text-[13px] font-semibold leading-none',
                c.tone === 'warn' ? 'text-warning' : 'text-hi',
              )}
            >
              {c.value}
            </span>
          </span>
        ))}
      </span>
      <ChevronDownIcon size={16} className="shrink-0 text-muted" />
    </button>
  );
}

function readOpen(): boolean {
  try {
    return localStorage.getItem(KPI_OPEN_KEY) === '1';
  } catch {
    return false;
  }
}

/** The strip: four cards on a grid of 1 : 1 : 1.45 : 1.12 (three without today's money on an older server). */
export function KpiStrip({ hall }: { hall: HallCounts | null }): JSX.Element {
  const { today, missing } = useToday();
  // A phone: one folded line at the top of the page (it scrolls away with it), the cards two by two when unfolded. A
  // tablet: one row of the compact cards.
  const phone = useMedia(PHONE_QUERY);
  const narrow = useMedia(NARROW_QUERY);
  const [open, setOpenState] = useState(readOpen);
  const setOpen = (v: boolean): void => {
    setOpenState(v);
    try {
      localStorage.setItem(KPI_OPEN_KEY, v ? '1' : '0');
    } catch {
      // remembered for this page only
    }
  };
  if (phone && !open) return <PhoneSummary hall={hall} today={today} missing={missing} onOpen={() => setOpen(true)} />;
  return (
    <section
      aria-label={t('Сводка смены')}
      className={clsx(
        'relative z-20 grid gap-3',
        phone
          ? 'kpi-phone mb-3 grid-cols-2 gap-2'
          : narrow
            ? clsx('kpi-phone gap-2.5', missing ? 'grid-cols-3' : 'grid-cols-4')
            : missing
              ? 'grid-cols-[minmax(0,1fr)_minmax(0,1.45fr)_minmax(0,1.12fr)]'
              : 'grid-cols-[minmax(0,1fr)_minmax(0,1fr)_minmax(0,1.45fr)_minmax(0,1.12fr)]',
      )}
    >
      <OccupancyCard hall={hall} phone={narrow} />
      {!missing && <TodayCard today={today} phone={narrow} />}
      <CashCard phone={narrow} />
      <CallsCard phone={narrow} />
      {phone && (
        <button
          type="button"
          aria-expanded
          onClick={() => setOpen(false)}
          className="focus-ring col-span-2 -mt-1 flex h-8 items-center justify-center gap-1.5 rounded-md text-xs font-medium text-muted"
        >
          <ChevronDownIcon size={14} className="rotate-180" />
          {t('Свернуть сводку')}
        </button>
      )}
    </section>
  );
}
