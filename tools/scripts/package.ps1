<#
.SYNOPSIS
    Assembles a ClubShell release under artifacts/release/<version>: Agent publish, Shell bundle (Tauri msi/nsis),
    config templates + themes, install scripts, the WiX Agent MSI and setup bundle, an UpdateManifest-shaped
    manifest.json (per component, unsigned until sign.ps1 runs), SHA256SUMS.txt and a zip of the whole folder.
.DESCRIPTION
    Layout produced (file names carry the version):

      artifacts/release/<version>/
        ClubShell-<version>.msi             WiX Agent MSI          -> manifest component "agent"
        ClubShellSetup-<version>.exe        WiX bundle (Agent + Shell + WebView2 bootstrapper)
        ClubShell-Shell-<version>.msi       Tauri MSI              -> manifest component "shell"
        ClubShell-Shell-<version>-setup.exe Tauri NSIS installer (when built)
        agent/                              published Agent payload (+ config/, Install/*.ps1 for manual installs)
        manifest.json                       { version, channel, publishedAt, components{agent,shell}, packages, files }
        SHA256SUMS.txt
      artifacts/release/ClubShell-<version>.zip

    The WiX project is built with `dotnet build installer/wix/ClubShell.Installer.wixproj` (WiX SDK from NuGet,
    no global wix tool) passing AgentPublishDir, ShellBundleDir (the cargo release folder holding
    clubshell-shell.exe) and Version; configuration Release (-p:BuildBundle=false) yields the MSI, configuration
    Bundle (-p:MsiPath=<that MSI>) yields ClubShellSetup.exe.

    Authenticode: when CODESIGN_PFX_PATH (+ CODESIGN_PFX_PASSWORD) is set, sign.ps1 signs ClubShellAgent.exe before
    WiX packs it and every *.msi / *.exe of the release folder before the hashes are taken, so manifest.json and
    SHA256SUMS.txt describe the signed files. Without it, run sign.ps1 afterwards (its manifest mode refreshes the
    hashes).

    Each manifest component is an UpdateManifest (ClubShell.Contracts): channel, component, version, url, sha256,
    size, signature (empty placeholder), releaseNotes, mandatory, publishedAt, minAgentVersion. `url` is built from
    -BaseUrl: placeholders {channel} {version} {file} {component} are substituted; without placeholders the layout
    <BaseUrl>/<channel>/<version>/<file> is used (what publish.ps1 uploads to).

    Requires a prior `build.ps1` (Shell bundle) unless the Agent publish is done here. Supports -WhatIf.
.PARAMETER Version
    Release version (semver). Default: artifacts/build-info.json -> version, else 1.0.0-local.
.PARAMETER Configuration
    MSBuild configuration for the Agent publish. Default: Release.
.PARAMETER Channel
    Update channel written into the manifest: stable | beta. Default: stable.
.PARAMETER BaseUrl
    Download URL template for manifest.url (see DESCRIPTION). Default: https://updates.clubshell.uz.
.PARAMETER NotesFile
    Markdown file with the release notes. Default: the `## [<version>]` / `## <version>` section of CHANGELOG.md,
    else "ClubShell <version>".
.PARAMETER Mandatory
    Mark both components mandatory (applied even during a session after the 60 s notice).
.PARAMETER MinAgentVersion
    minAgentVersion written into the shell component. Default: the release version.
.PARAMETER ForceAgentPublish
    Re-run `dotnet publish` even when artifacts/publish/agent already contains ClubShellAgent.exe.
.PARAMETER SkipInstaller
    Do not build the WiX MSI/bundle (the "agent" component is then omitted from the manifest).
.PARAMETER NoZip
    Do not create artifacts/release/ClubShell-<version>.zip.
.PARAMETER OutputRoot
    Root of the release layout. Default: artifacts/release.
.EXAMPLE
    .\tools\scripts\package.ps1 -Version 1.2.0 -Channel stable -BaseUrl https://updates.example.uz/clubshell
.EXAMPLE
    .\tools\scripts\package.ps1 -Channel beta -NotesFile .\notes.md -WhatIf
.OUTPUTS
    Exit code 0 on success, 1 on failure, 2 when a required tool or input is missing.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([-+][0-9A-Za-z.+-]+)?$')]
    [string] $Version,

    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [ValidateSet('stable', 'beta')]
    [string] $Channel = 'stable',

    [ValidatePattern('^https?://')]
    [string] $BaseUrl = 'https://updates.clubshell.uz',

    [string] $NotesFile,
    [switch] $Mandatory,

    [ValidatePattern('^$|^\d+\.\d+\.\d+([-+][0-9A-Za-z.+-]+)?$')]
    [string] $MinAgentVersion,

    [switch] $ForceAgentPublish,
    [switch] $SkipInstaller,
    [switch] $NoZip,
    [string] $OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$RepoRoot     = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$ArtifactsDir = Join-Path $RepoRoot 'artifacts'
if (-not $OutputRoot) { $OutputRoot = Join-Path $ArtifactsDir 'release' }

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

function Get-Sha256Hex {
    param([Parameter(Mandatory)][string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Find-FirstDirectory {
    param([Parameter(Mandatory)][string[]] $Candidates)
    foreach ($candidate in $Candidates) {
        $full = Join-Path $RepoRoot $candidate
        if (Test-Path -LiteralPath $full -PathType Container) { return $full }
    }
    return $null
}

function Get-ReleaseNotes {
    <#
    .SYNOPSIS
        Release notes markdown: -NotesFile, else the CHANGELOG.md section for the version, else a one-liner.
    #>
    param([Parameter(Mandatory)][string] $ForVersion)
    if ($NotesFile) {
        if (-not (Test-Path -LiteralPath $NotesFile)) { throw "Notes file not found: $NotesFile" }
        return (Get-Content -LiteralPath $NotesFile -Raw).Trim()
    }
    $changelog = Join-Path $RepoRoot 'CHANGELOG.md'
    if (Test-Path -LiteralPath $changelog) {
        $section = New-Object 'System.Collections.Generic.List[string]'
        $inside = $false
        $escaped = [regex]::Escape($ForVersion)
        foreach ($line in Get-Content -LiteralPath $changelog) {
            if ($line -match "^##\s+\[?v?$escaped\]?(\s|$)") { $inside = $true; continue }
            if ($inside -and $line -match '^##\s') { break }
            if ($inside) { $section.Add($line) }
        }
        $text = ($section -join "`n").Trim()
        if ($text) { return $text }
        Write-Warn "CHANGELOG.md has no section for $ForVersion"
    }
    return "ClubShell $ForVersion"
}

function Get-PackageUrl {
    <#
    .SYNOPSIS
        Expands the -BaseUrl template for one package file.
    #>
    param([Parameter(Mandatory)][string] $Component, [Parameter(Mandatory)][string] $FileName)
    $template = $BaseUrl
    if ($template -notmatch '\{(channel|version|file|component)\}') {
        $template = $template.TrimEnd('/') + '/{channel}/{version}/{file}'
    }
    return $template.Replace('{channel}', $Channel).Replace('{version}', $Version).Replace('{file}', $FileName).Replace('{component}', $Component)
}

function New-ComponentManifest {
    <#
    .SYNOPSIS
        One UpdateManifest (ClubShell.Contracts.Commands.UpdateManifest) for a package file, signature left empty.
    #>
    param(
        [Parameter(Mandatory)][string] $Component,
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $ReleaseNotes,
        [Parameter(Mandatory)][string] $PublishedAt,
        [string] $MinAgent = ''
    )
    $file = Get-Item -LiteralPath $Path
    $minAgentValue = $null
    if ($MinAgent) { $minAgentValue = $MinAgent }
    return [ordered]@{
        channel         = $Channel
        component       = $Component
        version         = $Version
        url             = Get-PackageUrl -Component $Component -FileName $file.Name
        sha256          = Get-Sha256Hex -Path $file.FullName
        size            = [long]$file.Length
        signature       = ''
        releaseNotes    = $ReleaseNotes
        mandatory       = [bool]$Mandatory
        publishedAt     = $PublishedAt
        minAgentVersion = $minAgentValue
    }
}

function Invoke-Sign {
    <#
    .SYNOPSIS
        Authenticode-signs files with sign.ps1 (certificate from CODESIGN_PFX_PATH / CODESIGN_PFX_PASSWORD;
        files that already carry a valid signature are skipped there).
    #>
    param([Parameter(Mandatory)][string[]] $Files)
    $signScript = Join-Path $PSScriptRoot 'sign.ps1'
    Write-Host "    > sign.ps1 -Files $($Files -join ', ')" -ForegroundColor DarkGray
    $global:LASTEXITCODE = 0
    & $signScript -Files $Files
    if ($LASTEXITCODE -ne 0) { throw "sign.ps1 failed with exit code $LASTEXITCODE" }
}

function Write-TextFile {
    <#
    .SYNOPSIS
        Writes text as UTF-8 without a BOM and with LF line endings (JSON consumers and `sha256sum --check` on
        Linux reject the BOM / CRLF that Set-Content produces on Windows PowerShell 5.1).
    #>
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][AllowEmptyString()][string] $Text)
    [IO.File]::WriteAllText($Path, ($Text -replace "`r`n", "`n").TrimEnd("`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))
}

function Copy-Tree {
    param([Parameter(Mandatory)][string] $Source, [Parameter(Mandatory)][string] $Destination)
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item -Path (Join-Path $Source '*') -Destination $Destination -Recurse -Force
}

# ---------------------------------------------------------------------------------------------------------------
# Inputs
# ---------------------------------------------------------------------------------------------------------------
$exitCode = 0
try {
    if (-not $Version) {
        $buildInfo = Join-Path $ArtifactsDir 'build-info.json'
        if (Test-Path -LiteralPath $buildInfo) {
            $info = Get-Content -LiteralPath $buildInfo -Raw | ConvertFrom-Json
            if ($info.PSObject.Properties['version'] -and $info.version) { $Version = [string]$info.version }
        }
        if (-not $Version) { $Version = '1.0.0-local' }
    }
    $numericVersion = ([regex]::Match($Version, '^\d+\.\d+\.\d+')).Value
    if (-not $MinAgentVersion) { $MinAgentVersion = $Version }
    $publishedAt = [DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
    $releaseDir = Join-Path $OutputRoot $Version
    $agentPublish = Join-Path $ArtifactsDir 'publish\agent'

    Write-Host "ClubShell package  version=$Version  channel=$Channel  -> $releaseDir" -ForegroundColor White
    if ($BaseUrl -eq 'https://updates.clubshell.uz') { Write-Warn "using the default -BaseUrl ($BaseUrl); pass -BaseUrl for a real deployment" }

    Write-Step 'Checking toolchain'
    $hasDotnetSdk = Test-DotnetSdk
    if ($hasDotnetSdk) { Write-Ok "dotnet SDK $((Get-NativeOutput -Command 'dotnet' -Arguments @('--version')).Output -join '')" }
    $needsPublish = $ForceAgentPublish -or -not (Test-Path -LiteralPath (Join-Path $agentPublish 'ClubShellAgent.exe'))
    if (($needsPublish -or -not $SkipInstaller) -and -not $hasDotnetSdk) {
        Write-Host 'ERROR: the .NET 8 SDK is required to publish the Agent / build the installer (winget install Microsoft.DotNet.SDK.8).' -ForegroundColor Red
        exit 2
    }

    # -----------------------------------------------------------------------------------------------------------
    # Agent publish
    # -----------------------------------------------------------------------------------------------------------
    Write-Step 'Agent'
    if ($needsPublish) {
        if ($PSCmdlet.ShouldProcess($agentPublish, 'dotnet publish ClubShell.Agent')) {
            Invoke-Native -Command 'dotnet' -Arguments @(
                'publish', (Join-Path $RepoRoot 'src\ClubShell.Agent\ClubShell.Agent.csproj'),
                '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'false',
                '-p:PublishSingleFile=true', "-p:Version=$Version", '-o', $agentPublish, '-nologo')
        }
    } else {
        Write-Ok "reusing $agentPublish (-ForceAgentPublish to republish)"
    }
    if ($env:CODESIGN_PFX_PATH -and $PSCmdlet.ShouldProcess((Join-Path $agentPublish 'ClubShellAgent.exe'), 'Authenticode sign (CODESIGN_PFX_PATH)')) {
        Invoke-Sign -Files @((Join-Path $agentPublish 'ClubShellAgent.exe'))
    }

    # -----------------------------------------------------------------------------------------------------------
    # Shell bundle
    # -----------------------------------------------------------------------------------------------------------
    Write-Step 'Shell bundle'
    $bundleDir = Find-FirstDirectory -Candidates @(
        'target\x86_64-pc-windows-msvc\release\bundle',
        'target\release\bundle',
        'apps\shell\src-tauri\target\x86_64-pc-windows-msvc\release\bundle',
        'apps\shell\src-tauri\target\release\bundle')
    if (-not $bundleDir) {
        Write-Host 'ERROR: Tauri bundle not found; run .\tools\scripts\build.ps1 -Target Rust first.' -ForegroundColor Red
        exit 2
    }
    $shellMsi = Get-ChildItem -LiteralPath $bundleDir -Recurse -File -Filter '*.msi' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $shellNsis = Get-ChildItem -LiteralPath $bundleDir -Recurse -File -Filter '*-setup.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $shellMsi) {
        Write-Host "ERROR: no Shell .msi under $bundleDir." -ForegroundColor Red
        exit 2
    }
    Write-Ok "$($shellMsi.FullName)"
    if ($shellNsis) { Write-Ok "$($shellNsis.FullName)" }

    # -----------------------------------------------------------------------------------------------------------
    # Release layout
    # -----------------------------------------------------------------------------------------------------------
    Write-Step "Release layout $releaseDir"
    if ($PSCmdlet.ShouldProcess($releaseDir, 'Create release layout')) {
        if (Test-Path -LiteralPath $releaseDir) { Remove-Item -LiteralPath $releaseDir -Recurse -Force }
        New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

        # agent/: published payload + config templates/themes + install scripts (install.ps1 expects exe, config\, Install\).
        $agentDir = Join-Path $releaseDir 'agent'
        Copy-Tree -Source $agentPublish -Destination $agentDir
        Copy-Tree -Source (Join-Path $RepoRoot 'config') -Destination (Join-Path $agentDir 'config')
        Copy-Tree -Source (Join-Path $RepoRoot 'src\ClubShell.Agent\Install') -Destination (Join-Path $agentDir 'Install')
        Write-Ok 'agent/ (payload, config/, Install/)'

        Copy-Item -LiteralPath $shellMsi.FullName -Destination (Join-Path $releaseDir "ClubShell-Shell-$Version.msi") -Force
        Write-Ok "ClubShell-Shell-$Version.msi"
        if ($shellNsis) {
            Copy-Item -LiteralPath $shellNsis.FullName -Destination (Join-Path $releaseDir "ClubShell-Shell-$Version-setup.exe") -Force
            Write-Ok "ClubShell-Shell-$Version-setup.exe"
        }
    }

    # -----------------------------------------------------------------------------------------------------------
    # WiX MSI + bundle
    # -----------------------------------------------------------------------------------------------------------
    $agentMsiName = $null
    if (-not $SkipInstaller) {
        Write-Step 'WiX installer'
        $wixproj = Join-Path $RepoRoot 'installer\wix\ClubShell.Installer.wixproj'
        if (-not (Test-Path -LiteralPath $wixproj) -or (Get-Item -LiteralPath $wixproj).Length -eq 0) {
            throw "WiX project missing or empty: $wixproj (use -SkipInstaller to package without the MSI)"
        }
        $wixOut = Join-Path $ArtifactsDir 'installer'
        $msiOut = Join-Path $wixOut 'msi'
        $bundleOut = Join-Path $wixOut 'bundle'
        # Components.wxs takes clubshell-shell.exe from ShellBundleDir: the cargo release folder next to bundle\,
        # or artifacts\shell where build.ps1 copies the exe.
        $shellExeDir = Split-Path -Parent $bundleDir
        if (-not (Test-Path -LiteralPath (Join-Path $shellExeDir 'clubshell-shell.exe'))) { $shellExeDir = Join-Path $ArtifactsDir 'shell' }
        if (-not (Test-Path -LiteralPath (Join-Path $shellExeDir 'clubshell-shell.exe'))) {
            throw "clubshell-shell.exe not found in $(Split-Path -Parent $bundleDir) or $shellExeDir (run build.ps1 -Target Rust)"
        }
        if ($PSCmdlet.ShouldProcess($wixproj, 'dotnet build (Release + Bundle)')) {
            if (Test-Path -LiteralPath $wixOut) { Remove-Item -LiteralPath $wixOut -Recurse -Force }
            $wixProps = @("-p:AgentPublishDir=$agentPublish", "-p:ShellBundleDir=$shellExeDir", "-p:Version=$numericVersion", '-nologo')
            # Release with BuildBundle=false: the MSI only (its default chained bundle build would look for the MSI
            # under installer\wix\bin, not under -o). The bundle is built explicitly against that MSI.
            Invoke-Native -Command 'dotnet' -Arguments (@('build', $wixproj, '-c', 'Release', '-o', $msiOut, '-p:BuildBundle=false') + $wixProps)
            $agentMsi = Get-ChildItem -LiteralPath $msiOut -Recurse -File -Filter '*.msi' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if (-not $agentMsi) { throw "WiX build produced no .msi under $msiOut" }
            # The bundle embeds the MSI, so the MSI is signed first; the bundle exe is signed with the release folder.
            if ($env:CODESIGN_PFX_PATH) { Invoke-Sign -Files @($agentMsi.FullName) }
            Invoke-Native -Command 'dotnet' -Arguments (@('build', $wixproj, '-c', 'Bundle', '-o', $bundleOut, "-p:MsiPath=$($agentMsi.FullName)") + $wixProps)

            $agentMsiName = "ClubShell-$Version.msi"
            Copy-Item -LiteralPath $agentMsi.FullName -Destination (Join-Path $releaseDir $agentMsiName) -Force
            Write-Ok $agentMsiName

            $setupExe = Get-ChildItem -LiteralPath $bundleOut -Recurse -File -Filter '*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if (-not $setupExe) { throw "WiX bundle build produced no .exe under $bundleOut" }
            Copy-Item -LiteralPath $setupExe.FullName -Destination (Join-Path $releaseDir "ClubShellSetup-$Version.exe") -Force
            Write-Ok "ClubShellSetup-$Version.exe"
        }
    } else {
        Write-Warn 'installer skipped (-SkipInstaller): no Agent MSI, manifest will only describe the shell component'
    }

    # -----------------------------------------------------------------------------------------------------------
    # Authenticode (before hashing, so manifest.json / SHA256SUMS.txt describe the signed files)
    # -----------------------------------------------------------------------------------------------------------
    if ($env:CODESIGN_PFX_PATH) {
        if ($PSCmdlet.ShouldProcess($releaseDir, 'Authenticode sign *.msi, *.exe (CODESIGN_PFX_PATH)')) {
            Write-Step 'Authenticode signing'
            Invoke-Sign -Files @((Join-Path $releaseDir '*.msi'), (Join-Path $releaseDir '*.exe'))
        }
    } else {
        Write-Warn 'CODESIGN_PFX_PATH not set: installers are not Authenticode-signed (sign.ps1 -Files ... afterwards, then sign.ps1 -ManifestPath to refresh the hashes)'
    }

    # -----------------------------------------------------------------------------------------------------------
    # manifest.json + SHA256SUMS.txt + zip
    # -----------------------------------------------------------------------------------------------------------
    Write-Step 'Manifest'
    if ($PSCmdlet.ShouldProcess((Join-Path $releaseDir 'manifest.json'), 'Write manifest')) {
        $notes = Get-ReleaseNotes -ForVersion $Version
        $components = [ordered]@{}
        $packages = [ordered]@{}
        if ($agentMsiName) {
            $components['agent'] = New-ComponentManifest -Component 'agent' -Path (Join-Path $releaseDir $agentMsiName) -ReleaseNotes $notes -PublishedAt $publishedAt
            $packages['agent'] = $agentMsiName
        }
        $components['shell'] = New-ComponentManifest -Component 'shell' -Path (Join-Path $releaseDir "ClubShell-Shell-$Version.msi") -ReleaseNotes $notes -PublishedAt $publishedAt -MinAgent $MinAgentVersion
        $packages['shell'] = "ClubShell-Shell-$Version.msi"

        $files = New-Object 'System.Collections.Generic.List[object]'
        $sums = New-Object 'System.Collections.Generic.List[string]'
        foreach ($file in Get-ChildItem -LiteralPath $releaseDir -Recurse -File | Sort-Object FullName) {
            $relative = $file.FullName.Substring($releaseDir.Length).TrimStart('\', '/').Replace('\', '/')
            $hash = Get-Sha256Hex -Path $file.FullName
            $files.Add([ordered]@{ path = $relative; size = [long]$file.Length; sha256 = $hash })
            $sums.Add("$hash *$relative")
        }

        $manifest = [ordered]@{
            schema             = 1
            product            = 'ClubShell'
            version            = $Version
            channel            = $Channel
            publishedAt        = $publishedAt
            baseUrl            = $BaseUrl
            components         = $components
            packages           = $packages
            manifestSignatures = [ordered]@{}
            files              = $files.ToArray()
        }
        Write-TextFile -Path (Join-Path $releaseDir 'manifest.json') -Text ($manifest | ConvertTo-Json -Depth 8)
        Write-TextFile -Path (Join-Path $releaseDir 'SHA256SUMS.txt') -Text ($sums -join "`n")
        Write-Ok "manifest.json ($($components.Count) component(s), $($files.Count) file(s)); signature placeholders empty until sign.ps1 -ManifestPath"
    }

    if (-not $NoZip) {
        $zip = Join-Path $OutputRoot "ClubShell-$Version.zip"
        if ($PSCmdlet.ShouldProcess($zip, 'Compress release folder')) {
            Write-Step 'Zip'
            if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
            Compress-Archive -Path (Join-Path $releaseDir '*') -DestinationPath $zip -CompressionLevel Optimal
            Write-Ok "$zip ($([math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)) MB)"
        }
    }

    Write-Host "`nRelease $Version packaged in $releaseDir" -ForegroundColor Green
    Write-Host 'Next: .\tools\scripts\sign.ps1 -ManifestPath <release>\manifest.json -ManifestKeyPem <key.pem> ; publish.ps1   (sign.ps1 -Files "<release>\*.msi","<release>\*.exe" first when CODESIGN_PFX_PATH was not set)' -ForegroundColor DarkGray
} catch {
    $exitCode = 1
    Write-Host "`nPACKAGE FAILED: $($_.Exception.Message)" -ForegroundColor Red
}
exit $exitCode
