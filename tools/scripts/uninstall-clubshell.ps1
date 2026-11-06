<#
.SYNOPSIS
    Removes ClubShell from this PC before a reinstall (pilot support): stops the agent first, so an uninstall does not
    hang on a service that keeps restarting, then runs the installer's own quiet uninstall — the bundle's
    (ClubShellSetup.exe) or, for a PC set up with the MSI alone (msiexec /i), msiexec /x {ProductCode} (SHELL_CHANGES 63).
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
function Get-ClubShellEntries {
    @(Get-ChildItem $keys -ErrorAction SilentlyContinue | ForEach-Object { Get-ItemProperty $_.PSPath } | Where-Object { $_.DisplayName -eq 'ClubShell' })
}
# A bundle install (ClubShellSetup.exe) has a QuietUninstallString; an MSI installed on its own is keyed by its ProductCode.
function Get-MsiEntries {
    @(Get-ClubShellEntries | Where-Object { -not $_.QuietUninstallString -and $_.PSChildName -match '^\{[0-9A-Fa-f-]{36}\}$' })
}
$entries = @(Get-ClubShellEntries | Where-Object { $_.QuietUninstallString })
$service = Get-Service ClubShellAgent -ErrorAction SilentlyContinue
if (-not $entries -and -not (Get-MsiEntries) -and -not $service) {
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

# What the bundle did not take with it: an MSI installed with msiexec /i.
foreach ($msi in Get-MsiEntries) {
    $log = Join-Path $env:TEMP 'ClubShell_uninstall.log'
    Write-Host "Удаляю ClubShell $($msi.DisplayVersion) (MSI $($msi.PSChildName))..."
    $p = Start-Process -FilePath 'msiexec.exe' -ArgumentList "/x $($msi.PSChildName) /qn /norestart /l*v `"$log`"" -Wait -PassThru
    switch ($p.ExitCode) {
        0 { Write-Host '  готово' }
        3010 { Write-Host '  готово, нужна перезагрузка' -ForegroundColor Yellow }
        1605 { Write-Host '  уже удалён' }
        default { Write-Host "  код завершения: $($p.ExitCode), журнал: $log" -ForegroundColor Yellow }
    }
}

# USB storage back on (SHELL_CHANGES 28) even when the installer's own uninstall did not run: the policy's
# USBSTOR Start=4 is machine-wide and outlives the agent.
Set-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\USBSTOR' -Name Start -Value 3 -Type DWord -ErrorAction SilentlyContinue
# Pilot helper task from enable-kiosk-autologon.ps1 (keeps the auto-logon domain); harmless when absent.
Unregister-ScheduledTask -TaskPath '\ClubShell\' -TaskName 'AutoLogonDomain' -Confirm:$false -ErrorAction SilentlyContinue

if ($RemoveData) {
    Remove-Item -LiteralPath (Join-Path $env:ProgramData 'ClubShell') -Recurse -Force -ErrorAction SilentlyContinue
}

$left = Get-ClubShellEntries
if ($left -or (Get-Service ClubShellAgent -ErrorAction SilentlyContinue)) {
    Write-Host 'ClubShell удалён не полностью: перезагрузите ПК и запустите этот файл ещё раз.' -ForegroundColor Yellow
} else {
    Write-Host 'Готово: ClubShell удалён. Теперь можно ставить новую версию.' -ForegroundColor Green
}
