using System.Text.Json;
using ClubShell.Agent.Games;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Tests;

/// <summary>
/// SHELL_CHANGES 54, 55, 71: the player's re-check is fresh and lands in the snapshot, the maintenance tick does not
/// wait for a catalogue refresh, and what a rescan found is saved.
/// </summary>
public sealed class GameLibraryRescanTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("clubshell-library-");
    private readonly AgentSettings _settings = new();
    private readonly IOptionsMonitor<AgentSettings> _options = Substitute.For<IOptionsMonitor<AgentSettings>>();
    private readonly IServerClient _server = Substitute.For<IServerClient>();
    private readonly Game _game;

    public GameLibraryRescanTests()
    {
        _settings.Paths.ProgramData = _dir.FullName;
        _settings.Games.LibraryRoots = [];
        _options.CurrentValue.Returns(_settings);
        string install = Directory.CreateDirectory(Path.Combine(_dir.FullName, "Games", "Half-Life")).FullName;
        _game = new Game(
            Guid.NewGuid(), "Counter-Strike 1.6", LauncherType.Exe, null, Path.Combine(install, "hl.exe"), "-game cstrike", install, false, ["shooter"], [], "",
            null, null, "", 0, 0, null, false, AntiCheatKind.None, null, 0.5);
        _server.GetGamesAsync(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new EtagResponse<GamesListResponse>(new GamesListResponse([_game], 1, 1, 100, "7"), "\"v7\"", false));
    }

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public async Task The_players_recheck_skips_the_cache_and_updates_the_snapshot_and_a_rescan_is_saved()
    {
        using GameLibrary library = NewLibrary();
        await library.RefreshAsync(force: true, CancellationToken.None);
        library.Get(_game.Id)!.Installed.Should().BeFalse();

        File.WriteAllText(_game.ExePath!, "MZ");
        (await library.GetInstallStatusAsync(_game.Id, CancellationToken.None)).Installed.Should().BeTrue("a re-check is not answered from the minute-old cache");
        library.Get(_game.Id)!.Installed.Should().BeTrue();
        CachedInstalled(library).Should().BeTrue();

        File.Delete(_game.ExePath!);
        await library.RescanAsync(CancellationToken.None);
        library.Get(_game.Id)!.Installed.Should().BeFalse();
        CachedInstalled(library).Should().BeFalse("cache\\games.json follows the last scan, not only the last refresh");
    }

    [Fact]
    public async Task The_tick_rescan_does_not_wait_for_a_refresh_in_progress()
    {
        var answer = new TaskCompletionSource<EtagResponse<GamesListResponse>>();
        _server.GetGamesAsync(Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(answer.Task);
        using GameLibrary library = NewLibrary();
        Task refresh = library.RefreshAsync(force: true, CancellationToken.None);

        Task<bool> rescan = library.RescanIfDueAsync(CancellationToken.None);
        (await rescan.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse("the rescan is skipped, and due again on the next tick");

        answer.SetResult(new EtagResponse<GamesListResponse>(new GamesListResponse([_game], 1, 1, 100, "7"), "\"v7\"", false));
        await refresh;
        (await library.RescanIfDueAsync(CancellationToken.None)).Should().BeFalse("the refresh has just scanned");
    }

    private GameLibrary NewLibrary() => new(
        _server, new GameDetector(_options, SystemClock.Instance, NullLogger<GameDetector>.Instance), _options, SystemClock.Instance,
        NullLogger<GameLibrary>.Instance);

    private static bool CachedInstalled(GameLibrary library)
    {
        using JsonDocument cache = JsonDocument.Parse(File.ReadAllText(library.CachePath));
        return cache.RootElement.GetProperty("items")[0].GetProperty("installed").GetBoolean();
    }
}
