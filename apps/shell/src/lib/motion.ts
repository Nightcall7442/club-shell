/**
 * Motion shared by the shell: one easing, one set of durations, and one switch. Motion runs only when the theme has
 * animations on and the OS does not ask for reduced motion, so a kiosk set to "less motion" gets plain cuts.
 */
import { useReducedMotion } from 'framer-motion';
import { useThemeStore } from '@/store/theme';

/** Fast out, long settle: things arrive quickly and come to rest softly. */
export const EASE_OUT: [number, number, number, number] = [0.16, 1, 0.3, 1];

/** Whether the UI animates right now. */
export function useMotion(): boolean {
  const theme = useThemeStore((s) => s.theme.animations);
  const reduced = useReducedMotion() === true;
  return theme && !reduced;
}

export { sectionIndex, slideDirection } from './sections';
