import { useCallback, useEffect, useMemo, type ReactNode } from 'react';
import clsx from 'clsx';
import { motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { useLocation, useNavigate } from 'react-router-dom';
import { useGamepad } from '@/hooks/useGamepad';
import { useKioskEvent } from '@/hooks/useTauriEvent';
import { trackScreen } from '@/lib/analytics';
import { blurFilter, veil } from '@/lib/look';
import { Clock } from '@/screens/Lock/LockScreen';
import { useSettingsStore } from '@/store/settings';
import { useThemeStore } from '@/store/theme';
import { ClubBrand } from '@/components/brand/ClubBrand';
import { useResolvedAsset } from '@/components/media/GameArtwork';
import { useScreenLook, useTintStyle } from '@/hooks/useClub';
import { AdsCarousel } from './AdsCarousel';
import { PriceList } from './PriceList';

const WAKE_EVENTS = ['keydown', 'pointerdown', 'touchstart', 'wheel'] as const;
const EMPTY_SLIDES: never[] = [];

/** The idle screen's own veil over the slides, as before the designer (D-105 dims it the owner's way). */
const IDLE_VEIL =
  'linear-gradient(180deg, rgb(var(--c-bg) / 0.7) 0%, rgb(var(--c-bg) / 0.25) 35%, rgb(var(--c-bg) / 0.35) 60%, rgb(var(--c-bg) / 0.9) 100%)';

/** The owner's still picture for the idle screen (D-105), in place of the built-in slides. */
function IdleWallpaper({ src }: { src: string }): JSX.Element | null {
  const { url } = useResolvedAsset(src);
  if (!url) return null;
  return (
    <img
      src={url}
      alt=""
      aria-hidden="true"
      draggable={false}
      data-testid="idle-wallpaper"
      className="absolute inset-0 h-full w-full object-cover"
    />
  );
}

/**
 * Attract screen (route `/idle`, no shell): ads carousel behind the price board, clock and a "touch to start"
 * prompt. Any key, click, touch, wheel or gamepad button returns to `/lock` (which re-routes onward when a session
 * is already open). The owner's designer (D-105) moves the club's mark, hides the clock, the price list or the
 * «free» badge, puts a picture of its own behind it and words the prompt.
 */
export default function IdleScreen(): JSX.Element {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { pathname } = useLocation();
  const look = useScreenLook('idle');
  const tint = useTintStyle();
  const animations = useThemeStore((s) => s.theme.animations);
  const ownItems = useSettingsStore((s) => s.settings.club?.screensaver?.items ?? EMPTY_SLIDES);
  // The wire says `type` is a string: only `video` plays as a clip, anything else shows as a picture.
  const ownSlides = useMemo(
    () => ownItems.map((i) => ({ ...i, type: i.type === 'video' ? ('video' as const) : ('image' as const) })),
    [ownItems],
  );

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
  const at = look.logo.position;
  const brand = <ClubBrand screen="idle" logo={look.logo} />;
  const blur = blurFilter(look.blur, 'none');
  // The owner's slides first (D-87), then the owner's still for this screen (D-105), then the club playlist or the
  // built-in slides.
  const still = ownSlides.length === 0 ? look.wallpaperUrl : null;

  const badge = look.freeBadge && (
    // On a pill: the wallpaper behind the corner can be any colour.
    <span className="hud-label flex items-center gap-2 rounded-full bg-bg/70 px-3 py-1.5 text-success">
      <span aria-hidden="true" className="h-2 w-2 rounded-full bg-success" />
      {t('idle.pcFree')}
    </span>
  );

  let header: ReactNode;
  if (at === 'topCenter') {
    header = (
      <>
        <span aria-hidden="true" />
        {brand}
        <div className="flex justify-end">{badge}</div>
      </>
    );
  } else if (at === 'topRight') {
    header = (
      <>
        {badge}
        <div className="ml-auto flex min-w-0 justify-end">{brand}</div>
      </>
    );
  } else {
    // `ml-auto`: the badge keeps its corner even when the owner hid every part of the mark.
    header = (
      <>
        {at === 'topLeft' && brand}
        {badge && <div className="ml-auto shrink-0">{badge}</div>}
      </>
    );
  }

  return (
    <div className="relative h-full w-full overflow-hidden bg-bg" style={tint} data-testid="idle-screen">
      {/* No "1 of 3" counter here: it sat on top of the tap-to-start prompt and told a passer-by nothing. */}
      {still ? (
        <IdleWallpaper src={still} />
      ) : (
        <AdsCarousel showCounter={false} items={ownSlides.length > 0 ? ownSlides : undefined} />
      )}
      <div
        aria-hidden="true"
        className="pointer-events-none absolute inset-0"
        style={{
          background: veil(look.dim, IDLE_VEIL),
          ...(blur !== 'none' ? { backdropFilter: blur, WebkitBackdropFilter: blur } : {}),
        }}
      />

      <div className="pointer-events-none relative z-10 flex h-full w-full flex-col justify-between px-[calc(var(--gutter)*1.5)] py-[calc(var(--gutter)*1.2)]">
        <motion.header
          initial={{ opacity: 0, y: -16 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ duration, ease: 'easeOut' }}
          className={clsx(
            'items-start gap-[var(--gap)]',
            at === 'topCenter' ? 'grid grid-cols-[1fr_auto_1fr]' : 'flex justify-between',
          )}
        >
          {header}
        </motion.header>

        {/* The clock is the poster: huge dot-matrix time in the middle of the screen. */}
        <motion.div
          initial={{ opacity: 0, scale: 0.96 }}
          animate={{ opacity: 1, scale: 1 }}
          transition={{ duration: animations ? 0.8 : 0, ease: [0.16, 1, 0.3, 1] }}
          className="flex min-h-0 flex-col items-center gap-[clamp(0.75rem,2vh,1.5rem)]"
        >
          {at === 'center' && brand}
          {/* A soft plate of the theme's own colour behind the digits: they stay readable over any slide. */}
          {look.clock && (
            <div className="rounded-[2rem] bg-bg/55 px-[clamp(1.5rem,3vw,3rem)] py-[clamp(0.5rem,1vw,1rem)] backdrop-blur-md">
              <Clock
                className={clsx(
                  'tracking-[0.02em]',
                  // Under a centred logo the digits give it room; on their own they are the poster.
                  at === 'center' ? 'text-[clamp(5rem,min(10vw,14vh),12rem)]' : 'text-[clamp(7rem,13vw,15rem)]',
                )}
              />
            </div>
          )}
        </motion.div>

        <div className="flex items-end justify-between gap-[var(--gap)]">
          <motion.div
            initial={{ opacity: 0, x: -24 }}
            animate={{ opacity: 1, x: 0 }}
            transition={{ duration, ease: 'easeOut', delay: animations ? 0.1 : 0 }}
            className="w-[min(44vw,42rem)]"
          >
            {look.prices && <PriceList />}
            {at === 'bottomLeft' && <div className="flex">{brand}</div>}
          </motion.div>

          <motion.div
            initial={{ opacity: 0, y: 24 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ duration, ease: 'easeOut', delay: animations ? 0.2 : 0 }}
            className="flex flex-col items-end gap-3 pb-[var(--gap)] text-right"
          >
            {at === 'bottomRight' && <div className="mb-[var(--gap)] flex justify-end">{brand}</div>}
            <p
              className="font-display text-[clamp(1.8rem,2.4vw,2.8rem)] font-light tracking-tight text-text"
              data-testid="idle-cta"
            >
              {look.text(look.cta, t('idle.touchToStart'))}
              <span
                aria-hidden="true"
                className="anim-caret ml-2 inline-block h-[0.8em] w-[0.45em] translate-y-[0.08em] bg-accent"
              />
            </p>
            <p className="hud-label text-text/80">{t('idle.subtitle')}</p>
          </motion.div>
        </div>
      </div>
    </div>
  );
}
