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

/** Full-screen backdrop: theme colour → wallpaper (slow pan) → video → gradient/blur veil → primary glow. */
export function Background({ image, video, dim = 0.55, className }: BackgroundProps): JSX.Element {
  const theme = useThemeStore((s) => s.theme);
  const wallpaper = image ?? theme.wallpaper ?? null;
  const videoSrc = video ?? theme.backgroundVideo ?? null;
  const animations = theme.animations;
  const { url } = useResolvedAsset(wallpaper);
  const [loaded, setLoaded] = useState(false);

  useEffect(() => setLoaded(false), [url]);

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
