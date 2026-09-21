<#
.SYNOPSIS
    Developer loop: starts the mock central server, optionally the Agent (console mode) and the kiosk shell
    (`pnpm tauri dev`, or the Vite dev server in browser mock mode with -WebOnly), each in its own console window
    with the output tee'd to artifacts/logs. Ctrl+C (or q) stops every child process tree.
.DESCRIPTION
    Reads .env (KEY=VALUE, values already set in the environment win), checks prerequisites with install hints,
    then launches:

      mock    pnpm mock                                   http://localhost:<MockPort>  (health: /health)
      agent   dotnet run --project src/ClubShell.Agent -- --console --dev      (only with -Agent)
      shell   pnpm tauri dev                              real kiosk window over the Vite dev server
              pnpm --filter @clubshell/shell dev          (-WebOnly: VITE_MOCK=1, opens http://localhost:1420)

    Environment handed to the children: MOCK_SERVER_PORT, CLUBSHELL_SERVER_URL, CLUBSHELL_WS_URL, CLUBSHELL_DEV=1,
    the CLUBSHELL__server__baseUrl / CLUBSHELL__server__wsUrl overrides the Agent's SettingsLoader understands,
    VITE_MOCK and VITE_SERVER_URL. Logs: artifacts/logs/dev-<name>.log.
.PARAMETER WebOnly
    Run the UI in the browser (Vite + in-page Tauri mocks) instead of the Tauri window; no Rust toolchain needed.
.PARAMETER Agent
    Also run the Agent as a console process against the mock server (needs the .NET 8 SDK; elevation recommended
    because the Agent creates C:\ProgramData\ClubShell and the named pipe with ACLs).
.PARAMETER NoMock
    Do not start the mock server (one is already running, or CLUBSHELL_SERVER_URL points elsewhere).
.PARAMETER NoShell
    Do not start the shell (mock server / Agent only).
.PARAMETER MockPort
    Mock server port. Default: MOCK_SERVER_PORT from .env / environment, else 8080.
.PARAMETER ResetMock
    Pass --reset to the mock server (drops its persisted state).
.PARAMETER NoBrowser
    With -WebOnly, do not open the browser automatically.
.EXAMPLE
    .\tools\scripts\dev.ps1
    Mock server + `pnpm tauri dev`.
.EXAMPLE
    .\tools\scripts\dev.ps1 -WebOnly
    Mock server + Vite in mock mode, opens http://localhost:1420.
.EXAMPLE
    .\tools\scripts\dev.ps1 -Agent -NoShell
    Mock server + Agent console, no UI.
.OUTPUTS
    Exit code 0 after a clean shutdown, 1 when a child failed to start, 2 when a prerequisite is missing.
#>
[CmdletBinding()]
param(
    [switch] $WebOnly,
    [switch] $Agent,
    [switch] $NoMock,
    [switch] $NoShell,
    [ValidateRange(1, 65535)]
    [int] $MockPort = 0,
    [switch] $ResetMock,
    [switch] $NoBrowser
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$LogDir   = Join-Path $RepoRoot 'artifacts\logs'

function Write-Step { param([Parameter(Mandatory)][string] $Message) Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Write-Ok   { param([Parameter(Mandatory)][string] $Message) Write-Host "    $Message" -ForegroundColor Green }
function Write-Warn { param([Parameter(Mandatory)][string] $Message) Write-Host "    WARNING: $Message" -ForegroundColor Yellow }

function Get-NativeOutput {
    <#
    .SYNOPSIS
        Runs a native command and returns @{ ExitCode; Output } without ever throwing (missing command => 127).
    #>
    param([Parameter(Mandatory)][string] $Command, [string[]] $Arguments = @(), [string] $WorkingDirectory = $RepoRoot)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    Push-Location -LiteralPath $WorkingDirectory
    try {
        $global:LASTEXITCODE = 0
        $lines = @(& $Command @Arguments 2>&1 | ForEach-Object { "$_" })
        return @{ ExitCode = $LASTEXITCODE; Output = $lines }
    } catch {
        return @{ ExitCode = 127; Output = @("$_") }
    } finally {
        Pop-Location
        $ErrorActionPreference = $previous
    }
}

function Import-DotEnv {
    <#
    .SYNOPSIS
        Loads KEY=VALUE pairs from <repo>/.env into the process environment; existing variables are not overridden.
    #>
    $path = Join-Path $RepoRoot '.env'
    if (-not (Test-Path -LiteralPath $path)) { return }
    foreach ($line in Get-Content -LiteralPath $path) {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith('#')) { continue }
        $separator = $trimmed.IndexOf('=')
        if ($separator -lt 1) { continue }
        $key = $trimmed.Substring(0, $separator).Trim()
        $value = $trimmed.Substring($separator + 1).Trim().Trim('"').Trim("'")
        if (-not [Environment]::GetEnvironmentVariable($key)) {
            [Environment]::SetEnvironmentVariable($key, $value)
            Write-Verbose ".env: $key=$value"
        }
    }
    Write-Ok "Loaded $path"
}

function Test-Prerequisite {
    <#
    .SYNOPSIS
        Checks one tool (or an arbitrary test) and prints an actionable hint. Returns $true when satisfied.
    #>
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][scriptblock] $Test,
        [Parameter(Mandatory)][string] $Hint
    )
    $detail = $null
    try { $detail = & $Test } catch { $detail = $null }
    if ($detail) {
        Write-Ok "$Name`: $detail"
        return $true
    }
    Write-Host "    MISSING: $Name" -ForegroundColor Red
    Write-Host "             $Hint" -ForegroundColor Yellow
    return $false
}

function Get-FirstLine {
    param([Parameter(Mandatory)][string] $Command, [string[]] $Arguments = @('--version'))
    if (-not (Get-Command $Command -ErrorAction SilentlyContinue)) { return $null }
    $result = Get-NativeOutput -Command $Command -Arguments $Arguments
    if ($result.ExitCode -ne 0 -or $result.Output.Count -eq 0) { return $null }
    return ($result.Output[0]).Trim()
}

function Test-WebView2 {
    <#
    .SYNOPSIS
        WebView2 Evergreen runtime version from the registry (machine or user install), or $null.
    #>
    $clientId = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    foreach ($key in @(
            "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$clientId",
            "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$clientId",
            "HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$clientId")) {
        $item = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($item -and $item.PSObject.Properties['pv'] -and $item.pv -and $item.pv -ne '0.0.0.0') { return $item.pv }
    }
    return $null
}

function Test-VcTools {
    <#
    .SYNOPSIS
        Visual Studio installation path with the C++ x64 toolset (via vswhere), or $null.
    #>
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { return $null }
    $result = Get-NativeOutput -Command $vswhere -Arguments @('-products', '*', '-requires', 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64', '-property', 'installationPath', '-latest')
    if ($result.ExitCode -eq 0 -and $result.Output.Count -gt 0 -and $result.Output[0]) { return ($result.Output[0]).Trim() }
    return $null
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

$script:Children = New-Object 'System.Collections.Generic.List[object]'

function Start-DevProcess {
    <#
    .SYNOPSIS
        Starts `<CommandLine>` in a new console window (same PowerShell host as this one), tee'ing its merged
        stdout/stderr into artifacts/logs/dev-<Name>.log, and records the process for shutdown.
    #>
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $CommandLine,
        [string] $WorkingDirectory = $RepoRoot
    )
    $log = Join-Path $LogDir "dev-$Name.log"
    if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }

    # cmd /s /c merges stderr into stdout before PowerShell sees it (no red ErrorRecords in the window), Tee-Object
    # keeps a copy on disk. The window stays open a few seconds after exit so a crash message can be read.
    $inner = @(
        "`$Host.UI.RawUI.WindowTitle = 'ClubShell dev: $Name'",
        "Set-Location -LiteralPath '$WorkingDirectory'",
        "cmd.exe /d /s /c `"$CommandLine 2>&1`" | Tee-Object -FilePath '$log'",
        "Write-Host ''; Write-Host ('[$Name] exited with code ' + `$LASTEXITCODE) -ForegroundColor Yellow",
        'Start-Sleep -Seconds 8'
    ) -join '; '
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))

    $hostExe = 'powershell.exe'
    try {
        $current = (Get-Process -Id $PID).Path
        if ($current -and ($current -match '(?i)\\(pwsh|powershell)\.exe$')) { $hostExe = $current }
    } catch { $hostExe = 'powershell.exe' }

    $process = Start-Process -FilePath $hostExe -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded) -PassThru -WindowStyle Normal
    $script:Children.Add([pscustomobject]@{ Name = $Name; Process = $process; Log = $log; Reported = $false })
    Write-Ok "[$Name] pid $($process.Id)  ->  $CommandLine"
    Write-Host "           log: $log" -ForegroundColor DarkGray
    return $process
}

function Stop-DevProcesses {
    <#
    .SYNOPSIS
        Kills every recorded child process tree (taskkill /T /F).
    #>
    foreach ($child in $script:Children) {
        try {
            $child.Process.Refresh()
            if (-not $child.Process.HasExited) {
                [Console]::WriteLine("    stopping [$($child.Name)] pid $($child.Process.Id)")
                Get-NativeOutput -Command 'taskkill.exe' -Arguments @('/PID', "$($child.Process.Id)", '/T', '/F') | Out-Null
            }
        } catch {
            [Console]::WriteLine("    could not stop [$($child.Name)]: $($_.Exception.Message)")
        }
    }
}

function Wait-HttpReady {
    <#
    .SYNOPSIS
        Polls a URL until it answers (any HTTP status) or the timeout / the watched process exit. Returns $true when ready.
    #>
    param(
        [Parameter(Mandatory)][string] $Url,
        [Parameter(Mandatory)][System.Diagnostics.Process] $Process,
        [int] $TimeoutSeconds = 90
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) { return $false }
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec 3 | Out-Null
            return $true
        } catch {
            if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) { return $true }
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Show-LogTail {
    param([Parameter(Mandatory)][string] $Path, [int] $Lines = 25)
    if (Test-Path -LiteralPath $Path) {
        Write-Host "    --- last $Lines lines of $Path ---" -ForegroundColor DarkGray
        Get-Content -LiteralPath $Path -Tail $Lines | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    }
}

# ---------------------------------------------------------------------------------------------------------------
# Environment
# ---------------------------------------------------------------------------------------------------------------
Write-Step 'Environment'
Import-DotEnv
if ($MockPort -eq 0) {
    $fromEnv = 0
    if ($env:MOCK_SERVER_PORT -and [int]::TryParse($env:MOCK_SERVER_PORT, [ref]$fromEnv) -and $fromEnv -gt 0) { $MockPort = $fromEnv } else { $MockPort = 8080 }
}
$mockBase = "http://localhost:$MockPort"
if (-not $env:CLUBSHELL_SERVER_URL) { $env:CLUBSHELL_SERVER_URL = "$mockBase/api/v1" }
if (-not $env:CLUBSHELL_WS_URL) {
    $env:CLUBSHELL_WS_URL = (($env:CLUBSHELL_SERVER_URL -replace '^http', 'ws') -replace '/api/v\d+/?$', '') + '/ws/agent'
}
$env:MOCK_SERVER_PORT = "$MockPort"
$env:CLUBSHELL_DEV = '1'
$env:CLUBSHELL__server__baseUrl = $env:CLUBSHELL_SERVER_URL
$env:CLUBSHELL__server__wsUrl = $env:CLUBSHELL_WS_URL
$env:VITE_SERVER_URL = $mockBase
if ($WebOnly) { $env:VITE_MOCK = '1' } else { $env:VITE_MOCK = '0' }
Write-Ok "CLUBSHELL_SERVER_URL=$env:CLUBSHELL_SERVER_URL"
Write-Ok "CLUBSHELL_WS_URL=$env:CLUBSHELL_WS_URL"
Write-Ok "VITE_MOCK=$env:VITE_MOCK  MOCK_SERVER_PORT=$MockPort"

# ---------------------------------------------------------------------------------------------------------------
# Prerequisites
# ---------------------------------------------------------------------------------------------------------------
Write-Step 'Prerequisites'
$ok = $true
$ok = (Test-Prerequisite -Name 'node' -Test { Get-FirstLine -Command 'node' -Arguments @('-v') } -Hint 'winget install OpenJS.NodeJS.LTS') -and $ok
$ok = (Test-Prerequisite -Name 'pnpm' -Test { Get-FirstLine -Command 'pnpm' -Arguments @('-v') } -Hint 'corepack enable; corepack prepare pnpm@9.9.0 --activate   (or: npm i -g pnpm@9)') -and $ok
$ok = (Test-Prerequisite -Name 'node_modules' -Test { if (Test-Path -LiteralPath (Join-Path $RepoRoot 'node_modules\.pnpm')) { 'installed' } } -Hint 'pnpm install --frozen-lockfile') -and $ok
if ($Agent) {
    $ok = (Test-Prerequisite -Name '.NET SDK' -Test {
            $sdks = Get-NativeOutput -Command 'dotnet' -Arguments @('--list-sdks')
            if ($sdks.ExitCode -eq 0 -and $sdks.Output.Count -gt 0 -and ($sdks.Output -match '^8\.')) { ($sdks.Output -match '^8\.')[-1] }
        } -Hint 'winget install Microsoft.DotNet.SDK.8') -and $ok
    if (-not (Test-Administrator)) {
        Write-Warn 'not elevated: the Agent needs to create C:\ProgramData\ClubShell and the named pipe ACLs; run this shell as Administrator if it fails'
    }
}
if (-not $WebOnly -and -not $NoShell) {
    $ok = (Test-Prerequisite -Name 'cargo' -Test { Get-FirstLine -Command 'cargo' -Arguments @('-V') } -Hint 'winget install Rustlang.Rustup   then restart the shell (rust-toolchain.toml installs 1.89.0 on first use)') -and $ok
    $ok = (Test-Prerequisite -Name 'MSVC C++ build tools' -Test { Test-VcTools } -Hint 'winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --norestart --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"') -and $ok
    $ok = (Test-Prerequisite -Name 'WebView2 runtime' -Test { Test-WebView2 } -Hint 'winget install Microsoft.EdgeWebView2Runtime') -and $ok
}
if (-not $ok) {
    Write-Host "`nMissing prerequisites; see hints above (tools/scripts/setup-dev-vm.ps1 installs everything)." -ForegroundColor Red
    exit 2
}

# ---------------------------------------------------------------------------------------------------------------
# Launch
# ---------------------------------------------------------------------------------------------------------------
New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
$exitCode = 0
$controlCAsInput = $false
try {
    Write-Step 'Starting'
    if (-not $NoMock) {
        $mockArgs = 'pnpm mock'
        if ($ResetMock) { $mockArgs += ' -- --reset' }
        $mock = Start-DevProcess -Name 'mock' -CommandLine $mockArgs
        Write-Host "    waiting for $mockBase/health ..." -ForegroundColor DarkGray
        if (-not (Wait-HttpReady -Url "$mockBase/health" -Process $mock)) {
            Show-LogTail -Path (Join-Path $LogDir 'dev-mock.log')
            throw "mock server did not become ready on $mockBase (is the port in use?)"
        }
        Write-Ok "mock server ready: $mockBase/api/v1  ws://localhost:$MockPort/ws/agent"
    }

    if ($Agent) {
        $agentProject = Join-Path $RepoRoot 'src\ClubShell.Agent\ClubShell.Agent.csproj'
        Start-DevProcess -Name 'agent' -CommandLine "dotnet run --project `"$agentProject`" -c Debug -- --console --dev" | Out-Null
    }

    if (-not $NoShell) {
        if ($WebOnly) {
            $vite = Start-DevProcess -Name 'shell' -CommandLine 'pnpm --filter @clubshell/shell dev' -WorkingDirectory $RepoRoot
            if (Wait-HttpReady -Url 'http://localhost:1420/' -Process $vite -TimeoutSeconds 60) {
                Write-Ok 'Vite ready: http://localhost:1420 (mock mode)'
                if (-not $NoBrowser) { Start-Process 'http://localhost:1420/' }
            } else {
                Show-LogTail -Path (Join-Path $LogDir 'dev-shell.log')
                throw 'Vite dev server did not start'
            }
        } else {
            Start-DevProcess -Name 'shell' -CommandLine 'pnpm tauri dev' | Out-Null
            Write-Host '    (first `tauri dev` compiles src-tauri; this takes a few minutes. Exit hotkey: Ctrl+Alt+Shift+F12)' -ForegroundColor DarkGray
        }
    }

    if ($script:Children.Count -eq 0) {
        Write-Warn 'nothing to run (-NoMock -NoShell without -Agent)'
        exit 0
    }

    Write-Host "`nRunning. Press Ctrl+C or q to stop all children." -ForegroundColor White
    try { [Console]::TreatControlCAsInput = $true; $controlCAsInput = $true } catch { $controlCAsInput = $false }

    while ($true) {
        $alive = 0
        foreach ($child in $script:Children) {
            $child.Process.Refresh()
            if ($child.Process.HasExited) {
                if (-not $child.Reported) {
                    $child.Reported = $true
                    $code = $child.Process.ExitCode
                    if ($code -eq 0) { Write-Ok "[$($child.Name)] exited" } else { Write-Warn "[$($child.Name)] exited with code $code"; Show-LogTail -Path $child.Log }
                }
            } else {
                $alive++
            }
        }
        if ($alive -eq 0) { Write-Host 'All children exited.'; break }

        if ($controlCAsInput) {
            try {
                if ([Console]::KeyAvailable) {
                    $key = [Console]::ReadKey($true)
                    $isControlC = ($key.Key -eq [ConsoleKey]::C) -and (($key.Modifiers -band [ConsoleModifiers]::Control) -ne 0)
                    if ($isControlC -or $key.KeyChar -eq 'q' -or $key.KeyChar -eq 'Q') { Write-Host 'Shutting down...'; break }
                }
            } catch {
                $controlCAsInput = $false
            }
        }
        Start-Sleep -Milliseconds 300
    }
} catch {
    $exitCode = 1
    Write-Host "`nERROR: $($_.Exception.Message)" -ForegroundColor Red
} finally {
    # Runs on Ctrl+C too (when TreatControlCAsInput could not be enabled); plain [Console] output survives pipeline stop.
    if ($controlCAsInput) { try { [Console]::TreatControlCAsInput = $false } catch { $controlCAsInput = $false } }
    Stop-DevProcesses
    [Console]::WriteLine("Logs: $LogDir")
}
exit $exitCode
