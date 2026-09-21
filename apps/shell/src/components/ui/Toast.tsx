import { useEffect, useState } from 'react';
import clsx from 'clsx';
import { AnimatePresence, motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import type { NotificationAction, NotificationLevel } from '@clubshell/contracts';
import { levelTone, type BadgeTone } from '@/components/ui/Badge';
import { useThemeStore } from '@/store/theme';

/** Structural shape of a toast; `Toast` of the notifications store satisfies it. */
export interface ToastItem {
  id: string;
  title: string;
  body?: string;
  level: NotificationLevel;
  /** Seconds until auto-dismiss; `undefined` = default per level, `null`/`<= 0` = sticky. */
  ttlSec?: number | null;
  /** Call-to-action: a frontend route (`/wallet`) or an IPC command name; executed by `onAction`. */
  action?: NotificationAction | null;
  createdAt: number;
}

export interface ToastProps {
  item: ToastItem;
  onDismiss: (id: string) => void;
  /** Runs the call-to-action; the toast is dismissed afterwards. */
  onAction?: (action: NotificationAction, item: ToastItem) => void;
}

export interface ToastViewportProps {
  items: ToastItem[];
  onDismiss: (id: string) => void;
  onAction?: (action: NotificationAction, item: ToastItem) => void;
  /** Visible toasts at once (oldest first); the rest wait (default 4). */
  max?: number;
  className?: string;
}

const DEFAULT_TTL: Record<NotificationLevel, number> = { info: 6, success: 5, warning: 8, error: 10 };

const EDGE: Record<BadgeTone, string> = {
  neutral: 'border-l-text',
  primary: 'border-l-primary',
  accent: 'border-l-accent',
  success: 'border-l-success',
  danger: 'border-l-danger',
  muted: 'border-l-muted',
};

const TEXT: Record<BadgeTone, string> = {
  neutral: 'text-text',
  primary: 'text-primary',
  accent: 'text-accent',
  success: 'text-success',
  danger: 'text-danger',
  muted: 'text-muted',
};

const BAR: Record<BadgeTone, string> = {
  neutral: 'bg-text',
  primary: 'bg-primary',
  accent: 'bg-accent',
  success: 'bg-success',
  danger: 'bg-danger',
  muted: 'bg-muted',
};

function LevelIcon({ level }: { level: NotificationLevel }): JSX.Element {
  const common = { viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', strokeWidth: 2, strokeLinecap: 'round', strokeLinejoin: 'round' } as const;
  switch (level) {
    case 'success':
      return (
        <svg {...common} aria-hidden="true">
          <circle cx="12" cy="12" r="9" />
          <path d="M8 12l3 3 5-6" />
        </svg>
      );
    case 'warning':
      return (
        <svg {...common} aria-hidden="true">
          <path d="M12 3l10 18H2L12 3z" />
          <path d="M12 10v4M12 17.5v.5" />
        </svg>
      );
    case 'error':
      return (
        <svg {...common} aria-hidden="true">
          <circle cx="12" cy="12" r="9" />
          <path d="M9 9l6 6M15 9l-6 6" />
        </svg>
      );
    default:
      return (
        <svg {...common} aria-hidden="true">
          <circle cx="12" cy="12" r="9" />
          <path d="M12 11v5M12 7.5v.5" />
        </svg>
      );
  }
}

/** Resolves the effective TTL in seconds; `null` = sticky. */
export function toastTtl(item: ToastItem): number | null {
  if (item.ttlSec === undefined) {
    return DEFAULT_TTL[item.level];
  }
  return item.ttlSec !== null && item.ttlSec > 0 ? item.ttlSec : null;
}

export function Toast({ item, onDismiss, onAction }: ToastProps): JSX.Element {
  const { t } = useTranslation();
  const tone = levelTone(item.level);
  const ttl = toastTtl(item);
  const [running, setRunning] = useState(false);

  useEffect(() => {
    if (ttl === null) {
      return undefined;
    }
    const frame = requestAnimationFrame(() => setRunning(true));
    const timer = setTimeout(() => onDismiss(item.id), ttl * 1000);
    return () => {
      cancelAnimationFrame(frame);
      clearTimeout(timer);
    };
  }, [ttl, item.id, onDismiss]);

  return (
    <div
      role={item.level === 'error' ? 'alert' : 'status'}
      className={clsx('glass-strong relative w-[min(92vw,26rem)] overflow-hidden rounded-lg border-l-4 pl-4 pr-3 pt-3 pb-3', EDGE[tone])}
    >
      <div className="flex items-start gap-3">
        <span className={clsx('mt-0.5 inline-flex h-6 w-6 shrink-0 items-center justify-center [&>svg]:h-full [&>svg]:w-full', TEXT[tone])}>
          <LevelIcon level={item.level} />
        </span>
        <div className="min-w-0 flex-1">
          <p className="text-base font-semibold leading-snug text-text">{item.title}</p>
          {item.body && <p className="mt-0.5 text-sm leading-snug text-muted">{item.body}</p>}
          {item.action && (
            <button
              type="button"
              data-nav="true"
              onClick={() => {
                if (item.action) {
                  onAction?.(item.action, item);
                }
                onDismiss(item.id);
              }}
              className={clsx('focus-ring mt-2 inline-flex h-9 items-center rounded-md px-3 text-sm font-semibold hover:bg-text/10', TEXT[tone])}
            >
              {item.action.label}
            </button>
          )}
        </div>
        <button
          type="button"
          data-nav="true"
          aria-label={t('notifications.closeToast')}
          onClick={() => onDismiss(item.id)}
          className="focus-ring -mr-1 -mt-1 inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-full text-muted hover:bg-text/10 hover:text-text"
        >
          <svg viewBox="0 0 24 24" className="h-5 w-5" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" aria-hidden="true">
            <path d="M6 6l12 12M18 6L6 18" />
          </svg>
        </button>
      </div>
      {ttl !== null && (
        <div
          aria-hidden="true"
          className={clsx('absolute bottom-0 left-0 h-0.5 w-full origin-left opacity-70', BAR[tone])}
          style={{ transform: running ? 'scaleX(0)' : 'scaleX(1)', transition: `transform ${ttl}s linear` }}
        />
      )}
    </div>
  );
}

/** Stacked toasts (top-right); a polite live region so screen readers announce them. */
export function ToastViewport({ items, onDismiss, onAction, max = 4, className }: ToastViewportProps): JSX.Element {
  const { t } = useTranslation();
  const animations = useThemeStore((s) => s.theme.animations);
  const visible = items.slice(0, max);
  const duration = animations ? 0.2 : 0;
  return (
    <div
      role="region"
      aria-live="polite"
      aria-label={t('notifications.liveRegion')}
      className={clsx('pointer-events-none fixed right-[var(--gutter)] top-[calc(var(--topbar-h)+var(--gap))] z-[90] flex flex-col items-end gap-3', className)}
    >
      <AnimatePresence initial={false} mode="popLayout">
        {visible.map((item) => (
          <motion.div
            key={item.id}
            layout
            className="pointer-events-auto"
            initial={{ opacity: 0, x: 32, scale: 0.98 }}
            animate={{ opacity: 1, x: 0, scale: 1 }}
            exit={{ opacity: 0, x: 32, scale: 0.98 }}
            transition={{ duration, ease: 'easeOut' }}
          >
            <Toast item={item} onDismiss={onDismiss} onAction={onAction} />
          </motion.div>
        ))}
      </AnimatePresence>
    </div>
  );
}
