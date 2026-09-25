/**
 * PC health ("Состояние ПК"): repair tickets the server opened from telemetry — a PC running hot, heating up more than
 * it did last week, losing frames, dropping off the network — with take-into-work / resolved, and every PC ranked by
 * health with its temperatures against its own usual and the last 24 hours. Owners also tune the thresholds and can let
 * the club take a PC with a serious problem out of service automatically.
 */
import { useCallback, useEffect, useState } from 'react';
import clsx from 'clsx';
import {
  clubApi,
  type HealthIssue,
  type HealthKind,
  type HealthReport,
  type HealthSettings,
  type HealthTicket,
  type PcHealth,
  type TicketStatus,
} from '@/api';
import { describe } from '@/errors';
import { dateLocale, t } from '@/i18n';
import { Button, Field, Note, NumberInput, PageHeader, Section, Table, Toggle } from '@/ui';

const REFRESH_MS = 30_000;

/** Problem in the console language, from the facts the server sends. */
function issueText(kind: HealthKind, p: Record<string, number>): string {
  switch (kind) {
    case 'cpuHot':
      return t('Процессор {temp} °C — перегрев', { temp: p['temp'] ?? 0 });
    case 'gpuHot':
      return t('Видеокарта {temp} °C — перегрев', { temp: p['temp'] ?? 0 });
    case 'cpuTrend':
      return t('Процессор греется на {rise} °C сильнее обычного — пора чистить', { rise: p['rise'] ?? 0 });
    case 'gpuTrend':
      return t('Видеокарта греется на {rise} °C сильнее обычного — проверьте вентиляторы', { rise: p['rise'] ?? 0 });
    case 'fpsDrop':
      return t('FPS упал на {drop}%: было {before}, сейчас {today}', {
        drop: p['drop'] ?? 0,
        before: p['before'] ?? 0,
        today: p['today'] ?? 0,
      });
    case 'unstable':
      return t('Пропадал из сети {drops} раз за сутки', { drops: p['drops'] ?? 0 });
    default:
      return kind;
  }
}

const STATUS_LABEL: Record<TicketStatus, string> = {
  open: 'Новая',
  inWork: 'В работе',
  resolved: 'Решено',
};

function dateTime(iso: string): string {
  return new Date(iso).toLocaleString(dateLocale(), {
    day: '2-digit',
    month: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function Dot({ severity }: { severity: 'high' | 'medium' }): JSX.Element {
  return (
    <span
      aria-hidden="true"
      className={clsx('inline-block h-2 w-2 shrink-0 rounded-full', severity === 'high' ? 'bg-danger' : 'bg-warning')}
    />
  );
}

/** 24 hourly values as a hairline; gaps where there was no data. */
function Spark({ values, warnAt }: { values: (number | null)[]; warnAt: number }): JSX.Element {
  const nums = values.filter((v): v is number => v !== null);
  if (nums.length < 2) return <span className="text-muted">—</span>;
  const min = Math.min(...nums, warnAt - 30);
  const max = Math.max(...nums, warnAt + 2);
  const w = 96;
  const h = 24;
  const x = (i: number): number => (i / (values.length - 1)) * w;
  const y = (v: number): number => h - ((v - min) / (max - min)) * h;
  let d = '';
  values.forEach((v, i) => {
    if (v === null) return;
    d += `${d === '' || values[i - 1] === null ? 'M' : 'L'}${x(i).toFixed(1)} ${y(v).toFixed(1)} `;
  });
  const hot = nums.some((v) => v >= warnAt);
  return (
    <svg viewBox={`0 0 ${w} ${h}`} className="h-6 w-24" aria-hidden="true">
      <line x1="0" x2={w} y1={y(warnAt)} y2={y(warnAt)} className="stroke-danger/40" strokeDasharray="2 3" />
      <path d={d} fill="none" strokeWidth="1.5" className={hot ? 'stroke-danger' : 'stroke-accent'} />
    </svg>
  );
}

function ScoreBar({ score }: { score: number }): JSX.Element {
  const tone = score >= 85 ? 'bg-success' : score >= 60 ? 'bg-warning' : 'bg-danger';
  return (
    <span className="flex items-center gap-2">
      <span className="h-1.5 w-16 overflow-hidden rounded-full bg-line">
        <span className={clsx('block h-full', tone)} style={{ width: `${score}%` }} />
      </span>
      <span className="tnum w-7 text-right">{score}</span>
    </span>
  );
}

function Temp({ now, usual, limit }: { now: number | null; usual: number | null; limit: number }): JSX.Element {
  if (now === null) return <span className="text-muted">—</span>;
  return (
    <span className="tnum">
      <span className={clsx(now >= limit ? 'text-danger' : now >= limit - 5 ? 'text-warning' : '')}>{now}°</span>
      {usual !== null && <span className="text-muted"> / {usual}°</span>}
    </span>
  );
}

function TicketRow({ ticket, onChange }: { ticket: HealthTicket; onChange: () => void }): JSX.Element {
  const [busy, setBusy] = useState(false);
  const move = async (status: TicketStatus): Promise<void> => {
    setBusy(true);
    try {
      await clubApi.updateTicket(ticket.id, status);
      onChange();
    } finally {
      setBusy(false);
    }
  };
  return (
    <li className="flex flex-wrap items-center gap-x-4 gap-y-2 py-3 first:pt-0 last:pb-0">
      <Dot severity={ticket.severity} />
      <span className="num-dot w-14 text-lg leading-none">{ticket.pcName.replace(/^PC-/, '')}</span>
      <div className="min-w-0 flex-1">
        <p className="text-sm">{issueText(ticket.kind, ticket.params)}</p>
        <p className="mt-0.5 text-xs text-muted">
          <span className="tnum">{dateTime(ticket.openedAt)}</span> · {t(STATUS_LABEL[ticket.status])}
          {ticket.autoMaintenance && ` · ${t('выведен в сервис автоматически')}`}
        </p>
      </div>
      <div className="flex gap-2">
        {ticket.status === 'open' && (
          <Button size="sm" disabled={busy} onClick={() => void move('inWork')}>
            {t('Взять в работу')}
          </Button>
        )}
        <Button size="sm" variant="ghost" disabled={busy} onClick={() => void move('resolved')}>
          {t('Решено')}
        </Button>
      </div>
    </li>
  );
}

function Thresholds({ initial, onSaved }: { initial: HealthSettings; onSaved: () => void }): JSX.Element {
  const [s, setS] = useState(initial);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const dirty = JSON.stringify(s) !== JSON.stringify(initial);
  const set = (patch: Partial<HealthSettings>): void => {
    setS((cur) => ({ ...cur, ...patch }));
    setSaved(false);
  };
  const save = async (): Promise<void> => {
    setSaving(true);
    try {
      await clubApi.saveHealthSettings(s);
      setSaved(true);
      onSaved();
    } finally {
      setSaving(false);
    }
  };
  return (
    <Section
      title={t('Пороги')}
      actions={
        dirty ? (
          <Button size="sm" variant="primary" disabled={saving} onClick={() => void save()}>
            {saving ? t('Сохраняем…') : t('Сохранить')}
          </Button>
        ) : saved ? (
          <span role="status" className="text-sm text-success">
            {t('Сохранено')}
          </span>
        ) : undefined
      }
    >
      <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
        <Field label={t('Перегрев процессора, °C')}>
          <NumberInput value={s.cpuHotC} min={50} max={110} onChange={(v) => set({ cpuHotC: v })} />
        </Field>
        <Field label={t('Перегрев видеокарты, °C')}>
          <NumberInput value={s.gpuHotC} min={50} max={110} onChange={(v) => set({ gpuHotC: v })} />
        </Field>
        <Field label={t('Греется сильнее обычного на, °C')}>
          <NumberInput value={s.trendC} min={2} max={40} onChange={(v) => set({ trendC: v })} />
        </Field>
        <Field label={t('Падение FPS, %')}>
          <NumberInput value={s.fpsDropPct} min={5} max={90} onChange={(v) => set({ fpsDropPct: v })} />
        </Field>
        <Field label={t('Пропаданий из сети за сутки')}>
          <NumberInput value={s.offlinePerDay} min={1} max={50} onChange={(v) => set({ offlinePerDay: v })} />
        </Field>
      </div>
      <div className="mt-5">
        <Toggle
          checked={s.autoMaintenance}
          onChange={(v) => set({ autoMaintenance: v })}
          label={t('Выводить ПК в сервис автоматически, когда проблема серьёзная и за ним никого нет')}
        />
      </div>
    </Section>
  );
}

export default function HealthPage({ isOwner }: { isOwner?: boolean }): JSX.Element {
  const [data, setData] = useState<HealthReport | null>(null);
  const [note, setNote] = useState<{ text: string; tone: 'ok' | 'err' } | null>(null);

  const load = useCallback(() => {
    clubApi
      .health()
      .then((r) => {
        setData(r);
        setNote(null);
      })
      .catch((e: unknown) => setNote({ text: describe(e), tone: 'err' }));
  }, []);

  useEffect(() => {
    load();
    const id = setInterval(load, REFRESH_MS);
    return () => clearInterval(id);
  }, [load]);

  const open = (data?.tickets ?? []).filter((x) => x.status !== 'resolved');
  const pcs = [...(data?.pcs ?? [])].sort((a, b) => a.score - b.score || a.name.localeCompare(b.name));
  const settings = data?.settings;

  return (
    <div className="flex flex-col gap-5">
      <PageHeader title={t('Состояние ПК')} />
      <Note note={note} />

      <Section title={open.length ? t('Нужен ремонт · {n}', { n: open.length }) : t('Нужен ремонт')}>
        {open.length === 0 ? (
          <p className="flex items-center gap-2 text-sm text-success">
            <span aria-hidden="true" className="h-2 w-2 rounded-full bg-success" />
            {t('Все ПК в порядке')}
          </p>
        ) : (
          <ul aria-label={t('Заявки на ремонт')} className="flex flex-col divide-y divide-line/60">
            {open.map((x) => (
              <TicketRow key={x.id} ticket={x} onChange={load} />
            ))}
          </ul>
        )}
      </Section>

      <Section title={t('Все ПК')} bodyClassName="p-2">
        <Table<PcHealth>
          rows={pcs}
          rowKey={(p) => p.id}
          empty={t('Нет данных')}
          columns={[
            { key: 'pc', title: t('ПК'), width: '6rem', render: (p) => <span className="font-medium">{p.name}</span> },
            {
              key: 'zone',
              title: t('Зона'),
              width: '7rem',
              render: (p) => <span className="text-muted">{p.zone}</span>,
            },
            { key: 'score', title: t('Здоровье'), width: '8rem', render: (p) => <ScoreBar score={p.score} /> },
            {
              key: 'cpu',
              title: t('CPU сейчас / обычно'),
              render: (p) => <Temp now={p.live.cpu} usual={p.baseline.cpu} limit={settings?.cpuHotC ?? 90} />,
            },
            {
              key: 'cpu24',
              title: t('CPU за сутки'),
              render: (p) => <Spark values={p.hourly.cpu} warnAt={settings?.cpuHotC ?? 90} />,
            },
            {
              key: 'gpu',
              title: t('GPU сейчас / обычно'),
              render: (p) => <Temp now={p.live.gpu} usual={p.baseline.gpu} limit={settings?.gpuHotC ?? 85} />,
            },
            {
              key: 'gpu24',
              title: t('GPU за сутки'),
              render: (p) => <Spark values={p.hourly.gpu} warnAt={settings?.gpuHotC ?? 85} />,
            },
            {
              key: 'issues',
              title: t('Проблемы'),
              render: (p) =>
                p.issues.length === 0 ? (
                  <span className="text-muted">—</span>
                ) : (
                  <ul className="flex flex-col gap-1">
                    {p.issues.map((i: HealthIssue) => (
                      <li key={i.kind} className="flex items-center gap-2">
                        <Dot severity={i.severity} />
                        <span>{issueText(i.kind, i.params)}</span>
                      </li>
                    ))}
                  </ul>
                ),
            },
          ]}
        />
      </Section>

      {isOwner && settings && <Thresholds key={JSON.stringify(settings)} initial={settings} onSaved={load} />}
    </div>
  );
}
