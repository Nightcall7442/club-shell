# ClubShell — Updates

Status: descriptive; `ARCHITECTURE.md` §3 (trust), §7 (recovery) and §11 (versioning) are normative.
This document follows one package from the release pipeline to a running PC. Symbols are from
`src/ClubShell.Core/Updates/*.cs`, `src/ClubShell.Core/Security/Signing.cs`,
`src/ClubShell.Agent/Updates/*.cs` and `src/ClubShell.Contracts/Commands/ServerCommand.cs`.

Two components are updated independently: the **Agent** (`ClubShellAgent.exe`, Windows service) and the
**Shell** (`clubshell-shell.exe`, Tauri kiosk). Both ship as MSI (an `.exe` bootstrapper and, for the
Shell, a ZIP are also accepted). The Agent is the only updater: it checks, downloads, verifies, stages
and applies packages for both components, and reports every stage to the Shell UI over IPC.

---

## 1. End-to-end picture

```
release engineering                      server                              gaming PC (Agent, SYSTEM)                        Shell UI
───────────────────                      ──────                              ─────────────────────────                        ────────
package.ps1  → agent-1.2.0.msi
sign.ps1     → sha256 + RSA-PSS sig
publish.ps1  → upload + manifest ──────▶ /updates/{channel}/manifest
                                         (component=agent|shell)
                                                 ▲
                                                 │ GET every checkIntervalSec (±20 % jitter)   UpdateChecker.CheckOnceAsync
                                                 │ ?component=&current=&arch=x64
                                                 └──── 200 UpdateManifest | 204 ─────────────▶ IsApplicable? suppressed?
                                                                                                │ Available event ─────────────────────▶ agent://update.available (toast)
                                                                                                ▼
                                         GET manifest.url (Bearer, Range) ◀───────────────── UpdateDownloader.DownloadAsync
                                                                                                cache\updates\<comp>-<ver>.tmp
                                                                                                size + sha256 + RSA-PSS ─────────────▶ agent://update.progress (downloading/verifying)
                                                                                                rename → <comp>-<ver>.msi
                                                                                                UpdateApplier.StageAsync
                                                                                                pending-update\<comp>\<file> + <comp>.json ▶ agent://update.progress (staging)
                                                                                                                                        ▶ agent://update.ready
                                                                                                AgentUpdater.ProcessComponentAsync:
                                                                                                  no session ∧ autoInstall ∧ in window
                                                                                                  (mandatory: ≥ 60 s after ready)
                                                                                                UpdateApplier.ApplyAsync
                                                                                                  rollback-<comp>.json marker
                                                                                                  Shell: ShellUpdater (stop → backup → msiexec → relaunch)
                                                                                                  Agent: ClubShell.Updater.exe | msiexec (detached, service restarts)
                                                                                                next start: ConfirmAppliedAsync / ReconcileRollbackAsync
```

---

## 2. Manifest

`UpdateManifest` (`ServerCommand.cs`; IPC_PROTOCOL.md §6.19; SERVER_API.md §4):

| Field | Type | Meaning |
|-------|------|---------|
| `channel` | `"stable" \| "beta"` (`UpdateChannel`) | Channel the manifest was published to |
| `component` | `"agent" \| "shell"` (`UpdateComponent`) | Target component |
| `version` | string, semver | Package version (`MAJOR.MINOR.PATCH[-prerelease][+build]`, parsed by `SemanticVersion`) |
| `url` | string | HTTPS download URL; requires the Agent Bearer token; must support `Range` |
| `sha256` | string | Lower-case hex SHA-256 of the package file |
| `size` | long | Package size in bytes (checked before hashing) |
| `signature` | string | Base64 RSA-PSS-SHA256 signature **over the raw package bytes** |
| `releaseNotes` | string | Markdown, shown in the UI |
| `mandatory` | bool | Apply even during a session (after a 60 s notice); also unlocks server-side rollbacks (a *different*, not necessarily newer, version) |
| `publishedAt` | ISO-8601 UTC | Publication time |
| `minAgentVersion` | string, optional | Shell packages only: minimum Agent version required |

Example:

```json
{
  "channel": "stable",
  "component": "shell",
  "version": "1.1.0",
  "url": "https://club.example.uz/updates/stable/shell-1.1.0.msi",
  "sha256": "6b86b273ff34fce19d6b804eff5a3f5747ada4eaa22f1d49c01e52ddb7875b4b",
  "size": 48234496,
  "signature": "MEUCIQ…base64…",
  "releaseNotes": "## 1.1.0\n- Theme picker\n- Uzbek strings",
  "mandatory": false,
  "publishedAt": "2026-09-01T08:00:00.000Z",
  "minAgentVersion": "1.1.0"
}
```

### 2.1 Channels

`policies.json → updates.channel` wins over `agent.json → updates.channel` (`UpdateChecker.Channel`).
Use `beta` on a handful of PCs (one per zone) for a soak of at least a few days, then publish the same
package to `stable`. Both channels are independent manifests on the server; a PC on `beta` that is moved
back to `stable` keeps its version (downgrade protection) until `stable` overtakes it, unless the server
marks the stable manifest `mandatory` (server-side rollback).

### 2.2 Server endpoint

`GET /api/v1/updates/{channel}/manifest?component=agent|shell&current=<semver>&arch=x64` — auth:
agent (Bearer + HMAC request signature). `200 UpdateManifest` when a package is published for the
channel/component, `204` when `current` is already the latest, `404` for an unknown channel/component
(`ServerClient.GetUpdateManifestAsync`, `Endpoints.UpdateManifest`). The MockServer implements it in
`tools/MockServer/src/routes/pcs.ts` (`204` when the published version is not newer than `current`).

---

## 3. Checking — `UpdateChecker`

* **Schedule**: `RunAsync` sleeps `Jitter(max(60 s, updates.checkIntervalSec))` (±20 % uniform, from
  `RandomNumberGenerator`) and then `CheckOnceAsync`. `AgentUpdater` also calls `CheckOnceAsync` once at
  start-up and on IPC `update.check`.
* **Per component**: fetch → `UpdateManifestDocument { Manifest, FetchedAt, CurrentVersion, AgentVersion }`
  → `IsApplicable` → not `IsSuppressed` → raise `Available` (→ `update.available` IPC event) and remember
  it as `LatestAgent` / `LatestShell`.
* **`IsApplicable(current, agentVersion)`** (`UpdateManifestExtensions`): the package version must parse;
  it must be **greater** than `current` (or merely *different* when `mandatory`); when
  `minAgentVersion` is set the running Agent must be ≥ it. Unparsable `current` counts as `0.0.0`.
  Prerelease ordering follows semver (`1.2.0-beta.2 < 1.2.0`).
* **Suppression**: `Suppress(component, version, FailedSuppression = 6 h)` after a failed apply; the
  same version is ignored until the timer expires (also persisted on disk as `failed-<component>.json`,
  see §6).
* **Effective policy**: `Channel`, `AutoInstall` (`policies.json → updates.autoInstall`, fallback
  `agent.json`), `ApplyWindow` (`agent.json → updates.applyWindow`, `{ from, to }` local `HH:mm`,
  wraps midnight, `null` = any time). `MayApplyNow(manifest)` = `mandatory || (AutoInstall && IsInApplyWindow())`.
* `Current` (`ComponentVersions { Agent, Shell }`) is `ClubShellVersion.Current` for the Agent and the
  version the Shell reported in `auth.hello` for the Shell (until then `0.0.0`, so the Shell manifest is
  always "newer" — the Agent still refuses to apply while no Shell has said hello only through the
  session/window rules, so run a Shell before expecting the version to settle).

---

## 4. Download and verification — `UpdateDownloader`

`DownloadAsync(manifest, publicKey, progress, ct)`:

1. Target `updates.downloadDir` (default `cache\updates`), file name `PackageFileName()` =
   `<agent|shell>-<version>.<msi|exe>` (extension from the URL; unsafe characters → `_`). A finished and
   verified package is reused without a download.
2. Download to `<name>.tmp` over the `RetryPolicy.DownloadHttpClientName` client with `Authorization:
   Bearer <agent access token>`; an existing `.tmp` resumes with `Range: bytes=<existing>-`. Up to
   `MaxAttempts = 3` attempts, 2 s × attempt between them, each resuming. Progress is reported at most
   every `ProgressInterval = 500 ms` as `UpdateProgress { phase: downloading, percent, bytesDone, bytesTotal }`.
3. `VerifyAsync`: file length must equal `size`; `Signing.Sha256FileAsync` must equal `sha256`
   (case-insensitive hex); `Signing.VerifyFileSignatureAsync(path, signature, publicKey)` must succeed
   (RSA-PSS, SHA-256, `RSASignaturePadding.Pss`). With no public key the package is **refused** unless
   `AllowUnsignedPackages` is set (dev/mock only; never in production).
4. Atomic rename `.tmp → .msi`. Any `UpdateException(UpdatePhase.Downloading|Verifying)` deletes the temp
   file and propagates to `AgentUpdater.FailAsync` (§6).

`CleanStale(7 d, keep)` at start-up removes old temp/packages except the currently staged ones.

### 4.1 What exactly is signed

Two signature forms exist in `Signing.cs`; only the first is verified by the pipeline today.

**Package signature (`UpdateManifest.signature`, verified)** — RSA-PSS-SHA256 over the raw bytes of the
`.msi`/`.exe`/`.zip` file, base64-encoded. `sign.ps1` must produce exactly this.

**Manifest signature (canonical JSON, available)** — `Signing.ManifestCanonicalBytes(manifest)` builds the
UTF-8 JSON object with **these fields, in this order, no whitespace**:

| # | Field | Encoding |
|---|-------|----------|
| 1 | `component` | camelCase enum name (`"agent"` / `"shell"`) |
| 2 | `version` | string as published |
| 3 | `url` | string as published |
| 4 | `sha256` | string as published |
| 5 | `size` | JSON number |
| 6 | `publishedAt` | `yyyy-MM-dd'T'HH:mm:ss.fff'Z'`, UTC |

`Signing.VerifyManifest(manifest, rsa)` checks a base64 RSA-PSS-SHA256 signature over those bytes. It is
the hook for a manifest-level signature (defence against a compromised server swapping `url`/`sha256`),
but `UpdateChecker` does not call it yet and the `UpdateManifest` record has a single `signature` field
(the package one). Adding a `manifestSignature` field is tracked in `ROADMAP.md`.

---

## 5. Staging and apply — `UpdateApplier`, `AgentUpdater`, `ShellUpdater`

### 5.1 Paths

| Path | Content |
|------|---------|
| `cache\updates\<comp>-<ver>.tmp` | in-flight download |
| `cache\updates\<comp>-<ver>.msi` | verified package |
| `cache\updates\shell-backup\` (+ `.version`) | copy of the current Shell install, taken before a Shell apply (`ShellUpdater.BackupDirectory`) |
| `pending-update\<agent\|shell>\<file>` | staged copy (`UpdateApplier.StageAsync`) |
| `pending-update\<comp>.json` | `PendingUpdate { Component, Version, PackagePath, Sha256, Mandatory, StagedAt, PreviousVersion, Attempts }` |
| `pending-update\rollback-<comp>.json` | `RollbackMarker { Component, FromVersion, ToVersion, PackagePath, CreatedAt, Attempts }` — written immediately before the installer runs |
| `pending-update\failed-<comp>.json` | `FailedUpdateMarker { Component, Version, Reason, FailedAt, RetryAfter }` |
| `logs\update-<comp>-<ver>.log` | `msiexec /l*v` log |

### 5.2 Decision loop (`AgentUpdater`)

`AgentUpdater` is a `BackgroundService`. After `StartupAsync` (§5.5) it subscribes to
`UpdateChecker.Available` and `ISessionService.Changed` (session ended → re-evaluate), runs the checker
loop, and evaluates both components every `EvaluateInterval = 1 min` or whenever signalled:

```
ProcessComponentAsync(component, latestManifest):
  pending = applier.GetPendingAsync(component)
  if latestManifest and not (suppressed or marked failed) and pending.version != manifest.version:
      pending = StageAsync(manifest)                     # download → verify → stage → update.ready
  if pending is null: return
  if pending.mandatory:
      allowed = no open session
             or (now − readyAt ≥ MandatoryNoticeDelay = 60 s)
  else:
      allowed = no open session and checker.AutoInstall and checker.IsInApplyWindow()
  if allowed: ApplyPendingAsync(component, pending)
```

"Open session" is `ISessionService.State.IsOpen()`; a paused or locked session still counts as open. A
mandatory package therefore interrupts a player after the 60 s banner (`update.ready.applyAt = readyAt +
60 s`), a normal one waits for the seat to free up **and** the apply window (default 04:00–07:00 local).

### 5.3 Shell update (`ShellUpdater.ApplyAsync`)

1. `update.progress { phase: applying, 0 % }`.
2. `IShellRelauncher.StopAsync` — the Shell process is terminated (the watchdog is told not to restart it).
3. `BackupCurrent(previousVersion)` copies `C:\Program Files\ClubShell\Shell\` to
   `cache\updates\shell-backup\` and writes `.version`; progress 25 %.
4. ZIP package (`IsZipPackage`: `PK\x03\x04` magic) → hash re-check + extract over the Shell directory;
   otherwise `UpdateApplier.ApplyAsync(Shell)` → rollback marker → `msiexec /i <msi> /qn /norestart /l*v
   <log>` (or `<exe> /quiet /norestart`), synchronous, `InstallTimeout = 10 min`, exit code `0` or
   `3010` (`MsiRebootRequired`) = success.
5. `shell.exePath` must exist afterwards; progress 100 %; pending + rollback markers cleared.
6. `finally`: `IShellRelauncher.RelaunchAsync` — the new Shell starts in the kiosk session and sends
   `auth.hello{shellVersion}`; `SessionHandlers` calls `ShellUpdater.ConfirmRunningVersionAsync` →
   `UpdateApplier.ConfirmAppliedAsync(Shell, version)`.
7. On `UpdateException`: `RestoreBackup()`, `MarkFailedAsync`, pending/rollback cleared,
   `update.progress { phase: failed, error }`, relaunch of the (restored) Shell.

`tauri.conf.json → bundle.windows.allowDowngrades = true` so a server-side rollback of the Shell MSI
installs without a manual uninstall.

### 5.4 Agent self-update (`UpdateApplier.ApplyAsync(Agent)`)

The installer stops the very service that runs the applier, so the apply is **detached**:

1. `update.progress { phase: applying }`, rollback marker written
   (`rollback-agent.json { FromVersion: running, ToVersion: package }`).
2. If `ClubShell.Updater.exe` exists next to the Agent (`UpdaterHelperPath`, `AppContext.BaseDirectory`)
   it is started detached with `--package <msi> --version <ver> --log <log>` and is expected to stop the
   service, run `msiexec /i … /qn /norestart /l*v <log>`, and start the service again. Otherwise
   `msiexec` itself is started detached with the same arguments; the MSI's service actions
   (`installer/wix/Service.wxs`) stop and restart `ClubShellAgent`.
3. `UpdateApplyResponse { scheduled: true, at: now }` is returned and the process waits to be killed.
   SCM recovery (restart after 5 s, 3 attempts) covers a helper crash.

### 5.5 Start-up reconciliation and crash detection (`AgentUpdater.StartupAsync`)

1. Load the public key from `updates.publicKeyPath` (`Signing.LoadRsaPublicKeyFileAsync`; PEM
   `PUBLIC KEY`, `RSA PUBLIC KEY` or an X.509 certificate). Missing/invalid → error logged, packages
   refused (`HasPublicKey = false`).
2. `ReconcileRollbackAsync(Agent)`: `ConfirmAppliedAsync(Agent, ClubShellVersion.Current)` clears
   pending + rollback when the running version equals `ToVersion`. A leftover `rollback-agent.json`
   whose `ToVersion` ≠ running version means the installer did not produce a running new version: the
   version is `MarkFailedAsync` + `Suppress`ed for 6 h, pending/rollback cleared. Because the MSI keeps the
   previous binaries when it fails (Windows Installer rollback), the old Agent is what is running now —
   that is the rollback.
3. `ReconcileRollbackAsync(Shell)`: a leftover `rollback-shell.json` means the Shell apply never
   completed (`ConfirmRunningVersionAsync` was never called with the new version — power loss mid-install,
   or the new Shell crash-looped and the watchdog's `CrashRecovery` entered safe mode before any
   `auth.hello`). The version is marked failed + suppressed and `ShellUpdater.RollbackAsync` restores
   `shell-backup\` and relaunches.
4. Any still-pending package publishes `update.ready` again so the UI banner survives an Agent restart;
   `CleanStale(7 d)` prunes the download directory.

---

## 6. Failures

`AgentUpdater.FailAsync(component, version, UpdateException)`:

* `UpdateChecker.Suppress(component, version, 6 h)` and `UpdateApplier.MarkFailedAsync` (persisted
  `failed-<comp>.json` with `RetryAfter`), so the same version is not retried in a loop.
* `update.progress { phase: failed, error: { code: internal, message } }` to the Shell.
* Telemetry: the error is in the Agent log (`update-*.log` for installer failures); `ARCHITECTURE.md` §7
  reports it as "mark manifest version as failed for 6 h".

A missing signing key is treated as a **local configuration problem**, not a bad package: the version is
not suppressed, `update.progress{failed, "No update signing key configured"}` is sent, and the next check
retries once the key is fixed.

---

## 7. Shell UI

Events (`agent://update.*`, wired in `apps/shell/src/store/index.ts`):

| Event | UI |
|-------|----|
| `update.available { manifest, current }` | Toast `update.availableTitle` with component + version (8 s) |
| `update.progress { component, version, phase, percent, bytesDone, bytesTotal, error? }` | `notifications.setUpdateProgress` → progress banner in `NotificationCenter`; `failed` shows the error |
| `update.ready { component, version, restartRequired, mandatory, applyAt? }` | `notifications.setUpdateReady` → "update ready / will install at …" banner; for `mandatory` a countdown to `applyAt` |

Commands: `update_check` (IPC `update.check` → `AgentUpdater.CheckNowAsync`, returns
`UpdateCheckResponse { current: { agent, shell }, agent?, shell? }`) and `update_apply { component }` (IPC
`update.apply` → `AgentUpdater.ApplyAsync`: applies now when no session is open or the package is
mandatory; otherwise `{ scheduled: true, at: null }` — deferred to the next idle moment; `{ scheduled:
false }` when nothing is staged). Both are behind the admin panel in the Shell (`AdminPanel`).

---

## 8. Server-pushed update command

`ServerCommand{ type: "update", payload: UpdateCommand { component, applyNow, manifest? } }` over WS or
`GET /agents/{pcId}/commands` → `AgentUpdater.HandleCommandAsync`:

* With `manifest`: it is staged as given (download + verify still apply) **bypassing suppression** — this
  is the operator's "I know, try again" path, and the way to push a specific version to one PC.
* Without: `CheckOnceAsync` and stage the latest applicable manifest for the component; nothing applicable
  → `{ scheduled: false }`.
* `applyNow: true` → `ApplyPendingAsync` immediately, regardless of session, auto-install flag or apply
  window (the operator has decided). `applyNow: false` → the package waits for the normal decision loop.
* The response (`UpdateApplyResponse`) is the command ack payload.

---

## 9. Offline behaviour

* Manifest fetch failures (`ServerApiException`, network errors) are logged at Warning and yield "no
  manifest" — nothing else changes; the next check happens after the normal jittered interval.
* A download interrupted by connectivity loss keeps its `.tmp`; the next attempt (same run, up to 3, or
  the next check once the manifest is seen again) resumes with `Range`.
* A package that is already **staged** is applied offline without any server contact (all decisions are
  local: session state, clock, apply window). A club can therefore pre-stage an update during the day and
  let it install at 04:00 even if the WAN is down at night.
* `ARCHITECTURE.md` §8 lists updates as "skipped" in offline mode — that describes the check, not a
  pending apply.

---

## 10. Release engineering

Scripts live in `tools/scripts/` (PowerShell 5.1). Together with `.github/workflows/release.yml` they
form the pipeline `package → sign → publish`.

| Step | Script | Input | Output |
|------|--------|-------|--------|
| Build | `build.ps1 [-Target All\|Dotnet\|Rust\|Web\|Installer\|Contracts\|Agent\|Shell]` | repo | `dotnet build`/`test`, `dotnet publish` of the Agent (win-x64) into `artifacts/publish/agent`, `pnpm --filter @clubshell/shell build`, `cargo build --release` / `tauri build`, `artifacts/build-info.json` |
| Package | `package.ps1 -Version <ver> -Channel stable\|beta -BaseUrl <url> [-NotesFile] [-Mandatory] [-MinAgentVersion]` | build outputs | `artifacts/release/<ver>/`: `ClubShell-<ver>.msi` (WiX Agent+Shell MSI → component `agent`), `ClubShellSetup-<ver>.exe` (Burn bundle), `ClubShell-Shell-<ver>.msi` (Tauri MSI → component `shell`), `agent/` payload, `manifest.json` (one unsigned `UpdateManifest` per component under `components.{agent,shell}`), `SHA256SUMS.txt`, plus `artifacts/release/ClubShell-<ver>.zip` |
| Sign | `sign.ps1 -Files <globs>` (Authenticode: `-PfxPath`/`-PfxPassword` or `CODESIGN_PFX_PATH`/`CODESIGN_PFX_PASSWORD`, or `-Thumbprint`) and `sign.ps1 -ManifestPath <release>/manifest.json -ManifestKeyPem <private.pem>` | MSI/EXE files, RSA private key PEM | Authenticode-signed binaries; `manifest.json` with `components.<c>.signature` = base64 RSA-PSS-SHA256 over the package bytes (the `signature` field), `manifestSignatures.<c>` over `Signing.ManifestCanonicalBytes`, refreshed `sha256`/`size`/`SHA256SUMS.txt`/zip. `-Verify` re-checks either |
| Publish | `publish.ps1 -Target S3\|Http\|Folder -Destination <s3://…\|https://…\|\\share> [-Version] [-Channel]` | signed release folder | uploads `<Destination>/<channel>/<version>/<files>` and `<Destination>/<channel>/manifest.json` (HTTP target: `PUT` files + `POST /api/v1/updates/<channel>/manifest` with `Authorization: Bearer $CLUBSHELL_PUBLISH_TOKEN`), then reads the manifest back and compares |

`release.yml` (GitHub Actions) runs on a `v*.*.*` tag or `workflow_dispatch` (channel + version): the tag's
pre-release suffix selects `beta`, otherwise `stable`. On `windows-latest`: web build → `tauri build` →
`dotnet publish` → Authenticode (`sign.ps1`, `CODESIGN_PFX_BASE64` / `CODESIGN_PFX_PASSWORD` secrets, optional) →
WiX MSI/bundle → `package.ps1` → `sign.ps1 -ManifestPath` with the `UPDATE_MANIFEST_KEY_PEM` secret → GitHub
release with the installers, `manifest.json` and `SHA256SUMS.txt` → `publish.ps1` (S3 with `AWS_*` secrets and
`UPDATE_S3_BUCKET`, or HTTP with `CLUBSHELL_PUBLISH_TOKEN` / `UPDATE_BASE_URL`) in the `production` environment,
for `stable` tags and every manual dispatch. Promotion of a soaked `beta` build to `stable` is a manual
dispatch of the same version with `channel = stable`. See `ci.yml` for the per-PR build/test matrix (no
dotnet/cargo build was executed in the authoring environment; the first CI run is expected to surface compile
errors — `ROADMAP.md`, technical debt).

Versioning: the Agent version comes from `Directory.Build.props` (`ClubShellVersion.Current` reads the
assembly informational version), the Shell from `Cargo.toml` (`[workspace.package] version`) and
`tauri.conf.json → version`; keep all three equal for a release. The manifest `version` must match the
MSI's `ProductVersion` or `ConfirmAppliedAsync` will never see a match and the rollback logic will flag a
successful install as failed.

---

## 11. Key management

The update signing key is an RSA keypair (2048-bit minimum; 4096 recommended, it is used rarely).
The **private** key never leaves the release machine / CI secret store; the **public** key is deployed
with the Agent MSI as `C:\Program Files\ClubShell\Agent\update-public.pem` (`agent.json →
updates.publicKeyPath`).

Generate (OpenSSL 3.x):

```powershell
# private key, PKCS#8 PEM, protected with a passphrase
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:4096 -aes-256-cbc -out update-private.pem

# public key, SubjectPublicKeyInfo PEM ("-----BEGIN PUBLIC KEY-----"), what Signing.LoadRsaPublicKey expects
openssl pkey -in update-private.pem -pubout -out update-public.pem

# sign a package the way UpdateDownloader.VerifyAsync verifies it (RSA-PSS, SHA-256, salt = digest length)
openssl dgst -sha256 -sign update-private.pem `
  -sigopt rsa_padding_mode:pss -sigopt rsa_pss_saltlen:-1 `
  -out ClubShellShell-1.1.0.msi.sig ClubShellShell-1.1.0.msi
[Convert]::ToBase64String([IO.File]::ReadAllBytes('ClubShellShell-1.1.0.msi.sig'))   # → manifest.signature

# hash for manifest.sha256
(Get-FileHash -Algorithm SHA256 ClubShellShell-1.1.0.msi).Hash.ToLowerInvariant()

# verify locally before publishing
openssl dgst -sha256 -verify update-public.pem -sigopt rsa_padding_mode:pss -sigopt rsa_pss_saltlen:-1 `
  -signature ClubShellShell-1.1.0.msi.sig ClubShellShell-1.1.0.msi
```

`rsa_pss_saltlen:-1` (salt length = digest length) matches .NET's `RSASignaturePadding.Pss` verifier.
`sign.ps1` wraps these steps; `-1` is also what `openssl` calls `digest`.

Rotation: ship the new public key in an Agent release signed with the **old** key (the MSI carries
`update-public.pem`), wait until every PC reports the new Agent version (heartbeat `X-Agent-Version`),
then sign subsequent releases with the new private key. Keep the old private key until then. A
compromised key means: rotate immediately by the same procedure and publish the rotation release as
`mandatory`.

Trust chain summary (`ARCHITECTURE.md` §3): TLS + Bearer + HMAC authenticate the **channel** to the
server; the RSA signature authenticates the **package** independently of the server, so a compromised
server cannot push code. The public key file is under `C:\Program Files` (Administrators/SYSTEM write
only), which is the same trust level as the Agent binary itself.

---

## 12. Troubleshooting

| Symptom | Where to look | Likely cause / fix |
|---------|---------------|--------------------|
| `Update signing key … not found; packages will be refused` | Agent log at start-up | `updates.publicKeyPath` wrong or the PEM not installed. Fix the path or reinstall the Agent. |
| `update.progress{failed}` "No update signing key configured" | Shell banner, Agent log | Same as above; the version is **not** suppressed, fix and wait for the next check (or `update_check`). |
| "Package signature is invalid" | Agent log (`Verifying`) | Package signed with a different key, wrong padding (PKCS#1 v1.5 instead of PSS), or signature made over the wrong file. Re-run `sign.ps1`; verify with the `openssl dgst -verify` line above. |
| "Package SHA-256 does not match the manifest" / size differs | Agent log | Manifest and file out of sync (re-uploaded file, CDN cache). Re-publish; the Agent re-downloads on the next check. |
| Same version keeps being skipped: `… is suppressed after a failed apply` | Agent log, `pending-update\failed-<comp>.json` | A previous apply failed; wait 6 h, or delete the marker and restart the service, or push `update{ manifest, applyNow }` from the server (bypasses suppression). |
| Shell manifest ignored: `Ignoring shell manifest … minAgent` | Agent log | `minAgentVersion` > running Agent. Publish the Agent first. |
| Update staged but never applied | `update.ready` banner stays | A session is open (paused/locked counts), or `autoInstall = false`, or outside `applyWindow`. Check `policies.json`; force with `update_apply` from the admin panel or `applyNow` from the server. |
| Agent update "handed to the installer" but old version still runs | `logs\update-agent-<ver>.log`, Event Viewer → Application (MsiInstaller), `rollback-agent.json` | msiexec failed (another install in progress = 1618, insufficient rights, bad MSI). After restart the Agent marks the version failed. Fix the MSI and republish with a new version number. |
| Shell rolled back after update | `rollback-shell.json` was present at start; `logs\shell-crash.log`, `logs\shell.*.log` | The new Shell never sent `auth.hello` (crash loop → `CrashRecovery` safe mode, missing WebView2, CSP change). Fix, republish. |
| `msiexec` exit 3010 | `update-*.log` | Reboot required; treated as success. Schedule the reboot via the server `reboot` command. |
| Download stuck at N % | Agent log `Download attempt k … failed; resuming` | Server without `Range` support or proxy stripping `Authorization`; the `.tmp` resumes on the next attempt/check. |
| `204` from the manifest endpoint although a newer version was published | Server | `current` compared as semver on the server; check the published `version` string (`v` prefix is tolerated by the Agent, not necessarily by the server). |
| Clock jump / PC clock wrong | Agent log HMAC `clockSkew` rejections | Manifest checks fail with `unauthorized`/`clockSkew`; fix the PC clock (the Agent re-derives from `X-Server-Time`). |

Manual reset of the update state on one PC (as Administrator, service stopped):

```powershell
Stop-Service ClubShellAgent
Remove-Item -Recurse -Force 'C:\ProgramData\ClubShell\pending-update\*'
Remove-Item -Force 'C:\ProgramData\ClubShell\cache\updates\*.tmp'
Start-Service ClubShellAgent
```

The next check re-stages whatever the channel offers.
