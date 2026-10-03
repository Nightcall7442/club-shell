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
    pipe token, cached saves and player settings.

    It refuses to run without the update signing key (Agent\update-public.pem): a clone without it refuses every
    update and can only get it by a visit. It prints the server and the last 4 characters of the club key in
    agent.json, so the owner can check that a rotated key reached this PC before cloning it.

    Capture the image right after it, without a reboot: on the next boot the agent starts and registers again.
    The PC this one registered as stays in the console (Map) as a pending or approved seat: delete it there.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$service = 'ClubShellAgent'
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
$agentJson = Join-Path $dataDir 'agent.json'
if (Test-Path -LiteralPath $agentJson) {
    try {
        $config = Get-Content -LiteralPath $agentJson -Raw | ConvertFrom-Json
        $key = [string]$config.server.clubApiKey
        $tail = if ($key.Length -ge 4) { $key.Substring($key.Length - 4) } else { '(нет ключа)' }
        Write-Host "Сервер: $($config.server.baseUrl)"
        Write-Host "Ключ клуба заканчивается на: $tail  (сверьте с Club__EnrollmentKey на Railway)" -ForegroundColor Cyan
    } catch {
        Write-Host "Не удалось прочитать agent.json: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

if ($svc.Status -ne 'Stopped') {
    Stop-Service -Name $service -Force
    $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

$removed = 0
$files = @(
    'agent-identity.json', 'secure\agent.tokens', 'secure\hwid.fallback', 'secure\shell.token',
    'cache\offline.db', 'cache\offline.db-wal', 'cache\offline.db-shm', 'crash-history.json')
foreach ($rel in $files) {
    $path = Join-Path $dataDir $rel
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
        $removed++
    }
}

# Folders are emptied, not removed: the installer created them with their ACLs.
foreach ($rel in 'pending-update', 'cache\updates', 'cache\saves', 'cache\player-settings', 'logs') {
    $path = Join-Path $dataDir $rel
    if (Test-Path -LiteralPath $path) {
        foreach ($item in Get-ChildItem -LiteralPath $path -Force) {
            Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction SilentlyContinue
            $removed++
        }
    }
}

Write-Host "Готово: агент остановлен, удалено файлов и папок: $removed." -ForegroundColor Green
Write-Host 'Если на этом ПК был открыт сеанс, закройте его в кассе.' -ForegroundColor Yellow
Write-Host 'Теперь удалите этот ПК в кассе («Карта»), если он там появился.' -ForegroundColor Yellow
Write-Host 'Дальше сразу, НЕ перезагружая ПК: sysprep /generalize /oobe /shutdown (образ для club-server)' -ForegroundColor Yellow
Write-Host 'или снятие образа диска (клонирование).' -ForegroundColor Yellow
