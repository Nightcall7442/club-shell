/**
 * When decorative video may play: shared by the theme's background loop and the game trailers, so both pause for the
 * same reasons and never decode at the same time.
 */
import { useEffect, useState } from 'react';
import { create } from 'zustand';
import { events } from '@/lib/tauri';

/** `true` while the shell is on screen and in front: the page is visible and the kiosk window has focus. */
export function useShellVisible(): boolean {
  const [hidden, setHidden] = useState(() => typeof document !== 'undefined' && document.hidden);
  const [focused, setFocused] = useState(true);

  useEffect(() => {
    const onVisibility = (): void => setHidden(document.hidden);
    document.addEventListener('visibilitychange', onVisibility);
    const off = events.onKiosk('focus', (p) => setFocused(p.focused));
    return () => {
      document.removeEventListener('visibilitychange', onVisibility);
      off();
    };
  }, []);

  return !hidden && focused;
}

/** Trailers playing on screen right now; the background loop pauses meanwhile (one video decoding at a time). */
export const useTrailerStore = create<{ playing: number }>(() => ({ playing: 0 }));

/** Selector: a trailer is playing. */
export const selectTrailerPlaying = (s: { playing: number }): boolean => s.playing > 0;
