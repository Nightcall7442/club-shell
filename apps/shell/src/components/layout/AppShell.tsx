import { useEffect, useRef, useState, type UIEvent } from 'react';
import { AnimatePresence, motion, type Variants } from 'framer-motion';
import { useLocation, useOutlet } from 'react-router-dom';
import { Background } from '@/components/layout/Background';
import { NotificationCenter } from '@/components/layout/NotificationCenter';
import { trackScreen } from '@/lib/analytics';
import { EASE_OUT, sectionIndex, slideDirection, useMotion } from '@/lib/motion';
import { press, tick } from '@/lib/sound';
import { StatusBar, TopBar } from '@/screens/Desktop/HudBars';
import { NAV_ITEMS } from '@/screens/Desktop/NavBar';
import { IdleLeavePrompt } from '@/screens/Desktop/LeaveButton';
import { METRICS_POLL_MS, useSettingsStore } from '@/store/settings';
import { useThemeStore } from '@/store/theme';

/**
 * Authenticated layout, framed like a game's pause menu: the TopBar (club and PC, section tabs, vitals, clock, sound,
 * lock) floats over the routed screen, which scrolls underneath it (top padding = bar height, so a screen may bleed
 * under the bar with a negative margin); the StatusBar (player, time left, balance, controller prompts) sits along the
 * bottom; corner brackets mark the edges of the screen. A screen slides in from the side of its tab (the next section
 * from the right, the previous one from the left) and the old one drifts out the other way; overlays (toasts, banners, staff
 * modal, on-screen keyboard) on top and the themed Background behind everything. Never scrolls horizontally.
 */
export function AppShell(): JSX.Element {
  const location = useLocation();
  // Captured element (not <Outlet/>): the exiting screen must keep rendering its own route during the transition.
  const outlet = useOutlet();
  const animations = useThemeStore((s) => s.theme.animations);
  const moving = useMotion();
  // Which way the screens slide: from the place of the old section in the menu to the new one. Worked out during
  // render (not in an effect), so the very frame that swaps the screens already knows the side.
  const slide = useRef({
    path: location.pathname,
    at: sectionIndex(location.pathname, SECTION_ORDER),
    dir: 0 as -1 | 0 | 1,
  });
  if (slide.current.path !== location.pathname) {
    const at = sectionIndex(location.pathname, SECTION_ORDER);
    slide.current = { path: location.pathname, at, dir: slideDirection(slide.current.at, at) };
  }
  const dir = slide.current.dir;
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

  // The PC the top bar names on every screen (graphics card, vitals): asked once, kept for the app's lifetime. Each
  // call is a no-op once its answer is in. The vitals are asked again whenever no `sys.metrics` event keeps them fresh
  // (no session open: the Agent does not push then).
  useEffect(() => {
    const { loadHardware, ensureMetrics, ensurePcInfo } = useSettingsStore.getState();
    void loadHardware();
    void ensureMetrics();
    void ensurePcInfo();
    const id = window.setInterval(() => void ensureMetrics(), METRICS_POLL_MS);
    return () => window.clearInterval(id);
  }, []);

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
            className="absolute inset-x-0 top-0 z-20 h-[var(--topbar-h)] min-w-0 border-b border-transparent bg-gradient-to-b from-bg/95 via-bg/85 to-transparent transition-[background-color,border-color] duration-[var(--dur-base)] data-[scrolled=true]:border-[color:var(--hairline)] data-[scrolled=true]:bg-bg"
          >
            <TopBar />
          </motion.header>
          <AnimatePresence mode="wait" initial={false} custom={dir}>
            <motion.main
              key={location.pathname}
              id="main"
              tabIndex={-1}
              className="no-scrollbar absolute inset-x-0 top-0 bottom-[var(--vk-h,0px)] overflow-y-auto overflow-x-hidden px-[var(--gutter)] pb-[var(--gutter)] pt-[calc(var(--topbar-h)+var(--gap))] outline-none"
              custom={dir}
              variants={moving ? SCREEN : STILL}
              initial="enter"
              animate="center"
              exit="exit"
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
      <IdleLeavePrompt />
    </div>
  );
}

const SECTION_ORDER = NAV_ITEMS.map((i) => i.to);

/** A screen comes in 32 px from the side of its tab, the old one leaves 20 px the other way; a sub-page just rises. */
const SCREEN: Variants = {
  enter: (dir: number) => ({ opacity: 0, x: dir * 32, y: dir === 0 ? 14 : 0 }),
  center: { opacity: 1, x: 0, y: 0, transition: { duration: 0.34, ease: EASE_OUT } },
  exit: (dir: number) => ({ opacity: 0, x: dir * -20, transition: { duration: 0.14, ease: 'easeIn' } }),
};

const STILL: Variants = {
  enter: { opacity: 1 },
  center: { opacity: 1 },
  exit: { opacity: 1, transition: { duration: 0 } },
};
