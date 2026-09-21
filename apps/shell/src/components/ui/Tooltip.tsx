import { cloneElement, useEffect, useId, useRef, useState, type ReactElement, type ReactNode } from 'react';
import clsx from 'clsx';
import { AnimatePresence, motion } from 'framer-motion';

export type TooltipPlacement = 'top' | 'bottom' | 'left' | 'right';

export interface TooltipProps {
  content: ReactNode;
  /** Single focusable child; receives `aria-describedby` while the tooltip is visible. */
  children: ReactElement<{ 'aria-describedby'?: string }>;
  placement?: TooltipPlacement;
  /** Hover/focus delay before showing (default 500 ms). */
  delayMs?: number;
  disabled?: boolean;
  className?: string;
}

const POSITION: Record<TooltipPlacement, string> = {
  top: 'bottom-full left-1/2 -translate-x-1/2 mb-2',
  bottom: 'top-full left-1/2 -translate-x-1/2 mt-2',
  left: 'right-full top-1/2 -translate-y-1/2 mr-2',
  right: 'left-full top-1/2 -translate-y-1/2 ml-2',
};

const OFFSET: Record<TooltipPlacement, { x?: number; y?: number }> = {
  top: { y: 6 },
  bottom: { y: -6 },
  left: { x: 6 },
  right: { x: -6 },
};

/** Delayed hover/focus tooltip; hidden on Escape and when the pointer leaves. */
export function Tooltip({
  content,
  children,
  placement = 'top',
  delayMs = 500,
  disabled = false,
  className,
}: TooltipProps): JSX.Element {
  const id = useId();
  const [open, setOpen] = useState(false);
  const timer = useRef<number | null>(null);

  const clear = (): void => {
    if (timer.current !== null) {
      window.clearTimeout(timer.current);
      timer.current = null;
    }
  };
  const show = (): void => {
    if (disabled) {
      return;
    }
    clear();
    timer.current = window.setTimeout(() => setOpen(true), delayMs);
  };
  const hide = (): void => {
    clear();
    setOpen(false);
  };

  useEffect(() => clear, []);
  useEffect(() => {
    if (!open) {
      return undefined;
    }
    const onKey = (e: KeyboardEvent): void => {
      if (e.key === 'Escape') {
        hide();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
    // hide is stable enough (state setter + ref); re-subscribing per `open` is intended.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  const offset = OFFSET[placement];

  return (
    <span className="relative inline-flex" onMouseEnter={show} onMouseLeave={hide} onFocus={show} onBlur={hide}>
      {cloneElement(children, { 'aria-describedby': open ? id : children.props['aria-describedby'] })}
      <AnimatePresence>
        {open && !disabled && (
          <motion.span
            id={id}
            role="tooltip"
            className={clsx(
              'glass-strong pointer-events-none absolute z-[95] whitespace-nowrap rounded-md px-3 py-1.5 text-sm font-medium text-text',
              POSITION[placement],
              className,
            )}
            initial={{ opacity: 0, x: offset.x ?? 0, y: offset.y ?? 0 }}
            animate={{ opacity: 1, x: 0, y: 0 }}
            exit={{ opacity: 0 }}
            transition={{ duration: 0.15, ease: 'easeOut' }}
          >
            {content}
          </motion.span>
        )}
      </AnimatePresence>
    </span>
  );
}
