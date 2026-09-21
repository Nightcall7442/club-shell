#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the ClubShell Agent: copies files, prepares C:\ProgramData\ClubShell with ACLs, writes agent.json, and
    registers + starts the service. Idempotent; supports -WhatIf.
.DESCRIPTION
    Copies the published payload from -SourceDir into -InstallDir, creates the ProgramData tree (config, cache, logs,
    secure, themes) with the ACLs from ARCHITECTURE.md section 9 (SYSTEM/Administrators full; the kiosk user reads config and
    writes cache/logs; secure/ is SYSTEM-only), seeds agent.json from the shipped defaults with the supplied server URL,
    club API key and kiosk user name (existing agent.json is preserved unless -Force), then invokes
    register-service.ps1 and starts the service.
.PARAMETER InstallDir
    Install location for ClubShellAgent.exe. Default: C:\Program Files\ClubShell\Agent.
.PARAMETER SourceDir
    Directory containing the published ClubShellAgent.exe (and its config\ folder). Default: this script's folder.
.PARAMETER ServerUrl
    Central server base URL, e.g. https://club.example.uz. baseUrl/wsUrl are derived from it.
.PARAMETER ClubApiKey
    Club registration key written to server.clubApiKey.
.PARAMETER KioskUser
    Kiosk account name written to shell.kioskUser.name. Default: club.
.PARAMETER Force
    Overwrite an existing agent.json.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $InstallDir = 'C:\Program Files\ClubShell\Agent',
    [string] $SourceDir  = $PSScriptRoot,
    [string] $ServerUrl,
    [string] $ClubApiKey,
    [string] $KioskUser = 'club',
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ProgramData = Join-Path $env:ProgramData 'ClubShell'
$exeName     = 'ClubShellAgent.exe'

# Locate the published payload (script may live in an Install\ subfolder next to the exe).
$source = $SourceDir
if (-not (Test-Path -LiteralPath (Join-Path $source $exeName))) {
    $parent = Split-Path -Parent $source
    if ($parent -and (Test-Path -LiteralPath (Join-Path $parent $exeName))) {
        $source = $parent
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $source $exeName))) {
    throw "Could not find $exeName under '$SourceDir'. Publish the Agent first, or pass -SourceDir."
}

function New-DirectorySafe {
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        if ($PSCmdlet.ShouldProcess($Path, 'Create directory')) {
            New-Item -ItemType Directory -Path $Path -Force | Out-Null
        }
    }
}

function Grant-Path {
    param([string] $Path, [string[]] $Grants, [switch] $ResetInheritance)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    if (-not $PSCmdlet.ShouldProcess($Path, "Set ACL ($($Grants -join ', '))")) { return }
    if ($ResetInheritance) {
        $global:LASTEXITCODE = 0
        & icacls.exe $Path /inheritance:r | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "icacls $Path /inheritance:r failed with exit code $LASTEXITCODE" }
    }
    foreach ($grant in $Grants) {
        $global:LASTEXITCODE = 0
        & icacls.exe $Path /grant "$grant" /t /c /q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "icacls $Path /grant $grant failed with exit code $LASTEXITCODE" }
    }
}

# 1. Files -------------------------------------------------------------------------------------
New-DirectorySafe -Path $InstallDir
if ($PSCmdlet.ShouldProcess($InstallDir, "Copy payload from '$source'")) {
    Copy-Item -Path (Join-Path $source '*') -Destination $InstallDir -Recurse -Force
    Write-Host "Copied Agent files to $InstallDir."
}

# 2. ProgramData tree + ACLs -------------------------------------------------------------------
New-DirectorySafe -Path $ProgramData
foreach ($sub in 'config', 'cache', 'cache\media', 'cache\updates', 'logs', 'secure', 'themes', 'pending-update') {
    New-DirectorySafe -Path (Join-Path $ProgramData $sub)
}

$system = '*S-1-5-18'          # NT AUTHORITY\SYSTEM
$admins = '*S-1-5-32-544'      # BUILTIN\Administrators
$users  = '*S-1-5-32-545'      # BUILTIN\Users

# Root: SYSTEM/Administrators full, Users read.
Grant-Path -Path $ProgramData -ResetInheritance -Grants @("$($system):(OI)(CI)F", "$($admins):(OI)(CI)F", "$($users):(OI)(CI)RX")

# secure\: SYSTEM only (shell.token is granted to the kiosk user at runtime by the Agent).
Grant-Path -Path (Join-Path $ProgramData 'secure') -ResetInheritance -Grants @("$($system):(OI)(CI)F", "$($admins):(OI)(CI)F")

# Kiosk user: read config/themes, write cache/logs.
$kioskSid = $null
try {
    $kioskSid = (New-Object System.Security.Principal.NTAccount($KioskUser)).Translate([System.Security.Principal.SecurityIdentifier]).Value
} catch {
    Write-Warning "Kiosk user '$KioskUser' does not exist yet; the Agent will create it and re-apply the token ACL. Skipping kiosk grants."
}
if ($kioskSid) {
    Grant-Path -Path (Join-Path $ProgramData 'config') -Grants @("*$($kioskSid):(OI)(CI)RX")
    Grant-Path -Path (Join-Path $ProgramData 'themes') -Grants @("*$($kioskSid):(OI)(CI)RX")
    Grant-Path -Path (Join-Path $ProgramData 'cache\media') -Grants @("*$($kioskSid):(OI)(CI)RX")
    Grant-Path -Path (Join-Path $ProgramData 'logs') -Grants @("*$($kioskSid):(OI)(CI)M")
}

# 3. agent.json --------------------------------------------------------------------------------
$agentJson = Join-Path $ProgramData 'agent.json'
$defaults  = Join-Path $source 'config\agent.default.json'
if ((Test-Path -LiteralPath $agentJson) -and -not $Force) {
    Write-Host "Existing agent.json preserved (use -Force to overwrite)."
} elseif (Test-Path -LiteralPath $defaults) {
    if ($PSCmdlet.ShouldProcess($agentJson, 'Write agent.json from defaults')) {
        $config = Get-Content -LiteralPath $defaults -Raw | ConvertFrom-Json
        if ($ServerUrl) {
            $apiBase = $ServerUrl.TrimEnd('/')
            if ($apiBase -notmatch '/api/v\d+$') { $apiBase = "$apiBase/api/v1" }
            $wsBase = ($apiBase -replace '^http', 'ws') -replace '/api/v\d+$', '/ws/agent'
            $config.server.baseUrl = $apiBase
            $config.server.wsUrl   = $wsBase
        }
        if ($PSBoundParameters.ContainsKey('ClubApiKey')) {
            $config.server.clubApiKey = $ClubApiKey
        }
        if ($config.shell -and $config.shell.kioskUser) {
            $config.shell.kioskUser.name = $KioskUser
        }
        ($config | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath $agentJson -Encoding utf8
        Write-Host "Wrote $agentJson."
    }
} else {
    Write-Warning "Shipped defaults not found at '$defaults'; the Agent will create agent.json from its built-in defaults on first run."
}

# 4. Service -----------------------------------------------------------------------------------
$register = Join-Path $PSScriptRoot 'register-service.ps1'
if (-not (Test-Path -LiteralPath $register)) {
    throw "register-service.ps1 not found next to install.ps1."
}
$whatIf = if ($WhatIfPreference) { $true } else { $false }
& $register -InstallDir $InstallDir -WhatIf:$whatIf

if ($PSCmdlet.ShouldProcess('ClubShellAgent', 'Start service')) {
    Start-Service -Name 'ClubShellAgent'
    Start-Sleep -Seconds 2
    Get-Service -Name 'ClubShellAgent' | Format-Table -AutoSize Name, Status, StartType
}

Write-Host "ClubShell Agent installation complete."
