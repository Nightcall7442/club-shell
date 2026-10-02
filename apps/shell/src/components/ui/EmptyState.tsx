import type { ReactNode } from 'react';
import clsx from 'clsx';

export interface EmptyStateProps {
  /** Line icon (inline SVG). */
  icon?: ReactNode;
  title: string;
  hint?: string;
  /** Optional action under the text (a Button). */
  action?: ReactNode;
  className?: string;
}

/**
 * "Nothing here yet": a muted line icon on a quiet disc, a display-face title and at most one line of what to do next.
 */
export function EmptyState({ icon, title, hint, action, className }: EmptyStateProps): JSX.Element {
  return (
    <div className={clsx('flex flex-col items-center justify-center gap-4 py-8 text-center', className)}>
      {icon && (
        <span
          aria-hidden="true"
          className="inline-flex h-16 w-16 items-center justify-center rounded-full bg-text/[0.04] text-muted/80 shadow-[inset_0_0_0_1px_var(--hairline)] [&>svg]:h-8 [&>svg]:w-8"
        >
          {icon}
        </span>
      )}
      <div className="flex flex-col gap-1.5">
        <p className="font-display text-lg font-normal tracking-tight text-text">{title}</p>
        {hint && <p className="mx-auto max-w-[26rem] text-sm text-muted">{hint}</p>}
      </div>
      {action}
    </div>
  );
}
