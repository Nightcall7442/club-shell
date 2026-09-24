import { forwardRef, type HTMLAttributes } from 'react';
import clsx from 'clsx';

export type ProgressTone = 'primary' | 'accent' | 'success' | 'danger';
export type ProgressSize = 'sm' | 'md' | 'lg';

export interface ProgressBarProps extends HTMLAttributes<HTMLDivElement> {
  /** Current value in `[0, max]`. */
  value: number;
  max?: number;
  tone?: ProgressTone;
  size?: ProgressSize;
  /** Accessible label (`aria-label`). */
  label?: string;
  /** Text rendered right of the bar (e.g. a percentage or `mm:ss`). */
  valueText?: string;
  /** Unknown progress: sliding shimmer instead of a fill. */
  indeterminate?: boolean;
}

const FILL: Record<ProgressTone, string> = {
  primary: 'bg-primary',
  accent: 'bg-accent',
  success: 'bg-success',
  danger: 'bg-danger',
};

const TRACK_H: Record<ProgressSize, string> = { sm: 'h-1.5', md: 'h-2.5', lg: 'h-4' };

export const ProgressBar = forwardRef<HTMLDivElement, ProgressBarProps>(function ProgressBar(
  { value, max = 100, tone = 'primary', size = 'md', label, valueText, indeterminate = false, className, ...rest },
  ref,
) {
  const safeMax = max > 0 ? max : 100;
  const pct = indeterminate ? 0 : Math.min(100, Math.max(0, (value / safeMax) * 100));
  return (
    <div ref={ref} className={clsx('flex w-full items-center gap-3', className)} {...rest}>
      <div
        role="progressbar"
        aria-label={label}
        aria-valuemin={0}
        aria-valuemax={safeMax}
        aria-valuenow={indeterminate ? undefined : Math.round(value)}
        aria-valuetext={valueText}
        className={clsx('relative w-full overflow-hidden rounded-full bg-text/10', TRACK_H[size])}
      >
        {indeterminate ? (
          <div className={clsx('anim-skeleton absolute inset-0', FILL[tone])} />
        ) : (
          <div
            className={clsx('h-full rounded-full transition-[width] duration-300 ease-out', FILL[tone])}
            style={{ width: `${pct}%` }}
          />
        )}
      </div>
      {valueText && <span className="tnum shrink-0 text-sm text-muted">{valueText}</span>}
    </div>
  );
});
