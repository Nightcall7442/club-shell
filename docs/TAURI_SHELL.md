# ClubShell — Tauri Shell (`apps/shell`)

Status: descriptive. The normative contracts are `ARCHITECTURE.md` (§2, §5.2, §6.2, §12.2) and
`TAURI_COMMANDS.md`; this document explains how `apps/shell` implements them. Symbol names below are
taken from the source under `apps/shell/src-tauri/src` and `apps/shell/src`.

The Shell is `clubshell-shell.exe`: a Tauri 2 host process (Rust, WebView2) that the Agent launches in
the kiosk user's interactive session (`CreateProcessAsUser`). It is UI only. Every privileged action is a
request over the named pipe `\\.\pipe\clubshell-agent` to `ClubShellAgent`; the Shell never calls a
privileged Win32 API and never touches the server directly.

```
apps/shell/
├── src-tauri/              Rust host (crate clubshell-shell, lib clubshell_shell_lib)
│   ├── src/lib.rs          run(): config → logging → single instance → transport → Builder → event loop
│   ├── src/main.rs         binary entry (windows_subsystem = "windows" in release)
│   ├── src/config.rs       ShellConfig (shell.json + CLUBSHELL_* env)
│   ├── src/logging.rs      tracing → logs\shell.YYYY-MM-DD.log (+ shell-crash.log)
│   ├── src/state.rs        AppState, ShellError, KioskControl seam
│   ├── src/agent/          AgentClient (pipe | mock), PipeTransport, Supervisor, EventForwarder
│   ├── src/commands/       #[tauri::command] proxies, one module per IPC group
│   ├── src/kiosk/          hardening: keyboard hook, alt-tab, taskbar, window guard, overlay, monitors, idle
│   ├── src/gamepad.rs      gilrs poll thread → kiosk://gamepad
│   ├── src/tray.rs         operator tray (dev / admin-unlock only)
│   ├── tauri.conf.json     windows, CSP, asset protocol scope, bundle
│   └── capabilities/default.json
└── src/                    React 18 + TypeScript + Vite + Tailwind + Zustand + i18next
    ├── main.tsx            boot(role): guards → i18n → stores → theme → <App/>
    ├── App.tsx             RouterProvider + GlobalListeners
    ├── router.tsx          hash router, guards, OverlayScreen / AdsScreen
    ├── lib/tauri.ts        invoke/listen wrapper, mock switch, typed `api` and `events`
    ├── store/              zustand stores; bootstrapStores() wires every listener once
    ├── theme/              themes.ts (applyTheme), tokens.css, animations.css
    ├── i18n/               en/ru/uz bundles + kiosk_i18n_bundle overrides
    ├── screens/            code-split route screens
    └── mocks/              in-browser mock registry (VITE_MOCK=1)
```

---

## 1. Rust side

### 1.1 `lib.rs` — `run()` sequence

`run()` is the whole process lifecycle. Order matters because each step depends on the previous one:

| # | Step | Code | Notes |
|---|------|------|-------|
| 1 | Load configuration | `ShellConfig::load()` | Embedded `config/shell.default.json` → `<data dir>\shell.json` overlay (deep merge, `merge_json`) → `CLUBSHELL_*` env → `validate()`. On error the embedded defaults are used and the error is logged once logging is up. |
| 2 | Logging | `logging::init_logging(&config)` | Returns a `WorkerGuard` that must stay alive until the event loop returns (non-blocking writer flush). |
| 3 | Single instance | `acquire_single_instance()` | `CreateMutexW("Global\\ClubShellShell")` (`SINGLE_INSTANCE_MUTEX`). `ERROR_ALREADY_EXISTS` → `focus_existing_instance()` (winutil `find_window` + `force_foreground`) and return. Skipped in dev mode. |
| 4 | Agent transport | `AgentClient::connect(&config).await` | `CLUBSHELL_DEV=1` → `MockTransport`. Debug builds probe the pipe once for `ipc.connectTimeoutMs` and fall back to the mock when unreachable. Release builds never fall back: the supervisor reconnects forever. |
| 5 | Shared state | `AppState::new(config, agent)` | One `Arc<AppStateInner>`; `manage`d on the Builder. |
| 6 | Tauri Builder | `.plugin(logging::tauri_log_plugin())`, `tauri_plugin_process`, `tauri_plugin_os`, `.manage(state)`, `.invoke_handler(commands::invoke_handler!(kiosk::commands::…))` | The `invoke_handler!` macro expands to one `tauri::generate_handler![]` with every proxy command plus the `kiosk_*` commands passed as arguments (a second `invoke_handler` call would replace the first). |
| 7 | `setup` | `state.agent.start(None)` → `EventForwarder::spawn(handle, state)` → `kiosk::spawn_all(&handle, &state)?` → `app.manage(kiosk)` | Reconnect supervisor, Agent→webview fan-out, then hardening + gamepad + tray. The main window is focused unless dev mode. |
| 8 | Event loop | `app.run(...)` | `WindowEvent::Destroyed` on `main` (webview crash) → `shutdown(app)`; `ExitRequested` / `Exit` → `cleanup(&state)` (`kiosk().shutdown()` then `agent.shutdown()`). |

`shutdown(app)` is the single orderly exit path (tray *Exit*, `kiosk_exit`); it sets the `EXITING` flag so
the destroyed main window does not request a second exit. When the process ends for any reason the Agent
watchdog restarts it (`ARCHITECTURE.md` §2 rule 3–4).

### 1.2 `config.rs` — `ShellConfig`

`ShellConfig` mirrors `shell.json` 1:1 (`#[serde(rename_all = "camelCase")]`) and adds a
`#[serde(skip)] runtime: RuntimeConfig { data_dir, token_path, dev, overlay_path }` so
`settings_get_shell_config` cannot leak process-level paths. Environment overrides (`config::env`):

| Variable | Effect |
|----------|--------|
| `CLUBSHELL_DATA_DIR` | Data directory (default `%ProgramData%\ClubShell`) |
| `CLUBSHELL_PIPE` | Overrides `ipc.pipeName` |
| `CLUBSHELL_SHELL_TOKEN` | Path of the shell token file (default `<data dir>\secure\shell.token`) |
| `CLUBSHELL_LOCALE` | Overrides `locale` (`en` / `ru` / `uz`) |
| `CLUBSHELL_DEV` | `1`/`true` → mock transport, hardening off |

`validate()` rejects zero timeouts, inverted backoff, a non-bare `theme` name, `gamepad.pollMs == 0`,
a dead-zone outside `[0, 1)`, `ui.gridColumns == 0`, volume > 100 and an unknown `logging.level`.
`load_shell_token()` reads the 64-hex-char token on every `auth.hello` (it is regenerated at each Agent
start, so it is never cached). Path helpers: `logs_dir()`, `themes_dir()`, `theme_path(name)`,
`media_dir()`, `locales_dir()`, `shell_json_path()`.

### 1.3 `logging.rs`

`tracing` subscriber with a JSON file layer (`tracing-appender`, daily rotation, prefix `shell`, suffix
`log`, `MAX_LOG_FILES = 14`) under `config.logs_dir()`, plus a plain stderr layer in dev / debug builds.
`RUST_LOG` overrides `logging.level`; noisy crates (`tao`, `wry`, `hyper`, `tokio`, `mio`, `gilrs`) are
pinned to `warn`. The `log` façade is bridged so `tauri-plugin-log` (webview `log()` calls) lands in the
same file — `tauri_log_plugin()` builds the plugin with `skip_logger()`. A panic hook writes the panic
through `tracing` **and** synchronously to `logs\shell-crash.log` (`CRASH_LOG`), because release builds
abort before the non-blocking writer flushes.

### 1.4 `state.rs`

* `ShellError { code: ErrorCode, message, details: Option<Value>, source: ErrorSource }` — the value every
  command rejects with (`TAURI_COMMANDS.md` §1.1). `ErrorSource` is `ipc` (Agent error envelope), `pipe`
  (transport: `agentOffline` / `timeout` / `protocolError`), `tauri` (Rust validation / OS) or `mock`.
  `From` conversions exist for `IpcError`, `WinUtilError`, `ProtocolError`, `serde_json::Error`,
  `anyhow::Error`, `tauri::Error` and `ConfigError`. `CmdResult<T> = Result<T, ShellError>`.
* `AppState` (cheap `Clone`, derefs to `AppStateInner`): `config`, `agent: Arc<AgentClient>`,
  `session_cache` / `user_cache` (`parking_lot::RwLock<Option<_>>`), `connectivity` (`watch::Sender<Option<ConnectivityEvent>>`
  — Agent ↔ Server link), `admin_unlock` (cached `sys.unlockAdmin` token + expiry), `launch_lock`
  (`tokio::sync::Mutex<()>` serialising `games_launch` / `apps_launch`), `game_running: AtomicBool`, and the
  `KioskControl` seam.
* `KioskControl` trait (`set_locked`, `set_game_mode`, `shutdown`) is how the agent event pipeline drives the
  kiosk module without a dependency cycle; `NoopKiosk` is installed until `kiosk::spawn_all` replaces it.

### 1.5 `agent/` — transport

| Type | File | Role |
|------|------|------|
| `AgentClient` (`enum { Pipe(PipeAgent), Mock(MockTransport) }`) | `agent/mod.rs` | What commands call: `request<Req, Res>(name, &payload)`, `request_with_timeout`, `request_optional` (nullable response), `request_raw`, `send(IpcEnvelope, timeout)`, `events()` (broadcast receiver surviving reconnects), `state()` (watch of `ConnectivityStatus`), `pc_info()` (`AuthHelloResponse`), `start`, `shutdown`, `is_mock`. `HELLO_WAIT = 3 s` bounds how long a request waits for the connection to reach `Connected` before failing with `agentOffline`. |
| `PipeTransport` | `agent/pipe_client.rs` | Owns the current `clubshell_winutil::pipe::PipeClient`, re-fans its events into one long-lived `broadcast` channel, performs `auth.hello`, maps transport errors to `ShellError`, keeps `MetricsSnapshot` counters (`requests`, `errors`, `timeouts`, `reconnects`, `connected_since`). Heartbeats (`sys.ping` every 5 s, three misses close the connection — `HEARTBEAT_INTERVAL`, `HEARTBEAT_MAX_MISSES` in `crates/winutil`) live inside `PipeClient`. |
| `Supervisor` + `Backoff` | `agent/reconnect.rs` | connect → hello → run until the pipe closes → exponential backoff with ±25 % jitter between `ipc.reconnectMinMs` and `ipc.reconnectMaxMs` → again, forever, until the shutdown watch flips. Every transition publishes a `ConnectivityStatus { agent: Connecting\|Connected\|Disconnected, attempts, since }`. |
| `EventForwarder` | `agent/events.rs` | Background task: `agent.events()` + `agent.state()` → webview. See §3. |
| `MockTransport` | `agent/mod.rs` | Canned answers for every IPC name with a little state (user, session, settings, running game) and matching events; admin PIN `0000`, currency UZS, two tariffs, two Steam games. |

### 1.6 `commands/` — proxy handlers

One module per IPC group (`auth`, `session` (also booking, tournaments, profile), `games`, `apps`,
`wallet`, `shop`, `chat`, `settings`, `system` (also policy, update)). Conventions (`commands/mod.rs`):

* Rust function name = command name (`auth_login`, `session_start`, …); `COMMAND_NAMES` lists all 64.
* Every handler is `async`, takes `tauri::State<'_, AppState>` and returns `CmdResult<T>`.
* Arguments: Tauri maps camelCase JS keys to snake_case parameters; DTO arguments (`req`, `q`, `patch`,
  `e`) are `clubshell_protocol` structs.
* Input is validated first (`validate::required_text`, `optional_text`, `range`, `optional_range`,
  `paging`, `pin`, `bare_name` → `ShellError::validation`, `source: tauri`), then forwarded with
  `state.agent.request(names::<group>::<NAME>, &payload)`.
* `commands::emit(&app, "kiosk://…", payload)` broadcasts a local event to every window; failures are logged,
  never surfaced.

Local (no IPC) commands in `settings.rs`: `settings_get_theme`, `settings_list_themes`,
`settings_get_shell_config` (blanks `kiosk.adminPinHash`). `settings_set` proxies to the Agent and then emits
`kiosk://themeChanged` (payload `Theme`) / `kiosk://localeChanged` (`{ locale }`) when those keys were in the
patch. `sys_log_client_error` is fire-and-forget: it logs under target `webview` and forwards on a
background task.

### 1.7 `kiosk/` — hardening

`Kiosk` (`kiosk/mod.rs`) owns every native guard and is `manage`d as `Arc<Kiosk>`; `kiosk_*` commands take
`State<'_, Arc<Kiosk>>`. `dev_mode(config)` (`CLUBSHELL_DEV=1` **or** a debug build) disables all hardening:
normal window, nothing blocked, no secondary windows, F11 toggles fullscreen.

| Module | Type | What it does |
|--------|------|--------------|
| `keyboard_hook.rs` | `KeyboardHook`, `HookPolicy`, `Hotkey` | `WH_KEYBOARD_LL` on a dedicated message-pump thread. Blocks `policy.explorer.blockedKeyCombos` + `shell.json → kiosk.blockAltTab/blockWinKey`; registered hotkeys go to `kiosk://hotkey` (`exit`, `callAdmin`, `lock`, `volumeUp`, `volumeDown`, `mute`; `blocked` rate-limited 1/s). Feeds the idle detector's `ActivityFeed`. |
| `alt_tab.rs` | `AltTabBlocker` | Foreground / topmost guard and stray-window sweep (`kiosk.topmostGuard`). |
| `window_guard.rs` | `WindowGuard` (`MAIN_LABEL = "main"`) | Fullscreen + always-on-top re-assertion (250 ms), `move_to_monitor`, `kiosk://focus` when the foreground changes (game running). |
| `taskbar.rs` | `Taskbar` | Hides the Explorer taskbar and re-hides it after `WM_DISPLAYCHANGE`. |
| `overlay.rs` | `Overlay` (`OVERLAY_LABEL = "overlay"`, url `index.html#/overlay`) | Lazily created transparent always-on-top webview spanning the virtual screen. Kinds: `lock`, `ads`, `message`, `none`. Broadcasts `kiosk://overlay { kind, payload }`; non-lock kinds are click-through. |
| `multi_monitor.rs` | `MultiMonitor` (`ADS_LABEL = "ads"`, `ads-2`, …) | Enumerates monitors, keeps the main window on `monitors.primaryIndex`, creates one `index.html#/ads?monitor=N&mode=black\|ads\|…` window per secondary monitor, emits `kiosk://monitorChanged`. |
| `idle_detector.rs` | `IdleDetector` | `GetLastInputInfo` + hook feed → `kiosk://idle { idle, idleSec, stage }` at `idle.dimAfterSec` / `timeoutSec` / `screensaverAfterSec`; `policy.kiosk.idleTimeoutSec` overrides via `set_timeout`. |
| `commands.rs` | `kiosk_*` | `kiosk_state`, `kiosk_set_guard`, `kiosk_set_fullscreen`, `kiosk_show_overlay`, `kiosk_monitors`, `kiosk_move_to_monitor`, `kiosk_virtual_keyboard` (TabTip.exe), `kiosk_focus`, `kiosk_exit` (needs an unexpired `sys_unlock_admin` token; `explorer` \| `quit`), `kiosk_reload`, `kiosk_open_devtools`, `kiosk_gamepad_state`, `kiosk_idle_reset`, `kiosk_i18n_bundle`, `kiosk_asset_url`. |

`Kiosk::on_agent_event` (the "bridge", spawned by `spawn_all`) applies native side effects of Agent events:
`policy.changed` → `apply_policy` (blocked chords, taskbar, idle timeout); `game.stateChanged` → game mode
(guard suspended while a game runs, re-armed and window re-focused on exit); `shell.command{lock|unlock}` →
overlay when `kiosk.overlayOnLock`; `admin.message` / `session.warning` / `admin.remoteControl` → toast on
the overlay (`TOAST_TTL = 15 s`) when a game owns the screen. Admin mode (an unexpired admin unlock, or dev
mode) shows the tray.

### 1.8 `gamepad.rs` and `tray.rs`

`GamepadService` polls `gilrs` on its own thread every `gamepad.pollMs`; button edges, axis changes beyond
`gamepad.deadzone` and connect/disconnect become `kiosk://gamepad`. With `gamepad.navigation` the left stick
is turned into `up/down/left/right` presses with auto-repeat (`REPEAT_DELAY = 400 ms`, `REPEAT_INTERVAL =
120 ms`). The tray (`tauri.conf.json → app.trayIcon`, id `main`) is an operator tool: *Reconnect agent*,
*Show logs folder*, *Toggle devtools* (debug / `devtools` feature builds), *Exit* (asks the frontend for the
PIN dialog through `kiosk://hotkey{exit}` when no admin unlock is cached).

### 1.9 Windows and capabilities

| Label | URL | Created by | Purpose |
|-------|-----|-----------|---------|
| `main` | `index.html` | `tauri.conf.json` (fullscreen, undecorated, always-on-top, not closable, `skipTaskbar`, drag-drop off, zoom hotkeys off) | The kiosk UI |
| `overlay` | `index.html#/overlay` | `Overlay` (lazy) | Lock veil, staff toasts, ads over a running game |
| `ads`, `ads-2`, … | `index.html#/ads?monitor=N&mode=…` | `MultiMonitor` | Secondary monitors |

`capabilities/default.json` applies to `["main", "overlay", "ads*"]` and grants `core:default`, the
window permissions the guard needs (`set-fullscreen`, `set-always-on-top`, `set-focus`, `show`, `hide`,
`minimize`, `unminimize`, `set-size`, `set-position`), `core:event:default`, `core:tray:default`, and
`log:default`, `process:default`, `os:default`. No `shell`, `fs`, `http` or `dialog` plugin is exposed to
the webview; every file/network need goes through an application command.

CSP (`tauri.conf.json → app.security.csp`): `default-src 'self'`; images and media may come from `https:`,
`data:`, `blob:` and the asset protocol (`asset:`, `http://asset.localhost`); `connect-src` allows the Tauri
IPC origins and `https:`. The asset protocol scope is limited to `C:/ProgramData/ClubShell/themes/**` and
`C:/ProgramData/ClubShell/cache/media/**`; `kiosk_asset_url` enforces the same roots before returning a URL.
The `devCsp` additionally allows `http://localhost:1420` (Vite) and `http://localhost:8080` (MockServer).

---

## 2. Frontend

### 2.1 Bootstrap — `main.tsx`

`boot(getWindowRole())`, where the role (`main` | `overlay` | `ads`) is derived from the hash:

1. `installGlobalErrorHandlers()` (`lib/logger.ts`: `window.onerror` / `unhandledrejection` → `log.error`
   → `sys_log_client_error`, rate-limited to 8/s).
2. `installKioskGuards()`: blocks drag, browser zoom (keys, ctrl+wheel, pinch) and, inside Tauri, reload
   (F5, Ctrl+R), F11, browser UI chords and devtools chords unless `shell.json → devtools` is on.
3. `document.documentElement.dataset.window = role`; the overlay window gets a transparent background.
4. Render `<BootSplash/>` (no `t()` before i18n).
5. `initI18n(DEFAULT_SETTINGS.locale)` then `bootstrapStores()` (main) or `bootstrapWindow()` (overlay /
   ads: settings → i18n + theme + shell config, plus `themeChanged` / `localeChanged` sync).
6. `applyTheme(currentTheme() ?? useThemeStore.getState().theme)`; `installCursorHider()` on main
   (`kiosk.hideCursorAfterSec`).
7. Render `<ErrorBoundary><App role={role}/></ErrorBoundary>`. The boundary offers *retry* (re-mount)
   and *reload* (`api.kiosk.reload()`).

### 2.2 Router — `router.tsx`

`createHashRouter` (Tauri serves `index.html`, so paths live in the hash). Main window route table
(`mainRoutes()`):

| Path | Screen | Guard |
|------|--------|-------|
| `/lock`, `/idle`, `/overlay`, `/ads` | bare routes (`BARE_ROUTES`), no shell | none |
| `/home`, `/games`, `/games/:id`, `/wallet`, `/support` | under `AppShell` | `RequireSession` |
| `/apps`, `/shop`, `/chat`, `/booking`, `/tournaments`, `/profile` | under `AppShell` | `RequireSession` + `RequireFeature(feature)` |
| `*`, index | `NotFound` → `shell.json → ui.defaultRoute` (`useDefaultRoute`, fallback `/home`) | — |

`RequireSession` renders a spinner until `auth.ready`, then redirects to `/lock` unless a user is logged in
with an open, unlocked session (`selectHasSession`, `selectIsLocked`). `RequireFeature` reads
`settings.features[feature]`. Every route carries a `RouteHandle { titleKey, feature? }` that
`DocumentTitle` writes to `document.title` (`"<screen> · ClubShell"`). Screens are `React.lazy` chunks with
`RouteFallback` as the `Suspense` fallback. Overlay and ads windows get a single-route table
(`OverlayScreen` / `AdsScreen`).

### 2.3 Stores and event wiring — `store/index.ts`

Zustand stores per domain: `auth`, `session`, `games`, `wallet`, `shop`, `chat`, `notifications`,
`settings`, `theme`. `bootstrapStores()` is the one-time boot (idempotent, retried on failure):

```
settings.load()                              settings_get + settings_get_shell_config + kiosk_state
initI18n(locale) ‖ theme.load(theme) ‖ settings.loadSystem()
wireListeners()                              every events.on / events.onKiosk + cross-store subscriptions
startSessionTicker()                         1 s local countdown, session_time_left resync
auth.refresh()                               auth_status (+ session_get)
games.load() · shop.load() · theme.loadList()   fire-and-forget
loadUserData() when a user is present        wallet, running games, chat, orders (allSettled)
```

`wireListeners()` registers each listener exactly once and keeps the unsubscribe functions;
`teardownStores()` (HMR dispose / tests) removes them; `resetStores()` clears the user-scoped slices on
logout / `auth.expired` while settings and theme (device-level) stay. The table below is the complete
mapping of Agent events to store reactions:

| Event | Reaction |
|-------|----------|
| `session.updated` / `session.warning` / `session.ended` | `session.setSession` / `onWarning` (+ toast) / `onEnded` (+ games reset, toast with `session.endedReason.<reason>`) |
| `wallet.updated` | `wallet.onUpdated`, `auth.setBalance` |
| `shop.orderUpdated` | `shop.onOrderUpdated`, toast on status change with a `/shop` action |
| `chat.message` | `chat.onMessage`, toast unless on `/chat` or own message |
| `notification.push` / `admin.message` / `admin.remoteControl` | notifications store (`pushNotification`, `pushAdmin`, `setRemoteControl`) |
| `game.stateChanged` | `games.onStateChanged`, toast for `failed` / `exited` / `killed` |
| `sys.metrics` / `sys.connectivity` / `policy.changed` | `settings.setMetrics` / `notify.setServerConnectivity` (+ toast on change) / `settings.setPolicy` |
| `update.available` / `update.progress` / `update.ready` | toast / `notify.setUpdateProgress` / `notify.setUpdateReady` |
| `auth.expired` | `auth.onExpired(reason)` → `resetStores()` via the `auth.user` subscription |
| `shell.command` | `lock` / `unlock` → `session.applyState`; `reboot` → scheduled-power banner; `showMessage` → toast; `showAds` handled in `App.tsx` |
| `kiosk://connectivity` | `notify.setAgentConnectivity`, toast on change, `auth.refresh()` on reconnect |
| `kiosk://themeChanged` / `localeChanged` / `monitorChanged` | `theme.apply` / `settings.applyLocale` / `settings.refreshKiosk` |
| `kiosk://hotkey` | `lock`, `callAdmin`, `volumeUp/Down`, `mute`, `blocked` |

Window-level behaviour that needs the router (`App.tsx → GlobalListeners`): session state `locked` /
`ended` / `idle` or a lost user → `navigate('/lock')`; `kiosk://idle` with nobody logged in → `/idle`;
`shell.command{showAds}` while no game runs → `AdsOverlay`; the exit hotkey (`kiosk.exitHotkey`, default
`Ctrl+Alt+Shift+F12`) and `kiosk://hotkey{exit}` → `AdminPanel`; `trackScreen(pathname)` analytics.

### 2.4 `lib/tauri.ts` — invoke, listen, mock mode

* `isTauri()` = `window.__TAURI_INTERNALS__` present **and** `VITE_MOCK !== '1'`.
* `invoke<T>(cmd, args?, opts?)`: inside Tauri → `@tauri-apps/api/core.invoke` wrapped in `withTimeout`
  (20 s default, 120 s for `games_launch` / `apps_launch`, `AbortSignal` support); otherwise the handler
  from `@/mocks/handlers.registry`. Every rejection is normalised to `ShellApiError { code, message,
  details, source }` by `toShellApiError`.
* `listen<T>(event, handler)`: `@tauri-apps/api/event.listen` or the mock bus (`subscribeMock`); returns a
  synchronous unsubscribe that is honoured even before the native listener attached.
* `api` — one typed method per command (`api.auth.login`, `api.session.start`, …, `api.kiosk.assetUrl`);
  `events.on(name, h)` for `agent://<name>` (payload types from `@clubshell/contracts` `AgentEventMap`) and
  `events.onKiosk(name, h)` for `kiosk://<name>` (raw Rust payloads normalised through `normalizeKiosk`
  into `KioskEventMap`, e.g. `connectivity.agent === 'connected'` → `{ connected: true }`).
* `assetUrl(path)` resolves a ProgramData-relative `themes\…` / `cache\media\…` path through
  `kiosk_asset_url`; absolute `https:`/`data:`/`blob:`/`asset:` URLs pass through.
* Hooks: `useAgentEvent` / `useKioskEvent` (`hooks/useTauriEvent.ts`) keep the handler in a ref so the
  native listener attaches once per event name.

Mock mode is two independent switches: `VITE_MOCK=1` makes the **frontend** use `src/mocks` in any browser
(no Tauri needed — Vite dev, Playwright); `CLUBSHELL_DEV=1` makes the **Rust host** use `MockTransport`
(no Agent needed) and disables hardening. Under `tauri dev` a debug build also falls back to the mock
transport when the pipe is unreachable.

### 2.5 i18n — `i18n/index.ts`

i18next with `initReactI18next`; bundled resources `en.json` / `ru.json` / `uz.json` (namespace
`translation`, `keySeparator: '.'`, `fallbackLng: 'en'`, `useSuspense: false`). Inside Tauri
`loadOverlay(locale)` merges the flat bundle returned by `kiosk_i18n_bundle` (embedded copy of the same
JSON plus `<data dir>\locales\<locale>.json` overrides, flattened to `a.b.c` keys) via `i18n.addResources`.
`changeLocale` is called by `settings.applyLocale` (after `settings_set{locale}` / `sys_set_locale` /
`kiosk://localeChanged`); it also sets `<html lang>` and `data-locale`. `LOCALE_NAMES` and `LOCALE_TAGS`
(`uz-UZ` etc.) drive the switcher and `Intl` formatting (`lib/format.ts`).

### 2.6 Theme application

`theme/themes.ts → applyTheme(theme)` writes `--c-<name>: R G B` for the eight palette keys plus
`--c-primary-hover` / `--c-primary-active` (`shade()`), `--radius`, `--font`, `--blur`, and
`data-theme` / `data-animations` on `<html>`. `tailwind.config.ts` maps `bg`, `surface`, `primary`,
`accent`, `text`, `muted`, `danger`, `success` to `rgb(var(--c-<name>) / <alpha-value>)`. The `theme`
store (`store/theme.ts`) loads by name (`settings_get_theme` → bundled fallback), applies, and persists
through `settings_set{theme}`. Full details in `THEMING.md`.

---

## 3. Event flow

```
Agent (session 0)                    Shell — Rust                                   Shell — WebView2
─────────────────                    ────────────────────────────────────────────    ───────────────────────────────
PipeServer writes                    PipeClient reader task (crates/winutil)
IpcEnvelope{kind:"event",  ──pipe──▶   frame → IpcEnvelope → broadcast(EVENT_BUFFER=256)
 name:"session.updated",              PipeTransport re-fans into one long-lived channel
 payload:Session}                     EventForwarder task (agent/events.rs):
                                        • unknown names dropped (names::events::ALL)
                                        • side effects: session/user caches, connectivity watch,
                                          game_running → KioskControl::set_game_mode,
                                          shell.command{lock|unlock} → KioskControl::set_locked,
                                          sys.metrics throttled to 1/s
                                        • app.emit("agent://session.updated", payload)  ──emit──▶  listen("agent://session.updated")
                                      Kiosk bridge (kiosk/mod.rs on_agent_event):                    (lib/tauri.ts → events.on)
                                        native toasts / overlay / policy apply                        store/index.ts wireListeners:
                                                                                                        session().setSession(s)
                                                                                                      ↓ zustand subscription
                                                                                                      React components re-render
                                                                                                      (useSessionStore selectors)
Pipe state change ──────────────────▶ Supervisor publishes ConnectivityStatus
                                      EventForwarder: app.emit("kiosk://connectivity", status) ─────▶ events.onKiosk('connectivity')
```

Requests go the other way: component → `api.<group>.<cmd>()` → `invoke` → `#[tauri::command]` handler →
`AgentClient::request` → `IpcEnvelope::request(name, payload)` → pipe → Agent → response envelope resolved
through the pending `oneshot` → `CmdResult` → promise. Errors keep their `source` so the UI can distinguish
"the Agent said no" (`ipc`) from "the Agent is gone" (`pipe`).

---

## 4. Development workflow

Prerequisites: Node 20+/pnpm (the repo is a pnpm workspace), Rust (see `rust-toolchain.toml`), the Tauri
2 CLI (`@tauri-apps/cli` is a devDependency of `@clubshell/shell`), WebView2 runtime (ships with Windows
11; the bundle embeds the bootstrapper).

| Goal | Command | What runs |
|------|---------|-----------|
| UI only, no Rust, no Agent | `VITE_MOCK=1 pnpm --filter @clubshell/shell dev` → http://localhost:1420 | Vite dev server; `src/mocks` answers every command and emits mock events |
| UI + Rust host, no Agent | `CLUBSHELL_DEV=1 pnpm --filter @clubshell/shell tauri dev` | Tauri dev build with `MockTransport`, hardening off, devtools available, tray visible |
| UI + Rust host + real Agent | `pnpm --filter @clubshell/shell tauri dev` (debug build) | Probes `\\.\pipe\clubshell-agent`; falls back to the mock when unreachable. Run the Agent with `--console` for a local pipe. |
| Agent against a fake server | `pnpm --filter @clubshell/mock-server dev` (http://localhost:8080) + Agent `--dev` | MockServer: REST + WS, update manifests, policies |
| Type-check / build the UI | `pnpm --filter @clubshell/shell build` | `tsc --noEmit` + `vite build` → `apps/shell/dist` |
| Release bundle | `pnpm --filter @clubshell/shell tauri build` | `beforeBuildCommand` builds the UI, then MSI + NSIS under the workspace `target/x86_64-pc-windows-msvc/release/bundle/` (`.cargo/config.toml` pins `build.target`) |
| Playwright e2e | `pnpm --filter @clubshell/shell-e2e test` | Starts Vite with `VITE_MOCK=1` (see `tests/shell-e2e/playwright.config.ts`) |
| Rust tests | `cargo test -p clubshell-shell` and `cargo test -p clubshell-shell-tests` | Unit tests inside the crate + `tests/shell-rs` |

`tauri.conf.json → build`: `beforeDevCommand = pnpm --filter @clubshell/shell dev`, `devUrl =
http://localhost:1420`, `frontendDist = ../dist`. `vite.config.ts` aliases `@clubshell/contracts` to the
package source so no contracts build step is needed for dev; `build.target = chrome110` matches the
evergreen WebView2 runtime.

Useful environment while developing: `CLUBSHELL_DATA_DIR=<tmp>` to keep a throw-away
`shell.json`/`themes\`/`logs\`; `CLUBSHELL_LOCALE=uz` to start in a locale; `RUST_LOG=debug,clubshell_shell_lib=trace`.

---

## 5. Debugging

| Where | What |
|-------|------|
| `C:\ProgramData\ClubShell\logs\shell.YYYY-MM-DD.log` | Rust `tracing` JSON lines (14 files kept). Webview `log()` calls and `console.warn/error` forwarded by `lib/logger.ts` land here under target `webview`. |
| `C:\ProgramData\ClubShell\logs\shell-crash.log` | Panics, written synchronously by the panic hook. |
| `C:\ProgramData\ClubShell\logs\agent-YYYYMMDD.json` | Agent side of every IPC request (`sys.logClientError` entries included). |
| stderr | Human-readable log in dev / debug builds. |
| Tray → *Show logs folder* | Opens the directory (admin unlock or dev). |
| `kiosk_state` | Diagnostics snapshot: `agentConnected`, `fullscreen`, `guardActive`, `hooksActive`, `monitors`, `idle`, `gamepadConnected`, `locked`, `gameMode`, `overlay`, `dev`, `version`, `devtools`. |

Devtools policy: they are compiled in for debug builds and for release builds made with the `devtools`
cargo feature; `kiosk_open_devtools` additionally requires `shell.json → devtools: true` (`forbidden`
otherwise, `internal` when not compiled in). The frontend swallows F12 / Ctrl+Shift+I chords unless that
flag is on. Production `shell.json` ships with `devtools: false`. Never enable devtools on a club PC that
players can reach: the kiosk lockdown is only as strong as the WebView.

Common symptoms:

* UI shows "Agent offline" and `kiosk://connectivity.attempts` keeps growing → the service is not running,
  the pipe name differs (`ipc.pipeName` vs `agent.json → ipc.pipeName`), or the pipe DACL does not include
  the kiosk user SID.
* `auth.hello` rejected → `secure\shell.token` unreadable by the kiosk user or stale (regenerated at Agent
  start; the Shell re-reads it on every hello).
* Commands time out at 20 s while events flow → the Agent handler is blocked; check the Agent log for the
  `id` of the envelope (it is the frontend `traceId`).
* Blank window in release → check the CSP (`img-src` / `media-src` for a new asset origin) and `shell-crash.log`.

---

## 6. Performance notes

* **Target**: 1920×1080 at 60 fps on integrated graphics with a game installing in the background. The
  typography and layout tokens (`tokens.css`) are fluid between 1600×900 and 2560×1440 (`--fs-base:
  clamp(16px, 0.9375vw, 24px)`), so one layout serves 1080p and 1440p without media queries.
* **WebView2**: one process per window (`main`, `overlay`, `ads-*`); the overlay is created lazily and the
  ads windows only when a secondary monitor exists. Keep the overlay route cheap — it is drawn over a
  running game. `backdrop-filter` (`.glass`, `--blur`) is the most expensive effect; themes can set
  `blur: 0` for weak GPUs, and `animations: false` disables every animation via `data-animations`.
* **Code-splitting**: every screen is a `React.lazy` chunk; the boot path loads only `main.tsx`, the
  stores, i18n and the current screen. `framer-motion` is used only in `AdsOverlay` and a few transitions
  and honours `theme.animations`.
* **Events**: `sys.metrics` is throttled to one per second in Rust before it reaches the webview;
  `update.progress` is capped at 2/s by the Agent. Store subscriptions use selectors so a metrics tick does
  not re-render the games grid.
* **Images**: covers are served from `cache\media\` (Agent `LocalCache`) through the asset protocol (no base64, no fetch
  through JS); `GameArtwork` resolves paths once per `src` (`useResolvedAsset`).
* **Rust**: the keyboard hook callback never blocks (channel hand-off), the topmost guard runs on a 250 ms
  timer, the gamepad thread sleeps `pollMs` (16 ms) and 250 ms while disabled.
* **Build**: `vite build` targets `chrome110` and minifies with esbuild; `TAURI_ENV_DEBUG` keeps sourcemaps
  and disables minification for `tauri dev`.

---

## 7. Checklists

### 7.1 Adding a screen

1. Create `src/screens/<Name>/<Name>Screen.tsx` with a **default export** (needed by `React.lazy`).
2. Add the lazy import and a route in `router.tsx → mainRoutes()` with a `RouteHandle { titleKey,
   feature? }`; wrap with `gated('<feature>', …)` when it is behind a `shell.json → features` flag.
3. Add the title key (and any strings) to `i18n/en.json`, `ru.json`, `uz.json`.
4. Add a navigation entry to `NAV_ITEMS` in `screens/Desktop/NavBar.tsx` (respect the same feature flag).
5. State: extend an existing store or add `store/<domain>.ts`, export it from `store/index.ts`, and if it
   reacts to Agent events, add the listener to `wireListeners()` (never inside a component).
6. Gamepad/keyboard navigation: mark focusable elements with `data-nav="true"` and the `focus-ring` class.
7. Test in `VITE_MOCK=1` (extend `src/mocks/data.ts` / `handlers.ts` if the screen needs new data), then
   under `tauri dev`; add a Playwright spec in `tests/shell-e2e`.

### 7.2 Adding a command

1. Contract first: add the request/response records to `src/ClubShell.Contracts`, regenerate
   `crates/protocol` and `packages/contracts-ts` (`tools/ContractsGen`), document the IPC message in
   `IPC_PROTOCOL.md` and the command in `TAURI_COMMANDS.md` §2.
2. Agent: implement the IPC handler.
3. Rust: add `pub async fn <group>_<name>(state: State<'_, AppState>, …) -> CmdResult<T>` in
   `src-tauri/src/commands/<group>.rs` (validate → `state.agent.request(names::<group>::<NAME>, &req)`),
   append it to `COMMAND_NAMES` and to the `invoke_handler!` macro in `commands/mod.rs`. Local commands
   go to `kiosk/commands.rs` and are passed to `invoke_handler!` in `lib.rs`.
4. Frontend: add the method to `api` in `lib/tauri.ts` (typed with the contracts package), a mock handler
   in `src/mocks/handlers.ts`, and use it from a store action — components call stores, not `api`.
5. If the command emits a local event, define the `kiosk://…` constant next to the command, add the raw
   and normalised payload to `RawKioskPayloads` / `KioskEventMap` in `lib/tauri.ts`, and document it in
   `TAURI_COMMANDS.md` §3.2.
6. `cargo clippy -D warnings`, `cargo test`, `pnpm --filter @clubshell/shell build`.
