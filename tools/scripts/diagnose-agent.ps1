<#
.SYNOPSIS
    Short report of the ClubShell agent on this PC (pilot support): service state, the agent's own last log lines,
    Windows errors about the service and the result of the last install. Saved to the Desktop as clubshell-diagnose.txt.
.DESCRIPTION
    Read-only: changes nothing. The agent log is Serilog compact JSON; messages are rendered from "@mt" and the event's
    properties. Values that look like keys or tokens are not printed (the agent's logging already redacts credentials).
#>
[CmdletBinding()]
param([int] $Lines = 25)

$ErrorActionPreference = 'Continue'
$data = Join-Path $env:ProgramData 'ClubShell'
$out = New-Object System.Collections.Generic.List[string]
function Add([string] $text) { $out.Add($text); Write-Host $text }

# ConvertFrom-Json (Windows PowerShell 5.1) rejects keys that differ only in case, and agent events carry "Version"
# (a message property) next to "version" (the agent version): parse case-sensitively and keep the first such key.
Add-Type -AssemblyName System.Web.Extensions
$jsonReader = New-Object System.Web.Script.Serialization.JavaScriptSerializer
function ConvertFrom-LogLine([string] $Line) {
    try { $map = $jsonReader.DeserializeObject($Line) } catch { return $null }
    if ($map -isnot [System.Collections.IDictionary]) { return $null }
    $entry = New-Object psobject
    foreach ($key in $map.Keys) {
        if (-not $entry.PSObject.Properties[$key]) { Add-Member -InputObject $entry -NotePropertyName $key -NotePropertyValue $map[$key] }
    }
    return $entry
}

Add "=== ClubShell diagnose $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') on $env:COMPUTERNAME"

$svc = Get-Service ClubShellAgent -ErrorAction SilentlyContinue
if ($svc) {
    $proc = Get-CimInstance Win32_Service -Filter "Name='ClubShellAgent'"
    $delayed = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\ClubShellAgent' -ErrorAction SilentlyContinue).DelayedAutostart
    Add "Service: $($svc.Status), start type $($svc.StartType)$(if ($delayed -eq 1) { ' (delayed)' }), process id $($proc.ProcessId)"
} else {
    Add 'Service: NOT INSTALLED'
}
$agentProc = Get-Process ClubShellAgent -ErrorAction SilentlyContinue
if ($agentProc) { Add "Agent process: running since $($agentProc.StartTime.ToString('HH:mm:ss')), CPU $([math]::Round($agentProc.CPU,1)) s" }
Add "Kiosk user 'club': $([bool](Get-LocalUser -Name club -ErrorAction SilentlyContinue))"
Add "agent.json present: $(Test-Path (Join-Path $data 'agent.json'))"

Add ''
Add '--- Kiosk account and auto-logon'
$club = Get-LocalUser -Name club -ErrorAction SilentlyContinue
if ($club) {
    $sid = $club.SID.Value
    $profilePath = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid" -ErrorAction SilentlyContinue).ProfileImagePath
    Add "club: enabled $($club.Enabled), SID $sid"
    Add "club profile: $(if ($profilePath) { "$profilePath (NTUSER.DAT present: $(Test-Path (Join-Path ([Environment]::ExpandEnvironmentVariables($profilePath)) 'NTUSER.DAT')))" } else { 'none' })"
}
$winlogon = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -ErrorAction SilentlyContinue
Add "Winlogon: AutoAdminLogon=$($winlogon.AutoAdminLogon) DefaultUserName=$($winlogon.DefaultUserName) DefaultDomainName=$($winlogon.DefaultDomainName) Shell=$($winlogon.Shell) DefaultPassword in registry=$([bool]($winlogon.PSObject.Properties.Name -contains 'DefaultPassword'))"
$passwordless = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device' -ErrorAction SilentlyContinue).DevicePasswordLessBuildVersion
Add "Windows Hello passwordless (2 blocks auto-logon): $passwordless"
Add "Sessions: $((query session 2>$null) -join ' | ')"
Add "Shell process: $(@(Get-Process clubshell-shell -ErrorAction SilentlyContinue | ForEach-Object { "pid $($_.Id) session $($_.SessionId)" }) -join ', ')"

$keyLog = Get-ChildItem -Path (Join-Path $data 'logs') -Filter 'agent-*.json' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
function Add-LogLines([string] $Pattern, [int] $Count) {
    if (-not $keyLog) { Add 'no agent log yet'; return }
    foreach ($line in Get-Content -LiteralPath $keyLog.FullName -Encoding UTF8 -ErrorAction SilentlyContinue | Select-String -Pattern $Pattern | Select-Object -Last $Count) {
        $e = ConvertFrom-LogLine $line.Line
        if (-not $e) { continue }
        $msg = [string] $e.'@mt'
        foreach ($p in $e.PSObject.Properties) { if ($p.Name -notlike '@*') { $msg = $msg.Replace('{' + $p.Name + '}', [string] $p.Value) } }
        $time = ([string] $e.'@t'); if ($time.Length -ge 19) { $time = $time.Substring(11, 8) }
        $x = if ($e.'@x') { ' | ' + (([string] $e.'@x') -split "`n")[0].Trim() } else { '' }
        Add ("{0} {1}{2}" -f $time, $msg, $x)
    }
}

Add ''
Add '--- Agent log: kiosk, policy and shell messages'
Add-LogLines 'Kiosk|kiosk|Profile|profile|Policy|policy|shell|Shell|auto-logon|Auto-logon|logon|Registered|[Pp]ipe|auth\.hello|IPC|[Ss]afe mode|Watchdog' 60

Add ''
Add '--- Games: Steam folder and the last launches / closes'
$steam = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' -ErrorAction SilentlyContinue).InstallPath
if (-not $steam) { $steam = (Get-ItemProperty 'HKLM:\SOFTWARE\Valve\Steam' -ErrorAction SilentlyContinue).InstallPath }
if ($steam -and (Test-Path -LiteralPath $steam)) {
    $drive = [System.IO.DriveInfo]::new([System.IO.Path]::GetPathRoot($steam))
    $usersAce = @(icacls $steam 2>$null | Select-String -Pattern 'BUILTIN\\|S-1-5-32-545' | ForEach-Object { $_.Line.Trim() })
    Add "Steam: $steam (drive $($drive.Name) $($drive.DriveType)); Users rights: $(if ($usersAce) { $usersAce -join ' ; ' } else { 'none' })"
} else {
    Add 'Steam: not installed (no InstallPath in the registry)'
}
Add-LogLines 'Launching |running as pid|Tracking |handed over|Killed |Kill of|killed |cancel|Cancel|Steam folder|game process|Launch of|exited with|Game process|launch\.' 40

Add ''
Add '--- Agent log (newest last)'
$log = Get-ChildItem -Path (Join-Path $data 'logs') -Filter 'agent-*.json' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $log) {
    Add 'no agent log yet'
} else {
    foreach ($line in Get-Content -LiteralPath $log.FullName -Tail $Lines -Encoding UTF8 -ErrorAction SilentlyContinue) {
        $e = ConvertFrom-LogLine $line
        if (-not $e) { continue }
        $msg = [string] $e.'@mt'
        foreach ($p in $e.PSObject.Properties) {
            if ($p.Name -notlike '@*') { $msg = $msg.Replace('{' + $p.Name + '}', [string] $p.Value) }
        }
        $time = ([string] $e.'@t')
        if ($time.Length -ge 19) { $time = $time.Substring(11, 8) }
        $level = if ($e.'@l') { [string] $e.'@l' } else { 'Information' }
        $x = if ($e.'@x') { ' | ' + (([string] $e.'@x') -split "`n")[0].Trim() } else { '' }
        Add ("{0} {1,-11} {2}{3}" -f $time, $level, $msg, $x)
    }
}

Add ''
Add '--- Windows events (last 30 min)'
$since = (Get-Date).AddMinutes(-30)
function Events([hashtable] $filter) {
    # A provider that never logged on this PC makes Get-WinEvent throw; that just means "no events".
    try { Get-WinEvent -FilterHashtable $filter -ErrorAction Stop } catch { @() }
}
$events = @()
$events += Events @{ LogName = 'Application'; ProviderName = 'ClubShellAgent'; StartTime = $since }
$events += Events @{ LogName = 'Application'; ProviderName = '.NET Runtime'; StartTime = $since }
$events += Events @{ LogName = 'System'; ProviderName = 'Service Control Manager'; StartTime = $since } | Where-Object { $_.Message -like '*ClubShell*' }
$events += Events @{ LogName = 'Application'; ProviderName = 'MsiInstaller'; StartTime = $since } | Where-Object { $_.Message -like '*ClubShell*' }
if (-not $events) { Add 'none' }
foreach ($ev in $events | Sort-Object TimeCreated) {
    $text = ($ev.Message -replace '\s+', ' ')
    Add ("{0} {1} {2}: {3}" -f $ev.TimeCreated.ToString('HH:mm:ss'), $ev.ProviderName, $ev.Id, $text.Substring(0, [Math]::Min(220, $text.Length)))
}

$file = Join-Path ([Environment]::GetFolderPath('Desktop')) 'clubshell-diagnose.txt'
$out | Set-Content -LiteralPath $file -Encoding UTF8
Write-Host ''
Write-Host "Saved: $file" -ForegroundColor Green
