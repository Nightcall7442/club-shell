/**
 * The main map as the hall stands (SHELL_CHANGES 94): every PC at its place from «Зал и устройства», over the plan's zone
 * areas and objects. By default the whole hall fits the panel (every PC of the club without scrolling, 1366–1920 px);
 * «+» enlarges it (then drag the empty plan, or scroll, to move around), «Весь зал» fits it again. Below a comfortable
 * size the tiles go compact: number, status colour, time.
 */
import clsx from 'clsx';
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type PointerEvent } from 'react';
import type { HallPlan, Seat, Zone } from '@/api';
import { t } from '@/i18n';
import { Button } from '@/ui';
import { PlanBackdrop, RotationMark, planSize } from './hallKit';

/** A cell at least this big draws the full tile (name, game, time); smaller ones the compact one. */
export const FULL_TILE_CELL = 112;
const FULL_TILE_WIDTH = 118;
const FULL_TILE_HEIGHT = 56;
/** A tile is at most this much taller than wide (height / width) … */
const MAX_RATIO = 0.82;
/** … and at least this tall for its width, so a very wide panel does not flatten it. */
const MIN_RATIO = 0.5;
/** An empty row or column of the plan (an aisle) takes this share of a cell. */
const AISLE = 0.32;
const MIN_CELL = 18;
const ZOOMS = [1, 1.5, 2, 3];
const GAP = 5;

/**
 * Where each column and row of the plan starts, in cells: a row or column holding no PC, object or area is an aisle and
 * takes {@link AISLE} of a cell.
 */
function lineLayout(
  places: { x: number; y: number }[],
  plan: HallPlan | null | undefined,
  cols: number,
  rows: number,
): { colStart: number[]; rowStart: number[]; colUnits: number; rowUnits: number } {
  const usedCol = new Array<boolean>(cols).fill(false);
  const usedRow = new Array<boolean>(rows).fill(false);
  for (const p of places) {
    usedCol[p.x] = true;
    usedRow[p.y] = true;
  }
  for (const o of [...(plan?.objects ?? []), ...(plan?.areas ?? [])]) {
    for (let x = o.x; x < o.x + o.w; x++) usedCol[x] = true;
    for (let y = o.y; y < o.y + o.h; y++) usedRow[y] = true;
  }
  const starts = (used: boolean[]): number[] => {
    const out = [0];
    for (const u of used) out.push(out[out.length - 1]! + (u ? 1 : AISLE));
    return out;
  };
  const colStart = starts(usedCol);
  const rowStart = starts(usedRow);
  return { colStart, rowStart, colUnits: colStart[cols]!, rowUnits: rowStart[rows]! };
}

/** One area per zone around its PCs, in plan cells, coloured as the zone. */
function autoZoneAreas(
  seats: Seat[],
  places: { x: number; y: number }[],
  zones: Zone[],
): { zone: string; color: string; x: number; y: number; w: number; h: number }[] {
  const bounds = new Map<string, { x0: number; y0: number; x1: number; y1: number }>();
  seats.forEach((s, i) => {
    const p = places[i]!;
    const b = bounds.get(s.pc.zone);
    bounds.set(
      s.pc.zone,
      b
        ? { x0: Math.min(b.x0, p.x), y0: Math.min(b.y0, p.y), x1: Math.max(b.x1, p.x), y1: Math.max(b.y1, p.y) }
        : { x0: p.x, y0: p.y, x1: p.x, y1: p.y },
    );
  });
  const colour = new Map(zones.map((z) => [z.name, z.color]));
  return [...bounds.entries()].map(([zone, b]) => ({
    zone,
    color: colour.get(zone) ?? '#7D8A96',
    x: b.x0,
    y: b.y0,
    w: b.x1 - b.x0 + 1,
    h: b.y1 - b.y0 + 1,
  }));
}

/** Every seat has a place: the server knows the hall layout (an older one sends none, and the map shows zones). */
export function hasPlaces(seats: Seat[]): boolean {
  return seats.length > 0 && seats.every((s) => s.place !== undefined);
}

/** «−», «+» and «Весь зал» of the plan view, for the map's toolbar. */
export function ZoomControls({ zoom, onZoom }: { zoom: number; onZoom: (zoom: number) => void }): JSX.Element {
  return (
    <span role="group" aria-label={t('Масштаб')} className="flex items-center gap-0.5">
      <Button
        size="sm"
        variant="ghost"
        aria-label={t('Мельче')}
        disabled={zoom === ZOOMS[0]}
        onClick={() => onZoom(ZOOMS[Math.max(0, ZOOMS.indexOf(zoom) - 1)]!)}
      >
        −
      </Button>
      <Button
        size="sm"
        variant="ghost"
        aria-label={t('Крупнее')}
        disabled={zoom === ZOOMS[ZOOMS.length - 1]}
        onClick={() => onZoom(ZOOMS[Math.min(ZOOMS.length - 1, ZOOMS.indexOf(zoom) + 1)]!)}
      >
        +
      </Button>
      <Button size="sm" variant="ghost" disabled={zoom === 1} onClick={() => onZoom(1)}>
        {t('Весь зал')}
      </Button>
    </span>
  );
}

export function HallPlanView({
  seats,
  plan,
  zones,
  renderTile,
  zoom,
  onZoom,
}: {
  /** 1 — the whole hall in the panel; more — enlarged (see {@link ZoomControls}). */
  zoom: number;
  onZoom: (zoom: number) => void;
  seats: Seat[];
  plan: HallPlan | null | undefined;
  zones: Zone[];
  /** The seat's tile in absolute position `box`; `compact` below {@link FULL_TILE_CELL}. */
  renderTile: (seat: Seat, box: CSSProperties, compact: boolean) => JSX.Element;
}): JSX.Element {
  const frame = useRef<HTMLDivElement>(null);
  const [room, setRoom] = useState({ w: 0, h: 0 });
  const pan = useRef<{ x: number; y: number; left: number; top: number } | null>(null);

  useLayoutEffect(() => {
    const el = frame.current;
    if (!el) return undefined;
    const measure = (): void => setRoom({ w: el.clientWidth, h: el.clientHeight });
    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const places = seats.map((s) => s.place ?? { x: 0, y: 0, rotation: 0 as const });
  const { cols, rows } = planSize(places, plan);
  const layout = useMemo(() => lineLayout(places, plan, cols, rows), [places, plan, cols, rows]);
  // Tiles fill the panel's width and stand wider than tall, so the full tile (name, game, time) fits; an empty aisle row
  // or column takes a sliver, not a whole cell. Square cells left two thirds of the panel empty and every tile compact.
  const fitW = room.w / layout.colUnits;
  // 12 px on top for the first zone's label.
  const fitH = Math.max(0, room.h - 14) / layout.rowUnits;
  const baseW = Math.min(fitW, fitH / MIN_RATIO);
  const baseH = Math.min(fitH, baseW * MAX_RATIO);
  const cellW = Math.max(MIN_CELL, Math.floor(baseW * zoom));
  const cellH = Math.max(MIN_CELL, Math.floor(baseH * zoom));
  const cell = Math.min(cellW, cellH);
  const compact = cellH < FULL_TILE_HEIGHT || cellW < FULL_TILE_WIDTH;
  const xAt = (c: number): number => layout.colStart[c]! * cellW;
  const yAt = (r: number): number => layout.rowStart[r]! * cellH;
  const place = (r: { x: number; y: number; w: number; h: number }): CSSProperties => ({
    left: xAt(r.x),
    top: yAt(r.y),
    width: xAt(r.x + r.w) - xAt(r.x),
    height: yAt(r.y + r.h) - yAt(r.y),
  });
  // Without areas drawn by the owner, each zone gets one around its PCs, so the cashier sees VIP / Standard at a glance.
  const zoneAreas = plan?.areas.length ? [] : autoZoneAreas(seats, places, zones);

  // Back to the whole hall when the hall itself changes size.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => onZoom(1), [cols, rows]);

  const onDown = (e: PointerEvent<HTMLDivElement>): void => {
    if (zoom === 1 || e.button !== 0 || e.target !== e.currentTarget) return;
    const el = frame.current;
    if (!el) return;
    el.setPointerCapture(e.pointerId);
    pan.current = { x: e.clientX, y: e.clientY, left: el.scrollLeft, top: el.scrollTop };
  };
  const onMove = (e: PointerEvent<HTMLDivElement>): void => {
    const p = pan.current;
    const el = frame.current;
    if (!p || !el) return;
    el.scrollLeft = p.left - (e.clientX - p.x);
    el.scrollTop = p.top - (e.clientY - p.y);
  };
  const onUp = (): void => {
    pan.current = null;
  };

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div
        ref={frame}
        data-testid="map-plan"
        data-cell={cell}
        onPointerDown={onDown}
        onPointerMove={onMove}
        onPointerUp={onUp}
        onPointerCancel={onUp}
        className={clsx('thin-scrollbar min-h-0 flex-1 overflow-auto', zoom !== 1 && 'cursor-grab')}
      >
        {room.w > 0 && (
          <div className="relative mt-3" style={{ width: xAt(cols), height: yAt(rows) }}>
            {zoneAreas.map((a) => (
              <div
                key={a.zone}
                aria-hidden="true"
                className="pointer-events-none absolute rounded-[10px] border"
                style={{ ...place(a), background: `${a.color}0D`, borderColor: `${a.color}40` }}
              >
                <span
                  className="absolute -top-[9px] left-3 bg-[rgb(var(--c-surface))] px-1.5 font-mono text-[10px] font-semibold uppercase leading-[18px] tracking-[0.18em]"
                  style={{ color: a.color }}
                >
                  {a.zone}
                </span>
              </div>
            ))}
            <PlanBackdrop plan={plan} zones={zones} cell={cell} compact={cell < 48} place={place} />
            {seats.map((s, i) => {
              const p = places[i]!;
              const at = place({ x: p.x, y: p.y, w: 1, h: 1 });
              const box: CSSProperties = {
                position: 'absolute',
                left: (at.left as number) + GAP,
                top: (at.top as number) + GAP,
                width: (at.width as number) - GAP * 2,
                height: (at.height as number) - GAP * 2,
              };
              return (
                <div key={s.pc.id} className="contents">
                  {renderTile(s, box, compact)}
                  {p.rotation !== 0 || plan ? (
                    <span
                      className="pointer-events-none absolute"
                      style={{ left: box.left, top: box.top, width: box.width, height: box.height }}
                    >
                      <RotationMark rotation={p.rotation} className="bg-accent/50" />
                    </span>
                  ) : null}
                </div>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}
