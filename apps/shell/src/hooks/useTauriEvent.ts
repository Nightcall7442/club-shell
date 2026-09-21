/**
 * Typed subscription to an `agent://…` or `kiosk://…` event that survives re-renders: the handler lives in a
 * ref, so the native listener is attached once per event name (and per `deps` change).
 */
import type { AgentEventMap, AgentEventName } from '@clubshell/contracts';
import { useEffect, useLayoutEffect, useRef, type DependencyList } from 'react';
import { events, type KioskEventMap, type KioskEventName } from '@/lib/tauri';

/** Full event name as delivered by Tauri. */
export type TauriEventName = `agent://${AgentEventName}` | `kiosk://${KioskEventName}`;

/** Payload type of a {@link TauriEventName}. */
export type TauriEventPayload<N extends TauriEventName> = N extends `agent://${infer A}`
  ? A extends AgentEventName
    ? AgentEventMap[A]
    : never
  : N extends `kiosk://${infer K}`
    ? K extends KioskEventName
      ? KioskEventMap[K]
      : never
    : never;

const PREFIX_LEN = 'agent://'.length;

/**
 * Subscribes to `name` for the lifetime of the component. `deps` re-subscribes (rarely needed: the latest
 * `handler` is always called). Pass `enabled: false` in `options` to pause.
 */
export function useTauriEvent<N extends TauriEventName>(
  name: N,
  handler: (payload: TauriEventPayload<N>) => void,
  deps: DependencyList = [],
  options: { enabled?: boolean } = {},
): void {
  const ref = useRef(handler);
  useLayoutEffect(() => {
    ref.current = handler;
  });
  const enabled = options.enabled ?? true;
  useEffect(() => {
    if (!enabled) {
      return undefined;
    }
    const call = (p: unknown): void => ref.current(p as TauriEventPayload<N>);
    if (name.startsWith('agent://')) {
      return events.on(name.slice(PREFIX_LEN) as AgentEventName, call);
    }
    return events.onKiosk(name.slice(PREFIX_LEN) as KioskEventName, call);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [name, enabled, ...deps]);
}

/** `useTauriEvent('agent://<name>', …)` without the prefix. */
export function useAgentEvent<K extends AgentEventName>(
  name: K,
  handler: (payload: AgentEventMap[K]) => void,
  deps: DependencyList = [],
): void {
  useTauriEvent(`agent://${name}` as `agent://${K}`, handler as (p: TauriEventPayload<`agent://${K}`>) => void, deps);
}

/** `useTauriEvent('kiosk://<name>', …)` without the prefix. */
export function useKioskEvent<K extends KioskEventName>(
  name: K,
  handler: (payload: KioskEventMap[K]) => void,
  deps: DependencyList = [],
): void {
  useTauriEvent(`kiosk://${name}` as `kiosk://${K}`, handler as (p: TauriEventPayload<`kiosk://${K}`>) => void, deps);
}
