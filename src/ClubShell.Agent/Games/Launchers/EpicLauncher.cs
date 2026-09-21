using System.Runtime.Versioning;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Processes;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Games.Launchers;

/// <summary>Epic Games Launcher: <c>EpicGamesLauncher.exe [auth args] com.epicgames.launcher://apps/&lt;id&gt;?action=launch&amp;silent=true</c>.</summary>
[SupportedOSPlatform("windows")]
public sealed class EpicLauncher : LauncherBase
{
    /// <summary>Creates the launcher.</summary>
    public EpicLauncher(ProcessLauncher processes, ProcessKiller killer, ProcessWatcher watcher, GameDetector detector, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<EpicLauncher> logger)
        : base(processes, killer, watcher, detector, settings, clock, logger)
    {
    }

    /// <inheritdoc />
    public override LauncherType Launcher => LauncherType.Epic;

    /// <inheritdoc />
    protected override LaunchCommand? BuildCommand(Game game, LaunchRequest request, LaunchContext context)
    {
        if (string.IsNullOrWhiteSpace(game.LauncherAppId))
        {
            throw IpcError.Validation("launcherAppId", "Epic app name is required").ToException();
        }

        string? exe = Detector.ResolveLauncherExe(LauncherType.Epic);
        if (exe is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(game.Args) || !string.IsNullOrWhiteSpace(request.ExtraArgs))
        {
            Logger.LogDebug("Epic launch URL cannot carry game arguments; ignoring args for {Title}", game.Title);
        }

        string url = "com.epicgames.launcher://apps/" + Uri.EscapeDataString(game.LauncherAppId.Trim()) + "?action=launch&silent=true";
        return new LaunchCommand(exe, url, Path.GetDirectoryName(exe));
    }
}
