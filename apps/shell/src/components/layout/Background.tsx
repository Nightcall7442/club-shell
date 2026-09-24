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

/**
 * Full-screen backdrop behind every authenticated screen and the lock screen.
 *
 * With a wallpaper or video (a club's own theme): theme colour → wallpaper (slow pan) → video → gradient/blur veil.
 * Without (the default): the theme background with a faint blueprint grid that fades out towards the edges.
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
      <div aria-hidden="true" className={clsx('pointer-events-none fixed inset-0 z-0 bg-bg', className)}>
        <div className="hud-grid absolute inset-0" />
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
    </div>
  );
}
