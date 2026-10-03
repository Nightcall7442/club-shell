/**
 * The counter ("Карта"): the hall map as numbered tiles per zone — PC number, who is on it, time left — under a header
 * that counts the busy seats and filters the hall (free, ending soon, postpaid, repair, offline), and the selected seat
 * on the right. Seating a client, extending, topping up and ending a session all happen in that panel; every money step
 * ends in the pay box (`paybox.tsx`), whose payment-method button is also the confirmation. Ending a session asks
 * first and then shows what the server refunded or charged. Keys: digits then Enter select a PC by number, F2 tops up
 * the selected client, Esc closes the open sheet, then the panel.
 * Polls `/admin/overview` every 2 s (the real console would follow the server's WebSocket).
 */
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import clsx from 'clsx';
import type { Money, Session } from '@clubshell/contracts';
import {
  adminApi,
  clubApi,
  type ClientHit,
  type Member,
  type Overview,
  type PriceQuote,
  type Seat,
  type SeatUser,
} from '@/api';
import { ClientPicker, pcLabel } from '@/clientSearch';
import { isTyping, onShowPc, sheetOpen } from '@/desk';
import { describe } from '@/errors';
import { t } from '@/i18n';
import { duration, minutesLabel, money } from '@/format';
import { PayBox, ShiftClosedNote, TopUpSheet, useShiftClosed, type Payee } from '@/paybox';
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

type NoteState = { text: string; tone: 'ok' | 'err' } | null;

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

/** The sheet open over the map: one at a time. */
type SheetState =
  | { kind: 'topup'; payee: Payee; initial?: number; title?: string }
  | { kind: 'extend' }
  | { kind: 'end' }
  | null;

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

/** Three lines: the PC number, who is on it, the time left (or the running bill of a postpaid session). */
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
  return (
    <button
      type="button"
      id={`seat-${seat.pc.id}`}
      onClick={onSelect}
      aria-pressed={selected}
      title={`${seat.pc.name} · ${t(s.label)}${seat.user ? ` · ${nameOf(seat.user)}` : ''}${repair ? ` · ${t('Нужен ремонт')}` : ''}`}
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
      <span className="block truncate text-xs leading-tight text-text">{seat.user ? nameOf(seat.user) : ' '}</span>
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

/** Message, lock, reboot, shutdown: behind "Ещё ⋯" so the money actions stay on top. */
function TechActions({
  seat,
  busy,
  run,
}: {
  seat: Seat;
  busy: boolean;
  run: (key: string, fn: () => Promise<string>) => Promise<void>;
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
  // Kept across the busy → free switch, so "Сеанс завершён · возврат …" stays readable after the PC frees up.
  const [note, setNote] = useState<NoteState>(null);
  const [version, setVersion] = useState(0);
  useEffect(() => setNote(null), [seat.pc.id]);

  const done = (): void => {
    onDone();
    shift.refresh();
    setVersion((v) => v + 1);
  };

  const run = async (key: string, fn: () => Promise<string>): Promise<void> => {
    setBusy(key);
    setNote(null);
    try {
      setNote({ text: await fn(), tone: 'ok' });
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
  run: (key: string, fn: () => Promise<string>) => Promise<void>;
  onDone: () => void;
  setNote: (n: NoteState) => void;
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
  const hit = useClientHit(user, username, version);
  const left = secondsLeft(session);
  const tariff = tariffs.find((x) => x.id === session.tariffId);
  const payee: Payee = { id: user.id, displayName: nameOf(user), balance: user.balance, bonus: hit?.bonus ?? null };
  const topUp = (): void => setSheet({ kind: 'topup', payee });
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

  return (
    <>
      {header(
        nameOf(user),
        <span className="font-mono text-xs text-muted">
          {[seat.pc.name, hit?.phoneTail ? `••${hit.phoneTail}` : null, hit ? `@${hit.username}` : null]
            .filter(Boolean)
            .join(' · ')}
        </span>,
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
        <p className="-mt-2 text-xs text-muted">{t('Постоплата: сумма спишется с баланса, когда сеанс закончится.')}</p>
      )}

      <Note note={note} />

      <section className={clsx('grid gap-1.5', session.isPrepaid ? 'grid-cols-2' : 'grid-cols-1')}>
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
        <Button className="h-12" disabled={busy !== null} onClick={topUp}>
          {t('Пополнить')}
          <Kbd>F2</Kbd>
        </Button>
      </section>

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
          onClose={() => setSheet(null)}
          onDone={(text) => {
            setSheet(null);
            setNote({ text, tone: 'ok' });
            onDone();
          }}
        />
      )}
      {sheet?.kind === 'end' && (
        <EndSheet
          seat={seat}
          session={session}
          user={user}
          onClose={() => setSheet(null)}
          onDone={(text) => {
            setSheet(null);
            setNote({ text, tone: 'ok' });
            onDone();
          }}
        />
      )}
    </>
  );
}

/** Duration chips with their price, then one button when the balance pays, else the pay box for the shortfall. */
function ExtendSheet({
  seat,
  session,
  user,
  onClose,
  onDone,
}: {
  seat: Seat;
  session: Session;
  user: SeatUser;
  onClose: () => void;
  onDone: (text: string) => void;
}): JSX.Element {
  const closed = useShiftClosed();
  const [minutes, setMinutes] = useState(60);
  const [prices, setPrices] = useState<Record<number, number>>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    let alive = true;
    void Promise.all(
      MINUTE_PRESETS.map((m) =>
        clubApi
          .quote({ tariffId: session.tariffId, pcId: seat.pc.id, minutes: m, userId: user.id })
          .then((q) => [m, q.total.amount] as const)
          .catch(() => null),
      ),
    ).then((rows) => {
      if (alive) setPrices(Object.fromEntries(rows.filter((r): r is readonly [number, number] => r !== null)));
    });
    return () => {
      alive = false;
    };
  }, [session.tariffId, seat.pc.id, user.id]);

  const price = prices[minutes];
  const shortfall = price === undefined ? 0 : Math.max(0, price - user.balance.amount);
  // Enter extends once the prices are in: the button is off (and so unfocusable) until then.
  const extendButton = useRef<HTMLButtonElement>(null);
  const priced = price !== undefined;
  useEffect(() => {
    if (priced && shortfall === 0) extendButton.current?.focus();
  }, [priced, shortfall]);

  const extend = async (): Promise<string> => {
    const r = await adminApi.extend({ pcId: seat.pc.id, minutes });
    return t('Добавлено {time} · списано {sum}', { time: minutesLabel(minutes), sum: money(r.charged) });
  };

  return (
    <Sheet title={t('Продлить · {name}', { name: nameOf(user) })} onClose={onClose}>
      <div className="grid grid-cols-4 gap-1.5">
        {MINUTE_PRESETS.map((m) => (
          <Button
            key={m}
            className={clsx('!h-auto min-h-11 flex-col !gap-0 py-1.5', m === minutes && 'choice-on')}
            aria-pressed={m === minutes}
            onClick={() => setMinutes(m)}
          >
            <span>+{minutesLabel(m)}</span>
            <span className="text-[0.65rem] font-normal text-muted">
              {prices[m] === undefined ? '…' : money(uzs(prices[m]))}
            </span>
          </Button>
        ))}
      </div>
      <PaySummary price={price ?? 0} balance={user.balance.amount} />
      {shortfall > 0 ? (
        <PayBox
          initial={shortfall}
          min={shortfall}
          verb={t('Продлить')}
          onPay={async (p) => {
            // One request: the server tops up and extends together, a refused extension books no money.
            await adminApi.extend({ pcId: seat.pc.id, minutes, payment: { amount: p.amount, method: p.method } });
            onDone(t('Принято {cash} · добавлено {time}', { cash: money(uzs(p.amount)), time: minutesLabel(minutes) }));
          }}
        />
      ) : (
        <>
          <ShiftClosedNote />
          <Button
            ref={extendButton}
            variant="primary"
            className="h-11"
            disabled={busy || closed || price === undefined}
            onClick={() => {
              setBusy(true);
              setError(null);
              extend()
                .then(onDone)
                .catch((e: unknown) => setError(describe(e)))
                .finally(() => setBusy(false));
            }}
          >
            {t('Продлить на {time}', { time: minutesLabel(minutes) })}
            <Kbd>Enter</Kbd>
          </Button>
        </>
      )}
      <Note note={error ? { text: error, tone: 'err' } : null} />
    </Sheet>
  );
}

/** The confirmation of ending: what happens to the money, then what the server actually refunded or charged. */
function EndSheet({
  seat,
  session,
  user,
  onClose,
  onDone,
}: {
  seat: Seat;
  session: Session;
  user: SeatUser;
  onClose: () => void;
  onDone: (text: string) => void;
}): JSX.Element {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const end = async (): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      const r = await adminApi.end({ pcId: seat.pc.id });
      const parts = [t('Сеанс завершён')];
      if (r.refunded && r.refunded.amount > 0) parts.push(t('возврат {sum} на баланс', { sum: money(r.refunded) }));
      if (!session.isPrepaid && r.charged.amount > 0) parts.push(t('списано {sum}', { sum: money(r.charged) }));
      onDone(parts.join(' · '));
    } catch (e) {
      setError(describe(e));
    } finally {
      setBusy(false);
    }
  };
  return (
    <Sheet title={t('Завершить сеанс · {pc}', { pc: pcLabel(seat.pc.name) })} onClose={onClose}>
      <p className="text-sm">
        {nameOf(user)} ·{' '}
        {session.isPrepaid
          ? t('осталось {time}', { time: duration(secondsLeft(session)) })
          : t('играет {time}', { time: duration(session.secondsUsed) })}
      </p>
      <p className="rounded-md bg-white/[0.04] px-3 py-2 text-sm text-text">
        {session.isPrepaid
          ? t('Неиспользованное время вернётся на баланс')
          : t('К оплате {sum}, спишется с баланса', { sum: money(session.cost) })}
      </p>
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
// Free seat: who, tariff, time, what it costs — then "Посадить", or the pay box for what the balance lacks
// ---------------------------------------------------------------------------------------------------------------------

function FreeSeat({ seat, tariffs, header, note, busy, run, onDone, setNote }: PartProps): JSX.Element {
  const closed = useShiftClosed();
  const zoneTariffs = useMemo(
    () =>
      tariffs.filter(
        (tf) => tf.zones.length === 0 || tf.zones.some((z) => z.toLowerCase() === seat.pc.zone.toLowerCase()),
      ),
    [tariffs, seat.pc.zone],
  );
  const [who, setWho] = useState<ClientHit | null>(null);
  const [tariffId, setTariffId] = useState('');
  const [minutes, setMinutes] = useState(60);

  useEffect(() => {
    setWho(null);
    setMinutes(60);
  }, [seat.pc.id]);
  useEffect(() => {
    if (!tariffId || !zoneTariffs.some((tf) => tf.id === tariffId)) {
      setTariffId(zoneTariffs[0]?.id ?? '');
    }
  }, [zoneTariffs, tariffId]);

  const tariff = zoneTariffs.find((x) => x.id === tariffId);
  // The server's quote: weekday / holiday price and the best discount (group, loyalty level, happy hour). It is kept
  // with what it priced, so money is never taken on the quote of the previous tariff, time or client.
  const userId = who?.id ?? null;
  const quoteFor = `${seat.pc.id}|${tariffId}|${minutes}|${userId ?? ''}`;
  const [quoted, setQuoted] = useState<{ key: string; q: PriceQuote | null } | null>(null);
  useEffect(() => {
    if (!tariffId) return undefined;
    let alive = true;
    clubApi
      .quote({ tariffId, pcId: seat.pc.id, minutes, userId })
      .then((q) => alive && setQuoted({ key: quoteFor, q }))
      .catch(() => alive && setQuoted({ key: quoteFor, q: null }));
    return () => {
      alive = false;
    };
  }, [tariffId, minutes, userId, seat.pc.id, quoteFor]);
  const priceQuote = quoted?.q ?? null;
  const quoteFresh = quoted?.key === quoteFor && priceQuote !== null;
  const price = priceQuote?.total.amount ?? 0;
  // Opening a session debits the client's balance, so what it lacks is taken first, here, as a top-up.
  const shortfall = who ? Math.max(0, price - who.balance.amount) : 0;
  const blocked = seat.pc.status === 'maintenance';
  const canSeat = who !== null && !!tariffId && quoteFresh && !blocked && busy === null;

  const open = async (): Promise<string> => {
    const r = await adminApi.openSession({ pcId: seat.pc.id, userId: who?.id ?? '', tariffId, minutes });
    return t('Сеанс открыт · списано {sum}', { sum: money(r.charged) });
  };

  return (
    <>
      {header(t('Посадить на {pc}', { pc: pcLabel(seat.pc.name) }), null)}
      <Note note={note} />

      <section className="flex flex-col gap-4">
        <ClientPicker label={t('Кто')} value={who} onChange={setWho} />
        <Field label={t('Тариф')}>
          <select className={inputCls} value={tariffId} onChange={(e) => setTariffId(e.target.value)}>
            {zoneTariffs.map((tf) => (
              <option key={tf.id} value={tf.id}>
                {tf.name} · {money(tf.isPackage ? (tf.packagePrice ?? tf.pricePerHour) : tf.pricePerHour)}
                {tf.isPackage ? '' : t(' / ч')}
              </option>
            ))}
          </select>
        </Field>
        {!tariff?.isPackage && (
          <div className="flex flex-col gap-1.5">
            <span className="label">{t('Время')}</span>
            <div className="grid grid-cols-4 gap-1.5">
              {MINUTE_PRESETS.map((m) => (
                <Button
                  key={m}
                  aria-pressed={m === minutes}
                  className={clsx(m === minutes && 'choice-on')}
                  onClick={() => setMinutes(m)}
                >
                  {minutesLabel(m)}
                </Button>
              ))}
            </div>
          </div>
        )}

        <div className="flex flex-col gap-1.5">
          <PaySummary price={price} balance={who?.balance.amount ?? 0} />
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
        </div>

        {who && shortfall > 0 && !blocked ? (
          <PayBox
            initial={shortfall}
            min={shortfall}
            verb={t('Посадить')}
            // Inline in the panel: no focus grab, or the PC number typed for the map would land in the amount.
            autoFocus={false}
            disabled={quoteFresh ? null : t('Считаем цену…')}
            onPay={async (p) => {
              // One request: the server tops up and opens together, so a refused session (blacklist, curfew, a busy
              // PC, a new price) books no money and the refusal stays in the box.
              const r = await adminApi.openSession({
                pcId: seat.pc.id,
                userId: who.id,
                tariffId,
                minutes,
                payment: { amount: p.amount, method: p.method },
              });
              setNote({
                text: t('Сеанс открыт · принято {cash} · списано {sum}', {
                  cash: money(uzs(p.amount)),
                  sum: money(r.charged),
                }),
                tone: 'ok',
              });
              onDone();
            }}
          />
        ) : (
          <>
            {who && <ShiftClosedNote />}
            <Button
              variant="primary"
              className="h-12"
              disabled={!canSeat || closed}
              onClick={() => void run('open', open)}
            >
              {busy === 'open' ? '…' : t('Посадить')}
            </Button>
            {!who && <p className="-mt-2 text-xs text-muted">{t('Выберите клиента')}</p>}
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
// Guest debts (postpaid guests, settings "Постоплата для гостей")
// ---------------------------------------------------------------------------------------------------------------------

function GuestDebts({
  debts,
  onCollect,
}: {
  debts: NonNullable<Overview['guestDebts']>;
  /** Opens the pay box for exactly the debt: a top-up of it brings the guest's balance back to zero. */
  onCollect: (d: NonNullable<Overview['guestDebts']>[number]) => void;
}): JSX.Element {
  return (
    <section className="panel flex shrink-0 flex-col gap-2 p-4">
      <h2 className="label text-warning">{t('Долги гостей')}</h2>
      <ul className="flex flex-col divide-y divide-line">
        {debts.map((d) => (
          <li key={d.userId} className="flex items-center justify-between gap-3 py-2">
            <span className="flex min-w-0 flex-col">
              <span className="truncate text-sm text-text">{d.displayName}</span>
              <span className="font-mono text-xs text-muted">
                {[d.pc, d.endedAt ? clock(d.endedAt) : null].filter(Boolean).join(' · ')}
              </span>
            </span>
            <Button variant="primary" onClick={() => onCollect(d)}>
              {t('Принять {sum}', { sum: money(d.debt) })}
            </Button>
          </li>
        ))}
      </ul>
    </section>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// App
// ---------------------------------------------------------------------------------------------------------------------

export function MapPage(): JSX.Element {
  const [data, setData] = useState<Overview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [filter, setFilter] = useState<Filter | null>(null);
  const [sheet, setSheet] = useState<SheetState>(null);
  const [digits, setDigits] = useState('');
  const [tick, setTick] = useState(0);

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
        setSheet(null);
        window.setTimeout(() => document.getElementById(`seat-${pcId}`)?.scrollIntoView({ block: 'nearest' }), 0);
      }),
    [],
  );

  const seats = data?.seats ?? [];
  const seat = seats.find((s) => s.pc.id === selected) ?? null;
  // "Продлить" / "Завершить" belong to one session: another seat, or the session ending meanwhile, drops them.
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
      <div className="grid min-h-0 flex-1 grid-cols-1 gap-5 lg:grid-cols-[minmax(0,1fr)_21rem] xl:grid-cols-[minmax(0,1fr)_26rem]">
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
                        onSelect={() => setSelected(x.pc.id)}
                      />
                    ))}
                  </div>
                </section>
              ))}
              {seats.length === 0 && !error && <p className="text-sm text-muted">{t('Нет данных о ПК')}</p>}
            </div>
          </div>

          {data?.guestDebts && data.guestDebts.length > 0 && (
            <GuestDebts
              debts={data.guestDebts}
              onCollect={(d) =>
                setSheet({
                  kind: 'topup',
                  payee: { id: d.userId, displayName: d.displayName, balance: { ...d.debt, amount: -d.debt.amount } },
                  initial: d.debt.amount,
                  title: t('Долг · {name}', { name: d.displayName }),
                })
              }
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

        <aside className="panel min-h-0 p-5">
          {seat && data ? (
            <SeatPanel
              seat={seat}
              members={data.users}
              tariffs={data.tariffs}
              sheet={sheet}
              setSheet={setSheet}
              onDone={() => void load()}
            />
          ) : (
            <div className="flex h-full flex-col items-center justify-center gap-2 text-center">
              <p className="font-display text-lg tracking-tight">{t('Выберите место')}</p>
              <p className="max-w-[20rem] text-sm text-muted">
                {t('Посадите клиента, продлите, пополните или завершите сеанс. Номер ПК и Enter — выбрать место.')}
              </p>
            </div>
          )}
        </aside>
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
    </div>
  );
}

export default MapPage;
