/** `pnpm --filter @clubshell/shell test` (Node's own runner, which strips the types itself). */
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import { contrastRatio, onColor, shade, solidFill } from './colors.ts';

const WHITE = '#FFFFFF';

describe('contrastRatio', () => {
  it('spans 1 to 21', () => {
    assert.equal(Math.round(contrastRatio('#FFFFFF', '#000000')), 21);
    assert.equal(contrastRatio('#7D8A96', '#7D8A96'), 1);
    assert.equal(contrastRatio('#07090C', '#E8F1F6'), contrastRatio('#E8F1F6', '#07090C'));
  });
});

describe('onColor', () => {
  it('puts the dark background on light and mid fills, white on dark ones', () => {
    assert.equal(onColor('#F4F4F5', '#07090C'), '#07090C'); // Obsidian's white primary
    assert.equal(onColor('#A855F7', '#07060F'), '#07060F'); // Neon's violet: white would be 3.96:1
    assert.equal(onColor('#1E3A8A', '#07090C'), WHITE); // a dark club accent
  });
});

describe('solidFill', () => {
  it('darkens a status colour only as far as white text needs', () => {
    for (const hex of ['#EF4444', '#FB7185', '#22C55E', '#F0ABFC']) {
      const fill = solidFill(hex);
      assert.ok(contrastRatio(WHITE, fill) >= 4.6, `${hex} → ${fill}`);
      assert.ok(
        contrastRatio(WHITE, shade(fill, 0.04)) < 4.6 || fill === hex,
        `${hex} → ${fill} is darker than needed`,
      );
    }
  });

  it('leaves a colour that already carries white text alone', () => {
    assert.equal(solidFill('#7F1D1D'), '#7f1d1d');
  });
});

/** The bundled theme files, as the Agent ships them. */
const themes = ['default', 'neon'].map(
  (name) =>
    JSON.parse(readFileSync(new URL(`../../../../config/themes/${name}.json`, import.meta.url), 'utf8')) as {
      name: string;
      colors: Record<'bg' | 'surface' | 'primary' | 'accent' | 'text' | 'muted' | 'danger' | 'success', string>;
    },
);

describe('bundled themes', () => {
  for (const { name, colors: c } of themes) {
    it(`${name}: text and muted read on every panel`, () => {
      for (const panel of [c.bg, c.surface]) {
        assert.ok(contrastRatio(c.text, panel) >= 7, `${name} text`);
        // 6:1 rather than 4.5: muted also sits on tinted badges and on glass over a wallpaper.
        assert.ok(contrastRatio(c.muted, panel) >= 6, `${name} muted ${contrastRatio(c.muted, panel).toFixed(2)}`);
        for (const key of ['accent', 'danger', 'success'] as const) {
          assert.ok(contrastRatio(c[key], panel) >= 4.5, `${name} ${key} as text`);
        }
      }
    });

    it(`${name}: labels on filled controls reach AA`, () => {
      for (const key of ['primary', 'accent'] as const) {
        const fill = c[key];
        assert.ok(contrastRatio(onColor(fill, c.bg), fill) >= 4.5, `${name} on-${key}`);
      }
      assert.ok(contrastRatio(WHITE, solidFill(c.danger)) >= 4.6, `${name} danger-solid`);
    });
  }
});
