import { useEffect } from 'react';
import { AnimatePresence, motion } from 'framer-motion';
import { useLocation, useOutlet } from 'react-router-dom';
import { Background } from '@/components/layout/Background';
import { NotificationCenter } from '@/components/layout/NotificationCenter';
import { VirtualKeyboard } from '@/components/ui/VirtualKeyboard';
import { trackScreen } from '@/lib/analytics';
import { TopBar } from '@/screens/Desktop/TopBar';
import { useThemeStore } from '@/store/theme';

/**
 * Authenticated layout: a translucent TopBar (brand, navigation, session) floating over the routed screen, which
 * scrolls underneath it (top padding = bar height, so screens may bleed under the bar with a negative margin);
 * fade+slide transition per pathname, overlays (toasts, banners, staff modal, on-screen keyboard) on top and the
 * themed Background behind everything. Never scrolls horizontally.
 */
export function AppShell(): JSX.Element {
  const location = useLocation();
  // Captured element (not <Outlet/>): the exiting screen must keep rendering its own route during the transition.
  const outlet = useOutlet();
  const animations = useThemeStore((s) => s.theme.animations);
  const duration = animations ? 0.2 : 0;

  useEffect(() => {
    trackScreen(location.pathname);
  }, [location.pathname]);

  return (
    <div className="relative h-full w-full overflow-hidden">
      <Background />
      <div className="relative z-10 h-full w-full">
        <header className="absolute inset-x-0 top-0 z-20 h-[var(--topbar-h)] min-w-0">
          <TopBar />
        </header>
        <div className="relative h-full min-h-0 min-w-0 overflow-hidden">
          <AnimatePresence mode="wait" initial={false}>
            <motion.main
              key={location.pathname}
              id="main"
              tabIndex={-1}
              className="no-scrollbar absolute inset-x-0 top-0 bottom-[var(--vk-h,0px)] overflow-y-auto overflow-x-hidden px-[var(--gutter)] pb-[var(--gap)] pt-[calc(var(--topbar-h)+var(--gap))] outline-none"
              initial={{ opacity: 0, y: 16 }}
              animate={{ opacity: 1, y: 0 }}
              exit={{ opacity: 0, y: -10 }}
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
