import { useCallback, useEffect, useMemo, useRef } from 'react';
import clsx from 'clsx';
import { motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { ChatRooms } from '@clubshell/contracts';
import { Badge } from '@/components/ui/Badge';
import { useGamepad } from '@/hooks/useGamepad';
import { useAuthStore } from '@/store/auth';
import { selectActiveRoom, useChatStore, type ChatRoom } from '@/store/chat';
import { useNotificationsStore } from '@/store/notifications';
import { useSettingsStore } from '@/store/settings';
import { useThemeStore } from '@/store/theme';
import { Composer, type ComposerHandle } from './Composer';
import { MessageList } from './MessageList';

export interface RoomEntry {
  roomId: string;
  label: string;
  hint: string;
  unread: number;
}

/** Human label of a room id (`pc:<id>` support, `club`, `zone:<zone>`, `dm:…`). */
export function roomLabel(roomId: string, t: (key: string, vars?: Record<string, unknown>) => string): string {
  if (roomId === ChatRooms.Club) {
    return t('chat.roomClub');
  }
  if (roomId.startsWith('pc:')) {
    return t('chat.roomSupport');
  }
  if (roomId.startsWith('zone:')) {
    return t('chat.roomZoneNamed', { zone: roomId.slice('zone:'.length) });
  }
  if (roomId.startsWith('dm:')) {
    return t('chat.admin');
  }
  return roomId;
}

const ICONS: Record<'support' | 'club' | 'zone' | 'other', JSX.Element> = {
  support: (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <path d="M3 18v-6a9 9 0 0 1 18 0v6" />
      <path d="M21 19a2 2 0 0 1-2 2h-1a2 2 0 0 1-2-2v-3a2 2 0 0 1 2-2h3zM3 19a2 2 0 0 0 2 2h1a2 2 0 0 0 2-2v-3a2 2 0 0 0-2-2H3z" />
    </svg>
  ),
  club: (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2" />
      <circle cx="9" cy="7" r="4" />
      <path d="M23 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75" />
    </svg>
  ),
  zone: (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <rect x="3" y="3" width="7" height="7" rx="1" />
      <rect x="14" y="3" width="7" height="7" rx="1" />
      <rect x="3" y="14" width="7" height="7" rx="1" />
      <rect x="14" y="14" width="7" height="7" rx="1" />
    </svg>
  ),
  other: (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" />
    </svg>
  ),
};

function roomIcon(roomId: string): JSX.Element {
  if (roomId.startsWith('pc:')) {
    return ICONS.support;
  }
  if (roomId === ChatRooms.Club) {
    return ICONS.club;
  }
  if (roomId.startsWith('zone:')) {
    return ICONS.zone;
  }
  return ICONS.other;
}

/** Support chat: room list (support / club / zone), the message log and the composer. */
export default function ChatScreen(): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const animations = useThemeStore((s) => s.theme.animations);
  const meId = useAuthStore((s) => s.user?.id ?? null);
  const pc = useSettingsStore((s) => s.pcInfo?.pc ?? null);
  const defaultRoute = useSettingsStore((s) => s.shellConfig?.ui.defaultRoute ?? '/home');
  const rooms = useChatStore((s) => s.rooms);
  const activeRoomId = useChatStore((s) => s.activeRoomId);
  const active = useChatStore(selectActiveRoom);
  const status = useChatStore((s) => s.status);
  const load = useChatStore((s) => s.load);
  const loadMore = useChatStore((s) => s.loadMore);
  const setActiveRoom = useChatStore((s) => s.setActiveRoom);
  const send = useChatStore((s) => s.send);
  const markRead = useChatStore((s) => s.markRead);
  const pushError = useNotificationsStore((s) => s.pushError);
  const composerRef = useRef<ComposerHandle | null>(null);

  const entries = useMemo<RoomEntry[]>(() => {
    const ids: string[] = [];
    const push = (id: string | null): void => {
      if (id && !ids.includes(id)) {
        ids.push(id);
      }
    };
    push(pc ? ChatRooms.forPc(pc.id) : activeRoomId);
    push(ChatRooms.Club);
    push(pc && pc.zone.length > 0 ? ChatRooms.forZone(pc.zone) : null);
    Object.keys(rooms).forEach(push);
    return ids.map((roomId) => ({
      roomId,
      label: roomLabel(roomId, t),
      hint: roomId.startsWith('pc:') ? (pc?.name ?? '') : roomId.startsWith('zone:') ? t('chat.roomZone') : '',
      unread: rooms[roomId]?.unread ?? 0,
    }));
  }, [pc, activeRoomId, rooms, t]);

  // First load of the default room (bootstrap may already have done it).
  useEffect(() => {
    if (status === 'idle') {
      void load();
    }
  }, [status, load]);

  const openRoom = useCallback(
    (roomId: string) => {
      if (roomId === activeRoomId) {
        return;
      }
      setActiveRoom(roomId);
      const room: ChatRoom | undefined = useChatStore.getState().rooms[roomId];
      if (!room || room.status === 'idle') {
        void load(roomId);
      }
      composerRef.current?.focus();
    },
    [activeRoomId, setActiveRoom, load],
  );

  useEffect(() => {
    composerRef.current?.focus();
  }, []);

  // Everything visible in the active room counts as read once the log sits at the bottom.
  const onReachBottom = useCallback(() => {
    if (active.unread > 0) {
      void markRead(active.roomId);
    }
  }, [active.unread, active.roomId, markRead]);

  useEffect(() => {
    if (active.status === 'ready' && active.unread > 0) {
      void markRead(active.roomId);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active.roomId, active.status]);

  const onSend = useCallback(
    async (text: string) => {
      try {
        await send(text, active.roomId || undefined);
      } catch (e) {
        pushError(e, t('chat.failed'));
        throw e;
      }
    },
    [send, active.roomId, pushError, t],
  );

  useGamepad({
    onBack: () => navigate(defaultRoute),
    onTab: (dir) => {
      const i = entries.findIndex((r) => r.roomId === activeRoomId);
      const n = entries.length;
      if (n > 1 && i >= 0) {
        const next = entries[(i + (dir === 'next' ? 1 : n - 1)) % n];
        if (next) {
          openRoom(next.roomId);
        }
      }
    },
  });

  const loading = status === 'loading' || active.status === 'loading';

  return (
    <motion.div
      initial={{ opacity: 0, y: 12 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: animations ? 0.2 : 0, ease: 'easeOut' }}
      className="grid h-full min-h-0 grid-cols-[clamp(15rem,18vw,22rem)_1fr] gap-[var(--gap)]"
    >
      <aside className="glass flex min-h-0 flex-col rounded-2xl p-3" aria-label={t('chat.rooms')}>
        <header className="px-2 pb-3 pt-1">
          <h1 className="text-2xl font-bold text-text">{t('chat.title')}</h1>
          <p className="text-sm text-muted">{t('chat.subtitle')}</p>
        </header>
        <nav className="no-scrollbar min-h-0 flex-1 overflow-y-auto">
          <ul role="list" className="flex flex-col gap-1">
            {entries.map((r) => {
              const selected = r.roomId === activeRoomId;
              return (
                <li key={r.roomId}>
                  <button
                    type="button"
                    data-nav="true"
                    aria-current={selected ? 'true' : undefined}
                    onClick={() => openRoom(r.roomId)}
                    className={clsx(
                      'focus-ring flex w-full items-center gap-3 rounded-xl px-3 py-3 text-left transition-colors duration-[var(--dur-fast)]',
                      selected
                        ? 'bg-primary/20 text-text shadow-[inset_0_0_0_1px_rgb(var(--c-primary)/0.5)]'
                        : 'text-muted hover:bg-text/5 hover:text-text',
                    )}
                  >
                    <span
                      className={clsx(
                        'inline-flex h-10 w-10 shrink-0 items-center justify-center rounded-lg [&>svg]:h-5 [&>svg]:w-5',
                        selected ? 'bg-primary text-on-primary' : 'bg-surface/70',
                      )}
                      aria-hidden="true"
                    >
                      {roomIcon(r.roomId)}
                    </span>
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-base font-semibold">{r.label}</span>
                      {r.hint && <span className="block truncate text-xs text-muted">{r.hint}</span>}
                    </span>
                    {r.unread > 0 && (
                      <Badge tone="primary" size="sm" solid aria-label={t('chat.unread', { count: r.unread })}>
                        {r.unread > 99 ? '99+' : r.unread}
                      </Badge>
                    )}
                  </button>
                </li>
              );
            })}
          </ul>
        </nav>
      </aside>

      <section
        className="glass flex min-h-0 flex-col overflow-hidden rounded-2xl"
        aria-label={active.roomId ? roomLabel(active.roomId, t) : t('chat.title')}
      >
        <header className="flex items-center gap-3 border-b border-text/10 px-5 py-3">
          <span
            className="inline-flex h-10 w-10 items-center justify-center rounded-lg bg-primary/15 text-primary [&>svg]:h-5 [&>svg]:w-5"
            aria-hidden="true"
          >
            {active.roomId ? roomIcon(active.roomId) : ICONS.other}
          </span>
          <div className="min-w-0 flex-1">
            <h2 className="truncate text-lg font-semibold text-text">
              {active.roomId ? roomLabel(active.roomId, t) : t('chat.title')}
            </h2>
            <p className="truncate text-sm text-muted">
              {active.roomId.startsWith('pc:') || !active.roomId ? t('chat.subtitle') : active.roomId}
            </p>
          </div>
          {active.unread > 0 && (
            <Badge tone="primary" size="md" solid>
              {t('chat.unread', { count: active.unread })}
            </Badge>
          )}
        </header>
        <MessageList
          className="min-h-0 flex-1"
          messages={active.messages}
          meId={meId}
          loading={loading}
          hasMore={active.hasMore}
          onLoadMore={() => void loadMore(active.roomId)}
          onReachBottom={onReachBottom}
        />
        <footer className="border-t border-text/10 px-4 pb-3 pt-3">
          <Composer ref={composerRef} onSend={onSend} disabled={!meId || status === 'error'} />
        </footer>
      </section>
    </motion.div>
  );
}
