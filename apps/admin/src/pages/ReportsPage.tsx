/**
 * Owner reports for 7 / 30 / 90 days: totals, revenue per day (sessions + shop, stacked, to scale), occupancy by
 * weekday × hour, top games and products, and the shifts of the period.
 */
import { useEffect, useState } from 'react';
import clsx from 'clsx';
import { clubApi, type Reports } from '@/api';
import { describe } from '@/errors';
import { dateLocale, t } from '@/i18n';
import { money } from '@/format';
import { Note, PageHeader, Section, Table } from '@/ui';

const PERIODS = [7, 30, 90] as const;
const nf = new Intl.NumberFormat('ru-RU');
const sum = (minor: number): string => nf.format(Math.round(minor / 100));
const uzs = (minor: number | null | undefined): string =>
  minor === null || minor === undefined ? '—' : money({ amount: minor, currency: 'UZS' });

/** Rows Monday first; the server's heat index is 0 = Sunday. */
const WEEK: { label: string; idx: number }[] = [
  { label: 'Пн', idx: 1 },
  { label: 'Вт', idx: 2 },
  { label: 'Ср', idx: 3 },
  { label: 'Чт', idx: 4 },
  { label: 'Пт', idx: 5 },
  { label: 'Сб', idx: 6 },
  { label: 'Вс', idx: 0 },
];

function Stat({ label, value, unit }: { label: string; value: string; unit?: string }): JSX.Element {
  return (
    <div className="flex min-w-0 flex-col gap-2 bg-surface px-5 py-4">
      <span className="label truncate">{label}</span>
      <span className="flex items-baseline gap-1.5">
        <span className="num-dot text-[1.9rem] leading-none">{value}</span>
        {unit && <span className="text-xs text-muted">{unit}</span>}
      </span>
    </div>
  );
}

function shortDate(iso: string): string {
  const [, m, d] = iso.split('-');
  return `${d}.${m}`;
}

function dateTime(iso: string | null): string {
  if (!iso) return '…';
  return new Date(iso).toLocaleString(dateLocale(), {
    day: '2-digit',
    month: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function RevenueChart({ rows }: { rows: Reports['byDay'] }): JSX.Element {
  const max = Math.max(1, ...rows.map((r) => r.sessions + r.shop));
  const every = rows.length <= 10 ? 1 : rows.length <= 31 ? 3 : 7;
  return (
    <div className="flex flex-col gap-3">
      <div className="flex gap-3">
        <div className="flex h-56 w-20 shrink-0 flex-col justify-between text-right">
          <span className="tnum text-xs text-muted">{sum(max)}</span>
          <span className="tnum text-xs text-muted">{sum(max / 2)}</span>
          <span className="tnum text-xs text-muted">0</span>
        </div>
        <div className="relative flex h-56 min-w-0 flex-1 items-end gap-[2px] border-b border-l border-line">
          <div className="pointer-events-none absolute inset-x-0 top-0 border-t border-dashed border-line" />
          <div className="pointer-events-none absolute inset-x-0 top-1/2 border-t border-dashed border-line" />
          {rows.map((r) => (
            <div
              key={r.date}
              title={`${shortDate(r.date)} · ${t('Сеансы')} ${uzs(r.sessions)} · ${t('Магазин')} ${uzs(r.shop)}`}
              className="flex h-full min-w-0 flex-1 flex-col justify-end"
            >
              <div className="bg-success" style={{ height: `${(r.shop / max) * 100}%` }} />
              <div className="bg-accent" style={{ height: `${(r.sessions / max) * 100}%` }} />
            </div>
          ))}
        </div>
      </div>
      <div className="flex gap-3">
        <div className="w-20 shrink-0" />
        <div className="flex min-w-0 flex-1 gap-[2px]">
          {rows.map((r, i) => (
            <span
              key={r.date}
              className="tnum min-w-0 flex-1 overflow-visible whitespace-nowrap text-[0.65rem] text-muted"
            >
              {i % every === 0 ? shortDate(r.date) : ''}
            </span>
          ))}
        </div>
      </div>
      <div className="flex gap-5 pl-[5.75rem] text-xs text-muted">
        <span className="flex items-center gap-2">
          <span className="h-2.5 w-2.5 bg-accent" />
          {t('Сеансы')}
        </span>
        <span className="flex items-center gap-2">
          <span className="h-2.5 w-2.5 bg-success" />
          {t('Магазин')}
        </span>
        <span className="ml-auto">{t('сум в день')}</span>
      </div>
    </div>
  );
}

function HeatMap({ heat }: { heat: number[][] }): JSX.Element {
  const max = Math.max(1, ...heat.flat());
  const cols = { gridTemplateColumns: '2.5rem repeat(24, minmax(0, 1fr))' };
  return (
    <div className="flex flex-col gap-3">
      <div className="grid gap-[3px]" style={cols}>
        {WEEK.map((d) => (
          <div key={d.idx} className="contents">
            <span className="label flex items-center">{t(d.label)}</span>
            {Array.from({ length: 24 }, (_, h) => {
              const v = heat[d.idx]?.[h] ?? 0;
              return (
                <div
                  key={h}
                  title={`${t(d.label)} ${String(h).padStart(2, '0')}:00 · ${v}`}
                  className="h-7 rounded-[3px] border border-line"
                  style={{
                    backgroundColor: v > 0 ? `rgba(154,223,255,${(0.12 + (v / max) * 0.88).toFixed(3)})` : undefined,
                  }}
                />
              );
            })}
          </div>
        ))}
        <span />
        {Array.from({ length: 24 }, (_, h) => (
          <span key={h} className="tnum text-[0.65rem] text-muted">
            {h % 3 === 0 ? String(h).padStart(2, '0') : ''}
          </span>
        ))}
      </div>
      <div className="flex items-center justify-end gap-2 text-xs text-muted">
        <span className="tnum">0</span>
        {[0.12, 0.34, 0.56, 0.78, 1].map((o) => (
          <span key={o} className="h-3 w-5 rounded-[2px]" style={{ backgroundColor: `rgba(154,223,255,${o})` }} />
        ))}
        <span className="tnum">{t('{n} место-ч', { n: max })}</span>
      </div>
    </div>
  );
}

export default function ReportsPage(): JSX.Element {
  const [days, setDays] = useState<number>(7);
  const [data, setData] = useState<Reports | null>(null);
  const [note, setNote] = useState<{ text: string; tone: 'ok' | 'err' } | null>(null);

  useEffect(() => {
    let alive = true;
    clubApi
      .reports(days)
      .then((r) => {
        if (!alive) return;
        setData(r);
        setNote(null);
      })
      .catch((e: unknown) => alive && setNote({ text: describe(e), tone: 'err' }));
    return () => {
      alive = false;
    };
  }, [days]);

  return (
    <div className="flex flex-col gap-5">
      <PageHeader
        title={t('Отчёты')}
        actions={PERIODS.map((p) => (
          <button
            key={p}
            type="button"
            aria-pressed={days === p}
            onClick={() => setDays(p)}
            className={clsx(
              'focus-ring h-10 rounded-md px-3.5 text-sm font-semibold transition-colors',
              'choice',
              days === p && 'choice-on',
            )}
          >
            {t('{n} дней', { n: p })}
          </button>
        ))}
      />
      <Note note={note} />

      {data && (
        <>
          <div className="grid grid-cols-2 gap-px overflow-hidden rounded-xl border border-line bg-line xl:grid-cols-4">
            <Stat label={t('Выручка сеансов')} value={sum(data.totals.sessions)} unit={t('сум')} />
            <Stat label={t('Магазин')} value={sum(data.totals.shop)} unit={t('сум')} />
            <Stat label={t('Пополнения')} value={sum(data.totals.topUps)} unit={t('сум')} />
            <Stat label={t('Сеансов')} value={nf.format(data.totals.sessionsCount)} />
          </div>

          <Section title={t('Выручка по дням')}>
            <RevenueChart rows={data.byDay} />
          </Section>

          <Section title={t('Загрузка по часам')}>
            <HeatMap heat={data.heat} />
          </Section>

          <div className="grid grid-cols-1 gap-5 xl:grid-cols-2">
            <Section title={t('Популярные игры')} bodyClassName="p-2">
              <Table
                rows={data.topGames}
                rowKey={(g) => g.id}
                empty={t('Нет данных')}
                columns={[
                  { key: 'title', title: t('Игра'), render: (g) => g.title },
                  { key: 'players', title: t('Игроков'), num: true, width: '8rem', render: (g) => g.players },
                ]}
              />
            </Section>
            <Section title={t('Популярные товары')} bodyClassName="p-2">
              <Table
                rows={data.topProducts}
                rowKey={(p) => p.title}
                empty={t('Нет данных')}
                columns={[
                  { key: 'title', title: t('Товар'), render: (p) => p.title },
                  { key: 'qty', title: t('Кол-во'), num: true, width: '6rem', render: (p) => p.qty },
                  { key: 'amount', title: t('Сумма'), num: true, width: '9rem', render: (p) => uzs(p.amount) },
                ]}
              />
            </Section>
          </div>

          <Section title={t('Смены')} bodyClassName="p-2">
            <Table
              rows={data.shifts}
              rowKey={(s) => s.id}
              empty={t('Смен за период нет')}
              columns={[
                { key: 'who', title: t('Кассир'), render: (s) => s.staffName },
                {
                  key: 'period',
                  title: t('Период'),
                  render: (s) => (
                    <span className="tnum">
                      {dateTime(s.openedAt)} — {s.closedAt ? dateTime(s.closedAt) : t('открыта')}
                    </span>
                  ),
                },
                { key: 'sessions', title: t('Сеансы'), num: true, render: (s) => uzs(s.totals?.sessions) },
                { key: 'shop', title: t('Магазин'), num: true, render: (s) => uzs(s.totals?.shop) },
                { key: 'cash', title: t('Наличные'), num: true, render: (s) => uzs(s.closingCash) },
              ]}
            />
          </Section>
        </>
      )}
    </div>
  );
}
