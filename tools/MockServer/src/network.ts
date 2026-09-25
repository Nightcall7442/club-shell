/**
 * A network of clubs under one owner: one console, one player base, one balance per player that works in every club.
 * The club this server runs is `local` and reports its real numbers (transactions, sessions, PCs, repair tickets,
 * cashier signals, shift). The other clubs of the demo network are played by a deterministic simulator — evening peaks,
 * weekend crowds, their own sizes — so the owner's view has something to compare; a club added from the console starts
 * empty until its Agents connect.
 */
import { club, openShift } from './club.js';
import { flagsFor } from './control.js';
import { health } from './health.js';
import { db, markDirty, now, openSessionForPc, uuid } from './db.js';

export interface NetworkClub {
  id: string;
  name: string;
  city: string;
  address: string;
  /** Seats; the local club's comes from its PCs. */
  pcs: number;
  local: boolean;
  /** Played by the demo simulator (the seeded neighbours), not by real Agents. */
  simulated: boolean;
  createdAt: string;
}

interface NetworkState {
  name: string;
  clubs: NetworkClub[];
}

type WithNetwork = typeof db & { network?: NetworkState };

export function network(): NetworkState {
  const store = db as WithNetwork;
  if (!store.network) {
    const t = now();
    store.network = {
      name: 'CyberArena',
      clubs: [
        {
          id: 'local',
          name: '',
          city: 'Ташкент',
          address: 'ул. Амира Темура, 15',
          pcs: 0,
          local: true,
          simulated: false,
          createdAt: t,
        },
        {
          id: 'chilanzar',
          name: 'CyberArena Чиланзар',
          city: 'Ташкент',
          address: 'Чиланзар, 9-й квартал',
          pcs: 32,
          local: false,
          simulated: true,
          createdAt: t,
        },
        {
          id: 'samarkand',
          name: 'CyberArena Самарканд',
          city: 'Самарканд',
          address: 'ул. Регистан, 4',
          pcs: 18,
          local: false,
          simulated: true,
          createdAt: t,
        },
      ],
    };
    markDirty();
  }
  return store.network;
}

export function addClub(input: { name: string; city: string; address: string; pcs: number }): NetworkClub {
  const c: NetworkClub = { id: uuid(), ...input, local: false, simulated: false, createdAt: now() };
  network().clubs.push(c);
  markDirty();
  return c;
}

// ---------------------------------------------------------------------------------------------------------------------
// Numbers
// ---------------------------------------------------------------------------------------------------------------------

export interface ClubReport {
  id: string;
  name: string;
  city: string;
  address: string;
  local: boolean;
  simulated: boolean;
  pcs: number;
  busyNow: number;
  /** Revenue (sessions + shop), minor units. */
  revenueToday: number;
  revenue: number;
  sessions: number;
  /** Revenue per session, minor units. */
  avgCheck: number;
  /** Revenue per day of the period, oldest first. */
  byDay: number[];
  /** Share of seats busy per hour today, 0–100. */
  hourly: number[];
  repairs: number;
  signals: number;
  shift: { staffName: string; since: string } | null;
}

const DAY = 86_400_000;

function noise(seed: number): number {
  const x = Math.sin(seed * 12.9898) * 43758.5453;
  return x - Math.floor(x);
}

function hash(s: string): number {
  let h = 0;
  for (const ch of s) h = (h * 31 + ch.charCodeAt(0)) | 0;
  return Math.abs(h);
}

/** Share of seats busy at `hour` (0–23) of a day, for a simulated club: quiet mornings, full evenings, busier weekends. */
function curve(clubId: string, dayIndex: number, hour: number, weekday: number): number {
  const base = hour < 9 ? 0.08 : hour < 13 ? 0.22 : hour < 17 ? 0.45 : hour < 23 ? 0.82 : 0.4;
  const weekend = weekday === 0 || weekday === 6 ? 1.15 : 1;
  const wobble = 0.85 + noise(hash(clubId) + dayIndex * 24 + hour) * 0.3;
  return Math.min(1, base * weekend * wobble);
}

const RATE_PER_HOUR = 1_200_000;
const SHOP_SHARE = 0.18;

function simulated(c: NetworkClub, days: number, nowMs: number): ClubReport {
  const start = new Date(nowMs);
  start.setHours(0, 0, 0, 0);
  const byDay: number[] = [];
  let sessions = 0;
  for (let i = days - 1; i >= 0; i -= 1) {
    const day = new Date(start.getTime() - i * DAY);
    const dayIndex = Math.floor(day.getTime() / DAY);
    const lastHour = i === 0 ? new Date(nowMs).getHours() : 23;
    let seatHours = 0;
    for (let h = 0; h <= lastHour; h += 1) seatHours += curve(c.id, dayIndex, h, day.getDay()) * c.pcs;
    byDay.push(Math.round((seatHours * RATE_PER_HOUR * (1 + SHOP_SHARE)) / 100) * 100);
    sessions += Math.round(seatHours / 2.4);
  }
  const todayIndex = Math.floor(start.getTime() / DAY);
  const nowHour = new Date(nowMs).getHours();
  const hourly = Array.from({ length: 24 }, (_, h) =>
    h > nowHour ? 0 : Math.round(curve(c.id, todayIndex, h, start.getDay()) * 100),
  );
  const revenue = byDay.reduce((a, b) => a + b, 0);
  return {
    ...base(c),
    pcs: c.pcs,
    busyNow: Math.round(((hourly[nowHour] ?? 0) / 100) * c.pcs),
    revenueToday: byDay.at(-1) ?? 0,
    revenue,
    sessions,
    avgCheck: sessions ? Math.round(revenue / sessions / 100) * 100 : 0,
    byDay,
    hourly,
    repairs: Math.round(noise(hash(c.id)) * 3),
    signals: Math.round(noise(hash(c.id) + 7) * 2),
    shift: {
      staffName: c.id === 'chilanzar' ? 'Кассир Шахзод' : 'Кассир Нигора',
      since: new Date(start.getTime() + 9 * 3_600_000).toISOString(),
    },
  };
}

function base(c: NetworkClub): Pick<ClubReport, 'id' | 'name' | 'city' | 'address' | 'local' | 'simulated'> {
  return {
    id: c.id,
    name: c.local ? club().branding.clubName : c.name,
    city: c.city,
    address: c.address,
    local: c.local,
    simulated: c.simulated,
  };
}

function local(c: NetworkClub, days: number, nowMs: number): ClubReport {
  const start = new Date(nowMs);
  start.setHours(0, 0, 0, 0);
  const from = new Date(start.getTime() - (days - 1) * DAY);
  const byDay = Array.from({ length: days }, () => 0);
  for (const tx of db.transactions) {
    if (tx.type !== 'charge' && tx.type !== 'purchase') continue;
    const at = Date.parse(tx.createdAt);
    if (at < from.getTime()) continue;
    const i = Math.floor((at - from.getTime()) / DAY);
    if (i >= 0 && i < days) byDay[i] = (byDay[i] ?? 0) + -tx.amount.amount;
  }
  const sessions = db.sessions.filter((s) => Date.parse(s.startedAt) >= from.getTime()).length;
  const hourly = Array.from({ length: 24 }, () => 0);
  const total = db.pcs.length || 1;
  for (let h = 0; h <= new Date(nowMs).getHours(); h += 1) {
    const t = start.getTime() + h * 3_600_000 + 30 * 60_000;
    const busy = db.sessions.filter((s) => {
      const a = Date.parse(s.startedAt);
      const b = s.endedAt ? Date.parse(s.endedAt) : nowMs;
      return a <= t && t < b;
    }).length;
    hourly[h] = Math.round((busy / total) * 100);
  }
  const revenue = byDay.reduce((a, b) => a + b, 0);
  const shift = openShift();
  const periodAudit = club().audit.filter((e) => Date.parse(e.at) >= from.getTime());
  return {
    ...base(c),
    pcs: db.pcs.length,
    // Seats with a session running (active, locked on a break, ending) — people actually at the PCs.
    busyNow: db.pcs.filter((p) => openSessionForPc(p.id) !== undefined).length,
    revenueToday: byDay.at(-1) ?? 0,
    revenue,
    sessions,
    avgCheck: sessions ? Math.round(revenue / sessions / 100) * 100 : 0,
    byDay,
    hourly,
    repairs: health().tickets.filter((t) => t.status !== 'resolved').length,
    signals: flagsFor(periodAudit).filter((f) => f.severity === 'high').length,
    shift: shift ? { staffName: shift.staffName, since: shift.openedAt } : null,
  };
}

function empty(c: NetworkClub, days: number): ClubReport {
  return {
    ...base(c),
    pcs: c.pcs,
    busyNow: 0,
    revenueToday: 0,
    revenue: 0,
    sessions: 0,
    avgCheck: 0,
    byDay: Array.from({ length: days }, () => 0),
    hourly: Array.from({ length: 24 }, () => 0),
    repairs: 0,
    signals: 0,
    shift: null,
  };
}

export function networkReport(
  days: number,
  nowMs = Date.now(),
): {
  name: string;
  days: number;
  clubs: ClubReport[];
  totals: { clubs: number; pcs: number; busyNow: number; revenue: number; revenueToday: number; sessions: number };
  players: { total: number; balance: number; multiClub: number };
} {
  const n = network();
  const clubs = n.clubs.map((c) =>
    c.local ? local(c, days, nowMs) : c.simulated ? simulated(c, days, nowMs) : empty(c, days),
  );
  const members = db.users.filter((u) => !u.transient && u.role !== 'admin' && u.role !== 'guest');
  const sum = (k: 'pcs' | 'busyNow' | 'revenue' | 'revenueToday' | 'sessions'): number =>
    clubs.reduce((a, c) => a + c[k], 0);
  return {
    name: n.name,
    days,
    clubs,
    totals: {
      clubs: clubs.length,
      pcs: sum('pcs'),
      busyNow: sum('busyNow'),
      revenue: sum('revenue'),
      revenueToday: sum('revenueToday'),
      sessions: sum('sessions'),
    },
    players: {
      total: members.length,
      // One wallet per player, valid in every club of the network.
      balance: members.reduce((a, u) => a + u.balance.amount, 0),
      // Demo: every third member has also played in a neighbouring club.
      multiClub: n.clubs.length > 1 ? members.filter((_, i) => i % 3 === 0).length : 0,
    },
  };
}
