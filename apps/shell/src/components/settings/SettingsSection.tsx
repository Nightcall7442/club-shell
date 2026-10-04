/**
 * Building blocks of every settings card (the PC block on Home, the profile's account tab): a glass card with a
 * heading, a row-sized switch and a pill radio group. All of them are `data-nav`, so a gamepad reaches every control.
 */
import { useId, type ReactNode } from 'react';
import clsx from 'clsx';

export interface SettingsSectionProps {
  title: string;
  description?: string;
  children: ReactNode;
  className?: string;
}

/** Glass card with a heading. */
export function SettingsSection({ title, description, children, className }: SettingsSectionProps): JSX.Element {
  const id = useId();
  return (
    <section aria-labelledby={id} className={clsx('glass flex flex-col gap-4 rounded-xl p-[var(--gap)]', className)}>
      <header>
        <h3 id={id} className="font-display text-xl font-normal tracking-tight">
          {title}
        </h3>
        {description && <p className="text-sm text-muted">{description}</p>}
      </header>
      {children}
    </section>
  );
}

export interface ToggleProps {
  label: string;
  hint?: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
}

/** Row-sized switch (`role="switch"`), focusable and gamepad-navigable. */
export function Toggle({ label, hint, checked, onChange, disabled = false }: ToggleProps): JSX.Element {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      data-nav="true"
      disabled={disabled}
      onClick={() => onChange(!checked)}
      className="focus-ring flex w-full items-center justify-between gap-4 rounded-lg px-3 py-3 text-left transition-colors duration-[var(--dur-fast)] hover:bg-text/5 disabled:cursor-not-allowed disabled:opacity-50"
    >
      <span className="min-w-0">
        <span className="block text-base font-medium">{label}</span>
        {hint && <span className="block text-sm text-muted">{hint}</span>}
      </span>
      <span
        aria-hidden="true"
        className={clsx(
          'relative h-8 w-14 shrink-0 rounded-full border transition-colors duration-[var(--dur-base)]',
          checked ? 'border-accent/70 bg-accent/25' : 'border-text/15 bg-text/10',
        )}
      >
        <span
          className={clsx(
            'absolute top-[3px] h-6 w-6 rounded-full transition-[transform,background-color] duration-[var(--dur-base)] ease-[var(--ease-out)]',
            checked ? 'translate-x-[1.6rem] bg-accent' : 'translate-x-[3px] bg-text/70',
          )}
        />
      </span>
    </button>
  );
}

export interface OptionGroupOption<K extends string> {
  key: K;
  label: ReactNode;
}

export interface OptionGroupProps<K extends string> {
  label: string;
  options: OptionGroupOption<K>[];
  value: K;
  onChange: (key: K) => void;
  disabled?: boolean;
}

/** Pill radio group. */
export function OptionGroup<K extends string>({
  label,
  options,
  value,
  onChange,
  disabled = false,
}: OptionGroupProps<K>): JSX.Element {
  return (
    <div role="radiogroup" aria-label={label} className="flex flex-wrap gap-2">
      {options.map((o) => {
        const active = o.key === value;
        return (
          <button
            key={o.key}
            type="button"
            role="radio"
            aria-checked={active}
            data-nav="true"
            disabled={disabled}
            onClick={() => onChange(o.key)}
            className={clsx(
              'focus-ring inline-flex h-11 items-center rounded-md px-5 text-base font-semibold transition-colors duration-[var(--dur-fast)] disabled:cursor-not-allowed disabled:opacity-50',
              active ? 'choice choice-on' : 'choice',
            )}
          >
            {o.label}
          </button>
        );
      })}
    </div>
  );
}
