/**
 * Club owner / cashier console API (`/api/v1/admin/*`, next to the counter routes in `admin.ts`): staff sign-in by
 * PIN with roles, shifts with X / Z reports, tariffs and pricing, client groups and profiles, promo codes, the hall map
 * editor, stock, the game catalogue, branding and player features, banners, automation rules, webhooks and reports.
 * Owner-only sections are guarded by {@link requireStaff}. Telegram is banned in the product: no route reads or
 * returns a Telegram field.
 */
import type { FastifyInstance, FastifyRequest } from 'fastify';
import { Weekday, type ShellFeatures, type Tariff, type TariffTimeWindow } from '@clubshell/contracts';

const WEEKDAYS = Object.values(Weekday) as Weekday[];
import {
  applyTransaction,
  arr,
  balanceOf,
  body,
  bool,
  db,
  errors,
  findGame,
  findPc,
  findTariff,
  findUser,
  idempotent,
  int,
  isObject,
  markDirty,
  now,
  obj,
  openSessionForUser,
  optInt,
  optStr,
  publicPc,
  str,
  uuid,
  uzs,
  type PcRecord,
  type UserRecord,
} from '../db.js';
import {
  CLUB_EVENTS,
  DEFAULT_CONTROL,
  club,
  clubHooks,
  emit,
  loyaltyOf,
  openShift,
  profileOf,
  quote,
  spentOf,
  topupBonus,
  totalsSince,
  visitsOf,
  type AutomationRule,
  type DeviceKind,
  type StaffRecord,
  type StaffRole,
} from '../club.js';
import { broadcast, pushToUser } from '../ws.js';
import { flagsFor, record, summaries } from '../control.js';
import { addClub, network, networkReport } from '../network.js';
import { DEFAULT_HEALTH, diagnose, health, updateTicket, type TicketStatus } from '../health.js';

const LEGACY_TOKEN = process.env['MOCK_ADMIN_TOKEN'] ?? 'admin-dev-token';

function bearer(req: FastifyRequest): string {
  const authz = req.headers.authorization;
  return typeof authz === 'string' && authz.startsWith('Bearer ') ? authz.slice(7).trim() : '';
}

/** The signed-in staff member; the legacy static token acts as the owner. `role` limits the route to owners. */
export function requireStaff(req: FastifyRequest, role: StaffRole | 'any' = 'any'): StaffRecord {
  const token = bearer(req);
  const c = club();
  let staff: StaffRecord | undefined;
  // The legacy static token and the club's API key (integrations, `Authorization: Bearer ck_…`) act as the owner.
  if (token === LEGACY_TOKEN || (token !== '' && token === c.apiKey)) staff = c.staff.find((s) => s.role === 'owner');
  else {
    const id = c.staffTokens[token];
    staff = id ? c.staff.find((s) => s.id === id && s.active) : undefined;
  }
  if (!staff) throw errors.unauthorized('invalid');
  if (role === 'owner' && staff.role !== 'owner') throw errors.forbidden('ownerOnly');
  return staff;
}

function clientView(u: UserRecord): Record<string, unknown> {
  const p = profileOf(u.id);
  const level = loyaltyOf(u.id);
  return {
    id: u.id,
    username: u.username,
    displayName: u.displayName,
    role: u.role,
    balance: u.balance,
    bonus: u.bonus,
    groupId: p.groupId,
    note: p.note,
    blacklisted: p.blacklisted,
    phone: p.phone,
    birthYear: p.birthYear,
    cardId: u.cardId,
    spent: spentOf(u.id),
    visits: visitsOf(u.id),
    level: level.level,
    levelName: level.name,
  };
}

/** A client's sign-in password as the cashier set it: 4-64 characters. */
function passwordOf(b: Record<string, unknown>): string {
  const password = str(b, 'password', 64);
  if (password.length < 4) throw errors.validation('password', 'min');
  return password;
}

/** The card number to bind to `userId` (`null` unbinds); one card belongs to one client. */
function cardOf(b: Record<string, unknown>, userId: string | null): string | null {
  const card = optStr(b, 'cardId', 64)?.trim() || null;
  if (card && db.users.some((u) => u.id !== userId && u.cardId?.toLowerCase() === card.toLowerCase()))
    throw errors.validation('cardId', 'taken');
  return card;
}

/** One row of the counter's client search (`GET /admin/clients/lookup`). */
function lookupView(u: UserRecord): Record<string, unknown> {
  const phone = profileOf(u.id).phone.replace(/\D/g, '');
  const session = openSessionForUser(u.id);
  const pc = session ? findPc(session.pcId) : undefined;
  return {
    id: u.id,
    displayName: u.displayName,
    username: u.username,
    phoneTail: phone ? phone.slice(-4) : null,
    balance: u.balance,
    bonus: u.bonus,
    cardId: u.cardId,
    playing: pc ? { pcId: pc.id, pcName: pc.name } : null,
  };
}

/**
 * How well `u` matches the search (0 — not at all): the exact card first, then a login or name that starts with it, a
 * word of the name that does, any substring of the name or login, and last a run of the phone's digits.
 */
function lookupScore(u: UserRecord, q: string, digits: string | null): number {
  const name = u.displayName.toLowerCase();
  const login = u.username.toLowerCase();
  if (u.cardId && u.cardId.toLowerCase() === q) return 100;
  if (login === q) return 90;
  if (login.startsWith(q) || name.startsWith(q)) return 70;
  if (name.split(/\s+/).some((w) => w.startsWith(q))) return 60;
  if (login.includes(q) || name.includes(q)) return 50;
  if (digits && profileOf(u.id).phone.replace(/\D/g, '').includes(digits)) return 40;
  return 0;
}

function money(n: number): string {
  return `${Math.round(n / 100).toLocaleString('ru-RU')} сум`;
}

function windows(v: unknown): TariffTimeWindow[] {
  if (!Array.isArray(v)) return [];
  return v.filter(isObject).map((w) => ({
    days: Array.isArray(w['days'])
      ? (w['days'] as unknown[]).filter((d): d is Weekday => typeof d === 'string' && WEEKDAYS.includes(d as Weekday))
      : [],
    from: typeof w['from'] === 'string' ? w['from'] : '00:00',
    to: typeof w['to'] === 'string' ? w['to'] : '23:59',
  }));
}

function tariffFrom(b: Record<string, unknown>, id: string): Tariff {
  const isPackage = bool(b, 'isPackage');
  return {
    id,
    name: str(b, 'name', 64),
    pricePerHour: uzs(int(b, 'pricePerHour', 0, 1_000_000_000)),
    minMinutes: optInt(b, 'minMinutes', 5, 1440) ?? 30,
    maxMinutes: optInt(b, 'maxMinutes', 5, 10_080),
    zones: arr(b, 'zones', 20).filter((z): z is string => typeof z === 'string'),
    timeWindows: windows(b['timeWindows']),
    isPackage,
    packageMinutes: isPackage ? int(b, 'packageMinutes', 5, 10_080) : null,
    packagePrice: isPackage ? uzs(int(b, 'packagePrice', 0, 1_000_000_000)) : null,
  };
}

function bumpCatalog(): void {
  db.catalogVersion = `v${Date.now()}`;
  db.configVersion += 1;
  markDirty();
}

export function clubRoutes(app: FastifyInstance): void {
  // ------------------------------------------------------------------------------------------------ staff & auth
  app.post('/admin/login', async (req) => {
    const pin = str(body(req), 'pin', 12);
    const c = club();
    const staff = c.staff.find((s) => s.active && s.pin === pin);
    if (!staff) throw errors.unauthorized('invalidPin');
    const token = `st_${uuid().replace(/-/g, '')}`;
    c.staffTokens[token] = staff.id;
    markDirty();
    return { token, staff: { id: staff.id, name: staff.name, role: staff.role }, shift: openShift() };
  });

  /** Signs the staff member out: the token stops working at once. Always 200, so a repeated sign-out is harmless. */
  app.post('/admin/logout', async (req) => {
    const c = club();
    const token = bearer(req);
    if (token && c.staffTokens[token]) {
      delete c.staffTokens[token];
      markDirty();
    }
    return { ok: true };
  });

  app.get('/admin/me', async (req) => {
    const s = requireStaff(req);
    return { staff: { id: s.id, name: s.name, role: s.role }, shift: openShift() };
  });

  app.get('/admin/staff', async (req) => {
    requireStaff(req, 'owner');
    return { items: club().staff.map(({ pin: _pin, ...s }) => s) };
  });

  app.post('/admin/staff', async (req) => {
    requireStaff(req, 'owner');
    const b = body(req);
    const role = str(b, 'role', 16) === 'owner' ? 'owner' : 'cashier';
    const pin = str(b, 'pin', 12);
    if (!/^\d{4,8}$/.test(pin)) throw errors.validation('pin', 'digits4to8');
    if (club().staff.some((s) => s.pin === pin)) throw errors.validation('pin', 'taken');
    const rec: StaffRecord = { id: uuid(), name: str(b, 'name', 64), role, pin, active: true };
    club().staff.push(rec);
    markDirty();
    return { id: rec.id };
  });

  app.patch<{ Params: { id: string } }>('/admin/staff/:id', async (req) => {
    const me = requireStaff(req, 'owner');
    const s = club().staff.find((x) => x.id === req.params.id);
    if (!s) throw errors.notFound('staff');
    const b = body(req);
    if (typeof b['name'] === 'string') s.name = str(b, 'name', 64);
    if (typeof b['active'] === 'boolean') {
      if (s.id === me.id && !b['active']) throw errors.validation('active', 'self');
      s.active = b['active'];
    }
    if (typeof b['pin'] === 'string') {
      const pin = str(b, 'pin', 12);
      if (!/^\d{4,8}$/.test(pin)) throw errors.validation('pin', 'digits4to8');
      s.pin = pin;
    }
    markDirty();
    return { ok: true };
  });

  // ------------------------------------------------------------------------------------------------ shifts
  app.get('/admin/shift', async (req) => {
    requireStaff(req);
    const shift = openShift();
    return {
      shift,
      x: shift ? totalsSince(shift.openedAt) : null,
      history: club()
        .shifts.filter((s) => s.closedAt !== null)
        .slice(-30)
        .reverse(),
    };
  });

  app.post('/admin/shift/open', async (req) => {
    const me = requireStaff(req);
    if (openShift()) throw errors.conflict('shiftOpen');
    const shift = {
      id: uuid(),
      staffId: me.id,
      staffName: me.name,
      openedAt: now(),
      closedAt: null,
      openingCash: int(body(req), 'openingCash', 0, 10_000_000_000),
      closingCash: null,
      totals: null,
    };
    club().shifts.push(shift);
    markDirty();
    record(me, 'shiftOpen', { amount: shift.openingCash, detail: me.name, meta: { openingCash: shift.openingCash } });
    return { shift };
  });

  app.post('/admin/shift/close', async (req, reply) => {
    const me = requireStaff(req);
    return idempotent(req, reply, async () => {
      const shift = openShift();
      if (!shift) throw errors.conflict('noShift');
      shift.closedAt = now();
      shift.closingCash = int(body(req), 'closingCash', 0, 10_000_000_000);
      shift.totals = totalsSince(shift.openedAt, shift.closedAt);
      markDirty();
      const expected = shift.openingCash + shift.totals.topUpCash;
      // The shift is already marked closed, so `record` sees no open shift: tag the entry with it explicitly.
      const entry = record(me, 'shiftClose', {
        amount: shift.closingCash,
        detail: shift.staffName,
        meta: { expected, counted: shift.closingCash, diff: shift.closingCash - expected },
      });
      entry.shiftId = shift.id;
      emit(
        'shiftClosed',
        `${shift.staffName}: сеансы ${money(shift.totals.sessions)}, магазин ${money(shift.totals.shop)}, ` +
          `касса ${money(shift.closingCash)} (ожидалось ${money(expected)})`,
        { shiftId: shift.id },
      );
      return { status: 200, body: { shift, expectedCash: expected } };
    });
  });

  // ------------------------------------------------------------------------------------------------ settings doc
  /** Everything the owner edits on the "Клуб" pages, minus secrets: the API key has its own owner-only route. */
  app.get('/admin/club', async (req) => {
    requireStaff(req);
    const c = club();
    return {
      branding: c.branding,
      features: c.features,
      zones: c.zones,
      pricing: c.pricing,
      groups: c.groups,
      bonusTiers: c.bonusTiers,
      promoCodes: c.promoCodes,
      happyHours: c.happyHours,
      loyalty: c.loyalty,
      limits: c.limits,
      catalog: c.catalog,
      banners: c.banners,
      rulesText: c.rulesText,
      stock: c.stock,
      automation: c.automation,
      notifications: { bigTopupAt: c.notifications.bigTopupAt },
      webhooks: c.webhooks,
      control: c.control,
      events: CLUB_EVENTS,
    };
  });

  /** Partial update of the settings doc: any top-level key present in the body replaces the stored one. */
  app.patch('/admin/club', async (req) => {
    requireStaff(req, 'owner');
    const b = body(req);
    const c = club();
    const keys = [
      'branding',
      'features',
      'zones',
      'pricing',
      'groups',
      'bonusTiers',
      'promoCodes',
      'happyHours',
      'loyalty',
      'limits',
      'catalog',
      'banners',
      'rulesText',
      'stock',
      'webhooks',
    ] as const;
    for (const k of keys) {
      if (k in b) (c as unknown as Record<string, unknown>)[k] = b[k];
    }
    if ('control' in b) {
      // Thresholds must stay usable numbers whatever the client sent.
      const n = (v: unknown, fallback: number, min: number, max: number): number =>
        typeof v === 'number' && Number.isFinite(v) ? Math.min(max, Math.max(min, Math.round(v))) : fallback;
      const raw = isObject(b['control']) ? (b['control'] as Record<string, unknown>) : {};
      const cur = { ...DEFAULT_CONTROL, ...c.control };
      c.control = {
        earlyEndMinutes: n(raw['earlyEndMinutes'], cur.earlyEndMinutes, 1, 120),
        earlyEndsPerShift: n(raw['earlyEndsPerShift'], cur.earlyEndsPerShift, 1, 50),
        discountPct: n(raw['discountPct'], cur.discountPct, 1, 100),
        sameClientTopups: n(raw['sameClientTopups'], cur.sameClientTopups, 2, 50),
        shortfallFrom: n(raw['shortfallFrom'], cur.shortfallFrom, 0, 1_000_000_000),
      };
    }
    if ('automation' in b && Array.isArray(b['automation'])) {
      c.automation = (b['automation'] as AutomationRule[]).map((r) => ({
        ...r,
        id: r.id || uuid(),
        fired: r.fired ?? 0,
        lastFiredAt: r.lastFiredAt ?? null,
      }));
    }
    if ('notifications' in b) {
      // Only the big top-up threshold; Telegram fields an older console still sends are ignored.
      const n = obj(b, 'notifications');
      c.notifications = {
        bigTopupAt: typeof n['bigTopupAt'] === 'number' ? n['bigTopupAt'] : c.notifications.bigTopupAt,
      };
    }
    if ('features' in b || 'branding' in b || 'catalog' in b || 'rulesText' in b || 'banners' in b) bumpCatalog();
    markDirty();
    return { ok: true };
  });

  /** The club API key acts as an owner token, so only the owner reads it (a cashier gets 403). */
  app.get('/admin/club/api-key', async (req) => {
    requireStaff(req, 'owner');
    return { apiKey: club().apiKey };
  });

  app.post('/admin/club/api-key', async (req) => {
    requireStaff(req, 'owner');
    club().apiKey = `ck_${uuid().replace(/-/g, '')}`;
    markDirty();
    return { apiKey: club().apiKey };
  });

  // ------------------------------------------------------------------------------------------------ tariffs
  app.get('/admin/tariffs', async (req) => {
    requireStaff(req);
    return { items: db.tariffs };
  });

  app.post('/admin/tariffs', async (req) => {
    requireStaff(req, 'owner');
    const t = tariffFrom(body(req), uuid());
    db.tariffs.push(t);
    markDirty();
    return { tariff: t };
  });

  app.put<{ Params: { id: string } }>('/admin/tariffs/:id', async (req) => {
    requireStaff(req, 'owner');
    const i = db.tariffs.findIndex((t) => t.id === req.params.id);
    if (i < 0) throw errors.notFound('tariff');
    db.tariffs[i] = tariffFrom(body(req), req.params.id);
    markDirty();
    return { tariff: db.tariffs[i] };
  });

  app.delete<{ Params: { id: string } }>('/admin/tariffs/:id', async (req) => {
    requireStaff(req, 'owner');
    db.tariffs = db.tariffs.filter((t) => t.id !== req.params.id);
    markDirty();
    return { ok: true };
  });

  /** Price preview the cashier sees before opening time: base, weekday percent, the discount that applies. */
  app.post('/admin/quote', async (req) => {
    requireStaff(req);
    const b = body(req);
    const tariff = findTariff(str(b, 'tariffId', 64));
    const pc = findPc(str(b, 'pcId', 64));
    if (!tariff) throw errors.notFound('tariff');
    if (!pc) throw errors.notFound('pc');
    const minutes = tariff.isPackage ? (tariff.packageMinutes ?? 60) : int(b, 'minutes', 5, 1440);
    return quote(tariff, minutes, optStr(b, 'userId', 64), pc.zone);
  });

  // ------------------------------------------------------------------------------------------------ clients
  /**
   * The counter's client search: at most 8, best first. `q` matches the name or login (any case), any run of the phone's
   * digits ("4521" finds +998 90 123 45 21) or the exact card; under 2 characters — the clients active last. Guests
   * and staff accounts are not clients.
   */
  app.get<{ Querystring: { q?: string } }>('/admin/clients/lookup', async (req) => {
    requireStaff(req);
    const q = (req.query.q ?? '').trim().toLowerCase();
    const pool = db.users.filter((u) => !u.transient && u.role !== 'admin' && u.role !== 'guest');
    if (q.length < 2) {
      const lastTx = new Map<string, string>();
      for (const tx of db.transactions) {
        if (tx.createdAt > (lastTx.get(tx.userId) ?? '')) lastTx.set(tx.userId, tx.createdAt);
      }
      const active = (u: UserRecord): string => {
        const seen = u.lastSeenAt ?? u.createdAt;
        const tx = lastTx.get(u.id) ?? '';
        return tx > seen ? tx : seen;
      };
      const recent = [...pool].sort((a, b) => active(b).localeCompare(active(a)));
      return { items: recent.slice(0, 8).map(lookupView) };
    }
    // Digits typed with phone punctuation only ("90 123-45-21") search the phone.
    const digits = /^[\d\s+()-]+$/.test(q) ? q.replace(/\D/g, '') : null;
    const items = pool
      .map((u) => ({ u, score: lookupScore(u, q, digits && digits.length >= 2 ? digits : null) }))
      .filter((x) => x.score > 0)
      .sort((a, b) => b.score - a.score || a.u.displayName.localeCompare(b.u.displayName))
      .slice(0, 8)
      .map((x) => lookupView(x.u));
    return { items };
  });

  app.get<{ Querystring: { q?: string } }>('/admin/clients', async (req) => {
    requireStaff(req);
    const q = (req.query.q ?? '').trim().toLowerCase();
    const items = db.users
      .filter((u) => !u.transient && u.role !== 'admin')
      .filter(
        (u) =>
          !q ||
          u.displayName.toLowerCase().includes(q) ||
          u.username.toLowerCase().includes(q) ||
          profileOf(u.id).phone.includes(q),
      )
      .map(clientView);
    return { items };
  });

  /** Registers a client with the password (the console generates a temporary one) and card they sign in with. */
  app.post('/admin/clients', async (req) => {
    requireStaff(req);
    const b = body(req);
    const username = str(b, 'username', 32).toLowerCase();
    if (db.users.some((u) => u.username.toLowerCase() === username)) throw errors.validation('username', 'taken');
    // No password sent: nobody knows one until the cashier resets it.
    const password = b['password'] === undefined || b['password'] === null ? uuid() : passwordOf(b);
    const cardId = cardOf(b, null);
    const template = db.users.find((u) => u.role === 'member') as UserRecord;
    const user: UserRecord = {
      ...template,
      id: uuid(),
      username,
      displayName: str(b, 'displayName', 64),
      role: 'member',
      balance: uzs(0),
      bonus: uzs(0),
      password,
      pin: null,
      cardId,
      loginToken: null,
      avatarUrl: null,
      createdAt: now(),
      transient: false,
      muted: false,
    };
    db.users.push(user);
    club().clients[user.id] = {
      groupId: optStr(b, 'groupId', 64),
      note: '',
      blacklisted: false,
      phone: optStr(b, 'phone', 32) ?? '',
      birthYear: optInt(b, 'birthYear', 1900, 2100),
    };
    markDirty();
    return { client: clientView(user) };
  });

  app.patch<{ Params: { id: string } }>('/admin/clients/:id', async (req) => {
    const me = requireStaff(req);
    const user = findUser(req.params.id);
    if (!user) throw errors.notFound('user');
    const b = body(req);
    const p = { ...profileOf(user.id) };
    const groupBefore = p.groupId;
    const blacklistedBefore = p.blacklisted;
    if ('groupId' in b) p.groupId = optStr(b, 'groupId', 64);
    if ('note' in b) p.note = optStr(b, 'note', 2000) ?? '';
    if ('phone' in b) p.phone = optStr(b, 'phone', 32) ?? '';
    if ('birthYear' in b) p.birthYear = optInt(b, 'birthYear', 1900, 2100);
    if ('blacklisted' in b) {
      if (me.role !== 'owner') throw errors.forbidden('ownerOnly');
      p.blacklisted = bool(b, 'blacklisted');
    }
    if (typeof b['displayName'] === 'string') user.displayName = str(b, 'displayName', 64);
    club().clients[user.id] = p;
    markDirty();
    if (p.groupId !== groupBefore) {
      const group = club().groups.find((g) => g.id === p.groupId);
      record(me, 'clientGroup', {
        userId: user.id,
        detail: `${user.displayName} → ${group?.name ?? '—'}`,
        meta: {
          groupId: p.groupId,
          groupName: group?.name ?? null,
          discountPct: group?.discountPct ?? 0,
          clientName: user.displayName,
        },
      });
    }
    if (p.blacklisted !== blacklistedBefore) {
      record(me, 'blacklist', { userId: user.id, detail: user.displayName, meta: { blacklisted: p.blacklisted } });
    }
    return { client: clientView(user) };
  });

  /** Sets a new sign-in password for a client (the console generates a temporary one and shows it once). */
  app.post<{ Params: { id: string } }>('/admin/clients/:id/password', async (req) => {
    const me = requireStaff(req);
    const user = findUser(req.params.id);
    if (!user || user.transient) throw errors.notFound('user');
    user.password = passwordOf(body(req));
    markDirty();
    record(me, 'clientPassword', { userId: user.id, detail: user.displayName });
    return { ok: true };
  });

  /** Binds a club card to a client (`cardId: null` unbinds it). */
  app.post<{ Params: { id: string } }>('/admin/clients/:id/card', async (req) => {
    const me = requireStaff(req);
    const user = findUser(req.params.id);
    if (!user || user.transient) throw errors.notFound('user');
    user.cardId = cardOf(body(req), user.id);
    markDirty();
    record(me, 'clientCard', {
      userId: user.id,
      detail: `${user.displayName} · ${user.cardId ?? '—'}`,
      meta: { cardId: user.cardId },
    });
    return { client: clientView(user) };
  });

  app.get<{ Params: { id: string } }>('/admin/clients/:id/transactions', async (req) => {
    requireStaff(req);
    return { items: db.transactions.filter((t) => t.userId === req.params.id).slice(0, 100) };
  });

  // ------------------------------------------------------------------------------------------------ promo codes
  /** Redeems a promo code for a client at the counter (bonus credit). */
  app.post('/admin/promo/redeem', async (req, reply) => {
    const me = requireStaff(req);
    return idempotent(req, reply, async () => {
      const b = body(req);
      const user = findUser(str(b, 'userId', 64));
      if (!user) throw errors.notFound('user');
      const code = club().promoCodes.find((p) => p.code.toUpperCase() === str(b, 'code', 32).toUpperCase());
      if (!code) throw errors.notFound('promo');
      if (code.expiresAt && code.expiresAt < now()) throw errors.validation('code', 'expired');
      if (code.usesLeft !== null && code.usesLeft <= 0) throw errors.validation('code', 'exhausted');
      if (code.kind !== 'bonus') throw errors.validation('code', 'discountAtCheckout');
      applyTransaction(user, 'bonus', uzs(code.value), `Промокод ${code.code}`, null);
      code.used += 1;
      if (code.usesLeft !== null) code.usesLeft -= 1;
      markDirty();
      pushToUser(user.id, 'walletUpdated', balanceOf(user));
      record(me, 'promoRedeem', {
        userId: user.id,
        amount: code.value,
        detail: `${user.displayName} · ${code.code}`,
        meta: { code: code.code },
      });
      return { status: 200, body: { balance: user.balance } };
    });
  });

  // ------------------------------------------------------------------------------------------------ hall map
  app.get('/admin/pcs', async (req) => {
    requireStaff(req);
    const c = club();
    return {
      items: db.pcs.map((p) => ({
        ...publicPc(p),
        x: p.x,
        y: p.y,
        device: c.devices[p.id] ?? 'pc',
        hardware: p.hardware,
        metrics: p.metrics.at(-1) ?? null,
      })),
      zones: c.zones,
    };
  });

  app.patch<{ Params: { pcId: string } }>('/admin/pcs/:pcId', async (req) => {
    requireStaff(req, 'owner');
    const pc = findPc(req.params.pcId);
    if (!pc) throw errors.notFound('pc');
    const b = body(req);
    if (typeof b['zone'] === 'string') pc.zone = str(b, 'zone', 32);
    if (typeof b['name'] === 'string') pc.name = str(b, 'name', 32);
    if (typeof b['number'] === 'number') pc.number = int(b, 'number', 1, 9999);
    if (typeof b['x'] === 'number') pc.x = int(b, 'x', 0, 200);
    if (typeof b['y'] === 'number') pc.y = int(b, 'y', 0, 200);
    if (typeof b['device'] === 'string') club().devices[pc.id] = str(b, 'device', 16) as DeviceKind;
    if (typeof b['maintenance'] === 'boolean') pc.status = b['maintenance'] ? 'maintenance' : 'free';
    markDirty();
    broadcast('pcStatusChanged', { pcId: pc.id, status: pc.status });
    return { pc: publicPc(pc) };
  });

  /** Adds a seat or a device (console, VR…) to the hall. */
  app.post('/admin/pcs', async (req) => {
    requireStaff(req, 'owner');
    const b = body(req);
    const number = int(b, 'number', 1, 9999);
    const device = (optStr(b, 'device', 16) ?? 'pc') as DeviceKind;
    const rec: PcRecord = {
      ...(db.pcs[0] as PcRecord),
      id: uuid(),
      name:
        optStr(b, 'name', 32) ?? `${device === 'pc' ? 'PC' : device.toUpperCase()}-${String(number).padStart(2, '0')}`,
      zone: str(b, 'zone', 32),
      number,
      x: optInt(b, 'x', 0, 200) ?? 0,
      y: optInt(b, 'y', 0, 200) ?? 0,
      hwid: null,
      ipAddress: '0.0.0.0',
      status: 'offline',
      currentSessionId: null,
      machineName: null,
      macAddress: null,
      signingSecret: null,
      hardware: null,
      metrics: [],
      lastHeartbeatAt: now(),
    };
    db.pcs.push(rec);
    club().devices[rec.id] = device;
    markDirty();
    return { pc: publicPc(rec) };
  });

  app.delete<{ Params: { pcId: string } }>('/admin/pcs/:pcId', async (req) => {
    requireStaff(req, 'owner');
    const pc = findPc(req.params.pcId);
    if (!pc) throw errors.notFound('pc');
    if (pc.status === 'busy') throw errors.conflict('pcBusy');
    db.pcs = db.pcs.filter((p) => p.id !== pc.id);
    markDirty();
    return { ok: true };
  });

  // ------------------------------------------------------------------------------------------------ shop & stock
  app.get('/admin/products', async (req) => {
    requireStaff(req);
    return { items: db.products, lowAt: club().stock.lowAt };
  });

  app.patch<{ Params: { id: string } }>('/admin/products/:id', async (req) => {
    const me = requireStaff(req, 'owner');
    const p = db.products.find((x) => x.id === req.params.id);
    if (!p) throw errors.notFound('product');
    const b = body(req);
    const qtyBefore = p.stockQty;
    if (typeof b['title'] === 'string') p.title = str(b, 'title', 80);
    if (typeof b['price'] === 'number') p.price = uzs(int(b, 'price', 0, 1_000_000_000));
    if (typeof b['inStock'] === 'boolean') p.inStock = b['inStock'];
    if ('stockQty' in b) p.stockQty = optInt(b, 'stockQty', 0, 1_000_000);
    markDirty();
    if (p.stockQty !== qtyBefore) {
      record(me, 'stockEdit', {
        detail: `${p.title}: ${qtyBefore ?? '—'} → ${p.stockQty ?? '—'}`,
        meta: { productId: p.id, before: qtyBefore ?? null, after: p.stockQty ?? null },
      });
    }
    return { product: p };
  });

  /** Goods received: adds to the tracked quantity. */
  app.post<{ Params: { id: string } }>('/admin/products/:id/receive', async (req, reply) => {
    const me = requireStaff(req);
    return idempotent(req, reply, async () => {
      const p = db.products.find((x) => x.id === req.params.id);
      if (!p) throw errors.notFound('product');
      const qty = int(body(req), 'qty', 1, 100_000);
      p.stockQty = (p.stockQty ?? 0) + qty;
      p.inStock = true;
      markDirty();
      record(me, 'stockReceive', { detail: `${p.title} +${qty}`, meta: { productId: p.id, qty } });
      // A copy: a replay answers with the stock right after this delivery, not the live record.
      return { status: 200, body: { product: { ...p } } };
    });
  });

  // ------------------------------------------------------------------------------------------------ games
  app.get('/admin/games', async (req) => {
    requireStaff(req);
    const c = club();
    return {
      items: db.games.map((g) => ({
        id: g.id,
        title: g.title,
        coverUrl: g.coverUrl,
        installed: g.installed,
        launcher: g.launcher,
        category: g.category,
        hidden: c.catalog.hidden.includes(g.id),
        featured: c.catalog.featured.includes(g.id),
        settingsPaths: g.settingsPaths ?? [],
      })),
      order: c.catalog.order,
    };
  });

  /** Where a game keeps a player's own settings, carried from PC to PC per player (`Game.settingsPaths`). */
  app.patch<{ Params: { id: string } }>('/admin/games/:id', async (req) => {
    requireStaff(req, 'owner');
    const game = findGame(req.params.id);
    if (!game) throw errors.notFound('game');
    const raw = body(req)['settingsPaths'];
    if (!Array.isArray(raw)) throw errors.validation('settingsPaths', 'array');
    const paths = raw
      .filter((p): p is string => typeof p === 'string')
      .map((p) => p.trim())
      .filter((p) => p.length > 0 && p.length <= 260)
      .slice(0, 10);
    game.settingsPaths = paths.length > 0 ? paths : null;
    bumpCatalog();
    return { settingsPaths: game.settingsPaths ?? [] };
  });

  // ------------------------------------------------------------------------------------------------ PC health
  /** Every PC's health (score, live and usual temperatures, FPS, last 24 h, problems) and the repair tickets. */
  app.get('/admin/health', async (req) => {
    requireStaff(req);
    const hs = health();
    const t = Date.now();
    return {
      settings: hs.settings,
      pcs: db.pcs.map((pc) => ({
        id: pc.id,
        name: pc.name,
        zone: pc.zone,
        status: pc.status,
        ...diagnose(pc, t),
        ticket: hs.tickets.find((x) => x.pcId === pc.id && x.status !== 'resolved') ?? null,
      })),
      tickets: hs.tickets.slice(0, 100),
    };
  });

  app.patch<{ Params: { id: string } }>('/admin/health/tickets/:id', async (req) => {
    const me = requireStaff(req);
    const b = body(req);
    const status = str(b, 'status', 16) as TicketStatus;
    if (!['open', 'inWork', 'resolved'].includes(status)) throw errors.validation('status', 'unknown');
    const ticket = updateTicket(req.params.id, status, optStr(b, 'note', 500));
    if (!ticket) throw errors.notFound('ticket');
    record(me, 'pcCommand', {
      pcId: ticket.pcId,
      detail: `${ticket.pcName} · ${status}`,
      meta: { kind: 'repair', ticketId: ticket.id, status },
    });
    return { ticket };
  });

  app.patch('/admin/health/settings', async (req) => {
    requireStaff(req, 'owner');
    const raw = body(req);
    const hs = health();
    const n = (v: unknown, fallback: number, min: number, max: number): number =>
      typeof v === 'number' && Number.isFinite(v) ? Math.min(max, Math.max(min, Math.round(v))) : fallback;
    const cur = { ...DEFAULT_HEALTH, ...hs.settings };
    hs.settings = {
      cpuHotC: n(raw['cpuHotC'], cur.cpuHotC, 50, 110),
      gpuHotC: n(raw['gpuHotC'], cur.gpuHotC, 50, 110),
      trendC: n(raw['trendC'], cur.trendC, 2, 40),
      fpsDropPct: n(raw['fpsDropPct'], cur.fpsDropPct, 5, 90),
      offlinePerDay: n(raw['offlinePerDay'], cur.offlinePerDay, 1, 50),
      autoMaintenance: typeof raw['autoMaintenance'] === 'boolean' ? raw['autoMaintenance'] : cur.autoMaintenance,
    };
    markDirty();
    return { settings: hs.settings };
  });

  // ------------------------------------------------------------------------------------------------ cashier control
  /** Flags, per-cashier totals and the journal for the last `days` days (owner only). */
  app.get<{ Querystring: { days?: string; staffId?: string } }>('/admin/control', async (req) => {
    requireStaff(req, 'owner');
    const days = Math.min(90, Math.max(1, Number.parseInt(req.query.days ?? '7', 10) || 7));
    const from = new Date(Date.now() - days * 86_400_000).toISOString();
    const c = club();
    const inPeriod = c.audit.filter((e) => e.at >= from);
    const flags = flagsFor(inPeriod);
    const staffId = req.query.staffId;
    return {
      from,
      to: now(),
      settings: c.control,
      staff: summaries(inPeriod, flags),
      flags: staffId ? flags.filter((f) => f.staffId === staffId) : flags,
      log: (staffId ? inPeriod.filter((e) => e.staffId === staffId) : inPeriod).slice(0, 300),
    };
  });

  // ------------------------------------------------------------------------------------------------ network
  /** Every club of the owner's network side by side, and the shared player base. */
  app.get<{ Querystring: { days?: string } }>('/admin/network', async (req) => {
    requireStaff(req, 'owner');
    const days = Math.min(90, Math.max(1, Number.parseInt(req.query.days ?? '7', 10) || 7));
    return networkReport(days);
  });

  app.post('/admin/network/clubs', async (req) => {
    requireStaff(req, 'owner');
    const b = body(req);
    const created = addClub({
      name: str(b, 'name', 64),
      city: str(b, 'city', 64),
      address: optStr(b, 'address', 128) ?? '',
      pcs: int(b, 'pcs', 1, 1000),
    });
    return { club: created };
  });

  app.patch('/admin/network', async (req) => {
    requireStaff(req, 'owner');
    const n = network();
    n.name = str(body(req), 'name', 64);
    markDirty();
    return { name: n.name };
  });

  // ------------------------------------------------------------------------------------------------ reports
  app.get<{ Querystring: { days?: string } }>('/admin/reports', async (req) => {
    requireStaff(req, 'owner');
    const days = Math.min(90, Math.max(1, Number(req.query.days ?? 7) || 7));
    const since = new Date(Date.now() - days * 86_400_000);
    since.setHours(0, 0, 0, 0);
    const sinceIso = since.toISOString();
    const byDay = new Map<string, { date: string; sessions: number; shop: number; topUps: number }>();
    for (let i = 0; i < days; i += 1) {
      const d = new Date(since.getTime() + (i + 1) * 86_400_000).toISOString().slice(0, 10);
      byDay.set(d, { date: d, sessions: 0, shop: 0, topUps: 0 });
    }
    for (const tx of db.transactions) {
      if (tx.createdAt < sinceIso) continue;
      const row = byDay.get(tx.createdAt.slice(0, 10));
      if (!row) continue;
      if (tx.type === 'charge') row.sessions += -tx.amount.amount;
      else if (tx.type === 'purchase') row.shop += -tx.amount.amount;
      else if (tx.type === 'topUp') row.topUps += tx.amount.amount;
    }
    // Occupancy heat map: seat-hours per weekday × hour.
    const heat = Array.from({ length: 7 }, () => Array.from({ length: 24 }, () => 0));
    for (const s of db.sessions) {
      if (s.startedAt < sinceIso) continue;
      const start = Date.parse(s.startedAt);
      const end = s.endedAt ? Date.parse(s.endedAt) : Date.now();
      for (let t = start; t < end; t += 3_600_000) {
        const d = new Date(t);
        (heat[d.getDay()] as number[])[d.getHours()] += 1;
      }
    }
    const games = new Map<string, number>();
    for (const perUser of Object.values(db.lastPlayed)) {
      for (const gameId of Object.keys(perUser)) games.set(gameId, (games.get(gameId) ?? 0) + 1);
    }
    const products = new Map<string, { title: string; qty: number; amount: number }>();
    for (const o of db.orders) {
      if (o.createdAt < sinceIso) continue;
      for (const line of o.items) {
        const cur = products.get(line.productId) ?? { title: line.title, qty: 0, amount: 0 };
        cur.qty += line.qty;
        cur.amount += line.price.amount * line.qty;
        products.set(line.productId, cur);
      }
    }
    const rows = [...byDay.values()];
    return {
      days,
      totals: {
        sessions: rows.reduce((a, r) => a + r.sessions, 0),
        shop: rows.reduce((a, r) => a + r.shop, 0),
        topUps: rows.reduce((a, r) => a + r.topUps, 0),
        sessionsCount: db.sessions.filter((s) => s.startedAt >= sinceIso).length,
      },
      byDay: rows,
      heat,
      topGames: [...games.entries()]
        .map(([id, n]) => ({ id, title: db.games.find((g) => g.id === id)?.title ?? id, players: n }))
        .sort((a, b) => b.players - a.players)
        .slice(0, 8),
      topProducts: [...products.values()].sort((a, b) => b.amount - a.amount).slice(0, 8),
      shifts: club()
        .shifts.filter((s) => s.openedAt >= sinceIso)
        .reverse(),
    };
  });
}

/** Counter top-up with the tier bonus; shared by the old `/admin/wallet/topup` route. */
export function topUpWithBonus(user: UserRecord, amount: number, method: string): { bonus: number } {
  applyTransaction(user, 'topUp', uzs(amount), `Top-up at the counter (${method})`, null);
  const bonus = topupBonus(amount);
  if (bonus > 0) applyTransaction(user, 'bonus', uzs(bonus), 'Бонус за пополнение', null);
  pushToUser(user.id, 'walletUpdated', balanceOf(user));
  clubHooks.topUp(user, amount);
  return { bonus };
}

export type { ShellFeatures };
