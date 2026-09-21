/**
 * Idle detection. The kiosk layer is authoritative (`kiosk://idle`, from the native input hooks); a local
 * pointer/keyboard/wheel/touch watcher provides the same signal in the browser (mock/dev) and while the
 * native events are absent. Thresholds come from settings (`idleTimeoutSec`) and `shell.json → idle`.
 */
import { useCallback, useEffect, useRef, useState } from 'react';
import { useShallow } from 'zustand/react/shallow';
import { api, isTauri } from '@/lib/tauri';
import { useSettingsStore } from '@/store/settings';
import { useKioskEvent } from './useTauriEvent';

export type IdleStage = 'active' | 'dim' | 'idle' | 'screensaver';

export interface IdleState {
  idle: boolean;
  /** Seconds since the last input. */
  seconds: number;
  stage: IdleStage;
  /** Marks the user active (also tells the kiosk layer). */
  reset: () => void;
}

const ACTIVITY_EVENTS = ['pointermove', 'pointerdown', 'keydown', 'wheel', 'touchstart'] as const;

function stageFor(seconds: number, idleSec: number, dimSec: number, screensaverSec: number): IdleStage {
  if (screensaverSec > 0 && seconds >= screensaverSec) {
    return 'screensaver';
  }
  if (idleSec > 0 && seconds >= idleSec) {
    return 'idle';
  }
  if (dimSec > 0 && seconds >= dimSec) {
    return 'dim';
  }
  return 'active';
}

/**
 * @param thresholdSec idle threshold override; defaults to `settings.idleTimeoutSec` (0 disables idle).
 */
export function useIdle(thresholdSec?: number): IdleState {
  const cfg = useSettingsStore(
    useShallow((s) => ({
      idleSec: thresholdSec ?? s.settings.idleTimeoutSec,
      dimSec: s.shellConfig?.idle.dimAfterSec ?? 0,
      screensaverSec: s.shellConfig?.idle.screensaverAfterSec ?? 0,
    })),
  );
  const [state, setState] = useState<Omit<IdleState, 'reset'>>({ idle: false, seconds: 0, stage: 'active' });
  const lastActivity = useRef(Date.now());
  const cfgRef = useRef(cfg);
  cfgRef.current = cfg;
  const stateRef = useRef(state);
  stateRef.current = state;

  const apply = useCallback((seconds: number, stageOverride?: IdleStage, idleOverride?: boolean) => {
    const c = cfgRef.current;
    const stage = stageOverride ?? stageFor(seconds, c.idleSec, c.dimSec, c.screensaverSec);
    const idle = idleOverride ?? (stage === 'idle' || stage === 'screensaver');
    setState((prev) =>
      prev.idle === idle && prev.stage === stage && prev.seconds === seconds ? prev : { idle, seconds, stage },
    );
  }, []);

  const reset = useCallback(() => {
    lastActivity.current = Date.now();
    apply(0, 'active', false);
    if (isTauri()) {
      void api.kiosk.idleReset().catch(() => undefined);
    }
  }, [apply]);

  useKioskEvent('idle', (p) => {
    if (!p.idle) {
      lastActivity.current = Date.now();
    } else {
      lastActivity.current = Date.now() - p.seconds * 1000;
    }
    apply(p.seconds, p.stage, p.idle);
  });

  useEffect(() => {
    const onActivity = (): void => {
      lastActivity.current = Date.now();
      if (stateRef.current.seconds !== 0 || stateRef.current.stage !== 'active') {
        apply(0, 'active', false);
      }
    };
    for (const ev of ACTIVITY_EVENTS) {
      window.addEventListener(ev, onActivity, { passive: true });
    }
    const timer = setInterval(() => apply(Math.floor((Date.now() - lastActivity.current) / 1000)), 1000);
    return () => {
      for (const ev of ACTIVITY_EVENTS) {
        window.removeEventListener(ev, onActivity);
      }
      clearInterval(timer);
    };
  }, [apply]);

  return { ...state, reset };
}
