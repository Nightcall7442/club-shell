import { useCallback, useEffect, useMemo, useState } from 'react';
import clsx from 'clsx';
import { AnimatePresence, motion } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import { useResolvedAsset } from '@/components/media/GameArtwork';
import { Skeleton } from '@/components/ui/Skeleton';
import { assetUrl, type ShellConfig } from '@/lib/tauri';
import { useSettingsStore } from '@/store/settings';
import { useThemeStore } from '@/store/theme';

/** One playlist entry of `shell.json → ads.playlist`. */
export type AdItem = ShellConfig['ads']['playlist'][number];

export interface AdsCarouselProps {
  /** Playlist override; defaults to `shellConfig.ads.playlist`, then to the built-in promo slides. */
  items?: AdItem[];
  /** `fixed inset-0` (secondary-monitor `/ads` window) instead of filling the parent. */
  fullscreen?: boolean;
  /** Show the "n of m" counter and dots (default `true`). */
  showCounter?: boolean;
  className?: string;
}

export type Slide = { key: string; kind: 'media'; item: AdItem } | { key: string; kind: 'promo'; n: 1 | 2 | 3 };

const EMPTY: AdItem[] = [];
const PROMO_SECONDS = 8;
const MIN_SECONDS = 3;
/** Fallback for videos that never fire `ended` (broken source). */
const VIDEO_FALLBACK_SECONDS = 60;

const PROMO_GRADIENTS: Record<1 | 2 | 3, string> = {
  1: 'radial-gradient(120% 90% at 15% 10%, rgb(var(--c-primary) / 0.7) 0%, transparent 60%), radial-gradient(90% 80% at 90% 90%, rgb(var(--c-accent) / 0.5) 0%, transparent 60%), rgb(var(--c-bg))',
  2: 'radial-gradient(110% 90% at 85% 15%, rgb(var(--c-accent) / 0.55) 0%, transparent 60%), radial-gradient(90% 90% at 10% 90%, rgb(var(--c-primary) / 0.6) 0%, transparent 60%), rgb(var(--c-bg))',
  3: 'radial-gradient(100% 100% at 50% 0%, rgb(var(--c-success) / 0.45) 0%, transparent 60%), radial-gradient(90% 80% at 100% 100%, rgb(var(--c-primary) / 0.55) 0%, transparent 60%), rgb(var(--c-bg))',
};

/** Playlist → slides; an empty playlist yields the three built-in promo slides. */
export function toSlides(items: readonly AdItem[]): Slide[] {
  if (items.length === 0) {
    return ([1, 2, 3] as const).map((n) => ({ key: `promo-${n}`, kind: 'promo', n }));
  }
  return items.map((item, i) => ({ key: `media-${i}-${item.url}`, kind: 'media', item }));
}

interface SlideViewProps {
  slide: Slide;
  animations: boolean;
  onEnded: () => void;
}

function MediaSlide({
  item,
  animations,
  onEnded,
}: {
  item: AdItem;
  animations: boolean;
  onEnded: () => void;
}): JSX.Element {
  const { t } = useTranslation();
  const { url, error } = useResolvedAsset(item.url);
  const [failed, setFailed] = useState(false);

  useEffect(() => setFailed(false), [url]);

  if (error || failed) {
    return (
      <div
        role="img"
        aria-label={t('idle.ads')}
        className="absolute inset-0"
        style={{ background: PROMO_GRADIENTS[1] }}
      />
    );
  }
  if (!url) {
    return <Skeleton variant="rect" className="absolute inset-0 h-full w-full rounded-none" />;
  }
  if (item.type === 'video') {
    return (
      <video
        src={url}
        autoPlay
        muted
        playsInline
        preload="auto"
        disablePictureInPicture
        aria-label={t('idle.ads')}
        onEnded={onEnded}
        onError={() => setFailed(true)}
        className="absolute inset-0 h-full w-full object-cover"
      />
    );
  }
  return (
    <img
      src={url}
      alt={t('idle.ads')}
      draggable={false}
      decoding="async"
      onError={() => setFailed(true)}
      className={clsx('absolute inset-0 h-full w-full object-cover', animations && 'anim-bg-pan')}
    />
  );
}

function PromoSlide({ n }: { n: 1 | 2 | 3 }): JSX.Element {
  const { t } = useTranslation();
  return (
    <div
      className="absolute inset-0 flex items-center justify-center p-[var(--gutter)]"
      style={{ background: PROMO_GRADIENTS[n] }}
    >
      <div className="max-w-[60vw] text-center">
        <p className="mb-[var(--gap)] text-lg font-bold text-accent">{t('idle.promo')}</p>
        <h2 className="font-display text-[length:var(--fs-display)] font-normal leading-[1.05] text-text tracking-tight">
          {t(`idle.promoSlides.s${n}.title`)}
        </h2>
        <p className="mt-[var(--gap)] text-2xl text-text/80">{t(`idle.promoSlides.s${n}.body`)}</p>
      </div>
    </div>
  );
}

function SlideView({ slide, animations, onEnded }: SlideViewProps): JSX.Element {
  return slide.kind === 'media' ? (
    <MediaSlide item={slide.item} animations={animations} onEnded={onEnded} />
  ) : (
    <PromoSlide n={slide.n} />
  );
}

/**
 * Auto-advancing full-bleed ad/promo carousel: images (Ken Burns pan) and videos from the configured playlist, or
 * three built-in promo slides. Crossfades between slides; the next image is preloaded.
 */
export function AdsCarousel({
  items,
  fullscreen = false,
  showCounter = true,
  className,
}: AdsCarouselProps): JSX.Element {
  const { t } = useTranslation();
  const playlist = useSettingsStore((s) => s.shellConfig?.ads.playlist);
  const animations = useThemeStore((s) => s.theme.animations);
  const source = items ?? playlist ?? EMPTY;
  const slides = useMemo(() => toSlides(source), [source]);
  const [index, setIndex] = useState(0);

  const safeIndex = index % slides.length;
  const slide = slides[safeIndex] ?? slides[0];
  const next = useCallback(() => setIndex((i) => (i + 1) % slides.length), [slides.length]);

  useEffect(() => setIndex(0), [slides]);

  // Auto-advance: promo/image after their duration; video on `ended` (with a long safety timer).
  useEffect(() => {
    if (!slide || slides.length < 2) {
      return undefined;
    }
    let seconds: number;
    if (slide.kind === 'promo') {
      seconds = PROMO_SECONDS;
    } else if (slide.item.type === 'video') {
      seconds = slide.item.durationSec > 0 ? slide.item.durationSec : VIDEO_FALLBACK_SECONDS;
    } else {
      seconds = Math.max(MIN_SECONDS, slide.item.durationSec);
    }
    const id = setTimeout(next, seconds * 1000);
    return () => clearTimeout(id);
  }, [slide, slides.length, next]);

  // Preload the next image so the crossfade never shows a blank frame.
  useEffect(() => {
    const upcoming = slides[(safeIndex + 1) % slides.length];
    if (!upcoming || upcoming.kind !== 'media' || upcoming.item.type !== 'image') {
      return undefined;
    }
    let active = true;
    assetUrl(upcoming.item.url).then(
      (u) => {
        if (active) {
          const img = new Image();
          img.src = u;
        }
      },
      () => undefined,
    );
    return () => {
      active = false;
    };
  }, [slides, safeIndex]);

  if (!slide) {
    return <div className={clsx(fullscreen ? 'fixed inset-0' : 'absolute inset-0', 'bg-bg', className)} />;
  }

  return (
    <div
      role="region"
      aria-label={t('idle.ads')}
      className={clsx(fullscreen ? 'fixed inset-0 z-0' : 'absolute inset-0', 'overflow-hidden bg-bg', className)}
    >
      <AnimatePresence initial={false}>
        <motion.div
          key={slide.key}
          className="absolute inset-0"
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0 }}
          transition={{ duration: animations ? 0.8 : 0, ease: 'easeInOut' }}
        >
          <SlideView slide={slide} animations={animations} onEnded={next} />
        </motion.div>
      </AnimatePresence>

      {showCounter && slides.length > 1 && (
        <div className="pointer-events-none absolute bottom-[var(--gap)] right-[var(--gutter)] flex items-center gap-3 text-sm text-text/80">
          <span className="tnum glass rounded-full px-3 py-1">
            {t('idle.adOf', { index: safeIndex + 1, total: slides.length })}
          </span>
          <span aria-hidden="true" className="flex items-center gap-1.5">
            {slides.map((s, i) => (
              <span
                key={s.key}
                className={clsx(
                  'h-2 rounded-full transition-all duration-[var(--dur-base)]',
                  i === safeIndex ? 'w-6 bg-primary' : 'w-2 bg-text/40',
                )}
              />
            ))}
          </span>
        </div>
      )}
    </div>
  );
}

export default AdsCarousel;
