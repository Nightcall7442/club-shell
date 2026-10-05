/**
 * The owner's network ("Сеть клубов"): every club side by side — seats busy now, revenue today and for the period,
 * sessions, average check, repair tickets, cashier signals, who is on shift, today's load by hour — the network totals,
 * the shared player base (one balance for every club), and adding a club.
 */
import { useCallback, useEffect, useState } from 'react';
import clsx from 'clsx';
import { clubApi, type NetworkClubReport, type NetworkReport } from '@/api';
import { describe } from '@/errors';
import { dateLocale, t } from '@/i18n';
import { Badge, Button, Field, Input, KpiCard, Note, NumberInput, PageHeader, Section } from '@/ui';
import { OwnerPage, PeriodChips } from './ownerKit';

const nf = new Intl.NumberFormat('ru-RU');
const sum = (minor: number): string => nf.format(Math.round(minor / 100));

/**
 * Revenue per day as thin bars, scaled to the busiest club of the network so clubs compare at a glance: past days
 * in `dim` at 40 %, today (the last bar) the one accent bar (spec §9).
 */
function Bars({ values, max }: { values: number[]; max: number }): JSX.Element {
  return (
    <div className="flex h-10 items-end gap-[2px]" aria-hidden="true">
      {values.map((v, i) => (
        <span
          key={i}
          className={clsx(
            'min-w-0 flex-1 rounded-t-[1px]',
            i === values.length - 1 ? 'bg-accent shadow-[0_0_8px_rgb(var(--c-accent)/0.5)]' : 'bg-dim/40',
          )}
          style={{ height: `${Math.max(2, (v / Math.max(1, max)) * 100)}%` }}
        />
      ))}
    </div>
  );
}

/** Today's load by hour, 0–100 %: the hours gone in `dim`, this hour in the accent, the rest a hairline. */
function Hours({ values }: { values: number[] }): JSX.Element {
  const now = new Date().getHours();
  return (
    <div className="flex flex-col gap-1.5">
      <div className="flex h-8 items-end gap-[2px]" aria-hidden="true">
        {values.map((v, h) => (
          <span
            key={h}
            className={clsx(
              'min-w-0 flex-1 rounded-t-[1px]',
              h === now ? 'bg-accent shadow-[0_0_8px_rgb(var(--c-accent)/0.6)]' : h > now ? 'bg-line' : 'bg-dim/40',
            )}
            style={{ height: `${h > now ? 6 : Math.max(4, v)}%` }}
          />
        ))}
      </div>
      <div className="flex justify-between font-mono text-[9.5px] tracking-[0.1em] text-muted">
        <span>00</span>
        <span>12</span>
        <span>23</span>
      </div>
    </div>
  );
}

function ClubCard({ c, maxDay }: { c: NetworkClubReport; maxDay: number }): JSX.Element {
  const load = c.pcs ? Math.round((c.busyNow / c.pcs) * 100) : 0;
  return (
    <article
      className={clsx('glass-panel flex flex-col gap-4 px-5 pb-4 pt-[18px]', c.local && 'border-accent/[0.22]')}
      aria-label={c.name}
    >
      <header className="flex items-start justify-between gap-3">
        <div className="flex min-w-0 flex-col gap-1.5">
          <h2 className="truncate font-display text-[17px] font-medium leading-6 tracking-[-0.01em] text-hi">
            {c.name}
          </h2>
          <p className="truncate text-xs text-dim">
            {c.city}
            {c.address ? ` · ${c.address}` : ''}
          </p>
        </div>
        <span className="flex shrink-0 items-center gap-2 pt-0.5">
          {c.local && <Badge tone="accent">{t('этот сервер')}</Badge>}
          {c.simulated && <Badge tone="muted">{t('демо')}</Badge>}
        </span>
      </header>

      <div className="flex items-baseline justify-between gap-3">
        <span className="flex items-baseline gap-2.5">
          <span className="num-dot leading-none">
            <span className="text-[30px] text-hi">{String(c.busyNow).padStart(2, '0')}</span>
            <span className="text-lg text-muted">/{String(c.pcs).padStart(2, '0')}</span>
          </span>
          <span className="text-[13px] text-dim">{t('играют сейчас')}</span>
        </span>
        <span className="tnum font-mono text-xs text-muted">{load}%</span>
      </div>
      <Hours values={c.hourly} />

      <dl className="grid grid-cols-[minmax(0,1fr)_auto] gap-x-4 gap-y-2 border-t border-accent/[0.07] pt-3 text-[13px]">
        <dt className="text-dim">{t('Выручка сегодня')}</dt>
        <dd className="tnum text-right font-mono text-[12.5px] font-medium text-text">
          {sum(c.revenueToday)} <span className="font-sans text-muted">{t('сум')}</span>
        </dd>
        <dt className="text-dim">{t('Выручка за период')}</dt>
        <dd className="tnum text-right font-mono text-[12.5px] font-medium text-text">
          {sum(c.revenue)} <span className="font-sans text-muted">{t('сум')}</span>
        </dd>
        <dt className="text-dim">{t('Сеансов')}</dt>
        <dd className="tnum text-right font-mono text-[12.5px] font-medium text-text">{nf.format(c.sessions)}</dd>
        <dt className="text-dim">{t('Средний чек')}</dt>
        <dd className="tnum text-right font-mono text-[12.5px] font-medium text-text">
          {sum(c.avgCheck)} <span className="font-sans text-muted">{t('сум')}</span>
        </dd>
      </dl>
      <Bars values={c.byDay} max={maxDay} />

      <footer className="flex flex-wrap items-center gap-x-4 gap-y-1 border-t border-accent/[0.08] pt-3 text-[12.5px]">
        <span className={clsx(c.repairs > 0 ? 'font-medium text-warning' : 'text-muted')}>
          {t('Ремонт: {n}', { n: c.repairs })}
        </span>
        <span className={clsx(c.signals > 0 ? 'font-medium text-warning' : 'text-muted')}>
          {t('Сигналы: {n}', { n: c.signals })}
        </span>
        <span className="ml-auto text-dim">
          {c.shift
            ? t('Смена · {name} с {time}', {
                name: c.shift.staffName,
                time: new Date(c.shift.since).toLocaleTimeString(dateLocale(), { hour: '2-digit', minute: '2-digit' }),
              })
            : t('Смена не открыта')}
        </span>
      </footer>
    </article>
  );
}

function AddClub({ onAdded }: { onAdded: () => void }): JSX.Element {
  const [name, setName] = useState('');
  const [city, setCity] = useState('');
  const [address, setAddress] = useState('');
  const [pcs, setPcs] = useState(20);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<{ text: string; tone: 'ok' | 'err' } | null>(null);
  const add = async (): Promise<void> => {
    setBusy(true);
    try {
      await clubApi.addNetworkClub({ name: name.trim(), city: city.trim(), address: address.trim(), pcs });
      setName('');
      setCity('');
      setAddress('');
      setNote({ text: t('Клуб добавлен — подключите агенты на его ПК'), tone: 'ok' });
      onAdded();
    } catch (e) {
      setNote({ text: describe(e), tone: 'err' });
    } finally {
      setBusy(false);
    }
  };
  return (
    <Section title={t('Добавить клуб')}>
      <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-4">
        <Field label={t('Название')}>
          <Input value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label={t('Город')}>
          <Input value={city} onChange={(e) => setCity(e.target.value)} />
        </Field>
        <Field label={t('Адрес')}>
          <Input value={address} onChange={(e) => setAddress(e.target.value)} />
        </Field>
        <Field label={t('Мест')}>
          <NumberInput value={pcs} min={1} max={1000} onChange={setPcs} />
        </Field>
      </div>
      <div className="flex items-center gap-4">
        <Button variant="primary" disabled={busy || !name.trim() || !city.trim()} onClick={() => void add()}>
          {t('Добавить')}
        </Button>
        <Note note={note} />
      </div>
    </Section>
  );
}

export default function NetworkPage(): JSX.Element {
  const [days, setDays] = useState<number>(7);
  const [data, setData] = useState<NetworkReport | null>(null);
  const [note, setNote] = useState<{ text: string; tone: 'ok' | 'err' } | null>(null);

  const load = useCallback(() => {
    clubApi
      .network(days)
      .then((r) => {
        setData(r);
        setNote(null);
      })
      .catch((e: unknown) => setNote({ text: describe(e), tone: 'err' }));
  }, [days]);

  useEffect(() => {
    load();
    const id = setInterval(load, 60_000);
    return () => clearInterval(id);
  }, [load]);

  const maxDay = Math.max(1, ...(data?.clubs ?? []).flatMap((c) => c.byDay));

  return (
    <OwnerPage>
      <PageHeader
        title={data ? t('Сеть клубов · {name}', { name: data.name }) : t('Сеть клубов')}
        caption={t('Бизнес')}
        actions={<PeriodChips value={days} onChange={setDays} />}
      />
      <Note note={note} />

      {data && (
        <>
          <div className="grid grid-cols-2 gap-3 xl:grid-cols-5">
            <KpiCard compact label={t('Клубов')} value={String(data.totals.clubs)} />
            <KpiCard
              compact
              label={t('Играют сейчас')}
              value={
                <>
                  {data.totals.busyNow}
                  <span className="text-lg text-muted">/{data.totals.pcs}</span>
                </>
              }
            />
            <KpiCard compact label={t('Выручка сегодня')} value={sum(data.totals.revenueToday)} unit={t('сум')} />
            <KpiCard compact label={t('Выручка за период')} value={sum(data.totals.revenue)} unit={t('сум')} />
            <KpiCard compact label={t('Сеансов')} value={nf.format(data.totals.sessions)} />
          </div>

          <div className="grid grid-cols-1 gap-4 lg:grid-cols-2 xl:grid-cols-3">
            {data.clubs.map((c) => (
              <ClubCard key={c.id} c={c} maxDay={maxDay} />
            ))}
          </div>

          <Section title={t('Игроки сети')}>
            <div className="grid grid-cols-1 gap-3 md:grid-cols-3">
              <p className="well flex items-baseline gap-3 px-4 py-3.5 text-[13px] text-dim">
                <span className="num-dot shrink-0 text-[26px] leading-none text-hi">{data.players.total}</span>
                {t('игроков с аккаунтом')}
              </p>
              <p className="well flex items-baseline gap-3 px-4 py-3.5 text-[13px] text-dim">
                <span className="num-dot shrink-0 text-[26px] leading-none text-hi">{data.players.multiClub}</span>
                {t('играли в нескольких клубах')}
              </p>
              <p className="well flex items-baseline gap-3 px-4 py-3.5 text-[13px] text-dim">
                <span className="num-dot shrink-0 text-[26px] leading-none text-hi">{sum(data.players.balance)}</span>
                {t('сум на балансах — один баланс работает в любом клубе сети')}
              </p>
            </div>
          </Section>
        </>
      )}

      <AddClub onAdded={load} />
    </OwnerPage>
  );
}
