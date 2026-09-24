import { useEffect, useLayoutEffect, useMemo, useRef, useState, type UIEvent } from 'react';
import clsx from 'clsx';
import { AnimatePresence, motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import type { ChatMessage } from '@clubshell/contracts';
import { Avatar } from '@/components/ui/Avatar';
import { Badge } from '@/components/ui/Badge';
import { Skeleton } from '@/components/ui/Skeleton';
import { Spinner } from '@/components/ui/Spinner';
import { useLocale } from '@/hooks/useLocale';
import { formatRelativeDay, formatTime } from '@/lib/format';
import { isPendingMessage } from '@/store/chat';
import { useThemeStore } from '@/store/theme';

export interface MessageListProps {
  messages: ChatMessage[];
  /** Current user id; messages from this sender (and optimistic ones) render on the right. */
  meId: string | null;
  loading?: boolean;
  hasMore?: boolean;
  onLoadMore?: () => void;
  /** Called when the list is scrolled to the bottom while unread messages exist. */
  onReachBottom?: () => void;
  className?: string;
}

interface DayGroup {
  key: string;
  label: string;
  items: ChatMessage[];
}

const NEAR_BOTTOM_PX = 120;
const LOAD_TOP_PX = 80;
const RENDER_WINDOW = 300;

function dayKey(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`;
}

function CheckIcon({ double }: { double: boolean }): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      className="h-[1em] w-[1em]"
    >
      <path d="m3 13 4 4L15 9" />
      {double && <path d="m11 17 8-8" />}
    </svg>
  );
}

function ArrowDownIcon(): JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      className="h-[1em] w-[1em]"
    >
      <path d="M12 5v14m-7-7 7 7 7-7" />
    </svg>
  );
}

function Bubble({ m, mine, showSender }: { m: ChatMessage; mine: boolean; showSender: boolean }): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const pending = isPendingMessage(m);
  const admin = m.kind === 'admin' || m.senderRole === 'admin';

  if (m.kind === 'system') {
    return (
      <li className="my-2 flex justify-center px-6">
        <span className="glass max-w-[70%] rounded-full px-4 py-1.5 text-center text-sm text-muted">
          <span className="sr-only">{t('chat.system')}: </span>
          {m.text}
        </span>
      </li>
    );
  }

  return (
    <li
      className={clsx('flex items-end gap-2', mine ? 'justify-end' : 'justify-start')}
      aria-label={mine ? t('chat.you') : t('chat.messageFrom', { name: m.senderName })}
    >
      {!mine && (
        <span className={clsx('shrink-0', !showSender && 'invisible')}>
          <Avatar name={m.senderName || t('chat.admin')} size="sm" />
        </span>
      )}
      <div className={clsx('flex max-w-[68%] flex-col gap-1', mine ? 'items-end' : 'items-start')}>
        {!mine && showSender && (
          <span className="flex items-center gap-2 px-1 text-sm text-muted">
            {m.senderName}
            {admin && (
              <Badge tone="accent" size="sm">
                {t('chat.admin')}
              </Badge>
            )}
          </span>
        )}
        <div
          className={clsx(
            'whitespace-pre-wrap break-words rounded-2xl px-4 py-2.5 text-base leading-relaxed transition-opacity',
            mine && 'rounded-br-sm bg-primary text-on-primary',
            !mine && admin && 'glass rounded-bl-sm border-accent/40 text-text',
            !mine && !admin && 'glass rounded-bl-sm text-text',
            pending && 'opacity-60',
          )}
        >
          {m.text}
        </div>
        <span className={clsx('tnum flex items-center gap-1 px-1 text-xs text-muted')}>
          {pending ? t('chat.sending') : formatTime(m.createdAt, locale)}
          {mine && !pending && (
            <span
              className={clsx(m.readAt ? 'text-primary' : 'text-muted')}
              aria-label={m.readAt ? t('chat.read') : t('chat.sent')}
              role="img"
            >
              <CheckIcon double={Boolean(m.readAt)} />
            </span>
          )}
        </span>
      </div>
    </li>
  );
}

/**
 * Scrollable message log grouped by day. Sticks to the bottom while the user is near it; otherwise a
 * "new messages" pill appears. Scrolling to the top requests older pages and keeps the viewport anchored.
 */
export function MessageList({
  messages,
  meId,
  loading = false,
  hasMore = false,
  onLoadMore,
  onReachBottom,
  className,
}: MessageListProps): JSX.Element {
  const { t } = useTranslation();
  const { locale } = useLocale();
  const animations = useThemeStore((s) => s.theme.animations);
  const scrollerRef = useRef<HTMLDivElement | null>(null);
  const atBottomRef = useRef(true);
  const prevHeightRef = useRef(0);
  const prevFirstIdRef = useRef<string | null>(null);
  const prevCountRef = useRef(0);
  const [unseen, setUnseen] = useState(0);

  // ponytail: count window instead of virtualization; pages are 50 and 300 bubbles render fine on a kiosk PC.
  const visible = messages.length > RENDER_WINDOW ? messages.slice(-RENDER_WINDOW) : messages;

  const groups = useMemo<DayGroup[]>(() => {
    const out: DayGroup[] = [];
    for (const m of visible) {
      const key = dayKey(m.createdAt);
      const last = out[out.length - 1];
      if (last && last.key === key) {
        last.items.push(m);
      } else {
        out.push({ key, label: formatRelativeDay(m.createdAt, locale), items: [m] });
      }
    }
    return out;
  }, [visible, locale]);

  const scrollToBottom = (smooth: boolean): void => {
    const el = scrollerRef.current;
    if (el) {
      el.scrollTo({ top: el.scrollHeight, behavior: smooth && animations ? 'smooth' : 'auto' });
    }
    atBottomRef.current = true;
    setUnseen(0);
    onReachBottom?.();
  };

  // Keep the viewport anchored when older messages are prepended; stick to the bottom for new ones.
  useLayoutEffect(() => {
    const el = scrollerRef.current;
    if (!el) {
      return;
    }
    const firstId = visible[0]?.id ?? null;
    const prepended =
      prevFirstIdRef.current !== null &&
      firstId !== prevFirstIdRef.current &&
      visible.some((m) => m.id === prevFirstIdRef.current);
    const appended = visible.length > prevCountRef.current && !prepended;
    if (prepended) {
      el.scrollTop += el.scrollHeight - prevHeightRef.current;
    } else if (appended) {
      const last = visible[visible.length - 1];
      const mine = last !== undefined && (last.senderId === meId || isPendingMessage(last));
      if (atBottomRef.current || mine) {
        el.scrollTop = el.scrollHeight;
        atBottomRef.current = true;
        onReachBottom?.();
      } else {
        setUnseen((n) => n + (visible.length - prevCountRef.current));
      }
    } else if (prevCountRef.current === 0 && visible.length > 0) {
      el.scrollTop = el.scrollHeight;
    }
    prevHeightRef.current = el.scrollHeight;
    prevFirstIdRef.current = firstId;
    prevCountRef.current = visible.length;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [visible, meId]);

  // Stay pinned to the newest message when the viewport shrinks (on-screen keyboard, window resize).
  useEffect(() => {
    const el = scrollerRef.current;
    if (!el || typeof ResizeObserver === 'undefined') {
      return undefined;
    }
    const ro = new ResizeObserver(() => {
      if (atBottomRef.current) {
        el.scrollTop = el.scrollHeight;
      }
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const onScroll = (e: UIEvent<HTMLDivElement>): void => {
    const el = e.currentTarget;
    const distance = el.scrollHeight - el.scrollTop - el.clientHeight;
    const nearBottom = distance < NEAR_BOTTOM_PX;
    if (nearBottom && !atBottomRef.current) {
      atBottomRef.current = true;
      setUnseen(0);
      onReachBottom?.();
    } else if (!nearBottom) {
      atBottomRef.current = false;
    }
    if (el.scrollTop < LOAD_TOP_PX && hasMore && !loading && onLoadMore) {
      prevHeightRef.current = el.scrollHeight;
      onLoadMore();
    }
  };

  const isMine = (m: ChatMessage): boolean => isPendingMessage(m) || (meId !== null && m.senderId === meId);

  return (
    <div className={clsx('relative min-h-0', className)}>
      <div
        ref={scrollerRef}
        onScroll={onScroll}
        role="log"
        aria-label={t('chat.messageList')}
        aria-live="polite"
        aria-relevant="additions"
        className="themed-scrollbar h-full overflow-y-auto overflow-x-hidden px-4 py-3"
      >
        {loading && messages.length === 0 && (
          <div className="flex flex-col gap-4 py-4" aria-hidden="true">
            {[0, 1, 2, 3, 4].map((i) => (
              <div key={i} className={clsx('flex items-end gap-2', i % 2 === 1 ? 'justify-end' : 'justify-start')}>
                {i % 2 === 0 && <Skeleton variant="circle" width={32} height={32} />}
                <Skeleton variant="rect" width={`${28 + ((i * 13) % 30)}%`} height={52} className="rounded-2xl" />
              </div>
            ))}
          </div>
        )}
        {loading && messages.length > 0 && (
          <div className="flex justify-center py-2">
            <Spinner size="sm" label={t('chat.loadOlder')} />
          </div>
        )}
        {!loading && hasMore && onLoadMore && (
          <div className="flex justify-center py-2">
            <button
              type="button"
              data-nav="true"
              onClick={onLoadMore}
              className="focus-ring glass rounded-full px-4 py-1.5 text-sm text-muted hover:text-text"
            >
              {t('chat.loadOlder')}
            </button>
          </div>
        )}
        {!loading && messages.length === 0 && (
          <div className="flex h-full flex-col items-center justify-center gap-3 text-center text-muted">
            <span className="text-5xl" aria-hidden="true">
              💬
            </span>
            <p className="text-lg">{t('chat.empty')}</p>
          </div>
        )}
        {groups.map((g) => (
          <section key={g.key} aria-label={g.label} className="mb-2">
            <div className="sticky top-0 z-10 my-2 flex justify-center">
              <span className="glass-strong rounded-full px-3 py-1 text-xs font-medium text-muted">{g.label}</span>
            </div>
            <ul className="flex flex-col gap-2">
              {g.items.map((m, i) => {
                const prev = g.items[i - 1];
                const showSender = !prev || prev.senderId !== m.senderId || prev.kind === 'system';
                return <Bubble key={m.id} m={m} mine={isMine(m)} showSender={showSender} />;
              })}
            </ul>
          </section>
        ))}
      </div>
      <AnimatePresence>
        {unseen > 0 && (
          <motion.div
            key="pill"
            initial={{ opacity: 0, y: 12 }}
            animate={{ opacity: 1, y: 0 }}
            exit={{ opacity: 0, y: 12 }}
            transition={{ duration: animations ? 0.18 : 0 }}
            className="pointer-events-none absolute inset-x-0 bottom-3 flex justify-center"
          >
            <button
              type="button"
              data-nav="true"
              onClick={() => scrollToBottom(true)}
              aria-label={t('chat.scrollToLatest')}
              className="focus-ring pointer-events-auto flex items-center gap-2 rounded-full bg-primary px-4 py-2 text-sm font-semibold text-on-primary shadow-[var(--shadow-glow)]"
            >
              <ArrowDownIcon />
              {t('chat.newMessages')}
              <span className="tnum rounded-full bg-white/20 px-2">{unseen}</span>
            </button>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}
