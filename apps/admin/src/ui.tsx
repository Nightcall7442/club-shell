/**
 * Console building blocks in the Obsidian material: buttons, fields, inputs, toggles, panels, section headers, tables
 * and a money input. Every page is made of these, so a change here changes the whole console.
 */
import { useEffect, useState, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode } from 'react';
import clsx from 'clsx';
import { t } from '@/i18n';

export function Button({
  children,
  variant = 'secondary',
  size = 'md',
  className,
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: 'primary' | 'secondary' | 'ghost' | 'danger';
  size?: 'sm' | 'md';
}): JSX.Element {
  return (
    <button
      type="button"
      {...rest}
      className={clsx(
        'focus-ring inline-flex select-none items-center justify-center gap-2 whitespace-nowrap rounded-md font-semibold transition-colors disabled:cursor-not-allowed disabled:opacity-40',
        size === 'md' ? 'h-10 px-3.5 text-sm' : 'h-8 px-2.5 text-xs',
        variant === 'primary' && 'cut-corners rounded-none text-on-accent hover:brightness-110',
        variant === 'secondary' && 'choice',
        variant === 'ghost' && 'text-muted hover:bg-white/[0.06] hover:text-text',
        variant === 'danger' && 'text-danger hover:bg-danger/10',
        className,
      )}
    >
      {children}
    </button>
  );
}

export const inputCls =
  'focus-ring h-10 w-full rounded-md border border-line bg-bg px-3 text-sm text-text placeholder:text-muted disabled:opacity-50';

export function Input(props: InputHTMLAttributes<HTMLInputElement>): JSX.Element {
  return <input {...props} className={clsx(inputCls, props.className)} />;
}

export function Field({
  label,
  hint,
  children,
  className,
}: {
  label: string;
  hint?: string;
  children: ReactNode;
  className?: string;
}): JSX.Element {
  return (
    <label className={clsx('flex flex-col gap-1.5', className)}>
      <span className="label">{label}</span>
      {children}
      {hint && <span className="text-xs text-muted">{hint}</span>}
    </label>
  );
}

/** Whole-sum input for money shown in сум and stored in minor units (×100). */
export function MoneyInput({
  value,
  onChange,
  disabled,
}: {
  value: number;
  onChange: (minor: number) => void;
  disabled?: boolean;
}): JSX.Element {
  const [text, setText] = useState(String(Math.round(value / 100)));
  useEffect(() => setText(String(Math.round(value / 100))), [value]);
  return (
    <div className="relative">
      <input
        inputMode="numeric"
        disabled={disabled}
        className={clsx(inputCls, 'tnum pr-12')}
        value={text}
        onChange={(e) => {
          const digits = e.target.value.replace(/\D/g, '');
          setText(digits);
          onChange(Number(digits || '0') * 100);
        }}
      />
      <span className="pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-xs text-muted">
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
        <span className="pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-xs text-muted">
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
          'relative h-6 w-11 shrink-0 rounded-full border transition-colors',
          checked ? 'border-accent/70 bg-accent/25' : 'border-white/15 bg-white/10',
        )}
      >
        <span
          className={clsx(
            'absolute top-[2px] h-[18px] w-[18px] rounded-full transition-transform',
            checked ? 'translate-x-[1.3rem] bg-accent' : 'translate-x-[2px] bg-text/70',
          )}
        />
      </span>
      {label && <span>{label}</span>}
    </button>
  );
}

/** Page title row: one-line display title, optional actions on the right. */
export function PageHeader({ title, actions }: { title: string; actions?: ReactNode }): JSX.Element {
  return (
    <header className="flex min-h-10 flex-wrap items-center justify-between gap-4">
      <h1 className="font-display text-2xl font-light leading-[1.2] tracking-tight">{title}</h1>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </header>
  );
}

/** A panel with an optional mono header row and actions. */
export function Section({
  title,
  actions,
  children,
  className,
  bodyClassName,
}: {
  title?: string;
  actions?: ReactNode;
  children: ReactNode;
  className?: string;
  bodyClassName?: string;
}): JSX.Element {
  return (
    <section className={clsx('panel flex min-w-0 flex-col', className)}>
      {(title || actions) && (
        <header className="flex min-h-12 items-center justify-between gap-3 border-b border-line px-5 py-2">
          {title && <h2 className="label text-text">{title}</h2>}
          {actions && <div className="flex items-center gap-2">{actions}</div>}
        </header>
      )}
      <div className={clsx('flex flex-col gap-4 p-5', bodyClassName)}>{children}</div>
    </section>
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
    <div className="overflow-x-auto">
      <table className="w-full border-collapse text-sm">
        <thead>
          <tr className="border-b border-line">
            {columns.map((c) => (
              <th
                key={c.key}
                style={{ width: c.width }}
                className={clsx('label px-3 py-2.5 font-medium', c.num ? 'text-right' : 'text-left')}
              >
                {c.title}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => {
            const k = rowKey(r);
            return (
              <tr
                key={k}
                onClick={onRowClick ? () => onRowClick(r) : undefined}
                className={clsx(
                  'border-b border-line/60 last:border-b-0',
                  onRowClick && 'cursor-pointer hover:bg-white/[0.03]',
                  selectedKey === k && 'bg-accent/[0.07]',
                )}
              >
                {columns.map((c) => (
                  <td key={c.key} className={clsx('px-3 py-2.5 align-middle', c.num && 'tnum text-right')}>
                    {c.render(r)}
                  </td>
                ))}
              </tr>
            );
          })}
        </tbody>
      </table>
      {rows.length === 0 && <p className="px-3 py-8 text-center text-sm text-muted">{empty ?? '—'}</p>}
    </div>
  );
}

/** Inline result line after an action. */
export function Note({ note }: { note: { text: string; tone: 'ok' | 'err' } | null }): JSX.Element | null {
  if (!note) return null;
  return (
    <p
      className={clsx(
        'rounded-md px-3 py-2 text-sm',
        note.tone === 'ok' ? 'bg-success/10 text-success' : 'bg-danger/10 text-danger',
      )}
    >
      {note.text}
    </p>
  );
}

/** Save bar for settings pages: shows only when something changed. */
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
  if (!dirty) return null;
  return (
    <div className="panel sticky bottom-0 z-10 flex items-center justify-between gap-3 px-5 py-3">
      <span className="text-sm text-muted">{label}</span>
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
