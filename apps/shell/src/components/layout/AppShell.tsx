import { useEffect } from 'react';
import { AnimatePresence, motion } from 'framer-motion';
import { useLocation, useOutlet } from 'react-router-dom';
import { Background } from '@/components/layout/Background';
import { NotificationCenter } from '@/components/layout/NotificationCenter';
import { VirtualKeyboard } from '@/components/ui/VirtualKeyboard';
import { trackScreen } from '@/lib/analytics';
import { press, tick } from '@/lib/sound';
import { Sidebar } from '@/screens/Desktop/Sidebar';
import { useThemeStore } from '@/store/theme';

/**
 * Authenticated layout: a fixed Sidebar (brand, navigation, session, player) on the left and the routed screen to
 * its right, which scrolls on its own; a short fade per pathname, overlays (toasts, banners, staff modal, on-screen
 * keyboard) on top and the themed Background behind everything. Never scrolls horizontally.
 */
export function AppShell(): JSX.Element {
  const location = useLocation();
  // Captured element (not <Outlet/>): the exiting screen must keep rendering its own route during the transition.
  const outlet = useOutlet();
  const animations = useThemeStore((s) => s.theme.animations);
  const duration = animations ? 0.15 : 0;

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
      <div className="relative z-10 flex h-full w-full">
        <header className="h-full w-[var(--sidebar-w)] shrink-0">
          <Sidebar />
        </header>
        <div className="relative h-full min-h-0 min-w-0 flex-1 overflow-hidden">
          <AnimatePresence mode="wait" initial={false}>
            <motion.main
              key={location.pathname}
              id="main"
              tabIndex={-1}
              className="no-scrollbar absolute inset-x-0 top-0 bottom-[var(--vk-h,0px)] overflow-y-auto overflow-x-hidden px-[var(--gutter)] py-[var(--gutter)] outline-none"
              initial={{ opacity: 0 }}
              animate={{ opacity: 1 }}
              exit={{ opacity: 0 }}
              transition={{ duration, ease: 'easeOut' }}
            >
              {outlet}
            </motion.main>
          </AnimatePresence>
        </div>
      </div>
      <NotificationCenter />
      <VirtualKeyboard />
    </div>
  );
}
