<#
.SYNOPSIS
    Provisions a Windows 11 developer / test VM for ClubShell: toolchains via winget, pnpm via corepack, the pinned
    Rust toolchain, Developer Mode, the kiosk test user, D:\Games, a firewall rule for the mock server, and
    `pnpm install`. Idempotent; every step is skipped when already done. Supports -WhatIf.
.DESCRIPTION
    Installs (winget, silent, license accepted): Git.Git, OpenJS.NodeJS.LTS, Microsoft.DotNet.SDK.8,
    Rustlang.Rustup (+ toolchain from rust-toolchain.toml with rustfmt/clippy and the x86_64-pc-windows-msvc target),
    Microsoft.VisualStudio.2022.BuildTools with the "Desktop development with C++" workload (override args; an
    existing Build Tools install is modified in place when the workload is missing), Microsoft.EdgeWebView2Runtime
    and, with -WithWindowsSdk, Microsoft.WindowsSDK.10.0.22621 (signtool for sign.ps1). WiX is not installed: the
    installer uses the WiX SDK from NuGet.

    System settings: Developer Mode (AppModelUnlock), NTFS long paths, git core.longpaths.
    Kiosk: local user (default "club", Users group, password never expires) as configured in agent.json ->
    shell.kioskUser.name; D:\Games when a D: drive exists; inbound TCP rule "ClubShell-MockServer-<port>".
    Finally runs `pnpm install --frozen-lockfile` in the repository (assumed to be checked out already).
.PARAMETER SkipVs
    Do not install Visual Studio Build Tools (no Rust/Tauri builds on this VM).
.PARAMETER SkipRust
    Do not install rustup / the toolchain.
.PARAMETER WithWindowsSdk
    Also install the Windows 11 SDK (signtool.exe).
.PARAMETER KioskUser
    Name of the local kiosk test account. Default: club.
.PARAMETER KioskPassword
    Password for the kiosk account (SecureString). Default: a random 16-character password, printed once.
.PARAMETER SkipKioskUser
    Do not create the kiosk account.
.PARAMETER MockPort
    Port opened in the firewall for tools/MockServer. Default: 8080.
.PARAMETER SkipPnpmInstall
    Do not run `pnpm install`.
.EXAMPLE
    .\tools\scripts\setup-dev-vm.ps1
.EXAMPLE
    .\tools\scripts\setup-dev-vm.ps1 -SkipVs -SkipRust -WhatIf
    UI-only developer box; shows what would be installed.
.OUTPUTS
    Exit code 0 on success, 1 on failure, 2 when winget is missing.
#>
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $SkipVs,
    [switch] $SkipRust,
    [switch] $WithWindowsSdk,
    [ValidatePattern('^[A-Za-z0-9_.-]{1,20}$')]
    [string] $KioskUser = 'club',
    [System.Security.SecureString] $KioskPassword,
    [switch] $SkipKioskUser,
    [ValidateRange(1, 65535)]
    [int] $MockPort = 8080,
    [switch] $SkipPnpmInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$WingetAlreadyInstalled = -1978335189   # 0x8A15002B APPINSTALLER_CLI_ERROR_PACKAGE_ALREADY_INSTALLED
$WingetNoUpgrade = -1978335135          # 0x8A150061 no applicable upgrade found

function Write-Step { param([Parameter(Mandatory)][string] $Message) Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Write-Ok   { param([Parameter(Mandatory)][string] $Message) Write-Host "    $Message" -ForegroundColor Green }
function Write-Warn { param([Parameter(Mandatory)][string] $Message) Write-Host "    WARNING: $Message" -ForegroundColor Yellow }

function Invoke-Native {
    <#
    .SYNOPSIS
        Runs a native command in the given directory and throws when its exit code is not in $AllowedExitCodes.
    #>
    param(
        [Parameter(Mandatory)][string] $Command,
        [string[]] $Arguments = @(),
        [string] $WorkingDirectory = $RepoRoot,
        [int[]] $AllowedExitCodes = @(0)
    )
    Write-Host "    > $Command $($Arguments -join ' ')" -ForegroundColor DarkGray
    Push-Location -LiteralPath $WorkingDirectory
    try {
        # Native stderr must never become a terminating error (Windows PowerShell 5.1 wraps it in
        # NativeCommandError records when the caller redirects 2>&1); the exit code is what counts.
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = 0
        & $Command @Arguments | Out-Host
        $ErrorActionPreference = 'Stop'
        if ($AllowedExitCodes -notcontains $LASTEXITCODE) {
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

function Update-SessionPath {
    <#
    .SYNOPSIS
        Reloads PATH from the registry (machine + user) so tools installed a moment ago resolve in this session.
    #>
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $extra = @((Join-Path $env:USERPROFILE '.cargo\bin'), (Join-Path $env:ProgramFiles 'dotnet'), (Join-Path $env:ProgramFiles 'nodejs'), (Join-Path $env:ProgramFiles 'Git\cmd'))
    $parts = New-Object 'System.Collections.Generic.List[string]'
    foreach ($segment in (@($machine, $user) -join ';').Split(';') + $extra) {
        if ($segment -and -not $parts.Contains($segment)) { $parts.Add($segment) }
    }
    $env:Path = $parts -join ';'
}

function Test-WingetPackage {
    param([Parameter(Mandatory)][string] $Id)
    $result = Get-NativeOutput -Command 'winget' -Arguments @('list', '--id', $Id, '-e', '--accept-source-agreements', '--disable-interactivity')
    return ($result.ExitCode -eq 0 -and (($result.Output -join "`n") -match [regex]::Escape($Id)))
}

function Install-WingetPackage {
    <#
    .SYNOPSIS
        Installs a winget package silently unless it is already present. Returns $true when something was installed.
    #>
    param(
        [Parameter(Mandatory)][string] $Id,
        [Parameter(Mandatory)][string] $Name,
        [string] $Override
    )
    if (Test-WingetPackage -Id $Id) {
        Write-Ok "$Name already installed ($Id)"
        return $false
    }
    if (-not $PSCmdlet.ShouldProcess($Id, "winget install $Name")) { return $false }
    $arguments = @('install', '--id', $Id, '-e', '--silent', '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
    if ($Override) { $arguments += @('--override', $Override) }
    Invoke-Native -Command 'winget' -Arguments $arguments -AllowedExitCodes @(0, $WingetAlreadyInstalled, $WingetNoUpgrade)
    Write-Ok "$Name installed"
    return $true
}

function Get-VsInstallation {
    <#
    .SYNOPSIS
        @{ Path; HasVcTools } for the newest Visual Studio / Build Tools install found by vswhere, or $null.
    #>
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { return $null }
    $any = Get-NativeOutput -Command $vswhere -Arguments @('-products', '*', '-latest', '-property', 'installationPath')
    if ($any.ExitCode -ne 0 -or $any.Output.Count -eq 0 -or -not $any.Output[0]) { return $null }
    $withVc = Get-NativeOutput -Command $vswhere -Arguments @('-products', '*', '-latest', '-requires', 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64', '-property', 'installationPath')
    $hasVc = ($withVc.ExitCode -eq 0 -and $withVc.Output.Count -gt 0 -and [bool]$withVc.Output[0])
    return @{ Path = ($any.Output[0]).Trim(); HasVcTools = $hasVc }
}

function Set-RegistryDword {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][string] $Name, [Parameter(Mandatory)][int] $Value, [Parameter(Mandatory)][string] $Label)
    $current = $null
    if (Test-Path -LiteralPath $Path) {
        $item = Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction SilentlyContinue
        if ($item -and $item.PSObject.Properties[$Name]) { $current = $item.$Name }
    }
    if ($current -eq $Value) { Write-Ok "$Label already set"; return }
    if ($PSCmdlet.ShouldProcess("$Path\$Name", "Set $Label = $Value")) {
        if (-not (Test-Path -LiteralPath $Path)) { New-Item -Path $Path -Force | Out-Null }
        New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType DWord -Force | Out-Null
        Write-Ok "$Label enabled"
    }
}

function New-RandomPassword {
    param([int] $Length = 16)
    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#%^*-_'
    $bytes = New-Object byte[] $Length
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $chars = foreach ($b in $bytes) { $alphabet[$b % $alphabet.Length] }
    return -join $chars
}

$exitCode = 0
try {
    Write-Host "ClubShell dev VM setup  repo=$RepoRoot" -ForegroundColor White

    # -----------------------------------------------------------------------------------------------------------
    Write-Step 'winget'
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Write-Host 'ERROR: winget is not available. Install "App Installer" from the Microsoft Store (or https://aka.ms/getwinget) and re-run.' -ForegroundColor Red
        exit 2
    }
    Write-Ok "winget $((Get-NativeOutput -Command 'winget' -Arguments @('--version')).Output -join '')"

    # -----------------------------------------------------------------------------------------------------------
    Write-Step 'Toolchains'
    Install-WingetPackage -Id 'Git.Git' -Name 'Git' | Out-Null
    Install-WingetPackage -Id 'OpenJS.NodeJS.LTS' -Name 'Node.js LTS' | Out-Null
    Install-WingetPackage -Id 'Microsoft.DotNet.SDK.8' -Name '.NET SDK 8' | Out-Null
    Install-WingetPackage -Id 'Microsoft.EdgeWebView2Runtime' -Name 'WebView2 Runtime' | Out-Null
    if ($WithWindowsSdk) { Install-WingetPackage -Id 'Microsoft.WindowsSDK.10.0.22621' -Name 'Windows SDK (signtool)' | Out-Null }
    if (-not $SkipRust) { Install-WingetPackage -Id 'Rustlang.Rustup' -Name 'rustup' | Out-Null }

    if (-not $SkipVs) {
        $vcOverride = '--quiet --wait --norestart --nocache --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended'
        $vs = Get-VsInstallation
        if ($vs -and $vs.HasVcTools) {
            Write-Ok "Visual Studio C++ tools present at $($vs.Path)"
        } elseif ($vs) {
            # Build Tools (or another VS edition) exist without the C++ workload: modify in place.
            $setup = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\setup.exe'
            if ($PSCmdlet.ShouldProcess($vs.Path, 'Add the Desktop development with C++ workload')) {
                Invoke-Native -Command $setup -Arguments (@('modify', '--installPath', $vs.Path) + ($vcOverride -split ' ')) -AllowedExitCodes @(0, 3010)
                Write-Ok 'C++ workload added (a reboot may be required)'
            }
        } else {
            Install-WingetPackage -Id 'Microsoft.VisualStudio.2022.BuildTools' -Name 'Visual Studio 2022 Build Tools (C++)' -Override $vcOverride | Out-Null
        }
    } else {
        Write-Warn 'Visual Studio Build Tools skipped (-SkipVs): Rust/Tauri builds will not work'
    }
    Update-SessionPath

    # -----------------------------------------------------------------------------------------------------------
    Write-Step 'pnpm (corepack)'
    $packageJson = Get-Content -LiteralPath (Join-Path $RepoRoot 'package.json') -Raw | ConvertFrom-Json
    $pnpmSpec = 'pnpm@9'
    if ($packageJson.PSObject.Properties['packageManager'] -and $packageJson.packageManager) { $pnpmSpec = [string]$packageJson.packageManager }
    $pnpmVersion = ($pnpmSpec -split '@')[-1]
    $havePnpm = Get-NativeOutput -Command 'pnpm' -Arguments @('-v')
    if ($havePnpm.ExitCode -eq 0 -and $havePnpm.Output.Count -gt 0 -and ($havePnpm.Output[0]).Trim() -eq $pnpmVersion) {
        Write-Ok "pnpm $pnpmVersion already active"
    } elseif ($PSCmdlet.ShouldProcess($pnpmSpec, 'Activate pnpm')) {
        if (Get-Command corepack -ErrorAction SilentlyContinue) {
            Invoke-Native -Command 'corepack' -Arguments @('enable')
            Invoke-Native -Command 'corepack' -Arguments @('prepare', $pnpmSpec, '--activate')
        } else {
            Write-Warn 'corepack not found (removed from recent Node releases); installing pnpm with npm'
            Invoke-Native -Command 'npm' -Arguments @('install', '-g', $pnpmSpec)
        }
        Update-SessionPath
        Write-Ok "pnpm $pnpmVersion activated"
    }

    # -----------------------------------------------------------------------------------------------------------
    if (-not $SkipRust) {
        Write-Step 'Rust toolchain'
        $toolchainToml = Get-Content -LiteralPath (Join-Path $RepoRoot 'rust-toolchain.toml') -Raw
        $channel = 'stable'
        if ($toolchainToml -match 'channel\s*=\s*"([^"]+)"') { $channel = $Matches[1] }
        $installed = Get-NativeOutput -Command 'rustup' -Arguments @('toolchain', 'list')
        if ($installed.ExitCode -eq 0 -and (($installed.Output -join "`n") -match ('^' + [regex]::Escape($channel) + '-x86_64-pc-windows-msvc'))) {
            Write-Ok "toolchain $channel already installed"
        } elseif ($PSCmdlet.ShouldProcess($channel, 'rustup toolchain install')) {
            Invoke-Native -Command 'rustup' -Arguments @('toolchain', 'install', $channel, '--profile', 'minimal', '-c', 'rustfmt', '-c', 'clippy', '-t', 'x86_64-pc-windows-msvc')
            Invoke-Native -Command 'rustup' -Arguments @('default', $channel)
            Write-Ok "toolchain $channel installed"
        }
    }

    # -----------------------------------------------------------------------------------------------------------
    Write-Step 'System settings'
    Set-RegistryDword -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name 'AllowDevelopmentWithoutDevLicense' -Value 1 -Label 'Developer Mode'
    Set-RegistryDword -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -Name 'LongPathsEnabled' -Value 1 -Label 'NTFS long paths'
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $longPaths = Get-NativeOutput -Command 'git' -Arguments @('config', '--global', '--get', 'core.longpaths')
        if ($longPaths.ExitCode -eq 0 -and ($longPaths.Output -join '').Trim() -eq 'true') {
            Write-Ok 'git core.longpaths already true'
        } elseif ($PSCmdlet.ShouldProcess('git config --global core.longpaths', 'Set true')) {
            Invoke-Native -Command 'git' -Arguments @('config', '--global', 'core.longpaths', 'true')
            Write-Ok 'git core.longpaths = true'
        }
    }

    # -----------------------------------------------------------------------------------------------------------
    if (-not $SkipKioskUser) {
        Write-Step "Kiosk user '$KioskUser'"
        $existing = Get-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue
        if ($existing) {
            Write-Ok "user '$KioskUser' already exists"
        } elseif ($PSCmdlet.ShouldProcess($KioskUser, 'Create local user')) {
            $password = $KioskPassword
            $generated = $null
            if (-not $password) {
                $generated = New-RandomPassword
                $password = ConvertTo-SecureString -String $generated -AsPlainText -Force
            }
            New-LocalUser -Name $KioskUser -Password $password -FullName 'ClubShell kiosk' -Description 'ClubShell kiosk test account' -PasswordNeverExpires -UserMayNotChangePassword -AccountNeverExpires | Out-Null
            Write-Ok "user '$KioskUser' created"
            if ($generated) { Write-Host "    generated password: $generated   (store it; agent.json -> shell.kioskUser)" -ForegroundColor Yellow }
        }
        if (Get-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue) {
            $members = @(Get-LocalGroupMember -SID 'S-1-5-32-545' -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
            if ($members -match ('\\' + [regex]::Escape($KioskUser) + '$')) {
                Write-Ok "'$KioskUser' is a member of Users"
            } elseif ($PSCmdlet.ShouldProcess($KioskUser, 'Add to Users group')) {
                Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $KioskUser
                Write-Ok "'$KioskUser' added to Users"
            }
        }
    }

    # -----------------------------------------------------------------------------------------------------------
    Write-Step 'Games folder'
    if (Test-Path -LiteralPath 'D:\' -PathType Container) {
        if (Test-Path -LiteralPath 'D:\Games' -PathType Container) {
            Write-Ok 'D:\Games exists'
        } elseif ($PSCmdlet.ShouldProcess('D:\Games', 'Create folder')) {
            New-Item -ItemType Directory -Path 'D:\Games' -Force | Out-Null
            Write-Ok 'D:\Games created'
        }
    } else {
        Write-Warn 'no D: drive; skipping D:\Games (games.libraryRoots in agent.json can point elsewhere)'
    }

    # -----------------------------------------------------------------------------------------------------------
    Write-Step "Firewall (mock server TCP $MockPort)"
    $ruleName = "ClubShell-MockServer-$MockPort"
    if (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue) {
        Write-Ok "rule '$ruleName' exists"
    } elseif ($PSCmdlet.ShouldProcess($ruleName, 'Create inbound allow rule')) {
        New-NetFirewallRule -DisplayName $ruleName -Name $ruleName -Direction Inbound -Protocol TCP -LocalPort $MockPort -Action Allow -Profile Any -Description 'ClubShell tools/MockServer (development)' | Out-Null
        Write-Ok "rule '$ruleName' created"
    }

    # -----------------------------------------------------------------------------------------------------------
    if (-not $SkipPnpmInstall) {
        Write-Step 'pnpm install'
        if (Get-Command pnpm -ErrorAction SilentlyContinue) {
            if ($PSCmdlet.ShouldProcess($RepoRoot, 'pnpm install --frozen-lockfile')) {
                Invoke-Native -Command 'pnpm' -Arguments @('install', '--frozen-lockfile')
                Write-Ok 'workspace dependencies installed'
            }
        } else {
            Write-Warn 'pnpm not on PATH yet; open a new shell and run `pnpm install --frozen-lockfile`'
        }
    }

    # -----------------------------------------------------------------------------------------------------------
    Write-Host "`nSetup complete. Next steps:" -ForegroundColor Green
    Write-Host '  1. Open a NEW PowerShell window (PATH changes) - reboot if Visual Studio Build Tools asked for it.'
    Write-Host '  2. Copy-Item .env.example .env'
    Write-Host '  3. .\tools\scripts\dev.ps1 -WebOnly          # UI in the browser against the mock server'
    Write-Host '  4. .\tools\scripts\dev.ps1 -Agent            # + Agent console + Tauri window (needs Rust + C++ tools)'
    Write-Host '  5. .\tools\scripts\build.ps1                  # full Release build'
    Write-Host "  Kiosk user: $KioskUser  (agent.json -> shell.kioskUser.name)"
} catch {
    $exitCode = 1
    Write-Host "`nSETUP FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.ScriptStackTrace) { Write-Verbose $_.ScriptStackTrace }
}
exit $exitCode
