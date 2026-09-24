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
      {/* No "1 of 3" counter here: it sat on top of the tap-to-start prompt and told a passer-by nothing. */}
      <AdsCarousel showCounter={false} />
      <div
        aria-hidden="true"
        className="pointer-events-none absolute inset-0 bg-[linear-gradient(180deg,rgb(var(--c-bg)/0.7)_0%,rgb(var(--c-bg)/0.25)_35%,rgb(var(--c-bg)/0.35)_60%,rgb(var(--c-bg)/0.9)_100%)]"
      />
      <div aria-hidden="true" className="hud-grid pointer-events-none absolute inset-0" />
      <div
        aria-hidden="true"
        className="hud-brackets pointer-events-none absolute inset-0 opacity-70 [--brk-inset:20px] [--brk-size:28px]"
      />

      <div className="pointer-events-none relative z-10 flex h-full w-full flex-col justify-between px-[calc(var(--gutter)*1.5)] py-[calc(var(--gutter)*1.2)]">
        <motion.header
          initial={{ opacity: 0, y: -16 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ duration, ease: 'easeOut' }}
          className="flex items-start justify-between gap-[var(--gap)]"
        >
          <div className="flex min-w-0 items-center gap-4">
            <span aria-hidden="true" className="h-8 w-8 shrink-0 rotate-45 border border-accent/70" />
            <div className="min-w-0">
              <p className="truncate font-display text-3xl font-light tracking-tight text-text">{t('idle.clubName')}</p>
              {pcName && <p className="hud-label mt-2">{t('idle.pcName', { name: pcName, zone: pcZone })}</p>}
            </div>
          </div>
          <span className="hud-label flex items-center gap-2 text-success">
            <span aria-hidden="true" className="h-2 w-2 rounded-full bg-success" />
            {t('idle.pcFree')}
          </span>
        </motion.header>

        {/* The clock is the poster: huge dot-matrix time in the middle of the screen. */}
        <motion.div
          initial={{ opacity: 0, scale: 0.96 }}
          animate={{ opacity: 1, scale: 1 }}
          transition={{ duration: animations ? 0.8 : 0, ease: [0.16, 1, 0.3, 1] }}
          className="flex flex-col items-center gap-3"
        >
          <Clock className="text-[clamp(7rem,13vw,15rem)] tracking-[0.02em]" />
        </motion.div>

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
            className="flex flex-col items-end gap-3 pb-[var(--gap)] text-right"
          >
            <p className="font-display text-[clamp(1.8rem,2.4vw,2.8rem)] font-light tracking-tight text-text">
              {t('idle.touchToStart')}
              <span
                aria-hidden="true"
                className="anim-caret ml-2 inline-block h-[0.8em] w-[0.45em] translate-y-[0.08em] bg-accent"
              />
            </p>
            <p className="hud-label">{t('idle.subtitle')}</p>
          </motion.div>
        </div>
      </div>
    </div>
  );
}
