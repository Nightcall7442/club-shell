/**
 * Dominant colour of an image as an `"R G B"` triplet for `rgb(var(--x) / a)`: a 12×12 downsample, pixels weighted
 * by saturation (so smoke, sky and near-black do not dilute the hue), then pushed to a mid-lightness, saturated tone
 * that works as a glow behind white text. `null` while loading, on failure or when the canvas is tainted (a
 * cross-origin asset without CORS) — callers must render fine without it.
 */
import { useEffect, useState } from 'react';

const SAMPLE = 12;

function hslToRgb(h: number, s: number, l: number): [number, number, number] {
  const k = (n: number): number => (n + h * 12) % 12;
  const a = s * Math.min(l, 1 - l);
  const f = (n: number): number => l - a * Math.max(-1, Math.min(k(n) - 3, 9 - k(n), 1));
  return [Math.round(f(0) * 255), Math.round(f(8) * 255), Math.round(f(4) * 255)];
}

/** Weighted average → HSL → clamp lightness 0.42–0.55 and boost saturation so the tint reads as a stage light. */
export function tintFromPixels(data: Uint8ClampedArray): string | null {
  let r = 0;
  let g = 0;
  let b = 0;
  let w = 0;
  for (let i = 0; i < data.length; i += 4) {
    const R = data[i] ?? 0;
    const G = data[i + 1] ?? 0;
    const B = data[i + 2] ?? 0;
    const max = Math.max(R, G, B);
    const min = Math.min(R, G, B);
    const sat = max === 0 ? 0 : (max - min) / max;
    const lum = max / 255;
    const k = sat * sat * (lum > 0.15 ? 1 : 0.15) + 0.01;
    r += R * k;
    g += G * k;
    b += B * k;
    w += k;
  }
  if (w === 0) {
    return null;
  }
  r /= w * 255;
  g /= w * 255;
  b /= w * 255;
  const max = Math.max(r, g, b);
  const min = Math.min(r, g, b);
  const l = (max + min) / 2;
  const d = max - min;
  if (d < 0.04) {
    return null;
  }
  const s = d / (1 - Math.abs(2 * l - 1));
  let h = 0;
  if (max === r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
  else if (max === g) h = ((b - r) / d + 2) / 6;
  else h = ((r - g) / d + 4) / 6;
  const [R, G, B] = hslToRgb(h, Math.min(1, s * 1.35 + 0.15), Math.min(0.55, Math.max(0.42, l)));
  return `${R} ${G} ${B}`;
}

export function useImageTint(src: string | null): string | null {
  const [tint, setTint] = useState<string | null>(null);

  useEffect(() => {
    if (!src) {
      setTint(null);
      return undefined;
    }
    let active = true;
    const img = new Image();
    img.crossOrigin = 'anonymous';
    img.decoding = 'async';
    img.onload = () => {
      if (!active) {
        return;
      }
      try {
        const canvas = document.createElement('canvas');
        canvas.width = SAMPLE;
        canvas.height = SAMPLE;
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        if (!ctx) {
          return;
        }
        ctx.drawImage(img, 0, 0, SAMPLE, SAMPLE);
        setTint(tintFromPixels(ctx.getImageData(0, 0, SAMPLE, SAMPLE).data));
      } catch {
        setTint(null);
      }
    };
    img.onerror = () => active && setTint(null);
    img.src = src;
    return () => {
      active = false;
    };
  }, [src]);

  return tint;
}
