/**
 * The player-screen designer (D-105) as the server keeps it: `settings.branding.lock|idle|desktop|font|tint` checked
 * on `PATCH /admin/club` (`400 branding.<field> <reason>`, as `BrandingLook.Validate`) and turned into
 * `shell.club.look` for the PCs (as `BrandingLook.ToShell`).
 */
import type { ShellLook, ShellScreenLook, ShellText } from '@clubshell/contracts';
import { errors } from './db.js';

type Screen = 'lock' | 'idle' | 'desktop';
const SCREENS: Screen[] = ['lock', 'idle', 'desktop'];
const FONTS = ['unbounded', 'inter', 'mono'];
const POSITIONS: Record<Screen, string[]> = {
  lock: ['topLeft', 'topCenter', 'topRight', 'center'],
  idle: ['topLeft', 'topCenter', 'topRight', 'center', 'bottomLeft', 'bottomRight'],
  desktop: ['topLeft'],
};
const SIZES: Record<Screen, string[]> = {
  lock: ['s', 'm', 'l', 'xl'],
  idle: ['s', 'm', 'l', 'xl'],
  desktop: ['s', 'm', 'l'],
};
const FLAGS: Record<Screen, string[]> = {
  lock: ['clock', 'language'],
  idle: ['clock', 'prices', 'freeBadge'],
  desktop: [],
};
const TEXTS: Record<Screen, [string, number][]> = {
  lock: [
    ['title', 60],
    ['subtitle', 120],
  ],
  idle: [['cta', 60]],
  desktop: [],
};
const HEX = /^#[0-9a-fA-F]{6}$/;

type Obj = Record<string, unknown>;
const isObj = (v: unknown): v is Obj => typeof v === 'object' && v !== null && !Array.isArray(v);
const present = (v: unknown): boolean => v !== undefined && v !== null;

function flag(o: Obj, at: string, name: string): void {
  if (present(o[name]) && typeof o[name] !== 'boolean') throw errors.validation(`${at}.${name}`, 'type');
}

function oneOf(o: Obj, at: string, name: string, allowed: string[]): void {
  const v = o[name];
  if (present(v) && (typeof v !== 'string' || !allowed.includes(v))) throw errors.validation(`${at}.${name}`, 'enum');
}

/** The owner's branding patch, checked like the server does. */
export function validateLook(branding: unknown): void {
  if (!isObj(branding)) return;
  oneOf(branding, 'branding', 'font', FONTS);
  const tint = branding['tint'];
  if (present(tint) && (typeof tint !== 'string' || !HEX.test(tint)))
    throw errors.validation('branding.tint', 'format');
  for (const screen of SCREENS) {
    const look = branding[screen];
    if (!present(look)) continue;
    const at = `branding.${screen}`;
    if (!isObj(look)) throw errors.validation(at, 'schema');
    const logo = look['logo'];
    if (present(logo)) {
      if (!isObj(logo)) throw errors.validation(`${at}.logo`, 'schema');
      flag(logo, `${at}.logo`, 'show');
      flag(logo, `${at}.logo`, 'name');
      oneOf(logo, `${at}.logo`, 'position', POSITIONS[screen]);
      oneOf(logo, `${at}.logo`, 'size', SIZES[screen]);
    }
    const url = look['wallpaperUrl'];
    if (present(url)) {
      if (typeof url !== 'string') throw errors.validation(`${at}.wallpaperUrl`, 'type');
      if (url.length > 2048) throw errors.validation(`${at}.wallpaperUrl`, 'max');
      if (url && !(url.startsWith('/') && !url.startsWith('//')) && !/^https?:\/\//i.test(url))
        throw errors.validation(`${at}.wallpaperUrl`, 'format');
    }
    const dim = look['dim'];
    if (present(dim)) {
      if (typeof dim !== 'number' || !Number.isInteger(dim)) throw errors.validation(`${at}.dim`, 'type');
      if (dim < 0) throw errors.validation(`${at}.dim`, 'min');
      if (dim > 80) throw errors.validation(`${at}.dim`, 'max');
    }
    flag(look, at, 'blur');
    for (const name of FLAGS[screen]) flag(look, at, name);
    for (const [name, max] of TEXTS[screen]) {
      const text = look[name];
      if (!present(text)) continue;
      if (!isObj(text)) throw errors.validation(`${at}.${name}`, 'schema');
      for (const lang of ['ru', 'uz', 'en']) {
        const line = text[lang];
        if (!present(line)) continue;
        if (typeof line !== 'string') throw errors.validation(`${at}.${name}.${lang}`, 'type');
        if (line.trim().length > max) throw errors.validation(`${at}.${name}.${lang}`, 'max');
      }
    }
  }
}

/** What the PCs get, or null when the owner never touched the designer (today's screens). */
export function lookForShell(branding: Obj | null | undefined): ShellLook | null {
  if (!branding || !['lock', 'idle', 'desktop', 'font', 'tint'].some((k) => present(branding[k]))) return null;
  const str = (v: unknown): string | null => (typeof v === 'string' && v.trim() ? v.trim() : null);
  const wallpaper = str(branding['wallpaperUrl']);
  const text = (v: unknown): ShellText | null => {
    if (!isObj(v)) return null;
    const out = { ru: str(v['ru']), uz: str(v['uz']), en: str(v['en']) };
    return out.ru || out.uz || out.en ? out : null;
  };
  const screen = (name: Screen, fallback: string | null): ShellScreenLook => {
    const s = isObj(branding[name]) ? (branding[name] as Obj) : {};
    const logo = isObj(s['logo']) ? (s['logo'] as Obj) : {};
    const position = str(logo['position']);
    const size = str(logo['size']);
    return {
      logo: {
        show: logo['show'] !== false,
        position: position && POSITIONS[name].includes(position) ? position : 'topLeft',
        size: size && SIZES[name].includes(size) ? size : 'm',
        name: logo['name'] !== false,
      },
      wallpaperUrl: str(s['wallpaperUrl']) ?? fallback,
      dim: typeof s['dim'] === 'number' ? Math.min(80, Math.max(0, Math.round(s['dim']))) : null,
      blur: typeof s['blur'] === 'boolean' ? s['blur'] : null,
      clock: s['clock'] !== false,
      language: s['language'] !== false,
      prices: s['prices'] !== false,
      freeBadge: s['freeBadge'] !== false,
      title: text(s['title']),
      subtitle: text(s['subtitle']),
      cta: text(s['cta']),
    };
  };
  const font = str(branding['font']);
  const tint = str(branding['tint']);
  return {
    lock: screen('lock', wallpaper),
    idle: screen('idle', null),
    desktop: screen('desktop', wallpaper),
    font: font && FONTS.includes(font) ? font : 'unbounded',
    tint: tint && HEX.test(tint) ? tint.toUpperCase() : null,
  };
}
