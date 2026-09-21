/**
 * Store entry point. `bootstrapStores()` loads settings → theme + i18n + system info → auth status (+ session),
 * then wires every Agent/kiosk event listener and cross-store reaction exactly once. `resetStores()` clears the
 * user-scoped slices on logout (listeners stay, the lock screen still needs them); `teardownStores()` removes
 * the listeners and timers (HMR / tests) so a later `bootstrapStores()` starts clean.
 */
import type { Session } from '@clubshell/contracts';
import i18n, { initI18n } from '@/i18n';
import { log } from '@/lib/logger';
import { api, events, isTauri } from '@/lib/tauri';
import { useAuthStore } from './auth';
import { useChatStore } from './chat';
import { useGamesStore } from './games';
import { stopToastTimer, useNotificationsStore } from './notifications';
import { startSessionTicker, stopSessionTicker, useSessionStore } from './session';
import { useSettingsStore } from './settings';
import { useShopStore } from './shop';
import { useThemeStore } from './theme';
import { useWalletStore } from './wallet';

export * from './auth';
export * from './chat';
export * from './games';
export * from './notifications';
export * from './session';
export * from './settings';
export * from './shop';
export * from './theme';
export * from './wallet';

let bootPromise: Promise<void> | null = null;
let unsubscribes: (() => void)[] = [];

const t = (key: string, vars?: Record<string, unknown>): string => i18n.t(key, vars);

function currentRoute(): string {
  return typeof window === 'undefined' ? '' : (window.location.hash.replace(/^#/, '').split('?')[0] ?? '');
}

/** Loads everything a logged-in user needs; each part fails on its own. */
async function loadUserData(): Promise<void> {
  await Promise.allSettled([
    useWalletStore.getState().load(),
    useGamesStore.getState().refreshRunning(),
    useChatStore.getState().load(),
    useShopStore.getState().loadOrders(true),
  ]);
}

function wireListeners(): void {
  const auth = useAuthStore.getState;
  const session = useSessionStore.getState;
  const games = useGamesStore.getState;
  const wallet = useWalletStore.getState;
  const shop = useShopStore.getState;
  const chat = useChatStore.getState;
  const notify = useNotificationsStore.getState;
  const settings = useSettingsStore.getState;
  const theme = useThemeStore.getState;

  unsubscribes = [
    // ----- cross-store reactions ---------------------------------------------------------------------------------------
    useAuthStore.subscribe(
      (s) => s.user,
      (user, prev) => {
        if (prev && !user) {
          resetStores();
        } else if (user && (!prev || prev.id !== user.id)) {
          void loadUserData();
        }
      },
    ),
    useSettingsStore.subscribe(
      (s) => s.settings.theme,
      (name) => {
        if (name !== theme().name) {
          void theme().load(name);
        }
      },
    ),
    useSettingsStore.subscribe(
      (s) => s.settings.locale,
      (locale) => {
        if (i18n.isInitialized && i18n.language !== locale) {
          void settings().applyLocale(locale);
        }
      },
    ),

    // ----- session -----------------------------------------------------------------------------------------------------
    events.on('session.updated', (s: Session) => session().setSession(s)),
    events.on('session.warning', (w) => {
      session().onWarning(w);
      notify().notifySessionWarning(w);
    }),
    events.on('session.ended', (e) => {
      session().onEnded(e);
      notify().clearSessionWarning();
      games().reset();
      notify().push({
        title: t('session.endedTitle'),
        body: t(`session.endedReason.${e.reason}`),
        level: e.reason === 'timeUp' ? 'warning' : 'info',
        ttlSec: 10,
        source: 'system',
      });
    }),

    // ----- wallet / shop -----------------------------------------------------------------------------------------------
    events.on('wallet.updated', (b) => {
      wallet().onUpdated(b);
      auth().setBalance(b.amount);
    }),
    events.on('shop.orderUpdated', (o) => {
      const prev = shop().orders.find((x) => x.id === o.id);
      shop().onOrderUpdated(o);
      if (prev && prev.status !== o.status && o.status !== 'pending') {
        const short = o.id.slice(0, 6).toUpperCase();
        notify().push({
          id: `order-${o.id}`,
          title:
            o.status === 'done'
              ? t('notifications.orderDone')
              : t('notifications.orderUpdated', { id: short, status: t(`shop.status.${o.status}`) }),
          body: t(`shop.statusHint.${o.status}`),
          level: o.status === 'done' ? 'success' : o.status === 'cancelled' ? 'warning' : 'info',
          action: { label: t('notifications.view'), command: '/shop', args: null },
          source: 'agent',
        });
      }
    }),

    // ----- chat / notifications / admin --------------------------------------------------------------------------------
    events.on('chat.message', (m) => {
      chat().onMessage(m);
      if (m.senderId !== auth().user?.id && currentRoute() !== '/chat') {
        notify().push({
          id: `chat-${m.id}`,
          title: t('notifications.newMessage', { name: m.senderName }),
          body: m.text,
          level: 'info',
          action: { label: t('notifications.open'), command: '/chat', args: null },
          source: 'agent',
        });
      }
    }),
    events.on('notification.push', (n) => notify().pushNotification(n)),
    events.on('admin.message', (m) => notify().pushAdmin(m)),
    events.on('admin.remoteControl', (e) => {
      notify().setRemoteControl(e);
      if (e.state === 'stopped') {
        notify().push({ title: t('admin.remoteControlEnded'), level: 'info', ttlSec: 4, source: 'system' });
      }
    }),

    // ----- games -------------------------------------------------------------------------------------------------------
    events.on('game.stateChanged', (e) => {
      games().onStateChanged(e);
      if (e.state === 'failed') {
        notify().push({
          title: t('notifications.gameFailed', { title: e.title }),
          body: e.error ? t(`errors.${e.error.code}`) : '',
          level: 'error',
          source: 'system',
        });
      } else if (e.state === 'exited') {
        notify().push({
          title: t('notifications.gameExited', { title: e.title }),
          level: 'info',
          ttlSec: 5,
          source: 'system',
        });
      } else if (e.state === 'killed') {
        notify().push({
          title: t('notifications.gameKilled', { title: e.title }),
          level: 'warning',
          ttlSec: 6,
          source: 'system',
        });
      }
    }),

    // ----- system ------------------------------------------------------------------------------------------------------
    events.on('sys.metrics', (m) => settings().setMetrics(m)),
    events.on('sys.connectivity', (c) => {
      const before = notify().serverConnectivity;
      notify().setServerConnectivity(c.state);
      if (before !== c.state) {
        notify().push({
          id: 'connectivity',
          title: c.state === 'online' ? t('notifications.connectivityOnline') : t('notifications.connectivityOffline'),
          level: c.state === 'online' ? 'success' : 'warning',
          ttlSec: c.state === 'online' ? 4 : 8,
          source: 'system',
        });
      }
    }),
    events.on('policy.changed', (p) => {
      settings().setPolicy(p.policy);
      notify().push({
        id: 'policy',
        title: t('notifications.policyChanged'),
        level: 'info',
        ttlSec: 5,
        source: 'system',
      });
    }),
    events.on('update.available', (u) =>
      notify().push({
        id: 'update-available',
        title: t('update.availableTitle', {
          component: t(`update.component.${u.manifest.component}`),
          version: u.manifest.version,
        }),
        level: 'info',
        ttlSec: 8,
        source: 'system',
      }),
    ),
    events.on('update.progress', (p) => notify().setUpdateProgress(p)),
    events.on('update.ready', (u) => notify().setUpdateReady(u)),
    events.on('auth.expired', (e) => auth().onExpired(e.reason)),
    events.on('shell.command', (c) => {
      switch (c.command) {
        case 'lock':
          session().applyState('locked');
          break;
        case 'unlock':
          session().applyState('active');
          break;
        case 'reboot':
          notify().setScheduledPower({
            kind: 'reboot',
            at: Date.now() + c.args.delaySec * 1000,
            message: c.args.message ?? null,
          });
          notify().push({
            id: 'reboot',
            title: t('admin.rebootScheduled'),
            body: c.args.message ?? t('admin.rebootIn', { seconds: c.args.delaySec }),
            level: 'warning',
            ttlSec: null,
            source: 'system',
          });
          break;
        case 'showMessage':
          notify().push({
            title: c.args.title,
            body: c.args.body,
            level: c.args.level,
            ttlSec: c.args.ttlSec ?? undefined,
            source: 'agent',
          });
          break;
        case 'showAds':
          log.debug('shell.command showAds', { items: c.args.items.length });
          break;
      }
    }),

    // ----- kiosk (local Rust layer) ------------------------------------------------------------------------------------
    events.onKiosk('connectivity', (c) => {
      const before = notify().agentConnected;
      notify().setAgentConnectivity(c.connected, c.attempts);
      if (before !== c.connected) {
        notify().push({
          id: 'agent-link',
          title: c.connected ? t('notifications.agentOnline') : t('notifications.agentOffline'),
          level: c.connected ? 'success' : 'error',
          ttlSec: c.connected ? 4 : null,
          source: 'system',
        });
        if (c.connected) {
          void auth().refresh();
        }
      }
    }),
    events.onKiosk('themeChanged', (p) => {
      if (p.theme) {
        theme().apply(p.theme);
      } else {
        void theme().load(p.name);
      }
    }),
    events.onKiosk('localeChanged', (p) => void settings().applyLocale(p.locale)),
    events.onKiosk('monitorChanged', () => void settings().refreshKiosk()),
    events.onKiosk('hotkey', (h) => {
      const { volume } = settings().settings;
      switch (h.name) {
        case 'lock':
          if (session().state === 'active') {
            void session()
              .lock('hotkey')
              .catch((e: unknown) => notify().pushError(e));
          }
          break;
        case 'callAdmin':
          api.system
            .callAdmin('help')
            .then(() =>
              notify().push({
                id: 'call-admin',
                title: t('notifications.callAdminSent'),
                body: t('admin.adminOnWay'),
                level: 'success',
                source: 'system',
              }),
            )
            .catch((e: unknown) => notify().pushError(e, t('admin.callAdminTitle')));
          break;
        case 'volumeUp':
        case 'volumeDown': {
          const level = Math.min(100, Math.max(0, volume + (h.name === 'volumeUp' ? 10 : -10)));
          void settings()
            .setVolume(level, false)
            .then(() =>
              notify().push({
                id: 'volume',
                title: t('kiosk.volumeChanged', { level }),
                level: 'info',
                ttlSec: 2,
                source: 'system',
              }),
            )
            .catch((e: unknown) => notify().pushError(e));
          break;
        }
        case 'mute':
          void settings()
            .toggleMute()
            .then(() =>
              notify().push({
                id: 'volume',
                title: settings().settings.muted ? t('kiosk.muted') : t('kiosk.unmuted'),
                level: 'info',
                ttlSec: 2,
                source: 'system',
              }),
            )
            .catch((e: unknown) => notify().pushError(e));
          break;
        case 'blocked':
          notify().push({
            id: 'hotkey-blocked',
            title: t('kiosk.hotkeyBlocked'),
            body: h.combo ?? '',
            level: 'warning',
            ttlSec: 3,
            source: 'system',
          });
          break;
        default:
          break;
      }
    }),
  ];
}

/**
 * One-time boot: settings → theme + i18n + system → listeners + ticker → auth status → user data + catalogue.
 * Idempotent; a failed boot clears the guard so it can be retried.
 */
export function bootstrapStores(): Promise<void> {
  if (bootPromise) {
    return bootPromise;
  }
  bootPromise = (async () => {
    const settings = useSettingsStore.getState();
    await settings.load();
    const { locale, theme } = useSettingsStore.getState().settings;
    await Promise.all([initI18n(locale), useThemeStore.getState().load(theme), settings.loadSystem()]);
    wireListeners();
    startSessionTicker();
    await useAuthStore.getState().refresh();
    void useGamesStore.getState().load();
    void useShopStore.getState().load();
    void useThemeStore.getState().loadList();
    if (useAuthStore.getState().user) {
      await loadUserData();
    }
    log.info(`stores ready (${isTauri() ? 'tauri' : 'mock'})`);
  })().catch((e: unknown) => {
    bootPromise = null;
    throw e;
  });
  return bootPromise;
}

/** `true` once {@link bootstrapStores} has completed (or is in flight). */
export function isBootstrapped(): boolean {
  return bootPromise !== null;
}

/** Clears every user-scoped slice (logout / `auth.expired`). Settings and theme are device-level and stay. */
export function resetStores(): void {
  useSessionStore.getState().reset();
  useGamesStore.getState().reset();
  useWalletStore.getState().reset();
  useShopStore.getState().reset();
  useChatStore.getState().reset();
  useNotificationsStore.getState().reset();
  if (useAuthStore.getState().user) {
    useAuthStore.getState().reset();
  }
}

/** Removes listeners and timers; the next {@link bootstrapStores} starts from scratch. */
export function teardownStores(): void {
  for (const off of unsubscribes) {
    off();
  }
  unsubscribes = [];
  stopSessionTicker();
  stopToastTimer();
  bootPromise = null;
}

if (import.meta.hot) {
  import.meta.hot.dispose(teardownStores);
}
