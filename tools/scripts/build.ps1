<#
.SYNOPSIS
    Builds ClubShell: web packages (contracts + shell UI), the .NET solution (+ tests, Agent publish), the Rust /
    Tauri shell bundle and, optionally, the WiX installer. Writes artifacts/build-info.json and prints a summary.
.DESCRIPTION
    Orchestrates the whole build from the repository root (resolved from this script's location):

      Web       pnpm install --frozen-lockfile, @clubshell/contracts build, workspace typecheck, shell UI build.
      Dotnet    dotnet restore / build / test ClubShell.sln, then publishes the Agent as a framework-dependent
                single file (win-x64) into artifacts/publish/agent.
      Rust      cargo test --workspace (unless -SkipTests) and `pnpm tauri build` with
                apps/shell/src-tauri/tauri.windows.conf.json (+ the build version); copies the bundle (msi / nsis)
                and clubshell-shell.exe into artifacts/shell.
      Installer delegates to package.ps1 (release layout + WiX MSI/bundle + manifest.json).
      All       Web, Dotnet, Rust, Installer in that order.

    README aliases are accepted too: Contracts = Web, Agent = Dotnet, Shell = Web + Rust.
    Every native tool invocation checks $LASTEXITCODE; the first failure aborts the build with exit code 1.
    Works on Windows PowerShell 5.1 and PowerShell 7.
.PARAMETER Target
    What to build: All | Dotnet | Rust | Web | Installer (or Contracts | Agent | Shell). Default: All.
.PARAMETER Configuration
    MSBuild configuration for the .NET solution (Release | Debug). Rust/Tauri always build the release profile.
    Default: Release.
.PARAMETER Version
    Semantic version stamped into assemblies, the Tauri bundle and build-info.json. Default: derived from
    `git describe --tags` (v1.2.3 -> 1.2.3, v1.2.3-4-gabc -> 1.2.3-dev.4) or 1.0.0-local without git/tags.
.PARAMETER SkipTests
    Skip `dotnet test` and `cargo test`.
.PARAMETER SkipInstall
    Skip `pnpm install --frozen-lockfile` (node_modules already up to date).
.EXAMPLE
    .\tools\scripts\build.ps1
    Full Release build of everything.
.EXAMPLE
    .\tools\scripts\build.ps1 -Target Dotnet -SkipTests -Version 1.2.0-beta.1
.OUTPUTS
    Exit code 0 on success, 1 on a build failure, 2 when a required toolchain is missing.
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'Dotnet', 'Rust', 'Web', 'Installer', 'Contracts', 'Agent', 'Shell')]
    [string] $Target = 'All',

    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [ValidatePattern('^\d+\.\d+\.\d+([-+][0-9A-Za-z.+-]+)?$')]
    [string] $Version,

    [switch] $SkipTests,
    [switch] $SkipInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

# ---------------------------------------------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------------------------------------------
$RepoRoot     = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$ArtifactsDir = Join-Path $RepoRoot 'artifacts'

function Write-Step { param([Parameter(Mandatory)][string] $Message) Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Write-Ok   { param([Parameter(Mandatory)][string] $Message) Write-Host "    $Message" -ForegroundColor Green }
function Write-Warn { param([Parameter(Mandatory)][string] $Message) Write-Host "    WARNING: $Message" -ForegroundColor Yellow }

function Invoke-Native {
    <#
    .SYNOPSIS
        Runs a native command in the given directory and throws when its exit code is non-zero.
    #>
    param(
        [Parameter(Mandatory)][string] $Command,
        [string[]] $Arguments = @(),
        [string] $WorkingDirectory = $RepoRoot
    )
    Write-Host "    > $Command $($Arguments -join ' ')" -ForegroundColor DarkGray
    Push-Location -LiteralPath $WorkingDirectory
    try {
        # Native stderr must never become a terminating error (Windows PowerShell 5.1 wraps it in
        # NativeCommandError records when the caller redirects 2>&1); the exit code is what counts.
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = 0
        & $Command @Arguments
        $ErrorActionPreference = 'Stop'
        if ($LASTEXITCODE -ne 0) {
            throw "'$Command $($Arguments -join ' ')' failed with exit code $LASTEXITCODE"
        }
    } finally {
        Pop-Location
    }
}

function Get-NativeOutput {
    <#
    .SYNOPSIS
        Runs a native command and returns @{ ExitCode; Output } without ever throwing (missing command => 127).
    #>
    param(
        [Parameter(Mandatory)][string] $Command,
        [string[]] $Arguments = @(),
        [string] $WorkingDirectory = $RepoRoot
    )
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

function Get-ToolVersion {
    <#
    .SYNOPSIS
        First output line of `<tool> <args>` or $null when the tool is missing or fails.
    #>
    param([Parameter(Mandatory)][string] $Command, [string[]] $Arguments = @('--version'))
    if (-not (Get-Command $Command -ErrorAction SilentlyContinue)) { return $null }
    $result = Get-NativeOutput -Command $Command -Arguments $Arguments
    if ($result.ExitCode -ne 0 -or $result.Output.Count -eq 0) { return $null }
    return ($result.Output[0]).Trim()
}

function Assert-Tool {
    <#
    .SYNOPSIS
        Fails with exit code 2 and an install hint when a required tool is not available.
    #>
    param([Parameter(Mandatory)][string] $Command, [Parameter(Mandatory)][string] $Hint, [string[]] $VersionArguments = @('--version'))
    $version = Get-ToolVersion -Command $Command -Arguments $VersionArguments
    if (-not $version) {
        Write-Host "ERROR: '$Command' is required for target '$Target' but was not found or does not work. $Hint" -ForegroundColor Red
        exit 2
    }
    Write-Ok "$Command $version"
}

function Get-BuildVersion {
    <#
    .SYNOPSIS
        Version from `git describe --tags` (v1.2.3 -> 1.2.3, v1.2.3-4-gabc-dirty -> 1.2.3-dev.4.dirty) or 1.0.0-local.
    #>
    $git = Get-NativeOutput -Command 'git' -Arguments @('describe', '--tags', '--always', '--dirty', '--match', 'v[0-9]*')
    if ($git.ExitCode -eq 0 -and $git.Output.Count -gt 0) {
        $described = ($git.Output[0]).Trim()
        if ($described -match '^v?(\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?)(?:-(\d+)-g[0-9a-f]+)?(-dirty)?$') {
            $base = $Matches[1]
            $ahead = $Matches[2]
            $dirty = $Matches[3]
            if (-not $ahead -and -not $dirty) { return $base }
            $suffix = 'dev'
            if ($ahead) { $suffix += ".$ahead" }
            if ($dirty) { $suffix += '.dirty' }
            if ($base -like '*-*') { return "$base.$suffix" }
            return "$base-$suffix"
        }
    }
    return '1.0.0-local'
}

function Get-GitValue {
    param([Parameter(Mandatory)][string[]] $Arguments)
    $result = Get-NativeOutput -Command 'git' -Arguments $Arguments
    if ($result.ExitCode -eq 0 -and $result.Output.Count -gt 0) { return ($result.Output[0]).Trim() }
    return $null
}

function Write-JsonFile {
    <#
    .SYNOPSIS
        Writes JSON as UTF-8 without a BOM (Set-Content -Encoding utf8 adds one on Windows PowerShell 5.1, which
        serde_json / the Tauri CLI reject).
    #>
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][AllowEmptyString()][string] $Json)
    [IO.File]::WriteAllText($Path, $Json + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
}

function Find-FirstDirectory {
    param([Parameter(Mandatory)][string[]] $Candidates)
    foreach ($candidate in $Candidates) {
        $full = Join-Path $RepoRoot $candidate
        if (Test-Path -LiteralPath $full -PathType Container) { return $full }
    }
    return $null
}

$script:Steps = New-Object 'System.Collections.Generic.List[object]'
function Invoke-Step {
    <#
    .SYNOPSIS
        Runs one named build step, timing it and recording the outcome for the final summary table.
    #>
    param([Parameter(Mandatory)][string] $Name, [Parameter(Mandatory)][scriptblock] $Action)
    Write-Step $Name
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $status = 'FAILED'
    try {
        & $Action
        $status = 'ok'
    } finally {
        $stopwatch.Stop()
        $script:Steps.Add([pscustomobject]@{ Step = $Name; Status = $status; Seconds = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1) })
    }
}

# ---------------------------------------------------------------------------------------------------------------
# Plan
# ---------------------------------------------------------------------------------------------------------------
$doWeb = $false; $doDotnet = $false; $doRust = $false; $doInstaller = $false
switch ($Target) {
    'All'       { $doWeb = $true; $doDotnet = $true; $doRust = $true; $doInstaller = $true }
    'Web'       { $doWeb = $true }
    'Contracts' { $doWeb = $true }
    'Dotnet'    { $doDotnet = $true }
    'Agent'     { $doDotnet = $true }
    'Rust'      { $doRust = $true }
    'Shell'     { $doWeb = $true; $doRust = $true }
    'Installer' { $doInstaller = $true }
}

if (-not $Version) { $Version = Get-BuildVersion }
$numericVersion = ([regex]::Match($Version, '^\d+\.\d+\.\d+')).Value
$commit = Get-GitValue -Arguments @('rev-parse', '--short', 'HEAD')
$branch = Get-GitValue -Arguments @('rev-parse', '--abbrev-ref', 'HEAD')
$startedAt = [DateTime]::UtcNow

Write-Host "ClubShell build  target=$Target  configuration=$Configuration  version=$Version" -ForegroundColor White
Write-Host "repo: $RepoRoot"
New-Item -ItemType Directory -Path $ArtifactsDir -Force | Out-Null

$exitCode = 0
try {
    # -----------------------------------------------------------------------------------------------------------
    # Prerequisites (only for what the selected target needs)
    # -----------------------------------------------------------------------------------------------------------
    Write-Step 'Checking toolchain'
    if ($doWeb -or $doRust) {
        Assert-Tool -Command 'node' -Hint 'Install Node.js LTS: winget install OpenJS.NodeJS.LTS'
        Assert-Tool -Command 'pnpm' -Hint 'Install pnpm: corepack enable; corepack prepare pnpm@9.9.0 --activate'
    }
    if ($doDotnet -or $doInstaller) {
        Assert-Tool -Command 'dotnet' -Hint 'Install the .NET 8 SDK: winget install Microsoft.DotNet.SDK.8'
    }
    if ($doRust) {
        Assert-Tool -Command 'cargo' -Hint 'Install Rust: winget install Rustlang.Rustup (then restart the shell)' -VersionArguments @('-V')
    }
    if ($commit) { Write-Ok "git $branch@$commit" } else { Write-Warn 'not a git checkout (or git missing): commit unknown' }

    # -----------------------------------------------------------------------------------------------------------
    # Web
    # -----------------------------------------------------------------------------------------------------------
    if ($doWeb) {
        if (-not $SkipInstall) {
            Invoke-Step 'pnpm install' { Invoke-Native -Command 'pnpm' -Arguments @('install', '--frozen-lockfile') }
        }
        Invoke-Step 'contracts build' { Invoke-Native -Command 'pnpm' -Arguments @('contracts:build') }
        Invoke-Step 'typecheck (workspace)' { Invoke-Native -Command 'pnpm' -Arguments @('-r', 'typecheck') }
        Invoke-Step 'shell UI build' { Invoke-Native -Command 'pnpm' -Arguments @('--filter', '@clubshell/shell', 'build') }
    }

    # -----------------------------------------------------------------------------------------------------------
    # .NET
    # -----------------------------------------------------------------------------------------------------------
    if ($doDotnet) {
        $solution = Join-Path $RepoRoot 'ClubShell.sln'
        Invoke-Step 'dotnet restore' { Invoke-Native -Command 'dotnet' -Arguments @('restore', $solution) }
        Invoke-Step 'dotnet build' {
            Invoke-Native -Command 'dotnet' -Arguments @('build', $solution, '-c', $Configuration, '--no-restore', "-p:Version=$Version", '-nologo')
        }
        if (-not $SkipTests) {
            Invoke-Step 'dotnet test' {
                $results = Join-Path $ArtifactsDir 'test-results'
                Invoke-Native -Command 'dotnet' -Arguments @('test', $solution, '-c', $Configuration, '--no-build', '--logger', 'trx', '--results-directory', $results, '-nologo')
            }
        }
        Invoke-Step 'dotnet publish Agent' {
            $out = Join-Path $ArtifactsDir 'publish\agent'
            Invoke-Native -Command 'dotnet' -Arguments @(
                'publish', (Join-Path $RepoRoot 'src\ClubShell.Agent\ClubShell.Agent.csproj'),
                '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'false',
                '-p:PublishSingleFile=true', "-p:Version=$Version", '-o', $out, '-nologo')
            Write-Ok "Agent published to $out"
        }
    }

    # -----------------------------------------------------------------------------------------------------------
    # Rust / Tauri
    # -----------------------------------------------------------------------------------------------------------
    if ($doRust) {
        $dist = Join-Path $RepoRoot 'apps\shell\dist'
        if (-not (Test-Path -LiteralPath (Join-Path $dist 'index.html'))) {
            # tauri-build refuses to compile when build.frontendDist does not exist.
            Invoke-Step 'shell UI build (frontendDist missing)' { Invoke-Native -Command 'pnpm' -Arguments @('--filter', '@clubshell/shell', 'build') }
        }
        if (-not $SkipTests) {
            Invoke-Step 'cargo test' { Invoke-Native -Command 'cargo' -Arguments @('test', '--workspace', '--release') }
        }
        Invoke-Step 'tauri build' {
            # Single merged --config (tauri-cli 2.0 accepts one): the Windows signing settings + the build version.
            # MSI/NSIS need a numeric x.y.z, so pre-release identifiers are dropped for the bundle only.
            $windowsConf = Get-Content -LiteralPath (Join-Path $RepoRoot 'apps\shell\src-tauri\tauri.windows.conf.json') -Raw | ConvertFrom-Json
            if ($windowsConf.PSObject.Properties['version']) { $windowsConf.version = $numericVersion } else { $windowsConf | Add-Member -NotePropertyName 'version' -NotePropertyValue $numericVersion }
            $mergedConf = Join-Path $ArtifactsDir 'tauri.build.conf.json'
            Write-JsonFile -Path $mergedConf -Json ($windowsConf | ConvertTo-Json -Depth 20)
            Invoke-Native -Command 'pnpm' -Arguments @('tauri', 'build', '--ci', '--config', $mergedConf)
        }
        Invoke-Step 'collect shell bundle' {
            $bundleDir = Find-FirstDirectory -Candidates @(
                'target\x86_64-pc-windows-msvc\release\bundle',
                'target\release\bundle',
                'apps\shell\src-tauri\target\x86_64-pc-windows-msvc\release\bundle',
                'apps\shell\src-tauri\target\release\bundle')
            if (-not $bundleDir) { throw 'Tauri bundle directory not found after `tauri build`.' }
            $shellOut = Join-Path $ArtifactsDir 'shell'
            New-Item -ItemType Directory -Path $shellOut -Force | Out-Null
            $bundles = @(Get-ChildItem -LiteralPath $bundleDir -Recurse -File -Include '*.msi', '*.exe')
            if ($bundles.Count -eq 0) { throw "No .msi/.exe bundle found under $bundleDir" }
            foreach ($bundle in $bundles) {
                Copy-Item -LiteralPath $bundle.FullName -Destination $shellOut -Force
                Write-Ok "$($bundle.Name) ($([math]::Round($bundle.Length / 1MB, 1)) MB)"
            }
            $exe = Get-ChildItem -LiteralPath (Split-Path -Parent $bundleDir) -File -Filter 'clubshell-shell.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($exe) { Copy-Item -LiteralPath $exe.FullName -Destination $shellOut -Force; Write-Ok 'clubshell-shell.exe' }
        }
    }

    # -----------------------------------------------------------------------------------------------------------
    # Installer (release layout + WiX) via package.ps1
    # -----------------------------------------------------------------------------------------------------------
    if ($doInstaller) {
        Invoke-Step 'package (WiX installer + manifest)' {
            $packageScript = Join-Path $PSScriptRoot 'package.ps1'
            & $packageScript -Version $Version -Configuration $Configuration
            if ($LASTEXITCODE -ne 0) { throw "package.ps1 failed with exit code $LASTEXITCODE" }
        }
    }

    # -----------------------------------------------------------------------------------------------------------
    # build-info.json
    # -----------------------------------------------------------------------------------------------------------
    Write-Step 'Writing artifacts/build-info.json'
    $info = [ordered]@{
        version        = $Version
        numericVersion = $numericVersion
        configuration  = $Configuration
        target         = $Target
        commit         = $commit
        branch         = $branch
        date           = $startedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        machine        = $env:COMPUTERNAME
        toolchain      = [ordered]@{
            powershell = $PSVersionTable.PSVersion.ToString()
            node       = Get-ToolVersion -Command 'node'
            pnpm       = Get-ToolVersion -Command 'pnpm'
            dotnet     = Get-ToolVersion -Command 'dotnet'
            cargo      = Get-ToolVersion -Command 'cargo' -Arguments @('-V')
            rustc      = Get-ToolVersion -Command 'rustc' -Arguments @('-V')
        }
        steps          = @($script:Steps | ForEach-Object { [ordered]@{ step = $_.Step; status = $_.Status; seconds = $_.Seconds } })
    }
    $infoPath = Join-Path $ArtifactsDir 'build-info.json'
    Write-JsonFile -Path $infoPath -Json ($info | ConvertTo-Json -Depth 6)
    Write-Ok $infoPath
} catch {
    $exitCode = 1
    Write-Host "`nBUILD FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.ScriptStackTrace) { Write-Verbose $_.ScriptStackTrace }
}

# ---------------------------------------------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------------------------------------------
Write-Host ''
Write-Host "Summary (version $Version, $([math]::Round(([DateTime]::UtcNow - $startedAt).TotalSeconds, 1)) s total):" -ForegroundColor White
if ($script:Steps.Count -gt 0) {
    $script:Steps | Format-Table -AutoSize Step, Status, Seconds | Out-String | ForEach-Object { Write-Host $_.TrimEnd() }
}
if ($exitCode -eq 0) { Write-Host 'BUILD SUCCEEDED' -ForegroundColor Green } else { Write-Host 'BUILD FAILED' -ForegroundColor Red }
exit $exitCode
