/**
 * Game and product art of variant F, and the console's wallpaper backdrop.
 *
 * {@link GameArt} fills its positioned parent with a cover or hero image under the scrims that keep text over it
 * legible: `tile` (the map tile: cover on the right 82 %, fading in from the left), `strip` (the seat card's 192 px band),
 * `hero` (a sheet's 150 px header), `product` (a bar tile: 60 % on the right). Without a picture, or when it fails to
 * load (Steam's CDN offline, a catalog game with `coverUrl ""`), it shows `fallback`, else the blueprint grid — the
 * no-art look is the common case on a real server and is meant to look finished.
 *
 * {@link Wallpaper} is the layer behind the whole console: the club's wallpaper (or the bundled default) blurred,
 * desaturated and dimmed by three overlays and a floor vignette, so dense pages stay calm (solid from about half way
 * down).
 */
import { useEffect, useState, type ReactNode } from 'react';
import clsx from 'clsx';

export type ArtVariant = 'tile' | 'strip' | 'hero' | 'product';

/** The bundled wallpaper the console shows while the club has none (`branding.wallpaperUrl` null). */
export const DEFAULT_WALLPAPER = `${import.meta.env.BASE_URL}default-wallpaper.jpg`;

const IMG: Record<ArtVariant, string> = {
  tile: 'art-mask right-0 top-0 h-full w-[82%] object-[50%_16%]',
  strip: 'inset-0 h-full w-full object-[70%_30%]',
  hero: 'inset-0 h-full w-full object-[70%_30%]',
  product: 'art-mask right-0 top-0 h-full w-[60%] object-center',
};

/** The scrims over the picture (spec §6.4 tile, §6.7 seat strip, §7 sheet hero). */
const SCRIMS: Record<ArtVariant, string[]> = {
  tile: [
    'linear-gradient(90deg, rgb(7 9 12 / 0.92) 0%, rgb(7 9 12 / 0.72) 50%, rgb(7 9 12 / 0.4) 78%, rgb(7 9 12 / 0.12) 100%), linear-gradient(180deg, rgb(7 9 12 / 0.3) 0%, rgb(7 9 12 / 0) 34%, rgb(7 9 12 / 0.6) 100%)',
  ],
  strip: [
    'linear-gradient(90deg, rgb(7 9 12 / 0.88) 0%, rgb(7 9 12 / 0.5) 44%, rgb(7 9 12 / 0) 78%)',
    'linear-gradient(180deg, rgb(7 9 12 / 0.6) 0%, rgb(7 9 12 / 0) 24%, rgb(7 9 12 / 0) 42%, rgb(10 13 18 / 0.86) 70%, rgb(10 13 18) 100%)',
  ],
  hero: [
    'linear-gradient(180deg, rgb(7 9 12 / 0.55) 0%, rgb(7 9 12 / 0) 30%, rgb(7 9 12 / 0.1) 50%, rgb(13 17 23 / 0.9) 82%, rgb(13 17 23) 100%)',
    'linear-gradient(90deg, rgb(7 9 12 / 0.86) 0%, rgb(7 9 12 / 0.4) 45%, rgb(7 9 12 / 0) 75%)',
  ],
  product: [
    'linear-gradient(90deg, rgb(7 9 12 / 0.92) 0%, rgb(7 9 12 / 0.7) 45%, rgb(7 9 12 / 0.25) 80%, rgb(7 9 12 / 0.1) 100%), linear-gradient(180deg, rgb(7 9 12 / 0.2) 0%, rgb(7 9 12 / 0) 40%, rgb(7 9 12 / 0.55) 100%)',
  ],
};

/** The accent hairline across the top of a strip or a hero (the primary surface's edge). */
function HeroLine({ inset }: { inset: number }): JSX.Element {
  return (
    <span
      aria-hidden="true"
      className="pointer-events-none absolute top-0 h-px"
      style={{
        left: inset,
        right: inset,
        background: `linear-gradient(90deg, rgb(var(--c-accent) / 0) 0%, rgb(var(--c-accent) / ${inset > 0 ? 0.6 : 0.7}) 50%, rgb(var(--c-accent) / 0) 100%)`,
      }}
    />
  );
}

/**
 * Art for a tile, a seat strip, a sheet hero or a product, filling its positioned parent (`absolute inset-0`, clipped,
 * the parent's radius). `zoom` scales the picture a little while the parent `.group` is hovered. `children` render on
 * top of the scrims inside the clipped layer (a progress bar).
 */
export function GameArt({
  src,
  variant,
  fallback,
  zoom,
  edge,
  className,
  children,
}: {
  src: string | null | undefined;
  variant: ArtVariant;
  /** Shown instead of the picture when there is none or it failed (default: the blueprint grid). */
  fallback?: ReactNode;
  zoom?: boolean;
  /** The accent hairline on top (seat strip, sheet hero). */
  edge?: boolean;
  className?: string;
  children?: ReactNode;
}): JSX.Element {
  const [failed, setFailed] = useState<string | null>(null);
  useEffect(() => setFailed(null), [src]);
  const shown = src && failed !== src ? src : null;
  return (
    <span
      aria-hidden="true"
      className={clsx('pointer-events-none absolute inset-0 overflow-hidden rounded-[inherit]', className)}
    >
      {shown ? (
        <>
          <img
            src={shown}
            alt=""
            loading="lazy"
            decoding="async"
            referrerPolicy="no-referrer"
            draggable={false}
            onError={() => setFailed(shown)}
            className={clsx(
              'absolute block select-none object-cover',
              IMG[variant],
              zoom && 'transition-transform duration-200 ease-out group-hover:scale-[1.03]',
            )}
          />
          {SCRIMS[variant].map((bg, i) => (
            <span key={i} className="absolute inset-0" style={{ background: bg }} />
          ))}
        </>
      ) : (
        (fallback ?? <span className="hud-grid absolute inset-0 opacity-70" />)
      )}
      {edge && <HeroLine inset={variant === 'hero' ? 60 : 0} />}
      {children}
    </span>
  );
}

/** {@link GameArt} under its other name (a product's picture is art too). */
export const Art = GameArt;

/**
 * The backdrop of the whole console (spec §4): rendered once in App, behind everything, `pointer-events: none`. The
 * club's wallpaper — or the bundled default while the club has none — blurred, desaturated and dimmed: a vertical
 * overlay that is solid from about 56 % down, a horizontal one darker on the left (the rail) and right, and a floor
 * vignette. `strong` (the PIN screen) lets more of the picture through. A picture that fails to load leaves the
 * blueprint grid and an accent haze.
 */
export function Wallpaper({ url, strong }: { url: string | null | undefined; strong?: boolean }): JSX.Element {
  const src = url || DEFAULT_WALLPAPER;
  const [failed, setFailed] = useState<string | null>(null);
  useEffect(() => setFailed(null), [src]);
  const own = Boolean(url);
  return (
    <div aria-hidden="true" className="pointer-events-none absolute inset-0 overflow-hidden">
      {failed !== src ? (
        <img
          src={src}
          alt=""
          decoding="async"
          referrerPolicy="no-referrer"
          draggable={false}
          onError={() => setFailed(src)}
          className="absolute left-0 top-[-4.5%] block h-[90.5%] w-full select-none object-cover object-[50%_40%]"
          style={{
            // The bundled picture is a stock hall: darker and softer than a club's own, so it never competes.
            filter: own ? 'blur(1.5px) saturate(0.7) brightness(0.95)' : 'blur(2.5px) saturate(0.7) brightness(0.88)',
          }}
        />
      ) : (
        <>
          <span className="hud-grid absolute inset-0" />
          <span
            className="absolute inset-0"
            style={{
              background: 'radial-gradient(900px 520px at 60% 0%, rgb(var(--c-accent) / 0.07), transparent 70%)',
            }}
          />
        </>
      )}
      <span
        className="absolute inset-0"
        style={{
          background: strong
            ? 'linear-gradient(180deg, rgb(7 9 12 / 0.45) 0%, rgb(7 9 12 / 0.6) 30%, rgb(7 9 12 / 0.8) 60%, rgb(7 9 12 / 0.93) 100%)'
            : 'linear-gradient(180deg, rgb(7 9 12 / 0.3) 0%, rgb(7 9 12 / 0.5) 14%, rgb(7 9 12 / 0.8) 30%, rgb(7 9 12 / 0.93) 56%, rgb(7 9 12) 100%)',
        }}
      />
      <span
        className="absolute inset-0"
        style={{
          background:
            'linear-gradient(90deg, rgb(7 9 12 / 0.7) 0%, rgb(7 9 12 / 0.2) 30%, rgb(7 9 12 / 0) 60%, rgb(7 9 12 / 0.45) 100%)',
        }}
      />
      <span
        className="absolute inset-0"
        style={{ background: 'radial-gradient(1100px 600px at 50% 120%, rgb(0 0 0 / 0.6) 0%, rgb(0 0 0 / 0) 70%)' }}
      />
    </div>
  );
}
