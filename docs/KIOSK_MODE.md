# ClubShell — Kiosk mode

Status: descriptive. Documents the lockdown layers as implemented in `apps/shell/src-tauri/src/kiosk/*.rs`,
`crates/winutil/src/{hooks,window,input,monitor}.rs`, `src/ClubShell.Agent/Policy/*.cs`,
`src/ClubShell.Windows/Registry/PolicyRegistry.cs` and `src/ClubShell.Windows/Hooks/BlockedKeyCombos.cs`.
Configuration schemas are in `ARCHITECTURE.md` §12.2 (`shell.json → kiosk`, `idle`, `monitors`) and §12.3
(`policies.json → explorer`, `processAllowlist`, `usb`, `webFilter`, `kiosk`).

Design rule: **defence in depth, no single point of trust.** The Shell process hardens the interactive session it
runs in; the Agent (LocalSystem, session 0) hardens the OS underneath it so that a crashed or killed Shell still
leaves the player inside a locked-down account.

---

## 1. Layers

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│ Player at keyboard / mouse / gamepad                                                              │
└───────────────────────────────────────────────┬──────────────────────────────────────────────────┘
                                                │ input
┌───────────────────────────────────────────────▼──────────────────────────────────────────────────┐
│ Layer 1 — Shell process (clubshell-shell.exe, kiosk user, interactive session)                    │
│  keyboard_hook.rs  WH_KEYBOARD_LL: blocked chords, hotkeys, lock-all (+ WH_MOUSE_LL), game mode   │
│  alt_tab.rs        TopmostGuard 250 ms + minimize_others                                          │
│  window_guard.rs   fullscreen borderless, close prevention, refocus, stray-window sweep 3 s        │
│  taskbar.rs        ShowWindow(SW_HIDE) / ABM_SETSTATE autohide, re-hide poll 5 s                  │
│  overlay.rs        always-on-top overlay webview (lock, ads, toasts) — click-through except lock   │
│  multi_monitor.rs  ads windows on secondary monitors, WM_DISPLAYCHANGE                            │
│  idle_detector.rs  GetLastInputInfo ∧ ActivityFeed → kiosk://idle                                 │
│  mod.rs            clip_cursor on lock, TabTip virtual keyboard, admin mode, kiosk_exit           │
└───────────────────────────────────────────────┬──────────────────────────────────────────────────┘
                                                │ pipe (auth.hello + shell token)
┌───────────────────────────────────────────────▼──────────────────────────────────────────────────┐
│ Layer 2 — Agent policies (ClubShellAgent.exe, SYSTEM, session 0) — PolicyEnforcer + IPolicyModule │
│  ShellReplacementPolicyModule  per-user Winlogon\Shell = clubshell-shell.exe, auto-logon           │
│  ExplorerPolicyModule          HKU\<sid>\...\Policies: DisableTaskMgr, NoRun, NoWinKeys, …         │
│  ProcessAllowlistModule        deny/allow patterns, sweep + ProcessWatcher kill in kiosk session   │
│  UsbPolicyModule               USBSTOR Start=4, DeviceInstall HID class deny, eject volumes        │
│  WebFilterPolicyModule         hosts sink-hole, DNS servers per adapter, outbound IP firewall      │
└───────────────────────────────────────────────┬──────────────────────────────────────────────────┘
                                                │
┌───────────────────────────────────────────────▼──────────────────────────────────────────────────┐
│ Layer 3 — Account model: kiosk user is a plain BUILTIN\Users member with a rotating random         │
│  password, no explorer, profile wiped between sessions (SHELL_REPLACEMENT.md)                     │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Layer 1 — Shell process hardening (`apps/shell/src-tauri/src/kiosk`)

`kiosk::spawn_all(&app, &state)` builds one `Kiosk` (`mod.rs`) owning every guard, installs it as the
`KioskControl` of `AppState` (lock / unlock / game mode / shutdown) and starts the Agent-event bridge
(`spawn_bridge`). All guards are disabled in dev mode (section 7).

### 2.1 Keyboard hook (`keyboard_hook.rs`, `crates/winutil/src/hooks.rs`)

Windows allows **one** `WH_KEYBOARD_LL` hook per process, so blocked chords, Alt+Tab, policy chords and hotkeys
all live in a single filter (`Shared::decide`) running on a dedicated hook thread with its own message pump
(`LowLevelKeyboardHook`). The filter budget is < 1 ms: atomics, one uncontended `RwLock` read, a channel send;
no logging or I/O.

Decision order per key event:

| # | Check | Result |
|---|-------|--------|
| 1 | `ev.injected()` (`LLKHF_INJECTED`) | pass — remote-control input and `SendInput` are never filtered |
| 2 | key down | `ActivityFeed.touch()` (idle detector input) |
| 3 | hotkey match (`Hotkey.combo.matches`, filtered by `allowed_in_game` in game mode) | emit `kiosk://hotkey{name, combo}` (250 ms debounce), block if `consume` |
| 4 | dev mode | pass |
| 5 | `lock_all` | block everything (mouse buttons/wheel too via `mouse_filter`) |
| 6 | `paused` | pass |
| 7 | chord in `combos` (or `game_combos` in game mode) | block, emit `kiosk://hotkey{blocked}` at most once per second |
| 8 | otherwise | pass |

Blocked chord set = `HookPolicy::combos()`: `BlockedCombo::kiosk_defaults()` (`hooks::KIOSK_DEFAULTS`) filtered by
`shell.json → kiosk.blockAltTab` / `blockWinKey`, ∪ `alt_tab_combos()` when Alt+Tab blocking is on, ∪
`policy.explorer.blockedKeyCombos`. A policy can only **add** chords (`HookPolicy::merge_explorer` ORs the flags).

| `KIOSK_DEFAULTS` chord | Purpose | Kept in game mode? |
|------------------------|---------|--------------------|
| `Alt+Tab`, `Alt+Esc`, `Win+Tab` (+ `Ctrl+Alt+Tab` from `alt_tab_combos`) | task switching | no |
| `Ctrl+Esc`, `Win` (lone key), `Win+D`, `Win+R`, `Win+E`, `Win+I`, `Win+L` | Start menu, desktop, Run, Explorer, Settings, lock | `Win*` yes |
| `Alt+F4` | close window | no |
| `Ctrl+Shift+Esc` | Task Manager | yes |
| `PrintScreen` | screenshots | no |

`Ctrl+Alt+Del` is parsed for config compatibility (`BlockedCombo::is_secure_attention`) but silently dropped from
every set — it cannot be hooked (section 4). The Agent-side equivalent table (`BlockedKeyCombos.Default` in
`src/ClubShell.Windows/Hooks/BlockedKeyCombos.cs`) additionally lists `Alt+Space`, `Apps`, `F1`; it is what
`ExplorerPolicyModule.BlockedKeyComboNames` reports back to the server.

Hotkeys (`default_hotkeys`):

| Chord | `HotkeyKind` | Consumed | Allowed in game |
|-------|--------------|----------|-----------------|
| `shell.json → kiosk.exitHotkey` (default `Ctrl+Alt+Shift+F12`) | `exit` | yes | yes |
| `Ctrl+Alt+Shift+A` (`ADMIN_UNLOCK_CHORD`) | `exit` | yes | yes |
| `F1` (`CALL_ADMIN_CHORD`) | `callAdmin` | yes | no |
| `Ctrl+L` (`LOCK_CHORD`) | `lock` | yes | no |
| media volume keys | `volumeUp` / `volumeDown` / `mute` | no | yes |
| `F11` (dev only) | `devFullscreen` | yes | handled natively |

### 2.2 Alt+Tab and foreground (`alt_tab.rs`)

The chords are blocked by the hook; `AltTabBlocker` owns the window side: winutil's `TopmostGuard` re-asserts
`HWND_TOPMOST` + foreground every `GUARD_INTERVAL` (250 ms) and `minimize_others` runs when the guard is
(re-)armed. Enabled by `shell.json → kiosk.topmostGuard`; **disabled while a game runs** so the guard never steals
the game's focus.

### 2.3 Window guard (`window_guard.rs`)

| Guard | Mechanism |
|-------|-----------|
| Fullscreen | borderless window sized to the primary monitor rect (`MultiMonitor::primary_rect`), re-applied on `kiosk_set_fullscreen` and display changes |
| Close prevention | `WindowEvent::CloseRequested` is cancelled unless `kiosk_exit` is in progress |
| Focus loss | after `REFOCUS_DELAY` (200 ms) `force_foreground` unless the new foreground pid is allow-listed (running game via `allow_foreground_pid`, `TabTip.exe`); emits `kiosk://focus` |
| Stray windows | every `SWEEP_INTERVAL` (3 s) `enumerate_windows` and `SW_FORCEMINIMIZE` top-level windows of non-allow-listed processes; classes in `SKIP_CLASSES` (`Shell_TrayWnd`, `Progman`, `WorkerW`, `IPTIP_Main_Window`, …) are never touched |
| Capture affinity | `set_window_display_affinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` is available but **kept off** so remote-control capture can see the Shell |
| Lock | `set_locked(true)` keeps the overlay in front and stops the sweep from fighting it |

### 2.4 Taskbar (`taskbar.rs`)

The kiosk user has no `explorer.exe`, so there is normally no taskbar. When one appears (administrator started
explorer, safe mode), `Taskbar::set_hidden(true)` uses `ShowWindow(SW_HIDE)` on the tray windows, falls back to
`SHAppBarMessage(ABM_SETSTATE, ABS_AUTOHIDE)`, re-hides every `REHIDE_INTERVAL` (5 s) and on display change, and
restores on shutdown / drop. Enabled by `kiosk.hideTaskbar ∪ policy.explorer.hideTaskbar`. The Agent's
`ExplorerPolicyModule.ApplyTaskbar` (`TaskbarController`) does the same from session 0 on a best-effort basis.

### 2.5 Overlay (`overlay.rs`)

A second, lazily created transparent webview (label `overlay`, `index.html#/overlay`) covering the whole virtual
screen. Kinds: `lock` (opaque, captures input; shown by `set_locked` when `kiosk.overlayOnLock`), `ads`,
`message` (click-through so the game keeps the mouse). Used while a game owns the primary monitor and the main
webview is hidden: `shell.command{showAds|showMessage}`, `admin.message`, `session.warning`, remote-control
indicator (`TOAST_TTL` 15 s). Every `show` broadcasts `kiosk://overlay{kind, payload}`.

### 2.6 Multi-monitor (`multi_monitor.rs`)

`EnumDisplayMonitors` enumeration, primary chosen by `shell.json → monitors.primaryIndex` (falls back to the OS
primary), a `WM_DISPLAYCHANGE` watcher window (`ClubShellDisplayWatch`) → `kiosk://monitorChanged`, and one
always-on-top window per secondary monitor (`ads`, `ads-2`, … = `index.html#/ads?monitor=N&mode=…`) rendering
`monitors.secondaryMode` (`black | mirror | wallpaper`). Not created in dev mode.

### 2.7 Idle (`idle_detector.rs`)

`GetLastInputInfo` polled every second, combined with the hook's `ActivityFeed` (minimum of the two, because
`GetLastInputInfo` cannot be reset by software and gamepad input is invisible to it). Stages from `shell.json →
idle`: `dimAfterSec` → `timeoutSec` → `screensaverAfterSec`; each crossing and the return to activity emit
`kiosk://idle`. Paused in game mode. `kiosk_idle_reset` touches the feed (gamepad navigation).

The Agent has its own session-0 `IdleDetector` (`src/ClubShell.Windows/Input/IdleDetector.cs`, adapter
`IdleMonitorAdapter`) for `session.autoLockOnIdleSec` and `power.idleShutdownMin`; it cannot see interactive
input directly and is superseded by the Shell's signal where available.

### 2.8 Lock, cursor, virtual keyboard

`KioskControl::set_locked(true)` (`shell.command{lock}`, session lock): window locked, `keyboard.set_lock_all(true)`
(keyboard + mouse buttons swallowed), cursor clipped to the primary monitor (`winutil::input::clip_cursor`), lock
overlay shown. `kiosk_virtual_keyboard` starts `%CommonProgramFiles%\microsoft shared\ink\TabTip.exe`
(`TABTIP_RELATIVE`), allow-listed for the foreground guard, only when `policy.kiosk.allowVirtualKeyboard`.

---

## 3. Layer 2 — Agent-side policies (`src/ClubShell.Agent/Policy`)

`PolicyEnforcer.ApplyAsync(policy)` applies only the sections that differ from `Current` (plus sections that failed
last time), in registration order, and reverts in reverse (`DependencyInjection.AddPolicySubsystem`). Every module
snapshots the registry before its first write and restores it on `RevertAsync` (`RegistryPolicyOps.Apply`,
`RegistryHelper.Snapshot/Apply`). `PolicyContext(KioskUserSid, KioskSessionId)` targets the kiosk account; user-hive
writes go through `RegistryPolicyOps.WithUserHive`, which uses `HKU\<sid>` while logged on and otherwise loads
`NTUSER.DAT` under the mount name `ClubShell-Kiosk`.

| Module (`Section`) | What it writes / does | Scope |
|--------------------|-----------------------|-------|
| `ShellReplacementPolicyModule` (`shellReplacement`) | `ShellRegistry.SetCustomShell(sid, exe)` + `SetAutoLogon` (`SHELL_REPLACEMENT.md`) | kiosk user hive + HKLM Winlogon |
| `ExplorerPolicyModule` (`explorer`) | `PolicyRegistry.RulesFor(policy, sid)` HKU rules (table below), taskbar hide (best effort), resolved `BlockedKeyCombos` for the Shell | kiosk user hive |
| `ProcessAllowlistModule` (`processAllowlist`) | sweep of running processes in the kiosk WTS session + `ProcessWatcher.ProcessStarted` kills | kiosk session only, never session 0 |
| `UsbPolicyModule` (`usb`) | HKLM rules (table below) + eject of already-mounted removable volumes (`IOCTL_STORAGE_EJECT_MEDIA`) when storage is blocked | machine |
| `WebFilterPolicyModule` (`webFilter`) | `DnsFilter`: hosts-file block between `# ClubShell-BEGIN/END`, `dnsServers` on every physical adapter; `FirewallRules` outbound blocks `ClubShell-WebFilter-Block-N` for the public IPv4s the blocked domains resolve to (cached 6 h) | machine |

Registry rules produced by `PolicyRegistry.RulesFor` (all `REG_DWORD` 1/0 unless noted):

| Key (below `HKU\<sid>\Software` or `HKLM\SOFTWARE`) | Value | From |
|------------------------------------------------------|-------|------|
| `Microsoft\Windows\CurrentVersion\Policies\System` | `DisableTaskMgr`, `DisableLockWorkstation`, `DisableChangePassword` | `explorer.disableTaskManager` |
| `Microsoft\Windows\CurrentVersion\Policies\Explorer` | `NoRun` | `explorer.disableRun` |
| same | `NoControlPanel`, `NoClose`, `NoLogoff` | `explorer.disableSettings` |
| same | `NoWinKeys` | `explorer.disableWinKey` |
| `HKLM\SYSTEM\CurrentControlSet\Services\USBSTOR` | `Start` = 3 (allow) / 4 (disabled) | `usb.allowStorage` |
| `HKLM\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions` | `DenyDeviceClasses` = 0/1, `DenyDeviceClassesRetroactive` = 0, `DenyDeviceClasses\1` = HID class GUID `{745a17a0-…}` (`REG_SZ`) or deleted | `usb.allowHid` |

Not registry-backed (by design, see the class remarks in `PolicyRegistry`): `hideTaskbar` (StuckRects blob is
undocumented → runtime hide), `disableAltTab` and `blockedKeyCombos` (hook only).

Process allow-list semantics (`ProcessAllowlistModule`):

| `mode` | Pattern matches (image name or full path, wildcards `*`/`?`, `ProcessKiller.WildcardToRegex`) | Effect |
|--------|-----------------------------------------------------------------------------------------------|--------|
| `deny` | match | killed (`ProcessBlocked` event, `policyDenied` for `apps.launch` / `games.launch`) |
| `allow` | no match | killed |
| any | `ProcessKiller.SystemCritical`, the Agent, every `IProtectedProcesses` pid (Shell, running games, remote-admin helpers) | never killed |

Example deny list from `config/policies.example.json`: `cmd.exe`, `powershell.exe`, `pwsh.exe`, `regedit.exe`,
`taskmgr.exe`, `mmc.exe`, `control.exe`, `msconfig.exe`, `wscript.exe`, `cscript.exe`, `mshta.exe`, `*cheat*`,
`*inject*`.

---

## 4. What cannot be blocked

| Input | Why | Mitigation |
|-------|-----|------------|
| `Ctrl+Alt+Del` | Secure Attention Sequence, consumed by winlogon before any hook (`BlockedKeyCombos.Unblockable`) | The SAS screen only offers what policy allows: `DisableTaskMgr`, `DisableLockWorkstation`, `DisableChangePassword`, `NoLogoff` remove Task Manager, Lock, Change password and Sign out; "Switch user" is irrelevant (kiosk auto-logon, no other interactive accounts on the image). What remains is a screen the player can cancel |
| `Win+L` | dispatched below the hook chain | `DisableLockWorkstation` (written with `explorer.disableTaskManager`); the hook still swallows the chord as belt-and-braces |
| Power / reset button, unplugging | hardware | session state is persisted every tick (`ARCHITECTURE.md` §7); the Agent resumes the timer after boot; `power.idleShutdownMin` / scheduled shutdown are Agent-driven |
| Boot menu, BIOS, USB boot | firmware | set a firmware password, disable external boot, enable Secure Boot (also required by Vanguard on Windows 11 — `ANTICHEAT.md`) |
| Injected input (`SendInput`) | intentionally passed (`ev.injected()`) so remote control works | only processes in the kiosk session can inject; the allow-list kills unknown ones |
| A game's own overlays / browsers (Steam overlay, in-game web views) | they are children of an allow-listed process | `webFilter` still applies (DNS + firewall are machine-wide) |

---

## 5. Game mode relaxations

`KioskControl::set_game_mode(true)` is called when `game.stateChanged{running}` arrives; `false` on
`exited | failed | killed`. `game.stateChanged` also feeds `WindowGuard::allow_foreground_pid(pid)`.

| Guard | Normal | Game mode |
|-------|--------|-----------|
| Blocked chords | `HookPolicy::combos()` | `HookPolicy::game_combos()`: only `Win*` chords and `Ctrl+Shift+Esc`; `Alt+Tab`, `Alt+F4`, `PrintScreen` released so games and their overlays work |
| Hotkeys | all | only `allowed_in_game`: `exit`, volume keys (`callAdmin`, `lock` disabled — the Shell UI is hidden) |
| `AltTabBlocker` (topmost guard) | on (if `topmostGuard`) | off |
| `WindowGuard` refocus / sweep | on | off; game pid allow-listed |
| Idle detector | on | paused (`IdleDetector::set_paused`) |
| Overlay | main webview | toasts and ads rendered by the overlay window over the game |
| `kiosk_set_guard` request | applied | remembered (`guard_wanted`) and re-applied when the game exits |

Game mode never relaxes Layer 2: process allow-list, USB, web filter and registry lockdown stay active.

---

## 6. Admin unlock

```
Admin presses exit hotkey (Ctrl+Alt+Shift+F12 or Ctrl+Alt+Shift+A)
   │ keyboard_hook: HotkeyKind::Exit → kiosk://hotkey{name:"exit"}
   ▼
React: AdminPinDialog → invoke sys_unlock_admin{pin}
   │ commands/system.rs: trims, ≤ ADMIN_PIN_MAX chars, IPC sys.unlockAdmin
   ▼
Agent SystemHandlers.UnlockAdmin: pin ≤ 32 chars, ≤ 3 failures/min (MaxUnlockAdminAttemptsPerMinute → rateLimited),
   VerifyPin(shell.json → kiosk.adminPinHash) → unauthorized{notConfigured|badPin} on failure
   success → 32 random bytes hex token, AdminTokenLifetime = 5 min
   ▼
Shell AppState.set_admin_unlock(token, expiresAt)  →  Kiosk::set_admin_mode(true) shows the tray (polled every 5 s)
   │
   ├─ kiosk_exit{adminToken, action: "explorer" | "quit"}  → Kiosk::exit: shutdown all guards, optionally spawn explorer.exe, exit(0)
   ├─ kiosk_set_guard{active}, kiosk_set_fullscreen{on}      → require has_admin_unlock()
   ├─ kiosk_reload                                            → admin unlock or dev mode
   └─ sys.reboot / sys.shutdown via the admin menu             → Agent power module
```

Notes:

- The PIN is validated by the **Agent** (never by the webview); `settings_get_shell_config` blanks `kiosk.adminPinHash`
  before returning `shell.json` to React because a hash is brute-forceable. With no hash configured every attempt fails
  with `notConfigured`.
- `AppState::admin_token_valid` compares the cached token and its expiry; `kiosk_exit` refuses invalid or expired
  tokens with `unauthorized`.
- After `kiosk_exit{explorer}` the watchdog **will relaunch the Shell** unless restarts are suspended or the policy
  is reverted (section 8) — use it for a quick look, use `uninstall.ps1` / policy `shellReplacement.enabled=false`
  for maintenance sessions (`SHELL_REPLACEMENT.md` §7).
- Dev mode is always admin mode (`admin_mode: AtomicBool::new(dev)`).

---

## 7. Dev mode

`kiosk::dev_mode(config)` = `CLUBSHELL_DEV=1` (`config::env::DEV`, `ShellConfig::is_dev`) **or** a debug build
(`cfg!(debug_assertions)`). Effects:

| Area | Dev mode behaviour |
|------|--------------------|
| Window | normal resizable 1280×800 window; `F11` toggles fullscreen natively |
| Hooks | filter installed but never blocks; hotkeys only fire while the Shell window is the foreground (`is_foreground(hwnd)`) |
| Guards | no topmost guard, no sweep, no taskbar hide, no cursor clip, no secondary-monitor windows |
| Agent | `CLUBSHELL_DEV=1` selects the mock agent (`agent/mod.rs`) — no pipe, canned IPC data; a debug build without the variable still uses the pipe |
| Admin | permanently in admin mode (tray visible, `kiosk_reload` allowed) |
| Devtools | `kiosk_open_devtools` still requires `shell.json → devtools: true` and a build with the `devtools` feature |

Frontend-only mock mode is `VITE_MOCK=1` with the Vite dev server (`http://localhost:1420`), independent of the
Rust side.

---

## 8. Recovery when the Shell dies

Owned by `ShellWatchdog` + `CrashRecovery` + `ShellLauncher` (`src/ClubShell.Agent/Watchdog`), detailed in
`SHELL_REPLACEMENT.md` §6. Summary:

| Event | Detection | Action |
|-------|-----------|--------|
| Shell process exits | `ShellLauncher.WaitForExitAsync` | `CrashRecovery.Decide`: uptime < 10 s → restart after `shell.restartDelayMs`; else immediate restart |
| ≥ `shell.maxRestartsPerMinute` crashes in 60 s | sliding window in `CrashRecovery` | **safe mode**: `ShellLauncher.LaunchExplorerAsync` drops `explorer.exe` into the kiosk session so the PC stays usable for an administrator, telemetry `shellCrashLoop`, relaunches paused until the session logs on again |
| Shell alive but never connects the pipe | `ShellWatchdog.HealthTimeout` (60 s without `IShellConnectionState.IsConnected`) | Shell stopped (WM_CLOSE, then terminate) and relaunched |
| Shell connected but silent | `PipeServer`: no `sys.ping` for `ipc.heartbeatIntervalSec × missedHeartbeatsBeforeKill` (15 s) | connection dropped, Shell terminated when `KillShellOnHeartbeatLoss` (default) → watchdog restarts |
| Kiosk session logs off / disconnects | `SessionChangeWatcher` | watchdog waits (`WaitingForSession`); auto-logon brings the session back |
| Profile reset / Shell update | `IShellRelauncher.StopAsync` | state `Suspended`; no automatic relaunch until `RelaunchAsync` |
| Agent restarts | SCM recovery | Shell is left running (`ShellLauncher.KeepShellOnAgentExit = true`, job without kill-on-close); it reconnects the pipe and re-hellos |

In safe mode the registry lockdown, allow-list, USB and web filter are still applied, and `Taskbar` re-hide logic is
inactive because the Shell is gone — the administrator sees a normal explorer desktop of a restricted user. Every
crash is appended to `C:\ProgramData\ClubShell\crash-history.json` with exit code, uptime, log tail and WER dump
path.

---

## 9. Testing checklist

Run on a real PC (not a VM) with the release build, `policies.example.json` applied and no admin unlock active.

| # | Test | Expected |
|---|------|----------|
| 1 | `Alt+Tab`, `Alt+Esc`, `Win+Tab`, `Ctrl+Alt+Tab` on the Shell | nothing; `kiosk://hotkey{blocked}` logged once per second at most |
| 2 | `Win`, `Win+R`, `Win+E`, `Win+I`, `Win+D`, `Ctrl+Esc` | nothing |
| 3 | `Ctrl+Shift+Esc` | nothing; `Ctrl+Alt+Del` shows the SAS screen without Task Manager / Lock / Sign out |
| 4 | `Alt+F4`, `PrintScreen`, `F1` (call admin), `Ctrl+L` (lock) | Shell stays; `F1` opens the call-admin dialog; `Ctrl+L` locks |
| 5 | Plug a USB stick | not mounted (`USBSTOR Start=4`); already-mounted stick ejected on policy apply |
| 6 | Plug a USB keyboard/mouse (`allowHid: true`) | works |
| 7 | Start a game; press `Alt+Tab`, `Alt+F4`, `PrintScreen` in the game | pass through to the game; `Win` and `Ctrl+Shift+Esc` still blocked |
| 8 | Game exits | Shell back in front within one sweep (≤ 3 s), guards re-armed, idle detector resumed |
| 9 | Kill `clubshell-shell.exe` from the admin panel | relaunched within `watchdogIntervalMs` + `restartDelayMs` |
| 10 | Kill it 5× within a minute | explorer fallback, `shellCrashLoop` telemetry, `crash-history.json` grows |
| 11 | Exit hotkey → wrong PIN ×3 | fourth attempt returns `rateLimited` |
| 12 | Exit hotkey → correct PIN → `kiosk_exit{explorer}` | explorer appears; Shell relaunched by the watchdog unless restarts are suspended |
| 13 | Browse to a `blockedDomains` entry by name and by IP | both fail (hosts + firewall) |
| 14 | Unplug the second monitor | `kiosk://monitorChanged`, ads window removed, primary window re-sized |
| 15 | Leave idle past `idle.timeoutSec` | `kiosk://idle{stage}` events; auto-lock when `policy.kiosk.idleTimeoutSec` / `session.autoLockOnIdleSec` |
| 16 | Remote-control session from the admin panel | injected input passes the hook, indicator toast over a running game |
| 17 | `shell.command{lock}` from the server | overlay, keyboard/mouse swallowed, cursor clipped; `unlock` restores |
| 18 | Revert policy (`shellReplacement.enabled=false`) and log the kiosk user off/on | explorer shell, no lockdown values left in `HKU\<sid>` |

---

## 10. Configuration keys

### `shell.json → kiosk` (Shell side)

| Key | Default | Consumer |
|-----|---------|----------|
| `fullscreen` | true | `WindowGuard` |
| `topmostGuard` | true | `AltTabBlocker` |
| `hideTaskbar` | true | `Taskbar` |
| `blockAltTab` | true | `HookPolicy.block_alt_tab` |
| `blockWinKey` | true | `HookPolicy.block_win_key` |
| `blockCtrlAltDel` | false | informational only (cannot be hooked) |
| `hideCursorAfterSec` | 0 | React (`apps/shell/src/main.tsx`) hides the cursor after N s without pointer movement (0 = never) |
| `overlayOnLock` | true | `set_locked` overlay |
| `allowVirtualKeyboard` | true | `kiosk_virtual_keyboard` (policy `kiosk.allowVirtualKeyboard` wins) |
| `exitHotkey` | `Ctrl+Alt+Shift+F12` | `default_hotkeys` (empty = only `Ctrl+Alt+Shift+A`) |
| `adminPinHash` | null | `SystemHandlers.UnlockAdmin` (read by the Agent from `shell.json`) |
| `idle.dimAfterSec` / `timeoutSec` / `screensaverAfterSec` | 120 / 300 / 600 | `IdleDetector` stages |
| `monitors.primaryIndex` / `secondaryMode` | 0 / `black` | `MultiMonitor` |
| `devtools` | false | `kiosk_open_devtools` |

### `policies.json` (server side, applied by the Agent, mirrored to the Shell by `policy.changed`)

| Key | Consumer |
|-----|----------|
| `explorer.disableTaskManager`, `disableRun`, `disableSettings`, `disableWinKey` | `PolicyRegistry.RulesFor` (registry) + `HookPolicy` (Win key) |
| `explorer.hideTaskbar`, `disableAltTab`, `blockedKeyCombos` | Shell hook / taskbar (`Kiosk::update_policy`) |
| `processAllowlist.mode`, `patterns` | `ProcessAllowlistModule`, `GameLaunchService.PolicyDenies`, `GameHandlers` apps launch |
| `usb.allowStorage`, `allowHid` | `UsbPolicyModule` |
| `webFilter.*` | `WebFilterPolicyModule` |
| `kiosk.idleTimeoutSec`, `adsIntervalSec`, `allowVirtualKeyboard` | `Kiosk::apply_policy`, session auto-lock, ads scheduler |

### Environment

| Variable | Set by | Effect |
|----------|--------|--------|
| `CLUBSHELL_DEV=1` | developer | dev mode + mock agent (section 7) |
| `CLUBSHELL_PIPE`, `CLUBSHELL_SHELL_TOKEN`, `CLUBSHELL_LOCALE` | `ShellLauncher.BuildEnvironment` | pipe name, token path, UI locale for the launched Shell |
