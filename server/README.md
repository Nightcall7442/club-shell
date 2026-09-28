# ClubShell Central Server

Центральный сервер клуба: REST `/api/v1/*` для агента, игрока (через агента) и кассы, позже — WebSocket `/ws/agent`.
Заменяет `tools/MockServer` в продакшене. Дизайн и план срезов — [`docs/server/DESIGN.md`](../docs/server/DESIGN.md);
провод задаёт контракт `deepunites/club-contracts`, его копия лежит в [`contracts/`](contracts).

Состояние: **срез S0** — скелет, миграции, аутентификация, `/health`, 501 на все операции контракта.

- Все 102 операции контракта смаплены и отвечают `501` с `error.code = notImplemented`, никогда `404`. Перед 501
  проверяется аутентификация в режиме операции (`club` / `agent` / `user` / `staff`, DESIGN §3.1). Режимы `user` и
  `staff` в S0 — заглушки: требуется только наличие `X-User-Token` / `Authorization: Bearer`.
- Подпись запросов агента (HMAC, окно ±300 с) проверяется всегда; повтор подписи только журналируется (D-4).
- Неизвестный маршрут `/api/v1/*` → `404 notFound`, `details.route`.
- `GET /health` → `200 {"status":"ok"}`.
- При старте: миграции под advisory lock, создание сети и клуба из `Club:*` (один клуб на развёртывание).

## Структура

```
server/
  ClubShell.Server.sln        сервер + тесты (+ src/ClubShell.Contracts)
  contracts/                  вендорный контракт: openapi.yaml, asyncapi.yaml, openapi.json, REF
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
(501 допускается всегда), тело — соответствовать схеме из `contracts/openapi.json`.

CI: job `server` в `.github/workflows/server.yml` (ubuntu, сервис `postgres:18`).

## Конфигурация

Все ключи и значения по умолчанию — таблица в DESIGN §2.5. В S0 используются: `ConnectionStrings:Club`,
`Database:MigrateOnStart`, `Contracts:OpenApiPath`, `Auth:Issuer` / `Audience` / `SigningKeyPath` /
`AgentTokenMinutes` / `SignatureWindowSec`, `Club:Name` / `TimeZone` / `EnrollmentKey` / `PreviousEnrollmentKey`,
`Proxy:Trusted` / `ClientIpHeader`, переменная окружения `PORT`.

`Proxy:ClientIpHeader` (Railway: `X-Real-IP`) включайте, только если сервер доступен исключительно через этот прокси:
иначе клиент подставит любой адрес сам.

## Контракт

`contracts/` — копия бандла `club-contracts` на коммите из `contracts/REF`. Обновление:

```powershell
./server/scripts/sync-contracts.ps1                 # ../club-contracts на HEAD
./server/scripts/sync-contracts.ps1 -Ref <commit>
```

Скрипт берёт файлы из git-объектов коммита, пишет `REF` и пересобирает `openapi.json` (нужны git и python с PyYAML).
CI проверяет, что `openapi.json` собран из `openapi.yaml` и что оба yaml совпадают с `club-contracts@REF`. Если
коммит `REF` ещё не запушен в `deepunites/club-contracts`, сравнение пропускается с предупреждением.
