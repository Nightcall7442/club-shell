<#
.SYNOPSIS
    Restore drill: decrypts a nightly backup (.github/workflows/db-backup.yml) and restores it into a local PostgreSQL,
    then checks it the way the server's daily check does (every wallet's balance against its ledger).
.DESCRIPTION
    Download the artifact (GitHub -> Actions -> db-backup -> a run -> Artifacts), unzip it and pass the .dump.gpg file.
    Needs gpg (Git for Windows has it) and a PostgreSQL 18 client (pg_restore, psql) on PATH or in -PgBin. The target
    database is dropped and recreated: never point this at the club's server.
.PARAMETER Path
    The clubshell-<stamp>.dump.gpg file.
.PARAMETER Database
    Local database to restore into (default clubshell_restore).
.PARAMETER PgBin
    Folder with pg_restore.exe / psql.exe (default: %LOCALAPPDATA%\Programs\pgsql18\bin when it exists).
.PARAMETER Port
    Local PostgreSQL port (default 5433, the test instance).
.EXAMPLE
    .\server\scripts\restore-backup.ps1 -Path .\clubshell-20261003-2230.dump.gpg
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Path,
    [string] $Database = 'clubshell_restore',
    [string] $PgBin,
    [int] $Port = 5433,
    [string] $User = 'postgres'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Path)) { throw "Backup file not found: $Path" }
if (-not $PgBin) {
    $candidate = Join-Path $env:LOCALAPPDATA 'Programs\pgsql18\bin'
    if (Test-Path -LiteralPath $candidate) { $PgBin = $candidate }
}
$pgRestore = if ($PgBin) { Join-Path $PgBin 'pg_restore.exe' } else { 'pg_restore' }
$psql = if ($PgBin) { Join-Path $PgBin 'psql.exe' } else { 'psql' }
$gpg = (Get-Command gpg -ErrorAction SilentlyContinue).Source
if (-not $gpg) {
    $gitGpg = Join-Path $env:ProgramFiles 'Git\usr\bin\gpg.exe'
    if (Test-Path -LiteralPath $gitGpg) { $gpg = $gitGpg } else { throw 'gpg not found (install Git for Windows or Gpg4win).' }
}

$secure = Read-Host -Prompt 'Пароль резервной копии (BACKUP_PASSPHRASE)' -AsSecureString
$ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try { $passphrase = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }

$dump = Join-Path ([IO.Path]::GetTempPath()) ("clubshell-restore-" + [Guid]::NewGuid().ToString('N') + '.dump')
try {
    $passphrase | & $gpg --batch --yes --pinentry-mode loopback --passphrase-fd 0 --decrypt --output $dump $Path
    if ($LASTEXITCODE -ne 0) { throw 'gpg could not decrypt the backup (wrong passphrase?).' }
    $passphrase = $null

    & $psql -h localhost -p $Port -U $User -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS $Database;" -c "CREATE DATABASE $Database;" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not recreate the restore database.' }
    & $pgRestore -h localhost -p $Port -U $User -d $Database --no-owner --no-privileges --exit-on-error $dump
    if ($LASTEXITCODE -ne 0) { throw 'pg_restore failed.' }

    $check = @'
SELECT (SELECT count(*) FROM users) AS users,
       (SELECT count(*) FROM ledger_entries) AS ledger_rows,
       (SELECT count(*) FROM sessions) AS sessions,
       (SELECT count(*) FROM wallets w LEFT JOIN (SELECT user_id, sum(amount) AS total FROM ledger_entries GROUP BY user_id) l
          ON l.user_id = w.user_id WHERE w.main_balance <> coalesce(l.total, 0)) AS mismatched_wallets;
'@
    $result = & $psql -h localhost -p $Port -U $User -d $Database -At -F ' ' -c $check
    if ($LASTEXITCODE -ne 0) { throw 'The check query failed.' }
    $parts = "$result".Trim().Split(' ')
    Write-Host "Восстановлено: игроков $($parts[0]), записей журнала $($parts[1]), сеансов $($parts[2])."
    if ([int]$parts[3] -eq 0) {
        Write-Host 'OK: балансы всех кошельков сходятся с журналом.' -ForegroundColor Green
    } else {
        Write-Host "ВНИМАНИЕ: у $($parts[3]) кошельков баланс не сходится с журналом." -ForegroundColor Red
        exit 1
    }
} finally {
    if (Test-Path -LiteralPath $dump) { Remove-Item -LiteralPath $dump -Force }
}
