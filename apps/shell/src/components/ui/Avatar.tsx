import { forwardRef, useEffect, useState, type HTMLAttributes } from 'react';
import clsx from 'clsx';
import { initials } from '@/lib/format';

export type AvatarSize = 'sm' | 'md' | 'lg' | 'xl';

export interface AvatarProps extends HTMLAttributes<HTMLSpanElement> {
  /** Display name; initials are derived from it when there is no image. */
  name: string;
  src?: string | null;
  size?: AvatarSize;
  /** Presence dot. */
  status?: 'online' | 'offline' | null;
  /** Glowing ring (current user, VIP). */
  ring?: boolean;
}

const SIZES: Record<AvatarSize, string> = {
  sm: 'h-8 w-8 text-xs',
  md: 'h-11 w-11 text-sm',
  lg: 'h-16 w-16 text-lg',
  xl: 'h-24 w-24 text-3xl',
};

/** Deterministic hue per name so guests without pictures still look distinct. */
function hueOf(name: string): number {
  let h = 0;
  for (let i = 0; i < name.length; i += 1) {
    h = (h * 31 + name.charCodeAt(i)) % 360;
  }
  return h;
}

export const Avatar = forwardRef<HTMLSpanElement, AvatarProps>(function Avatar(
  { name, src, size = 'md', status = null, ring = false, className, style, ...rest },
  ref,
) {
  const [failed, setFailed] = useState(false);
  useEffect(() => setFailed(false), [src]);
  const showImage = Boolean(src) && !failed;
  const hue = hueOf(name);

  return (
    <span
      ref={ref}
      role="img"
      aria-label={name}
      className={clsx(
        'relative inline-flex shrink-0 select-none items-center justify-center overflow-hidden rounded-full bg-surface font-bold uppercase text-text',
        SIZES[size],
        ring && 'border-glow',
        className,
      )}
      style={
        showImage
          ? style
          : { background: `linear-gradient(135deg, hsl(${hue} 70% 45%), hsl(${(hue + 40) % 360} 70% 30%))`, ...style }
      }
      {...rest}
    >
      {showImage ? (
        <img
          src={src ?? undefined}
          alt=""
          className="h-full w-full object-cover"
          loading="lazy"
          draggable={false}
          onError={() => setFailed(true)}
        />
      ) : (
        <span aria-hidden="true">{initials(name)}</span>
      )}
      {status && (
        <span
          aria-hidden="true"
          className={clsx(
            'absolute bottom-0 right-0 h-1/4 w-1/4 min-h-[8px] min-w-[8px] rounded-full border-2 border-bg',
            status === 'online' ? 'bg-success' : 'bg-muted',
          )}
        />
      )}
    </span>
  );
});
