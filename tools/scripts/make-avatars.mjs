// Preset avatars a player picks in the shell (Profile): an emoji on a two-tone gradient disc, one SVG per avatar in
// apps/shell/public/avatars/. The ids are the file names; apps/shell/src/lib/avatars.ts lists the same ids. Run again
// after changing the list: node tools/scripts/make-avatars.mjs
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

/** [id, emoji, hue of the gradient's light end, hue of its dark end]. */
const AVATARS = [
  ['wolf', '🐺', 210, 250],
  ['fox', '🦊', 22, 350],
  ['dragon', '🐉', 140, 190],
  ['alien', '👾', 275, 320],
  ['robot', '🤖', 195, 230],
  ['skull', '💀', 0, 280],
  ['ninja', '🥷', 230, 270],
  ['ufo', '👽', 100, 160],
  ['lion', '🦁', 38, 10],
  ['tiger', '🐯', 28, 0],
  ['panda', '🐼', 160, 200],
  ['eagle', '🦅', 200, 30],
  ['crown', '👑', 48, 20],
  ['bolt', '⚡', 55, 280],
  ['fire', '🔥', 15, 340],
  ['target', '🎯', 350, 20],
  ['gamepad', '🎮', 250, 300],
  ['joystick', '🕹️', 300, 330],
  ['shield', '🛡️', 190, 220],
  ['swords', '⚔️', 0, 220],
  ['snake', '🐍', 120, 80],
  ['shark', '🦈', 195, 215],
  ['bear', '🐻', 30, 15],
  ['owl', '🦉', 260, 40],
];

const out = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'apps', 'shell', 'public', 'avatars');
mkdirSync(out, { recursive: true });

for (const [id, emoji, a, b] of AVATARS) {
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
  <defs>
    <linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="hsl(${a} 85% 58%)"/>
      <stop offset="1" stop-color="hsl(${b} 75% 28%)"/>
    </linearGradient>
    <radialGradient id="s" cx="0.3" cy="0.25" r="0.75">
      <stop offset="0" stop-color="#fff" stop-opacity="0.35"/>
      <stop offset="1" stop-color="#fff" stop-opacity="0"/>
    </radialGradient>
  </defs>
  <rect width="128" height="128" fill="url(#g)"/>
  <rect width="128" height="128" fill="url(#s)"/>
  <text x="64" y="66" font-size="72" text-anchor="middle" dominant-baseline="central"
    font-family="'Segoe UI Emoji','Apple Color Emoji','Noto Color Emoji',sans-serif">${emoji}</text>
</svg>
`;
  writeFileSync(join(out, `${id}.svg`), svg, 'utf8');
}

console.log(`${AVATARS.length} avatars in ${out}`);
