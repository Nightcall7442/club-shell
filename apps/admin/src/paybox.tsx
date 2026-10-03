/**
 * The one way money is taken at the counter (Senet's pay box): type any amount (or fill it from a preset), optionally the
 * cash handed over for the change, then press the payment method — the method button IS the confirmation, Enter in the
 * amount is cash. What the money is for (a top-up, a top-up then a session) is the caller's `onPay`: the box only builds
 * the {@link Payment}, shows the busy state and the server's refusal inline, and refreshes the shift totals after. A
 * split payment later becomes a list of payments without changing the callers. While no shift is open the buttons stay
 * in place, disabled, with the reason and a button that opens the shift.
 */
import { useEffect, useRef, useState } from 'react';
import clsx from 'clsx';
import type { Money } from '@clubshell/contracts';
import { adminApi, type PayMethod } from '@/api';
import { describe } from '@/errors';
import { money } from '@/format';
import { t } from '@/i18n';
import { useShift } from '@/shift';
import { Button, Kbd, Sheet, inputCls } from '@/ui';

/** Counter methods in button order; `code` is the Alt+digit hotkey (layout-independent `KeyboardEvent.code`). */
export const PAY_METHODS: { id: PayMethod; label: string; hint: string; code: string }[] = [
  { id: 'cash', label: 'Наличные', hint: 'Enter', code: 'Digit1' },
  { id: 'card', label: 'оплата|Карта', hint: 'Alt+2', code: 'Digit2' },
  { id: 'payme', label: 'Payme', hint: 'Alt+3', code: 'Digit3' },
  { id: 'click', label: 'Click', hint: 'Alt+4', code: 'Digit4' },
  { id: 'uzum', label: 'Uzum', hint: 'Alt+5', code: 'Digit5' },
];

const PRESETS = [20_000, 50_000, 100_000, 200_000];
/** Whole сум a cashier can type: 999 999 999. */
const MAX_DIGITS = 9;

/** One payment as the cashier took it. Amounts are minor units (tiyin). */
export interface Payment {
  method: PayMethod;
  amount: number;
  /** Cash handed over, for the change; null when not typed or not cash. */
  received: number | null;
}

/** `45000` → `45 000`. */
export function groupDigits(digits: string): string {
  return digits.replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
}

const uzs = (minor: number): string => money({ amount: minor, currency: 'UZS' });

function digitsOf(text: string): string {
  return text.replace(/\D/g, '').replace(/^0+/, '').slice(0, MAX_DIGITS);
}

function sumDigits(minor: number): string {
  return minor > 0 ? String(Math.ceil(minor / 100)) : '';
}

/** True once the console knows no shift is open: money buttons stay in place but are off. */
export function useShiftClosed(): boolean {
  const { loaded, shift } = useShift();
  return loaded && !shift;
}

/** Why a money button is off while no shift is open, with the way out; nothing in an open shift. */
export function ShiftClosedNote(): JSX.Element | null {
  const shift = useShift();
  if (!shift.loaded || shift.shift) return null;
  return (
    <div className="flex items-center justify-between gap-3 rounded-md bg-warning/10 px-3 py-2 text-sm text-warning">
      <span>{t('Смена не открыта')}</span>
      <Button size="sm" variant="secondary" onClick={shift.requestOpen}>
        {t('Открыть смену')}
      </Button>
    </div>
  );
}

export function PayBox({
  initial = 0,
  min = 0,
  verb,
  autoFocus = true,
  onPay,
}: {
  /** Minor units the amount starts with (e.g. what a session lacks). */
  initial?: number;
  /** The least the box takes (minor units); 0 — any positive amount. */
  min?: number;
  /** Prefix of the method buttons: "Посадить" → "Посадить · Наличные". */
  verb?: string;
  autoFocus?: boolean;
  /** Does what the money is for; a throw is shown inline under the buttons, a success spends the box. */
  onPay: (p: Payment) => Promise<void>;
}): JSX.Element {
  const shift = useShift();
  const [digits, setDigits] = useState(sumDigits(initial));
  const [received, setReceived] = useState('');
  const [busy, setBusy] = useState<PayMethod | null>(null);
  // One box takes one payment: after it the buttons stay off until the caller moves on (a double Enter pays once).
  const [spent, setSpent] = useState(false);
  const inFlight = useRef(false);
  const [error, setError] = useState<string | null>(null);
  const amountRef = useRef<HTMLInputElement>(null);
  const alive = useRef(true);
  useEffect(
    () => () => {
      alive.current = false;
    },
    [],
  );
  // A new shortfall (another tariff or duration) refills the field.
  useEffect(() => setDigits(sumDigits(initial)), [initial]);

  const amount = Number(digits || '0') * 100;
  const receivedMinor = Number(received || '0') * 100;
  const closed = useShiftClosed();
  const tooLow = amount > 0 && amount < min;
  const ready = amount > 0 && !tooLow && !closed && busy === null && !spent;

  const pay = async (method: PayMethod): Promise<void> => {
    if (!ready || inFlight.current) return;
    inFlight.current = true;
    setBusy(method);
    setError(null);
    try {
      await onPay({ method, amount, received: method === 'cash' && receivedMinor > 0 ? receivedMinor : null });
      if (alive.current) setSpent(true);
    } catch (e) {
      inFlight.current = false;
      if (alive.current) setError(describe(e));
    } finally {
      if (alive.current) setBusy(null);
      // The chip's cash / cashless, and a `shiftClosed` refusal brings the gate state up to date.
      shift.refresh();
    }
  };

  return (
    <div
      className="flex flex-col gap-3"
      onKeyDown={(e) => {
        if (e.key === 'Enter' && e.target instanceof HTMLInputElement) {
          e.preventDefault();
          void pay('cash');
          return;
        }
        const m = e.altKey ? PAY_METHODS.find((x) => x.code === e.code) : undefined;
        if (m) {
          e.preventDefault();
          void pay(m.id);
        }
      }}
    >
      <label className="flex flex-col gap-1.5">
        <span className="label">{t('Сумма')}</span>
        <span className="relative">
          <input
            ref={amountRef}
            inputMode="numeric"
            autoComplete="off"
            autoFocus={autoFocus}
            // read-only, not disabled, while paying: the focus stays here for the next Enter after a refusal
            readOnly={busy !== null}
            className={clsx(inputCls, 'tnum h-12 pr-12 text-xl font-semibold')}
            value={groupDigits(digits)}
            placeholder="0"
            onChange={(e) => setDigits(digitsOf(e.target.value))}
          />
          <span className="pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-xs text-muted">
            {t('сум')}
          </span>
        </span>
      </label>
      <div className="grid grid-cols-4 gap-1.5">
        {PRESETS.map((p) => (
          <Button
            key={p}
            size="sm"
            disabled={busy !== null}
            className={clsx(digits === String(p) && 'choice-on')}
            onClick={() => {
              setDigits(String(p));
              amountRef.current?.focus();
            }}
          >
            {groupDigits(String(p))}
          </Button>
        ))}
      </div>
      {tooLow && <p className="text-xs text-warning">{t('Не меньше {sum}', { sum: uzs(min) })}</p>}

      <div className="grid grid-cols-[minmax(0,1fr)_auto] items-end gap-3">
        <label className="flex flex-col gap-1.5">
          <span className="label">{t('Получено наличными')}</span>
          <input
            inputMode="numeric"
            autoComplete="off"
            readOnly={busy !== null}
            className={clsx(inputCls, 'tnum h-9')}
            value={groupDigits(received)}
            placeholder={t('необязательно')}
            onChange={(e) => setReceived(digitsOf(e.target.value))}
          />
        </label>
        <span className="tnum pb-2 text-sm" aria-live="polite">
          {receivedMinor > 0 && amount > 0 ? (
            receivedMinor >= amount ? (
              <span className="text-success">{t('Сдача: {sum}', { sum: uzs(receivedMinor - amount) })}</span>
            ) : (
              <span className="text-warning">{t('Меньше суммы на {sum}', { sum: uzs(amount - receivedMinor) })}</span>
            )
          ) : null}
        </span>
      </div>

      <ShiftClosedNote />

      <div className={clsx('grid gap-1.5', verb ? 'grid-cols-2' : 'grid-cols-4')}>
        {PAY_METHODS.map((m, i) => (
          <Button
            key={m.id}
            variant={i === 0 ? 'primary' : 'secondary'}
            disabled={!ready}
            title={m.hint}
            className={clsx(i === 0 && (verb ? 'col-span-2' : 'col-span-4'), 'h-11')}
            onClick={() => void pay(m.id)}
          >
            {busy === m.id ? '…' : verb ? `${verb} · ${t(m.label)}` : t(m.label)}
            <Kbd>{m.hint}</Kbd>
          </Button>
        ))}
      </div>
      {error && <p className="rounded-md bg-danger/10 px-3 py-2 text-sm text-danger">{error}</p>}
    </div>
  );
}

/** Who is being topped up: enough to show whose balance moves. */
export interface Payee {
  id: string;
  displayName: string;
  balance: Money;
  bonus?: Money | null;
}

/**
 * Top-up of one client's balance as a sheet: the client, the pay box, then the new balance. Used by the seat panel
 * ("Пополнить", F2), the top-bar search and the guest debts (`initial` — the debt).
 */
export function TopUpSheet({
  payee,
  initial,
  title,
  onClose,
  onDone,
}: {
  payee: Payee;
  initial?: number;
  title?: string;
  onClose: () => void;
  onDone?: () => void;
}): JSX.Element {
  const [done, setDone] = useState<{ paid: number; method: PayMethod; balance: Money; change: number | null } | null>(
    null,
  );
  const method = PAY_METHODS.find((m) => m.id === done?.method);
  return (
    <Sheet title={title ?? t('Пополнить · {name}', { name: payee.displayName })} onClose={onClose}>
      <dl className="grid grid-cols-2 divide-x divide-line overflow-hidden rounded-md border border-line bg-bg text-center">
        <div className="flex flex-col gap-1.5 px-3 py-2.5">
          <dt className="label">{t('Баланс')}</dt>
          <dd className="tnum font-semibold leading-none">{money(done?.balance ?? payee.balance)}</dd>
        </div>
        <div className="flex flex-col gap-1.5 px-3 py-2.5">
          <dt className="label">{t('Бонусы')}</dt>
          <dd className="tnum font-semibold leading-none">{payee.bonus ? money(payee.bonus) : '—'}</dd>
        </div>
      </dl>
      {done ? (
        <>
          <p role="status" className="rounded-md bg-success/10 px-3 py-2 text-sm text-success">
            {t('Баланс пополнен · {sum} · {method} · теперь {balance}', {
              sum: uzs(done.paid),
              method: method ? t(method.label) : done.method,
              balance: money(done.balance),
            })}
            {done.change !== null && done.change > 0 && (
              <span className="block font-semibold">{t('Сдача: {sum}', { sum: uzs(done.change) })}</span>
            )}
          </p>
          <Button variant="primary" autoFocus onClick={onClose}>
            {t('Готово')}
          </Button>
        </>
      ) : (
        <PayBox
          initial={initial}
          onPay={async (p) => {
            const r = await adminApi.topUp({ userId: payee.id, amount: p.amount, method: p.method });
            setDone({
              paid: p.amount,
              method: p.method,
              balance: r.balance,
              change: p.received !== null ? p.received - p.amount : null,
            });
            onDone?.();
          }}
        />
      )}
    </Sheet>
  );
}
