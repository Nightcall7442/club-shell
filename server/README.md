# ClubShell Central Server

Центральный сервер клуба: REST `/api/v1/*` для агента, игрока (через агента) и кассы, WebSocket `/ws/agent`.
Заменяет `tools/MockServer` в продакшене. Дизайн и план срезов — [`docs/server/DESIGN.md`](../docs/server/DESIGN.md);
провод задаёт контракт `deepunites/club-contracts`, его копия лежит в [`contracts/`](contracts).

Состояние: **срез S2** — вход игрока, пользователи, сеансы, биллинг и кошелёк поверх S1 (агенты, WS hub) и S0 (скелет,
миграции, аутентификация, `/health`, 501).

- Реализованы 8 операций агента: `register`, `refresh`, `heartbeat`, `sendTelemetry`, `getConfig`, `getPolicies`,
  `getCommands`, `ackCommand`, и канал `/ws/agent` (рукопожатие, ping/pong, очередь команд с ack по WS и REST,
  7 событий агента).
- S2: 18 операций игрока — `login`, `startQrLogin`, `getQrLoginStatus`, `guestLogin`, `logout`, `getUser`, `updateUser`,
  `getUserStats`, `getUserAchievements`, `getUserLoyalty`, `getCurrentSession`, `createSession`, `pauseSession`,
  `resumeSession`, `endSession`, `extendSession`, `postSessionEvents`, `getTariffs` — и сверх контракта
  `GET /wallet/{userId}/balance` (`getBalance`) и `/users/{userId}/game-settings` (список → `{items:[]}`, `DELETE` → 204,
  остальные game-settings → 501). Пуши `sessionUpdated`, `walletUpdated` (`userRevoked` — с кассой в S4: `logout`
  самого ПК его не шлёт). Остальные 75 операций контракта
  отвечают `501`.
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
  балансом строками леджера; id — как у мока. PIN персонала 0000/1111 появятся в S4.
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
  проверяется аутентификация в режиме операции (`club` / `agent` / `user` / `staff`, DESIGN §3.1). Режим `user`
  проверяет токен игрока (живой, привязан к ПК токена агента, иначе `401 userToken` с `problem`); режим `staff` до S4 —
  заглушка: требуется только наличие `Authorization: Bearer`.
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
WS-тесты поднимают Kestrel на `127.0.0.1` со случайным портом. Классы тестов S2 наследуют `LedgerCheckedTest`: после
каждого теста проверяется инвариант `SUM(ledger_entries.amount) = wallets.main_balance`. Тик сеансов тесты вызывают
напрямую (`SessionTickWorker.RunOnceAsync`) с `FakeClock` фикстуры.

CI: job `server` в `.github/workflows/server.yml` (ubuntu, сервис `postgres:18`).

## Конфигурация

Все ключи и значения по умолчанию — таблица в DESIGN §2.5. Используются: `ConnectionStrings:Club`,
`Database:MigrateOnStart`, `Contracts:OpenApiPath`, `Auth:Issuer` / `Audience` / `SigningKeyPath` /
`AgentTokenMinutes` / `RefreshTokenDays` / `SignatureWindowSec` / `UserTokenHours`, `Club:Name` / `TimeZone` /
`EnrollmentKey` / `PreviousEnrollmentKey` / `AutoApprovePcs` / `OfflineLogin` / `GuestLogin`, `Realtime:PingSec` /
`PongTimeoutSec` / `MaxFrameBytes`, `Agents:HeartbeatSec` / `OfflineAfterSec` / `CommandTtlMin`, `Sessions:GraceSec` /
`MaxOfflineMinutes` (уходят в конфиг агента) / `TickMs` / `ResyncSec` / `PostpaidCreditLimit` (тиёны; `null` — без
лимита), `Catalog:PolicySeedPath`, `Workers:Enabled`, `Seed:Dev`, `Proxy:Trusted` / `ClientIpHeader`, переменная
окружения `PORT`.

Скидки и календарь цены (`pricing`, `groups`, `happyHours`, `loyalty`, `limits`) читаются из `clubs.settings` — документа
`AdminClubSettings`, который пишет `PATCH /admin/club` (S5). Пока ключа нет, правила нет: каждый день 100 %, без скидок,
комендантский час для младше 18 лет с 22:00 до 06:00.

`pcs.signing_secret` (HMAC-ключ подписи агента) хранится открытым: подпись симметрична (DESIGN §3.2). Резервные копии
базы нужно защищать как секрет.

`Proxy:ClientIpHeader` (Railway: `X-Real-IP`) включайте, только если сервер доступен исключительно через этот прокси:
иначе клиент подставит любой адрес сам.

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

## Контракт

`contracts/` — копия бандла `club-contracts` на коммите из `contracts/REF`. Обновление:

```powershell
./server/scripts/sync-contracts.ps1                 # ../club-contracts на HEAD
./server/scripts/sync-contracts.ps1 -Ref <commit>
```

Скрипт берёт файлы из git-объектов коммита, пишет `REF` и пересобирает `openapi.json` и `asyncapi.json` (нужны git и
python с PyYAML). CI проверяет, что оба json собраны из своих yaml и что оба yaml совпадают с `club-contracts@REF`. Если
коммит `REF` ещё не запушен в `deepunites/club-contracts`, сравнение пропускается с предупреждением.
