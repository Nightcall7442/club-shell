/**
 * `pnpm --filter @clubshell/shell test` (Node's own runner, which strips the types itself). The vendor and panel
 * helpers are covered by tests/shell-e2e/pc-settings.spec.ts; these are the ones the top bar uses.
 */
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { gpuShortName, primaryGpu } from './gpuVendor.ts';

describe('primaryGpu', () => {
  const gpu = (model: string): { model: string } => ({ model });

  it('takes the discrete card over the Intel iGPU next to it, whatever the order', () => {
    const igpu = gpu('Intel(R) UHD Graphics 770');
    const rtx = gpu('NVIDIA GeForce RTX 4070');
    assert.equal(primaryGpu([igpu, rtx]), rtx);
    assert.equal(primaryGpu([rtx, igpu]), rtx);
    assert.equal(primaryGpu([igpu, gpu('AMD Radeon RX 7800 XT')])?.model, 'AMD Radeon RX 7800 XT');
  });

  it('skips virtual adapters and falls back to the first one', () => {
    const parsec = gpu('Parsec Virtual Display Adapter');
    assert.equal(primaryGpu([parsec, gpu('Intel(R) Arc(TM) A770 Graphics')])?.model, 'Intel(R) Arc(TM) A770 Graphics');
    assert.equal(primaryGpu([parsec]), parsec);
    assert.equal(primaryGpu([]), null);
  });
});

describe('gpuShortName', () => {
  it('keeps the model number players know', () => {
    assert.equal(gpuShortName('NVIDIA GeForce RTX 4070'), 'RTX 4070');
    assert.equal(gpuShortName('AMD Radeon RX 7800 XT'), 'RX 7800 XT');
    assert.equal(gpuShortName('NVIDIA GeForce GTX 1660 SUPER'), 'GTX 1660 SUPER');
    assert.equal(gpuShortName('NVIDIA GeForce RTX 4060 Ti'), 'RTX 4060 Ti');
    assert.equal(gpuShortName('AMD Radeon RX 7900 XTX'), 'RX 7900 XTX');
    assert.equal(gpuShortName('Radeon RX 580 Series'), 'RX 580');
    assert.equal(gpuShortName('NVIDIA GeForce RTX 3060 Laptop GPU'), 'RTX 3060');
    assert.equal(gpuShortName('Intel(R) Arc(TM) A770 Graphics'), 'Arc A770');
  });

  it('drops only the vendor and trademarks from a name without a model number', () => {
    assert.equal(gpuShortName('Intel(R) UHD Graphics 770'), 'UHD Graphics 770');
    assert.equal(gpuShortName('AMD Radeon(TM) Graphics'), 'Radeon Graphics');
    assert.equal(gpuShortName('Microsoft Basic Display Adapter'), 'Microsoft Basic Display Adapter');
  });
});
