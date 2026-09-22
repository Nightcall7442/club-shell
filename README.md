<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/assets/logo-horizontal-dark.svg">
  <img src="docs/assets/logo-horizontal.svg" alt="ClubShell" width="420">
</picture>

**Клиентское ПО для компьютерных клубов**

Киоск-оболочка вместо рабочего стола, агент-служба с сеансами и тарифами, запуск игр из любых лаунчеров — на каждом игровом ПК клуба.

[![CI](https://github.com/Nightcall7442/club-shell/actions/workflows/ci.yml/badge.svg)](https://github.com/Nightcall7442/club-shell/actions/workflows/ci.yml)
[![Release](https://github.com/Nightcall7442/club-shell/actions/workflows/release.yml/badge.svg)](https://github.com/Nightcall7442/club-shell/actions/workflows/release.yml)
![.NET](https://img.shields.io/badge/.NET-8.0-512bd4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-12-239120?logo=csharp&logoColor=white)
![Rust](https://img.shields.io/badge/Rust-1.89-000000?logo=rust&logoColor=white)
![TypeScript](https://img.shields.io/badge/TypeScript-5.5-3178c6?logo=typescript&logoColor=white)

![Tauri](https://img.shields.io/badge/Tauri-2.0-24c8d8?logo=tauri&logoColor=white)
![React](https://img.shields.io/badge/React-18-20232a?logo=react&logoColor=61dafb)
![Vite](https://img.shields.io/badge/Vite-5-646cff?logo=vite&logoColor=white)
![Tailwind](https://img.shields.io/badge/Tailwind_CSS-3.4-06b6d4?logo=tailwindcss&logoColor=white)
![Zustand](https://img.shields.io/badge/Zustand-4-443e38?logo=react&logoColor=white)
![i18next](https://img.shields.io/badge/i18next-23-26a69a?logo=i18next&logoColor=white)
![tokio](https://img.shields.io/badge/tokio-1-000000?logo=rust&logoColor=white)
![WebView2](https://img.shields.io/badge/WebView2-evergreen-0078d4?logo=microsoftedge&logoColor=white)
![SQLite](https://img.shields.io/badge/SQLite-offline-003b57?logo=sqlite&logoColor=white)
![Serilog](https://img.shields.io/badge/Serilog-4-cc0000)

![xUnit](https://img.shields.io/badge/xUnit-424_проверки-5c2d91?logo=dotnet&logoColor=white)
![Playwright](https://img.shields.io/badge/Playwright-e2e-2ead33?logo=playwright&logoColor=white)
![Fastify](https://img.shields.io/badge/Fastify-mock--сервер-000000?logo=fastify&logoColor=white)
![WiX](https://img.shields.io/badge/WiX-v5_MSI-c41e3a?logo=windows&logoColor=white)
![Windows](https://img.shields.io/badge/Windows-10%2F11_x64-0078d6?logo=windows&logoColor=white)
![Steam](https://img.shields.io/badge/Steam-Epic_%C2%B7_Battle.net_%C2%B7_Riot_%C2%B7_EA_%C2%B7_Ubisoft-171a21?logo=steam&logoColor=white)
![Языки](https://img.shields.io/badge/языки-RU_%C2%B7_UZ_%C2%B7_EN-8a6d3b)
![Лицензия](https://img.shields.io/badge/лицензия-MIT-3da639)

</div>

---

## Как выглядит

<div align="center">

<img src="docs/img/02-home.jpg" alt="Главная" width="100%">

| Вход | Игры | Магазин |
|:---:|:---:|:---:|
| <img src="docs/img/01-lock.jpg" alt="Экран входа"> | <img src="docs/img/03-games.jpg" alt="Каталог игр"> | <img src="docs/img/04-shop.jpg" alt="Магазин"> |
| **Кошелёк** | **Чат** | **Бронь мест** |
| <img src="docs/img/05-wallet.jpg" alt="Кошелёк и тарифы"> | <img src="docs/img/06-chat.jpg" alt="Чат с администратором"> | <img src="docs/img/07-booking.jpg" alt="Карта зала"> |
| **Турниры** | **Профиль** | **Простой** |
| <img src="docs/img/08-tournaments.jpg" alt="Турниры"> | <img src="docs/img/09-profile.jpg" alt="Профиль"> | <img src="docs/img/10-idle.jpg" alt="Экран простоя"> |

**Панель в игре** — `Ctrl+Shift+H` поверх полноэкранной игры: время, баланс, «+30 мин», позвать админа

<img src="docs/img/11-hud.jpg" alt="HUD поверх игры" width="100%">

</div>

Снимки сделаны в mock-режиме (`VITE_MOCK=1`), 1920×1080, тема `default` («Onyx»). Арт, обложки и видеолупы игр —
оригинальные, сгенерированные для демо (без чужих брендов); на кадре с панелью арт подложен вместо игры.

### Как это устроено визуально

- **Onyx** — нейтральная тема: почти чёрный фон, графитовое стекло, белые действия, золото только для VIP, бонусов и
  предупреждений. Хром не имеет оттенка — цвет экрана даёт арт выбранной игры: из него берётся доминирующий тон и
  подсвечивает угол под заголовком (`useImageTint`).
- **Главная** — арт на весь экран с плавным наездом, живой видеолуп (`Game.videoUrl`, пауза во время игры), rack-focus
  при смене игры, плёночное зерно и виньетка, параллакс за курсором. Стрип постеров переключает игру; две карточки —
  сеанс + баланс и бронь с живой картой зала.
- **Каталог** — стена постеров: наклон за курсором с бликом, название внутри арта, метаданные при наведении, скользящая
  «таблетка» активной категории.
- **Мелочи, которые чувствуются**: blur-up обложек, тик/клик/«вжух»/чайм (WebAudio-синтез, без файлов, тумблер
  `sound.uiSounds`), предупреждение «осталось 5 минут» как нижняя плашка трансляции поверх игры.

---

## Какую задачу решает

Компьютерный клуб зарабатывает на минутах, а теряет их в четырёх местах:

| Где течёт | Что происходит на самом деле |
|---|---|
| **Свободный рабочий стол** | Игрок закрывает оболочку, выходит в Windows, ставит свой софт, меняет настройки. Через неделю ПК «тормозит», через месяц — переустановка |
| **Таймер сеанса** | Время считает сервер, ПК про него не знает. Сервер упал или сеть моргнула — сеанс не заканчивается, деньги не списываются |
| **Аккаунты лаунчеров** | Один Steam-аккаунт на всех, пароль на стикере. Игрок логинится в свой — библиотека клуба пропадает, чужие сохранения затираются |
| **Администратор** | Заблокировать ПК, продлить время, перезагрузить, посмотреть экран — только ногами, через весь зал |

ClubShell закрывает контур на самом ПК: **оболочка** заменяет `explorer.exe` для киоск-пользователя и не выпускает его дальше своих экранов; **агент** живёт в сессии 0 как служба Windows, ведёт таймер сам, запускает игры под арендованными аккаунтами и выполняет команды администратора с сервера; всё, что игроку нужно — игры, баланс, магазин, чат, бронь, турниры — на одном экране под мышь, клавиатуру или геймпад.

**Работает без связи.** Сервер недоступен — агент продолжает считать время, пускает по кэшированным учёткам, кладёт события сеанса в очередь SQLite и досылает их, когда связь вернётся. Повторная отправка не создаёт дубль — у каждого запроса свой ключ идемпотентности.

---

## Что внутри

**Оболочка (киоск)**

- Экран блокировки: вход по паролю, QR-коду, карте или как гость; PIN для разблокировки
- Главная: полноэкранный арт выбранной игры с видеолупом, стрип постеров, сеанс и баланс в одной карточке, бронь
- Каталог-«стена постеров» с категориями, поиском, спецификациями; запуск в один клик
- Кошелёк: баланс, тарифы по зонам и времени суток, история, пополнение через Payme / Click / Uzum / наличные
- Магазин с корзиной и статусом заказа, чат с администратором, бронь мест на карте зала, турниры с сеткой и таблицей
- Профиль: статистика, достижения, программа лояльности, настройки (язык, тема, громкость, PIN)
- Экран простоя с рекламой и прайс-листом; оверлей для предупреждений поверх полноэкранной игры; быстрая панель в игре по `Ctrl+Shift+H` — время, баланс, «+30 мин», позвать админа
- Три языка (русский, узбекский, английский), темы из JSON, навигация с геймпада и экранной клавиатуры

**Агент (служба Windows)**

- Сеансы: старт / пауза / продление / блокировка, монотонный таймер, предупреждения за 15 / 5 / 1 минуту, тарификация
- Запуск игр: Steam, Epic, Battle.net, Riot, EA, Ubisoft и обычные exe; ожидание реального игрового процесса; job objects; уборка при выходе
- Пул аккаунтов: аренда учётки у сервера, подстановка в лаунчер, откат конфигов и Credential Manager, синхронизация облачных сохранений
- Политики с сервера: белый список процессов, USB, DNS-фильтр, блокировки Explorer, расписание питания, канал обновлений
- Античит-проверки перед запуском: EAC, FACEIT, Vanguard, Secure Boot / TPM / HVCI
- Удалённое администрирование: сообщения, блокировка, перезагрузка, скриншот, удалённое управление, Wake-on-LAN
- Обновления агента и оболочки с проверкой RSA-подписи манифеста, откатом и окном применения
- Киоск-пользователь: создание, ротация пароля, автологон, перенаправление папок, сброс профиля
- Сторож: запуск оболочки в интерактивной сессии через `CreateProcessAsUser`, перезапуск при падении, безопасный режим при crash-loop

**Касса (`apps/admin`)**

- Карта зала: все ПК с зонами, статусом и таймером сеанса; счётчик свободных мест по зонам
- Открыть время на месте (клиент, тариф, минуты, цена до подтверждения), добавить время, завершить сеанс с возвратом
- Пополнение баланса на кассе, сообщение на экран игрока, блокировка / перезагрузка / выключение ПК
- Работает против mock-сервера (`/api/v1/admin/*`); в бою указывает на серверный продукт оператора

**Платформа**

- Контракты (DTO, IPC, серверный API) канонически описаны на C#; зеркала для TypeScript и Rust генерируются
- Именованный канал `\\.\pipe\clubshell-agent` с ACL, токеном оболочки и проверкой процесса-клиента
- HMAC-SHA256 подпись запросов к серверу, DPAPI для секретов, никаких входящих портов на ПК
- Mock-сервер на Fastify: весь REST + WebSocket, чтобы разрабатывать и показывать без бэкенда
- Установщик WiX v5: MSI со службой, оболочкой и конфигами + Burn-бандл с .NET 8 и WebView2

---

## Архитектура

```mermaid
flowchart TB
    subgraph Server["Сервер клуба (отдельный продукт)"]
        REST["REST /api/v1"]
        WS["WebSocket /ws/agent"]
    end

    subgraph PC["Игровой ПК — Windows 10/11 x64"]
        subgraph S0["Сессия 0"]
            A["ClubShellAgent<br/>.NET 8 Worker Service, LocalSystem<br/>сеансы · лаунчеры · политики · античит · обновления"]
        end
        subgraph S1["Интерактивная сессия киоск-пользователя"]
            SH["clubshell-shell.exe<br/>Tauri 2 (Rust): pipe-клиент, хуки, topmost, геймпад"]
            UI["WebView2<br/>React 18 + Vite + Tailwind + Zustand + i18next"]
            G["Игровые процессы"]
        end
    end

    A -->|"HTTPS · JWT · HMAC-SHA256"| REST
    A <-->|"WSS · clubshell.v1"| WS
    SH <-->|"\\\\.\\pipe\\clubshell-agent<br/>4-байтная длина + JSON envelope"| A
    UI <-->|"#[tauri::command] · события agent:// kiosk://"| SH
    A -->|"CreateProcessAsUser"| SH
    A -->|"CreateProcessAsUser"| G
```

Привилегии есть только у агента. Оболочка — интерфейс: любой побочный эффект в ОС (запуск, kill, реестр, firewall, питание, пользователи) уходит по каналу в агент. Подробно — в [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

---

## Технологии

| Слой | Что используется |
|---|---|
| Агент и библиотеки | .NET 8, C# 12, Worker Service, Serilog, Microsoft.Data.Sqlite, Polly (Http.Resilience), `TreatWarningsAsErrors` |
| Win32-слой | `LibraryImport` P/Invoke, низкоуровневые хуки, WMI, WTS, job objects, реестр, DPAPI |
| Оболочка, хост | Rust 2021, Tauri 2, tokio, `windows` 0.58, gilrs |
| Оболочка, UI | React 18, Vite 5, TypeScript 5.5, Tailwind CSS 3.4, Zustand 4, react-router 6, i18next 23, framer-motion |
| Контракты | C# → TypeScript и Rust через `tools/ContractsGen` (рефлексия, MANUAL-блоки сохраняются) |
| Проверки | xUnit + FluentAssertions + NSubstitute, cargo test, Playwright |
| Mock-сервер | Fastify 4, @fastify/websocket, tsx |
| Сборка и выпуск | pnpm 9, Cargo workspace, WiX v5, PowerShell 5.1, GitHub Actions |

---

## Быстрый старт

Только интерфейс, в браузере, на любой ОС — без Windows, агента и сервера:

```bash
pnpm install
pnpm mock                                        # mock-сервер → http://localhost:8080
VITE_MOCK=1 pnpm --filter @clubshell/shell dev   # → http://localhost:1420
```

Касса — отдельное приложение: `pnpm mock` (сервер) и `pnpm admin` → http://localhost:1421.

`VITE_MOCK=1` подменяет каждую Tauri-команду и событие обработчиками в браузере (`apps/shell/src/mocks/handlers.ts`): вход, тикающий таймер, кошелёк, заказы и чат живут в памяти вкладки.

### Учётные записи в mock-режиме

| Способ входа | Данные |
|---|---|
| Пароль | `demo` / `1234` (обычный), `vip` / `1234` (VIP), `player` / `player` |
| Гость | любое имя |
| Карта | любой номер; номера на `9` входят как VIP |
| QR | mock подтверждает вход сам через несколько секунд |
| PIN разблокировки сеанса | `1234` |
| PIN администратора (горячая клавиша `Ctrl+Alt+Shift+F12`) | `0000` |
| `banned` | любой пароль → ошибка бана, чтобы увидеть путь отказа |

### Полный цикл на Windows

```powershell
Copy-Item .env.example .env
.\tools\scripts\setup-dev-vm.ps1     # winget: Node, pnpm, .NET 8 SDK, Rust, VS Build Tools, WebView2 (нужен admin)
.\tools\scripts\dev.ps1              # mock-сервер + `tauri dev` (реальное киоск-окно, mock-транспорт агента)
.\tools\scripts\dev.ps1 -Agent       # … плюс настоящий агент консольным процессом (--console --dev)
.\tools\scripts\dev.ps1 -WebOnly     # mock-сервер + Vite в браузере (без Rust)
```

---

## Команды

| Что | Команда |
|---|---|
| Всё, Release | `.\tools\scripts\build.ps1` (`-Target All \| Dotnet \| Rust \| Web \| Installer \| Contracts \| Agent \| Shell`) |
| .NET | `dotnet build ClubShell.sln -c Release` · `dotnet test ClubShell.sln` |
| Rust | `cargo build --release --workspace` · `cargo test --workspace` · `cargo lint` (clippy `-D warnings`) |
| Web | `pnpm typecheck` · `pnpm lint` · `pnpm --filter @clubshell/shell build` · `pnpm tauri build` |
| E2E | `pnpm test:e2e` (Playwright против `vite dev` в mock-режиме); `PW_CHANNEL=msedge` — прогон в установленном Edge/Chrome, если bundled Chromium не стартует |
| Контракты | `.\tools\scripts\gen-contracts-ts.ps1` · `.\tools\scripts\gen-contracts-rs.ps1` (`-Check` для CI) |
| Установщик | `dotnet build installer/wix/ClubShell.Installer.wixproj -c Release -p:Version=<ver>` |
| Выпуск | `package.ps1` → `sign.ps1` (Authenticode + RSA-PSS манифест) → `publish.ps1` (S3 / HTTP / папка) |

---

## Проверки

| Что | Сколько |
|---|---|
| xUnit | 424 проверки в 4 проектах (Contracts 89, Core 181, Windows 62, Agent 92), включая сквозной тест именованного канала |
| Компиляция .NET | 8 проектов, Roslyn + NetAnalyzers, `/warnaserror`, 0 предупреждений |
| TypeScript | `tsc --noEmit` во всех пакетах, `vite build` без предупреждений о размере чанков |
| Интерфейс | 12 разделов, прогон в mock-режиме на 1600×900 / 1920×1080; Playwright e2e — 16 проверок (вход, каталог, HUD в игре) |
| Локализация | 1175 ключей, идентичные наборы в `en` / `ru` / `uz` |
| Протоколы | 63 IPC-запроса, 18 событий, 19 серверных команд, 80 Tauri-команд — покрыты обработчиками и документацией |

Что **не** собиралось на машине автора: `cargo` (крейты Rust проверены статически), WiX, `tauri build`, запуск Playwright. Первую сборку этих частей выполняет CI; список известных долгов — в [docs/ROADMAP.md](docs/ROADMAP.md).

---

## Структура

```
apps/admin/            касса: карта зала, открыть время, пополнить баланс, команды на ПК (React + Vite)
apps/shell/            киоск-оболочка: src/ (React) + src-tauri/ (Rust-хост Tauri 2)
apps/shell/public/mock-art/  оригинальный арт, обложки, видеолупы и фото товаров для mock-режима
config/                JSON-конфиги по умолчанию → C:\ProgramData\ClubShell
crates/protocol/       Rust-зеркало контрактов (serde)
crates/winutil/        Rust Win32: хуки, pipe, окна, мониторы, ввод
docs/                  спецификации и руководства (14 документов)
installer/wix/         WiX v5: ClubShell.msi + ClubShellSetup.exe (Burn)
packages/contracts-ts/ TypeScript-зеркало контрактов
src/ClubShell.Contracts/  канонические DTO, IPC, серверный API (C#)
src/ClubShell.Core/       абстракции, конфиг, HTTP/WS-клиенты, безопасность, обновления
src/ClubShell.Windows/    Win32-слой: P/Invoke, хуки, WMI, WTS, реестр, процессы, пользователи
src/ClubShell.Agent/      служба ClubShellAgent + Install/*.ps1
tests/                 xUnit, cargo integration (shell-rs), Playwright (shell-e2e)
tools/ContractsGen/    генератор зеркал контрактов
tools/MockServer/      mock центрального сервера (Fastify + ws)
tools/scripts/         build · dev · package · sign · publish · setup-dev-vm
.github/workflows/     ci.yml, release.yml
```

---

## Конфигурация

Рабочие данные — в `C:\ProgramData\ClubShell` (по умолчанию копируются из `config/` при первом старте):

| Файл | Назначение |
|---|---|
| `agent.json` | URL сервера, ключ клуба, IPC, киоск-пользователь (`club`), сеансы, офлайн, лаунчеры, хранилище, обновления, телеметрия, античит, питание |
| `shell.json` | язык (`ru` / `uz` / `en`), тема, киоск-защита, простой, реклама, геймпад, мониторы, флаги функций |
| `policies.json` | последний применённый снимок политик сервера |
| `themes\*.json` | темы (`default` = Onyx); цвета становятся CSS-переменными `--c-*`, контраст текста на `primary` / `accent` выводится из яркости |
| `cache\`, `logs\`, `secure\` | кэш и офлайн-очередь (SQLite), JSON-логи, секреты под DPAPI |

Любой ключ `agent.json` переопределяется переменной `CLUBSHELL__<Section>__<Key>`. Переменные разработки — в `.env` (`.env.example`).

---

## Роли на ПК

| Кто | Что может |
|---|---|
| `LocalSystem` (агент) | всё: процессы в чужой сессии, реестр, firewall, питание, пользователи |
| `club` (киоск-пользователь) | только группа Users; видит оболочку, свои игры и свои папки; `explorer.exe` заменён |
| Администратор клуба | через сервер: блокировка, продление, сообщение, скриншот, удалённое управление; локально — горячая клавиша + PIN |
| Игрок | экраны оболочки; ни одного побочного эффекта в ОС мимо агента |

---

## Документация

- [Архитектура](docs/ARCHITECTURE.md) — компоненты, сессии, границы доверия, потоки данных, восстановление, офлайн, схемы конфигов
- [Протокол IPC](docs/IPC_PROTOCOL.md) — именованный канал оболочка ⇄ агент: кадры, конверт, каждый запрос и событие
- [API сервера](docs/SERVER_API.md) — REST `/api/v1` и WebSocket `/ws/agent` глазами агента
- [Команды Tauri](docs/TAURI_COMMANDS.md) — все `#[tauri::command]` и события webview
- [Оболочка](docs/TAURI_SHELL.md) · [Киоск-режим](docs/KIOSK_MODE.md) · [Замена shell](docs/SHELL_REPLACEMENT.md) · [Темы](docs/THEMING.md)
- [Лаунчеры игр](docs/GAME_LAUNCHERS.md) · [Античит](docs/ANTICHEAT.md) · [Обновления](docs/UPDATES.md)
- [Безопасность](docs/SECURITY.md) — модель угроз, секреты, ACL канала, подпись запросов
- [Конкуренты](docs/COMPETITORS.md) · [Дорожная карта](docs/ROADMAP.md)

---

## Участие

- Контракты меняются только в `src/ClubShell.Contracts`; зеркала регенерируются `gen-contracts-*.ps1`, CI проверяет дрейф
- C#: file-scoped namespaces, nullable, `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`
- Rust: `cargo fmt`, `cargo lint`; `unsafe` только в `crates/winutil` и `src-tauri/src/kiosk`
- TypeScript: `strict`, Prettier, типы из `@clubshell/contracts`
- Строки интерфейса — через i18next, все три локали обновляются вместе

---

## Лицензия

[MIT](LICENSE) © 2026 ClubShell contributors.

<details>
<summary><b>English summary</b></summary>

**ClubShell** is client software for gaming clubs / internet cafés (Uzbekistan / CIS; UI in Russian, Uzbek and English; prices in UZS). Each gaming PC runs a **.NET 8 Windows service** (`ClubShellAgent`, session 0: sessions and billing timers, game launching via Steam / Epic / Battle.net / Riot / EA / Ubisoft with an account pool, server policies, anti-cheat checks, updates, telemetry, remote admin) and a **Tauri 2 + React kiosk shell** that replaces `explorer.exe` for the local kiosk user. The shell ships the neutral "Onyx" theme (near-black, white actions, gold accent — the game art carries the colour), a cinematic home with ambient video loops, a poster-wall catalogue and an in-game HUD (`Ctrl+Shift+H`: time, balance, +30 min, call admin) over the running game. They talk over the named pipe `\\.\pipe\clubshell-agent`; the agent talks to the club server over REST + WebSocket with HMAC-signed requests. Offline mode keeps the timer and queues events in SQLite.

Try the UI in a browser: `pnpm install && pnpm mock` then `VITE_MOCK=1 pnpm --filter @clubshell/shell dev` → http://localhost:1420 (login `demo` / `1234`). Full Windows dev loop: `tools/scripts/dev.ps1`. Verified here: all 8 .NET projects compile with `/warnaserror`, 424 xUnit tests pass, `tsc` and `vite build` are clean; Rust, WiX and Playwright runs are left to CI.

</details>
