using System.Runtime.Versioning;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Processes;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Games.Launchers;

/// <summary>Battle.net: <c>Battle.net.exe --exec="launch &lt;code&gt;"</c>, then waits for the product executable.</summary>
[SupportedOSPlatform("windows")]
public sealed class BattleNetLauncher : LauncherBase
{
    private static readonly Dictionary<string, string[]> ProductExes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wow"] = new[] { "Wow", "WowT", "WowB" },
        ["wow_classic"] = new[] { "WowClassic", "WowClassicT" },
        ["pro"] = new[] { "Overwatch" },
        ["d3"] = new[] { "Diablo III64", "Diablo III" },
        ["d4"] = new[] { "Diablo IV" },
        ["fenris"] = new[] { "Diablo IV" },
        ["d2r"] = new[] { "D2R" },
        ["osi"] = new[] { "D2R" },
        ["hs"] = new[] { "Hearthstone" },
        ["wtcg"] = new[] { "Hearthstone" },
        ["s2"] = new[] { "SC2_x64", "SC2" },
        ["s1"] = new[] { "StarCraft" },
        ["heroes"] = new[] { "HeroesOfTheStorm_x64", "HeroesOfTheStorm" },
        ["w3"] = new[] { "Warcraft III" },
        ["odin"] = new[] { "cod", "ModernWarfare" },
        ["auks"] = new[] { "cod" },
        ["lazr"] = new[] { "cod", "ModernWarfare" },
        ["zeus"] = new[] { "BlackOpsColdWar" },
        ["fore"] = new[] { "Vanguard" },
    };

    /// <summary>Creates the launcher.</summary>
    public BattleNetLauncher(ProcessLauncher processes, ProcessKiller killer, ProcessWatcher watcher, GameDetector detector, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<BattleNetLauncher> logger)
        : base(processes, killer, watcher, detector, settings, clock, logger)
    {
    }

    /// <inheritdoc />
    public override LauncherType Launcher => LauncherType.BattleNet;

    /// <inheritdoc />
    protected override LaunchCommand? BuildCommand(Game game, LaunchRequest request, LaunchContext context)
    {
        string code = game.LauncherAppId?.Trim() ?? string.Empty;
        if (code.Length == 0 || code.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
        {
            throw IpcError.Validation("launcherAppId", "Battle.net product code (alphanumeric) is required").ToException();
        }

        string? exe = Detector.ResolveLauncherExe(LauncherType.BattleNet);
        if (exe is null)
        {
            return null;
        }

        return new LaunchCommand(exe, "--exec=\"launch " + code + "\"", Path.GetDirectoryName(exe));
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> ExpectedProcessNames(Game game)
    {
        var names = new List<string>(base.ExpectedProcessNames(game));
        if (!string.IsNullOrWhiteSpace(game.LauncherAppId) && ProductExes.TryGetValue(game.LauncherAppId.Trim(), out string[]? known))
        {
            names.AddRange(known);
        }

        return names;
    }
}
