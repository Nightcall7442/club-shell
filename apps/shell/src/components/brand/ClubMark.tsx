import { useState, type CSSProperties } from 'react';
import clsx from 'clsx';
import { useResolvedAsset } from '@/components/media/GameArtwork';
import { useClub } from '@/hooks/useClub';

export interface ClubMarkProps {
  /** Box size, e.g. `h-8 w-8`. */
  className?: string;
  /** Box size as CSS (the owner's logo size, D-104); wins over the size in `className`. */
  size?: string;
}

/** The club's logo from the admin console; the accent diamond when there is none or it fails to load. */
export function ClubMark({ className = 'h-6 w-6', size }: ClubMarkProps): JSX.Element {
  const { logoUrl } = useClub();
  const { url } = useResolvedAsset(logoUrl);
  const [failed, setFailed] = useState<string | null>(null);
  const style: CSSProperties | undefined = size ? { width: size, height: size } : undefined;

  if (url && failed !== url) {
    return (
      <img
        src={url}
        alt=""
        aria-hidden="true"
        draggable={false}
        onError={() => setFailed(url)}
        data-testid="club-logo"
        className={clsx('shrink-0 object-contain', className)}
        style={style}
      />
    );
  }
  return (
    <span
      aria-hidden="true"
      data-testid="club-logo"
      className={clsx('shrink-0 rotate-45 border border-accent/70', className)}
      style={style}
    />
  );
}
