/**
 * The hall plan, shared by the hall editor («Зал и устройства») and the main map (SHELL_CHANGES 94): the plan's size in
 * cells, its objects (wall, bar, entrance, aisle, label) and zone areas drawn under the PCs, where a PC's monitor faces,
 * and PCs that share a cell (left from before the server refused it).
 */
import type { CSSProperties } from 'react';
import clsx from 'clsx';
import type { HallArea, HallObject, HallObjectKind, HallPlan, Rotation, Zone } from '@/api';
import { t } from '@/i18n';

export const ROTATIONS: Rotation[] = [0, 90, 180, 270];

export const OBJECT_KINDS: { id: HallObjectKind; label: string }[] = [
  { id: 'wall', label: 'Стена' },
  { id: 'bar', label: 'Бар / касса' },
  { id: 'entrance', label: 'Вход' },
  { id: 'aisle', label: 'Проход' },
  { id: 'label', label: 'Надпись' },
];

export const MAX_CELL = 200;

export function objectLabel(kind: HallObjectKind): string {
  return t(OBJECT_KINDS.find((k) => k.id === kind)?.label ?? 'Надпись');
}

export function nextRotation(r: Rotation | undefined, step: 1 | -1): Rotation {
  const i = ROTATIONS.indexOf(r ?? 0);
  return ROTATIONS[(i + step + ROTATIONS.length) % ROTATIONS.length] as Rotation;
}

interface Placed {
  x: number;
  y: number;
}

/** The plan's size in cells: the drawn plan's, grown to hold every PC, object and area. */
export function planSize(
  pcs: Placed[],
  plan: HallPlan | null | undefined,
  min = { cols: 1, rows: 1 },
): { cols: number; rows: number } {
  let cols = Math.max(min.cols, plan?.width ?? 0);
  let rows = Math.max(min.rows, plan?.height ?? 0);
  for (const p of pcs) {
    cols = Math.max(cols, p.x + 1);
    rows = Math.max(rows, p.y + 1);
  }
  for (const o of [...(plan?.objects ?? []), ...(plan?.areas ?? [])]) {
    cols = Math.max(cols, o.x + o.w);
    rows = Math.max(rows, o.y + o.h);
  }
  return { cols, rows };
}

/** Cells held by more than one PC: cell key → the PCs there, in the order given. */
export function sharedCells<T extends Placed & { id: string }>(pcs: T[]): Map<string, T[]> {
  const by = new Map<string, T[]>();
  for (const p of pcs) {
    const k = `${p.x}:${p.y}`;
    by.set(k, [...(by.get(k) ?? []), p]);
  }
  for (const [k, list] of by) if (list.length < 2) by.delete(k);
  return by;
}

const OBJECT_STYLE: Record<HallObjectKind, string> = {
  wall: 'bg-text/[0.28] border border-text/[0.35]',
  bar: 'bg-warning/[0.14] border border-warning/[0.45] text-warning',
  entrance: 'border-2 border-dashed border-success/60 text-success',
  aisle: 'bg-text/[0.04] border border-dashed border-text/[0.14] text-muted',
  label: 'text-dim',
};

function box(o: { x: number; y: number; w: number; h: number }, cell: number): CSSProperties {
  return { left: o.x * cell, top: o.y * cell, width: o.w * cell, height: o.h * cell };
}

/**
 * Zone areas and objects in absolute position for cells of `cell` px: put it first inside the plan's relative box, the
 * PCs above it. `selected` outlines an object or area being edited.
 */
export function PlanBackdrop({
  plan,
  zones,
  cell,
  selected = null,
  compact = false,
}: {
  plan: HallPlan | null | undefined;
  zones: Zone[];
  cell: number;
  selected?: string | null;
  compact?: boolean;
}): JSX.Element | null {
  if (!plan) return null;
  const colour = new Map(zones.map((z) => [z.name, z.color]));
  const text = compact ? 'text-[9px]' : 'text-[11px]';
  return (
    <>
      {plan.areas.map((a: HallArea) => (
        <div
          key={a.id}
          data-area={a.id}
          aria-hidden="true"
          className={clsx(
            'pointer-events-none absolute rounded-[10px] border',
            selected === a.id && 'outline outline-2 outline-accent',
          )}
          style={{
            ...box(a, cell),
            background: `${colour.get(a.zone) ?? '#7D8A96'}14`,
            borderColor: `${colour.get(a.zone) ?? '#7D8A96'}55`,
          }}
        >
          <span
            className={clsx('absolute left-2 top-1 font-mono font-semibold uppercase tracking-[0.18em]', text)}
            style={{ color: colour.get(a.zone) ?? undefined }}
          >
            {a.zone}
          </span>
        </div>
      ))}
      {plan.objects.map((o: HallObject) => (
        <div
          key={o.id}
          data-object={o.kind}
          aria-hidden="true"
          className={clsx(
            'pointer-events-none absolute flex items-center justify-center overflow-hidden rounded-[6px] text-center font-mono font-semibold uppercase leading-tight tracking-[0.14em]',
            OBJECT_STYLE[o.kind],
            text,
            selected === o.id && 'outline outline-2 outline-accent',
          )}
          style={box(o, cell)}
        >
          {o.text ?? (o.kind === 'wall' || o.kind === 'aisle' ? '' : objectLabel(o.kind))}
        </div>
      ))}
    </>
  );
}

/** Where the monitor faces: a bar along that side of the PC's tile. */
export function RotationMark({ rotation, className }: { rotation: Rotation | undefined; className?: string }) {
  const side: Record<Rotation, string> = {
    0: 'left-2 right-2 top-[2px] h-[3px]',
    90: 'bottom-2 right-[2px] top-2 w-[3px]',
    180: 'bottom-[2px] left-2 right-2 h-[3px]',
    270: 'bottom-2 left-[2px] top-2 w-[3px]',
  };
  return (
    <span
      aria-hidden="true"
      data-rotation={rotation ?? 0}
      className={clsx('pointer-events-none absolute rounded-full bg-text/40', side[rotation ?? 0], className)}
    />
  );
}

/** The faint grid of the plan's cells, as a background of its box. */
export function gridBackground(cell: number): CSSProperties {
  return {
    backgroundImage:
      'linear-gradient(to right, rgb(var(--c-accent) / 0.06) 1px, transparent 1px), linear-gradient(to bottom, rgb(var(--c-accent) / 0.06) 1px, transparent 1px)',
    backgroundSize: `${cell}px ${cell}px`,
  };
}
