/**
 * PC health: the Agent's telemetry (CPU / GPU temperature, FPS, load) folded into hourly buckets per PC, compared with
 * that PC's own last week, so a machine that is slowly getting hotter or losing frames is caught before it dies in the
 * middle of a match. A problem opens a maintenance ticket once (and tells the owner through the `hardware` event);
 * staff take it into work and close it from the "Состояние ПК" page. Optionally a PC with a serious problem is taken
 * out of service as soon as nobody sits at it.
 *
 * The mock has no real Agents, so {@link tickHealth} also plays their telemetry: steady machines plus three that are
 * going wrong in different ways (PC-07 heats up day by day, PC-15's GPU runs hot, PC-19 lost a third of its frames),
 * with a week of history back-filled on first start.
 */
import type { PcMetrics } from '@clubshell/contracts';
import { emit } from './club.js';
import { db, markDirty, now, openSessionForPc, uuid, type PcRecord } from './db.js';
import { broadcast } from './ws.js';

export type HealthKind = 'cpuHot' | 'gpuHot' | 'cpuTrend' | 'gpuTrend' | 'fpsDrop' | 'unstable';
export type HealthSeverity = 'high' | 'medium';
export type TicketStatus = 'open' | 'inWork' | 'resolved';

/** One hour of one PC: averages of the samples that fell in it. */
export interface HealthBucket {
  /** Hour start, epoch ms. */
  h: number;
  cpu: number;
  gpu: number;
  /** Average FPS while a game ran; `null` when none did. */
  fps: number | null;
  /** Share of samples with a session on the PC, 0–1. */
  busy: number;
  n: number;
}

export interface HealthIssue {
  kind: HealthKind;
  severity: HealthSeverity;
  params: Record<string, number>;
}

export interface HealthTicket {
  id: string;
  pcId: string;
  pcName: string;
  kind: HealthKind;
  severity: HealthSeverity;
  params: Record<string, number>;
  status: TicketStatus;
  openedAt: string;
  updatedAt: string;
  resolvedAt: string | null;
  note: string;
  /** Whether the system took the PC out of service for this ticket. */
  autoMaintenance: boolean;
}

export interface HealthSettings {
  cpuHotC: number;
  gpuHotC: number;
  /** Rise of the day average over the week before, °C. */
  trendC: number;
  /** FPS drop against the PC's own week, %. */
  fpsDropPct: number;
  /** Drop-outs (online → offline) in 24 h that make a PC "unstable". */
  offlinePerDay: number;
  /** Put a PC with a serious problem into maintenance as soon as it is free. */
  autoMaintenance: boolean;
}

interface HealthState {
  buckets: Record<string, HealthBucket[]>;
  offline: Record<string, string[]>;
  tickets: HealthTicket[];
  settings: HealthSettings;
  backfilled: boolean;
}

export const DEFAULT_HEALTH: HealthSettings = {
  cpuHotC: 90,
  gpuHotC: 85,
  trendC: 10,
  fpsDropPct: 30,
  offlinePerDay: 3,
  autoMaintenance: false,
};

const HOUR = 3_600_000;
const WEEK_HOURS = 7 * 24;
const LIVE_WINDOW_MS = 15 * 60_000;
const SIM_EVERY_MS = 30_000;
/** Real telemetry newer than this wins over the simulator. */
const REAL_FRESH_MS = 2 * 60_000;

type WithHealth = typeof db & { health?: HealthState };

export function health(): HealthState {
  const store = db as WithHealth;
  if (!store.health) {
    store.health = { buckets: {}, offline: {}, tickets: [], settings: { ...DEFAULT_HEALTH }, backfilled: false };
    markDirty();
  }
  store.health.settings = { ...DEFAULT_HEALTH, ...store.health.settings };
  return store.health;
}

// ---------------------------------------------------------------------------------------------------------------------
// Ingest
// ---------------------------------------------------------------------------------------------------------------------

/** Folds telemetry samples into the PC's hourly buckets (keeps one week). */
export function ingest(pc: PcRecord, samples: readonly PcMetrics[]): void {
  const hs = health();
  const list = (hs.buckets[pc.id] ??= []);
  const busy = openSessionForPc(pc.id) !== undefined ? 1 : 0;
  for (const s of samples) {
    const at = Date.parse(s.at);
    if (!Number.isFinite(at)) continue;
    const h = Math.floor(at / HOUR) * HOUR;
    let b = list.find((x) => x.h === h);
    if (!b) {
      b = { h, cpu: 0, gpu: 0, fps: null, busy: 0, n: 0 };
      list.push(b);
      list.sort((a, c) => a.h - c.h);
    }
    const n = b.n + 1;
    b.cpu = (b.cpu * b.n + (s.temps?.cpu ?? 0)) / n;
    b.gpu = (b.gpu * b.n + (s.temps?.gpu ?? 0)) / n;
    if (typeof s.fps === 'number' && s.fps > 0) {
      b.fps = b.fps === null ? s.fps : (b.fps * b.n + s.fps) / n;
    }
    b.busy = (b.busy * b.n + busy) / n;
    b.n = n;
  }
  const cutoff = Date.now() - WEEK_HOURS * HOUR;
  hs.buckets[pc.id] = list.filter((b) => b.h >= cutoff);
  markDirty();
}

// ---------------------------------------------------------------------------------------------------------------------
// Diagnosis
// ---------------------------------------------------------------------------------------------------------------------

const avg = (xs: number[]): number | null => (xs.length ? xs.reduce((a, b) => a + b, 0) / xs.length : null);
const round = (x: number | null): number | null => (x === null ? null : Math.round(x));

export interface PcHealth {
  score: number;
  live: { cpu: number | null; gpu: number | null; fps: number | null };
  baseline: { cpu: number | null; gpu: number | null; fps: number | null };
  /** Last 24 hours, oldest first; `null` for an hour with no data. */
  hourly: { cpu: (number | null)[]; gpu: (number | null)[]; fps: (number | null)[] };
  issues: HealthIssue[];
}

export function diagnose(pc: PcRecord, nowMs = Date.now()): PcHealth {
  const hs = health();
  const set = hs.settings;
  const buckets = hs.buckets[pc.id] ?? [];
  const recent = buckets.filter((b) => b.h >= nowMs - 24 * HOUR);
  const week = buckets.filter((b) => b.h < nowMs - 24 * HOUR);
  const liveSamples = pc.metrics.filter((m) => Date.parse(m.at) >= nowMs - LIVE_WINDOW_MS);
  const lastBucket = buckets.at(-1);

  const live = {
    cpu: round(avg(liveSamples.map((m) => m.temps.cpu)) ?? lastBucket?.cpu ?? null),
    gpu: round(avg(liveSamples.map((m) => m.temps.gpu)) ?? lastBucket?.gpu ?? null),
    fps: round(avg(liveSamples.flatMap((m) => (typeof m.fps === 'number' && m.fps > 0 ? [m.fps] : [])))),
  };
  const fpsOf = (bs: HealthBucket[]): number | null =>
    avg(bs.flatMap((b) => (b.fps !== null && b.busy >= 0.5 ? [b.fps] : [])));
  const baseline = {
    cpu: round(avg(week.map((b) => b.cpu))),
    gpu: round(avg(week.map((b) => b.gpu))),
    fps: round(fpsOf(week)),
  };
  const day = { cpu: avg(recent.map((b) => b.cpu)), gpu: avg(recent.map((b) => b.gpu)), fps: fpsOf(recent) };

  const issues: HealthIssue[] = [];
  const hot = (kind: 'cpuHot' | 'gpuHot', value: number | null, limit: number): void => {
    if (value === null || pc.status === 'offline') return;
    if (value >= limit) issues.push({ kind, severity: 'high', params: { temp: value, limit } });
    else if (value >= limit - 5) issues.push({ kind, severity: 'medium', params: { temp: value, limit } });
  };
  hot('cpuHot', live.cpu, set.cpuHotC);
  hot('gpuHot', live.gpu, set.gpuHotC);
  const trend = (kind: 'cpuTrend' | 'gpuTrend', today: number | null, before: number | null): void => {
    if (today === null || before === null) return;
    const rise = Math.round(today - before);
    if (rise >= set.trendC)
      issues.push({ kind, severity: 'medium', params: { rise, today: Math.round(today), before } });
  };
  trend('cpuTrend', day.cpu, baseline.cpu);
  trend('gpuTrend', day.gpu, baseline.gpu);
  if (day.fps !== null && baseline.fps !== null && baseline.fps > 0) {
    const drop = Math.round((1 - day.fps / baseline.fps) * 100);
    if (drop >= set.fpsDropPct) {
      issues.push({
        kind: 'fpsDrop',
        severity: 'medium',
        params: { drop, today: Math.round(day.fps), before: baseline.fps },
      });
    }
  }
  const drops = (hs.offline[pc.id] ?? []).filter((at) => Date.parse(at) >= nowMs - 24 * HOUR).length;
  if (drops >= set.offlinePerDay) issues.push({ kind: 'unstable', severity: 'medium', params: { drops } });

  const hourly = { cpu: [] as (number | null)[], gpu: [] as (number | null)[], fps: [] as (number | null)[] };
  const start = Math.floor(nowMs / HOUR) * HOUR - 23 * HOUR;
  for (let i = 0; i < 24; i += 1) {
    const b = buckets.find((x) => x.h === start + i * HOUR);
    hourly.cpu.push(b ? Math.round(b.cpu) : null);
    hourly.gpu.push(b ? Math.round(b.gpu) : null);
    hourly.fps.push(b && b.fps !== null ? Math.round(b.fps) : null);
  }

  const penalty = issues.reduce((s, i) => s + (i.severity === 'high' ? 35 : 15), 0);
  return { score: Math.max(0, 100 - penalty), live, baseline, hourly, issues };
}

// ---------------------------------------------------------------------------------------------------------------------
// Tickets
// ---------------------------------------------------------------------------------------------------------------------

const TITLE: Record<HealthKind, string> = {
  cpuHot: 'перегрев процессора',
  gpuHot: 'перегрев видеокарты',
  cpuTrend: 'процессор греется сильнее обычного',
  gpuTrend: 'видеокарта греется сильнее обычного',
  fpsDrop: 'упал FPS',
  unstable: 'часто пропадает из сети',
};

/** A resolved ticket is not reopened for the same problem within this time (the fix gets a chance to show). */
const REOPEN_AFTER_MS = 6 * HOUR;

function openTicketsFor(pc: PcRecord, issues: HealthIssue[]): void {
  const hs = health();
  for (const issue of issues) {
    const existing = hs.tickets.find((t) => t.pcId === pc.id && t.kind === issue.kind && t.status !== 'resolved');
    if (existing) {
      // Escalate in place when a medium problem became serious.
      if (existing.severity === 'medium' && issue.severity === 'high') {
        existing.severity = 'high';
        existing.params = issue.params;
        existing.updatedAt = now();
        maybeTakeOut(pc, existing);
        markDirty();
      }
      continue;
    }
    const recentlyFixed = hs.tickets.some(
      (t) =>
        t.pcId === pc.id &&
        t.kind === issue.kind &&
        t.status === 'resolved' &&
        t.resolvedAt !== null &&
        Date.now() - Date.parse(t.resolvedAt) < REOPEN_AFTER_MS,
    );
    if (recentlyFixed) continue;
    const ticket: HealthTicket = {
      id: uuid(),
      pcId: pc.id,
      pcName: pc.name,
      kind: issue.kind,
      severity: issue.severity,
      params: issue.params,
      status: 'open',
      openedAt: now(),
      updatedAt: now(),
      resolvedAt: null,
      note: '',
      autoMaintenance: false,
    };
    hs.tickets.unshift(ticket);
    hs.tickets = hs.tickets.slice(0, 500);
    markDirty();
    emit('hardware', `${pc.name}: ${TITLE[issue.kind]}${issue.severity === 'high' ? ' — серьёзно' : ''}`, {
      pcId: pc.id,
      ticketId: ticket.id,
      kind: issue.kind,
    });
    maybeTakeOut(pc, ticket);
  }
}

function maybeTakeOut(pc: PcRecord, ticket: HealthTicket): void {
  if (!health().settings.autoMaintenance || ticket.severity !== 'high') return;
  if (pc.status !== 'free' || openSessionForPc(pc.id)) return;
  pc.status = 'maintenance';
  ticket.autoMaintenance = true;
  markDirty();
  broadcast('pcStatusChanged', { pcId: pc.id, status: 'maintenance' });
}

/** Staff moves a ticket along; resolving a ticket that took the PC out of service puts it back. */
export function updateTicket(id: string, status: TicketStatus, note: string | null): HealthTicket | null {
  const t = health().tickets.find((x) => x.id === id);
  if (!t) return null;
  t.status = status;
  if (note !== null) t.note = note;
  t.updatedAt = now();
  t.resolvedAt = status === 'resolved' ? now() : null;
  if (status === 'resolved' && t.autoMaintenance) {
    const pc = db.pcs.find((p) => p.id === t.pcId);
    const stillOpen = health().tickets.some((x) => x.pcId === t.pcId && x.autoMaintenance && x.status !== 'resolved');
    if (pc && pc.status === 'maintenance' && !stillOpen) {
      pc.status = 'free';
      broadcast('pcStatusChanged', { pcId: pc.id, status: 'free' });
    }
  }
  markDirty();
  return t;
}

/** PCs with an open (or in-work) ticket and its worst severity — for the marks on the hall map. */
export function openTicketMarks(): { pcId: string; severity: HealthSeverity }[] {
  const worst = new Map<string, HealthSeverity>();
  for (const t of health().tickets) {
    if (t.status === 'resolved') continue;
    if (worst.get(t.pcId) !== 'high') worst.set(t.pcId, t.severity);
  }
  return [...worst].map(([pcId, severity]) => ({ pcId, severity }));
}

// ---------------------------------------------------------------------------------------------------------------------
// Tick: drop-outs, the simulator, diagnosis
// ---------------------------------------------------------------------------------------------------------------------

const lastStatus = new Map<string, string>();
let lastSim = 0;
/** PCs whose telemetry the simulator plays; a real Agent's upload takes a PC off this list. */
const simulatedPcs = new Set<string>();

/** A small deterministic noise in [-1, 1] so the demo looks the same on every start. */
function noise(seed: number): number {
  const x = Math.sin(seed * 12.9898) * 43758.5453;
  return (x - Math.floor(x)) * 2 - 1;
}

interface Profile {
  cpu: number;
  gpu: number;
  fps: number;
  /** °C added per day over the last three days (a machine slowly clogging with dust). */
  cpuCreep?: number;
  /** Constant GPU excess now (a failing fan). */
  gpuNow?: number;
  /** Share of the FPS lost in the last day. */
  fpsLoss?: number;
}

function profile(pc: PcRecord): Profile {
  const n = pc.number;
  const base: Profile = {
    cpu: 54 + (n % 5) * 2,
    gpu: 56 + (n % 4) * 2,
    fps: pc.zone === 'VIP' ? 280 : 220 + (n % 3) * 10,
  };
  if (pc.name === 'PC-07') return { ...base, cpuCreep: 9 };
  if (pc.name === 'PC-15') return { ...base, gpuNow: 22 };
  if (pc.name === 'PC-19') return { ...base, fpsLoss: 0.38 };
  return base;
}

function simulated(pc: PcRecord, at: number, busy: boolean): PcMetrics {
  const p = profile(pc);
  const daysAgo = (Date.now() - at) / (24 * HOUR);
  const load = busy ? 1 : 0.15;
  let cpu = p.cpu + load * 12 + noise(at / 60_000 + pc.number) * 2;
  let gpu = p.gpu + load * 14 + noise(at / 60_000 + pc.number * 7) * 2;
  let fps = busy ? p.fps + noise(at / 60_000 + pc.number * 3) * 12 : null;
  if (p.cpuCreep && daysAgo < 3) cpu += p.cpuCreep * (3 - daysAgo);
  if (p.gpuNow && daysAgo < 1) gpu += p.gpuNow;
  if (p.fpsLoss && fps !== null && daysAgo < 1) fps *= 1 - p.fpsLoss;
  return {
    cpuPct: Math.round(load * 70 + 5),
    gpuPct: Math.round(load * 85 + 3),
    ramUsedMb: busy ? 11_000 : 4_000,
    temps: { cpu: Math.round(cpu), gpu: Math.round(gpu) },
    fps: fps === null ? null : Math.round(fps),
    netMbps: { down: busy ? 12 : 0.4, up: busy ? 3 : 0.1 },
    uptimeSec: 3600,
    at: new Date(at).toISOString(),
  };
}

/** One week of hourly history for every PC, so trends exist from the first start of the demo. */
function backfill(): void {
  const hs = health();
  if (hs.backfilled) return;
  const nowH = Math.floor(Date.now() / HOUR) * HOUR;
  for (const pc of db.pcs) {
    const list: HealthBucket[] = [];
    for (let i = WEEK_HOURS; i >= 1; i -= 1) {
      const h = nowH - i * HOUR;
      const hour = new Date(h).getHours();
      // Evenings are busy, mornings quiet.
      const busy = hour >= 16 || hour < 1 ? 0.8 : hour >= 11 ? 0.45 : 0.1;
      const on = simulated(pc, h + 30 * 60_000, true);
      const off = simulated(pc, h + 30 * 60_000, false);
      list.push({
        h,
        cpu: on.temps.cpu * busy + off.temps.cpu * (1 - busy),
        gpu: on.temps.gpu * busy + off.temps.gpu * (1 - busy),
        fps: busy >= 0.5 ? (on.fps ?? null) : null,
        busy,
        n: 120,
      });
    }
    hs.buckets[pc.id] = list;
  }
  // PC-22 keeps dropping off the network today.
  const flaky = db.pcs.find((p) => p.name === 'PC-22');
  if (flaky) hs.offline[flaky.id] = [1, 3, 6, 9].map((hAgo) => new Date(Date.now() - hAgo * HOUR).toISOString());
  hs.backfilled = true;
  markDirty();
}

export function tickHealth(t: number): void {
  const hs = health();
  backfill();

  for (const pc of db.pcs) {
    const prev = lastStatus.get(pc.id);
    if (prev && prev !== 'offline' && pc.status === 'offline') {
      (hs.offline[pc.id] ??= []).push(now());
      hs.offline[pc.id] = (hs.offline[pc.id] ?? []).slice(-50);
      markDirty();
    }
    lastStatus.set(pc.id, pc.status);
  }

  if (t - lastSim < SIM_EVERY_MS) return;
  lastSim = t;
  for (const pc of db.pcs) {
    // An offline PC sends nothing, but its drop-outs are still worth a ticket.
    if (pc.status === 'offline') {
      const d = diagnose(pc, t);
      if (d.issues.length) openTicketsFor(pc, d.issues);
      continue;
    }
    const last = pc.metrics.at(-1);
    const realFresh = !simulatedPcs.has(pc.id) && last !== undefined && t - Date.parse(last.at) < REAL_FRESH_MS;
    if (!realFresh) {
      const sample = simulated(pc, t, openSessionForPc(pc.id) !== undefined);
      pc.metrics = [...pc.metrics, sample].slice(-120);
      simulatedPcs.add(pc.id);
      ingest(pc, [sample]);
    }
    const d = diagnose(pc, t);
    if (d.issues.length) openTicketsFor(pc, d.issues);
  }
}

export function realTelemetry(pc: PcRecord, samples: readonly PcMetrics[]): void {
  simulatedPcs.delete(pc.id);
  ingest(pc, samples);
}
