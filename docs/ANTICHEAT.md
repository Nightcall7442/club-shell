# ClubShell — Anti-cheat integration

Status: descriptive. This document describes what `src/ClubShell.Agent/AntiCheat` actually does. Where it
disagrees with the code, the code wins and this document must be fixed. Contract types live in
`src/ClubShell.Contracts/Games/LauncherType.cs` (`AntiCheatKind`, `AntiCheatChecks`, `AntiCheatCheckResult`,
`AntiCheatReport`, `AntiCheatSeverity`, `AntiCheatAction`) and `src/ClubShell.Contracts/Pc/PcInfo.cs`
(`AntiCheatPolicy`).

Scope: ClubShell does **not** scan for cheats. It verifies that the anti-cheat components a game depends on
(kernel driver, user-mode service, platform prerequisites) are installed, healthy and running, refuses or
stops a launch when they are not, and reports what it found to the server. Cheat detection itself is the
vendor's job (EAC, BattlEye, Vanguard, FACEIT, Ricochet).

---

## 1. Components

| File | Symbol | Role |
|------|--------|------|
| `src/ClubShell.Agent/AntiCheat/AntiCheatMonitor.cs` | `AntiCheatMonitor : BackgroundService, IAntiCheatGate` | Launch gate (`CheckForLaunchAsync`), runtime loop (`ExecuteAsync`), reporting (`ReportAsync`) |
| same file | `IAntiCheatChecker` | One probe per subsystem: `Kind`, `CheckAsync`, `CheckRuntimeAsync(gamePid)` |
| same file | `IAntiCheatGameControl` | Running games + `KillAsync(pid, force)` for the runtime loop (adapter over `GameLaunchService`) |
| same file | `IAgentEventSink` | Publishes `AgentEvent` to the server over WS (queued offline) |
| same file | `AntiCheatProbe` (internal) | Shared probes: `QueryService`, `IsProcessRunning`, `IsWithinStartupGrace`, `HasAuthenticodeSignature`, `NormalizeImagePath` |
| same file | `ServiceInfo` (internal record) | `Installed`, `Status`, `ImagePath`, `ImageExists`, `FileVersion`, `IsRunning` |
| `src/ClubShell.Agent/AntiCheat/EacChecker.cs` | `EacChecker` | Easy Anti-Cheat |
| `src/ClubShell.Agent/AntiCheat/FaceitChecker.cs` | `FaceitChecker` | FACEIT client + driver |
| `src/ClubShell.Agent/AntiCheat/VanguardChecker.cs` | `VanguardChecker` | Riot Vanguard (`vgk` / `vgc`) |
| `src/ClubShell.Agent/AntiCheat/SecureBootChecker.cs` | `SecureBootChecker` | Platform prerequisites (`Kind = AntiCheatKind.None`) |
| `src/ClubShell.Agent/Games/GameLaunchService.cs` | `GameLaunchService.CheckAntiCheatAsync` | Calls the gate from `games.launch` |
| `src/ClubShell.Agent/DependencyInjection.cs` | `AddAntiCheatSubsystem` | Registers the four checkers, `AntiCheatMonitor`, both `IAntiCheatGate` seams and `IAntiCheatGameControl` |
| `src/ClubShell.Windows/Hardware/WmiQueries.cs` | `WmiQueries.GetSecureBootEnabled`, `GetTpmPresentAsync` | UEFI / TPM facts |
| `apps/shell/src/screens/Games/LaunchOverlay.tsx` | `describeLaunchError` | Maps `antiCheatBlocked.details.reason` to a localized message |

DI registration (`DependencyInjection.AddAntiCheatSubsystem`):

```csharp
services.AddSingleton<IAntiCheatChecker, EacChecker>();
services.AddSingleton<IAntiCheatChecker, FaceitChecker>();
services.AddSingleton<IAntiCheatChecker, VanguardChecker>();
services.AddSingleton<IAntiCheatChecker, SecureBootChecker>();
services.TryAddSingleton<AntiCheatMonitor>();
services.TryAddSingleton<ClubShell.Agent.AntiCheat.IAntiCheatGate>(sp => sp.GetRequiredService<AntiCheatMonitor>());
```

Two `IAntiCheatGate` interfaces exist on purpose: `ClubShell.Agent.AntiCheat.IAntiCheatGate` (returns
`IReadOnlyList<AntiCheatCheckResult>`) is the monitor's own contract; `ClubShell.Agent.Games.IAntiCheatGate`
(returns `AntiCheatCheckResult[]`) is what `GameLaunchService` consumes. `AntiCheatGateAdapter` bridges them, and
`AntiCheatGameControlAdapter` resolves `GameLaunchService` lazily to break the
`AntiCheatMonitor → GameLaunchService → IAntiCheatGate(AntiCheatMonitor)` construction cycle.

---

## 2. Supported anti-cheats

| `AntiCheatKind` | Checker | Pre-launch check | Runtime check | Notes |
|-----------------|---------|------------------|---------------|-------|
| `eac` | `EacChecker` | service key + binary present | service running, or bootstrap process alive, or within 90 s grace | Demand-start service: "not running" is only a violation at runtime |
| `faceit` | `FaceitChecker` | `faceit` driver + `FACEIT` service installed, `faceit.sys` present and Authenticode-signed | driver loaded; service or client process alive (90 s grace) | Signature presence only; chain trust is not evaluated |
| `vanguard` | `VanguardChecker` | `vgk` installed **and loaded**, `vgc` installed; on Windows 11 Secure Boot + TPM | `vgk` loaded, `vgc` running (90 s grace) | Freshly installed `vgk` needs a reboot → `serviceStopped` at launch |
| `none` | `SecureBootChecker` | Secure Boot / TPM per `agent.json`, test-signing always | same as pre-launch (cached 60 s) | Runs whenever any kind is required or `requireSecureBoot`/`requireTpm` is on |
| `battlEye` | — | — | — | Enum value exists; **no checker registered** → treated as satisfied (`LogDebug "No checker registered"`) |
| `ricochet` | — | — | — | Same as BattlEye |

Missing checkers are a known gap (section 9). `AntiCheatMonitor.SupportedKinds` lists what is actually
registered at runtime; the value is `[eac, faceit, vanguard, none]` with the default DI.

---

## 3. What each checker verifies

### 3.1 Shared probes (`AntiCheatProbe`)

| Probe | Implementation |
|-------|----------------|
| `QueryService(name)` | Reads `HKLM\SYSTEM\CurrentControlSet\Services\<name>` (`Installed` = key exists), normalises `ImagePath` (`\??\`, `\SystemRoot\`, relative `system32\drivers\x.sys`, quoted paths with arguments), checks the binary exists, reads its `FileVersion`, and asks the SCM (`ServiceController.Status`) — SCM failures leave `Status = null` |
| `IsProcessRunning(name)` | `Process.GetProcessesByName` (no extension) |
| `IsWithinStartupGrace(pid, grace, now)` | `Process.StartTime` of the game pid compared with `IClock.UtcNow` |
| `HasAuthenticodeSignature(path)` | `X509Certificate.CreateFromSignedFile` succeeds (presence only, no chain validation) |

None of these touch game memory, enumerate modules or read vendor logs. Kernel-level facts come from the
registry and the SCM, which the Agent (LocalSystem) can always read.

### 3.2 `EacChecker`

| Item | Value |
|------|-------|
| Service names probed (newest first) | `EasyAntiCheat_EOS`, `EasyAntiCheat` (`EacChecker.ServiceNames`) |
| Bootstrap processes | `EasyAntiCheat_EOS`, `EasyAntiCheat`, `start_protected_game` (`EacChecker.ProcessNames`) |
| `StartupGrace` | 90 s |
| `CheckAsync` | no installed service → `driverMissing`; binary missing → `driverMissing`; otherwise OK (service may be stopped: EAC is demand-start) |
| `CheckRuntimeAsync(pid)` | installed + binary present, and (SCM `Running` **or** a bootstrap process alive **or** game younger than 90 s) → OK; else `serviceStopped` |

### 3.3 `FaceitChecker`

| Item | Value |
|------|-------|
| Driver | service key `faceit` (`FaceitChecker.DriverName`), binary `faceit.sys` |
| Service | `FACEIT` (`FaceitChecker.ServiceName`) |
| Processes | `faceitservice`, `faceitclient`, `FACEIT` |
| `CheckAsync` | neither key → `driverMissing`; driver key or binary missing → `driverMissing`; binary unsigned → `driverMissing` (logged "refusing to trust it"); otherwise OK |
| `CheckRuntimeAsync(pid)` | as above, then driver not `Running` → `serviceStopped` (after grace); then service not running and no client process → `serviceStopped` (after grace) |

### 3.4 `VanguardChecker`

| Item | Value |
|------|-------|
| Driver | `vgk` (boot-start kernel driver) |
| Service | `vgc` |
| `RequiresPlatformSecurity` | `OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)` (Windows 11) |
| `CheckAsync` | `vgk` key or binary missing → `driverMissing`; `vgk` installed but not `Running` → `serviceStopped` (log: "a reboot is required"); `vgc` missing → `driverMissing`; on Windows 11: `WmiQueries.GetSecureBootEnabled() == false` → `secureBootOff`, `GetTpmPresentAsync == false` → `tpmOff`; otherwise OK |
| `CheckRuntimeAsync(pid)` | `vgk` missing → `driverMissing`; `vgk` not loaded → `serviceStopped` (no grace); `vgc` not running after 90 s grace → `serviceStopped` |

Note the asymmetry with EAC: Vanguard's driver must already be loaded **before** the launch; EAC's service is
allowed to be stopped before the launch because the game starts it.

### 3.5 `SecureBootChecker` (platform prerequisites)

| Fact | Source | Effect |
|------|--------|--------|
| `SecureBootEnabled` | `WmiQueries.GetSecureBootEnabled()` (`null` = legacy BIOS / unknown) | `agent.json → anticheat.requireSecureBoot` and not `true` → `secureBootOff` |
| `TpmPresent` | `WmiQueries.GetTpmPresentAsync` | `anticheat.requireTpm` and not `true` → `tpmOff` |
| `VbsEnabled` | `HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\EnableVirtualizationBasedSecurity` | informational (debug log) |
| `HvciEnabled` | `...\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity\Enabled` | informational; `hvciOff` is **logged**, never returned as a failure |
| `TestSigningEnabled` | `bcdedit /enum {current}` parsed by `ParseTestSigning` (15 s timeout; yes/no in several locales) | `true` → `testSigningOn` (always a violation); unknown (`null`) is tolerated |

Results are cached for `SecureBootChecker.CacheTtl` (60 s) under a `SemaphoreSlim`; `CheckRuntimeAsync`
delegates to `CheckAsync`. Order of evaluation is Secure Boot → TPM → test-signing, and the first failure wins.

---

## 4. Check identifiers and severity

`AntiCheatChecks` (Contracts) is the closed vocabulary of `AntiCheatCheckResult.Reason` /
`AntiCheatReport.Check`; `AntiCheatMonitor.SeverityOf` maps it to `AntiCheatSeverity`:

| Check | Emitted by | Severity |
|-------|------------|----------|
| `driverMissing` | EAC, FACEIT, Vanguard | `warning` |
| `serviceStopped` | EAC, FACEIT, Vanguard | `warning` |
| `secureBootOff` | Vanguard (Win 11), SecureBoot | `warning` |
| `tpmOff` | Vanguard (Win 11), SecureBoot | `warning` |
| `hvciOff` | logged only | — |
| `testSigningOn` | SecureBoot | `critical` |
| `blockedProcess` | reserved (no checker emits it today) | `critical` |
| `injectedModule` | reserved | `critical` |
| `vmDetected` | reserved | `critical` |
| `debuggerAttached` | reserved | `critical` |

Severity only matters at runtime: a `critical` runtime finding additionally locks the session (section 6).
At launch every failure is handled the same way.

---

## 5. Launch-time gate

```
Shell (React)          Shell (Rust)        Agent: GameHandlers → GameLaunchService              AntiCheatMonitor
  │ games_launch ─────▶ │ games.launch ───▶ │ LaunchAsync(LaunchRequest)                            │
  │                     │                   │  1. session active?  (sessionNotActive)               │
  │                     │                   │  2. PolicyDenies?    (policyDenied processAllowlist)  │
  │                     │                   │  3. CheckAntiCheatAsync(game)                         │
  │                     │                   │     game.AntiCheat == none → OK, gate NOT called      │
  │                     │                   │     else IAntiCheatGate.CheckForLaunchAsync ─────────▶│ kinds = policy.required ∪ {game.AntiCheat}
  │                     │                   │                                                       │ (+ `none` platform checker first)
  │                     │                   │                                                       │ for each kind: checker.CheckAsync
  │                     │                   │                                                       │ failures → ReportAsync(phase=launch)
  │                     │                   │                                                       │ block? throw IpcError.AntiCheatBlocked
  │                     │                   │◀── AntiCheatCheckResult[] | IpcException ─────────────┤
  │                     │                   │  4. !ok && blockOnViolation → antiCheatBlocked         │
  │                     │◀── error ─────────┤     details { kind, reason }                          │
  │◀── toast + overlay ─┤                   │  ... lease, inject, launch, job, track, report        │
```

Step-by-step (`AntiCheatMonitor.CheckForLaunchAsync(Game game, CancellationToken)`):

1. `_kinds[game.Id] = game.AntiCheat` is remembered for the runtime loop.
2. The kind list is built from `policy.anticheat.required` (without `none`) plus `game.AntiCheat` when it is not
   `none` and not already listed.
3. If the list is non-empty, or `agent.json → anticheat.requireSecureBoot` / `requireTpm` is on, `AntiCheatKind.None`
   is inserted at position 0 so platform prerequisites run first.
4. For each kind, `Find(kind)` looks up the registered checker. No checker → skipped (debug log, treated as OK).
5. `checker.CheckAsync` is awaited. A checker that **throws** is treated as satisfied (`LogWarning`), i.e. the gate
   fails open on probe errors, not on findings. Cancellation propagates.
6. If there are failures, each one is reported (section 7) with `details = { gameId, title, phase: "launch" }` and
   `ActionTaken = blockedLaunch` when blocking, `none` otherwise.
7. `block = policy.anticheat.blockOnViolation` (default `true` when no policy is loaded). When blocking, the **first**
   failure becomes `IpcError.AntiCheatBlocked(kind, reason).ToException()`.

`GameLaunchService.LaunchAsync` catches the `IpcException`, releases nothing (no lease was taken yet), publishes
`game.stateChanged{failed, error}` and sends a `LaunchReport` with the failed `AntiCheatCheckResult` embedded
(`LaunchReport.AntiCheat`). The IPC response is:

```json
{ "error": { "code": "antiCheatBlocked", "message": "Anti-cheat check failed: secureBootOff",
             "details": { "kind": "vanguard", "reason": "secureBootOff" } } }
```

Important consequence of step "3" in the diagram: `GameLaunchService.CheckAntiCheatAsync` returns early for games
whose catalogue entry says `antiCheat: none`, so `policy.anticheat.required` and the Secure Boot / TPM settings
are only enforced for games that declare an anti-cheat. A club that wants Secure Boot on every launch must set
`antiCheat` on the games it cares about.

When `blockOnViolation` is `false`, the gate returns the results, `GameLaunchService` proceeds with the launch and
the failed check still lands in the launch report and the server report (`ActionTaken = none`).

---

## 6. Runtime monitoring

`AntiCheatMonitor.ExecuteAsync` runs only when an `IAntiCheatGameControl` is registered (it is, via
`AntiCheatGameControlAdapter`). Every `max(5, agent.json → anticheat.checkIntervalSec)` seconds (default 30 s)
`CheckRunningAsync` iterates `IAntiCheatGameControl.Running`:

| Condition | Result |
|-----------|--------|
| `RunningGame.State != Running` | skipped |
| `_kinds[gameId]` unknown or `none` | skipped (the game was launched without a kind) |
| No checker for the kind | skipped |
| `CheckRuntimeAsync(pid)` throws | logged, skipped this round |
| `!result.Ok` | `HandleRuntimeViolationAsync` |

`HandleRuntimeViolationAsync` deduplicates on `(pid, check)` in `_reported` so a persisting condition is acted upon
and reported **once** per process; the entry is dropped when the pid disappears from `Running`.

Actions, driven by `policy.anticheat.blockOnViolation`:

| `blockOnViolation` | Severity | Game | Session | `ActionTaken` |
|--------------------|----------|------|---------|---------------|
| `false` | any | untouched | untouched | `none` |
| `true` | `warning` | `KillAsync(pid, force: true)` | untouched | `killedGame` |
| `true` | `critical` | killed | `ISessionService.LockAsync("anticheat:<check>")` when `State.IsOpen()` | `lockedSession` (falls back to `killedGame` if the lock fails) |

Kill failures are logged and reporting still happens. The runtime report carries
`details = { gameId, title, pid, phase: "runtime" }`.

With the current checkers only `driverMissing`, `serviceStopped`, `secureBootOff`, `tpmOff` (all `warning`) and
`testSigningOn` (`critical`) can occur at runtime, so in practice a session lock happens when test-signing mode is
switched on while a protected game runs.

---

## 7. Reporting to the server

`AntiCheatMonitor.ReportAsync` builds one `AntiCheatReport`:

| Field | Value |
|-------|-------|
| `pcId` | `agent.json → pcId` (or `Guid.Empty` before registration) |
| `sessionId`, `userId` | from `ISessionService.Current` when a session exists |
| `gameId` | the game concerned |
| `kind`, `check`, `severity` | from the finding |
| `details` | camelCase JSON object built by `AntiCheatMonitor.Details(...)`; never contains credentials |
| `at` | `IClock.UtcNow` |
| `actionTaken` | see sections 5 and 6 |

Delivery, in order:

1. `IAgentEventSink.PublishAsync(AgentEvent.Of(AgentEventType.AnticheatViolation, now, report))` — the WebSocket
   `anticheatViolation` event (`SERVER_API.md` §6.2). The sink (`AgentEventPublisher`) queues it offline. Failures
   are logged and do not stop step 2.
2. If `agent.json → anticheat.reportViolations` (default `true`): `IServerClient.ReportAntiCheatAsync(report)` →
   `POST /anticheat/report` (`SERVER_API.md` §4.14, HMAC-signed like every request). `ServerApiException`,
   `HttpRequestException` and `TimeoutException` are logged as warnings.

Every report is also logged at `Warning`: `Anti-cheat violation {Kind}/{Check} ({Severity}), action {Action}`.

Independently of this, every launch (successful or not) sends `POST /games/{id}/launch-report` with the
`AntiCheatCheckResult` of the pre-launch check embedded, so the server sees "checked and OK" as well as failures.

---

## 8. Shell UX

The Shell has no anti-cheat logic of its own. `apps/shell/src/screens/Games/LaunchOverlay.tsx` renders the launch
steps `antiCheat → account → launcher → window` and, on an `antiCheatBlocked` error, `describeLaunchError` appends
the localized reason from `games.antiCheatReason.<reason>` (`apps/shell/src/i18n/{en,ru,uz}.json`):

| `details.reason` | English text |
|------------------|--------------|
| `driverMissing` | Anti-cheat driver is missing |
| `serviceStopped` | Anti-cheat service is not running |
| `secureBootOff` | Secure Boot is off |
| `tpmOff` | TPM is off |
| `testSigningOn` | Test signing mode is on |
| `hvciOff`, `blockedProcess`, `injectedModule`, `vmDetected`, `debuggerAttached` | translated, reserved |

The base message (`errors.antiCheatBlocked`) tells the player to call an administrator; the overlay offers the
`callAdmin` action. A runtime kill surfaces as `game.stateChanged{killed}`; a runtime lock surfaces as the normal
lock screen (`session.updated{locked}` with reason `anticheat:<check>`).

### 8.1 Vanguard reboot requirement

Riot Vanguard installs `vgk` as a boot-start driver: after the Riot Client installs or updates it, the driver is
registered but not loaded until the next boot, and Riot's own client refuses to start VALORANT / LoL until then.
`VanguardChecker.CheckAsync` reports this state as `serviceStopped` and logs
`Riot Vanguard driver (vgk) is installed but not loaded; a reboot is required`. What the player sees is the
generic "Anti-cheat service is not running — call an administrator" message.

Operator flow:

1. Confirm in `logs\agent-<date>.json` that the `vgk` line says "installed but not loaded".
2. End the session if one is open (a reboot kills it anyway), then reboot the PC (`power.reboot` from the admin
   panel, or the Shell's admin unlock → `sys.reboot`).
3. On Windows 11 also verify Secure Boot and TPM are enabled in firmware; otherwise the next attempt fails with
   `secureBootOff` / `tpmOff`.

Vanguard is therefore best pre-installed on the club image so the reboot happens once, during provisioning.

---

## 9. Known limitations

| Limitation | Detail |
|------------|--------|
| No BattlEye / Ricochet checkers | `AntiCheatKind.BattlEye` and `Ricochet` are accepted from the catalogue but no `IAntiCheatChecker` exists; the gate treats them as satisfied. Adding one is a 100-line class following `EacChecker` (BattlEye: service `BEService`, driver `BEDaisy`). |
| Kernel anti-cheats vs. kiosk lockdown | The kiosk user is a plain `BUILTIN\Users` member. Vendors that (re)install their driver from the launcher need an elevated installer; that must be done on the image by an administrator, not by the player. `AccountInjector` kills launcher processes before injection, so a vendor's first-run driver install triggered from inside a session will be interrupted. |
| Secure Boot on older PCs | `WmiQueries.GetSecureBootEnabled()` returns `null` on legacy BIOS boards. With `requireSecureBoot` on, `null` counts as off (`secureBootOff`). Vanguard on Windows 10 does not require it (`RequiresPlatformSecurity` is Windows 11 only). |
| Profile reset vs. vendor state | FACEIT and Riot keep per-user data under the kiosk profile; `ProfileResetService` wipes it between sessions, so first-launch bootstrap (and its 90 s grace) happens every session. |
| Fail-open on probe errors | A checker exception (WMI down, `bcdedit` missing) is treated as satisfied so a broken probe cannot take the whole games catalogue offline. Findings themselves never fail open. |
| `policy.anticheat.required` scope | Only enforced for games whose catalogue entry has `antiCheat != none` (section 5). |
| Signature check depth | `HasAuthenticodeSignature` only checks that a signature exists; it does not validate the chain or the signer identity. |
| Runtime loop granularity | Minimum interval is 5 s; a condition that appears and disappears between two ticks is not observed. |
| Test-signing detection | Relies on parsing `bcdedit` output; on systems where `bcdedit` fails (locked BCD store), the state is "unknown" and not treated as a violation. |

---

## 10. Troubleshooting

| Symptom | Where to look | Likely cause / fix |
|---------|---------------|--------------------|
| Every launch of an EAC game fails with `driverMissing` | agent log: `Easy Anti-Cheat service is not installed` | Run the game's `EasyAntiCheat_EOS_Setup.exe install <id>` as administrator on the image |
| EAC game killed ~2 min after start with `serviceStopped` | agent log warning `not running alongside game pid` | EAC bootstrap failed (often a vendor outage or the `start_protected_game` step never ran); check the game's own EAC log; lengthen nothing — 90 s grace is fixed |
| Vanguard: `serviceStopped` at launch | log: `installed but not loaded; a reboot is required` | Reboot (section 8.1) |
| Vanguard: `secureBootOff` / `tpmOff` on Windows 11 | `SecureBootChecker` debug line `Platform security: ...` | Enable Secure Boot / fTPM in UEFI; CSM must be off |
| FACEIT: `driverMissing` although installed | log: `driver ... is not signed` or `binary ... is missing` | Corrupt install; reinstall the FACEIT client as administrator |
| `testSigningOn` on every launch | `bcdedit /enum {current}` shows `testsigning Yes` | `bcdedit /set testsigning off` + reboot; check that no driver-dev tooling re-enables it |
| Launch blocked but the server shows no report | agent log `Anti-cheat report could not be sent` | Server unreachable; the WS event is queued offline; `anticheat.reportViolations` may be `false` |
| A game with anti-cheat launches even though the PC is misconfigured | `policies.json → anticheat.blockOnViolation` | It is `false`; findings are reported only |
| Secure Boot required but game with `antiCheat: none` still launches | section 5 | Expected: the gate only runs for games declaring an anti-cheat |
| Runtime checks never run | log at start: `Anti-cheat runtime checks disabled: no game control registered` | `IAntiCheatGameControl` not registered (custom host); the default DI registers `AntiCheatGameControlAdapter` |

---

## 11. Configuration keys

### `agent.json → anticheat` (`AntiCheatSettings`, `src/ClubShell.Core/Configuration/AppSettings.cs`)

| Key | Type / range | Default | Effect |
|-----|--------------|---------|--------|
| `checkIntervalSec` | int, 5–86400 | 30 | Runtime loop period (`AntiCheatMonitor.ExecuteAsync`) |
| `requireSecureBoot` | bool | false | `SecureBootChecker` fails with `secureBootOff` unless UEFI Secure Boot is on |
| `requireTpm` | bool | false | `SecureBootChecker` fails with `tpmOff` unless a TPM is present |
| `reportViolations` | bool | true | Send `POST /anticheat/report` (the WS event is always published) |

### `policies.json → anticheat` (`AntiCheatPolicy`, server-driven, `config/policies.example.json`)

| Key | Type | Effect |
|-----|------|--------|
| `required` | `AntiCheatKind[]` | Kinds checked on **every** launch of a game with an anti-cheat, in addition to the game's own kind |
| `blockOnViolation` | bool | `true`: refuse launches, kill games, lock sessions on critical findings. `false`: report only. Missing policy → `true` |

### Game catalogue (`Game.AntiCheat`, `GET /games`)

| Value | Effect |
|-------|--------|
| `none` | Gate not consulted, runtime loop skips the game |
| `eac` / `faceit` / `vanguard` | Corresponding checker at launch and at runtime |
| `battlEye` / `ricochet` | Accepted, currently unchecked (section 9) |

### Related

| Key | Doc |
|-----|-----|
| `games.launchTimeoutSec`, `games.killGraceSec` | `GAME_LAUNCHERS.md` |
| `POST /anticheat/report`, `anticheatViolation` | `SERVER_API.md` §4.14, §6.2 |
| `antiCheatBlocked` error envelope | `IPC_PROTOCOL.md` §5, §7.3 |
