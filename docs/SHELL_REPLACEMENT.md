# ClubShell — Shell replacement

Status: descriptive. How ClubShell makes `clubshell-shell.exe` the Windows shell of the kiosk account, keeps that
account provisioned, starts the Shell from the session-0 service, and puts everything back. Code:
`src/ClubShell.Windows/Registry/ShellRegistry.cs`, `src/ClubShell.Agent/Policy/ShellReplacementPolicy.cs`,
`src/ClubShell.Agent/Users/{TempUserProvisioner,ProfileResetService}.cs`,
`src/ClubShell.Windows/Users/{LocalUserManager,FolderRedirect,ProfileReset}.cs`,
`src/ClubShell.Windows/Session/ProcessAsUser.cs`, `src/ClubShell.Windows/Processes/ProcessLauncher.cs`,
`src/ClubShell.Agent/Watchdog/{ShellLauncher,ShellWatchdog,CrashRecovery}.cs`,
`src/ClubShell.Agent/Install/{install,register-service,uninstall}.ps1`.

---

## 1. Why a per-user shell, not HKLM

Winlogon reads the `Shell` value to decide what to start after logon. Two places exist:

| Location | Affects | ClubShell use |
|----------|---------|---------------|
| `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell` (`ShellRegistry.MachineWinlogonKey`) | **every** account, administrators included | only `ShellRegistry.SetCustomShell(null, exe)` — logged as a warning, never called by the policy module; reserved for dedicated single-purpose images |
| `HKU\<sid>\Software\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell` (`ShellRegistry.UserWinlogonKey`) | that user only | **default**: `ShellReplacementPolicyModule` writes it for the kiosk SID |
| `HKU\<sid>\Software\Microsoft\Windows\CurrentVersion\Policies\System\Shell` (`ShellRegistry.UserPoliciesSystemKey`) | that user only — the "Custom User Interface" policy that `userinit.exe` honours | written together with the value above, so the replacement holds whether userinit or winlogon resolves the shell |

Administrators keep `explorer.exe`; only the kiosk account boots into the Shell. `ShellRegistry.GetCurrentShell(sid)`
resolves in the same order Windows does: per-user policy value, per-user Winlogon value, then HKLM, then
`explorer.exe`.

Values written by `SetCustomShell(sid, exe)` are `REG_SZ` = absolute path of the Shell executable
(`policies.json → shellReplacement.shellExe`, default `C:\Program Files\ClubShell\Shell\clubshell-shell.exe`).
`RestoreExplorer(sid)` deletes both values; `RestoreExplorer(null)` rewrites HKLM to `explorer.exe`.

`ShellReplacementPolicyModule.ApplyAsync` refuses a `shellExe` that is not an absolute path to an existing file and
logs the executable's Authenticode signer (`DescribeSignature`) — presence only, chain trust is not validated.

---

## 2. Auto-logon

`ShellRegistry.SetAutoLogon(userName, password, domain = ".", count = null)`:

| HKLM `Winlogon` value | Written |
|-----------------------|---------|
| `AutoAdminLogon` | `"1"` |
| `DefaultUserName` | kiosk account name |
| `DefaultDomainName` | `"."` (local machine) |
| `DefaultPassword` | **deleted** — the clear-text registry value is never used |
| `AutoLogonCount` | set when `count` is given, otherwise deleted (unlimited) |
| LSA secret `DefaultPassword` (`ShellRegistry.DefaultPasswordSecret`) | password, via `LsaSecrets.Store` → `LsaOpenPolicy(POLICY_CREATE_SECRET)` + `LsaStorePrivateData` |

Winlogon reads the LSA secret exactly like the registry value, but the secret is only readable by SYSTEM (and
administrators through LSA), which matters because the kiosk password rotates and is otherwise unknown to anyone.
`ClearAutoLogon()` sets `AutoAdminLogon = 0`, deletes `DefaultPassword` / `AutoLogonCount` and stores a `null`
secret (deletes it).

Who calls it:

- `ShellReplacementPolicyModule.ApplyAsync` after setting the shell (needs `IKioskCredentials.Password`; fails the
  section with "auto-logon deferred" until the account is provisioned, which `PolicyEnforcer` retries).
- `TempUserProvisioner.RefreshAutoLogon` after every password rotation, **only if** auto-logon is enabled and
  `DefaultUserName` equals the kiosk account — an operator's own auto-logon is never overwritten.
- `RevertAsync` / `uninstall.ps1` clear it.

Fast user switching, "Switch user" and lock screens are handled by the explorer lockdown (`KIOSK_MODE.md` §3).

---

## 3. Kiosk account provisioning (`TempUserProvisioner`)

Registered as the **first** hosted service (`DependencyInjection.AddHostedServices`) so the SID exists before the
pipe DACL, the Shell-token ACL and the watchdog need it.

```
StartAsync (≤ StartupTimeout 60 s) ─▶ ProvisionAsync
   │
   ├─ !Exists(name) && createIfMissing ─▶ LocalUserManager.Create(name, GeneratePassword(), …)
   │       PasswordNeverExpires, UserCannotChangePassword, Enabled, member of BUILTIN\Users only
   │
   ├─ exists ─▶ reuse stored password iff  !rotatePasswordOnStart ∧ age < RotationPeriod (7 d) ∧ CanLogon(name, pw)
   │            else SetPassword(GeneratePassword()); SetEnabled(true); AddToGroup(BUILTIN\Users)
   │
   ├─ sid = GetSid(name)
   ├─ SaveAsync → secure\kiosk.cred  (JSON {userName, password, sid, rotatedAt} wrapped by ITokenProtector = DPAPI LocalMachine)
   ├─ RefreshAutoLogon(record)        (only when auto-logon already points at this user)
   ├─ ApplyFolderRedirect()           (deferred until the profile exists)
   └─ CredentialsChanged event        (IKioskCredentials: UserName, Password, Sid)
```

| Property | Value |
|----------|-------|
| Account name | `agent.json → shell.kioskUser.name` (default `club` in code, `config/agent.default.json`, `install.ps1 -KioskUser` and the MSI `KIOSKUSER` property; `install.ps1` writes it into `agent.json`) |
| Password | `LocalUserManager.GeneratePassword()` — 24 cryptographically random characters, one of each class, no ambiguous glyphs |
| Rotation | at every Agent start when `rotatePasswordOnStart` (default true); otherwise when older than `RotationPeriod` or when the stored one no longer logs on (`ProcessAsUser.LogonUser` probe); `RotatePasswordAsync` on demand |
| Group membership | `LocalUserManager.AddToGroup` refuses `BUILTIN\Administrators`; the account is a plain user |
| Storage | `C:\ProgramData\ClubShell\secure\kiosk.cred` (`TempUserProvisioner.CredentialsFileName`), DPAPI-protected, `secure\` is SYSTEM/Administrators only |
| Exposure | `IKioskCredentials` in memory for the policy module; never logged, never sent to the Shell or the server |
| Failure | logged, host keeps starting; `AgentWorker` retries while `IsProvisioned` is false |

### 3.1 Folder redirection

`TempUserProvisioner.ApplyFolderRedirect` → `FolderRedirect.Apply(sid, target, ntUserDat)` rewrites
`HKU\<sid>\...\Explorer\User Shell Folders` (and the cached `Shell Folders`) for Desktop, Documents, Downloads,
Pictures, Videos and Saved Games to `<root>\ClubShell\Users\<user>\<folder>`, creating the directories with an ACL
of SYSTEM / Administrators / the user. `<root>` is `RedirectRoot` or, by default, the largest non-system fixed drive
(`DetectDataRoot`); no such drive → redirection skipped. It works on the live hive or on `NTUSER.DAT` loaded through
`RegistryHelper.LoadUserHive` when the user is logged off, and is deferred (returns `false`) until the profile has
been created by the first logon.

### 3.2 Profile reset

`ProfileResetService` (hosted) subscribes to `ISessionService.Changed` and, on `SessionEventType.Ended` with
`shell.kioskUser.resetProfileOnLogout`, waits 2 s and calls `RequestResetAsync("sessionEnded")`:

| Guard | Effect |
|-------|--------|
| a reset is already running | skipped |
| `Cooldown` (5 min) since the last reset | skipped |
| a session is open (`ISessionService.State.IsOpen()`) | skipped |

Reset sequence: `IShellRelauncher.StopAsync` (Shell stopped, watchdog `Suspended`) → `WtsSessions.Logoff` for every
session of the kiosk user (up to 4 rounds) → `ProfileReset.ResetProfile(user)`: wait for hive unload, then
`DeleteProfileW` → WMI `Win32_UserProfile.Delete` → manual directory + `ProfileList` removal (3 attempts each) →
`IShellRelauncher.RelaunchAsync` (watchdog resumes; auto-logon recreates the session and a fresh profile) →
`ApplyFolderRedirect` (best effort, retried by the provisioner). The marker
`cache\profile-reset.marker` records the last reset. `ProfileReset.CleanCaches` is the light variant used by
`SessionCleanup` while the user stays logged on.

---

## 4. Session 0 versus the interactive session

```
Session 0 (services, no desktop the user can see)         Session N (console, kiosk user, winsta0\default)
┌──────────────────────────────────────────────┐          ┌──────────────────────────────────────────────┐
│ ClubShellAgent.exe  (NT AUTHORITY\SYSTEM)    │          │ winlogon → userinit → Shell = clubshell-shell │
│  • cannot show windows to the player         │          │ clubshell-shell.exe (kiosk user token)        │
│  • cannot install WH_KEYBOARD_LL for session N│  named  │ games (kiosk user token, job object)          │
│  • cannot read GetLastInputInfo of session N │◀──pipe──▶│                                              │
│  • CAN: WTS enumerate/logoff, load NTUSER.DAT,│          │ explorer.exe only in safe mode                │
│    CreateProcessAsUser into session N,        │          │                                              │
│    registry/firewall/services, LSA secrets    │          │                                              │
└──────────────────────────────────────────────┘          └──────────────────────────────────────────────┘
```

Consequences that shape the code:

| Need | Solution |
|------|----------|
| Find the kiosk session | `WtsSessions.FindByUser(name)` with `IsActive` (`ShellLauncher.ActiveSessionId`, `IKioskSessionLocator`) |
| Know when it appears / disappears | `SessionChangeWatcher` (`WTSRegisterSessionNotification(NOTIFY_FOR_ALL_SESSIONS)` on a message-only window; polling fallback) → `ShellWatchdog.OnSessionChanged` |
| Run something the player sees | `ProcessAsUser.CreateProcessAsUser` with the user's token (section 5); `RunAsSystemInSession` (SYSTEM token re-targeted with `TokenSessionId`) exists only for the fallback overlay, never for player-facing programs |
| Write the user's registry while logged off | `RegistryPolicyOps.WithUserHive` loads `NTUSER.DAT` under `HKU\ClubShell-Kiosk` |
| Delete the user's Credential Manager entries | `AccountInjector.ClearCredentialManagerAsync` impersonates the session token |
| Input / focus / idle facts | owned by the Shell (`KIOSK_MODE.md` §2); the Agent's `IdleMonitorAdapter` is a documented approximation |

---

## 5. `CreateProcessAsUser` flow

`ShellLauncher.LaunchAsync` (and every game launch through `LauncherBase`) goes through
`ProcessLauncher.LaunchAsync(ProcessStartSpec { SessionId = N })` → `LaunchAsUser`:

```
ProcessAsUser.GetUserToken(sessionId)
   │  PrivilegeScope(SeTcbPrivilege)                — enabled only for this call, restored on dispose
   │  WTSQueryUserToken(sessionId)                  — primary token of the logged-on user (LocalSystem only)
   │  DuplicateTokenEx(MAXIMUM_ALLOWED, TokenPrimary)
   ▼
EnvironmentBlock.Build(token, extra)
   │  CreateEnvironmentBlock(token, inherit=false)  — the user's own block (USERPROFILE, APPDATA, PATH, …)
   │  + extra variables (Shell: CLUBSHELL_PIPE, CLUBSHELL_SHELL_TOKEN, CLUBSHELL_LOCALE; games: launcher env)
   │  → sorted, double-NUL-terminated UTF-16 block
   ▼
ProcessAsUser.CreateProcessAsUser(token, exe, args, workingDir, envBlock, desktop = "winsta0\default",
                                  flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE [| CREATE_NO_WINDOW when Hidden])
   │  PrivilegeScope(SeAssignPrimaryTokenPrivilege), PrivilegeScope(SeIncreaseQuotaPrivilege)
   │  STARTUPINFOW.lpDesktop = winsta0\default; command line from ProcessLauncher.BuildCommandLine (quoted exe)
   │  thread handle closed, process handle returned as SafeProcessHandle
   ▼
LaunchedProcess(Pid, Handle, Exe, StartedAt)  →  spec.Job?.Assign(handle); SetPriorityClass when requested
```

Shell specifics (`ShellLauncher`):

| Item | Value |
|------|-------|
| Executable | `agent.json → shell.exePath`; missing file → error logged, no launch |
| Working dir | the Shell install directory |
| Environment | `CLUBSHELL_PIPE` = `ipc.pipeName`, `CLUBSHELL_SHELL_TOKEN` = absolute path of `secure\shell.token`, `CLUBSHELL_LOCALE` = `ShellLauncher.Locale` |
| Job object | `JobObject.Create(null, killOnClose: !KeepShellOnAgentExit)` — by default the Shell **survives** an Agent restart |
| Idempotence | one tracked process; `LaunchAsync` returns the current one when alive, `null` when no active kiosk session |
| Stop | `LaunchedProcess.CloseAsync(StopGrace = 5 s)`: `WM_CLOSE` to top-level windows, then `TerminateProcess` |
| Also provides | `IKioskSessionLocator` (`ActiveSessionId`, `KioskUser`), `IKioskProfilePaths` (`UserProfile` from `ProfileList\<sid>\ProfileImagePath`, `LocalAppData`, `RoamingAppData`), `IShellRelauncher` (suspend/resume) |

Games differ only in the spec: `Env` from the launcher, `WorkingDir` from `LaunchCommand`, and the per-launch
`JobObject` created by `GameLaunchService` after the real game pid is known (`GAME_LAUNCHERS.md` §8).

---

## 6. Watchdog restart policy

`ShellWatchdog.ExecuteAsync` loop (period `shell.watchdogIntervalMs`, min 500 ms, woken early by session events):

```
        ┌───────────────────────────────────────────────────────────────────────┐
        │ RestartsSuspended?  ──yes──▶ Suspended (profile reset / Shell update)  │
        │ ActiveSessionId null? ─yes─▶ WaitingForSession                         │
        │ Shell running?  ──no──▶ safe mode? ─yes─▶ SafeMode (wait for logon)    │
        │                          │ pendingDelay > 0 ─▶ Restarting, Task.Delay │
        │                          ▼                                            │
        │                       Launching ─▶ LaunchAsync ─▶ Running             │
        │ MonitorAsync: WaitForExitAsync  ∥  MonitorHealthAsync (pipe connected within HealthTimeout 60 s?)
        │        exit ─▶ HandleExitAsync            not connected ─▶ StopAsync ─▶ HandleExitAsync
        └───────────────────────────────────────────────────────────────────────┘
```

`CrashRecovery.Decide(exitCode, uptime, graceful)`:

| Condition | `RecoveryAction` | Watchdog state |
|-----------|------------------|----------------|
| graceful stop (not used by the watchdog today: every exit is `graceful: false`) | `RestartImmediately` | `Restarting` |
| crashes in the last 60 s ≥ `shell.maxRestartsPerMinute` (default 5) | `SafeMode` | `SafeMode`: `ShellLauncher.LaunchExplorerAsync` starts `%WINDIR%\explorer.exe` in the kiosk session; relaunches paused until a `Logon` / `ConsoleConnect` / `SessionCreate` / `Unlock` event resets the window |
| uptime < 10 s (`ShortRunThreshold`) | `RestartAfter(shell.restartDelayMs)` (default 1500 ms) | `Restarting` |
| otherwise | `RestartImmediately` | `Restarting` |

Every exit produces a `CrashReport` (exit code, uptime, last 50 log lines, WER dump found under `logs\`),
published as telemetry `shellCrash` (+ `shellCrashLoop` on safe mode) through `TelemetryBus`, and appended to
`C:\ProgramData\ClubShell\crash-history.json` (last 50 entries). `CrashRecovery.Reset()` clears the window on a new
logon so a new user is not penalised for the previous one's crash loop.

Related timers: `PipeServer.HelloTimeout` (5 s to send `auth.hello`), heartbeat kill after
`ipc.heartbeatIntervalSec × ipc.missedHeartbeatsBeforeKill` (default 15 s, `KillShellOnHeartbeatLoss`), SCM
recovery for the Agent itself (`register-service.ps1`: `sc failure … restart/5000/restart/10000/restart/30000`,
reset after 86400 s).

---

## 7. Restoring explorer

| Path | What happens |
|------|--------------|
| Policy `shellReplacement.enabled = false` (server or `policies.json`) | `ShellReplacementPolicyModule.ApplyAsync` → `ShellRegistry.RestoreExplorer(sid)`; auto-logon untouched; effective at the next logon of the kiosk user |
| `PolicyEnforcer.RevertAllAsync` (Agent stopping with revert, or policy removed; each module's `RevertAsync` in reverse order) | `RestoreExplorer(sid)` + `ClearAutoLogon()` |
| `kiosk_exit{action: "explorer"}` from the admin unlock (`KIOSK_MODE.md` §6) | `explorer.exe` spawned by the Shell before it exits — temporary; the watchdog relaunches the Shell unless suspended |
| Safe mode | `ShellLauncher.LaunchExplorerAsync` — temporary, same as above |
| `src/ClubShell.Agent/Install/uninstall.ps1` | stops/deletes the service (`register-service.ps1 -Uninstall`), sets `AutoAdminLogon = 0`, removes `DefaultUserName` / `DefaultPassword` / `DefaultDomainName` / `AutoLogonCount`, deletes `HKU\<kioskSid>\...\Winlogon\Shell` when the hive is loaded, removes `ClubShell-*` firewall rules and the hosts-file block; `-PurgeData` removes `C:\ProgramData\ClubShell`, `-RemoveKioskUser` deletes the account. Note it does **not** delete the LSA `DefaultPassword` secret nor the `Policies\System\Shell` value; run the manual steps below when the service is already gone |

Manual restore (service not running, hive loaded because the kiosk user is logged on):

```
reg delete "HKU\<sid>\Software\Microsoft\Windows NT\CurrentVersion\Winlogon" /v Shell /f
reg delete "HKU\<sid>\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v Shell /f
reg add    "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v AutoAdminLogon /t REG_SZ /d 0 /f
```

If the user is logged off: `reg load HKU\Tmp C:\Users\<kiosk>\NTUSER.DAT`, delete the same two values under
`HKU\Tmp`, `reg unload HKU\Tmp`. HKLM `Winlogon\Shell` is untouched by ClubShell unless someone called the
machine-wide variant; verify it still reads `explorer.exe`.

---

## 8. Windows version notes

| Topic | Note |
|-------|------|
| Supported | Windows 10 / 11 x64, Pro or Home (`ARCHITECTURE.md` §1). Per-user `Shell` values work on every edition |
| Windows 11 | Vanguard requires Secure Boot + TPM (`VanguardChecker.RequiresPlatformSecurity`); `WDA_EXCLUDEFROMCAPTURE` needs 10 2004+ (unused by default) |
| Assigned Access (kiosk mode / multi-app kiosk) | **Not used.** Assigned Access runs a single UWP app or Edge, or a multi-app configuration from a provisioning XML that pins a fixed set of Win32 apps behind a restricted Start layout — it cannot host a custom Win32 shell, does not allow a service to start arbitrary games in the session under a job object, and its multi-app form needs Pro/Enterprise plus MDM/provisioning tooling. ClubShell needs a custom shell, dynamic launches, credential injection and hooks, none of which fit that model |
| Shell Launcher v2 (`WEKF`/`ShellLauncher` WMI bridge, Enterprise/Education/IoT) | Not used for the same reason plus edition availability; the plain `Winlogon\Shell` mechanism gives the same result on all editions and needs no feature installation |
| `Policies\System\Shell` | Some builds honour only the `Winlogon\Shell` value, others only the policy value when both exist; writing both (`SetCustomShell`) covers every case |
| `NTUSER.DAT` loading | `RegistryPolicyOps.WithUserHive` requires the profile to exist (first logon done); before that the module fails the section with "deferred" and `PolicyEnforcer` retries on the next apply |
| Explorer started by a game or installer | the Shell's `Taskbar` re-hides the tray windows every 5 s; `ProcessAllowlist` can deny `explorer.exe` by pattern (not in the example policy) |
| Windows updates | `AutoAdminLogon` survives feature updates; the LSA secret does too. Re-check after in-place upgrades (section 9 verification) |

---

## 9. Manual setup and verification

Prerequisites: Administrator PowerShell, published Agent payload, server URL and club API key.

1. Install the Agent:
   `.\install.ps1 -ServerUrl https://club.example.uz -ClubApiKey <key>`
   (creates `C:\ProgramData\ClubShell` with the §9 ACLs, writes `agent.json`, registers `ClubShellAgent` as
   LocalSystem delayed-auto with recovery, starts it).
2. Install the Shell MSI to `C:\Program Files\ClubShell\Shell` (or point `agent.json → shell.exePath` and
   `policies.json → shellReplacement.shellExe` elsewhere).
3. Make sure the server policy (or `C:\ProgramData\ClubShell\policies.json` while offline) has
   `shellReplacement.enabled = true` and the correct `shellExe`.
4. Watch `logs\agent-<date>.json`:
   - `Kiosk user club provisioned (created True, password rotated True)`
   - `Shell token written to ...\secure\shell.token (kiosk read: S-1-5-21-…)`
   - `Policy v… applied … changed=[shellReplacement, explorer, …]`; if `shellReplacement` reports "deferred", log the
     kiosk user on once (or reboot) so the profile and hive exist, then trigger `policy.reload`.
5. Reboot. Expected: auto-logon into the kiosk account, no explorer, the Shell full-screen, log line
   `Shell launched as pid … in session 1`, then `auth.hello` accepted.

Verification commands (as administrator, from another account or via remote PowerShell):

| Check | Command | Expected |
|-------|---------|----------|
| Auto-logon | `Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' \| Select AutoAdminLogon, DefaultUserName, DefaultPassword` | `1`, kiosk name, no `DefaultPassword` property |
| Per-user shell | `Get-ItemProperty "Registry::HKEY_USERS\<sid>\Software\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name Shell` | the Shell exe path |
| Policy shell | same under `...\Windows\CurrentVersion\Policies\System` | same path |
| HKLM shell untouched | `Get-ItemProperty 'HKLM:\...\Winlogon' -Name Shell` | `explorer.exe` |
| Session | `query session` | kiosk user on the console session, state `Active` |
| Shell process | `Get-Process clubshell-shell \| Select Id, SessionId` | one process, `SessionId` = console session |
| Explorer absent | `Get-Process explorer -ErrorAction SilentlyContinue` | nothing (unless an admin is logged on) |
| Group | `Get-LocalGroupMember Users` / `Administrators` | kiosk user in `Users` only |
| Credentials file | `Get-Acl C:\ProgramData\ClubShell\secure\kiosk.cred` | SYSTEM / Administrators only |
| Shell token | `Get-Acl C:\ProgramData\ClubShell\secure\shell.token` | + kiosk user `Read` |
| Watchdog | kill `clubshell-shell.exe` | relaunched; `crash-history.json` gets an entry |
| Revert | set `shellReplacement.enabled=false`, `policy.reload`, log off/on | explorer desktop for the kiosk user |

Uninstall: `.\uninstall.ps1 [-PurgeData] [-RemoveKioskUser]`, then delete the two per-user `Shell` values manually if
the kiosk hive was not loaded at the time (section 7).
