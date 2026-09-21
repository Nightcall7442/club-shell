<#
.SYNOPSIS
    Explains STATUS_ENTRYPOINT_NOT_FOUND / STATUS_DLL_NOT_FOUND for a Windows executable.
.DESCRIPTION
    Lists every DLL the executable imports (dumpbin /imports), resolves each DLL the way the
    loader would (exe directory, System32, PATH) and checks that every imported function is
    actually exported by the DLL that was found. Missing DLLs and missing entry points are
    printed as errors. Used by CI when a cargo test binary fails to start, and handy on a
    fresh club PC when the shell refuses to launch.
.PARAMETER Path
    Executable(s) to inspect. Wildcards allowed.
.PARAMETER DumpBin
    Path to dumpbin.exe. Located automatically through vswhere when omitted.
.EXAMPLE
    .\tools\scripts\diagnose-imports.ps1 -Path target\debug\deps\clubshell_shell_lib-*.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string[]] $Path,

    [string] $DumpBin
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Find-DumpBin {
    param([string] $Explicit)
    if ($Explicit) { return $Explicit }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'vswhere.exe not found; pass -DumpBin' }
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw 'No Visual Studio with the C++ toolset found; pass -DumpBin' }
    $candidates = @(Get-ChildItem -Path (Join-Path $vs 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending)
    foreach ($c in $candidates) {
        $exe = Join-Path $c.FullName 'bin\Hostx64\x64\dumpbin.exe'
        if (Test-Path -LiteralPath $exe) { return $exe }
    }
    throw 'dumpbin.exe not found under the MSVC toolset; pass -DumpBin'
}

function Get-Imports {
    param([string] $Tool, [string] $Exe)
    $out = & $Tool /nologo /imports $Exe 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw "dumpbin /imports failed for $Exe" }
    $result = [ordered]@{}
    $current = $null
    foreach ($line in $out) {
        if ($line -match '^\s{4}(\S+\.dll)\s*$') {
            $current = $matches[1]
            $result[$current] = New-Object System.Collections.Generic.List[string]
            continue
        }
        if ($null -ne $current -and $line -match '^\s+[0-9A-F]+\s+(?:[0-9A-F]+\s+)?([A-Za-z_][A-Za-z0-9_@]*)\s*$') {
            $result[$current].Add($matches[1])
        }
        elseif ($null -ne $current -and $line -match '^\s+Ordinal\s+(\d+)\s*$') {
            $result[$current].Add("#$($matches[1])")
        }
    }
    return $result
}

function Get-Exports {
    param([string] $Tool, [string] $Dll)
    $out = & $Tool /nologo /exports $Dll 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw "dumpbin /exports failed for $Dll" }
    $names = New-Object System.Collections.Generic.HashSet[string]
    $ordinals = New-Object System.Collections.Generic.HashSet[string]
    foreach ($line in $out) {
        # "  ordinal hint RVA      name" / forwarded exports "name (forwarded to X)"
        if ($line -match '^\s+(\d+)\s+[0-9A-F]+\s+[0-9A-F]+\s+(\S+)') {
            [void]$ordinals.Add("#$($matches[1])"); [void]$names.Add($matches[2])
        }
        elseif ($line -match '^\s+(\d+)\s+[0-9A-F]+\s+(\S+)\s+\(forwarded') {
            [void]$ordinals.Add("#$($matches[1])"); [void]$names.Add($matches[2])
        }
        elseif ($line -match '^\s+(\d+)\s+\S+\s*$') {
            [void]$ordinals.Add("#$($matches[1])")
        }
    }
    return @{ Names = $names; Ordinals = $ordinals }
}

function Resolve-Dll {
    param([string] $Name, [string] $ExeDir)
    $dirs = @($ExeDir, (Join-Path $env:SystemRoot 'System32')) + ($env:PATH -split ';' | Where-Object { $_ })
    foreach ($d in $dirs) {
        $p = Join-Path $d $Name
        if (Test-Path -LiteralPath $p) { return (Resolve-Path -LiteralPath $p).Path }
    }
    return $null
}

$tool = Find-DumpBin -Explicit $DumpBin
Write-Host "dumpbin: $tool"
$problems = 0
foreach ($exe in (Get-ChildItem -Path $Path -File)) {
    Write-Host "`n=== $($exe.FullName)"
    $imports = Get-Imports -Tool $tool -Exe $exe.FullName
    foreach ($dll in $imports.Keys) {
        if ($dll -like 'api-ms-win-*' -or $dll -like 'ext-ms-*') {
            Write-Host ("  {0,-40} api set ({1} imports)" -f $dll, $imports[$dll].Count)
            continue
        }
        $resolved = Resolve-Dll -Name $dll -ExeDir $exe.DirectoryName
        if (-not $resolved) {
            Write-Host ("  {0,-40} MISSING DLL ({1} imports)" -f $dll, $imports[$dll].Count) -ForegroundColor Red
            $problems++
            continue
        }
        $exports = Get-Exports -Tool $tool -Dll $resolved
        $missing = @($imports[$dll] | Where-Object { -not ($exports.Names.Contains($_) -or $exports.Ordinals.Contains($_)) })
        if ($missing.Count -gt 0) {
            Write-Host ("  {0,-40} {1}: {2} missing entry point(s): {3}" -f $dll, $resolved, $missing.Count, ($missing -join ', ')) -ForegroundColor Red
            $problems++
        }
        else {
            Write-Host ("  {0,-40} ok ({1} imports) -> {2}" -f $dll, $imports[$dll].Count, $resolved)
        }
    }
}
if ($problems -gt 0) { Write-Host "`n$problems problem(s) found" -ForegroundColor Red; exit 1 }
Write-Host "`nAll imports resolve." -ForegroundColor Green
