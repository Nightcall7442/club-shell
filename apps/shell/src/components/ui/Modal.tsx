import { useEffect, useId, useRef, type KeyboardEvent, type MouseEvent, type ReactNode, type RefObject } from 'react';
import { createPortal } from 'react-dom';
import clsx from 'clsx';
import { AnimatePresence, motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { useThemeStore } from '@/store/theme';

export type ModalSize = 'sm' | 'md' | 'lg' | 'xl' | 'full';

export interface ModalProps {
  open: boolean;
  onClose: () => void;
  title?: ReactNode;
  description?: ReactNode;
  size?: ModalSize;
  children?: ReactNode;
  /** Action row rendered under the body. */
  footer?: ReactNode;
  /** Click on the backdrop closes (default `true`). */
  closeOnBackdrop?: boolean;
  /** Escape closes (default `true`). Set both to `false` for must-acknowledge dialogs. */
  closeOnEscape?: boolean;
  /** Show the top-right close button (default `true`). */
  showClose?: boolean;
  /** Element to focus when opened; defaults to the first focusable descendant. */
  initialFocusRef?: RefObject<HTMLElement>;
  /** Danger styling of the title. */
  danger?: boolean;
  className?: string;
}

const SIZE: Record<ModalSize, string> = {
  sm: 'w-[min(92vw,26rem)]',
  md: 'w-[min(92vw,36rem)]',
  lg: 'w-[min(92vw,50rem)]',
  xl: 'w-[min(94vw,70rem)]',
  full: 'h-[94vh] w-[96vw]',
};

const FOCUSABLE =
  'a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex="-1"])';

let openCount = 0;

/** Marks `<html data-modal-open>` and makes `#root` inert while at least one modal is open. */
function useModalGlobals(open: boolean): void {
  useEffect(() => {
    if (!open) {
      return undefined;
    }
    openCount += 1;
    const html = document.documentElement;
    const root = document.getElementById('root');
    html.dataset.modalOpen = 'true';
    root?.setAttribute('inert', '');
    return () => {
      openCount -= 1;
      if (openCount === 0) {
        delete html.dataset.modalOpen;
        root?.removeAttribute('inert');
      }
    };
  }, [open]);
}

function focusables(container: HTMLElement): HTMLElement[] {
  return Array.from(container.querySelectorAll<HTMLElement>(FOCUSABLE)).filter(
    (el) => el.offsetParent !== null || el === document.activeElement,
  );
}

/** Portal dialog with focus trap, Escape, backdrop click and fade/slide motion. */
export function Modal({
  open,
  onClose,
  title,
  description,
  size = 'md',
  children,
  footer,
  closeOnBackdrop = true,
  closeOnEscape = true,
  showClose = true,
  initialFocusRef,
  danger = false,
  className,
}: ModalProps): JSX.Element | null {
  const { t } = useTranslation();
  const animations = useThemeStore((s) => s.theme.animations);
  const id = useId();
  const titleId = `${id}-title`;
  const descId = `${id}-desc`;
  const panel = useRef<HTMLDivElement>(null);
  const restoreTo = useRef<HTMLElement | null>(null);

  useModalGlobals(open);

  useEffect(() => {
    if (!open) {
      return undefined;
    }
    restoreTo.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const frame = requestAnimationFrame(() => {
      const target =
        initialFocusRef?.current ?? (panel.current ? focusables(panel.current)[0] : undefined) ?? panel.current;
      target?.focus();
    });
    return () => {
      cancelAnimationFrame(frame);
      restoreTo.current?.focus();
      restoreTo.current = null;
    };
  }, [open, initialFocusRef]);

  // Escape must work even when focus fell back to <body> (e.g. the focused button was replaced by a step change).
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;
  useEffect(() => {
    if (!open) {
      return undefined;
    }
    const onDocKey = (e: globalThis.KeyboardEvent): void => {
      if (e.key !== 'Escape' || !panel.current || panel.current.contains(e.target as Node)) {
        return;
      }
      const dialogs = document.querySelectorAll('[role="dialog"][aria-modal="true"]');
      if (dialogs[dialogs.length - 1] !== panel.current) {
        return;
      }
      e.stopPropagation();
      if (closeOnEscape) {
        onCloseRef.current();
      }
    };
    document.addEventListener('keydown', onDocKey);
    return () => document.removeEventListener('keydown', onDocKey);
  }, [open, closeOnEscape]);

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>): void => {
    if (e.key === 'Escape') {
      e.stopPropagation();
      if (closeOnEscape) {
        onClose();
      }
      return;
    }
    if (e.key === 'Tab' && panel.current) {
      const items = focusables(panel.current);
      if (items.length === 0) {
        e.preventDefault();
        return;
      }
      const first = items[0];
      const last = items[items.length - 1];
      if (e.shiftKey && document.activeElement === first) {
        e.preventDefault();
        last?.focus();
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first?.focus();
      }
    }
  };

  const onBackdrop = (e: MouseEvent<HTMLDivElement>): void => {
    if (closeOnBackdrop && e.target === e.currentTarget) {
      onClose();
    }
  };

  if (typeof document === 'undefined') {
    return null;
  }

  const duration = animations ? 0.2 : 0;

  return createPortal(
    <AnimatePresence>
      {open && (
        <motion.div
          key="backdrop"
          className="fixed inset-0 z-[100] flex items-center justify-center bg-bg/75 p-[var(--gutter)]"
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0 }}
          transition={{ duration }}
          onMouseDown={onBackdrop}
        >
          <motion.div
            ref={panel}
            role="dialog"
            aria-modal="true"
            aria-labelledby={title ? titleId : undefined}
            aria-describedby={description ? descId : undefined}
            tabIndex={-1}
            data-nav-scope="modal"
            className={clsx(
              'glass-strong relative flex max-h-[94vh] flex-col overflow-hidden rounded-xl outline-none',
              SIZE[size],
              className,
            )}
            initial={{ opacity: 0, y: 14, scale: 0.97 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: 8, scale: 0.98 }}
            transition={animations ? { type: 'spring', stiffness: 420, damping: 34, mass: 0.9 } : { duration: 0 }}
            onKeyDown={onKeyDown}
          >
            {(title || showClose) && (
              <header className="flex items-start gap-4 px-6 pt-6">
                <div className="min-w-0 flex-1">
                  {title && (
                    <h2
                      id={titleId}
                      className={clsx(
                        'font-display text-[1.55rem] font-normal leading-tight tracking-tight',
                        danger ? 'text-danger' : 'text-text',
                      )}
                    >
                      {title}
                    </h2>
                  )}
                  {description && (
                    <p id={descId} className="mt-1 text-base text-muted">
                      {description}
                    </p>
                  )}
                </div>
                {showClose && (
                  <button
                    type="button"
                    data-nav="true"
                    aria-label={t('common.close')}
                    onClick={onClose}
                    className="focus-ring -mr-2 -mt-2 inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-md text-muted transition-colors duration-[var(--dur-fast)] hover:bg-text/[0.06] hover:text-text"
                  >
                    <svg
                      viewBox="0 0 24 24"
                      className="h-6 w-6"
                      fill="none"
                      stroke="currentColor"
                      strokeWidth="2"
                      strokeLinecap="round"
                      aria-hidden="true"
                    >
                      <path d="M6 6l12 12M18 6L6 18" />
                    </svg>
                  </button>
                )}
              </header>
            )}
            <div className="themed-scrollbar min-h-0 flex-1 overflow-y-auto overflow-x-hidden px-6 py-5">
              {children}
            </div>
            {footer && (
              <footer className="flex flex-wrap items-center justify-end gap-3 border-t border-[color:var(--hairline)] px-6 py-4">
                {footer}
              </footer>
            )}
          </motion.div>
        </motion.div>
      )}
    </AnimatePresence>,
    document.body,
  );
}
