import { useEffect, useState, type UIEvent } from 'react';
import { AnimatePresence, motion } from 'framer-motion';
import { useLocation, useOutlet } from 'react-router-dom';
import { Background } from '@/components/layout/Background';
import { NotificationCenter } from '@/components/layout/NotificationCenter';
import { VirtualKeyboard } from '@/components/ui/VirtualKeyboard';
import { trackScreen } from '@/lib/analytics';
import { press, tick } from '@/lib/sound';
import { StatusBar, TopBar } from '@/screens/Desktop/HudBars';
import { useThemeStore } from '@/store/theme';

/**
 * Authenticated layout, framed like a game's pause menu: the TopBar (club, section tabs, clock, lock) floats over
 * the routed screen, which scrolls underneath it (top padding = bar height, so a screen may bleed under the bar with
 * a negative margin); the StatusBar (player, time left, balance, controller prompts) sits along the bottom; corner
 * brackets mark the edges of the screen. A short fade per pathname, overlays (toasts, banners, staff modal,
 * on-screen keyboard) on top and the themed Background behind everything. Never scrolls horizontally.
 */
export function AppShell(): JSX.Element {
  const location = useLocation();
  // Captured element (not <Outlet/>): the exiting screen must keep rendering its own route during the transition.
  const outlet = useOutlet();
  const animations = useThemeStore((s) => s.theme.animations);
  const duration = animations ? 0.15 : 0;
  // The top bar floats over the screen; once the screen scrolls under it, it turns solid so nothing reads through.
  const [scrolled, setScrolled] = useState(false);
  useEffect(() => setScrolled(false), [location.pathname]);
  const onScroll = (e: UIEvent<HTMLDivElement>): void => {
    if (e.target instanceof HTMLElement && e.target.id === 'main') {
      setScrolled(e.target.scrollTop > 8);
    }
  };

  useEffect(() => {
    trackScreen(location.pathname);
  }, [location.pathname]);

  // UI sounds: one tick per `data-nav` element entered by pointer or keyboard, a press on activation.
  useEffect(() => {
    let last: Element | null = null;
    const enter = (target: EventTarget | null): void => {
      const el = target instanceof Element ? target.closest('[data-nav]') : null;
      if (el && el !== last) {
        last = el;
        tick();
      }
    };
    const onOver = (e: PointerEvent): void => enter(e.target);
    const onFocus = (e: FocusEvent): void => enter(e.target);
    const onDown = (e: PointerEvent): void => {
      if (e.target instanceof Element && e.target.closest('[data-nav]')) {
        press();
      }
    };
    document.addEventListener('pointerover', onOver, { passive: true });
    document.addEventListener('focusin', onFocus);
    document.addEventListener('pointerdown', onDown, { passive: true });
    return () => {
      document.removeEventListener('pointerover', onOver);
      document.removeEventListener('focusin', onFocus);
      document.removeEventListener('pointerdown', onDown);
    };
  }, []);

  return (
    <div className="relative h-full w-full overflow-hidden">
      <Background />
      <div className="relative z-10 flex h-full w-full flex-col">
        <div className="relative min-h-0 flex-1 overflow-hidden" onScrollCapture={onScroll}>
          {/* Boot: after sign-in the HUD powers on — the bars slide in. */}
          <motion.header
            initial={animations ? { y: '-100%', opacity: 0 } : false}
            animate={{ y: 0, opacity: 1 }}
            transition={{ duration: 0.6, delay: 0.15, ease: [0.16, 1, 0.3, 1] }}
            data-scrolled={scrolled}
            className="absolute inset-x-0 top-0 z-20 h-[var(--topbar-h)] min-w-0 border-b border-transparent bg-gradient-to-b from-bg/90 via-bg/50 to-transparent transition-[background-color,border-color] duration-[var(--dur-base)] data-[scrolled=true]:border-[color:var(--hairline)] data-[scrolled=true]:bg-bg"
          >
            <TopBar />
          </motion.header>
          <AnimatePresence mode="wait" initial={false}>
            <motion.main
              key={location.pathname}
              id="main"
              tabIndex={-1}
              className="no-scrollbar absolute inset-x-0 top-0 bottom-[var(--vk-h,0px)] overflow-y-auto overflow-x-hidden px-[var(--gutter)] pb-[var(--gutter)] pt-[calc(var(--topbar-h)+var(--gap))] outline-none"
              initial={{ opacity: 0 }}
              animate={{ opacity: 1 }}
              exit={{ opacity: 0 }}
              transition={{ duration, ease: 'easeOut' }}
            >
              {outlet}
            </motion.main>
          </AnimatePresence>
        </div>
        <motion.footer
          className="h-[var(--statusbar-h)] shrink-0"
          initial={animations ? { y: '100%', opacity: 0 } : false}
          animate={{ y: 0, opacity: 1 }}
          transition={{ duration: 0.6, delay: 0.25, ease: [0.16, 1, 0.3, 1] }}
        >
          <StatusBar />
        </motion.footer>
      </div>
      <NotificationCenter />
      <VirtualKeyboard />
    </div>
  );
}
