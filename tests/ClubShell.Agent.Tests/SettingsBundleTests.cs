using System.IO.Compression;
using ClubShell.Agent.Games.Accounts;
using ClubShell.Agent.Games.Saves;
using ClubShell.Contracts.Games;

namespace ClubShell.Agent.Tests;

public sealed class SettingsBundleTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed class Profile(string root) : IKioskProfilePaths
    {
        public string LocalAppData { get; } = Path.Combine(root, "Local");
        public string RoamingAppData { get; } = Path.Combine(root, "Roaming");
        public string UserProfile { get; } = root;
    }

    private static Game GameWith(string? installPath, params string[] paths) =>
        TestSupport.Game("CS2", LauncherType.Steam) with { InstallPath = installPath, SettingsPaths = paths };

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Templates_expand_against_the_kiosk_profile_and_the_install_path()
    {
        var profile = new Profile(_dir.Sub("home"));
        string install = _dir.Sub("games", "cs2");
        Game game = GameWith(install, @"{installPath}\cfg\autoexec.cfg", @"%LOCALAPPDATA%\Game\Config", "relative\\path", "%UNKNOWN%\\x");

        var targets = SettingsBundle.Targets(game, profile);

        Assert.Equal(2, targets.Count);
        Assert.Equal(Path.Combine(install, "cfg", "autoexec.cfg"), targets[0].Path);
        Assert.Equal(Path.Combine(profile.LocalAppData, "Game", "Config"), targets[1].Path);
    }

    [Fact]
    public void Install_path_templates_are_skipped_when_the_game_has_no_install_path()
    {
        var profile = new Profile(_dir.Sub("home"));
        Assert.Empty(SettingsBundle.Targets(GameWith(null, @"{installPath}\cfg\a.cfg"), profile));
    }

    [Fact]
    public void Files_and_directories_round_trip_to_another_pc()
    {
        // PC A: the player's autoexec and a whole config folder.
        var pcA = new Profile(_dir.Sub("pcA"));
        Game onA = GameWith(_dir.Sub("pcA-games", "cs2"), @"{installPath}\cfg\autoexec.cfg", @"%LOCALAPPDATA%\Game\Config");
        var targetsA = SettingsBundle.Targets(onA, pcA);
        Write(targetsA[0].Path, "sensitivity 1.8");
        Write(Path.Combine(targetsA[1].Path, "Input.ini"), "jump=space");
        Write(Path.Combine(targetsA[1].Path, "Video", "Graphics.ini"), "shadows=low");

        string zip = Path.Combine(_dir.Root, "bundle.zip");
        Assert.Equal(3, SettingsBundle.Pack(targetsA, zip));

        // PC B: different disk layout, the club's defaults already there.
        var pcB = new Profile(_dir.Sub("pcB"));
        Game onB = GameWith(_dir.Sub("pcB-games", "cs2"), @"{installPath}\cfg\autoexec.cfg", @"%LOCALAPPDATA%\Game\Config");
        var targetsB = SettingsBundle.Targets(onB, pcB);
        Write(targetsB[0].Path, "sensitivity 2.5");

        Assert.Equal(3, SettingsBundle.Unpack(zip, targetsB));
        Assert.Equal("sensitivity 1.8", File.ReadAllText(targetsB[0].Path));
        Assert.Equal("jump=space", File.ReadAllText(Path.Combine(targetsB[1].Path, "Input.ini")));
        Assert.Equal("shadows=low", File.ReadAllText(Path.Combine(targetsB[1].Path, "Video", "Graphics.ini")));
    }

    [Fact]
    public void Nothing_to_carry_writes_no_bundle()
    {
        var profile = new Profile(_dir.Sub("home"));
        string zip = Path.Combine(_dir.Root, "empty.zip");
        Assert.Equal(0, SettingsBundle.Pack(SettingsBundle.Targets(GameWith(null, @"%APPDATA%\Nothing"), profile), zip));
        Assert.False(File.Exists(zip));
    }

    [Fact]
    public void Entries_escaping_their_target_or_of_unknown_paths_are_ignored()
    {
        string target = _dir.Sub("home", "Config");
        string zip = Path.Combine(_dir.Root, "evil.zip");
        using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (string name in new[] { "0/d/../../escaped.txt", "7/d/unknown.txt", "0/x/bad-kind.txt", "0/d/ok.txt" })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write("data");
            }
        }

        Assert.Equal(1, SettingsBundle.Unpack(zip, [(0, target)]));
        Assert.True(File.Exists(Path.Combine(target, "ok.txt")));
        Assert.False(File.Exists(Path.Combine(_dir.Root, "escaped.txt")));
    }
}
