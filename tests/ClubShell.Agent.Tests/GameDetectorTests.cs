using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using ClubShell.Agent.Games;
using ClubShell.Contracts.Games;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Tests;

// ---------------------------------------------------------------------------------------------
// Shared test infrastructure for ClubShell.Agent.Tests (this is the first file alphabetically).
// ---------------------------------------------------------------------------------------------

/// <summary>A <see cref="FactAttribute"/> that is skipped on non-Windows hosts.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires Windows.";
        }
    }
}

/// <summary>A <see cref="TheoryAttribute"/> that is skipped on non-Windows hosts.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires Windows.";
        }
    }
}

/// <summary>A <see cref="FactAttribute"/> that is skipped unless the test process runs elevated on Windows.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WindowsAdminFactAttribute : FactAttribute
{
    public WindowsAdminFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires Windows.";
        }
        else if (!IsElevated())
        {
            Skip = "Requires an elevated (Administrator) test process.";
        }
    }

    private static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>
/// Deterministic <see cref="TimeProvider"/>: wall clock and monotonic timestamp advance only through
/// <see cref="Advance"/>, which also fires every <see cref="ITimer"/> that became due (in due order), so
/// <see cref="PeriodicTimer"/> and <c>Task.Delay(…, provider)</c> built on it are driven by the test.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = new();
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset start) => _now = start;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Timers currently scheduled to fire.</summary>
    public int ActiveTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.DueAt is not null);
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now.UtcTicks;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new FakeTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            timer.Schedule(_now, dueTime, period);
        }

        return timer;
    }

    /// <summary>Moves time forward, firing due timers on the calling thread in chronological order.</summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(by.Ticks);
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + by;
        }

        while (true)
        {
            FakeTimer? next = null;
            lock (_gate)
            {
                foreach (FakeTimer timer in _timers)
                {
                    if (timer.DueAt is { } due && due <= target && (next is null || due < next.DueAt))
                    {
                        next = timer;
                    }
                }

                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.DueAt!.Value;
                next.Reschedule(_now);
            }

            next.Fire();
        }
    }

    /// <summary>Waits (real time) until at least <paramref name="count"/> timers are scheduled, e.g. a background loop created its <see cref="PeriodicTimer"/>.</summary>
    public async Task WaitForTimersAsync(int count, CancellationToken cancellationToken)
    {
        while (ActiveTimers < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken);
        }
    }

    private void Change(FakeTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            timer.Schedule(_now, dueTime, period);
        }
    }

    private void Remove(FakeTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class FakeTimer : ITimer
    {
        private readonly FakeTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset? DueAt { get; private set; }

        private TimeSpan Period { get; set; }

        public void Schedule(DateTimeOffset now, TimeSpan dueTime, TimeSpan period)
        {
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;
            Period = period;
        }

        public void Reschedule(DateTimeOffset firedAt) =>
            DueAt = Period > TimeSpan.Zero && Period != Timeout.InfiniteTimeSpan ? firedAt + Period : null;

        public void Fire() => _callback(_state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _owner.Change(this, dueTime, period);
            return true;
        }

        public void Dispose()
        {
            _owner.Remove(this);
            GC.SuppressFinalize(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A unique directory under <c>%TEMP%\clubshell-tests</c>, deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Root = Path.Combine(Path.GetTempPath(), "clubshell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Creates (when needed) and returns a sub-directory.</summary>
    public string Sub(params string[] parts)
    {
        string full = Path.Combine(Root, Path.Combine(parts));
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Writes a file (creating parent directories) and returns its full path.</summary>
    public string File(string relativePath, string contents = "")
    {
        string full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, contents);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Left for the OS temp cleanup (e.g. a SQLite handle still closing).
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>Small factories shared by the test classes.</summary>
internal static class TestSupport
{
    /// <summary>An <see cref="IOptionsMonitor{TOptions}"/> that always returns <paramref name="settings"/>.</summary>
    public static IOptionsMonitor<AgentSettings> Monitor(AgentSettings settings)
    {
        IOptionsMonitor<AgentSettings> monitor = Substitute.For<IOptionsMonitor<AgentSettings>>();
        monitor.CurrentValue.Returns(settings);
        monitor.Get(Arg.Any<string?>()).Returns(settings);
        return monitor;
    }

    /// <summary>A catalogue entry with the fields the detector looks at.</summary>
    public static Game Game(string title, LauncherType launcher, string? appId = null, string? exePath = null, string? installPath = null) =>
        new(
            Guid.NewGuid(),
            title,
            launcher,
            appId,
            exePath,
            null,
            installPath,
            false,
            [],
            [],
            "https://cdn.example.uz/cover.png",
            null,
            null,
            "test game",
            0,
            0,
            null,
            false,
            AntiCheatKind.None,
            null,
            0);
}

// ---------------------------------------------------------------------------------------------
// GameDetector
// ---------------------------------------------------------------------------------------------

public sealed class GameDetectorTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(T0);
    private readonly AgentSettings _settings;

    public GameDetectorTests()
    {
        _settings = new AgentSettings();
        _settings.Games.LibraryRoots = [];
        // No configured launcher paths: every launcher resolves through registry / well-known locations only.
        _settings.Games.Launchers = new LaunchersSettings { Steam = null, Epic = null, BattleNet = null, Riot = null, Ea = null, Ubisoft = null };
    }

    public void Dispose()
    {
        _dir.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- Steam ----------------------------------------------------------------------------------

    [WindowsFact]
    public async Task Steam_ParsesLibraryFoldersAndAppManifest()
    {
        (string library, Game game) = SteamFixture(stateFlags: 4);

        GameInstallStatus status = await NewDetector().DetectAsync(game, CancellationToken.None);

        status.Installed.Should().BeTrue();
        status.InstallPath.Should().Be(Path.Combine(library, "steamapps", "common", "Counter-Strike Global Offensive"));
        status.SizeGb.Should().Be(30.0);
        status.Version.Should().Be("17512345");
        status.LauncherReady.Should().BeTrue("the configured steam.exe exists");
        status.VerifiedAt.Should().Be(T0);
    }

    [WindowsFact]
    public async Task Steam_ManifestWithoutFullyInstalledFlag_IsNotInstalled()
    {
        (_, Game game) = SteamFixture(stateFlags: 1026);

        GameInstallStatus status = await NewDetector().DetectAsync(game, CancellationToken.None);

        status.Installed.Should().BeFalse();
        status.InstallPath.Should().BeNull();
        status.SizeGb.Should().Be(game.SizeGb);
        status.Version.Should().BeNull();
        status.VerifiedAt.Should().BeNull();
    }

    // ---- Epic -----------------------------------------------------------------------------------

    [WindowsFact]
    public async Task Epic_ParsesItemManifest()
    {
        // Epic manifests live at a fixed ProgramData location (not injectable): write one uniquely named
        // .item there and remove it (and any directory created for it) afterwards.
        string manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        string? createdRoot = TopmostMissingDirectory(manifests);
        string appName = "ClubShellTest" + Guid.NewGuid().ToString("N");
        string install = _dir.Sub("EpicGames", "TestGame");
        string item = Path.Combine(manifests, appName + ".item");
        WriteEpicItem(item, appName, install);
        try
        {
            Game game = TestSupport.Game("Epic Test Game", LauncherType.Epic, appId: appName);

            GameInstallStatus status = await NewDetector().DetectAsync(game, CancellationToken.None);

            status.Installed.Should().BeTrue();
            status.InstallPath.Should().Be(install);
            status.SizeGb.Should().Be(20.0);
            status.Version.Should().Be("2.5.1");
        }
        finally
        {
            File.Delete(item);
            if (createdRoot is not null)
            {
                Directory.Delete(createdRoot, recursive: true);
            }
        }
    }

    // ---- Battle.net / EA / Ubisoft / Riot (registry-independent library-root fallback) ---------

    [WindowsTheory]
    [InlineData(LauncherType.BattleNet)]
    [InlineData(LauncherType.Ea)]
    [InlineData(LauncherType.Ubisoft)]
    [InlineData(LauncherType.Riot)]
    public async Task RegistryLaunchers_FallBackToLibraryRootByTitle(LauncherType launcher)
    {
        string root = _dir.Sub("Games");
        _settings.Games.LibraryRoots = [root];
        string unique = Guid.NewGuid().ToString("N");
        string title = "Zz" + unique + " Test Game";
        string install = _dir.Sub("Games", title);
        _ = _dir.File(Path.Combine("Games", title, "game.exe"));
        Game installed = TestSupport.Game(title, launcher, appId: "zz" + unique, exePath: "game.exe");
        Game missing = TestSupport.Game("Zz" + Guid.NewGuid().ToString("N") + " Missing", launcher, appId: "zz" + Guid.NewGuid().ToString("N"));
        GameDetector detector = NewDetector();

        GameInstallStatus found = await detector.DetectAsync(installed, CancellationToken.None);
        GameInstallStatus absent = await detector.DetectAsync(missing, CancellationToken.None);

        found.Installed.Should().BeTrue();
        found.InstallPath.Should().Be(install);
        found.VerifiedAt.Should().Be(T0);
        absent.Installed.Should().BeFalse();
        absent.InstallPath.Should().BeNull();
    }

    // ---- Plain executables ----------------------------------------------------------------------

    [Fact]
    public async Task Exe_AbsolutePath_IsInstalledWhenTheFileExists()
    {
        string exe = _dir.File(Path.Combine("Tools", "notepadpp.exe"));
        Game game = TestSupport.Game("Notepad++", LauncherType.Exe, exePath: exe);

        GameInstallStatus status = await NewDetector().DetectAsync(game, CancellationToken.None);

        status.Installed.Should().BeTrue();
        status.InstallPath.Should().Be(Path.GetDirectoryName(exe));
        status.LauncherReady.Should().BeTrue("plain executables need no launcher");
    }

    [Fact]
    public async Task Exe_RelativePathUnderCatalogueInstallPath_RequiresTheExecutable()
    {
        string install = _dir.Sub("Games", "Portable");
        Game withExe = TestSupport.Game("Portable A", LauncherType.Exe, exePath: @"bin\a.exe", installPath: install);
        Game withoutExe = TestSupport.Game("Portable B", LauncherType.Exe, exePath: @"bin\b.exe", installPath: install);
        _ = _dir.File(Path.Combine("Games", "Portable", "bin", "a.exe"));
        GameDetector detector = NewDetector();

        (await detector.DetectAsync(withExe, CancellationToken.None)).Installed.Should().BeTrue();
        (await detector.DetectAsync(withoutExe, CancellationToken.None)).Installed.Should().BeFalse();
    }

    [Fact]
    public async Task Exe_LibraryRootWithTitleDirectory_IsInstalled()
    {
        string root = _dir.Sub("Library");
        _settings.Games.LibraryRoots = [root];
        _ = _dir.File(Path.Combine("Library", "Dota 2", "dota2.exe"));
        Game game = TestSupport.Game("Dota 2", LauncherType.Exe, exePath: "dota2.exe");

        GameInstallStatus status = await NewDetector().DetectAsync(game, CancellationToken.None);

        status.Installed.Should().BeTrue();
        status.InstallPath.Should().Be(Path.Combine(root, "Dota 2"));
    }

    [Fact]
    public void ResolveExe_ResolvesRelativeAgainstInstallPathAndChecksExistence()
    {
        string install = _dir.Sub("Games", "X");
        string exe = _dir.File(Path.Combine("Games", "X", "bin", "x.exe"));

        GameDetector.ResolveExe(TestSupport.Game("X", LauncherType.Exe, exePath: @"bin\x.exe"), install).Should().Be(exe);
        GameDetector.ResolveExe(TestSupport.Game("X", LauncherType.Exe, exePath: @"bin\missing.exe"), install).Should().BeNull();
        GameDetector.ResolveExe(TestSupport.Game("X", LauncherType.Exe, exePath: exe), null).Should().Be(exe);
        GameDetector.ResolveExe(TestSupport.Game("X", LauncherType.Exe, exePath: @"bin\x.exe"), null).Should().BeNull();
        GameDetector.ResolveExe(TestSupport.Game("X", LauncherType.Exe), install).Should().BeNull();
    }

    // ---- Caching --------------------------------------------------------------------------------

    [Fact]
    public async Task DetectAsync_CachesForTtl_ThenRedetects()
    {
        string exe = _dir.File(Path.Combine("Cache", "game.exe"));
        Game game = TestSupport.Game("Cached", LauncherType.Exe, exePath: exe);
        GameDetector detector = NewDetector();

        (await detector.DetectAsync(game, CancellationToken.None)).Installed.Should().BeTrue();
        File.Delete(exe);

        (await detector.DetectAsync(game, CancellationToken.None)).Installed.Should().BeTrue("the result is cached");
        _time.Advance(GameDetector.CacheTtl - TimeSpan.FromSeconds(1));
        (await detector.DetectAsync(game, CancellationToken.None)).Installed.Should().BeTrue("the cache entry is still fresh");

        _time.Advance(TimeSpan.FromSeconds(2));
        GameInstallStatus stale = await detector.DetectAsync(game, CancellationToken.None);
        stale.Installed.Should().BeFalse("the TTL elapsed and the file is gone");
        stale.VerifiedAt.Should().BeNull();
    }

    [Fact]
    public async Task Invalidate_DropsTheCache()
    {
        string exe = _dir.File(Path.Combine("Cache2", "game.exe"));
        Game game = TestSupport.Game("Cached", LauncherType.Exe, exePath: exe);
        GameDetector detector = NewDetector();

        (await detector.DetectAsync(game, CancellationToken.None)).Installed.Should().BeTrue();
        File.Delete(exe);
        detector.Invalidate();

        (await detector.DetectAsync(game, CancellationToken.None)).Installed.Should().BeFalse();
    }

    // ---- DetectAllAsync -------------------------------------------------------------------------

    [Fact]
    public async Task DetectAllAsync_DetectsEveryGameInParallel_KeyedById()
    {
        var games = new List<Game>();
        var expected = new Dictionary<Guid, bool>();
        for (int i = 0; i < 24; i++)
        {
            string name = "g" + i.ToString(CultureInfo.InvariantCulture);
            bool installed = i % 2 == 0;
            string exe = Path.Combine(_dir.Root, "All", name + ".exe");
            if (installed)
            {
                _ = _dir.File(Path.Combine("All", name + ".exe"));
            }

            Game game = TestSupport.Game(name, LauncherType.Exe, exePath: exe);
            games.Add(game);
            expected[game.Id] = installed;
        }

        IReadOnlyDictionary<Guid, GameInstallStatus> result = await NewDetector().DetectAllAsync(games, CancellationToken.None);

        result.Should().HaveCount(games.Count);
        foreach ((Guid id, bool installed) in expected)
        {
            result[id].GameId.Should().Be(id);
            result[id].Installed.Should().Be(installed);
        }
    }

    [Fact]
    public async Task DetectAllAsync_ReusesCachedResults()
    {
        string exe = _dir.File(Path.Combine("All2", "game.exe"));
        Game game = TestSupport.Game("Once", LauncherType.Exe, exePath: exe);
        GameDetector detector = NewDetector();

        GameInstallStatus first = await detector.DetectAsync(game, CancellationToken.None);
        IReadOnlyDictionary<Guid, GameInstallStatus> all = await detector.DetectAllAsync([game], CancellationToken.None);

        all[game.Id].Should().BeSameAs(first);
    }

    [Fact]
    public async Task DetectAllAsync_HonoursCancellation()
    {
        Game game = TestSupport.Game("Any", LauncherType.Exe, exePath: Path.Combine(_dir.Root, "nope.exe"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => NewDetector().DetectAllAsync([game], cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- fixtures -------------------------------------------------------------------------------

    private GameDetector NewDetector() =>
        new(TestSupport.Monitor(_settings), new SystemClock(_time), NullLogger<GameDetector>.Instance);

    /// <summary>Fake Steam root (steam.exe + libraryfolders.vdf) and a second library holding appmanifest_730.acf.</summary>
    private (string Library, Game Game) SteamFixture(int stateFlags)
    {
        string steamRoot = _dir.Sub("Steam");
        string steamExe = _dir.File(Path.Combine("Steam", "steam.exe"));
        string library = _dir.Sub("SteamLibrary");
        _settings.Games.Launchers.Steam = new LauncherSettings { ExePath = steamExe };

        _ = _dir.File(Path.Combine("Steam", "steamapps", "libraryfolders.vdf"), $$"""
            // libraryfolders.vdf — comment lines are ignored by the parser
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"{{VdfEscape(steamRoot)}}"
            		"label"		""
            		"apps"
            		{
            		}
            	}
            	"1"
            	{
            		"path"		"{{VdfEscape(library)}}"
            		"label"		"Games \"SSD\""
            		"apps"
            		{
            			"730"		"32212254720"
            		}
            	}
            }
            """);

        _ = _dir.File(Path.Combine("SteamLibrary", "steamapps", "appmanifest_730.acf"), $$"""
            "AppState"
            {
            	"appid"		"730"
            	"name"		"Counter-Strike 2"
            	"StateFlags"		"{{stateFlags.ToString(CultureInfo.InvariantCulture)}}"
            	"installdir"		"Counter-Strike Global Offensive"
            	"buildid"		"17512345"
            	"SizeOnDisk"		"32212254720"
            	"UserConfig"
            	{
            		"language"		"english"
            	}
            }
            """);

        _ = _dir.File(Path.Combine("SteamLibrary", "steamapps", "common", "Counter-Strike Global Offensive", "game", "bin", "win64", "cs2.exe"));
        Game game = TestSupport.Game("Counter-Strike 2", LauncherType.Steam, appId: "730", exePath: @"game\bin\win64\cs2.exe");
        return (library, game);
    }

    private static string VdfEscape(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static void WriteEpicItem(string path, string appName, string installLocation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var item = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["FormatVersion"] = 0,
            ["bIsIncompleteInstall"] = false,
            ["AppVersionString"] = "2.5.1",
            ["LaunchExecutable"] = "TestGame.exe",
            ["InstallLocation"] = installLocation,
            ["InstallSize"] = 21474836480L,
            ["CatalogNamespace"] = "clubshelltests",
            ["CatalogItemId"] = Guid.NewGuid().ToString("N"),
            ["AppName"] = appName,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(item));
    }

    /// <summary>The highest directory of <paramref name="path"/> that does not exist yet (to delete after the test), or <see langword="null"/> when the path exists.</summary>
    private static string? TopmostMissingDirectory(string path)
    {
        string? missing = null;
        string? current = path;
        while (current is not null && !Directory.Exists(current))
        {
            missing = current;
            current = Path.GetDirectoryName(current);
        }

        return missing;
    }
}
