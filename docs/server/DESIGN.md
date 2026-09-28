# ClubShell Central Server — дизайн реализации (v1)

Статус: утверждённый план для реализации срезами S0–S6. Источник истины для провода — `deepunites/club-contracts`
ветки `shell-changes-p0-p1` (коммит `52a895a`): `openapi/openapi.yaml` (бандл), `openapi/fragments/*`,
`asyncapi/asyncapi.yaml`. Если этот документ расходится с контрактом, прав контракт, а документ нужно поправить.
Ссылки: `cs/` = этот репозиторий (club-shell `main` af4eb43), `cc/` = club-contracts, `srv@1c68cc8:` = файл в
истории `deepunites/club-server` (`git show 1c68cc8:<path>`), `mock/` = `cs/tools/MockServer/src`,
`frag/` = `cc/openapi/fragments`.

---

## 1. Цель, границы, что v1 НЕ делает

**Цель.** Сервер для одного клуба (архитектура допускает сеть клубов), который заменяет `tools/MockServer` в
продакшене и проходит те же проверки:

- реализует все 75 операций с `x-server-status: required`: admin 44, agents 8, sessions 7, auth 5, users 5, games 3,
  pcs 1, updates 1, wallet 1;
- реализует канал `/ws/agent` из AsyncAPI: все операции с `x-server-status: required` (§6);
- на остальные 27 операций контракта отвечает `501`, кроме `getBalance` и `reportAntiCheat`, которые реализуются
  сверх контракта (список ниже) — итого 25 × 501 после S3;
- является единственным авторитетом по деньгам, времени сеансов и статусу ПК.

**Границы.**

| В v1 | Не в v1 |
|---|---|
| REST `/api/v1/*` для агента, игрока (через агента) и кассы | Магазин/заказы, чат, бронирование, турниры, topup-intent, apps, пул аккаунтов, облачные сохранения |
| `/ws/agent` (команды, ack, пуши, keepalive) | WS для кассы (касса опрашивает, `frag/paths-admin.yaml:500-501`) |
| PostgreSQL 18, один инстанс сервера | Горизонтальное масштабирование (§6.8) |
| Биллинг сеансов, кошелёк, смены X/Z, контроль кассира, здоровье ПК, автоматизация, вебхуки | Удалённый рабочий стол, скриншоты, команда `update`, публикация обновлений |
| Команды кассы `message`/`lock`/`unlock`/`reboot`/`shutdown` | `screenshot`, `remoteControl*`, `update`, `setPolicy` из кассы |
| Каталог игр и товаров из seed-файлов (§12 D-14) | CRUD игр/товаров (в контракте его нет) |

**notImplemented → 501.** Каждая операция контракта с `x-server-status: notImplemented` (плюс
`POST /admin/notifications/test`) замаплена и отвечает так:

```
HTTP/1.1 501 Not Implemented
X-Trace-Id: <эхо или новый>
X-Server-Time: 2026-09-28T10:00:00.000Z
Content-Type: application/json

{"error":{"code":"notImplemented","message":"Operation <operationId> is not implemented by this server","details":{"reason":"notImplemented"},"traceId":"<id>"}}
```

- `Retry-After` не отправляется. Маршрут существует, поэтому `404` здесь не бывает никогда.
- Сначала проверяется аутентификация в режиме операции (agent/user/staff), затем сразу отдаётся 501. Тело не
  валидируется.
- Таблица 501 строится при старте из `server/contracts/openapi.yaml` (порт `srv@1c68cc8:src/Club.Server/Api/ContractStatus.cs`).
  Реализованные операции исключаются по их `operationId`; `getBalance` и `reportAntiCheat` стоят в этом списке явно,
  хотя в контракте они notImplemented. Тест «каждая notImplemented-операция отвечает 501, никогда 404» переносится
  из `srv@1c68cc8:tests/Club.Server.Tests/AgentApiTests.cs:209-234` и пропускает этот явный список.
- Код ошибки — `notImplemented`, **не** `serverUnavailable`, как было в `srv@1c68cc8` (`ContractStatus.cs:54`), см.
  `cc/openapi/base.yaml:993-1003`.
- Флаги в конфиге агента явно выключают фичи, которые упираются в 501 (§5.9). Поэтому текущие агенты такие
  операции не вызывают, а старые агенты не разгоняют circuit breaker (`cc/openapi/base.yaml` §8).

Отступления от «ровно 75» оформляются PR в club-contracts; до его слияния сервер реализует их как дополнительные
(§12, «Изменения контракта»):

- `GET /wallet/{userId}/balance` (`getBalance`) — делаем required (агент на 501 даёт жёсткую ошибку,
  `SessionHandlers.cs:651-672`);
- `POST /anticheat/report` (`reportAntiCheat`) — 204 и запись в `anticheat_reports`: WS-событие теряется, пока ПК
  офлайн, а REST-путь агент ставит в очередь; поэтому `config.anticheat.reportViolations=true` (§5.9);
- `/users/{userId}/game-settings*`: список → `{items:[]}`, `DELETE` → 204, остальное 501;
- `PATCH /admin/games/{id}` и поле `AdminGame.settingsPaths` — без них падает `CatalogPage.tsx:200-202`;
- `GET /admin/network`, `POST /admin/network/clubs` → 501.

---

## 2. Структура решения

### 2.1 Проекты и размещение

```
server/
  Directory.Build.props          импортирует ../Directory.Build.props; net10.0, LangVersion 14, IsTestProject для tests/
  ClubShell.Server.sln           Server + Server.Tests + (ссылки) src/ClubShell.Contracts, src/ClubShell.Core
  README.md                      запуск, конфиг, деплой, ротация ключей
  contracts/
    openapi.yaml, asyncapi.yaml  вендорная копия бандла club-contracts
    openapi.json                 генерируется из yaml (для JsonSchema.Net в тестах)
    REF                          sha коммита club-contracts (сейчас 52a895a)
  scripts/sync-contracts.ps1     копирует из ../club-contracts, пишет REF, конвертирует в json (python yaml, как srv@1c68cc8:scripts/sync-contracts.sh)
  src/ClubShell.Server/          ASP.NET Core minimal API, net10.0
  tests/ClubShell.Server.Tests/  xunit + WebApplicationFactory + временная PostgreSQL
  Dockerfile, compose.yaml       (S6; контекст сборки — корень репо: нужны src/ClubShell.Contracts и корневые props)
  railway.toml                   (S6) конфиг Railway (§S6)
```

Решения, которые учитывают уже существующий репозиторий:

- **global.json.** На main `8.0.400`; ветка поднимает до `10.0.100` / `latestFeature`, а `latestFeature` не
  переходит мажорную версию. Поэтому весь репозиторий (и агент) собирается SDK 10: `setup-dev-vm.ps1` ставит SDK 10
  и SDK 8 (рантайм 8.0 для тестов агента), `dev.ps1` проверяет SDK 10. Агентские проекты остаются `net8.0`
  (`cs/src/*/*.csproj`).
- **Корневой `Directory.Build.props`.** Фиксирует `LangVersion 12.0` и признаёт тестовыми только проекты под
  `cs/tests/` (`cs/Directory.Build.props:10, 38-41`). Поэтому `server/Directory.Build.props` переопределяет
  `LangVersion` на 14 и сам ставит `IsTestProject=true` для `server/tests/**`.
- **Central Package Management** (`cs/Directory.Packages.props`). Новые версии добавляются туда в группу
  `Server`:

  | Пакет | Версия |
  |---|---|
  | Npgsql | 10.0.3 |
  | Dapper | 2.1.89 |
  | FluentMigrator.Runner.Postgres | 8.0.1 |
  | Microsoft.IdentityModel.JsonWebTokens | 8.23.0 |
  | YamlDotNet | 18.1.0 |
  | Microsoft.AspNetCore.Mvc.Testing | 10.0.12 |
  | JsonSchema.Net | 9.4.0 |

  Версии совпадают с `srv/Club.Server.csproj:7-11` и тестами club-server. Для xunit, Test.Sdk и coverlet берутся уже
  закреплённые в репо версии. Глобальный `Microsoft.CodeAnalysis.NetAnalyzers 8.0.0` распространяется и на сервер.
  SDK 10 предупреждает, что его анализаторы новее пакета. В S0 в корневой `Directory.Build.props` ставится
  `_SkipUpgradeNetAnalyzersNuGetWarning=true`. Так весь репозиторий собирается на SDK 10.0.401 без предупреждений:
  проверено локально, 502/502 теста агента зелёные.
- **Отдельный `server/ClubShell.Server.sln`**, а не `ClubShell.sln`. CI-job `dotnet` работает на `windows-latest`
  без PostgreSQL (`cs/.github/workflows/ci.yml:165-186`). Серверный job — отдельный, ubuntu + `postgres:18` (S6).
- **ClubShell.Contracts** подключается через `ProjectReference` `../../../src/ClubShell.Contracts/ClubShell.Contracts.csproj`:
  net10 ссылается на библиотеку net8.0 без проблем. `ClubShell.Core` нужен **только тестам** — там реальный
  `ServerClient` (§10b).

### 2.2 Папки `server/src/ClubShell.Server`

| Папка | Содержимое (ключевые файлы) | Операции |
|---|---|---|
| `Infrastructure/` | `ApiErrors.cs` (`ApiException`, `ApiErrorMiddleware`: трассировка, X-Server-Time, конверт), `ServerJson.cs` (§2.4), `ContractStatus.cs` (501), `Database.cs` + `DapperSetup.cs` (миграции под advisory lock), `ETag.cs`, `Paging.cs`, `ClubTime.cs` (IANA-зона клуба), `AdvisoryLocks.cs` (ключи), `RateLimits.cs`, `Cors.cs` | — |
| `Db/Migrations/` | `M0001_Core.cs` … (§4) | — |
| `Auth/` | `TokenService.cs` (RS256), `AgentAuthMiddleware.cs` (режимы club/none/agent/user/staff), `RequestSignature.cs`, `ReplayLog.cs`, `UserTokens.cs`, `StaffTokens.cs`, `Passwords.cs` (PBKDF2), `AuthContext.cs`, `PlayerAuthEndpoints.cs` | auth ×5 |
| `Agents/` | `AgentEndpoints.cs`, `PcRepository.cs`, `CommandRepository.cs`, `AgentConfigBuilder.cs`, `TelemetryIngest.cs`, `PcStatus.cs` (выведение статуса) | agents ×8, `getPc` |
| `Realtime/` | `AgentSocketHub.cs` (порт srv@1c68cc8), `CommandDispatcher.cs`, `Pushes.cs` | `/ws/agent` |
| `Sessions/` | `SessionEndpoints.cs`, `SessionRepository.cs`, `SessionService.cs` (create/pause/resume/extend/end — общее для киоска и кассы), `SessionClock.cs`, `SessionEventsApplier.cs`, `SessionTickWorker.cs` | sessions ×7 |
| `Sessions/Billing/` | `Pricing.cs` (quote, §5.1), `PurchaseRules.cs` (§5.2), `Settlement.cs` (возврат и постоплата) | — |
| `Wallet/` | `Ledger.cs` (единственная точка записи денег), `WalletEndpoints.cs` | `getTariffs`, `GET /wallet/{userId}/balance` |
| `Users/` | `UserEndpoints.cs`, `UserRepository.cs`, `PlayerStats.cs`, `Loyalty.cs`, `GameSettingsStubs.cs` | users ×5 |
| `Games/` | `GameEndpoints.cs`, `CatalogRepository.cs`, `CatalogSeed.cs` | games ×3 |
| `Updates/` | `UpdateEndpoints.cs` (всегда 204 после валидации) | `getUpdateManifest` |
| `Admin/` | `AdminJson.cs`, `StaffAuthMiddleware` (в `Auth/`), подпапки ниже | admin ×44 |
| `Admin/Staff/` | login/logout/me, CRUD персонала | 6 |
| `Admin/Counter/` | overview, open/extend/end, topup, pc command | 6 |
| `Admin/Shifts/` | смена, X/Z | 3 |
| `Admin/Clients/` | клиенты, пароль, карта, транзакции, промокод | 7 |
| `Admin/Club/` | настройки, api-key, `ClubSettings.cs` (jsonb + JsonSchema-валидация) | 4 |
| `Admin/Pricing/` | тарифы CRUD, quote | 5 |
| `Admin/Pcs/` | карта зала CRUD | 4 |
| `Admin/Stock/` | товары, приёмка | 3 |
| `Admin/Catalog/` | игры (+ PATCH settingsPaths) | 1 (+1) |
| `Admin/Health/` | здоровье ПК, тикеты, `HealthWorker.cs` | 3 |
| `Admin/Control/` | журнал (`Audit.cs`), флаги, `/admin/control` | 1 |
| `Admin/Reports/` | `/admin/reports` | 1 |
| `Admin/Automation/` | `AutomationHooks.cs` (правила) | — |
| `Admin/Webhooks/` | `WebhookOutbox.cs`, `WebhookWorker.cs` | — |
| `Idempotency/` | `Idempotency.cs` (таблица, выполнение в одной транзакции) | — |
| `Program.cs` | композиция (§2.3) | `/health` |

Каждая область — статический класс `MapXxxEndpoints(this IEndpointRouteBuilder)` с `MapGroup(prefix)`, как в
`srv/Program.cs:71-77`. Интерфейсов с единственной реализацией нет. Репозитории — `sealed` классы над
`NpgsqlDataSource` (стиль `srv/MachineRepository.cs`).

### 2.3 Композиция (`Program.cs`, стиль club-server)

1. Опции связываются вручную: `GetSection("X").Get<XOptions>() ?? new()`, регистрируются как singleton. Время —
   только через `TimeProvider.System` (`srv/Program.cs:15-33`).
2. `NpgsqlDataSource` — singleton. При `Database:MigrateOnStart=true` миграции идут под `pg_advisory_lock('CSMig')`.
3. Порядок middleware:
   1. IP клиента. Если задан `Proxy:ClientIpHeader`, адрес берётся из этого заголовка; для Railway это
      `X-Real-IP`, а список адресов прокси Railway не публикует. Иначе, при заданном `Proxy:Trusted`, работает
      `ForwardedHeaders`. От этого адреса зависят rate limit и аудит (§3.6, §3.7).
   2. `ApiErrorMiddleware` — первым;
   3. `Cors` — только `/api/v1/admin`;
   4. `UseWebSockets`;
   5. `RateLimiter`;
   6. `AgentAuthMiddleware` — `/api/v1/*` кроме `/admin`;
   7. `StaffAuthMiddleware` — `/api/v1/admin/*`.
4. Маршруты: `Map*Endpoints()`, затем `app.Map("/ws/agent", hub.HandleAsync)`, затем
   `MapNotImplemented(ContractStatus, implemented)` и в конце fallback `/api/v1/{**}` → `404 notFound`
   `details.route`.
5. Фоновые воркеры (§8) регистрируются, только если `Workers:Enabled`. Тесты выключают их и вызывают
   `RunOnceAsync` напрямую.
6. В конце — `public partial class Program;`.

### 2.4 JSON: байт-в-байт с агентом

| Поверхность | Опции | Почему |
|---|---|---|
| Агент/игрок (`/api/v1/*` кроме admin), WS | `JsonDefaults.Options` из `cs/src/ClubShell.Contracts/Serialization/JsonDefaults.cs`. Для DTO, которые есть только на сервере: `new JsonSerializerOptions(JsonDefaults.Options) { TypeInfoResolver = JsonTypeInfoResolver.Combine(ContractsJsonContext.Default, ServerJsonContext.Default) }` | Те же camelCase, строковые enum с fallback `Unknown`, `.fffZ`, `WhenWritingNull`. Контракт допускает отсутствие поля вместо null для агента (`cc/openapi/base.yaml:16-18`). `ServerError.Details` уже помечен `JsonIgnore(Never)` (`ErrorCode.cs:184-188`) |
| Касса (`/api/v1/admin/*`) | `AdminJson.Options` = копия с `DefaultIgnoreCondition = Never` | `T\|null` в admin-схемах обязателен и сравнивается через `=== null` (`frag/schemas-admin.yaml:7-22`; `MapPage.tsx:91`) |
| Ввод | `UnmappedMemberHandling.Skip`, `NumberHandling.Strict`. `unknown` в enum → `400 validation reason=enum` | `cc/openapi/base.yaml:201-212` |

- DTO агента и игрока **не дублируются**: `AgentRegisterRequest`, `HeartbeatRequest`, `WsFrame`,
  `ServerCommandEnvelope`, `CommandAck`, `TelemetryBatch` (`Commands/ServerCommand.cs`), `Policy` и
  `AgentServerConfig` (`Pc/PcInfo.cs`), `Session`, `SessionCreateRequest` (`Session/*`), `Balance`, `Tariff`,
  `Transaction` (`Wallet/*`), `ServerError`, `ErrorCode` (`Errors/ErrorCode.cs`, 21 код, включая `NotImplemented`)
  берутся из Contracts.
- Если нужного DTO там нет (ответы `register`/`refresh`, `QrLoginStart`, `UserStats` и т.п.), его добавляют **в
  ClubShell.Contracts**: агент тоже его читает, и CI `contracts-drift` проверит TS/Rust-зеркала.
- Admin-DTO пишутся вручную как records в `Admin/**/Dtos.cs`: их нет в Contracts, TS-источник — `apps/admin/src/api.ts`.
- Форму всех DTO проверяют схемные тесты (§10a).

### 2.5 Конфигурация (`appsettings.json`, значения по умолчанию)

| Секция:ключ | По умолчанию | Назначение |
|---|---|---|
| `ConnectionStrings:Club` | — (обязателен) | Npgsql |
| `Database:MigrateOnStart` | `true` | |
| `Contracts:OpenApiPath` | `contracts/openapi.yaml` (копируется в output) | таблица 501 |
| `Auth:Issuer` / `Auth:Audience` | `clubshell-server` / `club-agent` | JWT агента |
| `Auth:SigningKeyPath` | `data/jwt-signing-key.pem` | RSA-3072, создаётся при первом старте (§3.2) |
| `Auth:PepperPath` | `data/pin-pepper.key` | 32 случайных байта, создаются при первом старте; HMAC для PIN персонала |
| `Auth:AgentTokenMinutes` | `60` | |
| `Auth:RefreshTokenDays` | `30` | |
| `Auth:UserTokenHours` | `12` | Предел непрерывного входа игрока (N3) |
| `Auth:StaffTokenSlidingHours` / `StaffTokenAbsoluteDays` | `12` / `7` | |
| `Auth:SignatureWindowSec` | `300` | |
| `Auth:ReplayMode` | `Log` | `Log` \| `Reject` (§3.3) |
| `Club:Name` | `ClubShell` | bootstrap единственного клуба |
| `Club:TimeZone` | `Asia/Tashkent` | |
| `Club:EnrollmentKey` / `Club:PreviousEnrollmentKey` | `""` / `""` | `X-Club-Key` агента. Пустое значение закрывает регистрацию |
| `Club:AutoApprovePcs` | `false` (Development: `true`) | |
| `Club:OwnerPin` | `""` | При пустой таблице `staff`: создать владельца. Пусто → PIN генерируется и один раз пишется в лог |
| `Club:OfflineLogin` | `true` | Выдавать `offlineHash` |
| `Club:GuestLogin` | `true` | Иначе `403 guestDisabled` |
| `Realtime:PingSec` / `PongTimeoutSec` / `MaxFrameBytes` | `20` / `10` / `1048576` | |
| `Agents:HeartbeatSec` | `30` | |
| `Agents:OfflineAfterSec` | `90` | Нет WS и heartbeat старше этого → `offline` |
| `Agents:CommandTtlMin` | `10` | `expiresAt` для lock/unlock/reboot/shutdown/message |
| `Agents:AckWaitSec` | `30` | Ожидание ack в `adminCommand` |
| `Sessions:TickMs` | `1000` | |
| `Sessions:GraceSec` | `60` | → `AgentServerConfig.session.graceSec` |
| `Sessions:MaxOfflineMinutes` | `240` | |
| `Sessions:ResyncSec` | `30` | |
| `Sessions:PostpaidCreditLimit` | `0` | Тиёны; `null` = без лимита (владелец, §12) |
| `RateLimit:AgentPerMinute` / `AgentBurst` | `600` / `100` | |
| `RateLimit:PinAttempts` / `PinWindowSec` | `5` / `300` | |
| `Cors:AllowedOrigins` | `[]` (Development: `http://localhost:1431`, `http://localhost:5174`) | Касса |
| `Idempotency:TtlHours` | `24` | |
| `Telemetry:RetentionDays` | `8` | Здоровью нужна неделя плюс сутки |
| `Webhooks:TimeoutSec` / `Retries` / `AllowPrivateTargets` | `5` / `3` / `false` (Development: `true`) | |
| `Catalog:GamesSeedPath` / `ProductsSeedPath` / `PolicySeedPath` | `data/games.json` / `data/products.json` / `data/policy.json` | §12 D-14 |
| `Workers:Enabled` | `true` | Тесты: `false` |
| `Seed:Dev` | `false` (Development: `true`) | Владелец 0000, кассир 1111, тариф Standard 12 000 сум/ч, группы staff/student — то, что ждёт `console.spec.ts` |
| `Diskless:BaseUrl` | `""` | Фаза 2 (§9) |
| `Proxy:Trusted` | `[]` | CIDR прокси для `ForwardedHeaders` |
| `Proxy:ClientIpHeader` | `""` (Railway: `X-Real-IP`) | Заголовок с IP клиента, заданный прокси. Включать, только если сервер доступен исключительно через этот прокси |
| env `PORT` | — | Если задан, Kestrel слушает `http://0.0.0.0:$PORT` (так Railway передаёт порт). Иначе действует `ASPNETCORE_HTTP_PORTS` образа (8080) |

Секреты не лежат в `appsettings.json`: их задают через переменные окружения `Club__EnrollmentKey` и т.п. или
`appsettings.Production.json` вне репозитория. Пустой секрет закрывает соответствующую функцию.

---

## 3. Безопасность

### 3.1 Режимы аутентификации (`cc/openapi/base.yaml:23-56`)

| Режим | Заголовки | Маршруты | Ошибки |
|---|---|---|---|
| `club` | `X-Club-Key` | `POST /agents/register` | `401 reason=clubKey` |
| `none` | refresh-токен в теле | `POST /agents/refresh` | `401 expired\|revoked\|reused` |
| `agent` | `Authorization: Bearer <JWT>` + `X-Timestamp` + `X-Signature` | `/agents/{pcId}/*`, `/pcs/*`, `/sessions/current`, `/sessions/{id}/events`, `/games*`, `/tariffs`, `/updates/*`, `/auth/login\|guest\|qr/*`, `/anticheat/report`, `/sessions` (offline replay, §5.11) | `401 expired\|invalid\|revoked\|signature\|clockSkew` |
| agent + необязательный user | как agent; `X-User-Token` учитывается, если валиден, а невалидный **игнорируется** (никогда не 401) | `/sessions/{id}/end` (сеанс должен принадлежать ПК токена, иначе `403 pcMismatch`), `GET /games` (user-токен только добавляет `lastPlayedAt`) | как agent |
| `user` | agent + `X-User-Token` | `/users/*` (включая `game-settings`), `/wallet/{userId}/balance`, `/auth/logout`, `/sessions` (кроме offline replay), pause/resume/extend | `401 reason=userToken problem=invalid\|expired\|boundElsewhere\|revoked` |
| `staff` | `Authorization: Bearer <staff token \| ck_…>` | `/admin/*` (кроме `POST /admin/login`) | `401 reason=invalid`; `403 reason=ownerOnly` |

- **`details.reason` обязателен в каждом 401.** Агент на любой 401, кроме `clockSkew` и `userToken`, делает refresh
  (`ServerClient.cs:750-789`).
- **`pcMismatch`.** Для путей `/agents/{pcId}/*` и тел с `pcId` проверяется равенство с `sub` токена, иначе
  `403 pcMismatch`.
- **`notOwner`.** Для путей `/users/{userId}` и `/wallet/{userId}` проверяется равенство с владельцем user-токена,
  иначе `403 notOwner`. User-токен должен быть привязан к ПК из agent-токена, иначе `401 userToken
  problem=boundElsewhere`.
- **Удалённый ПК** (`pcs.deleted_at`) — `401 revoked` на всех agent-маршрутах и на refresh, WS закрывается `4401`.
  Heartbeat никогда не отвечает 404.

### 3.2 Регистрация, JWT, refresh

Порт `srv@1c68cc8:src/Club.Server/Auth/*` и `srv/TokenService.cs`.

**Регистрация по HWID и ключу клуба:**

1. `X-Club-Key` сравнивается в постоянном времени: SHA-256 обоих значений, затем `FixedTimeEquals`
   (`srv/TokenService.cs:141`). Проверяются `EnrollmentKey` и `PreviousEnrollmentKey` — это льготный период ротации:
   агенты перерегистрируются каждое утро.
2. HWID уже известен в этом клубе → тот же `pcId`, новые токены, новый `signingSecret` (32 байта),
   `credentials_version += 1`, старые refresh-токены удаляются. Статус, одобрение и открытый сеанс не меняются.
3. HWID неизвестен:
   - сначала проверяется `previousPcId`: если такой ПК есть, `hwid IS NULL` и совпадает нормализованный MAC,
     сервер предлагает место этого ПК (pre-fill);
   - создаётся ПК `approved = Club:AutoApprovePcs`, `maintenance = !approved`;
   - если ПК не одобрен → `403 forbidden {reason: pendingApproval, pcId}` (`frag/paths-agents.yaml:14-15`).
4. Прочие ответы: HWID принадлежит другому клубу сети → `409 conflict`; клуб отключён → `403 clubDisabled`.
5. Одобряет владелец через `PATCH /admin/pcs/{pcId} {maintenance:false}`, это ставит `approved=true`
   (OPEN_QUESTIONS L98). Молчаливого захвата свободного места, как в `mock/routes/pcs.ts:67-101`, нет.
6. Ответ: `{pcId, pc, accessToken, refreshToken, signingSecret(base64), expiresAt, serverTime, config}`.

**Access JWT:**

- RS256 через `JsonWebTokenHandler`, `MapInboundClaims=false`.
- Claims: `sub=pcId`, `club`, `hwid`, `role=agent`, `cv`, `iat`, `exp`, `jti`. `aud=club-agent`, `iss` из конфига.
- Валидация: `ValidAlgorithms=[RS256]`, skew 30 с через `TimeProvider`.
- Отзыв: claim `cv` должен совпадать с `pcs.credentials_version` (`srv/MachineAuthenticator.cs:28-33`).

**Refresh:**

- Непрозрачный токен: 32 байта в base64url; в БД хранится только SHA-256.
- Одноразовый: `UPDATE agent_refresh_tokens SET used_at=now() WHERE token_hash=$1 AND used_at IS NULL RETURNING`.
- Повторное использование → `cv += 1`, все refresh-токены ПК удаляются, ответ `401 reused`.
- Несовпадение `hwid` → `401 revoked`.
- `signingSecret` в v1 при refresh не ротируется: поле опционально, агент поддерживает ротацию атомарно
  (`ServerClient.cs:913-927`).

**Ключ JWT:**

- PKCS#8 PEM в `Auth:SigningKeyPath`. При первом старте создаётся RSA-3072, права файла 600, `kid` = первые 16 hex
  от sha256(modulus) (`srv/TokenService` :116-128).
- Ротация — заменить PEM и перезапустить сервер. Все access-токены становятся `invalid`, агент один раз делает
  refresh; refresh-токены хранятся в БД и не затрагиваются. JWKS не нужен: токены проверяет только этот сервер.
- Runtime-папка `data/` (ключи, seed-JSON) целиком в `.gitignore`. Исходники не лежат в папке `data/` или `Data/`:
  на Windows это одна и та же папка, поэтому миграции живут в `Db/Migrations`. В продакшене `data/` — volume (S6).

`signing_secret` хранится в `pcs` открытым `bytea`: HMAC симметричен, иначе нельзя. Это отражено в README.

### 3.3 Подпись запросов HMAC

```
X-Signature = lowercase hex( HMAC-SHA256( base64decode(signingSecret),
              X-Timestamp + METHOD + raw request-target (с /api/v1 и query, без декодирования) + hex(SHA256(raw body)) ) )
```

- Тело буферизуется (`EnableBuffering`) до чтения. Пустое тело хешируется как `e3b0c442…b855`.
- Путь — сырой request-target (`IHttpRequestFeature.RawTarget`, `srv@1c68cc8 AgentAuthMiddleware.cs:139`).
  Прокси не должен переписывать путь.
- Окно ±`SignatureWindowSec`. Вне окна → `401 reason=clockSkew` и заголовок `X-Server-Time`.
- Нет подписи → `401 reason=signature problem=missing`; неверная → `problem=mismatch`. Сравнение
  `CryptographicOperations.FixedTimeEquals`.
- Подпись **обязательна** для режимов agent и user. Режима «только логировать», как в моке
  (`mock/index.ts:70-75`), нет.
- Тест-векторы из `cc/openapi/base.yaml:115-121` — первый юнит-тест S0.

**Правило повторов (решение по умолчанию).** Кортеж (`pcId`, `X-Timestamp`, `X-Signature`) может совпасть
легитимно: два одинаковых `GET` в одну секунду, повтор после refresh без ротации секрета, повторы старых агентов с
той же подписью (`cc/openapi/base.yaml:88-107`). Поэтому:

- `Auth:ReplayMode=Log` (по умолчанию): повтор кортежа **никогда** не отклоняется. `ReplayLog` (в памяти, TTL
  2×окно) пишет `LogInformation` для `POST`/`PATCH` без `Idempotency-Key`. Деньги защищены хранилищем
  идемпотентности (§7.1) и уникальными индексами.
- `Auth:ReplayMode=Reject` (включается только по решению владельца): отклоняются лишь `POST`/`PATCH` без
  `Idempotency-Key`, и только если кортеж уже завершился ответом не 5xx/408/429. Ответ `409 conflict
  details.reason=replay`, **никогда не 401** — на 401 агент сделал бы лишний refresh.
- `ReplayGuard` из `srv@1c68cc8` в режиме отказа по умолчанию **не** переносится.

### 3.4 Токены игрока

- `POST /auth/login` (`kind` = `password` | `card` | `token`) и `/auth/guest` выдают непрозрачный токен: 32 байта,
  в БД — SHA-256.
- Токен привязан к паре (user, pc). `UNIQUE(pc_id)` на `user_tokens`: новый вход на ПК вытесняет прежнего игрока.
  TTL — `Auth:UserTokenHours`. Refresh пользовательского токена не существует (`ServerClient.cs:929-930`).
- Пароли игроков хранятся как `pbkdf2$210000$<salt b64>$<hash b64>` (PBKDF2-SHA256, соль 16 байт, хеш 32 байта,
  `Rfc2898DeriveBytes.Pbkdf2`). Тот же формат у `offlineHash`, который агент проверяет сам
  (`OfflineSessionStore.cs:65, 796-802`).
- `offlineHash` вычисляется **заново** из присланного пароля с новой солью, только для `kind=password` и только
  при `Club:OfflineLogin`. Серверный хеш наружу не отдаётся.
- Unlock-PIN профиля (4–6 цифр) хранится тем же PBKDF2.
- `kind`: `password` — username + пароль; `card` — `users.card_id` без учёта регистра; `token` — в контракте нет
  операции, которая выпускала бы такие токены, поэтому в v1 всегда `401 badCredentials`; `qr`/`guest` в
  `/auth/login` → `400 validation field=kind reason=enum` (для них отдельные операции).
- `403` при входе: `banned` — `users.banned` или `client_profiles.blacklisted`; `ageRestricted` — несовершеннолетний
  (`limits.minorAge`, `birth_year`) во время `limits.minorCurfew` по местному времени; `zoneNotAllowed` в v1 не
  отдаётся (модели «зона только для роли» нет); `pcMismatch` — `pcId` тела ≠ `sub`. `409 conflict
  reason=activeSessionElsewhere {pcId, pcName}` — открытый сеанс пользователя на другом ПК.
- Успешный вход отзывает прежний user-токен этого ПК и возвращает открытый сеанс этого пользователя на этом ПК.
- `badCredentials`: `401` с `details.attemptsLeft`. Неудачи считаются **один раз на `X-Trace-Id`** — агент повторяет
  запрос после refresh с тем же trace-id (N2). Блокировка после 5 неудач за 15 мин: `attemptsLeft=0`, дальше тот же
  401 до конца окна.
- Отзыв: `logout`, смена пароля кассиром (OQ-24), блокировка или бан. Токены удаляются, агенту уходит push
  `userRevoked {userId, reason}`.

### 3.5 Персонал, роли, ключ API

- `POST /admin/login {pin}` → непрозрачный токен (32 байта, в БД SHA-256), срок скользящий 12 ч, абсолютный 7 д.
- PIN хранится как `HMAC-SHA256(pepper, pin)`. Хеш детерминирован и индексируется: вход идёт по одному PIN, поэтому
  bcrypt не подходит. `UNIQUE(network_id, pin_hmac)`, уникальность проверяется и при `PATCH` (`400 reason=taken`).
- Каждый запрос проверяет `staff.active`. Деактивация сразу убивает все токены сотрудника.
- Роли `owner` | `cashier`. Owner-only маршруты перечислены в §11 (S4/S5). Поле `blacklisted` в
  `PATCH /admin/clients/{id}` тоже только для владельца (`x-owner-only-fields`). Нарушение → `403 reason=ownerOnly`.
- **`ck_…` = токен владельца** (`frag/paths-admin.yaml:7, 941, 984`). Действия записываются в аудит от
  синтетического сотрудника «API key». Ключ хранится в извлекаемом виде (`clubs.api_key` text), потому что
  `GET /admin/club/api-key` его возвращает; сравнение в постоянном времени.
- `ck_` **отдельный** от `X-Club-Key` агента (§12 D-6), поэтому его ротация не ломает регистрацию агентов.
  Область `ck_` — только `/api/v1/admin/*`: как `X-Club-Key` и на agent/user-маршрутах он не принимается, а
  enrollment-ключ не принимается как staff-токен. `POST /admin/club/api-key` делает прежний `ck_` недействительным
  сразу, без льготного периода. `POST /admin/logout` с `ck_` его не отзывает.
- Dev-токена `admin-dev-token` (`mock/routes/club.ts:63`) нет.
- `401` на `/admin/*` означает только проблему аутентификации: касса сбрасывает токен на любой 401
  (`apps/admin/src/api.ts:108-111`).

### 3.6 Rate limits (встроенный `Microsoft.AspNetCore.RateLimiting`)

| Лимитер | Ключ | Лимит | Ответ |
|---|---|---|---|
| agent | `sub` JWT | token bucket 600/мин, burst 100 | `429 rateLimited`, `Retry-After`, `details.retryAfterSec` |
| register/refresh | IP | 30/мин | то же |
| admin login | IP | 5 неудач за 300 с | то же (нужна правка контракта, OQ-4) |
| player login | (user, pc) | см. §3.4 | `401 badCredentials attemptsLeft=0` |

### 3.7 Аудит

- **Действия персонала** пишутся в `audit_entries` (append-only, триггер). Это журнал `/admin/control`: staff,
  shiftId, userId, pcId, amount, detail, meta. Аудит пишется **после** валидации и в той же транзакции, что и
  действие. Мок пишет его до валидации (`mock/routes/admin.ts:258`). Лимита в 5000 записей нет.
- **Деньги** — в `ledger_entries` (append-only, §4.3).
- **События безопасности** пишутся в структурированный лог, без таблицы: регистрация, pendingApproval,
  refresh-reuse, несовпадение подписи, replay, неверный PIN.

### 3.8 CORS кассы (`cc/openapi` info §11)

- Только `/api/v1/admin/*`. Preflight `OPTIONS` → `204` **без** аутентификации для GET/POST/PUT/PATCH/DELETE.
- Origin из `Cors:AllowedOrigins` (точное совпадение), `Vary: Origin`; чужой origin — без CORS-заголовков.
- `Access-Control-Allow-Headers: Authorization, Content-Type, Idempotency-Key`;
  `Access-Control-Expose-Headers: ETag, X-Trace-Id, X-Server-Time, Retry-After`.
- CORS-заголовки ставятся и на ответы с ошибкой (401/403/4xx/5xx), иначе касса не прочитает конверт.
- `Content-Type: application/json` на GET/DELETE без тела принимается (касса шлёт его всегда).
- Каждый успешный admin-ответ имеет JSON-тело (`res.json()`, `apps/admin/src/api.ts:114`): без результата —
  `{ok:true}`; голого 204 нет. Успех — 200, кроме `POST /admin/sessions` → 201.

---

## 4. Модель данных

Стиль club-server (`srv/Data/Migrations/M0001_Agents.cs`):

- сырой SQL в FluentMigrator, у каждой миграции есть `Down`;
- snake_case, `text` + `CHECK` вместо PG enum, `timestamptz` в UTC, усечённый до мс;
- id — `uuid` (`Guid.CreateVersion7()`), кроме id, которые создаёт касса (`text`);
- деньги — `bigint` тиёнов, валюта `UZS` (CHECK).

### 4.1 Мультиарендность

- Таблица `networks` содержит клубы (`clubs`).
- **Уровень сети:** `users`, `wallets`, `ledger_entries`. Один кошелёк игрока действует во всех клубах
  (`mock/network.ts:1-6, 278-279`).
- **Уровень клуба:** `club_id` в каждой физической или конфигурационной строке. Составные FK `(club_id, x_id)`
  не дают ссылаться на объекты другого клуба.
- Клуб берётся из контекста аутентификации: claim `club` у агента, `staff.club_id` у персонала.
- Развёртывание v1 — одна сеть и один клуб (bootstrap из `Club:*`). RLS не используется.

### 4.2 Таблицы

Миграции: M0001 — ядро и агенты (S0/S1), M0002 — игроки, деньги и сеансы (S2), M0003 — каталог (S3), M0004 —
касса (S4), M0005 — остальное admin (S5).

| Таблица | Ключевые колонки | Ограничения / индексы | Миграция |
|---|---|---|---|
| `networks` | id, name, created_at | | M0001 |
| `clubs` | id, network_id, name, city, address, time_zone (default `Asia/Tashkent`), currency (`UZS`), enrollment_key_hash bytea, prev_enrollment_key_hash bytea NULL, api_key text NULL, settings jsonb (AdminClubSettings без `promoCodes`/`automation`/`webhooks`, `apiKey`, `events`), health_settings jsonb (`AdminHealthSettings` — в AdminClubSettings его нет), settings_version int, config_version int, catalog_version int, policy jsonb, policy_version int, disabled bool, created_at, updated_at | CHECK currency='UZS' | M0001 |
| `pcs` | id, club_id, number, name, zone, x, y, device_kind (`pc\|console\|vr\|other`), hwid NULL, mac_address NULL (нормализованный lower-case с двоеточиями), machine_name, ip_address, approved bool, maintenance bool, credentials_version int, signing_secret bytea, agent_version, shell_version, last_heartbeat_at NULL, last_heartbeat jsonb NULL (runningGames, offlineQueue, shellConnected, …), hardware jsonb NULL, created_at, updated_at, deleted_at NULL | `UNIQUE(club_id,id)`; `UNIQUE(club_id,number) WHERE deleted_at IS NULL AND approved` (место держит только одобренный ПК: ожидающий одобрения может нести предзаполненное место заменяемого ПК, §3.2/§9, такой ПК всегда создаётся pending; одобрение на занятое место → 409); `UNIQUE(hwid) WHERE hwid IS NOT NULL AND deleted_at IS NULL` (HWID уникален в пределах всей сети, иначе 409); `number BETWEEN 1 AND 9999` | M0001 |
| `agent_refresh_tokens` | token_hash bytea PK, pc_id FK ON DELETE CASCADE, cv int, expires_at, used_at NULL, created_at | | M0001 |
| `agent_commands` | id, club_id, pc_id, name, payload jsonb NULL, issued_by_staff_id NULL, supersedes uuid NULL, created_at, expires_at NULL, delivered_at NULL, acked_at NULL, ack jsonb NULL, superseded_at NULL | `INDEX (pc_id, created_at) WHERE acked_at IS NULL AND superseded_at IS NULL` | M0001 |
| `pc_metrics` | pc_id, at, data jsonb | PK (pc_id, at); чистка по `Telemetry:RetentionDays` | M0001 |
| `telemetry_events` | id bigint identity, club_id, pc_id, kind text, at, data jsonb, received_at | `INDEX (club_id, received_at)`; чистка 30 д | M0001 |
| `staff` | id, network_id, club_id, name, role (`owner\|cashier`), pin_hmac bytea, active, created_at, updated_at | `UNIQUE(network_id, pin_hmac)` | M0001 (для bootstrap) |
| `staff_tokens` | token_hash PK, staff_id, club_id, created_at, last_used_at, expires_at, revoked_at NULL | | M0001 |
| `users` | id, network_id, username, display_name, avatar_url NULL, role (`guest\|member\|vip\|admin`), locale (`en\|ru\|uz`), flags text[], password_hash NULL, unlock_pin_hash NULL, card_id NULL, banned bool, transient bool (гость), created_at, last_seen_at, deleted_at NULL | `UNIQUE(network_id, lower(username))`; `UNIQUE(network_id, lower(card_id)) WHERE card_id IS NOT NULL` | M0002 |
| `user_tokens` | token_hash PK, user_id, pc_id, created_at, expires_at | `UNIQUE(pc_id)` | M0002 |
| `login_failures` | user_id, trace_id uuid, at | PK (user_id, trace_id) — считаем один раз на trace-id | M0002 |
| `qr_logins` | token_hash PK, club_id, pc_id, user_id NULL, created_at, expires_at, confirmed_at NULL, consumed_at NULL | | M0002 |
| `client_profiles` | club_id, user_id, group_id text NULL, note, blacklisted, phone, birth_year NULL, updated_at | PK (club_id, user_id) | M0002 |
| `wallets` | user_id PK, network_id, currency, main_balance bigint, bonus_balance bigint (=0 в v1, §5.8), lifetime_spent bigint ≥0, version bigint, updated_at | CHECK bonus ≥ 0 | M0002 |
| `ledger_entries` | id (= `Transaction.id`), op_id, user_id, network_id, club_id, type (`topUp\|charge\|refund\|bonus\|purchase\|adjustment`), amount bigint ≠0 со знаком, balance_after bigint, method NULL (`cash\|card\|payme\|click\|uzum`), description, ref NULL, session_id NULL, shift_id NULL, staff_id NULL, pc_id NULL, overdraft bool, meta jsonb (quote), created_at | индексы (user_id, created_at DESC), (club_id, created_at), (shift_id), (session_id); триггер append-only | M0002 |
| `tariffs` | id, club_id, name, price_per_hour bigint, min_minutes, max_minutes NULL, zones text[], time_windows jsonb, is_package, package_minutes NULL, package_price NULL, created_at, updated_at, deleted_at NULL | CHECK пакет ⇒ оба поля; soft delete | M0002 |
| `sessions` | id, club_id, pc_id, user_id, tariff_id, state (`active\|paused\|locked\|ending\|ended`), is_prepaid, origin (`kiosk\|cashier\|offline`), started_at, ended_at NULL, end_reason NULL, purchased_sec, used_before_sec, running_since NULL, paused_at NULL, ends_at NULL, last_transition_at, price_per_hour_snapshot bigint, day_pct, discount_pct, discount_reason NULL, charged_total, refunded_total, warnings_sent int[], created_by_staff_id NULL, client_session_id NULL, agent_reported jsonb NULL (secondsUsed/endedAt для аудита), created_at, updated_at | `UNIQUE(pc_id) WHERE state<>'ended'`; `UNIQUE(user_id) WHERE state<>'ended'`; `INDEX(ends_at) WHERE is_prepaid AND state IN ('active','locked','ending')`; FK (club_id,pc_id), (club_id,tariff_id) | M0002 |
| `session_events` | id identity, session_id, club_id, source (`agent\|server\|staff`), type, at, received_at, data jsonb, applied bool | `UNIQUE(session_id,type,at) WHERE source='agent'`; append-only | M0002 |
| `idempotency_keys` | principal, method, path, key uuid, request_hash bytea, status_code, response jsonb NULL, created_at | PK (principal, method, path, key); INDEX(created_at) | M0001 |
| `games` | id, club_id, title, settings_paths text[] NULL, data jsonb (DTO `Game`), updated_at, deleted_at NULL | | M0003 |
| `launch_reports` | id, club_id, pc_id, game_id, user_id NULL, session_id NULL, phase, started_at, data jsonb, created_at | `UNIQUE(session_id, phase, started_at)` | M0003 |
| `anticheat_reports` | id, club_id, pc_id, data jsonb, at | составной FK на pcs; пишут WS `anticheatViolation` (S1) и REST (S3) | M0001 |
| `shifts` | id, club_id, staff_id, staff_name, opened_at, closed_at NULL, opening_cash, closing_cash NULL, expected_cash NULL, totals jsonb NULL | `UNIQUE(club_id) WHERE closed_at IS NULL` | M0004 |
| `audit_entries` | id, club_id, at, staff_id, staff_name, shift_id NULL, action, user_id NULL, pc_id NULL, amount bigint, detail, meta jsonb | INDEX (club_id, at DESC), (club_id, staff_id, at DESC); append-only | M0004 |
| `promo_codes` | id, club_id, code, kind (`bonus\|discountPct`), value, uses_left NULL, used, expires_at NULL, created_at, deleted_at NULL | `UNIQUE(club_id, upper(code)) WHERE deleted_at IS NULL` | M0005 |
| `promo_redemptions` | promo_code_id, user_id, op_id, staff_id, redeemed_at | PK (promo_code_id, user_id) | M0005 |
| `products` | id, club_id, title, category, price, image_url, in_stock, stock_qty NULL ≥0, tags text[], created_at, updated_at, deleted_at NULL | | M0005 |
| `automation_rules` | id text, club_id, name, enabled, trigger jsonb, action jsonb, fired int, last_fired_at NULL | PK (club_id, id) | M0005 |
| `rule_firings` | club_id, rule_id, target_key, fired_at | PK (club_id, rule_id, target_key) | M0005 |
| `webhooks` | id text, club_id, url, events text[], enabled, last_status NULL, last_at NULL | PK (club_id, id) | M0005 |
| `webhook_outbox` | id identity, club_id, webhook_id, payload jsonb, attempts, next_at, sent_at NULL | INDEX(next_at) WHERE sent_at IS NULL | M0005 |
| `health_tickets` | id, club_id, pc_id, kind, severity, status (`open\|inWork\|resolved`), issues jsonb, note NULL, put_maintenance bool, created_at, updated_at, resolved_at NULL | `UNIQUE(pc_id, kind) WHERE status<>'resolved'` | M0005 |

**Что не хранится, а выводится:**

- `PcStatus` (§6.6) и `Pc.currentSessionId` — из индекса открытых сеансов. Мок хранит оба поля отдельно, и они
  расходятся (`mock/routes/session.ts:215-216`).
- `UserStats`:
  - часы и число сеансов — из `sessions`;
  - `spent` = `lifetime_spent`;
  - `favoriteGames` — из `launch_reports`;
  - `rank` — по `lifetime_spent`.
- `AdminClient.visits` — число сеансов; `AdminClient.spent` = `lifetime_spent`; `level`/`levelName` — из
  `settings.loyalty`; `bonus` = 0 (D-9); `telegram` не хранится и не отдаётся (`x-prohibited`).
- `Game.lastPlayedAt` — `max(started_at)` успешных `launch_reports` фазы `launch` для (user, game).
- `User.balance` — из `wallets`; `User.loyaltyLevel`/`loyaltyPoints` — §5.13 п. 5.
- Число офлайн-обрывов ПК за сутки (issue `unstable` в здоровье) — из `telemetry_events kind='pcOffline'`, которые
  пишет `PcStatusWorker` (§6.6).
- Достижения — `[]`.

Групп, бонусных порогов, happy hours, уровней лояльности, баннеров и т.п. в отдельных таблицах нет: всё это лежит в
`clubs.settings` jsonb и валидируется JSON-схемой контракта. Исключение — массивы с серверными счётчиками:
`promoCodes` → `promo_codes`, `automation` → `automation_rules`, `webhooks` → `webhooks`. `PATCH /admin/club` с таким
ключом синхронизирует таблицу по `code`/`id` в одной транзакции (новые — insert, пропавшие — soft delete / delete),
но счётчики (promo used/usesLeft, fired/lastFiredAt правил, lastStatus/lastAt вебхуков) из тела игнорирует
(OQ-12); `GET /admin/club` собирает документ обратно. Константа `events` (8 значений) не хранится.

### 4.3 Леджер (деньги)

- Операция — одна транзакция БД:
  1. `SELECT … FROM wallets WHERE user_id=$1 FOR UPDATE`;
  2. проверки;
  3. `INSERT ledger_entries` (1..n строк с общим `op_id`, `balance_after` берётся из заблокированной строки);
  4. `UPDATE wallets` (`version+1`, `lifetime_spent`).
- Единственная точка записи — `Wallet/Ledger.cs`. Pushes и вебхуки уходят **после** commit.
- Баланс — это кеш суммы по леджеру. Тест в CI (и ночная проверка в `MaintenanceWorker`) сверяет
  `main_balance = SUM(amount)`. Исправления — только строками `adjustment`; `UPDATE` и `DELETE` запрещены триггером.
- Отрицательный `main_balance` допускается только в двух случаях, со строкой `overdraft=true`: расчёт постоплаты при
  завершении и офлайн-реплей (сеанс или событие `extended`). Во всех остальных случаях — `402 insufficientFunds
  {required, available}`.
- `lifetime_spent` += |charge|, |purchase| и −= refund. Мок возвраты не учитывает (`mock/club.ts:419-427`).
- **Смена:** каждая строка получает `shift_id` открытой смены. Писатель берёт `SELECT id FROM shifts … FOR SHARE`,
  закрытие берёт `FOR UPDATE`, так что ни одна строка не проскочит мимо Z-отчёта.
- **X/Z-отчёт:** суммы по `shift_id` с группировкой по type/method. `expectedCash = opening_cash + Σ topUp` с
  `method='cash'`. Регулярки по описанию, как в `mock/club.ts:689`, нет.
- **Провод:** `Transaction {id, userId, type, amount:Money, balanceAfter:Money, description, createdAt, ref}`
  (`cs/src/ClubShell.Contracts/Wallet/Transaction.cs`).
- **Валюта:** только `UZS`; любая другая во входе → `400 validation reason=unsupported` (OPEN_QUESTIONS L63).

### 4.4 Конкурентность

`READ COMMITTED`, явные блокировки строк, инварианты — уникальными индексами; `SERIALIZABLE` не используется.

- **Порядок блокировок** (глобальный, против дедлоков): строка идемпотентности → `sessions` → `wallets` →
  `products` (по id) → `shifts` (`FOR SHARE`). `SET LOCAL lock_timeout = '10s'`.
- **Гонки создания сеанса** (киоск/касса/реплей, два ПК одного игрока) — частичные уникальные индексы
  `sessions_open_pc/user`; `unique_violation` → `409 sessionAlreadyActive`.
- **Мутации сеанса** (pause/resume/extend/end/events/тик/касса) — `FOR UPDATE` на строке сеанса, затем проверка
  состояния; тик — `SKIP LOCKED`.
- **Промокод** — один атомарный `UPDATE promo_codes SET uses_left=uses_left-1, used=used+1 WHERE … AND (uses_left IS
  NULL OR uses_left>0) AND (expires_at IS NULL OR expires_at>now()) RETURNING` + `INSERT promo_redemptions` (PK
  (promo, user) → `400 reason=exhausted` при повторе клиентом).
- **Склад** — `UPDATE products SET stock_qty = coalesce(stock_qty,0) + $n …` атомарно, `CHECK stock_qty ≥ 0`.
- **Денежные правила автоматизации** — `INSERT rule_firings … ON CONFLICT DO NOTHING` до зачисления; счёт визитов
  для `visitCount` не включает только что открытый сеанс (ошибка мока `mock/club.ts:439-441`).
- **PATCH `/admin/club`** — `FOR UPDATE` строки `clubs` + проверка `settings_version`; redeem промокода и PATCH
  сериализуются на этой же строке.
- **Refresh-токены** — одноразовое потребление `UPDATE … WHERE used_at IS NULL RETURNING` (§3.2).

---

## 5. Биллинг и сеансы (сервер — авторитет)

### 5.1 Цена — одна функция на все каналы

Каналы: создание и продление в киоске, открытие и продление кассой, `adminQuote`, офлайн-реплей, постоплата.

```
base    = tariff.is_package ? package_price : ceil(price_per_hour × minutes / 60)       -- = Tariff.PriceFor (cs/src/ClubShell.Contracts/Wallet/Tariff.cs:119-131)
local   = at в clubs.time_zone
dayPct  = local.date ∈ settings.pricing.holidays ? holidayPct : weekdayPct[local.dow, 0=Sun]
disc    = max(группа клиента, уровень лояльности(lifetime_spent), happy hour(pc.zone, local), 0)   -- без суммирования
total   = (base × dayPct × (100 − disc) + 500_000) / 1_000_000 × 100                      -- целочисленно, half-up до целого сума
```

- Формула совпадает с float-версией мока (`mock/club.ts:490-516`) на 691 920 комбинациях (скрипт domain-скаута).
- Первый тест S2 — e2e-кейс `console.spec.ts:84-148`: 12 000 сум/ч × 120 % × 70 % = 1 008 000 тиёнов; скидка группы
  staff 50 % побеждает happy hour 30 %.
- Промокод вида `discountPct` при redeem → `400 discountAtCheckout`: скидки на кассе в v1 нет.
- Округления (единые для всех каналов): база почасового тарифа — ceil до 1 тиёна; `total` — half-up до 100 тиёнов;
  минуты постоплаты — `ceil(sec/60)`; возврат — floor до 100 тиёнов; бонус порога пополнения — half-up до 100
  тиёнов (`frag/schemas-admin.yaml:903`). Бонус лояльности киоска из мока (`mock/routes/wallet.ts:59-64`) не
  переносится.

### 5.2 Проверка покупки (create и extend, киоск и касса одинаково)

Порядок проверок и ответы:

| # | Правило | Ошибка | Источник |
|---|---|---|---|
| 1 | ПК, пользователь, тариф существуют (тариф не удалён) | `404 what=pc\|user\|tariff` | mock admin.ts:104-127 |
| 2 | ПК не в maintenance | `403 policyDenied rule=pcMaintenance` | |
| 3 | Нет открытого сеанса у ПК или у пользователя (гарантирует уникальный индекс) | `409 sessionAlreadyActive {sessionId, pcId}` | |
| 4 | Не бан и не blacklisted (клуб) | `403 policyDenied rule=blacklisted` (касса) / `403 banned` (вход) | OQ-7 |
| 5 | Комендантский час для несовершеннолетних (`limits.minorCurfew`, до 06:00, `birth_year`) | `403 rule=minorCurfew` | mock club.ts:443-453 |
| 6 | Зона тарифа (без учёта регистра; пустая = все зоны) | `403 rule=tariffZone` | OQ-8 (касса тоже) |
| 7 | Окно времени тарифа по местному времени | `403 rule=tariffTime` | Tariff.cs:134-155 |
| 8 | Минуты: почасовой тариф — min..max при создании; сумма купленного ≤ max при продлении. Пакет — `minutes := package_minutes` | `400 minutes required\|min\|max` | ≠ mock session.ts:286-290 |
| 9 | Постоплата запрещена гостю | `403 rule=postpaidNotAllowed` | |
| 10 | Средства: `available = main_balance` ≥ цене (кроме офлайн-реплея) | `402 {required, available}` | |

### 5.3 Создание (`POST /sessions`, `adminOpenSession`)

Всё выполняется в одной транзакции с идемпотентностью (§7.1):

1. `INSERT sessions`. Уникальный индекс превращает гонку в `409` без окна check-then-insert.
2. **Предоплата:** `price = quote(now)` → списание (`charge`, `ref=sessionId`, `meta=quote`);
   `purchased_sec = minutes×60`; `running_since = started_at = now`; `ends_at` = начало + купленное.
3. **Постоплата:** `purchased_sec=0`; `day_pct`, `discount_pct` и `price_per_hour_snapshot` фиксируются из
   `quote(now)`.
4. `id = clientSessionId`, если такой id свободен.
5. После commit:
   - `sessionUpdated` → ПК, `walletUpdated` → пользователь (`pcStatusChanged` не шлётся: в AsyncAPI он
     `notImplemented`, касса опрашивает `/admin/overview`);
   - хуки автоматизации `sessionStarted` и `visitCount` (дедупликация через `rule_firings`);
   - вебхук `sessionOpened`;
   - аудит `sessionOpen` (касса).

Ответ: киоску — `201 Session`, кассе — `201 {session, charged, balance}`.

### 5.4 Часы

```
used(t)     = used_before_sec + (state ∈ {active, locked, ending} ? t − running_since : 0)
secondsUsed = used(now) (у завершённого — used(ended_at))
secondsLeft = prepaid ? max(0, purchased − used(now)) : −1
cost        = prepaid ? charged_total − refunded_total : quote_frozen(ceil(used/60))
endsAt      = ends_at | ended_at
```

- `locked` **не** останавливает оплату: в Tariff нет поля для этого, у агента `PauseTimerOnLock=false`
  (`SessionManager.cs:116`).
- `GET /sessions/current` отдаёт точный `secondsLeft`: агент по нему пересинхронизирует таймер
  (`SessionManager.cs:770-858`).

### 5.5 Pause / resume

- **pause** (`active` → `paused`): `used_before = used(now)`, `running_since=NULL`, `paused_at=now`,
  `ends_at=NULL`. Ошибки: `409 alreadyPaused`, `409 sessionNotActive`.
- **resume** (`paused` → `active`):
  - для предоплаты нужно `left > 0`, иначе `409 sessionNotActive`;
  - `running_since=now`, `ends_at` пересчитывается;
  - ошибки: `409 notPaused`, `402`.
- Каждый переход обновляет `last_transition_at` и пушит `sessionUpdated`.

### 5.6 Продление (`/sessions/{id}/extend`, `adminExtend`)

- Только предоплата, иначе `409 postpaidSession`. Допустимые состояния: `active`, `paused`, `locked`, `ending`,
  иначе `409 sessionNotActive`.
- Проверки из §5.2, цена `quote(now)`, списание.
- `purchased_sec += minutes×60`; `tariff_id` меняется на тариф продления; `warnings_sent='{}'`; `ending` → `active`;
  `ends_at` пересчитывается.
- Push `sessionUpdated` и `walletUpdated`, аудит `sessionExtend` (касса). Продление кассой дополнительно ставит
  команду `extendSession {sessionId, minutes, charge:false}` (§6.4): агент перечитывает сеанс, а не списывает
  повторно (`CommandReceiver.cs:388-394`).

### 5.7 Завершение (агент `/end`, касса `/admin/sessions/end`, тик, позднее событие `ended`)

1. Сеанс берётся `SELECT … FOR UPDATE`. Если он уже `ended` → `409 sessionNotActive` с `details.session`: агент
   считает это успехом (`ServerClient.cs:307-319`). Для сеансов, известных серверу, `403`/`404` не отдаются
   (§B.25 скаута).
2. `t_end` = серверное `now` для живых вызовов и `at` события для `ended`. Присланные агентом `secondsUsed`/`endedAt`
   записываются в `agent_reported` только для аудита (`frag/paths-auth-sessions.yaml:1104-1107`).
3. Для предоплаты `used = min(used(t_end), purchased)`.
4. **Возврат** — только при предоплате, причине ∈ {`admin`, `error`} и не пакетном тарифе:
   `refund = floor_to_100((charged_total − refunded_total) × (purchased − used) / purchased)`. Возврат
   пропорционален **фактически оплаченному**, поэтому утечки нет. У мока при happy hour: оплачено 840 000, вернули
   1 200 000 (`mock/routes/session.ts:81-83`).
5. **Постоплата:** `charge = quote_frozen(ceil(used/60))`, `overdraft` допускается.
6. `state=ended`, заполняются `ended_at` и `end_reason`.
7. После commit: `sessionUpdated`, `walletUpdated`, аудит `sessionEnd` с `meta.sessionMinutes` (флаги earlyEnd).
   Если завершение начал сервер (касса, тик), в очередь ставится команда `endSession {sessionId, reason}` (§6.4):
   агент завершает локально, его `/end` получает `409 sessionNotActive` с `details.session` и считает это успехом.

Ответ: `SessionEndResult {session, charged, refunded}`.

### 5.8 Бонусы (решение по умолчанию D-9)

- В v1 соблюдается текст контракта (`frag/paths-admin.yaml:540`; `frag/schemas-admin.yaml:2387-2388`): бонусы
  порогов пополнения, промокодов и автоматизации зачисляются на **основной** баланс строкой `type=bonus`
  с реальной суммой.
- `wallets.bonus_balance` = 0, `Balance.bonus` = 0.
- В отчётах бонусы идут отдельной строкой `bonuses` и не считаются выручкой.
- Отдельный бонусный карман — вопрос владельцу (§12 Q-1).

### 5.9 Конфиг агента, от которого зависит биллинг и 501

`AgentServerConfig` строит `AgentConfigBuilder`. `version` = `clubs.config_version` = `HeartbeatResponse.configVersion`.

Что отправляется:

- `session.graceSec` = `Sessions:GraceSec`; `offline.maxOfflineMinutes` = 240.
- `shell.features`: `shop`, `chat`, `booking`, `tournaments`, `topup`, `apps`, `callAdmin` = **false** явно;
  `profile` = true. Старые агенты считают отсутствующий флаг равным true.
- `games.accountPool.enabled=false`, `games.cloudSave.enabled=false`, `updates.enabled=false`,
  `anticheat.reportViolations=true` (операция реализована, §11 S3).
- `theme` ∈ {`default`, `neon`}, `themes: []`.
- `shell.club` = {name, accent, logoUrl, wallpaperUrl, живые баннеры, rules}.

Выбор фич владельца хранится и возвращается в `GET /admin/club` (OQ-10), но агенту уходит `false`.

Когда растут версии:

- `config_version` — PATCH `features`/`branding`/`catalog`/`rulesText`/`banners`, смена набора живых баннеров
  (§8), а также `PATCH /admin/pcs/{pcId}` с изменением `name`/`zone`/`number` (они входят в
  `AgentServerConfig.pcName/zone/number`).
- `catalog_version` — изменение каталога игр (seed, `catalog.order/hidden`, `PATCH /admin/games/{id}`) и смена зоны ПК
  (каталог и тарифы фильтруются по зоне).
- `policy_version` — перезагрузка seed-политики.
- После bump подключённым ПК клуба ставится `refreshConfig` с **явными** флагами (`config`/`games`/`tariffs`) или
  `reloadPolicy` (§6.4) — без этого агент увидит изменение только со следующим heartbeat (≤30 с). Пустой
  `refreshConfig` не шлётся: он перечитал бы и `apps`/`products`, которые отвечают 501.

### 5.10 Время вышло (тик), постоплата без средств, офлайн

`SessionTickWorker`, раз в 1 с:

```
SELECT … FROM sessions
WHERE is_prepaid AND state IN ('active','locked','ending') AND ends_at <= now()
FOR UPDATE SKIP LOCKED LIMIT 100
```

- В `ends_at`: `state=ending`, push.
- В `ends_at + GraceSec`: завершение с `timeUp`. Грация бесплатна: `used` ограничен купленным.
- Это касается только ПК **онлайн** (§6.6). Для ПК офлайн сервер ждёт реплея событий от агента — пока агент офлайн,
  авторитет он. После `MaxOfflineMinutes` тишины сеанс завершается принудительно с `timeUp`.
- **Постоплата:** если `cost ≥ main_balance + PostpaidCreditLimit`, сеанс завершается `timeUp` (решение D-10). Мок
  так не делает (`mock/routes/session.ts:85-90`).
- Каждые `ResyncSec` активным ПК уходит `sessionUpdated`.
- Предупреждения `warning` шлёт сам агент. Сервер только пишет их в `warnings_sent` и запускает автоматизацию
  `minutesLeft`.

### 5.11 Офлайн-реплей создания (`POST /sessions` с `startedAt` + `clientSessionId`)

- **Аутентификация:** достаточно токена агента (решение D-11, `frag/paths-auth-sessions.yaml:825-831`).
  `pcId` = `sub`; пользователь существует, не забанен и не blacklisted.
- `startedAt` старше `MaxOfflineMinutes` → `400 tooOld`. `startedAt` ограничивается сверху значением `now`.
- Цена — `quote(startedAt)`. Списание идёт с `overdraft`, без `402`: иначе агент завершит сеанс `error`, и игра
  потеряется (`SessionManager.cs:717-721`). Диапазон минут для постоплаты и отказ `tariffTime` не применяются.
- `id = clientSessionId`, если свободен. Если такой id уже есть на этом ПК, возвращается существующий сеанс.
  `running_since = startedAt`, `origin='offline'`.
- Идемпотентность: при реплее тело отличается от исходного, но возвращается сохранённый ответ
  (`frag/paths-auth-sessions.yaml:781-782`).

### 5.12 Поздние события (`POST /sessions/{id}/events`, ≤100 событий, ключ идемпотентности обязателен)

Порядок обработки:

1. Существует ли сеанс (по `id` или `client_session_id`)? Если нет → `404`, откат, ключ **не** сохраняется: агент
   повторит после реплея создания.
2. Идемпотентность.
3. События обрабатываются в порядке `at`; `at` ограничивается диапазоном [`last_transition_at`, `now`].
   Дедупликация — `UNIQUE(session_id, type, at)`.

| Событие | Действие |
|---|---|
| `locked` / `unlocked` | `state` ⇄ `locked`, на оплату не влияет |
| `warning {minutesLeft}` | добавить в `warnings_sent` |
| `paused@t` / `resumed@t` | как §5.5 в момент `t` (офлайн-пауза не оплачивается) |
| `extended@t {minutes, cost}` | предоплата и сеанс открыт → `quote(t)`, `overdraft`. `data.cost` агента → `meta` (агент считал по базе, `SessionManager.cs:380-388`) |
| `ended@t {reason}` | §5.7 с `t_end=t`. **Обязательно** по контракту (`frag/paths-auth-sessions.yaml:1287-1288`), мок не делает |
| `charged`, `started` | только записать |
| любое для `ended`-сеанса | только записать |

Событие старше `last_transition_at` записывается с `applied=false`. Ответ: `204`.

### 5.13 Разрешённые несоответствия (сводка)

| # | Мок/контракт | Сервер v1 |
|---|---|---|
| 1 | Правила цены только в кассе; киоск берёт базу | Одна функция `quote` (§5.1) |
| 2 | Возврат по базовой цене | Пропорционально оплаченному (§5.7) |
| 3 | Продление пакетом стоит `packagePrice` за любые минуты; зона и окно не проверяются | `package_minutes` принудительно, полный §5.2 |
| 4 | Бонусы в трёх разных местах | Всё на основной баланс, `type=bonus` (§5.8) |
| 5 | Две модели лояльности (очки на киоске, траты в кассе) | Одна: настроенные уровни по `lifetime_spent`; `User.loyaltyLevel` = уровень − 1; `points = lifetime_spent/100`; perks генерируются из `discountPct` |
| 6 | Z-отчёт по времени и регулярке | `shift_id` и `method` в леджере |
| 7 | События `paused`/`resumed`/`extended`/`ended` не применяются | §5.12 |
| 8 | Реплей требует user-токен, отвечает 402 | §5.11 |
| 9 | Завершение ровно в 0, в том числе у офлайн-ПК | `ending` + grace, только онлайн (§5.10) |
| 10 | Смесь UTC и локального времени | `clubs.time_zone` везде |
| 11 | Идемпотентность не атомарна и не разделена по арендатору | §7.1 |
| 12 | Hard delete тарифа | Soft delete + `price_per_hour_snapshot` в сеансе |
| 13 | Промокод без лимита на клиента; PATCH затирает счётчики | `promo_redemptions` PK; серверные счётчики |
| 14 | Дедупликация автоматизации в памяти | `rule_firings` |
| 15 | `adminTopUp` без `transaction` | `{balance, transaction, bonus?}` |
| 16 | `cost` завершённого сеанса без учёта возврата | `charged_total − refunded_total` |
| 17 | Постоплата без лимита | D-10 |
| 18 | Статус ПК хранится | Выводится (§6.6) |
| 19 | `SessionState.cs:23` «пауза при блокировке» | Блокировка не останавливает оплату |
| 20 | `/sessions/current` не проверяет `pcId` | `403 pcMismatch` |

---

## 6. WebSocket hub (`/ws/agent`)

Порт `srv@1c68cc8:src/Club.Server/Realtime/AgentSocketHub.cs` с доработками по AsyncAPI §1–§10
(`cc/asyncapi/asyncapi.yaml:15-163, 335-395`).

### 6.1 Рукопожатие

- Путь `wss://host/ws/agent` (вне `/api/v1`). Подписи нет.
- Токен берётся из `Authorization: Bearer`; `?token=` принимается от старых агентов. Если присланы оба и они
  различаются → `4401`.
- Плохой токен отклоняется **до** upgrade: HTTP `401`/`403`. Агент тогда делает refresh с экспоненциальной
  задержкой (`RealtimeClient.cs:118-120`).
- Субпротокол `clubshell.v1` обязателен: сервер его выбирает. Если предложены только чужие → upgrade и close `4426`
  (`srv@1c68cc8 :71`).
- `426` и `503` на рукопожатии в v1 не используются (OQ L27).

### 6.2 Реестр соединений

- `ConcurrentDictionary<Guid pcId, Connection>`: одно соединение на ПК. Новое соединение вытесняет старое, старое
  закрывается `1000`.
- Подключение — это «онлайн» (§6.6).
- При подключении доставляются все неподтверждённые и не просроченные команды, от старых к новым.
- Close-коды:

  | Код | Когда |
  |---|---|
  | `4401` | Токен отозван, ПК удалён, наступил `exp` токена (таймер на соединение, `srv@1c68cc8 :187`) |
  | `4426` | Не предложен `clubshell.v1` |
  | `1000` | Соединение вытеснено новым |
  | `1001` | Остановка сервера |
  | `1009` | Кадр больше 1 MiB |

### 6.3 Кадры и keepalive

- `WsFrame {type, id, ts, name?, payload (ключ всегда есть), ack?, supersedes?, expiresAt?}`
  (`cs/src/ClubShell.Contracts/Commands/ServerCommand.cs:656`), только text-кадры ≤1 MiB.
- Сервер шлёт кадр `ping` каждые 20 с и ждёт `pong` с тем же `id` 10 с. Нет ответа → соединение разрывается.
  Новый ping не отправляется, пока предыдущий не отвечен.
- На `ping` агента сервер отвечает `pong`.
- Непрошеные управляющие Pong агента (каждые 30 с) допускаются. `KeepAliveInterval` 30 с оставляем.
- Неизвестные `type` игнорируются.
- Кадры `event` от агента — все 7 required receive-операций AsyncAPI: `anticheatViolation` (→ `anticheat_reports`),
  `hardwareChanged` (→ `pcs.hardware` + `telemetry_events`), `offlineQueueFlushed`, а также `sessionStarted`,
  `sessionEnded`, `gameLaunched`, `gameExited` (`x-agent-emits: false` — текущий агент их не шлёт) пишутся в
  `telemetry_events`. Биллинг и статус от них не зависят: источник истины — REST (`/sessions/*`,
  `/games/{id}/launch-report`) (OQ L48). Ответа на `event` нет.
- Входящие `ack` — §6.4; входящий `pong` закрывает ожидание ping; входящий `push`/`command` игнорируется.

### 6.4 Команды: очередь, доставка, ack

1. **Постановка** (`CommandDispatcher.EnqueueAsync`):
   - `INSERT agent_commands` с обязательным `expires_at` (`Agents:CommandTtlMin`) у всех команд, которые шлёт
     v1 (OQ L113: обязательно для power/update/launchGame);
   - если указан `supersedes` и прежняя команда ещё не доставлена → ей ставится `superseded_at`;
   - если ПК подключён, команда отправляется сразу и получает `delivered_at=now`.
2. **REST-fallback:** `HeartbeatResponse.pendingCommands` = число неподтверждённых, не просроченных и не вытесненных
   команд, **кроме** доставленных по текущему живому WS за последние 5 мин (N1: иначе REST-drain ждёт команду,
   уже выполняемую по WS, до 5 мин). Затем агент вызывает `GET /agents/{pcId}/commands` (от старых к новым) и
   `POST …/ack`.
3. **Ack по WS или REST:**
   - `acked_at` и `ack` перезаписываются при каждом повторе, это допустимо;
   - `message` получает второй REST-ack с `ackedAt`;
   - неизвестная или чужая команда → `404 what=command`;
   - ack завершает `TaskCompletionSource` в `CommandDispatcher`, на котором ждёт `adminCommand`.
4. **`adminCommand`:** постановка в очередь и ожидание ack до `AckWaitSec`. ПК офлайн →
   `{ack:{ok:false, error:{code:agentOffline}}}`, команда остаётся в очереди. Таймаут → `code timeout`. Касса обязана
   читать `ack.result` вместе с `ack.ok`.
5. **Имена** — все 19 команд AsyncAPI; отправляются только те, что `x-server-status: required`. У каждой
   required-команды есть типизированный построитель payload (DTO из Contracts) и тест формы кадра.

   | Команда | AsyncAPI | Кто ставит в v1 | Payload / что проверить в `ack.result` |
   |---|---|---|---|
   | `lock` | required | касса, автоматизация `lockPc` | `LockCommand{reason:staff, message?}` |
   | `unlock` | required | касса | без payload |
   | `message` | required | касса (`from:"Администратор"`, `requiresAck:true`), автоматизация (от имени клуба, `requiresAck:false`) | `MessageDeliveryResult`; второй REST-ack с `ackedAt` |
   | `reboot`, `shutdown` | required | касса (`delaySec:5, force:false`), автоматизация `shutdownPc` (`delaySec:30`) | `PowerCommand`; `ScheduledResult` |
   | `endSession` | required | сервер при завершении кассой или тиком (§5.7) | `EndSessionCommand{sessionId, reason}`; `SessionResult` |
   | `extendSession` | required | сервер при `adminExtend` (§5.6), всегда `charge:false` | `ExtendSessionCommand`; `SessionResult` |
   | `reloadPolicy` | required | сервер при росте `policy_version` (§5.9) | без payload; `{version}` |
   | `setPolicy` | required | триггера в v1 нет (редактора политики в контракте нет); построитель и тест есть | `Policy`; `{version, applied}` |
   | `refreshConfig` | required | сервер при росте `config_version`/`catalog_version`, тарифы (§5.9) | `RefreshConfigCommand` с явными флагами; `{refreshed[]}` |
   | `wake`, `launchGame`, `killGame`, `screenshot`, `remoteControlStart`, `remoteControlStop`, `update`, `showAds`, `setVolume` | notImplemented | не отправляются | — |

   `expires_at` (`Agents:CommandTtlMin`) ставится у всех отправляемых команд. Зависимые команды (`lock` →
   `unlock`) ставятся через `supersedes`, как требует AsyncAPI §7.

### 6.5 Pushes (не чаще одного раза, без очереди)

| Push | Кому | Когда |
|---|---|---|
| `sessionUpdated` (`Session`) | ПК сеанса | любой переход, resync 30 с |
| `walletUpdated` (`Balance`) | все ПК с валидным токеном пользователя | после commit денег |
| `userRevoked {userId, reason}` | ПК пользователя | logout, бан/blacklist, сброс пароля кассиром (OQ-24) |

Это все три push с `x-server-status: required`. `notification`, `pcStatusChanged`, `chatMessage`, `orderUpdated`,
`bookingUpdated`, `tournamentUpdated` в AsyncAPI — `notImplemented`: сервер v1 их **не** шлёт (мок шлёт
`pcStatusChanged`, агент его всё равно игнорирует; сообщения автоматизации идут командой `message`).

### 6.6 Статус ПК (выводится)

```
maintenance                                        → maintenance
!(hub.IsConnected(pc) || now − last_heartbeat_at < OfflineAfterSec)  → offline
open session state=locked                          → locked
open session                                       → busy
иначе                                              → free
```

`PcStatusWorker` раз в 5 с сравнивает выведенные статусы с прошлым снимком в памяти. При переходе в `offline` он
пишет `telemetry_events kind='pcOffline'` (счёт обрывов для здоровья, §4.2) и ставит вебхук `pcOffline`; при
переходе в `free` запускает таймер автоматизации `pcIdleMinutes`. Push `pcStatusChanged` не шлётся (§6.5). Мок
статус offline не ставит никогда (`mock/db.ts:839`).

### 6.7 4401 по истечении токена

При приёме соединения запоминается `exp` токена, и ставится таймер на закрытие `4401` в момент `exp`. Агент сам
обновляет токен за 2 мин до `exp` и переподключается (`ServerConnection.cs:282-311`). Отзыв (`cv`++, удаление ПК)
закрывает соединение немедленно.

### 6.8 Масштабирование

v1 — **один инстанс**: реестр соединений и ожидание ack живут в памяти. Команды при этом лежат в БД, поэтому
рестарт ничего не теряет: они доставляются при переподключении или через REST.

Для нескольких инстансов потребуется:

- маршрутизация команд и push через `LISTEN/NOTIFY` (канал `pc_<id>`) к инстансу, держащему сокет;
- sticky-сессии для WS.

Воркеры уже безопасны: advisory lock и `SKIP LOCKED`. Защита от случайного второго инстанса: при старте берётся
`pg_try_advisory_lock('CSHub')`. Если он занят — лог `Critical` и выход.

---

## 7. Идемпотентность, конверт ошибок, время

### 7.1 Idempotency-Key

- **Где:**
  - агент шлёт ключ всегда на `POST /sessions`, `/sessions/{id}/extend`, `/sessions/{id}/events`;
  - касса шлёт на 7 денежных маршрутах (`apps/admin/src/api.ts:142-157`);
  - остальные маршруты из `IdempotencyKeyOptional` тоже поддерживаются.
- **Таблица:** PK (`principal`, `method`, `path` без query, `key`).
  - `principal` = `pc:<pcId>` для агента, `club:<clubId>` для персонала — без этого можно воспроизвести чужой ответ
    (у мока ключ только METHOD+URL, `mock/db.ts:492`);
  - версия UUID не проверяется: ключ событий детерминирован (`OfflineSessionStore.cs:906-912`).
- **Поток** — внутри бизнес-транзакции:

```
INSERT INTO idempotency_keys(...) VALUES (...) ON CONFLICT DO NOTHING;   -- конкурентный близнец ждёт на индексе до commit
0 строк  → SELECT status_code, response → ответ + Idempotent-Replayed: true
1 строка → обработчик в той же транзакции → UPDATE status_code, response → COMMIT
ошибка/4xx → ROLLBACK → ключа нет (ошибки не хранятся; повтор исполняется заново; покрывает 404 ранних событий)
```

- `request_hash` сохраняется. Несовпадение только логируется: реплей `POST /sessions` законно приходит с
  дополненным телом.
- Длинные транзакции ограничены `SET LOCAL lock_timeout = '10s'` — это меньше 15-секундного тайм-аута агента.
- Чистка: `MaintenanceWorker` раз в 10 мин удаляет строки старше `TtlHours`.

### 7.2 Конверт ошибок и отображение кодов

Тело всегда `{"error":{"code","message","details" (ключ присутствует, null если пусто),"traceId"}}`. `ApiException`
бросается из любого слоя, `ApiErrorMiddleware` его сериализует. Необработанное исключение → лог с trace-id и
`500 internal`.

| ErrorCode | HTTP | Типичные `details` |
|---|---|---|
| `validation` | 400 | `field`, `reason` (`required\|min\|max\|enum\|format\|taken\|tooOld\|unsupported\|digits4to8\|self`) |
| `unauthorized` | 401 | `reason` (`expired\|invalid\|revoked\|reused\|clockSkew\|signature\|clubKey\|userToken\|badCredentials\|invalidPin`), `problem` |
| `insufficientFunds` | 402 | `required`, `available` (Money; в admin — как в схеме) |
| `forbidden` | 403 | `reason` (`pcMismatch\|notOwner\|pendingApproval\|clubDisabled\|banned\|guest\|guestDisabled\|ownerOnly\|zoneNotAllowed\|ageRestricted`) |
| `policyDenied` | 403 | `rule` |
| `notFound` | 404 | `what` / `route` |
| `conflict` | 409 | `reason` (`activeSessionElsewhere\|shiftOpen\|noShift\|pcBusy\|postpaidSession\|alreadyPaused\|notPaused\|replay\|superseded`) |
| `sessionNotActive` / `sessionAlreadyActive` | 409 | `session` / `{sessionId, pcId}` |
| `rateLimited` | 429 | `retryAfterSec` + заголовок `Retry-After` |
| `internal` | 500 | — |
| `notImplemented` | 501 | `{reason:"notImplemented"}` |
| `serverUnavailable` | 503 | только при недоступной БД (`Retry-After: 5`) |
| `versionMismatch` | 426 | не используется в v1 |

Коды, которые формирует только агент, сервер не отдаёт: `gameNotInstalled`, `gameLaunchFailed`,
`accountPoolExhausted`, `antiCheatBlocked`, `agentOffline` (последний бывает только внутри `ack`), `timeout`,
`protocolError`. Неизвестный маршрут `/api/v1/*` → `404 notFound details.route`. Некорректный JSON →
`400 validation`.

### 7.3 Трассировка и время

- `X-Trace-Id`: валидный GUID из запроса возвращается **как есть**, в виде сырой строки. club-server его
  переформатирует (`srv/ApiErrors.cs:55`) — здесь так не делаем. Если заголовка нет, генерируется новый. Тот же id
  попадает в `error.traceId` и в scope логов.
- `X-Server-Time` (`.fffZ`) ставится на каждом ответе, включая 304 и 501. Поля `serverTime` в
  register/heartbeat/tariffs берутся из того же `TimeProvider`.
- **Авторитет времени — сервер.** Агент корректирует смещение по heartbeat и по `clockSkew`. Все бизнес-расчёты
  используют серверное `now`; время агента учитывается только для офлайн-событий, в пределах [`last_transition_at`,
  `now`].
- Календарная логика (окна тарифов, weekdayPct, праздники, happy hours, комендантский час, дни отчётов) считается в
  `clubs.time_zone` через `ClubTime`. На проводе всегда UTC.
- **ETag** (RFC 9110, в кавычках):
  - `policies`: `"p<policy_version>"`;
  - `config`: `"c<config_version>"`, агент `If-None-Match` не шлёт, всегда 200;
  - `games`: `"g<catalog_version>-<hash(zone, lastPlayed пользователя)>"`;
  - `tariffs`: хеш `items`, но `/tariffs` **всегда отвечает 200** и игнорирует `If-None-Match` (OQ L87).

### 7.4 Пагинация

`Paging.Normalize`:

- `page` < 1 или не число → 1;
- `pageSize` = 0 или не число → 50;
- `pageSize` < 0 → 1;
- `pageSize` больше максимума → максимум (200; для `/games` — 1000).

Ответ `{items, total, page, pageSize}` с фактически применёнными значениями.

---

## 8. Фоновые задачи

Шаблон club-server (`srv/StorageWorker.cs:16-38`):

- `BackgroundService` держит выделенное соединение с `pg_advisory_lock(key)` на всё время жизни, так что второй
  инстанс ждёт;
- цикл: try/catch + `LogWarning` + `Task.Delay(interval, TimeProvider)`;
- у каждого воркера есть `public static Task RunOnceAsync(…)` для тестов;
- регистрация только при `Workers:Enabled`.

| Воркер | Ключ lock | Период | Что делает |
|---|---|---|---|
| `SessionTickWorker` | `CSSess` | 1 с | `ending`/`timeUp` + grace (§5.10), остановка постоплаты по средствам, принудительное завершение офлайн-сеансов после 240 мин тишины, resync push каждые 30 с |
| `PcStatusWorker` | `CSPcSt` | 5 с | Смена выведенного статуса → `telemetry_events kind=pcOffline` и вебхук `pcOffline`, автоматизация `pcIdleMinutes` (push нет, §6.5) |
| `HealthWorker` | `CSHlth` | 30 с | Часовые корзины из `pc_metrics`; issues hot/trend/fpsDrop/unstable; тикеты (один на пару ПК+вид, эскалация, без повторного открытия 6 ч), `autoMaintenance`, вебхук `hardware`. **Без симуляции** (`mock/health.ts:386-452`) |
| `ClubTickWorker` | `CSClub` | 60 с | Хеш набора живых баннеров → `config_version++` при изменении (OQ-21); автоматизация `minutesLeft` |
| `WebhookWorker` | `CSHook` | 1 с | `webhook_outbox`: POST с таймаутом 5 с, 3 повтора с задержкой; перед отправкой отклоняет адреса loopback, private, link-local и metadata (OQ-11); обновляет `last_status`/`last_at` |
| `MaintenanceWorker` | `CSMant` | 10 мин | Удаление ключей идемпотентности старше 24 ч, `pc_metrics` старше 8 д, `telemetry_events` старше 30 д, просроченных токенов (refresh, user, staff, qr); сверка кеша баланса с леджером (раз в сутки, `LogError` при расхождении) |

- **Сверка офлайн-событий** фонового воркера не требует: она синхронна в `POST /sessions/{id}/events` и
  `POST /sessions` (реплей). Хвосты закрывает `SessionTickWorker`.
- **Статусы заказов:** в v1 не нужны — заказы notImplemented. Воркер появится вместе с `createOrder`.
- Критические секции короче жизни воркера используют `pg_advisory_xact_lock`, например bootstrap клуба при первом
  старте.

---

## 9. Идентичность ПК и club-server

**Факты:**

- HWID считаются по-разному.
  - Хелпер: `sha256(lower(SMBIOS UUID)|board serial)` (`srv/Club.Helper/WindowsPlatform.cs:111`).
  - Агент: `sha256` от uuid, board, cpu, серийного номера первого диска и MAC, с fallback-GUID на VM
    (`cs/src/ClubShell.Core/Security/Hwid.cs:118-136, 186-207`).
  - HWID агента меняется при замене диска или сетевой карты.
- Номер места в club-server определяет IP (DHCP) и hostname (`KeaHostSync.cs:48-68`).
- Ключи и аудитории JWT разные: `X-Club-Key` агента ≠ ключ diskless; `club-agent` ≠ `club-diskless`.

**Решение (вариант E скаута):**

- Два независимых реестра. Слабая связь — по нормализованному MAC.
- club-server авторитетен для физического места (номер, имя). Центральный сервер авторитетен для всего
  коммерческого и для мест без ПК (консоли, VR).
- Зависимость односторонняя: центральный сервер читает club-server, club-server ничего не знает о шелле.

| | v1 | Фаза 2 (после решения владельца) |
|---|---|---|
| Хранение | `pcs.mac_address` из `AgentRegisterRequest.macAddress` (lower-case, двоеточия) | То же |
| Номер/имя | Вводятся в кассе; README: «ставьте тот же номер, что в панели diskless» | `DisklessSyncWorker` (lock `CSDisk`, 60 с) читает `Diskless:BaseUrl` (нужен стабильный read-only эндпоинт и токен в club-server). Для связанных ПК номер и имя зеркалируются, `PATCH number` → `409 conflict` |
| Одобрение | Отдельное в каждом домене доверия | Опционально: автоодобрение агента, чей MAC совпадает с одобренной машиной |
| Замена диска | Неизвестный HWID с MAC существующего ПК → новый ПК в `pendingApproval` с предзаполненным местом старого. Перепривязки только по MAC нет: MAC подделывается | То же |
| Зоны | Не синхронизируются (тарифные зоны ≠ сетевые) | То же |

Общий вход персонала для club-server и центрального сервера — не в v1. Дизайн токенов персонала не мешает позже
выпускать RS256 JWT `role=owner` для club-server. PIN кассира туда не принимается никогда.

---

## 10. Тестирование и приёмка

### 10.0 Инфраструктура

- `ServerFixture : WebApplicationFactory<Program>, IAsyncLifetime`:
  - создаёт `CREATE DATABASE clubshell_test_<guid>` и удаляет её через `DROP … WITH (FORCE)` до 20 попыток
    (`srv/TestDatabases.cs:13-31`);
  - отдельный временный путь для ключа JWT и pepper;
  - `Workers:Enabled=false`, `Seed:Dev=true`, `Club:AutoApprovePcs=true`;
  - словарь `Settings` для переопределений.
- Строка подключения администратора — из `CLUBSHELL_TEST_PG`, по умолчанию `Host=localhost;Username=postgres`.
  У club-server она зашита как unix-socket (`srv/ServerFixture.cs:11`).
- `FakeClock : TimeProvider` в DI — чтобы проверять тики и сроки без ожидания.
- `TestAgent`: подписывает запросы так же, как `RequestSigningHandler` (порт `srv@1c68cc8 ServerFixture.cs:60, 99`).
- Локально на машине разработки установлены .NET SDK 10.0.401 и PostgreSQL 18.6 (порт 5433, `CLUBSHELL_TEST_PG`),
  поэтому каждый срез собирается и тестируется локально до пуша. CI (job `server`, заводится в S0) повторяет это
  на ubuntu с `postgres:18`. На другой машине подойдёт любой PostgreSQL 18, например `docker compose up postgres`,
  и `CLUBSHELL_TEST_PG`.

### 10.a Соответствие контракту

- Хелпер `Contract`: весь `server/contracts/openapi.json` загружается в `SchemaRegistry`, Draft 2020-12,
  неизвестные ключевые слова разрешены, `RequireFormatValidation` (`srv/TestHelpers.cs:59-108`).
- `Contract.AssertResponse(operationId, status, body)` находит схему ответа по `operationId` и статусу. Проверяются
  и ошибки (`ServerError` + `code` + `reason`).
- Все HTTP-ответы в тестах проходят через `ContractValidatingHandler` фикстуры, поэтому **каждый** ответ
  валидируется автоматически, а не по выбору автора теста.
- Покрытие: фикстура собирает множество `operationId`, по которым пришёл валидный ответ. Тест `Coverage` для каждого
  среза проверяет, что все его операции (таблица §11) покрыты.
- Отдельные тесты:
  - «каждая notImplemented-операция → 501 с конвертом, никогда не 404»;
  - «каждый `/admin/*` 2xx имеет JSON-тело»;
  - «admin `T|null` сериализуется как null».
- Тест-векторы подписи, CORS preflight, 401-причины на каждый режим аутентификации.
- `MigrationTests`: down до 0, up, down, up (`srv/MigrationTests.cs:10-20`).
- `LedgerInvariantTests`: после каждого сценария `SUM(ledger) = wallets`.

### 10.b Реальный агентский `ServerClient`

`AgentHarness` (`server/tests/ClubShell.Server.Tests/AgentHarness.cs`) использует `ClubShell.Core` как
`ProjectReference` и собирает DI:

- `ConfigureServerHttpClient(...)` (`cs/src/ClubShell.Core/Http/RetryPolicy.cs:137`); primary handler именованного
  клиента `RetryPolicy.HttpClientName` заменяется на `factory.Server.CreateHandler()`. Так реальный конвейер Polly и
  `RequestSigningHandler` работают поверх in-process сервера;
- `ITokenStore` — реализация в памяти;
- `Hwid` над фейковым `IHardwareIdSource`;
- `AgentSettings.Server.BaseUrl = http://localhost/api/v1`.

Сценарии:

| Срез | Сценарий через `ServerClient` |
|---|---|
| S1 | register → pendingApproval → одобрение → register → heartbeat → config → policies (304 на второй) → commands/ack; истечение access → реактивный refresh; `clockSkew` → коррекция и повтор; `RealtimeClient` против `factory.UseKestrel()` (есть в .NET 10): рукопожатие, ping/pong, команда `lock` → ack, вытеснение 1000, 4401 на `exp` |
| S2 | login (пароль, карта, гость) → balance → tariffs → createSession → current (`secondsLeft`) → pause/resume → extend → end; offline replay (`startedAt`, `clientSessionId`) + events batch с тем же ключом; неверный пароль считается один раз |
| S3 | games (страницы по 500, 304), getGame, launch-report (повтор → без дубля), getPc, manifest → 204 |
| S4 | касса открывает сеанс → агент видит `sessionUpdated`; команда `message` → ack и второй ack с `ackedAt` |

Для гонок используются параллельные запросы: двойной `createSession` → ровно один 201 и один 409; двойной ключ
идемпотентности → один эффект.

### 10.c Playwright-e2e кассы против реального сервера

- В `tests/shell-e2e/playwright.config.ts` добавляется переключатель `ADMIN_SERVER=real`. Вместо
  `pnpm --filter @clubshell/mock-server …` команда `webServer` становится
  `dotnet run --project server/src/ClubShell.Server --urls http://localhost:8091`, с окружением
  `ASPNETCORE_ENVIRONMENT=Development`, `ConnectionStrings__Club=<временная БД>`, `Seed__Dev=true`,
  `Cors__AllowedOrigins__0=http://localhost:1431`. Health URL тот же (`/health`).
- `admin/console.spec.ts` (379 строк) должен проходить без правок, кроме тех мест, где он проверяет поведение мока,
  признанное неправильным (возврат по базе и т.п.). Каждая такая правка перечисляется в PR.
- CI job `e2e-admin-real` на ubuntu + `postgres:18` запускается в S4 (частичный набор: map/shift) и в полном объёме
  в S5.

### 10.d Паритет с MockServer

- Мок остаётся dev- и e2e-сервером для киоска.
- В `server/README.md` ведётся таблица «Сознательные отличия от мока»: §5.13, пункты E1–E15 agent-скаута,
  пункты 1–15 admin-скаута.
- Ошибки мока, которые мешают агенту или кассе увидеть реальное поведение, исправляются в моке отдельными задачами:
  `offline` статус, `ended`-событие, `expiresAt` у power-команд, флаги фич в config.
- Правило: новое поведение, видимое на проводе, сначала попадает в контракт, потом в сервер, затем мок
  догоняет контракт.

---

## 11. Срезы реализации

Общие критерии выхода любого среза:

- CI-job `server` зелёный: build с `/warnaserror` и тесты;
- все ответы среза проходят валидацию схем;
- тест `Coverage` среза зелёный;
- `NotImplementedTests` зелёный: каждая ещё не реализованная операция контракта отвечает 501 с конвертом (набор
  реализованных `operationId` берётся из того же списка, что и таблица 501);
- чек-лист ревью (не автотест): `server/README.md` обновлён, отличия от мока внесены в его таблицу.

### S0 — скелет, миграции, аутентификация, health, 501 catch-all

- **Операции:** нет бизнес-операций. `/health`, 501 для всех 102 операций, fallback 404.
- **Файлы:**
  - `server/Directory.Build.props`, `server/ClubShell.Server.sln`, `server/contracts/*`,
    `server/scripts/sync-contracts.ps1`;
  - записи в `cs/Directory.Packages.props`;
  - `ClubShell.Server.csproj`, `Program.cs`, `appsettings*.json`, `Infrastructure/*`;
  - `Db/Migrations/M0001_Core.cs` (networks, clubs, pcs, agent_refresh_tokens, agent_commands, pc_metrics,
    telemetry_events, anticheat_reports, staff, staff_tokens, idempotency_keys);
  - `Auth/TokenService.cs`, `Auth/RequestSignature.cs`, `Auth/ReplayLog.cs`, `Auth/AgentAuthMiddleware.cs`
    (все режимы, кроме user и staff — у них заглушки 401);
  - bootstrap клуба;
  - `Idempotency/Idempotency.cs`;
  - `.github/workflows/server.yml` (ubuntu, `postgres:18` service, setup-dotnet из `global.json`, build/test; шаг
    contracts-drift: `server/contracts/REF` против checkout `deepunites/club-contracts@REF`).
- **Тесты:**
  - `ErrorEnvelopeTests` (trace echo, X-Server-Time, `details:null`);
  - `SigningTests` (векторы `base.yaml:115-121`, окно, `missing`/`mismatch`, повтор не отклоняется);
  - `NotImplementedTests`;
  - `MigrationTests`;
  - `IdempotencyTests` (конкурентный близнец ждёт; ошибка не сохраняется);
  - `PagingTests`.
- **Выход (тесты):** `MigrationTests` (down → up → down → up на пустой БД); `NotImplementedTests` — 102 из 102
  операций → 501 с `code=notImplemented`, ни одного 404; `GET /health` → 200 `{status:"ok"}`; `SigningTests`
  проходят векторы `base.yaml:115-121`; `IdempotencyTests` зелёные; job `server` зелёный в CI.

### S1 — агенты + WS hub

- **Операции:** `register`, `refresh`, `heartbeat`, `sendTelemetry`, `getConfig`, `getPolicies`, `getCommands`,
  `ackCommand`, `/ws/agent`.
- **Файлы:** `Agents/*`, `Realtime/*`, `Auth/TokenService` (refresh, cv), `Admin/Club/ClubSettings.cs` (чтение jsonb
  для конфига), seed политики из `config/policies.example.json`.
- **Тесты:**
  - `AgentApiTests` (pendingApproval + `details.pcId`, повторная регистрация = тот же pcId, refresh reuse → 401
    reused и отзыв, hwid mismatch → revoked, удалённый ПК → 401 revoked, pcMismatch, telemetry 120/1024, callAdmin
    events → `telemetry_events`);
  - `ConfigTests` (флаги false явно, `version` = `configVersion`);
  - `PolicyETagTests`;
  - `CommandQueueTests` (expiry → не отдаётся, supersedes, двойной ack, `pendingCommands` без живых WS-команд);
  - `CommandCatalogTests` (каждая из 10 required-команд строится и валидна по схеме AsyncAPI-сообщения; 9
    notImplemented-команд `CommandDispatcher` отказывается ставить);
  - `WsEventTests` (все 7 событий принимаются и пишутся, неизвестный `type` игнорируется, соединение живо);
  - `SocketHubTests` (Kestrel: subprotocol/4426, заголовок против query → 4401, вытеснение 1000, ping/pong timeout,
    1009, redelivery при подключении, 4401 на `exp`);
  - `AgentHarness` S1 (§10b).
- **Выход (тесты):** `AgentHarness` S1 — реальный `ServerClient`/`RealtimeClient` проходят сценарий §10b S1;
  команда `lock`, поставленная при живом WS, получает ack по WS, а поставленная при отключённом WS — через
  `pendingCommands` → `GET /commands` → REST-ack; `Coverage` = 8 операций S1; `CommandCatalogTests`,
  `WsEventTests`, `SocketHubTests` зелёные.

### S2 — вход игрока, пользователи, сеансы, биллинг, кошелёк

- **Операции:** `login`, `startQrLogin`, `getQrLoginStatus`, `guestLogin`, `logout`, `getUser`, `updateUser`,
  `getUserStats`, `getUserAchievements`, `getUserLoyalty`, `getCurrentSession`, `createSession`, `pauseSession`,
  `resumeSession`, `endSession`, `extendSession`, `postSessionEvents`, `getTariffs`; сверх контракта
  `GET /wallet/{userId}/balance` и `/users/{userId}/game-settings` (GET list, DELETE).
- **Файлы:** `M0002_Players.cs`, `Auth/PlayerAuthEndpoints.cs`, `Auth/UserTokens.cs`, `Auth/Passwords.cs`,
  `Users/*`, `Sessions/**`, `Wallet/*`, `SessionTickWorker`.
- **Тесты:**
  - `PricingTests` (кейс e2e 1 008 000; half-up; пакет; weekday/holiday в зоне клуба);
  - `PurchaseRulesTests` (таблица §5.2);
  - `SessionLifecycleTests` (`secondsLeft` после pause, lock не останавливает оплату, extend пакетом);
  - `SettlementTests` (возврат пропорционально оплаченному, постоплата ceil, 409 + `details.session` на повторный
    end);
  - `OfflineReplayTests` (agent-only, `tooOld`, overdraft, `clientSessionId`, ранние события → 404 без ключа, затем
    успех);
  - `EventsTests` (paused/resumed/extended/ended в прошлом, дедупликация);
  - `TickTests` (FakeClock: ending → grace → timeUp; офлайн-ПК не трогается до 240 мин; постоплата по средствам);
  - `AuthTests` (badCredentials один раз на trace, `UNIQUE(pc_id)` вытесняет, `offlineHash` проверяется алгоритмом
    агента, userRevoked push);
  - `LedgerInvariantTests`;
  - `AgentHarness` S2.
- **Выход (тесты):** `AgentHarness` S2 проходит сценарий §10b S2, включая офлайн-сеанс; после каждого теста
  среза `LedgerInvariantTests` (`SUM(ledger) = wallets`) зелёный; `Coverage` = 18 операций S2 + `getBalance`.

### S3 — игры, обновления, ПК

- **Операции:** `getGames`, `getGame`, `sendLaunchReport`, `getUpdateManifest`, `getPc`; сверх контракта
  `POST /anticheat/report` (204, хранить).
- **Файлы:** `M0003_Catalog.cs`, `Games/*` (+ `CatalogSeed` из `Catalog:GamesSeedPath`), `Updates/UpdateEndpoints.cs`,
  `Agents/PcEndpoints` (`getPc`).
- **Тесты:**
  - `GamesTests` (страницы до 1000, скрытые исключены, порядок `catalog.order`, ETag учитывает `lastPlayedAt`,
    `catalogVersion` = heartbeat, нет `settingsPaths`);
  - `LaunchReportTests` (дедупликация, `lastPlayedAt`);
  - `ManifestTests` (204; 404 channel/component; 400 current);
  - `PcTests` (hwid только своего ПК);
  - `AgentHarness` S3.
- **Выход (тесты):** `AgentHarness` S3: `GameLibrary` реального агента собирает каталог из 2+ страниц и на
  повторном старте получает 304; `ServerClient.GetPcAsync` возвращает `Pc` с `hwid` своего ПК; manifest → 204;
  `Coverage` = 5 операций S3 + `reportAntiCheat`.

### S4 — касса, часть 1

- **Операции (17):** `adminLogin`, `adminLogout`, `adminMe`, `adminOverview`, `adminOpenSession`, `adminExtend`,
  `adminEnd`, `adminTopUp`, `adminCommand`, `adminShift`, `adminOpenShift`, `adminCloseShift`, `adminQuote`,
  `adminPcs`, `adminAddPc`, `adminUpdatePc`, `adminDeletePc`.
- **Файлы:** `M0004_Counter.cs`, `Auth/StaffTokens.cs` + `StaffAuthMiddleware`, `Infrastructure/Cors.cs`,
  `Admin/AdminJson.cs`, `Admin/Staff/Login*`, `Admin/Counter/*`, `Admin/Shifts/*`, `Admin/Pricing/Quote*`,
  `Admin/Pcs/*`, `Admin/Control/Audit.cs`, `PcStatusWorker`.
- **Тесты:**
  - `StaffAuthTests` (invalidPin, 429 после 5, logout всегда 200, деактивация убивает токены, `ck_` = owner,
    ownerOnly);
  - `CorsTests`;
  - `CounterTests` (порядок проверок §5.2, `{session, charged, balance}`, `transaction` в topup, бонус порога
    half-up 100);
  - `ShiftTests` (409 shiftOpen/noShift, Z по `shift_id`, expectedCash только cash, shortfall-флаг);
  - `PcAdminTests` (одобрение через `maintenance:false`, DELETE при открытом сеансе → 409 pcBusy, удалённый →
    агенту 401);
  - `AdminCommandTests` (онлайн ack, офлайн `agentOffline` + очередь, timeout, `expiresAt`; `adminEnd` ставит
    `endSession`, `adminExtend` — `extendSession charge:false`; `pcStatusChanged` не отправляется);
  - e2e `console.spec.ts` части «вход/карта/смена» против реального сервера.
- **Выход (тесты):** `console.spec.ts`, части «вход/карта/смена», зелёный против реального сервера (job
  `e2e-admin-real`); в `AgentHarness` S4 подключённый агент после `adminOpenSession`/`adminExtend`/`adminEnd`
  получает `sessionUpdated` и команды `extendSession`/`endSession` и сходится с `GET /sessions/current`;
  `Coverage` = 17 операций S4.

### S5 — касса, часть 2

- **Операции (27):** `adminStaff`, `adminAddStaff`, `adminUpdateStaff`, `adminClients`, `adminAddClient`,
  `adminUpdateClient`, `adminBindClientCard`, `adminSetClientPassword`, `adminClientTransactions`,
  `adminRedeemPromo`, `adminSettings`, `adminSaveSettings`, `adminApiKey`, `adminRotateApiKey`, `adminTariffs`,
  `adminAddTariff`, `adminSaveTariff`, `adminDeleteTariff`, `adminProducts`, `adminUpdateProduct`,
  `adminReceiveProduct`, `adminGames`, `adminHealth`, `adminUpdateTicket`, `adminSaveHealthSettings`,
  `adminControl`, `adminReports`.
- **Сверх контракта:** `PATCH /admin/games/{id}` (settingsPaths); `GET /admin/network` и
  `POST /admin/network/clubs` → 501; остальные `/users/{userId}/game-settings*` → 501.
- **Файлы:** `M0005_Admin.cs`, `Admin/{Staff,Clients,Club,Pricing,Stock,Catalog,Health,Control,Reports,Automation,Webhooks}/*`,
  `HealthWorker`, `ClubTickWorker`, `WebhookWorker`, `MaintenanceWorker`.
- **Тесты:**
  - `SettingsTests` (JSON Schema → 400; clamp для control/health; счётчики не затираются; `config_version`/
    `catalog_version` bump → `refreshConfig` с явными флагами подключённым ПК);
  - `ClientsTests` (уникальность username/card без учёта регистра; blacklisted только owner; сброс пароля отзывает
    токены);
  - `PromoTests` (атомарный decrement, один раз на клиента, expired/exhausted);
  - `TariffTests` (soft delete, ETag `/tariffs`);
  - `StockTests` (lowStock-вебхук при пересечении `lowAt`);
  - `HealthTests` (FakeClock + засеянные метрики → тикеты, эскалация, автоматическое обслуживание и возврат);
  - `ControlTests` (7 флагов);
  - `ReportsTests` (местные дни для `byDay` и `heat`, выручка = charges − refunds, `days` 0 → 1 и > 90 → 90,
    `topGames` — различные игроки по `launch_reports` за период, `topProducts = []` в v1: заказов нет);
  - `AutomationTests` (`rule_firings` переживает рестарт);
  - `WebhookTests` (SSRF-отказ, повторы);
  - e2e `console.spec.ts` целиком против реального сервера.
- **Выход (тесты):** весь `console.spec.ts` зелёный на реальном сервере (правки спеки — только перечисленные в PR,
  §10c); `Coverage` = 75/75 required + `getBalance`, `reportAntiCheat`, `PATCH /admin/games/{id}`;
  `NotImplementedTests` — ровно 25 операций контракта → 501.

### S6 — CI, развёртывание, документация

- **Файлы:**
  - `server/Dockerfile` (multi-stage `mcr.microsoft.com/dotnet/sdk:10.0` → `aspnet:10.0`, non-root, `/app/data`
    volume);
  - `server/compose.yaml` (`postgres:18` + сервер + volume `data`, healthcheck);
  - `.github/workflows/server.yml` (добавить build образа, job `e2e-admin-real`);
  - `server/README.md` (запуск, конфиг, секреты, reverse-proxy: TLS, `ForwardedHeaders`, путь не переписывать,
    WS upgrade; ротация JWT-ключа, enrollment-ключа, `ck_`; резервное копирование `data/` — без pepper все PIN
    недействительны; отличия от мока);
  - `docs/SERVER_API.md` ссылается на этот DESIGN;
  - `server/railway.toml` (Q-7: прод на Railway, регион EU West, Амстердам) — см. «Railway» ниже.
- **Railway** (проверено по докам Railway 2026-09; §12.1 D-23):
  - `railway.toml`:
    - `[build] builder = "DOCKERFILE"`, `dockerfilePath = "server/Dockerfile"`,
      `watchPatterns = ["server/**", "src/ClubShell.Contracts/**", "Directory.*.props", "global.json"]`;
    - `[deploy] healthcheckPath = "/health"`, `healthcheckTimeout = 120`, `restartPolicyType = "ON_FAILURE"`.
  - Настройки сервиса:
    - Root Directory — корень репозитория (Dockerfile берёт Contracts), config path `/server/railway.toml`;
    - 1 реплика;
    - автодеплой из `main` с «Wait for CI»;
    - volume на `/app/data`: ключ JWT, pepper PIN, seed-JSON.
  - Volume закрепляет D-20. Railway не запускает два деплоя сервиса с volume одновременно и не даёт ему реплик.
    Поэтому старый инстанс останавливается до старта нового, и `CSHub` не упирается в предыдущий. Цена — короткий
    простой на каждом деплое. Агенты переподключаются сами, команды лежат в БД и доставляются повторно (§6).
  - PostgreSQL — сервис Railway в том же проекте. Строку подключения собирают ссылками, без парсинга URL в коде:
    `ConnectionStrings__Club=Host=${{Postgres.PGHOST}};Port=${{Postgres.PGPORT}};Username=${{Postgres.PGUSER}};Password=${{Postgres.PGPASSWORD}};Database=${{Postgres.PGDATABASE}}`
    (приватная сеть Railway).
  - Переменные: `Proxy__ClientIpHeader=X-Real-IP`, `Club__EnrollmentKey`, `Club__OwnerPin`,
    `Cors__AllowedOrigins__0=<origin кассы>`, `ASPNETCORE_ENVIRONMENT=Production`.
  - WS: Railway не ограничивает длительность и простой WebSocket. HTTP-запрос живёт до 15 мин, тело загружается
    до 5 мин, лимит — 10 000 одновременных соединений.
  - Бэкапы: автоматические бэкапы volume и у Postgres, и у `/app/data` (тариф Pro). Без pepper все PIN
    недействительны, без ключа JWT агенты один раз делают refresh (§3.2). README описывает восстановление.
  - Обновления агента (когда `getUpdateManifest` перестанет отвечать 204): MSI лежат в GitHub Releases или S3,
    сервер отдаёт только манифест. Отдавать файлы через Railway нельзя из-за лимита HTTP в 15 мин и платного
    egress.
  - TLS: сертификат Railway на `*.up.railway.app` и на домен клуба (CNAME). В README — чек-лист первого деплоя:
    проект, Postgres, сервис, volume, переменные, домен, `ServerUrl` в MSI агента.
- **Тесты:** smoke в CI — `docker compose up`, `/health`, register тестовым агентом. Отдельный тест: при заданном
  `PORT` Kestrel слушает этот порт, `Proxy:ClientIpHeader` меняет `RemoteIpAddress`, а без настройки заголовок
  игнорируется.
- **Выход (тесты):** smoke-job в CI: `docker compose up -d` → `/health` 200 за ≤60 с → `TestAgent` (§10.0)
  против контейнера, с `Club__EnrollmentKey` и `Club__AutoApprovePcs=true` в окружении smoke, регистрируется и получает 200 на heartbeat → `docker compose
  down -v`. Сам деплой на Railway не входит в критерии выхода CI: первый деплой проходит по чек-листу README
  вместе с владельцем (нужен его аккаунт Railway и домен).

### 11.1 Все 75 required-операций + WS → срезы

| # | operationId | Метод и путь | Срез |
|---|---|---|---|
| 1 | register | POST /agents/register | S1 |
| 2 | refresh | POST /agents/refresh | S1 |
| 3 | heartbeat | POST /agents/{pcId}/heartbeat | S1 |
| 4 | sendTelemetry | POST /agents/{pcId}/telemetry | S1 |
| 5 | getConfig | GET /agents/{pcId}/config | S1 |
| 6 | getPolicies | GET /agents/{pcId}/policies | S1 |
| 7 | getCommands | GET /agents/{pcId}/commands | S1 |
| 8 | ackCommand | POST /agents/{pcId}/commands/{commandId}/ack | S1 |
| — | (AsyncAPI) | WS /ws/agent: все required-операции — `sendPing`/`receiveAgentPing`, 10 команд (§6.4), 7 событий (§6.3), push `walletUpdated`/`sessionUpdated`/`userRevoked` (§6.5) | S1: рукопожатие, ping/pong, очередь и ack, все 10 построителей команд, 7 событий; S2: push сеансов/кошелька/`userRevoked`, `endSession`/`extendSession`; S4: триггеры кассы; S5: `refreshConfig`/`reloadPolicy` по bump версий |
| 9 | login | POST /auth/login | S2 |
| 10 | startQrLogin | POST /auth/qr/start | S2 |
| 11 | getQrLoginStatus | GET /auth/qr/{token} | S2 |
| 12 | guestLogin | POST /auth/guest | S2 |
| 13 | logout | POST /auth/logout | S2 |
| 14 | getUser | GET /users/{userId} | S2 |
| 15 | updateUser | PATCH /users/{userId} | S2 |
| 16 | getUserStats | GET /users/{userId}/stats | S2 |
| 17 | getUserAchievements | GET /users/{userId}/achievements | S2 |
| 18 | getUserLoyalty | GET /users/{userId}/loyalty | S2 |
| 19 | getCurrentSession | GET /sessions/current | S2 |
| 20 | createSession | POST /sessions | S2 |
| 21 | pauseSession | POST /sessions/{id}/pause | S2 |
| 22 | resumeSession | POST /sessions/{id}/resume | S2 |
| 23 | endSession | POST /sessions/{id}/end | S2 |
| 24 | extendSession | POST /sessions/{id}/extend | S2 |
| 25 | postSessionEvents | POST /sessions/{id}/events | S2 |
| 26 | getTariffs | GET /tariffs | S2 |
| 27 | getGames | GET /games | S3 |
| 28 | getGame | GET /games/{id} | S3 |
| 29 | sendLaunchReport | POST /games/{id}/launch-report | S3 |
| 30 | getUpdateManifest | GET /updates/{channel}/manifest | S3 |
| 31 | getPc | GET /pcs/{pcId} | S3 |
| 32 | adminLogin | POST /admin/login | S4 |
| 33 | adminLogout | POST /admin/logout | S4 |
| 34 | adminMe | GET /admin/me | S4 |
| 35 | adminOverview | GET /admin/overview | S4 |
| 36 | adminOpenSession | POST /admin/sessions | S4 |
| 37 | adminExtend | POST /admin/sessions/extend | S4 |
| 38 | adminEnd | POST /admin/sessions/end | S4 |
| 39 | adminTopUp | POST /admin/wallet/topup | S4 |
| 40 | adminCommand | POST /admin/pcs/{pcId}/command | S4 |
| 41 | adminShift | GET /admin/shift | S4 |
| 42 | adminOpenShift | POST /admin/shift/open | S4 |
| 43 | adminCloseShift | POST /admin/shift/close | S4 |
| 44 | adminQuote | POST /admin/quote | S4 |
| 45 | adminPcs | GET /admin/pcs | S4 |
| 46 | adminAddPc | POST /admin/pcs | S4 |
| 47 | adminUpdatePc | PATCH /admin/pcs/{pcId} | S4 |
| 48 | adminDeletePc | DELETE /admin/pcs/{pcId} | S4 |
| 49 | adminStaff | GET /admin/staff | S5 |
| 50 | adminAddStaff | POST /admin/staff | S5 |
| 51 | adminUpdateStaff | PATCH /admin/staff/{id} | S5 |
| 52 | adminClients | GET /admin/clients | S5 |
| 53 | adminAddClient | POST /admin/clients | S5 |
| 54 | adminUpdateClient | PATCH /admin/clients/{id} | S5 |
| 55 | adminBindClientCard | POST /admin/clients/{id}/card | S5 |
| 56 | adminSetClientPassword | POST /admin/clients/{id}/password | S5 |
| 57 | adminClientTransactions | GET /admin/clients/{id}/transactions | S5 |
| 58 | adminRedeemPromo | POST /admin/promo/redeem | S5 |
| 59 | adminSettings | GET /admin/club | S5 |
| 60 | adminSaveSettings | PATCH /admin/club | S5 |
| 61 | adminApiKey | GET /admin/club/api-key | S5 |
| 62 | adminRotateApiKey | POST /admin/club/api-key | S5 |
| 63 | adminTariffs | GET /admin/tariffs | S5 |
| 64 | adminAddTariff | POST /admin/tariffs | S5 |
| 65 | adminSaveTariff | PUT /admin/tariffs/{id} | S5 |
| 66 | adminDeleteTariff | DELETE /admin/tariffs/{id} | S5 |
| 67 | adminProducts | GET /admin/products | S5 |
| 68 | adminUpdateProduct | PATCH /admin/products/{id} | S5 |
| 69 | adminReceiveProduct | POST /admin/products/{id}/receive | S5 |
| 70 | adminGames | GET /admin/games | S5 |
| 71 | adminHealth | GET /admin/health | S5 |
| 72 | adminUpdateTicket | PATCH /admin/health/tickets/{id} | S5 |
| 73 | adminSaveHealthSettings | PATCH /admin/health/settings | S5 |
| 74 | adminControl | GET /admin/control | S5 |
| 75 | adminReports | GET /admin/reports | S5 |

Итог: S1 — 8 и WS, S2 — 18, S3 — 5, S4 — 17, S5 — 27 (8+18+5+17+27 = 75). Остальные 27 операций контракта, включая
`adminTestNotification`, отвечают 501 с S0; `getBalance` (S2) и `reportAntiCheat` (S3) затем реализуются сверх
контракта, так что после S3 501 отвечают 25. Вне контракта: balance, game-settings (list, DELETE) — S2;
anticheat/report — S3; `PATCH /admin/games/{id}` (реализуется) и `/admin/network*` (501) — S5.

---

## 12. Решения по умолчанию и вопросы владельцу

### 12.1 Решения по умолчанию (приняты, работа идёт; владелец может отменить)

| ID | Решение |
|---|---|
| D-1 | Сервер живёт в монорепо `server/`, net10, отдельный sln и CI-job на ubuntu + PG18; агент остаётся на net8 |
| D-2 | DTO агента и игрока — из ClubShell.Contracts (новые добавляются туда же); admin DTO пишутся вручную; сериализация — `JsonDefaults` / `AdminJson` (§2.4) |
| D-3 | Контракт вендорится в `server/contracts` с `REF`; CI проверяет дрейф |
| D-4 | Повтор подписи **не** отклоняется (`ReplayMode=Log`); режим `Reject` → `409 replay` только для POST/PATCH без ключа |
| D-5 | Подпись обязательна для режимов agent и user; режима «только лог» нет |
| D-6 | `X-Club-Key` (enrollment) ≠ `ck_` (owner API key); обе ротации с grace только у enrollment |
| D-7 | Регистрация строгая: неизвестный HWID → pendingApproval, одобрение через `PATCH maintenance:false`; `AutoApprovePcs` только для dev и тестов |
| D-8 | Цена — одна функция для всех каналов; возврат пропорционален оплаченному; soft delete тарифов и снимок цены в сеансе |
| D-9 | Бонусы идут на основной баланс (`type=bonus`), `bonus_balance=0` |
| D-10 | Постоплата останавливается, когда стоимость ≥ баланс + `PostpaidCreditLimit` (0) |
| D-11 | Офлайн-реплей принимается по одному токену агента при выполнении условий §5.11, с overdraft |
| D-12 | Блокировка не останавливает оплату; grace 60 с бесплатно; сервер не завершает сеансы офлайн-ПК раньше 240 мин |
| D-13 | `features.callAdmin=false`, как и shop/chat/booking/tournaments/topup/apps: у кассы нет окна тикетов; события `callAdmin` из телеметрии сохраняются |
| D-14 | Каталог игр, товары и политика берутся из seed-JSON в `data/` (upsert по id при старте, bump версий): в контракте нет CRUD игр и товаров |
| D-15 | Один промокод — один раз на клиента |
| D-16 | Зона времени клуба `Asia/Tashkent` из конфига; вся календарная логика в местном времени |
| D-17 | TTL токена игрока 12 ч; неудачный вход считается один раз на `X-Trace-Id` |
| D-18 | QR: `start` и `status` реализованы, но без маршрута подтверждения статус всегда `pending`→`expired`; вкладку QR прячем флагом, когда он появится в контракте |
| D-19 | Одна сеть, один клуб на развёртывание, `club_id` везде; `/admin/network` → 501 |
| D-20 | Один инстанс (advisory lock `CSHub`); масштабирование — позже, через LISTEN/NOTIFY |
| D-21 | Сброс пароля клиента кассиром отзывает его токены (`userRevoked`) |
| D-22 | Правила: скидки не суммируются (берётся максимум); комендантский час и blacklist проверяются во всех каналах |
| D-23 | Прод на Railway, регион EU West: один сервис с volume `/app/data`, 1 реплика, Railway Postgres, IP клиента из `X-Real-IP`, MSI вне сервера (§S6). Почему EU West: ближе к Узбекистану у Railway региона нет. Почему облако: клубу не нужен свой сервер, владелец и касса работают откуда угодно, а общая сеть клубов (D-19, пока 501) ляжет туда же. Когда пропадает интернет клуба, ПК работают офлайн (§5.11), но кассир не может открыть новые сеансы. Локализация ПДн: с 27.03.2026 ст. 27¹ закона РУз «О персональных данных» разрешает хранение за рубежом при соблюдении условий. Строго локальными остались биометрия, генетические данные и данные абонентов связи; ClubShell их не хранит. Условия надзора уточнить у юриста |

### 12.2 Изменения контракта (PR в club-contracts, ведёт лид, владелец не нужен)

1. `getBalance` → required (агент на 501 отдаёт шеллу жёсткую ошибку). `reportAntiCheat` → required (204).
2. Добавить `/users/{userId}/game-settings*`: list, DELETE — required; GET, PUT и upload-target — notImplemented.
3. Добавить `PATCH /admin/games/{id}` (required) и поле `AdminGame.settingsPaths`. Добавить `GET /admin/network` и
   `POST /admin/network/clubs` как notImplemented.
4. `429 rateLimited` для `POST /admin/login`. В описании `staffBearer` указать, что `ck_` принимается как токен
   владельца.
5. Добавить правила `tariffZone`/`tariffTime` в 403 для `adminOpenSession` и `adminExtend`; `transaction` в ответе
   topup уже есть.
6. Операция подтверждения QR, например `POST /admin/qr/{token}/confirm` (касса) — либо флаг `features.qrLogin`.
7. Пометить в описании: сброс пароля кассиром отзывает токены пользователя.

### 12.3 Вопросы владельцу (только то, что без него не решить)

| ID | Вопрос | Умолчание до ответа |
|---|---|---|
| Q-1 | Нужен ли отдельный бонусный карман (бонусы не возвращаются и не выводятся, тратятся после основного)? | D-9: всё на основном |
| Q-2 | Может ли постоплата уходить в минус, и насколько (кредитный лимит постоянного клиента)? | D-10: 0, остановка |
| Q-3 | Защищаться ли в v1 от повтора неидемпотентных запросов (`ReplayMode=Reject`)? | D-4: нет, только лог |
| Q-4 | Принимать ли офлайн-сеанс по одному токену агента (иначе сеансы, начатые при офлайн-входе, теряются)? | D-11: да |
| Q-5 | Откуда берётся каталог игр: JSON вручную, библиотека club-server или редактор в кассе? | D-14: JSON |
| Q-6 | Нужен ли в v1 «вызов администратора» (тогда нужно окно тикетов в кассе и required `/support/call-admin`)? | D-13: выключен |
| Q-7 | ~~Где разворачиваем?~~ **Отвечено: Railway (D-23).** Открыто: домен кассы и API, тариф Railway (для бэкапов volume нужен Pro) | `*.up.railway.app` до выбора домена |
| Q-8 | Связывать ли ПК с club-server (фаза 2): добавит ли владелец в club-server read-only эндпоинт и токен для списка машин? | v1: хранится только MAC |
| Q-9 | Промокод — один раз на клиента? | D-15: да |
