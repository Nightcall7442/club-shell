import { forwardRef, useId, type InputHTMLAttributes, type ReactNode } from 'react';
import clsx from 'clsx';

export type InputSize = 'md' | 'lg';

export interface InputProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'size'> {
  label?: ReactNode;
  /** Error text; also sets `aria-invalid`. */
  error?: string | null;
  /** Helper text shown under the field when there is no error. */
  hint?: ReactNode;
  /** Leading adornment (icon). */
  leading?: ReactNode;
  /** Trailing adornment (icon / button). */
  trailing?: ReactNode;
  size?: InputSize;
  /** Class of the outer wrapper (the `className` goes to the `<input>`). */
  wrapperClassName?: string;
}

const SIZE: Record<InputSize, string> = { md: 'h-12 text-base', lg: 'h-14 text-lg' };

export const Input = forwardRef<HTMLInputElement, InputProps>(function Input(
  { label, error, hint, leading, trailing, size = 'md', wrapperClassName, className, id, disabled, ...rest },
  ref,
) {
  const autoId = useId();
  const inputId = id ?? autoId;
  const hintId = `${inputId}-hint`;

  const hasError = Boolean(error);
  const describedBy = hasError || hint ? hintId : undefined;

  return (
    <div className={clsx('flex w-full flex-col gap-1.5', wrapperClassName)}>
      {label && (
        <label htmlFor={inputId} className="text-sm font-medium text-muted">
          {label}
        </label>
      )}
      <div
        className={clsx(
          'glass flex items-center gap-2 rounded-md px-3 transition-[box-shadow,border-color] duration-[var(--dur-fast)]',
          'focus-within:border-primary/60 focus-within:shadow-[var(--shadow-glow)]',
          hasError && 'border-danger/70 focus-within:border-danger focus-within:shadow-[var(--shadow-glow-danger)]',
          disabled && 'opacity-50',
          SIZE[size],
        )}
      >
        {leading && (
          <span
            aria-hidden="true"
            className="inline-flex h-[1.25em] w-[1.25em] shrink-0 items-center justify-center text-muted [&>svg]:h-full [&>svg]:w-full"
          >
            {leading}
          </span>
        )}
        <input
          ref={ref}
          id={inputId}
          data-nav="true"
          disabled={disabled}
          aria-invalid={hasError || undefined}
          aria-describedby={describedBy}
          className={clsx(
            'h-full min-w-0 flex-1 bg-transparent text-text outline-none placeholder:text-muted/70 disabled:cursor-not-allowed',
            className,
          )}
          {...rest}
        />
        {trailing && <span className="inline-flex shrink-0 items-center text-muted">{trailing}</span>}
      </div>
      {(hasError || hint) && (
        <p
          id={hintId}
          role={hasError ? 'alert' : undefined}
          className={clsx('text-sm', hasError ? 'text-danger' : 'text-muted')}
        >
          {hasError ? error : hint}
        </p>
      )}
    </div>
  );
});
