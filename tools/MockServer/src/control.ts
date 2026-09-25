/**
 * Cashier control: every money-moving or client-changing action at the counter is journalled with who did it and in
 * which shift ({@link record}), and a small rule set reads that journal for the patterns behind most counter theft —
 * cash short at close, sessions opened and refunded minutes later, big discounts handed to friends, the same client
 * topped up again and again, money taken with no shift open. The owner's "Контроль" page lists the flags per cashier;
 * the serious ones also go out as the `suspicious` event (Telegram / webhooks) the moment they happen.
 */
import { club, emit, openShift, type AuditAction, type AuditEntry, type StaffRecord } from './club.js';
import { markDirty, now, uuid } from './db.js';

const MAX_AUDIT = 5000;

export type FlagKind = 'shortfall' | 'earlyEnds' | 'earlyEnd' | 'discount' | 'sameClient' | 'noShift' | 'bigCash';
export type Severity = 'high' | 'medium' | 'low';

export interface Flag {
  id: string;
  kind: FlagKind;
  severity: Severity;
  at: string;
  staffId: string;
  staffName: string;
  shiftId: string | null;
  userId: string | null;
  pcId: string | null;
  /** Money involved, minor units. */
  amount: number;
  /** Facts for the console to phrase the flag in the UI language. */
  params: Record<string, string | number>;
}

export interface StaffSummary {
  staffId: string;
  staffName: string;
  operations: number;
  topUps: number;
  refunds: number;
  earlyEnds: number;
  discounts: number;
  shortfall: number;
  flags: Record<Severity, number>;
}

const MONEY_ACTIONS: ReadonlySet<AuditAction> = new Set([
  'topUp',
  'sessionOpen',
  'sessionExtend',
  'sessionEnd',
  'promoRedeem',
]);

const SEVERITY_RANK: Record<Severity, number> = { high: 0, medium: 1, low: 2 };

function money(minor: number): string {
  return `${new Intl.NumberFormat('ru-RU').format(Math.round(Math.abs(minor) / 100))} сум`;
}

/** Journals one staff action and raises the live alert when it completes a serious pattern. */
export function record(
  staff: StaffRecord,
  action: AuditAction,
  fields: {
    userId?: string | null;
    pcId?: string | null;
    amount?: number;
    detail?: string;
    meta?: AuditEntry['meta'];
  } = {},
): AuditEntry {
  const c = club();
  const entry: AuditEntry = {
    id: uuid(),
    at: now(),
    staffId: staff.id,
    staffName: staff.name,
    shiftId: openShift()?.id ?? null,
    action,
    userId: fields.userId ?? null,
    pcId: fields.pcId ?? null,
    amount: fields.amount ?? 0,
    detail: fields.detail ?? '',
    meta: fields.meta ?? {},
  };
  c.audit.unshift(entry);
  if (c.audit.length > MAX_AUDIT) c.audit.length = MAX_AUDIT;
  markDirty();
  alertOn(entry);
  return entry;
}

function flag(e: AuditEntry, kind: FlagKind, severity: Severity, params: Flag['params'], amount = e.amount): Flag {
  return {
    id: `${kind}:${e.id}`,
    kind,
    severity,
    at: e.at,
    staffId: e.staffId,
    staffName: e.staffName,
    shiftId: e.shiftId,
    userId: e.userId,
    pcId: e.pcId,
    amount,
    params,
  };
}

function isEarlyEnd(e: AuditEntry): boolean {
  const minutes = Number(e.meta['sessionMinutes'] ?? Number.POSITIVE_INFINITY);
  return e.action === 'sessionEnd' && e.amount > 0 && minutes < club().control.earlyEndMinutes;
}

/** Flags for a slice of the journal (newest first in, most serious and newest first out). */
export function flagsFor(entries: readonly AuditEntry[]): Flag[] {
  const c = club();
  const ctl = c.control;
  const out: Flag[] = [];
  const earlyByShift = new Map<string, AuditEntry[]>();
  const topUpsByClient = new Map<string, AuditEntry[]>();

  for (const e of entries) {
    if (e.action === 'shiftClose') {
      const diff = Number(e.meta['diff'] ?? 0);
      if (diff < -ctl.shortfallFrom) out.push(flag(e, 'shortfall', 'high', { short: -diff }, -diff));
    }
    if (MONEY_ACTIONS.has(e.action) && e.shiftId === null && (e.amount > 0 || e.action !== 'sessionEnd')) {
      out.push(flag(e, 'noShift', 'medium', { action: e.action, detail: e.detail }));
    }
    if (isEarlyEnd(e)) {
      out.push(flag(e, 'earlyEnd', 'low', { minutes: Number(e.meta['sessionMinutes']), detail: e.detail }));
      const key = `${e.staffId}|${e.shiftId ?? 'none'}`;
      earlyByShift.set(key, [...(earlyByShift.get(key) ?? []), e]);
    }
    if (e.action === 'clientGroup' && Number(e.meta['discountPct'] ?? 0) >= ctl.discountPct) {
      out.push(
        flag(e, 'discount', 'medium', {
          pct: Number(e.meta['discountPct']),
          group: String(e.meta['groupName'] ?? ''),
          detail: String(e.meta['clientName'] ?? ''),
        }),
      );
    }
    if (e.action === 'topUp') {
      if (e.meta['method'] === 'cash' && e.amount >= c.notifications.bigTopupAt) {
        out.push(flag(e, 'bigCash', 'low', { detail: e.detail }));
      }
      if (e.userId) {
        const key = `${e.staffId}|${e.shiftId ?? 'none'}|${e.userId}`;
        topUpsByClient.set(key, [...(topUpsByClient.get(key) ?? []), e]);
      }
    }
  }

  for (const list of earlyByShift.values()) {
    if (list.length >= ctl.earlyEndsPerShift) {
      const newest = list[0] as AuditEntry;
      const total = list.reduce((s, e) => s + e.amount, 0);
      out.push(flag(newest, 'earlyEnds', 'high', { count: list.length }, total));
    }
  }
  for (const list of topUpsByClient.values()) {
    if (list.length >= ctl.sameClientTopups) {
      const newest = list[0] as AuditEntry;
      const total = list.reduce((s, e) => s + e.amount, 0);
      out.push(flag(newest, 'sameClient', 'medium', { count: list.length, detail: newest.detail }, total));
    }
  }

  return out.sort((a, b) => SEVERITY_RANK[a.severity] - SEVERITY_RANK[b.severity] || b.at.localeCompare(a.at));
}

/** Per-cashier totals over a slice of the journal plus its flags. */
export function summaries(entries: readonly AuditEntry[], flags: readonly Flag[]): StaffSummary[] {
  const byStaff = new Map<string, StaffSummary>();
  const of = (id: string, name: string): StaffSummary => {
    let s = byStaff.get(id);
    if (!s) {
      s = {
        staffId: id,
        staffName: name,
        operations: 0,
        topUps: 0,
        refunds: 0,
        earlyEnds: 0,
        discounts: 0,
        shortfall: 0,
        flags: { high: 0, medium: 0, low: 0 },
      };
      byStaff.set(id, s);
    }
    return s;
  };
  for (const e of entries) {
    const s = of(e.staffId, e.staffName);
    s.operations += 1;
    if (e.action === 'topUp') s.topUps += e.amount;
    if (e.action === 'sessionEnd') s.refunds += e.amount;
    if (isEarlyEnd(e)) s.earlyEnds += 1;
    if (e.action === 'clientGroup' && Number(e.meta['discountPct'] ?? 0) >= club().control.discountPct)
      s.discounts += 1;
    if (e.action === 'shiftClose') s.shortfall += Math.max(0, -Number(e.meta['diff'] ?? 0));
  }
  for (const f of flags) of(f.staffId, f.staffName).flags[f.severity] += 1;
  return [...byStaff.values()].sort(
    (a, b) => b.flags.high - a.flags.high || b.flags.medium - a.flags.medium || b.operations - a.operations,
  );
}

/** Live alert for the patterns that should not wait for the owner to open the page. */
function alertOn(e: AuditEntry): void {
  const ctl = club().control;
  if (e.action === 'shiftClose') {
    const diff = Number(e.meta['diff'] ?? 0);
    if (diff < -ctl.shortfallFrom) {
      emit('suspicious', `${e.staffName}: недостача в кассе ${money(diff)} при закрытии смены`, { entryId: e.id });
    }
    return;
  }
  if (isEarlyEnd(e)) {
    const count = club().audit.filter(
      (x) => x.staffId === e.staffId && x.shiftId === e.shiftId && isEarlyEnd(x),
    ).length;
    // Exactly at the threshold, so one bad shift sends one message rather than one per further refund.
    if (count === ctl.earlyEndsPerShift) {
      emit('suspicious', `${e.staffName}: ${count} сеанса закрыты с возвратом в первые ${ctl.earlyEndMinutes} мин`, {
        entryId: e.id,
      });
    }
  }
}
