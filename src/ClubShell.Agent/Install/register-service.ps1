#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Creates, updates, or removes the 'ClubShellAgent' Windows service.
.DESCRIPTION
    Registers ClubShellAgent.exe as a LocalSystem service (start = delayed-auto), grants it the privileges the
    Agent needs to run processes in the interactive session and control power/registry/storage, sets recovery
    (restart 5s / 10s / 30s, reset after 1 day), a dependency on the workstation and TCP/IP stacks, and an
    Application event-log source. Idempotent: re-running reconfigures the existing service. See ARCHITECTURE.md section 2.
.PARAMETER InstallDir
    Directory that contains ClubShellAgent.exe. Default: C:\Program Files\ClubShell\Agent.
.PARAMETER BinaryName
    Service binary. Default: ClubShellAgent.exe.
.PARAMETER Uninstall
    Stop and delete the service and remove the event-log source instead of creating it.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $InstallDir = 'C:\Program Files\ClubShell\Agent',
    [string] $BinaryName = 'ClubShellAgent.exe',
    [switch] $Uninstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ServiceName  = 'ClubShellAgent'
$DisplayName  = 'ClubShell Agent'
$Description   = 'ClubShell client agent: kiosk session control, game launching, policy enforcement and server sync.'
$EventSource   = 'ClubShellAgent'
$Privileges    = @(
    'SeTcbPrivilege',
    'SeAssignPrimaryTokenPrivilege',
    'SeIncreaseQuotaPrivilege',
    'SeShutdownPrivilege',
    'SeDebugPrivilege',
    'SeBackupPrivilege',
    'SeRestorePrivilege'
) -join '/'

function Invoke-Sc {
    param([Parameter(Mandatory)][string[]] $Arguments, [switch] $IgnoreFailure)
    # No 2>&1: in Windows PowerShell 5.1 that wraps native stderr as an ErrorRecord and trips $ErrorActionPreference=Stop.
    $output = & sc.exe @Arguments
    if ($LASTEXITCODE -ne 0 -and -not $IgnoreFailure) {
        throw "sc.exe $($Arguments -join ' ') failed ($LASTEXITCODE): $output"
    }
    return $output
}

function Test-Service {
    return $null -ne (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)
}

if ($Uninstall) {
    if (Test-Service) {
        if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop and delete service')) {
            Invoke-Sc -Arguments @('stop', $ServiceName) -IgnoreFailure | Out-Null
            Start-Sleep -Seconds 1
            Invoke-Sc -Arguments @('delete', $ServiceName) | Out-Null
            Write-Host "Service '$ServiceName' removed."
        }
    } else {
        Write-Host "Service '$ServiceName' is not installed."
    }

    if ([System.Diagnostics.EventLog]::SourceExists($EventSource)) {
        if ($PSCmdlet.ShouldProcess($EventSource, 'Remove event-log source')) {
            Remove-EventLog -Source $EventSource
            Write-Host "Event-log source '$EventSource' removed."
        }
    }
    return
}

$exePath = Join-Path $InstallDir $BinaryName
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Service binary not found: $exePath"
}

if (-not $PSCmdlet.ShouldProcess($ServiceName, 'Register/update service')) {
    return
}

if (Test-Service) {
    Write-Host "Updating existing service '$ServiceName'."
    Invoke-Sc -Arguments @('config', $ServiceName, 'binPath=', "`"$exePath`"", 'start=', 'delayed-auto', 'obj=', 'LocalSystem', 'DisplayName=', $DisplayName) | Out-Null
} else {
    Write-Host "Creating service '$ServiceName'."
    Invoke-Sc -Arguments @('create', $ServiceName, 'binPath=', "`"$exePath`"", 'start=', 'delayed-auto', 'obj=', 'LocalSystem', 'DisplayName=', $DisplayName) | Out-Null
}

Invoke-Sc -Arguments @('description', $ServiceName, $Description) | Out-Null
Invoke-Sc -Arguments @('config', $ServiceName, 'depend=', 'LanmanWorkstation/Tcpip') | Out-Null
Invoke-Sc -Arguments @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/10000/restart/30000') | Out-Null
Invoke-Sc -Arguments @('failureflag', $ServiceName, '1') -IgnoreFailure | Out-Null
Invoke-Sc -Arguments @('privs', $ServiceName, $Privileges) | Out-Null

if (-not [System.Diagnostics.EventLog]::SourceExists($EventSource)) {
    New-EventLog -LogName 'Application' -Source $EventSource
    Write-Host "Event-log source '$EventSource' created."
}

Write-Host "Service '$ServiceName' registered (LocalSystem, start = delayed-auto, recovery configured)."
