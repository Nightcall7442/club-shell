/** `pnpm --filter @clubshell/shell test` (Node's own runner, which strips the types itself). */
import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import type { PcMetrics } from '@clubshell/contracts';
import { HOT_C, leadVital, vitalsOf } from './vitals.ts';

const sample = (patch: Partial<PcMetrics> = {}): PcMetrics => ({
  cpuPct: 23.4,
  gpuPct: 11.6,
  ramUsedMb: 9_830,
  temps: { cpu: 47.2, gpu: 41 },
  fps: null,
  netMbps: { up: 2.4, down: 18.7 },
  uptimeSec: 3600,
  at: '2026-10-04T10:00:00Z',
  ...patch,
});

describe('vitalsOf', () => {
  it('shows CPU and GPU temperatures, rounded', () => {
    assert.deepEqual(vitalsOf(sample()), [
      { key: 'cpu', unit: 'temp', value: 47, hot: false },
      { key: 'gpu', unit: 'temp', value: 41, hot: false },
    ]);
  });

  it('falls back to the load when the sensor reports 0 °C, never showing 0°', () => {
    const vitals = vitalsOf(sample({ temps: { cpu: 0, gpu: 0 } }));
    assert.deepEqual(vitals, [
      { key: 'cpu', unit: 'load', value: 23, hot: false },
      { key: 'gpu', unit: 'load', value: 12, hot: false },
    ]);
    assert.equal(
      vitals.some((v) => v.unit === 'temp' && v.value === 0),
      false,
    );
  });

  it('keeps the load within 0–100 %', () => {
    const [cpu, gpu] = vitalsOf(sample({ cpuPct: 140, gpuPct: -3, temps: { cpu: 0, gpu: 0 } }));
    assert.equal(cpu?.value, 100);
    assert.equal(gpu?.value, 0);
  });

  it(`turns a part red from ${HOT_C} °C`, () => {
    const [cpu, gpu] = vitalsOf(sample({ temps: { cpu: HOT_C - 0.6, gpu: HOT_C } }));
    assert.equal(cpu?.hot, false);
    assert.equal(gpu?.hot, true);
  });

  it('adds the frame rate only while a game reports one', () => {
    assert.equal(
      vitalsOf(sample({ fps: null })).some((v) => v.key === 'fps'),
      false,
    );
    assert.equal(
      vitalsOf(sample({ fps: undefined })).some((v) => v.key === 'fps'),
      false,
    );
    assert.deepEqual(vitalsOf(sample({ fps: 143.6 }))[2], { key: 'fps', unit: 'fps', value: 144, hot: false });
  });
});

describe('leadVital', () => {
  it('prefers the frame rate, then a hot part, then the CPU', () => {
    assert.equal(leadVital(vitalsOf(sample({ fps: 214 })))?.key, 'fps');
    assert.equal(leadVital(vitalsOf(sample({ temps: { cpu: 60, gpu: 88 } })))?.key, 'gpu');
    assert.equal(leadVital(vitalsOf(sample()))?.key, 'cpu');
    assert.equal(leadVital([]), null);
  });
});
