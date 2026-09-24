# ClubShell — Architecture

Status: normative. Every other document (`IPC_PROTOCOL.md`, `SERVER_API.md`, `TAURI_COMMANDS.md`) and every
code component derives from this document. Where they conflict, the contract in `src/ClubShell.Contracts`
is the tie-breaker and this document must be fixed.

Scope: client-side software installed on every gaming PC of a club. The central server (billing, users,
shop, admin panel) is a separate product; only its API surface consumed by the client is specified here.

---

## 1. Component map

```
┌──────────────────────────────────────────────────────────────────────────────────────────┐
│                                   CENTRAL SERVER (cloud / LAN)                           │
│   REST https://<server>/api/v1        WebSocket wss://<server>/ws/agent                  │
│   users · billing · sessions · games catalog · shop · chat · bookings · tournaments ·     │
│   policies · update manifests · admin panel                                              │
└───────────────▲──────────────────────────────────────────────▲───────────────────────────┘
                │ HTTPS (Bearer agent JWT + HMAC request sig)  │ WSS (agent JWT)
                │ REST: pull / mutate                          │ push: ServerCommand / AgentEvent
                │                                              │
┌───────────────┴──────────────────────────────────────────────┴───────────────────────────┐
│  GAMING PC (Windows 10/11 x64)                                                           │
│                                                                                          │
│  ┌──────────────────────────── Session 0 (non-interactive) ─────────────────────────┐    │
│  │  ClubShellAgent.exe  — .NET 8 Worker Service, runs as NT AUTHORITY\SYSTEM        │    │
│  │   ├─ ServerClient (REST + RetryPolicy + signing)      ├─ SessionManager / Timers  │    │
│  │   ├─ RealtimeClient (WS + Reconnector)                ├─ OfflineSessionStore      │    │
│  │   ├─ PipeServer \\.\pipe\clubshell-agent              ├─ PolicyEnforcer          │    │
│  │   ├─ GameLaunchers (Steam/Epic/BNet/Riot/EA/Ubi/Exe)  ├─ AccountPool / CloudSave │    │
│  │   ├─ AntiCheat checks (EAC/Faceit/Vanguard/SecBoot)   ├─ Updater (self + shell)  │    │
│  │   ├─ RemoteAdmin (capture / input / messages)         ├─ Power (shutdown / WoL)  │    │
│  │   ├─ UserProvisioning (kiosk user, profile reset)     ├─ Storage (share mount)   │    │
│  │   └─ ShellWatchdog (CreateProcessAsUser + restart)    └─ Telemetry (WMI / perf)  │    │
│  └───────────────────────────▲──────────────────────────────────────────────────────┘    │
│                              │ named pipe, message mode, 4-byte LE length + UTF-8 JSON   │
│                              │ ACL: SYSTEM + kiosk user SID only                         │
│  ┌───────────────────────────┴───────── Interactive session (N ≥ 1) ─────────────────┐   │
│  │  Windows shell replacement for user "club"  (HKLM Winlogon\Shell override)        │   │
│  │  clubshell-shell.exe — Tauri 2 (Rust)                                             │   │
│  │   ├─ src-tauri: PipeClient (tokio) · kiosk hardening (WH_KEYBOARD_LL, alt-tab,    │   │
│  │   │     taskbar hide, topmost guard, overlay, multi-monitor, idle) · gamepad ·    │   │
│  │   │     tray · #[tauri::command] proxies                                          │   │
│  │   └─ WebView2: React 18 + TS + Vite + Tailwind + Zustand + react-router + i18next │   │
│  │                                                                                   │   │
│  │  Game processes (launched by Agent via CreateProcessAsUser into this session)     │   │
│  └───────────────────────────────────────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

### 1.1 Repository → component mapping

| Path | Component | Runtime |
|------|-----------|---------|
| `src/ClubShell.Contracts` | Canonical DTOs / enums (System.Text.Json, camelCase, string enums) | .NET 8 lib |
| `src/ClubShell.Core` | Abstractions, config, HTTP, logging, realtime, security, updates. No Win32. | .NET 8 lib |
| `src/ClubShell.Windows` | Win32 layer (P/Invoke, hooks, WMI, WTS, job objects, registry, firewall, users) | net8.0-windows lib |
| `src/ClubShell.Agent` | `ClubShellAgent` Windows service | .NET 8 Worker |
| `apps/shell` | Kiosk UI (Tauri 2 + React 18) | Rust + WebView2 |
| `crates/protocol` | Rust mirror of Contracts (serde) — generated | Rust crate |
| `crates/winutil` | Rust Win32 helpers (windows 0.58) | Rust crate |
| `packages/contracts-ts` | TS mirror of Contracts — generated | npm package |
| `tools/ContractsGen` | Reflection over Contracts → TS + Rust | .NET tool |
| `tools/MockServer` | Fastify + ws mock of central server | Node 20 |
| `installer/wix` | WiX v5: `ClubShell.msi` (Agent service + Shell + config/themes) and the `ClubShellSetup.exe` Burn bundle (.NET 8 runtime + WebView2 bootstrapper + MSI) | MSI / EXE |
| `config/*` | Defaults deployed to `C:\ProgramData\ClubShell` | JSON |

### 1.2 Contract mirroring rule

`ClubShell.Contracts` is the single source. `crates/protocol` and `packages/contracts-ts` are generated by
`tools/ContractsGen`; declarations are never hand-edited. Hand-written helpers (Money arithmetic, type guards,
typed maps, `impl` blocks, tests) live between `BEGIN MANUAL` / `END MANUAL` line comments (exact marker text in
`tools/ContractsGen/Program.cs`): the generator carries every such block over, in order, to the end of the regenerated
file and replaces everything else. `crates/protocol/src/lib.rs` and `ipc.rs` are entirely hand-written (not
generated); `gen-contracts-*.ps1 -Check` (CI `contracts-drift`) fails on any drift. Serialization rules (all three
languages):

| Rule | C# | Rust | TS |
|------|----|------|----|
| Property names | `JsonNamingPolicy.CamelCase` | `#[serde(rename_all = "camelCase")]` | camelCase identifiers |
| Enums | `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` | `#[serde(rename_all = "camelCase")]` on enum | string literal union |
| Null vs absent | Optional = nullable, serialized as `null` when set to null, omitted when `JsonIgnoreCondition.WhenWritingNull` | `Option<T>` + `skip_serializing_if = "Option::is_none"` | `field?: T \| null` |
| Ids | `Guid` → lowercase hyphenated `string` | `uuid::Uuid` (serde string) | `string` |
| Timestamps | `DateTimeOffset` → ISO-8601 UTC `2026-09-21T10:15:30.123Z` | `chrono::DateTime<Utc>` (RFC 3339, millis) | `string` |
| Money | `Money { long Amount; string Currency }` | `Money { amount: i64, currency: String }` | `{ amount: number; currency: string }` |
| Durations | `int` seconds | `i32` seconds | `number` seconds |
| Unknown fields | ignored on read | `#[serde(default)]` per struct, unknown ignored | ignored |

Byte-identical output requirement: a canonical encoder writes fields in declaration order, no whitespace,
UTF-8, `null` for explicitly-null optionals of the "nullable" class (see each table's Required column:
`yes` = always present, `no` = may be absent or `null`, both readers accept either).

---

## 2. Process and session model

| Process | Account | Windows session | Started by | Lifetime |
|---------|---------|-----------------|------------|----------|
| `ClubShellAgent.exe` | `NT AUTHORITY\SYSTEM` | 0 | Service Control Manager, start = Automatic (Delayed) | boot → shutdown |
| `clubshell-shell.exe` | kiosk user (`club`, local, non-admin) | interactive (1, 2, …) | Agent watchdog via `CreateProcessAsUser` with the kiosk user's primary token | logon → logoff / crash |
| Game / app processes | kiosk user | same as Shell | Agent (`ProcessAsUser` + job object) | launch → exit / kill |
| `explorer.exe` | — | — | never for the kiosk user (`Shell` registry value = `clubshell-shell.exe`) | — |

Rules:

1. The Agent is the only privileged component. It owns every OS-level side effect (launch, kill, firewall,
   registry policy, power, users, storage). The Shell is UI only and never calls a privileged API.
2. Windows auto-logon (`AutoAdminLogon`) logs the kiosk user in at boot (`shell.kioskUser.name`, default `club`
   in code, `config/agent.default.json`, `install.ps1 -KioskUser` and the MSI `KIOSKUSER` property).
   Winlogon's per-user `Shell` value for that account points at `clubshell-shell.exe`; for admins it remains
   `explorer.exe` (`SHELL_REPLACEMENT.md`).
3. The Agent's watchdog enumerates WTS sessions every `shell.watchdogIntervalMs`; for an active console
   session owned by the kiosk user with no live Shell process it launches one (`CreateProcessAsUser`,
   desktop `winsta0\default`, environment block of the user, working dir = Shell install dir).
4. Crash loop guard: at most `shell.maxRestartsPerMinute` restarts; beyond that the watchdog enters safe
   mode (`CrashRecovery`): `explorer.exe` is started in the kiosk session (`ShellLauncher.LaunchExplorerAsync`),
   relaunches pause until the next logon, and the telemetry event `shellCrashLoop` is raised
   (`SHELL_REPLACEMENT.md` §6).
5. Games run inside a Windows job object per session (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`) so a session
   end kills the whole tree.
6. One interactive user session at a time is supported. Fast user switching is disabled by policy.

---

## 3. Trust boundaries

```
  Untrusted                     Semi-trusted                     Trusted (SYSTEM)                Remote trusted
  ─────────                     ────────────                     ────────────────                ──────────────
  player at keyboard  ──(kiosk hardening)──▶  Shell (kiosk user) ──(pipe + shell token)──▶ Agent ──(TLS + JWT + HMAC)──▶ Server
                                              WebView2 renderer                                       ▲
                                              (no Node, no fs, CSP)                                   │ admin panel (out of scope)
```

| Boundary | Enforcement |
|----------|-------------|
| Player ↔ Shell | Low-level keyboard hook, `blockedKeyCombos`, topmost guard, no explorer, no task manager, no Run dialog, USB storage policy, DNS filter |
| Shell ↔ Agent | Pipe DACL = `SYSTEM:full`, `<kiosk SID>:read/write`, no `Everyone`; `auth.hello` shell token (32-byte random, stored at `C:\ProgramData\ClubShell\secure\shell.token`, ACL kiosk-user read-only, regenerated at each Agent start and written before Shell launch); rate limiting per connection; payload size cap 4 MiB; every request validated against Contracts |
| Agent ↔ Server | TLS 1.2+, optional SPKI pinning (`server.tlsPinSha256`), RS256 agent JWT, HMAC-SHA256 request signing with ±5 min skew window, refresh-token rotation, DPAPI (LocalMachine scope) for stored secrets |
| Agent ↔ OS | Runs as SYSTEM; every input from Shell/Server is treated as hostile: path allow-listing for `exePath`, argument escaping, no shell execution (`UseShellExecute=false`), registry writes only under fixed keys |
| Update packages | SHA-256 + RSA-PSS signature (`updates.publicKeyPath`), verified before apply; mandatory downgrade protection (`version` must be greater unless `mandatory` rollback flagged by server) |

Secrets on disk (all DPAPI-LocalMachine wrapped unless noted):

| File | Content |
|------|---------|
| `secure\agent.tokens` | `accessToken`, `refreshToken`, `signingSecret`, `expiresAt` |
| `secure\shell.token` | plain 32-byte hex, ACL restricted (see above) |
| `secure\share.cred` | SMB credentials for games share |
| `secure\kiosk.cred` | kiosk account name, password, SID, rotation time (`TempUserProvisioner`) |
| `secure\hwid.fallback` | persistent random HWID component used when fewer than `Hwid.MinComponents` hardware ids are readable |

---

## 4. Data flow

### 4.1 Player login → session → game launch

```
Player        Shell(React)      Shell(Rust)        Agent                     Server
  │  enter creds   │                │                │                          │
  ├───────────────▶│ invoke        │                │                          │
  │                ├──auth_login──▶│ IPC auth.login │                          │
  │                │               ├───────────────▶│ POST /auth/login         │
  │                │               │                ├─────────────────────────▶│
  │                │               │                │◀── AuthResponse ─────────┤
  │                │               │◀── response ───┤ store user token (DPAPI) │
  │                │◀── {user} ────┤                │                          │
  │  pick tariff   │               │                │                          │
  ├───────────────▶├─session_start▶├ session.start ▶│ POST /sessions           │
  │                │               │                ├─────────────────────────▶│
  │                │               │                │◀── Session ──────────────┤
  │                │               │                │ start timer, WS sessionStarted ▶
  │                │◀── Session ───┤◀── response ───┤                          │
  │                │◀ agent://session.updated ◀ event session.updated          │
  │  click game    │               │                │                          │
  ├───────────────▶├─games_launch─▶├ games.launch ─▶│ anti-cheat check         │
  │                │               │                │ GET /games/{id}/accounts/lease ▶
  │                │               │                │ inject creds → launcher  │
  │                │               │                │ CreateProcessAsUser + job│
  │                │               │                │ POST /games/{id}/launch-report ▶
  │                │◀ LaunchResult ┤◀── response ───┤                          │
  │                │◀ agent://game.stateChanged ◀ event                        │
```

### 4.2 Server push → Shell

```
Server ──WS ServerCommand{lock}──▶ Agent ──apply (PolicyEnforcer.Lock)──▶ IPC event shell.command{lock} ──▶ Shell(Rust) ──emit agent://shell.command──▶ React (LockScreen route)
                                    └──WS ack{ok}──▶ Server
```

### 4.3 Telemetry

```
Agent.Telemetry (5 s tick) ─▶ PcMetrics ─┬─▶ IPC event sys.metrics ─▶ Shell overlay
                                         └─▶ batched every telemetry.uploadIntervalSec ─▶ POST /agents/{pcId}/telemetry
Agent.Heartbeat (30 s)                   ─▶ POST /agents/{pcId}/heartbeat (status, session id, versions)
```

---

## 5. Threading model

### 5.1 Agent (.NET)

| Concern | Mechanism |
|---------|-----------|
| Hosting | `IHost` with a hosted service per subsystem (`TempUserProvisioner`, `PipeServer`, `ServerConnection`/`RealtimeClient`, `SessionManager` + `SessionTimer`, `TelemetryReporter`, `AgentUpdater`, `ShellWatchdog`, `AntiCheatMonitor`, `AgentWorker` for the offline flush) |
| Pipe server | One accept loop; each client connection = one `Task` reading frames; requests dispatched to `MessageDispatcher`; per-connection outbound `Channel<IpcEnvelope>` (bounded 1024) drained by one writer so frames never interleave; a per-connection `SemaphoreSlim` gate bounds handler concurrency |
| Server calls | `HttpClient` from `IHttpClientFactory`, `SocketsHttpHandler`, `RetryPolicy` (exponential, jitter, max 5), circuit breaker (open after 5 consecutive 5xx/timeouts for 30 s) |
| WebSocket | Single reader task + single writer task, outbound `Channel<AgentEvent>`; `Reconnector` with backoff 1 s → 60 s, jitter ±20 % |
| Session state | `SessionManager` (`ISessionService`) owns a single session state behind a `SemaphoreSlim`; all mutations go through it; timer ticks at 1 s using `IClock` (monotonic `Stopwatch` for elapsed, wall clock only for display) |
| Win32 | Every P/Invoke that blocks (WMI, WTS, hooks) is confined to a dedicated `TaskScheduler` (`ConcurrentExclusiveSchedulerPair`, max 2) — never on the thread pool's hot path |
| Cancellation | Every async method accepts `CancellationToken`; host shutdown token cascades |
| Static state | none mutable; singletons via DI only |

### 5.2 Shell (Rust / Tauri)

| Concern | Mechanism |
|---------|-----------|
| Runtime | tokio multi-thread runtime (Tauri's); `clubshell_winutil::pipe::PipeClient` = reader task + writer task (`mpsc<Bytes>`) + heartbeat task, one `oneshot` per pending request (`Mutex<HashMap<Uuid, oneshot::Sender<IpcEnvelope>>>`), request timeout `shell.ipc.requestTimeoutMs` |
| Events | Pipe events are forwarded to the webview via `app.emit("agent://<name>", payload)` from the reader task |
| Hooks | `WH_KEYBOARD_LL` requires a message loop; a dedicated OS thread runs the `GetMessage` pump (`crates/winutil/src/hooks.rs`); the hook callback hands events to a tokio unbounded `mpsc` channel (never blocks) |
| Window guard | 250 ms timer on main thread re-asserts topmost/fullscreen while `kiosk.topmostGuard` |
| Gamepad | `gilrs` poll thread at `gamepad.pollMs`, emits `kiosk://gamepad` |
| Idle | `GetLastInputInfo` polled every 1 s; emits `kiosk://idle` on threshold crossings |
| Shared state | `tauri::State<AppState>` (`Arc<AppStateInner>`, `parking_lot::RwLock` caches, `tokio::sync::Mutex` only around `games_launch`/`apps_launch`) plus `State<Arc<Kiosk>>` for the hardening guards |

### 5.3 Frontend

Zustand stores per domain (`auth`, `session`, `games`, `wallet`, `shop`, `chat`, `notifications`, `settings`,
`theme`); event listeners registered once in `store/index.ts → wireListeners()` via `events.on("<name>")`
(`agent://<name>`); no polling except the local 1 s countdown and the `session.timeLeft` resync every 30 s to
correct drift.

---

## 6. Startup sequence

### 6.1 Agent

```
 1. SCM starts service → Program.Main → Host.Build
 2. SettingsLoader: read C:\ProgramData\ClubShell\agent.json (create from config/agent.default.json if absent),
    validate schema, apply env overrides CLUBSHELL__*
 3. Serilog init (file sink logs\agent-<date>.json, rolling)
 4. Hwid.Compute() (SMBIOS UUID + disk serial + MAC, SHA-256)  → hwid
 5. TokenStore.Load() (DPAPI). If none or refresh fails → POST /agents/register (clubApiKey + hwid)
 6. GET /agents/{pcId}/config  → merge server overrides into runtime config
 7. GET /agents/{pcId}/policies → PolicyEnforcer.Apply (registry, firewall, DNS filter, USB, hooks config)
 8. Storage: mount games share if configured (retry 3×, non-fatal)
 9. UserProvisioning: ensure kiosk user exists, password rotated (random, stored DPAPI). Profile reset if
    shell.kioskUser.resetProfileOnLogout and the previous session never closed cleanly — a debt marker written at
    session start and cleared only once a reset has run, so a power cut cannot hand the profile to the next player.
    Runs after the persisted session has been restored, and again on every maintenance tick so a deletion that
    failed is retried without waiting for the next restart (throttled to once per cooldown). A session that is
    still legitimately open is left alone.
10. Write secure\shell.token (new random), set ACL
11. PipeServer.StartAsync (pipe with DACL), Heartbeat loop, Telemetry loop
12. RealtimeClient.Connect (WS) — non-blocking; offline mode if it fails
13. OfflineSessionStore.FlushAsync() if the outbox is non-empty and the server is reachable
14. Updater.CheckOnce() (manifest) — schedules apply for idle time unless mandatory
15. Watchdog.Start → launches Shell in interactive session when present
16. Ready. Service status = Running.
```

Steps 5–7 tolerate network failure: the last applied `policies.json`, the cached `cache\games.json`,
`cache\apps.json`, `cache\tariffs.json` and `cache\products.json` are used and the Agent enters offline mode
(section 8).

### 6.2 Shell

```
 1. Winlogon starts clubshell-shell.exe as kiosk user (or Agent watchdog does)
 2. Read C:\ProgramData\ClubShell\shell.json + themes\<theme>.json (fallback to embedded defaults)
 3. Apply kiosk hardening (hooks, taskbar hide, fullscreen, topmost) per shell.json + cached policy
 4. Connect pipe \\.\pipe\clubshell-agent (retry 500 ms → 5 s backoff, forever)
 5. Send auth.hello{shellToken, shellVersion} → receive hello response (pcId, agentVersion, policyVersion)
 6. Start heartbeat (sys.ping every 5 s)
 7. Load webview (index.html); frontend calls auth_status, settings_get, policy_get, games_list
 8. Route: authenticated && session active → /home, authenticated → /tariffs, else → /login
```

---

## 7. Crash and recovery matrix

| Failure | Detection | Recovery |
|---------|-----------|----------|
| Shell process crash | Watchdog (WTS enumeration + process exit wait) | Restart within `shell.restartDelayMs`; session timer unaffected (owned by Agent); frontend reloads state from `auth.status` + `session.get` |
| Shell hang (no `sys.ping` for 3 × 5 s) | `PipeServer` per-connection watchdog | Agent kills Shell (`TerminateProcess`) → watchdog restarts it |
| Agent crash | SCM recovery (restart 5 s, 3 attempts, reset after 1 d) | Session state persisted to `cache\offline.db` (`sessions` table) every `session.persistIntervalSec`; on restart Agent resumes timer from the persisted `endsAt` (wall clock, server-validated when online); Shell reconnects pipe and re-hellos |
| Pipe broken | Shell read error / Agent write error | Shell reconnects with backoff; Agent drops connection state; no data lost (all state server/agent-side) |
| Server unreachable | HTTP errors / WS close | Offline mode; queue events; keep serving cached data |
| Game crash | Job object / process exit | `game.stateChanged{exited, exitCode}`; account lease released; launch report sent |
| Policy apply failure | exceptions in PolicyEnforcer | Log + telemetry `policyApplyFailed`; keep previous policy; retry on next `policy.reload` |
| Update apply failure | Applier exception / hash mismatch | Keep current version, mark manifest version as failed for 6 h, report via telemetry |
| Clock jump | `IClock` compares wall vs monotonic each tick, \|Δ\| > 30 s | Timer uses monotonic elapsed; `endsAt` re-derived from server on next heartbeat |
| Disk full in logs | Serilog sink failure | Serilog self-log to Windows Event Log; logs pruned by `logging.retainDays` |

---

## 8. Offline mode

Triggered when heartbeat fails 3 consecutive times or WS is disconnected > 20 s. Cleared when a heartbeat
succeeds.

| Capability | Online | Offline |
|------------|--------|---------|
| Login (password) | server | allowed only for users cached in `cache\offline.db` (`users` table, TTL `offline.userCacheTtlHours`), password verified against the `offlineHash` delivered by the server on the last online login — a PBKDF2-SHA256 string `pbkdf2$<iterations>$<salt b64>$<hash b64>` (`OfflineSessionStore.HashPassword`; other formats such as Argon2id PHC strings fail closed) |
| Login (QR / card / token) | server | denied (`AgentOffline`) |
| Guest login | server | allowed if `offline.allowNewSessions` |
| Start session | server | allowed if `offline.allowNewSessions`; cost computed from cached tariffs; capped by `offline.maxOfflineMinutes` and cached balance |
| Timer / pause / extend | agent | agent (queued as `SessionEvent`) |
| Game launch (no account pool) | agent | agent |
| Game launch (account pool) | server lease | denied (`AccountPoolExhausted`) unless a lease is cached and unexpired |
| Shop, chat, booking, tournaments, top-up | server | denied (`ServerUnavailable`) |
| Policies | server | cached |
| Updates | server | skipped |

Queue: `OfflineSessionStore` (SQLite WAL, `cache\offline.db`, tables `sessions`, `events`, `users`). Session
events are appended to the `events` outbox (capped at `offline.maxQueue`, oldest dropped) and replayed FIFO
through `POST /sessions/{id}/events` in batches of ≤ 100 with client-generated ids (`Idempotency-Key`). A
non-retryable 4xx marks the batch dead-lettered (counted in `OfflineQueueStats.DeadLettered`, surfaced via
telemetry). After a successful flush the Agent emits `AgentEvent.offlineQueueFlushed{count, deadlettered}`.

---

## 9. File system layout

```
C:\Program Files\ClubShell\Agent\        ClubShellAgent.exe + deps (Agent MSI)
C:\Program Files\ClubShell\Shell\        clubshell-shell.exe + WebView2 loader (Shell MSI)
C:\ProgramData\ClubShell\
   agent.json            Agent configuration (schema §12.1). Admin-writable only.
   shell.json            Shell configuration (schema §12.2). Admin-writable; kiosk user read-only.
   policies.json         Last applied policy snapshot (schema §12.3). Written by Agent.
   themes\*.json         Theme files (schema §12.4). `default.json` mandatory.
   cache\                games.json, apps.json, tariffs.json, products.json (ETag caches), offline.db
                         (session snapshot, event outbox, cached users), media\ (covers, videos, ads,
                         theme assets: Agent LocalCache → Shell asset scope), saves\ (cloud-save bundles),
                         updates\ (packages)
   logs\                 agent-YYYYMMDD.json, shell.YYYY-MM-DD.log, shell-crash.log, update-*.log
   secure\               DPAPI-protected secrets (§3)
   pending-update\       staged package + <component>.json / rollback-<component>.json / failed-<component>.json
   crash-history.json    last 50 Shell crash reports (CrashRecovery)
```

ACLs: `C:\ProgramData\ClubShell` = Administrators/SYSTEM full, Users read; `logs\` kiosk user append;
`secure\` SYSTEM full only (+ `shell.token` kiosk read); `cache\media\` kiosk read.

Environment variable overrides (Agent): `CLUBSHELL__<Section>__<Key>` (double underscore), e.g.
`CLUBSHELL__server__baseUrl`. Command-line: `--config <path>`, `--console` (run as console app),
`--dev` (relaxed TLS pinning, mock server URL), `--install` / `--uninstall` (register or remove the
`ClubShellAgent` service from the published exe).

---

## 10. Logging

Serilog structured JSON, one line per event, UTF-8, fields: `@t` (timestamp), `@l` (level), `@m`
(rendered message), `@mt` (template), `@x` (exception), plus properties. Common enrichers on every
line: `app` (`agent`|`shell`), `version`, `pcId`, `sessionId?`, `userId?`, `traceId` (propagates to server
via `X-Trace-Id` header and to Shell via IPC envelope `id`), `machine`.

| Level | Usage |
|-------|-------|
| Verbose | pipe frames (hex-dumped first 256 B), never in production |
| Debug | IPC message names, HTTP method/path/status/latency, timer ticks every 60 s |
| Information | lifecycle: startup steps, login, session start/end, launches, policy applied, update stages |
| Warning | retries, offline transitions, missed heartbeats, policy denials, anti-cheat soft violations |
| Error | failed operations with exception |
| Fatal | unrecoverable, process exits |

PII rule: never log passwords, tokens, card ids, account-pool credentials. `username` is logged; `password`
fields are replaced by `"***"` by a destructuring policy on `AuthRequest`.

Rolling: daily file, `logging.maxFileMb` per file, `logging.retainDays` retention. Shell (Rust `tracing`)
writes `logs\shell.YYYY-MM-DD.log` (JSON lines, 14 files kept, plus `shell-crash.log` from the panic hook)
via the `tracing-subscriber` JSON layer; the frontend forwards `console.error` to Rust through
`sys_log_client_error` (IPC `sys.logClientError`).

---

## 11. Versioning and updates

- Semantic versioning `MAJOR.MINOR.PATCH` for Agent, Shell and protocol.
- IPC envelope carries `v` (integer protocol major). Agent supports `v` ∈ {current, current−1}. Mismatch →
  `versionMismatch` on `auth.hello`; Shell shows "updating…" and waits for the Agent to update it.
- REST: path prefix `/api/v1`; breaking change → `/api/v2`. Agent sends `User-Agent: ClubShellAgent/<ver>`
  and `X-Agent-Version`, `X-Shell-Version`; server may respond `426 Upgrade Required` with error code
  `versionMismatch`.
- Update channel from `policies.json → updates.channel` (fallback `agent.json → updates.channel`).
- Flow: `GET /updates/{channel}/manifest?component=agent|shell&current=<ver>` → `UpdateManifest` →
  download to `cache\updates\<component>-<version>.msi` (resumable, `Range`) → verify size + sha256 + RSA-PSS
  signature → stage to `pending-update\` → IPC `update.ready` → apply when no session is open and the apply
  window allows (or after a 60 s notice if `mandatory`) → Shell MSI applied by the Agent via
  `msiexec /i /qn /norestart` after a backup of the Shell directory; Agent self-update is detached
  (`ClubShell.Updater.exe` helper when present next to the Agent, otherwise `msiexec` directly; the MSI's
  service actions stop/restart `ClubShellAgent`, SCM recovery covers a crash). Rollback markers
  `pending-update\rollback-<component>.json` are reconciled at the next start: a Shell that never sent
  `auth.hello` with the new version is restored from `cache\updates\shell-backup\`, a failed Agent install is
  left at the previous (Windows Installer rolled back) version; the version is suppressed for 6 h.

See `UPDATES.md` for the detailed state machine.

---

## 12. Config schemas

All config files: UTF-8 JSON, comments not allowed, unknown keys → warning + ignored, missing keys → default
from the tables below. `version` is the schema version (integer), currently `1`. Durations in seconds
unless the key ends with `Ms`.

### 12.1 `agent.json`

```jsonc
{
  "version": 1,
  "pcId": null,                                   // string uuid | null. Assigned by server at registration, persisted by Agent.
  "pcName": null,                                 // string | null. Defaults to machine name.
  "zone": null,                                   // string | null. Server value wins.
  "server": {
    "baseUrl": "https://club.example.uz/api/v1",  // required
    "wsUrl": "wss://club.example.uz/ws/agent",    // required
    "clubApiKey": "",                             // required for first registration; may be blank after tokens exist
    "timeoutSec": 15,                             // per request
    "tlsPinSha256": [],                           // base64 SPKI pins; empty = system trust only
    "retry": { "maxAttempts": 5, "baseDelayMs": 500, "maxDelayMs": 15000 },
    "circuitBreaker": { "failures": 5, "openSec": 30 },
    "clockSkewToleranceSec": 300
  },
  "ipc": {
    "pipeName": "clubshell-agent",                // \\.\pipe\<pipeName>
    "maxMessageBytes": 4194304,
    "heartbeatIntervalSec": 5,
    "missedHeartbeatsBeforeKill": 3,
    "maxConnections": 4,
    "requestsPerSecond": 50,
    "shellTokenPath": "secure\\shell.token"       // relative to ProgramData root
  },
  "shell": {
    "exePath": "C:\\Program Files\\ClubShell\\Shell\\clubshell-shell.exe",
    "kioskUser": {
      "name": "club",
      "createIfMissing": true,
      "rotatePasswordOnStart": true,
      "resetProfileOnLogout": true,
      "profileTemplate": null,                    // string path | null. Folder copied into a fresh profile.
      "preserveOnReset": null                     // string[] | null. Profile-relative dirs carried across a reset;
                                                  // null = ProfileReset.PreservedDirectories (anti-cheat vendors).
    },
    "watchdogIntervalMs": 2000,
    "restartDelayMs": 1500,
    "maxRestartsPerMinute": 5,
    "launchTimeoutSec": 30
  },
  "session": {
    "warningMinutes": [10, 5, 1],                 // session.warning at each remaining-minute mark
    "heartbeatSec": 30,
    "graceSec": 60,                               // after endsAt before forced lock
    "autoLockOnIdleSec": 0,                       // 0 = disabled (policy.kiosk.idleTimeoutSec wins if set)
    "persistIntervalSec": 5
  },
  "offline": {
    "enabled": true,
    "allowNewSessions": true,
    "maxOfflineMinutes": 240,
    "maxQueue": 10000,
    "storePath": "cache\\offline.db",
    "userCacheTtlHours": 168
  },
  "games": {
    "libraryRoots": ["D:\\Games", "G:\\Games"],
    "scanIntervalSec": 900,
    "launchTimeoutSec": 90,
    "killGraceSec": 10,
    "accountPool": { "enabled": true, "leaseTtlSec": 14400, "releaseOnExit": true },
    "cloudSave": { "enabled": true, "root": "cache\\saves", "maxMb": 512 },
    "launchers": {
      "steam":     { "exePath": "C:\\Program Files (x86)\\Steam\\steam.exe" },
      "epic":      { "exePath": "C:\\Program Files (x86)\\Epic Games\\Launcher\\Portal\\Binaries\\Win32\\EpicGamesLauncher.exe" },
      "battleNet": { "exePath": "C:\\Program Files (x86)\\Battle.net\\Battle.net Launcher.exe" },
      "riot":      { "exePath": "C:\\Riot Games\\Riot Client\\RiotClientServices.exe" },
      "ea":        { "exePath": "C:\\Program Files\\Electronic Arts\\EA Desktop\\EA Desktop\\EADesktop.exe" },
      "ubisoft":   { "exePath": "C:\\Program Files (x86)\\Ubisoft\\Ubisoft Game Launcher\\UbisoftConnect.exe" }
    }
  },
  "storage": {
    "gamesShare": {
      "enabled": false,
      "uncPath": "\\\\nas01\\games",
      "driveLetter": "G",
      "credentialsRef": "secure\\share.cred",     // DPAPI file with {username,password}
      "mountRetries": 3,
      "iscsi": null                               // { "portal": "10.0.0.5:3260", "targetIqn": "iqn...",
                                                  //   "readOnly": false } | null. readOnly marks the target's disks
                                                  //   read-only before first use — set it for a LUN several PCs share.
    }
  },
  "updates": {
    "channel": "stable",                          // stable | beta (policy overrides)
    "checkIntervalSec": 3600,
    "autoInstall": true,
    "applyWindow": { "from": "04:00", "to": "07:00" },  // local time, HH:mm; null = any time when idle
    "publicKeyPath": "C:\\Program Files\\ClubShell\\Agent\\update-public.pem",
    "downloadDir": "cache\\updates"
  },
  "telemetry": {
    "enabled": true,
    "metricsIntervalSec": 5,                      // local sampling + IPC sys.metrics
    "uploadIntervalSec": 60,                      // batch to server
    "hardwareRescanSec": 3600
  },
  "anticheat": {
    "checkIntervalSec": 30,
    "requireSecureBoot": false,
    "requireTpm": false,
    "requireHvci": false,                         // HVCI/VBS off (or hypervisorlaunchtype=off) becomes hvciOff
    "reportViolations": true
  },
  "remoteAdmin": {
    "allowScreenCapture": true,
    "allowRemoteInput": true,
    "captureFps": 5,
    "captureQuality": 60,
    "showIndicator": true
  },
  "power": {
    "wolEnabled": true,
    "shutdownGraceSec": 20,
    "allowShellReboot": true
  },
  "logging": {
    "level": "Information",                       // Verbose|Debug|Information|Warning|Error|Fatal
    "directory": "logs",
    "retainDays": 14,
    "maxFileMb": 20,
    "eventLog": true
  }
}
```

### 12.2 `shell.json`

```jsonc
{
  "version": 1,
  "locale": "ru",                                 // en | ru | uz (user may change; persisted here)
  "theme": "default",                             // file name in themes\ without .json
  "ipc": {
    "pipeName": "clubshell-agent",
    "connectTimeoutMs": 3000,
    "requestTimeoutMs": 15000,
    "reconnectMinMs": 500,
    "reconnectMaxMs": 5000
  },
  "kiosk": {
    "fullscreen": true,
    "topmostGuard": true,
    "hideTaskbar": true,
    "blockAltTab": true,
    "blockWinKey": true,
    "blockCtrlAltDel": false,                     // informational; CAD cannot be blocked by hook, policy handles it
    "hideCursorAfterSec": 0,                      // 0 = never
    "overlayOnLock": true,
    "allowVirtualKeyboard": true,
    "exitHotkey": "Ctrl+Alt+Shift+F12",           // opens admin PIN dialog; empty = disabled
    "adminPinHash": null                          // string sha256 hex | null → PIN validated by Agent (sys.unlockAdmin) when null
  },
  "idle": {
    "timeoutSec": 300,                            // kiosk://idle {idle:true}
    "dimAfterSec": 120,
    "screensaverAfterSec": 600
  },
  "ads": {
    "enabled": true,
    "intervalSec": 900,
    "durationSec": 15,
    "playlist": []                                // string[] of image/video URLs; empty = server-driven via shell.command showAds
  },
  "gamepad": {
    "enabled": true,
    "pollMs": 16,
    "deadzone": 0.25,
    "navigation": true
  },
  "monitors": {
    "primaryIndex": 0,
    "secondaryMode": "black"                      // black | mirror | wallpaper
  },
  "ui": {
    "defaultRoute": "/home",
    "gridColumns": 7,
    "showClock": true,
    "clockFormat": "HH:mm",
    "showMetricsOverlay": false,
    "showSessionBar": true,
    "coverAspect": "2:3",
    "currencyFormat": { "locale": "uz-UZ", "minorDigits": 0 }
  },
  "features": {
    "shop": true,
    "chat": true,
    "booking": true,
    "tournaments": true,
    "profile": true,
    "topup": true,
    "apps": true,
    "callAdmin": true
  },
  "sound": { "uiSounds": true, "defaultVolume": 60 },
  "logging": { "level": "info", "directory": "logs" },
  "devtools": false
}
```

### 12.3 `policies.json` (Policy DTO snapshot)

```jsonc
{
  "version": 12,                                  // int, monotonic; server-assigned
  "updatedAt": "2026-09-01T08:00:00.000Z",
  "shellReplacement": { "enabled": true, "shellExe": "C:\\Program Files\\ClubShell\\Shell\\clubshell-shell.exe" },
  "processAllowlist": {
    "mode": "deny",                               // allow = only patterns may run; deny = patterns are killed
    "patterns": ["cmd.exe", "powershell.exe", "regedit.exe", "taskmgr.exe", "mmc.exe", "*cheat*"]
  },
  "usb": { "allowStorage": false, "allowHid": true },
  "webFilter": {
    "enabled": true,
    "blockedDomains": ["*.torrent-site.example"],
    "allowedDomains": [],                         // non-empty = allow-list mode
    "dnsServers": ["1.1.1.3", "1.0.0.3"]
  },
  "explorer": {
    "disableTaskManager": true,
    "disableRun": true,
    "disableSettings": true,
    "hideTaskbar": true,
    "disableAltTab": true,
    "disableWinKey": true,
    "blockedKeyCombos": ["Ctrl+Shift+Esc", "Win+R", "Win+E", "Win+I", "Alt+F4", "Ctrl+Esc"]
  },
  "power": {
    "idleShutdownMin": null,                      // int | null
    "scheduledShutdown": null                     // "HH:mm" local | null
  },
  "updates": { "channel": "stable", "autoInstall": true },
  "anticheat": { "required": [], "blockOnViolation": true },   // required: AntiCheatKind[] whose drivers/services must be healthy
  "kiosk": { "idleTimeoutSec": 300, "adsIntervalSec": 900, "allowVirtualKeyboard": true }
}
```

### 12.4 Theme file `themes\<name>.json`

```jsonc
{
  "version": 1,
  "name": "default",                              // must equal file name
  "displayName": "ClubShell Graphite",
  "colors": {                                     // all hex #RRGGBB or #RRGGBBAA
    "bg": "#0C0C0E",
    "surface": "#161619",
    "primary": "#F4F4F5",
    "accent": "#7AA2F7",
    "text": "#FAFAFA",
    "muted": "#8E8E96",
    "danger": "#EF4444",
    "success": "#22C55E"
  },
  "radius": 10,                                   // px
  "font": "Inter",                                // installed font family name; fallback system-ui
  "backgroundVideo": null,                        // string URL/path | null
  "wallpaper": "themes/assets/default-wallpaper.jpg",   // string | null, relative to ProgramData root or absolute URL
  "blur": 0,                                      // px; 0 = flat panels, > 0 = translucent blurred panels
  "animations": true
}
```

Themes are applied by the Shell as CSS custom properties `--c-bg`, `--c-surface`, `--c-primary`,
`--c-accent`, `--c-text`, `--c-muted`, `--c-danger`, `--c-success` (`R G B` triplets), the derived contrast
colours `--c-on-primary` / `--c-on-accent` (dark when the colour is light, white otherwise), `--radius`, `--font`,
`--blur`; `tailwind.config.ts` maps the colours `bg`, `surface`, `primary`, `on-primary`, `accent`, `on-accent`,
`text`, `muted`, `danger`, `success` to `rgb(var(--c-<name>) / <alpha-value>)`. See `THEMING.md`.
