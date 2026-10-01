<#
.SYNOPSIS
    Prepares a reference PC with ClubShell installed for capturing an image (docs/server/PILOT.md).
.DESCRIPTION
    The agent starts right after the install and registers this PC. Its registration lives in files that a clone would
    inherit: agent-identity.json, secure\agent.tokens and secure\hwid.fallback (the agent reuses stored tokens while
    they are valid, so every clone would be the same PC for the server). This script stops the agent and deletes them.
    Capture the image right after it, without a reboot: on the next boot the agent starts and registers again.
    The PC this one registered as stays in the console (Map) as a pending or approved seat: delete it there.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$service = 'ClubShellAgent'
$dataDir = Join-Path $env:ProgramData 'ClubShell'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Запустите от имени администратора.' -ForegroundColor Red
    exit 1
}

$svc = Get-Service -Name $service -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Host 'ClubShell не установлен на этом ПК: сначала установите его.' -ForegroundColor Red
    exit 1
}
if ($svc.Status -ne 'Stopped') {
    Stop-Service -Name $service -Force
    $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

$removed = 0
foreach ($rel in 'agent-identity.json', 'secure\agent.tokens', 'secure\hwid.fallback') {
    $path = Join-Path $dataDir $rel
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
        $removed++
    }
}

Write-Host "Готово: агент остановлен, удалено файлов регистрации: $removed." -ForegroundColor Green
Write-Host 'Теперь удалите этот ПК в кассе («Карта»), если он там появился.' -ForegroundColor Yellow
Write-Host 'Снимайте образ сразу, НЕ перезагружая ПК.' -ForegroundColor Yellow
