/**
 * The hall as it stands (SHELL_CHANGES 94), as the server: no two PCs in one cell (`409 cellTaken {pcId, x, y}`), a new PC
 * in the first free cell, the PCs' rotation and the club's hall plan (grid size, objects, zone areas).
 */
import { type ApiError, db, errors } from './db.js';
import { club } from './club.js';

export const MAX_CELL = 200;
export const OBJECT_KINDS = ['wall', 'bar', 'entrance', 'aisle', 'label'] as const;
export const ROTATIONS = [0, 90, 180, 270] as const;

export interface HallObject {
  id: string;
  kind: (typeof OBJECT_KINDS)[number];
  x: number;
  y: number;
  w: number;
  h: number;
  text?: string | null;
}

export interface HallArea {
  id: string;
  zone: string;
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface HallPlan {
  width: number;
  height: number;
  objects: HallObject[];
  areas: HallArea[];
}

export function hallPlan(): HallPlan | null {
  return club().hallPlan ?? null;
}

/** The PC (other than `except`) standing at (x, y), by number. */
export function holder(x: number, y: number, except?: string): string | null {
  const pc = db.pcs.filter((p) => p.id !== except && p.x === x && p.y === y).sort((a, b) => a.number - b.number)[0];
  return pc?.id ?? null;
}

export function cellTaken(pcId: string, x: number, y: number): ApiError {
  return errors.conflict('cellTaken', { pcId, x, y });
}

export function ensureFree(x: number, y: number, except?: string): void {
  const id = holder(x, y, except);
  if (id) throw cellTaken(id, x, y);
}

/** First free cell row by row within the plan's width (else the widest row in use, at least 10). */
export function freeCell(): { x: number; y: number } {
  const taken = new Set(db.pcs.map((p) => `${p.x},${p.y}`));
  const width = hallPlan()?.width ?? Math.max(10, ...db.pcs.map((p) => p.x + 1));
  for (let y = 0; y <= MAX_CELL; y++) {
    for (let x = 0; x < width; x++) if (!taken.has(`${x},${y}`)) return { x, y };
  }
  return { x: Math.min(width, MAX_CELL), y: 0 };
}

export function checkRotation(v: unknown, field = 'rotation'): number | null {
  if (v === undefined || v === null) return null;
  if (!ROTATIONS.includes(v as (typeof ROTATIONS)[number])) throw errors.validation(field, 'enum');
  return v as number;
}

const isInt = (v: unknown): v is number => typeof v === 'number' && Number.isInteger(v);

/** A plan from a request body, checked as the server does. */
export function readPlan(b: Record<string, unknown>): HallPlan {
  const size = (k: 'width' | 'height'): number => {
    const v = b[k];
    if (v === undefined || v === null) throw errors.validation(k, 'required');
    if (!isInt(v) || v < 1 || v > MAX_CELL + 1) throw errors.validation(k, 'range');
    return v;
  };
  const width = size('width');
  const height = size('height');
  const list = (k: 'objects' | 'areas'): Record<string, unknown>[] =>
    Array.isArray(b[k]) ? (b[k] as Record<string, unknown>[]) : [];
  const box = (field: string, o: Record<string, unknown>): void => {
    if (typeof o['id'] !== 'string' || o['id'].length === 0 || o['id'].length > 64)
      throw errors.validation(`${field}.id`, 'required');
    const [x, y, w, h] = [o['x'], o['y'], o['w'], o['h']];
    if (!isInt(x) || !isInt(y) || !isInt(w) || !isInt(h) || x < 0 || y < 0 || w < 1 || h < 1)
      throw errors.validation(field, 'range');
    if (x + w > MAX_CELL + 1 || y + h > MAX_CELL + 1) throw errors.validation(field, 'range');
  };
  const objects = list('objects').map((o, i): HallObject => {
    box(`objects[${i}]`, o);
    if (!OBJECT_KINDS.includes(o['kind'] as HallObject['kind'])) throw errors.validation(`objects[${i}].kind`, 'enum');
    const text = o['text'];
    if (text !== undefined && text !== null && (typeof text !== 'string' || text.length > 40))
      throw errors.validation(`objects[${i}].text`, 'max');
    return {
      id: o['id'] as string,
      kind: o['kind'] as HallObject['kind'],
      x: o['x'] as number,
      y: o['y'] as number,
      w: o['w'] as number,
      h: o['h'] as number,
      text: (text as string | null | undefined) ?? null,
    };
  });
  const areas = list('areas').map((a, i): HallArea => {
    box(`areas[${i}]`, a);
    if (typeof a['zone'] !== 'string' || a['zone'].length === 0 || a['zone'].length > 32)
      throw errors.validation(`areas[${i}].zone`, 'required');
    return {
      id: a['id'] as string,
      zone: a['zone'],
      x: a['x'] as number,
      y: a['y'] as number,
      w: a['w'] as number,
      h: a['h'] as number,
    };
  });
  if (objects.length + areas.length > 300) throw errors.validation('objects', 'max');
  return { width, height, objects, areas };
}
