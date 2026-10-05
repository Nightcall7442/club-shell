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
 *
 * Variant F (spec §7): the amount is the big Unbounded field with the focus glow (and, for a top-up, «Баланс станет»),
 * the presets only fill it, the cash handed over leads to the change box («Сдача», amber «Не хватает»), cash is the XL
 * cut-corner button that sums it up, the other methods are two-line buttons with their hotkey.
 */
import { useEffect, useId, useMemo, useRef, useState } from 'react';
import clsx from 'clsx';
import type { Money } from '@clubshell/contracts';
import { adminApi, newKey, type PayMethod } from '@/api';
import { GameArt } from '@/art';
import { useClub } from '@/club';
import { describe, isLostAnswer } from '@/errors';
import { exactDigits, money, moneyExact, moneyParts } from '@/format';
import { t } from '@/i18n';
import { ArrowRightIcon, CashIcon } from '@/icons';
import { PAY_METHOD_LABEL } from '@/labels';
import { Receipt, printDocument, type ReceiptData } from '@/print';
import { useShift } from '@/shift';
import { Button, Kbd, Note, Sheet, StatusDot, type ButtonSize } from '@/ui';

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
    <Note tone="warn">
      <span className="flex items-center justify-between gap-3">
        <span>{t('Смена не открыта')}</span>
        <Button size="sm" variant="secondary" className="-my-1" onClick={shift.requestOpen}>
          {t('Открыть смену')}
        </Button>
      </span>
    </Note>
  );
}

/** The label row over a step of the box: a mono caption (`text` while it is the step to do), a hint on the right. */
function StepLabel({
  label,
  hint,
  mono,
  active,
  htmlFor,
}: {
  label: string;
  hint?: string;
  /** The hint as a mono caption («ввод с клавиатуры») rather than a line of text. */
  mono?: boolean;
  active?: boolean;
  /** The field the caption names (the amount): a `<label>`, so the hint stays out of the field's name. */
  htmlFor?: string;
}): JSX.Element {
  const Caption = htmlFor ? 'label' : 'span';
  return (
    <span className="flex min-h-3 items-baseline justify-between gap-3">
      <Caption htmlFor={htmlFor} className={clsx('label-sm shrink-0', active && 'text-text')}>
        {label}
      </Caption>
      {hint && (
        <span className={clsx('min-w-0 truncate', mono ? 'label-sm' : 'text-[11px] leading-none text-muted')}>
          {hint}
        </span>
      )}
    </span>
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
  amountLabel,
  balanceBefore,
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
  /**
   * The amount field's caption (default «Сумма»); a top-up says «Сумма пополнения» (the field is still found by
   * «Сумма»).
   */
  amountLabel?: string;
  /** The payee's balance now (minor units): the field shows what it becomes with the typed amount. */
  balanceBefore?: number;
  /** Does what the money is for; a throw is shown inline under the buttons, a success spends the box. */
  onPay: (p: Payment) => Promise<void>;
}): JSX.Element {
  const amountId = useId();
  const receivedId = useId();
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

  // The change box: what goes back to the client (accent) or what is still missing (amber); nothing typed — a dash.
  const change = receivedMinor > 0 && amount > 0 ? receivedMinor - amount : null;
  const after = balanceBefore !== undefined && !fixed ? moneyParts(balanceBefore + amount) : null;
  const cashSummary =
    amount > 0
      ? [moneyExact(amount), change !== null && change > 0 ? t('сдача {sum}', { sum: exactDigits(change) }) : null]
          .filter(Boolean)
          .join(' · ')
      : '';

  return (
    <div
      className="flex flex-col"
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
      <StepLabel
        htmlFor={amountId}
        label={amountLabel ?? t('Сумма')}
        hint={fixed ? t('Ровно эта сумма, до тийина') : t('Ввод с клавиатуры')}
        mono
        active={!fixed}
      />
      <div
        className={clsx(
          'mt-2 flex h-[76px] items-center gap-4 rounded-md border pl-5 pr-[18px] transition-shadow duration-200',
          fixed
            ? 'border-accent/[0.12] bg-text/[0.03]'
            : 'border-transparent bg-bg/[0.62] focus-within:shadow-glow [&:not(:focus-within)]:border-accent/[0.16]',
        )}
      >
        <span className="flex min-w-0 flex-1 items-baseline gap-2.5">
          <input
            id={amountId}
            ref={amountRef}
            inputMode="numeric"
            autoComplete="off"
            autoFocus={autoFocus && !fixed}
            // read-only, not disabled, while paying: the focus stays here for the next Enter after a refusal
            readOnly={frozen || fixed}
            aria-readonly={frozen || fixed}
            className="tnum min-w-[1ch] max-w-full flex-[0_1_auto] bg-transparent font-display text-[36px] font-medium leading-none tracking-[-0.02em] text-hi caret-accent outline-none [field-sizing:content] placeholder:text-muted/60"
            value={fixed ? exactDigits(exact) : groupDigits(digits)}
            placeholder="0"
            onChange={(e) => {
              if (!fixed) setDigits(digitsOf(e.target.value));
            }}
          />
          <span className="shrink-0 text-base font-medium leading-none text-muted">{t('сум')}</span>
        </span>
        {after && (
          <span className="flex shrink-0 flex-col items-end gap-[7px]">
            <span className="label-sm text-[9px]">{t('Баланс станет')}</span>
            <span className="flex items-baseline gap-1 whitespace-nowrap">
              <span className="num-dot text-base leading-none text-text">{after.num}</span>
              <span className="text-[10.5px] font-medium leading-none text-muted">{after.unit}</span>
            </span>
          </span>
        )}
      </div>
      {tooLow && <p className="mt-2 text-xs font-medium text-warning">{t('Не меньше {sum}', { sum: uzs(min) })}</p>}
      {tooHigh && (
        <p className="mt-2 text-xs font-medium text-warning">{t('Не больше {sum}', { sum: uzs(MAX_AMOUNT) })}</p>
      )}

      {!fixed && (
        <>
          <div className="mt-3.5">
            <StepLabel label={t('Быстрые суммы')} hint={t('только подставляют сумму в поле')} />
          </div>
          <div className="mt-2 grid grid-cols-4 gap-2">
            {PRESETS.map((p) => (
              <button
                type="button"
                key={p}
                disabled={frozen}
                className={clsx(
                  'choice focus-ring tnum h-11 rounded-md px-1 text-[13.5px] font-semibold disabled:cursor-not-allowed disabled:opacity-40',
                  digits === String(p) && 'choice-on',
                )}
                onClick={() => {
                  setDigits(String(p));
                  amountRef.current?.focus();
                }}
              >
                {groupDigits(String(p))}
              </button>
            ))}
          </div>
        </>
      )}

      <div className="mt-[18px] grid grid-cols-[minmax(0,1fr)_20px_minmax(0,1fr)] items-end gap-2.5">
        <div className="flex min-w-0 flex-col gap-2">
          <StepLabel htmlFor={receivedId} label={t('Получено наличными')} />
          <span className="relative block">
            <input
              id={receivedId}
              inputMode="numeric"
              autoComplete="off"
              autoFocus={autoFocus && fixed}
              readOnly={frozen}
              className="focus-ring tnum h-[52px] w-full rounded-md border border-accent/[0.16] bg-bg/50 pl-4 pr-11 font-display text-xl font-medium tracking-[-0.01em] text-hi caret-accent placeholder:font-sans placeholder:text-[13px] placeholder:font-normal placeholder:tracking-normal placeholder:text-muted hover:border-accent/[0.26] focus-visible:border-transparent"
              value={groupDigits(received)}
              placeholder={t('необязательно')}
              onChange={(e) => setReceived(digitsOf(e.target.value))}
            />
            <span className="pointer-events-none absolute right-3.5 top-1/2 -translate-y-1/2 text-[13px] font-medium text-muted">
              {t('сум')}
            </span>
          </span>
        </div>
        <span aria-hidden="true" className="flex h-[52px] items-center justify-center text-muted">
          <ArrowRightIcon size={18} />
        </span>
        <div className="flex min-w-0 flex-col gap-2" aria-live="polite">
          <span
            className={clsx(
              'label-sm min-h-3',
              change === null ? '' : change >= 0 ? 'font-semibold text-accent' : 'font-semibold text-warning',
            )}
          >
            {change !== null && change < 0 ? t('Не хватает') : t('Сдача')}
          </span>
          <span
            className={clsx(
              'flex h-[52px] items-center justify-between gap-2 rounded-md border px-4',
              change === null
                ? 'border-accent/10 bg-bg/30'
                : change >= 0
                  ? 'border-accent/[0.34] bg-[linear-gradient(180deg,rgb(var(--c-accent)/0.12),rgb(var(--c-accent)/0.05))]'
                  : 'border-warning/40 bg-warning/[0.08]',
            )}
          >
            <span
              className={clsx(
                'tnum min-w-0 truncate font-display text-[22px] font-medium leading-none tracking-[-0.01em]',
                change === null ? 'text-muted' : change >= 0 ? 'text-white' : 'text-warning',
              )}
            >
              {change === null ? '—' : exactDigits(Math.abs(change))}
            </span>
            {change !== null && (
              <span className="shrink-0 text-[13px] font-medium leading-none text-dim">{t('сум')}</span>
            )}
          </span>
        </div>
      </div>

      {(closed || (disabled && !closed)) && (
        <div className="mt-3.5 flex flex-col gap-2">
          <ShiftClosedNote />
          {disabled && !closed && <p className="text-xs text-muted">{disabled}</p>}
        </div>
      )}

      <div className="mt-5">
        <StepLabel label={t('Провести оплату')} hint={t('кнопка способа сразу проводит платёж')} active />
      </div>
      {unknown ? (
        <Button
          variant="primary"
          size="xl"
          className="mt-2"
          disabled={busy !== null}
          onClick={() => void send(unknown)}
        >
          {busy !== null
            ? '…'
            : t('Повторить · {sum} · {method}', {
                sum: moneyExact(unknown.amount),
                method: methodName(unknown.method),
              })}
        </Button>
      ) : (
        <>
          <Button
            variant="primary"
            size="xl"
            disabled={!ready || cashShort}
            title={hints ? PAY_METHODS[0]?.hint : undefined}
            className="mt-2 w-full"
            onClick={() => pay('cash')}
          >
            <CashIcon size={20} strong />
            <span className="shrink-0">
              {busy === 'cash' ? '…' : verb ? `${verb} · ${t(PAY_METHOD_LABEL.cash)}` : t(PAY_METHOD_LABEL.cash)}
            </span>
            {/* The sum and the change, when they fit beside the label: a summary that does not fit wraps out of sight. */}
            <span className="flex h-5 min-w-0 flex-1 flex-wrap items-center justify-end overflow-hidden">
              <span aria-hidden="true" className="h-5 w-0" />
              {busy === null && cashSummary && (
                <span className="h-5 whitespace-nowrap text-[12.5px] font-medium leading-5 opacity-[0.72]">
                  {cashSummary}
                </span>
              )}
            </span>
            {hints && (
              <Kbd onPrimary className="h-6 px-2 text-[10.5px]">
                {PAY_METHODS[0]?.hint}
              </Kbd>
            )}
          </Button>
          <div className="mt-2 grid grid-cols-4 gap-2">
            {PAY_METHODS.slice(1).map((m) => (
              <button
                type="button"
                key={m.id}
                disabled={!ready}
                title={hints ? m.hint : undefined}
                onClick={() => pay(m.id)}
                className={clsx(
                  'focus-ring flex flex-col items-center justify-center gap-[7px] rounded-md border border-accent/[0.14] bg-accent/[0.04] px-1 text-text hover:border-accent/[0.3] hover:bg-accent/[0.08] disabled:cursor-not-allowed disabled:opacity-40',
                  hints ? 'h-14' : 'h-11',
                )}
              >
                <span className="max-w-full truncate text-[13.5px] font-semibold leading-none">
                  {busy === m.id ? (
                    '…'
                  ) : (
                    <>
                      {/* The name says the verb too («Посадить · Карта»): the four buttons show the method only. */}
                      {verb && <span className="sr-only">{`${verb} · `}</span>}
                      {t(m.label)}
                    </>
                  )}
                </span>
                {hints && (
                  <span className="font-mono text-[9.5px] font-medium uppercase leading-none tracking-[0.1em] text-muted">
                    {m.hint}
                  </span>
                )}
              </button>
            ))}
          </div>
        </>
      )}
      {error && (
        <Note tone="err" role="alert" className="mt-3">
          {error}
        </Note>
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
export function ReceiptButton({
  receipt,
  className,
  size,
}: {
  receipt: ReceiptData;
  className?: string;
  size?: ButtonSize;
}): JSX.Element {
  return (
    <Button size={size} className={className} onClick={() => void printDocument(<Receipt r={receipt} />, 'receipt')}>
      {t('Чек')}
    </Button>
  );
}

/**
 * The footer of a money sheet (spec §7.9): whose shift takes the money (the green dot), and «Esc отмена». It never says
 * «Смена открыта» (that line belongs to the shift page alone); without a shift it leaves the left side to the box's own
 * note.
 */
export function PayFooter(): JSX.Element {
  const { shift } = useShift();
  return (
    <>
      <span className="flex min-w-0 items-center gap-[9px] font-medium">
        {shift && (
          <>
            <StatusDot tone="ok" />
            <span className="truncate">{t('Смена · {name}', { name: shift.staffName })}</span>
          </>
        )}
      </span>
      <span className="flex shrink-0 items-center gap-2 text-xs text-muted">
        <Kbd className="h-[22px] px-[7px] text-text">Esc</Kbd>
        {t('отмена')}
      </span>
    </>
  );
}

/**
 * Where a top-up happens, for the sheet's header: from a seat it is the PC, its zone and the game being played, with the
 * game's art as a 150 px hero (spec §7); from the client search there is none and the header is plain.
 */
export interface TopUpContext {
  /** Mono line over the title: «ПК 02 · STANDARD · COUNTER-STRIKE 2». */
  caption?: string;
  /** The hero picture (the game's hero or cover); none — the plain header with the caption. */
  art?: string | null;
  /** Under the title over the hero: «@dilnoza · ••4521». */
  sub?: string;
}

/** The sheet's hero: the game's art under its scrims, the caption, the title with a dim «·», the client's handle. */
function TopUpHero({ title, context }: { title: string; context: TopUpContext }): JSX.Element {
  const cut = title.indexOf(' · ');
  return (
    <div className="relative h-[150px] shrink-0 overflow-hidden">
      <GameArt src={context.art} variant="hero" edge />
      <div className="absolute left-6 right-[62px] top-3.5 flex h-9 items-center">
        {context.caption && <span className="label-sm truncate text-text/[0.86]">{context.caption}</span>}
      </div>
      <div className="absolute inset-x-6 bottom-3.5">
        <h2 className="truncate font-display text-[26px] font-medium leading-[1.1] tracking-[-0.02em] text-white [text-shadow:0_2px_12px_rgb(0_0_0/0.6)]">
          {cut > 0 ? (
            <>
              {title.slice(0, cut)} <span className="text-artlabel">·</span> {title.slice(cut + 3)}
            </>
          ) : (
            title
          )}
        </h2>
        {context.sub && (
          <p className="mt-2 truncate text-xs leading-none text-text/80 [text-shadow:0_1px_3px_rgb(0_0_0/0.8)]">
            {context.sub}
          </p>
        )}
      </div>
    </div>
  );
}

/**
 * Top-up of one client's balance as a sheet: the client, the pay box, then the new balance. Used by the seat panel
 * ("Пополнить", F2; with the seat as `context`, so the game's art heads it) and the top-bar search; guests are never
 * topped up (D-36), their debts go through the settle sheet.
 */
export function TopUpSheet({
  payee,
  initial,
  title,
  context,
  onClose,
  onDone,
}: {
  payee: Payee;
  initial?: number;
  title?: string;
  context?: TopUpContext;
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
  const heading = title ?? t('Пополнить · {name}', { name: payee.displayName });
  const balance = moneyParts((done?.balance ?? payee.balance).amount);
  const bonus = payee.bonus ? moneyParts(payee.bonus.amount) : null;
  return (
    <Sheet
      title={heading}
      onClose={onClose}
      caption={context?.art ? undefined : context?.caption}
      hero={context?.art ? <TopUpHero title={heading} context={context} /> : undefined}
      footer={<PayFooter />}
    >
      <dl className="grid grid-cols-2 gap-2">
        <div className="well flex h-[60px] flex-col justify-between px-3.5 py-3">
          <dt className="label-sm">{t('Баланс')}</dt>
          <dd className="flex items-baseline gap-[5px] whitespace-nowrap">
            <span className="num-dot text-xl leading-none text-hi">{balance.num}</span>
            <span className="text-[11.5px] font-medium leading-none text-muted">{balance.unit}</span>
          </dd>
        </div>
        <div className="well flex h-[60px] flex-col justify-between px-3.5 py-3">
          <dt className="label-sm">{t('Бонусы')}</dt>
          <dd className="flex items-baseline gap-[5px] whitespace-nowrap">
            {bonus ? (
              <>
                <span className="num-dot text-xl leading-none text-accent">{bonus.num}</span>
                <span className="text-[11.5px] font-medium leading-none text-muted">{bonus.unit}</span>
              </>
            ) : (
              <span className="num-dot text-xl leading-none text-muted">—</span>
            )}
          </dd>
        </div>
      </dl>
      {done ? (
        <>
          <Note tone="ok" role="status">
            {t('Баланс пополнен · {sum} · {method} · теперь {balance}', {
              sum: uzs(done.paid),
              method: methodName(done.method),
              balance: money(done.balance),
            })}
            {done.change !== null && done.change > 0 && (
              <span className="mt-1 block font-semibold">{t('Сдача: {sum}', { sum: moneyExact(done.change) })}</span>
            )}
          </Note>
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
          amountLabel={t('Сумма пополнения')}
          balanceBefore={payee.balance.amount}
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
