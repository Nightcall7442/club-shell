import { useState } from 'react';
import clsx from 'clsx';
import { useResolvedAsset } from '@/components/media/GameArtwork';
import { useClub } from '@/hooks/useClub';

export interface ClubMarkProps {
  /** Box size, e.g. `h-8 w-8`. */
  className?: string;
}

/** The club's logo from the admin console; the accent diamond when there is none or it fails to load. */
export function ClubMark({ className = 'h-6 w-6' }: ClubMarkProps): JSX.Element {
  const { logoUrl } = useClub();
  const { url } = useResolvedAsset(logoUrl);
  const [failed, setFailed] = useState<string | null>(null);

  if (url && failed !== url) {
    return (
      <img
        src={url}
        alt=""
        aria-hidden="true"
        draggable={false}
        onError={() => setFailed(url)}
        className={clsx('shrink-0 object-contain', className)}
      />
    );
  }
  return <span aria-hidden="true" className={clsx('shrink-0 rotate-45 border border-accent/70', className)} />;
}
