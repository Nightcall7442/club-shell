/**
 * Console building blocks in the Obsidian material, variant F «Командный центр»: buttons, fields, inputs, toggles,
 * panels, section headers, tables, notes, sheets, chips, segmented choices, badges, KPI cards, wells, ticks and empty
 * states. Every page is made of these, so a change here changes the whole console. Names, roles and props that pages
 * and the E2E tests rely on stay as they were; F only adds.
 */
import {
  forwardRef,
  useEffect,
  useRef,
  useState,
  type ButtonHTMLAttributes,
  type HTMLAttributes,
  type InputHTMLAttributes,
  type ReactNode,
} from 'react';
import { createPortal } from 'react-dom';
import clsx from 'clsx';
import { exactDigits, moneyParts } from '@/format';
import { t } from '@/i18n';
import { AlertTriangleIcon, CloseIcon } from '@/icons';

// ---------------------------------------------------------------------------------------------------------------------
// Buttons
// ---------------------------------------------------------------------------------------------------------------------

export type ButtonVariant = 'primary' | 'secondary' | 'tertiary' | 'utility' | 'ghost' | 'danger' | 'warn';
export type ButtonSize = 'xs' | 'sm' | 'md' | 'lg' | 'xl';

const BUTTON_SIZE: Record<ButtonSize, string> = {
  xs: 'h-7 px-2.5 text-xs',
  sm: 'h-9 px-3 text-[13px]',
  md: 'h-11 px-4 text-sm',
  lg: 'h-[46px] px-4 text-sm',
  xl: 'h-14 pl-[18px] pr-3.5 text-[15px]',
};

const BUTTON_VARIANT: Record<ButtonVariant, string> = {
  primary: 'cut-corners rounded-none font-semibold text-on-accent',
  secondary: 'btn-secondary font-semibold',
  tertiary: 'btn-tertiary font-medium',
  utility: 'btn-utility font-medium',
  ghost: 'btn-ghost font-medium',
  danger: 'btn-danger focus-ring-danger font-semibold',
  warn: 'btn-warn font-semibold',
};

/**
 * Variants: `primary` (the one main action: accent fill, cut corners, glow), `secondary` (accent hairline and tint),
 * `tertiary` (quiet), `utility` (header and KPI tools), `ghost`, `danger` (ending a session), `warn` (a call).
 * Sizes: `xs` 28 (row actions, never money), `sm` 36, `md` 44 (money and seat actions), `lg` 46 (the seat card), `xl`
 * 56 (cash). Always `type="button"` unless given.
 */
export const Button = forwardRef<
  HTMLButtonElement,
  ButtonHTMLAttributes<HTMLButtonElement> & {
    variant?: ButtonVariant;
    size?: ButtonSize;
  }
>(function Button({ children, variant = 'secondary', size = 'md', className, ...rest }, ref) {
  return (
    <button
      ref={ref}
      type="button"
      {...rest}
      className={clsx(
        // `font-sans`: a button in a `num` table cell does not take the cell's mono.
        'focus-ring inline-flex select-none items-center justify-center gap-2 whitespace-nowrap rounded-md font-sans disabled:cursor-not-allowed',
        // A primary not ready yet keeps its cut-corner shape on a neutral fill (no washed-out accent slab); the rest fade.
        variant === 'primary' ? 'disabled:text-muted disabled:[--fill:rgb(var(--c-text)/0.06)]' : 'disabled:opacity-40',
        BUTTON_SIZE[size],
        BUTTON_VARIANT[variant],
        variant === 'primary' && size === 'xl' && '[--cut:12px]',
        (variant === 'tertiary' || variant === 'utility' || variant === 'ghost') && size === 'md' && 'text-[13px]',
        className,
      )}
    >
      {children}
    </button>
  );
});

// ---------------------------------------------------------------------------------------------------------------------
// Fields
// ---------------------------------------------------------------------------------------------------------------------

/** A text input, select or textarea: 44 high, a dark well, accent hairline, the focus glow. */
export const inputCls =
  'focus-ring h-11 w-full rounded-md border border-accent/[0.16] bg-bg/50 px-3.5 text-sm text-text [color-scheme:dark] placeholder:text-muted hover:border-accent/[0.26] focus-visible:border-transparent read-only:bg-text/[0.03] disabled:opacity-50';

export function Input(props: InputHTMLAttributes<HTMLInputElement>): JSX.Element {
  return <input {...props} className={clsx(inputCls, props.className)} />;
}

/**
 * A labelled field: the `<label>` wraps the control (so `getByLabel` finds it), a mono caption that turns `text` while
 * the field has focus, an optional mono hint on the right (`aside`) and a hint line below.
 */
export function Field({
  label,
  hint,
  aside,
  children,
  className,
}: {
  label: string;
  hint?: string;
  /** A mono note on the label row, right-aligned («ввод с клавиатуры»); part of the label's text. */
  aside?: ReactNode;
  children: ReactNode;
  className?: string;
}): JSX.Element {
  return (
    <label className={clsx('group/field flex flex-col gap-2', className)}>
      <span className="flex items-baseline justify-between gap-3">
        <span className="label-sm transition-colors group-focus-within/field:text-text">{label}</span>
        {aside && <span className="label-sm">{aside}</span>}
      </span>
      {children}
      {hint && <span className="text-[11.5px] leading-4 text-muted">{hint}</span>}
    </label>
  );
}

/**
 * Whole-sum input for money shown in сум and stored in minor units (×100): 52 high, Unbounded digits, «сум» on the
 * right. The value is the bare digits as typed (`77000`), never grouped. `compact` is the 44 px settings variant.
 */
export function MoneyInput({
  value,
  onChange,
  disabled,
  autoFocus,
  compact,
}: {
  value: number;
  onChange: (minor: number) => void;
  disabled?: boolean;
  autoFocus?: boolean;
  compact?: boolean;
}): JSX.Element {
  const [text, setText] = useState(String(Math.round(value / 100)));
  useEffect(() => setText(String(Math.round(value / 100))), [value]);
  return (
    <div className="relative">
      <input
        inputMode="numeric"
        autoFocus={autoFocus}
        disabled={disabled}
        className={clsx(
          inputCls,
          'tnum caret-accent',
          compact ? 'pr-12' : 'h-[52px] pl-4 pr-14 font-display text-xl font-medium tracking-[-0.01em]',
        )}
        value={text}
        onChange={(e) => {
          const digits = e.target.value.replace(/\D/g, '');
          setText(digits);
          onChange(Number(digits || '0') * 100);
        }}
      />
      <span
        className={clsx(
          'pointer-events-none absolute top-1/2 -translate-y-1/2 font-medium text-muted',
          compact ? 'right-3.5 text-xs' : 'right-4 text-[13px]',
        )}
      >
        {t('сум')}
      </span>
    </div>
  );
}

export function NumberInput({
  value,
  onChange,
  min,
  max,
  suffix,
  disabled,
}: {
  value: number;
  onChange: (n: number) => void;
  min?: number;
  max?: number;
  suffix?: string;
  disabled?: boolean;
}): JSX.Element {
  return (
    <div className="relative">
      <input
        type="number"
        min={min}
        max={max}
        disabled={disabled}
        className={clsx(inputCls, 'tnum', suffix && 'pr-10')}
        value={Number.isFinite(value) ? value : 0}
        onChange={(e) => onChange(Number(e.target.value))}
      />
      {suffix && (
        <span className="pointer-events-none absolute right-3.5 top-1/2 -translate-y-1/2 text-xs text-muted">
          {suffix}
        </span>
      )}
    </div>
  );
}

export function Toggle({
  checked,
  onChange,
  label,
  disabled,
}: {
  checked: boolean;
  onChange: (v: boolean) => void;
  label?: string;
  disabled?: boolean;
}): JSX.Element {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      disabled={disabled}
      onClick={() => onChange(!checked)}
      className="focus-ring group inline-flex items-center gap-3 rounded-md text-left text-sm disabled:opacity-40"
    >
      <span
        aria-hidden="true"
        className={clsx(
          'relative h-6 w-11 shrink-0 rounded-full border transition-colors duration-200',
          checked ? 'border-accent/70 bg-accent/25' : 'border-text/15 bg-text/[0.06]',
        )}
      >
        <span
          className={clsx(
            'absolute top-[2px] h-[18px] w-[18px] rounded-full transition-transform duration-200 ease-out',
            checked
              ? 'translate-x-[1.3rem] bg-accent shadow-[0_0_8px_rgb(var(--c-accent)/0.6)]'
              : 'translate-x-[2px] bg-text/70',
          )}
        />
      </span>
      {label && <span>{label}</span>}
    </button>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Layout: page header, panels, sections, tables
// ---------------------------------------------------------------------------------------------------------------------

/** Page title row: Unbounded title, an optional mono caption on its baseline, actions on the right. */
export function PageHeader({
  title,
  actions,
  caption,
}: {
  title: string;
  actions?: ReactNode;
  caption?: ReactNode;
}): JSX.Element {
  return (
    <header className="flex min-h-11 flex-wrap items-center justify-between gap-4">
      <div className="flex min-w-0 flex-wrap items-baseline gap-x-4 gap-y-1">
        <h1 className="font-display text-2xl font-medium leading-8 tracking-[-0.01em] text-hi">{title}</h1>
        {caption && <span className="label text-[10.5px] tracking-[0.14em]">{caption}</span>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </header>
  );
}

export type PanelVariant = 'glass' | 'side' | 'solid' | 'well';

const PANEL_CLASS: Record<PanelVariant, string> = {
  glass: 'glass-panel',
  side: 'glass-side',
  solid: 'panel-solid',
  well: 'well',
};

/**
 * A surface: `glass` (the page's main panel), `side` (lists and feeds), `solid` (the focused object: seat card, cart,
 * client card), `well` (an inset box). `edge` draws the accent hairline on top (one primary surface per screen).
 */
export function Panel({
  variant = 'glass',
  edge,
  className,
  children,
  ...rest
}: HTMLAttributes<HTMLDivElement> & { variant?: PanelVariant; edge?: boolean }): JSX.Element {
  return (
    <div {...rest} className={clsx(PANEL_CLASS[variant], edge && 'edge-top', className)}>
      {children}
    </div>
  );
}

/**
 * A panel's title row: `page` — Unbounded 20/28 with a mono caption on its baseline (the hall's «Карта зала»); `side` —
 * Unbounded 13/18 (a feed, a cart). `aside` sits on the right (a link, a hint, a tool).
 */
export function PanelHeader({
  title,
  caption,
  aside,
  size = 'page',
  level = 2,
  className,
}: {
  title: ReactNode;
  caption?: ReactNode;
  aside?: ReactNode;
  size?: 'page' | 'side';
  level?: 1 | 2 | 3;
  className?: string;
}): JSX.Element {
  const H = `h${level}` as 'h1' | 'h2' | 'h3';
  return (
    <div
      className={clsx(
        'flex items-center justify-between gap-3',
        size === 'page' ? 'min-h-7' : 'min-h-[18px]',
        className,
      )}
    >
      <div className="flex min-w-0 items-baseline gap-4">
        <H
          className={clsx(
            'truncate font-display font-medium text-hi',
            size === 'page' ? 'text-xl leading-7 tracking-[-0.01em]' : 'text-[13px] leading-[18px]',
          )}
        >
          {title}
        </H>
        {caption && <span className="label shrink-0 text-[10.5px] tracking-[0.14em]">{caption}</span>}
      </div>
      {aside && <div className="flex shrink-0 items-center gap-2">{aside}</div>}
    </div>
  );
}

/**
 * A panel with an optional mono header row and actions: still a `<section>` with an `<h2>` (pages and tests find
 * panels by section and heading). `variant` picks the surface (glass by default). The body's padding is a component
 * class, so `bodyClassName="p-2"` (tables) wins over it.
 */
export function Section({
  title,
  actions,
  children,
  className,
  bodyClassName,
  variant = 'glass',
}: {
  title?: string;
  actions?: ReactNode;
  children: ReactNode;
  className?: string;
  bodyClassName?: string;
  variant?: 'glass' | 'side' | 'solid';
}): JSX.Element {
  const head = Boolean(title || actions);
  return (
    <section className={clsx(PANEL_CLASS[variant], 'flex min-w-0 flex-col', className)}>
      {head && (
        <header className="flex min-h-11 items-center justify-between gap-3 px-5 pt-3">
          {title && <h2 className="label text-text">{title}</h2>}
          {actions && <div className="flex items-center gap-2">{actions}</div>}
        </header>
      )}
      <div className={clsx('section-body', head && 'section-body-headed', bodyClassName)}>{children}</div>
    </section>
  );
}

/**
 * Money in a table cell or a list (spec §8.8): the digits in the cell's face (mono in a `num` column), «сум» in Inter,
 * muted. `exact` keeps the tiyin (`45 000,50`); null or undefined reads «—».
 */
export function Sum({
  minor,
  exact,
  className,
}: {
  minor: number | null | undefined;
  exact?: boolean;
  className?: string;
}): JSX.Element {
  if (minor === null || minor === undefined) return <span className={className}>—</span>;
  return (
    <span className={clsx('whitespace-nowrap', className)}>
      {exact ? exactDigits(minor) : moneyParts(minor).num}{' '}
      <span className="font-sans font-medium text-muted">{t('сум')}</span>
    </span>
  );
}

/** Strict data table: mono header row, hairline rows, right-aligned numeric columns via `num`. */
export function Table<T>({
  rows,
  columns,
  rowKey,
  onRowClick,
  selectedKey,
  empty,
}: {
  rows: T[];
  columns: { key: string; title: string; num?: boolean; width?: string; render: (row: T) => ReactNode }[];
  rowKey: (row: T) => string;
  onRowClick?: (row: T) => void;
  selectedKey?: string | null;
  empty?: string;
}): JSX.Element {
  return (
    <div className="thin-scrollbar overflow-x-auto">
      {rows.length === 0 ? (
        // Nothing to list: the F empty state (mono caption), not a lone header row over a grey line.
        <EmptyState compact title={empty ?? '—'} />
      ) : (
        <table className="w-full border-collapse text-[13px]">
          <thead>
            <tr className="h-9 border-b border-line">
              {columns.map((c) => (
                <th
                  key={c.key}
                  style={{ width: c.width }}
                  className={clsx('label-sm px-3 py-2 font-medium', c.num ? 'text-right' : 'text-left')}
                >
                  {c.title}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => {
              const k = rowKey(r);
              const selected = selectedKey === k;
              return (
                <tr
                  key={k}
                  onClick={onRowClick ? () => onRowClick(r) : undefined}
                  className={clsx(
                    'h-11 border-t border-line/70 first:border-t-0',
                    onRowClick && 'cursor-pointer hover:bg-text/[0.03]',
                    selected && 'bg-accent/[0.07] shadow-[inset_2px_0_0_rgb(var(--c-accent))]',
                  )}
                >
                  {columns.map((c) => (
                    <td
                      key={c.key}
                      className={clsx('px-3 py-2.5 align-middle', c.num && 'tnum text-right font-mono text-[12.5px]')}
                    >
                      {c.render(r)}
                    </td>
                  ))}
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Notes, banner, toast
// ---------------------------------------------------------------------------------------------------------------------

export type Tone = 'ok' | 'warn' | 'err';

const TONE_BOX: Record<Tone, string> = {
  ok: 'border-accent/30 bg-accent/[0.08]',
  warn: 'border-warning/30 bg-warning/[0.08]',
  err: 'border-danger/30 bg-danger/[0.08]',
};
const TONE_BAR: Record<Tone, string> = {
  ok: 'bg-accent shadow-[0_0_10px_rgb(var(--c-accent)/0.8)]',
  warn: 'bg-warning shadow-[0_0_10px_rgb(var(--c-warning)/0.8)]',
  err: 'bg-danger shadow-[0_0_10px_rgb(var(--c-danger)/0.8)]',
};

/**
 * Inline result line after an action: a tinted box with a glowing tone bar on the left — ok in the accent, warn amber,
 * err red. Either `note` (the old shape; null renders nothing) or `tone` + `children`. No role unless given (a sheet
 * keeps exactly one `status`).
 */
export function Note({
  note,
  tone,
  children,
  role,
  className,
}: {
  note?: { text: string; tone: Tone } | null;
  tone?: Tone;
  children?: ReactNode;
  role?: 'status' | 'alert';
  className?: string;
}): JSX.Element | null {
  const kind = note?.tone ?? tone ?? 'ok';
  const body = note ? note.text : children;
  if (note === null || (note === undefined && (children === undefined || children === null || children === false)))
    return null;
  return (
    <p
      role={role}
      className={clsx(
        'relative overflow-hidden rounded-md border py-2.5 pl-4 pr-3.5 text-[13px] font-medium leading-5 text-text',
        TONE_BOX[kind],
        className,
      )}
    >
      <span aria-hidden="true" className={clsx('absolute bottom-2 left-0 top-2 w-0.5 rounded-full', TONE_BAR[kind])} />
      {body}
    </p>
  );
}

/** A system notice across the top of a page (an outdated agent): 36 high, amber, a mono label and the text. */
export function Banner({
  label,
  children,
  className,
}: {
  label?: string;
  children: ReactNode;
  className?: string;
}): JSX.Element {
  return (
    <div
      className={clsx(
        'flex min-h-9 items-center gap-3 rounded-md border border-warning/30 bg-warning/[0.08] px-3.5 py-1.5 text-[13px] text-text',
        className,
      )}
    >
      <AlertTriangleIcon size={14} className="text-warning" />
      {label && <span className="label-sm text-warning">{label}</span>}
      <span className="min-w-0">{children}</span>
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Sheet
// ---------------------------------------------------------------------------------------------------------------------

/** Sheets open now: the page behind them (`#root`) is inert until the last one closes. */
let openSheets = 0;

/** The sheet's × (named «Закрыть»; Esc does the same). */
export function SheetClose({ onClose, className }: { onClose: () => void; className?: string }): JSX.Element {
  return (
    <button
      type="button"
      aria-label={t('Закрыть')}
      title="Esc"
      onClick={onClose}
      className={clsx(
        'focus-ring inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-md border border-text/[0.14] bg-bg/50 text-soft hover:border-text/25 hover:text-text',
        className,
      )}
    >
      <CloseIcon size={15} />
    </button>
  );
}

/**
 * A modal sheet over the console (a money action, a confirmation, the shift gate): the console blurred behind a scrim,
 * one glass panel centred (a tall one scrolls from its top), the title as the dialog's name. Esc and × close it when
 * `onClose` is given; the backdrop does not, so a stray click never drops a typed amount. Focus goes to the first
 * `autoFocus` field inside, else to the panel, so Esc works at once.
 *
 * `size`: `md` 496 (default) or `wide` 640 (`wide` is the old boolean for the same). `caption` is a mono line over the
 * title; `hero` replaces the whole header (art, eyebrow, title — the × stays on top of it); `footer` is a row under a
 * hairline (shift status, «Esc отмена»).
 */
export function Sheet({
  title,
  onClose,
  children,
  wide,
  size,
  caption,
  hero,
  footer,
}: {
  title: string;
  onClose?: () => void;
  children: ReactNode;
  wide?: boolean;
  size?: 'md' | 'wide';
  caption?: ReactNode;
  hero?: ReactNode;
  footer?: ReactNode;
}): JSX.Element {
  const panel = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (panel.current && !panel.current.contains(document.activeElement)) panel.current.focus();
  }, []);
  // Modal for real: the page behind it takes neither Tab nor clicks while a sheet is open.
  useEffect(() => {
    const root = document.getElementById('root');
    if (!root) return undefined;
    openSheets += 1;
    root.setAttribute('inert', '');
    return () => {
      openSheets -= 1;
      if (openSheets === 0) root.removeAttribute('inert');
    };
  }, []);
  const isWide = size ? size === 'wide' : Boolean(wide);
  return createPortal(
    <div className="thin-scrollbar fixed inset-0 z-50 overflow-y-auto">
      <div aria-hidden="true" className="scrim anim-fade" />
      {/* A short counter screen (1366×768): less margin and air, so a pay sheet fits without scrolling. */}
      <div className="relative flex min-h-full items-center justify-center p-6 [@media(max-height:840px)]:p-3">
        <div
          ref={panel}
          role="dialog"
          aria-modal="true"
          aria-label={title}
          tabIndex={-1}
          onKeyDown={(e) => {
            if (e.key === 'Escape' && onClose) {
              // Handled here: the page's own Esc (close the seat panel) must not fire too.
              e.stopPropagation();
              onClose();
            }
          }}
          className={clsx(
            'sheet anim-rise relative flex w-full flex-col overflow-hidden outline-none',
            isWide ? 'max-w-[640px]' : 'max-w-[496px]',
          )}
        >
          {hero ? (
            <>
              {hero}
              {onClose && <SheetClose onClose={onClose} className="absolute right-3.5 top-3.5 z-10" />}
            </>
          ) : (
            <header className="flex items-start justify-between gap-3 px-6 pt-5">
              <div className="flex min-w-0 flex-col gap-2">
                {caption && <p className="label-sm">{caption}</p>}
                <h2 className="font-display text-[22px] font-medium leading-7 tracking-[-0.01em] text-hi">{title}</h2>
              </div>
              {onClose && <SheetClose onClose={onClose} className="-mr-1" />}
            </header>
          )}
          <div className="flex flex-col gap-4 px-6 pb-[22px] pt-[18px] [@media(max-height:840px)]:gap-3 [@media(max-height:840px)]:pb-4 [@media(max-height:840px)]:pt-3.5">
            {children}
          </div>
          {footer && (
            <footer className="mx-6 flex min-h-14 items-center justify-between gap-3 border-t border-accent/[0.08] py-4 text-[12.5px] text-dim [@media(max-height:840px)]:min-h-11 [@media(max-height:840px)]:py-2.5">
              {footer}
            </footer>
          )}
        </div>
      </div>
    </div>,
    document.body,
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Small marks: Kbd, badges, dots
// ---------------------------------------------------------------------------------------------------------------------

/** A key hint on a button: `Enter`, `F2`, `Alt+2`. `onPrimary` sits on the accent fill. */
export function Kbd({
  children,
  className,
  onPrimary,
}: {
  children: ReactNode;
  className?: string;
  onPrimary?: boolean;
}): JSX.Element {
  return (
    <kbd
      className={clsx(
        'inline-flex h-5 shrink-0 items-center rounded-sm border px-1.5 font-mono text-[10px] leading-none tracking-[0.04em]',
        onPrimary
          ? 'border-on-accent/[0.28] bg-on-accent/10 font-semibold text-on-accent'
          : 'border-accent/[0.18] font-medium text-dim',
        className,
      )}
    >
      {children}
    </kbd>
  );
}

export type BadgeTone = 'neutral' | 'live' | 'accent' | 'warn' | 'danger' | 'muted';

const BADGE_TONE: Record<BadgeTone, string> = {
  neutral: 'border-accent/[0.24] bg-bg/60 text-text',
  live: 'border-accent/[0.24] bg-bg/60 text-text',
  accent: 'border-accent/40 text-accent',
  warn: 'border-warning/50 bg-warning/[0.08] text-warning',
  danger: 'border-danger/40 bg-danger/[0.06] text-danger-ink',
  muted: 'border-text/[0.12] text-muted',
};

/** A status pill: mono caps (CSS; the DOM text stays as written), never a solid fill. `live` leads with the live dot. */
export function Badge({
  tone = 'neutral',
  children,
  className,
  title,
}: {
  tone?: BadgeTone;
  children: ReactNode;
  className?: string;
  title?: string;
}): JSX.Element {
  return (
    <span
      title={title}
      className={clsx(
        'inline-flex h-5 shrink-0 items-center gap-1.5 whitespace-nowrap rounded-sm border px-2 font-mono text-[9px] font-semibold uppercase leading-none tracking-[0.14em]',
        BADGE_TONE[tone],
        className,
      )}
    >
      {tone === 'live' && <LiveDot />}
      {children}
    </span>
  );
}

/** 6 px accent dot with a glow: something is running now. */
export function LiveDot({ className }: { className?: string }): JSX.Element {
  return (
    <span
      aria-hidden="true"
      className={clsx(
        'inline-block h-1.5 w-1.5 shrink-0 rounded-full bg-accent shadow-[0_0_8px_rgb(var(--c-accent)/0.9)]',
        className,
      )}
    />
  );
}

/** 7 px status dot: `ok` green with a ring (open, online), `warn` amber, `danger` red, `muted`, `accent`. */
export function StatusDot({
  tone,
  className,
}: {
  tone: 'ok' | 'warn' | 'danger' | 'muted' | 'accent';
  className?: string;
}): JSX.Element {
  return (
    <span
      aria-hidden="true"
      className={clsx(
        'inline-block h-[7px] w-[7px] shrink-0 rounded-full',
        tone === 'ok' && 'bg-success shadow-[0_0_0_3px_rgb(var(--c-success)/0.14),0_0_10px_rgb(var(--c-success)/0.55)]',
        tone === 'warn' &&
          'bg-warning shadow-[0_0_0_3px_rgb(var(--c-warning)/0.14),0_0_10px_rgb(var(--c-warning)/0.5)]',
        tone === 'danger' && 'bg-danger',
        tone === 'muted' && 'bg-muted',
        tone === 'accent' && 'bg-accent shadow-[0_0_8px_rgb(var(--c-accent)/0.8)]',
        className,
      )}
    />
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Chips, segmented choice, tabs
// ---------------------------------------------------------------------------------------------------------------------

/**
 * A filter or category chip: `aria-pressed` when `pressed` is given. `attention` is amber (action needed: «Заканчиваются»
 * with a count), `outlined` a tool chip («Выбрать»). The count is mono after the label.
 */
export const Chip = forwardRef<
  HTMLButtonElement,
  ButtonHTMLAttributes<HTMLButtonElement> & {
    pressed?: boolean;
    tone?: 'default' | 'attention' | 'outlined';
    count?: ReactNode;
    icon?: ReactNode;
  }
>(function Chip({ pressed, tone = 'default', count, icon, children, className, ...rest }, ref) {
  const on = pressed === true;
  return (
    <button
      ref={ref}
      type="button"
      aria-pressed={pressed}
      {...rest}
      className={clsx(
        'focus-ring inline-flex h-9 shrink-0 select-none items-center gap-2 whitespace-nowrap rounded-chip px-3 text-[13px] font-medium disabled:cursor-not-allowed disabled:opacity-40',
        tone === 'default' &&
          (on
            ? 'bg-accent/10 text-hi shadow-[inset_0_0_0_1px_rgb(var(--c-accent)/0.5)]'
            : 'text-soft hover:bg-text/[0.04] hover:text-text'),
        tone === 'attention' &&
          (on
            ? 'bg-warning/[0.14] text-hi shadow-[inset_0_0_0_1px_rgb(var(--c-warning)/0.7)]'
            : 'bg-warning/[0.08] text-hi shadow-[inset_0_0_0_1px_rgb(var(--c-warning)/0.34)] hover:bg-warning/[0.12]'),
        tone === 'outlined' &&
          (on
            ? 'border border-accent/70 bg-accent/10 pl-3 pr-3.5 text-hi'
            : 'border border-accent/[0.14] pl-3 pr-3.5 text-text hover:border-accent/[0.26]'),
        className,
      )}
    >
      {icon ?? (tone === 'attention' ? <AlertTriangleIcon size={14} strong className="text-warning" /> : null)}
      {children}
      {count !== undefined && count !== null && (
        <span
          className={clsx(
            'tnum font-mono text-[11px]',
            tone === 'attention'
              ? 'font-semibold text-warning'
              : on
                ? 'font-medium text-accent'
                : 'font-medium text-muted',
          )}
        >
          {count}
        </span>
      )}
    </button>
  );
});

/**
 * One of two or more, as a tray of segments: a `role="group"` named `label` of `aria-pressed` buttons (the seat's «Кто
 * садится» / «Оплата», the bar's «Покупатель»). `full` stretches the segments over the width; `size` `md` is 40 high
 * (header, filters), `lg` 44 (forms). A disabled option says why in its tooltip.
 */
export function Segmented<T extends string>({
  label,
  value,
  options,
  onChange,
  size = 'lg',
  full = true,
  className,
}: {
  label?: string;
  value: T | null;
  options: { id: T; label: ReactNode; disabled?: string | null; title?: string }[];
  onChange: (v: T) => void;
  size?: 'md' | 'lg';
  full?: boolean;
  className?: string;
}): JSX.Element {
  return (
    <div
      role="group"
      aria-label={label}
      className={clsx(
        'gap-0.5 rounded-md border border-line bg-surface/50 p-1',
        full ? 'grid' : 'inline-flex items-center',
        size === 'lg' ? 'min-h-11' : 'min-h-10',
        className,
      )}
      style={full ? { gridTemplateColumns: `repeat(${options.length}, minmax(0, 1fr))` } : undefined}
    >
      {options.map((o) => {
        const on = value === o.id;
        return (
          <button
            key={o.id}
            type="button"
            aria-pressed={on}
            disabled={Boolean(o.disabled)}
            title={o.disabled ?? o.title}
            onClick={() => onChange(o.id)}
            className={clsx(
              'focus-ring inline-flex items-center justify-center gap-2 whitespace-nowrap rounded-seg px-3 text-[13px] disabled:cursor-not-allowed disabled:opacity-40',
              size === 'lg' ? 'h-9' : 'h-[30px]',
              on ? 'bg-accent/[0.12] font-semibold text-accent' : 'font-medium text-dim hover:text-text',
            )}
          >
            {o.label}
          </button>
        );
      })}
    </div>
  );
}

/** Page tabs: a tablist row, the active tab marked like the rail (an accent underline with a glow). */
export function Tabs<T extends string>({
  label,
  tabs,
  value,
  onChange,
  className,
}: {
  label: string;
  tabs: { id: T; label: ReactNode; count?: ReactNode }[];
  value: T;
  onChange: (v: T) => void;
  className?: string;
}): JSX.Element {
  return (
    <div
      role="tablist"
      aria-label={label}
      className={clsx('flex h-10 items-stretch gap-1 border-b border-line', className)}
    >
      {tabs.map((tab) => {
        const on = tab.id === value;
        return (
          <button
            key={tab.id}
            type="button"
            role="tab"
            aria-selected={on}
            onClick={() => onChange(tab.id)}
            className={clsx(
              'focus-ring relative inline-flex items-center gap-2 rounded-t-md px-4 text-sm font-medium',
              on ? 'text-hi' : 'text-dim hover:text-text',
            )}
          >
            {tab.label}
            {tab.count !== undefined && <span className="tnum font-mono text-[11px] text-muted">{tab.count}</span>}
            {on && (
              <span
                aria-hidden="true"
                className="absolute inset-x-3 -bottom-px h-0.5 rounded-full bg-accent shadow-[0_0_10px_rgb(var(--c-accent)/0.8)]"
              />
            )}
          </button>
        );
      })}
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// HUD readouts: money, KPI cards, wells, ticks
// ---------------------------------------------------------------------------------------------------------------------

/**
 * Money as a HUD readout: Doto digits (`.num-dot`), «сум» in Inter at about half the size, muted. `size` is the digits'
 * px; `unitClassName` recolours the unit (`text-artlabel` over art).
 */
export function HudMoney({
  minor,
  size = 22,
  className,
  unitClassName,
}: {
  minor: number;
  size?: number;
  className?: string;
  unitClassName?: string;
}): JSX.Element {
  const { num, unit } = moneyParts(minor);
  return (
    <span className={clsx('inline-flex items-baseline gap-[0.3em] whitespace-nowrap', className)}>
      <span className="num-dot leading-none" style={{ fontSize: size }}>
        {num}
      </span>{' '}
      <span
        className={clsx('font-sans font-medium leading-none text-muted', unitClassName)}
        style={{ fontSize: Math.max(10.5, Math.round(size * 0.46 * 2) / 2) }}
      >
        {unit}
      </span>
    </span>
  );
}

/**
 * A KPI card of the strip and of report grids: a mono label, a Doto value (36 for counts with `big`, 30 for money, 26
 * when `compact`) with its unit, a sub line, and an `aside` on the right (ticks, a tool button). `attention` is amber
 * with the glowing left bar (a call), `quiet` mutes the value. With `groupLabel` the card is a named `group` (the X
 * report's stats are found that way). Text pieces are separated by real spaces, so the card's text reads as a line.
 */
export function KpiCard({
  label,
  value,
  unit,
  sub,
  aside,
  tone = 'default',
  compact,
  big,
  icon,
  groupLabel,
  className,
  valueClassName,
}: {
  label: ReactNode;
  value: ReactNode;
  unit?: ReactNode;
  sub?: ReactNode;
  aside?: ReactNode;
  tone?: 'default' | 'attention' | 'quiet';
  compact?: boolean;
  big?: boolean;
  icon?: ReactNode;
  groupLabel?: string;
  className?: string;
  valueClassName?: string;
}): JSX.Element {
  return (
    <div
      role={groupLabel ? 'group' : undefined}
      aria-label={groupLabel}
      className={clsx(
        // No overflow-hidden: an `aside` may open a popover below the card (the «Вызовы игроков» list); the body
        // truncates its own lines.
        'relative flex min-w-0 items-center justify-between gap-3 rounded-md',
        compact ? 'h-20 px-4 py-3' : 'kpi-card',
        tone === 'attention' ? 'kpi-attention' : 'glass-kpi',
        className,
      )}
    >
      {tone === 'attention' && (
        <span
          aria-hidden="true"
          className="absolute bottom-3.5 left-0 top-3.5 w-0.5 bg-warning shadow-[0_0_10px_rgb(var(--c-warning)/0.8)]"
        />
      )}
      <KpiBody
        label={label}
        value={value}
        unit={unit}
        sub={sub}
        tone={tone}
        compact={compact}
        big={big}
        icon={icon}
        valueClassName={valueClassName}
      />
      {aside && <div className="flex shrink-0 items-center gap-2">{aside}</div>}
    </div>
  );
}

/** The label / value / sub column of a KPI card (also inside a KPI that is itself a button). */
export function KpiBody({
  label,
  value,
  unit,
  sub,
  tone = 'default',
  compact,
  big,
  icon,
  valueClassName,
}: {
  label: ReactNode;
  value: ReactNode;
  unit?: ReactNode;
  sub?: ReactNode;
  tone?: 'default' | 'attention' | 'quiet';
  compact?: boolean;
  big?: boolean;
  icon?: ReactNode;
  valueClassName?: string;
}): JSX.Element {
  return (
    <span className="kpi-body flex h-full min-w-0 flex-col justify-between gap-0.5">
      <span className={clsx('kpi-label label flex items-center gap-[7px]', tone === 'attention' && 'text-warning')}>
        {icon}
        {label}
      </span>{' '}
      <span className="kpi-value flex min-w-0 items-baseline gap-2">
        <span
          className={clsx(
            'num-dot truncate leading-none',
            compact ? 'text-[26px]' : big ? 'kpi-big text-[36px]' : 'kpi-money text-[30px]',
            tone === 'attention' ? 'text-warning' : tone === 'quiet' ? 'text-muted' : 'text-hi',
            valueClassName,
          )}
        >
          {value}
        </span>
        {unit && (
          <>
            {' '}
            <span className="shrink-0 text-sm font-medium leading-none text-muted">{unit}</span>
          </>
        )}
      </span>
      {sub && (
        <>
          {' '}
          <span className="kpi-sub truncate text-xs leading-none text-muted">{sub}</span>
        </>
      )}
    </span>
  );
}

/**
 * An inset readout: mono label, Doto value, unit. A named `group` when `groupLabel` is given. `tone` colours the value
 * (`accent` a computed result or a bonus, `warn` a difference).
 */
export function Well({
  label,
  value,
  unit,
  tone,
  size = 20,
  groupLabel,
  className,
  children,
}: {
  label: ReactNode;
  value: ReactNode;
  unit?: ReactNode;
  tone?: 'accent' | 'warn' | 'muted';
  size?: number;
  groupLabel?: string;
  className?: string;
  children?: ReactNode;
}): JSX.Element {
  return (
    <div
      role={groupLabel ? 'group' : undefined}
      aria-label={groupLabel}
      className={clsx('well flex min-w-0 flex-col justify-between gap-2 px-3.5 py-3', className)}
    >
      <span className="label-sm">{label}</span>{' '}
      <span className="flex min-w-0 items-baseline gap-1.5">
        <span
          className={clsx(
            'num-dot truncate leading-none',
            tone === 'accent'
              ? 'text-accent'
              : tone === 'warn'
                ? 'text-warning'
                : tone === 'muted'
                  ? 'text-muted'
                  : 'text-hi',
          )}
          style={{ fontSize: size }}
        >
          {value}
        </span>
        {unit && (
          <>
            {' '}
            <span className="shrink-0 text-[11.5px] font-medium leading-none text-muted">{unit}</span>
          </>
        )}
      </span>
      {children}
    </div>
  );
}

/**
 * Occupancy ticks: busy (accent, glowing), free (faint accent), unavailable (faint grey); at most `max` bars — with more
 * PCs one tick stands for several. `label` names the group (the counts in words).
 */
export function Ticks({
  busy,
  free,
  unavailable = 0,
  max = 32,
  label,
  className,
}: {
  busy: number;
  free: number;
  unavailable?: number;
  max?: number;
  label: string;
  className?: string;
}): JSX.Element {
  const total = busy + free + unavailable;
  const per = Math.max(1, Math.ceil(total / max));
  const kinds: ('busy' | 'free' | 'off')[] = [
    ...Array<'busy'>(Math.ceil(busy / per)).fill('busy'),
    ...Array<'free'>(Math.ceil(free / per)).fill('free'),
    ...Array<'off'>(Math.ceil(unavailable / per)).fill('off'),
  ].slice(0, max);
  return (
    <span role="img" aria-label={label} className={clsx('flex gap-[3px]', className)}>
      {kinds.map((k, i) => (
        <span
          key={i}
          className={clsx(
            'h-[18px] w-[3px] rounded-[1px]',
            k === 'busy' && 'bg-accent shadow-[0_0_6px_rgb(var(--c-accent)/0.6)]',
            k === 'free' && 'bg-accent/[0.22]',
            k === 'off' && 'bg-text/[0.07]',
          )}
        />
      ))}
    </span>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Empty state
// ---------------------------------------------------------------------------------------------------------------------

/**
 * Nothing to show yet: a framed icon with static corner brackets, a title, a line of text and a hint row (a key to
 * press). `compact` is the one-line variant for tables and lists.
 */
export function EmptyState({
  icon,
  title,
  text,
  hint,
  compact,
  className,
}: {
  icon?: ReactNode;
  title: ReactNode;
  text?: ReactNode;
  hint?: ReactNode;
  compact?: boolean;
  className?: string;
}): JSX.Element {
  if (compact) {
    return (
      <div className={clsx('flex flex-col items-center gap-1.5 py-8 text-center', className)}>
        <span className="label">{title}</span>
        {text && <span className="text-xs text-muted">{text}</span>}
      </div>
    );
  }
  return (
    <div className={clsx('mx-auto flex max-w-[320px] flex-col items-center text-center', className)}>
      {icon && (
        <span className="hud-brackets relative flex h-14 w-14 items-center justify-center rounded-md border border-line bg-surface/40 text-muted [--brk-size:10px]">
          {icon}
        </span>
      )}
      <p
        className={clsx(
          'font-display text-base font-medium leading-[22px] text-hi [text-wrap:balance]',
          icon && 'mt-4',
        )}
      >
        {title}
      </p>
      {text && <p className="mt-1.5 text-[13px] leading-5 text-dim [text-wrap:balance]">{text}</p>}
      {hint && <div className="label mt-3 flex items-center gap-2">{hint}</div>}
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Save bar and toast
// ---------------------------------------------------------------------------------------------------------------------

/** Save bar for settings pages: shows only when something changed; «Сохранено» after a save, as a toast. */
export function SaveBar({
  dirty,
  saving,
  onSave,
  onReset,
  label,
}: {
  dirty: boolean;
  saving: boolean;
  onSave: () => void;
  onReset: () => void;
  label: string;
}): JSX.Element | null {
  // A save that went through leaves nothing dirty: say so for a moment instead of the bar just vanishing.
  const [saved, setSaved] = useState(false);
  const wasSaving = useRef(false);
  useEffect(() => {
    if (wasSaving.current && !saving && !dirty) setSaved(true);
    wasSaving.current = saving;
  }, [saving, dirty]);
  useEffect(() => {
    if (dirty) setSaved(false);
    if (!saved) return undefined;
    const id = setTimeout(() => setSaved(false), 2500);
    return () => clearTimeout(id);
  }, [saved, dirty]);

  if (!dirty) {
    return saved ? (
      <div
        role="status"
        className="panel-solid anim-rise fixed bottom-6 right-6 z-40 flex w-[360px] max-w-[calc(100vw-3rem)] items-center gap-3 px-4 py-3.5 text-sm font-medium text-text"
      >
        <StatusDot tone="ok" />
        {t('Сохранено')}
      </div>
    ) : null;
  }
  return (
    <div className="panel-solid sticky bottom-0 z-10 flex min-h-16 items-center justify-between gap-3 px-5 py-3">
      <span className="text-sm text-dim">{label}</span>
      <div className="flex gap-2">
        <Button variant="ghost" onClick={onReset} disabled={saving}>
          {t('Отменить')}
        </Button>
        <Button variant="primary" onClick={onSave} disabled={saving}>
          {saving ? t('Сохраняем…') : t('Сохранить')}
        </Button>
      </div>
    </div>
  );
}
