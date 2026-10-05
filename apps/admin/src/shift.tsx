/**
 * The cash shift as the whole console sees it: whether one is open, its X totals and the cash the drawer should hold
 * (the top-bar chip), the gate that asks to open one, and the drawer's own moves. Money is taken only in an open shift
 * (the server answers 409 `shiftClosed` otherwise), so after sign-in or a reload without a shift the console shows a
 * blocking "Открыть смену" with the drawer float of the last closed shift; the owner may put it off ("Позже"), a cashier
 * cannot. Money buttons ask for it again through {@link ShiftState.requestOpen}. Polls `/admin/shift` every 30 s and on
 * {@link ShiftState.refresh} after a money action; {@link ShiftState.version} counts the answers, so the operations feed
 * refetches with them.
 *
 * The «±» menu next to the chip puts cash into the drawer or takes it out ({@link CashMoveSheet}: amount, reason, note,
 * then the slip) and prints the X report from figures fetched at that moment.
 */
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import clsx from 'clsx';
import {
  AdminError,
  clubApi,
  expectedOf,
  newKey,
  type CashMoveKind,
  type CashMovement,
  type CashReason,
  type Shift,
  type ShiftTotals,
  type StaffMember,
} from '@/api';
import { useClub } from '@/club';
import { describe, isLostAnswer } from '@/errors';
import { money, moneyExact } from '@/format';
import { t } from '@/i18n';
import { REASONS, REASON_LABEL } from '@/labels';
import { CashSlip, ShiftReport, printDocument, type ReportMove } from '@/print';
import { PlusMinusIcon } from '@/icons';
import { Button, Field, MoneyInput, Note, Sheet, StatusDot, inputCls } from '@/ui';

const POLL_MS = 30_000;
const nf = new Intl.NumberFormat('ru-RU');
/** Most a single move can be, minor units (the server's limit). */
const MAX_MOVE = 10_000_000_000;

export interface ShiftState {
  /** False until the first answer: nothing is gated on a shift the console has not heard about yet. */
  loaded: boolean;
  shift: Shift | null;
  x: ShiftTotals | null;
  /** What the open shift's drawer should hold now: the server's figure, else float + cash top-ups (older server). */
  expectedCash: number | null;
  /** Counted cash of the last closed shift: what the drawer should hold when the next one opens. */
  lastClosingCash: number | null;
  /** Grows with every answer of `/admin/shift`: what depends on the shift's money refetches when it changes. */
  version: number;
  /**
   * False when the server predates cash desk part 2 (its operations feed answers 404): the console then hides the
   * drawer's moves, the feed, walk-in guests and postpaid seats, which such a server would refuse or misread.
   */
  cashDesk2: boolean;
  refresh: () => void;
  /** Shows the "Открыть смену" sheet (from a money button that the closed shift blocks). */
  requestOpen: () => void;
  /** Opens the cash-in / cash-out sheet. */
  requestCashMove: (kind: CashMoveKind) => void;
}

const ShiftContext = createContext<ShiftState>({
  loaded: false,
  shift: null,
  x: null,
  expectedCash: null,
  lastClosingCash: null,
  version: 0,
  cashDesk2: true,
  refresh: () => undefined,
  requestOpen: () => undefined,
  requestCashMove: () => undefined,
});

export function useShift(): ShiftState {
  return useContext(ShiftContext);
}

/**
 * Cash and cashless money of the shift: top-ups by method when the server splits them (else its old two totals), plus
 * the bar's money by method (cash desk part 3).
 */
export function cashSplit(x: ShiftTotals): { cash: number; cashless: number } {
  const m = x.topUpByMethod;
  const s = x.shopByMethod;
  const shopCash = s?.cash ?? 0;
  const shopCashless = s ? s.card + s.payme + s.click + s.uzum : 0;
  if (!m) return { cash: x.topUpCash + shopCash, cashless: x.topUpOther + shopCashless };
  return { cash: m.cash + shopCash, cashless: m.card + m.payme + m.click + m.uzum + m.other + shopCashless };
}

/**
 * The X report of the open shift, fetched now (not the chip's last poll), with the drawer's moves; then printed. Throws
 * «Смена не открыта» when the shift was closed meanwhile (another console, the owner's phone).
 */
export async function printX(club: string | null): Promise<void> {
  const state = await clubApi.shift();
  if (!state.shift || !state.x) throw new Error(t('Смена не открыта'));
  const moves = await drawerMoves(state.shift.id);
  await printDocument(
    <ShiftReport
      r={{
        type: 'X',
        club,
        shift: state.shift,
        x: state.x,
        expected: state.expectedCash ?? null,
        moves,
        at: new Date().toISOString(),
      }}
    />,
    'report',
  );
}

/** Cash in / out and payouts of a shift, oldest first, for the X / Z slip; none from a server without the feed. */
export async function drawerMoves(shiftId: string): Promise<ReportMove[]> {
  try {
    const out: ReportMove[] = [];
    let before: string | null = null;
    // A shift has a handful of moves; a few pages at most.
    for (let page = 0; page < 10; page += 1) {
      const r = await clubApi.operations({ shiftId, before, kinds: ['cashIn', 'cashOut', 'payout'], limit: 200 });
      for (const op of r.items) {
        if (op.kind === 'cashIn' || op.kind === 'cashOut' || op.kind === 'payout') {
          out.push({
            at: op.at,
            kind: op.kind,
            amount: op.amount,
            reasonCode: op.reasonCode,
            note: op.note,
            who: op.kind === 'payout' ? (op.client?.displayName ?? null) : op.staffName,
          });
        }
      }
      if (!r.next) break;
      before = r.next;
    }
    return out.reverse();
  } catch {
    return [];
  }
}

export function ShiftProvider({
  staff,
  onSignOut,
  children,
}: {
  staff: StaffMember;
  onSignOut: () => void;
  children: ReactNode;
}): JSX.Element {
  const [loaded, setLoaded] = useState(false);
  const [shift, setShift] = useState<Shift | null>(null);
  const [x, setX] = useState<ShiftTotals | null>(null);
  const [expectedCash, setExpectedCash] = useState<number | null>(null);
  const [lastClosingCash, setLastClosingCash] = useState<number | null>(null);
  const [version, setVersion] = useState(0);
  // 'auto' — no shift at sign-in (blocking for a cashier); 'asked' — a money button asked for it (always closable).
  const [gate, setGate] = useState<'auto' | 'asked' | null>(null);
  const [cashMove, setCashMove] = useState<CashMoveKind | null>(null);
  // A drawer move whose answer was lost outlives its sheet: reopened, the sheet offers only that move again (D-46).
  const [pendingMove, setPendingMove] = useState<PendingMove | null>(null);
  const checked = useRef(false);

  const refresh = useCallback(async (): Promise<void> => {
    try {
      const r = await clubApi.shift();
      setShift(r.shift);
      setX(r.x);
      setExpectedCash(r.shift && r.x ? (r.expectedCash ?? expectedOf(r.shift.openingCash, r.x)) : null);
      // History is newest first.
      setLastClosingCash(r.history[0]?.closingCash ?? null);
      setLoaded(true);
      setVersion((v) => v + 1);
      if (r.shift) setGate(null);
      else if (!checked.current) setGate('auto');
      checked.current = true;
    } catch {
      // the pages show their own errors; the chip keeps the last answer
    }
  }, []);

  useEffect(() => {
    void refresh();
    const id = window.setInterval(() => void refresh(), POLL_MS);
    return () => window.clearInterval(id);
  }, [refresh]);

  // One probe at sign-in: a server without the operations feed has none of part 2 (a new console on an old server).
  const [cashDesk2, setCashDesk2] = useState(true);
  useEffect(() => {
    clubApi.operations({ limit: 1 }).catch((e: unknown) => {
      if (e instanceof AdminError && e.status === 404 && e.details?.['what'] !== 'shift') setCashDesk2(false);
    });
  }, []);

  const value = useMemo<ShiftState>(
    () => ({
      loaded,
      shift,
      x,
      expectedCash,
      lastClosingCash,
      version,
      cashDesk2,
      refresh: () => void refresh(),
      requestOpen: () => setGate('asked'),
      requestCashMove: (kind) => setCashMove(kind),
    }),
    [loaded, shift, x, expectedCash, lastClosingCash, version, cashDesk2, refresh],
  );

  const owner = staff.role === 'owner';
  return (
    <ShiftContext.Provider value={value}>
      {children}
      {gate && !shift && (
        <ShiftGate
          staffName={staff.name}
          float={lastClosingCash}
          onOpened={() => void refresh()}
          onLater={gate === 'asked' || owner ? () => setGate(null) : undefined}
          laterLabel={gate === 'asked' ? t('Отмена') : t('Позже')}
          onSignOut={gate === 'auto' && !owner ? onSignOut : undefined}
        />
      )}
      {cashMove && (
        <CashMoveSheet
          initialKind={cashMove}
          pending={pendingMove}
          onPending={setPendingMove}
          onClose={() => setCashMove(null)}
          onDone={() => void refresh()}
        />
      )}
    </ShiftContext.Provider>
  );
}

function ShiftGate({
  staffName,
  float,
  onOpened,
  onLater,
  laterLabel,
  onSignOut,
}: {
  staffName: string;
  float: number | null;
  onOpened: () => void;
  /** Absent for a cashier at sign-in: the shift must be opened (or the cashier signs out). */
  onLater?: () => void;
  laterLabel: string;
  onSignOut?: () => void;
}): JSX.Element {
  const [opening, setOpening] = useState(float ?? 0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // The history may arrive after the sheet: take the float then, unless the cashier already typed something.
  const touched = useRef(false);
  useEffect(() => {
    if (!touched.current && float !== null) setOpening(float);
  }, [float]);

  const open = async (): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      await clubApi.openShift(opening);
      onOpened();
    } catch (e) {
      setError(describe(e));
      // Someone else opened it meanwhile: the refresh closes the sheet.
      onOpened();
    } finally {
      setBusy(false);
    }
  };

  return (
    <Sheet title={t('Открыть смену')} onClose={onLater}>
      <p className="text-sm text-muted">
        {t('Деньги принимаются только в открытую смену. Кассир: {name}.', { name: staffName })}
      </p>
      <div
        onKeyDown={(e) => {
          if (e.key === 'Enter' && !busy) void open();
        }}
      >
        <Field
          label={t('Наличные в кассе на начало')}
          hint={
            float !== null
              ? t('При закрытии прошлой смены: {sum}', { sum: money({ amount: float, currency: 'UZS' }) })
              : undefined
          }
        >
          <MoneyInput
            value={opening}
            onChange={(v) => {
              touched.current = true;
              setOpening(v);
            }}
            disabled={busy}
          />
        </Field>
      </div>
      <Note note={error ? { text: error, tone: 'err' } : null} />
      <div className="flex flex-wrap items-center justify-end gap-2 border-t border-line pt-4">
        {onSignOut && (
          <Button variant="ghost" className="mr-auto" onClick={onSignOut}>
            {t('Выйти')}
          </Button>
        )}
        {onLater && (
          <Button variant="ghost" onClick={onLater}>
            {laterLabel}
          </Button>
        )}
        <Button variant="primary" disabled={busy} onClick={() => void open()}>
          {t('Открыть смену')}
        </Button>
      </div>
    </Sheet>
  );
}

/**
 * Header chip: a status dot, «Смена · {name}» and the mono word «открыта»; a click goes to the Смена page. Its name and
 * tooltip are the full line with the cash the drawer should hold and the cashless taken so far (the KPI strip shows the
 * figures; owner pages without the strip still have them on hover). A closed shift reads «Смена не открыта».
 */
export function ShiftChip({ onClick }: { onClick: () => void }): JSX.Element {
  const { loaded, shift, x, expectedCash } = useShift();
  const split = x ? cashSplit(x) : null;
  const who = shift ? t('Смена · {name}', { name: shift.staffName }) : t('Смена не открыта');
  const sums = split
    ? `${t('в кассе {sum}', { sum: nf.format(Math.round((expectedCash ?? split.cash) / 100)) })} · ${t('безнал {sum}', {
        sum: nf.format(Math.round(split.cashless / 100)),
      })}`
    : null;
  const full = sums ? `${who} · ${sums}` : who;
  return (
    <button
      type="button"
      onClick={onClick}
      aria-label={full}
      title={full}
      className="focus-ring -mx-2 flex h-9 min-w-0 shrink items-center gap-2.5 whitespace-nowrap rounded-md px-2 text-[13px] font-medium text-text hover:bg-text/[0.04]"
    >
      <StatusDot tone={shift ? 'ok' : loaded ? 'danger' : 'muted'} />
      <span className="truncate">{who}</span>
      {shift && (
        <span aria-hidden="true" className="label-sm hidden min-[1280px]:inline">
          {t('открыта')}
        </span>
      )}
    </button>
  );
}

/**
 * Cash into the drawer, cash out of it, the X report on paper; only in an open shift. `kpi` — the 44 px tool button of
 * the «В кассе» card (icon and words, the icon alone below 1400 px); `compact` — the «±» of the header on the owner's
 * setup pages. Either way it is named «Внесение и изъятие» and opens the same menu.
 */
export function CashMenu({ variant = 'compact' }: { variant?: 'kpi' | 'compact' }): JSX.Element | null {
  const { shift, requestCashMove, cashDesk2 } = useShift();
  const club = useClub();
  const [open, setOpen] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open && !error) return undefined;
    const close = (e: MouseEvent): void => {
      if (box.current && !box.current.contains(e.target as Node)) {
        setOpen(false);
        setError(null);
      }
    };
    window.addEventListener('mousedown', close);
    return () => window.removeEventListener('mousedown', close);
  }, [open, error]);
  // A failed «Печать X» says so for a while, then gets out of the way.
  useEffect(() => {
    if (!error) return undefined;
    const id = window.setTimeout(() => setError(null), 8000);
    return () => window.clearTimeout(id);
  }, [error]);
  if (!shift || !cashDesk2) return null;
  const item = (label: string, act: () => void): JSX.Element => (
    <button
      type="button"
      role="menuitem"
      className="focus-ring-inset flex h-11 w-full items-center px-4 text-left text-sm text-text hover:bg-accent/[0.06]"
      onClick={() => {
        setOpen(false);
        act();
      }}
    >
      {label}
    </button>
  );
  const kpi = variant === 'kpi';
  // Under the button, kept inside the screen: the KPI card's popover opens to the left of its right edge.
  const below = kpi ? 'right-0 top-[calc(100%+8px)]' : 'left-0 top-[calc(100%+8px)]';
  return (
    <div ref={box} className="relative">
      <button
        type="button"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label={t('Внесение и изъятие')}
        title={t('Внесение и изъятие')}
        onClick={() => {
          setError(null);
          setOpen((v) => !v);
        }}
        onKeyDown={(e) => {
          if (e.key === 'Escape') {
            setOpen(false);
            setError(null);
          }
        }}
        className={clsx(
          'btn-utility focus-ring inline-flex items-center justify-center rounded-md',
          kpi
            ? 'h-11 gap-2 whitespace-nowrap px-3 text-[13px] font-medium min-[1400px]:px-3.5'
            : 'h-9 w-9 font-mono text-sm text-dim',
        )}
      >
        {kpi ? (
          <>
            <PlusMinusIcon size={16} strokeWidth={1.7} />
            <span className="hidden min-[1400px]:inline">{t('Внесение и изъятие')}</span>
          </>
        ) : (
          '±'
        )}
      </button>
      {open && (
        <div
          role="menu"
          aria-label={t('Касса')}
          className={clsx('panel-solid anim-rise absolute z-40 flex w-52 flex-col overflow-hidden py-1', below)}
          onKeyDown={(e) => {
            if (e.key === 'Escape') setOpen(false);
          }}
        >
          {item(t('Внесение'), () => requestCashMove('in'))}
          {item(t('Изъятие'), () => requestCashMove('out'))}
          {item(t('Печать X'), () => {
            setError(null);
            printX(club.clubName).catch((e: unknown) => setError(describe(e)));
          })}
        </div>
      )}
      {error && (
        <Note role="alert" tone="err" className={clsx('absolute z-40 w-64 text-xs', below)}>
          {error}
        </Note>
      )}
    </div>
  );
}

/** A drawer move as sent: its body and its `Idempotency-Key`. */
export interface PendingMove {
  kind: CashMoveKind;
  key: string;
  amount: number;
  reason: CashReason;
  note: string;
}

const LOST_MOVE = 'Ответ сервера не пришёл: деньги могли пройти. Повторите это же действие — дважды оно не проведётся.';

/**
 * Cash into or out of the drawer: the kind, the amount, a reason (Размен / Инкассация / Хозрасходы / Другое, the last
 * with a note), then the slip to sign; Enter in the form sends it. The action holds one `Idempotency-Key` until a
 * definite answer: a lost answer freezes the sheet and offers only the same move again (D-46), and the move is kept by
 * the shift provider (`pending`), so closing and reopening the sheet cannot send it a second time under a new key. A
 * cash-out above the drawer is refused with what it holds (`cashShort`); with `limits.cashOutOwnerOnly` only the owner
 * takes cash out.
 */
export function CashMoveSheet({
  initialKind,
  pending,
  onPending,
  onClose,
  onDone,
}: {
  initialKind: CashMoveKind;
  /** The move whose answer was lost: only it can be sent again. */
  pending: PendingMove | null;
  onPending: (move: PendingMove | null) => void;
  onClose: () => void;
  onDone: () => void;
}): JSX.Element {
  const club = useClub();
  const [kind, setKind] = useState<CashMoveKind>(pending?.kind ?? initialKind);
  const [amount, setAmount] = useState(pending?.amount ?? 0);
  const [reason, setReason] = useState<CashReason | null>(pending?.reason ?? null);
  const [note, setNote] = useState(pending?.note ?? '');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(pending ? t(LOST_MOVE) : null);
  const [done, setDone] = useState<{ movement: CashMovement; expectedCash: number } | null>(null);

  const trimmed = note.trim();
  const noteOk = reason === 'other' ? trimmed.length >= 3 && trimmed.length <= 200 : trimmed.length <= 200;
  const ready = amount > 0 && amount <= MAX_MOVE && reason !== null && noteOk && !busy && pending === null;
  const frozen = busy || pending !== null;

  const send = async (p: PendingMove): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      const r = await clubApi.cashMove(
        { kind: p.kind, amount: p.amount, reasonCode: p.reason, note: p.note.length > 0 ? p.note : null },
        p.key,
      );
      onPending(null);
      setDone(r);
      onDone();
    } catch (e) {
      const lost = isLostAnswer(e);
      onPending(lost ? p : null);
      setError(
        lost
          ? t(LOST_MOVE)
          : e instanceof AdminError && e.code === 'forbidden' && e.details?.['reason'] === 'ownerOnly'
            ? t('Только владелец может изымать деньги из кассы')
            : describe(e),
      );
    } finally {
      setBusy(false);
    }
  };
  const submit = (): void => {
    if (pending) {
      if (!busy) void send(pending);
    } else if (ready && reason) {
      void send({ kind, key: newKey(), amount, reason, note: trimmed });
    }
  };

  const title = kind === 'in' ? t('Внесение') : t('Изъятие');
  const slip = (m: CashMovement): JSX.Element => (
    <CashSlip
      s={{
        kind: m.kind,
        amount: m.amount,
        reasonCode: m.reasonCode,
        note: m.note,
        at: m.at,
        ref: m.id,
        club: club.clubName,
        staffName: m.staffName,
      }}
    />
  );

  return (
    <Sheet title={title} onClose={onClose}>
      {done ? (
        <>
          <p role="status" className="rounded-md bg-success/10 px-3 py-2 text-sm text-success">
            {t(done.movement.kind === 'in' ? 'Внесено {sum} · {reason}' : 'Изъято {sum} · {reason}', {
              sum: moneyExact(done.movement.amount),
              reason: t(REASON_LABEL[done.movement.reasonCode] ?? done.movement.reasonCode),
            })}
            <span className="block">{t('В кассе теперь {sum}', { sum: moneyExact(done.expectedCash) })}</span>
          </p>
          <div className="grid grid-cols-2 gap-2">
            <Button onClick={() => void printDocument(slip(done.movement), 'receipt')}>{t('Печать')}</Button>
            <Button variant="primary" autoFocus onClick={onClose}>
              {t('Готово')}
            </Button>
          </div>
        </>
      ) : (
        <div
          className="flex flex-col gap-4"
          onKeyDown={(e) => {
            // Enter anywhere in the form sends the move (a held Enter sends it once); the buttons keep their own.
            if (e.key === 'Enter' && !(e.target instanceof HTMLButtonElement)) {
              e.preventDefault();
              if (!e.repeat) submit();
            }
          }}
        >
          <div role="group" aria-label={t('Что делаем')} className="grid grid-cols-2 gap-1.5">
            {(['in', 'out'] as const).map((k) => (
              <Button
                key={k}
                aria-pressed={kind === k}
                disabled={frozen}
                className={clsx(kind === k && 'choice-on')}
                onClick={() => setKind(k)}
              >
                {k === 'in' ? t('Внесение') : t('Изъятие')}
              </Button>
            ))}
          </div>
          <Field label={t('Сумма')}>
            <MoneyInput value={amount} onChange={setAmount} disabled={frozen} autoFocus={!frozen} />
          </Field>
          <div className="flex flex-col gap-1.5">
            <span className="label">{t('Причина')}</span>
            <div role="group" aria-label={t('Причина')} className="grid grid-cols-2 gap-1.5">
              {REASONS.map((r) => (
                <Button
                  key={r}
                  size="sm"
                  aria-pressed={reason === r}
                  disabled={frozen}
                  className={clsx(reason === r && 'choice-on')}
                  onClick={() => setReason(r)}
                >
                  {t(REASON_LABEL[r] as string)}
                </Button>
              ))}
            </div>
            {/* The button stays off until a reason is chosen: say why, once there is an amount to send. */}
            {amount > 0 && reason === null && !frozen && (
              <span className="text-xs text-warning">{t('Выберите причину')}</span>
            )}
          </div>
          <Field
            label={t('Комментарий')}
            hint={reason === 'other' ? t('Обязателен для «Другое»: от 3 до 200 символов') : t('Необязательно')}
          >
            <input
              className={inputCls}
              value={note}
              maxLength={200}
              readOnly={frozen}
              onChange={(e) => setNote(e.target.value)}
            />
          </Field>
          <Note note={error ? { text: error, tone: 'err' } : null} />
          <div className="flex justify-end gap-2 border-t border-line pt-4">
            <Button variant="ghost" onClick={onClose}>
              {t('Отмена')}
            </Button>
            {pending ? (
              <Button variant="primary" disabled={busy} onClick={() => void send(pending)}>
                {busy ? '…' : t('Повторить · {sum}', { sum: moneyExact(pending.amount) })}
              </Button>
            ) : (
              <Button variant="primary" disabled={!ready} onClick={submit}>
                {busy ? '…' : kind === 'in' ? t('Внести') : t('Изъять')}
              </Button>
            )}
          </div>
        </div>
      )}
    </Sheet>
  );
}
