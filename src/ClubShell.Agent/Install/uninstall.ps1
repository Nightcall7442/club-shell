#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Uninstalls the ClubShell Agent: stops and removes the service, restores explorer.exe as the shell, clears
    auto-logon, removes ClubShell firewall rules and the hosts-file block, and optionally purges data and the kiosk user.
.DESCRIPTION
    Reverses install.ps1 / the Agent's runtime policy. Safe and logged; supports -WhatIf. The Agent normally restores
    the shell and auto-logon when a policy is reverted, but this script re-asserts those in case the service is gone.
.PARAMETER KioskUser
    Kiosk account name whose per-user shell override is removed. Default: club.
.PARAMETER PurgeData
    Remove C:\ProgramData\ClubShell (configuration, cache, logs, secrets).
.PARAMETER RemoveKioskUser
    Delete the local kiosk account.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $KioskUser = 'club',
    [switch] $PurgeData,
    [switch] $RemoveKioskUser
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ServiceName  = 'ClubShellAgent'
$ProgramData  = Join-Path $env:ProgramData 'ClubShell'
$WinlogonKey  = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$HostsPath    = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'

function Remove-RegistryValueSafe {
    param([string] $Path, [string] $Name)
    if ((Test-Path -LiteralPath $Path) -and ($null -ne (Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction SilentlyContinue))) {
        if ($PSCmdlet.ShouldProcess("$Path\$Name", 'Remove registry value')) {
            Remove-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction SilentlyContinue
        }
    }
}

# 1. Stop + delete the service (delegated to register-service.ps1 for the full teardown). ------
$register = Join-Path $PSScriptRoot 'register-service.ps1'
if (Test-Path -LiteralPath $register) {
    & $register -Uninstall -WhatIf:([bool]$WhatIfPreference)
} elseif (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop and delete service')) {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        $global:LASTEXITCODE = 0
        & sc.exe delete $ServiceName | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning "sc.exe delete $ServiceName failed with exit code $LASTEXITCODE" }
    }
}

# 2. Restore explorer.exe as the shell and clear auto-logon. -----------------------------------
if ($PSCmdlet.ShouldProcess('Winlogon', 'Clear auto-logon')) {
    Set-ItemProperty -LiteralPath $WinlogonKey -Name 'AutoAdminLogon' -Value '0' -ErrorAction SilentlyContinue
}
Remove-RegistryValueSafe -Path $WinlogonKey -Name 'DefaultUserName'
Remove-RegistryValueSafe -Path $WinlogonKey -Name 'DefaultPassword'
Remove-RegistryValueSafe -Path $WinlogonKey -Name 'DefaultDomainName'
Remove-RegistryValueSafe -Path $WinlogonKey -Name 'AutoLogonCount'

# Per-user custom shell (reverts to the default explorer.exe). Best effort: only when the hive is loaded.
try {
    $kioskSid = (New-Object System.Security.Principal.NTAccount($KioskUser)).Translate([System.Security.Principal.SecurityIdentifier]).Value
    $userWinlogon = "Registry::HKEY_USERS\$kioskSid\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
    Remove-RegistryValueSafe -Path $userWinlogon -Name 'Shell'
} catch {
    Write-Verbose "Kiosk user '$KioskUser' shell override not reverted (user missing or hive not loaded)."
}

# 3. Firewall rules ('ClubShell-*'). -----------------------------------------------------------
$rules = Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'ClubShell-*' -or $_.Name -like 'ClubShell-*' }
foreach ($rule in $rules) {
    if ($PSCmdlet.ShouldProcess($rule.DisplayName, 'Remove firewall rule')) {
        Remove-NetFirewallRule -Name $rule.Name -ErrorAction SilentlyContinue
    }
}
Write-Host "Removed $((@($rules)).Count) ClubShell firewall rule(s)."

# 4. Hosts-file block ('# ClubShell-BEGIN' .. '# ClubShell-END'). ------------------------------
if (Test-Path -LiteralPath $HostsPath) {
    $lines = Get-Content -LiteralPath $HostsPath
    $begin = ($lines | Select-String -SimpleMatch '# ClubShell-BEGIN' | Select-Object -First 1)
    if ($begin -and $PSCmdlet.ShouldProcess($HostsPath, 'Remove ClubShell block')) {
        $kept = New-Object System.Collections.Generic.List[string]
        $skip = $false
        foreach ($line in $lines) {
            if ($line -match '^\s*#\s*ClubShell-BEGIN') { $skip = $true; continue }
            if ($line -match '^\s*#\s*ClubShell-END')   { $skip = $false; continue }
            if (-not $skip) { $kept.Add($line) }
        }
        Set-Content -LiteralPath $HostsPath -Value $kept -Encoding ascii
        Write-Host "Removed the ClubShell hosts-file block."
    }
}

# 5. Optional data + kiosk user. ---------------------------------------------------------------
if ($PurgeData -and (Test-Path -LiteralPath $ProgramData)) {
    if ($PSCmdlet.ShouldProcess($ProgramData, 'Remove ProgramData tree')) {
        Remove-Item -LiteralPath $ProgramData -Recurse -Force
        Write-Host "Removed $ProgramData."
    }
}

if ($RemoveKioskUser) {
    $account = Get-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue
    if ($account -and $PSCmdlet.ShouldProcess($KioskUser, 'Remove local user')) {
        Remove-LocalUser -Name $KioskUser
        Write-Host "Removed kiosk user '$KioskUser'."
    }
}

Write-Host "ClubShell Agent uninstall complete."
