/**
 * The PC's vitals as the top bar and "Мой компьютер" show them: CPU and GPU temperature, or their load when the PC
 * has no such sensor (the Agent sends 0 °C then, which is never shown), and the frame rate while a game reports one.
 * Pure, so `vitals.test.ts` runs it without a browser.
 */
import type { PcMetrics } from '@clubshell/contracts';

/** From this temperature (°C) a reading turns red. */
export const HOT_C = 85;

export type VitalKey = 'cpu' | 'gpu' | 'fps';

export interface Vital {
  key: VitalKey;
  /** `temp`: °C; `load`: % (no temperature sensor); `fps`: frames per second. */
  unit: 'temp' | 'load' | 'fps';
  /** Rounded for display. */
  value: number;
  hot: boolean;
}

function partVital(key: 'cpu' | 'gpu', tempC: number, loadPct: number): Vital {
  const temp = Math.round(tempC);
  if (temp > 0) {
    return { key, unit: 'temp', value: temp, hot: temp >= HOT_C };
  }
  return { key, unit: 'load', value: Math.round(Math.min(100, Math.max(0, loadPct || 0))), hot: false };
}

/** CPU, GPU and, while a game reports it, FPS — in that order. */
export function vitalsOf(metrics: PcMetrics): Vital[] {
  const vitals = [
    partVital('cpu', metrics.temps.cpu, metrics.cpuPct),
    partVital('gpu', metrics.temps.gpu, metrics.gpuPct),
  ];
  const fps = metrics.fps;
  if (typeof fps === 'number' && Number.isFinite(fps)) {
    vitals.push({ key: 'fps', unit: 'fps', value: Math.max(0, Math.round(fps)), hot: false });
  }
  return vitals;
}

/** The one reading a crowded bar keeps: the frame rate in a game, else a part running hot, else the CPU. */
export function leadVital(vitals: readonly Vital[]): Vital | null {
  return vitals.find((v) => v.key === 'fps') ?? vitals.find((v) => v.hot) ?? vitals[0] ?? null;
}
