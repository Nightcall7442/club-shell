<#
.SYNOPSIS
    Prepares a reference PC with ClubShell installed for capturing an image (docs/server/PILOT.md).
.DESCRIPTION
    The agent starts right after the install and registers this PC. Its registration lives in files that a clone would
    inherit: agent-identity.json, secure\agent.tokens and secure\hwid.fallback (the agent reuses stored tokens while
    they are valid, so every clone would be the same PC for the server). This script stops the agent and deletes them.

    It also deletes what this PC accumulated while it was tested and what no clone may inherit: the offline store
    (cache\offline.db: cached players with their offline password hashes, the session outbox and a pending offline
    session that every clone would replay as its own), staged and downloaded updates, crash history, logs, the Shell's
    pipe token, cached saves and player settings, the kiosk password (secure\kiosk.cred: every clone would share it;
    a clone takes the one auto-logon holds and rotates it once the kiosk is logged on), the power-command acks and
    the other server caches (cache\*.json), the profile-reset markers and the anti-cheat folders kept across the last
    reset (cache\profile-keep). USB storage is switched back on (USBSTOR Start=3, the policy's HID block removed) so
    the image carries Windows' own state; the agent applies the club's policy again at the first boot of each clone.

    The agent keeps the club key DPAPI-wrapped in secure\club.key (machine scope) and blanks it in agent.json. A clone
    whose machine keys differ (sysprep) could not open that file, so the script puts the key back into agent.json and
    deletes secure\club.key: each clone's agent moves it into its own secure\club.key at the first start.

    It refuses to run without the update signing key (Agent\update-public.pem): a clone without it refuses every
    update and can only get it by a visit. It prints the server and the last 4 characters of the club key, so the
    owner can check that a rotated key reached this PC before cloning it.

    Capture the image right after it, without a reboot: on the next boot the agent starts and registers again.
    The PC this one registered as stays in the console (Map) as a pending or approved seat: delete it there.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$service = 'ClubShellAgent'
$persistentDir = $null
$dataDir = Join-Path $env:ProgramData 'ClubShell'
$agentDir = Join-Path $env:ProgramFiles 'ClubShell\Agent'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Запустите от имени администратора.' -ForegroundColor Red
    exit 1
}

$svc = Get-Service -Name $service -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Host 'ClubShell не установлен на этом ПК: сначала установите его.' -ForegroundColor Red
    exit 1
}

if (-not (Test-Path -LiteralPath (Join-Path $agentDir 'update-public.pem'))) {
    Write-Host 'На этом ПК нет ключа обновлений (update-public.pem): склонированные ПК не смогут обновляться.' -ForegroundColor Red
    Write-Host 'Установите ClubShell 1.0.15 или новее и запустите этот файл снова.' -ForegroundColor Red
    exit 1
}

# Which server and key the clones will register with (agent.json is kept across upgrades; the setup replaces the key).
# The key is in agent.json until the agent moves it into secure\club.key (DPAPI, machine scope, the agent's entropy).
$agentJson = Join-Path $dataDir 'agent.json'
$config = $null
$keyFile = $null
$storedKey = $null
if (Test-Path -LiteralPath $agentJson) {
    try {
        $config = Get-Content -LiteralPath $agentJson -Raw | ConvertFrom-Json
        $persistentDir = [string]$config.paths.persistent
        $key = [string]$config.server.clubApiKey
        if (-not $key) {
            $secureRoot = if ($persistentDir -and [IO.Path]::IsPathRooted($persistentDir)) { $persistentDir } else { $dataDir }
            $keyFile = Join-Path $secureRoot 'secure\club.key'
            if (Test-Path -LiteralPath $keyFile) {
                Add-Type -AssemblyName System.Security
                $entropy = [Text.Encoding]::UTF8.GetBytes('ClubShell.Agent.tokens.v1')
                $plain = [Security.Cryptography.ProtectedData]::Unprotect(
                    [IO.File]::ReadAllBytes($keyFile), $entropy, [Security.Cryptography.DataProtectionScope]::LocalMachine)
                $storedKey = [Text.Encoding]::UTF8.GetString($plain)
                $key = $storedKey
            }
        }
        $tail = if ($key.Length -ge 4) { $key.Substring($key.Length - 4) } else { '(нет ключа)' }
        Write-Host "Сервер: $($config.server.baseUrl)"
        Write-Host "Ключ клуба заканчивается на: $tail  (сверьте с Club__EnrollmentKey на Railway)" -ForegroundColor Cyan
    } catch {
        Write-Host "Не удалось прочитать agent.json или ключ клуба: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}
# secure\club.key is deleted below: without the key back in agent.json the clones could not register.
if ($keyFile -and (Test-Path -LiteralPath $keyFile) -and -not $storedKey) {
    Write-Host 'Ключ клуба в secure\club.key не расшифровывается. Запишите ключ в agent.json -> server.clubApiKey и запустите этот файл снова.' -ForegroundColor Red
    exit 1
}

if ($svc.Status -ne 'Stopped') {
    Stop-Service -Name $service -Force
    $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

$removed = 0
# paths.persistent (diskless seats, SHELL_CHANGES 25) holds the identity, the secrets, the outbox and the acks.
$roots = @($dataDir)
if ($persistentDir -and [IO.Path]::IsPathRooted($persistentDir) -and (Test-Path -LiteralPath $persistentDir)) {
    $roots += $persistentDir
}
$files = @(
    'agent-identity.json', 'secure\agent.tokens', 'secure\hwid.fallback', 'secure\shell.token', 'secure\kiosk.cred', 'secure\club.key',
    'cache\offline.db', 'cache\offline.db-wal', 'cache\offline.db-shm', 'crash-history.json')
foreach ($root in $roots) {
    foreach ($rel in $files + 'cache\power-acks.json') {
        $path = Join-Path $root $rel
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
            $removed++
        }
    }
}

# Server caches (server-config.json, power-acks.json, games.json, ...) and the profile-reset markers. Not
# cache\dns-filter.state: it remembers the DNS the adapters had before the policy, which the agent puts back.
$cache = Join-Path $dataDir 'cache'
if (Test-Path -LiteralPath $cache) {
    foreach ($item in Get-ChildItem -LiteralPath $cache -File -Force | Where-Object { $_.Name -like '*.json' -or $_.Name -like 'profile-*.marker' }) {
        Remove-Item -LiteralPath $item.FullName -Force
        $removed++
    }
}

# Folders are emptied, not removed: the installer created them with their ACLs.
foreach ($rel in 'pending-update', 'cache\updates', 'cache\saves', 'cache\player-settings', 'cache\profile-keep', 'logs') {
    $path = Join-Path $dataDir $rel
    if (Test-Path -LiteralPath $path) {
        foreach ($item in Get-ChildItem -LiteralPath $path -Force) {
            Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction SilentlyContinue
            $removed++
        }
    }
}

# The club key goes back into agent.json (secure\club.key is deleted above): each clone stores it under its own DPAPI keys.
if ($storedKey -and $config) {
    $config.server | Add-Member -NotePropertyName clubApiKey -NotePropertyValue $storedKey -Force
    ($config | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath $agentJson -Encoding utf8
    Write-Host 'Ключ клуба возвращён в agent.json: каждый клон при первом запуске спрячет его в secure\club.key.'
}

# USB storage as Windows ships it; the clone's agent applies the club's policy at its first boot.
$usbStor = 'HKLM:\SYSTEM\CurrentControlSet\Services\USBSTOR'
if (Test-Path -LiteralPath $usbStor) {
    Set-ItemProperty -LiteralPath $usbStor -Name 'Start' -Value 3 -Type DWord
}
$deviceInstall = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions'
$hidClass = '{745a17a0-74d3-11d0-b6fe-00a0c9f57da2}'
if ((Get-ItemProperty -LiteralPath "$deviceInstall\DenyDeviceClasses" -Name '1' -ErrorAction SilentlyContinue).'1' -eq $hidClass) {
    Remove-ItemProperty -LiteralPath "$deviceInstall\DenyDeviceClasses" -Name '1'
    Set-ItemProperty -LiteralPath $deviceInstall -Name 'DenyDeviceClasses' -Value 0 -Type DWord
}

Write-Host "Готово: агент остановлен, удалено файлов и папок: $removed." -ForegroundColor Green
Write-Host 'Если на этом ПК был открыт сеанс, закройте его в кассе.' -ForegroundColor Yellow
Write-Host 'Теперь удалите этот ПК в кассе («Карта»), если он там появился.' -ForegroundColor Yellow
Write-Host 'Дальше сразу, НЕ перезагружая ПК: sysprep /generalize /oobe /shutdown (образ для club-server)' -ForegroundColor Yellow
Write-Host 'или снятие образа диска (клонирование).' -ForegroundColor Yellow
