<#
.SYNOPSIS
    Removes ClubShell from this PC before a reinstall (pilot support): stops the agent first, so an uninstall does not
    hang on a service that keeps restarting, then runs the installer's own quiet uninstall.
.DESCRIPTION
    Pilot builds all carry MSI version 1.0.0, so a newer pilot build does not replace an older one by itself. Settings and
    registration in C:\ProgramData\ClubShell are kept (the installer never removes them); -RemoveData deletes them too.
#>
[CmdletBinding()]
param([switch] $RemoveData)

$ErrorActionPreference = 'Continue'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Запустите от имени администратора.' -ForegroundColor Red
    exit 1
}

$keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
$entries = @(Get-ChildItem $keys -ErrorAction SilentlyContinue | ForEach-Object { Get-ItemProperty $_.PSPath } |
    Where-Object { $_.DisplayName -eq 'ClubShell' -and $_.QuietUninstallString })
$service = Get-Service ClubShellAgent -ErrorAction SilentlyContinue
if (-not $entries -and -not $service) {
    Write-Host 'ClubShell не установлен.' -ForegroundColor Green
    exit 0
}

if ($service) {
    Write-Host 'Останавливаю агента...'
    & sc.exe config ClubShellAgent start= disabled | Out-Null
    Get-Process ClubShellAgent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    & sc.exe stop ClubShellAgent | Out-Null
    Start-Sleep -Seconds 2
    Get-Process ClubShellAgent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

foreach ($entry in $entries) {
    Write-Host "Удаляю ClubShell $($entry.DisplayVersion)..."
    # QuietUninstallString: "C:\ProgramData\Package Cache\{...}\ClubShellSetup.exe" /uninstall /quiet
    if ($entry.QuietUninstallString -match '^"(?<exe>[^"]+)"\s*(?<args>.*)$') {
        $p = Start-Process -FilePath $Matches.exe -ArgumentList $Matches.args -Wait -PassThru
        Write-Host "  код завершения: $($p.ExitCode)"
    }
}

if ($RemoveData) {
    Remove-Item -LiteralPath (Join-Path $env:ProgramData 'ClubShell') -Recurse -Force -ErrorAction SilentlyContinue
}

$left = @(Get-ChildItem $keys -ErrorAction SilentlyContinue | ForEach-Object { Get-ItemProperty $_.PSPath } | Where-Object { $_.DisplayName -eq 'ClubShell' })
if ($left -or (Get-Service ClubShellAgent -ErrorAction SilentlyContinue)) {
    Write-Host 'ClubShell удалён не полностью: перезагрузите ПК и запустите этот файл ещё раз.' -ForegroundColor Yellow
} else {
    Write-Host 'Готово: ClubShell удалён. Теперь можно ставить новую версию.' -ForegroundColor Green
}
