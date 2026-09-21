using System.Runtime.Versioning;
using ClubShell.Contracts.Games;
using ClubShell.Core.Abstractions;

namespace ClubShell.Agent.AntiCheat;

/// <summary>
/// Easy Anti-Cheat: the <c>EasyAntiCheat_EOS</c> (Epic Online Services build) or legacy <c>EasyAntiCheat</c> service
/// must be installed with its driver binary present. The service is demand-start and only runs while a protected
/// game is up, so "not running" is a violation only at runtime, after a start-up grace period.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EacChecker : IAntiCheatChecker
{
    /// <summary>Service names probed, newest first.</summary>
    public static IReadOnlyList<string> ServiceNames { get; } = new[] { "EasyAntiCheat_EOS", "EasyAntiCheat" };

    /// <summary>Process names (without extension) that indicate the EAC bootstrap is alive.</summary>
    public static IReadOnlyList<string> ProcessNames { get; } = new[] { "EasyAntiCheat_EOS", "EasyAntiCheat", "start_protected_game" };

    /// <summary>Time after game start during which a stopped EAC service is tolerated.</summary>
    public static TimeSpan StartupGrace { get; } = TimeSpan.FromSeconds(90);

    private readonly IClock _clock;
    private readonly ILogger<EacChecker> _logger;

    /// <summary>Creates the checker.</summary>
    public EacChecker(IClock clock, ILogger<EacChecker> logger)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public AntiCheatKind Kind => AntiCheatKind.Eac;

    /// <inheritdoc />
    public Task<AntiCheatCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var service = FindInstalled();
        if (service is null)
        {
            _logger.LogWarning("Easy Anti-Cheat service is not installed");
            return Task.FromResult(new AntiCheatCheckResult(Kind, false, AntiCheatChecks.DriverMissing));
        }

        if (!service.ImageExists)
        {
            _logger.LogWarning("Easy Anti-Cheat binary {Path} is missing", service.ImagePath ?? "?");
            return Task.FromResult(new AntiCheatCheckResult(Kind, false, AntiCheatChecks.DriverMissing));
        }

        _logger.LogDebug("Easy Anti-Cheat {Service} {Version} installed ({Status})", service.Name, service.FileVersion ?? "?", service.Status?.ToString() ?? "unknown");
        return Task.FromResult(new AntiCheatCheckResult(Kind, true));
    }

    /// <inheritdoc />
    public Task<AntiCheatCheckResult> CheckRuntimeAsync(int gamePid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var service = FindInstalled();
        if (service is null || !service.ImageExists)
        {
            return Task.FromResult(new AntiCheatCheckResult(Kind, false, AntiCheatChecks.DriverMissing));
        }

        if (service.IsRunning || ProcessNames.Any(AntiCheatProbe.IsProcessRunning) || AntiCheatProbe.IsWithinStartupGrace(gamePid, StartupGrace, _clock.UtcNow))
        {
            return Task.FromResult(new AntiCheatCheckResult(Kind, true));
        }

        _logger.LogWarning("Easy Anti-Cheat is not running alongside game pid {Pid}", gamePid);
        return Task.FromResult(new AntiCheatCheckResult(Kind, false, AntiCheatChecks.ServiceStopped));
    }

    private static ServiceInfo? FindInstalled()
    {
        foreach (var name in ServiceNames)
        {
            var info = AntiCheatProbe.QueryService(name);
            if (info.Installed)
            {
                return info;
            }
        }

        return null;
    }
}
