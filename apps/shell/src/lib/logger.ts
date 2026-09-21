/**
 * Frontend logger: console always; `warn`/`error` are also forwarded to the Agent log via
 * `sys_log_client_error` (rate limited, never throws) and to tauri-plugin-log when inside Tauri.
 */
import * as pluginLog from '@tauri-apps/plugin-log';
import type { ClientErrorLevel } from '@clubshell/contracts';
import { api, isTauri } from '@/lib/tauri';

type Level = 'debug' | 'info' | 'warn' | 'error';

/** Max forwarded warn/error entries per second (the Agent silently drops beyond 10/s). */
const FORWARD_LIMIT_PER_SEC = 8;
let windowStart = 0;
let windowCount = 0;

function allowForward(): boolean {
  const now = Date.now();
  if (now - windowStart >= 1000) {
    windowStart = now;
    windowCount = 0;
  }
  windowCount += 1;
  return windowCount <= FORWARD_LIMIT_PER_SEC;
}

function stringify(value: unknown): string {
  if (typeof value === 'string') {
    return value;
  }
  if (value instanceof Error) {
    return `${value.name}: ${value.message}`;
  }
  try {
    return JSON.stringify(value);
  } catch {
    return String(value);
  }
}

function currentRoute(): string | undefined {
  if (typeof window === 'undefined') {
    return undefined;
  }
  const hash = window.location.hash;
  return hash.startsWith('#') ? hash.slice(1) : window.location.pathname;
}

function stackOf(args: unknown[]): string | undefined {
  const err = args.find((a): a is Error => a instanceof Error);
  return err?.stack ?? undefined;
}

function forward(level: ClientErrorLevel, message: string, args: unknown[]): void {
  if (!allowForward()) {
    return;
  }
  const payload = { level, message, stack: stackOf(args) ?? null, route: currentRoute() ?? null };
  api.system.logClientError(payload).catch(() => undefined);
  if (isTauri()) {
    const line = args.length > 0 ? `${message} ${args.map(stringify).join(' ')}` : message;
    const fn = level === 'error' ? pluginLog.error : pluginLog.warn;
    fn(line).catch(() => undefined);
  }
}

function emit(level: Level, message: string, args: unknown[]): void {
  const prefix = `[shell] ${message}`;
  switch (level) {
    case 'debug':
      if (import.meta.env.DEV) {
        console.debug(prefix, ...args);
        if (isTauri()) {
          pluginLog
            .debug(args.length > 0 ? `${message} ${args.map(stringify).join(' ')}` : message)
            .catch(() => undefined);
        }
      }
      break;
    case 'info':
      console.info(prefix, ...args);
      if (isTauri()) {
        pluginLog
          .info(args.length > 0 ? `${message} ${args.map(stringify).join(' ')}` : message)
          .catch(() => undefined);
      }
      break;
    case 'warn':
      console.warn(prefix, ...args);
      forward('warn', message, args);
      break;
    case 'error':
      console.error(prefix, ...args);
      forward('error', message, args);
      break;
  }
}

/** Application logger. */
export const log = {
  debug: (message: string, ...args: unknown[]): void => emit('debug', message, args),
  info: (message: string, ...args: unknown[]): void => emit('info', message, args),
  warn: (message: string, ...args: unknown[]): void => emit('warn', message, args),
  error: (message: string, ...args: unknown[]): void => emit('error', message, args),
};

/** Registers `window.onerror` / `unhandledrejection` forwarding once. */
export function installGlobalErrorHandlers(): void {
  if (
    typeof window === 'undefined' ||
    (window as { __clubshellErrorsInstalled?: boolean }).__clubshellErrorsInstalled
  ) {
    return;
  }
  (window as { __clubshellErrorsInstalled?: boolean }).__clubshellErrorsInstalled = true;
  window.addEventListener('error', (e) => {
    log.error('Uncaught error', e.error ?? e.message);
  });
  window.addEventListener('unhandledrejection', (e) => {
    log.error('Unhandled rejection', e.reason);
  });
}
