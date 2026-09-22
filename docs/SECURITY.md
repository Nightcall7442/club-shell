# ClubShell — Security

Status: descriptive with a normative checklist (section 13). Every control named here points at the code that
implements it; when the code changes, this document changes with it. Related: `ARCHITECTURE.md` §3 (trust
boundaries, secrets on disk), `IPC_PROTOCOL.md` §1–§4, `SERVER_API.md` §2, `KIOSK_MODE.md`, `SHELL_REPLACEMENT.md`,
`ANTICHEAT.md`.

---

## 1. Threat model

| Actor | Goal | Capabilities assumed | Primary controls |
|-------|------|----------------------|------------------|
| **Untrusted kiosk user** (the player) | leave the Shell, run own programs, extend play time for free, steal pooled game accounts, read other players' data | physical keyboard/mouse/USB; runs code as the kiosk account (any game is arbitrary code); can read files the account can read | kiosk lockdown (`KIOSK_MODE.md`), non-admin account with rotating password, pipe DACL + shell token, session state owned by the Agent, secrets never reachable by the account, profile reset |
| **LAN attacker** (another PC, rogue Wi-Fi client) | impersonate the server, replay or forge Agent requests, reach the Agent's pipe | on-path or same-subnet position; no admin on the PC | TLS + optional SPKI pinning, HMAC request signing with timestamp window, JWT + refresh rotation, no inbound listeners on the PC |
| **Malicious game / launcher / mod** | persist beyond the session, harvest credentials, tamper with the Shell | runs as the kiosk user inside a job object in the interactive session | job objects + tree kill, allow-list, Credential Manager cleanup, config backup/restore, profile wipe, `injected()` input passes but processes are killed by policy |
| **Compromised or hostile server** | push arbitrary commands, ship a malicious update, exfiltrate | full control of every API response and WS push | update packages verified with an RSA key the server does not hold, path/argument validation of everything server-supplied (`exePath`, lease files, `blockedDomains`), registry writes only under fixed keys, no shell execution |
| **Local administrator** | out of scope | full control of the PC | not defended against; the Agent trusts Administrators on the pipe (diagnostics) |

Non-goals: cheat detection (vendor anti-cheat does it; ClubShell only checks its health), DRM, protection against
an attacker with physical access to the disk (use BitLocker: DPAPI-LocalMachine secrets are recoverable by any
administrator of the same machine).

---

## 2. Trust boundaries

```
  untrusted                    semi-trusted (kiosk user)                trusted (SYSTEM)                        remote
  ─────────                    ────────────────────────                 ────────────────                        ──────
  player ──(Layer 1+2 kiosk)──▶ clubshell-shell.exe ──(pipe DACL +      ClubShellAgent.exe ──(TLS, JWT, HMAC)──▶ server
  USB / keyboard                WebView2 renderer,      shell token,     session 0, LocalSystem                   admin panel
                                CSP, no Node, no fs     client exe check)                                         (out of scope)
                                                                              │
  games ──(job object, allowlist, profile wipe)──────────────────────────────▶│ every input treated as hostile
```

| Boundary | Enforcement (code) |
|----------|--------------------|
| Player → Shell | `apps/shell/src-tauri/src/kiosk/*` hooks and guards; `tauri.conf.json → app.security.csp` (`default-src 'self'`, `object-src 'none'`, `frame-ancestors 'none'`, `connect-src` limited to the Tauri IPC origins and `https:`); the webview has no filesystem or shell plugin — every effect goes through `#[tauri::command]` proxies to IPC |
| Shell → Agent | `PipeSecurityFactory.Create` DACL, `PipeSecurityFactory.ValidateClient`, `ShellTokenStore.Verify` in `auth.hello`, per-connection rate limit and frame cap (`PipeServer`) |
| Agent → Server | `RetryPolicy` handler (TLS 1.2+, SPKI pins), `ServerClient` (Bearer + `Signing.Sign`), `TokenStore` (DPAPI) |
| Agent → OS | `ProcessAsUser` with per-call `PrivilegeScope`, `ProcessStartSpec` never uses `UseShellExecute`, registry writes only through `RegistryHelper` under the keys listed in `PolicyRegistry`, `ShellRegistry`, `FolderRedirect` |
| Update package → Agent | `UpdateDownloader` size + SHA-256 + RSA-PSS (`Signing.VerifyFileSignatureAsync`), `UpdateManifest.IsNewerThan` downgrade protection |

---

## 3. IPC: pipe ACL, shell token, client validation

### 3.1 DACL (`PipeSecurityFactory.Create(kioskSid)`)

| Principal | Rights |
|-----------|--------|
| `NT AUTHORITY\SYSTEM` | `FullControl` |
| `BUILTIN\Administrators` | `FullControl` (diagnostics clients) |
| kiosk user SID | `ReadWrite \| Synchronize` **allow**, `CreateNewInstance` **deny** (cannot squat the pipe name) |
| everyone else | no ACE → access denied |

Before the kiosk account is provisioned the DACL contains only SYSTEM and Administrators; `PipeServer` re-reads
`IKioskCredentials.Sid` for every new instance and calls `ShellTokenStore.EnsureAcl`. The pipe is created with
`NamedPipeServerStreamAcl.Create(..., ipc.maxConnections, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, ...)`.

### 3.2 Client validation (`PipeSecurityFactory.ValidateClient`, `PipeServer.ClientValidation`)

For every accepted connection, without impersonation:

1. `GetNamedPipeClientProcessId` → pid; `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`;
   `QueryFullProcessImageName`; `ProcessIdToSessionId`; `OpenProcessToken(TOKEN_QUERY)` → `WindowsIdentity`.
2. `IsTrusted` = token user is the kiosk SID, or `IsSystem`, or member of `Administrators`. Otherwise dropped.
3. `ClientValidationMode` (default `ExePath`): kiosk-user clients must run from `shell.exePath`
   (`PathsEqual`). `SignatureVerified` additionally requires the client's Authenticode signer thumbprint to equal
   `ExpectedSignerThumbprint` or the Agent's own signer (`AgentSignerThumbprint`); when neither is known (unsigned
   Agent, no thumbprint configured) there is nothing to compare against and **every** client is dropped, because
   falling back to the weaker exe check would silently downgrade the mode the operator asked for. `None` disables the
   exe check (tests only).
4. Privileged clients (SYSTEM / Administrators) skip steps 3 — they can do anything to the PC anyway.

### 3.3 Shell token (`ShellTokenStore`)

| Property | Value |
|----------|-------|
| Generation | `PipeServer.StartAsync` → `ShellTokenStore.Generate(kioskSid)`: `RandomNumberGenerator.GetBytes(32)` as 64 lowercase hex chars, written to `agent.json → ipc.shellTokenPath` (`secure\shell.token`) **before** the pipe opens and before the watchdog launches the Shell |
| File ACL (`ApplyAcl`) | inheritance removed; SYSTEM and Administrators `FullControl`, kiosk SID `Read` only |
| Lifetime | new token at every Agent start; a Shell that outlives an Agent restart re-reads the file on reconnect |
| Verification | `auth.hello{shellToken}` → `ShellTokenStore.Verify`: `IsHex64` shape check, then `CryptographicOperations.FixedTimeEquals` |
| Delivery to the Shell | `CLUBSHELL_SHELL_TOKEN` environment variable carries the **path**, not the value (`ShellLauncher.BuildEnvironment`) |
| Failure | `unauthorized`, connection closed after the response is flushed (`connection.CloseReason = "auth.hello rejected"`) |

Why both a DACL and a token: the DACL stops other accounts; the token plus the exe-path check stop other processes
of the kiosk account (a game) from talking to the Agent. A game can read the token file (kiosk `Read`) but cannot
pass `ExePath` validation unless it injects into the Shell process itself — which is why the Shell must stay a
sealed, signed binary (`SignatureVerified` mode on production builds).

### 3.4 Connection hygiene (`PipeServer`)

| Control | Value |
|---------|-------|
| `HelloTimeout` | 5 s to send `auth.hello`, else closed |
| Pre-hello requests | everything except `auth.hello` → `unauthorized` |
| Frame cap | `min(ipc.maxMessageBytes, IpcEnvelope.MaxFrameBytes = 4 MiB)`; larger → `protocolError`, connection closed |
| Rate limit | token bucket `ipc.requestsPerSecond` per connection → `rateLimited{retryAfter}`; `sys.ping` exempt |
| Concurrency | bounded per connection (`connection.Gate`); `ipc.maxConnections` instances |
| Heartbeat | no `sys.ping` for `heartbeatIntervalSec × missedHeartbeatsBeforeKill` → connection dropped, Shell terminated (`KillShellOnHeartbeatLoss`) |
| Schema | every payload deserialised into the Contracts type; unknown names → `notFound`; validation errors → `validation{field}` |
| Client errors | `sys.logClientError` capped at `MaxClientErrorsPerSecond` = 10 |

---

## 4. Server: TLS, JWT, request signing, clock skew

| Control | Implementation |
|---------|----------------|
| TLS | `RetryPolicy` builds the `SocketsHttpHandler`; certificate chain must validate; optional SPKI pinning: `server.tlsPinSha256[]` = base64 SHA-256 of the SubjectPublicKeyInfo (`RetryPolicy.ComputeSpkiPin`); the `ClubShell:Dev` relaxation only exists in dev configuration |
| Identity | `POST /agents/register` with `X-Club-Key` (`server.clubApiKey`) + HWID (`Hwid`, SHA-256 over SMBIOS UUID / disk serial / MAC from `HardwareInventory`, persistent fallback when fewer than `Hwid.MinComponents`) → `accessToken` (RS256 JWT ≈ 1 h), `refreshToken` (30 d, single-use rotation), `signingSecret` (32-byte base64) |
| Every request | `Authorization: Bearer <accessToken>` + `X-Timestamp` + `X-Signature` (`ServerClient` auth section, `server.signingEnabled` default true) |
| Signature | `Signing.Sign(secret, unixSeconds, method, pathAndQuery, bodySha256Hex)` = lowercase hex `HMAC-SHA256(secret, ts + METHOD + PATH + BODY_HASH)`; `BODY_HASH` = SHA-256 of the raw body, `Signing.EmptyBodySha256` for none; `PATH` includes the query and the `/api/v1` prefix exactly as sent |
| Clock skew | server accepts ±300 s (`server.clockSkewToleranceSec`, `Signing.DefaultSkew`); the Agent signs with `ServerClient.ServerNow` = local clock + offset learned from `serverTime` in register/refresh responses; a `401 unauthorized{reason: clockSkew}` with `X-Server-Time` corrects the offset and retries **once** |
| Replay | server-side uniqueness of (`pcId`, `X-Timestamp`, `X-Signature`); the Agent additionally sends `Idempotency-Key` on creating POSTs so retries are safe |
| Expiry | `401{reason: expired}` → `POST /agents/refresh` once (rotates the refresh token; a new `signingSecret` replaces the old one); refresh failure → re-register |
| User scope | `X-User-Token` for user endpoints; the server checks the user is bound to this `pcId` |
| WebSocket | `wss://…/ws/agent?token=<accessToken>`, subprotocol `clubshell.v1`; reconnect with backoff; commands are acked, never executed without a valid envelope |
| Tracing | `X-Trace-Id` propagates the IPC envelope id; `User-Agent` / `X-Agent-Version` / `X-Shell-Version` for `426 versionMismatch` |

`Signing.Verify` (constant-time compare + skew window) exists in Core for tests and tooling; the Agent only signs.

---

## 5. Token and secret storage

| Secret | Where | Protection |
|--------|-------|------------|
| Agent tokens (`accessToken`, `refreshToken`, `signingSecret`, `expiresAt`) | `secure\agent.tokens` (`ClubShellPaths.AgentTokensFileName`) | `TokenStore` + `DpapiTokenProtector`: `ProtectedData.Protect(..., entropy "ClubShell.Agent.tokens.v1", DataProtectionScope.LocalMachine)`; unreadable file → treated as empty → re-registration |
| User tokens (`X-User-Token`) | same file, `UserTokens` | held by the Agent only; **never** sent to the Shell (the Shell gets `User` DTOs, not tokens) |
| Kiosk account password | `secure\kiosk.cred` | same `ITokenProtector`; in memory as `IKioskCredentials.Password`; also the LSA secret `DefaultPassword` for auto-logon (`ShellRegistry.SetAutoLogon`) — never the clear-text registry value |
| Shell token | `secure\shell.token` | plain hex by design (the Shell must read it); ACL kiosk `Read` only; regenerated per Agent start |
| Games share credentials | `secure\share.cred` | DPAPI |
| Account-pool secrets | memory only (`ActiveLease`) | see section 6 |
| Admin PIN | `shell.json → kiosk.adminPinHash` (sha256 hex) | verified by the Agent (`SystemHandlers.UnlockAdmin`), 3 failures/min, tokens 32 random bytes with 5 min lifetime; `settings_get_shell_config` blanks the hash before it reaches the webview |
| `NullTokenProtector` | tests only | pass-through; must never be registered in production DI |

`secure\` is SYSTEM/Administrators only (`install.ps1`, `ARCHITECTURE.md` §9); the kiosk user cannot list it. DPAPI
LocalMachine means any process running as SYSTEM or an administrator on the same machine can decrypt — the
boundary protected is "kiosk user vs. machine", not "administrator vs. machine".

---

## 6. Secrets handling in code

| Rule | Implementation |
|------|----------------|
| Account-pool secrets are decrypted late and kept off disk | `AccountPool.LeaseAsync` → `Signing.DecryptAccountPoolSecret(signingSecret, secret)`: key = `HKDF-SHA256(signingSecret, info "account-pool")`, AES-256-GCM `nonce(12) ‖ ciphertext ‖ tag(16)`; key material zeroed (`CryptographicOperations.ZeroMemory`) after use |
| Single accessor | `ActiveLease.RevealSecret()`; `ActiveLease.ToString()` is redacted (`lease <id> (<launcher> account <user>, expires …)`) and is what the logs print |
| Secrets never enter environment blocks | `InjectionResult.EnvVars` is documented "never credentials"; launcher args travel in `CLUBSHELL_LAUNCHER_ARGS` and are stripped from the block before `CreateProcessAsUser` (`LauncherBase.SplitEnv`) |
| Config patches are reversible | `AccountInjector` backs up every touched file (`FileBackups`, ≤ 8 MiB) and restores or deletes it after exit; Credential Manager entries matching `CredentialFilters` are deleted under the kiosk token |
| Server-provided files are confined | `ProfileFiles.ApplyAsync` refuses any `extra.files` target outside the kiosk profile (`IsInsideProfile`) |
| Passwords are redacted in logs | `LoggingSetup`: `.Destructure.ByTransforming<AuthRequest>(r => r.Redacted())`; `sys.unlockAdmin` logs only the reason, never the PIN; Steam `-login` arguments are not logged |
| No secrets over IPC | the Shell never receives tokens, the kiosk password, pool secrets or the PIN hash |

---

## 7. Least privilege

| Component | Account | Notes |
|-----------|---------|-------|
| `ClubShellAgent.exe` | `NT AUTHORITY\SYSTEM` (`register-service.ps1`, `obj= LocalSystem`) | needed for `WTSQueryUserToken`, LSA secrets, `USBSTOR`, firewall, user management, hive loading |
| Privileges | `SeTcbPrivilege` (`GetUserToken`, `RunAsSystemInSession`), `SeAssignPrimaryTokenPrivilege` + `SeIncreaseQuotaPrivilege` (`CreateProcessAsUser`) | enabled per call by `PrivilegeScope` and disabled again on dispose; never left on |
| `clubshell-shell.exe` | kiosk user | no privileged API; every OS effect is an IPC request the Agent validates |
| Kiosk user | local, `BUILTIN\Users` only (`LocalUserManager.AddToGroup` refuses `Administrators`), `UserCannotChangePassword`, random 24-char password rotated on start | cannot read `secure\`, `agent.json` is read-only for it, `logs\` append only |
| Games | kiosk user, inside a `JobObject` (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`) | tree-killed on exit / session end; `processAllowlist` kills anything else in the kiosk session |
| Fallback overlay | SYSTEM in the interactive session (`ProcessAsUser.RunAsSystemInSession`) | documented as the only SYSTEM process the player can see; never player-facing programs |

Input validation at the boundaries:

| Input | Check |
|-------|-------|
| `games.launch.extraArgs` | `GameLaunchService.SanitizeArgs`: control characters stripped, ≤ 512 chars |
| `launcherAppId` | per launcher: digits (Steam), alphanumeric/underscore (Battle.net, Riot), positive int (Ubisoft), `Uri.EscapeDataString` in URLs (Epic, EA) |
| `game.exePath` / `installPath` | must resolve to an existing file (`GameDetector.ResolveExe`); allow-list / deny-list applied (`PolicyDenies`) |
| `policy.shellReplacement.shellExe` | absolute path to an existing file, signer logged |
| Registry policy | only the fixed keys in `PolicyRegistry.RulesFor`, `ShellRegistry`, `FolderRedirect`; snapshot/revert |
| `webFilter.blockedDomains` | resolved to **public** IPv4 only (`WebFilterPolicyModule.IsPublicIpv4`); LAN, loopback and the sink-hole address are never firewalled. `DnsFilter.ProtectedDomains` (launcher, CDN and anti-cheat back-ends) are dropped from the block list, and every address they resolve to is removed from the firewall set — a blocked domain parked on a shared CDN must not take Steam or Vanguard down with it |
| `blockedKeyCombos` | parsed (`KeyCombo.TryParse`, `BlockedCombo::parse`); `Ctrl+Alt+Del` dropped; malformed entries skipped |
| IPC payloads | Contracts DTOs, `validation` errors, size and rate caps |

---

## 8. Logging hygiene

- Serilog compact JSON (`LoggingSetup`), one line per event, daily rolling, `logging.retainDays` retention, size cap
  `logging.maxFileMb`. Windows Event Log sink for Fatal / self-log.
- Never logged: passwords, tokens, signing secrets, pool secrets, shell token value, PIN, card ids. Logged:
  usernames, pcId, session ids, game titles, pids, exe paths, policy notes.
- Pipe frames are hex-dumped only at `Verbose` (first 256 bytes) and `Verbose` is never a production level.
- The Shell (`tracing` JSON) forwards `console.error` through `sys_log_client_error`, rate-limited on the Agent.
- Crash reports (`CrashRecovery`) include the last 50 Shell log lines — those are subject to the same redaction
  rules because they are the Shell's own log.
- `logs\` is kiosk-user append-only; a player can write noise into the Shell log but cannot read the Agent log's
  history beyond what the ACL allows (`install.ps1` grants `(OI)(CI)M` on `logs\` — treat Shell logs as untrusted).

---

## 9. Network exposure

| Direction | What | Notes |
|-----------|------|-------|
| Inbound | **nothing** | the Agent opens no TCP/UDP listener; the named pipe is local (`\\.\pipe\clubshell-agent`); remote pipe access would require SMB plus a principal in the DACL — block inbound 445 on the club firewall anyway |
| Outbound | HTTPS `server.baseUrl`, WSS `server.wsUrl` | pinned/validated TLS; proxied through the resilience pipeline |
| Outbound | cloud-save bundles, update packages, cover images | HTTPS to URLs the server supplies; every package is hash- and signature-verified; bundle size capped |
| Outbound | `WakeOnLan.SendAsync` / `SendManyAsync` magic packets (UDP broadcast) | send-only, on server command |
| Outbound | DNS to `webFilter.dnsServers` | set per adapter by `DnsFilter` |
| Local | hosts-file sink-hole, `ClubShell-WebFilter-Block-N` outbound firewall rules | only public IPv4s of blocked domains, chunked 200 per rule |
| Remote admin | screen capture / input injection | initiated by the server over the existing WS, executed by the Agent; indicator toast shown when `remoteAdmin.showIndicator`; `allowScreenCapture` / `allowRemoteInput` gates in `agent.json` |

---

## 10. Update verification

1. `GET /updates/{channel}/manifest?component=…&current=…` → `UpdateManifest{component, version, url, sha256, size,
   publishedAt, signature, mandatory}`.
2. `UpdateManifest.IsNewerThan(currentVersion)` — downgrade refused unless the server flags a mandatory rollback
   (`ARCHITECTURE.md` §11).
3. Download (resumable) to `cache\updates\<component>-<version>.msi`.
4. `UpdateDownloader` verification, in order: file length == `size`; `Signing.Sha256FileAsync` == `sha256`;
   `Signing.VerifyFileSignatureAsync(path, signature, publicKey)` — RSA-PSS with SHA-256 over the raw file bytes.
   No public key → refused unless `AllowUnsignedPackages` (must be `false` in production).
5. Public key: `updates.publicKeyPath` (`C:\Program Files\ClubShell\Agent\update-public.pem`, in the
   Administrators-only install directory), loaded by `Signing.LoadRsaPublicKeyFileAsync` (`PUBLIC KEY`,
   `RSA PUBLIC KEY` or an X.509 `CERTIFICATE` PEM).
6. Manifest-level signatures use the canonical form `Signing.ManifestCanonicalBytes`:
   `{"component":"agent","version":"…","url":"…","sha256":"…","size":N,"publishedAt":"yyyy-MM-ddTHH:mm:ss.fffZ"}` —
   fields in that order, no whitespace, UTF-8 — verified by `Signing.VerifyManifest`.
7. Apply: MSI via `msiexec /i /qn /norestart`; Agent self-update detached (helper `ClubShell.Updater.exe` when present,
   otherwise `msiexec`) with SCM recovery; `pending-update\rollback-<component>.json` markers are reconciled at the next
   start and a failed version is suppressed for 6 h (`UPDATES.md` §5.4–§5.5).

The private key never leaves the release pipeline (`tools/scripts/sign.ps1`, `release.yml`); the server only
distributes what was signed offline. A compromised server can therefore withhold or replay old-but-valid packages,
not ship new code; downgrade protection limits the replay to "current or newer".

---

## 11. Supply chain

| Layer | Pin | Reproducibility |
|-------|-----|-----------------|
| .NET | `global.json` (SDK 8.0.400, `rollForward: latestFeature`), `Directory.Packages.props` central package management, `nuget.config` with `<clear/>` + nuget.org only | restore is feed-independent; versions are single-sourced |
| Rust | `rust-toolchain.toml` (`channel = "1.89.0"`, `x86_64-pc-windows-msvc`, clippy + rustfmt), `Cargo.toml` workspace with `windows 0.58`, `tauri 2`; commit `Cargo.lock` | `clippy -D warnings` |
| Node | `pnpm-workspace.yaml`, `pnpm-lock.yaml`, `package.json` engines | `pnpm install --frozen-lockfile` |
| Contracts | `tools/ContractsGen` regenerates `crates/protocol` and `packages/contracts-ts`; hand edits only inside MANUAL blocks (ARCHITECTURE.md §1.2) | CI diff check keeps mirrors byte-identical |
| CI (`.github/workflows/ci.yml`) | build + tests for all three stacks, `TreatWarningsAsErrors`, `clippy -D warnings`, `tsc --noEmit`, `pnpm audit` / `cargo audit` / NuGet vulnerability audit | any drift fails the build |
| Release (`.github/workflows/release.yml`, `tools/scripts/{package,sign,publish}.ps1`) | MSIs built from a tagged commit, Authenticode-signed, update manifest signed with the offline RSA key | signer thumbprint is what `ClientValidationMode.SignatureVerified` and `ShellReplacementPolicyModule.DescribeSignature` see |

Third-party binaries the Agent talks to (Steam, Epic, Riot, EA, Ubisoft, anti-cheat drivers) are not part of the
supply chain ClubShell controls; the Agent only checks their presence and (for FACEIT) signature presence.

---

## 12. Responsible disclosure

Report vulnerabilities privately to **security@<club-domain>** (placeholder — replace with the operator's real
mailbox and, ideally, a PGP key in `SECURITY.md` of the deployment repository). Include the Agent/Shell versions
(`sys.pcInfo`, `X-Agent-Version`), a reproduction, and whether the finding is reachable by the kiosk user, the LAN or
only an administrator. Expected response: acknowledgement within 3 business days, fix or mitigation for
kiosk-user-reachable issues within 30 days, credit on request. Do not test against a production club without the
operator's written consent.

---

## 13. Hardening checklist

Image / OS

- [ ] Firmware password set, external boot disabled, Secure Boot on (also needed for Vanguard on Windows 11), fTPM on.
- [ ] BitLocker on the system drive (DPAPI-LocalMachine secrets are only as safe as the disk).
- [ ] No other interactive local accounts except administrators with strong passwords; RDP off or restricted.
- [ ] Windows Update configured to install outside club hours; `policies.json → power.scheduledShutdown` set.

Agent

- [ ] `agent.json → server.tlsPinSha256` filled with the server's SPKI pin(s) (`RetryPolicy.ComputeSpkiPin`); `server.signingEnabled = true`.
- [ ] `updates.publicKeyPath` present, `AllowUnsignedPackages` not enabled, `updates.channel = stable` in production.
- [ ] `PipeServer.ClientValidation = SignatureVerified` on signed release builds; Agent and Shell binaries Authenticode-signed with the same certificate.
- [ ] `shell.kioskUser.rotatePasswordOnStart = true`, `resetProfileOnLogout = true`, `createIfMissing = true`.
- [ ] `anticheat.reportViolations = true`; `requireSecureBoot` / `requireTpm` where the game catalogue needs it.
- [ ] `remoteAdmin.showIndicator = true`; disable `allowRemoteInput` if the club does not use remote assistance.
- [ ] `logging.level = Information` (never `Verbose`), `retainDays` per local data-retention rules.
- [ ] `C:\ProgramData\ClubShell` ACLs as written by `install.ps1` (root Users read, `secure\` SYSTEM/Admins only, `logs\` kiosk modify, `agent.json` not writable by the kiosk user).

Policy (`policies.json`)

- [ ] `shellReplacement.enabled = true` with the installed `shellExe`.
- [ ] `processAllowlist` deny list at least as strict as `config/policies.example.json` (`cmd`, `powershell`, `pwsh`, `regedit`, `taskmgr`, `mmc`, `control`, `msconfig`, `wscript`, `cscript`, `mshta`, `*cheat*`, `*inject*`); prefer `allow` mode on fixed images.
- [ ] `usb.allowStorage = false`; `allowHid = true` only if players bring their own peripherals.
- [ ] `webFilter.enabled = true`, filtering `dnsServers`, blocked domains maintained centrally.
- [ ] `explorer.disableTaskManager / disableRun / disableSettings / disableWinKey / disableAltTab = true`, `blockedKeyCombos` reviewed.
- [ ] `anticheat.blockOnViolation = true`, `required` set for the club's competitive titles.

Shell

- [ ] `shell.json → kiosk.adminPinHash` set (sha256 of a PIN not shared with players), `exitHotkey` changed from the default, `devtools = false`.
- [ ] Release build only (`cfg!(debug_assertions)` turns hardening off), `CLUBSHELL_DEV` never set on club PCs.
- [ ] Theme / wallpaper / ads assets served from trusted hosts (`img-src https:` in the CSP is broad by necessity for covers).

Server side (out of scope of this repo, but required for the controls above to mean anything)

- [ ] Replay detection on (`pcId`, `X-Timestamp`, `X-Signature`), skew window ≤ 300 s, refresh-token single use.
- [ ] Account-pool secrets encrypted per agent (`HKDF(signingSecret)`), rotated when a lease is released with `launchFailed` repeatedly.
- [ ] Update manifests signed offline; the signing key never on the API host.
- [ ] `POST /anticheat/report` and `shellCrashLoop` telemetry alerting in the admin panel.
