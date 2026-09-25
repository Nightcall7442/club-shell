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
import { Button, Field, Input, Note, NumberInput, PageHeader, Section } from '@/ui';

const PERIODS = [7, 30, 90] as const;
const nf = new Intl.NumberFormat('ru-RU');
const sum = (minor: number): string => nf.format(Math.round(minor / 100));

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

/** Revenue per day as thin bars, scaled to the busiest club of the network so clubs compare at a glance. */
function Bars({ values, max }: { values: number[]; max: number }): JSX.Element {
  return (
    <div className="flex h-10 items-end gap-[2px]" aria-hidden="true">
      {values.map((v, i) => (
        <span
          key={i}
          className="min-w-0 flex-1 bg-accent/70"
          style={{ height: `${Math.max(2, (v / Math.max(1, max)) * 100)}%` }}
        />
      ))}
    </div>
  );
}

/** Today's load by hour, 0–100 %. */
function Hours({ values }: { values: number[] }): JSX.Element {
  const now = new Date().getHours();
  return (
    <div className="flex flex-col gap-1">
      <div className="flex h-8 items-end gap-[2px]" aria-hidden="true">
        {values.map((v, h) => (
          <span
            key={h}
            className={clsx('min-w-0 flex-1', h === now ? 'bg-accent' : h > now ? 'bg-line' : 'bg-text/30')}
            style={{ height: `${h > now ? 6 : Math.max(4, v)}%` }}
          />
        ))}
      </div>
      <div className="flex justify-between font-mono text-[0.6rem] text-muted">
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
    <article className="panel flex flex-col gap-4 p-5" aria-label={c.name}>
      <header className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="truncate font-display text-lg tracking-tight">{c.name}</h2>
          <p className="truncate text-sm text-muted">
            {c.city}
            {c.address ? ` · ${c.address}` : ''}
          </p>
        </div>
        <span className="flex shrink-0 items-center gap-2">
          {c.local && <span className="rounded bg-accent/15 px-2 py-0.5 text-xs text-accent">{t('этот сервер')}</span>}
          {c.simulated && <span className="rounded bg-white/[0.06] px-2 py-0.5 text-xs text-muted">{t('демо')}</span>}
        </span>
      </header>

      <div className="flex items-baseline justify-between gap-3">
        <span className="flex items-baseline gap-2">
          <span className="num-dot text-3xl leading-none">
            <span className="text-accent">{String(c.busyNow).padStart(2, '0')}</span>
            <span className="text-muted">/{String(c.pcs).padStart(2, '0')}</span>
          </span>
          <span className="text-sm text-muted">{t('играют сейчас')}</span>
        </span>
        <span className="tnum text-sm text-muted">{load}%</span>
      </div>
      <Hours values={c.hourly} />

      <dl className="grid grid-cols-2 gap-x-4 gap-y-2 text-sm">
        <dt className="text-muted">{t('Выручка сегодня')}</dt>
        <dd className="tnum text-right">
          {sum(c.revenueToday)} {t('сум')}
        </dd>
        <dt className="text-muted">{t('Выручка за период')}</dt>
        <dd className="tnum text-right">
          {sum(c.revenue)} {t('сум')}
        </dd>
        <dt className="text-muted">{t('Сеансов')}</dt>
        <dd className="tnum text-right">{nf.format(c.sessions)}</dd>
        <dt className="text-muted">{t('Средний чек')}</dt>
        <dd className="tnum text-right">
          {sum(c.avgCheck)} {t('сум')}
        </dd>
      </dl>
      <Bars values={c.byDay} max={maxDay} />

      <footer className="flex flex-wrap items-center gap-x-4 gap-y-1 border-t border-line pt-3 text-sm">
        <span className={clsx(c.repairs > 0 ? 'text-warning' : 'text-muted')}>
          {t('Ремонт: {n}', { n: c.repairs })}
        </span>
        <span className={clsx(c.signals > 0 ? 'text-danger' : 'text-muted')}>
          {t('Сигналы: {n}', { n: c.signals })}
        </span>
        <span className="ml-auto text-muted">
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
      <div className="mt-4 flex items-center gap-4">
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
    <div className="flex flex-col gap-5">
      <PageHeader
        title={data ? t('Сеть клубов · {name}', { name: data.name }) : t('Сеть клубов')}
        actions={PERIODS.map((p) => (
          <button
            key={p}
            type="button"
            aria-pressed={days === p}
            onClick={() => setDays(p)}
            className={clsx(
              'focus-ring choice h-10 rounded-md px-3.5 text-sm font-semibold transition-colors',
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
          <div className="grid grid-cols-2 gap-px overflow-hidden rounded-xl border border-line bg-line xl:grid-cols-5">
            <Stat label={t('Клубов')} value={String(data.totals.clubs)} />
            <Stat label={t('Играют сейчас')} value={`${data.totals.busyNow}/${data.totals.pcs}`} />
            <Stat label={t('Выручка сегодня')} value={sum(data.totals.revenueToday)} unit={t('сум')} />
            <Stat label={t('Выручка за период')} value={sum(data.totals.revenue)} unit={t('сум')} />
            <Stat label={t('Сеансов')} value={nf.format(data.totals.sessions)} />
          </div>

          <div className="grid grid-cols-1 gap-5 lg:grid-cols-2 2xl:grid-cols-3">
            {data.clubs.map((c) => (
              <ClubCard key={c.id} c={c} maxDay={maxDay} />
            ))}
          </div>

          <Section title={t('Игроки сети')}>
            <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
              <p className="text-sm">
                <span className="num-dot mr-2 text-2xl">{data.players.total}</span>
                {t('игроков с аккаунтом')}
              </p>
              <p className="text-sm">
                <span className="num-dot mr-2 text-2xl">{data.players.multiClub}</span>
                {t('играли в нескольких клубах')}
              </p>
              <p className="text-sm">
                <span className="num-dot mr-2 text-2xl">{sum(data.players.balance)}</span>
                {t('сум на балансах — один баланс работает в любом клубе сети')}
              </p>
            </div>
          </Section>
        </>
      )}

      <AddClub onAdded={load} />
    </div>
  );
}
