import { useEffect, useRef, type ReactNode } from 'react';
import clsx from 'clsx';

export interface PopoverProps {
  open: boolean;
  onClose: () => void;
  /** Accessible name of the panel. */
  label: string;
  /** The button that toggles it; mark it `data-popover-trigger` so opening focuses the panel, not the trigger. */
  trigger: ReactNode;
  children: ReactNode;
  /** `below-end`: under the trigger, right edges aligned (default); `above-start`: over it, left edges aligned. */
  placement?: 'below-end' | 'above-start';
  className?: string;
}

/**
 * Panel anchored to its trigger (see `placement`): closes on an outside pointer-down and on Escape, and moves focus
 * to its first `data-nav` control when it opens so keyboard and gamepad land inside it.
 */
export function Popover({
  open,
  onClose,
  label,
  trigger,
  children,
  placement = 'below-end',
  className,
}: PopoverProps): JSX.Element {
  const root = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) {
      return undefined;
    }
    const onPointer = (e: PointerEvent): void => {
      if (root.current && e.target instanceof Node && !root.current.contains(e.target)) {
        onClose();
      }
    };
    const onKey = (e: KeyboardEvent): void => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        onClose();
      }
    };
    document.addEventListener('pointerdown', onPointer, true);
    window.addEventListener('keydown', onKey, true);
    const frame = requestAnimationFrame(() =>
      root.current?.querySelector<HTMLElement>('[data-nav]:not([data-popover-trigger])')?.focus(),
    );
    return () => {
      document.removeEventListener('pointerdown', onPointer, true);
      window.removeEventListener('keydown', onKey, true);
      cancelAnimationFrame(frame);
    };
  }, [open, onClose]);

  return (
    <div ref={root} className="relative">
      {trigger}
      {open && (
        <div
          role="dialog"
          aria-label={label}
          className={clsx(
            'glass-strong anim-pop absolute z-50 rounded-lg p-3',
            placement === 'below-end' ? 'right-0 top-[calc(100%+0.5rem)]' : 'bottom-[calc(100%+0.5rem)] left-0',
            className,
          )}
        >
          {children}
        </div>
      )}
    </div>
  );
}

export default Popover;
