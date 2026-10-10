/**
 * Hall map editor (owner): devices on a square grid at their (x, y), selected device on the right (with its live load,
 * games disk and hardware as its Agent reports them), zones below the add form when nothing is selected. Click a device to select it, click an empty cell or use the arrow keys to move it.
 */
import { useCallback, useEffect, useMemo, useState, type KeyboardEvent } from 'react';
import clsx from 'clsx';
import { clubApi, type DeviceKind, type HallPc, type Zone } from '@/api';
import { GamesDisk, HardwareList, gamesDisk } from '@/device';
import { describe } from '@/errors';
import { t } from '@/i18n';
import { WrenchIcon } from '@/icons';
import { useClubSettings } from '@/settings';
import {
  Badge,
  Button,
  Chip,
  Field,
  Input,
  Kbd,
  Note,
  NumberInput,
  PageHeader,
  SaveBar,
  Section,
  Segmented,
  Toggle,
  Well,
} from '@/ui';
import { FieldGroup, OwnerPage } from './ownerKit';

const MIN_COLS = 16;
const MIN_ROWS = 10;
const HOT_TEMP = 85;

const DEVICES: { id: DeviceKind; label: string }[] = [
  { id: 'pc', label: 'ПК' },
  { id: 'console', label: 'Консоль' },
  { id: 'vr', label: 'VR' },
  { id: 'other', label: 'Другое' },
];

/**
 * The status mark in a device's corner, in F's colours: the faint accent of a free tick for "on and free", the lit
 * accent for a session, red for a blocked PC, the accent ring for a booking; maintenance is a muted wrench (below),
 * offline a faint grey dot. (Green and amber mean "online" and "act now" elsewhere, so the editor uses neither.)
 */
const STATUS_DOT: Record<HallPc['status'], string> = {
  free: 'bg-accent/[0.22]',
  busy: 'bg-accent shadow-[0_0_6px_rgb(var(--c-accent)/0.8)]',
  locked: 'bg-danger',
  maintenance: '',
  booked: 'border border-accent',
  offline: 'bg-text/[0.18]',
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

/** One zone of several: outlined chips that wrap (a club may have more zones than a segmented tray holds). */
function ZonePick({
  zones,
  value,
  onChange,
}: {
  zones: { name: string }[];
  value: string;
  onChange: (zone: string) => void;
}): JSX.Element {
  return (
    <div className="flex flex-wrap gap-1.5">
      {zones.map((z) => (
        <Chip key={z.name} tone="outlined" pressed={z.name === value} onClick={() => onChange(z.name)}>
          {z.name}
        </Chip>
      ))}
    </div>
  );
}

/** The device type: ПК / Консоль / VR / Другое as one segmented tray. */
function DevicePick({ value, onChange }: { value: DeviceKind; onChange: (d: DeviceKind) => void }): JSX.Element {
  return (
    <Segmented value={value} onChange={onChange} options={DEVICES.map((d) => ({ id: d.id, label: t(d.label) }))} />
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
  // A PC approved before any zone existed has zone "": not a choice, just nothing chosen yet.
  const zoneOptions =
    pc.zone === '' || zones.some((z) => z.name === pc.zone) ? zones : [...zones, { name: pc.zone, color: '' }];
  const m = pc.metrics;
  const cpuT = m?.temps.cpu ?? null;
  const gpuT = m?.temps.gpu ?? null;
  const hot = (cpuT ?? 0) > HOT_TEMP || (gpuT ?? 0) > HOT_TEMP;

  return (
    <Section variant="solid" title={pc.name}>
      <div className="grid grid-cols-[minmax(0,1fr)_7rem] gap-4">
        <Field label={t('Название')}>
          <Input value={name} maxLength={32} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label={t('Номер')}>
          <NumberInput value={number} min={1} max={9999} onChange={setNumber} />
        </Field>
      </div>
      <FieldGroup label={t('Зона')}>
        {zoneOptions.length > 0 ? (
          <ZonePick zones={zoneOptions} value={zone} onChange={setZone} />
        ) : (
          <p className="text-[13px] text-muted">{t('Зон пока нет: добавьте зону в блоке «Зоны» ниже и сохраните.')}</p>
        )}
      </FieldGroup>
      <FieldGroup label={t('Тип устройства')}>
        <DevicePick value={device} onChange={setDevice} />
      </FieldGroup>
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
        <div className="flex flex-col gap-2">
          <div className="grid grid-cols-2 gap-2">
            <Well label={t('CPU')} value={Math.round(m.cpuPct)} unit="%" />
            <Well label={t('GPU')} value={Math.round(m.gpuPct)} unit="%" />
            {cpuT !== null && (
              <Well
                label={t('Темп. CPU')}
                value={Math.round(cpuT)}
                unit="°C"
                tone={cpuT > HOT_TEMP ? 'warn' : undefined}
              />
            )}
            {gpuT !== null && (
              <Well
                label={t('Темп. GPU')}
                value={Math.round(gpuT)}
                unit="°C"
                tone={gpuT > HOT_TEMP ? 'warn' : undefined}
              />
            )}
            {m.fps != null && <Well label={t('FPS')} value={Math.round(m.fps)} />}
          </div>
          {hot && (
            <Badge tone="warn" className="self-start">
              {t('Перегрев')}
            </Badge>
          )}
        </div>
      )}

      {gamesDisk(pc.gamesVolume, pc.status) && (
        <FieldGroup label={t('Игровой диск')}>
          <GamesDisk volume={pc.gamesVolume} status={pc.status} />
        </FieldGroup>
      )}

      {/* Only a PC runs an Agent; a console or a VR seat has no inventory to wait for. */}
      {(pc.hardware || pc.device === 'pc') && (
        <FieldGroup label={t('Железо')}>
          {pc.hardware ? (
            <HardwareList hardware={pc.hardware} />
          ) : (
            <p className="text-[13px] text-muted">{t('Нет данных — агент на этом ПК ещё не подключался')}</p>
          )}
        </FieldGroup>
      )}

      <Note note={note} />

      <div className="flex items-center justify-between gap-2 border-t border-accent/[0.08] pt-4">
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
          <Button variant="tertiary" size="sm" disabled={busy} onClick={() => setConfirm(true)}>
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
      <FieldGroup label={t('Зона')}>
        <ZonePick zones={zones} value={zone} onChange={setZone} />
      </FieldGroup>
      <Field label={t('Номер')}>
        <NumberInput value={number} min={1} max={9999} onChange={setNumber} />
      </Field>
      <FieldGroup label={t('Тип устройства')}>
        <DevicePick value={device} onChange={setDevice} />
      </FieldGroup>
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
  // The zone whose «Удалить» was pressed: its row asks once (danger «Удалить», «Отмена») before the draft drops it.
  const [asking, setAsking] = useState<number | null>(null);

  return (
    <>
      <Section
        variant="side"
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
                className="focus-ring h-11 w-11 shrink-0 cursor-pointer rounded-md border border-accent/[0.16] bg-bg/50 p-1 hover:border-accent/[0.26]"
              />
              {asking === i ? (
                <>
                  <span className="min-w-0 flex-1 truncate text-sm text-muted">
                    {t('Удалить {name}?', { name: z.name })}
                  </span>
                  <Button
                    variant="danger"
                    onClick={() => {
                      setZones(zones.filter((_, j) => j !== i));
                      setAsking(null);
                    }}
                  >
                    {t('Удалить')}
                  </Button>
                  <Button variant="ghost" onClick={() => setAsking(null)}>
                    {t('Отмена')}
                  </Button>
                </>
              ) : (
                <>
                  <Input
                    value={z.name}
                    maxLength={32}
                    onChange={(e) => setZones(zones.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))}
                  />
                  <Button variant="tertiary" aria-label={t('Удалить')} onClick={() => setAsking(i)}>
                    {t('Удалить')}
                  </Button>
                </>
              )}
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
        <div key={`${x}:${y}`} className="relative aspect-square border-b border-r border-accent/[0.06]">
          {p ? (
            <button
              type="button"
              tabIndex={-1}
              onClick={() => setSelected(on ? null : p.id)}
              aria-pressed={on}
              title={`${p.name} · ${p.zone}`}
              data-selected={on}
              // F's hairline frames the device; the selection adds the target brackets. The zone's own colour (the
              // owner's data) is only the small bar inside its left edge, never a border.
              className={clsx(
                'hud-focus absolute inset-[3px] flex items-center justify-center rounded-[7px] border bg-bg/70 font-display text-[15px] font-medium leading-none tracking-[-0.01em] transition-colors [--brk-inset:-5px] [--brk-size:10px] hover:bg-text/[0.05]',
                on ? 'z-10 border-accent/80 bg-accent/[0.08] text-hi shadow-sel' : 'border-accent/[0.16]',
                p.status === 'offline' ? 'text-text/[0.34]' : !on && 'text-text/[0.88]',
              )}
            >
              {colorOf.get(p.zone) && (
                <span
                  aria-hidden="true"
                  className="absolute bottom-3 left-0 top-3 w-[3px] rounded-r-[2px] opacity-70"
                  style={{ background: colorOf.get(p.zone) }}
                />
              )}
              <span className="tnum">{String(p.number).padStart(2, '0')}</span>
              {p.status === 'maintenance' ? (
                <WrenchIcon size={10} strokeWidth={2} className="absolute right-1 top-1 text-muted" />
              ) : (
                <span className={clsx('absolute right-1 top-1 h-1.5 w-1.5 rounded-full', STATUS_DOT[p.status])} />
              )}
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
    <OwnerPage>
      <PageHeader title={t('Зал и устройства')} caption={t('Настройка клуба')} />
      {error && <Note note={{ text: error, tone: 'err' }} />}
      <div className="grid grid-cols-1 items-start gap-5 lg:grid-cols-[minmax(0,1fr)_400px]">
        <div
          tabIndex={0}
          onKeyDown={onKey}
          aria-label={t('Схема зала')}
          className="focus-ring glass-panel flex min-w-0 flex-col gap-4 px-5 pb-5 pt-[18px] outline-none"
        >
          {/* A mono section title like the panels beside it (every page under a PageHeader heads its panels so). */}
          <header className="flex min-h-7 flex-wrap items-center justify-between gap-x-3 gap-y-1">
            <h2 className="label text-text">{t('Схема зала')}</h2>
            {pc ? (
              <span className="flex items-center gap-2">
                <Kbd>← ↑ ↓ →</Kbd>
                <span className="label-sm">{t('сдвинуть')}</span>
                <Kbd className="ml-2">Esc</Kbd>
                <span className="label-sm">{t('снять выбор')}</span>
              </span>
            ) : (
              <span className="label-sm">{t('Нажмите устройство, чтобы изменить его')}</span>
            )}
          </header>
          {/* Padded so the selection's brackets, just outside the device, are not clipped by the scroller. */}
          <div className="thin-scrollbar -m-1.5 overflow-x-auto p-1.5">
            <div
              className="grid border-l border-t border-accent/[0.06]"
              style={{
                gridTemplateColumns: `repeat(${cols}, minmax(2.75rem, 1fr))`,
              }}
            >
              {cells}
            </div>
          </div>
          <ul className="flex flex-wrap gap-x-6 gap-y-2">
            {zones.map((z) => (
              <li key={z.name} className="flex items-center gap-2">
                <span className="h-2 w-2 rounded-[2px]" style={{ background: z.color }} />
                <span className="font-mono text-[10.5px] font-semibold uppercase tracking-[0.2em] text-text">
                  {z.name}
                </span>
                <span className="tnum font-mono text-[10px] tracking-[0.1em] text-muted">
                  {pcs.filter((p) => p.zone === z.name).length}
                </span>
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
            <AddDevice
              zones={zones}
              pcs={pcs}
              freeCell={freeCell}
              onAdded={async (id) => {
                await load();
                if (id) setSelected(id);
              }}
            />
          )}
          {/* Always here: a PC can only be put in a zone that exists, so the zones must be reachable from its panel too. */}
          <ZonesEditor onSaved={load} />
        </aside>
      </div>
    </OwnerPage>
  );
}
