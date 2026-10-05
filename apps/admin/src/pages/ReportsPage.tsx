/**
 * Owner reports for 7 / 30 / 90 days: totals, revenue per day (sessions + shop, stacked, to scale), occupancy by
 * weekday × hour, top games and products, and the shifts of the period.
 */
import { useEffect, useState } from 'react';
import { clubApi, type Reports } from '@/api';
import { describe } from '@/errors';
import { dateLocale, t } from '@/i18n';
import { money } from '@/format';
import { KpiCard, Note, PageHeader, Section, Sum, Table } from '@/ui';
import { OwnerPage, PeriodChips } from './ownerKit';

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
        <div className="flex h-56 w-20 shrink-0 flex-col justify-between text-right font-mono text-[10.5px] text-muted">
          <span className="tnum -translate-y-1/2">{sum(max)}</span>
          <span className="tnum">{sum(max / 2)}</span>
          <span className="tnum translate-y-1/2">0</span>
        </div>
        <div className="relative flex h-56 min-w-0 flex-1 items-end gap-[3px] border-b border-l border-accent/[0.16]">
          <div className="pointer-events-none absolute inset-x-0 top-0 border-t border-dashed border-accent/[0.08]" />
          <div className="pointer-events-none absolute inset-x-0 top-1/2 border-t border-dashed border-accent/[0.08]" />
          {rows.map((r) => (
            <div
              key={r.date}
              title={`${shortDate(r.date)} · ${t('Сеансы')} ${uzs(r.sessions)} · ${t('Магазин')} ${uzs(r.shop)}`}
              className="flex h-full min-w-0 flex-1 flex-col justify-end"
            >
              <div className="rounded-t-[2px] bg-dim/40" style={{ height: `${(r.shop / max) * 100}%` }} />
              <div
                className="bg-accent/85 shadow-[0_0_10px_-2px_rgb(var(--c-accent)/0.5)]"
                style={{ height: `${(r.sessions / max) * 100}%` }}
              />
            </div>
          ))}
        </div>
      </div>
      <div className="flex gap-3">
        <div className="w-20 shrink-0" />
        <div className="flex min-w-0 flex-1 gap-[3px]">
          {rows.map((r, i) => (
            <span
              key={r.date}
              className="tnum min-w-0 flex-1 overflow-visible whitespace-nowrap font-mono text-[10px] text-muted"
            >
              {i % every === 0 ? shortDate(r.date) : ''}
            </span>
          ))}
        </div>
      </div>
      <div className="flex items-center gap-5 pl-[5.75rem] text-xs text-dim">
        <span className="flex items-center gap-2">
          <span className="h-2.5 w-2.5 rounded-[2px] bg-accent shadow-[0_0_6px_rgb(var(--c-accent)/0.6)]" />
          {t('Сеансы')}
        </span>
        <span className="flex items-center gap-2">
          <span className="h-2.5 w-2.5 rounded-[2px] bg-dim/40" />
          {t('Магазин')}
        </span>
        <span className="label-sm ml-auto">{t('сум в день')}</span>
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
                  className="h-7 rounded-[3px] bg-text/[0.035]"
                  style={{
                    backgroundColor:
                      v > 0 ? `rgb(var(--c-accent) / ${(0.12 + (v / max) * 0.88).toFixed(3)})` : undefined,
                  }}
                />
              );
            })}
          </div>
        ))}
        <span />
        {Array.from({ length: 24 }, (_, h) => (
          <span key={h} className="tnum font-mono text-[10px] text-muted">
            {h % 3 === 0 ? String(h).padStart(2, '0') : ''}
          </span>
        ))}
      </div>
      <div className="flex items-center justify-end gap-2 font-mono text-[10.5px] text-muted">
        <span className="tnum">0</span>
        {[0.12, 0.34, 0.56, 0.78, 1].map((o) => (
          <span key={o} className="h-3 w-5 rounded-[2px]" style={{ backgroundColor: `rgb(var(--c-accent) / ${o})` }} />
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
    <OwnerPage>
      <PageHeader title={t('Отчёты')} caption={t('Бизнес')} actions={<PeriodChips value={days} onChange={setDays} />} />
      <Note note={note} />

      {data && (
        <>
          <div className="grid grid-cols-2 gap-3 xl:grid-cols-4">
            <KpiCard compact label={t('Выручка сеансов')} value={sum(data.totals.sessions)} unit={t('сум')} />
            <KpiCard compact label={t('Магазин')} value={sum(data.totals.shop)} unit={t('сум')} />
            <KpiCard compact label={t('Пополнения')} value={sum(data.totals.topUps)} unit={t('сум')} />
            <KpiCard compact label={t('Сеансов')} value={nf.format(data.totals.sessionsCount)} />
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
                  { key: 'title', title: t('Игра'), render: (g) => <span className="font-medium">{g.title}</span> },
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
                  { key: 'title', title: t('Товар'), render: (p) => <span className="font-medium">{p.title}</span> },
                  { key: 'qty', title: t('Кол-во'), num: true, width: '6rem', render: (p) => p.qty },
                  {
                    key: 'amount',
                    title: t('Сумма'),
                    num: true,
                    width: '9rem',
                    render: (p) => <Sum minor={p.amount} />,
                  },
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
                { key: 'who', title: t('Кассир'), render: (s) => <span className="font-medium">{s.staffName}</span> },
                {
                  key: 'period',
                  title: t('Период'),
                  render: (s) => (
                    <span className="tnum text-dim">
                      {dateTime(s.openedAt)} — {s.closedAt ? dateTime(s.closedAt) : t('открыта')}
                    </span>
                  ),
                },
                { key: 'sessions', title: t('Сеансы'), num: true, render: (s) => <Sum minor={s.totals?.sessions} /> },
                { key: 'shop', title: t('Магазин'), num: true, render: (s) => <Sum minor={s.totals?.shop} /> },
                { key: 'cash', title: t('Наличные'), num: true, render: (s) => <Sum minor={s.closingCash} /> },
              ]}
            />
          </Section>
        </>
      )}
    </OwnerPage>
  );
}
