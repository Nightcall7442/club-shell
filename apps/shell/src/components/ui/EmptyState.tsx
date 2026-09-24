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
 * "Nothing here yet": a muted line icon, a display-face title and at most one line of what to do next.
 */
export function EmptyState({ icon, title, hint, action, className }: EmptyStateProps): JSX.Element {
  return (
    <div className={clsx('flex flex-col items-center justify-center gap-3 py-8 text-center', className)}>
      <span
        aria-hidden="true"
        className="inline-flex h-10 w-10 items-center justify-center text-muted/70 [&>svg]:h-full [&>svg]:w-full"
      >
        {icon}
      </span>
      <div className="flex flex-col gap-1.5">
        <p className="font-display text-lg font-normal tracking-tight text-text">{title}</p>
        {hint && <p className="mx-auto max-w-[26rem] text-sm text-muted">{hint}</p>}
      </div>
      {action}
    </div>
  );
}
