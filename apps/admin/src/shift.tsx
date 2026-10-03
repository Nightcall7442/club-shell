/**
 * The cash shift as the whole console sees it: whether one is open, its X totals (the top-bar chip) and the gate that
 * asks to open one. Money is taken only in an open shift (the server answers 409 `shiftClosed` otherwise), so after
 * sign-in or a reload without a shift the console shows a blocking "Открыть смену" with the drawer float of the last
 * closed shift; the owner may put it off ("Позже"), a cashier cannot. Money buttons ask for it again through
 * {@link ShiftState.requestOpen}. Polls `/admin/shift` every 30 s and on {@link ShiftState.refresh} after a money action.
 */
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import clsx from 'clsx';
import { clubApi, type Shift, type ShiftTotals, type StaffMember } from '@/api';
import { describe } from '@/errors';
import { money } from '@/format';
import { t } from '@/i18n';
import { Button, Field, MoneyInput, Note, Sheet } from '@/ui';

const POLL_MS = 30_000;
const nf = new Intl.NumberFormat('ru-RU');

export interface ShiftState {
  /** False until the first answer: nothing is gated on a shift the console has not heard about yet. */
  loaded: boolean;
  shift: Shift | null;
  x: ShiftTotals | null;
  /** Counted cash of the last closed shift: what the drawer should hold when the next one opens. */
  lastClosingCash: number | null;
  refresh: () => void;
  /** Shows the "Открыть смену" sheet (from a money button that the closed shift blocks). */
  requestOpen: () => void;
}

const ShiftContext = createContext<ShiftState>({
  loaded: false,
  shift: null,
  x: null,
  lastClosingCash: null,
  refresh: () => undefined,
  requestOpen: () => undefined,
});

export function useShift(): ShiftState {
  return useContext(ShiftContext);
}

/** Cash and cashless top-ups of the shift; by method when the server splits them, else its old two totals. */
export function cashSplit(x: ShiftTotals): { cash: number; cashless: number } {
  const m = x.topUpByMethod;
  if (!m) return { cash: x.topUpCash, cashless: x.topUpOther };
  return { cash: m.cash, cashless: m.card + m.payme + m.click + m.uzum + m.other };
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
  const [lastClosingCash, setLastClosingCash] = useState<number | null>(null);
  // 'auto' — no shift at sign-in (blocking for a cashier); 'asked' — a money button asked for it (always closable).
  const [gate, setGate] = useState<'auto' | 'asked' | null>(null);
  const checked = useRef(false);

  const refresh = useCallback(async (): Promise<void> => {
    try {
      const r = await clubApi.shift();
      setShift(r.shift);
      setX(r.x);
      // History is newest first.
      setLastClosingCash(r.history[0]?.closingCash ?? null);
      setLoaded(true);
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

  const value = useMemo<ShiftState>(
    () => ({
      loaded,
      shift,
      x,
      lastClosingCash,
      refresh: () => void refresh(),
      requestOpen: () => setGate('asked'),
    }),
    [loaded, shift, x, lastClosingCash, refresh],
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

/** Top-bar chip: who runs the shift and the cash / cashless taken so far; a click goes to the Смена page. */
export function ShiftChip({ onClick }: { onClick: () => void }): JSX.Element {
  const { loaded, shift, x } = useShift();
  const split = x ? cashSplit(x) : null;
  return (
    <button
      type="button"
      onClick={onClick}
      className="focus-ring flex min-w-0 items-center gap-2 whitespace-nowrap rounded-md px-2 py-1 text-sm hover:bg-white/[0.04]"
    >
      <span
        className={clsx('h-2 w-2 shrink-0 rounded-full', shift ? 'bg-success' : loaded ? 'bg-danger' : 'bg-muted')}
      />
      {shift ? (
        <>
          <span className="truncate">{t('Смена · {name}', { name: shift.staffName })}</span>
          {split && (
            <span className="tnum hidden truncate text-muted lg:inline">
              · {t('нал {sum}', { sum: nf.format(Math.round(split.cash / 100)) })} ·{' '}
              {t('безнал {sum}', { sum: nf.format(Math.round(split.cashless / 100)) })}
            </span>
          )}
        </>
      ) : (
        t('Смена не открыта')
      )}
    </button>
  );
}
