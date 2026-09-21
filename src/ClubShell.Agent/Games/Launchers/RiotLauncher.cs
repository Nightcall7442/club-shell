using System.Runtime.Versioning;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Processes;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Games.Launchers;

/// <summary>Riot Client: <c>RiotClientServices.exe --launch-product=&lt;valorant|league_of_legends&gt; --launch-patchline=live</c>.</summary>
[SupportedOSPlatform("windows")]
public sealed class RiotLauncher : LauncherBase
{
    private const string PatchlinePrefix = "--launch-patchline=";

    private static readonly Dictionary<string, string[]> ProductExes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["valorant"] = new[] { "VALORANT-Win64-Shipping", "VALORANT" },
        ["league_of_legends"] = new[] { "LeagueClient", "League of Legends" },
        ["bacon"] = new[] { "LoR" },
        ["lor"] = new[] { "LoR" },
    };

    /// <summary>Creates the launcher.</summary>
    public RiotLauncher(ProcessLauncher processes, ProcessKiller killer, ProcessWatcher watcher, GameDetector detector, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<RiotLauncher> logger)
        : base(processes, killer, watcher, detector, settings, clock, logger)
    {
    }

    /// <inheritdoc />
    public override LauncherType Launcher => LauncherType.Riot;

    /// <inheritdoc />
    protected override LaunchCommand? BuildCommand(Game game, LaunchRequest request, LaunchContext context)
    {
        string product = game.LauncherAppId?.Trim() ?? string.Empty;
        if (product.Length == 0 || product.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
        {
            throw IpcError.Validation("launcherAppId", "Riot product id (alphanumeric) is required").ToException();
        }

        string? exe = Detector.ResolveLauncherExe(LauncherType.Riot);
        if (exe is null)
        {
            return null;
        }

        string patchline = "live";
        if (game.Args is { } args && args.StartsWith(PatchlinePrefix, StringComparison.OrdinalIgnoreCase))
        {
            string candidate = args[PatchlinePrefix.Length..].Trim();
            if (candidate.Length > 0 && candidate.All(char.IsLetterOrDigit))
            {
                patchline = candidate;
            }
        }

        return new LaunchCommand(exe, JoinArgs("--launch-product=" + product, PatchlinePrefix + patchline, request.ExtraArgs), Path.GetDirectoryName(exe));
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
