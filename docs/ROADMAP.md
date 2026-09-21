# ClubShell — Roadmap

Status: planning document, revised per release. Status vocabulary:

| Status | Meaning |
|--------|---------|
| **Done** | Implemented and verified in the authoring environment (type-checked / built / smoke-tested). |
| **Implemented-untested** | Code is complete in this repository but has not been compiled or run here (see *Technical debt*). |
| **Partial** | Some of the feature exists; the gap is named. |
| **Planned** | Agreed scope, not started. |
| **Idea** | Under discussion. |

Dates are intentionally absent: releases are cut when the *Done* column of a version is complete and the
soak on the `beta` channel (`UPDATES.md` §2.1) has passed.

---

## v1.0 — what is in this repository

Goal: a complete kiosk client for one club — lockdown, sessions/billing against the central server,
launchers with account pools, shop/chat/booking/tournaments, offline mode, signed updates, three
languages, UZS.

### Contracts and documentation

| Item | Status | Notes |
|------|--------|-------|
| `src/ClubShell.Contracts` canonical DTOs (camelCase, string enums, `Money{amount,currency}`) | Implemented-untested | C# authored to compile by construction; no `dotnet build` was run |
| `crates/protocol` Rust mirror (serde) | Implemented-untested | Hand-mirrored; `tools/ContractsGen` is the intended generator |
| `packages/contracts-ts` TS mirror | Done | `tsc` clean |
| `docs/ARCHITECTURE.md`, `IPC_PROTOCOL.md`, `SERVER_API.md`, `TAURI_COMMANDS.md` | Done | Normative |
| `docs/TAURI_SHELL.md`, `THEMING.md`, `UPDATES.md`, `ROADMAP.md`, `COMPETITORS.md` | Done | This batch |
| `docs/KIOSK_MODE.md`, `SHELL_REPLACEMENT.md`, `GAME_LAUNCHERS.md`, `ANTICHEAT.md`, `SECURITY.md`, `README.md` | Done | Descriptive; audited against the code (file paths, symbols, config keys) |

### Agent (`src/ClubShell.Core`, `src/ClubShell.Windows`, `src/ClubShell.Agent`)

| Item | Status | Notes |
|------|--------|-------|
| Worker Service `ClubShellAgent` (LocalSystem, session 0), `--console`, `--dev`, `CLUBSHELL__*` env overrides | Implemented-untested | |
| Settings: `agent.json` + defaults + env + server overrides (`SettingsLoader.ApplyServerOverrides`) | Implemented-untested | |
| Registration, JWT + refresh, HMAC-SHA256 request signing (`X-Timestamp`/`X-Signature`), DPAPI token store | Implemented-untested | |
| REST client with retry/circuit breaker; WS realtime client with reconnect | Implemented-untested | |
| Named pipe IPC server, DACL, shell token `auth.hello`, `sys.ping`/`pong`, 4 MiB cap, rate limit | Implemented-untested | |
| Session service, timers, warnings, persistence, grace, offline queue (SQLite outbox) | Implemented-untested | |
| Policy enforcer: registry, firewall/DNS filter, USB, process allow/deny list, key combos | Implemented-untested | Requires a real Windows PC to validate |
| Launchers: Steam, Epic, Battle.net, Riot, EA, Ubisoft, plain EXE (`Games/Launchers/*.cs`); account pool + injector; cloud saves | Implemented-untested | Launcher CLI/URI behaviour changes between launcher versions; see *Open questions* |
| Anti-cheat checks: EAC, FACEIT, Vanguard, Secure Boot (`AntiCheat/*.cs`) | Implemented-untested | Best-effort service/driver state checks, not a certification |
| Remote admin: screen capture, remote input, messages, remote-control indicator | Implemented-untested | |
| Power: shutdown/reboot scheduling, Wake-on-LAN | Implemented-untested | |
| Kiosk user provisioning, password rotation, profile reset between sessions | Implemented-untested | |
| Shell watchdog with crash-loop budget and safe mode (`CrashRecovery`) | Implemented-untested | |
| Games share mount (SMB); iSCSI config slot present | Partial | iSCSI orchestration is v2.0 |
| Telemetry (WMI/perf, 5 s metrics, batched upload, hardware rescan) | Implemented-untested | |
| Updates: checker, downloader (resume, SHA-256, RSA-PSS), applier, Agent self-update, Shell update + backup/rollback, server `update` command | Implemented-untested | `ClubShell.Updater.exe` helper is referenced but not in the repo; msiexec fallback is used. The `--cs-*` / `--c-*` theme-variable mismatch once listed here is fixed in `ARCHITECTURE.md` |
| Local media cache (`LocalCache`, LRU, sha256) | Implemented-untested | Server-provided themes (`ThemeRef`) not yet downloaded through it |

### Shell (`apps/shell`, `crates/winutil`)

| Item | Status | Notes |
|------|--------|-------|
| Tauri 2 host: config, logging, single instance, pipe transport + reconnect, event forwarder, 64 proxy commands + 15 `kiosk_*` commands | Implemented-untested | Rust not compiled in the authoring environment; unit tests are written |
| Kiosk hardening: `WH_KEYBOARD_LL`, Alt+Tab/topmost guard, taskbar, window guard, overlay window, multi-monitor ads windows, idle detector, virtual keyboard | Implemented-untested | |
| Gamepad navigation (gilrs), tray | Implemented-untested | |
| React UI: 12 route screens + game details, hash router with guards, 9 zustand stores, notification center, i18n en/ru/uz, theming, framer-motion | Done | `tsc` + `vite build` verified; mock mode (`VITE_MOCK=1`) smoke-tested in a browser |
| Mock mode (browser) and mock transport (Rust) | Done / Implemented-untested | |
| Playwright specs (`tests/shell-e2e`: `lock.spec.ts`, `games.spec.ts`) | Partial | Run against Vite mock mode only (`ci.yml` job `e2e`); never executed against the Tauri-hosted UI |

### Tooling, installer, CI

| Item | Status | Notes |
|------|--------|-------|
| `tools/MockServer` (Fastify + ws: auth, sessions, games, shop, chat, policies, update manifests) | Done | `tsc` clean |
| `tools/ContractsGen` (reflection over Contracts → TS + Rust) | Implemented-untested | `ci.yml` job `contracts-drift` runs `gen-contracts-{ts,rs}.ps1 -Check`; needs the .NET SDK, never executed here |
| `installer/wix` (WiX v5: Agent MSI with service, Shell MSI, bundle) | Implemented-untested | No WiX toolchain in the authoring environment |
| `tools/scripts/*.ps1` (`setup-dev-vm`, `dev`, `build`, `package`, `sign`, `publish`, `gen-contracts-*`) | Implemented-untested | PowerShell 5.1 |
| `.github/workflows/ci.yml`, `release.yml` | Implemented-untested | First run will be the first real build |
| xUnit test projects (`tests/ClubShell.*.Tests`), `tests/shell-rs` | Partial | Scaffolding + first suites; coverage is thin |

---

## v1.1 — hardening and verification

Theme: turn *Implemented-untested* into *Done*, then close the gaps that block a second club.

| Item | Status | Detail |
|------|--------|--------|
| Green CI on Windows runners: `dotnet build/test`, `cargo clippy -D warnings`/`test`, `pnpm build`, WiX package | Planned | Fix whatever the first build surfaces; add `dotnet format` and `rustfmt --check` gates |
| Real Playwright e2e in CI | Planned | `windows-latest` runner: Vite mock mode first (`tests/shell-e2e`), then a `tauri dev` build with `CLUBSHELL_DEV=1` driven through the WebView2 CDP endpoint; screenshots per locale |
| ContractsGen drift gate | Planned | CI regenerates `crates/protocol` + `packages/contracts-ts` and fails on `git diff`; forbid hand edits (ARCHITECTURE §1.2) |
| Telemetry dashboards | Planned | Server-side Grafana/Metabase boards over `POST /agents/{pcId}/telemetry`: seat utilisation, crash loops, update failures, anti-cheat violations, offline minutes per PC |
| Server-provided themes | Planned | Walk `AgentServerConfig.Themes` (`ThemeRef`) through `LocalCache` into `themes\`; `refreshConfig{themes}` triggers it (`THEMING.md` §5) |
| Manifest-level signature | Planned | Add `manifestSignature` to `UpdateManifest`, verify with `Signing.VerifyManifest` (canonical form already defined) before trusting `url`/`sha256` |
| `ClubShell.Updater.exe` helper | Planned | Small .NET or Rust exe: stop service → msiexec → start service → report; today msiexec is launched directly |
| Localised theme display names | Planned | Picker prefers `settings.theme<Name>` i18n keys (`THEMING.md` §8) |
| Windows Event Log + crash dumps (`*.dmp`) collection to the server | Planned | `ARCHITECTURE.md` §9 lists the paths; upload endpoint missing |
| Agent integration tests against MockServer (register → policies → session → launch report) | Planned | `tests/ClubShell.Agent.Tests` with a spawned MockServer |
| Installer QA matrix | Planned | Win10 22H2 / Win11 23H2+, fresh + upgrade + rollback, WebView2 absent |
| Uzbek copy review by a native speaker; Cyrillic Uzbek variant decision | Planned | Current `uz.json` is Latin script |

---

## v1.2 — club operations

| Item | Status | Detail |
|------|--------|--------|
| Admin web console integration | Planned | Deep links from the Shell admin panel to the console; console-driven `ServerCommand`s (`lock`, `message`, `update`, `remoteControl`) already exist Agent-side; add per-PC live status page fed by heartbeat + telemetry |
| Printer receipts | Planned | ESC/POS over USB/LAN from the Agent (`session.ended`, shop orders, top-ups); templates per locale; fiscalisation hook for Uzbekistan (online cash register integration is server-side) |
| RFID / NFC card login hardware | Planned | HID keyboard-wedge readers already work through `auth.login{ kind: card, cardId }` (`AuthLoginRequest`); add a proper reader abstraction (PC/SC) in `ClubShell.Windows`, card enrolment flow in the admin panel, anti-passback |
| VR zone support | Planned | Seat type `vr` in tariffs/zones; SteamVR / Oculus launcher variants; headset-connected check before session start; per-minute billing already covered by tariffs |
| Booking kiosk mode | Idea | Idle screen shows free seats and lets a walk-in reserve from the PC |
| Screen-time / age restrictions | Idea | `ageRating` exists on `Game`; enforce against `User` birth date and local law |
| Per-game process allow-lists | Idea | Tighten `processAllowlist` while a game runs (anti-cheat requirement for some titles) |

---

## v2.0 — platform

| Item | Status | Detail |
|------|--------|--------|
| Diskless / iSCSI boot orchestration | Idea | `storage.gamesShare.iscsi` slot exists in `agent.json`; needs PXE/iPXE boot images, per-PC write-back cache, image versioning and a server component — large scope |
| Per-game GPU profiles | Idea | Apply NVIDIA/AMD driver profiles and display settings (refresh rate, HDR) per launch; revert on exit |
| Multi-club federation | Idea | One user/wallet across clubs; `clubId` in contracts, server-side federation; Agent mostly unaffected |
| Linux shell | Idea | Tauri already builds for Linux; the Agent (`ClubShell.Windows` P/Invoke) does not. Would need a `ClubShell.Linux` layer (systemd service, X11/Wayland kiosk session, Proton launchers) |
| Web-based Shell (browser kiosk) | Idea | The React UI already runs in a browser in mock mode; a WebSocket bridge to the Agent instead of Tauri IPC would give a zero-install shell for demo/booking terminals |
| Plugin API for third-party screens | Idea | Sandboxed iframes with a postMessage subset of `api.*` |

---

## Technical debt (as of the v1.0 repository)

Explicit and known. Items are ordered by risk.

1. **No `dotnet` build and no `cargo` build were executed in the authoring environment.** All C# and Rust
   was written to compile by construction against the pinned toolchains (`global.json` 8.0.400,
   `rust-toolchain.toml` 1.89.0, `Directory.Packages.props`). The first CI run is expected to surface
   compile errors (typos, missing `using`s, analyzer warnings promoted by `TreatWarningsAsErrors`,
   clippy `-D warnings`). Budget a fix-up pass before any other v1.1 work.
2. **TS/Vite build is verified**, mock mode was smoke-tested in a browser; the Tauri-hosted UI has not
   been run (no Rust build). Expect CSP / asset-protocol adjustments on first `tauri dev`.
3. **WiX installer is untested.** `installer/wix/*.wxs` were authored without the WiX v5 toolchain;
   service install/upgrade/rollback and the WebView2 bootstrapper need a QA pass on real machines.
4. **Anti-cheat checks are best-effort.** `EacChecker`, `FaceitChecker`, `VanguardChecker`,
   `SecureBootChecker` inspect services/drivers/registry state and can report false negatives after
   vendor updates. They are a policy signal, not a guarantee; publishers do not certify café software.
5. **Launcher integrations are fragile by nature.** Steam/Epic/Battle.net/Riot/EA/Ubisoft change CLI
   flags, URI schemes and login flows without notice; account injection paths need per-release re-testing.
6. **`ClubShell.Updater.exe` does not exist.** `UpdateApplier` falls back to detached `msiexec`, which
   relies on the MSI's own service stop/start actions.
7. **Server-provided themes are not downloaded** (`AgentServerConfig.Themes` unused) and the manifest-level
   signature is not verified (`Signing.VerifyManifest` unused).
8. **Tests are thin**: unit tests exist inside the Rust crate and for a handful of C# classes; no
   integration test exercises pipe + Agent + MockServer end to end; the Playwright `e2e` CI job covers the
   browser mock only.
9. **ContractsGen never ran in CI**: the `contracts-drift` job exists but the mirrors were hand-written. The
   generator has been executed locally (Roslyn harness) against both mirrors: hand-written code is fenced in
   MANUAL blocks and survives regeneration, the regenerated TS typechecks, but declaration order, doc comments and
   some Rust derives (`Copy`/`Default`) still differ, so the first `-Check` run will report cosmetic drift until the
   mirrors are regenerated once with the .NET SDK.
10. **Rust MSRV mismatch**: `Cargo.toml` declares `rust-version = "1.80"` while `rust-toolchain.toml`
    pins 1.89.0 (needed by transitive dependencies). Raise `rust-version` or vendor older deps.
11. **Secrets in memory**: account-pool credentials are decrypted into managed strings before injection;
    consider `SecureString`/pinned buffers and zeroing after use.
12. **Single interactive session assumption** (ARCHITECTURE §2 rule 6) is not enforced by code beyond
    policy; fast user switching must be disabled by GPO at install.
13. **Offline login** depends on the server delivering an `offlineHash` in the PBKDF2 form the Agent verifies
    (`pbkdf2$<iter>$<salt>$<hash>`, stored in `cache\offline.db`); Argon2id PHC strings fail closed. The
    server side of that contract is outside this repo and must be confirmed.

---

## Open questions

* **Vanguard**: Riot's kernel driver rejects some virtualisation/driver setups; do clubs with Valorant
  need a separate "Vanguard-compatible" policy profile (no DNS filter driver, no hooks) per PC?
* **Steam family/account sharing terms**: account pools may violate publisher ToS in some regions; the
  product needs a legal position per launcher before pools are enabled by default.
* **Fiscal receipts (Uzbekistan)**: is receipt printing purely server-side (online cash register) with the
  Agent printing a copy, or must the Agent talk to a fiscal printer directly?
* **Uzbek script**: Latin only, or both Latin and Cyrillic (`uz-Cyrl`) locales?
* **Payments**: which top-up providers (Payme, Click, Uzum) get first-class `TopupProvider` support and
  which run purely as server-side QR flows?
* **Diskless**: is v2.0 diskless worth building in-house versus integrating an existing solution (e.g.
  CCBoot-class products, "(as of 2026, verify)") through the `storage` config?
* **Linux**: is there real demand (cost-sensitive clubs with Proton-friendly catalogues) or is it a
  long-tail idea?
* **Update apply window vs. 24 h clubs**: the default 04:00–07:00 window assumes a nightly lull; 24/7
  venues need per-seat rolling updates (apply when *this* seat is free, not club-wide).
