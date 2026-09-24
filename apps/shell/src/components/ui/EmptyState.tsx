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
 * "Nothing here yet", said in the product's voice: an empty target — four brackets around a faint grid — with the
 * icon in the middle, a display-face title and one line of what to do next.
 */
export function EmptyState({ icon, title, hint, action, className }: EmptyStateProps): JSX.Element {
  return (
    <div className={clsx('flex flex-col items-center justify-center gap-4 py-8 text-center', className)}>
      <span
        aria-hidden="true"
        className="hud-brackets hud-grid relative inline-flex h-24 w-24 items-center justify-center text-muted [--brk-size:12px] [&>svg]:h-9 [&>svg]:w-9"
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
