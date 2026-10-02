/**
 * The console as an installable app (PWA): registers `/sw.js` in a production build and keeps the browser's install
 * offer (`beforeinstallprompt`, Chromium browsers), so the console can show its own "install" button. The offer may
 * come before React mounts, hence it is caught at module load (imported first by `main.tsx`).
 */
import { useSyncExternalStore } from 'react';

interface InstallPromptEvent extends Event {
  prompt(): Promise<void>;
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>;
}

let offer: InstallPromptEvent | null = null;
const listeners = new Set<() => void>();
const notify = (): void => listeners.forEach((l) => l());

if (typeof window !== 'undefined') {
  window.addEventListener('beforeinstallprompt', (e) => {
    // The browser's own mini-bar stays away; the console offers the install itself.
    e.preventDefault();
    offer = e as InstallPromptEvent;
    notify();
  });
  window.addEventListener('appinstalled', () => {
    offer = null;
    notify();
  });
  if ('serviceWorker' in navigator && import.meta.env.PROD) {
    window.addEventListener('load', () => {
      navigator.serviceWorker.register('/sw.js').catch(() => undefined);
    });
  }
}

/** Whether the browser offers the install now, and the call that shows its dialog. */
export function useInstall(): { canInstall: boolean; install: () => Promise<void> } {
  const canInstall = useSyncExternalStore(
    (l) => {
      listeners.add(l);
      return () => listeners.delete(l);
    },
    () => offer !== null,
  );
  return {
    canInstall,
    install: async () => {
      const e = offer;
      if (!e) return;
      // An offer is good for one prompt; after a "no" the browser sends a fresh one later.
      offer = null;
      notify();
      await e.prompt();
      await e.userChoice;
    },
  };
}
