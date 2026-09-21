import { forwardRef, type ButtonHTMLAttributes, type ReactNode } from 'react';
import clsx from 'clsx';
import { Spinner } from '@/components/ui/Spinner';

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger';
export type ButtonSize = 'md' | 'lg' | 'xl';

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  /** Shows a spinner and disables the button; the label stays to avoid width jumps. */
  loading?: boolean;
  /** Leading icon (inline SVG). */
  icon?: ReactNode;
  /** Trailing icon. */
  iconRight?: ReactNode;
  /** Full width. */
  block?: boolean;
  /** Square icon-only button (pass `aria-label`). */
  iconOnly?: boolean;
}

const VARIANT: Record<ButtonVariant, string> = {
  primary:
    'bg-primary text-on-primary shadow-[inset_0_1px_0_rgb(255_255_255/0.5),inset_0_-1px_0_rgb(0_0_0/0.14),0_14px_36px_-12px_rgb(0_0_0/0.85)] hover:bg-[rgb(var(--c-primary-hover))] hover:shadow-[inset_0_1px_0_rgb(255_255_255/0.5),inset_0_-1px_0_rgb(0_0_0/0.14),0_18px_40px_-12px_rgb(0_0_0/0.9)] active:bg-[rgb(var(--c-primary-active))]',
  secondary: 'glass text-text hover:bg-surface/80 active:bg-surface',
  ghost: 'bg-transparent text-text hover:bg-text/10 active:bg-text/15',
  danger:
    'bg-danger text-white shadow-[0_10px_30px_-10px_rgb(var(--c-danger)/0.7)] hover:bg-danger/90 active:bg-danger/80',
};

const SIZE: Record<ButtonSize, string> = {
  md: 'h-11 gap-2 rounded-md px-4 text-base',
  lg: 'h-[3.25rem] gap-3 rounded-lg px-6 text-lg',
  xl: 'h-16 gap-3 rounded-xl px-8 text-xl',
};

const ICON_ONLY: Record<ButtonSize, string> = { md: 'w-11 px-0', lg: 'w-[3.25rem] px-0', xl: 'w-16 px-0' };

const ICON_BOX = 'inline-flex h-[1.25em] w-[1.25em] shrink-0 items-center justify-center [&>svg]:h-full [&>svg]:w-full';

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  {
    variant = 'primary',
    size = 'md',
    loading = false,
    icon,
    iconRight,
    block = false,
    iconOnly = false,
    className,
    children,
    disabled,
    type = 'button',
    ...rest
  },
  ref,
) {
  const inactive = Boolean(disabled) || loading;
  return (
    <button
      ref={ref}
      type={type}
      data-nav="true"
      disabled={inactive}
      aria-disabled={inactive || undefined}
      aria-busy={loading || undefined}
      className={clsx(
        'focus-ring relative inline-flex select-none items-center justify-center whitespace-nowrap font-semibold leading-none',
        'transition-[background-color,transform,box-shadow] duration-[var(--dur-fast)] ease-[var(--ease-out)] active:scale-[0.98]',
        'disabled:cursor-not-allowed disabled:opacity-50 disabled:active:scale-100',
        VARIANT[variant],
        SIZE[size],
        iconOnly && ICON_ONLY[size],
        block && 'w-full',
        className,
      )}
      {...rest}
    >
      {loading && (
        <span className="absolute inset-0 flex items-center justify-center">
          <Spinner size="sm" inherit />
        </span>
      )}
      <span
        className={clsx(
          'inline-flex min-w-0 max-w-full items-center justify-center gap-[inherit]',
          loading && 'invisible',
        )}
      >
        {icon && (
          <span aria-hidden="true" className={ICON_BOX}>
            {icon}
          </span>
        )}
        {children !== undefined && children !== null && <span className="min-w-0 truncate">{children}</span>}
        {iconRight && (
          <span aria-hidden="true" className={ICON_BOX}>
            {iconRight}
          </span>
        )}
      </span>
    </button>
  );
});
