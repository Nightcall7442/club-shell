/**
 * Dock in the middle of the status line: one icon per program open in the player's session (`kiosk_open_windows`), a
 * running catalogue game by its cover, first and set apart. Activating one brings it to the front
 * (`kiosk_focus_window`): the Shell goes behind it until the player comes back (HUD "back to ClubShell", or the
 * program closing). Polls every 2 s while the Shell has the focus and the page is visible, refreshes the moment it gets
 * the focus back, and renders nothing when nothing is open.
 */
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import clsx from 'clsx';
import { AnimatePresence, motion, useReducedMotion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { useResolvedAsset } from '@/components/media/GameArtwork';
import { Tooltip } from '@/components/ui/Tooltip';
import { log } from '@/lib/logger';
import { api, events, isShellApiError, type OpenWindow } from '@/lib/tauri';
import { toDockItems, sameWindows, type DockItem } from '@/screens/Desktop/dock';
import { useGamesStore } from '@/store/games';
import { useNotificationsStore } from '@/store/notifications';
import { selectAnimationsEnabled, useThemeStore } from '@/store/theme';

/** Refresh period while the Shell is in front. */
export const DOCK_POLL_MS = 2000;

/** Open programs, polled while the Shell has the focus and the page is visible; `refresh` polls at once. */
function useOpenWindows(): { windows: OpenWindow[]; refresh: () => void } {
  const [windows, setWindows] = useState<OpenWindow[]>([]);
  const [focused, setFocused] = useState(true);
  const [visible, setVisible] = useState(() => typeof document === 'undefined' || !document.hidden);
  const [nudge, setNudge] = useState(0);

  useEffect(() => events.onKiosk('focus', (p) => setFocused(p.focused)), []);
  useEffect(() => {
    const onVisibility = (): void => setVisible(!document.hidden);
    document.addEventListener('visibilitychange', onVisibility);
    return () => document.removeEventListener('visibilitychange', onVisibility);
  }, []);

  useEffect(() => {
    if (!focused || !visible) {
      return undefined;
    }
    let alive = true;
    let timer: number | undefined;
    const poll = async (): Promise<void> => {
      try {
        const next = await api.kiosk.openWindows();
        if (alive) {
          setWindows((prev) => (sameWindows(prev, next) ? prev : next));
        }
      } catch (e) {
        log.debug('kiosk_open_windows failed', e);
      }
      if (alive) {
        timer = window.setTimeout(() => void poll(), DOCK_POLL_MS);
      }
    };
    void poll();
    return () => {
      alive = false;
      window.clearTimeout(timer);
    };
  }, [focused, visible, nudge]);

  const refresh = useCallback(() => setNudge((n) => n + 1), []);
  return { windows, refresh };
}

/** Generic program glyph, for an exe without an icon (or one that fails to decode). */
const IconWindow = (): JSX.Element => (
  <svg
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth={1.75}
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
    className="h-6 w-6"
  >
    <rect x="3" y="4" width="18" height="16" rx="2.5" />
    <path d="M3 9h18M6.5 6.5h.01M9 6.5h.01" />
  </svg>
);

/** What a dock button shows: the game's cover, else the exe icon, else the generic glyph. */
function DockGlyph({ item }: { item: DockItem }): JSX.Element {
  const { url: cover } = useResolvedAsset(item.cover);
  const [broken, setBroken] = useState<readonly string[]>([]);
  const src = [cover, item.icon].find((s): s is string => Boolean(s) && !broken.includes(s as string)) ?? null;
  if (!src) {
    return (
      <span className="text-muted transition-colors duration-[var(--dur-fast)] group-hover:text-text">
        <IconWindow />
      </span>
    );
  }
  const isCover = src === cover;
  return (
    <img
      src={src}
      alt=""
      draggable={false}
      onError={() => setBroken((b) => [...b, src])}
      className={clsx(
        'pointer-events-none select-none',
        isCover
          ? 'h-10 w-[1.75rem] rounded-[3px] object-cover shadow-[0_0_0_1px_rgb(var(--c-text)/0.14)]'
          : 'h-7 w-7 object-contain',
      )}
    />
  );
}

export function RunningDock(): JSX.Element | null {
  const { t } = useTranslation();
  // The CSS kill switch for motion does not reach framer-motion's JS animations.
  const reducedMotion = useReducedMotion();
  const animations = useThemeStore(selectAnimationsEnabled) && !reducedMotion;
  const running = useGamesStore((s) => s.running);
  const byId = useGamesStore((s) => s.byId);
  const push = useNotificationsStore((s) => s.push);
  const pushError = useNotificationsStore((s) => s.pushError);
  const { windows, refresh } = useOpenWindows();
  const [switching, setSwitching] = useState<string | null>(null);

  // Icons keep their place as the windows change Z order: the previous render's order seeds the next.
  const order = useRef<string[]>([]);
  const items = useMemo(() => toDockItems(windows, running, byId, order.current), [windows, running, byId]);
  useEffect(() => {
    order.current = items.map((i) => i.key);
  }, [items]);

  const activate = async (item: DockItem): Promise<void> => {
    if (switching) {
      return;
    }
    setSwitching(item.key);
    try {
      if (!(await api.kiosk.focusWindow(item.hwnd))) {
        push({
          id: 'dock-switch',
          title: t('desktop.dock.switchFailed'),
          body: t('desktop.dock.switchFailedBody'),
          level: 'warning',
          source: 'local',
        });
        refresh();
      }
    } catch (e) {
      // Closed between two polls: drop it quietly.
      if (isShellApiError(e) && e.code === 'notFound') {
        refresh();
      } else {
        pushError(e, t('desktop.dock.switchFailed'));
      }
    } finally {
      setSwitching(null);
    }
  };

  if (items.length === 0) {
    return null;
  }

  const motionProps = animations
    ? {
        initial: { opacity: 0, scale: 0.6 },
        animate: { opacity: 1, scale: 1 },
        exit: { opacity: 0, scale: 0.6 },
        transition: { duration: 0.2, ease: [0.16, 1, 0.3, 1] },
      }
    : { initial: false as const, transition: { duration: 0 } };

  return (
    <ul aria-label={t('desktop.dock.label')} className="flex items-center gap-1">
      <AnimatePresence initial={false} mode="popLayout">
        {items.map((item, i) => {
          const next = items[i + 1];
          return [
            <motion.li key={item.key} layout={animations ? 'position' : false} {...motionProps}>
              {/* Window titles can run long (a browser tab): one line, capped. */}
              <Tooltip
                content={<span className="block max-w-[24rem] truncate">{item.title}</span>}
                placement="top"
                delayMs={350}
              >
                <button
                  type="button"
                  data-nav="true"
                  aria-label={item.title}
                  aria-busy={switching === item.key || undefined}
                  onClick={() => void activate(item)}
                  className="focus-ring group relative flex h-12 w-12 items-center justify-center rounded-md transition-colors duration-[var(--dur-fast)] hover:bg-text/[0.06] active:bg-text/[0.09]"
                >
                  <DockGlyph item={item} />
                  {/* Running pip, as on the Windows taskbar; the game's is longer. */}
                  <span
                    aria-hidden="true"
                    className={clsx(
                      'absolute bottom-0.5 left-1/2 h-0.5 w-4 -translate-x-1/2 rounded-full bg-accent transition-[transform,opacity] duration-[var(--dur-base)]',
                      item.gameId
                        ? 'opacity-90'
                        : 'scale-x-[0.375] opacity-60 group-hover:scale-x-75 group-hover:opacity-90',
                    )}
                  />
                </button>
              </Tooltip>
            </motion.li>,
            // The game is set apart from the programs.
            item.gameId && next && !next.gameId ? (
              <motion.li
                key={`${item.key}:divider`}
                aria-hidden="true"
                className="mx-1.5 h-6 w-px bg-text/15"
                {...motionProps}
              />
            ) : null,
          ];
        })}
      </AnimatePresence>
    </ul>
  );
}
