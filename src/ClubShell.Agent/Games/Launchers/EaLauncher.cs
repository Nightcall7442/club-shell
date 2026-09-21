using System.Runtime.Versioning;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Processes;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Games.Launchers;

/// <summary>EA app: <c>EADesktop.exe origin2://game/launch?offerIds=&lt;id&gt;</c>, then waits for the game executable.</summary>
[SupportedOSPlatform("windows")]
public sealed class EaLauncher : LauncherBase
{
    /// <summary>Creates the launcher.</summary>
    public EaLauncher(ProcessLauncher processes, ProcessKiller killer, ProcessWatcher watcher, GameDetector detector, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<EaLauncher> logger)
        : base(processes, killer, watcher, detector, settings, clock, logger)
    {
    }

    /// <inheritdoc />
    public override LauncherType Launcher => LauncherType.Ea;

    /// <inheritdoc />
    protected override LaunchCommand? BuildCommand(Game game, LaunchRequest request, LaunchContext context)
    {
        if (string.IsNullOrWhiteSpace(game.LauncherAppId))
        {
            throw IpcError.Validation("launcherAppId", "EA offer id is required").ToException();
        }

        string? exe = Detector.ResolveLauncherExe(LauncherType.Ea);
        if (exe is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(game.Args) || !string.IsNullOrWhiteSpace(request.ExtraArgs))
        {
            Logger.LogDebug("EA launch URL cannot carry game arguments; ignoring args for {Title}", game.Title);
        }

        string url = "origin2://game/launch?offerIds=" + Uri.EscapeDataString(game.LauncherAppId.Trim()) + "&autoDownload=1";
        return new LaunchCommand(exe, url, Path.GetDirectoryName(exe));
    }
}
