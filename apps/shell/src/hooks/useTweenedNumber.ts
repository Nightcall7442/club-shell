import { useEffect, useRef, useState } from 'react';
import { animate } from 'framer-motion';
import { EASE_OUT, useMotion } from '@/lib/motion';

/**
 * A number that counts to its new value instead of jumping (a top-up runs the balance up, a purchase down). The first
 * value shows as it is; without motion every value does. For display only: labels keep the exact value.
 */
export function useTweenedNumber(value: number, duration = 0.8): number {
  const moving = useMotion();
  const [shown, setShown] = useState(value);
  const from = useRef(value);
  useEffect(() => {
    if (!moving || from.current === value) {
      from.current = value;
      setShown(value);
      return undefined;
    }
    const controls = animate(from.current, value, {
      duration,
      ease: EASE_OUT,
      onUpdate: (v) => setShown(Math.round(v)),
    });
    from.current = value;
    return () => controls.stop();
  }, [value, moving, duration]);
  return shown;
}
