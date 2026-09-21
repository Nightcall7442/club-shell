# ClubShell — Tauri Commands & Events (src-tauri ⇄ React)

Status: normative. Every `#[tauri::command]` in `apps/shell/src-tauri/src/commands/*.rs`, every event
emitted to the webview, and the frontend invoke wrapper (`apps/shell/src/lib/tauri.ts`).

Types referenced by name come from `packages/contracts-ts` (generated from `ClubShell.Contracts`, see
`IPC_PROTOCOL.md` §6). Rust command handlers use `crates/protocol` for the same types.

---

## 1. Conventions

| Topic | Rule |
|-------|------|
| Command names | snake_case, `<group>_<action>`; invoked as `invoke("session_start", { ... })` |
| Args | One JSON object; Tauri converts camelCase JS keys → snake_case Rust parameters (`rename_all = "camelCase"` on the command). Arg objects that are DTOs keep camelCase inside. |
| Return | `Result<T, ShellError>`; `T` serialized with serde camelCase |
| Errors | `ShellError` (§1.1). Tauri rejects the promise with this object. |
| Proxy commands | Handler builds the IPC request (`IPC_PROTOCOL.md` §7), awaits the response via `PipeClient::request`, maps `IpcError` → `ShellError { source: "ipc" }`. No business logic in Rust. |
| Timeout | Rust side: `shell.json → ipc.requestTimeoutMs` (default 15 s) per IPC request; frontend `invoke` adds its own 20 s guard, 120 s for `games_launch` / `apps_launch` |
| Concurrency | Commands are `async`, run on tokio; unlimited concurrency except `games_launch` / `apps_launch` (serialized by a `Mutex`) |
| Security | `src-tauri/capabilities/default.json` (windows `main`, `overlay`, `ads*`) grants `core:default`, the window/event/tray permissions and the `log`/`process`/`os` plugins only; no `shell`, `fs`, `http` or `dialog` plugin reaches the webview. Application commands are allowed by default in Tauri 2, so §2 is the complete surface. CSP (`tauri.conf.json → app.security.csp`): `default-src 'self'`, `img-src`/`media-src` also `data:`, `blob:`, `https:`, `asset:`, `http://asset.localhost`, `connect-src 'self' ipc: http://ipc.localhost tauri: https:`, `object-src 'none'`, `frame-ancestors 'none'`; `devCsp` adds `http://localhost:1420` and `http://localhost:8080` |

### 1.1 `ShellError`

```ts
export interface ShellError {
  code: ErrorCode;                // IPC_PROTOCOL.md §5
  message: string;
  details: Record<string, unknown> | null;
  source: "ipc" | "pipe" | "tauri" | "mock";
  //  ipc   – Agent responded with error
  //  pipe  – transport failure (disconnected / timeout / protocol) → code ∈ agentOffline | timeout | protocolError
  //  tauri – Rust-side validation / OS failure → code ∈ validation | internal | forbidden
  //  mock  – produced by the browser mock (dev)
}
```

Rust:

```rust
// apps/shell/src-tauri/src/state.rs
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ShellError { pub code: ErrorCode, pub message: String, pub details: Option<serde_json::Value>, pub source: ErrorSource }
pub type CmdResult<T> = Result<T, ShellError>;   // Display + std::error::Error implemented by hand; From<IpcError>, From<WinUtilError>, ...
```

---

## 2. Commands

Column "IPC" = proxied message. "—" = handled locally in Rust. The "TS signature" column is the typed method on
the `api` object exported by `apps/shell/src/lib/tauri.ts` (`api.<group>.<method>`, thin arrows over `invoke<T>`;
request/response types come from `@clubshell/contracts`, kiosk-only types from `lib/tauri.ts`). Every method
returns `Promise<T>` and rejects with `ShellApiError` (§4).

### 2.1 auth

| Command | TS signature | Args | Returns | IPC |
|---------|--------------|------|---------|-----|
| `auth_login` | `api.auth.login(req: AuthLoginRequest): Promise<AuthLoginResponse>` | `{ req: { kind: AuthKind; username?: string; password?: string; qrToken?: string; cardId?: string; token?: string } }` | `{ user: User; session: Session \| null; expiresAt: string; mode: ConnectivityState }` | `auth.login` |
| `auth_logout` | `api.auth.logout(reason?: SessionEndReason): Promise<AuthLogoutResponse>` | `{ reason?: string }` | `{ ok: true; sessionEnded: boolean; session?: Session }` | `auth.logout` |
| `auth_status` | `api.auth.status(): Promise<AuthStatusResponse>` | `{}` | `{ authenticated: boolean; user?: User; session?: Session; mode: ConnectivityState; expiresAt?: string }` | `auth.status` |
| `auth_qr_start` | `api.auth.qrStart(): Promise<QrLoginStart>` | `{}` | `{ qrToken: string; qrUrl: string; expiresAt: string; pollIntervalSec: number }` | `auth.qrStart` |

Errors: as per the IPC message. `auth_login` with `kind = "password"` and empty `password` → `validation`
raised in Rust before the pipe is touched (`source: "tauri"`).

### 2.2 session

| Command | TS signature | Args | Returns | IPC |
|---------|--------------|------|---------|-----|
| `session_get` | `api.session.get(): Promise<Session \| null>` | `{}` | `Session \| null` | `session.get` |
| `session_start` | `api.session.start(req: SessionStartRequest): Promise<Session>` | `{ req: { tariffId: string; minutes?: number; prepaid: boolean } }` | `Session` | `session.start` |
| `session_pause` | `api.session.pause(reason?: string): Promise<Session>` | `{ reason?: string }` | `Session` | `session.pause` |
| `session_resume` | `api.session.resume(): Promise<Session>` | `{}` | `Session` | `session.resume` |
| `session_end` | `api.session.end(reason?: SessionEndReason): Promise<SessionEndResult>` | `{ reason?: SessionEndReason }` | `{ session: Session; charged: Money; refunded: Money }` | `session.end` |
| `session_extend` | `api.session.extend(minutes: number, tariffId?: string): Promise<Session>` | `{ minutes: number; tariffId?: string }` | `Session` | `session.extend` |
| `session_lock` | `api.session.lock(reason?: string): Promise<Session>` | `{ reason?: string }` | `Session` | `session.lock` |
| `session_unlock` | `api.session.unlock(secret: { password?: string; pin?: string }): Promise<Session>` | `{ password?: string; pin?: string }` | `Session` | `session.unlock` |
| `session_time_left` | `api.session.timeLeft(): Promise<SessionTimeLeftResponse>` | `{}` | `{ sessionId?: string; state: SessionState; secondsLeft: number; secondsUsed: number; endsAt?: string; serverTime: string }` | `session.timeLeft` |

### 2.3 games

| Command | TS signature | Args | Returns | IPC |
|---------|--------------|------|---------|-----|
| `games_list` | `api.games.list(q?: GamesListRequest): Promise<GamesListResponse>` | `{ q?: { category?: string; search?: string; installedOnly?: boolean; launcher?: LauncherType; sort?: "popularity"\|"title"\|"lastPlayed"; page?: number; pageSize?: number } }` | `{ items: Game[]; total: number; page: number; pageSize: number; catalogVersion: string }` | `games.list` |
| `games_get` | `api.games.get(gameId: string): Promise<Game>` | `{ gameId: string }` | `Game` | `games.get` |
| `games_launch` | `api.games.launch(req: GamesLaunchRequest): Promise<LaunchResult>` | `{ req: { gameId: string; useAccountPool?: boolean; extraArgs?: string; resolution?: { width: number; height: number } } }` | `LaunchResult` | `games.launch` |
| `games_kill` | `api.games.kill(target?: GamesKillRequest): Promise<GamesKillResponse>` | `{ gameId?: string; pid?: number; force?: boolean }` | `{ killed: number; pids: number[] }` | `games.kill` |
| `games_running` | `api.games.running(): Promise<RunningGame[]>` | `{}` | `RunningGame[]` (unwrapped `items`) | `games.running` |
| `games_install_status` | `api.games.installStatus(gameId: string): Promise<GameInstallStatus>` | `{ gameId: string }` | `{ gameId: string; installed: boolean; installPath?: string; sizeGb: number; version?: string; verifiedAt?: string; launcherReady: boolean }` | `games.installStatus` |

`games_launch` side effects in Rust (`commands/games.rs`): the call is serialised with `apps_launch` through
`AppState::launch_lock`, runs with a 120 s timeout and enters game mode (`KioskControl::set_game_mode(true)`:
foreground guard and sweep suspended) so the game can take the foreground; on a failed launch, and on
`game.stateChanged{exited|failed|killed}`, game mode is left, the guard is re-armed and the Shell window is
re-focused.

### 2.4 apps

| Command | TS signature | Args | Returns | IPC |
|---------|--------------|------|---------|-----|
| `apps_list` | `api.apps.list(): Promise<App[]>` | `{}` | `App[]` | `apps.list` |
| `apps_launch` | `api.apps.launch(appId: string, args?: string): Promise<AppsLaunchResponse>` | `{ appId: string; args?: string }` | `{ ok: true; pid: number; startedAt: string }` | `apps.launch` |

### 2.5 wallet

| Command | TS signature | Args | Returns | IPC |
|---------|--------------|------|---------|-----|
| `wallet_balance` | `api.wallet.balance(): Promise<Balance>` | `{}` | `Balance` | `wallet.balance` |
| `wallet_tariffs` | `api.wallet.tariffs(zone?: string): Promise<WalletTariffsResponse>` | `{ zone?: string }` | `{ items: Tariff[]; zone: string; serverTime: string }` | `wallet.tariffs` |
| `wallet_history` | `api.wallet.history(q?: WalletHistoryRequest): Promise<WalletHistoryResponse>` | `{ q?: { page?: number; pageSize?: number; from?: string; to?: string; type?: TransactionType } }` | `{ items: Transaction[]; total: number; page: number; pageSize: number }` | `wallet.history` |
| `wallet_topup_intent` | `api.wallet.topupIntent(amount: Money, provider: TopupProvider): Promise<TopupIntent>` | `{ amount: Money; provider: TopupProvider }` | `TopupIntent` | `wallet.topupIntent` |

### 2.6 shop

| Command | TS signature | Args | Returns | IPC |
|---------|--------------|------|---------|-----|
| `shop_products` | `api.shop.products(q?: ShopProductsRequest): Promise<Product[]>` | `{ q?: {...} }` | `Product[]` | `shop.products` |
| `shop_order` | `api.shop.order(req: ShopOrderRequest): Promise<Order>` | `{ req: { items: { productId: string; qty: number }[]; note?: string; idempotencyKey: string } }` | `Order` | `shop.order` |
| `shop_order_status` | `api.shop.orderStatus(orderId: string): Promise<Order>` | `{ orderId: string }` | `Order` | `shop.orderStatus` |
| `shop_orders` | `api.shop.orders(q?: ShopOrdersRequest): Promise<ShopOrdersResponse>` | `{ q?: {...} }` | `{ items: Order[]; total: number }` | `shop.orders` |

`idempotencyKey` is generated by the frontend (`uuid()` in `lib/tauri.ts`, `crypto.randomUUID()` with a
`getRandomValues` fallback) once per cart submission (`store/shop.ts`) and reused on retry; `api.chat.send`
generates one per call.

### 2.7 chat

| Command | TS signature | Args | Returns | IPC |
|---------|--------------|------|---------|-----|
| `chat_history` | `api.chat.history(q?: ChatHistoryRequest): Promise<ChatHistoryResponse>` | `{ q?: {...} }` | `{ roomId: string; items: ChatMessage[]; hasMore: boolean; unread: number }` | `chat.history` |
| `chat_send` | `api.chat.send(text: string, roomId?: string): Promise<ChatMessage>` | `{ text: string; roomId?: string; idempotencyKey: string }` | `ChatMessage` | `chat.send` |
| `chat_mark_read` | `api.chat.markRead(upToMessageId: string, roomId?: string): Promise<ChatMarkReadResponse>` | `{ upToMessageId: string; roomId?: string }` | `{ roomId: string; unread: number }` | `chat.markRead` |

### 2.8 booking

| Command | TS signature | Args | Returns | IPC |
|---------|--------------|------|---------|-----|
| `booking_seats` | `api.booking.seats(date: string): Promise<BookingSeatsResponse>` | `{ date: string }` | `{ date: string; seats: Seat[]; bookings: Booking[]; slotMinutes: number }` | `booking.seats` |
| `booking_reserve` | `api.booking.reserve(pcId: string, from: string, to: string): Promise<Booking>` | `{ pcId: string; from: string; to: string }` | `Booking` | `booking.reserve` |
| `booking_cancel` | `api.booking.cancel(bookingId: string): Promise<Booking>` | `{ bookingId: string }` | `Booking` | `booking.cancel` |

### 2.9 tournaments

| Command | TS signature | Args | Returns | IPC |
|---------|--------------|------|---------|-----|
| `tournaments_list` | `api.tournaments.list(q?: TournamentsListRequest): Promise<Tournament[]>` | `{ q?: {...} }` | `Tournament[]` | `tournaments.list` |
| `tournaments_join` | `api.tournaments.join(tournamentId: string): Promise<Tournament>` | `{ tournamentId: string }` | `Tournament` | `tournaments.join` |
| `tournaments_leaderboard` | `api.tournaments.leaderboard(tournamentId: string, limit?: number): Promise<TournamentsLeaderboardResponse>` | `{ tournamentId: string; limit?: number }` | `{ tournamentId: string; entries: LeaderboardEntry[]; me?: LeaderboardEntry; updatedAt: string }` | `tournaments.leaderboard` |

### 2.10 profile

| Command | TS signature | Args | Returns | IPC |
|---------|--------------|------|---------|-----|
| `profile_get` | `api.profile.get(): Promise<User>` | `{}` | `User` | `profile.get` |
| `profile_update` | `api.profile.update(patch: ProfileUpdateRequest): Promise<User>` | `{ patch: { displayName?: string; avatarUrl?: string; locale?: Locale; pin?: string } }` | `User` | `profile.update` |
| `profile_stats` | `api.profile.stats(): Promise<UserStats>` | `{}` | `UserStats` | `profile.stats` |
| `profile_achievements` | `api.profile.achievements(): Promise<Achievement[]>` | `{}` | `Achievement[]` | `profile.achievements` |
| `profile_loyalty` | `api.profile.loyalty(): Promise<Loyalty>` | `{}` | `Loyalty` | `profile.loyalty` |

### 2.11 settings

| Command | TS signature | Args | Returns | IPC |
|---------|--------------|------|---------|-----|
| `settings_get` | `api.settings.get(): Promise<ShellSettings>` | `{}` | `ShellSettings` | `settings.get` |
| `settings_set` | `api.settings.set(patch: SettingsSetRequest): Promise<ShellSettings>` | `{ patch: { locale?; theme?; volume?; muted?; idleTimeoutSec?; showMetricsOverlay?; allowVirtualKeyboard?; uiSounds? } }` | `ShellSettings` | `settings.set` |
| `settings_get_theme` | `api.settings.getTheme(name?: string): Promise<Theme>` | `{ name?: string }` | `Theme` (`ARCHITECTURE.md` §12.4) | — (reads `themes\<name>.json`; falls back to embedded default) |
| `settings_list_themes` | `api.settings.listThemes(): Promise<string[]>` | `{}` | `string[]` | — |
| `settings_get_shell_config` | `api.settings.getShellConfig(): Promise<ShellConfig>` | `{}` | `shell.json` object | — |

`settings_set` with `theme` or `locale` also triggers `kiosk://themeChanged` / `kiosk://localeChanged` (§3.2)
after the Agent confirms, so all windows (overlay, secondary monitors) re-render.

### 2.12 policy

| Command | TS signature | Args | Returns | IPC |
|---------|--------------|------|---------|-----|
| `policy_get` | `api.policy.get(): Promise<Policy>` | `{}` | `Policy` | `policy.get` |
| `policy_reload` | `api.policy.reload(force?: boolean): Promise<PolicyReloadResponse>` | `{ force?: boolean }` | `{ policy: Policy; source: "server"\|"cache"\|"file"; applied: boolean; changed: string[] }` | `policy.reload` |

### 2.13 system

| Command | TS signature | Args | Returns | IPC |
|---------|--------------|------|---------|-----|
| `sys_pc_info` | `api.system.pcInfo(): Promise<PcInfo>` | `{}` | `PcInfo` | `sys.pcInfo` |
| `sys_hardware` | `api.system.hardware(refresh?: boolean): Promise<HardwareInfo>` | `{ refresh?: boolean }` | `HardwareInfo` | `sys.hardware` |
| `sys_metrics` | `api.system.metrics(): Promise<PcMetrics>` | `{}` | `PcMetrics` | `sys.metrics` |
| `sys_call_admin` | `api.system.callAdmin(category: CallAdminCategory, message?: string): Promise<SysCallAdminResponse>` | `{ category: CallAdminCategory; message?: string }` | `{ ticketId: string; createdAt: string; queuePosition?: number }` | `sys.callAdmin` |
| `sys_reboot` | `api.system.reboot(delaySec?: number, reason?: string): Promise<ScheduledResult>` | `{ delaySec?: number; reason?: string }` | `{ scheduledAt: string }` | `sys.reboot` |
| `sys_shutdown` | `api.system.shutdown(delaySec?: number, reason?: string): Promise<ScheduledResult>` | same | same | `sys.shutdown` |
| `sys_lock_screen` | `api.system.lockScreen(reason?: string): Promise<OkResponse>` | `{ reason?: string }` | `{ ok: true }` | `sys.lockScreen` |
| `sys_set_volume` | `api.system.setVolume(level: number, muted?: boolean): Promise<VolumeState>` | `{ level: number; muted?: boolean }` | `{ level: number; muted: boolean }` | `sys.setVolume` |
| `sys_set_locale` | `api.system.setLocale(locale: Locale): Promise<SysSetLocaleResponse>` | `{ locale: Locale }` | `{ locale: Locale }` | `sys.setLocale` |
| `sys_unlock_admin` | `api.system.unlockAdmin(pin: string): Promise<SysUnlockAdminResponse>` | `{ pin: string }` | `{ ok: true; adminToken: string; expiresAt: string }` | `sys.unlockAdmin` |
| `sys_ack_admin_message` | `api.system.ackAdminMessage(id: string): Promise<OkResponse>` | `{ id: string }` | `{ ok: true }` | `sys.ackAdminMessage` |
| `sys_log_client_error` | `api.system.logClientError(e: SysLogClientErrorRequest): Promise<void>` | `{ e: {...} }` | `null` | `sys.logClientError` (fire-and-forget; also written to `logs\shell.YYYY-MM-DD.log`) |
| `update_check` | `api.system.updateCheck(): Promise<UpdateCheckResponse>` | `{}` | `{ agent?: UpdateManifest; shell?: UpdateManifest; current: { agent: string; shell: string } }` | `update.check` |
| `update_apply` | `api.system.updateApply(component: UpdateComponent): Promise<UpdateApplyResponse>` | `{ component: UpdateComponent }` | `{ scheduled: boolean; at?: string }` | `update.apply` |

### 2.14 kiosk (local, no IPC)

| Command | TS signature | Args | Returns | Notes |
|---------|--------------|------|---------|-------|
| `kiosk_state` | `api.kiosk.state(): Promise<KioskState>` | `{}` | `{ agentConnected: boolean; fullscreen: boolean; guardActive: boolean; hooksActive: boolean; monitors: MonitorInfo[]; idle: boolean; idleSec: number; gamepadConnected: boolean; version: string; devtools: boolean }` | |
| `kiosk_set_guard` | `api.kiosk.setGuard(active: boolean): Promise<void>` | `{ active: boolean }` | `null` | Enables/disables topmost + foreground guard. `false` allowed only while a game is running or with a valid `adminToken` — otherwise `forbidden`. |
| `kiosk_set_fullscreen` | `api.kiosk.setFullscreen(on: boolean): Promise<void>` | `{ on: boolean }` | `null` | `forbidden` unless admin token present |
| `kiosk_show_overlay` | `api.kiosk.showOverlay(kind: OverlayKind, payload?: Record<string, unknown>): Promise<void>` | `{ kind; payload? }` | `null` | Native always-on-top overlay window (covers all monitors) used for lock/ads when the main webview must stay hidden |
| `kiosk_monitors` | `api.kiosk.monitors(): Promise<KioskMonitor[]>` | `{}` | `{ index: number; name: string; x: number; y: number; width: number; height: number; scale: number; hz: number; primary: boolean }[]` | |
| `kiosk_move_to_monitor` | `api.kiosk.moveToMonitor(index: number): Promise<void>` | `{ index: number }` | `null` | `notFound` |
| `kiosk_virtual_keyboard` | `api.kiosk.virtualKeyboard(show: boolean): Promise<void>` | `{ show: boolean }` | `null` | Launches/hides `TabTip.exe` when `allowVirtualKeyboard`; `policyDenied` otherwise |
| `kiosk_focus` | `api.kiosk.focus(): Promise<void>` | `{}` | `null` | Re-assert foreground (used after game exit) |
| `kiosk_exit` | `api.kiosk.exit(adminToken: string, action: 'explorer' \| 'quit'): Promise<void>` | `{ adminToken: string; action }` | `null` | Validates token with Agent (`sys.unlockAdmin` result cached in Rust), drops hooks, optionally starts `explorer.exe`, exits. `unauthorized` on bad token. |
| `kiosk_reload` | `api.kiosk.reload(): Promise<void>` | `{}` | `null` | Reloads the webview (dev/admin) |
| `kiosk_open_devtools` | `api.kiosk.openDevtools(): Promise<void>` | `{}` | `null` | Only when `shell.json → devtools = true`, else `forbidden` |
| `kiosk_gamepad_state` | `api.kiosk.gamepadState(): Promise<GamepadState[]>` | `{}` | `{ index: number; connected: boolean; buttons: number; leftX: number; leftY: number; rightX: number; rightY: number; lt: number; rt: number }[]` | Snapshot; continuous input via `kiosk://gamepad` |
| `kiosk_idle_reset` | `api.kiosk.idleReset(): Promise<void>` | `{}` | `null` | Marks activity (e.g. gamepad navigation) |
| `kiosk_i18n_bundle` | `api.kiosk.i18nBundle(locale: Locale): Promise<Record<string, string>>` | `{ locale: Locale }` | flat key→string | Loads `locales\<locale>.json` overrides from ProgramData merged over embedded bundle |
| `kiosk_asset_url` | `api.kiosk.assetUrl(path: string): Promise<string>` | `{ path: string }` | `string` | Converts a ProgramData-relative path (wallpaper, cached cover) to a `asset://` URL (`convertFileSrc`); `forbidden` for paths outside `themes\` / `cache\media\` |

---

## 3. Events (Rust → webview)

Subscribed with `listen<T>(name, handler)` from `@tauri-apps/api/event`. Payload is the raw object.

### 3.1 `agent://*` — mirror of IPC events (`IPC_PROTOCOL.md` §8)

| Event | Payload |
|-------|---------|
| `agent://session.updated` | `Session` |
| `agent://session.warning` | `{ sessionId: string; minutesLeft: number; secondsLeft: number; endsAt: string }` |
| `agent://session.ended` | `{ session: Session; reason: SessionEndReason; charged: Money }` |
| `agent://wallet.updated` | `Balance` |
| `agent://chat.message` | `ChatMessage` |
| `agent://notification.push` | `Notification` |
| `agent://admin.message` | `{ id: string; from: string; text: string; level: NotificationLevel; requiresAck: boolean; at: string }` |
| `agent://admin.remoteControl` | `{ state: RemoteControlState; adminName?: string; at: string; showIndicator: boolean }` |
| `agent://game.stateChanged` | `{ gameId: string; title: string; pid?: number; state: GameState; exitCode?: number; error?: IpcError; at: string }` |
| `agent://policy.changed` | `{ version: number; updatedAt: string; changed: string[]; policy: Policy }` |
| `agent://update.available` | `{ manifest: UpdateManifest; current: string }` |
| `agent://update.progress` | `{ component: UpdateComponent; version: string; phase: UpdatePhase; percent: number; bytesDone: number; bytesTotal: number; error?: IpcError }` |
| `agent://update.ready` | `{ component: UpdateComponent; version: string; restartRequired: boolean; applyAt?: string; mandatory: boolean }` |
| `agent://sys.metrics` | `PcMetrics` |
| `agent://sys.connectivity` | `{ state: ConnectivityState; since: string; queuedEvents: number; serverLatencyMs?: number }` |
| `agent://shell.command` | `{ command: ShellCommandKind; args: object \| null; commandId: string }` |
| `agent://auth.expired` | `{ reason: "tokenExpired" \| "revoked" \| "admin" }` |
| `agent://shop.orderUpdated` | `Order` |

Rust handles some `shell.command`s itself before forwarding: `lock`/`unlock` toggle the native overlay
when `kiosk.overlayOnLock`; `showAds` uses the overlay when a game is running (webview hidden), otherwise
the React `AdsScreen`.

### 3.2 `kiosk://*` — local events

| Event | Payload | When |
|-------|---------|------|
| `kiosk://idle` | `{ idle: boolean; idleSec: number; stage: "active" \| "dim" \| "idle" \| "screensaver" }` | crossing `idle.dimAfterSec`, `idle.timeoutSec`, `idle.screensaverAfterSec`, and on return to activity |
| `kiosk://gamepad` | `{ index: number; button?: GamepadButton; axis?: "leftX"\|"leftY"\|"rightX"\|"rightY"\|"lt"\|"rt"; value: number; pressed?: boolean; connected?: boolean }` | button edge / axis change beyond deadzone / connect / disconnect. `GamepadButton` = `a`,`b`,`x`,`y`,`lb`,`rb`,`back`,`start`,`ls`,`rs`,`up`,`down`,`left`,`right`,`guide` |
| `kiosk://hotkey` | `{ name: "exit" \| "callAdmin" \| "lock" \| "volumeUp" \| "volumeDown" \| "mute" \| "blocked"; combo: string }` | registered hotkeys; `blocked` fired (rate-limited 1/s) when a policy-blocked combo was suppressed |
| `kiosk://monitorChanged` | `{ monitors: MonitorInfo[]; primaryIndex: number; reason: "added" \| "removed" \| "resolution" \| "dpi" }` | `WM_DISPLAYCHANGE` / device notifications |
| `kiosk://connectivity` | `{ agent: "connected" \| "disconnected" \| "connecting"; attempts: number; since: string }` | pipe state (distinct from `agent://sys.connectivity`, which is the Agent ↔ Server link) |
| `kiosk://themeChanged` | `Theme` | after `settings_set{theme}` or theme file change |
| `kiosk://localeChanged` | `{ locale: Locale }` | after `settings_set{locale}` / `sys_set_locale` |
| `kiosk://focus` | `{ hasFocus: boolean; foregroundProcess?: string }` | Shell window gained/lost foreground (game running) |
| `kiosk://overlay` | `{ kind: "lock" \| "ads" \| "message" \| "none" }` | native overlay shown/hidden |

---

## 4. Frontend invoke wrapper — `apps/shell/src/lib/tauri.ts`

Contract (exports as implemented):

```ts
import type { ErrorCode } from "@clubshell/contracts";

export type ShellErrorSource = "ipc" | "pipe" | "tauri" | "mock";
export type ShellError = { code: ErrorCode; message: string; details: Record<string, unknown> | null; source: ShellErrorSource };

export class ShellApiError extends Error { code; message; details; source; /* name = "ShellApiError" */ }
export function isShellApiError(e: unknown): e is ShellApiError;
export function toShellApiError(e: unknown, fallbackSource?: ShellErrorSource): ShellApiError;
//   object with a known `code` → kept as-is; string / Error / anything else → { code: "internal", source: fallbackSource }

export function isTauri(): boolean;   // window.__TAURI_INTERNALS__ present && import.meta.env.VITE_MOCK !== "1"
export function uuid(): string;       // crypto.randomUUID() with a getRandomValues fallback

export interface InvokeOptions { timeoutMs?: number; signal?: AbortSignal; }
export function invoke<T>(cmd: string, args?: Record<string, unknown>, opts?: InvokeOptions): Promise<T>;
//  - inside Tauri: @tauri-apps/api/core invoke; every rejection is normalised through toShellApiError
//    timeoutMs (default 20 000; 120 000 for games_launch / apps_launch) → ShellApiError { code: "timeout", source: "pipe", details: { timeoutMs } }
//    signal aborted → ShellApiError { code: "timeout", source: "pipe", details: { aborted: true } }
//  - outside Tauri (Vite dev, Playwright): the handler registered in `@/mocks/handlers` (`registry[cmd]`);
//    a missing mock rejects with { code: "notFound", source: "mock" }

export function listen<T>(event: string, handler: (payload: T) => void): () => void;
//  - inside Tauri: @tauri-apps/api/event listen; the returned unsubscribe is synchronous and honoured even
//    before the native listener attached
//  - mock: subscribeMock on the in-memory event bus

export function emitMockEvent<T>(event: string, payload: T): void;   // dev only; no-op inside Tauri
export function assetUrl(path: string): Promise<string>;             // https:/data:/blob:/asset:/tauri:/file: pass through, else kiosk_asset_url

export const api: { auth; session; games; apps; wallet; shop; chat; booking; tournaments; profile; settings; policy; system; kiosk };  // §2
export type ShellApi = typeof api;

export interface KioskEventMap { idle; gamepad; hotkey; monitorChanged; connectivity; themeChanged; localeChanged; focus; overlay }  // normalised payloads
export const events: {
  on<K extends keyof AgentEventMap>(name: K, h: (p: AgentEventMap[K]) => void): () => void;      // "agent://<name>", contracts payload
  onKiosk<K extends keyof KioskEventMap>(name: K, h: (p: KioskEventMap[K]) => void): () => void; // "kiosk://<name>", normalised (§3.2 raw shapes)
};
export const agentEventName: (name) => `agent://${name}`;
export const kioskEventName: (name) => `kiosk://${name}`;
```

The mock side lives in `apps/shell/src/mocks/handlers.ts` (`registry`, `registerMock`, `emitMock`, `subscribeMock`,
`mockError`, `mockState`, `resetMock`, `simulate`, `MOCKED_COMMANDS`) with data in `apps/shell/src/mocks/data.ts`.

Rules:

1. Components never call `invoke` or `api` directly; stores (`apps/shell/src/store/*.ts`) call `api.<group>.<method>`
   with the exact command name and arg shape from §2 and decide how to surface errors.
2. `mocks/handlers.ts` registers a mock for **every** command in §2 so the UI is fully exercisable in a plain
   browser (`VITE_MOCK=1`); mocks keep in-memory state (login → session with a 1 s ticker and warnings, wallet,
   orders that progress, chat echoes, top-ups that settle) and emit `agent://*` events through `emitMock`.
   `MOCKED_COMMANDS` exposes the registered names; keeping it aligned with `COMMAND_NAMES` in
   `src-tauri/src/commands/mod.rs` is a review rule, not a CI test yet (`ROADMAP.md`).
3. Error → UI mapping: the i18n bundles carry `errors.<code>` for every `ErrorCode` (plus `errors.generic`,
   `errors.network`, `errors.unknown`); screens pick the key from `ShellApiError.code` and interpolate `details`
   where useful (`LaunchOverlay.describeLaunchError` appends `games.antiCheatReason.<reason>` for
   `antiCheatBlocked`).
4. Codes that force navigation regardless of caller (`App.tsx → GlobalListeners`, `store/index.ts`): a lost user
   or a `locked`/`ended`/`idle` session → `/lock`; `kiosk://idle` with nobody logged in → `/idle`;
   `agent://auth.expired` → `auth.onExpired` → `resetStores()` → `/lock`; `kiosk://connectivity` changes show a
   toast and `auth.refresh()` runs on reconnect. There is no `/updating` route: `versionMismatch` surfaces as an
   error toast.
5. Log correlation: the frontend does not add a trace id to `args`; the Rust handler generates the IPC envelope
   `id` (`IpcEnvelope::request`) and logs it, which is what to search for in the Agent log.

### 4.1 Command registry

There is no generated `commands.json`: `src-tauri/build.rs` only runs `tauri_build::build()`. The registry is the
`COMMAND_NAMES` constant in `apps/shell/src-tauri/src/commands/mod.rs` (the 64 proxy commands below, asserted by a
unit test) plus the 15 `kiosk_*` commands registered from `kiosk/commands.rs` through the `invoke_handler!` macro in
`lib.rs`:

```json
{ "protocol": 1, "commands": ["auth_login", "auth_logout", "auth_status", "auth_qr_start", "session_get", "session_start", "session_pause", "session_resume", "session_end", "session_extend", "session_lock", "session_unlock", "session_time_left", "games_list", "games_get", "games_launch", "games_kill", "games_running", "games_install_status", "apps_list", "apps_launch", "wallet_balance", "wallet_tariffs", "wallet_history", "wallet_topup_intent", "shop_products", "shop_order", "shop_order_status", "shop_orders", "chat_history", "chat_send", "chat_mark_read", "booking_seats", "booking_reserve", "booking_cancel", "tournaments_list", "tournaments_join", "tournaments_leaderboard", "profile_get", "profile_update", "profile_stats", "profile_achievements", "profile_loyalty", "settings_get", "settings_set", "settings_get_theme", "settings_list_themes", "settings_get_shell_config", "policy_get", "policy_reload", "sys_pc_info", "sys_hardware", "sys_metrics", "sys_call_admin", "sys_reboot", "sys_shutdown", "sys_lock_screen", "sys_set_volume", "sys_set_locale", "sys_unlock_admin", "sys_ack_admin_message", "sys_log_client_error", "update_check", "update_apply", "kiosk_state", "kiosk_set_guard", "kiosk_set_fullscreen", "kiosk_show_overlay", "kiosk_monitors", "kiosk_move_to_monitor", "kiosk_virtual_keyboard", "kiosk_focus", "kiosk_exit", "kiosk_reload", "kiosk_open_devtools", "kiosk_gamepad_state", "kiosk_idle_reset", "kiosk_i18n_bundle", "kiosk_asset_url"],
  "events": ["agent://session.updated", "agent://session.warning", "agent://session.ended", "agent://wallet.updated", "agent://chat.message", "agent://notification.push", "agent://admin.message", "agent://admin.remoteControl", "agent://game.stateChanged", "agent://policy.changed", "agent://update.available", "agent://update.progress", "agent://update.ready", "agent://sys.metrics", "agent://sys.connectivity", "agent://shell.command", "agent://auth.expired", "agent://shop.orderUpdated", "kiosk://idle", "kiosk://gamepad", "kiosk://hotkey", "kiosk://monitorChanged", "kiosk://connectivity", "kiosk://themeChanged", "kiosk://localeChanged", "kiosk://focus", "kiosk://overlay"] }
```

`src-tauri/capabilities/default.json` (windows `main`, `overlay`, `ads*`) grants only the core window/event/tray permissions and the `log`/`process`/`os` plugins; application commands are allowed by default in Tauri 2, so this list is the complete command surface and the mock registry (`MOCKED_COMMANDS`) mirrors it.
