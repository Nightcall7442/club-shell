import { useEffect, useState } from 'react';
import clsx from 'clsx';
import { useResolvedAsset } from '@/components/media/GameArtwork';
import { VideoBackground } from '@/components/media/VideoBackground';
import { useThemeStore } from '@/store/theme';
import { useScreenLook } from '@/hooks/useClub';
import { blurFilter, veil } from '@/lib/look';

export interface BackgroundProps {
  /** Wallpaper override (game hero on details screens); falls back to the screen's picture, then `theme.wallpaper`. */
  image?: string | null;
  /** Video override; falls back to `theme.backgroundVideo`. */
  video?: string | null;
  /** Darkening overlay strength 0–1 (default 0.55); the owner's dimming of the screen (D-104) wins. */
  dim?: number;
  /** Whose picture, dimming and blur the owner set (D-104): the lock screen's or the desktop's (default). */
  screen?: 'lock' | 'desktop';
  className?: string;
}

const THEME_BLUR = 'blur(calc(var(--blur) * 0.5))';

/**
 * Full-screen backdrop behind every authenticated screen and the lock screen.
 *
 * With a wallpaper or video (a club's own theme): theme colour → wallpaper (slow pan) → video → gradient/blur veil.
 * Without (the default): the plain theme background.
 */
export function Background({ image, video, dim = 0.55, screen = 'desktop', className }: BackgroundProps): JSX.Element {
  const theme = useThemeStore((s) => s.theme);
  const look = useScreenLook(screen);
  // A game's hero first, then the owner's picture for this screen (the club wallpaper unless set), then the theme's.
  const wallpaper = image ?? look.wallpaperUrl ?? theme.wallpaper ?? null;
  const videoSrc = video ?? theme.backgroundVideo ?? null;
  const animations = theme.animations;
  const { url } = useResolvedAsset(wallpaper);
  const [loaded, setLoaded] = useState(false);
  const blur = blurFilter(look.blur, THEME_BLUR);

  useEffect(() => setLoaded(false), [url]);

  if (!wallpaper && !videoSrc) {
    return <div aria-hidden="true" className={clsx('pointer-events-none fixed inset-0 z-0 bg-bg', className)} />;
  }

  return (
    <div
      aria-hidden="true"
      data-testid={`background-${screen}`}
      data-wallpaper={url ?? ''}
      className={clsx('pointer-events-none fixed inset-0 z-0 overflow-hidden bg-bg', className)}
    >
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
          // The owner's dimming keeps the header and footer readable (`veil`); without it, the screen's own.
          background: veil(
            look.dim,
            `linear-gradient(180deg, rgb(var(--c-bg) / ${dim * 0.6}) 0%, rgb(var(--c-bg) / ${dim}) 55%, rgb(var(--c-bg) / ${Math.min(1, dim + 0.35)}) 100%)`,
          ),
          backdropFilter: blur,
          WebkitBackdropFilter: blur,
        }}
      />
    </div>
  );
}
