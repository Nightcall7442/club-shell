/**
 * The owner's player-screen designer (D-104, `shell.club.look`) on the lock screen, the idle screen and the top bar,
 * against the Vite dev server in mock mode. The look comes from the mock's `?look=` switch in the console's storage
 * shape (`settings.branding`), `?logo=demo` adds a logo picture. The app boots in Russian (mock settings).
 */
import { readFileSync } from 'node:fs';
import { expect, test, type Page } from '@playwright/test';

type Strings = Record<string, unknown>;

function bundle(locale: string): (key: string) => string {
  const strings = JSON.parse(
    readFileSync(new URL(`../../apps/shell/src/i18n/${locale}.json`, import.meta.url), 'utf8'),
  ) as Strings;
  return (key) => {
    const value = key
      .split('.')
      .reduce<unknown>(
        (node, part) => (typeof node === 'object' && node !== null ? (node as Strings)[part] : undefined),
        strings,
      );
    if (typeof value !== 'string') throw new Error(`missing i18n key: ${key}`);
    return value;
  };
}

const ru = bundle('ru');
const en = bundle('en');

function lookUrl(look: unknown, route: string, extra = ''): string {
  return `/?logo=demo${extra}&look=${encodeURIComponent(JSON.stringify(look))}#/${route}`;
}

async function box(page: Page, testId: string): Promise<{ x: number; y: number; width: number; height: number }> {
  const b = await page.getByTestId(testId).first().boundingBox();
  expect(b, testId).not.toBeNull();
  return b!;
}

/** The root font size in px (the shell scales it with the screen). */
async function rem(page: Page): Promise<number> {
  return page.evaluate(() => parseFloat(getComputedStyle(document.documentElement).fontSize));
}

test.describe('player-screen designer (D-104)', () => {
  test('without a look the lock screen is today’s: the logo top left at its size, the bundled texts, clock and languages', async ({
    page,
  }) => {
    await page.goto('/?logo=demo#/lock');
    await expect(page.getByRole('heading', { level: 1, name: ru('lock.title') })).toBeVisible();
    await expect(page.getByText(ru('lock.subtitle'))).toBeVisible();
    const logo = await box(page, 'club-logo');
    expect(logo.x).toBeLessThan(120);
    expect(logo.y).toBeLessThan(120);
    // h-7: today's 1.75rem (the root size follows the screen).
    expect(logo.width).toBeCloseTo(1.75 * (await rem(page)), 0);
    await expect(page.getByTestId('club-brand')).toHaveAttribute('data-position', 'topLeft');
    await expect(page.getByLabel(ru('lock.clock'))).toBeVisible();
    await expect(page.getByRole('button', { name: 'English', exact: true })).toBeVisible();
  });

  test('the lock logo stands where the owner put it, as large as set, with the owner’s texts and without the clock or languages', async ({
    page,
  }) => {
    await page.goto(
      lookUrl(
        {
          lock: {
            logo: { position: 'topCenter', size: 'xl', name: false },
            clock: false,
            language: false,
            title: { ru: 'Вход в зал' },
            subtitle: { ru: 'Карта, QR или логин' },
          },
        },
        'lock',
      ),
    );
    await expect(page.getByRole('heading', { level: 1, name: 'Вход в зал' })).toBeVisible();
    await expect(page.getByText('Карта, QR или логин')).toBeVisible();
    await expect(page.getByText(ru('lock.subtitle'))).toHaveCount(0);
    await expect(page.getByTestId('club-brand')).toHaveAttribute('data-position', 'topCenter');
    const logo = await box(page, 'club-logo');
    const viewport = page.viewportSize()!;
    // Centred, and XL: 8vh of 1080 is ~86 px.
    expect(Math.abs(logo.x + logo.width / 2 - viewport.width / 2)).toBeLessThan(4);
    expect(logo.width).toBeGreaterThan(70);
    await expect(page.getByText(ru('idle.clubName'))).toHaveCount(0);
    await expect(page.getByLabel(ru('lock.clock'))).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'English', exact: true })).toHaveCount(0);
  });

  test('a lock logo in the centre stands above the sign-in card, never over it', async ({ page }) => {
    await page.goto(lookUrl({ lock: { logo: { position: 'center', size: 'l' } } }, 'lock', '&club=demo'));
    const heading = page.getByRole('heading', { level: 1, name: ru('lock.title') });
    await expect(heading).toBeVisible();
    const logo = await box(page, 'club-logo');
    const card = (await heading.boundingBox())!;
    expect(logo.y + logo.height).toBeLessThan(card.y);
    expect(Math.abs(logo.x + logo.width / 2 - card.x - card.width / 2)).toBeLessThan(4);
    await expect(page.getByText('CyberArena Tashkent')).toBeVisible();
  });

  test('an owner’s text only in Russian falls back to the bundled line in English', async ({ page }) => {
    await page.goto(lookUrl({ lock: { title: { ru: 'Вход в зал' } } }, 'lock'));
    await expect(page.getByRole('heading', { level: 1, name: 'Вход в зал' })).toBeVisible();
    await page.getByRole('button', { name: 'English', exact: true }).click();
    await expect(page.getByRole('heading', { level: 1, name: en('lock.title') })).toBeVisible();
  });

  test('the idle screen takes the owner’s logo place and size, call to action, and hides the prices, badge and clock', async ({
    page,
  }) => {
    await page.goto(
      lookUrl(
        {
          idle: {
            logo: { position: 'bottomRight', size: 'l' },
            prices: false,
            freeBadge: false,
            clock: false,
            cta: { ru: 'Нажмите любую клавишу' },
          },
        },
        'idle',
      ),
    );
    await expect(page.getByTestId('idle-cta')).toContainText('Нажмите любую клавишу');
    await expect(page.getByText(ru('idle.touchToStart'))).toHaveCount(0);
    await expect(page.getByTestId('club-brand')).toHaveAttribute('data-position', 'bottomRight');
    const viewport = page.viewportSize()!;
    const brand = await box(page, 'club-brand');
    expect(brand.x).toBeGreaterThan(viewport.width / 2);
    expect(brand.y).toBeGreaterThan(viewport.height / 2);
    // L on the idle screen: 5.5vh of 1080 is ~59 px (M is 32).
    const logo = await box(page, 'club-logo');
    expect(logo.width).toBeGreaterThan(50);
    await expect(page.getByLabel(ru('idle.priceList'))).toHaveCount(0);
    await expect(page.getByText(ru('idle.pcFree'))).toHaveCount(0);
    await expect(page.getByLabel(ru('lock.clock'))).toHaveCount(0);
  });

  test('the idle screen without a look keeps its prices, badge, clock and prompt', async ({ page }) => {
    await page.goto('/#/idle');
    await expect(page.getByText(ru('idle.touchToStart'))).toBeVisible();
    await expect(page.getByLabel(ru('idle.priceList'))).toBeVisible();
    await expect(page.getByText(ru('idle.pcFree'))).toBeVisible();
    await expect(page.getByLabel(ru('lock.clock'))).toBeVisible();
    await expect(page.getByTestId('club-brand')).toHaveAttribute('data-position', 'topLeft');
  });

  test('the idle logo in the centre stands above the clock, and the owner’s still replaces the slides', async ({
    page,
  }) => {
    await page.goto(
      lookUrl({ idle: { logo: { position: 'center', size: 'xl' }, wallpaperUrl: '/mock-art/cs2-hero.jpg' } }, 'idle'),
    );
    const clock = page.getByLabel(ru('lock.clock'));
    await expect(clock).toBeVisible();
    const brand = await box(page, 'club-brand');
    const time = (await clock.boundingBox())!;
    expect(brand.y + brand.height).toBeLessThan(time.y);
    await expect(page.getByTestId('idle-wallpaper')).toHaveAttribute('src', /cs2-hero\.jpg$/);
  });

  test('the top bar hides the logo and the club name, and the PC line stays', async ({ page }) => {
    await page.goto(lookUrl({ desktop: { logo: { show: false, name: false } } }, 'home', '&mock=auth'));
    const brand = page.getByTestId('topbar-brand');
    await expect(brand).toBeVisible();
    await expect(brand.getByTestId('club-logo')).toHaveCount(0);
    await expect(brand.getByText(ru('idle.clubName'))).toHaveCount(0);
    await expect(brand.getByText(/PC-12/)).toBeVisible();
  });

  test('a larger top-bar logo is sized as set', async ({ page }) => {
    await page.goto(lookUrl({ desktop: { logo: { size: 'l' } } }, 'home', '&mock=auth'));
    const logo = page.getByTestId('topbar-brand').getByTestId('club-logo');
    await expect(logo).toBeVisible();
    expect((await logo.boundingBox())!.width).toBeCloseTo(2 * (await rem(page)), 0);
  });
});
