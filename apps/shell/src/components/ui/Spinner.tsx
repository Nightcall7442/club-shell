import { forwardRef, type HTMLAttributes } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';

export type SpinnerSize = 'sm' | 'md' | 'lg' | 'xl';

export interface SpinnerProps extends HTMLAttributes<HTMLSpanElement> {
  size?: SpinnerSize;
  /** Accessible label; defaults to `common.loading`. */
  label?: string;
  /** Use the surrounding text colour instead of the primary colour (inside buttons). */
  inherit?: boolean;
}

const SIZES: Record<SpinnerSize, string> = {
  sm: 'h-4 w-4 border-2',
  md: 'h-6 w-6 border-[3px]',
  lg: 'h-9 w-9 border-4',
  xl: 'h-14 w-14 border-4',
};

/** Circular spinner; keeps turning under reduced motion (`.anim-spin` is exempt). */
export const Spinner = forwardRef<HTMLSpanElement, SpinnerProps>(function Spinner(
  { size = 'md', label, inherit = false, className, ...rest },
  ref,
) {
  const { t } = useTranslation();
  return (
    <span
      ref={ref}
      role="status"
      aria-label={label ?? t('common.loading')}
      className={clsx(
        'anim-spin inline-block shrink-0 rounded-full border-solid border-current border-r-transparent',
        SIZES[size],
        inherit ? 'text-current' : 'text-primary',
        className,
      )}
      {...rest}
    />
  );
});
