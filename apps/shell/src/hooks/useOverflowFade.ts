import { useEffect, type RefObject } from 'react';

/**
 * Marks a horizontally scrolling row with `data-fade-start` / `data-fade-end` while there is more content off that
 * edge; the `.fade-x` utility turns them into a soft edge. Without it a row that overflows just cuts its last item
 * in half, which reads as a layout bug rather than as "scroll for more". `contentKey` re-measures when the items
 * change (a resize of the row itself is observed on its own).
 */
export function useOverflowFade(ref: RefObject<HTMLElement>, contentKey?: unknown): void {
  useEffect(() => {
    const el = ref.current;
    if (!el) {
      return undefined;
    }
    const update = (): void => {
      const max = el.scrollWidth - el.clientWidth;
      el.toggleAttribute('data-fade-start', el.scrollLeft > 1);
      el.toggleAttribute('data-fade-end', el.scrollLeft < max - 1);
    };
    update();
    el.addEventListener('scroll', update, { passive: true });
    const ro = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(update);
    ro?.observe(el);
    return () => {
      el.removeEventListener('scroll', update);
      ro?.disconnect();
    };
  }, [ref, contentKey]);
}
