import { useEffect } from 'react';
import { AnimatePresence, motion } from 'framer-motion';
import { useLocation, useOutlet } from 'react-router-dom';
import { Background } from '@/components/layout/Background';
import { NotificationCenter } from '@/components/layout/NotificationCenter';
import { VirtualKeyboard } from '@/components/ui/VirtualKeyboard';
import { trackScreen } from '@/lib/analytics';
import { Sidebar } from '@/screens/Desktop/Sidebar';
import { TopBar } from '@/screens/Desktop/TopBar';
import { useThemeStore } from '@/store/theme';

/**
 * Authenticated layout: TopBar across the top, Sidebar on the left, the routed screen in the remaining cell with a
 * fade+slide transition per pathname, overlays (toasts, banners, staff modal, on-screen keyboard) on top and the
 * themed Background behind everything. Never scrolls horizontally; only the screen cell scrolls vertically.
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
      <div className="relative z-10 grid h-full w-full grid-cols-[auto_1fr] grid-rows-[var(--topbar-h)_1fr]">
        <header className="col-span-2 row-start-1 min-w-0">
          <TopBar />
        </header>
        <aside className="col-start-1 row-start-2 min-h-0">
          <Sidebar />
        </aside>
        <div className="relative col-start-2 row-start-2 min-h-0 min-w-0 overflow-hidden">
          <AnimatePresence mode="wait" initial={false}>
            <motion.main
              key={location.pathname}
              id="main"
              tabIndex={-1}
              className="no-scrollbar absolute inset-x-0 top-0 bottom-[var(--vk-h,0px)] overflow-y-auto overflow-x-hidden px-[var(--gutter)] py-[var(--gap)] outline-none"
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
