<#
.SYNOPSIS
    Uploads a packaged (and signed) release from artifacts/release/<version> to the update server: an S3 bucket
    (aws cli), an HTTP update service (PUT files + POST manifest, bearer token) or a folder/UNC share. Verifies the
    remote manifest afterwards.
.DESCRIPTION
    Remote layout (all targets):
      <Destination>/<channel>/<version>/<every file of the release folder>
      <Destination>/<channel>/manifest.json            latest manifest of the channel

    package.ps1 builds manifest URLs as <BaseUrl>/<channel>/<version>/<file>, so -Destination (or the public URL in
    front of the bucket) must be the same base as that -BaseUrl.

    Targets
      S3      aws s3 cp per file with content-type and cache-control (packages immutable for a year, manifest
              no-cache). -Destination s3://bucket[/prefix]. Optional -AwsProfile / -AwsRegion.
      Http    PUT <Destination>/<channel>/<version>/<file> for every file and POST <Destination>/api/v1/updates/
              <channel>/manifest with manifest.json, both with `Authorization: Bearer <token>` (-Token or
              CLUBSHELL_PUBLISH_TOKEN). Verification: GET /api/v1/updates/<channel>/manifest?component=<c>&current=0.0.0
              per component and compare version / url / sha256 / size / signature with the local manifest.
      Folder  Copy-Item to a local or UNC path.

    The manifest must be signed (every component has a signature and a manifestSignatures entry, see sign.ps1);
    unsigned manifests are refused unless -AllowUnsigned. -DryRun (or -WhatIf) prints every action without
    touching the remote.
.PARAMETER Target
    S3 | Http | Folder.
.PARAMETER Destination
    s3://bucket/prefix, https://updates.example.uz or \\server\share\updates (see DESCRIPTION).
.PARAMETER Version
    Release version to publish. Default: the newest artifacts/release/<version>/manifest.json.
.PARAMETER Channel
    stable | beta. Default: the channel recorded in manifest.json; a different value is refused (repackage instead).
.PARAMETER ReleaseRoot
    Root of the release layouts. Default: artifacts/release.
.PARAMETER Token
    Bearer token for -Target Http. Default: $env:CLUBSHELL_PUBLISH_TOKEN.
.PARAMETER AwsProfile
    aws cli --profile for -Target S3.
.PARAMETER AwsRegion
    aws cli --region for -Target S3.
.PARAMETER DryRun
    Print the actions only.
.PARAMETER AllowUnsigned
    Publish even when the manifest carries no signatures (development servers only).
.PARAMETER SkipVerify
    Do not read the manifest back from the remote after uploading.
.EXAMPLE
    .\tools\scripts\publish.ps1 -Target S3 -Destination s3://clubshell-updates/clubshell -Version 1.2.0
.EXAMPLE
    .\tools\scripts\publish.ps1 -Target Http -Destination https://updates.example.uz -Channel beta -DryRun
.EXAMPLE
    .\tools\scripts\publish.ps1 -Target Folder -Destination \\fileserver\updates -AllowUnsigned
.OUTPUTS
    Exit code 0 on success, 1 on failure or verification mismatch, 2 when a tool, token or release is missing.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('S3', 'Http', 'Folder')]
    [string] $Target,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Destination,

    [ValidatePattern('^\d+\.\d+\.\d+([-+][0-9A-Za-z.+-]+)?$')]
    [string] $Version,

    [ValidateSet('stable', 'beta')]
    [string] $Channel,

    [string] $ReleaseRoot,
    [string] $Token,
    [string] $AwsProfile,
    [string] $AwsRegion,
    [switch] $DryRun,
    [switch] $AllowUnsigned,
    [switch] $SkipVerify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
if (-not $ReleaseRoot) { $ReleaseRoot = Join-Path $RepoRoot 'artifacts\release' }
if ($DryRun) { $WhatIfPreference = $true }

function Write-Step { param([Parameter(Mandatory)][string] $Message) Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Write-Ok   { param([Parameter(Mandatory)][string] $Message) Write-Host "    $Message" -ForegroundColor Green }
function Write-Warn { param([Parameter(Mandatory)][string] $Message) Write-Host "    WARNING: $Message" -ForegroundColor Yellow }
function Write-Fail { param([Parameter(Mandatory)][string] $Message) Write-Host "    FAILED: $Message" -ForegroundColor Red }

function Invoke-Native {
    <#
    .SYNOPSIS
        Runs a native command and throws when its exit code is non-zero.
    #>
    param([Parameter(Mandatory)][string] $Command, [string[]] $Arguments = @())
    Write-Host "    > $Command $($Arguments -join ' ')" -ForegroundColor DarkGray
    # Native stderr must never become a terminating error (Windows PowerShell 5.1 wraps it in
    # NativeCommandError records when the caller redirects 2>&1); the exit code is what counts.
    $ErrorActionPreference = 'Continue'
    $global:LASTEXITCODE = 0
    & $Command @Arguments | Out-Host
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 0) { throw "'$Command $($Arguments -join ' ')' failed with exit code $LASTEXITCODE" }
}

function Get-PropertyValue {
    param([Parameter(Mandatory)][object] $Object, [Parameter(Mandatory)][string] $Name)
    $property = $Object.PSObject.Properties[$Name]
    if ($property) { return $property.Value }
    return $null
}

function Get-Sha256Hex {
    param([Parameter(Mandatory)][string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-ContentType {
    param([Parameter(Mandatory)][string] $Path)
    switch ([IO.Path]::GetExtension($Path).ToLowerInvariant()) {
        '.msi'  { return 'application/x-msi' }
        '.exe'  { return 'application/vnd.microsoft.portable-executable' }
        '.dll'  { return 'application/octet-stream' }
        '.json' { return 'application/json' }
        '.zip'  { return 'application/zip' }
        '.txt'  { return 'text/plain; charset=utf-8' }
        '.md'   { return 'text/markdown; charset=utf-8' }
        '.ps1'  { return 'text/plain; charset=utf-8' }
        '.pem'  { return 'application/x-pem-file' }
        default { return 'application/octet-stream' }
    }
}

function Get-CacheControl {
    param([Parameter(Mandatory)][string] $RemoteKey)
    if ($RemoteKey -like '*manifest.json') { return 'no-cache, max-age=0' }
    return 'public, max-age=31536000, immutable'
}

function Find-ReleaseDirectory {
    <#
    .SYNOPSIS
        artifacts/release/<version>, or the newest folder containing manifest.json when no version is given.
    #>
    if ($Version) {
        $dir = Join-Path $ReleaseRoot $Version
        if (-not (Test-Path -LiteralPath (Join-Path $dir 'manifest.json'))) { throw "No manifest.json in $dir (run package.ps1 first)" }
        return $dir
    }
    if (-not (Test-Path -LiteralPath $ReleaseRoot)) { throw "Release root not found: $ReleaseRoot" }
    $newest = Get-ChildItem -LiteralPath $ReleaseRoot -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'manifest.json') } |
        Sort-Object { (Get-Item -LiteralPath (Join-Path $_.FullName 'manifest.json')).LastWriteTimeUtc } -Descending |
        Select-Object -First 1
    if (-not $newest) { throw "No packaged release under $ReleaseRoot (run package.ps1 first)" }
    return $newest.FullName
}

function Get-AwsArguments {
    $extra = @()
    if ($AwsProfile) { $extra += @('--profile', $AwsProfile) }
    if ($AwsRegion) { $extra += @('--region', $AwsRegion) }
    return $extra
}

function Publish-File {
    <#
    .SYNOPSIS
        Uploads one local file to <Destination>/<RemoteKey> using the selected target.
    #>
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][string] $RemoteKey)
    $contentType = Get-ContentType -Path $Path
    $cacheControl = Get-CacheControl -RemoteKey $RemoteKey
    $size = (Get-Item -LiteralPath $Path).Length
    $label = "$RemoteKey ($([math]::Round($size / 1MB, 2)) MB, $contentType)"
    if (-not $PSCmdlet.ShouldProcess("$Target $Destination", "Upload $label")) { return }
    switch ($Target) {
        'S3' {
            $uri = $Destination.TrimEnd('/') + '/' + $RemoteKey
            Invoke-Native -Command 'aws' -Arguments (@('s3', 'cp', $Path, $uri, '--content-type', $contentType, '--cache-control', $cacheControl, '--only-show-errors') + @(Get-AwsArguments))
        }
        'Http' {
            $uri = $Destination.TrimEnd('/') + '/' + $RemoteKey
            Write-Host "    PUT $uri" -ForegroundColor DarkGray
            Invoke-RestMethod -Method Put -Uri $uri -InFile $Path -ContentType $contentType -Headers @{ Authorization = "Bearer $script:BearerToken"; 'Cache-Control' = $cacheControl } -TimeoutSec 1800 | Out-Null
        }
        'Folder' {
            $targetPath = Join-Path $Destination ($RemoteKey -replace '/', '\')
            New-Item -ItemType Directory -Path (Split-Path -Parent $targetPath) -Force | Out-Null
            Copy-Item -LiteralPath $Path -Destination $targetPath -Force
        }
    }
    Write-Ok "uploaded $label"
}

function Get-RemoteManifestFile {
    <#
    .SYNOPSIS
        Downloads <Destination>/<channel>/manifest.json (S3 / Folder) to a temp file and returns its path.
    #>
    param([Parameter(Mandatory)][string] $RemoteKey)
    $temp = Join-Path ([IO.Path]::GetTempPath()) ("clubshell-remote-manifest-" + [Guid]::NewGuid().ToString('N') + '.json')
    switch ($Target) {
        'S3' {
            Invoke-Native -Command 'aws' -Arguments (@('s3', 'cp', ($Destination.TrimEnd('/') + '/' + $RemoteKey), $temp, '--only-show-errors') + @(Get-AwsArguments))
        }
        'Folder' {
            Copy-Item -LiteralPath (Join-Path $Destination ($RemoteKey -replace '/', '\')) -Destination $temp -Force
        }
    }
    return $temp
}

function Test-HttpManifest {
    <#
    .SYNOPSIS
        GETs the channel manifest per component from the update API and compares it with the local entries.
        Returns the number of mismatches.
    #>
    param([Parameter(Mandatory)][object] $LocalManifest, [Parameter(Mandatory)][string] $ChannelName)
    $mismatches = 0
    $components = Get-PropertyValue -Object $LocalManifest -Name 'components'
    foreach ($property in $components.PSObject.Properties) {
        $name = $property.Name
        $local = $property.Value
        $uri = $Destination.TrimEnd('/') + "/api/v1/updates/$ChannelName/manifest?component=$name&current=0.0.0"
        Write-Host "    GET $uri" -ForegroundColor DarkGray
        $remote = $null
        try {
            $remote = Invoke-RestMethod -Method Get -Uri $uri -Headers @{ Authorization = "Bearer $script:BearerToken" } -TimeoutSec 60
        } catch {
            Write-Fail "$name`: $($_.Exception.Message)"
            $mismatches++
            continue
        }
        if (-not $remote) { Write-Fail "$name`: server returned no manifest (204?)"; $mismatches++; continue }
        foreach ($field in 'version', 'url', 'sha256', 'size', 'signature') {
            $expected = [string](Get-PropertyValue -Object $local -Name $field)
            $actual = [string](Get-PropertyValue -Object $remote -Name $field)
            if ($expected -ne $actual) {
                Write-Fail "$name.$field differs: local '$expected' remote '$actual'"
                $mismatches++
            }
        }
        if ($mismatches -eq 0) { Write-Ok "$name matches the server" }
    }
    return $mismatches
}

# ---------------------------------------------------------------------------------------------------------------
$exitCode = 0
try {
    Write-Step 'Release'
    $releaseDir = Find-ReleaseDirectory
    $manifestPath = Join-Path $releaseDir 'manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $manifestVersion = [string](Get-PropertyValue -Object $manifest -Name 'version')
    $manifestChannel = [string](Get-PropertyValue -Object $manifest -Name 'channel')
    if (-not $manifestVersion -or -not $manifestChannel) { throw "$manifestPath has no version/channel" }
    if ($Channel -and $Channel -ne $manifestChannel) {
        throw "manifest.json was packaged for channel '$manifestChannel' but -Channel $Channel was requested; repackage with package.ps1 -Channel $Channel"
    }
    $Channel = $manifestChannel
    Write-Ok "$releaseDir  version=$manifestVersion  channel=$Channel"

    # Signatures --------------------------------------------------------------------------------------------
    $components = Get-PropertyValue -Object $manifest -Name 'components'
    if (-not $components) { throw 'manifest.json has no components' }
    $manifestSignatures = Get-PropertyValue -Object $manifest -Name 'manifestSignatures'
    $unsigned = New-Object 'System.Collections.Generic.List[string]'
    foreach ($property in $components.PSObject.Properties) {
        $signature = [string](Get-PropertyValue -Object $property.Value -Name 'signature')
        $canonical = $null
        if ($manifestSignatures) { $canonical = [string](Get-PropertyValue -Object $manifestSignatures -Name $property.Name) }
        if (-not $signature -or -not $canonical) { $unsigned.Add($property.Name) }
    }
    if ($unsigned.Count -gt 0) {
        if ($AllowUnsigned) {
            Write-Warn "unsigned component(s): $($unsigned -join ', ') (publishing anyway: -AllowUnsigned)"
        } else {
            Write-Host "ERROR: manifest is not signed for: $($unsigned -join ', '). Run sign.ps1 -ManifestPath $manifestPath -ManifestKeyPem <key> first (or -AllowUnsigned for a dev server)." -ForegroundColor Red
            exit 2
        }
    } else {
        Write-Ok 'manifest signed (package + canonical signatures present for every component)'
    }

    # Target prerequisites ------------------------------------------------------------------------------------
    Write-Step "Target $Target -> $Destination"
    $script:BearerToken = $null
    switch ($Target) {
        'S3' {
            if ($Destination -notmatch '^s3://[^/]+') { throw "-Destination must look like s3://bucket[/prefix] for -Target S3" }
            if (-not (Get-Command aws -ErrorAction SilentlyContinue)) {
                if (-not $WhatIfPreference) {
                    Write-Host 'ERROR: aws cli not found (winget install Amazon.AWSCLI).' -ForegroundColor Red
                    exit 2
                }
                Write-Warn 'aws cli not found (winget install Amazon.AWSCLI); continuing because this is a dry run'
            }
        }
        'Http' {
            if ($Destination -notmatch '^https?://') { throw "-Destination must be an http(s) URL for -Target Http" }
            $script:BearerToken = $Token
            if (-not $script:BearerToken) { $script:BearerToken = $env:CLUBSHELL_PUBLISH_TOKEN }
            if (-not $script:BearerToken) {
                Write-Host 'ERROR: no bearer token: pass -Token or set CLUBSHELL_PUBLISH_TOKEN.' -ForegroundColor Red
                exit 2
            }
            if ($Destination -notmatch '^https://' -and $Destination -notmatch '^http://(localhost|127\.0\.0\.1)') { Write-Warn 'publishing over plain HTTP to a non-local host' }
            $urlPrefix = $Destination.TrimEnd('/') + "/$Channel/$manifestVersion/"
            foreach ($property in $components.PSObject.Properties) {
                $url = [string](Get-PropertyValue -Object $property.Value -Name 'url')
                if (-not $url.StartsWith($urlPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    Write-Warn "$($property.Name).url ($url) is not under $urlPrefix; clients will download from the manifest URL, not from this upload"
                }
            }
        }
        'Folder' {
            if (-not (Test-Path -LiteralPath $Destination -PathType Container)) {
                if ($PSCmdlet.ShouldProcess($Destination, 'Create destination folder')) { New-Item -ItemType Directory -Path $Destination -Force | Out-Null }
            }
        }
    }

    # Upload --------------------------------------------------------------------------------------------------
    Write-Step "Uploading $Channel/$manifestVersion"
    $files = @(Get-ChildItem -LiteralPath $releaseDir -Recurse -File | Sort-Object FullName)
    # Packages first, manifest.json last so a half-finished upload never advertises missing files.
    $ordered = @($files | Where-Object { $_.Name -ne 'manifest.json' }) + @($files | Where-Object { $_.Name -eq 'manifest.json' })
    foreach ($file in $ordered) {
        $relative = $file.FullName.Substring($releaseDir.Length).TrimStart('\', '/').Replace('\', '/')
        Publish-File -Path $file.FullName -RemoteKey "$Channel/$manifestVersion/$relative"
    }

    Write-Step "Publishing channel manifest $Channel/manifest.json"
    switch ($Target) {
        'Http' {
            $uri = $Destination.TrimEnd('/') + "/api/v1/updates/$Channel/manifest"
            if ($PSCmdlet.ShouldProcess($uri, 'POST manifest.json')) {
                Write-Host "    POST $uri" -ForegroundColor DarkGray
                Invoke-RestMethod -Method Post -Uri $uri -InFile $manifestPath -ContentType 'application/json' -Headers @{ Authorization = "Bearer $script:BearerToken" } -TimeoutSec 120 | Out-Null
                Write-Ok 'manifest posted'
            }
        }
        default { Publish-File -Path $manifestPath -RemoteKey "$Channel/manifest.json" }
    }

    # Verify --------------------------------------------------------------------------------------------------
    if ($SkipVerify) {
        Write-Warn 'remote verification skipped (-SkipVerify)'
    } elseif ($WhatIfPreference) {
        Write-Host "`n(dry run: nothing uploaded, nothing to verify)" -ForegroundColor DarkGray
    } else {
        Write-Step 'Verifying remote manifest'
        $mismatches = 0
        if ($Target -eq 'Http') {
            $mismatches = Test-HttpManifest -LocalManifest $manifest -ChannelName $Channel
        } else {
            $remoteFile = Get-RemoteManifestFile -RemoteKey "$Channel/manifest.json"
            try {
                $localHash = Get-Sha256Hex -Path $manifestPath
                $remoteHash = Get-Sha256Hex -Path $remoteFile
                if ($localHash -eq $remoteHash) {
                    Write-Ok "remote $Channel/manifest.json is byte-identical to the local manifest (sha256 $($localHash.Substring(0, 16))...)"
                } else {
                    Write-Fail "remote $Channel/manifest.json differs from the local manifest"
                    $mismatches++
                }
            } finally {
                Remove-Item -LiteralPath $remoteFile -Force -ErrorAction SilentlyContinue
            }
        }
        if ($mismatches -gt 0) { throw "$mismatches verification mismatch(es)" }
    }

    Write-Host "`nPublished ClubShell $manifestVersion to $Target $Destination ($Channel)." -ForegroundColor Green
} catch {
    $exitCode = 1
    Write-Host "`nPUBLISH FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.ScriptStackTrace) { Write-Verbose $_.ScriptStackTrace }
}
exit $exitCode
