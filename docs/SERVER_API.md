# ClubShell — Central Server API v1 (consumed by the Agent)

Status: normative for the client. The server team implements this surface; `tools/MockServer` implements
it for development. Types referenced by name are defined in `IPC_PROTOCOL.md` §6 and are byte-compatible.

Base URL: `https://<server>/api/v1` (`agent.json → server.baseUrl`). WebSocket: `wss://<server>/ws/agent`.

---

## 1. Conventions

| Topic | Rule |
|-------|------|
| Content type | `application/json; charset=utf-8` both directions. Empty body allowed on 204. |
| Ids | UUID v4 lowercase strings |
| Time | ISO-8601 UTC with milliseconds (`2026-09-21T10:15:30.123Z`). Query params: same, URL-encoded. Dates: `YYYY-MM-DD` in club local time. |
| Money | `{ "amount": <int64 minor units>, "currency": "UZS" }` |
| Enums | camelCase strings (`IPC_PROTOCOL.md` §6.1) |
| Pagination | query `page` (1-based, default 1), `pageSize` (default 50, max 200); response `{ items: T[], total: int, page: int, pageSize: int }` |
| Idempotency | Every `POST` that creates or charges accepts header `Idempotency-Key: <uuid>`; server stores the key 24 h and replays the original response (same status/body) on repeat. Required for `/sessions`, `/sessions/{id}/extend`, `/shop/orders`, `/wallet/{userId}/topup-intent`, `/booking/reserve`, `/chat/{roomId}/messages`, `/sessions/{id}/events`. |
| Tracing | Request header `X-Trace-Id: <uuid>` (Agent generates; equals IPC envelope `id` when the call originates from the Shell). Response echoes it; error envelope carries it as `traceId`. |
| Versions | Request headers `X-Agent-Version`, `X-Shell-Version`, `User-Agent: ClubShellAgent/<ver> (Windows NT 10.0)` |
| Locale | `Accept-Language: ru` for localized `title`/`description` fields |
| Rate limits | Per agent token: 600 req/min burst 100; `429` with `Retry-After` seconds and error `rateLimited` |
| Compression | `Accept-Encoding: gzip, br` supported; responses > 1 KiB compressed |
| Caching | `ETag` on `GET /games`, `/apps`, `/tariffs`, `/shop/products`, `/agents/{pcId}/policies`, `/agents/{pcId}/config`; Agent sends `If-None-Match`, `304` = use cache |
| Timeouts | Agent: 15 s per request (`server.timeoutSec`); long uploads (telemetry batch) 30 s |
| Retry | Agent retries `GET`, `PUT`, `DELETE`, and idempotent-keyed `POST` on `408/425/429/5xx` and network errors with exponential backoff (`server.retry`); never retries `4xx` other than those |

---

## 2. Authentication

### 2.1 Agent identity

1. `POST /agents/register` with `X-Club-Key: <clubApiKey>` (from `agent.json`) — no Bearer.
2. Response gives `accessToken` (JWT RS256, `exp` ≈ 1 h), `refreshToken` (opaque, 30 d, single-use
   rotation), `signingSecret` (32-byte base64, used for HMAC), `pcId`.
3. Every other request: `Authorization: Bearer <accessToken>`.
4. 401 with `code = unauthorized` and `details.reason = "expired"` → `POST /agents/refresh`; if refresh
   fails with 401 → re-register (server re-issues if HWID matches an existing PC; else creates a new PC in
   `maintenance` status pending admin approval — `403 forbidden`, `details.reason = "pendingApproval"`).

JWT claims: `sub` = pcId, `club` = clubId, `hwid`, `role = "agent"`, `iat`, `exp`, `jti`. Agent validates
`exp` locally only (no signature check needed client-side; server public key is not distributed).

### 2.2 Request signing

Every request with a Bearer token MUST carry:

| Header | Value |
|--------|-------|
| `X-Timestamp` | Unix seconds (decimal string) of the Agent's clock |
| `X-Signature` | lowercase hex of `HMAC-SHA256(signingSecret, X-Timestamp + METHOD + PATH + BODY_HASH)` |

- `METHOD` = uppercase HTTP method.
- `PATH` = request path **including** query string, starting with `/api/v1/...`, exactly as sent (no
  scheme/host, no normalization).
- `BODY_HASH` = lowercase hex SHA-256 of the raw request body bytes; for an empty body the hash of zero
  bytes (`e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`).
- Concatenation is plain string concatenation, no separators.
- Server rejects `|now − X-Timestamp| > 300 s` with `401 unauthorized`, `details.reason = "clockSkew"`,
  and returns `X-Server-Time` (ISO-8601) so the Agent can compute an offset and retry once.
- Server rejects replay: (`pcId`, `X-Timestamp`, `X-Signature`) tuple must be unique within the window.

Example (`signingSecret` = base64 `c2VjcmV0MTIzNDU2Nzg5MDEyMzQ1Njc4OTAxMjM0NQ==`, decoded bytes are the key):

```
X-Timestamp: 1789992930
METHOD:      POST
PATH:        /api/v1/agents/7d2f1c3a-1111-4222-8333-444455556666/heartbeat
BODY:        {"status":"busy","currentSessionId":"9c1e...","agentVersion":"1.4.2","shellVersion":"1.4.2","uptimeSec":8123,"ipAddress":"10.0.1.12"}
BODY_HASH:   sha256(BODY)
message:     "1789992930" + "POST" + "/api/v1/agents/7d2f.../heartbeat" + BODY_HASH
X-Signature: hex(HMAC-SHA256(key, message))
```

### 2.3 User context

User-scoped endpoints (`/users/*`, `/sessions*`, `/wallet/*`, `/shop/orders*`, `/chat/*`, `/booking/*`,
`/tournaments/{id}/join`, `/games/{id}/accounts/*`) additionally require `X-User-Token: <user accessToken>`
obtained from `POST /auth/*`. The server verifies the user token belongs to a user currently bound to this
`pcId`. Missing/invalid → `401 unauthorized` with `details.reason = "userToken"`.

---

## 3. Error envelope

Every non-2xx response:

```json
{ "error": { "code": "insufficientFunds", "message": "Balance too low", "details": { "required": { "amount": 500000, "currency": "UZS" }, "available": { "amount": 120000, "currency": "UZS" } }, "traceId": "6f1d…" } }
```

| Field | Type | Required |
|-------|------|----------|
| `error.code` | `ErrorCode` | yes |
| `error.message` | string | yes |
| `error.details` | object \| null | yes |
| `error.traceId` | string | yes |

HTTP status ↔ `ErrorCode` mapping is the "HTTP equiv" column in `IPC_PROTOCOL.md` §5. The Agent maps
server errors 1:1 onto IPC errors (`details` passed through, `traceId` moved into `details.traceId`).
Transport failures map to `serverUnavailable` (5xx/connection) or `timeout`.

---

## 4. REST endpoints

Legend — Auth: `club` = `X-Club-Key`; `agent` = Bearer + signature; `user` = agent + `X-User-Token`.
Errors listed are in addition to the universal `unauthorized`, `forbidden`, `validation`, `rateLimited`,
`internal`, `versionMismatch` (426).

### 4.1 Agents

#### `POST /agents/register` — auth: club

Request

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `hwid` | string | yes | sha256 hex |
| `machineName` | string | yes | |
| `agentVersion` | string | yes | |
| `hardware` | `HardwareInfo` | yes | |
| `ipAddress` | string | yes | |
| `macAddress` | string | yes | |
| `previousPcId` | uuid | no | hint for re-registration after reinstall |

Response `200`

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `pcId` | uuid | yes | |
| `pc` | `Pc` | yes | |
| `accessToken` | string | yes | JWT |
| `refreshToken` | string | yes | |
| `signingSecret` | string | yes | base64, 32 bytes |
| `expiresAt` | datetime | yes | access token |
| `serverTime` | datetime | yes | |
| `config` | `AgentServerConfig` | yes | §4.1 `GET /agents/{pcId}/config` body |

Errors: `403 forbidden` (`pendingApproval`, `clubDisabled`), `409 conflict` (`hwid` bound to a PC in a
different club), `400 validation`.

#### `POST /agents/refresh` — auth: none (body carries refresh token)

Request `{ refreshToken: string, hwid: string }` → Response `{ accessToken, refreshToken, expiresAt, signingSecret?: string }` (`signingSecret` present only when rotated; Agent must switch atomically).
Errors: `401 unauthorized` (`details.reason`: `expired` | `revoked` | `reused`).

#### `POST /agents/{pcId}/heartbeat` — auth: agent

Request

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `status` | `PcStatus` | yes | agent's own view |
| `currentSessionId` | uuid | no | |
| `agentVersion` | string | yes | |
| `shellVersion` | string | yes | |
| `uptimeSec` | long | yes | |
| `ipAddress` | string | yes | |
| `policyVersion` | int | yes | applied version |
| `runningGames` | `{ gameId: uuid, pid: int, startedAt: datetime }[]` | yes | |
| `offlineQueue` | int | yes | outbox size |
| `shellConnected` | bool | yes | |

Response `200`

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `serverTime` | datetime | yes | |
| `pcStatus` | `PcStatus` | yes | authoritative |
| `policyVersion` | int | yes | latest available; Agent reloads if newer |
| `configVersion` | int | yes | |
| `catalogVersion` | string | yes | games/apps ETag-like; Agent refreshes lists if changed |
| `pendingCommands` | int | yes | commands queued while WS was down; Agent expects them via WS on reconnect or polls `GET /agents/{pcId}/commands` |
| `session` | `Session` | no | server view of the current session (reconciliation) |

Errors: `404 notFound` (pc deleted → re-register).

#### `GET /agents/{pcId}/commands` — auth: agent (fallback when WS unavailable)

Response `{ items: ServerCommandEnvelope[] }` — each item is a `command` `WsFrame` without `type` (§6, §6.1). Acked by `POST /agents/{pcId}/commands/{commandId}/ack` with `{ ok: bool, error?: IpcError, result?: object }`.

#### `POST /agents/{pcId}/telemetry` — auth: agent

Request

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `samples` | `PcMetrics[]` | yes | ≤ 120 per batch |
| `hardware` | `HardwareInfo` | no | when changed / every `hardwareRescanSec` |
| `events` | `{ kind: string, at: datetime, data: object }[]` | yes | agent diagnostics: `shellCrash`, `shellCrashLoop`, `policyApplyFailed`, `updateFailed`, `pipeError`, `launcherError`, `deadletter` |
| `logsTail` | string[] | no | last ≤ 50 Warning+ log lines when `events` non-empty |

Response `204`.

#### `GET /agents/{pcId}/config` — auth: agent, ETag

Response `AgentServerConfig` — server-side overrides merged over `agent.json` by the Agent (server wins for keys present):

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `version` | int | yes | |
| `pcName` | string | yes | |
| `zone` | string | yes | |
| `number` | int | yes | |
| `session` | partial `agent.json → session` | no | |
| `offline` | partial `agent.json → offline` | no | |
| `games` | `{ libraryRoots?: string[], accountPool?: {...}, cloudSave?: {...} }` | no | |
| `storage` | partial `agent.json → storage` | no | |
| `updates` | `{ channel?: UpdateChannel, checkIntervalSec?: int, applyWindow?: {from,to} }` | no | |
| `telemetry` | partial | no | |
| `remoteAdmin` | partial | no | |
| `shell` | `{ locale?: Locale, theme?: string, features?: object, ads?: object, idle?: object }` | no | pushed into `shell.json` |
| `themes` | `{ name: string, url: string, sha256: string }[]` | no | Agent downloads into `themes\` |
| `wsUrl` | string | no | override |

#### `GET /agents/{pcId}/policies` — auth: agent, ETag

Response `Policy`. `304` when unchanged.

### 4.2 PCs

#### `GET /pcs` — auth: agent — query `zone?` → `{ items: Pc[] }` (club-wide, for the seat map and "friends" views; `hwid` omitted for other PCs).
#### `GET /pcs/{pcId}` — auth: agent → `Pc`. Errors: `404`.

### 4.3 Auth (user)

#### `POST /auth/login` — auth: agent

Request `AuthRequest` (`kind` ∈ `password`, `card`, `token`). Response `200 AuthResponse` — `session` set
if this user has an open session on this `pcId` (e.g. after Agent restart). Server additionally returns
`offlineHash?: string` when `kind = password` and the club allows offline login; the Agent stores it in the
`users` table of `cache\offline.db`. The Agent verifies only the PBKDF2-SHA256 form
`pbkdf2$<iterations>$<salt base64>$<hash base64>` (`OfflineSessionStore.HashPassword` / `VerifyPassword`);
any other encoding (e.g. an Argon2id PHC string) is stored but fails closed at offline login.
Errors: `401 unauthorized` (bad credentials; `details.attemptsLeft`), `403 forbidden` (`banned`, `zoneNotAllowed`, `ageRestricted`), `409 conflict` (active session elsewhere; `details: { pcId, pcName }`), `429`.

#### `POST /auth/qr/start` — auth: agent

Request `{ pcId: uuid }` → Response `{ qrToken: string, qrUrl: string, expiresAt: datetime, pollIntervalSec: int }`. `qrUrl` is a deep link for the club's mobile app (`https://<server>/q/<qrToken>`).

#### `GET /auth/qr/{token}` — auth: agent

Response `{ status: QrStatus, auth?: AuthResponse }` — `auth` present only when `status = confirmed`
(single read; subsequent reads return `expired`). Errors: `404`.

#### `POST /auth/guest` — auth: agent

Request `{ pcId: uuid, hwid: string, displayName?: string, locale?: Locale }` → `AuthResponse` with a
transient `guest` user (`username = "guest-<pcNumber>-<n>"`). Errors: `403 forbidden` (`guestDisabled`).

#### `POST /auth/logout` — auth: user

Request `{ reason: "user" | "idle" | "admin" | "agentRestart" }` → `204`. Ends the open session if any
(server records `SessionEndReason`).

### 4.4 Users

#### `GET /users/{userId}` — auth: user (self) → `User`.
#### `PATCH /users/{userId}` — auth: user (self) — Request `{ displayName?: string, avatarUrl?: string, locale?: Locale, pin?: string }` → `User`. Errors: `403` (guest), `400`.
#### `GET /users/{userId}/stats` → `UserStats`.
#### `GET /users/{userId}/achievements` → `{ items: Achievement[] }`.
#### `GET /users/{userId}/loyalty` → `Loyalty`.

### 4.5 Sessions

#### `GET /sessions/current?pcId=` — auth: agent → `Session` or `204 No Content`.

#### `POST /sessions` — auth: user, idempotent

Request

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `pcId` | uuid | yes | |
| `userId` | uuid | yes | |
| `tariffId` | uuid | yes | |
| `minutes` | int | no | required unless package tariff |
| `prepaid` | bool | yes | |
| `startedAt` | datetime | no | set by Agent for offline-created sessions (server accepts ≤ `maxOfflineMinutes` in the past) |
| `clientSessionId` | uuid | no | Agent-generated id used offline; server adopts it as `id` if free |

Response `201 Session`.
Errors: `409 sessionAlreadyActive`, `402 insufficientFunds`, `404 notFound` (tariff/pc), `403 policyDenied` (`details.rule`: `tariffZone`, `tariffTime`, `postpaidNotAllowed`, `pcBooked`, `pcMaintenance`).

#### `POST /sessions/{id}/pause` — auth: user → `Session`. Errors: `409 sessionNotActive`, `409 conflict` (already paused), `403 policyDenied`.
#### `POST /sessions/{id}/resume` — auth: user → `Session`. Errors: `409 sessionNotActive`, `409 conflict`, `402`.
#### `POST /sessions/{id}/end` — auth: agent (user token optional; Agent may end on timeout/admin)

Request `{ reason: SessionEndReason, endedAt?: datetime, secondsUsed: int }` → `{ session: Session, charged: Money, refunded: Money }`. Errors: `409 sessionNotActive` (already ended → treated as success by the Agent when `details.session.state = ended`).

#### `POST /sessions/{id}/extend` — auth: user, idempotent

Request `{ minutes: int, tariffId?: uuid }` → `Session`. Errors: `409 sessionNotActive`, `402`, `400` (over `maxMinutes`), `403 policyDenied`.

#### `POST /sessions/{id}/events` — auth: agent, idempotent

Request `{ events: SessionEvent[] }` (≤ 100; used for offline replay and for `locked`/`unlocked`/`warning` audit) → `204`. Errors: `404`.

### 4.6 Games

#### `GET /games` — auth: agent, ETag — query `zone?`, `page?`, `pageSize?` (default all, max 1000) → `{ items: Game[], total, page, pageSize, catalogVersion: string }`. Server omits local-only fields (`installed`, `installPath`, `lastPlayedAt` requires `X-User-Token`); Agent fills them from its scan.
#### `GET /games/{id}` → `Game`. Errors: `404`.

#### `GET /games/{id}/accounts/lease` — auth: user

Query `sessionId` (required). Response `200`

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `leaseId` | uuid | yes | |
| `launcher` | `LauncherType` | yes | |
| `username` | string | yes | |
| `secret` | string | yes | password or token; AES-256-GCM encrypted with a per-agent key derived from `signingSecret` (`HKDF-SHA256`, info `"account-pool"`), base64 `nonce\|\|ciphertext\|\|tag` |
| `extra` | object | no | launcher-specific (`steamGuardSecret`, `authenticatorSeed`, `region`) |
| `expiresAt` | datetime | yes | lease TTL |
| `cloudSave` | `{ url: string, sha256: string, sizeBytes: long }` | no | pre-signed download of the user's save bundle for this game |

Errors: `409 accountPoolExhausted`, `409 sessionNotActive`, `403 policyDenied` (age), `404`.

#### `POST /games/{id}/accounts/{leaseId}/release` — auth: agent

Request `{ reason: "exit" | "sessionEnd" | "launchFailed" | "manual", cloudSave?: { uploadUrl?: string, sha256: string, sizeBytes: long } }` — when `cloudSave` present the Agent has already `PUT` the bundle to the pre-signed `uploadUrl` returned by `GET /games/{id}/accounts/{leaseId}/save-upload`. Response `204`. Errors: `404` (treated as success).

#### `GET /games/{id}/accounts/{leaseId}/save-upload` — auth: agent → `{ uploadUrl: string, expiresAt: datetime, maxBytes: long }`.

#### `POST /games/{id}/launch-report` — auth: agent

Request

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `sessionId` | uuid | yes | |
| `userId` | uuid | yes | |
| `result` | `LaunchResult` | yes | |
| `durationMs` | int | yes | launch latency |
| `launcher` | `LauncherType` | yes | |
| `antiCheat` | `{ kind: AntiCheatKind, ok: bool, reason?: string }` | yes | |
| `exitCode` | int | no | on exit reports |
| `playedSec` | int | no | on exit reports |
| `phase` | `"launch" \| "exit"` | yes | |

Response `204`.

### 4.7 Apps

#### `GET /apps` — auth: agent, ETag → `{ items: App[] }` (`allowed` computed server-side per zone; Agent re-evaluates with local policy).

### 4.8 Wallet & tariffs

#### `GET /wallet/{userId}/balance` — auth: user → `Balance`.
#### `GET /wallet/{userId}/transactions` — auth: user — query `page`, `pageSize`, `from?`, `to?`, `type?` → paginated `Transaction`.
#### `GET /tariffs` — auth: agent, ETag — query `zone?` → `{ items: Tariff[], serverTime: datetime }`.
#### `POST /wallet/{userId}/topup-intent` — auth: user, idempotent

Request `{ amount: Money, provider: TopupProvider, pcId: uuid }` → `201 TopupIntent`. Completion is
delivered by the WS push `walletUpdated` (§6.3); `GET /wallet/{userId}/topup-intent/{id}` → `TopupIntent`
exists for polling when WS is down. Errors: `400` (min amount), `403 policyDenied`, `503 serverUnavailable` (provider down).

### 4.9 Shop

#### `GET /shop/products` — auth: agent, ETag — query `category?` → `{ items: Product[] }`.
#### `POST /shop/orders` — auth: user, idempotent

Request `{ userId: uuid, pcId: uuid, sessionId?: uuid, items: { productId: uuid, qty: int }[], note?: string }` → `201 Order`. Errors: `402`, `404`, `409 conflict` (`outOfStock`), `403 policyDenied`.
#### `GET /shop/orders/{id}` — auth: user → `Order`. Errors: `404`, `403` (not owner).
#### `GET /shop/orders?userId=` — auth: user — query `page`, `pageSize`, `activeOnly?` → paginated `Order`.
#### `POST /shop/orders/{id}/cancel` — auth: user → `Order`. Errors: `409 conflict` (not `pending`).

### 4.10 Chat

#### `GET /chat/{roomId}/messages` — auth: user — query `before?` (message id), `limit?` (≤ 200) → `{ roomId, items: ChatMessage[], hasMore: bool, unread: int }`. Errors: `404`, `403` (not a member).
#### `POST /chat/{roomId}/messages` — auth: user, idempotent — Request `{ text: string }` → `201 ChatMessage`. Errors: `400`, `403 policyDenied` (muted), `429`.
#### `POST /chat/{roomId}/read` — auth: user — Request `{ upToMessageId: uuid }` → `{ roomId, unread: int }`.

Room ids: `pc:<pcId>` (created implicitly), `zone:<zone>`, `club`, `dm:<userIdA>:<userIdB>` (sorted).

### 4.11 Booking

#### `GET /booking/seats?date=` — auth: agent → `{ date, seats: Seat[], bookings: Booking[], slotMinutes: int, openFrom: time, openTo: time }`.
#### `POST /booking/reserve` — auth: user, idempotent — Request `{ userId, pcId, from: datetime, to: datetime }` → `201 Booking`. Errors: `409 conflict` (`slotTaken`), `400` (alignment / past / max duration `details.maxMinutes`), `402` (deposit), `403 policyDenied`.
#### `DELETE /booking/{id}` — auth: user → `200 Booking` (status `cancelled`). Errors: `404`, `403`, `409 conflict` (`alreadyStarted`).

### 4.12 Tournaments

#### `GET /tournaments` — auth: agent — query `state?`, `gameId?` → `{ items: Tournament[] }` (`joined` requires `X-User-Token`, else `false`).
#### `POST /tournaments/{id}/join` — auth: user → `Tournament`. Errors: `404`, `409 conflict` (`full`, `alreadyJoined`, `notOpen`), `402`.
#### `GET /tournaments/{id}/leaderboard` — auth: agent — query `limit?` (≤ 100) → `{ tournamentId, entries: LeaderboardEntry[], me?: LeaderboardEntry, updatedAt }`.

### 4.13 Updates

#### `GET /updates/{channel}/manifest` — auth: agent — query `component` (required), `current` (semver, required), `arch?` (`x64`) → `200 UpdateManifest` or `204` (up to date). Errors: `404` (unknown channel/component). Package download at `manifest.url` requires the same Bearer header; supports `Range`.

### 4.14 Support / anti-cheat

#### `POST /support/call-admin` — auth: agent (user optional), idempotent

Request `{ pcId: uuid, userId?: uuid, category: CallAdminCategory, message?: string, at: datetime }` → `201 { ticketId: uuid, createdAt: datetime, queuePosition?: int }`. Errors: `429`.

#### `POST /anticheat/report` — auth: agent

Request

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `pcId` | uuid | yes | |
| `sessionId` | uuid | no | |
| `userId` | uuid | no | |
| `gameId` | uuid | no | |
| `kind` | `AntiCheatKind` | yes | subsystem |
| `check` | string | yes | `driverMissing`, `serviceStopped`, `secureBootOff`, `tpmOff`, `hvciOff`, `testSigningOn`, `blockedProcess`, `injectedModule`, `vmDetected`, `debuggerAttached` |
| `severity` | `"info" \| "warning" \| "critical"` | yes | |
| `details` | object | yes | |
| `at` | datetime | yes | |
| `actionTaken` | `"none" \| "blockedLaunch" \| "killedGame" \| "lockedSession"` | yes | |

Response `204`.

---

## 5. Endpoint index

| Method | Path | Auth |
|--------|------|------|
| POST | `/agents/register` | club |
| POST | `/agents/refresh` | none |
| POST | `/agents/{pcId}/heartbeat` | agent |
| POST | `/agents/{pcId}/telemetry` | agent |
| GET | `/agents/{pcId}/config` | agent |
| GET | `/agents/{pcId}/policies` | agent |
| GET | `/agents/{pcId}/commands` | agent |
| POST | `/agents/{pcId}/commands/{commandId}/ack` | agent |
| GET | `/pcs` | agent |
| GET | `/pcs/{pcId}` | agent |
| POST | `/auth/login` | agent |
| POST | `/auth/qr/start` | agent |
| GET | `/auth/qr/{token}` | agent |
| POST | `/auth/guest` | agent |
| POST | `/auth/logout` | user |
| GET | `/users/{userId}` | user |
| PATCH | `/users/{userId}` | user |
| GET | `/users/{userId}/stats` | user |
| GET | `/users/{userId}/achievements` | user |
| GET | `/users/{userId}/loyalty` | user |
| GET | `/sessions/current?pcId=` | agent |
| POST | `/sessions` | user |
| POST | `/sessions/{id}/pause` | user |
| POST | `/sessions/{id}/resume` | user |
| POST | `/sessions/{id}/end` | agent |
| POST | `/sessions/{id}/extend` | user |
| POST | `/sessions/{id}/events` | agent |
| GET | `/games` | agent |
| GET | `/games/{id}` | agent |
| GET | `/games/{id}/accounts/lease` | user |
| POST | `/games/{id}/accounts/{leaseId}/release` | agent |
| GET | `/games/{id}/accounts/{leaseId}/save-upload` | agent |
| POST | `/games/{id}/launch-report` | agent |
| GET | `/apps` | agent |
| GET | `/wallet/{userId}/balance` | user |
| GET | `/wallet/{userId}/transactions` | user |
| GET | `/tariffs` | agent |
| POST | `/wallet/{userId}/topup-intent` | user |
| GET | `/wallet/{userId}/topup-intent/{id}` | user |
| GET | `/shop/products` | agent |
| POST | `/shop/orders` | user |
| GET | `/shop/orders/{id}` | user |
| GET | `/shop/orders?userId=` | user |
| POST | `/shop/orders/{id}/cancel` | user |
| GET | `/chat/{roomId}/messages` | user |
| POST | `/chat/{roomId}/messages` | user |
| POST | `/chat/{roomId}/read` | user |
| GET | `/booking/seats?date=` | agent |
| POST | `/booking/reserve` | user |
| DELETE | `/booking/{id}` | user |
| GET | `/tournaments` | agent |
| POST | `/tournaments/{id}/join` | user |
| GET | `/tournaments/{id}/leaderboard` | agent |
| GET | `/updates/{channel}/manifest` | agent |
| POST | `/support/call-admin` | agent |
| POST | `/anticheat/report` | agent |

---

## 6. WebSocket `wss://<server>/ws/agent?token=<accessToken>`

| Property | Value |
|----------|-------|
| Subprotocol | `clubshell.v1` |
| Auth | query `token` = agent JWT; `401` close (code 4401) when invalid; `4426` when protocol unsupported |
| Frames | text, one JSON `WsFrame` per frame, ≤ 1 MiB |
| Keepalive | server `ping` frame every 20 s; Agent replies `pong` (WsFrame) within 10 s; Agent also sends WebSocket-level pings every 30 s |
| Reconnect | Agent backoff 1 s → 60 s (×2, ±20 % jitter); on token expiry refresh first |
| Delivery | commands are at-least-once: server retries un-acked commands on reconnect (`pendingCommands` in heartbeat); Agent dedupes by `id` for 24 h |
| Ordering | per-connection FIFO; a command with `supersedes` cancels a pending earlier command id |

`WsFrame`

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `type` | `"command"` \| `"ack"` \| `"event"` \| `"ping"` \| `"pong"` \| `"push"` | yes | |
| `id` | uuid | yes | |
| `ts` | datetime | yes | |
| `name` | string | yes for command/event/push | `ServerCommandType` / `AgentEventType` / push kind |
| `payload` | object \| null | yes | |
| `ack` | `{ id: uuid, ok: bool, error?: IpcError, result?: object }` | yes for `ack` | |
| `supersedes` | uuid | no | command only |
| `expiresAt` | datetime | no | command only; Agent discards expired commands with `ack.error = timeout` |

### 6.1 Server → Agent commands (`ServerCommandType`)

| name | payload | Agent action | ack.result |
|------|---------|--------------|------------|
| `lock` | `{ reason?: string, message?: string }` | `session.lock` if session else idle-lock; IPC `shell.command{lock}` | `null` |
| `unlock` | `null` | unlock; IPC `shell.command{unlock}` | `null` |
| `message` | `{ id: uuid, from: string, text: string, level: NotificationLevel, requiresAck: bool }` | IPC `admin.message` | `{ deliveredAt, ackedAt? }` (second ack sent when user acks) |
| `reboot` | `{ delaySec: int, force: bool, message?: string }` | end session if `force`, IPC `shell.command{reboot}`, `InitiateSystemShutdownEx` | `{ scheduledAt }` |
| `shutdown` | same as `reboot` | | `{ scheduledAt }` |
| `wake` | `{ targetMac: string }` | send WoL magic packet to another PC (LAN relay) | `null` |
| `endSession` | `{ sessionId: uuid, reason: SessionEndReason }` | end + IPC `session.ended` | `{ session }` |
| `extendSession` | `{ sessionId: uuid, minutes: int, charge: bool }` | apply server-authoritative extension (already billed if `charge=false`) | `{ session }` |
| `launchGame` | `LaunchRequest` | launch as if from Shell | `LaunchResult` |
| `killGame` | `{ gameId?: uuid, pid?: int, force: bool }` | kill | `{ killed, pids }` |
| `setPolicy` | `Policy` | apply + cache; IPC `policy.changed` | `{ version, applied: bool }` |
| `reloadPolicy` | `null` | `GET /agents/{pcId}/policies` + apply | `{ version }` |
| `screenshot` | `{ monitor?: int, quality: int, maxWidth?: int, uploadUrl: string }` | capture via helper in user session, `PUT` JPEG to `uploadUrl` | `{ width, height, bytes, uploadedAt }` |
| `remoteControlStart` | `{ sessionToken: string, relayUrl: string, fps: int, allowInput: bool, adminName: string }` | connect capture/input stream to relay (`wss`), IPC `admin.remoteControl{started}` | `{ startedAt }` |
| `remoteControlStop` | `{ sessionToken: string }` | stop, IPC `admin.remoteControl{stopped}` | `{ stoppedAt, durationSec }` |
| `update` | `{ component: UpdateComponent, manifest?: UpdateManifest, applyNow: bool }` | run update check/apply | `{ scheduled, at? }` |
| `showAds` | `{ items: { url, type, durationSec }[], skippable: bool }` | IPC `shell.command{showAds}` | `null` |
| `setVolume` | `{ level: int, muted?: bool }` | set volume | `{ level, muted }` |
| `refreshConfig` | `{ config?: bool, games?: bool, apps?: bool, tariffs?: bool, products?: bool, themes?: bool }` | re-fetch selected caches (all when empty) | `{ refreshed: string[] }` |

### 6.2 Agent → Server events (`AgentEventType`)

| name | payload |
|------|---------|
| `sessionStarted` | `{ session: Session }` |
| `sessionEnded` | `{ session: Session, reason: SessionEndReason, charged: Money }` |
| `gameLaunched` | `{ sessionId: uuid, gameId: uuid, pid: int, accountLeaseId?: uuid, at: datetime }` |
| `gameExited` | `{ sessionId: uuid, gameId: uuid, pid: int, exitCode: int, playedSec: int, at: datetime }` |
| `anticheatViolation` | body of `POST /anticheat/report` |
| `hardwareChanged` | `{ hardware: HardwareInfo, diff: string[] }` |
| `offlineQueueFlushed` | `{ count: int, deadlettered: int, offlineFrom: datetime, offlineTo: datetime }` |

Events are fire-and-forget over WS; the server does not ack them. When WS is down they are stored in the
outbox and replayed via their REST equivalents (`/sessions/{id}/events`, `/games/{id}/launch-report`,
`/anticheat/report`, `/agents/{pcId}/telemetry`), never re-sent over WS.

### 6.3 Server → Agent pushes (`type = "push"`, no ack)

| name | payload | Agent action |
|------|---------|--------------|
| `walletUpdated` | `Balance` | IPC `wallet.updated` |
| `chatMessage` | `ChatMessage` | IPC `chat.message` |
| `notification` | `Notification` | IPC `notification.push` |
| `orderUpdated` | `Order` | IPC `notification.push` (level `info`) + `shop.orderUpdated` (Tauri `agent://shop.orderUpdated`, payload `Order`) |
| `bookingUpdated` | `Booking` | IPC `notification.push` |
| `tournamentUpdated` | `Tournament` | IPC `notification.push` |
| `sessionUpdated` | `Session` | reconcile; IPC `session.updated` |
| `pcStatusChanged` | `{ pcId: uuid, status: PcStatus }` | cache for seat map |
| `userRevoked` | `{ userId: uuid, reason: string }` | IPC `auth.expired{revoked}`, end session |

Example frames:

```json
{ "type": "command", "id": "c0a8…", "ts": "2026-09-21T10:20:00.000Z", "name": "lock", "payload": { "reason": "admin", "message": "Please come to the desk" }, "expiresAt": "2026-09-21T10:25:00.000Z" }
{ "type": "ack", "id": "77aa…", "ts": "2026-09-21T10:20:00.050Z", "ack": { "id": "c0a8…", "ok": true, "result": null } }
{ "type": "event", "id": "e1f2…", "ts": "2026-09-21T10:21:00.000Z", "name": "gameLaunched", "payload": { "sessionId": "9c1e…", "gameId": "5a6b…", "pid": 7788, "accountLeaseId": "1c2d…", "at": "2026-09-21T10:21:00.000Z" } }
{ "type": "push", "id": "p9…", "ts": "2026-09-21T10:22:00.000Z", "name": "walletUpdated", "payload": { "userId": "3f9a…", "amount": { "amount": 2000000, "currency": "UZS" }, "bonus": { "amount": 50000, "currency": "UZS" }, "currency": "UZS", "updatedAt": "2026-09-21T10:22:00.000Z" } }
```

---

## 7. Mock server (`tools/MockServer`)

Implements every endpoint and WS frame above with in-memory state seeded from `tools/MockServer/src/db.ts`
(realistic club data; persisted to `tools/MockServer/.mock-db.json`, `--reset` reseeds), accepts any
`X-Club-Key` (set `MOCK_STRICT_REGISTER=1` to require a pre-provisioned PC), logs but does not enforce HMAC
signatures by default (`MOCK_VERIFY_SIGNATURE=1` enforces, `MOCK_SKIP_SIGNATURE=1` never checks), and exposes
mock-control endpoints for e2e tests: `POST /_mock/command` `{ pcId, type | name, payload, expiresInSec?,
supersedes?, issuedBy? }` (also `POST /mock/pcs/{pcId}/command`) to inject a `ServerCommand` and await its ack,
`POST /_mock/push` `{ name, payload, pcId? | userId? }` for pushes, `POST /mock/qr/{token}/confirm`,
`GET /mock/events`, `GET /mock/connections`, `GET /mock/commands`, `POST /mock/reset-tokens`, `PUT /mock/upload/{id}`
and `GET /health`. Other knobs: `MOCK_SERVER_PORT` (default 8080), `--latency <ms>`, `--fail-rate <0..1>`,
`MOCK_QR_AUTOCONFIRM_SEC`, `MOCK_GUEST_DISABLED=1`. Listens on `http://localhost:8080` (`ws://localhost:8080/ws/agent`).
