/**
 * Gamepad + keyboard spatial navigation. Navigable elements opt in with a `data-nav` attribute; focus moves to
 * the geometrically nearest candidate in the pressed direction (D-pad / left stick / arrow keys), `A` clicks
 * the focused element, `B`/`Escape` go back, `Start` opens the menu, `LB`/`RB` switch tabs.
 *
 * Input sources: `kiosk://gamepad` (Rust) inside Tauri, the browser Gamepad API in the mock/dev build, and
 * `keydown` everywhere. All sources feed one module-level dispatcher that routes to the most recently mounted
 * *enabled* hook instance (so a Modal's instance shadows the screen underneath it).
 */
import { useCallback, useEffect, useMemo, useRef, useSyncExternalStore, type RefObject } from 'react';
import { events, isTauri, type GamepadButtonName } from '@/lib/tauri';

export type NavDirection = 'up' | 'down' | 'left' | 'right';

export interface UseGamepadOptions {
  /** Called before the default focus move with the computed target; return `true` to suppress the move. */
  onNavigate?: (dir: NavDirection, next: HTMLElement | null) => boolean | void;
  /** `A` / Enter-equivalent; return `true` to suppress the default `click()`. */
  onActivate?: (el: HTMLElement | null) => boolean | void;
  /** `B` / Escape. */
  onBack?: () => void;
  /** `Start`. */
  onMenu?: () => void;
  /** `LB` (prev) / `RB` (next). */
  onTab?: (dir: 'prev' | 'next') => void;
  /** Raw button edge, for anything else (`x`, `y`, `back`, …). */
  onButton?: (name: GamepadButtonName, pressed: boolean) => void;
  /** `false` lets the previous instance handle input (default `true`). */
  enabled?: boolean;
  /** Restricts candidates to this container (dialogs, tab panels). */
  scope?: RefObject<HTMLElement>;
  /** Also drive arrows / Escape from the keyboard (default `true`). */
  keyboard?: boolean;
}

export interface GamepadController {
  moveFocus: (dir: NavDirection) => HTMLElement | null;
  /** Focuses the first candidate in the scope. */
  focusFirst: () => HTMLElement | null;
  connected: boolean;
}

// ---------------------------------------------------------------------------------------------------------------------
// Spatial navigation (pure DOM helpers, reusable outside the hook)
// ---------------------------------------------------------------------------------------------------------------------

const DIRECTIONS: ReadonlySet<string> = new Set<NavDirection>(['up', 'down', 'left', 'right']);
const isDirection = (name: string): name is NavDirection => DIRECTIONS.has(name);

/** Elements with `data-nav` that are enabled and laid out (non-zero box, not under `aria-hidden`/`inert`). */
export function collectNavigables(root: ParentNode = document): HTMLElement[] {
  const out: HTMLElement[] = [];
  root.querySelectorAll<HTMLElement>('[data-nav]').forEach((el) => {
    if (el.dataset['nav'] === 'off' || el.hasAttribute('disabled') || el.getAttribute('aria-disabled') === 'true') {
      return;
    }
    if (el.closest('[aria-hidden="true"],[inert]')) {
      return;
    }
    const r = el.getBoundingClientRect();
    if (r.width > 0 || r.height > 0) {
      out.push(el);
    }
  });
  return out;
}

/** Nearest candidate in `dir` from `from`; aligned neighbours (overlapping on the cross axis) win over diagonal ones. */
export function findNext(from: HTMLElement, dir: NavDirection, candidates: HTMLElement[]): HTMLElement | null {
  const f = from.getBoundingClientRect();
  const fx = f.left + f.width / 2;
  const fy = f.top + f.height / 2;
  let best: HTMLElement | null = null;
  let bestScore = Number.POSITIVE_INFINITY;
  for (const el of candidates) {
    if (el === from) {
      continue;
    }
    const r = el.getBoundingClientRect();
    const cx = r.left + r.width / 2;
    const cy = r.top + r.height / 2;
    let primary: number;
    let secondary: number;
    let aligned: boolean;
    switch (dir) {
      case 'up':
        if (cy >= fy - 1) continue;
        primary = Math.max(0, f.top - r.bottom);
        secondary = Math.abs(cx - fx);
        aligned = r.right > f.left && r.left < f.right;
        break;
      case 'down':
        if (cy <= fy + 1) continue;
        primary = Math.max(0, r.top - f.bottom);
        secondary = Math.abs(cx - fx);
        aligned = r.right > f.left && r.left < f.right;
        break;
      case 'left':
        if (cx >= fx - 1) continue;
        primary = Math.max(0, f.left - r.right);
        secondary = Math.abs(cy - fy);
        aligned = r.bottom > f.top && r.top < f.bottom;
        break;
      case 'right':
        if (cx <= fx + 1) continue;
        primary = Math.max(0, r.left - f.right);
        secondary = Math.abs(cy - fy);
        aligned = r.bottom > f.top && r.top < f.bottom;
        break;
    }
    // ponytail: weighted Manhattan distance; a full Euclidean/overlap-ratio model is not worth it for a grid UI.
    const score = primary + secondary * 2 + (aligned ? 0 : 400);
    if (score < bestScore) {
      bestScore = score;
      best = el;
    }
  }
  return best;
}

let lastFocused: HTMLElement | null = null;
let globalHandlersInstalled = false;

function clearFocusMark(except?: HTMLElement | null): void {
  document.querySelectorAll<HTMLElement>('[data-focused="true"]').forEach((el) => {
    if (el !== except) {
      delete el.dataset['focused'];
    }
  });
}

function installGlobalHandlers(): void {
  if (globalHandlersInstalled || typeof document === 'undefined') {
    return;
  }
  globalHandlersInstalled = true;
  document.addEventListener('pointerdown', () => clearFocusMark(), true);
  document.addEventListener('focusin', (e) => {
    const t = e.target;
    if (t instanceof HTMLElement && t.dataset['focused'] !== 'true') {
      clearFocusMark();
    }
  });
}

/** Focuses `el`, marks it `data-focused="true"` (visible ring even without keyboard focus-visible heuristics). */
export function focusElement(el: HTMLElement): void {
  clearFocusMark(el);
  el.focus({ preventScroll: true });
  el.dataset['focused'] = 'true';
  el.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  lastFocused = el;
}

function currentNavigable(candidates: HTMLElement[]): HTMLElement | null {
  const active = document.activeElement;
  if (active instanceof HTMLElement && candidates.includes(active)) {
    return active;
  }
  if (lastFocused && candidates.includes(lastFocused)) {
    return lastFocused;
  }
  return null;
}

/** Moves focus one step in `dir` within `root`; returns the newly focused element (or `null` when none). */
export function navigateFocus(dir: NavDirection, root: ParentNode = document): HTMLElement | null {
  const candidates = collectNavigables(root);
  if (candidates.length === 0) {
    return null;
  }
  const from = currentNavigable(candidates);
  const next = from ? findNext(from, dir, candidates) : (candidates[0] ?? null);
  if (next) {
    focusElement(next);
  }
  return next;
}

// ---------------------------------------------------------------------------------------------------------------------
// Instance stack + dispatcher
// ---------------------------------------------------------------------------------------------------------------------

interface Instance {
  opts: RefObject<UseGamepadOptions>;
}

const instances: Instance[] = [];

function topInstance(): Instance | null {
  for (let i = instances.length - 1; i >= 0; i -= 1) {
    const inst = instances[i];
    if (inst && inst.opts.current?.enabled !== false) {
      return inst;
    }
  }
  return null;
}

function scopeOf(inst: Instance): ParentNode {
  return inst.opts.current?.scope?.current ?? document;
}

function navigate(inst: Instance, dir: NavDirection): void {
  const o = inst.opts.current ?? {};
  const root = scopeOf(inst);
  const candidates = collectNavigables(root);
  const from = currentNavigable(candidates);
  const next = from ? findNext(from, dir, candidates) : (candidates[0] ?? null);
  if (o.onNavigate?.(dir, next) === true) {
    return;
  }
  if (next) {
    focusElement(next);
  }
}

function activate(inst: Instance): void {
  const active = document.activeElement;
  const el = active instanceof HTMLElement && active !== document.body ? active : null;
  if (inst.opts.current?.onActivate?.(el) === true) {
    return;
  }
  el?.click();
}

const REPEAT_DELAY_MS = 350;
const REPEAT_INTERVAL_MS = 120;
const repeatTimers = new Map<NavDirection, ReturnType<typeof setTimeout>>();

function stopRepeat(dir: NavDirection): void {
  const t = repeatTimers.get(dir);
  if (t !== undefined) {
    clearTimeout(t);
    clearInterval(t);
    repeatTimers.delete(dir);
  }
}

function startRepeat(dir: NavDirection): void {
  stopRepeat(dir);
  const first = setTimeout(() => {
    const every = setInterval(() => {
      const inst = topInstance();
      if (inst) {
        navigate(inst, dir);
      }
    }, REPEAT_INTERVAL_MS);
    repeatTimers.set(dir, every);
  }, REPEAT_DELAY_MS);
  repeatTimers.set(dir, first);
}

function handleButton(name: string, pressed: boolean): void {
  const inst = topInstance();
  if (!inst) {
    return;
  }
  const o = inst.opts.current ?? {};
  o.onButton?.(name as GamepadButtonName, pressed);
  if (isDirection(name)) {
    if (pressed) {
      navigate(inst, name);
      startRepeat(name);
    } else {
      stopRepeat(name);
    }
    return;
  }
  if (!pressed) {
    return;
  }
  switch (name) {
    case 'a':
      activate(inst);
      break;
    case 'b':
      o.onBack?.();
      break;
    case 'start':
      o.onMenu?.();
      break;
    case 'lb':
      o.onTab?.('prev');
      break;
    case 'rb':
      o.onTab?.('next');
      break;
    default:
      break;
  }
}

const AXIS_PRESS = 0.5;
const AXIS_RELEASE = 0.3;
const axisHeld: Record<'leftX' | 'leftY', NavDirection | null> = { leftX: null, leftY: null };

function handleAxis(name: string, value: number): void {
  if (name !== 'leftX' && name !== 'leftY') {
    return;
  }
  const held = axisHeld[name];
  const positive: NavDirection = name === 'leftX' ? 'right' : 'down';
  const negative: NavDirection = name === 'leftX' ? 'left' : 'up';
  let next: NavDirection | null = held;
  if (value > AXIS_PRESS) {
    next = positive;
  } else if (value < -AXIS_PRESS) {
    next = negative;
  } else if (Math.abs(value) < AXIS_RELEASE) {
    next = null;
  }
  if (next === held) {
    return;
  }
  if (held) {
    handleButton(held, false);
  }
  axisHeld[name] = next;
  if (next) {
    handleButton(next, true);
  }
}

const KEY_DIRECTIONS: Readonly<Record<string, NavDirection>> = {
  ArrowUp: 'up',
  ArrowDown: 'down',
  ArrowLeft: 'left',
  ArrowRight: 'right',
};

function isTextEntry(el: Element | null): boolean {
  if (!(el instanceof HTMLElement)) {
    return false;
  }
  if (el instanceof HTMLInputElement) {
    return !['button', 'checkbox', 'radio', 'submit', 'reset', 'file', 'color'].includes(el.type);
  }
  return el instanceof HTMLTextAreaElement || el instanceof HTMLSelectElement || el.isContentEditable;
}

function onKeyDown(e: KeyboardEvent): void {
  const inst = topInstance();
  if (!inst || inst.opts.current?.keyboard === false || e.defaultPrevented) {
    return;
  }
  if (e.key === 'Escape') {
    if (inst.opts.current?.onBack) {
      e.preventDefault();
      inst.opts.current.onBack();
    }
    return;
  }
  const dir = KEY_DIRECTIONS[e.key];
  if (!dir || e.ctrlKey || e.altKey || e.metaKey) {
    return;
  }
  const target = e.target instanceof Element ? e.target : null;
  if (isTextEntry(target) && (dir === 'left' || dir === 'right' || target instanceof HTMLSelectElement)) {
    return;
  }
  e.preventDefault();
  if (!e.repeat) {
    navigate(inst, dir);
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Input sources (attached while at least one instance is mounted)
// ---------------------------------------------------------------------------------------------------------------------

const STANDARD_BUTTONS: readonly (GamepadButtonName | null)[] = [
  'a',
  'b',
  'x',
  'y',
  'lb',
  'rb',
  null,
  null,
  'back',
  'start',
  'ls',
  'rs',
  'up',
  'down',
  'left',
  'right',
  'guide',
];

let connected = false;
const connectedListeners = new Set<() => void>();

function setConnected(v: boolean): void {
  if (connected !== v) {
    connected = v;
    connectedListeners.forEach((l) => l());
  }
}

function subscribeConnected(l: () => void): () => void {
  connectedListeners.add(l);
  return () => connectedListeners.delete(l);
}

let offKiosk: (() => void) | null = null;
let rafId = 0;
const padPrev = new Map<number, boolean[]>();

function pollBrowserGamepads(): void {
  const pads =
    typeof navigator !== 'undefined' && typeof navigator.getGamepads === 'function' ? navigator.getGamepads() : [];
  let any = false;
  for (const gp of pads) {
    if (!gp) {
      continue;
    }
    any = true;
    const prev = padPrev.get(gp.index);
    const cur = gp.buttons.map((b) => b.pressed);
    if (prev) {
      cur.forEach((pressed, i) => {
        const name = STANDARD_BUTTONS[i];
        if (name && pressed !== prev[i]) {
          handleButton(name, pressed);
        }
      });
    }
    padPrev.set(gp.index, cur);
    handleAxis('leftX', gp.axes[0] ?? 0);
    handleAxis('leftY', gp.axes[1] ?? 0);
  }
  setConnected(any);
  rafId = requestAnimationFrame(pollBrowserGamepads);
}

function attachSources(): void {
  if (offKiosk !== null) {
    return;
  }
  installGlobalHandlers();
  window.addEventListener('keydown', onKeyDown);
  offKiosk = events.onKiosk('gamepad', (p) => {
    if (p.kind === 'axis') {
      handleAxis(p.name, p.value);
    } else if (p.name === 'connection') {
      setConnected(p.connected ?? false);
    } else {
      setConnected(true);
      handleButton(p.name, p.pressed ?? p.value > 0.5);
    }
  });
  if (!isTauri() && typeof requestAnimationFrame === 'function') {
    rafId = requestAnimationFrame(pollBrowserGamepads);
  }
}

function detachSources(): void {
  if (offKiosk === null) {
    return;
  }
  window.removeEventListener('keydown', onKeyDown);
  offKiosk();
  offKiosk = null;
  if (rafId !== 0) {
    cancelAnimationFrame(rafId);
    rafId = 0;
  }
  padPrev.clear();
  for (const dir of Array.from(repeatTimers.keys())) {
    stopRepeat(dir);
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Hook
// ---------------------------------------------------------------------------------------------------------------------

/** Whether a controller is connected right now (prompts switch between controller glyphs and keys on it). */
export function useGamepadConnected(): boolean {
  return useSyncExternalStore(
    subscribeConnected,
    () => connected,
    () => false,
  );
}

export function useGamepad(options: UseGamepadOptions = {}): GamepadController {
  const optsRef = useRef<UseGamepadOptions>(options);
  optsRef.current = options;

  useEffect(() => {
    const inst: Instance = { opts: optsRef };
    instances.push(inst);
    attachSources();
    return () => {
      const i = instances.indexOf(inst);
      if (i >= 0) {
        instances.splice(i, 1);
      }
      if (instances.length === 0) {
        detachSources();
      }
    };
  }, []);

  const isConnected = useSyncExternalStore(
    subscribeConnected,
    () => connected,
    () => false,
  );

  const moveFocus = useCallback(
    (dir: NavDirection) => navigateFocus(dir, optsRef.current.scope?.current ?? document),
    [],
  );
  const focusFirst = useCallback(() => {
    const el = collectNavigables(optsRef.current.scope?.current ?? document)[0] ?? null;
    if (el) {
      focusElement(el);
    }
    return el;
  }, []);

  return useMemo(() => ({ moveFocus, focusFirst, connected: isConnected }), [moveFocus, focusFirst, isConnected]);
}
