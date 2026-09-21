/**
 * Pointer-following 3D tilt for `.tilt` elements: writes `--rx/--ry` (rotation) and `--mx/--my` (sheen origin) as
 * CSS variables straight on the element, so React never re-renders while the pointer moves. No-op when the theme
 * turns animations off. Spread the returned handlers on the element.
 */
import type { PointerEvent } from 'react';

const RESET = { '--rx': '0deg', '--ry': '0deg', '--mx': '50%', '--my': '50%' } as const;

function animationsOn(): boolean {
  return document.documentElement.dataset['animations'] !== 'false';
}

export function tiltHandlers(maxDeg = 7): {
  onPointerMove: (e: PointerEvent<HTMLElement>) => void;
  onPointerLeave: (e: PointerEvent<HTMLElement>) => void;
} {
  return {
    onPointerMove(e) {
      if (!animationsOn() || e.pointerType === 'touch') {
        return;
      }
      const el = e.currentTarget;
      const r = el.getBoundingClientRect();
      if (r.width === 0 || r.height === 0) {
        return;
      }
      const x = (e.clientX - r.left) / r.width;
      const y = (e.clientY - r.top) / r.height;
      el.style.setProperty('--ry', `${((x - 0.5) * 2 * maxDeg).toFixed(2)}deg`);
      el.style.setProperty('--rx', `${((0.5 - y) * 2 * maxDeg).toFixed(2)}deg`);
      el.style.setProperty('--mx', `${(x * 100).toFixed(1)}%`);
      el.style.setProperty('--my', `${(y * 100).toFixed(1)}%`);
      el.dataset['tilting'] = 'true';
    },
    onPointerLeave(e) {
      const el = e.currentTarget;
      for (const [k, v] of Object.entries(RESET)) {
        el.style.setProperty(k, v);
      }
      el.dataset['tilting'] = 'false';
    },
  };
}
