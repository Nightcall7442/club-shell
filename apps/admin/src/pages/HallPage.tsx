/**
 * Hall editor (owner, SHELL_CHANGES 94): the hall as it stands — devices at their (x, y) on the plan with where their
 * monitor faces, over the plan's zone areas and objects (walls, bar, entrance, aisles, labels). Drag a device (mouse or
 * finger) to move it; Shift/Ctrl-click or a frame drawn on the empty plan selects several, and dragging one of them moves
 * the group — one save for the whole move (`PATCH /admin/pcs/layout`), refused on a taken cell. «Расставить» lines the
 * selected PCs (or a whole zone) up by number. PCs that still share a cell are flagged to be moved apart. On the right:
 * the selection's place, the selected device (live load, games disk, hardware), the add form, the plan and the zones.
 * Below, the PC policy of the club (`policyEditor.tsx`).
 */
import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent, type PointerEvent } from 'react';
import clsx from 'clsx';
import {
  AdminError,
  clubApi,
  type DeviceKind,
  type HallObjectKind,
  type HallPc,
  type HallPlan,
  type Rotation,
  type Zone,
} from '@/api';
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
import { InstallKit } from './installKit';
import { FieldGroup, OwnerPage } from './ownerKit';
import {
  MAX_CELL,
  OBJECT_KINDS,
  PlanBackdrop,
  RotationMark,
  gridBackground,
  nextRotation,
  objectLabel,
  planSize,
  sharedCells,
} from './hallKit';
import { PolicyEditor } from './policyEditor';

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
      <div className="flex flex-col gap-1">
        <Toggle
          label={t('Без постоянного диска')}
          checked={pc.stateless === true}
          disabled={busy}
          onChange={(v) =>
            void run(async () => {
              await clubApi.updatePc(pc.id, { stateless: v });
              await onChanged();
            }, null)
          }
        />
        <p className="text-[12px] text-muted">
          {t('Бездисковый ПК: C: чистый при каждой загрузке, и ПК регистрируется заново — это не тревога.')}
        </p>
      </div>

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

/** A new seat: the server puts it in the first free cell of the hall (drag it to its place after). */
function AddDevice({
  zones,
  pcs,
  onAdded,
}: {
  zones: Zone[];
  pcs: HallPc[];
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
      const r = (await clubApi.addPc({ zone, number, device })) as { pc?: { id: string } };
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
      <Button variant="primary" className="self-end" disabled={busy || !zone || number < 1} onClick={() => void add()}>
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
// Placement: one move of the selected PCs, checked against the rest of the hall before one save
// ---------------------------------------------------------------------------------------------------------------------

type Target = { id: string; x: number; y: number; rotation?: Rotation };

/**
 * Why `targets` cannot be placed: a cell outside the grid, two of them in one cell, or a cell held by a PC that does not
 * move. null — they can.
 */
function placementProblem(targets: Target[], pcs: HallPc[]): string | null {
  const moving = new Set(targets.map((t) => t.id));
  const held = new Map(pcs.filter((p) => !moving.has(p.id)).map((p) => [`${p.x}:${p.y}`, p]));
  const seen = new Set<string>();
  for (const tg of targets) {
    if (tg.x < 0 || tg.y < 0 || tg.x > MAX_CELL || tg.y > MAX_CELL) return t('За пределами зала');
    const key = `${tg.x}:${tg.y}`;
    const other = held.get(key);
    if (other) return t('Клетка занята: {name}', { name: other.name });
    if (seen.has(key)) return t('Два ПК не могут стоять в одной клетке');
    seen.add(key);
  }
  return null;
}

/** The server's refusal of a placement, naming the PC in the way when it says which. */
function placementError(e: unknown, pcs: HallPc[]): string {
  if (e instanceof AdminError && e.details?.['reason'] === 'cellTaken') {
    const other = pcs.find((p) => p.id === e.details?.['pcId']);
    if (other) return t('Клетка занята: {name}', { name: other.name });
  }
  return describe(e);
}

/**
 * «Расставить»: the PCs by number from (x0, y0), in a row (or a column) of `perLine`, `gap` empty cells between PCs and
 * between lines.
 */
export function arrange(
  pcs: HallPc[],
  from: { x: number; y: number },
  direction: 'row' | 'column',
  perLine: number,
  gap: number,
): Target[] {
  const step = 1 + Math.max(0, gap);
  const line = Math.max(1, perLine);
  return [...pcs]
    .sort((a, b) => a.number - b.number)
    .map((p, i) => {
      const along = (i % line) * step;
      const across = Math.floor(i / line) * step;
      return direction === 'row'
        ? { id: p.id, x: from.x + along, y: from.y + across }
        : { id: p.id, x: from.x + across, y: from.y + along };
    });
}

function RotateButtons({ onRotate, disabled }: { onRotate: (step: 1 | -1) => void; disabled?: boolean }): JSX.Element {
  return (
    <div className="flex gap-1.5">
      <Button
        size="sm"
        variant="ghost"
        disabled={disabled}
        onClick={() => onRotate(-1)}
        aria-label={t('Повернуть влево')}
      >
        ⟲ {t('Повернуть влево')}
      </Button>
      <Button
        size="sm"
        variant="ghost"
        disabled={disabled}
        onClick={() => onRotate(1)}
        aria-label={t('Повернуть вправо')}
      >
        ⟳ {t('Повернуть вправо')}
      </Button>
    </div>
  );
}

/** The selected PCs: their cell (one PC), where they face, «Расставить» for a group or a whole zone. */
function PlacePanel({
  selected,
  zones,
  pcs,
  busy,
  onPlace,
  onSelectZone,
}: {
  selected: HallPc[];
  zones: Zone[];
  pcs: HallPc[];
  busy: boolean;
  onPlace: (targets: Target[]) => Promise<boolean>;
  onSelectZone: (zone: string) => void;
}): JSX.Element {
  const one = selected.length === 1 ? selected[0] : undefined;
  const minX = Math.min(...selected.map((p) => p.x));
  const minY = Math.min(...selected.map((p) => p.y));
  const [x, setX] = useState(minX);
  const [y, setY] = useState(minY);
  const [direction, setDirection] = useState<'row' | 'column'>('row');
  const [perLine, setPerLine] = useState(Math.max(1, selected.length));
  const [gap, setGap] = useState(0);
  const ids = selected.map((p) => p.id).join(',');

  useEffect(() => {
    setX(minX);
    setY(minY);
    setPerLine(Math.max(1, selected.length));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ids]);

  const rotate = (step: 1 | -1): void =>
    void onPlace(selected.map((p) => ({ id: p.id, x: p.x, y: p.y, rotation: nextRotation(p.rotation, step) })));

  return (
    <Section
      variant="solid"
      title={one ? t('Место {name}', { name: one.name }) : t('Выбрано: {n} ПК', { n: selected.length })}
    >
      {one ? (
        <div className="grid grid-cols-2 gap-3">
          <Field label={t('Колонка (x)')}>
            <NumberInput value={x + 1} min={1} max={MAX_CELL + 1} onChange={(v) => setX(v - 1)} />
          </Field>
          <Field label={t('Ряд (y)')}>
            <NumberInput value={y + 1} min={1} max={MAX_CELL + 1} onChange={(v) => setY(v - 1)} />
          </Field>
          <Button
            className="col-span-2"
            disabled={busy || (x === one.x && y === one.y)}
            onClick={() => void onPlace([{ id: one.id, x, y }])}
          >
            {t('Поставить сюда')}
          </Button>
        </div>
      ) : (
        <>
          <p className="text-[13px] text-muted">
            {t('Перетащите любой из выбранных ПК — сдвинется вся группа. Или расставьте их по номерам:')}
          </p>
          <div className="grid grid-cols-2 gap-3">
            <Field label={t('С колонки (x)')}>
              <NumberInput value={x + 1} min={1} max={MAX_CELL + 1} onChange={(v) => setX(v - 1)} />
            </Field>
            <Field label={t('С ряда (y)')}>
              <NumberInput value={y + 1} min={1} max={MAX_CELL + 1} onChange={(v) => setY(v - 1)} />
            </Field>
            <Field label={t('ПК в линии')}>
              <NumberInput value={perLine} min={1} max={MAX_CELL} onChange={setPerLine} />
            </Field>
            <Field label={t('Промежуток, клеток')}>
              <NumberInput value={gap} min={0} max={5} onChange={setGap} />
            </Field>
          </div>
          <Segmented
            label={t('Направление')}
            value={direction}
            onChange={setDirection}
            options={[
              { id: 'row', label: t('В ряд') },
              { id: 'column', label: t('Колонкой') },
            ]}
          />
          <Button
            variant="primary"
            disabled={busy}
            onClick={() => void onPlace(arrange(selected, { x, y }, direction, perLine, gap))}
          >
            {t('Расставить')}
          </Button>
        </>
      )}
      <FieldGroup label={t('Куда смотрит монитор')}>
        <RotateButtons disabled={busy} onRotate={rotate} />
      </FieldGroup>
      {zones.length > 0 && (
        <FieldGroup label={t('Выбрать всю зону')}>
          <div className="flex flex-wrap gap-1.5">
            {zones.map((z) => (
              <Chip key={z.name} tone="outlined" onClick={() => onSelectZone(z.name)}>
                {z.name} · {pcs.filter((p) => p.zone === z.name).length}
              </Chip>
            ))}
          </div>
        </FieldGroup>
      )}
    </Section>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// The plan: size, objects, zone areas
// ---------------------------------------------------------------------------------------------------------------------

const EMPTY_PLAN: HallPlan = { width: 16, height: 10, objects: [], areas: [] };

let planIds = 0;
const newId = (prefix: string): string => `${prefix}-${Date.now().toString(36)}-${(planIds++).toString(36)}`;

function BoxFields({
  value,
  onChange,
}: {
  value: { x: number; y: number; w: number; h: number };
  onChange: (v: { x: number; y: number; w: number; h: number }) => void;
}): JSX.Element {
  const field = (k: 'x' | 'y' | 'w' | 'h', label: string, shift: number) => (
    <Field label={label}>
      <NumberInput
        value={value[k] + shift}
        min={shift === 1 ? 1 : 1}
        max={MAX_CELL + 1}
        onChange={(v) => onChange({ ...value, [k]: Math.max(shift === 1 ? 0 : 1, v - shift) })}
      />
    </Field>
  );
  return (
    <div className="grid grid-cols-4 gap-2">
      {field('x', t('x'), 1)}
      {field('y', t('y'), 1)}
      {field('w', t('Шир.'), 0)}
      {field('h', t('Выс.'), 0)}
    </div>
  );
}

/** Size of the hall, objects and zone areas, saved together (`PUT /admin/pcs/plan`). */
function PlanEditor({
  draft,
  saved,
  zones,
  pcs,
  onChange,
  onSaved,
  focus,
  onFocus,
}: {
  draft: HallPlan;
  saved: HallPlan | null;
  zones: Zone[];
  pcs: HallPc[];
  onChange: (p: HallPlan) => void;
  onSaved: () => Promise<void>;
  focus: string | null;
  onFocus: (id: string | null) => void;
}): JSX.Element {
  const [saving, setSaving] = useState(false);
  const [note, setNote] = useState<NoteState>(null);
  const dirty = JSON.stringify(draft) !== JSON.stringify(saved ?? EMPTY_PLAN);
  const set = (p: Partial<HallPlan>): void => onChange({ ...draft, ...p });

  const addObject = (kind: HallObjectKind): void => {
    const id = newId(kind);
    const size = kind === 'wall' ? { w: 6, h: 1 } : kind === 'label' ? { w: 3, h: 1 } : { w: 2, h: 2 };
    set({
      objects: [...draft.objects, { id, kind, x: 0, y: Math.max(0, draft.height - size.h), ...size, text: null }],
    });
    onFocus(id);
  };

  /** A zone's area around its PCs (one free cell of margin), or a 4×3 box when it has none yet. */
  const addArea = (zone: string): void => {
    const own = pcs.filter((p) => p.zone === zone);
    const id = newId('area');
    const area =
      own.length > 0
        ? (() => {
            const x = Math.max(0, Math.min(...own.map((p) => p.x)) - 1);
            const y = Math.max(0, Math.min(...own.map((p) => p.y)) - 1);
            return {
              id,
              zone,
              x,
              y,
              w: Math.max(...own.map((p) => p.x)) + 2 - x,
              h: Math.max(...own.map((p) => p.y)) + 2 - y,
            };
          })()
        : { id, zone, x: 0, y: 0, w: 4, h: 3 };
    set({ areas: [...draft.areas.filter((a) => a.zone !== zone), area] });
    onFocus(id);
  };

  const save = async (): Promise<void> => {
    setSaving(true);
    setNote(null);
    try {
      await clubApi.saveHallPlan(draft);
      await onSaved();
      setNote({ text: t('План зала сохранён'), tone: 'ok' });
    } catch (e) {
      setNote({ text: describe(e), tone: 'err' });
    } finally {
      setSaving(false);
    }
  };

  return (
    <>
      <Section variant="side" title={t('План зала')}>
        <div className="grid grid-cols-2 gap-3">
          <Field label={t('Ширина зала, клеток')}>
            <NumberInput value={draft.width} min={1} max={MAX_CELL + 1} onChange={(v) => set({ width: v })} />
          </Field>
          <Field label={t('Высота зала, клеток')}>
            <NumberInput value={draft.height} min={1} max={MAX_CELL + 1} onChange={(v) => set({ height: v })} />
          </Field>
        </div>

        <FieldGroup label={t('Добавить на план')}>
          <div className="flex flex-wrap gap-1.5">
            {OBJECT_KINDS.map((k) => (
              <Chip key={k.id} tone="outlined" onClick={() => addObject(k.id)}>
                + {t(k.label)}
              </Chip>
            ))}
          </div>
        </FieldGroup>
        {draft.objects.length > 0 && (
          <ul className="flex flex-col gap-3">
            {draft.objects.map((o, i) => (
              <li
                key={o.id}
                data-plan-object={o.id}
                onFocusCapture={() => onFocus(o.id)}
                className={clsx(
                  'flex flex-col gap-2 rounded-md border p-2.5',
                  focus === o.id ? 'border-accent/60' : 'border-line',
                )}
              >
                <div className="flex items-center justify-between gap-2">
                  <span className="label-sm text-text">{objectLabel(o.kind)}</span>
                  <Button
                    size="sm"
                    variant="tertiary"
                    onClick={() => set({ objects: draft.objects.filter((_, j) => j !== i) })}
                  >
                    {t('Удалить')}
                  </Button>
                </div>
                <BoxFields
                  value={o}
                  onChange={(b) => set({ objects: draft.objects.map((x, j) => (j === i ? { ...x, ...b } : x)) })}
                />
                <Field label={t('Подпись')}>
                  <Input
                    value={o.text ?? ''}
                    maxLength={40}
                    onChange={(e) =>
                      set({
                        objects: draft.objects.map((x, j) => (j === i ? { ...x, text: e.target.value || null } : x)),
                      })
                    }
                  />
                </Field>
              </li>
            ))}
          </ul>
        )}

        {zones.length > 0 && (
          <FieldGroup
            label={t('Область зоны')}
            hint={t('Прямоугольник в цвете зоны вокруг её ПК; поправьте размеры ниже')}
          >
            <div className="flex flex-wrap gap-1.5">
              {zones.map((z) => (
                <Chip key={z.name} tone="outlined" onClick={() => addArea(z.name)}>
                  {t('Обвести {zone}', { zone: z.name })}
                </Chip>
              ))}
            </div>
          </FieldGroup>
        )}
        {draft.areas.length > 0 && (
          <ul className="flex flex-col gap-3">
            {draft.areas.map((a, i) => (
              <li
                key={a.id}
                onFocusCapture={() => onFocus(a.id)}
                className={clsx(
                  'flex flex-col gap-2 rounded-md border p-2.5',
                  focus === a.id ? 'border-accent/60' : 'border-line',
                )}
              >
                <div className="flex items-center justify-between gap-2">
                  <span className="label-sm text-text">{t('Зона {zone}', { zone: a.zone })}</span>
                  <Button
                    size="sm"
                    variant="tertiary"
                    onClick={() => set({ areas: draft.areas.filter((_, j) => j !== i) })}
                  >
                    {t('Удалить')}
                  </Button>
                </div>
                <BoxFields
                  value={a}
                  onChange={(b) => set({ areas: draft.areas.map((x, j) => (j === i ? { ...x, ...b } : x)) })}
                />
              </li>
            ))}
          </ul>
        )}
        <Note note={note} />
        {saved && (
          <Button
            variant="tertiary"
            size="sm"
            className="self-start"
            disabled={saving}
            onClick={() =>
              void (async () => {
                setSaving(true);
                try {
                  await clubApi.clearHallPlan();
                  await onSaved();
                } catch (e) {
                  setNote({ text: describe(e), tone: 'err' });
                } finally {
                  setSaving(false);
                }
              })()
            }
          >
            {t('Убрать план')}
          </Button>
        )}
      </Section>
      <SaveBar
        dirty={dirty}
        saving={saving}
        label={t('План зала изменён')}
        onReset={() => onChange(saved ?? EMPTY_PLAN)}
        onSave={() => void save()}
      />
    </>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------------------------------------------------

const CELL = 44;
const ZOOMS = [0.5, 0.65, 0.8, 1, 1.25];

type Drag =
  | { kind: 'move'; x0: number; y0: number; ids: string[]; moved: boolean; pressed: string; additive: boolean }
  | { kind: 'band'; x0: number; y0: number; additive: boolean; base: Set<string> };

export default function HallPage(): JSX.Element {
  const [pcs, setPcs] = useState<HallPc[]>([]);
  const [zones, setZones] = useState<Zone[]>([]);
  const [plan, setPlan] = useState<HallPlan | null>(null);
  const [draft, setDraft] = useState<HallPlan>(EMPTY_PLAN);
  const [focus, setFocus] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selection, setSelection] = useState<Set<string>>(new Set());
  const [delta, setDelta] = useState<{ dx: number; dy: number }>({ dx: 0, dy: 0 });
  const [band, setBand] = useState<{ x: number; y: number; w: number; h: number } | null>(null);
  const [busy, setBusy] = useState(false);
  const [zoom, setZoom] = useState(1);
  const drag = useRef<Drag | null>(null);
  const boxRef = useRef<HTMLDivElement>(null);
  const cell = Math.round(CELL * zoom);

  const load = useCallback(async () => {
    try {
      const r = await clubApi.pcs();
      setPcs(r.items);
      setZones(r.zones);
      setPlan(r.plan ?? null);
      setDraft(r.plan ?? EMPTY_PLAN);
      setSelection((s) => new Set([...s].filter((id) => r.items.some((p) => p.id === id))));
      setError(null);
    } catch (e) {
      setError(describe(e));
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  // One spare row and column beyond everything, to drag into.
  const size = planSize(pcs, draft, { cols: MIN_COLS - 1, rows: MIN_ROWS - 1 });
  const cols = size.cols + 1;
  const rows = size.rows + 1;
  const colorOf = useMemo(() => new Map(zones.map((z) => [z.name, z.color])), [zones]);
  const shared = useMemo(() => sharedCells(pcs), [pcs]);
  const selected = pcs.filter((p) => selection.has(p.id));
  const one = selected.length === 1 ? selected[0] : null;

  const place = async (targets: Target[]): Promise<boolean> => {
    const problem = placementProblem(targets, pcs);
    if (problem) {
      setError(problem);
      return false;
    }
    const by = new Map(targets.map((tg) => [tg.id, tg]));
    setPcs((list) => list.map((p) => (by.has(p.id) ? { ...p, ...by.get(p.id) } : p)));
    setBusy(true);
    try {
      await clubApi.layoutPcs(targets);
      setError(null);
      return true;
    } catch (e) {
      setError(placementError(e, pcs));
      await load();
      return false;
    } finally {
      setBusy(false);
    }
  };

  /** The pointer's position in the plan's box, px. */
  const at = (e: PointerEvent<HTMLElement>): { x: number; y: number } => {
    const r = boxRef.current?.getBoundingClientRect();
    return { x: e.clientX - (r?.left ?? 0), y: e.clientY - (r?.top ?? 0) };
  };

  const onTileDown = (e: PointerEvent<HTMLButtonElement>, p: HallPc): void => {
    if (e.button !== 0) return;
    e.stopPropagation();
    boxRef.current?.setPointerCapture(e.pointerId);
    const additive = e.shiftKey || e.ctrlKey || e.metaKey;
    let ids: string[];
    if (additive) {
      const next = new Set(selection);
      if (next.has(p.id)) next.delete(p.id);
      else next.add(p.id);
      setSelection(next);
      ids = [...next];
    } else if (selection.has(p.id)) {
      ids = [...selection];
    } else {
      setSelection(new Set([p.id]));
      ids = [p.id];
    }
    const { x, y } = at(e);
    drag.current = { kind: 'move', x0: x, y0: y, ids, moved: false, pressed: p.id, additive };
  };

  const onBoxDown = (e: PointerEvent<HTMLDivElement>): void => {
    if (e.button !== 0) return;
    boxRef.current?.setPointerCapture(e.pointerId);
    const additive = e.shiftKey || e.ctrlKey || e.metaKey;
    const { x, y } = at(e);
    drag.current = { kind: 'band', x0: x, y0: y, additive, base: additive ? new Set(selection) : new Set() };
  };

  const onMove = (e: PointerEvent<HTMLDivElement>): void => {
    const d = drag.current;
    if (!d) return;
    const { x, y } = at(e);
    if (d.kind === 'move') {
      const dx = Math.round((x - d.x0) / cell);
      const dy = Math.round((y - d.y0) / cell);
      if (dx !== 0 || dy !== 0) d.moved = true;
      setDelta((old) => (old.dx === dx && old.dy === dy ? old : { dx, dy }));
      return;
    }
    const r = { x: Math.min(d.x0, x), y: Math.min(d.y0, y), w: Math.abs(x - d.x0), h: Math.abs(y - d.y0) };
    if (r.w < 4 && r.h < 4) return;
    setBand(r);
    const inside = pcs.filter((p) => {
      const cx = (p.x + 0.5) * cell;
      const cy = (p.y + 0.5) * cell;
      return cx >= r.x && cx <= r.x + r.w && cy >= r.y && cy <= r.y + r.h;
    });
    setSelection(new Set([...d.base, ...inside.map((p) => p.id)]));
  };

  const onUp = (): void => {
    const d = drag.current;
    drag.current = null;
    if (!d) return;
    if (d.kind === 'band') {
      if (!band && !d.additive) setSelection(new Set());
      setBand(null);
      return;
    }
    const { dx, dy } = delta;
    setDelta({ dx: 0, dy: 0 });
    if (!d.moved || (dx === 0 && dy === 0)) {
      // A plain click on a PC of a group: that PC alone.
      if (!d.additive && !d.moved && selection.size > 1) setSelection(new Set([d.pressed]));
      return;
    }
    const moving = pcs.filter((p) => d.ids.includes(p.id));
    void place(moving.map((p) => ({ id: p.id, x: p.x + dx, y: p.y + dy })));
  };

  const onKey = (e: KeyboardEvent<HTMLDivElement>): void => {
    if (e.key === 'Escape') {
      setSelection(new Set());
      return;
    }
    const d: Record<string, [number, number]> = {
      ArrowLeft: [-1, 0],
      ArrowRight: [1, 0],
      ArrowUp: [0, -1],
      ArrowDown: [0, 1],
    };
    const step = d[e.key];
    if (!step || selected.length === 0 || busy) return;
    e.preventDefault();
    void place(selected.map((p) => ({ id: p.id, x: p.x + step[0], y: p.y + step[1] })));
  };

  const overlapNames = [...shared.values()].map((list) => list.map((p) => p.name).join(' + '));

  return (
    <OwnerPage>
      <PageHeader title={t('Зал и устройства')} caption={t('Настройка клуба')} />
      {error && <Note note={{ text: error, tone: 'err' }} />}
      {overlapNames.length > 0 && (
        <Note
          note={{
            text: t('В одной клетке стоят: {list}. Перетащите один из них на свободное место.', {
              list: overlapNames.join(', '),
            }),
            tone: 'warn',
          }}
        />
      )}
      <div className="grid grid-cols-1 items-start gap-5 lg:grid-cols-[minmax(0,1fr)_400px]">
        <div
          tabIndex={0}
          onKeyDown={onKey}
          aria-label={t('Схема зала')}
          className="focus-ring glass-panel flex min-w-0 flex-col gap-4 px-5 pb-5 pt-[18px] outline-none"
        >
          <header className="flex min-h-7 flex-wrap items-center justify-between gap-x-3 gap-y-1">
            <h2 className="label text-text">{t('Схема зала')}</h2>
            <span className="flex flex-wrap items-center gap-2">
              {selected.length > 0 ? (
                <>
                  <span className="label-sm">{t('тащите мышью или')}</span>
                  <Kbd>← ↑ ↓ →</Kbd>
                  <Kbd className="ml-2">Esc</Kbd>
                  <span className="label-sm">{t('снять выбор')}</span>
                </>
              ) : (
                <span className="label-sm">
                  {t('Нажмите ПК или обведите несколько рамкой; Shift — добавить к выбору')}
                </span>
              )}
              <span className="ml-2 flex items-center gap-1">
                <Button
                  size="sm"
                  variant="ghost"
                  aria-label={t('Мельче')}
                  disabled={zoom <= ZOOMS[0]!}
                  onClick={() => setZoom(ZOOMS[Math.max(0, ZOOMS.indexOf(zoom) - 1)]!)}
                >
                  −
                </Button>
                <span className="tnum w-10 text-center font-mono text-[11px] text-muted">
                  {Math.round(zoom * 100)}%
                </span>
                <Button
                  size="sm"
                  variant="ghost"
                  aria-label={t('Крупнее')}
                  disabled={zoom >= ZOOMS[ZOOMS.length - 1]!}
                  onClick={() => setZoom(ZOOMS[Math.min(ZOOMS.length - 1, ZOOMS.indexOf(zoom) + 1)]!)}
                >
                  +
                </Button>
              </span>
            </span>
          </header>
          {/* Padded so the selection's brackets, just outside the device, are not clipped by the scroller. */}
          <div className="thin-scrollbar -m-1.5 overflow-auto p-1.5">
            <div
              ref={boxRef}
              data-testid="hall-plan"
              onPointerDown={onBoxDown}
              onPointerMove={onMove}
              onPointerUp={onUp}
              onPointerCancel={onUp}
              className="relative select-none border-l border-t border-accent/[0.06]"
              style={{ width: cols * cell, height: rows * cell, touchAction: 'none', ...gridBackground(cell) }}
            >
              <PlanBackdrop plan={draft} zones={zones} cell={cell} selected={focus} compact={zoom < 0.8} />
              {pcs.map((p) => {
                const on = selection.has(p.id);
                const moving = on && (delta.dx !== 0 || delta.dy !== 0);
                const stack = shared.get(`${p.x}:${p.y}`);
                const offset = stack ? stack.indexOf(p) * 8 : 0;
                const x = p.x + (moving ? delta.dx : 0);
                const y = p.y + (moving ? delta.dy : 0);
                return (
                  <button
                    key={p.id}
                    type="button"
                    tabIndex={-1}
                    data-pc={p.number}
                    onPointerDown={(e) => onTileDown(e, p)}
                    aria-pressed={on}
                    title={`${p.name} · ${p.zone} · ${x + 1}, ${y + 1}`}
                    data-selected={on}
                    className={clsx(
                      'hud-focus absolute flex touch-none items-center justify-center rounded-[7px] border bg-bg/80 font-display font-medium leading-none tracking-[-0.01em] transition-colors [--brk-inset:-5px] [--brk-size:10px] hover:bg-text/[0.05]',
                      zoom < 0.8 ? 'text-[11px]' : 'text-[15px]',
                      on ? 'z-10 border-accent/80 bg-accent/[0.08] text-hi shadow-sel' : 'border-accent/[0.16]',
                      moving && 'opacity-80',
                      stack && 'ring-2 ring-danger/70',
                      p.status === 'offline' ? 'text-text/50' : !on && 'text-text/[0.88]',
                    )}
                    style={{
                      left: x * cell + 3 + offset,
                      top: y * cell + 3 + offset,
                      width: cell - 6,
                      height: cell - 6,
                    }}
                  >
                    {colorOf.get(p.zone) && (
                      <span
                        aria-hidden="true"
                        className="absolute bottom-2 left-0 top-2 w-[3px] rounded-r-[2px] opacity-70"
                        style={{ background: colorOf.get(p.zone) }}
                      />
                    )}
                    <RotationMark rotation={p.rotation} />
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
                );
              })}
              {band && (
                <div
                  aria-hidden="true"
                  className="pointer-events-none absolute z-20 rounded-[4px] border border-accent/70 bg-accent/[0.08]"
                  style={{ left: band.x, top: band.y, width: band.w, height: band.h }}
                />
              )}
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
          {selected.length > 0 && (
            <PlacePanel
              selected={selected}
              zones={zones}
              pcs={pcs}
              busy={busy}
              onPlace={place}
              onSelectZone={(zone) => setSelection(new Set(pcs.filter((p) => p.zone === zone).map((p) => p.id)))}
            />
          )}
          {one ? (
            <DevicePanel
              pc={one}
              zones={zones}
              onChanged={load}
              onDeleted={() => {
                setSelection(new Set());
                void load();
              }}
            />
          ) : selected.length === 0 ? (
            <AddDevice
              zones={zones}
              pcs={pcs}
              onAdded={async (id) => {
                await load();
                if (id) setSelection(new Set([id]));
              }}
            />
          ) : null}
          <PlanEditor
            draft={draft}
            saved={plan}
            zones={zones}
            pcs={pcs}
            onChange={setDraft}
            onSaved={load}
            focus={focus}
            onFocus={setFocus}
          />
          {/* Always here: a PC can only be put in a zone that exists, so the zones must be reachable from its panel too. */}
          <ZonesEditor onSaved={load} />
        </aside>
      </div>
      <InstallKit />
      <PolicyEditor />
    </OwnerPage>
  );
}
