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

Add "=== ClubShell diagnose $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') on $env:COMPUTERNAME"

$svc = Get-Service ClubShellAgent -ErrorAction SilentlyContinue
if ($svc) {
    $proc = Get-CimInstance Win32_Service -Filter "Name='ClubShellAgent'"
    Add "Service: $($svc.Status), start type $($svc.StartType), process id $($proc.ProcessId)"
} else {
    Add 'Service: NOT INSTALLED'
}
$agentProc = Get-Process ClubShellAgent -ErrorAction SilentlyContinue
if ($agentProc) { Add "Agent process: running since $($agentProc.StartTime.ToString('HH:mm:ss')), CPU $([math]::Round($agentProc.CPU,1)) s" }
Add "Kiosk user 'club': $([bool](Get-LocalUser -Name club -ErrorAction SilentlyContinue))"
Add "agent.json present: $(Test-Path (Join-Path $data 'agent.json'))"

Add ''
Add '--- Agent log (newest last)'
$log = Get-ChildItem -Path (Join-Path $data 'logs') -Filter 'agent-*.json' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $log) {
    Add 'no agent log yet'
} else {
    foreach ($line in Get-Content -LiteralPath $log.FullName -Tail $Lines -ErrorAction SilentlyContinue) {
        try { $e = $line | ConvertFrom-Json } catch { continue }
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
