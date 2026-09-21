/**
 * Support chat: rooms (default `pc:<pcId>`, chosen by the Agent), messages oldest-first, unread counters,
 * optimistic sending, paging (`before`). `agent://chat.message` appends into the matching room.
 */
import { ChatRooms, type ChatMessage } from '@clubshell/contracts';
import { create } from 'zustand';
import { subscribeWithSelector } from 'zustand/middleware';
import { track } from '@/lib/analytics';
import { log } from '@/lib/logger';
import { api, toShellApiError, uuid, type ShellError } from '@/lib/tauri';
import { asShellError, type AsyncStatus } from './settings';

export interface ChatRoom {
  roomId: string;
  messages: ChatMessage[];
  unread: number;
  hasMore: boolean;
  status: AsyncStatus;
}

export interface ChatState {
  rooms: Readonly<Record<string, ChatRoom>>;
  /** Room shown by the chat screen; `null` until the first `load()` resolves the default room. */
  activeRoomId: string | null;
  /** Ids of messages currently being sent (optimistic bubbles). */
  pending: string[];
  status: AsyncStatus;
  error: ShellError | null;
}

export interface ChatActions {
  /** Loads the newest page of `roomId` (default room when omitted) and makes it active. */
  load(roomId?: string): Promise<ChatRoom | null>;
  loadMore(roomId?: string): Promise<void>;
  setActiveRoom(roomId: string): void;
  /** Optimistic send; rethrows on failure and removes the optimistic bubble. */
  send(text: string, roomId?: string): Promise<ChatMessage>;
  /** Marks everything up to the newest message as read (no-op when nothing is unread). */
  markRead(roomId?: string): Promise<void>;
  onMessage(m: ChatMessage): void;
  reset(): void;
}

export type ChatStore = ChatState & ChatActions;

const PAGE = 50;
const OPTIMISTIC_SENDER = 'me';

const initialState: ChatState = {
  rooms: {},
  activeRoomId: null,
  pending: [],
  status: 'idle',
  error: null,
};

function emptyRoom(roomId: string): ChatRoom {
  return { roomId, messages: [], unread: 0, hasMore: false, status: 'idle' };
}

function mergeMessages(existing: ChatMessage[], incoming: ChatMessage[]): ChatMessage[] {
  const byId = new Map<string, ChatMessage>();
  for (const m of [...existing, ...incoming]) {
    byId.set(m.id, m);
  }
  return Array.from(byId.values()).sort((a, b) => Date.parse(a.createdAt) - Date.parse(b.createdAt));
}

export const useChatStore = create<ChatStore>()(
  subscribeWithSelector((set, get) => {
    const patchRoom = (roomId: string, patch: Partial<ChatRoom>): void => {
      set((s) => ({ rooms: { ...s.rooms, [roomId]: { ...(s.rooms[roomId] ?? emptyRoom(roomId)), ...patch } } }));
    };
    const resolveRoom = (roomId: string | undefined): string | null => roomId ?? get().activeRoomId;

    return {
      ...initialState,

      async load(roomId) {
        set({ status: 'loading', error: null });
        try {
          const res = await api.chat.history({ roomId: roomId ?? null, limit: PAGE });
          const prev = get().rooms[res.roomId];
          patchRoom(res.roomId, {
            messages: mergeMessages(prev?.messages ?? [], res.items),
            unread: res.unread,
            hasMore: res.hasMore,
            status: 'ready',
          });
          set({ activeRoomId: res.roomId, status: 'ready' });
          return get().rooms[res.roomId] ?? null;
        } catch (e) {
          const error = asShellError(e);
          log.warn('chat.load failed', error);
          set({ status: 'error', error });
          return null;
        }
      },

      async loadMore(roomId) {
        const id = resolveRoom(roomId);
        const room = id ? get().rooms[id] : undefined;
        if (!id || !room || !room.hasMore || room.status === 'loading') {
          return;
        }
        patchRoom(id, { status: 'loading' });
        try {
          const res = await api.chat.history({ roomId: id, before: room.messages[0]?.id ?? null, limit: PAGE });
          patchRoom(id, { messages: mergeMessages(res.items, room.messages), hasMore: res.hasMore, status: 'ready' });
        } catch (e) {
          log.warn('chat.loadMore failed', asShellError(e));
          patchRoom(id, { status: 'error' });
        }
      },

      setActiveRoom(roomId) {
        set((s) => ({
          activeRoomId: roomId,
          rooms: s.rooms[roomId] ? s.rooms : { ...s.rooms, [roomId]: emptyRoom(roomId) },
        }));
      },

      async send(text, roomId) {
        const body = text.trim();
        if (body.length === 0 || body.length > ChatRooms.MaxTextLength) {
          throw toShellApiError({
            code: 'validation',
            message: 'text must be 1–2000 chars',
            details: { field: 'text', reason: body.length === 0 ? 'required' : 'max' },
          });
        }
        const id = resolveRoom(roomId) ?? ChatRooms.Club;
        const tempId = `pending-${uuid()}`;
        const optimistic: ChatMessage = {
          id: tempId,
          roomId: id,
          senderId: OPTIMISTIC_SENDER,
          senderName: '',
          senderRole: 'member',
          text: body,
          createdAt: new Date().toISOString(),
          readAt: null,
          kind: 'text',
        };
        patchRoom(id, { messages: [...(get().rooms[id]?.messages ?? []), optimistic] });
        set((s) => ({ pending: [...s.pending, tempId] }));
        try {
          const sent = await api.chat.send(body, roomId ?? get().activeRoomId ?? undefined);
          set((s) => {
            const room = s.rooms[sent.roomId] ?? s.rooms[id] ?? emptyRoom(sent.roomId);
            const messages = mergeMessages(
              room.messages.filter((m) => m.id !== tempId),
              [sent],
            );
            const rooms = { ...s.rooms, [sent.roomId]: { ...room, roomId: sent.roomId, messages } };
            if (sent.roomId !== id && rooms[id]) {
              rooms[id] = {
                ...(rooms[id] as ChatRoom),
                messages: (rooms[id] as ChatRoom).messages.filter((m) => m.id !== tempId),
              };
            }
            return {
              rooms,
              pending: s.pending.filter((p) => p !== tempId),
              activeRoomId: s.activeRoomId ?? sent.roomId,
            };
          });
          track('chat.send', { length: body.length });
          return sent;
        } catch (e) {
          const err = toShellApiError(e);
          set((s) => ({
            pending: s.pending.filter((p) => p !== tempId),
            rooms: s.rooms[id]
              ? {
                  ...s.rooms,
                  [id]: {
                    ...(s.rooms[id] as ChatRoom),
                    messages: (s.rooms[id] as ChatRoom).messages.filter((m) => m.id !== tempId),
                  },
                }
              : s.rooms,
            error: err.toJSON(),
          }));
          throw err;
        }
      },

      async markRead(roomId) {
        const id = resolveRoom(roomId);
        const room = id ? get().rooms[id] : undefined;
        if (!id || !room || room.unread === 0) {
          return;
        }
        const last = [...room.messages].reverse().find((m) => !m.id.startsWith('pending-'));
        if (!last) {
          return;
        }
        patchRoom(id, {
          unread: 0,
          messages: room.messages.map((m) => (m.readAt ? m : { ...m, readAt: new Date().toISOString() })),
        });
        try {
          const res = await api.chat.markRead(last.id, id);
          patchRoom(id, { unread: res.unread });
        } catch (e) {
          log.warn('chat.markRead failed', asShellError(e));
        }
      },

      onMessage(m) {
        const room = get().rooms[m.roomId] ?? emptyRoom(m.roomId);
        const unread = m.readAt ? room.unread : room.unread + 1;
        patchRoom(m.roomId, { messages: mergeMessages(room.messages, [m]), unread });
        set((s) => ({ activeRoomId: s.activeRoomId ?? m.roomId }));
      },

      reset() {
        set(initialState);
      },
    };
  }),
);

const EMPTY_ROOM: ChatRoom = emptyRoom('');

/** Selector: the active room (stable empty placeholder before load). */
export const selectActiveRoom = (s: ChatStore): ChatRoom =>
  s.activeRoomId ? (s.rooms[s.activeRoomId] ?? EMPTY_ROOM) : EMPTY_ROOM;
/** Selector: unread messages across all rooms. */
export const selectUnreadTotal = (s: ChatStore): number => Object.values(s.rooms).reduce((sum, r) => sum + r.unread, 0);
/** `true` for the optimistic bubble of a message being sent. */
export const isPendingMessage = (m: ChatMessage): boolean => m.senderId === OPTIMISTIC_SENDER;
