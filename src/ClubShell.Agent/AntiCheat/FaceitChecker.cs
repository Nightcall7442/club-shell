using System.Runtime.Versioning;
using ClubShell.Contracts.Games;
using ClubShell.Core.Abstractions;

namespace ClubShell.Agent.AntiCheat;

/// <summary>
/// FACEIT Anti-Cheat: the <c>faceit</c> kernel driver (<c>faceit.sys</c>) and the <c>FACEIT</c> service must be
/// installed, the driver binary present and Authenticode-signed (integrity of the FACEIT client itself; no cheat
/// signatures are scanned). At runtime the driver has to be loaded and the service or client process alive.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FaceitChecker : IAntiCheatChecker
{
    /// <summary>User-mode service name.</summary>
    public const string ServiceName = "FACEIT";

    /// <summary>Kernel driver service name.</summary>
    public const string DriverName = "faceit";

    /// <summary>Client / service process names (without extension).</summary>
    public static IReadOnlyList<string> ProcessNames { get; } = new[] { "faceitservice", "faceitclient", "FACEIT" };

    /// <summary>Time after game start during which a stopped FACEIT service is tolerated.</summary>
    public static TimeSpan StartupGrace { get; } = TimeSpan.FromSeconds(90);

    private readonly IClock _clock;
    private readonly ILogger<FaceitChecker> _logger;

    /// <summary>Creates the checker.</summary>
    public FaceitChecker(IClock clock, ILogger<FaceitChecker> logger)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public AntiCheatKind Kind => AntiCheatKind.Faceit;

    /// <inheritdoc />
    public Task<AntiCheatCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CheckInstalled(out _));
    }

    /// <inheritdoc />
    public Task<AntiCheatCheckResult> CheckRuntimeAsync(int gamePid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var installed = CheckInstalled(out var driver);
        if (!installed.Ok || driver is null)
        {
            return Task.FromResult(installed);
        }

        if (!driver.IsRunning)
        {
            if (AntiCheatProbe.IsWithinStartupGrace(gamePid, StartupGrace, _clock.UtcNow))
            {
                return Task.FromResult(new AntiCheatCheckResult(Kind, true));
            }

            _logger.LogWarning("FACEIT driver is not loaded while game pid {Pid} runs", gamePid);
            return Task.FromResult(new AntiCheatCheckResult(Kind, false, AntiCheatChecks.ServiceStopped));
        }

        var service = AntiCheatProbe.QueryService(ServiceName);
        if (service.IsRunning || ProcessNames.Any(AntiCheatProbe.IsProcessRunning) || AntiCheatProbe.IsWithinStartupGrace(gamePid, StartupGrace, _clock.UtcNow))
        {
            return Task.FromResult(new AntiCheatCheckResult(Kind, true));
        }

        _logger.LogWarning("FACEIT service is not running alongside game pid {Pid}", gamePid);
        return Task.FromResult(new AntiCheatCheckResult(Kind, false, AntiCheatChecks.ServiceStopped));
    }

    private AntiCheatCheckResult CheckInstalled(out ServiceInfo? driver)
    {
        driver = AntiCheatProbe.QueryService(DriverName);
        var service = AntiCheatProbe.QueryService(ServiceName);
        if (!driver.Installed && !service.Installed)
        {
            _logger.LogWarning("FACEIT anti-cheat is not installed");
            return new AntiCheatCheckResult(Kind, false, AntiCheatChecks.DriverMissing);
        }

        if (!driver.Installed || !driver.ImageExists || driver.ImagePath is null)
        {
            _logger.LogWarning("FACEIT driver binary {Path} is missing", driver.ImagePath ?? "faceit.sys");
            return new AntiCheatCheckResult(Kind, false, AntiCheatChecks.DriverMissing);
        }

        if (!AntiCheatProbe.HasAuthenticodeSignature(driver.ImagePath))
        {
            _logger.LogWarning("FACEIT driver {Path} is not signed; refusing to trust it", driver.ImagePath);
            return new AntiCheatCheckResult(Kind, false, AntiCheatChecks.DriverMissing);
        }

        _logger.LogDebug("FACEIT driver {Version} installed ({Status}), service {ServiceStatus}", driver.FileVersion ?? "?", driver.Status?.ToString() ?? "unknown", service.Status?.ToString() ?? "unknown");
        return new AntiCheatCheckResult(Kind, true);
    }
}
