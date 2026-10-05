/**
 * The KPI strip over the counter pages (variant F): four glass cards that answer "how is the shift going" at a glance.
 *
 * 1. «Занято» — seats with a session out of all, the occupancy ticks and how many are free (from App's 5 s overview).
 * 2. «Сегодня принято» — today's money (the club's local day) net of the cash given back to guests, cash and cashless;
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
import { exactDigits, moneyParts } from '@/format';
import { t } from '@/i18n';
import { BellIcon } from '@/icons';
import { methodName } from '@/paybox';
import { CashMenu, cashSplit, useShift } from '@/shift';
import { KpiBody, KpiCard, Ticks } from '@/ui';

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

function OccupancyCard({ hall }: { hall: HallCounts | null }): JSX.Element {
  const unavailable = hall ? Math.max(0, hall.total - hall.occupied - hall.free) : 0;
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
 * «Сегодня принято»: the card is the button and its text reads «Сегодня принято 38 400 сум нал … · безнал …» (real
 * spaces between the pieces); the split by method opens under it.
 */
function TodayCard({ today }: { today: Today | null }): JSX.Element {
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
  if (!today) return <KpiCard label={t('Сегодня принято')} value="—" tone="quiet" />;
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
          label={t('Сегодня принято')}
          value={moneyParts(split.taken).num}
          unit={t('сум')}
          sub={`${t('нал {sum}', { sum: exactDigits(split.cash) })} · ${t('безнал {sum}', { sum: exactDigits(split.cashless) })}`}
        />
      </button>
      {open && (
        <dl className="panel-solid anim-rise absolute left-0 top-[calc(100%+8px)] z-40 grid w-[19rem] grid-cols-2 gap-x-5 gap-y-1.5 px-4 py-3.5 text-xs">
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

function CashCard(): JSX.Element {
  const { shift, x, expectedCash } = useShift();
  const split = x ? cashSplit(x) : null;
  const cash = shift && split ? (expectedCash ?? split.cash) : null;
  return (
    <KpiCard
      label={t('В кассе')}
      value={cash !== null ? moneyParts(cash).num : '—'}
      unit={cash !== null ? t('сум') : undefined}
      tone={cash !== null ? 'default' : 'quiet'}
      sub={shift && split ? t('безнал {sum} сум', { sum: nf.format(Math.round(split.cashless / 100)) }) : undefined}
      aside={<CashMenu variant="kpi" />}
    />
  );
}

function CallsCard(): JSX.Element {
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
    />
  );
}

/** The strip: four cards on a grid of 1 : 1 : 1.45 : 1.12 (three without today's money on an older server). */
export function KpiStrip({ hall }: { hall: HallCounts | null }): JSX.Element {
  const { today, missing } = useToday();
  return (
    <section
      aria-label={t('Сводка смены')}
      className={clsx(
        'relative z-20 grid gap-3',
        missing
          ? 'grid-cols-[minmax(0,1fr)_minmax(0,1.45fr)_minmax(0,1.12fr)]'
          : 'grid-cols-[minmax(0,1fr)_minmax(0,1fr)_minmax(0,1.45fr)_minmax(0,1.12fr)]',
      )}
    >
      <OccupancyCard hall={hall} />
      {!missing && <TodayCard today={today} />}
      <CashCard />
      <CallsCard />
    </section>
  );
}
