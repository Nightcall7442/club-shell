import { forwardRef, type CSSProperties, type HTMLAttributes } from 'react';
import clsx from 'clsx';

export type SkeletonVariant = 'text' | 'rect' | 'circle' | 'cover' | 'hero';

export interface SkeletonProps extends HTMLAttributes<HTMLDivElement> {
  variant?: SkeletonVariant;
  width?: number | string;
  height?: number | string;
  /** `text` only: number of stacked lines (last one shorter). */
  lines?: number;
}

const VARIANT: Record<SkeletonVariant, string> = {
  text: 'h-[1em] rounded-sm',
  rect: 'rounded',
  circle: 'rounded-full',
  cover: 'aspect-[2/3] w-full rounded',
  hero: 'aspect-[16/9] w-full rounded',
};

/** Shimmering placeholder block (`.anim-skeleton`), flat under reduced motion. */
export const Skeleton = forwardRef<HTMLDivElement, SkeletonProps>(function Skeleton(
  { variant = 'rect', width, height, lines = 1, className, style, ...rest },
  ref,
) {
  const box: CSSProperties = { width, height, ...style };
  if (variant === 'text' && lines > 1) {
    return (
      <div ref={ref} aria-hidden="true" className={clsx('flex flex-col gap-2', className)} style={box} {...rest}>
        {Array.from({ length: lines }, (_, i) => (
          <div key={i} className={clsx('anim-skeleton', VARIANT.text)} style={{ width: i === lines - 1 ? '60%' : '100%' }} />
        ))}
      </div>
    );
  }
  return <div ref={ref} aria-hidden="true" className={clsx('anim-skeleton', VARIANT[variant], className)} style={box} {...rest} />;
});
