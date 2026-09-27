# Diskless games library: ClubDisklessHelper

ClubShell does not build diskless boot in-house. The games library of a diskless club is mounted by
**ClubDisklessHelper**, a separate, shell-agnostic Windows service (it works the same under ClubShell, Senet,
iCafeCloud…). This document covers how the two coexist on a gaming PC, how the helper is installed with
ClubShell.msi, and what is still unverified. The helper's own README is the reference for its behaviour.

## What the helper does

| | |
|---|---|
| Service | `ClubDisklessHelper`, LocalSystem, automatic start; `C:\Program Files\ClubDiskless\ClubDisklessHelper.exe` |
| Binary | .NET 10 self-contained single file (x64, ~76 MB); no third-party packages, only Microsoft.Extensions.*, ProtectedData, ServiceController |
| Server | the club's diskless server: `POST diskless/v1/machines/register` (header `X-Club-Key`), `…/refresh`, `PUT …/machines/{id}/status` every 30 s (Bearer token); TLS validated against the club CA (`CaCertificatePath`) |
| Volume | logs in to the iSCSI target the server assigns (`…:games-<version>`, never persistent), sets the disk **read-only and verifies it before bringing it online** (fail-closed), assigns the drive letter; other iSCSI sessions of the PC are left alone |
| Version switch | deferred (`switchPending`) while any process runs from the volume, so a new library version never pulls the disk from under a game |
| Offline | keeps the last assignment in `assignment.json` and mounts it after a reboot without the server; a mounted volume is not dropped when the server goes away |
| State | `%ProgramData%\ClubDiskless` (SYSTEM + Administrators only): `helper.json` settings, `credentials.bin` tokens under machine DPAPI, `assignment.json` |
| Logs | Windows event log *Application*, source `ClubDisklessHelper` |

Storage operations run as Windows PowerShell storage cmdlets (`Set-StorageSetting -NewDiskPolicy OfflineShared`,
`Connect-IscsiTarget`, `Set-Disk -IsReadOnly`, `Set-Partition`), started with `-EncodedCommand` and parameters passed
through environment variables, so server-provided values never become script text.

## Who owns the games library

One drive letter, one owner. The Agent mounts the library itself only when ClubDisklessHelper is **not** installed:

| `storage.gamesShare.enabled` | Helper service installed | Games library mounted by |
|---|---|---|
| `false` | no | nobody (local disks only) |
| `true` | no | the Agent (`GamesShareMounter`: SMB share or `iscsi` target) |
| any | yes | **ClubDisklessHelper** — the Agent does not map, log in, mark read-only or log out anything |

The check is the service key `HKLM\SYSTEM\CurrentControlSet\Services\ClubDisklessHelper`, evaluated when the Agent
starts (`GamesShareMounter.StartAsync`). The Agent log says which case applies:

```
Games library volume is managed by ClubDisklessHelper
storage.gamesShare is enabled, but ClubDisklessHelper is installed and owns the games library volume; the Agent will not map, log in or log out anything. Set storage.gamesShare.enabled = false
Games share: managed by ClubDisklessHelper        (AgentWorker startup summary)
```

Leave `storage.gamesShare.enabled = false` on diskless PCs; the warning above means both were configured. The
decision is taken at Agent start: after installing or removing the helper, restart `ClubShellAgent` (a ClubShell
install or upgrade restarts it by itself).

Everything else in the Agent is unaffected: game detection reads the launchers' manifests wherever the library is
mounted, and the process allow-list only polices the kiosk session (the helper and its PowerShell children run in
session 0).

## Installing with ClubShell.msi

The helper is not in this repository. Build the MSI with its folder and the optional feature appears:

```powershell
.\tools\scripts\package.ps1 -Version 1.2.0 -DisklessHelperDir D:\drops\ClubDisklessHelper
# or: dotnet build installer/wix/ClubShell.Installer.wixproj -c Release -p:DisklessHelperDir=D:\drops\ClubDisklessHelper
```

`package.ps1` copies `ClubDisklessHelper.exe` into `artifacts/installer/diskless` and Authenticode-signs that copy
when `CODESIGN_PFX_PATH` is set (the helper ships unsigned; the original stays byte-identical to its `.sha256`).
Without `-DisklessHelperDir` the MSI is exactly the previous one.

The feature `DisklessHelper` is off by default. Public properties (MSI or `ClubShellSetup.exe`):

| Property | Meaning |
|---|---|
| `INSTALLDISKLESS=1` | install the feature (`ADDLOCAL=DisklessHelper` works too) |
| `DISKLESSSERVERURL` | club diskless server, e.g. `https://club-server` → `helper.json` `Helper.ServerUrl` |
| `DISKLESSCLUBKEY` | club key → `Helper.ClubKey` (hidden from MSI logs) |
| `DISKLESSCACERT` | club CA PEM path → `Helper.CaCertificatePath`; omitted when empty — place the PEM there yourself |
| `DISKLESSSTART=1` | start the service at the end of the install (a PC that is not a reference image) |

```powershell
msiexec /i ClubShell-1.2.0.msi /qn SERVERURL=https://club.example.uz CLUBAPIKEY=... `
  INSTALLDISKLESS=1 DISKLESSSERVERURL=https://club-server DISKLESSCLUBKEY=... `
  DISKLESSCACERT=C:\ProgramData\ClubDiskless\club-ca.pem
```

`helper.json` is written by `C:\Program Files\ClubDiskless\configure-helper.ps1` only when both the URL and the key
are given and the file does not exist yet. The same script re-points a PC later:
`configure-helper.ps1 -ServerUrl … -ClubKey … [-CaCertificatePath …] -Force`, then `Restart-Service ClubDisklessHelper`.

**Start policy and the reference image.** A fresh install registers the service but does not start it: started
before `sysprep /generalize`, it would register the reference machine, and every PC cloned from the image would
share that registration (the helper reads the HWID on each PC, so an image captured before the first start is
identical for all). It starts at the next boot. If it was started on the reference machine anyway, delete
`%ProgramData%\ClubDiskless\credentials.bin` before capturing the image. Upgrades — including an Agent MSI update
applied by `AgentUpdater` — stop the service to replace the exe and start it again at the end
(`CA_StartDisklessHelper`, best effort: a helper that cannot start never rolls back the ClubShell install). Major
upgrades keep the feature installed without `INSTALLDISKLESS` (MigrateFeatureStates).

**Uninstall** stops and deletes the service and removes the exe and script. `%ProgramData%\ClubDiskless` stays
(registration and last assignment), so a reinstall resumes as the same machine; delete it for a clean slate.

**Already installed by hand** (the helper README's `New-Service`): stop and remove that service first
(`Stop-Service ClubDisklessHelper; sc.exe delete ClubDisklessHelper`) so the MSI creates and owns it; `helper.json`
and the registration in `%ProgramData%\ClubDiskless` are kept.

## Bench verification

The helper's Windows side has not been run yet (its README says so), and neither has this MSI feature. Before
enabling it in a club, on one bench PC (Windows 11 Pro, TrueNAS with a published library version):

1. Install with `INSTALLDISKLESS=1 DISKLESSSTART=1 …`; event log shows `Library <version> mounted read-only at G:`.
2. Agent log shows `Games library volume is managed by ClubDisklessHelper`; no `iSCSI` / `Mapping` lines from the Agent.
3. `Get-Disk | ? BusType -eq iSCSI | fl Number,IsReadOnly,IsOffline` → `IsReadOnly : True`, `IsOffline : False`;
   `New-Item G:\probe.txt` is refused.
4. `Get-StorageSetting | fl NewDiskPolicy` → `OfflineShared`; `fsutil dirty query G:` → `NOT Dirty` after a day and
   several reboots.
5. Launch a game from G: through the Shell, publish a new library version → `switchPending` in the panel, the game
   keeps running; after it exits the new version is mounted within 30 s, same letter, and the Shell catalogue still
   shows the game as installed.
6. Upgrade ClubShell (MSI and `AgentUpdater`) during a session → the helper is running again afterwards and G: never
   went away while the game was open.
7. Server off + reboot → G: comes back from `assignment.json`; TrueNAS off + reboot → desktop without delay, no G:.
8. FACEIT and Vanguard with a game on G: (anti-cheat drivers reading from a read-only iSCSI volume).

## Known gaps

* The helper's server API (`diskless/v1/…`) belongs to the club's diskless server; it is not part of
  `SERVER_API.md`, and `tools/MockServer` does not emulate it.
* The Agent reports nothing about the helper to the club server; the helper reports its own state to its server.
* The release workflow does not fetch the helper: pass `-DisklessHelperDir` to `package.ps1` for builds that
  should carry the feature.
