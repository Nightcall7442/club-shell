<#
.SYNOPSIS
    Pilot install of ClubShell on one PC, with a plain-language report of the first launch (docs/server/PILOT.md).
.DESCRIPTION
    Run from an elevated PowerShell next to ClubShellSetup-<version>.exe. The script
      1. asks for the server address and the club key (the key is typed hidden, never written to disk by this script),
      2. checks that the server answers (GET /health),
      3. installs ClubShell silently (agent + shell),
      4. watches the agent's log until the PC is registered, and says in Russian what to do next
         (a new PC waits for the owner's approval in the console: Map -> approve).
    With -ForImage the PC is a reference for an image: after the install the agent is stopped and its registration files
    are removed, so that PCs cloned from the image register on their own instead of sharing one identity.
.PARAMETER ServerUrl
    Server origin, e.g. https://club.example.uz (no /api/v1). Asked for when omitted.
.PARAMETER ClubKey
    Club registration key (the server's Club__EnrollmentKey). Asked for (hidden) when omitted.
.PARAMETER SetupPath
    Path to ClubShellSetup-<version>.exe. Default: the newest ClubShellSetup*.exe next to this script, in the current
    folder or in Downloads.
.PARAMETER KioskUser
    Windows account of the kiosk. Default: club.
.PARAMETER ForImage
    Reference PC for an image: install, then stop the agent and delete agent-identity.json, agent.tokens, hwid.fallback.
.PARAMETER WaitMinutes
    How long to watch for the registration. Default: 5.
.EXAMPLE
    .\pilot-install.ps1
.EXAMPLE
    .\pilot-install.ps1 -ForImage
#>
[CmdletBinding()]
param(
    [string] $ServerUrl,
    [string] $ClubKey,
    [string] $SetupPath,
    [string] $KioskUser = 'club',
    [switch] $ForImage,
    [int] $WaitMinutes = 5
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'ClubShellAgent'
$DataDir = Join-Path $env:ProgramData 'ClubShell'

function Say([string] $text, [string] $color = 'White') { Write-Host $text -ForegroundColor $color }
function Fail([string] $text) { Say "ОШИБКА: $text" Red; exit 1 }

# What the agent says about its registration, newest state wins. The log is Serilog compact JSON: the message template is
# in "@mt", its values are properties of the same object, the exception text in "@x". Pure function: easy to test.
function Get-RegistrationState([string[]] $lines) {
    $state = [pscustomobject]@{ Kind = 'none'; Detail = '' }
    foreach ($line in $lines) {
        if ($line -notmatch '"@mt"') { continue }
        try { $e = $line | ConvertFrom-Json } catch { continue }
        switch -Wildcard ($e.'@mt') {
            'Registered as PC*' {
                $state = [pscustomobject]@{ Kind = 'registered'; Detail = "место «$($e.PcName)», зона «$($e.Zone)», id $($e.PcId)" }
            }
            'Reusing stored agent tokens*' {
                $state = [pscustomobject]@{ Kind = 'reused'; Detail = "id $($e.PcId)" }
            }
            'Registration attempt*failed*' {
                $x = [string] $e.'@x'
                $kind = if ($x -match 'waiting for approval|pendingApproval') { 'waiting' }
                        elseif ($x -match 'X-Club-Key|clubKey') { 'badKey' }
                        else { 'failing' }
                $first = ($x -split "`n")[0].Trim()
                $state = [pscustomobject]@{ Kind = $kind; Detail = "попытка $($e.Attempt)$(if ($kind -eq 'failing') { ": $first" })" }
            }
        }
    }
    $state
}

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail 'Запустите PowerShell «от имени администратора» и повторите.'
}

# --- 1. Inputs -------------------------------------------------------------------------------------------------------
if (-not $ServerUrl) { $ServerUrl = Read-Host 'Адрес сервера (например https://club.example.uz)' }
$ServerUrl = $ServerUrl.Trim().TrimEnd('/') -replace '/api/v\d+$', ''
if ($ServerUrl -notmatch '^https?://[^/\s]+$') { Fail "Адрес сервера выглядит неверно: $ServerUrl" }
if (-not $ClubKey) {
    $secure = Read-Host 'Ключ клуба (Club__EnrollmentKey; ввод не показывается)' -AsSecureString
    $ClubKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
}
if ([string]::IsNullOrWhiteSpace($ClubKey)) { Fail 'Ключ клуба пустой.' }

if (-not $SetupPath) {
    $here = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
    $candidates = @($here, (Get-Location).Path, (Join-Path $env:USERPROFILE 'Downloads')) | Select-Object -Unique |
        ForEach-Object { Get-ChildItem -Path $_ -Filter 'ClubShellSetup*.exe' -File -ErrorAction SilentlyContinue }
    $SetupPath = ($candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
if (-not $SetupPath -or -not (Test-Path -LiteralPath $SetupPath)) { Fail 'Не найден ClubShellSetup-*.exe. Положите его рядом со скриптом или укажите -SetupPath.' }
Say "Установщик: $SetupPath"

# --- 2. The server must answer ---------------------------------------------------------------------------------------
try {
    $health = Invoke-WebRequest -Uri "$ServerUrl/health" -UseBasicParsing -TimeoutSec 15
    if ($health.StatusCode -ne 200) { Fail "Сервер ответил на /health кодом $($health.StatusCode)." }
    Say "Сервер отвечает: $ServerUrl/health -> 200" Green
}
catch {
    Fail "Сервер $ServerUrl не отвечает ($($_.Exception.Message)). Проверьте адрес, интернет и что сервер запущен."
}

# --- 3. Install ------------------------------------------------------------------------------------------------------
Say 'Ставлю ClubShell (агент и шелл), это несколько минут...'
$setupArgs = @('/quiet', "SERVERURL=$ServerUrl", "CLUBAPIKEY=$ClubKey", "KIOSKUSER=$KioskUser", 'INSTALLSHELL=1')
$proc = Start-Process -FilePath $SetupPath -ArgumentList $setupArgs -Wait -PassThru
$ClubKey = $null
if ($proc.ExitCode -notin 0, 3010) {
    Fail "Установщик завершился с кодом $($proc.ExitCode). Журнал установки: $env:TEMP\ClubShellSetup_*.log"
}
if ($proc.ExitCode -eq 3010) { Say 'Установка просит перезагрузку ПК: перезагрузите и запустите скрипт ещё раз с -SetupPath, чтобы увидеть регистрацию.' Yellow }
Say 'ClubShell установлен.' Green

# --- 4a. Reference PC for an image ------------------------------------------------------------------------------------
if ($ForImage) {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force; $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
    foreach ($rel in 'agent-identity.json', 'secure\agent.tokens', 'secure\hwid.fallback') {
        Remove-Item -LiteralPath (Join-Path $DataDir $rel) -Force -ErrorAction SilentlyContinue
    }
    Say 'Эталонный ПК готов: агент остановлен, следы регистрации удалены.' Green
    Say 'Если этот ПК уже появлялся в кассе («Карта»), удалите его оттуда до снятия образа.' Yellow
    Say 'Теперь можно снимать образ. Агент не запускайте до снятия образа.' Green
    exit 0
}

# --- 4b. First launch: watch the registration ------------------------------------------------------------------------
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) { Fail "Служба $ServiceName не найдена после установки." }
if ($svc.Status -ne 'Running') { Start-Service -Name $ServiceName }
Say "Служба $ServiceName запущена. Слежу за регистрацией (до $WaitMinutes мин)..."

$deadline = (Get-Date).AddMinutes($WaitMinutes)
$last = ''
while ((Get-Date) -lt $deadline) {
    $log = Get-ChildItem -Path (Join-Path $DataDir 'logs') -Filter 'agent-*.json' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $state = if ($log) { Get-RegistrationState (Get-Content -LiteralPath $log.FullName -Tail 200 -ErrorAction SilentlyContinue) } else { [pscustomobject]@{ Kind = 'none'; Detail = '' } }
    $key = "$($state.Kind) $($state.Detail)"
    if ($key -ne $last) {
        $last = $key
        switch ($state.Kind) {
            'none'       { Say 'Агент запускается...' }
            'waiting'    { Say "ПК зарегистрирован на сервере и ждёт одобрения владельца ($($state.Detail)). В кассе откройте «Карта» и одобрите новый ПК: укажите номер и зону." Yellow }
            'registered' { Say "Готово: ПК зарегистрирован — $($state.Detail)." Green }
            'reused'     { Say "Агент использует прежнюю регистрацию ($($state.Detail)). Если это клон образа — вы забыли подготовить эталон (-ForImage)." Yellow }
            'badKey'     { Say "Сервер не принял ключ клуба ($($state.Detail)). Ключ должен совпадать с Club__EnrollmentKey на Railway; переустановите с правильным ключом." Red }
            'failing'    { Say "Агент не может зарегистрироваться ($($state.Detail)). Он продолжает попытки сам." Red }
        }
    }
    if ($state.Kind -in 'registered', 'reused', 'badKey') { break }
    Start-Sleep -Seconds 5
}

if ($state.Kind -eq 'waiting') { Say 'Время ожидания вышло, но агент продолжит сам: после одобрения в кассе он зарегистрируется в течение минуты.' Yellow }
elseif ($state.Kind -eq 'none') { Say "Регистрации в журнале пока нет. Журнал: $DataDir\logs. Проверьте адрес сервера и ключ клуба." Red }
Say "Журнал агента: $DataDir\logs"
