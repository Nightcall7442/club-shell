<#
.SYNOPSIS
    Turns the kiosk profile reset between players on or off on this PC (agent.json → shell.kioskUser.resetProfileOnLogout).
.DESCRIPTION
    With the reset on, the kiosk account's profile is deleted after every session, so the next player does not inherit the
    previous one's Steam, Discord or browser sign-ins. Windows recreates the profile at the next logon; from ClubShell
    1.0.15 the Agent writes the shell replacement and the lockdown into it again and closes the explorer.exe that the
    fresh profile starts (1.0.6-1.0.14 left a Windows desktop there, which is why enable-kiosk-autologon.ps1 turns the
    reset off). Restarts the Agent service so the change applies; run it between sessions. Run as administrator.
.PARAMETER Off
    Turns the reset off again.
.EXAMPLE
    .\set-profile-reset.ps1
    .\set-profile-reset.ps1 -Off
#>
[CmdletBinding()]
param([switch] $Off)

$ErrorActionPreference = 'Stop'
$agentConfig = Join-Path $env:ProgramData 'ClubShell\agent.json'
$enable = -not $Off

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Запустите от имени администратора.' -ForegroundColor Red
    exit 1
}
if (-not (Test-Path -LiteralPath $agentConfig)) {
    Write-Host 'Нет agent.json: ClubShell не установлен на этом ПК.' -ForegroundColor Red
    exit 1
}

$config = Get-Content -LiteralPath $agentConfig -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $config.shell -or -not $config.shell.kioskUser) {
    Write-Host 'В agent.json нет раздела shell.kioskUser.' -ForegroundColor Red
    exit 1
}
$kiosk = $config.shell.kioskUser
if ($kiosk.PSObject.Properties['resetProfileOnLogout']) { $kiosk.resetProfileOnLogout = $enable }
else { Add-Member -InputObject $kiosk -NotePropertyName 'resetProfileOnLogout' -NotePropertyValue $enable }

# Written to a temp file and read back before it replaces agent.json.
$text = $config | ConvertTo-Json -Depth 30
if (($text | ConvertFrom-Json).shell.kioskUser.resetProfileOnLogout -ne $enable) {
    Write-Host 'Проверка записи не прошла: agent.json не изменён.' -ForegroundColor Red
    exit 1
}
$temp = $agentConfig + '.tmp'
[IO.File]::WriteAllText($temp, $text, (New-Object System.Text.UTF8Encoding $false))
Move-Item -LiteralPath $temp -Destination $agentConfig -Force

Restart-Service -Name 'ClubShellAgent' -Force
if ($enable) {
    Write-Host 'Сброс профиля между игроками ВКЛЮЧЁН. Проверьте: войдите в Steam, завершите сеанс,' -ForegroundColor Green
    Write-Host 'подождите 1-2 минуты и войдите снова: Steam не должен помнить аккаунт, рабочий стол Windows не должен появиться.' -ForegroundColor Green
} else {
    Write-Host 'Сброс профиля между игроками ВЫКЛЮЧЕН.' -ForegroundColor Yellow
}
