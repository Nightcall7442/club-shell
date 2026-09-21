import { useEffect, useRef, useState } from 'react';
import clsx from 'clsx';
import { assetUrl, events } from '@/lib/tauri';
import { useGamesStore } from '@/store/games';
import { useThemeStore } from '@/store/theme';

export interface VideoBackgroundProps {
  /** Video URL or ProgramData-relative path; resolved through `assetUrl`. */
  src: string;
  poster?: string | null;
  /** 0–1 (default 1). */
  opacity?: number;
  /** Force pause (e.g. while a modal video plays). */
  paused?: boolean;
  className?: string;
}

/**
 * Muted looping background video. Pauses while the page is hidden, the shell lost focus, a game is running,
 * or `paused` is set; renders nothing when the theme disables animations (the wallpaper stays).
 */
export function VideoBackground({
  src,
  poster,
  opacity = 1,
  paused = false,
  className,
}: VideoBackgroundProps): JSX.Element | null {
  const video = useRef<HTMLVideoElement>(null);
  const [url, setUrl] = useState<string | null>(null);
  const [posterUrl, setPosterUrl] = useState<string | undefined>(undefined);
  const [hidden, setHidden] = useState(() => typeof document !== 'undefined' && document.hidden);
  const [focused, setFocused] = useState(true);
  const [ready, setReady] = useState(false);
  const animations = useThemeStore((s) => s.theme.animations);
  const gameRunning = useGamesStore((s) => s.running.length > 0);

  useEffect(() => {
    let active = true;
    setReady(false);
    assetUrl(src).then(
      (u) => {
        if (active) {
          setUrl(u);
        }
      },
      () => {
        if (active) {
          setUrl(null);
        }
      },
    );
    return () => {
      active = false;
    };
  }, [src]);

  useEffect(() => {
    let active = true;
    if (!poster) {
      setPosterUrl(undefined);
      return undefined;
    }
    assetUrl(poster).then(
      (u) => {
        if (active) {
          setPosterUrl(u);
        }
      },
      () => undefined,
    );
    return () => {
      active = false;
    };
  }, [poster]);

  useEffect(() => {
    const onVisibility = (): void => setHidden(document.hidden);
    document.addEventListener('visibilitychange', onVisibility);
    const off = events.onKiosk('focus', (p) => setFocused(p.focused));
    return () => {
      document.removeEventListener('visibilitychange', onVisibility);
      off();
    };
  }, []);

  const shouldPlay = animations && !paused && !hidden && focused && !gameRunning;

  useEffect(() => {
    const el = video.current;
    if (!el) {
      return;
    }
    if (shouldPlay) {
      el.play().catch(() => undefined);
    } else {
      el.pause();
    }
  }, [shouldPlay, url]);

  if (!animations || !url) {
    return null;
  }

  return (
    <video
      ref={video}
      src={url}
      poster={posterUrl}
      muted
      loop
      autoPlay
      playsInline
      preload="auto"
      disablePictureInPicture
      aria-hidden="true"
      onCanPlay={() => setReady(true)}
      className={clsx(
        'absolute inset-0 h-full w-full object-cover transition-opacity duration-[var(--dur-slow)]',
        ready ? '' : 'opacity-0',
        className,
      )}
      style={ready ? { opacity } : undefined}
    />
  );
}
