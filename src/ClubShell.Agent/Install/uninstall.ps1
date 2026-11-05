#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Uninstalls the ClubShell Agent: stops and removes the service, restores explorer.exe as the shell, clears
    auto-logon, turns USB storage back on, removes ClubShell firewall rules and the hosts-file block, and optionally
    purges data and the kiosk user.
.DESCRIPTION
    Reverses install.ps1 / the Agent's runtime policy. Safe and logged; supports -WhatIf. The Agent normally restores
    the shell and auto-logon when a policy is reverted, but this script re-asserts those in case the service is gone.
.PARAMETER KioskUser
    Kiosk account name whose per-user shell override is removed. Default: the one this PC has (HKLM\SOFTWARE\ClubShell\KioskUser,
    then agent.json), else player.
.PARAMETER PurgeData
    Remove C:\ProgramData\ClubShell (configuration, cache, logs, secrets).
.PARAMETER RemoveKioskUser
    Delete the local kiosk account.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $KioskUser = '',
    [switch] $PurgeData,
    [switch] $RemoveKioskUser
)

# The kiosk this PC already has (SHELL_CHANGES 64): the name the installer stored, then agent.json; 'player' on a new PC.
if (-not $KioskUser) {
    try { $KioskUser = [string](Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\ClubShell' -Name KioskUser -ErrorAction Stop).KioskUser } catch { }
}
if (-not $KioskUser) {
    try { $KioskUser = [string](Get-Content -LiteralPath (Join-Path $env:ProgramData 'ClubShell\agent.json') -Raw -ErrorAction Stop | ConvertFrom-Json).shell.kioskUser.name } catch { }
}
if (-not $KioskUser) { $KioskUser = 'player' }

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
Remove-RegistryValueSafe -Path $WinlogonKey -Name 'ForceAutoLogon'

# The kiosk password auto-logon kept as the LSA secret DefaultPassword (SHELL_CHANGES 67): storing nothing deletes it.
if ($PSCmdlet.ShouldProcess('LSA secret DefaultPassword', 'Delete')) {
    Add-Type -ErrorAction Stop -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ClubShellLsaSecret {
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }
    [DllImport("advapi32.dll")] private static extern uint LsaOpenPolicy(IntPtr systemName, ref ObjectAttributes attributes, uint access, out IntPtr policy);
    [DllImport("advapi32.dll")] private static extern uint LsaStorePrivateData(IntPtr policy, ref UnicodeString keyName, IntPtr privateData);
    [DllImport("advapi32.dll")] private static extern uint LsaClose(IntPtr policy);
    [DllImport("advapi32.dll")] private static extern int LsaNtStatusToWinError(uint status);
    // Win32 error code; 0 = deleted, 2 = there was none.
    public static int Delete(string name) {
        var attributes = new ObjectAttributes();
        attributes.Length = Marshal.SizeOf(typeof(ObjectAttributes));
        IntPtr policy;
        uint status = LsaOpenPolicy(IntPtr.Zero, ref attributes, 0x20, out policy); // POLICY_CREATE_SECRET, as the Agent stores it
        if (status != 0) { return LsaNtStatusToWinError(status); }
        var key = new UnicodeString();
        key.Buffer = Marshal.StringToHGlobalUni(name);
        key.Length = (ushort)(name.Length * 2);
        key.MaximumLength = (ushort)((name.Length + 1) * 2);
        try { status = LsaStorePrivateData(policy, ref key, IntPtr.Zero); return status == 0 ? 0 : LsaNtStatusToWinError(status); }
        finally { Marshal.FreeHGlobal(key.Buffer); LsaClose(policy); }
    }
}
'@
    $lsaResult = [ClubShellLsaSecret]::Delete('DefaultPassword')
    if ($lsaResult -notin 0, 2) { Write-Warning "LSA secret DefaultPassword not deleted (error $lsaResult)." }
}

# The kiosk's own hive (SHELL_CHANGES 67): the custom shell (Winlogon\Shell, Policies\System\Shell) and the Explorer
# restrictions, or the kiosk would start the removed clubshell-shell.exe at its next logon. Loaded for the purpose when
# the kiosk is not signed in.
function Clear-KioskHive {
    param([string] $Root)
    Remove-RegistryValueSafe -Path "$Root\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name 'Shell'
    foreach ($section in 'System', 'Explorer') {
        $key = "$Root\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\$section"
        if ((Test-Path -LiteralPath $key) -and $PSCmdlet.ShouldProcess($key, 'Remove the kiosk restrictions')) {
            Remove-Item -LiteralPath $key -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

$kioskSid = $null
try { $kioskSid = (New-Object System.Security.Principal.NTAccount($KioskUser)).Translate([System.Security.Principal.SecurityIdentifier]).Value } catch { }
if (-not $kioskSid) {
    Write-Verbose "Kiosk user '$KioskUser' not found; its hive is left alone."
} elseif (Test-Path -LiteralPath "Registry::HKEY_USERS\$kioskSid") {
    Clear-KioskHive -Root "Registry::HKEY_USERS\$kioskSid"
} else {
    $profilePath = $null
    try { $profilePath = [string](Get-ItemProperty -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$kioskSid" -Name ProfileImagePath -ErrorAction Stop).ProfileImagePath } catch { }
    $ntuser = if ($profilePath) { Join-Path ([Environment]::ExpandEnvironmentVariables($profilePath)) 'NTUSER.DAT' } else { $null }
    if ($ntuser -and (Test-Path -LiteralPath $ntuser) -and $PSCmdlet.ShouldProcess($ntuser, 'Load and clean the kiosk hive')) {
        $mount = 'HKU\ClubShellUninstall'
        $global:LASTEXITCODE = 0
        & reg.exe load $mount $ntuser | Out-Null
        if ($LASTEXITCODE -eq 0) {
            try { Clear-KioskHive -Root 'Registry::HKEY_USERS\ClubShellUninstall' }
            finally {
                [GC]::Collect()
                [GC]::WaitForPendingFinalizers()
                & reg.exe unload $mount | Out-Null
            }
        } else {
            Write-Warning "Kiosk hive $ntuser could not be loaded (reg load exit code $LASTEXITCODE); its shell override stays."
        }
    }
}

# 2b. USB storage back on (SHELL_CHANGES 28): the policy's USBSTOR Start=4 is machine-wide and outlives the service. ----
$usbStor = 'HKLM:\SYSTEM\CurrentControlSet\Services\USBSTOR'
if ((Test-Path -LiteralPath $usbStor) -and $PSCmdlet.ShouldProcess('USBSTOR', 'Allow USB storage (Start=3)')) {
    Set-ItemProperty -LiteralPath $usbStor -Name 'Start' -Value 3 -Type DWord -ErrorAction SilentlyContinue
}
# The HID install block the policy may have set (DenyDeviceClasses\1 = HID class); left alone when it is someone else's.
$deviceInstall = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions'
$hidClass = '{745a17a0-74d3-11d0-b6fe-00a0c9f57da2}'
if ((Get-ItemProperty -LiteralPath "$deviceInstall\DenyDeviceClasses" -Name '1' -ErrorAction SilentlyContinue).'1' -eq $hidClass) {
    Remove-RegistryValueSafe -Path "$deviceInstall\DenyDeviceClasses" -Name '1'
    Set-ItemProperty -LiteralPath $deviceInstall -Name 'DenyDeviceClasses' -Value 0 -Type DWord -ErrorAction SilentlyContinue
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
