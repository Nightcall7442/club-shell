import { useCallback, useEffect } from 'react';
import { motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { useLocation, useNavigate } from 'react-router-dom';
import { useGamepad } from '@/hooks/useGamepad';
import { useKioskEvent } from '@/hooks/useTauriEvent';
import { trackScreen } from '@/lib/analytics';
import { Clock } from '@/screens/Lock/LockScreen';
import { useSettingsStore } from '@/store/settings';
import { useThemeStore } from '@/store/theme';
import { AdsCarousel } from './AdsCarousel';
import { PriceList } from './PriceList';

const WAKE_EVENTS = ['keydown', 'pointerdown', 'touchstart', 'wheel'] as const;

/**
 * Attract screen (route `/idle`, no shell): ads carousel behind the price board, clock and a "touch to start"
 * prompt. Any key, click, touch, wheel or gamepad button returns to `/lock` (which re-routes onward when a session
 * is already open).
 */
export default function IdleScreen(): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { pathname } = useLocation();
  const pcName = useSettingsStore((s) => s.pcInfo?.pc.name ?? null);
  const pcZone = useSettingsStore((s) => s.pcInfo?.pc.zone ?? '');
  const animations = useThemeStore((s) => s.theme.animations);

  const wake = useCallback(() => navigate('/lock', { replace: true }), [navigate]);

  useEffect(() => {
    trackScreen(pathname);
  }, [pathname]);

  useEffect(() => {
    for (const ev of WAKE_EVENTS) {
      window.addEventListener(ev, wake, { passive: true });
    }
    return () => {
      for (const ev of WAKE_EVENTS) {
        window.removeEventListener(ev, wake);
      }
    };
  }, [wake]);

  useGamepad({
    onActivate: () => {
      wake();
      return true;
    },
    onBack: wake,
    onMenu: wake,
    onButton: (_name, pressed) => {
      if (pressed) {
        wake();
      }
    },
  });

  useKioskEvent('idle', (p) => {
    if (!p.idle) {
      wake();
    }
  });

  const duration = animations ? 0.4 : 0;

  return (
    <div className="relative h-full w-full overflow-hidden bg-bg">
      <AdsCarousel />
      <div
        aria-hidden="true"
        className="pointer-events-none absolute inset-0 bg-[linear-gradient(180deg,rgb(var(--c-bg)/0.55)_0%,transparent_30%,transparent_55%,rgb(var(--c-bg)/0.85)_100%)]"
      />

      <div className="pointer-events-none relative z-10 flex h-full w-full flex-col justify-between px-[var(--gutter)] py-[var(--gap)]">
        <motion.header
          initial={{ opacity: 0, y: -16 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ duration, ease: 'easeOut' }}
          className="flex items-start justify-between gap-[var(--gap)]"
        >
          <div className="min-w-0">
            <p className="text-glow truncate text-3xl font-black uppercase tracking-[0.25em] text-text">
              {t('idle.clubName')}
            </p>
            <p className="mt-1 text-lg text-text/80">
              {pcName ? t('idle.pcName', { name: pcName, zone: pcZone }) : t('idle.pcFree')}
            </p>
          </div>
          <Clock className="text-[var(--fs-display)]" />
        </motion.header>

        <div className="flex items-end justify-between gap-[var(--gap)]">
          <motion.div
            initial={{ opacity: 0, x: -24 }}
            animate={{ opacity: 1, x: 0 }}
            transition={{ duration, ease: 'easeOut', delay: animations ? 0.1 : 0 }}
            className="w-[min(44vw,42rem)]"
          >
            <PriceList />
          </motion.div>

          <motion.div
            initial={{ opacity: 0, y: 24 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ duration, ease: 'easeOut', delay: animations ? 0.2 : 0 }}
            className="flex flex-col items-center gap-4 pb-[var(--gap)] text-center"
          >
            <span aria-hidden="true" className="relative flex h-24 w-24 items-center justify-center">
              <span
                className={
                  animations
                    ? 'absolute inset-0 animate-pulse-glow rounded-full bg-primary/20'
                    : 'absolute inset-0 rounded-full bg-primary/20'
                }
              />
              <span className="glass-strong border-glow relative flex h-16 w-16 items-center justify-center rounded-full text-primary">
                <svg
                  viewBox="0 0 24 24"
                  className="h-8 w-8"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                >
                  <path d="M9 11V5a2 2 0 0 1 4 0v6M13 11V8a2 2 0 0 1 4 0v4M17 12a2 2 0 0 1 4 0v3a6 6 0 0 1-6 6h-2a6 6 0 0 1-5-2.7L4 13.5A1.8 1.8 0 0 1 6.8 11l2.2 2.5" />
                </svg>
              </span>
            </span>
            <p className="text-glow text-3xl font-black text-text">{t('idle.touchToStart')}</p>
            <p className="text-lg text-text/80">{t('idle.subtitle')}</p>
          </motion.div>
        </div>
      </div>
    </div>
  );
}
