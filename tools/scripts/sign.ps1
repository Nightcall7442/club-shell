<#
.SYNOPSIS
    Signs ClubShell release artifacts: Authenticode (signtool) for exe / msi / dll, and the update manifest
    (RSA-PSS-SHA256, base64) exactly as ClubShell.Core.Security.Signing verifies it. -Verify re-checks either.
.DESCRIPTION
    Authenticode mode (default parameter set)
      Locates signtool.exe (-SignTool, PATH, or the newest Windows Kits 10 bin\<ver>\x64), signs every file matched
      by -Files (globs; `**` recurses) with SHA-256 digest and an RFC 3161 timestamp, in parallel (-ThrottleLimit),
      then runs `signtool verify /pa` on each. Certificate: -PfxPath/-PfxPassword (or the CODESIGN_PFX_PATH /
      CODESIGN_PFX_PASSWORD environment variables) or -Thumbprint from the certificate store. Files whose signature
      is already valid are skipped unless -Force. With -Verify only the verification runs.

    Manifest mode (-ManifestPath)
      For every component in manifest.json (package.ps1 layout: components.{agent,shell} = UpdateManifest,
      packages.{component} = package file) the script recomputes size + sha256 of the package (Authenticode changes
      them), sets components.<c>.signature = base64 RSA-PSS-SHA256 over the raw package bytes (what
      Signing.VerifyFileSignatureAsync / UpdateDownloader.VerifyAsync check on the client), and sets
      manifestSignatures.<c> = base64 RSA-PSS-SHA256 over Signing.ManifestCanonicalBytes, i.e. the canonical JSON
      {"component","version","url","sha256","size","publishedAt"} (that order, no whitespace, UTF-8, System.Text.Json
      default escaping, publishedAt as yyyy-MM-ddTHH:mm:ss.fffZ UTC) verified by Signing.VerifyManifest.
      SHA256SUMS.txt and the sibling ClubShell-<version>.zip are refreshed. The private key is a PEM
      (PKCS#8 "PRIVATE KEY" or PKCS#1 "RSA PRIVATE KEY"); the public key for -Verify is a PEM "PUBLIC KEY",
      "RSA PUBLIC KEY" or "CERTIFICATE" (the same formats Signing.LoadRsaPublicKey accepts), or is derived from the
      private key when only that is given. RSA-PSS uses MGF1-SHA256 with a 32-byte salt (.NET default), matching
      RSASignaturePadding.Pss on the client.

    Works on Windows PowerShell 5.1 (RSACng + a small DER reader) and PowerShell 7 (RSA.ImportFromPem).
    Supports -WhatIf.
.PARAMETER Files
    Files or globs to Authenticode-sign. Default: every *.exe, *.msi and *.dll under artifacts/release.
.PARAMETER SignTool
    Path to signtool.exe. Default: PATH, then the newest Windows 10/11 SDK.
.PARAMETER PfxPath
    Code-signing certificate (.pfx). Default: $env:CODESIGN_PFX_PATH.
.PARAMETER PfxPassword
    Password of the .pfx as a SecureString. Default: $env:CODESIGN_PFX_PASSWORD.
.PARAMETER Thumbprint
    SHA-1 thumbprint of a certificate in the CurrentUser\My store (-MachineStore: LocalMachine\My) instead of a .pfx.
.PARAMETER MachineStore
    Look the thumbprint up in the LocalMachine store (signtool /sm).
.PARAMETER TimestampUrl
    RFC 3161 timestamp server. Default: http://timestamp.digicert.com.
.PARAMETER Description
    Signature description (signtool /d). Default: ClubShell.
.PARAMETER DescriptionUrl
    Signature URL (signtool /du). Default: https://clubshell.uz.
.PARAMETER ThrottleLimit
    Maximum concurrent signtool processes. Default: 4.
.PARAMETER Force
    Re-sign files that already carry a valid signature.
.PARAMETER ManifestPath
    Path to a release manifest.json (switches to manifest mode).
.PARAMETER ManifestKeyPem
    RSA private key PEM used to sign the manifest (required unless -Verify).
.PARAMETER ManifestPublicKeyPem
    RSA public key / certificate PEM used by -Verify (defaults to the public part of -ManifestKeyPem).
.PARAMETER Verify
    Only verify: Authenticode signatures of -Files, or the manifest signatures with the public key.
.EXAMPLE
    .\tools\scripts\sign.ps1 -Files 'artifacts\release\1.2.0\*.msi','artifacts\release\1.2.0\*.exe'
    Uses CODESIGN_PFX_PATH / CODESIGN_PFX_PASSWORD from the environment.
.EXAMPLE
    .\tools\scripts\sign.ps1 -Thumbprint 0123456789ABCDEF0123456789ABCDEF01234567 -MachineStore -Files 'artifacts\release\**\*.exe'
.EXAMPLE
    .\tools\scripts\sign.ps1 -ManifestPath artifacts\release\1.2.0\manifest.json -ManifestKeyPem C:\keys\update-private.pem
.EXAMPLE
    .\tools\scripts\sign.ps1 -ManifestPath artifacts\release\1.2.0\manifest.json -ManifestPublicKeyPem C:\keys\update-public.pem -Verify
.OUTPUTS
    Exit code 0 when everything is signed/verified, 1 on any failure, 2 when a tool, key or certificate is missing.
#>
[CmdletBinding(SupportsShouldProcess = $true, DefaultParameterSetName = 'Authenticode')]
param(
    [Parameter(ParameterSetName = 'Authenticode', Position = 0)]
    [string[]] $Files,

    [Parameter(ParameterSetName = 'Authenticode')]
    [string] $SignTool,

    [Parameter(ParameterSetName = 'Authenticode')]
    [string] $PfxPath,

    [Parameter(ParameterSetName = 'Authenticode')]
    [System.Security.SecureString] $PfxPassword,

    [Parameter(ParameterSetName = 'Authenticode')]
    [ValidatePattern('^$|^[0-9A-Fa-f]{40}$')]
    [string] $Thumbprint,

    [Parameter(ParameterSetName = 'Authenticode')]
    [switch] $MachineStore,

    [Parameter(ParameterSetName = 'Authenticode')]
    [ValidatePattern('^https?://')]
    [string] $TimestampUrl = 'http://timestamp.digicert.com',

    [Parameter(ParameterSetName = 'Authenticode')]
    [string] $Description = 'ClubShell',

    [Parameter(ParameterSetName = 'Authenticode')]
    [string] $DescriptionUrl = 'https://clubshell.uz',

    [Parameter(ParameterSetName = 'Authenticode')]
    [ValidateRange(1, 16)]
    [int] $ThrottleLimit = 4,

    [Parameter(ParameterSetName = 'Authenticode')]
    [switch] $Force,

    [Parameter(ParameterSetName = 'Manifest', Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $ManifestPath,

    [Parameter(ParameterSetName = 'Manifest')]
    [string] $ManifestKeyPem,

    [Parameter(ParameterSetName = 'Manifest')]
    [string] $ManifestPublicKeyPem,

    [switch] $Verify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$Sha256 = [System.Security.Cryptography.HashAlgorithmName]::SHA256
$Pss = [System.Security.Cryptography.RSASignaturePadding]::Pss
$CanonicalTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'"

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

function ConvertTo-PlainText {
    param([Parameter(Mandatory)][System.Security.SecureString] $Secure)
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

function Get-Sha256Hex {
    param([Parameter(Mandatory)][string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-TextFile {
    <#
    .SYNOPSIS
        Writes text as UTF-8 without a BOM and with LF line endings, like package.ps1 (JSON consumers and
        `sha256sum --check` on Linux reject the BOM / CRLF that Set-Content produces on Windows PowerShell 5.1).
    #>
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][AllowEmptyString()][string] $Text)
    [IO.File]::WriteAllText($Path, ($Text -replace "`r`n", "`n").TrimEnd("`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))
}

# ---------------------------------------------------------------------------------------------------------------
# Authenticode helpers
# ---------------------------------------------------------------------------------------------------------------
function Find-SignTool {
    <#
    .SYNOPSIS
        signtool.exe from -SignTool, PATH or the newest Windows Kits 10 SDK (x64). $null when not found.
    #>
    if ($SignTool) {
        if (Test-Path -LiteralPath $SignTool) { return (Resolve-Path -LiteralPath $SignTool).Path }
        throw "signtool not found at $SignTool"
    }
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    foreach ($kitsRoot in @((Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'), (Join-Path $env:ProgramFiles 'Windows Kits\10\bin'))) {
        if (-not (Test-Path -LiteralPath $kitsRoot)) { continue }
        $candidates = Get-ChildItem -LiteralPath $kitsRoot -Directory |
            Where-Object { $_.Name -match '^10\.\d+\.\d+\.\d+$' } |
            Sort-Object { [Version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
            Where-Object { Test-Path -LiteralPath $_ }
        if ($candidates) { return @($candidates)[0] }
    }
    return $null
}

function Resolve-FileGlobs {
    <#
    .SYNOPSIS
        Expands file patterns (PowerShell wildcards; a `**` segment recurses) relative to the repo root.
    #>
    param([Parameter(Mandatory)][string[]] $Patterns)
    $result = New-Object 'System.Collections.Generic.List[string]'
    foreach ($pattern in $Patterns) {
        $full = $pattern
        if (-not [IO.Path]::IsPathRooted($full)) { $full = Join-Path $RepoRoot $full }
        $matched = @()
        if ($full -match '^(.*?)[\\/]\*\*[\\/](.+)$') {
            $base = $Matches[1]
            $leaf = $Matches[2]
            if (Test-Path -LiteralPath $base -PathType Container) {
                $matched = @(Get-ChildItem -LiteralPath $base -Recurse -File -Filter $leaf | ForEach-Object { $_.FullName })
            }
        } else {
            $matched = @(Get-ChildItem -Path $full -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
        }
        if ($matched.Count -eq 0) { Write-Warn "no files match $pattern" }
        foreach ($m in $matched) { if (-not $result.Contains($m)) { $result.Add($m) } }
    }
    return $result.ToArray()
}

function ConvertTo-CommandLineArgument {
    param([Parameter(Mandatory)][string] $Value)
    if ($Value -match '[\s"]') { return '"' + ($Value -replace '"', '\"') + '"' }
    return $Value
}

function Test-Signed {
    param([Parameter(Mandatory)][string] $Path)
    try { return ((Get-AuthenticodeSignature -LiteralPath $Path).Status -eq 'Valid') } catch { return $false }
}

function Invoke-AuthenticodeSigning {
    <#
    .SYNOPSIS
        Signs the given files with signtool, at most $ThrottleLimit processes at a time. Returns the list of files
        that failed (with their output already printed).
    #>
    param(
        [Parameter(Mandatory)][string] $Tool,
        [Parameter(Mandatory)][string[]] $Targets,
        [Parameter(Mandatory)][string[]] $CommonArguments,
        [Parameter(Mandatory)][string] $RedactedArguments
    )
    $failed = New-Object 'System.Collections.Generic.List[string]'
    $running = New-Object 'System.Collections.Generic.List[object]'
    $queue = New-Object 'System.Collections.Generic.Queue[string]'
    foreach ($t in $Targets) { $queue.Enqueue($t) }
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("clubshell-sign-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    try {
        while ($queue.Count -gt 0 -or $running.Count -gt 0) {
            while ($queue.Count -gt 0 -and $running.Count -lt $ThrottleLimit) {
                $file = $queue.Dequeue()
                $stdout = Join-Path $tempRoot ([IO.Path]::GetFileName($file) + '.' + [Guid]::NewGuid().ToString('N') + '.out')
                $stderr = "$stdout.err"
                $argumentLine = (($CommonArguments + @($file)) | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join ' '
                Write-Host "    > signtool $RedactedArguments $(ConvertTo-CommandLineArgument $file)" -ForegroundColor DarkGray
                $process = Start-Process -FilePath $Tool -ArgumentList $argumentLine -NoNewWindow -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
                [void]$process.Handle   # Windows PowerShell 5.1: ExitCode stays empty unless the handle was cached before exit.
                $running.Add([pscustomobject]@{ File = $file; Process = $process; StdOut = $stdout; StdErr = $stderr })
            }
            Start-Sleep -Milliseconds 200
            foreach ($job in $running.ToArray()) {
                $job.Process.Refresh()
                if (-not $job.Process.HasExited) { continue }
                $running.Remove($job) | Out-Null
                $output = @()
                foreach ($f in @($job.StdOut, $job.StdErr)) { if (Test-Path -LiteralPath $f) { $output += @(Get-Content -LiteralPath $f) } }
                if ($job.Process.ExitCode -eq 0) {
                    Write-Ok "signed $($job.File)"
                } else {
                    Write-Fail "$($job.File) (signtool exit code $($job.Process.ExitCode))"
                    $output | Where-Object { $_ } | ForEach-Object { Write-Host "        $_" -ForegroundColor DarkGray }
                    $failed.Add($job.File)
                }
            }
        }
    } finally {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    return $failed.ToArray()
}

# ---------------------------------------------------------------------------------------------------------------
# RSA / PEM helpers (Windows PowerShell 5.1 has no RSA.ImportFromPem, so DER is read by hand there)
# ---------------------------------------------------------------------------------------------------------------
function ConvertFrom-Pem {
    <#
    .SYNOPSIS
        First PEM block of the text as @{ Label; Bytes }.
    #>
    param([Parameter(Mandatory)][string] $Pem)
    $match = [regex]::Match($Pem, '-----BEGIN ([A-Z0-9 ]+)-----([A-Za-z0-9+/=\s]+?)-----END \1-----')
    if (-not $match.Success) { throw 'Not a PEM document (no -----BEGIN ...----- block)' }
    return @{ Label = $match.Groups[1].Value.Trim(); Bytes = [Convert]::FromBase64String(($match.Groups[2].Value -replace '\s', '')) }
}

function Read-DerElement {
    <#
    .SYNOPSIS
        Reads the TLV header at $Offset: @{ Tag; Start (content offset); Length; End }.
    #>
    param([Parameter(Mandatory)][byte[]] $Data, [Parameter(Mandatory)][int] $Offset)
    if ($Offset -ge $Data.Length) { throw 'DER: unexpected end of data' }
    $tag = [int]$Data[$Offset]
    $pos = $Offset + 1
    $length = [int]$Data[$pos]
    $pos++
    if ($length -band 0x80) {
        $count = $length -band 0x7F
        if ($count -gt 4) { throw 'DER: length too large' }
        $length = 0
        for ($i = 0; $i -lt $count; $i++) { $length = ($length -shl 8) -bor [int]$Data[$pos]; $pos++ }
    }
    if ($pos + $length -gt $Data.Length) { throw 'DER: element exceeds data' }
    return @{ Tag = $tag; Start = $pos; Length = $length; End = $pos + $length }
}

function Get-DerChildren {
    param([Parameter(Mandatory)][byte[]] $Data, [Parameter(Mandatory)][hashtable] $Element)
    $children = New-Object 'System.Collections.Generic.List[object]'
    $pos = $Element.Start
    while ($pos -lt $Element.End) {
        $child = Read-DerElement -Data $Data -Offset $pos
        $children.Add($child)
        $pos = $child.End
    }
    return $children.ToArray()
}

function Get-DerBytes {
    param([Parameter(Mandatory)][byte[]] $Data, [Parameter(Mandatory)][hashtable] $Element)
    if ($Element.Length -eq 0) { return [byte[]]@() }
    return [byte[]]$Data[$Element.Start..($Element.End - 1)]
}

function Get-DerIntegerBytes {
    <#
    .SYNOPSIS
        Unsigned big-endian magnitude of a DER INTEGER (leading zero bytes removed).
    #>
    param([Parameter(Mandatory)][byte[]] $Data, [Parameter(Mandatory)][hashtable] $Element)
    if ($Element.Tag -ne 0x02) { throw "DER: expected INTEGER, got tag 0x$('{0:X2}' -f $Element.Tag)" }
    $bytes = Get-DerBytes -Data $Data -Element $Element
    $skip = 0
    while ($skip -lt $bytes.Length - 1 -and $bytes[$skip] -eq 0) { $skip++ }
    if ($skip -eq 0) { return $bytes }
    return [byte[]]$bytes[$skip..($bytes.Length - 1)]
}

function ConvertTo-FixedLength {
    param([Parameter(Mandatory)][byte[]] $Bytes, [Parameter(Mandatory)][int] $Length)
    if ($Bytes.Length -ge $Length) { return $Bytes }
    $padded = New-Object byte[] $Length
    [Array]::Copy($Bytes, 0, $padded, $Length - $Bytes.Length, $Bytes.Length)
    return $padded
}

function ConvertFrom-Pkcs1PrivateKey {
    param([Parameter(Mandatory)][byte[]] $Der)
    $sequence = Read-DerElement -Data $Der -Offset 0
    if ($sequence.Tag -ne 0x30) { throw 'DER: RSAPrivateKey must be a SEQUENCE' }
    $fields = @(Get-DerChildren -Data $Der -Element $sequence)
    if ($fields.Count -lt 9) { throw 'DER: RSAPrivateKey has too few fields' }
    $modulus = Get-DerIntegerBytes -Data $Der -Element $fields[1]
    $modulusLength = $modulus.Length
    $primeLength = [int][math]::Ceiling($modulusLength / 2)
    $parameters = New-Object System.Security.Cryptography.RSAParameters
    $parameters.Modulus = $modulus
    $parameters.Exponent = Get-DerIntegerBytes -Data $Der -Element $fields[2]
    $parameters.D = ConvertTo-FixedLength -Bytes (Get-DerIntegerBytes -Data $Der -Element $fields[3]) -Length $modulusLength
    $parameters.P = ConvertTo-FixedLength -Bytes (Get-DerIntegerBytes -Data $Der -Element $fields[4]) -Length $primeLength
    $parameters.Q = ConvertTo-FixedLength -Bytes (Get-DerIntegerBytes -Data $Der -Element $fields[5]) -Length $primeLength
    $parameters.DP = ConvertTo-FixedLength -Bytes (Get-DerIntegerBytes -Data $Der -Element $fields[6]) -Length $primeLength
    $parameters.DQ = ConvertTo-FixedLength -Bytes (Get-DerIntegerBytes -Data $Der -Element $fields[7]) -Length $primeLength
    $parameters.InverseQ = ConvertTo-FixedLength -Bytes (Get-DerIntegerBytes -Data $Der -Element $fields[8]) -Length $primeLength
    return $parameters
}

function ConvertFrom-Pkcs1PublicKey {
    param([Parameter(Mandatory)][byte[]] $Der)
    $sequence = Read-DerElement -Data $Der -Offset 0
    if ($sequence.Tag -ne 0x30) { throw 'DER: RSAPublicKey must be a SEQUENCE' }
    $fields = @(Get-DerChildren -Data $Der -Element $sequence)
    if ($fields.Count -lt 2) { throw 'DER: RSAPublicKey has too few fields' }
    $parameters = New-Object System.Security.Cryptography.RSAParameters
    $parameters.Modulus = Get-DerIntegerBytes -Data $Der -Element $fields[0]
    $parameters.Exponent = Get-DerIntegerBytes -Data $Der -Element $fields[1]
    return $parameters
}

function Import-RsaPem {
    <#
    .SYNOPSIS
        RSA object from a PEM ("PRIVATE KEY", "RSA PRIVATE KEY", "PUBLIC KEY", "RSA PUBLIC KEY" or "CERTIFICATE").
        Uses RSA.ImportFromPem on PowerShell 7; parses the DER by hand into RSACng on Windows PowerShell 5.1.
    #>
    param([Parameter(Mandatory)][string] $Pem)
    $block = ConvertFrom-Pem -Pem $Pem
    if ($block.Label -eq 'CERTIFICATE') {
        $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (, $block.Bytes)
        $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($certificate)
        if (-not $rsa) { throw 'Certificate does not contain an RSA public key' }
        return $rsa
    }

    if ([System.Security.Cryptography.RSA].GetMethod('ImportFromPem')) {
        try {
            $modern = [System.Security.Cryptography.RSA]::Create()
            $modern.ImportFromPem($Pem)
            return $modern
        } catch {
            Write-Verbose "RSA.ImportFromPem failed ($($_.Exception.Message)); falling back to the DER reader"
        }
    }

    $der = $block.Bytes
    switch ($block.Label) {
        'RSA PRIVATE KEY' { $parameters = ConvertFrom-Pkcs1PrivateKey -Der $der }
        'PRIVATE KEY' {
            # PKCS#8 PrivateKeyInfo ::= SEQUENCE { version, AlgorithmIdentifier, OCTET STRING (RSAPrivateKey) }
            $info = Read-DerElement -Data $der -Offset 0
            $fields = @(Get-DerChildren -Data $der -Element $info)
            if ($fields.Count -lt 3 -or $fields[2].Tag -ne 0x04) { throw 'DER: malformed PKCS#8 PrivateKeyInfo' }
            $parameters = ConvertFrom-Pkcs1PrivateKey -Der (Get-DerBytes -Data $der -Element $fields[2])
        }
        'RSA PUBLIC KEY' { $parameters = ConvertFrom-Pkcs1PublicKey -Der $der }
        'PUBLIC KEY' {
            # SubjectPublicKeyInfo ::= SEQUENCE { AlgorithmIdentifier, BIT STRING (RSAPublicKey) }
            $info = Read-DerElement -Data $der -Offset 0
            $fields = @(Get-DerChildren -Data $der -Element $info)
            if ($fields.Count -lt 2 -or $fields[1].Tag -ne 0x03) { throw 'DER: malformed SubjectPublicKeyInfo' }
            $bits = Get-DerBytes -Data $der -Element $fields[1]
            $parameters = ConvertFrom-Pkcs1PublicKey -Der ([byte[]]$bits[1..($bits.Length - 1)])
        }
        default { throw "Unsupported PEM label '$($block.Label)'" }
    }
    $cng = New-Object System.Security.Cryptography.RSACng
    $cng.ImportParameters($parameters)
    return $cng
}

function Get-RsaSignatureBase64 {
    <#
    .SYNOPSIS
        Base64 RSA-PSS-SHA256 signature over a file's raw bytes (UpdateManifest.Signature semantics).
    #>
    param([Parameter(Mandatory)][System.Security.Cryptography.RSA] $Key, [Parameter(Mandatory)][string] $Path)
    $stream = [IO.File]::OpenRead($Path)
    try { return [Convert]::ToBase64String($Key.SignData($stream, $Sha256, $Pss)) } finally { $stream.Dispose() }
}

function Test-RsaFileSignature {
    param([Parameter(Mandatory)][System.Security.Cryptography.RSA] $Key, [Parameter(Mandatory)][string] $Path, [AllowEmptyString()][string] $SignatureBase64)
    if (-not $SignatureBase64) { return $false }
    $signature = $null
    try { $signature = [Convert]::FromBase64String($SignatureBase64.Trim()) } catch { return $false }
    $stream = [IO.File]::OpenRead($Path)
    try { return $Key.VerifyData($stream, $signature, $Sha256, $Pss) } finally { $stream.Dispose() }
}

# ---------------------------------------------------------------------------------------------------------------
# Canonical manifest bytes: ClubShell.Core.Security.Signing.ManifestCanonicalBytes
# ---------------------------------------------------------------------------------------------------------------
function ConvertTo-JsonStringLiteral {
    <#
    .SYNOPSIS
        Quoted JSON string exactly as Utf8JsonWriter writes it with the default JavaScriptEncoder: ASCII 0x20-0x7E
        pass through except " & ' + < > \ ` which, like control characters and all non-ASCII UTF-16 units, become
        \uXXXX (upper-case hex); \b \f \n \r \t and \\ use their short escapes.
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Value)
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    foreach ($ch in $Value.ToCharArray()) {
        $code = [int]$ch
        if ($ch -eq '\') { [void]$builder.Append('\\') }
        elseif ($code -eq 0x08) { [void]$builder.Append('\b') }
        elseif ($code -eq 0x0C) { [void]$builder.Append('\f') }
        elseif ($code -eq 0x0A) { [void]$builder.Append('\n') }
        elseif ($code -eq 0x0D) { [void]$builder.Append('\r') }
        elseif ($code -eq 0x09) { [void]$builder.Append('\t') }
        elseif ($code -ge 0x20 -and $code -le 0x7E -and $code -ne 0x22 -and $code -ne 0x26 -and $code -ne 0x27 -and $code -ne 0x2B -and $code -ne 0x3C -and $code -ne 0x3E -and $code -ne 0x60) {
            [void]$builder.Append($ch)
        } else {
            [void]$builder.Append('\u' + $code.ToString('X4'))
        }
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Get-ManifestCanonicalBytes {
    <#
    .SYNOPSIS
        UTF-8 bytes of {"component":..,"version":..,"url":..,"sha256":..,"size":N,"publishedAt":".."} for one
        UpdateManifest object, byte-identical to Signing.ManifestCanonicalBytes.
    #>
    param([Parameter(Mandatory)][object] $Entry)
    # JsonNamingPolicy.CamelCase of the UpdateComponent enum name (Agent -> agent).
    $componentName = [string]$Entry.component
    $componentName = $componentName.Substring(0, 1).ToLowerInvariant() + $componentName.Substring(1)
    $publishedAt = [DateTimeOffset]::Parse([string]$Entry.publishedAt, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal)
    $json = '{' +
        '"component":' + (ConvertTo-JsonStringLiteral $componentName) + ',' +
        '"version":' + (ConvertTo-JsonStringLiteral ([string]$Entry.version)) + ',' +
        '"url":' + (ConvertTo-JsonStringLiteral ([string]$Entry.url)) + ',' +
        '"sha256":' + (ConvertTo-JsonStringLiteral ([string]$Entry.sha256)) + ',' +
        '"size":' + ([long]$Entry.size).ToString([Globalization.CultureInfo]::InvariantCulture) + ',' +
        '"publishedAt":' + (ConvertTo-JsonStringLiteral $publishedAt.UtcDateTime.ToString($CanonicalTimestampFormat, [Globalization.CultureInfo]::InvariantCulture)) +
        '}'
    Write-Verbose "canonical: $json"
    return (New-Object System.Text.UTF8Encoding($false)).GetBytes($json)
}

function Get-PropertyValue {
    param([Parameter(Mandatory)][object] $Object, [Parameter(Mandatory)][string] $Name)
    $property = $Object.PSObject.Properties[$Name]
    if ($property) { return $property.Value }
    return $null
}

# ---------------------------------------------------------------------------------------------------------------
# Manifest mode
# ---------------------------------------------------------------------------------------------------------------
function Invoke-ManifestMode {
    $manifestFile = (Resolve-Path -LiteralPath $ManifestPath).Path
    $releaseDir = Split-Path -Parent $manifestFile
    $manifest = Get-Content -LiteralPath $manifestFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $components = Get-PropertyValue -Object $manifest -Name 'components'
    $packages = Get-PropertyValue -Object $manifest -Name 'packages'
    if (-not $components -or -not $packages) { throw "$manifestFile has no components/packages (not a package.ps1 manifest)" }

    $privateKey = $null
    $publicKey = $null
    if ($ManifestKeyPem) {
        if (-not (Test-Path -LiteralPath $ManifestKeyPem)) { throw "Private key PEM not found: $ManifestKeyPem" }
        $privateKey = Import-RsaPem -Pem (Get-Content -LiteralPath $ManifestKeyPem -Raw)
        Write-Ok "private key: $ManifestKeyPem ($($privateKey.KeySize)-bit)"
    }
    if ($ManifestPublicKeyPem) {
        if (-not (Test-Path -LiteralPath $ManifestPublicKeyPem)) { throw "Public key PEM not found: $ManifestPublicKeyPem" }
        $publicKey = Import-RsaPem -Pem (Get-Content -LiteralPath $ManifestPublicKeyPem -Raw)
        Write-Ok "public key: $ManifestPublicKeyPem ($($publicKey.KeySize)-bit)"
    } elseif ($privateKey) {
        $publicKey = $privateKey
    }
    if (-not $Verify -and -not $privateKey) {
        Write-Host 'ERROR: -ManifestKeyPem is required to sign (or pass -Verify with -ManifestPublicKeyPem).' -ForegroundColor Red
        exit 2
    }
    if ($Verify -and -not $publicKey) {
        Write-Host 'ERROR: -ManifestPublicKeyPem (or -ManifestKeyPem) is required to verify.' -ForegroundColor Red
        exit 2
    }

    $failures = 0
    if (-not $Verify) {
        Write-Step "Signing $manifestFile"
        if (-not $PSCmdlet.ShouldProcess($manifestFile, 'Sign manifest')) { return 0 }
        $manifestSignatures = Get-PropertyValue -Object $manifest -Name 'manifestSignatures'
        if (-not $manifestSignatures) {
            $manifestSignatures = New-Object psobject
            $manifest | Add-Member -NotePropertyName 'manifestSignatures' -NotePropertyValue $manifestSignatures
        }
        foreach ($property in $components.PSObject.Properties) {
            $name = $property.Name
            $entry = $property.Value
            $packageName = Get-PropertyValue -Object $packages -Name $name
            if (-not $packageName) { throw "packages.$name missing in manifest" }
            $packagePath = Join-Path $releaseDir $packageName
            if (-not (Test-Path -LiteralPath $packagePath)) { throw "Package not found: $packagePath" }

            # Authenticode signing changed the package bytes: refresh size + sha256 before signing.
            $entry.size = [long](Get-Item -LiteralPath $packagePath).Length
            $entry.sha256 = Get-Sha256Hex -Path $packagePath
            $entry.signature = Get-RsaSignatureBase64 -Key $privateKey -Path $packagePath
            $canonicalSignature = [Convert]::ToBase64String($privateKey.SignData((Get-ManifestCanonicalBytes -Entry $entry), $Sha256, $Pss))
            if ($manifestSignatures.PSObject.Properties[$name]) { $manifestSignatures.$name = $canonicalSignature } else { $manifestSignatures | Add-Member -NotePropertyName $name -NotePropertyValue $canonicalSignature }
            Write-Ok "$name  $packageName  size=$($entry.size)  sha256=$($entry.sha256.Substring(0, 16))...  signature + manifestSignature set"
        }

        # Refresh the file inventory (sizes/hashes changed) and SHA256SUMS.txt.
        $files = Get-PropertyValue -Object $manifest -Name 'files'
        $sums = New-Object 'System.Collections.Generic.List[string]'
        if ($files) {
            foreach ($file in $files) {
                $path = Join-Path $releaseDir ([string]$file.path)
                if (-not (Test-Path -LiteralPath $path)) { Write-Warn "listed file missing: $($file.path)"; continue }
                $file.size = [long](Get-Item -LiteralPath $path).Length
                $file.sha256 = Get-Sha256Hex -Path $path
                $sums.Add("$($file.sha256) *$($file.path)")
            }
        }
        Write-TextFile -Path $manifestFile -Text ($manifest | ConvertTo-Json -Depth 8)
        if ($sums.Count -gt 0) { Write-TextFile -Path (Join-Path $releaseDir 'SHA256SUMS.txt') -Text ($sums -join "`n") }
        Write-Ok "wrote $manifestFile"

        $version = [string](Get-PropertyValue -Object $manifest -Name 'version')
        $zip = Join-Path (Split-Path -Parent $releaseDir) "ClubShell-$version.zip"
        if ($version -and (Test-Path -LiteralPath $zip)) {
            Remove-Item -LiteralPath $zip -Force
            Compress-Archive -Path (Join-Path $releaseDir '*') -DestinationPath $zip -CompressionLevel Optimal
            Write-Ok "refreshed $zip"
        }
    }

    Write-Step 'Verifying manifest signatures'
    $manifestSignatures = Get-PropertyValue -Object $manifest -Name 'manifestSignatures'
    foreach ($property in $components.PSObject.Properties) {
        $name = $property.Name
        $entry = $property.Value
        $packageName = Get-PropertyValue -Object $packages -Name $name
        $packagePath = Join-Path $releaseDir ([string]$packageName)
        if (-not $packageName -or -not (Test-Path -LiteralPath $packagePath)) { Write-Fail "$name package missing ($packageName)"; $failures++; continue }

        $actualSize = (Get-Item -LiteralPath $packagePath).Length
        if ([long]$entry.size -ne $actualSize) { Write-Fail "$name size $($entry.size) != actual $actualSize"; $failures++ }
        $actualHash = Get-Sha256Hex -Path $packagePath
        if ([string]$entry.sha256 -ne $actualHash) { Write-Fail "$name sha256 mismatch"; $failures++ }

        if (Test-RsaFileSignature -Key $publicKey -Path $packagePath -SignatureBase64 ([string](Get-PropertyValue -Object $entry -Name 'signature'))) {
            Write-Ok "$name package signature valid (RSA-PSS-SHA256 over $packageName)"
        } else {
            Write-Fail "$name package signature invalid or empty"; $failures++
        }

        $canonicalSignature = $null
        if ($manifestSignatures) { $canonicalSignature = Get-PropertyValue -Object $manifestSignatures -Name $name }
        $canonicalOk = $false
        if ($canonicalSignature) {
            try { $canonicalOk = $publicKey.VerifyData((Get-ManifestCanonicalBytes -Entry $entry), [Convert]::FromBase64String([string]$canonicalSignature), $Sha256, $Pss) } catch { $canonicalOk = $false }
        }
        if ($canonicalOk) { Write-Ok "$name manifest signature valid (Signing.VerifyManifest canonical form)" } else { Write-Fail "$name manifest signature invalid or missing"; $failures++ }
    }
    return $failures
}

# ---------------------------------------------------------------------------------------------------------------
# Authenticode mode
# ---------------------------------------------------------------------------------------------------------------
function Invoke-AuthenticodeMode {
    $patterns = $Files
    if (-not $patterns -or $patterns.Count -eq 0) {
        $patterns = @('artifacts\release\**\*.exe', 'artifacts\release\**\*.msi', 'artifacts\release\**\*.dll')
    }
    Write-Step 'Resolving files'
    $targets = @(Resolve-FileGlobs -Patterns $patterns)
    if ($targets.Count -eq 0) { throw 'No files to sign.' }
    $targets | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }

    $tool = Find-SignTool
    if (-not $tool) {
        Write-Host 'ERROR: signtool.exe not found. Install the Windows 10/11 SDK (winget install Microsoft.WindowsSDK.10.0.22621) or pass -SignTool.' -ForegroundColor Red
        exit 2
    }
    Write-Ok "signtool: $tool"

    $failures = 0
    if (-not $Verify) {
        Write-Step 'Signing'
        $pfx = $PfxPath
        if (-not $pfx -and $env:CODESIGN_PFX_PATH) { $pfx = $env:CODESIGN_PFX_PATH }
        $password = $null
        if ($PfxPassword) { $password = ConvertTo-PlainText -Secure $PfxPassword } elseif ($env:CODESIGN_PFX_PASSWORD) { $password = $env:CODESIGN_PFX_PASSWORD }

        $common = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', '/d', $Description, '/du', $DescriptionUrl)
        $redacted = @($common)
        if ($Thumbprint) {
            $common += @('/sha1', $Thumbprint)
            $redacted += @('/sha1', $Thumbprint)
            if ($MachineStore) { $common += '/sm'; $redacted += '/sm' }
            Write-Ok "certificate: thumbprint $Thumbprint ($(if ($MachineStore) { 'LocalMachine' } else { 'CurrentUser' })\My)"
        } elseif ($pfx) {
            if (-not (Test-Path -LiteralPath $pfx)) { Write-Host "ERROR: certificate not found: $pfx" -ForegroundColor Red; exit 2 }
            $common += @('/f', $pfx)
            $redacted += @('/f', $pfx)
            if ($password) { $common += @('/p', $password); $redacted += @('/p', '********') }
            Write-Ok "certificate: $pfx"
        } else {
            Write-Host 'ERROR: no certificate. Pass -PfxPath/-PfxPassword, -Thumbprint, or set CODESIGN_PFX_PATH / CODESIGN_PFX_PASSWORD.' -ForegroundColor Red
            exit 2
        }

        $toSign = New-Object 'System.Collections.Generic.List[string]'
        foreach ($target in $targets) {
            if (-not $Force -and (Test-Signed -Path $target)) { Write-Ok "already signed, skipping: $target (use -Force to re-sign)"; continue }
            if ($PSCmdlet.ShouldProcess($target, 'Authenticode sign')) { $toSign.Add($target) }
        }
        if ($toSign.Count -gt 0) {
            $failed = @(Invoke-AuthenticodeSigning -Tool $tool -Targets $toSign.ToArray() -CommonArguments $common -RedactedArguments (($redacted | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join ' '))
            if ($failed.Count -gt 0) {
                # Timestamp servers are flaky: one sequential retry for whatever failed.
                Write-Warn "$($failed.Count) file(s) failed; retrying once"
                Start-Sleep -Seconds 5
                $failed = @(Invoke-AuthenticodeSigning -Tool $tool -Targets $failed -CommonArguments $common -RedactedArguments (($redacted | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join ' '))
                $failures += $failed.Count
            }
        }
        if ($WhatIfPreference) { return 0 }
    }

    Write-Step 'Verifying (signtool verify /pa)'
    foreach ($target in $targets) {
        try {
            Invoke-Native -Command $tool -Arguments @('verify', '/pa', '/q', $target)
            Write-Ok "valid: $target"
        } catch {
            Write-Fail "$target ($($_.Exception.Message))"
            $failures++
        }
    }
    return $failures
}

# ---------------------------------------------------------------------------------------------------------------
$exitCode = 0
try {
    if ($PSCmdlet.ParameterSetName -eq 'Manifest') {
        $failures = Invoke-ManifestMode
    } else {
        $failures = Invoke-AuthenticodeMode
    }
    if ($failures -gt 0) {
        Write-Host "`n$failures failure(s)." -ForegroundColor Red
        $exitCode = 1
    } else {
        Write-Host "`nDone." -ForegroundColor Green
    }
} catch {
    $exitCode = 1
    Write-Host "`nSIGN FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.ScriptStackTrace) { Write-Verbose $_.ScriptStackTrace }
}
exit $exitCode
