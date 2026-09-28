# ClubShell Central Server

Центральный сервер клуба: REST `/api/v1/*` для агента, игрока (через агента) и кассы, WebSocket `/ws/agent`.
Заменяет `tools/MockServer` в продакшене. Дизайн и план срезов — [`docs/server/DESIGN.md`](../docs/server/DESIGN.md);
провод задаёт контракт `deepunites/club-contracts`, его копия лежит в [`contracts/`](contracts).

Состояние: **срез S1** — агенты и WS hub поверх S0 (скелет, миграции, аутентификация, `/health`, 501).

- Реализованы 8 операций агента: `register`, `refresh`, `heartbeat`, `sendTelemetry`, `getConfig`, `getPolicies`,
  `getCommands`, `ackCommand`, и канал `/ws/agent` (рукопожатие, ping/pong, очередь команд с ack по WS и REST,
  7 событий агента). Остальные 94 операции отвечают `501`.
- Регистрация строгая (D-7): новый ПК получает `403 forbidden`, `details.reason = pendingApproval`, `details.pcId`, пока
  владелец его не одобрит (`PATCH /admin/pcs/{pcId} {maintenance:false}` появится в S4; до этого —
  `UPDATE pcs SET approved = true, maintenance = false`). `Club:AutoApprovePcs=true` — только для dev и тестов.
  Неизвестный HWID с MAC живого ПК клуба (замена диска) создаёт ПК в ожидании с номером и именем старого места.
- Refresh-токен одноразовый; повтор использованного отзывает все токены ПК (`401 reused`), WS закрывается `4401`.
- Политика ПК берётся из `data/policy.json`, а если его нет — из `config/policies.example.json` (копируется в
  `seed/`). Изменённый seed при старте поднимает `policyVersion`.
- Команды лежат в `agent_commands` и доставляются при подключении и через REST; `pendingCommands` в heartbeat не
  считает команды, уже отправленные по живому WS за последние 5 мин (N1).
- Один инстанс на базу: при старте берётся `pg_try_advisory_lock('CSHub')` (при `Workers:Enabled`); занят — сервер
  не стартует.
- Все 102 операции контракта смаплены: нереализованные отвечают `501` с `error.code = notImplemented`, никогда `404`. Перед 501
  проверяется аутентификация в режиме операции (`club` / `agent` / `user` / `staff`, DESIGN §3.1). Режимы `user` и
  `staff` в S0 — заглушки: требуется только наличие `X-User-Token` / `Authorization: Bearer`.
- Подпись запросов агента (HMAC, окно ±300 с) проверяется всегда; повтор подписи только журналируется (D-4).
- Неизвестный маршрут `/api/v1/*` → `404 notFound`, `details.route`.
- `GET /health` → `200 {"status":"ok"}`.
- При старте: миграции под advisory lock, создание сети и клуба из `Club:*` (один клуб на развёртывание).

## Структура

```
server/
  ClubShell.Server.sln        сервер + тесты (+ src/ClubShell.Contracts, src/ClubShell.Core — реальный агентский клиент для тестов)
  contracts/                  вендорный контракт: openapi.yaml, asyncapi.yaml, openapi.json, asyncapi.json, REF
  scripts/sync-contracts.ps1  обновление contracts/ из club-contracts
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
WS-тесты поднимают Kestrel на `127.0.0.1` со случайным портом.

CI: job `server` в `.github/workflows/server.yml` (ubuntu, сервис `postgres:18`).

## Конфигурация

Все ключи и значения по умолчанию — таблица в DESIGN §2.5. Используются: `ConnectionStrings:Club`,
`Database:MigrateOnStart`, `Contracts:OpenApiPath`, `Auth:Issuer` / `Audience` / `SigningKeyPath` /
`AgentTokenMinutes` / `RefreshTokenDays` / `SignatureWindowSec`, `Club:Name` / `TimeZone` / `EnrollmentKey` /
`PreviousEnrollmentKey` / `AutoApprovePcs`, `Realtime:PingSec` / `PongTimeoutSec` / `MaxFrameBytes`,
`Agents:HeartbeatSec` / `OfflineAfterSec` / `CommandTtlMin`, `Sessions:GraceSec` / `MaxOfflineMinutes` (уходят в
конфиг агента), `Catalog:PolicySeedPath`, `Workers:Enabled`, `Proxy:Trusted` / `ClientIpHeader`, переменная
окружения `PORT`.

`pcs.signing_secret` (HMAC-ключ подписи агента) хранится открытым: подпись симметрична (DESIGN §3.2). Резервные копии
базы нужно защищать как секрет.

`Proxy:ClientIpHeader` (Railway: `X-Real-IP`) включайте, только если сервер доступен исключительно через этот прокси:
иначе клиент подставит любой адрес сам.

## Контракт

`contracts/` — копия бандла `club-contracts` на коммите из `contracts/REF`. Обновление:

```powershell
./server/scripts/sync-contracts.ps1                 # ../club-contracts на HEAD
./server/scripts/sync-contracts.ps1 -Ref <commit>
```

Скрипт берёт файлы из git-объектов коммита, пишет `REF` и пересобирает `openapi.json` и `asyncapi.json` (нужны git и
python с PyYAML). CI проверяет, что оба json собраны из своих yaml и что оба yaml совпадают с `club-contracts@REF`. Если
коммит `REF` ещё не запушен в `deepunites/club-contracts`, сравнение пропускается с предупреждением.
