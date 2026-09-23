import { useEffect, useState } from 'react';
import clsx from 'clsx';
import { useResolvedAsset } from '@/components/media/GameArtwork';
import { VideoBackground } from '@/components/media/VideoBackground';
import { useThemeStore } from '@/store/theme';

export interface BackgroundProps {
  /** Wallpaper override (game hero on details screens); falls back to `theme.wallpaper`. */
  image?: string | null;
  /** Video override; falls back to `theme.backgroundVideo`. */
  video?: string | null;
  /** Darkening overlay strength 0–1 (default 0.55). */
  dim?: number;
  className?: string;
}

/** `"R G B"` of the light cast on the ambient backdrop: the last game Home featured, else the theme accent. */
const AMBIENT = 'var(--ambient, var(--c-accent))';

/**
 * Publishes the light the ambient backdrop is lit with, as an `"R G B"` triplet (from `useImageTint`). A CSS
 * variable rather than state: every screen's backdrop follows it without re-rendering, and the colour glides because
 * the glows transition `background-color`.
 */
export function setAmbientLight(rgb: string | null): void {
  if (rgb) {
    document.documentElement.style.setProperty('--ambient', rgb);
  }
}

/**
 * Full-screen backdrop behind every authenticated screen and the lock screen.
 *
 * With a wallpaper or video (a club's own theme): theme colour → wallpaper (slow pan) → video → gradient/blur veil.
 * Without (the default): Onyx black, lit from the top left by the colour of the game last featured on Home and
 * finished with film grain, so the inner screens carry the mood of the hero instead of a stock picture showing
 * through every glass panel.
 */
export function Background({ image, video, dim = 0.55, className }: BackgroundProps): JSX.Element {
  const theme = useThemeStore((s) => s.theme);
  const wallpaper = image ?? theme.wallpaper ?? null;
  const videoSrc = video ?? theme.backgroundVideo ?? null;
  const animations = theme.animations;
  const { url } = useResolvedAsset(wallpaper);
  const [loaded, setLoaded] = useState(false);

  useEffect(() => setLoaded(false), [url]);

  if (!wallpaper && !videoSrc) {
    return (
      <div
        aria-hidden="true"
        className={clsx('pointer-events-none fixed inset-0 z-0 overflow-hidden bg-bg', className)}
      >
        <div
          className="absolute -left-[20%] -top-[45%] h-[90vh] w-[75vw] rounded-full blur-[160px] transition-[background-color] duration-1000"
          style={{ backgroundColor: `rgb(${AMBIENT} / 0.16)` }}
        />
        <div
          className="absolute -bottom-[50%] -right-[20%] h-[80vh] w-[65vw] rounded-full blur-[180px] transition-[background-color] duration-1000"
          style={{ backgroundColor: `rgb(${AMBIENT} / 0.07)` }}
        />
        <div className="film-grain" />
      </div>
    );
  }

  return (
    <div aria-hidden="true" className={clsx('pointer-events-none fixed inset-0 z-0 overflow-hidden bg-bg', className)}>
      {url && (
        <img
          src={url}
          alt=""
          draggable={false}
          onLoad={() => setLoaded(true)}
          className={clsx(
            'absolute inset-0 h-full w-full object-cover transition-opacity duration-[var(--dur-slow)]',
            animations && 'anim-bg-pan',
            loaded ? 'opacity-100' : 'opacity-0',
          )}
        />
      )}
      {videoSrc && <VideoBackground src={videoSrc} poster={wallpaper} opacity={0.9} />}
      <div
        className="absolute inset-0"
        style={{
          background: `linear-gradient(180deg, rgb(var(--c-bg) / ${dim * 0.6}) 0%, rgb(var(--c-bg) / ${dim}) 55%, rgb(var(--c-bg) / ${Math.min(1, dim + 0.35)}) 100%)`,
          backdropFilter: 'blur(calc(var(--blur) * 0.5))',
          WebkitBackdropFilter: 'blur(calc(var(--blur) * 0.5))',
        }}
      />
      <div className="absolute -left-[10%] -top-[20%] h-[60vh] w-[60vw] rounded-full bg-primary/[0.04] blur-[120px]" />
      <div className="absolute -bottom-[25%] -right-[10%] h-[55vh] w-[50vw] rounded-full bg-accent/[0.05] blur-[140px]" />
    </div>
  );
}
