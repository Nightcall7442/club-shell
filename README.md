# ClubShell

[![CI](https://github.com/clubshell/club-shell/actions/workflows/ci.yml/badge.svg)](.github/workflows/ci.yml)
[![Release](https://github.com/clubshell/club-shell/actions/workflows/release.yml/badge.svg)](.github/workflows/release.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](global.json)
[![Rust 1.89](https://img.shields.io/badge/rust-1.89-orange)](rust-toolchain.toml)
[![Tauri 2](https://img.shields.io/badge/tauri-2.0-24C8D8)](apps/shell/src-tauri/tauri.conf.json)

Client software for gaming clubs and internet cafés. Every gaming PC runs two pieces: a **Windows agent**
(.NET 8 service in session 0 that owns sessions, billing timers, game launching, policy enforcement and
the link to the club's central server) and a **kiosk shell** (Tauri 2 + React full-screen UI that replaces
the Windows desktop for the player). The two talk over a named pipe; the agent talks to the server over
REST + WebSocket. Target market is Uzbekistan / CIS: UI in English, Russian and Uzbek, prices in UZS.

## Screenshots

| Lock screen | Games grid | Wallet & tariffs | Shop |
|-------------|------------|------------------|------|
| TBD (`docs/img/lock.png`) | TBD (`docs/img/games.png`) | TBD (`docs/img/wallet.png`) | TBD (`docs/img/shop.png`) |

Run the UI in mock mode (below) to see the real thing in a browser.

## Architecture

```
┌────────────────────────────────────────────────────────────────┐
│  CENTRAL SERVER (separate product)                             │
│  REST https://<server>/api/v1  ·  WS wss://<server>/ws/agent   │
└───────────────▲──────────────────────────────▲─────────────────┘
                │ HTTPS · Bearer JWT · HMAC-SHA256 request signing
                │                              │ WSS · subprotocol clubshell.v1
┌───────────────┴──────────────────────────────┴─────────────────┐
│  GAMING PC (Windows 10/11 x64)                                 │
│                                                                │
│  Session 0 ─────────────────────────────────────────────────┐  │
│  │ ClubShellAgent.exe  (.NET 8 Worker Service, LocalSystem) │  │
│  │ sessions · timers · launchers · policies · anti-cheat ·  │  │
│  │ updates · telemetry · watchdog (CreateProcessAsUser)     │  │
│  └──────────────────────────▲───────────────────────────────┘  │
│                             │ \\.\pipe\clubshell-agent          │
│                             │ 4-byte LE length + JSON envelope │
│                             │ auth.hello (shell token) · sys.ping 5 s
│  Interactive session ───────┴───────────────────────────────┐  │
│  │ clubshell-shell.exe  (Tauri 2, local kiosk user)         │  │
│  │ src-tauri: pipe client · kiosk hardening · gamepad · tray│  │
│  │ WebView2:  React 18 + Vite + Tailwind + Zustand + i18next│  │
│  │ Game processes (launched by the Agent into this session) │  │
│  └──────────────────────────────────────────────────────────┘  │
└────────────────────────────────────────────────────────────────┘
```

Only the Agent is privileged. The Shell is UI: every OS side effect (launch, kill, firewall, registry,
power, users) goes through the pipe to the Agent. Full detail in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Repository layout

| Path | What lives there |
|------|------------------|
| `apps/shell/` | Kiosk shell: React frontend (`src/`) + Tauri 2 Rust host (`src-tauri/`) |
| `config/` | Default JSON configs deployed to `C:\ProgramData\ClubShell` (`agent.default.json`, `shell.default.json`, `policies.example.json`, `themes/`) |
| `crates/protocol/` | Rust mirror of the C# contracts (serde), generated |
| `crates/winutil/` | Rust Win32 helpers (hooks, pipe, window, monitor, input) on `windows` 0.58 |
| `docs/` | Normative specs and guides (index below) |
| `installer/wix/` | WiX v5: `ClubShell.msi` (Agent service + Shell + config/themes) and the `ClubShellSetup.exe` Burn bundle (.NET 8 runtime + WebView2 bootstrapper + MSI) |
| `packages/contracts-ts/` | TypeScript mirror of the C# contracts, generated |
| `src/ClubShell.Contracts/` | Canonical DTOs / enums (System.Text.Json, camelCase, string enums) |
| `src/ClubShell.Core/` | Abstractions, config, HTTP client, realtime, security, updates. No Win32. |
| `src/ClubShell.Windows/` | Win32 layer: P/Invoke, hooks, WMI, WTS, job objects, registry, firewall, users |
| `src/ClubShell.Agent/` | `ClubShellAgent` Windows service (Worker Service) + `Install/*.ps1` |
| `tests/` | xUnit projects per .NET assembly, `shell-rs` (cargo integration tests), `shell-e2e` (Playwright) |
| `tools/ContractsGen/` | Reflection over Contracts → emits the TS and Rust mirrors |
| `tools/MockServer/` | Fastify + ws mock of the central server on `http://localhost:8080` |
| `tools/scripts/` | PowerShell dev/build/package/sign/publish scripts |
| `.github/workflows/` | `ci.yml` (web, Rust, .NET, contracts drift, Playwright e2e, Tauri bundle), `release.yml` (package + sign + GitHub release + publish) |

## Tech stack

| Layer | Technology | Version |
|-------|------------|---------|
| Agent / libraries | .NET (C# 12, nullable, `TreatWarningsAsErrors`) | SDK 8.0.400 (`global.json`) |
| Agent hosting | Worker Service, Serilog, Microsoft.Data.Sqlite | Serilog 4.0.1, Sqlite 8.0.8 |
| .NET tests | xUnit, FluentAssertions, NSubstitute, coverlet | 2.9.0 / 6.12.0 / 5.1.0 |
| Shell host | Rust 2021, Tauri 2, tokio, `windows` crate | toolchain 1.89.0 (MSRV 1.80), tauri 2, windows 0.58 |
| Shell UI | React, Vite, TypeScript, Tailwind, Zustand, react-router, i18next, framer-motion | 18.3.1 / 5.4.2 / 5.5.4 / 3.4.10 / 4.5.5 / 6.26.1 / 23.14.0 |
| Web runtime | WebView2 (evergreen, bootstrapper embedded in the Shell MSI) | Chromium 110+ target |
| Mock server | Fastify, @fastify/websocket, tsx | 4.28.1 / 10.0.1 / 4.19.0 |
| E2E | Playwright (chromium) | 1.47.0 |
| Package manager | pnpm workspaces | pnpm 9.9.0, node ≥ 20 |
| Installer | WiX Toolset | v5 |
| Formatting | Prettier (TS), `.editorconfig` (C#/Rust), rustfmt, clippy | Prettier 3.3.3 |

## Prerequisites

| Goal | Needs |
|------|-------|
| UI only, in a browser (mock mode) | Node 20+ and pnpm 9 — works on any OS |
| Full stack (Agent + Shell + installer) | Windows 11 x64 (Windows 10 1809+ at runtime) |
| .NET projects | .NET 8 SDK 8.0.400+ |
| Rust / Tauri | Rust 1.89 via `rustup` (the toolchain file installs it), Visual Studio Build Tools 2022 with the "Desktop development with C++" workload, Windows 10/11 SDK |
| Running the Shell | WebView2 Runtime (preinstalled on Windows 11; the MSI embeds the bootstrapper) |
| Installer | WiX v5 SDK, restored from NuGet by `installer/wix/ClubShell.Installer.wixproj` (no global `wix` tool needed) |
| Scripts | Windows PowerShell 5.1 (all `*.ps1` are 5.1-compatible) |

`tools/scripts/setup-dev-vm.ps1` installs everything above on a fresh Windows VM.

## Quick start (UI only, in a browser)

```powershell
pnpm install
pnpm mock                                    # mock central server → http://localhost:8080
$env:VITE_MOCK = '1'; pnpm --filter @clubshell/shell dev   # → http://localhost:1420
```

On bash: `VITE_MOCK=1 pnpm --filter @clubshell/shell dev`. `VITE_MOCK=1` replaces every Tauri command and
event with in-browser handlers (`apps/shell/src/mocks/handlers.ts`), so no Agent, pipe or Windows is
needed. The mock keeps state (login → session → ticking timer → wallet) in memory for the tab.

Demo accounts (from `apps/shell/src/mocks/data.ts`):

| Login method | Credentials |
|--------------|-------------|
| Password | `demo` / `1234` (regular), `vip` / `1234` (VIP), `player` / `player` |
| Guest | any display name |
| Card | any card id; ids ending in `9` log in as VIP |
| QR | start a QR login; the mock auto-confirms it after a few seconds |
| Session unlock PIN | `1234` |
| Admin PIN (exit hotkey `Ctrl+Alt+Shift+F12`) | `0000` |
| `banned` | any password → banned error, to see the failure path |

## Full dev loop (Windows)

```powershell
Copy-Item .env.example .env         # edit if needed; defaults point at the mock server
.\tools\scripts\dev.ps1             # mock server + `tauri dev` (Rust host with the mock Agent transport)
.\tools\scripts\dev.ps1 -Agent      # ... plus the real Agent as a console process (--console --dev)
.\tools\scripts\dev.ps1 -WebOnly    # mock server + Vite in browser mock mode (no Rust toolchain needed)
```

`dev.ps1` reads `.env`, starts the mock server on `http://localhost:8080`, exports `CLUBSHELL_DEV=1` and runs
`pnpm tauri dev`, which builds `src-tauri` and opens the real kiosk window over the Vite dev server with
hardening off and the in-process mock Agent transport. `-Agent` additionally starts the Agent against
`http://localhost:8080/api/v1` (`--console --dev` relaxes TLS pinning and points at the mock); a debug Shell
build probes the pipe and uses it when the Agent is up. Use the exit hotkey and admin PIN to get back to the
desktop. Individual pieces:

```powershell
pnpm mock                                   # mock server only
dotnet run --project src/ClubShell.Agent -- --console --dev   # Agent in the foreground
pnpm tauri dev                              # Shell only (debug build: real pipe when an Agent runs, mock transport otherwise)
```

## Building

```powershell
.\tools\scripts\build.ps1                   # everything, Release
.\tools\scripts\build.ps1 -Target Agent     # Targets: All | Dotnet | Rust | Web | Installer | Contracts | Agent | Shell
```

What the targets do, if you want to run them by hand:

| Step | Command |
|------|---------|
| .NET solution | `dotnet build ClubShell.sln -c Release` (output under `artifacts/`, see `Directory.Build.props`) |
| Agent publish | `dotnet publish src/ClubShell.Agent -c Release -r win-x64` |
| Rust crates | `cargo build --release --workspace` (target `x86_64-pc-windows-msvc` from `.cargo/config.toml`) |
| Shell (frontend + Tauri) | `pnpm --filter @clubshell/shell build` then `pnpm tauri build` → `target/x86_64-pc-windows-msvc/release/clubshell-shell.exe` |
| Installer | `dotnet build installer/wix/ClubShell.Installer.wixproj -c Release -p:Version=<ver>` (WiX SDK from NuGet) → `ClubShell.msi` + `ClubShellSetup.exe` |
| Package | `.\tools\scripts\package.ps1 -Version <ver> -Channel stable\|beta -BaseUrl <url>` — assembles `artifacts/release/<ver>/` (`ClubShell-<ver>.msi`, `ClubShellSetup-<ver>.exe`, `ClubShell-Shell-<ver>.msi`, `agent/`, `manifest.json`, `SHA256SUMS.txt`) and `artifacts/release/ClubShell-<ver>.zip` |
| Sign | `.\tools\scripts\sign.ps1 -Files <globs>` — Authenticode via `CODESIGN_PFX_PATH` / `CODESIGN_PFX_PASSWORD` (or `-Thumbprint`); `.\tools\scripts\sign.ps1 -ManifestPath <release>/manifest.json -ManifestKeyPem <key.pem>` — RSA-PSS package + manifest signatures (canonical form in `src/ClubShell.Core/Security/Signing.cs`) |
| Publish | `.\tools\scripts\publish.ps1 -Target S3\|Http\|Folder -Destination <url> [-Channel stable\|beta]` — uploads the release folder and the channel manifest, then verifies it |

The installer (`installer/wix`) produces `ClubShell.msi` (service `ClubShellAgent`, LocalSystem, delayed
auto-start, recovery configured, plus the Shell under `C:\Program Files\ClubShell\Shell`, config defaults and
themes; `INSTALLSHELL=0` for Agent-only) and the `ClubShellSetup.exe` Burn bundle that chains the .NET 8 runtime
and the WebView2 bootstrapper in front of it. Manual service registration without the MSI:
`src/ClubShell.Agent/Install/install.ps1` (or `ClubShellAgent.exe --install` from the published folder).

## Configuration

Runtime data lives in `C:\ProgramData\ClubShell` (defaults copied from `config/` on first start):

| File | Purpose |
|------|---------|
| `agent.json` | Agent config: server URLs, club API key, IPC, kiosk user (`shell.kioskUser.name`, default `club` everywhere: code, `config/agent.default.json`, `install.ps1 -KioskUser`, the MSI `KIOSKUSER` property), session, offline, games/launchers, storage, updates, telemetry, anti-cheat, remote admin, power, logging |
| `shell.json` | Shell config: locale (`en`/`ru`/`uz`), theme, kiosk hardening, idle, ads, gamepad, monitors, UI, feature flags |
| `policies.json` | Last applied server policy snapshot (process allowlist, USB, web filter, explorer lockdown, power, update channel) |
| `themes\*.json` | Themes (`default.json` mandatory); applied as `--c-*` CSS custom properties |
| `cache\`, `logs\`, `secure\` | Cached server data + offline queue, structured JSON logs, DPAPI-wrapped secrets |

Any `agent.json` key can be overridden with `CLUBSHELL__<Section>__<Key>` (double underscore), e.g.
`CLUBSHELL__server__baseUrl`. Command line: `--config <path>`, `--console`, `--dev`, `--install` / `--uninstall`.

Development variables come from `.env` (copy `.env.example`):

| Variable | Default | Used by |
|----------|---------|---------|
| `CLUBSHELL_SERVER_URL` | `http://localhost:8080/api/v1` | scripts → `agent.json → server.baseUrl` |
| `CLUBSHELL_WS_URL` | `ws://localhost:8080/ws/agent` | scripts → `server.wsUrl` |
| `CLUBSHELL_CLUB_API_KEY` | `dev-club-api-key` | first agent registration |
| `CLUBSHELL_UPDATE_CHANNEL` | `stable` | `stable` or `beta` |
| `CLUBSHELL_LOG_LEVEL` | `Debug` | Agent Serilog level |
| `MOCK_SERVER_PORT` | `8080` | `tools/MockServer` |
| `VITE_MOCK` | `1` | Shell frontend: browser mock mode |
| `VITE_SERVER_URL` | `http://localhost:8080` | dev-only links (QR pages, images) |
| `TAURI_SIGNING_PRIVATE_KEY[_PASSWORD]` | — | Tauri updater signing |
| `CODESIGN_PFX_PATH`, `CODESIGN_PFX_PASSWORD` | — | `sign.ps1` Authenticode |

## Documentation

| Document | Contents |
|----------|----------|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Normative: component map, process/session model, trust boundaries, data flows, threading, startup, recovery, offline mode, file layout, config schemas |
| [docs/IPC_PROTOCOL.md](docs/IPC_PROTOCOL.md) | Shell ⇄ Agent named-pipe protocol v1: framing, envelope, every request/response/event, error codes |
| [docs/SERVER_API.md](docs/SERVER_API.md) | Central server REST `/api/v1` + WebSocket `/ws/agent` as consumed by the Agent; auth, signing, endpoints |
| [docs/TAURI_COMMANDS.md](docs/TAURI_COMMANDS.md) | Every `#[tauri::command]` and webview event; the frontend `api` / `invoke` wrapper in `lib/tauri.ts` |
| [docs/TAURI_SHELL.md](docs/TAURI_SHELL.md) | Shell internals: window setup, pipe client, state, tray, gamepad, logging |
| [docs/KIOSK_MODE.md](docs/KIOSK_MODE.md) | Kiosk hardening: keyboard hooks, blocked combos, taskbar, topmost guard, multi-monitor, idle, exit hotkey |
| [docs/SHELL_REPLACEMENT.md](docs/SHELL_REPLACEMENT.md) | Replacing `explorer.exe` for the kiosk user: Winlogon `Shell`, auto-logon, watchdog, rollback |
| [docs/GAME_LAUNCHERS.md](docs/GAME_LAUNCHERS.md) | Steam / Epic / Battle.net / Riot / EA / Ubisoft / plain exe launch strategies, account pool, cloud saves |
| [docs/ANTICHEAT.md](docs/ANTICHEAT.md) | Coexistence with EAC, FACEIT, Vanguard, Secure Boot / TPM checks |
| [docs/UPDATES.md](docs/UPDATES.md) | Update state machine: manifests, channels, signature verification, staging, apply, rollback |
| [docs/SECURITY.md](docs/SECURITY.md) | Threat model, secrets at rest, pipe ACLs, request signing, hardening checklist |
| [docs/THEMING.md](docs/THEMING.md) | Theme file schema and the `--c-*` CSS variable mapping |
| [docs/COMPETITORS.md](docs/COMPETITORS.md) | Feature comparison with existing club software |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Planned work by milestone |

## Testing

```powershell
dotnet test ClubShell.sln                       # xUnit: Contracts, Core, Windows, Agent test projects
cargo test --workspace                          # crates + tests/shell-rs (kiosk helpers, pipe client)
cargo lint                                      # alias: clippy --workspace --all-targets -- -D warnings
pnpm typecheck                                  # tsc across every workspace package
pnpm lint                                       # prettier --check
pnpm test:e2e                                   # Playwright against `vite dev` in VITE_MOCK=1 (auto-started)
```

E2E needs browsers once: `pnpm --filter @clubshell/shell-e2e exec playwright install chromium`.
`ClubShell.Windows.Tests` and `tests/shell-rs` touch real Win32 APIs and only run on Windows.

## Contributing

- **Contracts are canonical in C#.** Edit `src/ClubShell.Contracts` only, then regenerate the mirrors:
  `.\tools\scripts\gen-contracts-ts.ps1` (→ `packages/contracts-ts/src`) and
  `.\tools\scripts\gen-contracts-rs.ps1` (→ `crates/protocol/src`). Hand edits to generated files are
  rejected in review; CI diffs the regenerated output.
- **C#:** file-scoped namespaces, `Nullable` + `ImplicitUsings` on, C# 12, `TreatWarningsAsErrors`,
  `AnalysisLevel=latest-recommended`, style enforced at build (`EnforceCodeStyleInBuild`). Tests:
  xUnit + FluentAssertions + NSubstitute.
- **Rust:** edition 2021, `cargo fmt`, `cargo lint` must pass (`clippy -D warnings`), tokio, no
  `unsafe` outside `crates/winutil` and `src-tauri/src/kiosk`.
- **TypeScript:** `strict`, ESM, Prettier (`pnpm format`), no `any` at module boundaries; types come
  from `@clubshell/contracts`.
- **PowerShell:** 5.1-compatible, `Set-StrictMode -Version Latest`, comment-based help on every script.
- Language of code, comments, docs and commit messages: English. UI strings go through i18next
  (`apps/shell/src/i18n/{en,ru,uz}.json`), all three locales updated together.
- Keep `docs/*.md` in sync with behaviour; `ARCHITECTURE.md` is normative and the Contracts project is
  the tie-breaker when a document and the code disagree.

## Status

The TypeScript/Vite build and the mock-mode UI are verified (tsc clean, `vite build` clean, smoke-tested
in a browser). The .NET and Rust code were authored without compiling in this environment — expect a
first-build fix pass (typos, missing usings, borrow-checker nits) before `dotnet build` and `cargo lint`
go green. The installer and CI workflows have not been executed on a real machine yet.

## License

[MIT](LICENSE) © 2026 ClubShell contributors.

---

## Кратко по-русски

**ClubShell** — клиентское ПО для компьютерных клубов (Узбекистан / СНГ; интерфейс на английском,
русском и узбекском; валюта — сум). На каждом игровом ПК работают две части:

- **Агент** (`ClubShellAgent`) — служба Windows на .NET 8, работает от `LocalSystem` в сессии 0. Отвечает за
  сеансы и таймеры, запуск игр (Steam, Epic, Battle.net, Riot, EA, Ubisoft, exe), политики (блокировка
  процессов, USB, DNS-фильтр, реестр), античит-проверки, обновления, телеметрию и связь с сервером клуба
  (REST `/api/v1` + WebSocket `/ws/agent`, подпись запросов HMAC-SHA256).
- **Оболочка** (`clubshell-shell.exe`) — киоск на Tauri 2 + React, заменяет рабочий стол Windows для
  локального киоск-пользователя (`club` по умолчанию: `agent.default.json`, `install.ps1 -KioskUser`,
  свойство MSI `KIOSKUSER`). Агент запускает её в интерактивной сессии через `CreateProcessAsUser`; общаются
  через именованный канал `\\.\pipe\clubshell-agent`. Горячая клавиша выхода — `Ctrl+Alt+Shift+F12`
  плюс PIN администратора.

Быстрый старт без Windows и без Агента (только интерфейс в браузере):

```
pnpm install
pnpm mock
VITE_MOCK=1 pnpm --filter @clubshell/shell dev   # → http://localhost:1420
```

Демо-логины: `demo` / `1234`, `vip` / `1234`, гость; PIN сеанса `1234`, PIN администратора `0000`.

Полный цикл разработки на Windows — `tools/scripts/dev.ps1`; сборка — `tools/scripts/build.ps1`;
конфигурация — `C:\ProgramData\ClubShell\{agent.json,shell.json,policies.json,themes\}`.
Документация — в `docs/` (главный документ — `ARCHITECTURE.md`). Контракты (DTO) канонически описаны
на C# в `src/ClubShell.Contracts`, зеркала для TypeScript и Rust генерируются скриптами
`gen-contracts-*.ps1`.

Состояние: TypeScript-сборка и UI в mock-режиме проверены; код на .NET и Rust написан без компиляции в этой
среде — при первой сборке потребуется проход по ошибкам компилятора. Лицензия — MIT.
