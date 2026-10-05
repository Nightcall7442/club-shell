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
 * Cash desk part 3: Ctrl+click toggles a PC into a set, Shift+click adds a range, «Выбрать» (for touch) makes clicks
 * toggle, Ctrl+A takes every PC shown; with two or more the panel sends one command to all of them with a result per PC
 * (D-66). «Пересадить на другой ПК…» of a busy seat dims the PCs that cannot take the session and waits for a click on
 * the target (or its number and Enter); the move asks first (D-59). A PC whose player called the desk wears an amber
 * bell, and its panel answers the call («Иду»).
 *
 * Variant F «Командный центр»: the hall is the glass panel with the accent edge, its tiles carry the running game's
 * cover (`seat.game`, D-71) or a finished no-art look, amber marks what needs the cashier (ending within 10 minutes, a
 * call), the selected tile wears the corner brackets; the seat is a solid card with the game's hero over the readouts.
 *
 * The shift's operations feed (`operations.tsx`) sits under the seat card (or under «Выберите место») in the right
 * column. From 1800 px it is always open there (the «column» placement, D-45); below that it is the «panel» and folds to
 * a bar while the right column holds a tall form (seating, several PCs).
 * Polls `/admin/overview` every 2 s (the real console would follow the server's WebSocket).
 */
import { useCallback, useEffect, useId, useMemo, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import clsx from 'clsx';
import type { Session, Tariff } from '@clubshell/contracts';
import {
  adminApi,
  clubApi,
  newKey,
  type BulkInput,
  type BulkResult,
  type ClientHit,
  type CommandAck,
  type GuestDebt,
  type GuestRefund,
  type Member,
  type MoveResponse,
  type Overview,
  type PcCommandKind,
  type PriceQuote,
  type Seat,
  type SeatUser,
  type SessionResult,
} from '@/api';
import { DEFAULT_WALLPAPER, GameArt } from '@/art';
import { CallGroupActions, CallMark, groupCalls, groupLine, setCalls, useCalls, type CallGroup } from '@/calls';
import { ClientPicker, pcLabel } from '@/clientSearch';
import { useClub } from '@/club';
import { isTyping, onShowPc, onSignedOut, sheetOpen, showBar } from '@/desk';
import { amountOf, changedAmount, describe, isLostAnswer, reasonOf } from '@/errors';
import { t } from '@/i18n';
import { duration, exactDigits, minutesLabel, money, moneyExact, moneyParts } from '@/format';
import {
  AlertTriangleIcon,
  CalendarIcon,
  CheckIcon,
  CheckSquareIcon,
  ChevronDownIcon,
  MonitorIcon,
  PowerIcon,
  SignInIcon,
  StopSquareIcon,
  SwapIcon,
  TimerIcon,
  WrenchIcon,
} from '@/icons';
import { guestDisplayName } from '@/labels';
import { OperationsFeed } from '@/operations';
import {
  PayBox,
  PayFooter,
  ReceiptButton,
  ShiftClosedNote,
  TopUpSheet,
  methodName,
  useHeldKey,
  useShiftClosed,
  type Payee,
  type Payment,
  type TopUpContext,
} from '@/paybox';
import { guestSignIn, type ReceiptData } from '@/print';
import { useShift } from '@/shift';
import { Badge, Button, Chip, EmptyState, Field, Kbd, Note, Segmented, Sheet, inputCls } from '@/ui';

const POLL_MS = 2000;
const MINUTE_PRESETS = [30, 60, 120, 180];
/**
 * The "Заканчиваются" filter, and the amber of a tile and of the seat's clock: the chip counts exactly the amber tiles
 * (owner's decision; the red pulse at 5 minutes is gone).
 */
const ENDING_SEC = 10 * 60;
/** Typed PC digits are forgotten after this pause. */
const DIGITS_MS = 2500;
/** From this width the operations feed under the seat card never folds (D-45, its «column» placement). */
const WIDE_QUERY = '(min-width: 1800px)';
/**
 * A screen as short as 1366×768 (the KPI strip is compact there too): a busy seat's card needs the whole column, so the
 * feed under it folds to its bar like under the seating form.
 */
const SHORT_QUERY = '(max-height: 840px)';
/** Below 1800 px, whether the feed is unfolded under a tall form (seating, several PCs): 'feed' — unfolded. */
const PANEL_KEY = 'clubshell.admin.map.panel';
/**
 * `minutes` is required by the contract even where the server ignores it (a package, postpaid): the console sends
 * this then.
 */
const IGNORED_MINUTES = 60;

/** A result line in the seat panel; a money step adds its slip («Чек») and a walk-in guest how to sign in. */
type NoteState = { text: string; tone: 'ok' | 'warn' | 'err'; receipt?: ReceiptData; hint?: string } | null;

/**
 * A PC status in words: `label` for the tile's title and the seat card, `word` for the tile's bottom line (free,
 * service, offline, locked, booked) and the card's status pill.
 */
const STATUS: Record<Seat['pc']['status'], { label: string; word: string }> = {
  free: { label: 'Свободен', word: 'Свободен' },
  busy: { label: 'Занят', word: 'Занят' },
  locked: { label: 'Заблокирован', word: 'Заблокирован' },
  maintenance: { label: 'Обслуживание', word: 'Сервис' },
  booked: { label: 'Бронь', word: 'Бронь' },
  offline: { label: 'Офлайн', word: 'Офлайн' },
};

type Filter = 'free' | 'ending' | 'postpaid' | 'repair' | 'offline';

const FILTERS: { id: Filter; label: string; title?: string; test: (s: Seat, repair: boolean) => boolean }[] = [
  { id: 'free', label: 'Свободны', test: (s) => s.pc.status === 'free' && !s.session },
  {
    id: 'ending',
    label: 'Заканчиваются',
    title: 'Заканчиваются ≤10 мин',
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

function useMedia(query: string): boolean {
  const [hit, setHit] = useState(() => window.matchMedia(query).matches);
  useEffect(() => {
    const m = window.matchMedia(query);
    const on = (): void => setHit(m.matches);
    on();
    m.addEventListener('change', on);
    return () => m.removeEventListener('change', on);
  }, [query]);
  return hit;
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
  | { kind: 'topup'; payee: Payee; initial?: number; title?: string; context?: TopUpContext }
  | { kind: 'extend' }
  | { kind: 'end' }
  | { kind: 'settle'; target: SettleTarget }
  | { kind: 'move'; fromPcId: string; toPcId: string }
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

function Wrench({ severity, size = 14 }: { severity: 'high' | 'medium'; size?: number }): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      aria-label={t('Нужен ремонт')}
      width={size}
      height={size}
      className={clsx('shrink-0', severity === 'high' ? 'text-warning' : 'text-muted')}
      fill="none"
      stroke="currentColor"
      strokeWidth="1.9"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <path d="M14.7 6.3a4 4 0 0 0-5.4 5.4L3.5 17.5a1.8 1.8 0 0 0 2.5 2.5l5.8-5.8a4 4 0 0 0 5.4-5.4l-2.6 2.6-2.3-.6-.6-2.3z" />
    </svg>
  );
}

function Lock(): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      aria-label={t('Заблокирован')}
      width={15}
      height={15}
      className="shrink-0 text-danger"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <rect x="5" y="11" width="14" height="9" rx="2" />
      <path d="M8 11V8a4 4 0 0 1 8 0v3" />
    </svg>
  );
}

/** `7` → `07`: PC numbers on the map. */
function pad2(n: number): string {
  return String(n).padStart(2, '0');
}

/** A seat's clock on the map: `4:12` under an hour, as the amber tile reads in F (`duration` keeps `04:12` elsewhere). */
function clockLeft(sec: number): string {
  return duration(sec).replace(/^0(\d:)/, '$1');
}

/** Text over a picture: the shadows that keep it legible (spec §6.4). */
const SHADOW_NUM = '[text-shadow:0_1px_3px_rgb(0_0_0/0.6)]';
const SHADOW_NAME = '[text-shadow:0_1px_2px_rgb(0_0_0/0.8)]';
const SHADOW_SUB = '[text-shadow:0_0_6px_rgb(0_0_0/0.95),0_1px_2px_rgb(0_0_0/0.9)]';
const SHADOW_READOUT = '[text-shadow:0_0_8px_rgb(0_0_0/0.9),0_1px_3px_rgb(0_0_0/0.85)]';

/** Tile surfaces per state (spec §6.4): tiles never blur, they are drawn over the glass. */
const TILE_BG = {
  free: 'linear-gradient(180deg, rgb(var(--c-text) / 0.022) 0%, rgb(var(--c-text) / 0) 50%), rgb(8 11 15 / 0.4)',
  art: 'rgb(var(--c-art))',
  plain: 'linear-gradient(180deg, rgb(var(--c-accent) / 0.06) 0%, rgb(var(--c-accent) / 0) 70%), rgb(var(--c-art))',
  waiting:
    'repeating-linear-gradient(135deg, rgb(var(--c-accent) / 0.05) 0 1px, rgb(var(--c-accent) / 0) 1px 8px), rgb(10 13 18 / 0.82)',
  booked: 'linear-gradient(180deg, rgb(var(--c-accent) / 0.045) 0%, rgb(var(--c-accent) / 0) 60%), rgb(8 11 15 / 0.5)',
  locked: 'linear-gradient(180deg, rgb(var(--c-danger) / 0.06) 0%, rgb(var(--c-danger) / 0) 60%), rgb(8 11 15 / 0.55)',
  offline: 'rgb(7 9 12 / 0.35)',
  call: 'linear-gradient(180deg, rgb(var(--c-warning) / 0.09) 0%, rgb(var(--c-warning) / 0.02) 100%), rgb(8 11 15 / 0.6)',
} as const;

/**
 * Three lines: the PC number with its marks, who is on it and what they play, the time left (or the running bill of a
 * postpaid session). A busy seat shows its game's cover behind the text when the PC reports one (`seat.game`), else
 * the tariff; a desk session nobody has signed in to yet says «ждёт входа»: its clock already runs (D-49). Amber means
 * the cashier is needed: 10 minutes or less left, a player's call. The selected PC wears the corner brackets; in
 * «Выбрать» mode a box shows whether the PC is in the set; while a move picks its target, a PC that cannot take the
 * session is dimmed and does nothing.
 *
 * The button itself is not clipped (the brackets sit outside it); the picture and the progress line live in an inner
 * clipped layer.
 */
function SeatTile({
  seat,
  tariff,
  selected,
  checked,
  dimmed,
  blocked,
  call,
  onSelect,
  repair,
}: {
  seat: Seat;
  /** The session's tariff name: the tile's second line when the PC reports no game. */
  tariff?: string;
  selected: boolean;
  /** null — no box (not in «Выбрать» mode). */
  checked: boolean | null;
  dimmed: boolean;
  /** Move pick mode: this PC cannot take the session. */
  blocked?: boolean;
  /** The players' calls of this PC. */
  call?: CallGroup;
  onSelect: (e: React.MouseEvent<HTMLButtonElement>) => void;
  /** Worst open repair ticket on this PC (from "Состояние ПК"). */
  repair?: 'high' | 'medium';
}): JSX.Element {
  const s = STATUS[seat.pc.status];
  const status = seat.pc.status;
  const session = seat.session;
  const left = secondsLeft(session);
  const prepaid = session?.isPrepaid ?? false;
  const ending = session !== null && prepaid && left >= 0 && left <= ENDING_SEC;
  const waiting = session !== null && seat.signedIn === false;
  const game = session ? (seat.game ?? null) : null;
  const cover = game?.coverUrl ?? null;
  // Nobody signed in yet: the hatched waiting surface, never a cover (F's tile 06).
  const art = cover !== null && !waiting;
  // The surface: a call wins (amber), then the picture, then what the PC is doing.
  const surface: keyof typeof TILE_BG = call
    ? 'call'
    : art
      ? 'art'
      : session
        ? waiting
          ? 'waiting'
          : 'plain'
        : status === 'locked'
          ? 'locked'
          : status === 'booked'
            ? 'booked'
            : status === 'offline'
              ? 'offline'
              : status === 'busy'
                ? 'plain'
                : 'free';
  const background = checked
    ? `linear-gradient(rgb(var(--c-accent) / 0.08), rgb(var(--c-accent) / 0.08)), ${TILE_BG[surface]}`
    : TILE_BG[surface];
  // How far the prepaid time has run: the line under the tile.
  const fraction =
    session && prepaid && left >= 0 ? Math.min(1, Math.max(0, left / Math.max(1, left + session.secondsUsed))) : null;
  const bar =
    fraction !== null ? (
      <span className={clsx('absolute inset-x-0 bottom-0 h-0.5', ending ? 'bg-warning/[0.18]' : 'bg-accent/[0.12]')}>
        <span
          className={clsx(
            'block h-full',
            ending
              ? 'bg-warning shadow-[0_0_8px_rgb(var(--c-warning)/0.9)]'
              : 'bg-accent opacity-85 shadow-[0_0_8px_rgb(var(--c-accent)/0.7)]',
          )}
          style={{ width: `${Math.round(fraction * 1000) / 10}%` }}
        />
      </span>
    ) : null;
  // The second line under the name: the call, the sign-in still awaited, the game, else postpaid or the tariff. While
  // nobody has signed in the game stays only for a screen reader (the line has no room for both).
  const sub: { text: string; tone: 'warn' | 'accent' | 'plain' | 'hidden' }[] = [];
  if (call) sub.push({ text: t('Зовёт админа'), tone: 'warn' });
  if (waiting) sub.push({ text: t('ждёт входа'), tone: 'accent' });
  if (game) sub.push({ text: game.title, tone: waiting ? 'hidden' : 'plain' });
  else if (session && !waiting && !call && (!prepaid || tariff))
    sub.push({ text: prepaid ? (tariff ?? '') : t('Постоплата'), tone: 'plain' });
  const numberTone =
    status === 'offline' && !session
      ? 'text-text/[0.34]'
      : status === 'locked' && !session
        ? 'text-text/[0.55]'
        : session || call
          ? 'text-white'
          : 'text-text/[0.88]';
  const style: CSSProperties = { background };
  return (
    <button
      type="button"
      id={`seat-${seat.pc.id}`}
      onClick={onSelect}
      // Shift+click picks a range: no text selection on the way.
      onMouseDown={(e) => {
        if (e.shiftKey) e.preventDefault();
      }}
      aria-pressed={checked ?? selected}
      aria-disabled={blocked || undefined}
      data-selected={selected || undefined}
      title={`${seat.pc.name} · ${t(s.label)}${seat.user ? ` · ${nameOf(seat.user)}` : ''}${waiting ? ` · ${t('ждёт входа')}` : ''}${repair ? ` · ${t('Нужен ремонт')}` : ''}${call ? ` · ${t('Вызов администратора')}` : ''}`}
      style={style}
      className={clsx(
        'focus-ring hud-focus group relative flex h-28 min-w-0 flex-col justify-between rounded-md border pb-[13px] pl-3.5 pr-3 pt-3 text-left transition-[border-color,box-shadow,opacity] duration-200',
        // Border and glow: amber when the cashier is needed, the accent when selected, else the state's hairline.
        call
          ? 'border-warning/[0.45] shadow-[0_0_22px_-8px_rgb(var(--c-warning)/0.45)]'
          : ending
            ? 'border-warning/[0.55] shadow-warn'
            : selected
              ? 'border-accent/80 shadow-sel'
              : checked
                ? 'border-accent/70 shadow-[inset_0_0_0_1px_rgb(var(--c-accent)/0.7)]'
                : surface === 'locked'
                  ? 'border-danger/30 hover:border-danger/50'
                  : surface === 'offline'
                    ? 'border-dashed border-accent/[0.12] hover:border-accent/[0.26]'
                    : surface === 'free'
                      ? 'border-accent/[0.07] hover:border-accent/[0.26]'
                      : surface === 'waiting'
                        ? 'border-accent/20 hover:border-accent/[0.26]'
                        : surface === 'booked'
                          ? 'border-accent/[0.14] hover:border-accent/[0.26]'
                          : 'border-accent/[0.16] hover:border-accent/[0.26]',
        (dimmed || blocked) && 'opacity-25',
        blocked && 'cursor-not-allowed',
      )}
    >
      {art ? (
        <GameArt src={cover} variant="tile" zoom>
          {bar}
        </GameArt>
      ) : (
        bar && (
          <span aria-hidden="true" className="pointer-events-none absolute inset-0 overflow-hidden rounded-[inherit]">
            {bar}
          </span>
        )
      )}

      <span className="relative flex h-[22px] items-center justify-between gap-1.5">
        <span
          className={clsx(
            'font-display text-xl font-medium leading-none tracking-[-0.01em]',
            numberTone,
            art && SHADOW_NUM,
          )}
        >
          {pad2(seat.pc.number)}
        </span>
        <span className="flex min-w-0 items-center gap-1">
          {call && (
            <span
              className={clsx(
                'flex h-[22px] w-[22px] items-center justify-center rounded-full bg-warning/[0.16] shadow-[0_0_0_4px_rgb(var(--c-warning)/0.07)] [&>svg]:h-3 [&>svg]:w-3',
                call.ringing && 'anim-warn-glow',
              )}
            >
              <CallMark ringing={call.ringing} />
            </span>
          )}
          {ending && !call && (
            <span
              role="img"
              aria-label={t('Заканчивается')}
              className="flex h-[22px] w-[22px] items-center justify-center rounded-[6px] border border-warning/50 bg-bg/[0.62]"
            >
              <AlertTriangleIcon size={13} strong className="text-warning" />
            </span>
          )}
          {session && !prepaid && (
            <span
              title={t('Постоплата')}
              className="inline-flex h-5 items-center rounded-sm border border-accent/[0.28] bg-bg/60 px-1.5 text-sm font-medium leading-none text-accent"
            >
              ∞
            </span>
          )}
          {/* In «Выбрать» mode only the marks that need the cashier stay beside the box; the title lists the rest. */}
          {checked === null && (
            <>
              {waiting && !call && <SignInIcon size={15} className="text-accent" />}
              {status === 'locked' && <Lock />}
              {repair && <Wrench severity={repair} size={13} />}
              {status === 'offline' && !session && <PowerIcon size={14} className="text-muted" />}
              {status === 'booked' && !session && <CalendarIcon size={14} className="text-accent" />}
            </>
          )}
          {checked !== null && (
            <span
              aria-hidden="true"
              className={clsx(
                'flex h-4 w-4 shrink-0 items-center justify-center rounded-[4px] border',
                checked ? 'border-accent bg-accent text-on-accent' : 'border-muted/60 bg-bg/40',
              )}
            >
              {checked && <CheckIcon size={12} strokeWidth={2.4} />}
            </span>
          )}
        </span>
      </span>

      {(session || call) && (
        <span className="relative block min-w-0">
          {session && seat.user && (
            <span
              className={clsx('block truncate text-[12.5px] font-semibold leading-4 text-white', art && SHADOW_NAME)}
            >
              {nameOf(seat.user)}
            </span>
          )}
          {sub.length > 0 && (
            <span
              className={clsx(
                'mt-0.5 block truncate font-mono text-[9px] font-medium uppercase leading-3 tracking-[0.06em] text-[#D3DBE1]',
                art && SHADOW_SUB,
              )}
            >
              {sub.map((x, i) => (
                <span
                  key={i}
                  className={clsx(
                    x.tone === 'warn' && 'font-semibold tracking-[0.12em] text-warning',
                    x.tone === 'accent' && 'tracking-[0.08em] text-accent',
                    x.tone === 'hidden' && 'sr-only',
                  )}
                >
                  {i > 0 ? ' · ' : ''}
                  {x.text}
                </span>
              ))}
            </span>
          )}
        </span>
      )}

      {session ? (
        // Postpaid has no time left (`secondsLeft` −1): its running bill, the ∞ mark is the badge above.
        prepaid ? (
          <span
            className={clsx(
              'num-dot relative text-base leading-none tracking-[0.02em]',
              ending ? 'text-warning' : waiting && !art ? 'text-text/[0.86]' : 'text-white',
              art && SHADOW_READOUT,
            )}
          >
            {clockLeft(left)}
          </span>
        ) : (
          <span className="relative flex items-baseline gap-1 whitespace-nowrap">
            <span className={clsx('num-dot text-[15px] leading-none text-white', art && SHADOW_READOUT)}>
              {moneyParts(session.cost.amount).num}
            </span>
            <span className="text-[10.5px] font-medium leading-none text-soft">{t('сум')}</span>
          </span>
        )
      ) : (
        <span
          className={clsx(
            'relative flex items-center gap-1.5 font-mono text-[10px] uppercase leading-none',
            status === 'locked'
              ? 'font-semibold tracking-[0.12em] text-danger'
              : 'font-medium tracking-[0.14em] text-muted',
          )}
        >
          {status === 'maintenance' && <WrenchIcon size={12} />}
          {t(s.word)}
        </span>
      )}
    </button>
  );
}

/** The panel's result line, with «Чек» for a money step and the guest's way in after a walk-in seat. */
function SeatNote({ note }: { note: NoteState }): JSX.Element | null {
  if (!note) return null;
  return (
    <Note tone={note.tone} role={note.tone === 'err' ? 'alert' : 'status'}>
      <span className="flex items-start justify-between gap-3">
        <span className="min-w-0">{note.text}</span>
        {note.receipt && <ReceiptButton receipt={note.receipt} size="xs" className="-my-0.5 shrink-0" />}
      </span>
      {note.hint && <span className="mt-1.5 block text-xs font-normal text-dim">{note.hint}</span>}
    </Note>
  );
}

/**
 * The seat card's band (spec §6.7): the game's hero (or its cover) under the scrims and the accent edge, else the club's
 * wallpaper faint over the blueprint grid; a mono caption and a status pill on top, the title (a name, «Посадить на
 * ПК 05») and a meta line under it. `children` sit at the band's bottom (the readouts over a picture).
 */
function SeatStrip({
  art,
  height,
  caption,
  pill,
  title,
  meta,
  children,
}: {
  art: string | null;
  height: number;
  caption: string;
  pill?: ReactNode;
  title: string;
  meta?: ReactNode;
  children?: ReactNode;
}): JSX.Element {
  const club = useClub();
  const wallpaper = club.wallpaperUrl || DEFAULT_WALLPAPER;
  return (
    <div className="relative shrink-0 overflow-hidden" style={{ height }}>
      <GameArt
        src={art}
        variant="strip"
        edge
        fallback={
          <span className="absolute inset-0 bg-art">
            <span
              className="absolute inset-0 bg-cover bg-[50%_40%] opacity-[0.35] [filter:blur(1.5px)_saturate(0.7)]"
              style={{ backgroundImage: `url(${JSON.stringify(wallpaper)})` }}
            />
            <span className="hud-grid absolute inset-0 opacity-60" />
            <span
              className="absolute inset-0"
              style={{
                background:
                  'linear-gradient(90deg, rgb(7 9 12 / 0.75) 0%, rgb(7 9 12 / 0.2) 70%), linear-gradient(180deg, rgb(10 13 18 / 0) 35%, rgb(var(--c-art)) 100%)',
              }}
            />
          </span>
        }
      />
      <div className="relative flex h-full flex-col px-4 pt-3.5">
        <div className="flex h-5 items-center justify-between gap-3">
          <span className="label-sm min-w-0 truncate text-text/90">{caption}</span>
          {pill}
        </div>
        <h2
          className={clsx(
            'truncate font-display text-[26px] font-medium leading-[1.1] tracking-[-0.02em] text-white [text-shadow:0_2px_12px_rgb(0_0_0/0.6)]',
            art ? 'mt-6' : 'mt-2.5',
          )}
        >
          {title}
        </h2>
        {meta && (
          <p className="mt-[7px] truncate text-xs leading-none text-text/80 [text-shadow:0_1px_3px_rgb(0_0_0/0.8)]">
            {meta}
          </p>
        )}
        {children}
      </div>
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

/**
 * One line of a money well: a mono label, the sum as a Doto readout with «сум». `exact` keeps the tiyin (a guest's
 * price, a debt); `tone` colours the figure (`warn` what is still to pay, `muted` a zero).
 */
function SumRow({
  label,
  minor,
  exact,
  tone,
}: {
  label: string;
  minor: number;
  exact?: boolean;
  tone?: 'warn' | 'muted';
}): JSX.Element {
  return (
    <div className="flex min-h-10 items-center justify-between gap-3 px-3.5 py-2">
      <dt className="label-sm">{label}</dt>
      <dd className="flex items-baseline gap-1.5 whitespace-nowrap">
        <span
          className={clsx(
            'num-dot text-xl leading-none',
            tone === 'warn' ? 'text-warning' : tone === 'muted' ? 'text-muted' : 'text-hi',
          )}
        >
          {exact ? exactDigits(minor) : moneyParts(minor).num}
        </span>
        <span className="text-[11.5px] font-medium leading-none text-muted">{t('сум')}</span>
      </dd>
    </div>
  );
}

/** "К оплате N · на балансе M · доплата K": the wells of a seat or an extension, one per line. */
function PaySummary({ price, balance }: { price: number; balance: number }): JSX.Element {
  const shortfall = Math.max(0, price - balance);
  return (
    <dl className="well flex flex-col divide-y divide-accent/[0.07]">
      <SumRow label={t('К оплате')} minor={price} />
      <SumRow label={t('На балансе')} minor={balance} />
      <SumRow label={t('Доплата')} minor={shortfall} tone={shortfall > 0 ? 'warn' : 'muted'} />
    </dl>
  );
}

/** A guest pays exactly the price, to the tiyin (D-48): no balance, no change left on a throwaway account. */
function ExactSummary({ price, label }: { price: number; label?: string }): JSX.Element {
  return (
    <dl className="well">
      <SumRow label={label ?? t('К оплате')} minor={price} exact />
    </dl>
  );
}

/** What a PC did with a command, in words: never «done» when it was only queued or did not answer (D-66). */
function ackNote(ack: CommandAck, done: string): NoteState {
  if (ack.ok) return { text: done, tone: 'ok' };
  if (ack.error?.code === 'agentOffline') return { text: t('ПК офлайн — команда в очереди до 10 мин'), tone: 'warn' };
  if (ack.error?.code === 'timeout') return { text: t('Нет ответа от ПК за 30 с — команда в очереди'), tone: 'warn' };
  return { text: t('Ошибка: {code}', { code: ack.error?.code ?? '—' }), tone: 'err' };
}

/** One PC's result of a bulk command (D-65), as the results list and the panel's note say it. */
function outcomeText(r: BulkResult): string {
  const parts: string[] = [];
  const refunded = amountOf(r.ended?.refunded);
  const charged = amountOf(r.ended?.charged);
  if (r.ended) {
    parts.push(
      refunded && refunded > 0
        ? t('сеанс завершён, возврат {sum}', { sum: moneyExact(refunded) })
        : charged && charged > 0
          ? t('сеанс завершён, списано {sum}', { sum: moneyExact(charged) })
          : t('сеанс завершён'),
    );
  }
  switch (r.outcome) {
    case 'done':
      parts.push(t('✓ выполнено'));
      break;
    case 'queued':
      parts.push(t('в очереди до 10 мин'));
      break;
    case 'noAnswer':
      parts.push(t('нет ответа за 30 с'));
      break;
    case 'failed':
      parts.push(t('ошибка: {code}', { code: r.ack?.error?.code ?? '—' }));
      break;
    case 'skipped':
      parts.push(
        r.skipped === 'sessionOpen'
          ? t('пропущен: идёт сеанс')
          : r.skipped === 'offline'
            ? t('пропущен: офлайн')
            : t('пропущен: нет такого ПК'),
      );
      break;
  }
  return parts.join(' · ');
}

/**
 * A button of a row in the 360–400 px card that may wrap: a long translation («Qayta yuklash (6 tadan 4)») goes to a
 * second line instead of widening the card.
 */
const WRAP = '!h-auto min-h-11 !whitespace-normal py-1.5 text-center leading-tight';

/** The button of each bulk command (its «Повторить» after a lost answer says which). */
const BULK_VERB: Record<PcCommandKind, string> = {
  message: 'Сообщение',
  lock: 'Заблокировать',
  unlock: 'Разблокировать',
  reboot: 'Перезагрузить',
  shutdown: 'Выключить',
};

const POWER_LABEL: Record<'reboot' | 'shutdown', { verb: string; done: string }> = {
  reboot: { verb: 'Перезагрузить', done: 'ПК перезагружается' },
  shutdown: { verb: 'Выключить', done: 'ПК выключается' },
};

/** The players a power command or a lock would interrupt: PC, who, time left. */
function BusyList({ seats }: { seats: Seat[] }): JSX.Element {
  return (
    <ul
      aria-label={t('Идут сеансы')}
      className="well thin-scrollbar flex max-h-56 flex-col divide-y divide-accent/[0.07] overflow-y-auto px-3.5"
    >
      {seats.map((s) => (
        <li key={s.pc.id} className="flex min-h-10 items-center justify-between gap-3 py-2 text-[13px]">
          <span className="min-w-0 truncate font-medium text-text">
            <span className="mr-2 font-mono text-[11px] font-medium uppercase tracking-[0.08em] text-muted">
              {pcLabel(s.pc.name)}
            </span>
            {s.user ? nameOf(s.user) : '—'}
          </span>
          <span className="tnum shrink-0 font-mono text-[11.5px] text-dim">
            {s.session?.isPrepaid
              ? t('осталось {time}', { time: duration(secondsLeft(s.session)) })
              : s.session
                ? t('играет {time}', { time: duration(s.session.secondsUsed) })
                : ''}
          </span>
        </li>
      ))}
    </ul>
  );
}

/**
 * Asks before a command interrupts players (D-66): a reboot or shutdown ends their sessions first (unused prepaid time
 * back to the balance), a lock closes the game while the clock runs. One busy PC may be moved instead.
 */
function PowerConfirm({
  kind,
  busy,
  sending,
  onConfirm,
  onMove,
  onClose,
}: {
  kind: 'lock' | 'reboot' | 'shutdown';
  busy: Seat[];
  sending: boolean;
  onConfirm: () => void;
  onMove?: (seat: Seat) => void;
  onClose: () => void;
}): JSX.Element {
  const lock = kind === 'lock';
  const verb = lock ? t('Заблокировать') : t(POWER_LABEL[kind].verb);
  const one = busy.length === 1 ? busy[0] : undefined;
  return (
    <Sheet
      title={t('{verb} · идут сеансы: {n}', { verb, n: busy.length })}
      onClose={onClose}
      footer={
        <div className="ml-auto flex flex-wrap justify-end gap-2">
          <Button variant="ghost" autoFocus onClick={onClose}>
            {t('Отмена')}
          </Button>
          {one && onMove && !lock && (
            <Button variant="secondary" onClick={() => onMove(one)}>
              <SwapIcon size={15} />
              {t('Пересадить')}
            </Button>
          )}
          <Button variant="danger" disabled={sending} onClick={onConfirm}>
            {sending ? '…' : verb}
          </Button>
        </div>
      }
    >
      <BusyList seats={busy} />
      <Note tone="warn">
        {lock ? t('Игра закроется, время идёт') : t('Сеанс будет завершён, неиспользованное время вернётся на баланс')}
      </Note>
    </Sheet>
  );
}

/** A command sent with its `Idempotency-Key` whose answer never came: only it may go again, under that key (D-46). */
interface LostCommand {
  input: BulkInput;
  key: string;
}

/**
 * Power commands of one PC whose answer was lost, by PC. They outlive the seat panel — the session the command ended
 * turns the seat free, which mounts another panel — so the retry is the request that went, never one rebuilt from the
 * map as it is now (that would queue a second reboot under a new key). A sign-out drops them.
 */
const lostPower = new Map<string, LostCommand>();
onSignedOut(() => lostPower.clear());

/**
 * Message, lock, reboot, shutdown: behind "Ещё ⋯" so the money actions stay on top. Each says what the PC did: an
 * offline PC queues the command, a silent one did not answer. Reboot and shutdown go through the bulk route (D-66): of a
 * busy PC they ask first and end the session the confirm showed at the desk (refund, journal) before the PC restarts.
 * After a lost answer only «Повторить» of that same command is offered until a definite answer.
 *
 * The «Ещё ⋯» button that opens it is the card's (`open`): a 64 px tool beside «Бар» on a busy seat, a full row on a
 * free one ({@link MoreButton}).
 */
function TechActions({
  seat,
  busy,
  run,
  onStartMove,
  open,
}: {
  seat: Seat;
  busy: boolean;
  run: (key: string, fn: () => Promise<string | NoteState>) => Promise<void>;
  onStartMove?: () => void;
  open: boolean;
}): JSX.Element | null {
  const [message, setMessage] = useState('');
  /** The confirm with the seat as it was when it opened: its session is the one that may be ended. */
  const [confirm, setConfirm] = useState<{ kind: 'reboot' | 'shutdown'; seat: Seat } | null>(null);
  const [lost, setLost] = useState<LostCommand | null>(() => lostPower.get(seat.pc.id) ?? null);
  const current = useRef(seat.pc.id);
  current.current = seat.pc.id;
  useEffect(() => {
    setConfirm(null);
    setLost(lostPower.get(seat.pc.id) ?? null);
  }, [seat.pc.id]);
  const command = (kind: 'lock' | 'unlock', done: string): void =>
    void run(kind, async () => ackNote((await adminApi.command(seat.pc.id, { kind })).ack, done));
  /** Sends `input` under `key`: a new command has a new key, a retry the one it first went with. */
  const send = (input: BulkInput, key: string): void => {
    setConfirm(null);
    const pcId = input.pcIds[0] ?? seat.pc.id;
    const kind = input.kind === 'shutdown' ? 'shutdown' : 'reboot';
    const keep = (l: LostCommand | null): void => {
      if (l) lostPower.set(pcId, l);
      else lostPower.delete(pcId);
      if (current.current === pcId) setLost(l);
    };
    void run(kind, async () => {
      try {
        const r = await adminApi.pcCommands(input, key);
        keep(null);
        const result = r.results[0];
        if (!result) return { text: t('ПК не ответил'), tone: 'warn' };
        const tone = result.outcome === 'failed' ? 'err' : result.outcome === 'done' ? 'ok' : 'warn';
        const text = result.outcome === 'done' && !result.ended ? t(POWER_LABEL[kind].done) : outcomeText(result);
        return { text, tone };
      } catch (e) {
        if (!isLostAnswer(e)) {
          keep(null);
          throw e;
        }
        keep({ input, key });
        return {
          text: t('Ответ сервера не пришёл: команда могла уйти. Повторите её — дважды она не отправится.'),
          tone: 'err',
        };
      }
    });
  };
  const power = (kind: 'reboot' | 'shutdown', ending: Seat | null): void =>
    send(
      {
        pcIds: [seat.pc.id],
        kind,
        ...(ending?.session ? { includeBusy: true, sessionIds: [ending.session.id] } : {}),
      },
      newKey(),
    );
  const ask = (kind: 'reboot' | 'shutdown'): void => {
    if (seat.session) setConfirm({ kind, seat });
    else power(kind, null);
  };
  const lostKind = lost?.input.kind === 'shutdown' ? 'shutdown' : 'reboot';
  if (!open && !lost && !confirm) return null;
  return (
    <section className="flex flex-col gap-2">
      {open && (
        <div className="well flex flex-col gap-3 p-3">
          {seat.pc.agentVersion && (
            <span className="label-sm tnum">
              {t('Агент {agent} · Оболочка {shell}', {
                agent: seat.pc.agentVersion,
                shell: seat.pc.shellVersion ?? '—',
              })}
            </span>
          )}
          <Field label={t('Сообщение на экран')}>
            <div className="flex gap-2">
              <input
                className={inputCls}
                value={message}
                placeholder={t('Закрываемся через 20 минут')}
                onChange={(e) => setMessage(e.target.value)}
              />
              <Button
                variant="secondary"
                disabled={busy || message.trim().length === 0}
                onClick={() =>
                  void run('msg', async () => {
                    const r = await adminApi.command(seat.pc.id, { kind: 'message', text: message.trim() });
                    setMessage('');
                    return ackNote(r.ack, t('Сообщение доставлено'));
                  })
                }
              >
                {t('Отправить')}
              </Button>
            </div>
          </Field>
          <div className="grid grid-cols-2 gap-2">
            <Button
              variant="tertiary"
              className={WRAP}
              disabled={busy}
              onClick={() => command('lock', t('ПК заблокирован'))}
            >
              {t('Заблокировать')}
            </Button>
            <Button
              variant="tertiary"
              className={WRAP}
              disabled={busy}
              onClick={() => command('unlock', t('ПК разблокирован'))}
            >
              {t('Разблокировать')}
            </Button>
            <Button variant="tertiary" className={WRAP} disabled={busy || lost !== null} onClick={() => ask('reboot')}>
              {t('Перезагрузить')}
            </Button>
            <Button
              variant="tertiary"
              className={WRAP}
              disabled={busy || lost !== null}
              onClick={() => ask('shutdown')}
            >
              {t('Выключить')}
            </Button>
          </div>
        </div>
      )}
      {lost && (
        <div className="flex flex-col gap-2">
          <Note tone="warn" role="status">
            {t('Ответ сервера не пришёл: команда могла уйти. Повторите её — дважды она не отправится.')}
          </Note>
          <Button variant="secondary" disabled={busy} onClick={() => send(lost.input, lost.key)}>
            {busy ? '…' : `${t('Повторить')} · ${t(POWER_LABEL[lostKind].verb)}`}
          </Button>
        </div>
      )}
      {confirm && (
        <PowerConfirm
          kind={confirm.kind}
          busy={[confirm.seat]}
          sending={busy}
          onConfirm={() => power(confirm.kind, confirm.seat)}
          onMove={
            onStartMove
              ? () => {
                  setConfirm(null);
                  onStartMove();
                }
              : undefined
          }
          onClose={() => setConfirm(null)}
        />
      )}
    </section>
  );
}

/**
 * «Ещё ⋯», the toggle of {@link TechActions}: `compact` is the 64 px tool of the busy seat's row (what it holds in its
 * tooltip), else a full row with the list on the right. Its name always starts with «Ещё ⋯».
 */
function MoreButton({
  open,
  onToggle,
  compact,
}: {
  open: boolean;
  onToggle: () => void;
  compact?: boolean;
}): JSX.Element {
  return compact ? (
    <Button
      variant="tertiary"
      className={clsx('shrink-0 !px-1.5 !text-[12.5px]', open && 'choice-on')}
      aria-expanded={open}
      title={t('сообщение, блокировка, питание')}
      onClick={onToggle}
    >
      {t('Ещё ⋯')}
    </Button>
  ) : (
    <Button
      variant="tertiary"
      className={clsx('w-full justify-between', open && 'choice-on')}
      aria-expanded={open}
      onClick={onToggle}
    >
      <span>{t('Ещё ⋯')}</span>
      <span className="flex items-center gap-2 text-xs font-normal text-muted">
        {t('сообщение, блокировка, питание')}
        <ChevronDownIcon size={15} className={clsx('transition-transform duration-200', open && 'rotate-180')} />
      </span>
    </Button>
  );
}

/** Ready-made texts of a message to several PCs. */
const MESSAGE_PRESETS = ['Закрываемся через 20 минут', 'Подойдите, пожалуйста, к администратору', 'Пожалуйста, потише'];

/**
 * Two or more PCs picked on the map (D-66): one command to all of them, with a count of the PCs it will reach on each
 * button and why the rest are left out (offline PCs get no lock or power command: the next player would). Busy PCs are
 * listed before a lock or a power command, and a reboot or shutdown ends only the sessions that list showed; then every
 * PC's result: done, queued (offline: up to 10 minutes), no answer, skipped and why, the session ended with its refund.
 * «Повторить для неудачных» sends again only to the failed ones. After a lost answer the panel offers only «Повторить»
 * of the request that went (its body and key), never one rebuilt from the map as it is now.
 */
function BulkPanel({
  seats,
  onDone,
  onClear,
  onStartMove,
}: {
  seats: Seat[];
  onDone: () => void;
  onClear: () => void;
  onStartMove: (seat: Seat) => void;
}): JSX.Element {
  const shift = useShift();
  const [text, setText] = useState('');
  const [level, setLevel] = useState<'info' | 'warning'>('info');
  const [sending, setSending] = useState<PcCommandKind | null>(null);
  /** The confirm with the busy PCs as they were when it opened: only their sessions may be ended. */
  const [confirm, setConfirm] = useState<{ kind: 'lock' | 'reboot' | 'shutdown'; busy: Seat[] } | null>(null);
  const [results, setResults] = useState<{ input: BulkInput; list: BulkResult[] } | null>(null);
  /** The command whose answer was lost: until a definite answer only it goes again, with its key and its body. */
  const [lost, setLost] = useState<LostCommand | null>(null);
  const [error, setError] = useState<string | null>(null);
  const offline = seats.filter((s) => s.pc.status === 'offline');
  const reachable = seats.filter((s) => s.pc.status !== 'offline');
  const busy = reachable.filter((s) => s.session !== null);
  const numbers = seats.map((s) => String(s.pc.number).padStart(2, '0'));
  const names = new Map(seats.map((s) => [s.pc.id, s.pc.name]));
  const held = sending !== null || lost !== null;

  /** Sends `input` under `key`: a new command has a new key, a retry the one it first went with. */
  const send = async (input: BulkInput, key: string): Promise<void> => {
    setSending(input.kind);
    setConfirm(null);
    setError(null);
    try {
      const r = await adminApi.pcCommands(input, key);
      setLost(null);
      setResults({ input, list: r.results });
      onDone();
      shift.refresh();
    } catch (e) {
      const unknown = isLostAnswer(e);
      setLost(unknown ? { input, key } : null);
      setError(
        unknown
          ? t('Ответ сервера не пришёл: команда могла уйти. Повторите её — дважды она не отправится.')
          : describe(e),
      );
    } finally {
      setSending(null);
    }
  };

  /** A new command to `pcIds`; `ending` — the busy PCs the confirm listed, whose sessions end first (reboot, shutdown). */
  const fresh = (kind: PcCommandKind, pcIds: string[], ending: Seat[] | null): void =>
    void send(
      {
        pcIds,
        kind,
        ...(kind === 'message' ? { text: text.trim(), level } : {}),
        ...(ending ? { includeBusy: true, sessionIds: ending.flatMap((s) => (s.session ? [s.session.id] : [])) } : {}),
      },
      newKey(),
    );

  const act = (kind: PcCommandKind): void => {
    if ((kind === 'lock' || kind === 'reboot' || kind === 'shutdown') && busy.length > 0) setConfirm({ kind, busy });
    else
      fresh(
        kind,
        seats.map((s) => s.pc.id),
        null,
      );
  };
  const count = (n: number): string => (n === seats.length ? `(${n})` : t('({k} из {n})', { k: n, n: seats.length }));
  const failed = results?.list.filter((r) => r.outcome === 'failed') ?? [];

  return (
    <div className="panel-solid flex flex-col overflow-hidden">
      <SeatStrip
        art={null}
        height={104}
        caption={t('Несколько ПК')}
        title={t('Выбрано {n}', { n: seats.length })}
        meta={
          <span className="tnum font-mono">
            {numbers.slice(0, 16).join(', ')}
            {numbers.length > 16 ? '…' : ''}
          </span>
        }
      />

      <div className="flex flex-col gap-4 px-4 pb-4 pt-3.5">
        <section className="flex flex-col gap-2.5">
          <Field label={t('Сообщение на экран')}>
            <input
              className={inputCls}
              value={text}
              maxLength={500}
              placeholder={t('Текст для игроков')}
              onChange={(e) => setText(e.target.value)}
            />
          </Field>
          <div className="flex flex-wrap gap-1.5">
            {MESSAGE_PRESETS.map((m) => (
              <Button
                key={m}
                size="sm"
                variant="tertiary"
                className="!h-auto min-h-9 !whitespace-normal py-1.5 text-left leading-tight"
                onClick={() => setText(t(m))}
              >
                {t(m)}
              </Button>
            ))}
          </div>
          <Segmented
            label={t('Важность')}
            value={level}
            options={[
              { id: 'info', label: t('Обычное') },
              { id: 'warning', label: t('Важное') },
            ]}
            onChange={setLevel}
          />
          <Button variant="primary" disabled={held || text.trim().length === 0} onClick={() => act('message')}>
            {sending === 'message' ? '…' : `${t('Сообщение')} ${count(seats.length)}`}
          </Button>
        </section>

        <section className="grid grid-cols-2 gap-2">
          <Button
            variant="tertiary"
            className={clsx(WRAP, '!px-2.5')}
            disabled={held || reachable.length === 0}
            onClick={() => act('lock')}
          >
            {sending === 'lock' ? '…' : `${t('Заблокировать')} ${count(reachable.length)}`}
          </Button>
          <Button variant="tertiary" className={clsx(WRAP, '!px-2.5')} disabled={held} onClick={() => act('unlock')}>
            {sending === 'unlock' ? '…' : `${t('Разблокировать')} ${count(seats.length)}`}
          </Button>
          <Button
            variant="tertiary"
            className={clsx(WRAP, '!px-2.5')}
            disabled={held || reachable.length === 0}
            onClick={() => act('reboot')}
          >
            {sending === 'reboot' ? '…' : `${t('Перезагрузить')} ${count(reachable.length)}`}
          </Button>
          <Button
            variant="tertiary"
            className={clsx(WRAP, '!px-2.5')}
            disabled={held || reachable.length === 0}
            onClick={() => act('shutdown')}
          >
            {sending === 'shutdown' ? '…' : `${t('Выключить')} ${count(reachable.length)}`}
          </Button>
        </section>
        {offline.length > 0 && (
          <p className="text-xs leading-5 text-muted">
            {t('Офлайн: {list} — блокировку и питание они не получат', {
              list: offline.map((s) => pad2(s.pc.number)).join(', '),
            })}
          </p>
        )}
        {busy.length > 0 && (
          <p className="text-xs leading-5 text-muted">
            {t('Идут сеансы: {list} — перед блокировкой и питанием спросим', {
              list: busy.map((s) => pad2(s.pc.number)).join(', '),
            })}
          </p>
        )}
        {error && (
          <Note tone="err" role="alert">
            {error}
          </Note>
        )}
        {lost && (
          <Button variant="primary" disabled={sending !== null} onClick={() => void send(lost.input, lost.key)}>
            {sending !== null
              ? '…'
              : `${t('Повторить')} · ${t(BULK_VERB[lost.input.kind])} (${lost.input.pcIds.length})`}
          </Button>
        )}

        {results && (
          <section className="flex flex-col gap-2">
            <h3 className="label text-text">{t('Результат по ПК')}</h3>
            <ul aria-label={t('Результат по ПК')} className="well flex flex-col divide-y divide-accent/[0.07] px-3.5">
              {results.list.map((r) => (
                <li
                  key={r.pcId}
                  data-pc-result={r.pcId}
                  className="flex min-h-9 items-baseline gap-3 py-2 text-[13px] leading-5"
                >
                  <span className="w-14 shrink-0 font-mono text-[11px] uppercase tracking-[0.08em] text-muted">
                    {pcLabel(r.pcName ?? names.get(r.pcId) ?? '—')}
                  </span>
                  <span
                    className={clsx(
                      'min-w-0',
                      r.outcome === 'done' && 'text-accent',
                      r.outcome === 'queued' && 'text-text',
                      r.outcome === 'failed' && 'text-danger-ink',
                      (r.outcome === 'skipped' || r.outcome === 'noAnswer') && 'text-warning',
                    )}
                  >
                    {outcomeText(r)}
                  </span>
                </li>
              ))}
            </ul>
            {failed.length > 0 && (
              <Button
                variant="secondary"
                disabled={held}
                // The same command (its text, its sessions to end) to the failed PCs only: a new request.
                onClick={() => void send({ ...results.input, pcIds: failed.map((r) => r.pcId) }, newKey())}
              >
                {t('Повторить для неудачных ({n})', { n: failed.length })}
              </Button>
            )}
          </section>
        )}

        <div aria-hidden="true" className="h-px bg-accent/[0.08]" />
        <Button variant="ghost" onClick={onClear}>
          {t('Снять выбор')}
          <Kbd>Esc</Kbd>
        </Button>
      </div>

      {confirm && (
        <PowerConfirm
          kind={confirm.kind}
          busy={confirm.busy}
          sending={sending !== null}
          onConfirm={() =>
            fresh(
              confirm.kind,
              seats.map((s) => s.pc.id),
              confirm.kind === 'lock' ? null : confirm.busy,
            )
          }
          onMove={(s) => {
            setConfirm(null);
            onStartMove(s);
          }}
          onClose={() => setConfirm(null)}
        />
      )}
    </div>
  );
}

/**
 * «Пересадить» confirmed (D-59..D-61): from → to, the zones, the tariff kept or one to pick when the new zone does not
 * sell it (prepaid; postpaid keeps its price), that the clock runs on and the game on the old PC closes, and how the
 * player signs in there. One key is held until a definite answer: a lost one offers only the same move again.
 */
function MoveSheet({
  from,
  to,
  tariffs,
  onClose,
  onMoved,
}: {
  from: Seat;
  to: Seat;
  tariffs: Tariff[];
  onClose: () => void;
  onMoved: (r: MoveResponse) => void;
}): JSX.Element {
  const session = from.session;
  const user = from.user;
  const current = tariffs.find((x) => x.id === session?.tariffId);
  const sold = (tf: Tariff): boolean =>
    tf.zones.length === 0 || tf.zones.some((z) => z.toLowerCase() === to.pc.zone.toLowerCase());
  const choices = useMemo(
    () => zoneTariffsOf(tariffs, to.pc.zone).filter((tf) => !tf.isPackage),
    [tariffs, to.pc.zone],
  );
  const [needTariff, setNeedTariff] = useState(Boolean(session?.isPrepaid && current && !sold(current)));
  const [tariffId, setTariffId] = useState(choices[0]?.id ?? '');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lost, setLost] = useState(false);
  const key = useHeldKey();
  const guest = user?.role === 'guest';
  if (!session || !user) {
    return (
      <Sheet title={t('Пересадить')} onClose={onClose}>
        <p className="text-sm">{t('На ПК больше нет сеанса')}</p>
      </Sheet>
    );
  }
  const move = async (): Promise<void> => {
    setBusy(true);
    setError(null);
    try {
      const r = await adminApi.moveSession(
        {
          fromPcId: from.pc.id,
          sessionId: session.id,
          toPcId: to.pc.id,
          ...(needTariff && tariffId ? { tariffId } : {}),
        },
        key.take(),
      );
      key.settle();
      onMoved(r);
    } catch (e) {
      key.settle(e);
      setLost(isLostAnswer(e));
      if (reasonOf(e) === 'tariffZone' && !needTariff) {
        // The map was older than the tariffs: pick one sold in the new zone. Another body, so another key.
        setNeedTariff(true);
        key.reset();
      }
      setError(
        isLostAnswer(e)
          ? t('Ответ сервера не пришёл: пересадка могла пройти. Повторите её — дважды она не пройдёт.')
          : describe(e),
      );
    } finally {
      setBusy(false);
    }
  };
  const zoneChange = from.pc.zone.toLowerCase() !== to.pc.zone.toLowerCase();
  return (
    <Sheet
      title={t('Пересадить · {from} → {to}', { from: pcLabel(from.pc.name), to: pcLabel(to.pc.name) })}
      caption={zoneChange ? t('Зона: {from} → {to}', { from: from.pc.zone, to: to.pc.zone }) : from.pc.zone}
      onClose={onClose}
      footer={
        <div className="ml-auto flex gap-2">
          <Button variant="ghost" onClick={onClose}>
            {t('Отмена')}
          </Button>
          <Button variant="primary" autoFocus disabled={busy || (needTariff && !tariffId)} onClick={() => void move()}>
            <SwapIcon size={16} strong />
            {busy ? '…' : lost ? t('Повторить') : t('Пересадить')}
          </Button>
        </div>
      }
    >
      <p className="flex items-baseline justify-between gap-3 text-sm">
        <span className="min-w-0 truncate font-medium text-hi">{nameOf(user)}</span>
        <span className="tnum shrink-0 font-mono text-[12px] text-dim">
          {session.isPrepaid
            ? t('осталось {time}', { time: duration(secondsLeft(session)) })
            : t('играет {time}', { time: duration(session.secondsUsed) })}
        </span>
      </p>
      {!session.isPrepaid ? (
        <p className="text-[13px] text-dim">{t('Постоплата: цена минуты остаётся прежней')}</p>
      ) : needTariff ? (
        <Field label={t('Тариф на новом ПК')} hint={t('Нынешний тариф не продаётся в этой зоне')}>
          <select
            className={inputCls}
            value={tariffId}
            disabled={busy || lost}
            onChange={(e) => {
              setTariffId(e.target.value);
              key.reset();
            }}
          >
            {choices.map((tf) => (
              <option key={tf.id} value={tf.id}>
                {tf.name} · {money(tf.pricePerHour)}
                {t(' / ч')}
              </option>
            ))}
          </select>
        </Field>
      ) : (
        current && <p className="text-[13px] text-dim">{t('Тариф остаётся: {name}', { name: current.name })}</p>
      )}
      <ul className="well flex flex-col gap-2 px-3.5 py-3 text-[13px] leading-5 text-text">
        <li className="flex gap-2.5">
          <TimerIcon size={16} className="mt-0.5 text-warning" />
          {t('Часы идут: время не останавливается, пока игрок пересаживается')}
        </li>
        <li className="flex gap-2.5">
          <PowerIcon size={16} className="mt-0.5 text-muted" />
          {t('На {pc} игра закроется', { pc: pcLabel(from.pc.name) })}
        </li>
        <li className="flex gap-2.5">
          <SignInIcon size={16} className="mt-0.5 text-accent" />
          {guest
            ? t('{pc}: гость входит кнопкой «Гость»', { pc: pcLabel(to.pc.name) })
            : t('{pc}: игрок входит своим логином', { pc: pcLabel(to.pc.name) })}
        </li>
      </ul>
      <Note note={error ? { text: error, tone: 'err' } : null} />
    </Sheet>
  );
}

function SeatPanel({
  seat,
  members,
  tariffs,
  sheet,
  setSheet,
  onDone,
  call,
  onStartMove,
}: {
  seat: Seat;
  members: Member[];
  tariffs: Overview['tariffs'];
  sheet: SheetState;
  setSheet: (s: SheetState) => void;
  onDone: () => void;
  /** The player's calls of this PC. */
  call?: CallGroup;
  /** «Пересадить на другой ПК…»: the map picks the target. */
  onStartMove: () => void;
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

  // The player's call heads the card's body: amber, glowing while it rings, with «Иду» / «Закрыть вызов».
  const banner = call ? (
    <section
      aria-label={t('Вызов администратора')}
      className={clsx(
        'relative flex flex-col gap-2.5 overflow-hidden rounded-md border border-warning/40 bg-warning/[0.08] py-2.5 pl-4 pr-3',
        call.ringing && 'anim-warn-glow',
      )}
    >
      <span
        aria-hidden="true"
        className="absolute bottom-2 left-0 top-2 w-0.5 rounded-full bg-warning shadow-[0_0_10px_rgb(var(--c-warning)/0.8)]"
      />
      <p className="flex items-start gap-2 text-[13px] font-medium leading-5 text-text">
        <span className="mt-[3px] text-warning">
          <CallMark ringing={call.ringing} />
        </span>
        <span>{groupLine(call)}</span>
      </p>
      <CallGroupActions group={call} compact />
    </section>
  ) : null;

  return seat.session && seat.user ? (
    <BusySeat
      seat={seat}
      session={seat.session}
      user={seat.user}
      username={members.find((m) => m.id === seat.user?.id)?.username}
      tariffs={tariffs}
      banner={banner}
      note={note}
      busy={busy}
      run={run}
      sheet={sheet}
      setSheet={setSheet}
      version={version}
      onDone={done}
      setNote={setNote}
      onStartMove={onStartMove}
    />
  ) : (
    <FreeSeat
      seat={seat}
      tariffs={tariffs}
      banner={banner}
      note={note}
      busy={busy}
      run={run}
      onDone={done}
      setNote={setNote}
    />
  );
}

/**
 * The status pill of the seat card's band: «В игре» while a signed-in player plays, «Ждёт входа» while nobody has
 * signed in yet, else what the PC is.
 */
function StatusPill({ seat }: { seat: Seat }): JSX.Element | null {
  const status = seat.pc.status;
  if (status === 'locked') return <Badge tone="danger">{t(STATUS.locked.word)}</Badge>;
  if (status === 'offline') return <Badge tone="muted">{t(STATUS.offline.word)}</Badge>;
  if (status === 'maintenance') return <Badge tone="muted">{t(STATUS.maintenance.word)}</Badge>;
  if (seat.session)
    return seat.signedIn === false ? (
      <Badge tone="accent">{t('ждёт входа')}</Badge>
    ) : (
      <Badge tone="live">{t('В игре')}</Badge>
    );
  if (status === 'booked') return <Badge tone="accent">{t(STATUS.booked.word)}</Badge>;
  return <Badge tone="muted">{t(STATUS[status].word)}</Badge>;
}

interface PartProps {
  seat: Seat;
  tariffs: Overview['tariffs'];
  /** The player's call (the amber section) at the top of the card's body; null — none. */
  banner: ReactNode;
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
  banner,
  note,
  busy,
  run,
  sheet,
  setSheet,
  version,
  onDone,
  setNote,
  onStartMove,
}: PartProps & {
  session: Session;
  user: SeatUser;
  username: string | undefined;
  sheet: SheetState;
  setSheet: (s: SheetState) => void;
  version: number;
  onStartMove: () => void;
}): JSX.Element {
  const shift = useShift();
  const hit = useClientHit(user, username, version);
  const [techOpen, setTechOpen] = useState(false);
  const left = secondsLeft(session);
  const tariff = tariffs.find((x) => x.id === session.tariffId);
  // A walk-in guest's account is throwaway: no top-ups and no bonus money on it (D-36), only the exact price.
  const guest = user.role === 'guest';
  const game = seat.game ?? null;
  const art = game ? (game.heroUrl ?? game.coverUrl) : null;
  const ending = session.isPrepaid && left >= 0 && left <= ENDING_SEC;
  // «ПК 02 · STANDARD · С 12:27»: the zone, and the tariff when it is not named like the zone.
  const where = [
    pcLabel(seat.pc.name),
    seat.pc.zone,
    tariff && tariff.name.toLowerCase() !== seat.pc.zone.toLowerCase() ? tariff.name : null,
  ].filter(Boolean);
  const meta = [
    hit ? `@${hit.username}` : null,
    hit?.phoneTail ? `••${hit.phoneTail}` : null,
    guest && guestDisplayName(user.displayName) !== nameOf(user) ? guestDisplayName(user.displayName) : null,
  ].filter(Boolean);
  const payee: Payee = { id: user.id, displayName: nameOf(user), balance: user.balance, bonus: hit?.bonus ?? null };
  // The top-up sheet carries the seat (its hero is the game's art; without art, the plain header with this caption).
  const context: TopUpContext = {
    caption: [...where, game?.title ?? null].filter(Boolean).join(' · '),
    art,
    sub: meta.join(' · ') || undefined,
  };
  const topUp = (): void => {
    if (!guest) setSheet({ kind: 'topup', payee, context });
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

  // Over a picture the readouts sit at the band's bottom (spec §6.7); without one they follow the band.
  const readouts = (
    <dl className={clsx('grid grid-cols-2', art ? 'absolute inset-x-4 bottom-3 h-[50px]' : 'well px-3.5 py-3')}>
      <div className="flex min-w-0 flex-col justify-between gap-2 border-r border-accent/[0.14] pr-3.5">
        {session.isPrepaid ? (
          <>
            <dt className={clsx('label-sm', art && 'text-artlabel')}>{t('Осталось')}</dt>
            <dd
              className={clsx(
                'num-dot truncate text-[30px] leading-none tracking-[0.02em]',
                ending ? 'text-warning' : art ? 'text-white' : 'text-hi',
              )}
            >
              {left < 0 ? '∞' : clockLeft(left)}
            </dd>
          </>
        ) : (
          // Postpaid is charged in one go when the session ends: the balance stays untouched until then, so the
          // running bill is what the counter needs to see, with the time played.
          <>
            <dt className={clsx('label-sm flex items-center justify-between gap-2', art && 'text-artlabel')}>
              {t('Начислено')}
              <span className="tnum font-mono normal-case tracking-normal text-accent">
                ∞ {duration(session.secondsUsed)}
              </span>
            </dt>
            <dd className="flex min-w-0 items-baseline gap-1.5 whitespace-nowrap">
              <span className={clsx('num-dot truncate text-[26px] leading-none', art ? 'text-white' : 'text-hi')}>
                {moneyParts(session.cost.amount).num}
              </span>
              <span className={clsx('text-[11.5px] font-medium', art ? 'text-artlabel' : 'text-muted')}>
                {t('сум')}
              </span>
            </dd>
          </>
        )}
      </div>
      <div className="flex min-w-0 flex-col justify-between gap-2 pl-4">
        <dt className={clsx('label-sm flex items-baseline justify-between gap-2', art && 'text-artlabel')}>
          {t('Баланс')}
          {hit && hit.bonus.amount > 0 && (
            <span className="tnum truncate font-sans text-[11px] font-medium normal-case tracking-normal text-accent">
              {t('+ бонусы {sum}', { sum: money(hit.bonus) })}
            </span>
          )}
        </dt>
        <dd className="flex min-w-0 items-baseline gap-[5px] whitespace-nowrap">
          <span className={clsx('num-dot truncate text-[22px] leading-none', art ? 'text-white' : 'text-hi')}>
            {moneyParts(user.balance.amount).num}
          </span>
          <span className={clsx('text-[11.5px] font-medium', art ? 'text-artlabel' : 'text-muted')}>{t('сум')}</span>
        </dd>
      </div>
    </dl>
  );

  return (
    <div className="panel-solid flex flex-col overflow-hidden">
      <SeatStrip
        art={art}
        height={art ? 192 : 120}
        caption={[...where, t('с {time}', { time: clock(session.startedAt) })].join(' · ')}
        pill={<StatusPill seat={seat} />}
        title={nameOf(user)}
        meta={
          meta.length > 0 || game ? (
            <>
              {meta.join(' · ')}
              {meta.length > 0 && game ? ' · ' : ''}
              {game && <span className="font-medium text-white">{game.title}</span>}
            </>
          ) : undefined
        }
      >
        {art && readouts}
      </SeatStrip>
      {ending && (
        <span aria-hidden="true" className="h-0.5 shrink-0 bg-warning shadow-[0_0_8px_rgb(var(--c-warning)/0.9)]" />
      )}

      <div className="flex flex-col gap-3 px-4 pb-4 pt-3.5">
        {banner}
        {seat.signedIn === false && (
          <Note tone="warn" className="text-[12.5px]">
            {t('на ПК ещё никто не вошёл, а время уже идёт')}
          </Note>
        )}
        {!art && readouts}
        {!session.isPrepaid && (
          <p className="text-xs leading-5 text-muted">
            {guest
              ? t('Постоплата: гость платит на кассе, когда сеанс закончится.')
              : t('Постоплата: сумма спишется с баланса, когда сеанс закончится.')}
          </p>
        )}

        <SeatNote note={note} />

        <div className="flex flex-col gap-2">
          {actions > 0 && (
            <div className={clsx('grid gap-2', actions === 2 ? 'grid-cols-2' : 'grid-cols-1')}>
              {session.isPrepaid && (
                <Button
                  variant="primary"
                  size="lg"
                  disabled={busy !== null}
                  onClick={() => setSheet({ kind: 'extend' })}
                >
                  <TimerIcon size={17} strong />
                  {t('Продлить')}
                </Button>
              )}
              {!guest && (
                <Button variant="secondary" size="lg" disabled={busy !== null} onClick={topUp}>
                  {t('Пополнить')}
                  <Kbd>F2</Kbd>
                </Button>
              )}
            </div>
          )}

          {/* The move gets the room, «Бар» and «Ещё ⋯» are as wide as their words (12.5 px, so the Russian line fits the
              360 px column); «Boshqa kompyuterga koʻchirish…» wraps. */}
          <div className="flex gap-2">
            <Button
              variant="tertiary"
              className={clsx(WRAP, 'min-w-0 flex-1 !gap-1.5 !px-2 !text-[12.5px]')}
              disabled={busy !== null}
              onClick={onStartMove}
            >
              <SwapIcon size={15} />
              {t('Пересадить на другой ПК…')}
            </Button>
            <Button
              variant="tertiary"
              className="shrink-0 !px-2 !text-[12.5px]"
              disabled={busy !== null}
              onClick={() => {
                // The bar with this player and PC as the buyer.
                showBar({ pcId: seat.pc.id, userId: user.id });
                window.location.hash = '/bar';
              }}
            >
              {t('Бар')}
            </Button>
            <MoreButton compact open={techOpen} onToggle={() => setTechOpen((v) => !v)} />
          </div>
        </div>

        <TechActions seat={seat} busy={busy !== null} run={run} onStartMove={onStartMove} open={techOpen} />

        <div aria-hidden="true" className="h-px bg-accent/[0.08]" />
        <Button variant="danger" className="w-full" disabled={busy !== null} onClick={() => setSheet({ kind: 'end' })}>
          <StopSquareIcon size={14} strong />
          {t('Завершить сеанс')}
        </Button>
      </div>

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
    </div>
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
    <Sheet
      title={t('Продлить · {name}', { name: nameOf(user) })}
      caption={[pcLabel(seat.pc.name), current?.name].filter(Boolean).join(' · ')}
      onClose={onClose}
      footer={<PayFooter />}
    >
      {current?.isPackage && (
        <button
          type="button"
          className={clsx(
            'choice focus-ring flex min-h-14 items-center justify-between gap-3 rounded-md px-4 py-2 text-left',
            choice.pkg && 'choice-on',
          )}
          aria-pressed={choice.pkg}
          onClick={() => setChoice({ pkg: true })}
        >
          <span className="min-w-0 truncate text-sm font-semibold">
            {t('Ещё пакет · {name}', { name: current.name })}
          </span>
          <span className="tnum shrink-0 font-mono text-[12.5px] text-dim">
            {pkgQuote ? money(pkgQuote.total) : '…'}
          </span>
        </button>
      )}
      {hourly.length > 0 && (
        <div className="flex flex-col gap-2">
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
          <div className="grid grid-cols-4 gap-2">
            {MINUTE_PRESETS.map((m) => {
              const on = !choice.pkg && m === choice.minutes;
              return (
                <button
                  type="button"
                  key={m}
                  className={clsx(
                    'choice focus-ring tnum flex h-14 flex-col items-center justify-center gap-1.5 rounded-md px-1',
                    on && 'choice-on',
                  )}
                  aria-pressed={on}
                  onClick={() => setChoice({ pkg: false, tariffId: hourlyId, minutes: m })}
                >
                  <span className="text-[13.5px] font-semibold leading-none">+{minutesLabel(m)}</span>
                  <span className="font-mono text-[10.5px] leading-none text-muted">
                    {prices[m] === undefined ? '…' : money(prices[m].total)}
                  </span>
                </button>
              );
            })}
          </div>
        </div>
      )}
      {guest ? <ExactSummary price={price ?? 0} /> : <PaySummary price={price ?? 0} balance={user.balance.amount} />}
      {blocked && <p className="text-xs font-medium text-warning">{blocked}</p>}
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
            size="xl"
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
            <TimerIcon size={20} strong />
            <span className="mr-auto">{t('Продлить на {time}', { time: minutesLabel(bought) })}</span>
            <Kbd onPrimary className="h-6 px-2 text-[10.5px]">
              Enter
            </Kbd>
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
    <Sheet
      title={t('Завершить сеанс · {pc}', { pc: pcLabel(seat.pc.name) })}
      caption={seat.pc.zone}
      onClose={onClose}
      footer={
        <div className="ml-auto flex gap-2">
          <Button variant="ghost" autoFocus onClick={onClose}>
            {t('Отмена')}
          </Button>
          <Button variant="danger" disabled={busy} onClick={() => void end()}>
            {busy ? '…' : t('Завершить сеанс')}
          </Button>
        </div>
      }
    >
      <p className="flex items-baseline justify-between gap-3 text-sm">
        <span className="min-w-0 truncate font-medium text-hi">{nameOf(user)}</span>
        <span className="tnum shrink-0 font-mono text-[12px] text-dim">
          {session.isPrepaid
            ? t('осталось {time}', { time: duration(secondsLeft(session)) })
            : t('играет {time}', { time: duration(session.secondsUsed) })}
        </span>
      </p>
      <p className="well px-3.5 py-3 text-[13px] leading-5 text-text">{copy}</p>
      <Note note={error ? { text: error, tone: 'err' } : null} />
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
    <Sheet
      title={payout ? t('Выдать наличными · {name}', { name }) : t('Долг · {name}', { name })}
      caption={target.pc ? pcLabel(target.pc) : undefined}
      onClose={onClose}
      footer={<PayFooter />}
    >
      {done ? (
        <>
          <Note tone="ok" role="status">
            {done.text}
          </Note>
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
            <p className="text-xs leading-5 text-muted">
              {t('Остальное ({sum}) оплачено картой или онлайн — наличными не выдаётся', {
                sum: moneyExact(target.balance - payable),
              })}
            </p>
          )}
          <ShiftClosedNote />
          {/* The amount is exact and read-only: Enter on the focused button gives it out. */}
          <Button
            variant="primary"
            size="xl"
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
            <Note tone="err" role="alert">
              {error}
            </Note>
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

function FreeSeat({ seat, tariffs, banner, note, busy, run, onDone, setNote }: PartProps): JSX.Element {
  const [techOpen, setTechOpen] = useState(false);
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
      size="lg"
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
    <div className="panel-solid flex flex-col overflow-hidden">
      <SeatStrip
        art={null}
        height={96}
        caption={[pcLabel(seat.pc.name), seat.pc.zone].join(' · ')}
        pill={<StatusPill seat={seat} />}
        title={t('Посадить на {pc}', { pc: pcLabel(seat.pc.name) })}
      />

      <div className="flex flex-col gap-4 px-4 pb-4 pt-3.5">
        {banner}
        <SeatNote note={note} />

        {cashDesk2 && (
          <Segmented
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
          <div className="group/field flex flex-col gap-2">
            <label htmlFor={nameId} className="label-sm transition-colors group-focus-within/field:text-text">
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
            <span className="text-[11.5px] leading-4 text-muted">
              {t('Необязательно. Гость входит кнопкой «Гость» на этом ПК.')}
            </span>
          </div>
        ) : (
          <ClientPicker label={t('Кто')} value={who} onChange={setWho} />
        )}

        {cashDesk2 && (
          <div className="flex flex-col gap-1.5">
            <Segmented
              label={t('Оплата')}
              value={prepaid ? 'pre' : 'post'}
              options={[
                { id: 'pre', label: t('Предоплата') },
                { id: 'post', label: t('Постоплата'), disabled: postpaidOff },
              ]}
              onChange={(v) => setWantPrepaid(v === 'pre')}
            />
            {postpaidOff && (guest || who) && <p className="text-xs leading-5 text-muted">{postpaidOff}</p>}
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
          <div className="flex flex-col gap-2">
            <span className="label-sm">{t('Время')}</span>
            <div className="grid grid-cols-4 gap-2">
              {MINUTE_PRESETS.map((m) => {
                const on = !pkg && m === minutes;
                return (
                  <button
                    type="button"
                    key={m}
                    aria-pressed={on}
                    className={clsx(
                      'choice focus-ring tnum h-11 rounded-md px-1 text-[13.5px] font-semibold',
                      on && 'choice-on',
                    )}
                    onClick={() => {
                      setMinutes(m);
                      setPkgId(null);
                    }}
                  >
                    {minutesLabel(m)}
                  </button>
                );
              })}
            </div>
          </div>
        )}
        {prepaid && packages.length > 0 && (
          <div className="flex flex-col gap-2">
            <span className="label-sm">{t('Пакеты')}</span>
            <div className="grid grid-cols-2 gap-2">
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
                      'choice focus-ring flex min-h-[72px] flex-col items-start justify-between gap-1 rounded-md px-3 py-2.5 text-left disabled:cursor-not-allowed disabled:opacity-40',
                      on && 'choice-on',
                    )}
                  >
                    <span className="w-full truncate text-[13px] font-semibold leading-4">{p.name}</span>
                    <span className="tnum font-mono text-[10.5px] leading-4 text-muted">
                      {minutesLabel(p.packageMinutes ?? q?.minutes ?? 0)}
                      {win ? ` · ${win}` : ''}
                    </span>
                    <span className="tnum font-mono text-[12px] font-semibold leading-4 text-text">
                      {q ? (
                        <>
                          {q.base.amount !== q.total.amount && (
                            <s className="mr-1.5 font-medium text-muted">{money(q.base)}</s>
                          )}
                          {money(q.total)}
                        </>
                      ) : (
                        '…'
                      )}
                    </span>
                    {rule && <span className="text-[11px] leading-4 text-warning">{describeRule(rule)}</span>}
                  </button>
                );
              })}
            </div>
          </div>
        )}

        <div className="flex flex-col gap-1.5">
          {!prepaid ? (
            <p className="well px-3.5 py-3 text-[13px] leading-5 text-text">
              {hourlyQuote ? t('≈ {sum} / ч · оплата в конце', { sum: money(hourlyQuote.total) }) : t('Считаем цену…')}
            </p>
          ) : guest ? (
            <ExactSummary price={price} />
          ) : (
            <PaySummary price={price} balance={who?.balance.amount ?? 0} />
          )}
          {priceQuote && priceQuote.discountPct > 0 && (
            <span className="text-xs font-medium text-accent">
              −{priceQuote.discountPct}% · {priceQuote.discountReason}
            </span>
          )}
          {priceQuote && priceQuote.dayPct !== 100 && (
            <span className="text-xs text-muted">
              {t('Цена дня')}: {priceQuote.dayPct}%
            </span>
          )}
          {blockedRule && <span className="text-xs font-medium text-warning">{blockedRule}</span>}
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
          <div className="flex flex-col gap-2">
            {someone && <ShiftClosedNote />}
            {seatButton(prepaid ? t('Посадить') : t('Посадить · постоплата'))}
            {!someone && <p className="text-xs text-muted">{t('Выберите клиента')}</p>}
            {blocked && <p className="text-xs text-muted">{t('ПК на обслуживании')}</p>}
          </div>
        )}

        <div aria-hidden="true" className="h-px bg-accent/[0.08]" />
        <MoreButton open={techOpen} onToggle={() => setTechOpen((v) => !v)} />
        <TechActions seat={seat} busy={busy !== null} run={run} open={techOpen} />
      </div>
    </div>
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
    <section aria-labelledby={id} className="glass-side flex shrink-0 flex-col gap-2 px-5 pb-2 pt-3.5">
      <h2 id={id} className="label flex items-center gap-2 text-warning">
        <AlertTriangleIcon size={13} strong />
        {t('Расчёт с гостями и долги')}
      </h2>
      <ul className="thin-scrollbar flex max-h-56 flex-col divide-y divide-accent/[0.07] overflow-y-auto">
        {debts.map((d) => {
          const guest = d.role === undefined || d.role === 'guest';
          return (
            <li key={`debt-${d.userId}`} className="flex min-h-14 items-center justify-between gap-3 py-2">
              <span className="flex min-w-0 flex-col gap-1">
                <span className="flex min-w-0 items-center gap-2 text-[13px] font-medium text-text">
                  <span className="truncate">{guest ? guestDisplayName(d.displayName) : d.displayName}</span>
                  <Badge tone="muted">{guest ? t('гость') : t('клиент')}</Badge>
                </span>
                <span className="tnum font-mono text-[11px] uppercase tracking-[0.08em] text-muted">
                  {where(d.pc, d.endedAt)}
                </span>
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
          <li key={`refund-${r.userId}`} className="flex min-h-14 items-center justify-between gap-3 py-2">
            <span className="flex min-w-0 flex-col gap-1">
              <span className="flex min-w-0 items-center gap-2 text-[13px] font-medium text-text">
                <span className="truncate">{guestDisplayName(r.displayName)}</span>
                <Badge tone="muted">{t('гость')}</Badge>
              </span>
              <span className="tnum font-mono text-[11px] uppercase tracking-[0.08em] text-muted">
                {where(r.pc, r.endedAt)}
              </span>
            </span>
            <Button
              variant="secondary"
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
        <p className="pb-2 text-xs text-muted">
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
  /** Two or more PCs picked for one command (D-66); empty — the single `selected` seat. */
  const [multi, setMulti] = useState<Set<string>>(() => new Set());
  /** «Выбрать»: clicks toggle PCs in and out of the set (for touch, where there is no Ctrl). */
  const [selectMode, setSelectMode] = useState(false);
  /** A session waiting for its new PC: the map picks the target. */
  const [movePick, setMovePick] = useState<{ fromPcId: string } | null>(null);
  /** «Пересажен на ПК 07 · ждёт входа», shown over the target's panel after a move. */
  const [moved, setMoved] = useState<{ pcId: string; text: string } | null>(null);
  const [filter, setFilter] = useState<Filter | null>(null);
  const [sheet, setSheet] = useState<SheetState>(null);
  const [digits, setDigits] = useState('');
  const [tick, setTick] = useState(0);
  const [panel, setPanelState] = useState<'seat' | 'feed'>(readPanel);
  const anchor = useRef<string | null>(null);
  const wide = useMedia(WIDE_QUERY);
  const short = useMedia(SHORT_QUERY);
  const shift = useShift();
  const calls = useCalls();

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
      const o = await adminApi.overview();
      setData(o);
      // The map polls faster than the top bar: the calls ring and stop sooner here.
      setCalls(o.calls);
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

  // "Показать ПК" from the top-bar search and the calls.
  useEffect(
    () =>
      onShowPc((pcId) => {
        setSelected(pcId);
        setMulti(new Set());
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
  useEffect(() => {
    if (moved && moved.pcId !== selected) setMoved(null);
  }, [moved, selected]);
  // The session ended (or moved elsewhere) while its target was being picked: nothing left to move.
  useEffect(() => {
    if (movePick && data && !data.seats.find((s) => s.pc.id === movePick.fromPcId)?.session) setMovePick(null);
  }, [movePick, data]);

  const repairs = useMemo(
    () => new Map((data?.repairs ?? []).map((r) => [r.pcId, r.severity] as const)),
    [data?.repairs],
  );
  const callsByPc = useMemo(() => new Map(groupCalls(calls ?? []).map((g) => [g.pcId, g] as const)), [calls]);
  const zones = useMemo(() => {
    const out = new Map<string, Seat[]>();
    for (const s of seats) {
      out.set(s.pc.zone, [...(out.get(s.pc.zone) ?? []), s]);
    }
    return [...out.entries()];
  }, [seats]);
  const active = filter ? FILTERS.find((x) => x.id === filter) : undefined;
  void tick;
  const shownIds = zones.flatMap(([, list]) =>
    list.filter((x) => !active || active.test(x, repairs.has(x.pc.id))).map((x) => x.pc.id),
  );

  /** The set as it is: two or more go to the bulk panel, one is the selected seat, none clears both. */
  const applySelection = (next: Set<string>): void => {
    if (next.size >= 2) {
      setMulti(next);
      return;
    }
    setMulti(new Set());
    const [only] = [...next];
    pick(only ?? null);
  };
  const currentSet = (): Set<string> => {
    const out = new Set(multi);
    if (out.size === 0 && selected) out.add(selected);
    return out;
  };
  const toggle = (pcId: string): void => {
    const next = currentSet();
    if (next.has(pcId)) next.delete(pcId);
    else next.add(pcId);
    anchor.current = pcId;
    applySelection(next);
  };
  /** A target the move cannot take: busy, offline, in maintenance, or the PC it comes from. */
  const notTarget = (x: Seat): boolean =>
    movePick !== null &&
    (x.pc.id === movePick.fromPcId || x.session !== null || x.pc.status === 'offline' || x.pc.status === 'maintenance');
  const chooseTarget = (x: Seat): void => {
    if (!movePick || notTarget(x)) return;
    setSheet({ kind: 'move', fromPcId: movePick.fromPcId, toPcId: x.pc.id });
  };
  const onTile = (x: Seat, e: React.MouseEvent<HTMLButtonElement>): void => {
    if (movePick) {
      chooseTarget(x);
      return;
    }
    if (e.shiftKey && anchor.current) {
      const a = shownIds.indexOf(anchor.current);
      const b = shownIds.indexOf(x.pc.id);
      if (a >= 0 && b >= 0) {
        const next = currentSet();
        for (const id of shownIds.slice(Math.min(a, b), Math.max(a, b) + 1)) next.add(id);
        applySelection(next);
        return;
      }
    }
    if (selectMode || e.ctrlKey || e.metaKey) {
      toggle(x.pc.id);
      return;
    }
    // A plain click: that one PC, whatever was picked before.
    anchor.current = x.pc.id;
    setMulti(new Set());
    pick(x.pc.id);
  };
  const startMove = (from: Seat): void => {
    setMulti(new Set());
    setSelectMode(false);
    setSheet(null);
    pick(from.pc.id);
    setMovePick({ fromPcId: from.pc.id });
  };

  // Keys: digits then Enter pick a PC by number (in «Выбрать» mode they toggle it, while a move waits they pick its
  // target); Ctrl+A takes every PC shown; Esc closes the sheet, then the set, then the move, then the typed number,
  // then the panel.
  const keys = useRef({ seats, digits, sheet, selected, multi, selectMode, movePick, shownIds, toggle, chooseTarget });
  keys.current = { seats, digits, sheet, selected, multi, selectMode, movePick, shownIds, toggle, chooseTarget };
  useEffect(() => {
    let timer = 0;
    const on = (e: KeyboardEvent): void => {
      const k = keys.current;
      if (e.key === 'Escape') {
        if (k.sheet) setSheet(null);
        // In a field (the bulk message, the seat's message) the first Esc only leaves it; the next one clears the set
        // or closes the panel, so a typed message and a picked set are never lost to one key.
        else if (isTyping(e) && e.target instanceof HTMLElement) e.target.blur();
        else if (k.multi.size > 0 || k.selectMode) {
          setMulti(new Set());
          setSelectMode(false);
        } else if (k.movePick) setMovePick(null);
        else if (k.digits) setDigits('');
        else if (k.selected) setSelected(null);
        return;
      }
      if (isTyping(e) || sheetOpen()) return;
      if ((e.ctrlKey || e.metaKey) && !e.altKey && (e.code === 'KeyA' || e.key.toLowerCase() === 'a')) {
        if (k.movePick || k.shownIds.length === 0) return;
        e.preventDefault();
        const all = new Set(k.shownIds);
        if (all.size >= 2) setMulti(all);
        return;
      }
      if (e.altKey || e.ctrlKey || e.metaKey) return;
      if (/^\d$/.test(e.key)) {
        setDigits((d) => (d + e.key).slice(-3));
        window.clearTimeout(timer);
        timer = window.setTimeout(() => setDigits(''), DIGITS_MS);
      } else if (e.key === 'Enter' && k.digits) {
        e.preventDefault();
        const n = Number(k.digits);
        const hit = k.seats.find((s) => s.pc.number === n);
        if (hit) {
          if (k.movePick) k.chooseTarget(hit);
          else if (k.selectMode) k.toggle(hit.pc.id);
          else {
            setSelected(hit.pc.id);
            setMulti(new Set());
            setPanelState('seat');
          }
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

  // Recounted every second: "ending soon" moves with the clock.
  const matches = (f: Filter, s: Seat): boolean =>
    FILTERS.find((x) => x.id === f)?.test(s, repairs.has(s.pc.id)) ?? false;
  const occupied = seats.filter((s) => s.session !== null).length;
  const debts = data?.guestDebts ?? [];
  const allRefunds = data?.guestRefunds ?? [];
  const refunds = allRefunds.filter((r) => r.payable.amount > 0);
  const bulk = multi.size >= 2 ? seats.filter((s) => multi.has(s.pc.id)) : [];
  // Below 1800 px the right column stacks the seat over the feed; a tall form (seating, several PCs) folds the feed to a
  // bar unless the cashier unfolded it, and so does any seat card on a short screen.
  const tall = bulk.length > 0 || (seat !== null && (short || !(seat.session && seat.user)));
  const folded = !wide && tall && panel !== 'feed';
  const moveFrom = movePick ? seats.find((s) => s.pc.id === movePick.fromPcId) : undefined;
  const moveSheet =
    sheet?.kind === 'move'
      ? {
          from: seats.find((s) => s.pc.id === sheet.fromPcId),
          to: seats.find((s) => s.pc.id === sheet.toPcId),
        }
      : null;
  const tariffName = (id: string | undefined): string | undefined => data?.tariffs.find((x) => x.id === id)?.name;
  // «Занято 6 из 24» with the busy count lit, in any language's word order.
  const [busyBefore, busyAfter = ''] = t('Занято {n} из {total}', { n: '\u0000', total: seats.length }).split('\u0000');
  // PCs on an older agent: a quiet line in the hall's header (nothing the cashier acts on, so no amber block).
  const outdatedText =
    outdated.names.length > 0
      ? t('На старой версии ({newest} есть): {list}', {
          newest: outdated.newest ?? '',
          list: outdated.names.join(', '),
        })
      : null;

  const seatRegion =
    bulk.length > 0 ? (
      <BulkPanel
        seats={bulk}
        onDone={() => void load()}
        onClear={() => {
          setMulti(new Set());
          setSelectMode(false);
        }}
        onStartMove={startMove}
      />
    ) : seat && data ? (
      <>
        {moved && moved.pcId === seat.pc.id && (
          <Note tone="ok" role="status">
            {moved.text}
          </Note>
        )}
        <SeatPanel
          seat={seat}
          members={data.users}
          tariffs={data.tariffs}
          sheet={sheet}
          setSheet={setSheet}
          onDone={() => void load()}
          call={callsByPc.get(seat.pc.id)}
          onStartMove={() => startMove(seat)}
        />
      </>
    ) : (
      <div className="glass-side flex flex-col items-center justify-center px-6 py-9">
        <EmptyState
          icon={<MonitorIcon size={22} />}
          title={t('Выберите место')}
          text={t('Посадите клиента, продлите, пополните или завершите сеанс. Номер ПК и Enter — выбрать место.')}
          hint={
            <>
              {t('номер ПК')}
              <Kbd className="text-text">Enter</Kbd>
            </>
          }
        />
      </div>
    );

  return (
    <div className="flex h-full min-h-0 flex-col gap-3">
      {error && <Note tone="err">{error}</Note>}
      {movePick && (
        // The move's HUD bar: what the map waits for, and the way out.
        <div
          role="status"
          className="panel-solid edge-top flex min-h-12 shrink-0 items-center gap-3 py-1.5 pl-4 pr-2 text-[13px] font-medium text-text"
        >
          <SwapIcon size={16} className="text-accent" />
          <span className="min-w-0 flex-1">
            {t('Пересадка с {pc}: нажмите свободный ПК на карте (или номер и Enter)', {
              pc: moveFrom ? pcLabel(moveFrom.pc.name) : '—',
            })}
          </span>
          <Button size="sm" variant="ghost" onClick={() => setMovePick(null)}>
            {t('Отмена')}
            <Kbd>Esc</Kbd>
          </Button>
        </div>
      )}
      <div className="grid min-h-0 flex-1 grid-cols-1 gap-4 lg:grid-cols-[minmax(0,1fr)_360px] min-[1800px]:grid-cols-[minmax(0,1fr)_400px]">
        <div className="flex min-h-0 flex-col gap-4">
          <div className="glass-panel edge-top flex min-h-0 flex-1 flex-col px-5 pb-2 pt-[18px]">
            <header className="flex min-h-7 shrink-0 flex-wrap items-center justify-between gap-x-4 gap-y-1">
              <div className="flex min-w-0 items-baseline gap-4">
                <h1 className="whitespace-nowrap font-display text-xl font-medium leading-7 tracking-[-0.01em] text-hi">
                  {t('Карта зала')}
                </h1>
                <span className="label tnum whitespace-nowrap text-[10.5px] tracking-[0.14em]">
                  {busyBefore}
                  <span className="text-text">{occupied}</span>
                  {busyAfter}
                </span>
              </div>
              {outdatedText && (
                <span
                  title={outdatedText}
                  className="flex min-w-0 flex-1 items-center justify-center gap-1.5 text-xs text-muted"
                >
                  <AlertTriangleIcon size={12} className="shrink-0 text-warning" />
                  <span className="truncate">{outdatedText}</span>
                </span>
              )}
              <span className="label flex items-center gap-2 whitespace-nowrap" aria-live="polite">
                {multi.size >= 2 ? (
                  <span className="text-accent">{t('Выбрано {n}', { n: multi.size })}</span>
                ) : digits ? (
                  <>
                    <span className="text-accent">{t('ПК {n}', { n: digits })}</span>
                    <Kbd className="text-text">Enter</Kbd>
                  </>
                ) : (
                  <>
                    {t('номер ПК')}
                    <Kbd className="text-text">Enter</Kbd>
                  </>
                )}
              </span>
            </header>
            <div className="-ml-3 mt-3 flex shrink-0 flex-wrap items-center gap-1">
              <div role="group" aria-label={t('Фильтр')} className="flex flex-wrap items-center gap-1">
                {FILTERS.map((f) => {
                  const n = seats.filter((s) => matches(f.id, s)).length;
                  const on = filter === f.id;
                  return (
                    <Chip
                      key={f.id}
                      pressed={on}
                      tone={f.id === 'ending' && n > 0 ? 'attention' : 'default'}
                      count={n}
                      title={f.title ? t(f.title) : undefined}
                      onClick={() => setFilter(on ? null : f.id)}
                    >
                      {t(f.label)}
                    </Chip>
                  );
                })}
              </div>
              <span className="min-w-2 flex-1" />
              <Chip
                tone="outlined"
                pressed={selectMode}
                disabled={movePick !== null}
                title={t('Ctrl+клик, Shift+клик, Ctrl+A')}
                icon={<CheckSquareIcon size={15} strokeWidth={1.7} />}
                onClick={() => {
                  if (selectMode) setMulti(new Set());
                  setSelectMode((v) => !v);
                }}
              >
                {t('Выбрать')}
              </Chip>
            </div>
            {/*
              The tiles scroll inside the panel, with room around them for the selected tile's brackets and glow; the
              bottom edge fades (only the bottom: a top fade would dim the brackets of a selected first-row tile).
            */}
            <div className="thin-scrollbar -mx-3 flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto px-3 pb-[18px] pt-[18px] [mask-image:linear-gradient(180deg,#000_calc(100%-18px),transparent)]">
              {zones.map(([zone, list]) => {
                const numbers = list.map((x) => x.pc.number);
                const lo = Math.min(...numbers);
                const hi = Math.max(...numbers);
                return (
                  <section key={zone} className="flex flex-col gap-2.5">
                    <h2 className="flex h-4 items-center gap-3">
                      <span className="font-mono text-[10.5px] font-semibold uppercase leading-none tracking-[0.2em] text-text">
                        {zone}
                      </span>
                      <span className="tnum font-mono text-[10px] font-medium leading-none tracking-[0.1em] text-muted">
                        {lo === hi ? pad2(lo) : `${pad2(lo)}–${pad2(hi)}`}
                      </span>
                      <span
                        aria-hidden="true"
                        className="h-px min-w-4 flex-1 bg-[linear-gradient(90deg,rgb(var(--c-accent)/0.18),rgb(var(--c-accent)/0.04))]"
                      />
                      <span className="label tnum whitespace-nowrap tracking-[0.14em]">
                        {t('{n} свободно', { n: list.filter((x) => x.pc.status === 'free').length })}
                      </span>
                    </h2>
                    {/* Six to a row from 1366 to 1920 (F's rhythm): narrower tiles on the small screen, wider on the big. */}
                    <div className="grid grid-cols-[repeat(auto-fill,minmax(122px,1fr))] gap-2.5 min-[1400px]:grid-cols-[repeat(auto-fill,minmax(136px,1fr))] min-[1800px]:grid-cols-[repeat(auto-fill,minmax(196px,1fr))]">
                      {list.map((x) => (
                        <SeatTile
                          key={x.pc.id}
                          seat={x}
                          tariff={tariffName(x.session?.tariffId)}
                          repair={repairs.get(x.pc.id)}
                          call={callsByPc.get(x.pc.id)}
                          selected={multi.size === 0 && x.pc.id === selected}
                          checked={
                            selectMode || multi.size > 0
                              ? multi.has(x.pc.id) || (multi.size === 0 && x.pc.id === selected)
                              : null
                          }
                          dimmed={active !== undefined && !active.test(x, repairs.has(x.pc.id))}
                          blocked={notTarget(x)}
                          onSelect={(e) => onTile(x, e)}
                        />
                      ))}
                    </div>
                  </section>
                );
              })}
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
        </div>

        <aside className="flex min-h-0 flex-col gap-3">
          <div
            className={clsx(
              'thin-scrollbar flex min-h-0 flex-col gap-3 overflow-y-auto',
              folded ? 'flex-1' : 'flex-[0_1_auto]',
            )}
          >
            {seatRegion}
          </div>
          {wide ? (
            // 1800 px and wider: the feed under the seat card never folds (F's right column, spec §6.1).
            <div className="glass-side flex min-h-[280px] flex-1 flex-col px-4 pb-2 pt-3.5">
              <OperationsFeed placement="column" className="min-h-0 flex-1" />
            </div>
          ) : folded ? (
            <button
              type="button"
              aria-expanded={false}
              onClick={() => setPanel('feed')}
              className="glass-side focus-ring flex h-11 shrink-0 items-center justify-between gap-3 px-4 text-left hover:border-accent/[0.26]"
            >
              <span className="font-display text-[13px] font-medium text-hi">{t('Операции смены')}</span>
              <ChevronDownIcon size={16} className="rotate-180 text-muted" />
            </button>
          ) : (
            <div className="glass-side flex min-h-[180px] flex-1 flex-col px-4 pb-2 pt-3.5">
              {tall && (
                <button
                  type="button"
                  aria-expanded
                  aria-label={t('Операции смены')}
                  onClick={() => setPanel('seat')}
                  className="focus-ring-inset -mx-4 -mt-3.5 mb-1.5 flex h-6 shrink-0 items-center justify-center rounded-t-md text-muted hover:text-text"
                >
                  <ChevronDownIcon size={16} />
                </button>
              )}
              <OperationsFeed placement="panel" className="min-h-0 flex-1" />
            </div>
          )}
        </aside>
      </div>

      {sheet?.kind === 'topup' && (
        <TopUpSheet
          payee={sheet.payee}
          initial={sheet.initial}
          title={sheet.title}
          context={sheet.context}
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
      {moveSheet?.from && moveSheet.to && data && (
        <MoveSheet
          from={moveSheet.from}
          to={moveSheet.to}
          tariffs={data.tariffs}
          onClose={() => setSheet(null)}
          onMoved={(r) => {
            setSheet(null);
            setMovePick(null);
            setMulti(new Set());
            pick(r.to.pcId);
            setMoved({
              pcId: r.to.pcId,
              text: `${t('Пересажен на {pc}', { pc: pcLabel(r.to.name) })} · ${t('ждёт входа')}`,
            });
            void load();
            shift.refresh();
          }}
        />
      )}
    </div>
  );
}

export default MapPage;
