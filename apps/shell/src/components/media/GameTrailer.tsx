/**
 * A game's trailer (`Game.videoUrl`) over its big art. It waits until the player has stayed on the game for
 * {@link TRAILER_DELAY_MS}, so browsing past games loads no video, then fades in over the art: muted, looped, cropped
 * like the art (`object-cover` at the element's own size: no poster, no filters, so a 1080p file stays sharp; the art
 * under it shows until the video's first frame is drawn). Render it right after the `GameArtwork` and before the hero's gradients, so the text stays readable.
 *
 * There is no video element at all while a game runs or launches (the game gets the GPU, memory and network), with the
 * theme's animations off or the system's reduced motion; it pauses while the shell is hidden or not in front. A trailer
 * that fails to load leaves the art as it was and is not tried again for a while.
 *
 * From a club server the trailer is the server's own copy (D-97) and `fallback` the link it came from: when the copy
 * does not play (the server is down, the file is gone), the original link is tried once before giving up.
 */
import { useEffect, useRef, useState } from 'react';
import clsx from 'clsx';
import { useReducedMotion } from 'framer-motion';
import { selectGameMode, useGamesStore } from '@/store/games';
import { selectAnimationsEnabled, useThemeStore } from '@/store/theme';
import { useResolvedAsset } from './GameArtwork';
import { useShellVisible, useTrailerStore } from './playback';

/** Time on one game before its trailer starts. */
export const TRAILER_DELAY_MS = 1500;

/** A trailer that failed (dead link, not a video, no internet) stays off this long instead of failing on every visit. */
const RETRY_AFTER_MS = 10 * 60_000;

/** When each trailer URL last failed, for this run of the shell. */
const failures = new Map<string, number>();

function recentlyFailed(url: string): boolean {
  const at = failures.get(url);
  return at !== undefined && Date.now() - at < RETRY_AFTER_MS;
}

export interface GameTrailerProps {
  /** `Game.videoUrl`: a direct .mp4/.webm link (or a ProgramData-relative path); nothing to play when empty. */
  src: string | null | undefined;
  /** `Game.videoSourceUrl`: the original link of a club server's copy, tried once when `src` fails. */
  fallback?: string | null | undefined;
}

export function GameTrailer({ src, fallback }: GameTrailerProps): JSX.Element | null {
  const animations = useThemeStore(selectAnimationsEnabled);
  const reducedMotion = useReducedMotion() === true;
  const gameMode = useGamesStore(selectGameMode);
  const visible = useShellVisible();
  const primary = useResolvedAsset(src);
  const second = useResolvedAsset(fallback);
  /** URLs that failed while this trailer was shown. */
  const [failed, setFailed] = useState<readonly string[]>([]);
  // The fallback is only for a copy that failed: never played while the copy's own URL is still being resolved.
  const primaryPending = Boolean(src) && primary.url === null && !primary.error;
  const url =
    (primaryPending ? [] : [primary.url, second.url]).find(
      (u): u is string => u !== null && !failed.includes(u) && !recentlyFailed(u),
    ) ?? null;
  /** The URL whose wait is over (a URL, not a flag: a new trailer never renders with the previous one's go-ahead). */
  const [dueUrl, setDueUrl] = useState<string | null>(null);
  const [shown, setShown] = useState(false);
  const video = useRef<HTMLVideoElement>(null);

  const allowed = url !== null && animations && !reducedMotion && !gameMode;
  const mounted = allowed && dueUrl === url;
  const playing = mounted && shown && visible;

  // The wait starts over for every trailer and whenever it may play again (back from a game, animations on again).
  useEffect(() => {
    setDueUrl(null);
    setShown(false);
    if (!allowed) {
      return undefined;
    }
    const timer = window.setTimeout(() => setDueUrl(url), TRAILER_DELAY_MS);
    return () => window.clearTimeout(timer);
  }, [allowed, url]);

  useEffect(() => {
    const el = video.current;
    if (!mounted || !el) {
      return;
    }
    if (visible) {
      el.play().catch(() => undefined);
    } else {
      el.pause();
    }
  }, [mounted, visible]);

  // A removed <video> keeps downloading until it is garbage-collected: let go of the file as soon as it is gone.
  useEffect(() => {
    const el = video.current;
    if (!mounted || !el) {
      return undefined;
    }
    return () => {
      el.pause();
      el.removeAttribute('src');
      el.load();
    };
  }, [mounted]);

  useEffect(() => {
    if (!playing) {
      return undefined;
    }
    useTrailerStore.setState((s) => ({ playing: s.playing + 1 }));
    return () => useTrailerStore.setState((s) => ({ playing: s.playing - 1 }));
  }, [playing]);

  if (!mounted || url === null) {
    return null;
  }

  return (
    <video
      ref={video}
      src={url}
      muted
      autoPlay={visible}
      loop
      playsInline
      preload="metadata"
      disablePictureInPicture
      disableRemotePlayback
      aria-hidden="true"
      tabIndex={-1}
      onPlaying={(e) => {
        // The art stays until the first frame is on screen: no poster, no blank or half-decoded frame in between.
        const el = e.currentTarget as HTMLVideoElement & {
          requestVideoFrameCallback?: (cb: () => void) => number;
        };
        if (typeof el.requestVideoFrameCallback === 'function') el.requestVideoFrameCallback(() => setShown(true));
        else setShown(true);
      }}
      onError={(e) => {
        // Only a real media error (not the source being dropped on the way out) counts as a broken link.
        if (e.currentTarget.error !== null) {
          failures.set(url, Date.now());
          setFailed((list) => [...list, url]);
        }
      }}
      className={clsx(
        'pointer-events-none absolute inset-0 h-full w-full object-cover transition-opacity duration-[700ms] ease-[var(--ease-out)]',
        shown ? 'opacity-100' : 'opacity-0',
      )}
    />
  );
}
