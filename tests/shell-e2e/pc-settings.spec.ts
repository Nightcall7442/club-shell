/**
 * The player's PC settings in Profile → Settings: mouse, output device, graphics-card panel. The helpers behind them are
 * plain functions tested without a browser; the screen itself runs in mock mode (demo PC: NVIDIA RTX 4070 with the
 * NVIDIA Control Panel, Realtek speakers + a HyperX headset + the monitor's HDMI).
 */
import { expect, test } from '@playwright/test';
import { gpuVendorOf, pickGpuPanel } from '../../apps/shell/src/screens/Profile/pc/gpuVendor';
import {
  doubleClickMsAt,
  doubleClickPosition,
  judgeClick,
  splitDeviceName,
} from '../../apps/shell/src/screens/Profile/pc/pcFormat';

test.describe('pc settings helpers', () => {
  test('the GPU vendor is read from the model name', () => {
    expect(gpuVendorOf('NVIDIA GeForce RTX 4070')).toBe('nvidia');
    expect(gpuVendorOf('GeForce GTX 1660 SUPER')).toBe('nvidia');
    expect(gpuVendorOf('AMD Radeon RX 7800 XT')).toBe('amd');
    expect(gpuVendorOf('Radeon RX 580 Series')).toBe('amd');
    expect(gpuVendorOf('AMD Radeon(TM) Graphics')).toBe('amd');
    expect(gpuVendorOf('Intel(R) UHD Graphics 770')).toBe('intel');
    expect(gpuVendorOf('Intel(R) Arc(TM) A770 Graphics')).toBe('intel');
    expect(gpuVendorOf('Microsoft Basic Display Adapter')).toBeNull();
    expect(gpuVendorOf('Parsec Virtual Display Adapter')).toBeNull();
  });

  test('the panel offered is the one for the card the PC has', () => {
    const nvidia = { vendor: 'nvidia' as const, name: 'NVIDIA Control Panel' };
    const intel = { vendor: 'intel' as const, name: 'Intel Graphics Command Center' };
    const amd = { vendor: 'amd' as const, name: 'AMD Software' };
    // A discrete card wins over the Intel iGPU next to it.
    expect(pickGpuPanel(['Intel(R) UHD Graphics 770', 'NVIDIA GeForce RTX 4070'], [intel, nvidia])).toEqual({
      vendor: 'nvidia',
      panel: nvidia,
      model: 'NVIDIA GeForce RTX 4070',
    });
    // NVIDIA card without its panel installed: the iGPU's panel rather than nothing.
    expect(pickGpuPanel(['NVIDIA GeForce RTX 4070', 'Intel(R) UHD Graphics 770'], [intel])?.vendor).toBe('intel');
    // A panel for a card this PC does not have is never offered.
    expect(pickGpuPanel(['NVIDIA GeForce RTX 4070'], [amd])).toBeNull();
    expect(pickGpuPanel(['NVIDIA GeForce RTX 4070'], [])).toBeNull();
    // Hardware unknown (Agent offline): the first installed panel.
    expect(pickGpuPanel(null, [intel, amd])).toEqual({ vendor: 'amd', panel: amd, model: null });
    expect(pickGpuPanel(null, [])).toBeNull();
  });

  test('the double-click slider maps to Windows times', () => {
    expect(doubleClickPosition(900)).toBe(0);
    expect(doubleClickPosition(500)).toBe(8);
    expect(doubleClickPosition(200)).toBe(14);
    expect(doubleClickPosition(530)).toBe(7);
    expect(doubleClickPosition(5000)).toBe(0);
    expect(doubleClickPosition(10)).toBe(14);
    expect(doubleClickMsAt(0)).toBe(900);
    expect(doubleClickMsAt(8)).toBe(500);
    expect(doubleClickMsAt(14)).toBe(200);
    expect(doubleClickMsAt(99)).toBe(200);
    for (let p = 0; p <= 14; p++) {
      expect(doubleClickPosition(doubleClickMsAt(p))).toBe(p);
    }
  });

  test('the double-click test uses the configured time', () => {
    expect(judgeClick(null, 1000, 500)).toBe('armed');
    expect(judgeClick(1000, 1400, 500)).toBe('ok');
    expect(judgeClick(1000, 1500, 500)).toBe('ok');
    expect(judgeClick(1000, 1700, 500)).toBe('slow');
    expect(judgeClick(1000, 1700, 900)).toBe('ok');
    expect(judgeClick(1000, 9000, 500)).toBe('armed');
  });

  test('output names split into endpoint and adapter', () => {
    expect(splitDeviceName('Speakers (Realtek(R) Audio)')).toEqual({ title: 'Speakers', detail: 'Realtek(R) Audio' });
    expect(splitDeviceName('Наушники (HyperX Cloud II)')).toEqual({ title: 'Наушники', detail: 'HyperX Cloud II' });
    expect(splitDeviceName('LG ULTRAGEAR')).toEqual({ title: 'LG ULTRAGEAR', detail: null });
    expect(splitDeviceName('(weird)')).toEqual({ title: '(weird)', detail: null });
    expect(splitDeviceName('Speakers ()')).toEqual({ title: 'Speakers', detail: null });
  });
});

test('mouse, output device and graphics panel are set from Settings', async ({ page }) => {
  await page.goto('/?mock=auth#/profile?tab=settings');

  const mouse = page.getByRole('region', { name: 'Мышь' });
  const speed = mouse.getByRole('slider', { name: 'Скорость указателя' });
  await expect(speed).toHaveValue('10');
  await mouse.getByRole('button', { name: 'Увеличить: Скорость указателя' }).click();
  await expect(speed).toHaveValue('11');
  const precision = mouse.getByRole('switch', { name: /Повышенная точность указателя/ });
  await expect(precision).toHaveAttribute('aria-checked', 'true');
  await precision.click();
  await expect(precision).toHaveAttribute('aria-checked', 'false');
  const pad = mouse.getByRole('button', { name: 'Проверка двойного щелчка' });
  await pad.dblclick();
  await expect(mouse.getByText('Получилось — это двойной щелчок')).toBeVisible();

  const outputs = page
    .getByRole('region', { name: 'Устройство вывода' })
    .getByRole('radiogroup', { name: 'Устройство вывода' });
  const headset = outputs.getByRole('radio', { name: /HyperX Cloud II/ });
  await expect(headset).toHaveAttribute('aria-checked', 'false');
  await headset.click();
  await expect(headset).toHaveAttribute('aria-checked', 'true');
  await expect(outputs.getByRole('radio', { name: /Realtek/ })).toHaveAttribute('aria-checked', 'false');

  const gpu = page.getByRole('region', { name: 'Видеокарта' });
  await expect(gpu.getByText('NVIDIA GeForce RTX 4070')).toBeVisible();
  await gpu.getByRole('button', { name: 'Открыть NVIDIA Control Panel' }).click();
  await expect(gpu.getByRole('button', { name: 'Открыть NVIDIA Control Panel' })).toBeEnabled();
});
