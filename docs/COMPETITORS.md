# ClubShell — Competitive landscape

Status: informational. This document compares ClubShell with the gaming-café / esports-venue management
clients a club in Uzbekistan or the wider CIS is likely to evaluate. Everything about third-party
products is written from public product pages, documentation and community knowledge; vendors change
plans and features often, so every such statement is marked **(as of 2026, verify)** and no exact prices
are quoted. ClubShell facts refer to this repository (v1.0, see `ROADMAP.md` for what is verified).

Products covered: **Senet** (senet.cloud), **ggLeap** (ggCircuit), **SmartLaunch**, **Gizmo**
(gizmopowered), **LanCafe / EasyCafe** (the long-lived Windows cybercafé billing tools; grouped because
they target the same segment), **iCafeCloud** (iCafe8 / iCafeMenu family) and **ClubShell**.

---

## 1. Positioning in one paragraph each

| Product | Model | Positioning (as of 2026, verify) |
|---------|-------|----------------------------------|
| Senet | Cloud SaaS, per-PC subscription | Modern cloud platform for esports venues; strong in Eastern Europe/CIS, Russian-language support, online booking, tournaments, marketing tools. |
| ggLeap | Cloud SaaS, per-PC subscription | Widely deployed in North America/Europe; game-launcher account pools ("ggRock" diskless sibling), tournaments, Discord integration, rewards. |
| SmartLaunch | Cloud + on-prem hybrid, subscription | One of the oldest Windows client/server café suites; kiosk client, admin console, POS; broad launcher list. |
| Gizmo | On-prem server + cloud licensing, subscription | Server/client suite with a detailed lockdown client, deployment/imaging focus, active community; popular where clubs want data on-prem. |
| LanCafe / EasyCafe | On-prem, one-time or cheap licence | Classic timer/billing tools for small cafés; lightweight lockdown, no real launcher/account integration. |
| iCafeCloud | Cloud SaaS (with on-prem diskless companion iCafe8) | Asia-first (China/SEA) cloud management with diskless boot and game-update distribution as headline features; multi-language. |
| ClubShell | Open client, self-hosted or cloud server, no per-PC vendor licence in the client | The **client side only**: Agent service + Tauri kiosk shell with open, generated contracts, built for CIS clubs (ru/uz/en, UZS) against a central server the operator runs or buys. |

---

## 2. Feature matrix

Legend: ● full · ◐ partial / add-on · ○ none or not public · ? unknown. Third-party cells are
(as of 2026, verify). ClubShell cells reflect the code in this repository; *untested* items are marked
in `ROADMAP.md`.

| Capability | Senet | ggLeap | SmartLaunch | Gizmo | LanCafe/EasyCafe | iCafeCloud | ClubShell |
|------------|-------|--------|-------------|-------|------------------|------------|-----------|
| Kiosk lockdown (shell replacement, hotkey/Task Manager blocking, USB policy) | ● | ● | ● | ● | ◐ | ● | ● shell replacement via Winlogon `Shell`, `WH_KEYBOARD_LL`, topmost guard, USB/DNS/process policies (`PolicyEnforcer`) |
| Game launcher support (Steam, Epic, Battle.net, Riot, EA, Ubisoft) | ● | ● | ● | ● | ○ | ● | ● all six + plain EXE (`Games/Launchers/*.cs`) |
| Shared account pools with credential injection | ● | ● | ◐ | ◐ | ○ | ◐ | ● server-leased pool, AES-GCM secrets, release on exit (`AccountPool`, `AccountInjector`) |
| Cloud saves / per-user profile sync | ◐ | ◐ | ○ | ○ | ○ | ◐ | ◐ `games.cloudSave` (local root, size cap; server sync contract) |
| Anti-cheat awareness (EAC / FACEIT / Vanguard / Secure Boot state) | ◐ | ◐ | ○ | ○ | ○ | ◐ | ◐ checks + `policy.anticheat.required` gating; best-effort |
| Offline mode (timer keeps running, cached users, queued events) | ◐ | ◐ | ● | ● | ● | ◐ | ● SQLite outbox, cached offline logins (server-issued PBKDF2 hashes), capped offline minutes |
| Client auto-update with signature verification | ● | ● | ● | ● | ○ | ● | ● RSA-PSS package signature, channels, rollback markers (`UPDATES.md`) |
| Shop / food & drink orders from the seat | ● | ● | ● | ● | ◐ | ● | ● `shop.*` + `shop.orderUpdated` |
| Seat booking / reservations | ● | ● | ◐ | ◐ | ○ | ● | ● `booking.*` (server-side calendar) |
| Tournaments / leaderboards | ● | ● | ◐ | ○ | ○ | ◐ | ● `tournaments.*` (client side; server logic external) |
| In-club chat / staff messaging / call admin | ● | ● | ● | ● | ◐ | ● | ● `chat.*`, `admin.message`, `sys.callAdmin`, hotkey |
| Remote admin (screen view, remote input, lock, message, reboot) | ● | ● | ● | ● | ◐ | ● | ● `RemoteAdmin`, `ServerCommand` set |
| Diskless / image distribution | ○ (partners) | ◐ (ggRock) | ○ | ◐ (deployment tools) | ○ | ● (iCafe8) | ○ v2.0 idea; iSCSI config slot only |
| Multi-language UI | ● (incl. ru) | ● | ● | ● | ◐ | ● (zh/en/…) | ● en / ru / **uz** bundled; per-PC overrides |
| Uzbek (uz) UI | ○ ? | ○ ? | ○ ? | ○ ? | ○ | ○ ? | ● |
| Local currency formatting (UZS, no minor units) | ◐ | ◐ | ◐ | ◐ | ◐ | ◐ | ● `Money{amount, "UZS"}`, `ui.currencyFormat.minorDigits = 0` |
| Local payment providers (Payme / Click / Uzum style QR top-ups) | ◐ | ○ | ○ | ○ | ○ | ○ | ◐ `wallet.topupIntent` + `TopupProvider` contract; provider integration is server-side |
| Gamepad / controller navigation of the kiosk UI | ◐ | ◐ | ○ | ○ | ○ | ◐ | ● gilrs, stick-to-D-pad, focus ring |
| Theming / branding of the client | ◐ | ● | ◐ | ◐ | ○ | ◐ | ● JSON themes, live switch, video wallpaper (`THEMING.md`) |
| Multi-monitor (ads on secondary screens) | ◐ | ◐ | ○ | ○ | ○ | ◐ | ● `ads-*` windows, `secondaryMode` |
| Telemetry (hardware, perf, crash loops) | ● | ● | ● | ● | ○ | ● | ● 5 s metrics, heartbeat, hardware rescan |
| Pricing model | Per PC / month | Per PC / month (tiers) | Per PC / month (tiers) | Per PC / month (tiers) | One-time / cheap | Per PC / month | No client licence; cost is the server product / hosting the operator chooses |
| On-prem vs cloud | Cloud | Cloud | Hybrid | On-prem server, cloud licence | On-prem | Cloud (+ on-prem diskless) | Either: the client only needs `https://<server>/api/v1` + WS |
| Open API / open contracts | ◐ (partner API) | ◐ | ◐ | ◐ | ○ | ◐ | ● `ClubShell.Contracts` → generated TS/Rust; `SERVER_API.md`, `IPC_PROTOCOL.md` |
| Source availability | ○ | ○ | ○ | ○ | ○ | ○ | ● this repository |

Notes on specific cells:

* **Offline mode** in cloud products typically means "the timer keeps counting and syncs later"; how
  new logins and new sessions behave offline differs per vendor. ClubShell's behaviour is specified in
  `ARCHITECTURE.md` §8.
* **Anti-cheat**: no café product is "certified" by publishers; all of them, ClubShell included, can only
  check that the vendor driver/service is present and healthy and refuse to launch when policy requires
  it. Riot Vanguard's interaction with kernel-level lockdown/DNS drivers is the recurring pain point for
  every vendor.
* **Diskless** is a separate product line for the vendors that offer it (ggRock, iCafe8). ClubShell
  leaves boot orchestration to the operator's existing solution in v1.x.

---

## 3. What ClubShell does differently

1. **Open contracts as the product boundary.** Every DTO, IPC message and REST/WS call is defined once in
   `src/ClubShell.Contracts` and generated into Rust (`crates/protocol`) and TypeScript
   (`packages/contracts-ts`). A club, an integrator or a competing server vendor can implement
   `SERVER_API.md` and run the same client. The vendors above ship the client as a black box bound to
   their cloud.
2. **Bring your own server.** ClubShell is the client half only. Billing, users, shop and admin UI live
   in whatever server the operator picks (self-hosted on a club NAS, a regional provider, or a cloud).
   `tools/MockServer` is a runnable reference of the API for development.
3. **Tauri 2 shell instead of a WinForms/WPF/Electron client.** WebView2 (already on Windows 10/11) + a
   small Rust host: no bundled Chromium, low idle CPU/RAM next to a running game, modern React UI with
   code-splitting, themes as JSON, i18n as JSON. Electron-based clients carry 150–250 MB and a second
   Chromium; native clients tend to have dated UIs (as of 2026, verify per product).
4. **Rust + .NET split by trust level.** The privileged part (service, session 0, SYSTEM) is .NET 8 with
   a Win32 layer; the untrusted, player-facing part is a separate process in the kiosk user's session
   talking over an ACL'd named pipe with a per-boot token. The shell can crash or be attacked without
   touching billing state; the Agent restarts it. Most competitors run the kiosk client with elevated
   rights or fold both roles into one process.
5. **CIS / Uzbekistan first.** Uzbek UI out of the box next to Russian and English; UZS with zero minor
   digits; `uz-UZ` number/date formatting; Tashkent-timezone tests; contract slots for local QR top-up
   providers; a default apply window for nightly updates that matches local club hours. Global vendors
   treat the region as a locale afterthought (as of 2026, verify which offer `uz`).
6. **Documented lockdown internals.** `KIOSK_MODE.md`, `SHELL_REPLACEMENT.md`, `SECURITY.md` and the
   policy schema (`policies.json`) are public; an operator can audit exactly what registry keys, hooks
   and firewall rules are applied instead of trusting a vendor installer.
7. **Signed updates with rollback as a first-class flow** (`UPDATES.md`): package signatures verified
   locally with an operator-held key, channels, apply windows, mandatory updates with a 60 s notice,
   rollback markers and crash detection. Cloud vendors update silently on their own schedule.
8. **Testability without hardware.** Mock mode in the browser (`VITE_MOCK=1`), a mock Agent transport
   (`CLUBSHELL_DEV=1`) and a mock server let the whole UI and most of the Agent be developed and
   Playwright-tested on a laptop.

---

## 4. Gaps to close (where competitors are ahead)

| Gap | Who has it | ClubShell plan |
|-----|-----------|----------------|
| Turn-key server + admin console + POS | Every SaaS vendor | Out of scope for this repo by design; needs a partner server product or the operator's own. The admin console *integration* is v1.2. |
| Diskless boot / game image distribution | iCafeCloud, ggLeap (ggRock), Gizmo (deployment) | v2.0 idea (`ROADMAP.md`); v1.x relies on the operator's existing solution and the SMB games share |
| Proven anti-cheat compatibility matrix maintained per game | Senet, ggLeap publish guidance | Best-effort checks only; needs field data and a maintained per-game profile list (v1.2 idea: per-game process allow-lists) |
| Printer receipts / fiscal integration | SmartLaunch, Gizmo, iCafeCloud (POS) | v1.2 |
| RFID/NFC card readers beyond keyboard-wedge | Gizmo, SmartLaunch | v1.2 (PC/SC abstraction) |
| Marketing tools (loyalty campaigns, push, Discord) | Senet, ggLeap | Client has loyalty levels/points and `notification.push`; campaigns are server-side |
| Online booking page for players (web) | Senet, ggLeap, iCafeCloud | Server-side; the client exposes `booking.*` |
| Field-tested installer and long-running deployments | All incumbents | v1.0 is *implemented-untested* for the native parts; v1.1 is the hardening release |
| Vendor support / SLA | All incumbents | Community + integrator model; no vendor SLA on the client |
| Linux / thin-client support | ○ across the board (a few diskless products run Linux boot servers) | v2.0 idea |
| Per-seat GPU/driver profiles | ○ generally | v2.0 idea |

---

## 5. Evaluation checklist for a club

When comparing on site, run the same script on every candidate:

1. Lock the seat, press `Ctrl+Shift+Esc`, `Win+R`, `Alt+F4`, `Ctrl+Alt+Del`; unplug the network; pull
   a USB stick — what still works? (ClubShell: `policies.json → explorer`, `usb`; CAD cannot be
   hooked, only policy-limited.)
2. Log in with a shared Steam account on two seats at once — is the second lease refused?
   (`AccountPoolExhausted`.)
3. Kill the network for 10 minutes mid-session — does the timer continue, does the session sync back,
   can a new customer start? (`offline.allowNewSessions`, `maxOfflineMinutes`.)
4. Push an update during a session — what does the player see, can staff defer it, what happens when the
   update fails? (`UPDATES.md` §5.2, §6.)
5. Switch the UI to Uzbek and check the receipt/currency formatting.
6. Ask for the API documentation and the data export path — is the club's data portable?
7. Ask what runs with SYSTEM rights on the seat and whether it is separable from the player UI.

The vendors' answers change over time — re-check before every purchasing decision (as of 2026, verify).
