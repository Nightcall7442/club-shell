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
| Volume | logs in to the iSCSI target the server assigns (`…:games-<version>`, never persistent), brings the disk online as the server assigns it — a shared library LUN **read-only, verified before it goes online** (fail-closed), or a seat's own **writable copy that the club server resets at every boot** (the club this reconciliation is for: G: on every PC) — and assigns the drive letter; other iSCSI sessions of the PC are left alone |
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
| `true` | no | the Agent (`GamesShareMounter`, SMB share only; see below) |
| any | yes | **ClubDisklessHelper** — the Agent does not map or unmap anything |

The check is the service key `HKLM\SYSTEM\CurrentControlSet\Services\ClubDisklessHelper`, evaluated when the Agent
starts (`GamesShareMounter.StartAsync`). The Agent log says which case applies:

```
Games library volume is managed by ClubDisklessHelper
storage.gamesShare is enabled, but ClubDisklessHelper is installed and owns the games library volume; the Agent will not map or unmap anything. Set storage.gamesShare.enabled = false
Games share: managed by ClubDisklessHelper        (AgentWorker startup summary)
```

Leave `storage.gamesShare.enabled = false` on diskless PCs; the warning above means both were configured. The
decision is taken at Agent start: after installing or removing the helper, restart `ClubShellAgent` (a ClubShell
install or upgrade restarts it by itself).

The heartbeat tells the club server who owns the library (`gamesVolume.owner`: `agent`, `disklessHelper` or `none`,
SERVER_API.md §4.1). With the helper, `mounted` is left out: the helper reports the volume to its own server.

Everything else in the Agent is unaffected: game detection reads the launchers' manifests wherever the library is
mounted, and the process allow-list only polices the kiosk session (the helper and its PowerShell children run in
session 0).

## The Agent's own share (clubs without the helper)

The Agent mounts only an **SMB share**. It no longer logs in to iSCSI targets: a shared LUN must be read-only before
Windows brings the disk online, a per-seat copy belongs to the club server that resets it, and both are the helper's job. An `agent.json` (or server config)
that still has `storage.gamesShare.iscsi` loads fine, but the Agent then mounts nothing and logs an error:

```
storage.gamesShare.iscsi is set (iqn...), but the Agent does not mount iSCSI: the games library over iSCSI is handled by ClubDisklessHelper (docs/DISKLESS.md). Nothing is mounted; install ClubDisklessHelper or set storage.gamesShare.iscsi = null for an SMB share
```

The heartbeat then reports `gamesVolume: { owner: "agent", mounted: false }`.

How the SMB share behaves:

* **Settings are followed live.** `storage.gamesShare` from `agent.json` or from the server config
  (`GET /agents/{pcId}/config`) takes effect without an Agent restart: `enabled: true` starts mounting,
  `enabled: false` unmaps. A new `uncPath`, `driveLetter` or `credentialsRef` first unmaps the old letter (in session 0
  and in the logged-on user's session), then maps the new one. The unmap is forced, so a game running from the old
  share loses its files: publish a new library path between sessions.
* **From the first boot.** The last server config is kept in `cache\server-config.json` and applied when the Agent
  starts, before the first heartbeat; the heartbeat then fetches the current one.
* **Healthy mapping is only verified.** Every 30 s (and on network changes) the Agent checks that the letter is still
  readable; it remaps only when it is not (backoff 5 s → 2 min).
* **Agent stop, restart or update never unmaps.** Mappings are not persistent (`CONNECT_TEMPORARY`), so a reboot
  clears them; an Agent restart finds the letter already mapped and keeps it.
* **Anti-cheat caveat.** Games on a network share are a poor fit for kernel anti-cheats: some refuse to start from a
  network path or a mapped network drive. Keep games with Vanguard, FACEIT, EAC or BattlEye on a local disk, or use
  ClubDisklessHelper (its iSCSI volume is a local disk to Windows; bench step 8 below), and put only the rest of the
  library on the share. The Agent enforces this: it refuses to launch a game with an anti-cheat from a UNC path or a
  network drive (`antiCheatBlocked`, reason `networkPath`; docs/ANTICHEAT.md §5.1).

## Installing with ClubShell.msi

The helper is not in this repository. Build the MSI with its folder and the optional feature appears:

```powershell
.\tools\scripts\package.ps1 -Version 1.2.0 -DisklessHelperDir D:\drops\ClubDisklessHelper
# or: dotnet build installer/wix/ClubShell.Installer.wixproj -c Release -p:DisklessHelperDir=D:\drops\ClubDisklessHelper
```

`package.ps1` copies `ClubDisklessHelper.exe` into `artifacts/installer/diskless` byte for byte: the helper carries its
owner's signature and ClubShell never re-signs it (SHELL_CHANGES 32). Without `-DisklessHelperDir` the MSI is exactly
the previous one — the release workflow never passes it, so released ClubShell MSIs do not carry the helper at all.

**Who updates the helper.** Its owner: ClubShell's MSI only puts the helper on a PC that has none (the exe component is
`NeverOverwrite`), never replaces a helper that is there, whatever its version, never stops or restarts its service,
and leaves it installed when ClubShell is upgraded or removed (`Permanent`, no `ServiceControl`). A ClubShell update
in the middle of a session therefore never takes G: away from a player. MSIs built before this change did stop and
replace the helper on upgrade; a club that installs the helper itself (the club of this reconciliation) is not affected.

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
`%ProgramData%\ClubDiskless\credentials.bin` before capturing the image. `DISKLESSSTART=1` starts it at the end of the
install (`CA_StartDisklessHelper`, best effort: a helper that cannot start never rolls back the ClubShell install).

**Uninstalling ClubShell** leaves the helper, its service and `%ProgramData%\ClubDiskless` in place. Remove the helper
with its own uninstaller, or by hand: `Stop-Service ClubDisklessHelper; sc.exe delete ClubDisklessHelper`, then delete
`C:\Program Files\ClubDiskless` (and `%ProgramData%\ClubDiskless` for a clean slate).

**Already installed by hand** (the helper README's `New-Service`, or the club's own installer): leave it — the MSI
sees the exe and installs nothing over it, even with `INSTALLDISKLESS=1`.

## Bench verification

The helper's Windows side has not been run yet (its README says so), and neither has this MSI feature. Before
enabling it in a club, on one bench PC (Windows 11 Pro, TrueNAS with a published library version):

1. Install with `INSTALLDISKLESS=1 DISKLESSSTART=1 …` (or the helper's own installer); event log shows the library
   `<version>` mounted at G:.
2. Agent log shows `Games library volume is managed by ClubDisklessHelper`; no `iSCSI` / `Mapping` lines from the Agent.
3. `Get-Disk | ? BusType -eq iSCSI | fl Number,IsReadOnly,IsOffline` → `IsOffline : False` and `IsReadOnly` as the
   server assigns it: `True` for a shared LUN (`New-Item G:\probe.txt` is refused); `False` for a per-seat copy, whose
   writes are gone after the next reboot.
4. `Get-StorageSetting | fl NewDiskPolicy` → `OfflineShared`; `fsutil dirty query G:` → `NOT Dirty` after a day and
   several reboots.
5. Launch a game from G: through the Shell, publish a new library version → `switchPending` in the panel, the game
   keeps running; after it exits the new version is mounted within 30 s, same letter, and the Shell catalogue still
   shows the game as installed.
6. Upgrade ClubShell (MSI and `AgentUpdater`) during a session → the helper's service is never stopped (its start time
   in `Get-Process ClubDisklessHelper` does not change) and G: never goes away while the game is open.
7. Server off + reboot → G: comes back from `assignment.json`; TrueNAS off + reboot → desktop without delay, no G:.
8. FACEIT and Vanguard with a game on G: (anti-cheat drivers reading from an iSCSI volume).

## PCs whose C: is clean at every boot

A diskless seat boots Windows over iSCSI from a fresh copy of the image, so everything the Agent keeps in
`C:\ProgramData\ClubShell` is gone after each reboot: the agent tokens and signing secret (`secure\agent.tokens`), the
kiosk password (`secure\kiosk.cred`), `agent-identity.json`, the offline outbox (`cache\offline.db`) and the
power-command acks (`cache\power-acks.json`). Two ways to run on such a PC (SHELL_CHANGES 25):

* **A directory that survives the reboot** — `agent.json → paths.persistent` (absolute, e.g. a persistent volume the
  club server gives each seat). The identity, the secrets, the outbox and the acks then live there; the Agent creates it
  accessible to SYSTEM and Administrators only. The PC keeps its credentials and its unsent events across reboots.
* **No such directory** — mark the PC in the cash desk, «Зал и устройства», «Без постоянного диска». The server then
  expects the PC to register again without proof at every boot (an information in the log, not the D-78 warning), and
  `Agents:RegisterProof=enforce` lets it through while the PC is offline. Unsent offline events of a boot are lost.

The kiosk password needs neither: while Windows auto-logon still has to log the kiosk on, the Agent takes the password
auto-logon holds and rotates it only once the kiosk session exists (`shell.kioskUser.rotatePasswordOnStart`).

The hardware id leaves out a disk on an iSCSI, USB, SD or virtual bus (SHELL_CHANGES 30), so a diskless seat's id does
not follow the seat disk the club server assigns it, and a hybrid's does not change with the order its disks come up in.

The seed policy sets no DNS servers (SHELL_CHANGES 27): the Agent leaves every adapter, the boot NIC included, with the
resolvers the club's DHCP gives. A club that wants filtering resolvers sets them in its own policy (cash desk,
«Зал и устройства», «Политика ПК»), which the server's seed then no longer overwrites.

## Games on the games disk

With Steam on the games disk, `agent.json → games.launchers.steam.exePath = "G:\\Steam\\steam.exe"` is all the Agent
needs (SHELL_CHANGES 31). Without it the Agent also finds the client through the `SteamPath` Steam records for the
signed-in user, and a `games.libraryRoots` entry holding a `steamapps` folder (`G:\Steam`) counts as a Steam library
even when the client's `libraryfolders.vdf` does not list it. When G: comes up after the Agent started, the next
maintenance tick (15 s) sees the new disk and rescans the installs; otherwise they are rescanned every
`games.scanIntervalSec`. The Shell asks the Agent again before it refuses a game the catalogue still marks as missing.

To service a seat, unlock the Shell with the admin PIN and exit: the Agent then keeps the Shell closed for 30 minutes,
lets USB drives work and does not kill `cmd`, `reg` or `powershell` in the kiosk session (SHELL_CHANGES 28, 33, 34).
Signing out hands the seat back at once; a reboot always comes back sealed.

## Known gaps

* The helper's server API (`diskless/v1/…`) belongs to the club's diskless server; it is not part of
  `SERVER_API.md`, and `tools/MockServer` does not emulate it.
* The Agent tells the club server only that the helper owns the library (`gamesVolume.owner = disklessHelper`); the
  mounted version, letter and read-only or writable state go from the helper to its own server.
* The release workflow does not fetch the helper: pass `-DisklessHelperDir` to `package.ps1` for builds that
  should carry the feature.
