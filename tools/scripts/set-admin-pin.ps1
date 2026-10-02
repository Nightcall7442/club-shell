<#
.SYNOPSIS
    Sets (or removes) the admin PIN that unlocks the kiosk's admin panel on this PC (Ctrl+Alt+Shift+A).
.DESCRIPTION
    The PIN is stored as a slow salted hash (PBKDF2-SHA256, 600 000 iterations: the format of
    OfflineSessionStore.HashPassword) in C:\ProgramData\ClubShell\secure\admin-pin. That folder is readable by SYSTEM and
    Administrators only, so a player cannot copy the hash and guess the PIN offline. shell.json → kiosk.adminPinHash is
    no longer read (players can read shell.json). The Agent reads the file at every unlock attempt: no restart needed.
    Use 6 to 12 digits and do not share the PIN with players. Run as administrator.
.PARAMETER Remove
    Deletes the PIN: the admin panel can no longer be unlocked on this PC.
.EXAMPLE
    .\set-admin-pin.ps1
#>
[CmdletBinding()]
param([switch] $Remove)

$ErrorActionPreference = 'Stop'
$secureDir = Join-Path $env:ProgramData 'ClubShell\secure'
$pinFile = Join-Path $secureDir 'admin-pin'
$iterations = 600000

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Запустите от имени администратора.' -ForegroundColor Red
    exit 1
}
if (-not (Test-Path -LiteralPath $secureDir)) {
    Write-Host 'ClubShell не установлен на этом ПК (нет папки secure).' -ForegroundColor Red
    exit 1
}

if ($Remove) {
    if (Test-Path -LiteralPath $pinFile) { Remove-Item -LiteralPath $pinFile -Force }
    Write-Host 'PIN администратора удалён: панель администратора на этом ПК не откроется.' -ForegroundColor Yellow
    exit 0
}

function Read-Pin([string] $Prompt) {
    $secure = Read-Host -Prompt $Prompt -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

$pin = Read-Pin 'Новый PIN администратора (6-12 цифр)'
if ($pin -notmatch '^[0-9]{6,12}$') {
    Write-Host 'PIN должен состоять из 6-12 цифр.' -ForegroundColor Red
    exit 1
}
if ((Read-Pin 'Повторите PIN') -ne $pin) {
    Write-Host 'PIN не совпадает.' -ForegroundColor Red
    exit 1
}

$salt = New-Object byte[] 16
$rng = New-Object Security.Cryptography.RNGCryptoServiceProvider
try { $rng.GetBytes($salt) } finally { $rng.Dispose() }
$kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes(([Text.Encoding]::UTF8.GetBytes($pin)), $salt, $iterations, [Security.Cryptography.HashAlgorithmName]::SHA256)
try { $hash = $kdf.GetBytes(32) } finally { $kdf.Dispose() }
$pin = $null

$encoded = 'pbkdf2$' + $iterations + '$' + [Convert]::ToBase64String($salt) + '$' + [Convert]::ToBase64String($hash)
# ASCII without BOM; the file inherits the secure folder's SYSTEM + Administrators ACL.
[IO.File]::WriteAllText($pinFile, $encoded, (New-Object Text.ASCIIEncoding))
Write-Host 'PIN администратора сохранён. Панель: Ctrl+Alt+Shift+A на экране оболочки.' -ForegroundColor Green
