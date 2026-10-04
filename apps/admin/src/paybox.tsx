/**
 * The one way money is taken at the counter (Senet's pay box): type any amount (or fill it from a preset), optionally the
 * cash handed over for the change, then press the payment method — the method button IS the confirmation, Enter in the
 * amount is cash. What the money is for (a top-up, a top-up then a session) is the caller's `onPay`: the box only builds
 * the {@link Payment}, shows the busy state and the server's refusal inline, and refreshes the shift totals after. A
 * split payment later becomes a list of payments without changing the callers. While no shift is open the buttons stay
 * in place, disabled, with the reason and a button that opens the shift.
 *
 * `exact` takes an amount the server checks to the tiyin (a debt, a walk-in guest's price): it is shown read-only with
 * its tiyin, without presets, and sent unchanged; "Получено" still gives the change.
 *
 * Every payment carries its own `Idempotency-Key` ({@link Payment.key}), taken when the cashier presses a method and
 * kept until a definite answer. A lost answer (no response or 5xx) may still have booked the money: the box then freezes
 * the amount and the method and offers only the same payment again, under the same key however late (the server replays
 * the first result instead of booking twice, D-46). Cash typed in "Получено" below the amount refuses cash; a held
 * Enter pays once.
 */
import { useEffect, useMemo, useRef, useState } from 'react';
import clsx from 'clsx';
import type { Money } from '@clubshell/contracts';
import { adminApi, newKey, type PayMethod } from '@/api';
import { useClub } from '@/club';
import { describe, isLostAnswer } from '@/errors';
import { exactDigits, money, moneyExact } from '@/format';
import { t } from '@/i18n';
import { PAY_METHOD_LABEL } from '@/labels';
import { Receipt, printDocument, type ReceiptData } from '@/print';
import { useShift } from '@/shift';
import { Button, Kbd, Sheet, inputCls } from '@/ui';

/** Counter methods in button order; `code` is the Alt+digit hotkey (layout-independent `KeyboardEvent.code`). */
export const PAY_METHODS: { id: PayMethod; label: string; hint: string; code: string }[] = [
  { id: 'cash', label: PAY_METHOD_LABEL.cash, hint: 'Enter', code: 'Digit1' },
  { id: 'card', label: PAY_METHOD_LABEL.card, hint: 'Alt+2', code: 'Digit2' },
  { id: 'payme', label: PAY_METHOD_LABEL.payme, hint: 'Alt+3', code: 'Digit3' },
  { id: 'click', label: PAY_METHOD_LABEL.click, hint: 'Alt+4', code: 'Digit4' },
  { id: 'uzum', label: PAY_METHOD_LABEL.uzum, hint: 'Alt+5', code: 'Digit5' },
];

const PRESETS = [20_000, 50_000, 100_000, 200_000];
/** Whole сум a cashier can type: 999 999 999 (more than {@link MAX_AMOUNT} is shown as an error, not cut off). */
const MAX_DIGITS = 9;
/** The server's limit of one counter payment, minor units: 1 000 000 сум. */
export const MAX_AMOUNT = 100_000_000;

/** One payment as the cashier took it. Amounts are minor units (tiyin). */
export interface Payment {
  method: PayMethod;
  amount: number;
  /** Cash handed over, for the change; null when not typed or not cash. */
  received: number | null;
  /** `Idempotency-Key` of this payment: the same on a retry after a lost answer, a new one for the next payment. */
  key: string;
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

/** The method's name in the console language. */
export function methodName(method: string): string {
  return t(PAY_METHOD_LABEL[method as PayMethod] ?? method);
}

/**
 * One `Idempotency-Key` for a money action outside the pay box (a payout, a postpaid seat): {@link HeldKey.take} gives
 * the key held for the action or a new one, {@link HeldKey.settle} drops it after a definite answer (a success, a 4xx)
 * and keeps it after a lost one, so the retry replays instead of booking twice (D-46). {@link HeldKey.reset} drops it
 * when the action itself changes (another PC): a key never goes out with another body than the one it was taken for.
 */
export interface HeldKey {
  take: () => string;
  settle: (error?: unknown) => void;
  reset: () => void;
}

export function useHeldKey(): HeldKey {
  const key = useRef<string | null>(null);
  return useMemo(
    () => ({
      take: () => (key.current ??= newKey()),
      settle: (error?: unknown) => {
        if (error === undefined || !isLostAnswer(error)) key.current = null;
      },
      reset: () => {
        key.current = null;
      },
    }),
    [],
  );
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
  exact,
  verb,
  autoFocus = true,
  hints = true,
  disabled,
  onPay,
}: {
  /** Minor units the amount starts with (e.g. what a session lacks). */
  initial?: number;
  /** The least the box takes (minor units); 0 — any positive amount. */
  min?: number;
  /** Exactly this, to the tiyin, read-only: a debt or a guest's price the server checks exactly. */
  exact?: number;
  /** Prefix of the method buttons: "Посадить" → "Посадить · Наличные". */
  verb?: string;
  autoFocus?: boolean;
  /**
   * Show the keys (Enter — cash, Alt+2..5) on the method buttons. Off where the page's focus lives outside the box and
   * those keys do something else there (the bar: Enter in its search adds a product).
   */
  hints?: boolean;
  /** Why the box cannot take money yet (e.g. the price is being recounted); null or absent — it can. */
  disabled?: string | null;
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
  // The payment whose answer was lost: only it may be sent again (same body, same key), nothing else.
  const [unknown, setUnknown] = useState<Payment | null>(null);
  const amountRef = useRef<HTMLInputElement>(null);
  const alive = useRef(true);
  // Set on every mount: StrictMode mounts twice, and a box left "dead" would never leave its busy state.
  useEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
    };
  }, []);
  // A new shortfall (another tariff or duration) refills the field.
  useEffect(() => setDigits(sumDigits(initial)), [initial]);

  const fixed = exact !== undefined;
  const amount = fixed ? exact : Number(digits || '0') * 100;
  const receivedMinor = Number(received || '0') * 100;
  const closed = useShiftClosed();
  const tooLow = !fixed && amount > 0 && amount < min;
  const tooHigh = amount > MAX_AMOUNT;
  // Cash handed over below the amount: the cashier has not got the money yet.
  const cashShort = receivedMinor > 0 && receivedMinor < amount;
  const ready =
    amount > 0 && !tooLow && !tooHigh && !closed && !disabled && busy === null && !spent && unknown === null;

  const send = async (p: Payment): Promise<void> => {
    if (inFlight.current) return;
    inFlight.current = true;
    setBusy(p.method);
    setError(null);
    try {
      await onPay(p);
      if (alive.current) {
        setSpent(true);
        setUnknown(null);
      }
    } catch (e) {
      inFlight.current = false;
      if (alive.current) {
        const lost = isLostAnswer(e);
        setUnknown(lost ? p : null);
        setError(
          lost
            ? t('Ответ сервера не пришёл: деньги могли пройти. Повторите этот же платёж — дважды он не проведётся.')
            : describe(e),
        );
      }
    } finally {
      if (alive.current) setBusy(null);
      // The chip's cash / cashless, and a `shiftClosed` refusal brings the gate state up to date.
      shift.refresh();
    }
  };

  const pay = (method: PayMethod): void => {
    if (!ready || (method === 'cash' && cashShort)) return;
    void send({
      method,
      amount,
      received: method === 'cash' && receivedMinor > 0 ? receivedMinor : null,
      key: newKey(),
    });
  };
  const frozen = busy !== null || unknown !== null;

  return (
    <div
      className="flex flex-col gap-3"
      onKeyDown={(e) => {
        if (e.key === 'Enter' && e.target instanceof HTMLInputElement) {
          e.preventDefault();
          // A held Enter (auto-repeat) is not a second decision.
          if (!e.repeat) pay('cash');
          return;
        }
        const m = e.altKey ? PAY_METHODS.find((x) => x.code === e.code) : undefined;
        if (m) {
          e.preventDefault();
          if (!e.repeat) pay(m.id);
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
            autoFocus={autoFocus && !fixed}
            // read-only, not disabled, while paying: the focus stays here for the next Enter after a refusal
            readOnly={frozen || fixed}
            aria-readonly={frozen || fixed}
            className={clsx(inputCls, 'tnum h-12 pr-12 text-xl font-semibold', fixed && 'bg-white/[0.03]')}
            value={fixed ? exactDigits(exact) : groupDigits(digits)}
            placeholder="0"
            onChange={(e) => {
              if (!fixed) setDigits(digitsOf(e.target.value));
            }}
          />
          <span className="pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-xs text-muted">
            {t('сум')}
          </span>
        </span>
      </label>
      {fixed ? (
        <p className="-mt-1 text-xs text-muted">{t('Ровно эта сумма, до тийина')}</p>
      ) : (
        <div className="grid grid-cols-4 gap-1.5">
          {PRESETS.map((p) => (
            <Button
              key={p}
              size="sm"
              disabled={frozen}
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
      )}
      {tooLow && <p className="text-xs text-warning">{t('Не меньше {sum}', { sum: uzs(min) })}</p>}
      {tooHigh && <p className="text-xs text-warning">{t('Не больше {sum}', { sum: uzs(MAX_AMOUNT) })}</p>}

      <div className="grid grid-cols-[minmax(0,1fr)_auto] items-end gap-3">
        <label className="flex flex-col gap-1.5">
          <span className="label">{t('Получено наличными')}</span>
          <input
            inputMode="numeric"
            autoComplete="off"
            autoFocus={autoFocus && fixed}
            readOnly={frozen}
            className={clsx(inputCls, 'tnum h-9')}
            value={groupDigits(received)}
            placeholder={t('необязательно')}
            onChange={(e) => setReceived(digitsOf(e.target.value))}
          />
        </label>
        <span className="tnum pb-2 text-sm" aria-live="polite">
          {receivedMinor > 0 && amount > 0 ? (
            receivedMinor >= amount ? (
              <span className="text-success">{t('Сдача: {sum}', { sum: moneyExact(receivedMinor - amount) })}</span>
            ) : (
              <span className="text-warning">
                {t('Меньше суммы на {sum}', { sum: moneyExact(amount - receivedMinor) })}
              </span>
            )
          ) : null}
        </span>
      </div>

      <ShiftClosedNote />
      {disabled && !closed && <p className="text-xs text-muted">{disabled}</p>}

      {unknown ? (
        <Button variant="primary" className="h-11" disabled={busy !== null} onClick={() => void send(unknown)}>
          {busy !== null
            ? '…'
            : t('Повторить · {sum} · {method}', {
                sum: moneyExact(unknown.amount),
                method: methodName(unknown.method),
              })}
        </Button>
      ) : (
        <div className={clsx('grid gap-1.5', verb ? 'grid-cols-2' : 'grid-cols-4')}>
          {PAY_METHODS.map((m, i) => (
            <Button
              key={m.id}
              variant={i === 0 ? 'primary' : 'secondary'}
              disabled={!ready || (m.id === 'cash' && cashShort)}
              title={hints ? m.hint : undefined}
              className={clsx(i === 0 && (verb ? 'col-span-2' : 'col-span-4'), 'h-11')}
              onClick={() => pay(m.id)}
            >
              {busy === m.id ? '…' : verb ? `${verb} · ${t(m.label)}` : t(m.label)}
              {hints && <Kbd>{m.hint}</Kbd>}
            </Button>
          ))}
        </div>
      )}
      {error && (
        <p role="alert" className="rounded-md bg-danger/10 px-3 py-2 text-sm text-danger">
          {error}
        </p>
      )}
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

/** «Чек»: prints the slip of the money just taken. */
export function ReceiptButton({ receipt, className }: { receipt: ReceiptData; className?: string }): JSX.Element {
  return (
    <Button className={className} onClick={() => void printDocument(<Receipt r={receipt} />, 'receipt')}>
      {t('Чек')}
    </Button>
  );
}

/**
 * Top-up of one client's balance as a sheet: the client, the pay box, then the new balance. Used by the seat panel
 * ("Пополнить", F2) and the top-bar search; guests are never topped up (D-36), their debts go through the settle sheet.
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
  const club = useClub();
  const [done, setDone] = useState<{
    paid: number;
    method: PayMethod;
    balance: Money;
    change: number | null;
    receipt: ReceiptData;
  } | null>(null);
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
              method: methodName(done.method),
              balance: money(done.balance),
            })}
            {done.change !== null && done.change > 0 && (
              <span className="block font-semibold">{t('Сдача: {sum}', { sum: moneyExact(done.change) })}</span>
            )}
          </p>
          <div className="grid grid-cols-2 gap-2">
            <ReceiptButton receipt={done.receipt} />
            <Button variant="primary" autoFocus onClick={onClose}>
              {t('Готово')}
            </Button>
          </div>
        </>
      ) : (
        <PayBox
          initial={initial}
          onPay={async (p) => {
            const r = await adminApi.topUp({ userId: payee.id, amount: p.amount, method: p.method }, p.key);
            setDone({
              paid: p.amount,
              method: p.method,
              balance: r.balance,
              change: p.received !== null ? p.received - p.amount : null,
              receipt: {
                kind: 'topup',
                at: r.transaction.createdAt,
                ref: r.transaction.id,
                club: club.clubName,
                cashier: club.staff?.name ?? null,
                client: payee.displayName,
                guest: false,
                pc: null,
                paid: { amount: p.amount, method: p.method },
                received: p.received,
                balance: r.balance.amount,
              },
            });
            onDone?.();
          }}
        />
      )}
    </Sheet>
  );
}
