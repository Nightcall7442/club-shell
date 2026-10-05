/**
 * PC health ("Состояние ПК"): repair tickets the server opened from telemetry — a PC running hot, heating up more than
 * it did last week, losing frames, dropping off the network — with take-into-work / resolved, and every PC ranked by
 * health with its temperatures against its own usual and the last 24 hours. Owners also tune the thresholds and can let
 * the club take a PC with a serious problem out of service automatically.
 *
 * Variant F: no red here. A serious problem, a temperature at its limit, a low health score and the part of a day's
 * curve above the limit are amber; the rest is the accent (sparklines, the score's tick scale) or muted.
 */
import { useCallback, useEffect, useId, useState, type CSSProperties } from 'react';
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
import { CheckIcon } from '@/icons';
import { Badge, Button, Field, Note, NumberInput, PageHeader, Section, StatusDot, Table, Toggle } from '@/ui';

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

/** Severity of a problem: amber when it is serious (act now), muted otherwise; never red. */
function Dot({ severity }: { severity: 'high' | 'medium' }): JSX.Element {
  return <StatusDot tone={severity === 'high' ? 'warn' : 'muted'} />;
}

/**
 * 24 hourly values as an accent hairline over the dashed limit; the part above the limit is amber. Gaps where there was
 * no data.
 */
function Spark({ values, warnAt }: { values: (number | null)[]; warnAt: number }): JSX.Element {
  // React's ids carry colons; an SVG `url(#…)` reference is safer without them.
  const clip = `spark-${useId().replace(/:/g, '')}`;
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
  const limit = y(warnAt);
  const hot = nums.some((v) => v >= warnAt);
  return (
    <svg viewBox={`0 0 ${w} ${h}`} className="h-6 w-24 overflow-visible" aria-hidden="true">
      <line x1="0" x2={w} y1={limit} y2={limit} className="stroke-warning/40" strokeDasharray="2 3" />
      <path d={d} fill="none" strokeWidth="1.5" strokeLinejoin="round" className="stroke-accent" />
      {hot && (
        <>
          <clipPath id={clip}>
            <rect x="0" y={-2} width={w} height={limit + 2} />
          </clipPath>
          <path
            d={d}
            fill="none"
            strokeWidth="1.5"
            strokeLinejoin="round"
            clipPath={`url(#${clip})`}
            className="stroke-warning"
          />
        </>
      )}
    </svg>
  );
}

/** Health 0–100 as the instrument tick scale (accent; amber below 60) and the figure. */
function ScoreBar({ score }: { score: number }): JSX.Element {
  return (
    <span className="flex items-center gap-2.5">
      <span
        aria-hidden="true"
        className={clsx('tick-scale w-16 shrink-0', score < 60 ? 'text-warning' : 'text-accent')}
        style={{ '--value': Math.max(0, Math.min(100, score)) / 100 } as CSSProperties}
      />
      <span className={clsx('tnum w-7 text-right font-mono text-[12.5px]', score < 60 && 'font-semibold text-warning')}>
        {score}
      </span>
    </span>
  );
}

/** Now against the PC's own usual: amber at the limit, a fainter amber within 5 °C of it. */
function Temp({ now, usual, limit }: { now: number | null; usual: number | null; limit: number }): JSX.Element {
  if (now === null) return <span className="text-muted">—</span>;
  return (
    <span className="tnum font-mono text-[12.5px]">
      <span
        className={clsx(
          now >= limit ? 'font-semibold text-warning' : now >= limit - 5 ? 'text-warning/75' : 'text-text',
        )}
      >
        {now}°
      </span>
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
    <li className="flex flex-wrap items-center gap-x-4 gap-y-2 border-t border-accent/[0.07] py-3 first:border-t-0 first:pt-1 last:pb-0">
      <Dot severity={ticket.severity} />
      {/* The PC's number as on its tile. */}
      <span className="tnum w-10 shrink-0 font-display text-xl font-medium leading-none tracking-[-0.01em] text-hi">
        {ticket.pcName.replace(/^PC-/, '')}
      </span>
      <div className="min-w-0 flex-1">
        <p className="text-[13px] font-medium leading-5 text-text">{issueText(ticket.kind, ticket.params)}</p>
        <p className="mt-1 flex flex-wrap items-center gap-x-2.5 gap-y-1 text-xs text-muted">
          <span className="tnum font-mono text-[11px]">{dateTime(ticket.openedAt)}</span>
          <Badge tone={ticket.status === 'inWork' ? 'accent' : 'muted'}>{t(STATUS_LABEL[ticket.status])}</Badge>
          {ticket.autoMaintenance && <span>{t('выведен в сервис автоматически')}</span>}
        </p>
      </div>
      <div className="flex gap-2">
        {ticket.status === 'open' && (
          <Button size="sm" disabled={busy} onClick={() => void move('inWork')}>
            {t('Взять в работу')}
          </Button>
        )}
        <Button size="sm" variant="tertiary" disabled={busy} onClick={() => void move('resolved')}>
          <CheckIcon size={15} />
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
          <span role="status" className="flex items-center gap-2 text-[13px] font-medium text-dim">
            <StatusDot tone="ok" />
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

      <Section
        className="edge-top"
        title={open.length ? t('Нужен ремонт · {n}', { n: open.length }) : t('Нужен ремонт')}
      >
        {open.length === 0 ? (
          <p className="flex items-center gap-2.5 text-[13px] font-medium text-soft">
            <StatusDot tone="ok" />
            {t('Все ПК в порядке')}
          </p>
        ) : (
          <ul aria-label={t('Заявки на ремонт')} className="flex flex-col">
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
            {
              key: 'pc',
              title: t('ПК'),
              width: '6rem',
              render: (p) => <span className="font-medium text-text">{p.name}</span>,
            },
            {
              key: 'zone',
              title: t('Зона'),
              width: '7rem',
              render: (p) => <span className="text-dim">{p.zone}</span>,
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
                      <li key={i.kind} className="flex items-center gap-2.5 leading-5">
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
