/**
 * The player-screen designer of «Интерфейс и логотипы» (D-104): the rules each screen keeps (where the logo may stand,
 * how large), the colour checks, and the live preview — a faithful miniature of the PC's lock screen, idle screen and
 * desktop, drawn on a 1920×1080 canvas and scaled to the panel, so what the owner sees is what the hall sees.
 */
import { useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import type { ClubSettings, LogoPosition, LogoSize, LookText, PcLabelSettings, ScreenLookSettings } from '@/api';
import { mediaUrl } from '@/api';

export type Screen = 'lock' | 'idle' | 'desktop';
export type Lang = 'ru' | 'uz' | 'en';
type Branding = ClubSettings['branding'];
export type ThemeId = NonNullable<Branding['theme']>;
export type FontId = NonNullable<Branding['font']>;

export interface ThemeSwatch {
  id: ThemeId;
  name: string;
  hint: string;
  bg: string;
  panel: string;
  text: string;
  muted: string;
  accent: string;
}

/** The bundled shell themes: background, panel, text, muted text and the accent the theme brings. */
export const THEMES: ThemeSwatch[] = [
  {
    id: 'default',
    name: 'Obsidian',
    hint: 'Тёмная, по умолчанию',
    bg: '#07090C',
    panel: '#0D1117',
    text: '#E8F1F6',
    muted: '#9AA6B2',
    accent: '#9ADFFF',
  },
  {
    id: 'light',
    name: 'Daylight',
    hint: 'Светлая, для светлого зала',
    bg: '#EEF2F6',
    panel: '#FFFFFF',
    text: '#0F172A',
    muted: '#465363',
    accent: '#0369A1',
  },
  {
    id: 'neon',
    name: 'Neon Night',
    hint: 'Тёмная, неоновая',
    bg: '#07060F',
    panel: '#120F24',
    text: '#FAF5FF',
    muted: '#9A92BE',
    accent: '#F0ABFC',
  },
];

/** The headings fonts bundled with the shell (no remote fonts on the PCs). */
export const FONTS: { id: FontId; name: string; stack: string }[] = [
  { id: 'unbounded', name: 'Unbounded', stack: '"Unbounded Variable", "Unbounded", sans-serif' },
  { id: 'inter', name: 'Inter', stack: '"Inter Variable", "Inter", sans-serif' },
  { id: 'mono', name: 'JetBrains Mono', stack: '"JetBrains Mono Variable", "JetBrains Mono", monospace' },
];

/** Where the logo may stand: never over the sign-in card, the clock, the prices or the top bar's tabs. */
export const POSITIONS: Record<Screen, LogoPosition[]> = {
  lock: ['topLeft', 'topCenter', 'topRight', 'center'],
  idle: ['topLeft', 'topCenter', 'topRight', 'center', 'bottomLeft', 'bottomRight'],
  desktop: ['topLeft'],
};

export const SIZES: Record<Screen, LogoSize[]> = {
  lock: ['s', 'm', 'l', 'xl'],
  idle: ['s', 'm', 'l', 'xl'],
  desktop: ['s', 'm', 'l'],
};

export const POSITION_NAMES: Record<LogoPosition, string> = {
  topLeft: 'Слева вверху',
  topCenter: 'По центру вверху',
  topRight: 'Справа вверху',
  center: 'В центре',
  bottomLeft: 'Слева внизу',
  bottomRight: 'Справа внизу',
};

/** The dimming each screen has without the owner's: lock 60 %, desktop 55 %, idle its own soft veil. */
export const DEFAULT_DIM: Record<Screen, number> = { lock: 60, idle: 40, desktop: 55 };
export const MAX_DIM = 80;

/** The shell's own lines (what a PC says when the owner leaves a text empty) and the preview's chrome, per language. */
export const SHELL_TEXT: Record<Lang, Record<string, string>> = {
  ru: {
    title: 'Добро пожаловать', // i18n-ignore
    subtitle: 'Войдите, чтобы начать играть', // i18n-ignore
    cta: 'Коснитесь, чтобы начать', // i18n-ignore
    ctaHint: 'Коснитесь экрана или нажмите любую клавишу', // i18n-ignore
    free: 'Это место свободно', // i18n-ignore
    callAdmin: 'Позвать администратора', // i18n-ignore
    restart: 'Перезагрузить', // i18n-ignore
    power: 'Выключить', // i18n-ignore
    qr: 'QR-код', // i18n-ignore
    password: 'Пароль', // i18n-ignore
    card: 'Карта', // i18n-ignore
    scan: 'Отсканируйте в приложении клуба, чтобы войти', // i18n-ignore
    waiting: 'Ожидание сканирования…', // i18n-ignore
    newCode: 'Получить новый код', // i18n-ignore
    prices: 'Тарифы', // i18n-ignore
    perHour: 'сум / ч', // i18n-ignore
    home: 'Главная', // i18n-ignore
    games: 'Игры', // i18n-ignore
    shop: 'Магазин', // i18n-ignore
    wallet: 'Кошелёк', // i18n-ignore
    play: 'Играть сейчас', // i18n-ignore
    pcWord: 'ПК', // i18n-ignore
  },
  uz: {
    title: 'Xush kelibsiz',
    subtitle: "O'ynashni boshlash uchun tizimga kiring",
    cta: 'Boshlash uchun tegining',
    ctaHint: 'Boshlash uchun ekranga tegining yoki istalgan tugmani bosing',
    free: "Bu o'rin bo'sh",
    callAdmin: 'Administratorni chaqirish',
    restart: 'Qayta yuklash',
    power: "O'chirish",
    qr: 'QR-kod',
    password: 'Parol',
    card: 'Karta',
    scan: 'Kirish uchun klub ilovasida skanerlang',
    waiting: 'Skanerlash kutilmoqda…',
    newCode: 'Yangi kod olish',
    prices: 'Tariflar',
    perHour: "so'm / soat",
    home: 'Bosh sahifa',
    games: "O'yinlar",
    shop: "Do'kon",
    wallet: 'Hamyon',
    play: "Hozir o'ynash",
    pcWord: 'PK',
  },
  en: {
    title: 'Welcome',
    subtitle: 'Sign in to start playing',
    cta: 'Touch to start',
    ctaHint: 'Touch the screen or press any key to start',
    free: 'This seat is free',
    callAdmin: 'Call administrator',
    restart: 'Restart',
    power: 'Shut down',
    qr: 'QR code',
    password: 'Password',
    card: 'Card',
    scan: 'Scan in the club app to sign in',
    waiting: 'Waiting for the scan…',
    newCode: 'Get a new code',
    prices: 'Prices',
    perHour: 'sum / h',
    home: 'Home',
    games: 'Games',
    shop: 'Shop',
    wallet: 'Wallet',
    play: 'Play now',
    pcWord: 'PC',
  },
};

/** Longest owner text per field (as the server checks it). */
export const TEXT_MAX: Record<'title' | 'subtitle' | 'cta', number> = { title: 60, subtitle: 120, cta: 60 };

// ---------------------------------------------------------------------------------------------------------------------
// Colour math (as the shell's theme/colors.ts)
// ---------------------------------------------------------------------------------------------------------------------

export const HEX = /^#[0-9a-fA-F]{6}$/;

function rgb(hex: string): [number, number, number] {
  const h = hex.replace('#', '');
  return [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16)];
}

export function luminance(hex: string): number {
  const [r, g, b] = rgb(hex).map((c) => {
    const v = c / 255;
    return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
  }) as [number, number, number];
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

export function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [number, number];
  return (hi + 0.05) / (lo + 0.05);
}

function shade(hex: string, amount: number): string {
  const target = amount < 0 ? 0 : 255;
  const k = Math.min(1, Math.abs(amount));
  return `#${rgb(hex)
    .map((c) =>
      Math.round(c + (target - c) * k)
        .toString(16)
        .padStart(2, '0'),
    )
    .join('')}`.toUpperCase();
}

/** The text on an accent button: the theme background or white, whichever reads better (as the shell does). */
export function onAccent(accent: string, bg: string): string {
  return contrast(bg, accent) >= contrast('#FFFFFF', accent) ? bg : '#FFFFFF';
}

/** The owner's tint made readable for the theme's text (4.5:1), as the PC will show it; `null` — no tint. */
export function safeTint(tint: string | null | undefined, text: string): string | null {
  if (!tint || !HEX.test(tint)) return null;
  const toward = luminance(text) > 0.4 ? -1 : 1;
  for (let i = 0; i <= 40; i += 1) {
    const c = shade(tint, (toward * i) / 40);
    if (contrast(text, c) >= 4.5) return c;
  }
  return toward < 0 ? '#000000' : '#FFFFFF';
}

function alpha(hex: string, a: number): string {
  const [r, g, b] = rgb(hex);
  return `rgba(${r}, ${g}, ${b}, ${a})`;
}

// ---------------------------------------------------------------------------------------------------------------------
// Resolved layout (as the shell's lib/look.ts)
// ---------------------------------------------------------------------------------------------------------------------

export interface Resolved {
  show: boolean;
  position: LogoPosition;
  size: LogoSize;
  name: boolean;
  wallpaper: string | null;
  dim: number | null;
  blur: boolean | null;
  clock: boolean;
  language: boolean;
  prices: boolean;
  freeBadge: boolean;
}

export function resolve(branding: Branding, screen: Screen): Resolved {
  const s: ScreenLookSettings = branding[screen] ?? {};
  const prices = s.prices !== false;
  let position = POSITIONS[screen].includes(s.logo?.position as LogoPosition)
    ? (s.logo?.position as LogoPosition)
    : 'topLeft';
  if (screen === 'idle' && position === 'bottomLeft' && prices) position = 'topLeft';
  return {
    show: s.logo?.show !== false,
    position,
    size: SIZES[screen].includes(s.logo?.size as LogoSize) ? (s.logo?.size as LogoSize) : 'm',
    name: s.logo?.name !== false,
    wallpaper: s.wallpaperUrl || (screen === 'idle' ? null : branding.wallpaperUrl) || null,
    dim: typeof s.dim === 'number' ? Math.min(MAX_DIM, Math.max(0, s.dim)) : null,
    blur: typeof s.blur === 'boolean' ? s.blur : null,
    clock: s.clock !== false,
    language: s.language !== false,
    prices,
    freeBadge: s.freeBadge !== false,
  };
}

export function textOf(line: LookText | undefined, lang: Lang, fallback: string): string {
  const v = line?.[lang]?.trim();
  return v ? v : fallback;
}

// ---------------------------------------------------------------------------------------------------------------------
// Preview
// ---------------------------------------------------------------------------------------------------------------------

/** A stand-in QR code: the three finder squares and a fixed pseudo-random field (no real link in a preview). */
function FakeQr(): JSX.Element {
  const n = 25;
  const cells: JSX.Element[] = [];
  let seed = 7;
  const finder = (x: number, y: number): boolean => {
    const at = (cx: number, cy: number): boolean => x >= cx && x < cx + 7 && y >= cy && y < cy + 7;
    return at(0, 0) || at(n - 7, 0) || at(0, n - 7);
  };
  for (let y = 0; y < n; y += 1) {
    for (let x = 0; x < n; x += 1) {
      seed = (seed * 1103515245 + 12345) % 2147483648;
      let on: boolean;
      if (finder(x, y)) {
        const lx = x >= n - 7 ? x - (n - 7) : x;
        const ly = y >= n - 7 ? y - (n - 7) : y;
        const ring = Math.max(Math.abs(lx - 3), Math.abs(ly - 3));
        on = ring !== 2;
      } else {
        const nearFinder = (x < 8 && y < 8) || (x >= n - 8 && y < 8) || (x < 8 && y >= n - 8);
        on = !nearFinder && seed % 100 < 48;
      }
      if (on) cells.push(<rect key={`${x}-${y}`} x={x} y={y} width={1} height={1} />);
    }
  }
  return (
    <svg viewBox={`0 0 ${n} ${n}`} width="100%" height="100%" shapeRendering="crispEdges" fill="#0B0F14">
      {cells}
    </svg>
  );
}

/** A 1920×1080 canvas scaled to its box: the miniature keeps the PC's proportions at any panel width. */
function Canvas({ children, label }: { children: ReactNode; label: string }): JSX.Element {
  const box = useRef<HTMLDivElement>(null);
  const [scale, setScale] = useState(0.3);
  useLayoutEffect(() => {
    const el = box.current;
    if (!el) return undefined;
    const measure = (): void => setScale(el.clientWidth / 1920 || 0.3);
    measure();
    if (typeof ResizeObserver === 'undefined') return undefined;
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  return (
    <div
      ref={box}
      role="img"
      aria-label={label}
      className="relative aspect-video w-full overflow-hidden rounded-md border border-text/10 shadow-[0_12px_40px_rgb(0_0_0/0.35)]"
    >
      <div
        className="absolute left-0 top-0 origin-top-left overflow-hidden"
        style={{ width: 1920, height: 1080, transform: `scale(${scale})` }}
      >
        {children}
      </div>
    </div>
  );
}

export interface PreviewProps {
  screen: Screen;
  branding: Branding;
  features: { callAdmin?: boolean; lockScreenPower?: boolean | null };
  pcLabel: PcLabelSettings;
  lang: Lang;
  label: string;
}

/** Logo box (virtual px at 1920×1080) per screen and size, as the shell's `logoBox`. */
const LOGO_PX: Record<Screen, Record<LogoSize, number>> = {
  lock: { s: 20, m: 28, l: 50, xl: 86 },
  idle: { s: 24, m: 32, l: 59, xl: 108 },
  desktop: { s: 20, m: 24, l: 32, xl: 32 },
};
const NAME_PX: Record<Screen, Record<LogoSize, number>> = {
  lock: { s: 20, m: 24, l: 30, xl: 36 },
  idle: { s: 24, m: 30, l: 36, xl: 48 },
  desktop: { s: 14, m: 14, l: 16, xl: 16 },
};

const MONO = '"JetBrains Mono Variable", "JetBrains Mono", monospace';
const DOT = '"Doto Variable", "Doto", monospace';
const SANS = '"Inter Variable", "Inter", sans-serif';

/** The miniature of one PC screen with the owner's draft applied. */
export function ScreenPreview({ screen, branding, features, pcLabel, lang, label }: PreviewProps): JSX.Element {
  const theme = THEMES.find((x) => x.id === (branding.theme ?? 'default')) ?? THEMES[0]!;
  const accent = HEX.test(branding.accent) ? branding.accent : theme.accent;
  const plate = safeTint(branding.tint, theme.text) ?? theme.bg;
  const display = FONTS.find((f) => f.id === (branding.font ?? 'unbounded'))?.stack ?? FONTS[0]!.stack;
  const r = resolve(branding, screen);
  const words = SHELL_TEXT[lang];
  const logoUrl = mediaUrl(branding.logoUrl);
  const wallpaper = mediaUrl(r.wallpaper);
  const look = branding[screen] ?? {};
  const pcSample =
    pcLabel.format === 'number' ? `${words.pcWord} 05` : pcLabel.format === 'name' ? 'PC-05' : 'PC-05 · VIP';
  const labelOn = screen === 'lock' ? pcLabel.lock : screen === 'idle' ? pcLabel.idle : pcLabel.topBar;

  const mark = (px: number): JSX.Element =>
    logoUrl ? (
      <img src={logoUrl} alt="" style={{ width: px, height: px, objectFit: 'contain', flexShrink: 0 }} />
    ) : (
      <span
        style={{
          width: px * 0.8,
          height: px * 0.8,
          margin: px * 0.1,
          border: `${Math.max(2, px / 14)}px solid ${alpha(accent, 0.75)}`,
          transform: 'rotate(45deg)',
          flexShrink: 0,
        }}
      />
    );

  const pcLine = (align: 'start' | 'center' | 'end'): ReactNode =>
    labelOn &&
    (pcLabel.large && screen !== 'desktop' ? (
      <div style={{ display: 'flex', alignItems: 'flex-end', gap: 12, marginTop: 10, justifyContent: align }}>
        <span style={{ fontFamily: display, fontWeight: 300, fontSize: 72, lineHeight: 1, color: theme.text }}>05</span>
        <span style={{ fontFamily: MONO, fontSize: 13, letterSpacing: '0.2em', color: theme.muted, paddingBottom: 6 }}>
          {pcLabel.format === 'number' ? words.pcWord : pcSample.toUpperCase()}
        </span>
      </div>
    ) : (
      <div
        style={{
          fontFamily: MONO,
          fontSize: screen === 'desktop' ? 11 : 13,
          letterSpacing: '0.2em',
          color: theme.muted,
          marginTop: screen === 'desktop' ? 3 : 10,
          textAlign: align === 'start' ? 'left' : align === 'end' ? 'right' : 'center',
          textTransform: 'uppercase',
        }}
      >
        {pcSample}
      </div>
    ));

  const brand = (s: 'lock' | 'idle'): ReactNode => {
    const stacked = r.position === 'center';
    const align =
      r.position === 'topCenter' || stacked
        ? 'center'
        : r.position === 'topRight' || r.position === 'bottomRight'
          ? 'end'
          : 'start';
    const name = r.name && (
      <span
        style={{
          fontFamily: display,
          fontWeight: 300,
          fontSize: NAME_PX[s][r.size],
          letterSpacing: '-0.02em',
          color: theme.text,
          whiteSpace: 'nowrap',
          lineHeight: 1.15,
        }}
      >
        {branding.clubName || 'ClubShell'}
      </span>
    );
    const row = (
      <div
        style={{
          display: 'flex',
          flexDirection: stacked ? 'column' : 'row',
          alignItems: 'center',
          gap: stacked ? 14 : s === 'lock' ? 12 : 16,
          justifyContent: align,
        }}
      >
        {r.show && mark(LOGO_PX[s][r.size])}
        {s === 'idle' && !stacked ? (
          <div>
            {name}
            {pcLine(align)}
          </div>
        ) : (
          name
        )}
      </div>
    );
    if (!r.show && !r.name && !labelOn) return null;
    const body = (
      <>
        {(r.show || r.name) && row}
        {(s === 'lock' || stacked || (!r.show && !r.name)) && pcLine(align)}
      </>
    );
    return s === 'idle' ? (
      <div
        data-testid="preview-brand"
        data-position={r.position}
        style={{
          display: 'inline-flex',
          flexDirection: 'column',
          alignItems: stacked ? 'center' : 'stretch',
          background: alpha(plate, 0.62),
          backdropFilter: 'blur(12px)',
          borderRadius: 16,
          padding: stacked ? '18px 28px' : '8px 20px 8px 12px',
        }}
      >
        {body}
      </div>
    ) : (
      <div
        data-testid="preview-brand"
        data-position={r.position}
        style={{ textAlign: align === 'start' ? 'left' : align }}
      >
        {body}
      </div>
    );
  };

  // The tint lies only under the lock and idle screens; the desktop keeps the theme's background.
  const under = screen === 'desktop' ? theme.bg : plate;
  const veil = (fallback: number): string => {
    // The shell's own veils without the owner's dimming: the idle screen's soft one, the backdrop's on the others.
    if (r.dim === null && screen === 'idle') {
      return `linear-gradient(180deg, ${alpha(under, 0.7)} 0%, ${alpha(under, 0.25)} 35%, ${alpha(under, 0.35)} 60%, ${alpha(under, 0.9)} 100%)`;
    }
    if (r.dim === null) {
      const d = fallback / 100;
      return `linear-gradient(180deg, ${alpha(under, d * 0.6)} 0%, ${alpha(under, d)} 55%, ${alpha(under, Math.min(1, d + 0.35))} 100%)`;
    }
    // As the shell's `veil`: the owner's dimming in the middle, the top and bottom never under 55 % / 80 %.
    const d = r.dim / 100;
    const top = Math.min(1, Math.max(0.55, d + 0.15));
    const bottom = Math.min(1, Math.max(0.8, d + 0.3));
    return `linear-gradient(180deg, ${alpha(under, top)} 0%, ${alpha(under, d)} 35%, ${alpha(under, d)} 60%, ${alpha(under, bottom)} 100%)`;
  };

  const backdrop = (fallbackDim: number, placeholder: ReactNode = null): ReactNode => (
    <div style={{ position: 'absolute', inset: 0, background: theme.bg }}>
      {wallpaper ? (
        <img
          src={wallpaper}
          alt=""
          style={{
            position: 'absolute',
            inset: 0,
            width: '100%',
            height: '100%',
            objectFit: 'cover',
            filter: r.blur ? 'blur(16px)' : undefined,
            transform: r.blur ? 'scale(1.04)' : undefined,
          }}
        />
      ) : (
        placeholder
      )}
      <div style={{ position: 'absolute', inset: 0, background: veil(fallbackDim) }} />
    </div>
  );

  const clock = (px: number): JSX.Element => (
    <span style={{ fontFamily: DOT, fontSize: px, lineHeight: 1, color: theme.text, letterSpacing: '0.02em' }}>
      19:55
    </span>
  );

  const root: CSSProperties = {
    position: 'absolute',
    inset: 0,
    fontFamily: SANS,
    color: theme.text,
    background: theme.bg,
  };

  if (screen === 'lock') {
    const at = r.position;
    const cluster = (r.language || r.clock) && (
      <div style={{ display: 'flex', alignItems: 'center', gap: 24 }}>
        {r.language && (
          <div
            style={{
              display: 'flex',
              gap: 4,
              padding: 6,
              borderRadius: 12,
              background: alpha(theme.panel, 0.85),
              border: `1px solid ${alpha(theme.text, 0.1)}`,
            }}
          >
            {(['en', 'ru', 'uz'] as const).map((l) => (
              <span
                key={l}
                style={{
                  fontFamily: MONO,
                  fontSize: 14,
                  letterSpacing: '0.12em',
                  padding: '10px 18px',
                  borderRadius: 8,
                  color: l === lang ? accent : theme.muted,
                  background: l === lang ? alpha(accent, 0.14) : 'transparent',
                }}
              >
                {l.toUpperCase()}
              </span>
            ))}
          </div>
        )}
        {r.clock && clock(36)}
      </div>
    );
    const tabs = [words.qr, words.password, words.card];
    const card = (
      <div
        style={{
          width: 544,
          padding: 32,
          borderRadius: 20,
          background: alpha(theme.panel, 0.86),
          border: `1px solid ${alpha(theme.text, 0.1)}`,
          boxShadow: '0 30px 80px rgba(0,0,0,0.35)',
          position: 'relative',
          flexShrink: 1,
          minHeight: 0,
          overflow: 'hidden',
        }}
      >
        <div
          style={{
            position: 'absolute',
            left: 32,
            right: 32,
            top: 0,
            height: 1,
            background: `linear-gradient(90deg, transparent, ${alpha(accent, 0.85)}, transparent)`,
          }}
        />
        <div style={{ textAlign: 'center', marginBottom: 24 }}>
          <div
            data-testid="preview-title"
            style={{ fontFamily: display, fontWeight: 300, fontSize: 30, letterSpacing: '-0.02em', color: theme.text }}
          >
            {textOf(look.title, lang, words.title)}
          </div>
          <div data-testid="preview-subtitle" style={{ fontSize: 16, color: theme.muted, marginTop: 6 }}>
            {textOf(look.subtitle, lang, words.subtitle)}
          </div>
        </div>
        <div style={{ display: 'flex', gap: 6, marginBottom: 24 }}>
          {tabs.map((tab, i) => (
            <span
              key={tab}
              style={{
                flex: 1,
                textAlign: 'center',
                padding: '14px 0',
                borderRadius: 10,
                fontSize: 17,
                fontWeight: 500,
                color: i === 0 ? theme.text : theme.muted,
                border: `1.5px solid ${i === 0 ? accent : 'transparent'}`,
                background: i === 0 ? alpha(accent, 0.08) : 'transparent',
              }}
            >
              {tab}
            </span>
          ))}
        </div>
        <div style={{ textAlign: 'center', fontSize: 17, color: theme.muted, marginBottom: 18 }}>{words.scan}</div>
        <div
          style={{
            width: 260,
            height: 260,
            margin: '0 auto',
            borderRadius: 14,
            background: '#FFFFFF',
            padding: 20,
            boxShadow: `0 0 40px ${alpha(accent, 0.25)}`,
          }}
        >
          <FakeQr />
        </div>
        <div style={{ textAlign: 'center', fontSize: 17, color: theme.text, marginTop: 26 }}>{words.waiting}</div>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 14, marginTop: 18 }}>
          <span style={{ width: 176, height: 4, borderRadius: 4, background: accent }} />
          <span style={{ fontSize: 15, color: theme.muted }}>01:59</span>
        </div>
        <div style={{ textAlign: 'center', fontSize: 17, fontWeight: 600, color: theme.text, marginTop: 30 }}>
          {words.newCode}
        </div>
      </div>
    );
    const footButton = (text: string): JSX.Element => (
      <span style={{ fontSize: 18, fontWeight: 600, color: theme.text, padding: '12px 20px' }}>{text}</span>
    );
    return (
      <Canvas label={label}>
        <div style={root} data-testid="preview-lock">
          {backdrop(DEFAULT_DIM.lock)}
          <div
            style={{
              position: 'absolute',
              inset: 0,
              padding: '23px 38px',
              display: 'flex',
              flexDirection: 'column',
              gap: 23,
            }}
          >
            <div
              style={{
                display: 'grid',
                gridTemplateColumns: '1fr auto 1fr',
                alignItems: 'start',
                minHeight: 52,
              }}
            >
              <div style={{ justifySelf: 'start' }}>
                {at === 'topLeft' ? brand('lock') : at === 'topRight' ? cluster : null}
              </div>
              <div>{at === 'topCenter' && brand('lock')}</div>
              <div style={{ justifySelf: 'end' }}>{at === 'topRight' ? brand('lock') : cluster}</div>
            </div>
            <div
              style={{
                flex: 1,
                minHeight: 0,
                display: 'flex',
                flexDirection: 'column',
                alignItems: 'center',
                justifyContent: 'center',
                gap: 24,
              }}
            >
              {at === 'center' && brand('lock')}
              {card}
            </div>
            <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 24, minHeight: 48 }}>
              {features.lockScreenPower !== false && (
                <>
                  {footButton(words.restart)}
                  {footButton(words.power)}
                </>
              )}
              {features.callAdmin !== false && footButton(words.callAdmin)}
            </div>
          </div>
        </div>
      </Canvas>
    );
  }

  if (screen === 'idle') {
    const at = r.position;
    const badge = r.freeBadge && (
      <span
        style={{
          display: 'inline-flex',
          alignItems: 'center',
          gap: 10,
          padding: '9px 16px',
          borderRadius: 999,
          background: alpha(plate, 0.75),
          fontFamily: MONO,
          fontSize: 13,
          letterSpacing: '0.2em',
          textTransform: 'uppercase',
          color: '#4ADE80',
        }}
      >
        <span style={{ width: 9, height: 9, borderRadius: 9, background: '#22C55E' }} />
        {words.free}
      </span>
    );
    const placeholder = (
      <div
        style={{
          position: 'absolute',
          inset: 0,
          background: `radial-gradient(60% 80% at 75% 35%, ${alpha(accent, 0.28)}, transparent 70%), linear-gradient(135deg, ${theme.panel}, ${theme.bg})`,
        }}
      />
    );
    return (
      <Canvas label={label}>
        <div style={root} data-testid="preview-idle">
          {backdrop(DEFAULT_DIM.idle, placeholder)}
          <div
            style={{
              position: 'absolute',
              inset: 0,
              padding: '46px 57px',
              display: 'flex',
              flexDirection: 'column',
              justifyContent: 'space-between',
            }}
          >
            <div style={{ display: 'grid', gridTemplateColumns: '1fr auto 1fr', alignItems: 'start', minHeight: 40 }}>
              <div style={{ justifySelf: 'start' }}>
                {at === 'topLeft' ? brand('idle') : at === 'topRight' ? badge : null}
              </div>
              <div>{at === 'topCenter' && brand('idle')}</div>
              <div style={{ justifySelf: 'end' }}>{at === 'topRight' ? brand('idle') : badge}</div>
            </div>
            <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 22 }}>
              {at === 'center' && brand('idle')}
              {r.clock && (
                <div
                  style={{
                    background: alpha(plate, 0.62),
                    borderRadius: 32,
                    padding: '14px 48px',
                    backdropFilter: 'blur(12px)',
                  }}
                >
                  {clock(at === 'center' ? 190 : 250)}
                </div>
              )}
            </div>
            <div style={{ display: 'flex', alignItems: 'flex-end', justifyContent: 'space-between', gap: 24 }}>
              <div style={{ width: 672 }}>
                {r.prices && (
                  <div
                    style={{
                      background: alpha(theme.panel, 0.9),
                      borderRadius: 18,
                      padding: 30,
                      border: `1px solid ${alpha(theme.text, 0.08)}`,
                    }}
                  >
                    <div style={{ fontFamily: display, fontSize: 28, fontWeight: 300, marginBottom: 18 }}>
                      {words.prices}
                    </div>
                    {['Standard', 'VIP'].map((n, i) => (
                      <div
                        key={n}
                        style={{
                          display: 'flex',
                          justifyContent: 'space-between',
                          padding: '18px 18px',
                          marginTop: 10,
                          borderRadius: 10,
                          background: alpha(theme.text, 0.05),
                          fontSize: 22,
                          fontWeight: 600,
                        }}
                      >
                        <span>{n}</span>
                        <span style={{ fontFamily: DOT, fontWeight: 400 }}>
                          {i === 0 ? '12 000' : '20 000'}{' '}
                          <span style={{ fontFamily: SANS, fontSize: 15, color: theme.muted }}>{words.perHour}</span>
                        </span>
                      </div>
                    ))}
                  </div>
                )}
                {at === 'bottomLeft' && brand('idle')}
              </div>
              <div
                style={{
                  display: 'flex',
                  flexDirection: 'column',
                  alignItems: 'flex-end',
                  gap: 12,
                  paddingBottom: 23,
                  textAlign: 'right',
                }}
              >
                {at === 'bottomRight' && <div style={{ marginBottom: 23 }}>{brand('idle')}</div>}
                <div
                  data-testid="preview-cta"
                  style={{ fontFamily: display, fontWeight: 300, fontSize: 46, letterSpacing: '-0.02em' }}
                >
                  {textOf(look.cta, lang, words.cta)}
                  <span
                    style={{
                      display: 'inline-block',
                      width: '0.45em',
                      height: '0.8em',
                      marginLeft: 10,
                      background: accent,
                      verticalAlign: '-0.08em',
                    }}
                  />
                </div>
                <div style={{ fontFamily: MONO, fontSize: 13, letterSpacing: '0.2em', textTransform: 'uppercase' }}>
                  {words.ctaHint}
                </div>
              </div>
            </div>
          </div>
        </div>
      </Canvas>
    );
  }

  const nav = [words.home, words.games, words.shop, words.wallet];
  return (
    <Canvas label={label}>
      <div style={root} data-testid="preview-desktop">
        {backdrop(DEFAULT_DIM.desktop)}
        <div
          style={{
            position: 'absolute',
            left: 0,
            right: 0,
            top: 0,
            height: 84,
            padding: '0 38px',
            display: 'grid',
            gridTemplateColumns: '1fr auto 1fr',
            alignItems: 'center',
            background: `linear-gradient(180deg, ${alpha(theme.bg, 0.95)}, ${alpha(theme.bg, 0.85)} 60%, transparent)`,
          }}
        >
          <div data-testid="preview-brand" style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
            {r.show && mark(LOGO_PX.desktop[r.size])}
            <div>
              {r.name && (
                <div style={{ fontFamily: display, fontSize: NAME_PX.desktop[r.size], color: theme.text }}>
                  {branding.clubName || 'ClubShell'}
                </div>
              )}
              {labelOn && (
                <div
                  style={{ fontFamily: MONO, fontSize: 11, letterSpacing: '0.2em', color: theme.muted, marginTop: 3 }}
                >
                  {pcSample.toUpperCase()} · RTX 4070 · 240 HZ
                </div>
              )}
            </div>
          </div>
          <div style={{ display: 'flex', gap: 36 }}>
            {nav.map((n, i) => (
              <span
                key={n}
                style={{
                  fontFamily: display,
                  fontSize: 15,
                  letterSpacing: '0.08em',
                  textTransform: 'uppercase',
                  color: i === 0 ? theme.text : theme.muted,
                  borderBottom: i === 0 ? `2px solid ${accent}` : '2px solid transparent',
                  paddingBottom: 8,
                }}
              >
                {n}
              </span>
            ))}
          </div>
          <div style={{ justifySelf: 'end' }}>{clock(28)}</div>
        </div>
        <div style={{ position: 'absolute', left: 38, bottom: 340 }}>
          <div style={{ fontFamily: display, fontSize: 96, fontWeight: 500, letterSpacing: '-0.03em' }}>
            Counter-Strike 2
          </div>
          <span
            style={{
              display: 'inline-block',
              marginTop: 28,
              padding: '16px 30px',
              borderRadius: 10,
              background: accent,
              color: onAccent(accent, theme.bg),
              fontSize: 20,
              fontWeight: 600,
            }}
          >
            {words.play}
          </span>
        </div>
        <div style={{ position: 'absolute', right: 38, bottom: 340, display: 'flex', gap: 14 }}>
          {[0, 1, 2, 3, 4].map((i) => (
            <span
              key={i}
              style={{
                width: 118,
                height: 177,
                borderRadius: 8,
                background: `linear-gradient(160deg, ${alpha(accent, 0.12 + i * 0.05)}, ${theme.panel})`,
                border: `1px solid ${alpha(theme.text, 0.08)}`,
              }}
            />
          ))}
        </div>
        <div
          style={{
            position: 'absolute',
            left: 38,
            right: 38,
            bottom: 96,
            height: 200,
            display: 'grid',
            gridTemplateColumns: 'repeat(3, 1fr)',
            gap: 24,
          }}
        >
          {[0, 1, 2].map((i) => (
            <span
              key={i}
              style={{
                borderRadius: 16,
                background: alpha(theme.panel, 0.88),
                border: `1px solid ${alpha(theme.text, 0.08)}`,
              }}
            />
          ))}
        </div>
        <div
          style={{
            position: 'absolute',
            left: 0,
            right: 0,
            bottom: 0,
            height: 64,
            background: alpha(theme.panel, 0.96),
            borderTop: `1px solid ${alpha(theme.text, 0.08)}`,
          }}
        />
      </div>
    </Canvas>
  );
}

/** The 16:9 pad the logo's place is picked on: only the places the screen allows are buttons. */
export function PositionPad({
  screen,
  value,
  onChange,
  disabled,
  label,
  names,
}: {
  screen: Screen;
  value: LogoPosition;
  onChange: (v: LogoPosition) => void;
  disabled: Partial<Record<LogoPosition, string>>;
  label: string;
  names: Record<LogoPosition, string>;
}): JSX.Element {
  const cells: (LogoPosition | null)[] = [
    'topLeft',
    'topCenter',
    'topRight',
    null,
    'center',
    null,
    'bottomLeft',
    null,
    'bottomRight',
  ];
  return (
    <div
      role="radiogroup"
      aria-label={label}
      className="grid aspect-video w-60 max-w-full grid-cols-3 grid-rows-3 gap-1 rounded-md border border-text/10 bg-bg/50 p-1.5"
    >
      {cells.map((pos, i) => {
        if (!pos || !POSITIONS[screen].includes(pos)) {
          // The fixed element in that place (the card, the clock), drawn faintly so the owner sees why.
          const fixed = (screen === 'lock' && i >= 6) || (screen === 'desktop' && i > 0);
          return <span key={i} aria-hidden="true" className={fixed ? 'rounded-sm bg-text/[0.04]' : ''} />;
        }
        const on = value === pos;
        const why = disabled[pos];
        return (
          <button
            key={pos}
            type="button"
            role="radio"
            aria-checked={on}
            aria-label={names[pos]}
            title={why ?? names[pos]}
            disabled={Boolean(why)}
            onClick={() => onChange(pos)}
            className={
              'focus-ring flex items-center justify-center rounded-sm border transition-colors disabled:cursor-not-allowed disabled:opacity-40 ' +
              (on ? 'border-accent/70 bg-accent/[0.16]' : 'border-text/10 hover:border-text/30')
            }
          >
            <span aria-hidden="true" className={'h-1.5 w-4 rounded-full ' + (on ? 'bg-accent' : 'bg-text/30')} />
          </button>
        );
      })}
    </div>
  );
}
