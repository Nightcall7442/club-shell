import { forwardRef, type HTMLAttributes, type ReactNode } from 'react';
import clsx from 'clsx';
import type { NotificationLevel } from '@clubshell/contracts';

export type BadgeTone = 'neutral' | 'primary' | 'accent' | 'success' | 'danger' | 'muted';
export type BadgeSize = 'sm' | 'md' | 'lg';

export interface BadgeProps extends HTMLAttributes<HTMLSpanElement> {
  tone?: BadgeTone;
  size?: BadgeSize;
  /** Leading status dot. */
  dot?: boolean;
  /** Pulsing dot (live tournaments, remote control). */
  live?: boolean;
  icon?: ReactNode;
  /** Solid fill instead of the translucent tint. */
  solid?: boolean;
}

const TINT: Record<BadgeTone, string> = {
  neutral: 'border border-accent/20 bg-accent/[0.04] text-text/80',
  primary: 'bg-primary/15 text-primary',
  accent: 'bg-accent/15 text-accent',
  success: 'bg-success/15 text-success',
  danger: 'bg-danger/15 text-danger',
  muted: 'bg-muted/15 text-muted',
};

const SOLID: Record<BadgeTone, string> = {
  neutral: 'bg-text text-bg',
  primary: 'bg-primary text-on-primary',
  accent: 'bg-accent text-on-accent',
  success: 'bg-success text-bg',
  danger: 'bg-danger text-white',
  muted: 'bg-muted text-bg',
};

const DOT: Record<BadgeTone, string> = {
  neutral: 'bg-text',
  primary: 'bg-primary',
  accent: 'bg-accent',
  success: 'bg-success',
  danger: 'bg-danger',
  muted: 'bg-muted',
};

const SIZES: Record<BadgeSize, string> = {
  sm: 'h-6 px-2 text-[0.62rem] gap-1.5',
  md: 'h-7 px-2.5 text-[0.68rem] gap-1.5',
  lg: 'h-9 px-3.5 text-xs gap-2',
};

/** Maps a notification/admin severity to a badge tone (also used by toasts and banners). */
export function levelTone(level: NotificationLevel): BadgeTone {
  switch (level) {
    case 'success':
      return 'success';
    case 'warning':
      return 'accent';
    case 'error':
      return 'danger';
    default:
      return 'primary';
  }
}

export const Badge = forwardRef<HTMLSpanElement, BadgeProps>(function Badge(
  { tone = 'neutral', size = 'md', dot = false, live = false, icon, solid = false, className, children, ...rest },
  ref,
) {
  return (
    <span
      ref={ref}
      className={clsx(
        'inline-flex shrink-0 items-center whitespace-nowrap rounded-sm font-mono font-medium uppercase tracking-[0.1em]',
        SIZES[size],
        solid ? SOLID[tone] : TINT[tone],
        className,
      )}
      {...rest}
    >
      {(dot || live) && (
        <span aria-hidden="true" className={clsx('h-2 w-2 rounded-full', DOT[tone], live && 'anim-live-dot')} />
      )}
      {icon && (
        <span
          aria-hidden="true"
          className="inline-flex h-[1em] w-[1em] items-center justify-center [&>svg]:h-full [&>svg]:w-full"
        >
          {icon}
        </span>
      )}
      {children}
    </span>
  );
});
