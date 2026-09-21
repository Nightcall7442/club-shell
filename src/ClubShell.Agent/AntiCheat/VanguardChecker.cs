using System.Runtime.Versioning;
using ClubShell.Contracts.Games;
using ClubShell.Core.Abstractions;
using ClubShell.Windows.Hardware;

namespace ClubShell.Agent.AntiCheat;

/// <summary>
/// Riot Vanguard: the <c>vgk</c> boot-start driver must be installed and loaded (a freshly installed driver only
/// loads after a reboot, which is reported as <c>serviceStopped</c>), the <c>vgc</c> service installed, and on
/// Windows 11 UEFI Secure Boot and a TPM must be present. At runtime <c>vgc</c> has to run alongside the game.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VanguardChecker : IAntiCheatChecker
{
    /// <summary>User-mode service name.</summary>
    public const string ServiceName = "vgc";

    /// <summary>Kernel driver service name.</summary>
    public const string DriverName = "vgk";

    /// <summary>Time after game start during which a stopped <c>vgc</c> is tolerated.</summary>
    public static TimeSpan StartupGrace { get; } = TimeSpan.FromSeconds(90);

    private readonly WmiQueries _wmi;
    private readonly IClock _clock;
    private readonly ILogger<VanguardChecker> _logger;

    /// <summary>Creates the checker.</summary>
    public VanguardChecker(WmiQueries wmi, IClock clock, ILogger<VanguardChecker> logger)
    {
        ArgumentNullException.ThrowIfNull(wmi);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _wmi = wmi;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public AntiCheatKind Kind => AntiCheatKind.Vanguard;

    /// <summary><see langword="true"/> on Windows 11, where Vanguard enforces Secure Boot and TPM 2.0.</summary>
    public static bool RequiresPlatformSecurity => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <inheritdoc />
    public async Task<AntiCheatCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var driver = AntiCheatProbe.QueryService(DriverName);
        if (!driver.Installed || !driver.ImageExists)
        {
            _logger.LogWarning("Riot Vanguard driver (vgk) is not installed");
            return new AntiCheatCheckResult(Kind, false, AntiCheatChecks.DriverMissing);
        }

        if (!driver.IsRunning)
        {
            _logger.LogWarning("Riot Vanguard driver (vgk) is installed but not loaded; a reboot is required");
            return new AntiCheatCheckResult(Kind, false, AntiCheatChecks.ServiceStopped);
        }

        var service = AntiCheatProbe.QueryService(ServiceName);
        if (!service.Installed || !service.ImageExists)
        {
            _logger.LogWarning("Riot Vanguard service (vgc) is not installed");
            return new AntiCheatCheckResult(Kind, false, AntiCheatChecks.DriverMissing);
        }

        if (RequiresPlatformSecurity)
        {
            if (WmiQueries.GetSecureBootEnabled() == false)
            {
                _logger.LogWarning("Riot Vanguard requires UEFI Secure Boot, which is off");
                return new AntiCheatCheckResult(Kind, false, AntiCheatChecks.SecureBootOff);
            }

            if (await _wmi.GetTpmPresentAsync(cancellationToken).ConfigureAwait(false) == false)
            {
                _logger.LogWarning("Riot Vanguard requires a TPM, which is absent or disabled");
                return new AntiCheatCheckResult(Kind, false, AntiCheatChecks.TpmOff);
            }
        }

        _logger.LogDebug("Riot Vanguard vgk {Version} loaded, vgc {Status}", driver.FileVersion ?? "?", service.Status?.ToString() ?? "unknown");
        return new AntiCheatCheckResult(Kind, true);
    }

    /// <inheritdoc />
    public Task<AntiCheatCheckResult> CheckRuntimeAsync(int gamePid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var driver = AntiCheatProbe.QueryService(DriverName);
        if (!driver.Installed || !driver.ImageExists)
        {
            return Task.FromResult(new AntiCheatCheckResult(Kind, false, AntiCheatChecks.DriverMissing));
        }

        if (!driver.IsRunning)
        {
            _logger.LogWarning("Riot Vanguard driver (vgk) is not loaded while game pid {Pid} runs", gamePid);
            return Task.FromResult(new AntiCheatCheckResult(Kind, false, AntiCheatChecks.ServiceStopped));
        }

        var service = AntiCheatProbe.QueryService(ServiceName);
        if (service.IsRunning || AntiCheatProbe.IsWithinStartupGrace(gamePid, StartupGrace, _clock.UtcNow))
        {
            return Task.FromResult(new AntiCheatCheckResult(Kind, true));
        }

        _logger.LogWarning("Riot Vanguard service (vgc) is not running alongside game pid {Pid}", gamePid);
        return Task.FromResult(new AntiCheatCheckResult(Kind, false, AntiCheatChecks.ServiceStopped));
    }
}
