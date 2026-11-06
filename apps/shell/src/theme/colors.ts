/**
 * Colour math for themes: parsing, shading and WCAG contrast. Pure (no imports), so `colors.test.ts` runs it under
 * `node --test` and checks the bundled themes with it.
 */

/**
 * `#RGB`, `#RRGGBB` or `#RRGGBBAA` → `"R G B"` (alpha ignored, Tailwind supplies it).
 * Returns `null` for anything else so a malformed theme cannot blank the UI.
 */
export function hexToRgb(hex: string): string | null {
  const m = /^#?([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$/i.exec(hex.trim());
  if (!m) {
    return null;
  }
  let h = m[1] ?? '';
  if (h.length === 3) {
    h = h
      .split('')
      .map((c) => c + c)
      .join('');
  }
  const r = parseInt(h.slice(0, 2), 16);
  const g = parseInt(h.slice(2, 4), 16);
  const b = parseInt(h.slice(4, 6), 16);
  return `${r} ${g} ${b}`;
}

/** Mixes a theme hex colour towards black/white (for hover/active shades); `amount` −1..1. */
export function shade(hex: string, amount: number): string {
  const rgb = hexToRgb(hex);
  if (!rgb) {
    return hex;
  }
  const [r, g, b] = rgb.split(' ').map(Number) as [number, number, number];
  const target = amount < 0 ? 0 : 255;
  const k = Math.min(1, Math.abs(amount));
  const mix = (c: number): number => Math.round(c + (target - c) * k);
  const to = (c: number): string => mix(c).toString(16).padStart(2, '0');
  return `#${to(r)}${to(g)}${to(b)}`;
}

/** WCAG relative luminance 0..1 of a hex colour (`0` for malformed input). */
export function luminance(hex: string): number {
  const rgb = hexToRgb(hex);
  if (!rgb) {
    return 0;
  }
  const [r, g, b] = rgb.split(' ').map((c) => {
    const v = Number(c) / 255;
    return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
  }) as [number, number, number];
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/** `true` when text on this colour should be dark (light primaries such as white buttons). */
export function isLight(hex: string): boolean {
  return luminance(hex) > 0.4;
}

/** WCAG contrast ratio of two colours, 1..21. */
export function contrastRatio(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [number, number];
  return (hi + 0.05) / (lo + 0.05);
}

/**
 * The text colour for a filled control: the theme background or white, whichever reads better on the fill. A mid
 * tone (Neon's violet, a club accent) gets dark text when white would fall under 4.5:1.
 */
export function onColor(fill: string, bg: string): string {
  return contrastRatio(bg, fill) >= contrastRatio('#FFFFFF', fill) ? bg : '#FFFFFF';
}

/** Contrast white text keeps on {@link solidFill}: AA with a little room for anti-aliasing. */
const SOLID_FILL_CONTRAST = 4.6;

/**
 * The fill for white text on a status colour (solid danger badges and buttons): the colour itself, darkened just
 * enough that white reaches {@link SOLID_FILL_CONTRAST}. Red keeps its white "destructive" label in every theme.
 */
export function solidFill(hex: string): string {
  for (let i = 0; i <= 50; i += 1) {
    const fill = shade(hex, -i / 50);
    if (contrastRatio('#FFFFFF', fill) >= SOLID_FILL_CONTRAST) {
      return fill;
    }
  }
  return '#000000';
}
