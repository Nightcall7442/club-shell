/**
 * UI sounds synthesised with WebAudio (no assets): a focus tick, a press, a whoosh for the hero switch and a
 * confirmation chime. Gated by `settings.uiSounds`, `muted` and `volume`. The context is created lazily on the
 * first call, which always follows a user gesture (focus, pointer), so autoplay policy never blocks it.
 */
import { useSettingsStore } from '@/store/settings';

let ctx: AudioContext | null = null;
let noise: AudioBuffer | null = null;

function context(): AudioContext | null {
  try {
    ctx ??= new AudioContext();
    if (ctx.state === 'suspended') {
      void ctx.resume();
    }
    return ctx;
  } catch {
    return null;
  }
}

/** Master level 0..~0.3; `0` when sounds are off. */
function level(): number {
  const { uiSounds, muted, volume } = useSettingsStore.getState().settings;
  if (!uiSounds || muted || volume <= 0) {
    return 0;
  }
  return (volume / 100) * 0.3;
}

function tone(c: AudioContext, from: number, to: number, start: number, dur: number, peak: number): void {
  const o = c.createOscillator();
  const g = c.createGain();
  o.type = 'sine';
  o.frequency.setValueAtTime(from, start);
  o.frequency.exponentialRampToValueAtTime(to, start + dur);
  g.gain.setValueAtTime(0.0001, start);
  g.gain.exponentialRampToValueAtTime(peak, start + 0.005);
  g.gain.exponentialRampToValueAtTime(0.0001, start + dur);
  o.connect(g).connect(c.destination);
  o.start(start);
  o.stop(start + dur + 0.02);
}

function noiseBuffer(c: AudioContext): AudioBuffer {
  if (!noise) {
    noise = c.createBuffer(1, c.sampleRate, c.sampleRate);
    const d = noise.getChannelData(0);
    for (let i = 0; i < d.length; i++) {
      d[i] = Math.random() * 2 - 1;
    }
  }
  return noise;
}

/** Focus / hover tick: 40 ms, high and quiet. */
export function tick(): void {
  const c = context();
  const v = level();
  if (!c || v === 0) return;
  tone(c, 1900, 1400, c.currentTime, 0.04, v * 0.35);
}

/** Press: shorter, lower. */
export function press(): void {
  const c = context();
  const v = level();
  if (!c || v === 0) return;
  tone(c, 900, 600, c.currentTime, 0.05, v * 0.5);
}

/** Whoosh: band-passed noise sweeping up, for the hero switch. */
export function whoosh(): void {
  const c = context();
  const v = level();
  if (!c || v === 0) return;
  const t = c.currentTime;
  const src = c.createBufferSource();
  src.buffer = noiseBuffer(c);
  const bp = c.createBiquadFilter();
  bp.type = 'bandpass';
  bp.Q.value = 1.2;
  bp.frequency.setValueAtTime(260, t);
  bp.frequency.exponentialRampToValueAtTime(2200, t + 0.32);
  const g = c.createGain();
  g.gain.setValueAtTime(0.0001, t);
  g.gain.exponentialRampToValueAtTime(v * 0.45, t + 0.08);
  g.gain.exponentialRampToValueAtTime(0.0001, t + 0.38);
  src.connect(bp).connect(g).connect(c.destination);
  src.start(t);
  src.stop(t + 0.4);
}

/** Confirmation chime: a fifth, second note a beat later. */
export function chime(): void {
  const c = context();
  const v = level();
  if (!c || v === 0) return;
  const t = c.currentTime;
  tone(c, 880, 880, t, 0.5, v * 0.5);
  tone(c, 1318.5, 1318.5, t + 0.11, 0.6, v * 0.45);
}
