# ClubShell — Game launchers

Status: descriptive. Documents `src/ClubShell.Agent/Games/**` as implemented. The IPC surface is
`IPC_PROTOCOL.md` §7.3 (`games.list`, `games.get`, `games.launch`, `games.kill`, `games.running`,
`games.installStatus`); the server side is `SERVER_API.md` §4.6.

---

## 1. Components

| File | Symbol | Role |
|------|--------|------|
| `src/ClubShell.Core/Abstractions/IGameLauncher.cs` | `IGameLauncher`, `LaunchContext` | Backend contract, per-launch session context |
| `src/ClubShell.Agent/Games/Launchers/ExeLauncher.cs` | `LauncherBase`, `LaunchCommand`, `ExeLauncher` | Shared plumbing (install check, `CreateProcessAsUser`, game-process discovery, tree kill) + plain-exe backend |
| `.../Launchers/SteamLauncher.cs` … `UbisoftLauncher.cs` | `SteamLauncher`, `EpicLauncher`, `BattleNetLauncher`, `RiotLauncher`, `EaLauncher`, `UbisoftLauncher` | Store backends: only `BuildCommand` (+ `ExpectedProcessNames`) |
| `src/ClubShell.Agent/Games/GameDetector.cs` | `GameDetector`, `VdfNode` (internal) | Install detection per launcher, launcher exe resolution, 60 s cache |
| `src/ClubShell.Agent/Games/GameLibrary.cs` | `GameLibrary` | Catalogue (`GET /games`, `cache\games.json`) + install status |
| `src/ClubShell.Agent/Games/GameLaunchService.cs` | `GameLaunchService` | Orchestrates `games.launch` / `games.kill` / `games.running` |
| `src/ClubShell.Agent/Games/GameSessionTracker.cs` | `GameSessionTracker`, `GameLaunchRecord` | Exit watch, cleanup, exit report, job disposal |
| `src/ClubShell.Agent/Games/Accounts/AccountPool.cs` | `AccountPool`, `ActiveLease` | Server leases, secret decryption, expiry watchdog |
| `src/ClubShell.Agent/Games/Accounts/AccountInjector.cs` | `AccountInjector`, `ILauncherCredentialStrategy`, `InjectionResult`, `IKioskProfilePaths` | Credential injection + restore + Credential Manager cleanup |
| `src/ClubShell.Agent/Games/Saves/CloudSaveSync.cs` | `CloudSaveSync` | Cloud-save download before launch / upload after exit |
| `src/ClubShell.Agent/Ipc/Handlers/GameHandlers.cs` | `GameHandlers` | Registers the `games.*` IPC handlers |
| `src/ClubShell.Windows/Processes/*.cs` | `ProcessLauncher`, `ProcessWatcher`, `ProcessKiller`, `JobObject` | Win32 layer |

DI (`DependencyInjection.AddGamesSubsystem`): one `AddSingleton<IGameLauncher, X>()` per backend; `GameLaunchService`
indexes them by `IGameLauncher.Launcher` (`Dictionary<LauncherType, IGameLauncher>`, first registration wins).

---

## 2. The launcher contract

```csharp
public interface IGameLauncher
{
    LauncherType Launcher { get; }
    Task<bool> IsAvailableAsync(CancellationToken ct);                       // launcher client present
    Task<GameInstallStatus> GetInstallStatusAsync(Game game, CancellationToken ct);
    Task<LaunchResult> LaunchAsync(Game game, LaunchRequest request, AccountLease? lease, LaunchContext context, CancellationToken ct);
    Task KillAsync(int pid, bool force, CancellationToken ct);
}

public sealed record LaunchContext(int WtsSessionId, string UserName, IReadOnlyDictionary<string, string> Env, Resolution? Resolution);
```

`LauncherBase` implements everything except `BuildCommand`:

```csharp
protected abstract LaunchCommand? BuildCommand(Game game, LaunchRequest request, LaunchContext context);
protected virtual IReadOnlyList<string> ExpectedProcessNames(Game game);   // default: file name of game.ExePath
public sealed record LaunchCommand(string Exe, string? Args, string? WorkingDir, bool WaitForGameProcess = true);
```

`LauncherBase.LaunchAsync` pipeline:

| Step | Detail | Failure → `LaunchResult.Failure(...)` |
|------|--------|---------------------------------------|
| 1 | `GameDetector.DetectAsync(game)` | `gameNotInstalled` |
| 2 | `BuildCommand(resolved, request, context)` | `IpcException` from validation; `null` → `gameLaunchFailed{stage: "launcher"}` ("client is not installed") |
| 3 | `SplitEnv`: removes `CLUBSHELL_LAUNCHER_ARGS` (`LauncherBase.LauncherArgsEnvKey`) from the env block and puts its value **before** the command's own args | — |
| 4 | Snapshot of already-matching pids in the kiosk session (`SnapshotMatchingPids`) so a pre-existing process is never mistaken for the new game | — |
| 5 | `ProcessLauncher.LaunchAsync(ProcessStartSpec{Exe, Args, WorkingDir, Env, SessionId})` → `CreateProcessAsUser` in the kiosk WTS session (`SHELL_REPLACEMENT.md` §5) | `gameLaunchFailed{stage: "start"}` |
| 6 | `WaitForGameProcess == false` (Exe) → success with the launched pid | — |
| 7 | `WaitForGameProcessAsync` until the real game process appears or `LaunchTimeoutSec` elapses | `gameLaunchFailed{stage: "wait", exitCode?}` |

Credentials never enter `LaunchContext.Env`; the only injected environment key is the launcher-arguments carrier,
which is stripped before the process is created.

---

## 3. Per-launcher reference

### 3.1 Steam (`LauncherType.Steam`)

| Aspect | Implementation |
|--------|----------------|
| Launcher exe | `agent.json → games.launchers.steam.exePath`; else `HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath` / `HKLM\SOFTWARE\Valve\Steam\InstallPath` + `steam.exe`; else `%ProgramFiles(x86)%\Steam\steam.exe` (`GameDetector.ResolveLauncherExe`, `SteamRoot`) |
| Install detection | `GameDetector.DetectSteam`: for every library in `steamapps\libraryfolders.vdf` (parsed by `VdfNode`), read `steamapps\appmanifest_<appid>.acf`; `StateFlags & 4` must be set (fully installed); install dir = `steamapps\common\<installdir>`, size from `SizeOnDisk`, version from `buildid` |
| `launcherAppId` | numeric app id (validated: digits only → `validation{launcherAppId}`) |
| Command | `steam.exe [-login <user> <pass>] -applaunch <appid> [game.Args] [extraArgs]` |
| Game process | `ExpectedProcessNames` default = file name of `game.ExePath`; when the catalogue has no `exePath`, any non-helper process whose image lives under the install dir |
| Credential injection | `SteamStrategy`: `-login user pass` on the command line (through `CLUBSHELL_LAUNCHER_ARGS`), and `<steam>\config\loginusers.vdf` patched so every stored user has `RememberPassword=0`, `MostRecent=0`, `AllowAutoLogin=0` (backed up, restored after exit) |
| Cleanup | launcher processes `steam.exe`, `steamwebhelper.exe` killed before injection and after exit; no Credential Manager filter |
| Save dir default | `%USERPROFILE%\Saved Games\{title}` |

### 3.2 Epic Games (`LauncherType.Epic`)

| Aspect | Implementation |
|--------|----------------|
| Launcher exe | configured path, else `%ProgramFiles(x86)%\Epic Games\Launcher\Portal\Binaries\Win32\EpicGamesLauncher.exe` |
| Install detection | `DetectEpic`: every `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item` (JSON); matches `AppName`, `CatalogNamespace:CatalogItemId` or `CatalogItemId` against `launcherAppId`; skips `bIsIncompleteInstall`; `InstallLocation`, `InstallSize`, `AppVersionString` |
| Command | `EpicGamesLauncher.exe [-AUTH_LOGIN=… -AUTH_PASSWORD=… -AUTH_TYPE=password] com.epicgames.launcher://apps/<id>?action=launch&silent=true` |
| Arguments | the URL cannot carry game arguments; `game.Args` / `extraArgs` are ignored (debug log) |
| Game process | as Steam (expected exe name or install-dir fallback) |
| Credential injection | `EpicStrategy`: `-AUTH_LOGIN/-AUTH_PASSWORD/-AUTH_TYPE=password` launch args; `%LOCALAPPDATA%\EpicGamesLauncher\Saved\Config\Windows\GameUserSettings.ini` stripped of `[RememberMe] Data=/Enable=`; then `extra.files` |
| Cleanup | kills `EpicGamesLauncher.exe`, `EpicWebHelper.exe`; Credential Manager filter `Epic*` |
| Save dir default | `%LOCALAPPDATA%\{title}\Saved` |

### 3.3 Battle.net (`LauncherType.BattleNet`)

| Aspect | Implementation |
|--------|----------------|
| Launcher exe | configured, else `%ProgramFiles(x86)%\Battle.net\Battle.net.exe`, else `Battle.net Launcher.exe` |
| Install detection | `DetectBattleNet`: `FindUninstallEntry` over `HKLM\...\Uninstall` (32/64-bit) by `DisplayName` = title or the product's display names (`GameDetector.BattleNetProducts`: `wow`, `pro`, `d4`, `hs`, `s2`, `odin`, …), publisher hint `Blizzard`; then `<libraryRoot>\<name>` |
| `launcherAppId` | Battle.net product code (alphanumeric/underscore, e.g. `pro`, `wow`, `fenris`) |
| Command | `Battle.net.exe --exec="launch <code>"` |
| Game process | `BattleNetLauncher.ProductExes` maps the code to known image names (`Overwatch`, `Wow`, `Diablo IV`, `SC2_x64`, `cod`, …) in addition to `game.ExePath` |
| Credential injection | `BattleNetStrategy`: `%APPDATA%\Battle.net\Battle.net.config` → `Client.SavedAccountNames = <username>` (pre-fills the login form); session files only from `extra.files`; without them a warning is logged and the player must type the password |
| Cleanup | kills `Battle.net.exe`, `Battle.net Launcher.exe`, `Battle.net Helper.exe`; Credential Manager filters `Battle.net*`, `Blizzard*` |
| Save dir default | `%USERPROFILE%\Documents\{title}` |

### 3.4 Riot (`LauncherType.Riot`)

| Aspect | Implementation |
|--------|----------------|
| Launcher exe | configured; else `%ProgramData%\Riot Games\RiotClientInstalls.json` → `rc_live` / `rc_default`; else `<systemdrive>\Riot Games\Riot Client\RiotClientServices.exe` (`RiotClientDir`) |
| Install detection | `DetectRiot`: `%ProgramData%\Riot Games\Metadata\<product>.*\*.product_settings.yaml` → `product_install_full_path`; fallback uninstall entry (publisher `Riot`) with `GameDetector.RiotProducts` display names |
| `launcherAppId` | `valorant`, `league_of_legends`, `bacon`/`lor` |
| Command | `RiotClientServices.exe --launch-product=<id> --launch-patchline=<live\|…> [extraArgs]`; the patchline comes from `game.Args` when it starts with `--launch-patchline=` (alphanumeric), default `live` |
| Game process | `RiotLauncher.ProductExes`: `VALORANT-Win64-Shipping`/`VALORANT`, `LeagueClient`/`League of Legends`, `LoR` |
| Credential injection | `RiotStrategy`: **no password launch option exists**. The lease must carry session data: `extra.privateSettingsYaml` → `%LOCALAPPDATA%\Riot Games\Riot Client\Data\RiotGamesPrivateSettings.yaml`, `extra.clientPrivateSettingsYaml` → `RiotClientPrivateSettings.yaml`, and/or `extra.files`. Nothing written → backups restored and `accountPoolExhausted` ("Riot lease carries no session data") |
| Cleanup | kills `RiotClientServices.exe`, `RiotClientUx.exe`, `RiotClientUxRender.exe`, `RiotClientCrashHandler.exe`; Credential Manager filter `Riot*` |
| Anti-cheat | VALORANT requires Vanguard (`ANTICHEAT.md` §3.4) |
| Save dir default | `%LOCALAPPDATA%\Riot Games\{title}` |

### 3.5 EA app (`LauncherType.Ea`)

| Aspect | Implementation |
|--------|----------------|
| Launcher exe | configured; else `HKLM\SOFTWARE\Electronic Arts\EA Desktop\InstallLocation` + `EA Desktop\EADesktop.exe`; else `%ProgramFiles%\Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe` |
| Install detection | `DetectEa`: sub-keys of `HKLM\SOFTWARE\[WOW6432Node\]EA Games` and `Electronic Arts` matching the title or `launcherAppId` → `Install Dir` / `DisplayVersion`; fallback uninstall entry (publisher `Electronic Arts`) |
| `launcherAppId` | EA offer id |
| Command | `EADesktop.exe origin2://game/launch?offerIds=<id>&autoDownload=1`; game arguments ignored |
| Credential injection | `EaStrategy`: writes a hint file `%LOCALAPPDATA%\Electronic Arts\EA Desktop\clubshell-account.json` (`username`, `gameId`, `leasedAt`) and `extra.files`; without session files the EA app shows its login form (warning) |
| Cleanup | kills `EADesktop.exe`, `EALocalHostSvc.exe`, `EACefSubProcess.exe`; Credential Manager filters `EA*`, `Origin*`, `Electronic Arts*` |
| Save dir default | `%USERPROFILE%\Documents\{title}` |

### 3.6 Ubisoft Connect (`LauncherType.Ubisoft`)

| Aspect | Implementation |
|--------|----------------|
| Launcher exe | configured; else `HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\InstallDir` + `UbisoftConnect.exe` / `upc.exe`; else `%ProgramFiles(x86)%\Ubisoft\Ubisoft Game Launcher\UbisoftConnect.exe` |
| Install detection | `DetectUbisoft`: `HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\<id>\InstallDir`; fallback uninstall entry (publisher `Ubisoft`) |
| `launcherAppId` | numeric Ubisoft game id (> 0) |
| Command | `UbisoftConnect.exe uplay://launch/<id>/0`; game arguments ignored |
| Credential injection | `UbisoftStrategy`: `%LOCALAPPDATA%\Ubisoft Game Launcher\settings.yml` → `user:` / `  login: <username>` (inserted or replaced); `extra.files` |
| Cleanup | kills `UbisoftConnect.exe`, `upc.exe`, `UplayWebCore.exe`, `UbisoftGameLauncher.exe`; Credential Manager filters `Ubisoft*`, `Uplay*` |
| Save dir default | `%USERPROFILE%\Documents\{title}` |

### 3.7 Plain executable (`LauncherType.Exe`)

| Aspect | Implementation |
|--------|----------------|
| Availability | always (`ExeLauncher.IsAvailableAsync` → `true`) |
| Install detection | `DetectByPath`: `game.InstallPath` if it exists, else `<libraryRoot>\<SafeDirName(title)>` for each `games.libraryRoots`, else the directory of an absolute existing `game.ExePath`; `installed` additionally requires `GameDetector.ResolveExe(game, installPath)` to find the exe (absolute or relative to the install dir) |
| Command | `<exe> [game.Args] [extraArgs]`, working dir = exe directory, `WaitForGameProcess: false` (the launched pid **is** the game) |
| Credential injection | `ExeStrategy` → `InjectionResult.None` |
| Save dir default | `%USERPROFILE%\Saved Games\{title}` |

---

## 4. Install detection (`GameDetector`)

```
DetectAsync(game)  ──cache hit (< 60 s)──▶ cached GameInstallStatus
       │ miss
       ▼
Detect(game):  launcherReady = Exe || ResolveLauncherExe(launcher) != null
               (installPath, sizeGb, version) = DetectSteam | DetectEpic | DetectBattleNet | DetectRiot | DetectEa | DetectUbisoft
               installPath ??= DetectByPath(game)
               installed = Directory.Exists(installPath) && (game.ExePath == null || ResolveExe(game, installPath) != null)
               → GameInstallStatus(gameId, installed, installPath, sizeGb ?? game.SizeGb, version ?? game.Version, verifiedAt, launcherReady)
```

`DetectAllAsync` runs detection for the whole catalogue with `Parallel.ForEachAsync` (max 8); exceptions per game
degrade to "not installed". `Invalidate()` drops the cache (called by `GameLibrary.RescanAsync`). `FindUninstallEntry`
matches `DisplayName` with `TitleMatches` (alphanumeric-only, case-insensitive, prefix either way) and filters by
`Publisher` when several names are searched.

---

## 5. Identifying the real game process

Store launchers start a client (`steam.exe`, `EpicGamesLauncher.exe`, …) which then starts the game; the pid the
Agent must track is the game's, not the launcher's. `LauncherBase.WaitForGameProcessAsync`:

| Input | Source |
|-------|--------|
| Expected image names | `ExpectedProcessNames(game)`: `game.ExePath` file name + launcher-specific product tables |
| Session filter | only processes whose `SessionId == LaunchContext.WtsSessionId` |
| Exclusions | pids captured in the pre-launch snapshot; helper executables (`LauncherBase.HelperNameFragments`: `crashhandler`, `crashpad`, `vcredist`, `dxsetup`, `easyanticheat_setup`, `beservice`, `redist`, `installer`, `uninstall`, …) when falling back to path matching |
| Match rule (`IsGameProcess`) | name matches an expected name; **or**, only when the catalogue names no executable, a non-helper process whose `MainModule` path is under `game.InstallPath` |
| Event source | `ProcessWatcher.ProcessStarted` (WMI `Win32_ProcessStartTrace`; Toolhelp snapshot diff every 1 s when WMI is unavailable) |
| Poll | `Process.GetProcesses()` scan every 500 ms as a safety net (WMI events can be delayed or lost) |
| Timeout | `LaunchRequest.LaunchTimeoutSec` (from `games.launchTimeoutSec`, default 90); `GameLaunchService` adds `LaunchGrace` (15 s) to its own cancellation so the launcher's timeout error wins over a bare `timeout` |

On timeout the launcher's exit code (if it already exited) is attached: `gameLaunchFailed{stage: "wait", exitCode}`.

---

## 6. Account pool: lease, injection, restore

### 6.1 Lease (`AccountPool.LeaseAsync(game, sessionId, reuseLeaseId)`)

1. Reuse `reuseLeaseId` or an existing lease for the same game + session unless it expires within 1 min.
2. `ITokenStore.Agent.DecodeSigningSecret()` — without agent registration → `unauthorized`.
3. `GET /games/{id}/accounts/lease` (retried 3× with 500 ms base delay on transient errors) → `AccountLease`
   with `Secret` encrypted.
4. `Signing.DecryptAccountPoolSecret(signingSecret, contract.Secret)`: key = HKDF-SHA256(signingSecret,
   info `"account-pool"`), payload = base64 `nonce(12) || ciphertext || tag(16)`, AES-256-GCM. Decryption failure
   releases the lease on the server (`launchFailed`) and raises `gameLaunchFailed{stage: "lease"}`.
5. `ActiveLease` keeps the secret in memory only; `RevealSecret()` is the single accessor, `ToString()` is redacted.
   The signing secret buffer is zeroed after use.

Expiry: a 30 s watchdog raises `AccountPool.LeaseExpired`; `GameLaunchService.OnLeaseExpired` kills every game
using that lease (graceful, `AccountLeaseReleaseReason.Exit`).

### 6.2 Injection (`AccountInjector.InjectAsync(game, lease)`)

```
KillLauncherAsync(launcher)            ProcessKiller.KillByName for LauncherProcessNames(launcher), 5 s grace
        │
        ▼
strategy.InjectAsync(game, lease, launcherExe)
        │  FileBackups.Capture(path) before every write (≤ 8 MiB per file; missing file recorded as "delete on restore")
        │  ProfileFiles.ApplyAsync: lease.extra.files { "<path>": "<base64>" } — %LOCALAPPDATA% / %APPDATA% / %USERPROFILE%
        │     expanded against IKioskProfilePaths; any target outside the kiosk profile is refused (warning)
        ▼
InjectionResult(ExtraArgs, EnvVars, RestoreAction)
        │  ExtraArgs → LaunchContext.Env["CLUBSHELL_LAUNCHER_ARGS"] → placed on the launcher command line by LauncherBase
        ▼
CloudSaveSync.DownloadAsync(game, lease)   (section 7)
```

`IKioskProfilePaths` is implemented by `ShellLauncher` (`SHELL_REPLACEMENT.md`): `UserProfile` from
`ProfileList\<sid>\ProfileImagePath`, `LocalAppData`, `RoamingAppData`.

### 6.3 Restore and cleanup (`AccountInjector.RestoreAsync(result, launcher)`)

Called by `GameSessionTracker` after the game exits and by `GameLaunchService.FailAsync` when a launch fails after
injection. Never throws.

1. Kill the launcher client again (`LauncherProcessNames`).
2. `InjectionResult.RestoreAsync` → `FileBackups.RestoreAsync`: original bytes written back, files that did not exist
   deleted.
3. `ClearCredentialManagerAsync`: obtains the kiosk session token (`ProcessAsUser.GetUserToken(session)`),
   duplicates it, `WindowsIdentity.RunImpersonated`, then `Advapi32.EnumerateCredentials(filter)` +
   `Advapi32.CredDeleteW` for every `CredentialFilters(launcher)` pattern — removes tokens the launcher stored in the
   kiosk user's Credential Manager.

The kiosk profile is additionally wiped between sessions by `ProfileResetService` (`SHELL_REPLACEMENT.md` §3.2), which
is the backstop for anything a launcher stores elsewhere in the profile.

### 6.4 Lease release reasons (`AccountLeaseReleaseReason`)

| Reason | When |
|--------|------|
| `exit` | game exited on its own / lease expiry kill |
| `manual` | `games.kill` |
| `sessionEnd` | `GameLaunchService.KillAllAsync` from `SessionCleanup` |
| `launchFailed` | any failure after the lease was taken |

Release carries the `CloudSaveUpload` descriptor when an upload succeeded (`AccountPool.ReleaseAsync(leaseId,
reason, cloudSave)` → `POST /games/{id}/accounts/{leaseId}/release`).

---

## 7. Cloud saves (`CloudSaveSync`)

| Item | Value |
|------|-------|
| Enabled | `agent.json → games.cloudSave.enabled` |
| Bundle dir | `games.cloudSave.root` (default `cache\saves`), files `<leaseId>-download.zip` / `<leaseId>-upload.zip`, deleted afterwards |
| Size cap | `games.cloudSave.maxMb` (default 512) — download stream is bounded (`CopyBoundedAsync`), upload is skipped above the cap or above `SaveUploadTarget.MaxBytes` |
| Transfer timeout | `CloudSaveSync.TransferTimeout` = 5 min |
| Save directory | `ResolveSaveDir`: `lease.extra.saveDir` template, else the per-launcher default (section 3); placeholders `%LOCALAPPDATA%`, `%APPDATA%`, `%USERPROFILE%`, `{installPath}`, `{title}`; unresolved `{installPath}` or a relative result → skipped |
| Download | `ActiveLease.CloudSave` (`url`, `sha256`, `sizeBytes`) → HTTP GET via the `RetryPolicy.DownloadHttpClientName` client → `Signing.Sha256FileAsync` must match → `ZipFile.ExtractToDirectory(overwrite)` |
| Upload | `ZipFile.CreateFromDirectory` → sha256 → `IServerClient.GetSaveUploadTargetAsync(gameId, leaseId)` → HTTP PUT `application/zip` → `CloudSaveUpload(url, sha256, size)` returned to the lease release |
| Failure policy | every failure is logged and swallowed: saves never block a launch or an exit |

---

## 8. Kill semantics

| Mechanism | Where | Behaviour |
|-----------|-------|-----------|
| Job object | `GameLaunchService.CreateJob(pid)` → `JobObject.Create(null, killOnClose: true)` + `Assign(pid)` | One job per launch; disposing it (`GameSessionTracker` after exit, `FailAsync`) kills anything still inside. Assignment can fail when the launcher already put the game in a non-nestable job — logged at debug, tree kill still works |
| Tree kill | `LauncherBase.KillAsync(pid, force)` → `ProcessKiller.KillTree(pid, grace)` | Toolhelp snapshot, descendants first, root last; `grace = 0` when `force`, else `games.killGraceSec` (default 10): `WM_CLOSE` first, terminate after the grace |
| Protected images | `ProcessKiller.SystemCritical` | never killed (`clubshellagent.exe`, `clubshell-shell.exe`, `csrss.exe`, …) |
| `games.kill` | `GameLaunchService.KillAsync(GamesKillRequest)` | by `pid`, by `gameId` (all instances) or everything; `force` from the request; lease reason `manual` |
| Session end | `GameLaunchService.KillAllAsync(reason)` via `IGameSessionCleanup` | `force = reason != SessionEndReason.User`; waits `killGraceSec + 5 s` for exit processing, then `AccountPool.ReleaseAllAsync(sessionEnd)` |
| Anti-cheat | `AntiCheatMonitor` → `IAntiCheatGameControl.KillAsync(pid, force: true)` | see `ANTICHEAT.md` §6 |
| Lease expiry | `OnLeaseExpired` | graceful kill of every game on that lease |

`GameSessionTracker.MarkKilled(pid, reason)` is called before the kill so the exit is reported as
`GameState.Killed` instead of `Exited`.

Exit processing (`GameSessionTracker.WatchAsync`, one task per tracked pid): wait for exit →
`game.stateChanged{exited|killed, exitCode}` → `AccountInjector.RestoreAsync` → `CloudSaveSync.UploadAsync` →
`AccountPool.ReleaseAsync(reason, upload)` → `LaunchReport` with `LaunchReportPhase.Exit` → `Job.Dispose()` →
`Exited` event.

---

## 9. Known quirks

| Launcher | Quirk | Handling |
|----------|-------|----------|
| Riot | The Riot Client has no `-login` style option; sessions are file-based (`RiotGamesPrivateSettings.yaml`) and bound to the account's device trust | Leases must include `extra.privateSettingsYaml`; otherwise the launch fails early with `accountPoolExhausted` rather than showing the login form to a player |
| Riot | `RiotClientServices.exe` keeps running after the game exits | `RestoreAsync` kills the client family; the profile reset removes the rest |
| Epic | `com.epicgames.launcher://…?silent=true` still shows the launcher briefly on first run; a stale `[RememberMe]` block logs in the previous account | `GameUserSettings.ini` is stripped before every launch |
| Epic / EA / Ubisoft | URL launches cannot pass game arguments | `game.Args` and `extraArgs` are ignored with a debug log |
| Battle.net | `--exec="launch <code>"` requires the client to be logged in; there is no password argument | `SavedAccountNames` pre-fills the account, `extra.files` may carry a session; otherwise the player types the password |
| Battle.net | Product codes are not the display names (`pro` = Overwatch, `fenris` = Diablo IV) | `BattleNetProducts` / `ProductExes` tables |
| Steam | `-login` on the command line is visible to `Process.GetProcesses` callers in the same session | Accepted: the kiosk user owns the session; the argument is not logged by the Agent |
| Steam | Steam Guard / mobile confirmation on a fresh PC | Out of scope for the Agent; pool accounts must be pre-authorised on the club image |
| All stores | Auto-updates on launch delay process discovery | `launchTimeoutSec` (default 90 s) is the only knob; pre-update games during maintenance |
| All | `WaitForGameProcess` can match a wrong process when the catalogue has no `exePath` and the install dir also hosts a launcher stub | Provide `exePath` in the catalogue; path fallback is disabled once names are known |

---

## 10. Adding a new launcher

1. **Contracts** — add the member to `LauncherType` in `src/ClubShell.Contracts/Games/LauncherType.cs` (camelCase on
   the wire, e.g. `Gog` → `"gog"`). Add its settings slot to `GamesSettings.Launchers` (`AppSettings.cs`,
   `ForLauncher`) and a default `exePath` in `config/agent.default.json → games.launchers`.
2. **Regenerate mirrors** — run `tools/scripts/gen-contracts-ts.ps1` and `tools/scripts/gen-contracts-rs.ps1`
   (`tools/ContractsGen`) so `packages/contracts-ts/src/games.ts` and `crates/protocol/src/games.rs` gain the value;
   declarations are never hand-edited, only the MANUAL blocks are (`ARCHITECTURE.md` §1.2).
3. **Detection** — add `ResolveLauncherExe` candidates and a `DetectXxx` branch in `GameDetector.Detect`.
4. **Backend** — `public sealed class GogLauncher : LauncherBase` overriding `Launcher` and `BuildCommand`
   (return `null` when the client is missing, throw `IpcError.Validation("launcherAppId", …)` on bad ids); override
   `ExpectedProcessNames` when product ids do not map to exe names.
5. **Credentials** — add an `ILauncherCredentialStrategy` to the array in the `AccountInjector` constructor, extend
   `LauncherProcessNames` and `CredentialFilters`.
6. **Saves** — add a default template to `CloudSaveSync.ResolveSaveDir` if the store has a conventional location.
7. **DI** — `services.AddSingleton<IGameLauncher, GogLauncher>();` in `DependencyInjection.AddGamesSubsystem`.
8. **UI** — the Shell only needs the new string literal for filters (`games.list.launcher`) and, optionally, an icon.
9. **Tests** — `tests/ClubShell.Agent.Tests`: `BuildCommand` cases, detection against a fixture manifest, and the
   injector strategy's backup/restore round trip.

---

## 11. `games.launch` end-to-end

```
Player    React (LaunchOverlay)   Rust (commands/games.rs)   GameHandlers      GameLaunchService                 AccountPool/Injector/Saves     Launcher (LauncherBase)          Win32                 Server
  │ click   │                       │                          │                 │                                  │                              │                                │                     │
  ├────────▶│ games_launch ────────▶│ games.launch ───────────▶│ LaunchAsync ───▶│ session active? library.Get      │                              │                                │                     │
  │         │ steps: antiCheat…     │                          │                 │ PolicyDenies (processAllowlist)  │                              │                                │                     │
  │         │                       │                          │                 │ CheckAntiCheatAsync ─▶ AntiCheatMonitor (ANTICHEAT.md §5)       │                                │                     │
  │         │                       │                          │                 │ ActiveSessionId (IKioskSessionLocator)                          │                                │                     │
  │         │                       │                          │                 │ launcher.IsAvailableAsync; alreadyRunning?                      │                                │                     │
  │         │                       │                          │                 │ requiresAccount → LeaseAsync ───▶│ GET /games/{id}/accounts/lease ──────────────────────────────────────────────────────▶│
  │         │                       │                          │                 │                                  │ DecryptAccountPoolSecret     │                                │                     │
  │         │                       │                          │                 │ InjectAsync ────────────────────▶│ kill launcher, patch config  │                                │                     │
  │         │                       │                          │                 │ DownloadAsync ──────────────────▶│ GET bundle, sha256, unzip    │                                │                     │
  │         │◀ agent://game.stateChanged{launching} ◀──────────┼─────────────────┤ PublishAsync(Launching)          │                              │                                │                     │
  │         │                       │                          │                 │ launcher.LaunchAsync(effective, lease, context) ───────────────▶│ DetectAsync, BuildCommand      │                     │
  │         │                       │                          │                 │                                  │                              │ snapshot pids                  │                     │
  │         │                       │                          │                 │                                  │                              │ ProcessLauncher.LaunchAsync ──▶│ CreateProcessAsUser │
  │         │                       │                          │                 │                                  │                              │ WaitForGameProcessAsync ◀─────│ ProcessWatcher      │
  │         │                       │                          │                 │◀── LaunchResult{pid} ────────────┼──────────────────────────────┤                                │                     │
  │         │                       │                          │                 │ CreateJob(pid); tracker.TrackAsync(GameLaunchRecord)            │                                │ JobObject           │
  │         │◀ agent://game.stateChanged{running,pid} ◀────────┼─────────────────┤ PublishAsync(Running)            │                              │                                │                     │
  │         │                       │                          │                 │ ReportAsync → SendLaunchReportAsync ──────────────────────────────────────────────────────────────────────────────────▶│ POST /games/{id}/launch-report
  │         │◀── LaunchResult ──────┼◀── response ─────────────┼◀────────────────┤                                  │                              │                                │                     │
  │  plays  │ (kiosk game mode: KIOSK_MODE.md §5)              │                 │                                  │                              │                                │                     │
  │  exits  │                       │                          │                 │ GameSessionTracker.WatchAsync: exit → stateChanged{exited} → RestoreAsync → UploadAsync → ReleaseAsync → exit report → Job.Dispose
```

Failure at any step after the lease is taken goes through `GameLaunchService.FailAsync`: job disposed, injection
restored, lease released (`launchFailed`), `game.stateChanged{failed, error}`, launch report sent, and the
`IpcError` is returned to the Shell (`sessionNotActive`, `notFound`, `policyDenied`, `antiCheatBlocked`,
`gameLaunchFailed{stage}`, `accountPoolExhausted`, `timeout`, `conflict{alreadyRunning}`).

---

## 12. Configuration keys (`agent.json → games`)

| Key | Default | Used by |
|-----|---------|---------|
| `libraryRoots` | `["D:\\Games", "G:\\Games"]` | `GameDetector.DetectByPath`, `FindUninstallEntry` fallback |
| `scanIntervalSec` | 900 | reserved: parsed into `GamesSettings` but no periodic rescan timer reads it today; `GameLibrary.RescanAsync` runs on demand (`games.list` refresh) |
| `launchTimeoutSec` | 90 | `LaunchRequest.LaunchTimeoutSec`, `WaitForGameProcessAsync` |
| `killGraceSec` | 10 | `LauncherBase.KillAsync` graceful window, `KillAllAsync` wait |
| `accountPool.enabled` | true | `AccountPool.Enabled` (disabled → `accountPoolExhausted` for games needing an account) |
| `accountPool.leaseTtlSec` | 14400 | server-side hint; expiry is `AccountLease.ExpiresAt` |
| `accountPool.releaseOnExit` | true | release on game exit |
| `cloudSave.enabled` / `root` / `maxMb` | true / `cache\saves` / 512 | `CloudSaveSync` |
| `launchers.<steam\|epic\|battleNet\|riot\|ea\|ubisoft>.exePath` | see `config/agent.default.json` | `GameDetector.ResolveLauncherExe` first candidate |

Related policy: `policies.json → processAllowlist` is evaluated against the game's exe name before every launch
(`GameLaunchService.PolicyDenies`) and enforced at runtime by `ProcessAllowlistModule` (`KIOSK_MODE.md` §3).
