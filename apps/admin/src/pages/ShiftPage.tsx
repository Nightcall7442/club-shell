/**
 * Cash shift: open with the drawer float, watch the X-report while the shift runs, close with the counted cash and
 * get the Z-report. Below, the closed shifts. Polls `/admin/shift` every 5 s.
 */
import { useCallback, useEffect, useState } from 'react';
import clsx from 'clsx';
import { clubApi, type Shift, type ShiftTotals } from '@/api';
import { describe } from '@/errors';
import { t } from '@/i18n';
import { money } from '@/format';
import { Button, Field, MoneyInput, Note, PageHeader, Section, Table } from '@/ui';

const POLL_MS = 5000;
const nf = new Intl.NumberFormat('ru-RU');

type NoteState = { text: string; tone: 'ok' | 'err' } | null;

const sum = (minor: number): string => nf.format(Math.round(minor / 100));
const uzs = (minor: number | null | undefined): string =>
  minor === null || minor === undefined ? '—' : money({ amount: minor, currency: 'UZS' });

function dateTime(iso: string | null): string {
  if (!iso) return '—';
  return new Date(iso).toLocaleString('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function time(iso: string): string {
  return new Date(iso).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
}

/** One stat cell: mono label, dot-matrix figure, small unit. */
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
    <div className="flex min-w-0 flex-col justify-between gap-2 bg-surface px-5 py-4">
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
  ];
  return (
    <StatGrid cols="grid-cols-2 md:grid-cols-4 xl:grid-cols-7">
      {cells.map(([label, v]) => (
        <Stat key={label} label={label} value={sum(v)} unit={t('сум')} />
      ))}
      <Stat label={t('Операций')} value={String(x.count)} />
    </StatGrid>
  );
}

function diffTone(d: number): 'ok' | 'err' | undefined {
  if (d === 0) return 'ok';
  return d > 0 ? 'ok' : 'err';
}

function signed(minor: number): string {
  return `${minor > 0 ? '+' : minor < 0 ? '−' : ''}${sum(Math.abs(minor))}`;
}

export default function ShiftPage(): JSX.Element {
  const [shift, setShift] = useState<Shift | null>(null);
  const [x, setX] = useState<ShiftTotals | null>(null);
  const [history, setHistory] = useState<Shift[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [opening, setOpening] = useState(0);
  const [counted, setCounted] = useState(0);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<NoteState>(null);
  const [zReport, setZReport] = useState<{ shift: Shift; expectedCash: number } | null>(null);

  const load = useCallback(async (): Promise<void> => {
    try {
      const r = await clubApi.shift();
      setShift(r.shift);
      setX(r.x);
      setHistory(r.history);
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

  const open = async (): Promise<void> => {
    setBusy(true);
    setNote(null);
    try {
      await clubApi.openShift(opening);
      setZReport(null);
      setNote({ text: t('Смена открыта'), tone: 'ok' });
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
      await load();
    } catch (e) {
      setNote({ text: describe(e), tone: 'err' });
    } finally {
      setBusy(false);
    }
  };

  const expected = shift && x ? shift.openingCash + x.topUpCash : 0;
  const diff = counted - expected;

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
            <Button variant="ghost" size="sm" onClick={() => setZReport(null)}>
              {t('Скрыть')}
            </Button>
          }
        >
          <TotalsGrid x={zReport.shift.totals} />
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
          <Section title={t('X-отчёт')}>
            <TotalsGrid x={x} />
          </Section>

          <Section title={t('Закрыть смену')}>
            <StatGrid cols="grid-cols-1 md:grid-cols-3">
              <Stat label={t('На начало смены')} value={sum(shift.openingCash)} unit={t('сум')} />
              <Stat label={t('Ожидается в кассе')} value={sum(expected)} unit={t('сум')} />
              <Stat label={t('Расхождение')} value={signed(diff)} unit={t('сум')} tone={diffTone(diff)} />
            </StatGrid>
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
            { key: 'sessions', title: t('Сеансы'), num: true, render: (s) => uzs(s.totals?.sessions) },
            { key: 'shop', title: t('Магазин'), num: true, render: (s) => uzs(s.totals?.shop) },
            { key: 'cash', title: t('Наличные в кассе'), num: true, render: (s) => uzs(s.closingCash) },
            {
              key: 'diff',
              title: t('Расхождение'),
              num: true,
              render: (s) => {
                if (s.closingCash === null || !s.totals) return '—';
                const d = s.closingCash - (s.openingCash + s.totals.topUpCash);
                return (
                  <span className={d < 0 ? 'text-danger' : d > 0 ? 'text-success' : 'text-muted'}>
                    {`${signed(d)} ${t('сум')}`}
                  </span>
                );
              },
            },
          ]}
        />
      </Section>
    </div>
  );
}
