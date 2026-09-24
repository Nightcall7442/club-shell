/**
 * Hall map editor (owner): devices on a square grid at their (x, y), selected device on the right, zones below the
 * add form when nothing is selected. Click a device to select it, click an empty cell or use the arrow keys to move it.
 */
import { useCallback, useEffect, useMemo, useState, type KeyboardEvent } from 'react';
import clsx from 'clsx';
import { clubApi, type DeviceKind, type HallPc, type Zone } from '@/api';
import { describe } from '@/errors';
import { t } from '@/i18n';
import { useClubSettings } from '@/settings';
import { Button, Field, Input, Note, NumberInput, PageHeader, SaveBar, Section, Toggle } from '@/ui';

const MIN_COLS = 16;
const MIN_ROWS = 10;
const HOT_TEMP = 85;

const DEVICES: { id: DeviceKind; label: string }[] = [
  { id: 'pc', label: 'ПК' },
  { id: 'console', label: 'Консоль' },
  { id: 'vr', label: 'VR' },
  { id: 'other', label: 'Другое' },
];

const STATUS_DOT: Record<HallPc['status'], string> = {
  free: 'bg-success',
  busy: 'bg-accent',
  locked: 'bg-danger',
  maintenance: 'bg-fuchsia-400',
  booked: 'bg-amber-300',
  offline: 'bg-muted/40',
};

type NoteState = { text: string; tone: 'ok' | 'err' } | null;

function DeviceGlyph({ kind }: { kind: DeviceKind }): JSX.Element | null {
  if (kind === 'console') {
    return (
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.8} className="h-3 w-3">
        <path d="M6 9h12a3 3 0 0 1 3 3v2a3 3 0 0 1-5.2 2L14 14h-4l-1.8 2A3 3 0 0 1 3 14v-2a3 3 0 0 1 3-3z" />
        <path d="M7.5 11.5v2M6.5 12.5h2" strokeLinecap="round" />
      </svg>
    );
  }
  if (kind === 'vr') {
    return (
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.8} className="h-3 w-3">
        <path d="M3 8h18v8h-6l-2-2h-2l-2 2H3z" strokeLinejoin="round" />
      </svg>
    );
  }
  return null;
}

function Choice<T extends string>({
  options,
  value,
  onChange,
  cols,
}: {
  options: { id: T; label: string }[];
  value: T;
  onChange: (v: T) => void;
  cols: number;
}): JSX.Element {
  return (
    <div className="grid gap-1.5" style={{ gridTemplateColumns: `repeat(${cols}, minmax(0, 1fr))` }}>
      {options.map((o) => (
        <Button key={o.id} size="sm" className={clsx(o.id === value && 'choice-on')} onClick={() => onChange(o.id)}>
          {o.label}
        </Button>
      ))}
    </div>
  );
}

function Stat({ label, value, warn }: { label: string; value: string; warn?: boolean }): JSX.Element {
  return (
    <div className="flex items-center justify-between border-b border-line/60 py-1.5 last:border-b-0">
      <span className="label">{label}</span>
      <span className={clsx('tnum font-mono text-sm', warn ? 'text-danger' : 'text-text')}>{value}</span>
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Selected device
// ---------------------------------------------------------------------------------------------------------------------

function DevicePanel({
  pc,
  zones,
  onChanged,
  onDeleted,
}: {
  pc: HallPc;
  zones: Zone[];
  onChanged: () => Promise<void>;
  onDeleted: () => void;
}): JSX.Element {
  const [name, setName] = useState(pc.name);
  const [number, setNumber] = useState(pc.number);
  const [zone, setZone] = useState(pc.zone);
  const [device, setDevice] = useState<DeviceKind>(pc.device);
  const [busy, setBusy] = useState(false);
  const [confirm, setConfirm] = useState(false);
  const [note, setNote] = useState<NoteState>(null);

  useEffect(() => {
    setName(pc.name);
    setNumber(pc.number);
    setZone(pc.zone);
    setDevice(pc.device);
    setConfirm(false);
    setNote(null);
  }, [pc.id, pc.name, pc.number, pc.zone, pc.device]);

  const run = async (fn: () => Promise<unknown>, ok: string | null): Promise<boolean> => {
    setBusy(true);
    setNote(null);
    try {
      await fn();
      if (ok) setNote({ text: ok, tone: 'ok' });
      return true;
    } catch (e) {
      setNote({ text: describe(e), tone: 'err' });
      return false;
    } finally {
      setBusy(false);
    }
  };

  const changed = name !== pc.name || number !== pc.number || zone !== pc.zone || device !== pc.device;
  const zoneOptions = zones.some((z) => z.name === pc.zone) ? zones : [...zones, { name: pc.zone, color: '' }];
  const m = pc.metrics;
  const cpuT = m?.temps.cpu ?? null;
  const gpuT = m?.temps.gpu ?? null;
  const hot = (cpuT ?? 0) > HOT_TEMP || (gpuT ?? 0) > HOT_TEMP;

  return (
    <Section title={pc.name}>
      <Field label={t('Название')}>
        <Input value={name} maxLength={32} onChange={(e) => setName(e.target.value)} />
      </Field>
      <Field label={t('Номер')}>
        <NumberInput value={number} min={1} max={9999} onChange={setNumber} />
      </Field>
      <Field label={t('Зона')}>
        <Choice
          cols={3}
          value={zone}
          onChange={setZone}
          options={zoneOptions.map((z) => ({ id: z.name, label: z.name }))}
        />
      </Field>
      <Field label={t('Тип устройства')}>
        <Choice
          cols={4}
          value={device}
          onChange={setDevice}
          options={DEVICES.map((d) => ({ ...d, label: t(d.label) }))}
        />
      </Field>
      <Toggle
        label={t('Обслуживание')}
        checked={pc.status === 'maintenance'}
        disabled={busy || pc.status === 'busy'}
        onChange={(v) =>
          void run(async () => {
            await clubApi.updatePc(pc.id, { maintenance: v });
            await onChanged();
          }, null)
        }
      />

      {m && (
        <div className="flex flex-col rounded-md border border-line bg-bg px-3 py-1.5">
          <Stat label={t('CPU')} value={`${Math.round(m.cpuPct)} %`} />
          <Stat label={t('GPU')} value={`${Math.round(m.gpuPct)} %`} />
          {cpuT !== null && <Stat label={t('Темп. CPU')} value={`${Math.round(cpuT)} °C`} warn={cpuT > HOT_TEMP} />}
          {gpuT !== null && <Stat label={t('Темп. GPU')} value={`${Math.round(gpuT)} °C`} warn={gpuT > HOT_TEMP} />}
          {m.fps != null && <Stat label={t('FPS')} value={String(Math.round(m.fps))} />}
          {hot && <span className="py-1.5 text-xs text-danger">{t('Перегрев')}</span>}
        </div>
      )}

      <Note note={note} />

      <div className="flex items-center justify-between gap-2 border-t border-line pt-4">
        {confirm ? (
          <div className="flex items-center gap-1.5">
            <Button
              variant="danger"
              size="sm"
              disabled={busy}
              onClick={() =>
                void run(async () => {
                  await clubApi.deletePc(pc.id);
                  onDeleted();
                }, null).then((ok) => !ok && setConfirm(false))
              }
            >
              {t('Удалить {name}?', { name: pc.name })}
            </Button>
            <Button variant="ghost" size="sm" disabled={busy} onClick={() => setConfirm(false)}>
              {t('Нет')}
            </Button>
          </div>
        ) : (
          <Button variant="danger" size="sm" disabled={busy} onClick={() => setConfirm(true)}>
            {t('Удалить')}
          </Button>
        )}
        <Button
          variant="primary"
          disabled={busy || !changed || name.trim().length === 0 || number < 1}
          onClick={() =>
            void run(async () => {
              await clubApi.updatePc(pc.id, { name: name.trim(), number, zone, device });
              await onChanged();
            }, t('Сохранено'))
          }
        >
          {t('Сохранить')}
        </Button>
      </div>
    </Section>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Nothing selected: add a device, manage zones
// ---------------------------------------------------------------------------------------------------------------------

function AddDevice({
  zones,
  pcs,
  freeCell,
  onAdded,
}: {
  zones: Zone[];
  pcs: HallPc[];
  freeCell: { x: number; y: number } | null;
  onAdded: (id: string | null) => Promise<void>;
}): JSX.Element {
  const nextNumber = pcs.reduce((n, p) => Math.max(n, p.number), 0) + 1;
  const [zone, setZone] = useState(zones[0]?.name ?? '');
  const [number, setNumber] = useState(nextNumber);
  const [device, setDevice] = useState<DeviceKind>('pc');
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<NoteState>(null);

  useEffect(() => setNumber(nextNumber), [nextNumber]);
  useEffect(() => {
    if (!zones.some((z) => z.name === zone)) setZone(zones[0]?.name ?? '');
  }, [zones, zone]);

  const add = async (): Promise<void> => {
    setBusy(true);
    setNote(null);
    try {
      const r = (await clubApi.addPc({ zone, number, device, ...(freeCell ?? {}) })) as { pc?: { id: string } };
      await onAdded(r.pc?.id ?? null);
    } catch (e) {
      setNote({ text: describe(e), tone: 'err' });
    } finally {
      setBusy(false);
    }
  };

  return (
    <Section title={t('Добавить устройство')}>
      <Field label={t('Зона')}>
        <Choice cols={3} value={zone} onChange={setZone} options={zones.map((z) => ({ id: z.name, label: z.name }))} />
      </Field>
      <Field label={t('Номер')}>
        <NumberInput value={number} min={1} max={9999} onChange={setNumber} />
      </Field>
      <Field label={t('Тип устройства')}>
        <Choice
          cols={4}
          value={device}
          onChange={setDevice}
          options={DEVICES.map((d) => ({ ...d, label: t(d.label) }))}
        />
      </Field>
      <Note note={note} />
      <Button
        variant="primary"
        className="self-end"
        disabled={busy || !zone || number < 1 || freeCell === null}
        onClick={() => void add()}
      >
        {t('Добавить')}
      </Button>
    </Section>
  );
}

function ZonesEditor({ onSaved }: { onSaved: () => Promise<void> }): JSX.Element {
  const s = useClubSettings();
  const zones = s.draft?.zones ?? [];
  const setZones = (next: Zone[]): void => s.set('zones', next);
  const names = zones.map((z) => z.name.trim());
  const invalid = names.some((n) => n.length === 0) || new Set(names).size !== names.length;

  return (
    <>
      <Section
        title={t('Зоны')}
        actions={
          <Button
            size="sm"
            onClick={() => setZones([...zones, { name: t('Зона {n}', { n: zones.length + 1 }), color: '#7D8A96' }])}
          >
            {t('Добавить')}
          </Button>
        }
      >
        {s.error && <Note note={{ text: s.error, tone: 'err' }} />}
        <ul className="flex flex-col gap-2">
          {zones.map((z, i) => (
            <li key={i} className="flex items-center gap-2">
              <input
                type="color"
                aria-label={t('Цвет')}
                value={z.color}
                onChange={(e) => setZones(zones.map((x, j) => (j === i ? { ...x, color: e.target.value } : x)))}
                className="focus-ring h-10 w-10 shrink-0 cursor-pointer rounded-md border border-line bg-bg p-1"
              />
              <Input
                value={z.name}
                maxLength={32}
                onChange={(e) => setZones(zones.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))}
              />
              <Button
                variant="danger"
                size="sm"
                aria-label={t('Удалить')}
                onClick={() => setZones(zones.filter((_, j) => j !== i))}
              >
                {t('Удалить')}
              </Button>
            </li>
          ))}
        </ul>
      </Section>
      <SaveBar
        dirty={s.dirty}
        saving={s.saving}
        label={invalid ? t('Названия зон должны быть заполнены и не повторяться') : t('Зоны изменены')}
        onReset={s.reset}
        onSave={() => {
          if (invalid) return;
          void s.save().then(async (ok) => {
            if (ok) await onSaved();
          });
        }}
      />
    </>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------------------------------------------------

export default function HallPage(): JSX.Element {
  const [pcs, setPcs] = useState<HallPc[]>([]);
  const [zones, setZones] = useState<Zone[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const r = await clubApi.pcs();
      setPcs(r.items);
      setZones(r.zones);
      setError(null);
    } catch (e) {
      setError(describe(e));
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const cols = Math.max(MIN_COLS, ...pcs.map((p) => p.x + 2));
  const rows = Math.max(MIN_ROWS, ...pcs.map((p) => p.y + 2));
  const at = useMemo(() => {
    const m = new Map<string, HallPc>();
    for (const p of pcs) m.set(`${p.x}:${p.y}`, p);
    return m;
  }, [pcs]);
  const colorOf = useMemo(() => new Map(zones.map((z) => [z.name, z.color])), [zones]);
  const pc = pcs.find((p) => p.id === selected) ?? null;

  const freeCell = useMemo(() => {
    for (let y = 0; y < rows; y++) for (let x = 0; x < cols; x++) if (!at.has(`${x}:${y}`)) return { x, y };
    return null;
  }, [at, cols, rows]);

  const move = async (target: HallPc, x: number, y: number): Promise<void> => {
    if (x < 0 || y < 0 || x >= cols || y >= rows || at.has(`${x}:${y}`)) return;
    setPcs((list) => list.map((p) => (p.id === target.id ? { ...p, x, y } : p)));
    try {
      await clubApi.updatePc(target.id, { x, y });
      setError(null);
    } catch (e) {
      setError(describe(e));
      await load();
    }
  };

  const onKey = (e: KeyboardEvent<HTMLDivElement>): void => {
    if (!pc) return;
    const d: Record<string, [number, number]> = {
      ArrowLeft: [-1, 0],
      ArrowRight: [1, 0],
      ArrowUp: [0, -1],
      ArrowDown: [0, 1],
    };
    if (e.key === 'Escape') {
      setSelected(null);
      return;
    }
    const step = d[e.key];
    if (!step) return;
    e.preventDefault();
    void move(pc, pc.x + step[0], pc.y + step[1]);
  };

  const cells: JSX.Element[] = [];
  for (let y = 0; y < rows; y++) {
    for (let x = 0; x < cols; x++) {
      const p = at.get(`${x}:${y}`);
      const on = p !== undefined && p.id === selected;
      cells.push(
        <div key={`${x}:${y}`} className="relative aspect-square border-b border-r border-line/50">
          {p ? (
            <button
              type="button"
              tabIndex={-1}
              onClick={() => setSelected(on ? null : p.id)}
              aria-pressed={on}
              title={`${p.name} · ${p.zone}`}
              style={{ borderColor: colorOf.get(p.zone) ?? undefined }}
              className={clsx(
                'absolute inset-[3px] flex items-center justify-center rounded-md border-2 border-line bg-bg transition-colors hover:bg-white/[0.04]',
                on && 'ring-2 ring-accent ring-offset-2 ring-offset-surface',
                p.status === 'offline' && 'text-muted/60',
              )}
            >
              <span className="num-dot text-lg leading-none">{String(p.number).padStart(2, '0')}</span>
              <span className={clsx('absolute right-1 top-1 h-1.5 w-1.5 rounded-full', STATUS_DOT[p.status])} />
              {p.device !== 'pc' && (
                <span className="absolute bottom-0.5 left-1 text-muted">
                  <DeviceGlyph kind={p.device} />
                </span>
              )}
            </button>
          ) : (
            <button
              type="button"
              tabIndex={-1}
              aria-label={`${x + 1}, ${y + 1}`}
              onClick={() => (pc ? void move(pc, x, y) : undefined)}
              className={clsx('absolute inset-0', pc ? 'hover:bg-accent/[0.06]' : 'cursor-default')}
            />
          )}
        </div>,
      );
    }
  }

  return (
    <div className="flex flex-col gap-5">
      <PageHeader title={t('Зал')} />
      {error && <Note note={{ text: error, tone: 'err' }} />}
      <div className="grid grid-cols-1 items-start gap-5 lg:grid-cols-[minmax(0,1fr)_24rem]">
        <div
          tabIndex={0}
          onKeyDown={onKey}
          aria-label={t('Схема зала')}
          className="focus-ring panel overflow-x-auto p-5 outline-none"
        >
          <div
            className="grid border-l border-t border-line/50"
            style={{
              gridTemplateColumns: `repeat(${cols}, minmax(2.75rem, 1fr))`,
            }}
          >
            {cells}
          </div>
          <ul className="mt-4 flex flex-wrap gap-x-5 gap-y-2">
            {zones.map((z) => (
              <li key={z.name} className="flex items-center gap-2 text-sm">
                <span className="h-3 w-3 rounded-[3px] border-2" style={{ borderColor: z.color }} />
                {z.name}
                <span className="tnum font-mono text-xs text-muted">{pcs.filter((p) => p.zone === z.name).length}</span>
              </li>
            ))}
          </ul>
        </div>

        <aside className="flex flex-col gap-5">
          {pc ? (
            <DevicePanel
              pc={pc}
              zones={zones}
              onChanged={load}
              onDeleted={() => {
                setSelected(null);
                void load();
              }}
            />
          ) : (
            <>
              <AddDevice
                zones={zones}
                pcs={pcs}
                freeCell={freeCell}
                onAdded={async (id) => {
                  await load();
                  if (id) setSelected(id);
                }}
              />
              <ZonesEditor onSaved={load} />
            </>
          )}
        </aside>
      </div>
    </div>
  );
}
