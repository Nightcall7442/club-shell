import { forwardRef, type ButtonHTMLAttributes, type ReactNode } from 'react';
import clsx from 'clsx';
import { Spinner } from '@/components/ui/Spinner';

/** `cta`: the screen's one main call to action (accent, cut corners, focus brackets). */
export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger' | 'cta';
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
  primary: 'bg-primary text-on-primary hover:bg-[rgb(var(--c-primary-hover))] active:bg-[rgb(var(--c-primary-active))]',
  secondary: 'bg-text/[0.06] text-text hover:bg-text/10 active:bg-text/[0.14]',
  ghost: 'bg-transparent text-text hover:bg-text/[0.06] active:bg-text/10',
  danger: 'bg-danger text-white hover:bg-danger/90 active:bg-danger/80',
  cta: 'cut-corners hud-focus !rounded-none text-on-accent',
};

const SIZE: Record<ButtonSize, string> = {
  md: 'h-11 gap-2 rounded-md px-4 text-base',
  lg: 'h-12 gap-2.5 rounded-lg px-5 text-base',
  xl: 'h-14 gap-3 rounded-lg px-7 text-lg',
};

const ICON_ONLY: Record<ButtonSize, string> = { md: 'w-11 px-0', lg: 'w-12 px-0', xl: 'w-14 px-0' };

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
        'transition-[background-color,box-shadow] duration-[var(--dur-fast)] ease-[var(--ease-out)]',
        'disabled:cursor-not-allowed disabled:opacity-40',
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
