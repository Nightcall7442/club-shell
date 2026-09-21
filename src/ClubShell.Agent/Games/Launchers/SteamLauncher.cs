using System.Runtime.Versioning;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Processes;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Games.Launchers;

/// <summary>Steam: <c>steam.exe [-login user pass] -applaunch &lt;appid&gt; [args]</c>, then waits for the game executable.</summary>
[SupportedOSPlatform("windows")]
public sealed class SteamLauncher : LauncherBase
{
    /// <summary>Creates the launcher.</summary>
    public SteamLauncher(ProcessLauncher processes, ProcessKiller killer, ProcessWatcher watcher, GameDetector detector, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<SteamLauncher> logger)
        : base(processes, killer, watcher, detector, settings, clock, logger)
    {
    }

    /// <inheritdoc />
    public override LauncherType Launcher => LauncherType.Steam;

    /// <inheritdoc />
    protected override LaunchCommand? BuildCommand(Game game, LaunchRequest request, LaunchContext context)
    {
        if (string.IsNullOrWhiteSpace(game.LauncherAppId) || !game.LauncherAppId.Trim().All(char.IsDigit))
        {
            throw IpcError.Validation("launcherAppId", "Steam numeric app id is required").ToException();
        }

        string? exe = Detector.ResolveLauncherExe(LauncherType.Steam);
        if (exe is null)
        {
            return null;
        }

        return new LaunchCommand(exe, JoinArgs("-applaunch", game.LauncherAppId.Trim(), game.Args, request.ExtraArgs), Path.GetDirectoryName(exe));
    }
}
