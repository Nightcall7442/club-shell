/**
 * The main map as the hall stands (SHELL_CHANGES 94): every PC at its place from «Зал и устройства», over the plan's zone
 * areas and objects. By default the whole hall fits the panel (every PC of the club without scrolling, 1366–1920 px);
 * «+» enlarges it (then drag the empty plan, or scroll, to move around), «Весь зал» fits it again. Below a comfortable
 * size the tiles go compact: number, status colour, time.
 */
import clsx from 'clsx';
import { useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type PointerEvent } from 'react';
import type { HallPlan, Seat, Zone } from '@/api';
import { t } from '@/i18n';
import { Button } from '@/ui';
import { PlanBackdrop, RotationMark, gridBackground, planSize } from './hallKit';

/** A cell at least this big draws the full tile (name, game, time); smaller ones the compact one. */
export const FULL_TILE_CELL = 112;
const MIN_CELL = 18;
const ZOOMS = [1, 1.5, 2, 3];
const GAP = 3;

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
  const fit = Math.max(MIN_CELL, Math.floor(Math.min(room.w / cols, room.h / rows)));
  const cell = Math.round(fit * zoom);
  const compact = cell < FULL_TILE_CELL;

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
          <div className="relative" style={{ width: cols * cell, height: rows * cell, ...gridBackground(cell) }}>
            <PlanBackdrop plan={plan} zones={zones} cell={cell} compact={cell < 48} />
            {seats.map((s, i) => {
              const p = places[i]!;
              const box: CSSProperties = {
                position: 'absolute',
                left: p.x * cell + GAP,
                top: p.y * cell + GAP,
                width: cell - GAP * 2,
                height: cell - GAP * 2,
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
