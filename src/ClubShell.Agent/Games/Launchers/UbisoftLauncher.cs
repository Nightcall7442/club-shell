using System.Globalization;
using System.Runtime.Versioning;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Processes;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Games.Launchers;

/// <summary>Ubisoft Connect: <c>UbisoftConnect.exe uplay://launch/&lt;id&gt;/0</c>, then waits for the game executable.</summary>
[SupportedOSPlatform("windows")]
public sealed class UbisoftLauncher : LauncherBase
{
    /// <summary>Creates the launcher.</summary>
    public UbisoftLauncher(ProcessLauncher processes, ProcessKiller killer, ProcessWatcher watcher, GameDetector detector, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<UbisoftLauncher> logger)
        : base(processes, killer, watcher, detector, settings, clock, logger)
    {
    }

    /// <inheritdoc />
    public override LauncherType Launcher => LauncherType.Ubisoft;

    /// <inheritdoc />
    protected override LaunchCommand? BuildCommand(Game game, LaunchRequest request, LaunchContext context)
    {
        if (!int.TryParse(game.LauncherAppId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id <= 0)
        {
            throw IpcError.Validation("launcherAppId", "Ubisoft Connect numeric game id is required").ToException();
        }

        string? exe = Detector.ResolveLauncherExe(LauncherType.Ubisoft);
        if (exe is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(game.Args) || !string.IsNullOrWhiteSpace(request.ExtraArgs))
        {
            Logger.LogDebug("Ubisoft launch URL cannot carry game arguments; ignoring args for {Title}", game.Title);
        }

        return new LaunchCommand(exe, "uplay://launch/" + id.ToString(CultureInfo.InvariantCulture) + "/0", Path.GetDirectoryName(exe));
    }
}
