import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { ShellLook } from '@clubshell/contracts';
import { contrastRatio } from '../theme/colors.ts';
import {
  blurFilter,
  displayFontStack,
  logoBox,
  lookFromBranding,
  lookText,
  onServer,
  resolveScreen,
  safeTint,
  veil,
} from './look.ts';

const look = (branding: Record<string, unknown>, wallpaper: string | null = null): ShellLook => {
  const l = lookFromBranding(branding, wallpaper);
  assert.ok(l);
  return l;
};

test('without a look every screen keeps today’s layout and the club wallpaper', () => {
  const lock = resolveScreen(null, 'lock', '/w.jpg');
  assert.deepEqual(lock.logo, { show: true, position: 'topLeft', size: 'm', name: true });
  assert.equal(lock.wallpaperUrl, '/w.jpg');
  assert.equal(lock.dim, null);
  assert.equal(lock.blur, null);
  assert.ok(lock.clock && lock.language && lock.prices && lock.freeBadge);
  assert.equal(resolveScreen(undefined, 'idle').wallpaperUrl, null);
  assert.equal(lookFromBranding({ clubName: 'X', accent: '#000000' }, null), null);
});

test('today’s sizes are the m of each screen', () => {
  assert.equal(logoBox('lock', 'm'), '1.75rem');
  assert.equal(logoBox('idle', 'm'), '2rem');
  assert.equal(logoBox('desktop', 'm'), '1.5rem');
});

test('a position the screen cannot take falls back to the top left', () => {
  const l = look({
    lock: { logo: { position: 'bottomRight', size: 'xl' } },
    desktop: { logo: { position: 'center' } },
  });
  assert.equal(resolveScreen(l, 'lock').logo.position, 'topLeft');
  assert.equal(resolveScreen(l, 'lock').logo.size, 'xl');
  assert.equal(resolveScreen(l, 'desktop').logo.position, 'topLeft');
  // A forged look straight from shell.json is checked as well.
  const forged = { ...l, desktop: { ...l.desktop, logo: { ...l.desktop.logo, size: 'xl', position: 'topRight' } } };
  assert.equal(resolveScreen(forged, 'desktop').logo.size, 'm');
  assert.equal(resolveScreen(forged, 'desktop').logo.position, 'topLeft');
});

test('the idle logo takes the bottom left only when the price list is off', () => {
  const withPrices = look({ idle: { logo: { position: 'bottomLeft' } } });
  assert.equal(resolveScreen(withPrices, 'idle').logo.position, 'topLeft');
  const noPrices = look({ idle: { logo: { position: 'bottomLeft' }, prices: false } });
  assert.equal(resolveScreen(noPrices, 'idle').logo.position, 'bottomLeft');
  assert.equal(resolveScreen(noPrices, 'idle').prices, false);
});

test('each screen takes its own picture, the lock and desktop fall back to the club wallpaper, the idle one does not', () => {
  const l = look({ lock: { wallpaperUrl: '/lock.jpg', dim: 95, blur: true }, idle: {} }, '/club.jpg');
  const lock = resolveScreen(l, 'lock');
  assert.equal(lock.wallpaperUrl, '/lock.jpg');
  assert.equal(lock.dim, 0.8);
  assert.equal(lock.blur, true);
  assert.equal(resolveScreen(l, 'desktop').wallpaperUrl, '/club.jpg');
  assert.equal(resolveScreen(l, 'idle').wallpaperUrl, null);
});

test('the owner’s text is used per language, an empty one falls back to the bundled line', () => {
  const l = look({ lock: { title: { ru: '  Привет ', uz: '', en: null } } });
  const title = resolveScreen(l, 'lock').title;
  assert.equal(lookText(title, 'ru'), 'Привет');
  assert.equal(lookText(title, 'uz'), null);
  assert.equal(lookText(title, 'en'), null);
  assert.equal(lookText(null, 'ru'), null);
});

test('a look picture on the club server gets the server address of the club’s own pictures', () => {
  const anchors = [null, 'http://10.0.0.2:5080/api/v1/media/abc'];
  assert.equal(onServer('/api/v1/media/x', anchors), 'http://10.0.0.2:5080/api/v1/media/x');
  assert.equal(onServer('https://cdn.example/x.jpg', anchors), 'https://cdn.example/x.jpg');
  assert.equal(onServer('/mock-art/x.jpg', ['/mock-art/y.jpg']), '/mock-art/x.jpg');
  assert.equal(onServer('//evil/x', anchors), '//evil/x');
  assert.equal(onServer(null, anchors), null);
});

test('a tint the theme text cannot read on is darkened or lightened until it can', () => {
  const darkText = '#0F172A';
  const lightText = '#E8F1F6';
  assert.equal(safeTint('#102030', lightText), '#102030');
  const fixedForLight = safeTint('#C0C8D0', lightText);
  assert.ok(fixedForLight && contrastRatio(lightText, fixedForLight) >= 4.5);
  const fixedForDark = safeTint('#203040', darkText);
  assert.ok(fixedForDark && contrastRatio(darkText, fixedForDark) >= 4.5);
  assert.equal(safeTint('red', lightText), null);
  assert.equal(safeTint(null, lightText), null);
});

test('fonts are only the bundled ones; veil and blur keep the screen’s own when unset', () => {
  assert.match(displayFontStack('mono'), /JetBrains Mono/);
  assert.match(displayFontStack('inter'), /Inter/);
  assert.match(displayFontStack('https://fonts.example/x.woff2'), /Unbounded/);
  assert.equal(veil(null, 'own'), 'own');
  assert.match(veil(0.4, 'own'), /0\.40/);
  assert.equal(blurFilter(null, 'theme'), 'theme');
  assert.equal(blurFilter(false, 'theme'), 'none');
  assert.equal(blurFilter(true, 'theme'), 'blur(16px)');
});

test('the mock’s look reads the console’s storage shape as the server does', () => {
  const l = look({ font: 'comic', tint: '#abcdef', idle: { cta: { en: 'Tap' }, clock: false } });
  assert.equal(l.font, 'unbounded');
  assert.equal(l.tint, '#ABCDEF');
  assert.equal(l.idle.clock, false);
  assert.equal(l.idle.cta?.en, 'Tap');
  assert.equal(l.lock.clock, true);
});
