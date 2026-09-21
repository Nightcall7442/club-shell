<#
.SYNOPSIS
    Regenerates the Rust contracts mirror (crates/protocol/src) from ClubShell.Contracts with tools/ContractsGen,
    formats it with rustfmt, and optionally checks it against the committed files (CI).
.DESCRIPTION
    Steps: dotnet build tools/ContractsGen -> dotnet run ContractsGen -- --target rs --out <dir> ->
    rustfmt --edition 2021 (when rustfmt is on PATH, unless -NoFormat) -> optional -Verify
    (cargo test -p clubshell-protocol).

    With -Check the generator writes into artifacts/contracts-gen/rs instead and the script compares every
    generated file with crates/protocol/src (line endings normalised); any difference or missing file is listed
    (with `git diff --no-index` when git is available) and the script exits with code 1. Nothing under crates/ is
    modified in that mode.
.PARAMETER Check
    Compare the freshly generated output with the committed sources instead of overwriting them; exit 1 on drift.
.PARAMETER Verify
    After generating, run `cargo test -p clubshell-protocol` (skipped with a warning when cargo is missing).
.PARAMETER NoFormat
    Skip rustfmt.
.PARAMETER Configuration
    Build configuration for ContractsGen. Default: Release.
.EXAMPLE
    .\tools\scripts\gen-contracts-rs.ps1 -Verify
.EXAMPLE
    .\tools\scripts\gen-contracts-rs.ps1 -Check
    CI drift check: fails when a contract changed without regenerating the Rust mirror.
.OUTPUTS
    Exit code 0 on success, 1 on generation/format failure or drift (-Check), 2 when the .NET SDK is missing.
#>
[CmdletBinding()]
param(
    [switch] $Check,
    [switch] $Verify,
    [switch] $NoFormat,
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$RepoRoot   = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$Project    = Join-Path $RepoRoot 'tools\ContractsGen\ContractsGen.csproj'
$TargetDir  = Join-Path $RepoRoot 'crates\protocol\src'
$ScratchDir = Join-Path $RepoRoot 'artifacts\contracts-gen\rs'

function Write-Step { param([Parameter(Mandatory)][string] $Message) Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Write-Ok   { param([Parameter(Mandatory)][string] $Message) Write-Host "    $Message" -ForegroundColor Green }
function Write-Warn { param([Parameter(Mandatory)][string] $Message) Write-Host "    WARNING: $Message" -ForegroundColor Yellow }

function Invoke-Native {
    <#
    .SYNOPSIS
        Runs a native command in the given directory and throws when its exit code is non-zero.
    #>
    param([Parameter(Mandatory)][string] $Command, [string[]] $Arguments = @(), [string] $WorkingDirectory = $RepoRoot)
    Write-Host "    > $Command $($Arguments -join ' ')" -ForegroundColor DarkGray
    Push-Location -LiteralPath $WorkingDirectory
    try {
        # Native stderr must never become a terminating error (Windows PowerShell 5.1 wraps it in
        # NativeCommandError records when the caller redirects 2>&1); the exit code is what counts.
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = 0
        & $Command @Arguments
        $ErrorActionPreference = 'Stop'
        if ($LASTEXITCODE -ne 0) { throw "'$Command $($Arguments -join ' ')' failed with exit code $LASTEXITCODE" }
    } finally {
        Pop-Location
    }
}

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

function Test-DotnetSdk {
    $sdks = Get-NativeOutput -Command 'dotnet' -Arguments @('--list-sdks')
    return ($sdks.ExitCode -eq 0 -and (@($sdks.Output) -match '^\d').Count -gt 0)
}

function Get-NormalizedText {
    param([Parameter(Mandatory)][string] $Path)
    return ([IO.File]::ReadAllText($Path) -replace "`r`n", "`n")
}

function Compare-GeneratedTree {
    <#
    .SYNOPSIS
        Compares every file under $Generated with its counterpart under $Committed. Returns the list of relative
        paths that differ or are missing; prints `git diff --no-index` for each when git is available.
    #>
    param([Parameter(Mandatory)][string] $Generated, [Parameter(Mandatory)][string] $Committed)
    $drift = New-Object 'System.Collections.Generic.List[string]'
    $git = [bool](Get-Command git -ErrorAction SilentlyContinue)
    foreach ($file in Get-ChildItem -LiteralPath $Generated -Recurse -File) {
        $relative = $file.FullName.Substring($Generated.Length).TrimStart('\', '/')
        $counterpart = Join-Path $Committed $relative
        if (-not (Test-Path -LiteralPath $counterpart)) {
            Write-Host "    MISSING  $relative (generated, not committed)" -ForegroundColor Red
            $drift.Add($relative)
            continue
        }
        if ((Get-NormalizedText -Path $file.FullName) -ceq (Get-NormalizedText -Path $counterpart)) {
            Write-Verbose "    same     $relative"
            continue
        }
        Write-Host "    DIFFERS  $relative" -ForegroundColor Red
        $drift.Add($relative)
        if ($git) {
            $diff = Get-NativeOutput -Command 'git' -Arguments @('--no-pager', 'diff', '--no-index', '--ignore-cr-at-eol', '--', $counterpart, $file.FullName)
            $diff.Output | Select-Object -First 80 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath $Committed -Recurse -File) {
        $relative = $file.FullName.Substring($Committed.Length).TrimStart('\', '/')
        if (-not (Test-Path -LiteralPath (Join-Path $Generated $relative))) {
            Write-Warn "$relative exists in the committed tree but the generator did not emit it"
        }
    }
    return $drift
}

$exitCode = 0
try {
    Write-Step 'Checking toolchain'
    if (-not (Test-DotnetSdk)) {
        Write-Host 'ERROR: the .NET 8 SDK is required (winget install Microsoft.DotNet.SDK.8).' -ForegroundColor Red
        exit 2
    }
    Write-Ok "dotnet SDK $((Get-NativeOutput -Command 'dotnet' -Arguments @('--version')).Output -join '')"
    $hasRustfmt = [bool](Get-Command rustfmt -ErrorAction SilentlyContinue)
    $hasCargo = [bool](Get-Command cargo -ErrorAction SilentlyContinue)

    Write-Step 'Building ContractsGen'
    Invoke-Native -Command 'dotnet' -Arguments @('build', $Project, '-c', $Configuration, '-nologo')

    $outDir = $TargetDir
    if ($Check) {
        $outDir = $ScratchDir
        if (Test-Path -LiteralPath $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
    }
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    if ($Check) {
        # Seed the scratch tree with the committed files so the generator carries their MANUAL blocks over
        # (hand-written helpers between the BEGIN/END MANUAL markers; see tools/ContractsGen/Program.cs).
        Copy-Item -Path (Join-Path $TargetDir '*.rs') -Destination $outDir -Force
    }

    Write-Step "Generating Rust contracts -> $outDir"
    Invoke-Native -Command 'dotnet' -Arguments @('run', '--project', $Project, '-c', $Configuration, '--no-build', '--', '--target', 'rs', '--out', $outDir)
    $generated = @(Get-ChildItem -LiteralPath $outDir -Recurse -File -Filter '*.rs')
    if ($generated.Count -eq 0) { throw "ContractsGen produced no .rs files in $outDir" }
    Write-Ok "$($generated.Count) file(s)"

    if (-not $NoFormat) {
        Write-Step 'Formatting (rustfmt)'
        if ($hasRustfmt) {
            # Files are passed explicitly (not `cargo fmt`) so the scratch tree used by -Check is formatted the same way.
            $files = @($generated | ForEach-Object { $_.FullName })
            Invoke-Native -Command 'rustfmt' -Arguments (@('--edition', '2021') + $files)
            Write-Ok 'formatted'
        } else {
            Write-Warn 'rustfmt not found; skipping formatting (rustup component add rustfmt)'
        }
    }

    if ($Check) {
        Write-Step "Comparing with $TargetDir"
        $drift = Compare-GeneratedTree -Generated $outDir -Committed $TargetDir
        if ($drift.Count -gt 0) {
            Write-Host "`n$($drift.Count) generated file(s) differ from the committed Rust contracts." -ForegroundColor Red
            Write-Host 'Run .\tools\scripts\gen-contracts-rs.ps1 and commit crates/protocol/src.' -ForegroundColor Yellow
            $exitCode = 1
        } else {
            Write-Ok 'no drift'
        }
    }

    if ($Verify) {
        Write-Step 'Verifying (cargo test -p clubshell-protocol)'
        if ($hasCargo) {
            Invoke-Native -Command 'cargo' -Arguments @('test', '-p', 'clubshell-protocol')
            Write-Ok 'tests passed'
        } else {
            Write-Warn 'cargo not found; skipping tests (winget install Rustlang.Rustup)'
        }
    }
} catch {
    $exitCode = 1
    Write-Host "`nERROR: $($_.Exception.Message)" -ForegroundColor Red
}

if ($exitCode -eq 0) { Write-Host "`nDone." -ForegroundColor Green }
exit $exitCode
