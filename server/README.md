# ClubShell Central Server

Центральный сервер клуба: REST `/api/v1/*` для агента, игрока (через агента) и кассы, WebSocket `/ws/agent`.
Заменяет `tools/MockServer` в продакшене. Дизайн и план срезов — [`docs/server/DESIGN.md`](../docs/server/DESIGN.md);
провод задаёт контракт `deepunites/club-contracts`, его копия лежит в [`contracts/`](contracts).

Состояние: **срезы S0–S6** — скелет и аутентификация (S0), агенты и WS hub (S1), игрок, сеансы, биллинг, кошелёк (S2),
игры, обновления, ПК (S3), касса (S4, S5), образ, compose, Railway и smoke в CI (S6). Развёртывание — раздел
[«Docker и развёртывание»](#docker-и-развёртывание).

- Реализованы 8 операций агента: `register`, `refresh`, `heartbeat`, `sendTelemetry`, `getConfig`, `getPolicies`,
  `getCommands`, `ackCommand`, и канал `/ws/agent` (рукопожатие, ping/pong, очередь команд с ack по WS и REST,
  7 событий агента).
- S2: 18 операций игрока — `login`, `startQrLogin`, `getQrLoginStatus`, `guestLogin`, `logout`, `getUser`, `updateUser`,
  `getUserStats`, `getUserAchievements`, `getUserLoyalty`, `getCurrentSession`, `createSession`, `pauseSession`,
  `resumeSession`, `endSession`, `extendSession`, `postSessionEvents`, `getTariffs` — и сверх контракта
  `GET /wallet/{userId}/balance` (`getBalance`) и `/users/{userId}/game-settings` (список → `{items:[]}`, `DELETE` → 204,
  остальные game-settings → 501). Пуши `sessionUpdated`, `walletUpdated` (`userRevoked` — с кассой в S4: `logout`
  самого ПК его не шлёт).
- S3: `getGames` (страницы до 1000, скрытые исключены, порядок владельца, ETag `"g<catalogVersion>-<hash>"` с
  `lastPlayedAt` игрока), `getGame`, `sendLaunchReport` (повтор из офлайн-очереди хранится один раз), `getUpdateManifest`
  (всегда `204`, решение владельца), `getPc` (`hwid` только своего ПК) и сверх контракта `POST /anticheat/report` (204).
  Каталог — из `data/games.json` (`Catalog:GamesSeedPath`, массив контрактных `Game` + `settingsPaths`): upsert по id при
  старте, пропавшие — soft delete, изменение поднимает `catalogVersion`. Нет файла — каталог не трогается.
- S4: 17 операций кассы — вход по PIN (`adminLogin`/`Logout`/`Me`; PIN хранится как HMAC с pepper из
  `data/pin-pepper.key`, токен скользящий 12 ч / абсолютный 7 д, 5 неверных PIN с IP за 300 с → `429`; `ck_…` из
  `clubs.api_key` = владелец), карта (`adminOverview`, `adminPcs`, `adminAddPc`/`UpdatePc`/`DeletePc` — одобрение ПК
  через `maintenance:false`), сеансы и деньги (`adminOpenSession`/`Extend`/`End`, `adminTopUp` с бонусом `bonusTiers`,
  `adminQuote` — та же `SessionService`/`Pricing`/`Ledger`, что у киоска), команды ПК (`adminCommand`: ack до
  `Agents:AckWaitSec`, офлайн → `agentOffline`), смена (`adminShift`/`OpenShift`/`CloseShift`, X/Z по `shift_id` строк
  леджера, `expectedCash` = открытие + наличные пополнения). Журнал действий — `audit_entries` (append-only). CORS только
  для `/api/v1/admin/*` и `Cors:AllowedOrigins`. `PcStatusWorker` пишет `pcOffline` в `telemetry_events`. Первый старт с
  пустой `staff` создаёт владельца с `Club:OwnerPin` (пусто — PIN пишется в лог один раз).
- S5: 27 операций кассы, часть 2 — персонал (`adminStaff`/`AddStaff`/`UpdateStaff`; последний активный владелец не
  отключается), клиенты (`adminClients` … `adminClientTransactions`; `adminRedeemPromo` — атомарно и раз на клиента), тарифы,
  товары и остатки (вебхук `lowStock`), настройки клуба (`adminSettings`/`SaveSettings` по JSON Schema контракта,
  `refreshConfig` подключённым ПК), ключ API (`ck_…`: HMAC под pepper + запечатанная копия для владельца), игры
  (`adminGames`, сверх контракта `PATCH /admin/games/{id}`), здоровье ПК (`adminHealth`, тикеты, автообслуживание), контроль
  (`adminControl`, 7 флагов) и отчёты (`adminReports`). Воркеры `HealthWorker`, `ClubTickWorker` (автоматизация),
  `WebhookWorker` (защита от SSRF, 3 повтора), `MaintenanceWorker`.
- Остальные 25 операций контракта отвечают `501` (кассиру на owner-only из них — сначала `403 ownerOnly`).
- Деньги (DESIGN §4.3, §5): единственная точка записи — `Wallet/Ledger.cs` (строка кошелька под `FOR UPDATE`,
  append-only `ledger_entries` с `balance_after`; `wallets.main_balance` — кеш суммы леджера). Цена — одна функция
  `Sessions/Billing/Pricing.cs`: день недели и праздники в зоне клуба, лучшая из скидок группы, уровня лояльности и
  happy hour, half-up до целого сума. Правила покупки §5.2, возврат пропорционально оплаченному, постоплата по
  замороженной цене, офлайн-реплей по одному токену агента с overdraft, поздние события с дедупликацией —
  `Sessions/SessionService.cs`.
- `Idempotency-Key` обязателен на `POST /sessions`, `/extend` и `/events`; повтор отдаёт сохранённый ответ с
  `Idempotent-Replayed: true`. Гонку открытия сеанса решают частичные уникальные индексы (`409 sessionAlreadyActive`).
- `SessionTickWorker` (раз в `Sessions:TickMs`, advisory lock `CSSess`, только при `Workers:Enabled`): `ending` →
  бесплатная грация → `timeUp` и команда `endSession` агенту; постоплата останавливается по средствам (D-10); сеанс
  офлайн-ПК не трогается до 240 мин тишины; resync-пуш `sessionUpdated` каждые `Sessions:ResyncSec`.
- `Seed:Dev` (Development и тесты): тарифы Standard 12 000 сум/ч, VIP, «Night Pack (5h)», группы клиентов
  (staff −50 %, student −15 %, …), демо-игроки alisher / dilnoza / bekzod (пароль `demo`, карты CARD-0001..0003) с
  балансом строками леджера; id — как у мока. Персонал: владелец 0000, «Кассир Азиз» 1111; зоны зала и бонусы пополнения
  как у мока.
- Регистрация строгая (D-7): новый ПК получает `403 forbidden`, `details.reason = pendingApproval`, `details.pcId`, пока
  владелец его не одобрит (`PATCH /admin/pcs/{pcId} {maintenance:false}`). `Club:AutoApprovePcs=true` — только для dev
  и тестов.
  Неизвестный HWID с MAC живого ПК клуба (замена диска) создаёт ПК в ожидании с номером и именем старого места.
- Refresh-токен одноразовый; повтор использованного отзывает все токены ПК (`401 reused`), WS закрывается `4401`.
- Политика ПК берётся из `data/policy.json`, а если его нет — из `config/policies.example.json` (копируется в
  `seed/`). Изменённый seed при старте поднимает `policyVersion`.
- Команды лежат в `agent_commands` и доставляются при подключении и через REST; `pendingCommands` в heartbeat не
  считает команды, уже отправленные по живому WS за последние 5 мин (N1).
- Один инстанс на базу: при старте берётся `pg_try_advisory_lock('CSHub')` (при `Workers:Enabled`); занят — сервер
  не стартует.
- Все 102 операции контракта смаплены: нереализованные отвечают `501` с `error.code = notImplemented`, никогда `404`. Перед 501
  проверяется аутентификация в режиме операции (`club` / `agent` / `user` / `staff`, DESIGN §3.1). Режим `user`
  проверяет токен игрока (живой, привязан к ПК токена агента, иначе `401 userToken` с `problem`); режим `staff` — живой
  токен персонала или `ck_` (иначе `401 invalid`).
- Подпись запросов агента (HMAC, окно ±300 с) проверяется всегда; повтор подписи только журналируется (D-4).
- Неизвестный маршрут `/api/v1/*` → `404 notFound`, `details.route`.
- `GET /health` → `200 {"status":"ok"}`.
- При старте: миграции под advisory lock, создание сети и клуба из `Club:*` (один клуб на развёртывание).

## Структура

```
server/
  ClubShell.Server.sln        сервер + тесты (+ src/ClubShell.Contracts, src/ClubShell.Core — реальный агентский клиент для тестов)
  Dockerfile                  образ (контекст сборки — корень репозитория), Dockerfile.dockerignore
  compose.yaml, .env.example  postgres:18 + сервер для запуска одной командой
  railway.toml                конфигурация сервиса Railway
  contracts/                  вендорный контракт: openapi.yaml, asyncapi.yaml, openapi.json, asyncapi.json, REF
  scripts/sync-contracts.ps1  обновление contracts/ из club-contracts
  scripts/smoke.sh            smoke запущенного сервера: /health, регистрация ПК, подписанный heartbeat
  src/ClubShell.Server/       ASP.NET Core minimal API, net10.0
  tests/ClubShell.Server.Tests/  xunit + WebApplicationFactory + временная база PostgreSQL
```

## Запуск локально

Нужны .NET SDK 10 (`global.json`) и PostgreSQL 18.

```powershell
createdb -h localhost -p 5432 -U postgres clubshell_dev   # один раз: сервер создаёт схему, но не базу
$env:ConnectionStrings__Club = 'Host=localhost;Port=5432;Database=clubshell_dev;Username=postgres;Password=...'
dotnet run --project server/src/ClubShell.Server
```

`dotnet run` берёт профиль `Properties/launchSettings.json`: окружение `Development`, `http://localhost:5209`. В нём
ключ регистрации агента — `dev-club-key` (`appsettings.Development.json`). Секреты
(`ConnectionStrings__Club`, `Club__EnrollmentKey`, …) задаются переменными окружения, не в `appsettings.json`.
Если задана переменная `PORT`, сервер слушает `http://0.0.0.0:$PORT` (так порт передаёт Railway).

При первом старте сервер создаёт ключ подписи JWT агента `data/jwt-signing-key.pem` (относительно content root).
Каталог `data/` в git не попадает; в продакшене это volume. Ротация ключа — заменить файл и перезапустить сервер:
агенты один раз обновят токен.

## Docker и развёртывание

Образ собирается из **корня репозитория** (сервер берёт `src/ClubShell.Contracts` и `config/`):

```bash
docker build -f server/Dockerfile -t clubshell-server .
```

Многоэтапная сборка `mcr.microsoft.com/dotnet/sdk:10.0` → `aspnet:10.0`; процесс идёт от пользователя `app` (не root), слушает
8080 (при заданном `PORT` — его). Каталог `/app/data` (ключ JWT, pepper PINов, seed-JSON) отдаётся под volume; `VOLUME` в
Dockerfile нет намеренно: Railway его не принимает. В образ не попадают `appsettings.Development.json` и `launchSettings.json`.

### docker compose

```bash
cp server/.env.example server/.env        # POSTGRES_PASSWORD, CLUB_ENROLLMENT_KEY, CORS_ORIGIN
docker compose -f server/compose.yaml up -d --build
curl http://localhost:8080/health          # {"status":"ok"}
```

`server/compose.yaml`: `postgres:18` (volume `pgdata` монтируется в `/var/lib/postgresql`: у образов PostgreSQL 18 кластер лежит
в подкаталоге версии) и сервер (volume `data` в `/app/data`), у обоих healthcheck. Один экземпляр сервера на базу (D-20).

Smoke — то же самое делает CI (job `docker-smoke`): `/health`, регистрация ПК и подписанный heartbeat агента.

```bash
CLUB_KEY=<CLUB_ENROLLMENT_KEY> bash server/scripts/smoke.sh http://localhost:8080
```

Сервер должен работать с `Club__AutoApprovePcs=true` (иначе регистрация отвечает `403 pendingApproval` — так задумано); нужны
`curl`, `openssl`, `jq`.

### Reverse proxy

- TLS завершает прокси, сервер слушает HTTP. Прокси **не переписывает путь** (`/api/v1/*` и `/ws/agent` уходят как есть) и
  пропускает WebSocket upgrade (`Upgrade`, `Connection`); таймаут простоя WS — не меньше минуты (сервер шлёт ping раз в 20 с).
- Адрес клиента (по нему считается лимит неверных PIN): либо `Proxy__ClientIpHeader=X-Real-IP`, либо список доверенных
  прокси `Proxy__Trusted__0=<CIDR>` (тогда читается `X-Forwarded-For` / `-Proto`). Включайте только если сервер доступен
  исключительно через этот прокси и тот перезаписывает присланный клиентом заголовок; иначе клиент подставит любой адрес сам.

### Секреты и ротация

| Что | Где | Ротация |
|---|---|---|
| Ключ подписи JWT агента | `data/jwt-signing-key.pem` (создаётся при первом старте) | заменить файл и перезапустить: агенты один раз обновят токен |
| Pepper PINов и паролей | `data/pin-pepper.key` | **не менять**: без него все PIN персонала и пароли клиентов недействительны |
| Ключ регистрации агентов | `Club__EnrollmentKey` | новый ключ в `Club__EnrollmentKey`, старый в `Club__PreviousEnrollmentKey`; раздать агентам; убрать старый |
| Ключ API `ck_…` | хеш в базе, показывается владельцу в кассе | `adminRotateApiKey`: старый ключ недействителен сразу |
| PIN владельца | первый старт: `Club__OwnerPin`, пусто — случайный PIN один раз в лог | менять в кассе (`adminUpdateStaff`) |
| Пароль PostgreSQL | `POSTGRES_PASSWORD` / переменные Railway | менять вместе со строкой подключения |

### Резервное копирование и восстановление

Копируйте **вместе**: базу PostgreSQL и каталог `data/`. Без `pin-pepper.key` PIN и пароли из базы не проверить; без ключа
JWT агенты один раз пройдут refresh. Восстановление: развернуть базу из копии, положить `data/` на volume, запустить сервер
(миграции добавят недостающее).

```bash
docker compose -f server/compose.yaml exec db pg_dump -U clubshell -Fc clubshell > clubshell.dump
docker compose -f server/compose.yaml cp server:/app/data ./data-backup
```

### Railway (прод, EU West — Амстердам)

`server/railway.toml` задаёт сборку из Dockerfile, healthcheck `/health` и одну реплику. Настройки сервиса Railway: Root
Directory — корень репозитория, config path `/server/railway.toml`, автодеплой из `main` с «Wait for CI». Сервис с volume не
масштабируется и не деплоится параллельно: старый инстанс останавливается до старта нового (короткий простой на деплой;
агенты переподключаются сами, команды лежат в БД).

Чек-лист первого деплоя (нужны аккаунт Railway и домен клуба); порядок пилота на 1–2 ПК с приёмкой — [`docs/server/PILOT.md`](../docs/server/PILOT.md):

1. Проект Railway, регион EU West; сервис **PostgreSQL** в том же проекте.
2. Сервис из этого репозитория (Root Directory = корень, config path `/server/railway.toml`), реплик — 1.
3. Volume на `/app/data`. Если сервис не может писать в volume (Railway монтирует его от root, а процесс идёт от `app`),
   задать переменную `RAILWAY_RUN_UID=0`.
4. Переменные сервиса:
   - `ConnectionStrings__Club=Host=${{Postgres.PGHOST}};Port=${{Postgres.PGPORT}};Username=${{Postgres.PGUSER}};Password=${{Postgres.PGPASSWORD}};Database=${{Postgres.PGDATABASE}}`
   - `ASPNETCORE_ENVIRONMENT=Production`
   - `Proxy__ClientIpHeader=X-Real-IP`
   - `Club__EnrollmentKey=<случайная строка>`, `Club__OwnerPin=<PIN владельца>`, `Club__TimeZone=<зона клуба>`
   - `Cors__AllowedOrigins__0=<origin кассы>`
5. Проверить, что край Railway перезаписывает присланный клиентом `X-Real-IP` (от этого зависит лимит неверных PIN): запрос
   с поддельным `X-Real-IP` должен считаться по реальному адресу. **Не проверено.**
6. Домен: сертификат Railway на `*.up.railway.app` или CNAME домена клуба; `https://<домен>/health` → 200.
7. `ServerUrl` в MSI агента — этот адрес; ключ регистрации — `Club__EnrollmentKey`. Первый ПК ждёт одобрения владельца в кассе.
8. Включить автоматические бэкапы volume у PostgreSQL и у `/app/data` (тариф Pro).

Файлы обновлений агента (MSI) сервер не раздаёт: когда `getUpdateManifest` перестанет отвечать 204, MSI лежат в GitHub Releases
или S3, сервер отдаёт только манифест (лимит HTTP-запроса Railway — 15 минут).

## Тесты

Тесты создают для каждого класса временную базу `clubshell_test_<guid>` и удаляют её в конце. Строка подключения
администратора берётся из `CLUBSHELL_TEST_PG` (по умолчанию `Host=localhost;Username=postgres`):

```powershell
$env:CLUBSHELL_TEST_PG = 'Host=localhost;Port=5433;Username=postgres;Password=...'
dotnet build server/ClubShell.Server.sln -warnaserror
dotnet test server/ClubShell.Server.sln
```

Каждый ответ `/api/v1/*` в тестах проходит через `ContractValidatingHandler`: статус должен быть объявлен в контракте
(501 допускается всегда), тело — соответствовать схеме из `contracts/openapi.json`. Кадры WS проверяются по сообщениям
`contracts/asyncapi.json`. `AgentHarness` гоняет настоящий `ServerClient` и `RealtimeClient` из `src/ClubShell.Core`;
WS-тесты поднимают Kestrel на `127.0.0.1` со случайным портом. Классы тестов S2 наследуют `LedgerCheckedTest`: после
каждого теста проверяется инвариант `SUM(ledger_entries.amount) = wallets.main_balance`. Тик сеансов тесты вызывают
напрямую (`SessionTickWorker.RunOnceAsync`) с `FakeClock` фикстуры.

CI: job `server` в `.github/workflows/server.yml` (ubuntu, сервис `postgres:18`) и job `docker-smoke` (образ, compose, `scripts/smoke.sh`).

E2e кассы против этого сервера (DESIGN §10.c; job `e2e-admin-real`): пустая одноразовая база и

```powershell
$env:ADMIN_SERVER = 'real'
$env:ADMIN_SERVER_DB = 'Host=localhost;Port=5433;Database=clubshell_e2e_x;Username=postgres;Password=...'
pnpm --filter @clubshell/shell-e2e exec playwright test --project admin   # PW_CHANNEL=chrome, если bundled Chromium не стартует
```

Playwright сам запускает `dotnet run` (Development, `Seed:Dev`, CORS для `:1431`) и кассу.

## Конфигурация

Все ключи и значения по умолчанию — таблица в DESIGN §2.5. Используются: `ConnectionStrings:Club`,
`Database:MigrateOnStart`, `Contracts:OpenApiPath`, `Auth:Issuer` / `Audience` / `SigningKeyPath` /
`AgentTokenMinutes` / `RefreshTokenDays` / `SignatureWindowSec` / `UserTokenHours` / `PepperPath` / `StaffTokenSlidingHours` /
`StaffTokenAbsoluteDays`, `Club:Name` / `TimeZone` / `EnrollmentKey` / `PreviousEnrollmentKey` / `AutoApprovePcs` /
`OwnerPin` / `OfflineLogin` / `GuestLogin`, `Realtime:PingSec` / `PongTimeoutSec` / `MaxFrameBytes`, `Agents:HeartbeatSec` /
`OfflineAfterSec` / `CommandTtlMin` / `AckWaitSec`, `RateLimit:PinAttempts` / `PinWindowSec`, `Cors:AllowedOrigins`, `Sessions:GraceSec` /
`MaxOfflineMinutes` (уходят в конфиг агента) / `TickMs` / `ResyncSec` / `PostpaidCreditLimit` (тиёны; `null` — без
лимита), `Catalog:PolicySeedPath` / `GamesSeedPath`, `Workers:Enabled`, `Seed:Dev`, `Proxy:Trusted` / `ClientIpHeader`, переменная
окружения `PORT`.

Скидки и календарь цены (`pricing`, `groups`, `happyHours`, `loyalty`, `limits`) читаются из `clubs.settings` — документа
`AdminClubSettings`, который пишет `PATCH /admin/club` (S5). Пока ключа нет, правила нет: каждый день 100 %, без скидок,
комендантский час для младше 18 лет с 22:00 до 06:00.

`pcs.signing_secret` (HMAC-ключ подписи агента) хранится открытым: подпись симметрична (DESIGN §3.2). Резервные копии
базы нужно защищать как секрет.

`Proxy:ClientIpHeader` (Railway: `X-Real-IP`) включайте, только если сервер доступен исключительно через этот прокси:
иначе клиент подставит любой адрес сам.

### Суперадминка: несколько клубов на одном сервере

Один сервер держит несколько клубов (сверх контракта, `/api/v1/platform/*`). Включается секретом
`Platform__AdminKey` — случайная строка от 24 символов; без него маршруты отвечают `404`, как будто их нет.

- **Страница:** касса по адресу `/platform` (например `https://<касса>/platform`). Вход — ключ суперадмина; он хранится только
  до закрытия вкладки.
- **Что умеет:** создать клуб (название, код, часовой пояс, владелец с PIN) — сервер выдаёт ключ для ПК этого клуба, показывает
  его один раз; новый ключ для ПК (прежний работает до следующей смены); добавить владельца; задать владельцу новый PIN (его
  прежние входы закрываются); выключить и включить клуб (сотрудники не входят, новые ПК не подключаются).
- **Код клуба.** PIN уникален только внутри клуба, поэтому касса спрашивает «Код клуба» (вводится один раз, запоминается на
  устройстве). Пока на сервере один клуб, код можно не вводить. Код первого клуба создаётся при старте и виден в суперадминке.
- **Первый клуб** создаётся из `Club:*`, как раньше; его ключ для ПК меняется через `Club__EnrollmentKey`, а не в суперадминке.
  Новый клуб получает данные из тех же seed-файлов (политика, игры, товары).
- **Установщик для клуба:** ключ из суперадминки вшивается так же, как в [`docs/server/PILOT.md`](../docs/server/PILOT.md)
  (`-p:DefaultClubApiKey=<ключ>`).

### Каталог игр и товаров

В контракте нет CRUD игр и товаров (DESIGN D-14), поэтому списки приходят из JSON-файлов, которые читаются при каждом старте:
`Catalog:GamesSeedPath` (по умолчанию `data/games.json`) и `Catalog:ProductsSeedPath` (`data/products.json`). Нет файла — каталог
остаётся как есть, то есть в новой базе пуст, и киоск показывает пустую библиотеку.

- **Игры** — массив контрактных `Game` (+ необязательный `settingsPaths`). Игра, которой нет в файле, скрывается, поэтому `id`
  должны быть постоянными. Пути настроек игрока и порядок задаются в кассе.
- **Товары** — массив контрактных `Product`. Товар, который уже есть, остаётся таким, как его отредактировали в кассе
  (название, цена, остаток); товар, которого нет в файле, скрывается.
- **Стартовый набор** лежит в образе: `seed/games.example.json` (15 популярных игр; у игр Steam обложки с CDN Steam, у остальных
  обложек нет) и `seed/products.example.json` (12 товаров с демонстрационными ценами и остатками). Включить:
  `Catalog__GamesSeedPath=seed/games.example.json` и `Catalog__ProductsSeedPath=seed/products.example.json` (в compose —
  `CATALOG_GAMES_SEED` и `CATALOG_PRODUCTS_SEED` в `server/.env`). Свой список — положить файлы в `data/` (volume) и вернуть
  значения по умолчанию. `exePath` игр с лаунчером `exe` и обложки не-Steam игр владелец задаёт сам.

## Сознательные отличия от мока

Мок остаётся dev- и e2e-сервером киоска (DESIGN §10.d). Здесь сервер ведёт себя иначе (S2; дополняется в S4/S5):

| Тема | Мок | Сервер |
|---|---|---|
| Цена киоска | база тарифа | одна функция `quote` с днём недели, праздниками и лучшей скидкой, как в кассе (§5.1) |
| Возврат при `admin`/`error` | по базовой цене за неиспользованные минуты | пропорционально фактически оплаченному, вниз до 100 тиёнов (§5.7) |
| Продление пакетом | `packagePrice` за любые минуты, зона и окно не проверяются | `package_minutes` принудительно, все правила §5.2 |
| `paused`/`resumed`/`extended`/`ended` в `/events` | только записываются | применяются в момент `at` (§5.12), `ended` рассчитывает сеанс |
| Офлайн-реплей `POST /sessions` | нужен токен игрока, отвечает 402 | хватает токена агента, overdraft вместо 402 (D-11) |
| Время вышло | завершение ровно в 0, в том числе у офлайн-ПК | `ending` + бесплатная грация, только онлайн; офлайн — после 240 мин тишины (D-12) |
| Постоплата | без лимита | `402`, если нет средств на первую минуту; останавливается на границе минуты, когда следующая не по средствам с `PostpaidCreditLimit` (D-10) |
| Блокировка | — | не останавливает оплату (`SessionState.cs:23` агента устарел) |
| `/sessions/current` | `pcId` не сверяется | `403 pcMismatch` |
| Неверный пароль | считается на каждый запрос (агент повторяет после refresh → −2), по имени | по имени, известному или нет; один повтор `X-Trace-Id` бесплатный; 5 за 15 мин — `attemptsLeft=0`; попытки по одному имени по очереди |
| `offlineHash` | строка вида Argon2, агент её не проверит | PBKDF2 в формате агента, заново из присланного пароля |
| Лояльность | очки на киоске отдельно от уровней кассы | уровни клуба по `lifetime_spent`; `points = spent / 100` |
| `lifetime_spent` | возвраты не учитываются | `− refund` |
| Тариф удалён | удаляется совсем | soft delete, цена постоплаты заморожена в сеансе |
| Журнал кассира | пишется до проверок, 5000 записей | после проверок, в транзакции действия, append-only без лимита |
| X/Z-отчёт смены | транзакции по времени, наличные — регуляркой по описанию | строки леджера по `shift_id`, наличные — `method = cash` |
| Неверный PIN | без ограничений | 5 с одного IP за 300 с → `429` |
| Номер места | повторы разрешены | занятый номер — `400 number taken` |
| Завершение с кассы | `sessionUpdated` агенту | команда `endSession {reason: admin}`, без push (§5.7) |
| `pcStatusChanged` | рассылается | не шлётся (AsyncAPI notImplemented), касса опрашивает |

## Контракт

`contracts/` — копия бандла `club-contracts` на коммите из `contracts/REF`. Обновление:

```powershell
./server/scripts/sync-contracts.ps1                 # ../club-contracts на HEAD
./server/scripts/sync-contracts.ps1 -Ref <commit>
```

Скрипт берёт файлы из git-объектов коммита, пишет `REF` и пересобирает `openapi.json` и `asyncapi.json` (нужны git и
python с PyYAML). CI проверяет, что оба json собраны из своих yaml и что оба yaml совпадают с `club-contracts@REF`. Если
коммит `REF` ещё не запушен в `deepunites/club-contracts`, сравнение пропускается с предупреждением.
