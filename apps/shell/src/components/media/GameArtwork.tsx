import { forwardRef, useEffect, useState, type HTMLAttributes, type ReactNode } from 'react';
import clsx from 'clsx';
import { useTranslation } from 'react-i18next';
import { assetUrl } from '@/lib/tauri';
import { Skeleton } from '@/components/ui/Skeleton';
import { useSettingsStore } from '@/store/settings';

export type ArtworkKind = 'cover' | 'hero';

export interface GameArtworkProps extends HTMLAttributes<HTMLDivElement> {
  /** Cover/hero URL or ProgramData-relative path (`cache\media\…`); resolved through `assetUrl`. */
  src?: string | null;
  /** Game title: alt text and the fallback tile. */
  title: string;
  kind?: ArtworkKind;
  /** `W:H`; defaults to `ui.coverAspect` (2:3) for covers and 16:9 for heroes. */
  aspect?: string;
  /** Eager load (above the fold). */
  priority?: boolean;
  fit?: 'cover' | 'contain';
  /** Content rendered over the image (badges, gradients, buttons). */
  overlay?: ReactNode;
}

/** `'2:3'` → `'2 / 3'` for `aspect-ratio`; invalid input falls back. */
export function aspectToCss(aspect: string | undefined, fallback: string): string {
  const m = aspect?.match(/^\s*(\d+(?:\.\d+)?)\s*[:/]\s*(\d+(?:\.\d+)?)\s*$/);
  if (!m || Number(m[2]) === 0) {
    return fallback;
  }
  return `${m[1]} / ${m[2]}`;
}

/** Resolves an asset path to a loadable URL; `url` is `null` until resolved (or when there is no path). */
export function useResolvedAsset(path: string | null | undefined): { url: string | null; error: boolean } {
  const [state, setState] = useState<{ url: string | null; error: boolean }>({ url: null, error: false });
  useEffect(() => {
    let active = true;
    setState({ url: null, error: false });
    if (!path) {
      return undefined;
    }
    assetUrl(path).then(
      (url) => {
        if (active) {
          setState({ url, error: false });
        }
      },
      () => {
        if (active) {
          setState({ url: null, error: true });
        }
      },
    );
    return () => {
      active = false;
    };
  }, [path]);
  return state;
}

/** Lazy game cover/hero with skeleton, `assetUrl` resolution and a titled gradient fallback. */
export const GameArtwork = forwardRef<HTMLDivElement, GameArtworkProps>(function GameArtwork(
  { src, title, kind = 'cover', aspect, priority = false, fit = 'cover', overlay, className, style, ...rest },
  ref,
) {
  const { t } = useTranslation();
  const configured = useSettingsStore((s) => s.shellConfig?.ui.coverAspect);
  const { url, error } = useResolvedAsset(src);
  const [loaded, setLoaded] = useState(false);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    setLoaded(false);
    setFailed(false);
  }, [url]);

  const ratio = kind === 'cover' ? aspectToCss(aspect ?? configured, '2 / 3') : aspectToCss(aspect, '16 / 9');
  const showFallback = !src || error || failed;
  const showSkeleton = !showFallback && !loaded;

  return (
    <div
      ref={ref}
      className={clsx(
        'relative w-full overflow-hidden rounded bg-[linear-gradient(160deg,rgb(var(--c-surface))_0%,rgb(var(--c-bg))_100%)]',
        className,
      )}
      style={{ aspectRatio: ratio, ...style }}
      {...rest}
    >
      {url && !showFallback && (
        <img
          src={url}
          alt={kind === 'cover' ? t('games.coverAlt', { title }) : t('games.heroAlt', { title })}
          loading={priority ? 'eager' : 'lazy'}
          decoding="async"
          draggable={false}
          onLoad={() => setLoaded(true)}
          onError={() => setFailed(true)}
          className={clsx(
            'absolute inset-0 h-full w-full transition-opacity duration-[var(--dur-slow)]',
            fit === 'cover' ? 'object-cover' : 'object-contain',
            loaded ? 'opacity-100' : 'opacity-0',
          )}
        />
      )}
      {showSkeleton && <Skeleton variant="rect" className="absolute inset-0 h-full w-full rounded-none" />}
      {showFallback && (
        <div
          role="img"
          aria-label={title}
          className="absolute inset-0 flex items-end bg-[radial-gradient(120%_80%_at_20%_0%,rgb(var(--c-primary)/0.45)_0%,transparent_60%),radial-gradient(100%_80%_at_100%_100%,rgb(var(--c-accent)/0.35)_0%,transparent_60%)] p-4"
        >
          <span className={clsx('line-clamp-3 font-bold leading-tight text-text/90', kind === 'cover' ? 'text-xl' : 'text-3xl')}>{title}</span>
        </div>
      )}
      {overlay}
    </div>
  );
});
