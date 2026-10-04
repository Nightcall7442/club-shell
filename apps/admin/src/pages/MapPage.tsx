/**
 * The counter ("Карта"): the hall map as numbered tiles per zone — PC number, who is on it, time left — under a header
 * that counts the busy seats and filters the hall (free, ending soon, postpaid, repair, offline), and the selected seat
 * on the right. Seating a client or a walk-in guest (prepaid, postpaid, an hourly tariff or a package), extending,
 * topping up and ending a session all happen in that panel; every money step ends in the pay box (`paybox.tsx`), whose
 * payment-method button is also the confirmation. Ending a session asks first and then shows what the server refunded
 * or charged; a bill left to pay or a guest's refund to give back opens the settle sheet over the map, and what is not
 * settled there waits in «Расчёт с гостями и долги» under the map. Keys: digits then Enter select a PC by number, F2
 * tops up the selected client, Esc closes the open sheet, then the panel.
 *
 * The shift's operations feed (`operations.tsx`) is a third column from 1800 px; below that it fills the right panel
 * while no seat is selected, and a «Место | Операции» switch brings it back over a selected seat.
 * Polls `/admin/overview` every 2 s (the real console would follow the server's WebSocket).
 */
import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react';
import clsx from 'clsx';
import type { Money, Session, Tariff } from '@clubshell/contracts';
import {
  adminApi,
  clubApi,
  type ClientHit,
  type GuestDebt,
  type GuestRefund,
  type Member,
  type Overview,
  type PriceQuote,
  type Seat,
  type SeatUser,
  type SessionResult,
} from '@/api';
import { ClientPicker, pcLabel } from '@/clientSearch';
import { useClub } from '@/club';
import { isTyping, onShowPc, sheetOpen } from '@/desk';
import { changedAmount, describe, isLostAnswer } from '@/errors';
import { t } from '@/i18n';
import { duration, minutesLabel, money, moneyExact } from '@/format';
import { guestDisplayName } from '@/labels';
import { OperationsFeed } from '@/operations';
import {
  PayBox,
  ReceiptButton,
  ShiftClosedNote,
  TopUpSheet,
  methodName,
  useHeldKey,
  useShiftClosed,
  type Payee,
  type Payment,
} from '@/paybox';
import { guestSignIn, type ReceiptData } from '@/print';
import { useShift } from '@/shift';
import { Button, Field, Kbd, Note, Sheet, inputCls } from '@/ui';

const POLL_MS = 2000;
const MINUTE_PRESETS = [30, 60, 120, 180];
/** Red pulse on a tile. */
const WARN_SEC = 5 * 60;
/** The "Заканчиваются" filter. */
const ENDING_SEC = 10 * 60;
/** Typed PC digits are forgotten after this pause. */
const DIGITS_MS = 2500;
/** The operations feed gets a column of its own from this width (D-45); below it shares the right panel. */
const WIDE_QUERY = '(min-width: 1800px)';
/** Which face the right panel shows over a selected seat below 1800 px. */
const PANEL_KEY = 'clubshell.admin.map.panel';
/**
 * `minutes` is required by the contract even where the server ignores it (a package, postpaid): the console sends
 * this then.
 */
const IGNORED_MINUTES = 60;

/** A result line in the seat panel; a money step adds its slip («Чек») and a walk-in guest how to sign in. */
type NoteState = { text: string; tone: 'ok' | 'err'; receipt?: ReceiptData; hint?: string } | null;

const STATUS: Record<Seat['pc']['status'], { label: string; short: string; dot: string; cell: string }> = {
  free: { label: 'Свободен', short: 'своб.', dot: 'bg-success', cell: 'border-success/40 text-text' },
  busy: { label: 'Занят', short: 'занят', dot: 'bg-accent', cell: 'border-accent/60 bg-accent/[0.06] text-accent' },
  locked: { label: 'Заблокирован', short: 'блок', dot: 'bg-danger', cell: 'border-danger/60 text-danger' },
  maintenance: {
    label: 'Обслуживание',
    short: 'сервис',
    dot: 'bg-fuchsia-400',
    cell: 'border-fuchsia-400/50 text-fuchsia-300',
  },
  booked: { label: 'Бронь', short: 'бронь', dot: 'bg-amber-300', cell: 'border-amber-300/50 text-amber-200' },
  offline: { label: 'Офлайн', short: 'офлайн', dot: 'bg-muted/40', cell: 'border-line text-muted/50' },
};

const LEGEND_ORDER: Seat['pc']['status'][] = ['free', 'busy', 'booked', 'locked', 'maintenance', 'offline'];

/** Who is on the seat, for the tile's bottom bar. */
type Kind = 'member' | 'guest' | 'postpaid' | 'free';

const KIND_BAR: Record<Kind, string> = {
  member: 'bg-accent',
  guest: 'bg-warning',
  postpaid: 'bg-fuchsia-400',
  free: 'bg-transparent',
};

function kindOf(seat: Seat): Kind {
  if (!seat.session) return 'free';
  if (!seat.session.isPrepaid) return 'postpaid';
  return seat.user?.role === 'guest' ? 'guest' : 'member';
}

type Filter = 'free' | 'ending' | 'postpaid' | 'repair' | 'offline';

const FILTERS: { id: Filter; label: string; test: (s: Seat, repair: boolean) => boolean }[] = [
  { id: 'free', label: 'Свободны', test: (s) => s.pc.status === 'free' && !s.session },
  {
    id: 'ending',
    label: 'Заканчиваются ≤10 мин',
    test: (s) => {
      const left = secondsLeft(s.session);
      return s.session !== null && s.session.isPrepaid && left >= 0 && left <= ENDING_SEC;
    },
  },
  { id: 'postpaid', label: 'Постоплата', test: (s) => s.session !== null && !s.session.isPrepaid },
  { id: 'repair', label: 'Ремонт', test: (s, repair) => repair || s.pc.status === 'maintenance' },
  { id: 'offline', label: 'Офлайн', test: (s) => s.pc.status === 'offline' },
];

/** `1.0.15` → [1, 0, 15]; anything unparsable sorts first. */
function versionParts(v: string | undefined): number[] {
  return (v ?? '')
    .split(/[.+-]/)
    .slice(0, 3)
    .map((x) => Number.parseInt(x, 10) || 0);
}

function compareVersions(a: string | undefined, b: string | undefined): number {
  const [x, y] = [versionParts(a), versionParts(b)];
  for (let i = 0; i < 3; i++) {
    if ((x[i] ?? 0) !== (y[i] ?? 0)) {
      return (x[i] ?? 0) - (y[i] ?? 0);
    }
  }
  return 0;
}

function secondsLeft(s: Session | null): number {
  if (!s) {
    return 0;
  }
  if (s.secondsLeft < 0) {
    return -1;
  }
  return s.endsAt ? Math.max(0, Math.round((Date.parse(s.endsAt) - Date.now()) / 1000)) : s.secondsLeft;
}

const uzs = (minor: number): Money => ({ amount: minor, currency: 'UZS' });

function clock(iso: string): string {
  return new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

/** A seat's client as the panel names them: guests are "Гость", not their technical account name. */
function nameOf(user: SeatUser): string {
  return user.role === 'guest' ? t('Гость') : user.displayName;
}

/** The tariff's sale windows: `22:00–08:00`; empty — any time. */
function windowsOf(tf: Tariff): string {
  return tf.timeWindows.map((w) => `${w.from}–${w.to}`).join(', ');
}

/** Price of one minute by a quote, rounded up to whole сум: what a postpaid seat must afford at the start. */
function firstMinute(q: PriceQuote, minutes: number): number {
  return Math.ceil(q.total.amount / Math.max(1, minutes) / 100) * 100;
}

function useWide(): boolean {
  const [wide, setWide] = useState(() => window.matchMedia(WIDE_QUERY).matches);
  useEffect(() => {
    const m = window.matchMedia(WIDE_QUERY);
    const on = (): void => setWide(m.matches);
    m.addEventListener('change', on);
    return () => m.removeEventListener('change', on);
  }, []);
  return wide;
}

/** What is left to settle after an end, at the map: a debt to take or a guest's refund to give back in cash. */
interface SettleTarget {
  userId: string;
  displayName: string;
  guest: boolean;
  /** Tiyin to take exactly; 0 — none. */
  debt: number;
  /** Tiyin that may be given back in cash now; 0 — none. */
  payable: number;
  /** The balance after the end (for the note about money that is not given back in cash). */
  balance: number;
  pc: string | null;
}

/** The sheet open over the map: one at a time. */
type SheetState =
  | { kind: 'topup'; payee: Payee; initial?: number; title?: string }
  | { kind: 'extend' }
  | { kind: 'end' }
  | { kind: 'settle'; target: SettleTarget }
  | null;

/** The settle sheet an end calls for, from the server's answer; null when nothing is left to settle. */
function settleOf(r: SessionResult, seat: Seat, user: SeatUser): SettleTarget | null {
  const balance = r.balance?.amount;
  if (balance === undefined) return null;
  const guest = (r.user?.role ?? user.role) === 'guest';
  const base = {
    userId: user.id,
    displayName: r.user?.displayName ?? user.displayName,
    guest,
    balance,
    pc: seat.pc.name,
  };
  if (balance < 0) return { ...base, debt: -balance, payable: 0 };
  const payable = r.payable?.amount ?? 0;
  if (guest && payable > 0) return { ...base, debt: 0, payable };
  return null;
}

// ---------------------------------------------------------------------------------------------------------------------
// Pieces
// ---------------------------------------------------------------------------------------------------------------------

function Wrench({ severity }: { severity: 'high' | 'medium' }): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      aria-label={t('Нужен ремонт')}
      className={clsx('h-3.5 w-3.5', severity === 'high' ? 'text-danger' : 'text-warning')}
      fill="none"
      stroke="currentColor"
      strokeWidth="2.2"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <path d="M14.7 6.3a4 4 0 0 0-5.4 5.4L3 18l3 3 6.3-6.3a4 4 0 0 0 5.4-5.4l-2.6 2.6-2.4-.6-.6-2.4 2.6-2.6z" />
    </svg>
  );
}

function Lock(): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      aria-label={t('Заблокирован')}
      className="h-3.5 w-3.5 text-danger"
      fill="none"
      stroke="currentColor"
      strokeWidth="2.2"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <path d="M6 11h12v9H6zM8.5 11V8a3.5 3.5 0 0 1 7 0v3" />
    </svg>
  );
}

/**
 * Three lines: the PC number, who is on it, the time left (or the running bill of a postpaid session). A desk session
 * nobody has signed in to yet says «ждёт входа» instead of the name: its clock already runs (D-49).
 */
function SeatTile({
  seat,
  selected,
  dimmed,
  onSelect,
  repair,
}: {
  seat: Seat;
  selected: boolean;
  dimmed: boolean;
  onSelect: () => void;
  /** Worst open repair ticket on this PC (from "Состояние ПК"). */
  repair?: 'high' | 'medium';
}): JSX.Element {
  const s = STATUS[seat.pc.status];
  const kind = kindOf(seat);
  const left = secondsLeft(seat.session);
  const warn = seat.session !== null && seat.session.isPrepaid && left >= 0 && left <= WARN_SEC;
  const waiting = seat.session !== null && seat.signedIn === false;
  return (
    <button
      type="button"
      id={`seat-${seat.pc.id}`}
      onClick={onSelect}
      aria-pressed={selected}
      title={`${seat.pc.name} · ${t(s.label)}${seat.user ? ` · ${nameOf(seat.user)}` : ''}${waiting ? ` · ${t('ждёт входа')}` : ''}${repair ? ` · ${t('Нужен ремонт')}` : ''}`}
      className={clsx(
        'focus-ring relative flex h-[5.75rem] flex-col justify-between overflow-hidden rounded-md border bg-bg px-2.5 pb-2.5 pt-2 text-left transition-[background-color,opacity] hover:bg-white/[0.04]',
        s.cell,
        selected && 'ring-2 ring-accent ring-offset-2 ring-offset-surface',
        dimmed && 'opacity-25',
      )}
    >
      <span className="flex items-start justify-between gap-1">
        <span className="num-dot text-[1.6rem] leading-none">{String(seat.pc.number).padStart(2, '0')}</span>
        <span className="flex items-center gap-1">
          {seat.pc.status === 'locked' && <Lock />}
          {repair && <Wrench severity={repair} />}
        </span>
      </span>
      {waiting ? (
        <span className="block truncate text-xs leading-tight text-warning">{t('ждёт входа')}</span>
      ) : (
        <span className="block truncate text-xs leading-tight text-text">{seat.user ? nameOf(seat.user) : ' '}</span>
      )}
      {seat.session ? (
        <span
          className={clsx(
            'tnum block truncate font-mono text-[0.7rem] leading-none',
            warn ? 'font-semibold text-danger' : 'text-muted',
          )}
        >
          {seat.session.isPrepaid ? duration(left) : `∞ ${money(seat.session.cost)}`}
        </span>
      ) : (
        <span className="block font-mono text-[0.62rem] uppercase leading-none tracking-[0.1em] text-muted">
          {t(s.short)}
        </span>
      )}
      <span
        aria-hidden="true"
        className={clsx('absolute inset-x-0 bottom-0 h-[3px]', warn ? 'animate-pulse bg-danger' : KIND_BAR[kind])}
      />
    </button>
  );
}

/** The panel's result line, with «Чек» for a money step and the guest's way in after a walk-in seat. */
function SeatNote({ note }: { note: NoteState }): JSX.Element | null {
  if (!note) return null;
  if (!note.receipt && !note.hint) return <Note note={note} />;
  return (
    <div
      className={clsx(
        'flex flex-col gap-2 rounded-md px-3 py-2 text-sm',
        note.tone === 'ok' ? 'bg-success/10 text-success' : 'bg-danger/10 text-danger',
      )}
    >
      <div className="flex items-start justify-between gap-3">
        <p>{note.text}</p>
        {note.receipt && <ReceiptButton receipt={note.receipt} className="h-8 shrink-0 px-2.5 text-xs" />}
      </div>
      {note.hint && <p className="text-xs text-text">{note.hint}</p>}
    </div>
  );
}

/** Two or more buttons of which one is on: who sits (client / guest), how they pay (prepaid / postpaid). */
function Choice<T extends string>({
  label,
  value,
  options,
  onChange,
}: {
  label: string;
  value: T;
  options: { id: T; label: string; disabled?: string | null }[];
  onChange: (v: T) => void;
}): JSX.Element {
  return (
    <div role="group" aria-label={label} className="grid grid-cols-2 gap-1.5">
      {options.map((o) => (
        <Button
          key={o.id}
          aria-pressed={value === o.id}
          disabled={Boolean(o.disabled)}
          title={o.disabled ?? undefined}
          className={clsx(value === o.id && 'choice-on')}
          onClick={() => onChange(o.id)}
        >
          {o.label}
        </Button>
      ))}
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Seat panel
// ---------------------------------------------------------------------------------------------------------------------

/** The busy seat's client as the lookup knows them: phone tail and bonus (the overview has neither). */
function useClientHit(user: SeatUser | null, username: string | undefined, version: number): ClientHit | null {
  const [hit, setHit] = useState<ClientHit | null>(null);
  const id = user?.id ?? null;
  const guest = user?.role === 'guest';
  const q = username ?? user?.displayName ?? '';
  useEffect(() => {
    setHit(null);
    if (!id || guest) return undefined;
    let alive = true;
    adminApi
      .lookupClients(q)
      .then((r) => alive && setHit(r.items.find((x) => x.id === id) ?? null))
      .catch(() => alive && setHit(null));
    return () => {
      alive = false;
    };
  }, [id, guest, q, version]);
  return hit;
}

/** "К оплате N · на балансе M · доплата K". */
function PaySummary({ price, balance }: { price: number; balance: number }): JSX.Element {
  const shortfall = Math.max(0, price - balance);
  return (
    <dl className="grid grid-cols-3 divide-x divide-line overflow-hidden rounded-md border border-line bg-bg text-center">
      <div className="flex flex-col gap-1.5 px-2 py-2.5">
        <dt className="label">{t('К оплате')}</dt>
        <dd className="tnum text-sm font-semibold leading-none">{money(uzs(price))}</dd>
      </div>
      <div className="flex flex-col gap-1.5 px-2 py-2.5">
        <dt className="label">{t('На балансе')}</dt>
        <dd className="tnum text-sm font-semibold leading-none">{money(uzs(balance))}</dd>
      </div>
      <div className="flex flex-col gap-1.5 px-2 py-2.5">
        <dt className="label">{t('Доплата')}</dt>
        <dd className={clsx('tnum text-sm font-semibold leading-none', shortfall > 0 ? 'text-warning' : 'text-muted')}>
          {money(uzs(shortfall))}
        </dd>
      </div>
    </dl>
  );
}

/** A guest pays exactly the price, to the tiyin (D-48): no balance, no change left on a throwaway account. */
function ExactSummary({ price, label }: { price: number; label?: string }): JSX.Element {
  return (
    <div className="flex items-baseline justify-between rounded-md border border-line bg-bg px-3 py-2.5">
      <span className="label">{label ?? t('К оплате')}</span>
      <span className="tnum text-sm font-semibold">{moneyExact(price)}</span>
    </div>
  );
}

/** Message, lock, reboot, shutdown: behind "Ещё ⋯" so the money actions stay on top. */
function TechActions({
  seat,
  busy,
  run,
}: {
  seat: Seat;
  busy: boolean;
  run: (key: string, fn: () => Promise<string | NoteState>) => Promise<void>;
}): JSX.Element {
  const [open, setOpen] = useState(false);
  const [message, setMessage] = useState('');
  const command = (kind: 'lock' | 'unlock' | 'reboot' | 'shutdown', done: string): void =>
    void run(kind, async () => {
      await adminApi.command(seat.pc.id, { kind });
      return done;
    });
  return (
    <section className="flex flex-col gap-2">
      <Button variant="ghost" className="justify-between" aria-expanded={open} onClick={() => setOpen((v) => !v)}>
        <span>{t('Ещё ⋯')}</span>
        <span className="text-xs font-normal text-muted">{t('сообщение, блокировка, питание')}</span>
      </Button>
      {open && (
        <div className="flex flex-col gap-2 rounded-md border border-line p-3">
          <Field label={t('Сообщение на экран')}>
            <div className="flex gap-1.5">
              <input
                className={inputCls}
                value={message}
                placeholder={t('Закрываемся через 20 минут')}
                onChange={(e) => setMessage(e.target.value)}
              />
              <Button
                disabled={busy || message.trim().length === 0}
                onClick={() =>
                  void run('msg', async () => {
                    await adminApi.command(seat.pc.id, { kind: 'message', text: message.trim() });
                    setMessage('');
                    return t('Сообщение отправлено');
                  })
                }
              >
                {t('Отправить')}
              </Button>
            </div>
          </Field>
          <div className="grid grid-cols-2 gap-1.5">
            <Button variant="ghost" disabled={busy} onClick={() => command('lock', t('ПК заблокирован'))}>
              {t('Заблокировать')}
            </Button>
            <Button variant="ghost" disabled={busy} onClick={() => command('unlock', t('ПК разблокирован'))}>
              {t('Разблокировать')}
            </Button>
            <Button variant="ghost" disabled={busy} onClick={() => command('reboot', t('ПК перезагружается'))}>
              {t('Перезагрузить')}
            </Button>
            <Button variant="ghost" disabled={busy} onClick={() => command('shutdown', t('ПК выключается'))}>
              {t('Выключить')}
            </Button>
          </div>
        </div>
      )}
    </section>
  );
}

function SeatPanel({
  seat,
  members,
  tariffs,
  sheet,
  setSheet,
  onDone,
}: {
  seat: Seat;
  members: Member[];
  tariffs: Overview['tariffs'];
  sheet: SheetState;
  setSheet: (s: SheetState) => void;
  onDone: () => void;
}): JSX.Element {
  const shift = useShift();
  const [busy, setBusy] = useState<string | null>(null);
  // Kept across the busy → free switch, so "Сеанс завершён · возврат …" stays readable after the PC frees up (and
  // «Чек» of a seat stays after the PC turns busy).
  const [note, setNote] = useState<NoteState>(null);
  const [version, setVersion] = useState(0);
  useEffect(() => setNote(null), [seat.pc.id]);

  const done = (): void => {
    onDone();
    shift.refresh();
    setVersion((v) => v + 1);
  };

  const run = async (key: string, fn: () => Promise<string | NoteState>): Promise<void> => {
    setBusy(key);
    setNote(null);
    try {
      const r = await fn();
      setNote(typeof r === 'string' ? { text: r, tone: 'ok' } : r);
      done();
    } catch (e) {
      setNote({ text: describe(e), tone: 'err' });
    } finally {
      setBusy(null);
    }
  };

  const status = STATUS[seat.pc.status];
  const header = (title: string, sub: React.ReactNode): JSX.Element => (
    <header className="flex flex-col gap-1">
      <span className="label flex items-center gap-2">
        <span className={clsx('h-2 w-2 rounded-full', status.dot)} />
        {seat.pc.zone} · {t(status.label)}
      </span>
      <h2 className="font-display text-2xl font-normal leading-tight tracking-tight">{title}</h2>
      {sub}
      {seat.pc.agentVersion && (
        <span className="tnum font-mono text-xs text-muted">
          {t('Агент {agent} · Оболочка {shell}', { agent: seat.pc.agentVersion, shell: seat.pc.shellVersion ?? '—' })}
        </span>
      )}
    </header>
  );

  return (
    <div className="flex h-full flex-col gap-5 overflow-y-auto pr-1">
      {seat.session && seat.user ? (
        <BusySeat
          seat={seat}
          session={seat.session}
          user={seat.user}
          username={members.find((m) => m.id === seat.user?.id)?.username}
          tariffs={tariffs}
          header={header}
          note={note}
          busy={busy}
          run={run}
          sheet={sheet}
          setSheet={setSheet}
          version={version}
          onDone={done}
          setNote={setNote}
        />
      ) : (
        <FreeSeat
          seat={seat}
          tariffs={tariffs}
          header={header}
          note={note}
          busy={busy}
          run={run}
          onDone={done}
          setNote={setNote}
        />
      )}
    </div>
  );
}

interface PartProps {
  seat: Seat;
  tariffs: Overview['tariffs'];
  header: (title: string, sub: React.ReactNode) => JSX.Element;
  note: NoteState;
  busy: string | null;
  run: (key: string, fn: () => Promise<string | NoteState>) => Promise<void>;
  onDone: () => void;
  setNote: (n: NoteState) => void;
}

/** The tariffs a PC of `zone` sells. */
function zoneTariffsOf(tariffs: Tariff[], zone: string): Tariff[] {
  return tariffs.filter((tf) => tf.zones.length === 0 || tf.zones.some((z) => z.toLowerCase() === zone.toLowerCase()));
}

// ---------------------------------------------------------------------------------------------------------------------
// Busy seat: time, money, then the tech actions; ending is last and asks first
// ---------------------------------------------------------------------------------------------------------------------

function BusySeat({
  seat,
  session,
  user,
  username,
  tariffs,
  header,
  note,
  busy,
  run,
  sheet,
  setSheet,
  version,
  onDone,
  setNote,
}: PartProps & {
  session: Session;
  user: SeatUser;
  username: string | undefined;
  sheet: SheetState;
  setSheet: (s: SheetState) => void;
  version: number;
}): JSX.Element {
  const shift = useShift();
  const hit = useClientHit(user, username, version);
  const left = secondsLeft(session);
  const tariff = tariffs.find((x) => x.id === session.tariffId);
  // A walk-in guest's account is throwaway: no top-ups and no bonus money on it (D-36), only the exact price.
  const guest = user.role === 'guest';
  const payee: Payee = { id: user.id, displayName: nameOf(user), balance: user.balance, bonus: hit?.bonus ?? null };
  const topUp = (): void => {
    if (!guest) setSheet({ kind: 'topup', payee });
  };
  const latest = useRef(topUp);
  latest.current = topUp;

  // F2: top up the client of the selected seat (also from a field: F2 types nothing).
  useEffect(() => {
    const on = (e: KeyboardEvent): void => {
      if (e.key !== 'F2' || sheetOpen()) return;
      e.preventDefault();
      latest.current();
    };
    window.addEventListener('keydown', on);
    return () => window.removeEventListener('keydown', on);
  }, []);

  const actions = [session.isPrepaid, !guest].filter(Boolean).length;

  return (
    <>
      {header(
        nameOf(user),
        <span className="font-mono text-xs text-muted">
          {[
            seat.pc.name,
            guest && guestDisplayName(user.displayName) !== nameOf(user) ? guestDisplayName(user.displayName) : null,
            hit?.phoneTail ? `••${hit.phoneTail}` : null,
            hit ? `@${hit.username}` : null,
          ]
            .filter(Boolean)
            .join(' · ')}
        </span>,
      )}
      {seat.signedIn === false && (
        <p className="-mt-2 rounded-md bg-warning/10 px-3 py-1.5 text-xs text-warning">
          {t('ждёт входа')} · {t('на ПК ещё никто не вошёл, а время уже идёт')}
        </p>
      )}

      <dl
        className={clsx(
          'grid divide-x divide-line overflow-hidden rounded-md border border-line bg-bg text-center',
          session.isPrepaid ? 'grid-cols-2' : 'grid-cols-3',
        )}
      >
        {session.isPrepaid ? (
          <div className="flex flex-col gap-1.5 px-3 py-3">
            <dt className="label">{t('Осталось')}</dt>
            <dd className={clsx('num-dot text-3xl leading-none', left >= 0 && left <= WARN_SEC && 'text-danger')}>
              {left < 0 ? '∞' : duration(left)}
            </dd>
          </div>
        ) : (
          // Postpaid is charged in one go when the session ends: the balance stays untouched until then, so the
          // running bill is what the counter needs to see.
          <>
            <div className="flex flex-col gap-1.5 px-3 py-3">
              <dt className="label">{t('Играет')}</dt>
              <dd className="num-dot text-2xl leading-none">{duration(session.secondsUsed)}</dd>
            </div>
            <div className="flex flex-col gap-1.5 px-3 py-3">
              <dt className="label">{t('Набежало')}</dt>
              <dd className="tnum text-lg font-semibold leading-none">{money(session.cost)}</dd>
            </div>
          </>
        )}
        <div className="flex flex-col gap-1.5 px-3 py-3">
          <dt className="label">{t('Баланс')}</dt>
          <dd className="tnum text-lg font-semibold leading-none">{money(user.balance)}</dd>
          {hit && hit.bonus.amount > 0 && (
            <dd className="tnum text-xs text-muted">{t('+ бонусы {sum}', { sum: money(hit.bonus) })}</dd>
          )}
        </div>
      </dl>
      <p className="-mt-2 font-mono text-xs text-muted">
        {[tariff?.name, t('с {time}', { time: clock(session.startedAt) })].filter(Boolean).join(' · ')}
      </p>
      {!session.isPrepaid && (
        <p className="-mt-2 text-xs text-muted">
          {guest
            ? t('Постоплата: гость платит на кассе, когда сеанс закончится.')
            : t('Постоплата: сумма спишется с баланса, когда сеанс закончится.')}
        </p>
      )}

      <SeatNote note={note} />

      {actions > 0 && (
        <section className={clsx('grid gap-1.5', actions === 2 ? 'grid-cols-2' : 'grid-cols-1')}>
          {session.isPrepaid && (
            <Button
              variant="primary"
              className="h-12"
              disabled={busy !== null}
              onClick={() => setSheet({ kind: 'extend' })}
            >
              {t('Продлить')}
            </Button>
          )}
          {!guest && (
            <Button className="h-12" disabled={busy !== null} onClick={topUp}>
              {t('Пополнить')}
              <Kbd>F2</Kbd>
            </Button>
          )}
        </section>
      )}

      <TechActions seat={seat} busy={busy !== null} run={run} />

      <section className="mt-auto flex flex-col gap-2 border-t border-dashed border-danger/30 pt-4">
        <Button
          variant="danger"
          className="w-full border border-danger/40"
          disabled={busy !== null}
          onClick={() => setSheet({ kind: 'end' })}
        >
          {t('Завершить сеанс')}
        </Button>
      </section>

      {sheet?.kind === 'extend' && session.isPrepaid && (
        <ExtendSheet
          seat={seat}
          session={session}
          user={user}
          tariffs={tariffs}
          onClose={() => setSheet(null)}
          onDone={(n) => {
            setSheet(null);
            setNote(n);
            onDone();
          }}
        />
      )}
      {sheet?.kind === 'end' && (
        <EndSheet
          seat={seat}
          session={session}
          user={user}
          tariff={tariff}
          onClose={() => setSheet(null)}
          onDone={(r, text) => {
            setNote({ text, tone: 'ok' });
            // A bill to take or a refund to give back: the sheet opens over the map, which outlives this panel.
            const target = settleOf(r, seat, user);
            setSheet(target && shift.shift ? { kind: 'settle', target } : null);
            onDone();
          }}
        />
      )}
    </>
  );
}

type ExtendChoice = { pkg: true } | { pkg: false; tariffId: string; minutes: number };

/**
 * More time: duration chips with their price on an hourly tariff of the zone, or «Ещё пакет» for a package session
 * (the same package again; the server sells its own minutes). Then one button when the balance pays, else the pay box
 * for the shortfall; a guest pays exactly the price.
 */
function ExtendSheet({
  seat,
  session,
  user,
  tariffs,
  onClose,
  onDone,
}: {
  seat: Seat;
  session: Session;
  user: SeatUser;
  tariffs: Tariff[];
  onClose: () => void;
  onDone: (note: NoteState) => void;
}): JSX.Element {
  const club = useClub();
  const closed = useShiftClosed();
  const guest = user.role === 'guest';
  const current = tariffs.find((x) => x.id === session.tariffId);
  const hourly = useMemo(
    () => zoneTariffsOf(tariffs, seat.pc.zone).filter((tf) => !tf.isPackage),
    [tariffs, seat.pc.zone],
  );
  const [choice, setChoice] = useState<ExtendChoice>(() =>
    current?.isPackage
      ? { pkg: true }
      : { pkg: false, tariffId: current?.id ?? hourly[0]?.id ?? session.tariffId, minutes: 60 },
  );
  const hourlyId = choice.pkg ? (hourly[0]?.id ?? '') : choice.tariffId;
  const [prices, setPrices] = useState<Record<number, PriceQuote>>({});
  const [pkgQuote, setPkgQuote] = useState<PriceQuote | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [requote, setRequote] = useState(0);

  useEffect(() => {
    if (!hourlyId) return undefined;
    let alive = true;
    setPrices({});
    void Promise.all(
      MINUTE_PRESETS.map((m) =>
        clubApi
          .quote({ tariffId: hourlyId, pcId: seat.pc.id, minutes: m, userId: user.id })
          .then((q) => [m, q] as const)
          .catch(() => null),
      ),
    ).then((rows) => {
      if (alive) setPrices(Object.fromEntries(rows.filter((r): r is readonly [number, PriceQuote] => r !== null)));
    });
    return () => {
      alive = false;
    };
  }, [hourlyId, seat.pc.id, user.id, requote]);

  useEffect(() => {
    if (!current?.isPackage) return undefined;
    let alive = true;
    clubApi
      .quote({ tariffId: current.id, pcId: seat.pc.id, minutes: IGNORED_MINUTES, userId: user.id })
      .then((q) => alive && setPkgQuote(q))
      .catch(() => alive && setPkgQuote(null));
    return () => {
      alive = false;
    };
  }, [current?.id, current?.isPackage, seat.pc.id, user.id, requote]);

  const quote = choice.pkg ? pkgQuote : prices[choice.minutes];
  const price = quote?.total.amount;
  const bought = choice.pkg ? (current?.packageMinutes ?? quote?.minutes ?? 0) : choice.minutes;
  const blocked = quote?.rule ? describeRule(quote.rule) : null;
  const shortfall = price === undefined ? 0 : Math.max(0, price - user.balance.amount);
  // Enter extends once the prices are in: the button is off (and so unfocusable) until then.
  const extendButton = useRef<HTMLButtonElement>(null);
  const priced = price !== undefined;
  useEffect(() => {
    if (priced && shortfall === 0 && !guest) extendButton.current?.focus();
  }, [priced, shortfall, guest]);

  const request = (): { pcId: string; minutes: number; tariffId: string } =>
    choice.pkg
      ? { pcId: seat.pc.id, minutes: IGNORED_MINUTES, tariffId: session.tariffId }
      : { pcId: seat.pc.id, minutes: choice.minutes, tariffId: choice.tariffId };

  const tariffName = choice.pkg ? current?.name : hourly.find((x) => x.id === hourlyId)?.name;
  const receipt = (r: SessionResult, p: Payment | null): ReceiptData => ({
    kind: 'extend',
    at: new Date().toISOString(),
    ref: r.payment?.transaction.id ?? r.session.id,
    club: club.clubName,
    cashier: club.staff?.name ?? null,
    client: guest ? null : user.displayName,
    guest,
    pc: seat.pc.name,
    tariff: tariffName ?? null,
    minutes: bought,
    pkg: choice.pkg,
    prepaid: true,
    quote: quote ? { base: quote.base.amount, dayPct: quote.dayPct, discountPct: quote.discountPct } : null,
    total: r.charged.amount,
    paid: p ? { amount: p.amount, method: p.method } : null,
    received: p?.received ?? null,
    balance: guest ? null : (r.balance?.amount ?? null),
  });

  const extend = async (): Promise<NoteState> => {
    const r = await adminApi.extend(request());
    return {
      text: t('Добавлено {time} · списано {sum}', { time: minutesLabel(bought), sum: money(r.charged) }),
      tone: 'ok',
      receipt: receipt(r, null),
    };
  };

  return (
    <Sheet title={t('Продлить · {name}', { name: nameOf(user) })} onClose={onClose}>
      {current?.isPackage && (
        <Button
          className={clsx('!h-auto min-h-11 justify-between py-2', choice.pkg && 'choice-on')}
          aria-pressed={choice.pkg}
          onClick={() => setChoice({ pkg: true })}
        >
          <span>{t('Ещё пакет · {name}', { name: current.name })}</span>
          <span className="text-xs font-normal text-muted">{pkgQuote ? money(pkgQuote.total) : '…'}</span>
        </Button>
      )}
      {hourly.length > 0 && (
        <div className="flex flex-col gap-1.5">
          {(current?.isPackage || hourly.length > 1) && (
            <Field label={t('Почасовой тариф')}>
              <select
                className={inputCls}
                value={hourlyId}
                onChange={(e) =>
                  setChoice({ pkg: false, tariffId: e.target.value, minutes: choice.pkg ? 60 : choice.minutes })
                }
              >
                {hourly.map((tf) => (
                  <option key={tf.id} value={tf.id}>
                    {tf.name} · {money(tf.pricePerHour)}
                    {t(' / ч')}
                  </option>
                ))}
              </select>
            </Field>
          )}
          <div className="grid grid-cols-4 gap-1.5">
            {MINUTE_PRESETS.map((m) => {
              const on = !choice.pkg && m === choice.minutes;
              return (
                <Button
                  key={m}
                  className={clsx('!h-auto min-h-11 flex-col !gap-0 py-1.5', on && 'choice-on')}
                  aria-pressed={on}
                  onClick={() => setChoice({ pkg: false, tariffId: hourlyId, minutes: m })}
                >
                  <span>+{minutesLabel(m)}</span>
                  <span className="text-[0.65rem] font-normal text-muted">
                    {prices[m] === undefined ? '…' : money(prices[m].total)}
                  </span>
                </Button>
              );
            })}
          </div>
        </div>
      )}
      {guest ? <ExactSummary price={price ?? 0} /> : <PaySummary price={price ?? 0} balance={user.balance.amount} />}
      {blocked && <p className="text-xs text-warning">{blocked}</p>}
      {guest || shortfall > 0 ? (
        <PayBox
          key={`${choice.pkg ? 'pkg' : `${hourlyId}|${choice.minutes}`}`}
          exact={guest ? (price ?? 0) : undefined}
          initial={guest ? undefined : shortfall}
          min={guest ? undefined : shortfall}
          verb={t('Продлить')}
          disabled={price === undefined ? t('Считаем цену…') : blocked}
          onPay={async (p) => {
            // One request: the server tops up and extends together, a refused extension books no money.
            try {
              const r = await adminApi.extend({ ...request(), payment: { amount: p.amount, method: p.method } }, p.key);
              onDone({
                text: t('Принято {cash} · добавлено {time}', {
                  cash: moneyExact(p.amount),
                  time: minutesLabel(bought),
                }),
                tone: 'ok',
                receipt: receipt(r, p),
              });
            } catch (e) {
              if (changedAmount(e, 'priceChanged', 'total') !== null) setRequote((n) => n + 1);
              throw e;
            }
          }}
        />
      ) : (
        <>
          <ShiftClosedNote />
          <Button
            ref={extendButton}
            variant="primary"
            className="h-11"
            disabled={busy || closed || price === undefined || blocked !== null}
            onClick={() => {
              setBusy(true);
              setError(null);
              extend()
                .then(onDone)
                .catch((e: unknown) => setError(describe(e)))
                .finally(() => setBusy(false));
            }}
          >
            {t('Продлить на {time}', { time: minutesLabel(bought) })}
            <Kbd>Enter</Kbd>
          </Button>
        </>
      )}
      <Note note={error ? { text: error, tone: 'err' } : null} />
    </Sheet>
  );
}

/** Why a tariff cannot be sold here now. */
function describeRule(rule: 'tariffZone' | 'tariffTime'): string {
  return rule === 'tariffTime' ? t('Тариф сейчас не действует') : t('Тариф не для этой зоны');
}

/**
 * The confirmation of ending: what happens to the money (a member's unused hourly time goes back to the balance, a
 * package's does not, a guest's comes back in cash only from the desk, a postpaid bill is charged now), then what the
 * server actually refunded or charged.
 */
function EndSheet({
  seat,
  session,
  user,
  tariff,
  onClose,
  onDone,
}: {
  seat: Seat;
  session: Session;
  user: SeatUser;
  tariff: Tariff | undefined;
  onClose: () => void;
  onDone: (r: SessionResult, text: string) => void;
}): JSX.Element {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const guest = user.role === 'guest';
  const end = async (): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      const r = await adminApi.end({ pcId: seat.pc.id });
      const parts = [t('Сеанс завершён')];
      if (r.refunded && r.refunded.amount > 0)
        parts.push(
          guest
            ? t('возврат {sum}', { sum: moneyExact(r.refunded.amount) })
            : t('возврат {sum} на баланс', { sum: money(r.refunded) }),
        );
      if (!session.isPrepaid && r.charged.amount > 0) parts.push(t('списано {sum}', { sum: money(r.charged) }));
      onDone(r, parts.join(' · '));
    } catch (e) {
      setError(describe(e));
    } finally {
      setBusy(false);
    }
  };
  const copy = !session.isPrepaid
    ? `${t('К оплате ≈ {sum}', { sum: money(session.cost) })} · ${guest ? t('гость платит на кассе') : t('спишется с баланса')}`
    : tariff?.isPackage
      ? t('Время пакета не возвращается')
      : guest
        ? t('Остаток времени выдаётся наличными только при завершении на кассе')
        : t('Неиспользованное время вернётся на баланс');
  return (
    <Sheet title={t('Завершить сеанс · {pc}', { pc: pcLabel(seat.pc.name) })} onClose={onClose}>
      <p className="text-sm">
        {nameOf(user)} ·{' '}
        {session.isPrepaid
          ? t('осталось {time}', { time: duration(secondsLeft(session)) })
          : t('играет {time}', { time: duration(session.secondsUsed) })}
      </p>
      <p className="rounded-md bg-white/[0.04] px-3 py-2 text-sm text-text">{copy}</p>
      <Note note={error ? { text: error, tone: 'err' } : null} />
      <div className="flex justify-end gap-2 border-t border-line pt-4">
        <Button variant="ghost" autoFocus onClick={onClose}>
          {t('Отмена')}
        </Button>
        <Button variant="danger" className="border border-danger/50" disabled={busy} onClick={() => void end()}>
          {busy ? '…' : t('Завершить сеанс')}
        </Button>
      </div>
    </Sheet>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Settle: a debt taken to the tiyin, or a guest's refund given back in cash
// ---------------------------------------------------------------------------------------------------------------------

/**
 * Over the map, after an end or from «Расчёт с гостями и долги». A debt: the pay box takes exactly the debt
 * (`settleDebt`; a changed debt comes back with the new figure). A guest's refund: «Выдать … наличными», no more than
 * the guest paid in cash and got back (the server's `payable`); money paid by card stays on the account. Both end with
 * «Чек». Closing the sheet leaves the row in the list.
 */
function SettleSheet({
  target,
  onClose,
  onDone,
}: {
  target: SettleTarget;
  onClose: () => void;
  onDone: () => void;
}): JSX.Element {
  const club = useClub();
  const shift = useShift();
  const closed = useShiftClosed();
  const [debt, setDebt] = useState(target.debt);
  const [payable, setPayable] = useState(target.payable);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lost, setLost] = useState(false);
  const [done, setDone] = useState<{ text: string; receipt: ReceiptData } | null>(null);
  const key = useHeldKey();
  const payout = target.debt <= 0;
  const name = target.guest
    ? target.displayName
      ? guestDisplayName(target.displayName)
      : t('Гость')
    : target.displayName;
  const base = {
    at: new Date().toISOString(),
    club: club.clubName,
    cashier: club.staff?.name ?? null,
    client: target.guest ? null : target.displayName,
    guest: target.guest,
    pc: target.pc,
  };

  const give = async (): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      const r = await adminApi.payout({ userId: target.userId, amount: payable }, key.take());
      key.settle();
      setLost(false);
      setDone({
        text: t('Выдано {sum} наличными', { sum: moneyExact(payable) }),
        receipt: { ...base, kind: 'payout', ref: r.transaction.id, total: payable, at: r.transaction.createdAt },
      });
      onDone();
    } catch (e) {
      key.settle(e);
      const next = changedAmount(e, 'payableChanged', 'payable');
      if (next !== null) setPayable(next);
      setLost(isLostAnswer(e));
      setError(
        isLostAnswer(e)
          ? t('Ответ сервера не пришёл: деньги могли пройти. Повторите это же действие — дважды оно не проведётся.')
          : describe(e),
      );
    } finally {
      setBusy(false);
      shift.refresh();
    }
  };

  return (
    <Sheet title={payout ? t('Выдать наличными · {name}', { name }) : t('Долг · {name}', { name })} onClose={onClose}>
      {done ? (
        <>
          <p role="status" className="rounded-md bg-success/10 px-3 py-2 text-sm text-success">
            {done.text}
          </p>
          <div className="grid grid-cols-2 gap-2">
            <ReceiptButton receipt={done.receipt} />
            <Button variant="primary" autoFocus onClick={onClose}>
              {t('Готово')}
            </Button>
          </div>
        </>
      ) : payout ? (
        <>
          <ExactSummary price={payable} label={t('К выдаче')} />
          {target.balance > payable && (
            <p className="text-xs text-muted">
              {t('Остальное ({sum}) оплачено картой или онлайн — наличными не выдаётся', {
                sum: moneyExact(target.balance - payable),
              })}
            </p>
          )}
          <ShiftClosedNote />
          {/* The amount is exact and read-only: Enter on the focused button gives it out. */}
          <Button
            variant="primary"
            className="h-11"
            autoFocus
            disabled={busy || closed || payable <= 0}
            onClick={() => void give()}
          >
            {busy
              ? '…'
              : lost
                ? t('Повторить · {sum}', { sum: moneyExact(payable) })
                : t('Выдать {sum} наличными', { sum: moneyExact(payable) })}
          </Button>
          {error && (
            <p role="alert" className="rounded-md bg-danger/10 px-3 py-2 text-sm text-danger">
              {error}
            </p>
          )}
        </>
      ) : (
        <>
          <ExactSummary price={debt} />
          <PayBox
            exact={debt}
            verb={t('Принять')}
            onPay={async (p) => {
              try {
                const r = await adminApi.settle({ userId: target.userId, amount: p.amount, method: p.method }, p.key);
                setDone({
                  text: t('Долг оплачен · {sum} · {method}', {
                    sum: moneyExact(p.amount),
                    method: methodName(p.method),
                  }),
                  receipt: {
                    ...base,
                    kind: 'debt',
                    at: r.transaction.createdAt,
                    ref: r.transaction.id,
                    total: p.amount,
                    paid: { amount: p.amount, method: p.method },
                    received: p.received,
                    balance: target.guest ? null : r.balance.amount,
                  },
                });
                onDone();
              } catch (e) {
                const next = changedAmount(e, 'debtChanged', 'debt');
                if (next !== null) setDebt(next);
                throw e;
              }
            }}
          />
        </>
      )}
    </Sheet>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Free seat: who (a client or a walk-in guest), how they pay, the tariff or a package — then "Посадить", or the pay box
// ---------------------------------------------------------------------------------------------------------------------

function FreeSeat({ seat, tariffs, header, note, busy, run, onDone, setNote }: PartProps): JSX.Element {
  const closed = useShiftClosed();
  const club = useClub();
  // A server before part 2 has neither the guest route nor postpaid from the desk: both switches stay away then.
  const { cashDesk2 } = useShift();
  const zoneTariffs = useMemo(() => zoneTariffsOf(tariffs, seat.pc.zone), [tariffs, seat.pc.zone]);
  const hourly = useMemo(() => zoneTariffs.filter((tf) => !tf.isPackage), [zoneTariffs]);
  const packages = useMemo(() => zoneTariffs.filter((tf) => tf.isPackage), [zoneTariffs]);
  const [wantGuest, setGuest] = useState(false);
  const guest = wantGuest && cashDesk2;
  const [guestName, setGuestName] = useState('');
  const [who, setWho] = useState<ClientHit | null>(null);
  const [wantPrepaid, setWantPrepaid] = useState(true);
  const [tariffId, setTariffId] = useState('');
  const [minutes, setMinutes] = useState(60);
  const [pkgId, setPkgId] = useState<string | null>(null);
  // Bumped when the server says the price changed: every quote is asked again.
  const [requote, setRequote] = useState(0);
  const openKey = useHeldKey();
  const nameId = useId();

  useEffect(() => {
    setWho(null);
    setMinutes(60);
    setGuest(false);
    setGuestName('');
    setPkgId(null);
    setWantPrepaid(true);
    // The panel stays mounted from PC to PC: a key held for this PC's seat must not go out with another PC's body.
    openKey.reset();
  }, [seat.pc.id, openKey]);
  useEffect(() => {
    if (!tariffId || !hourly.some((tf) => tf.id === tariffId)) {
      setTariffId(hourly[0]?.id ?? '');
    }
  }, [hourly, tariffId]);

  // The server's quote: weekday / holiday price and the best discount (group, loyalty level, happy hour). It is kept
  // with what it priced, so money is never taken on the quote of the previous tariff, time or client. A guest is
  // priced as a walk-in (no client).
  const userId = guest ? null : (who?.id ?? null);
  const hourlyMinutes = wantPrepaid ? minutes : 60;
  const quoteFor = `${seat.pc.id}|${tariffId}|${hourlyMinutes}|${userId ?? ''}|${requote}`;
  const [quoted, setQuoted] = useState<{ key: string; q: PriceQuote | null } | null>(null);
  useEffect(() => {
    if (!tariffId) return undefined;
    let alive = true;
    clubApi
      .quote({ tariffId, pcId: seat.pc.id, minutes: hourlyMinutes, userId })
      .then((q) => alive && setQuoted({ key: quoteFor, q }))
      .catch(() => alive && setQuoted({ key: quoteFor, q: null }));
    return () => {
      alive = false;
    };
  }, [tariffId, hourlyMinutes, userId, seat.pc.id, quoteFor]);
  const hourlyQuote = quoted?.key === quoteFor ? quoted.q : null;

  // Package cards, each priced by the server for this PC and client (cached by pc|tariff|user).
  const [pkgQuotes, setPkgQuotes] = useState<Record<string, PriceQuote | null>>({});
  const pkgKey = (id: string): string => `${seat.pc.id}|${id}|${userId ?? ''}|${requote}`;
  // The overview is polled: its tariff list is a new array every time, the ids are what counts.
  const packageIds = packages.map((p) => p.id).join(',');
  useEffect(() => {
    let alive = true;
    for (const id of packageIds ? packageIds.split(',') : []) {
      const k = `${seat.pc.id}|${id}|${userId ?? ''}|${requote}`;
      if (k in pkgQuotes) continue;
      clubApi
        .quote({ tariffId: id, pcId: seat.pc.id, minutes: IGNORED_MINUTES, userId })
        .then((q) => alive && setPkgQuotes((m) => ({ ...m, [k]: q })))
        .catch(() => alive && setPkgQuotes((m) => ({ ...m, [k]: null })));
    }
    return () => {
      alive = false;
    };
    // `pkgQuotes` is the cache itself: reading it must not refetch.
  }, [packageIds, seat.pc.id, userId, requote]);

  // Postpaid: a guest only when the club allows it; a member while the balance (and the club's member debt limit)
  // covers the first minute — the server refuses otherwise (D-30, D-31).
  const limits = club.limits;
  const memberLimit = limits?.memberDebtLimit ?? 0;
  const postpaidOff: string | null = guest
    ? limits?.guestPostpaid
      ? null
      : t('Постоплата для гостей выключена в настройках клуба')
    : who && hourlyQuote && who.balance.amount + memberLimit < firstMinute(hourlyQuote, hourlyMinutes)
      ? memberLimit > 0
        ? t('Баланс и разрешённый долг не покрывают первую минуту')
        : t('Постоплата — только с баланса, а на нём нет даже на минуту')
      : null;
  const prepaid = wantPrepaid || postpaidOff !== null || !cashDesk2;
  // Forced back to prepaid (the client cannot afford a minute, a guest while guest postpaid is off): the choice follows,
  // so the quote is of the minutes the chips sell, not the 60 a postpaid estimate asks for.
  useEffect(() => {
    if (postpaidOff !== null) setWantPrepaid(true);
  }, [postpaidOff]);

  const pkg = prepaid && pkgId ? packages.find((p) => p.id === pkgId) : undefined;
  const pkgQuote = pkg ? (pkgQuotes[pkgKey(pkg.id)] ?? null) : null;
  const priceQuote = pkg ? pkgQuote : hourlyQuote;
  const quoteFresh = priceQuote !== null;
  const price = priceQuote?.total.amount ?? 0;
  const blockedRule = priceQuote?.rule ? describeRule(priceQuote.rule) : null;
  const selTariffId = pkg ? pkg.id : tariffId;
  const selTariff = pkg ?? hourly.find((x) => x.id === tariffId);
  const sentMinutes = pkg || !prepaid ? IGNORED_MINUTES : minutes;
  const bought = pkg ? (pkg.packageMinutes ?? priceQuote?.minutes ?? 0) : prepaid ? minutes : null;
  // Opening a session debits the client's balance, so what it lacks is taken first, here, as a top-up.
  const shortfall = who && prepaid ? Math.max(0, price - who.balance.amount) : 0;
  const blocked = seat.pc.status === 'maintenance';
  const someone = guest || who !== null;
  const canSeat = someone && !!selTariffId && quoteFresh && !blocked && !blockedRule && busy === null;

  const seatNote = (r: SessionResult, p: Payment | null): NoteState => ({
    text: p
      ? t('Сеанс открыт · принято {cash} · списано {sum}', { cash: moneyExact(p.amount), sum: money(r.charged) })
      : prepaid
        ? t('Сеанс открыт · списано {sum}', { sum: money(r.charged) })
        : t('Сеанс открыт · постоплата'),
    tone: 'ok',
    hint: guest ? guestSignIn(pcLabel(seat.pc.name)) : undefined,
    receipt: {
      kind: 'seat',
      at: r.session.startedAt,
      ref: r.payment?.transaction.id ?? r.session.id,
      club: club.clubName,
      cashier: club.staff?.name ?? null,
      client: guest ? null : (who?.displayName ?? null),
      guest,
      pc: seat.pc.name,
      tariff: selTariff?.name ?? null,
      minutes: bought,
      pkg: pkg !== undefined,
      prepaid,
      quote: priceQuote
        ? { base: priceQuote.base.amount, dayPct: priceQuote.dayPct, discountPct: priceQuote.discountPct }
        : null,
      total: prepaid ? r.charged.amount : null,
      paid: p ? { amount: p.amount, method: p.method } : null,
      received: p?.received ?? null,
      balance: guest ? null : (r.balance?.amount ?? null),
    },
  });

  /** One request: the server tops up (if paid) and opens together, so a refusal books no money. */
  const openWith = async (p: Payment | null, key?: string): Promise<SessionResult> => {
    const payment = p ? { amount: p.amount, method: p.method } : undefined;
    try {
      return guest
        ? await adminApi.openGuestSession(
            {
              pcId: seat.pc.id,
              tariffId: selTariffId,
              minutes: sentMinutes,
              prepaid,
              ...(guestName.trim() ? { displayName: guestName.trim() } : {}),
              ...(payment ? { payment } : {}),
            },
            key ?? p?.key ?? openKey.take(),
          )
        : await adminApi.openSession(
            {
              pcId: seat.pc.id,
              userId: who?.id ?? '',
              tariffId: selTariffId,
              minutes: sentMinutes,
              ...(prepaid ? {} : { prepaid: false }),
              ...(payment ? { payment } : {}),
            },
            p?.key ?? key,
          );
    } catch (e) {
      if (changedAmount(e, 'priceChanged', 'total') !== null) setRequote((n) => n + 1);
      throw e;
    }
  };

  const seatButton = (label: string): JSX.Element => (
    <Button
      variant="primary"
      className="h-12"
      disabled={!canSeat || closed}
      onClick={() =>
        void run('open', async () => {
          // A postpaid guest's seat needs a key: held until a definite answer, so a retry replays.
          const key = guest ? openKey.take() : undefined;
          try {
            const r = await openWith(null, key);
            openKey.settle();
            return seatNote(r, null);
          } catch (e) {
            openKey.settle(e);
            throw e;
          }
        })
      }
    >
      {busy === 'open' ? '…' : label}
    </Button>
  );

  return (
    <>
      {header(t('Посадить на {pc}', { pc: pcLabel(seat.pc.name) }), null)}
      <SeatNote note={note} />

      <section className="flex flex-col gap-4">
        {cashDesk2 && (
          <Choice
            label={t('Кто садится')}
            value={guest ? 'guest' : 'client'}
            options={[
              { id: 'client', label: t('Клиент') },
              { id: 'guest', label: t('Гость') },
            ]}
            onChange={(v) => setGuest(v === 'guest')}
          />
        )}
        {guest ? (
          <div className="flex flex-col gap-1.5">
            <label htmlFor={nameId} className="label">
              {t('Имя')}
            </label>
            <input
              id={nameId}
              className={inputCls}
              value={guestName}
              maxLength={32}
              autoComplete="off"
              placeholder={t('Гость {n}', { n: seat.pc.number })}
              onChange={(e) => setGuestName(e.target.value)}
            />
            <span className="text-xs text-muted">{t('Необязательно. Гость входит кнопкой «Гость» на этом ПК.')}</span>
          </div>
        ) : (
          <ClientPicker label={t('Кто')} value={who} onChange={setWho} />
        )}

        {cashDesk2 && (
          <div className="flex flex-col gap-1.5">
            <Choice
              label={t('Оплата')}
              value={prepaid ? 'pre' : 'post'}
              options={[
                { id: 'pre', label: t('Предоплата') },
                { id: 'post', label: t('Постоплата'), disabled: postpaidOff },
              ]}
              onChange={(v) => setWantPrepaid(v === 'pre')}
            />
            {postpaidOff && (guest || who) && <p className="text-xs text-muted">{postpaidOff}</p>}
          </div>
        )}

        {hourly.length > 0 && (
          <Field label={t('Тариф')}>
            <select
              className={inputCls}
              value={tariffId}
              onChange={(e) => {
                setTariffId(e.target.value);
                setPkgId(null);
              }}
            >
              {hourly.map((tf) => (
                <option key={tf.id} value={tf.id}>
                  {tf.name} · {money(tf.pricePerHour)}
                  {t(' / ч')}
                </option>
              ))}
            </select>
          </Field>
        )}
        {prepaid && (
          <div className="flex flex-col gap-1.5">
            <span className="label">{t('Время')}</span>
            <div className="grid grid-cols-4 gap-1.5">
              {MINUTE_PRESETS.map((m) => {
                const on = !pkg && m === minutes;
                return (
                  <Button
                    key={m}
                    aria-pressed={on}
                    className={clsx(on && 'choice-on')}
                    onClick={() => {
                      setMinutes(m);
                      setPkgId(null);
                    }}
                  >
                    {minutesLabel(m)}
                  </Button>
                );
              })}
            </div>
          </div>
        )}
        {prepaid && packages.length > 0 && (
          <div className="flex flex-col gap-1.5">
            <span className="label">{t('Пакеты')}</span>
            <div className="grid grid-cols-2 gap-1.5">
              {packages.map((p) => {
                const q = pkgQuotes[pkgKey(p.id)];
                const rule = q?.rule ?? null;
                const on = pkgId === p.id;
                const win = windowsOf(p);
                return (
                  <button
                    key={p.id}
                    type="button"
                    aria-pressed={on}
                    disabled={rule !== null}
                    title={rule ? describeRule(rule) : undefined}
                    onClick={() => setPkgId(on ? null : p.id)}
                    className={clsx(
                      'choice focus-ring flex flex-col items-start gap-0.5 rounded-md px-3 py-2 text-left text-sm disabled:cursor-not-allowed disabled:opacity-40',
                      on && 'choice-on',
                    )}
                  >
                    <span className="w-full truncate font-semibold">{p.name}</span>
                    <span className="tnum text-xs text-muted">
                      {minutesLabel(p.packageMinutes ?? q?.minutes ?? 0)}
                      {win ? ` · ${win}` : ''}
                    </span>
                    <span className="tnum text-xs">
                      {q ? (
                        <>
                          {q.base.amount !== q.total.amount && <s className="mr-1.5 text-muted">{money(q.base)}</s>}
                          {money(q.total)}
                        </>
                      ) : (
                        '…'
                      )}
                    </span>
                    {rule && <span className="text-[0.65rem] text-warning">{describeRule(rule)}</span>}
                  </button>
                );
              })}
            </div>
          </div>
        )}

        <div className="flex flex-col gap-1.5">
          {!prepaid ? (
            <p className="rounded-md border border-line bg-bg px-3 py-2.5 text-sm">
              {hourlyQuote ? t('≈ {sum} / ч · оплата в конце', { sum: money(hourlyQuote.total) }) : t('Считаем цену…')}
            </p>
          ) : guest ? (
            <ExactSummary price={price} />
          ) : (
            <PaySummary price={price} balance={who?.balance.amount ?? 0} />
          )}
          {priceQuote && priceQuote.discountPct > 0 && (
            <span className="text-xs text-success">
              −{priceQuote.discountPct}% · {priceQuote.discountReason}
            </span>
          )}
          {priceQuote && priceQuote.dayPct !== 100 && (
            <span className="text-xs text-muted">
              {t('Цена дня')}: {priceQuote.dayPct}%
            </span>
          )}
          {blockedRule && <span className="text-xs text-warning">{blockedRule}</span>}
        </div>

        {prepaid && guest && !blocked ? (
          <PayBox
            key={`${selTariffId}|${sentMinutes}`}
            exact={price}
            verb={t('Посадить гостя')}
            autoFocus={false}
            disabled={quoteFresh ? blockedRule : t('Считаем цену…')}
            onPay={async (p) => {
              const r = await openWith(p);
              setNote(seatNote(r, p));
              onDone();
            }}
          />
        ) : prepaid && who && shortfall > 0 && !blocked ? (
          <PayBox
            initial={shortfall}
            min={shortfall}
            verb={t('Посадить')}
            // Inline in the panel: no focus grab, or the PC number typed for the map would land in the amount.
            autoFocus={false}
            disabled={quoteFresh ? blockedRule : t('Считаем цену…')}
            onPay={async (p) => {
              // One request: the server tops up and opens together, so a refused session (blacklist, curfew, a busy
              // PC, a new price) books no money and the refusal stays in the box.
              const r = await openWith(p);
              setNote(seatNote(r, p));
              onDone();
            }}
          />
        ) : (
          <>
            {someone && <ShiftClosedNote />}
            {seatButton(prepaid ? t('Посадить') : t('Посадить · постоплата'))}
            {!someone && <p className="-mt-2 text-xs text-muted">{t('Выберите клиента')}</p>}
            {blocked && <p className="-mt-2 text-xs text-muted">{t('ПК на обслуживании')}</p>}
          </>
        )}
      </section>

      <div className="mt-auto">
        <TechActions seat={seat} busy={busy !== null} run={run} />
      </div>
    </>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// «Расчёт с гостями и долги»: postpaid bills to take, guests' refunds to give back
// ---------------------------------------------------------------------------------------------------------------------

/**
 * Only what the cashier can act on: debts to take and refunds with cash to give back (`refunds` holds those). Guests
 * whose rest stays on the account (paid by card, nothing payable) are only counted (`kept`): nothing at the desk clears
 * them, and the panel must not sit under the map for good.
 */
function SettleList({
  debts,
  refunds,
  kept,
  onSettle,
}: {
  debts: GuestDebt[];
  refunds: GuestRefund[];
  kept: number;
  onSettle: (target: SettleTarget) => void;
}): JSX.Element {
  const id = useId();
  const where = (pc: string | null, endedAt: string | null): string =>
    [pc ? pcLabel(pc) : null, endedAt ? clock(endedAt) : null].filter(Boolean).join(' · ');
  return (
    <section aria-labelledby={id} className="panel flex shrink-0 flex-col gap-2 p-4">
      <h2 id={id} className="label text-warning">
        {t('Расчёт с гостями и долги')}
      </h2>
      <ul className="flex max-h-56 flex-col divide-y divide-line overflow-y-auto">
        {debts.map((d) => {
          const guest = d.role === undefined || d.role === 'guest';
          return (
            <li key={`debt-${d.userId}`} className="flex items-center justify-between gap-3 py-2">
              <span className="flex min-w-0 flex-col">
                <span className="truncate text-sm text-text">
                  {guest ? guestDisplayName(d.displayName) : d.displayName}
                  <span className="ml-2 text-xs text-muted">{guest ? t('гость') : t('клиент')}</span>
                </span>
                <span className="font-mono text-xs text-muted">{where(d.pc, d.endedAt)}</span>
              </span>
              <Button
                variant="primary"
                onClick={() =>
                  onSettle({
                    userId: d.userId,
                    displayName: d.displayName,
                    guest,
                    debt: d.debt.amount,
                    payable: 0,
                    balance: -d.debt.amount,
                    pc: d.pc,
                  })
                }
              >
                {t('Принять {sum}', { sum: moneyExact(d.debt.amount) })}
              </Button>
            </li>
          );
        })}
        {refunds.map((r) => (
          <li key={`refund-${r.userId}`} className="flex items-center justify-between gap-3 py-2">
            <span className="flex min-w-0 flex-col">
              <span className="truncate text-sm text-text">
                {guestDisplayName(r.displayName)}
                <span className="ml-2 text-xs text-muted">{t('гость')}</span>
              </span>
              <span className="font-mono text-xs text-muted">{where(r.pc, r.endedAt)}</span>
            </span>
            <Button
              onClick={() =>
                onSettle({
                  userId: r.userId,
                  displayName: r.displayName,
                  guest: true,
                  debt: 0,
                  payable: r.payable.amount,
                  balance: r.balance.amount,
                  pc: r.pc,
                })
              }
            >
              {t('Выдать {sum}', { sum: moneyExact(r.payable.amount) })}
            </Button>
          </li>
        ))}
      </ul>
      {kept > 0 && (
        <p className="text-xs text-muted">
          {t('Ещё гостей с остатком, который не выдаётся наличными: {n}', { n: kept })}
        </p>
      )}
    </section>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// App
// ---------------------------------------------------------------------------------------------------------------------

function readPanel(): 'seat' | 'feed' {
  try {
    return localStorage.getItem(PANEL_KEY) === 'feed' ? 'feed' : 'seat';
  } catch {
    return 'seat';
  }
}

function writePanel(v: 'seat' | 'feed'): void {
  try {
    localStorage.setItem(PANEL_KEY, v);
  } catch {
    // private mode: the switch lasts until the reload
  }
}

export function MapPage(): JSX.Element {
  const [data, setData] = useState<Overview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [filter, setFilter] = useState<Filter | null>(null);
  const [sheet, setSheet] = useState<SheetState>(null);
  const [digits, setDigits] = useState('');
  const [tick, setTick] = useState(0);
  const [panel, setPanelState] = useState<'seat' | 'feed'>(readPanel);
  const wide = useWide();
  const shift = useShift();

  const setPanel = (v: 'seat' | 'feed'): void => {
    setPanelState(v);
    writePanel(v);
  };
  /** Picking a seat shows it, whatever the panel showed. */
  const pick = (pcId: string | null): void => {
    setSelected(pcId);
    if (pcId) setPanelState('seat');
  };

  const load = useCallback(async () => {
    try {
      setData(await adminApi.overview());
      setError(null);
    } catch (e) {
      setError(describe(e));
    }
  }, []);

  useEffect(() => {
    void load();
    const poll = setInterval(() => void load(), POLL_MS);
    const clock = setInterval(() => setTick((n) => n + 1), 1000);
    return () => {
      clearInterval(poll);
      clearInterval(clock);
    };
  }, [load]);

  // "Показать ПК" from the top-bar search.
  useEffect(
    () =>
      onShowPc((pcId) => {
        setSelected(pcId);
        setPanelState('seat');
        setSheet(null);
        window.setTimeout(() => document.getElementById(`seat-${pcId}`)?.scrollIntoView({ block: 'nearest' }), 0);
      }),
    [],
  );

  const seats = data?.seats ?? [];
  const seat = seats.find((s) => s.pc.id === selected) ?? null;
  // "Продлить" / "Завершить" belong to one session: another seat, or the session ending meanwhile, drops them. The
  // settle sheet belongs to the map and stays.
  const sessionId = seat?.session?.id ?? null;
  useEffect(() => setSheet((s) => (s?.kind === 'extend' || s?.kind === 'end' ? null : s)), [selected, sessionId]);

  // Keys: digits then Enter pick a PC by number; Esc closes the sheet, then the typed number, then the panel.
  const keys = useRef({ seats, digits, sheet, selected });
  keys.current = { seats, digits, sheet, selected };
  useEffect(() => {
    let timer = 0;
    const on = (e: KeyboardEvent): void => {
      const k = keys.current;
      if (e.key === 'Escape') {
        if (k.sheet) setSheet(null);
        else if (k.digits) setDigits('');
        // In a field the first Esc only leaves it; the next one closes the panel.
        else if (isTyping(e) && e.target instanceof HTMLElement) e.target.blur();
        else if (k.selected) setSelected(null);
        return;
      }
      if (isTyping(e) || sheetOpen() || e.altKey || e.ctrlKey || e.metaKey) return;
      if (/^\d$/.test(e.key)) {
        setDigits((d) => (d + e.key).slice(-3));
        window.clearTimeout(timer);
        timer = window.setTimeout(() => setDigits(''), DIGITS_MS);
      } else if (e.key === 'Enter' && k.digits) {
        e.preventDefault();
        const n = Number(k.digits);
        const hit = k.seats.find((s) => s.pc.number === n);
        if (hit) {
          setSelected(hit.pc.id);
          setPanelState('seat');
          document.getElementById(`seat-${hit.pc.id}`)?.scrollIntoView({ block: 'nearest' });
        }
        setDigits('');
      } else if (e.key === 'Backspace' && k.digits) {
        setDigits((d) => d.slice(0, -1));
      }
    };
    window.addEventListener('keydown', on);
    return () => {
      window.removeEventListener('keydown', on);
      window.clearTimeout(timer);
    };
  }, []);

  const repairs = useMemo(
    () => new Map((data?.repairs ?? []).map((r) => [r.pcId, r.severity] as const)),
    [data?.repairs],
  );
  const zones = useMemo(() => {
    const out = new Map<string, Seat[]>();
    for (const s of seats) {
      out.set(s.pc.zone, [...(out.get(s.pc.zone) ?? []), s]);
    }
    return [...out.entries()];
  }, [seats]);

  // PCs behind the newest version seen in the hall: the check list of a manual update round.
  const outdated = useMemo(() => {
    const reported = seats.filter((x) => x.pc.agentVersion);
    const newest = reported.reduce<string | undefined>(
      (best, x) => (compareVersions(x.pc.agentVersion, best) > 0 ? x.pc.agentVersion : best),
      undefined,
    );
    return {
      newest,
      names: reported.filter((x) => compareVersions(x.pc.agentVersion, newest) < 0).map((x) => x.pc.name),
    };
  }, [seats]);

  const counts = useMemo(() => {
    const c = new Map<Seat['pc']['status'], number>();
    for (const x of seats) {
      c.set(x.pc.status, (c.get(x.pc.status) ?? 0) + 1);
    }
    return c;
  }, [seats]);
  void tick;
  // Recounted every second: "ending soon" moves with the clock.
  const matches = (f: Filter, s: Seat): boolean =>
    FILTERS.find((x) => x.id === f)?.test(s, repairs.has(s.pc.id)) ?? false;
  const occupied = seats.filter((s) => s.session !== null).length;
  const active = filter ? FILTERS.find((x) => x.id === filter) : undefined;
  const debts = data?.guestDebts ?? [];
  const allRefunds = data?.guestRefunds ?? [];
  const refunds = allRefunds.filter((r) => r.payable.amount > 0);
  const showFeedInPanel = !wide && (!seat || panel === 'feed');

  return (
    <div className="flex h-full min-h-0 flex-col gap-3">
      {error && <p className="rounded-md bg-danger/10 px-3 py-1.5 text-sm text-danger">{error}</p>}
      {outdated.names.length > 0 && (
        <p className="rounded-md bg-warning/10 px-3 py-1.5 text-sm text-warning">
          {t('На старой версии ({newest} есть): {list}', {
            newest: outdated.newest ?? '',
            list: outdated.names.join(', '),
          })}
        </p>
      )}
      <div className="grid min-h-0 flex-1 grid-cols-1 gap-5 lg:grid-cols-[minmax(0,1fr)_21rem] xl:grid-cols-[minmax(0,1fr)_26rem] min-[1800px]:grid-cols-[minmax(0,1fr)_26rem_22rem]">
        <div className="flex min-h-0 flex-col gap-5">
          <div className="panel min-h-0 flex-1 overflow-y-auto p-5">
            <header className="mb-5 flex flex-wrap items-center gap-x-5 gap-y-3">
              <h1 className="font-display text-2xl font-light tracking-tight">{t('Карта зала')}</h1>
              <span className="tnum font-mono text-sm text-muted">
                {t('Занято {n}/{total}', { n: occupied, total: seats.length })}
              </span>
              <div role="group" aria-label={t('Фильтр')} className="flex flex-wrap gap-1.5">
                {FILTERS.map((f) => {
                  const n = seats.filter((s) => matches(f.id, s)).length;
                  const on = filter === f.id;
                  return (
                    <button
                      key={f.id}
                      type="button"
                      aria-pressed={on}
                      onClick={() => setFilter(on ? null : f.id)}
                      className={clsx(
                        'choice focus-ring inline-flex h-8 items-center gap-2 rounded-md px-2.5 text-xs font-medium',
                        on && 'choice-on',
                      )}
                    >
                      {t(f.label)}
                      <span className={clsx('tnum font-mono', n > 0 ? 'text-text' : 'text-muted')}>{n}</span>
                    </button>
                  );
                })}
              </div>
              <span className="ml-auto flex items-center gap-2 font-mono text-xs text-muted" aria-live="polite">
                {digits ? (
                  <>
                    <span className="text-accent">{t('ПК {n}', { n: digits })}</span>
                    <Kbd>Enter</Kbd>
                  </>
                ) : (
                  t('номер ПК + Enter')
                )}
              </span>
            </header>
            <div className="flex flex-col gap-6">
              {zones.map(([zone, list]) => (
                <section key={zone} className="flex flex-col gap-2.5">
                  <h2 className="flex items-baseline justify-between gap-2 border-b border-line pb-2">
                    <span className="label text-text">{zone}</span>
                    <span className="tnum font-mono text-xs text-muted">
                      {t('{free}/{total} свободно', {
                        free: list.filter((x) => x.pc.status === 'free').length,
                        total: list.length,
                      })}
                    </span>
                  </h2>
                  <div className="grid grid-cols-[repeat(auto-fill,minmax(6.25rem,1fr))] gap-2">
                    {list.map((x) => (
                      <SeatTile
                        key={x.pc.id}
                        seat={x}
                        repair={repairs.get(x.pc.id)}
                        selected={x.pc.id === selected}
                        dimmed={active !== undefined && !active.test(x, repairs.has(x.pc.id))}
                        onSelect={() => pick(x.pc.id)}
                      />
                    ))}
                  </div>
                </section>
              ))}
              {seats.length === 0 && !error && <p className="text-sm text-muted">{t('Нет данных о ПК')}</p>}
            </div>
          </div>

          {(debts.length > 0 || refunds.length > 0) && (
            <SettleList
              debts={debts}
              refunds={refunds}
              kept={allRefunds.length - refunds.length}
              onSettle={(target) => setSheet({ kind: 'settle', target })}
            />
          )}

          {/* Legend that counts: every status, how many seats are in it right now */}
          <ul className="panel flex shrink-0 flex-wrap items-center gap-x-6 gap-y-2 px-4 py-3">
            {LEGEND_ORDER.map((k) => (
              <li key={k} className="flex items-center gap-2.5 whitespace-nowrap text-sm">
                <span className={clsx('h-3 w-3 shrink-0 rounded-[3px] border-2 bg-transparent', STATUS[k].cell)} />
                <span>{t(STATUS[k].label)}</span>
                <span className="num-dot text-lg leading-none">{String(counts.get(k) ?? 0).padStart(2, '0')}</span>
              </li>
            ))}
          </ul>
        </div>

        <aside className="panel flex min-h-0 flex-col gap-4 p-5">
          {seat && !wide && (
            <Choice
              label={t('Панель')}
              value={panel}
              options={[
                { id: 'seat', label: t('Место') },
                { id: 'feed', label: t('Операции') },
              ]}
              onChange={setPanel}
            />
          )}
          {seat && data && !showFeedInPanel ? (
            <div className="min-h-0 flex-1">
              <SeatPanel
                seat={seat}
                members={data.users}
                tariffs={data.tariffs}
                sheet={sheet}
                setSheet={setSheet}
                onDone={() => void load()}
              />
            </div>
          ) : showFeedInPanel ? (
            <>
              {!seat && (
                <p className="text-sm text-muted">{t('Выберите место: номер ПК и Enter. Ниже — операции смены.')}</p>
              )}
              <OperationsFeed placement="panel" className="flex-1" />
            </>
          ) : (
            <div className="flex h-full flex-col items-center justify-center gap-2 text-center">
              <p className="font-display text-lg tracking-tight">{t('Выберите место')}</p>
              <p className="max-w-[20rem] text-sm text-muted">
                {t('Посадите клиента, продлите, пополните или завершите сеанс. Номер ПК и Enter — выбрать место.')}
              </p>
            </div>
          )}
        </aside>

        {wide && (
          <aside className="panel flex min-h-0 flex-col p-5">
            <OperationsFeed placement="column" className="flex-1" />
          </aside>
        )}
      </div>

      {sheet?.kind === 'topup' && (
        <TopUpSheet
          payee={sheet.payee}
          initial={sheet.initial}
          title={sheet.title}
          onClose={() => setSheet(null)}
          onDone={() => void load()}
        />
      )}
      {sheet?.kind === 'settle' && (
        <SettleSheet
          key={sheet.target.userId}
          target={sheet.target}
          onClose={() => setSheet(null)}
          onDone={() => {
            void load();
            shift.refresh();
          }}
        />
      )}
    </div>
  );
}

export default MapPage;
