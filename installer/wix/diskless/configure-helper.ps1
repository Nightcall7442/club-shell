<#
.SYNOPSIS
    Writes %ProgramData%\ClubDiskless\helper.json for ClubDisklessHelper (docs/DISKLESS.md).
.DESCRIPTION
    Creates the helper's state directory with SYSTEM + Administrators only access (helper.json carries the club key)
    and writes { "Helper": { "ServerUrl", "ClubKey", "CaCertificatePath" } }; CaCertificatePath is left out when it is
    not given. An existing helper.json is kept unless -Force is passed.

    Run by ClubShell.msi (feature DisklessHelper: DISKLESSSERVERURL, DISKLESSCLUBKEY, DISKLESSCACERT) and by
    administrators pointing a PC at another club server. The service reads the file at start:
    Restart-Service ClubDisklessHelper after changing it.
.PARAMETER ServerUrl
    Club server the helper registers with, e.g. https://club-server (absolute http/https URL).
.PARAMETER ClubKey
    Club key the helper registers the machine with.
.PARAMETER CaCertificatePath
    PEM of the club CA the server certificate chains to, e.g. C:\ProgramData\ClubDiskless\club-ca.pem.
.PARAMETER DataDir
    Helper state directory. Default: %ProgramData%\ClubDiskless.
.PARAMETER Force
    Overwrite an existing helper.json.
.EXAMPLE
    & 'C:\Program Files\ClubDiskless\configure-helper.ps1' -ServerUrl https://club-server -ClubKey K3y -CaCertificatePath C:\ProgramData\ClubDiskless\club-ca.pem -Force
.OUTPUTS
    Exit code 0 on success (including "already configured"), non-zero on failure.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string] $ServerUrl,

    [Parameter(Mandatory = $true)]
    [string] $ClubKey,

    [string] $CaCertificatePath = '',

    [string] $DataDir = (Join-Path $env:ProgramData 'ClubDiskless'),

    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$url = $ServerUrl.Trim()
$uri = $null
if (-not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref] $uri) -or ($uri.Scheme -ne 'https' -and $uri.Scheme -ne 'http')) {
    throw "ServerUrl must be an absolute http(s) URL: $ServerUrl"
}
if ([string]::IsNullOrWhiteSpace($ClubKey)) {
    throw 'ClubKey is empty'
}

$configPath = Join-Path $DataDir 'helper.json'
if ((Test-Path -LiteralPath $configPath) -and -not $Force) {
    Write-Host "$configPath exists; left unchanged (-Force overwrites it)"
    exit 0
}

if (-not $PSCmdlet.ShouldProcess($configPath, 'write helper.json')) {
    exit 0
}

if (-not (Test-Path -LiteralPath $DataDir)) {
    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
}
# SYSTEM + Administrators only, inheritance cut (SIDs, so it works on any Windows display language).
& icacls.exe $DataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "icacls failed on $DataDir (exit code $LASTEXITCODE)"
}

$helper = [ordered]@{ ServerUrl = $url; ClubKey = $ClubKey }
if (-not [string]::IsNullOrWhiteSpace($CaCertificatePath)) {
    $helper['CaCertificatePath'] = $CaCertificatePath.Trim()
}
$json = ConvertTo-Json -InputObject ([ordered]@{ Helper = $helper }) -Depth 3
# UTF-8 without BOM: Windows PowerShell's Set-Content -Encoding UTF8 would prepend one.
[System.IO.File]::WriteAllText($configPath, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote $configPath"
