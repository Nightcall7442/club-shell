using System.IO.Compression;
using ClubShell.Agent.Games.Accounts;
using ClubShell.Agent.Games.Saves;
using ClubShell.Contracts.Games;

namespace ClubShell.Agent.Tests;

public sealed class SettingsBundleTests : IDisposable
{
    private const long Max = 1024 * 1024;
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
        Game game = GameWith(install, @"{installPath}\cfg\autoexec.cfg", @"%LOCALAPPDATA%\Game\Config\", "relative\\path", "%UNKNOWN%\\x", @"%APPDATA%\..\..\escape");

        var targets = SettingsBundle.Targets(game, profile);

        Assert.Equal(2, targets.Count);
        Assert.Equal(Path.Combine(install, "cfg", "autoexec.cfg"), targets[0].Path);
        // A trailing separator is trimmed, so the directory restores (it used to fail the containment check).
        Assert.Equal(Path.Combine(profile.LocalAppData, "Game", "Config"), targets[1].Path);
        Assert.Equal(profile.LocalAppData, targets[1].Anchor);
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
        var pcA = new Profile(_dir.Sub("pcA"));
        Game onA = GameWith(_dir.Sub("pcA-games", "cs2"), @"{installPath}\cfg\autoexec.cfg", @"%LOCALAPPDATA%\Game\Config\");
        var targetsA = SettingsBundle.Targets(onA, pcA);
        Write(targetsA[0].Path, "sensitivity 1.8");
        Write(Path.Combine(targetsA[1].Path, "Input.ini"), "jump=space");
        Write(Path.Combine(targetsA[1].Path, "Video", "Graphics.ini"), "shadows=low");

        string zip = Path.Combine(_dir.Root, "bundle.zip");
        Assert.Equal(3, SettingsBundle.Pack(targetsA, zip, Max));

        var pcB = new Profile(_dir.Sub("pcB"));
        Game onB = GameWith(_dir.Sub("pcB-games", "cs2"), @"{installPath}\cfg\autoexec.cfg", @"%LOCALAPPDATA%\Game\Config\");
        var targetsB = SettingsBundle.Targets(onB, pcB);
        Write(targetsB[0].Path, "sensitivity 2.5");

        Assert.Equal(3, SettingsBundle.Unpack(zip, targetsB));
        Assert.Equal("sensitivity 1.8", File.ReadAllText(targetsB[0].Path));
        Assert.Equal("jump=space", File.ReadAllText(Path.Combine(targetsB[1].Path, "Input.ini")));
        Assert.Equal("shadows=low", File.ReadAllText(Path.Combine(targetsB[1].Path, "Video", "Graphics.ini")));
    }

    [Fact]
    public void Editing_the_path_list_never_sends_a_file_to_another_path()
    {
        var profile = new Profile(_dir.Sub("home"));
        string install = _dir.Sub("games", "cs2");
        var before = SettingsBundle.Targets(GameWith(install, @"{installPath}\cfg\autoexec.cfg", @"{installPath}\cfg\video.txt"), profile);
        Write(before[0].Path, "binds");
        Write(before[1].Path, "video");
        string zip = Path.Combine(_dir.Root, "bundle.zip");
        SettingsBundle.Pack(before, zip, Max);
        File.Delete(before[0].Path);
        File.Delete(before[1].Path);

        // The owner removed the first line: video.txt must still get "video", not autoexec's "binds".
        var after = SettingsBundle.Targets(GameWith(install, @"{installPath}\cfg\video.txt"), profile);
        Assert.Equal(1, SettingsBundle.Unpack(zip, after));
        Assert.Equal("video", File.ReadAllText(after[0].Path));
        Assert.False(File.Exists(before[0].Path));
    }

    [Fact]
    public void Nothing_to_carry_writes_no_bundle_unless_a_baseline_is_asked_for()
    {
        var profile = new Profile(_dir.Sub("home"));
        var targets = SettingsBundle.Targets(GameWith(null, @"%APPDATA%\Nothing"), profile);
        string zip = Path.Combine(_dir.Root, "empty.zip");
        Assert.Equal(0, SettingsBundle.Pack(targets, zip, Max));
        Assert.False(File.Exists(zip));
        Assert.Equal(0, SettingsBundle.Pack(targets, zip, Max, writeEmpty: true));
        Assert.True(File.Exists(zip));
    }

    [Fact]
    public void A_path_holding_more_than_the_limit_is_refused_before_compressing()
    {
        var profile = new Profile(_dir.Sub("home"));
        var targets = SettingsBundle.Targets(GameWith(null, @"%LOCALAPPDATA%\Huge"), profile);
        Write(Path.Combine(targets[0].Path, "a.bin"), new string('x', 600));
        Write(Path.Combine(targets[0].Path, "b.bin"), new string('x', 600));
        string zip = Path.Combine(_dir.Root, "huge.zip");

        Assert.Equal(-1, SettingsBundle.Pack(targets, zip, maxBytes: 1000));
        Assert.False(File.Exists(zip));
    }

    [Fact]
    public void Baseline_cycle_leaves_the_next_player_with_the_club_defaults()
    {
        var profile = new Profile(_dir.Sub("home"));
        var targets = SettingsBundle.Targets(GameWith(_dir.Sub("games", "cs2"), @"{installPath}\cfg\autoexec.cfg", @"%LOCALAPPDATA%\Game\Config"), profile);
        Write(targets[0].Path, "club default");
        string baseline = Path.Combine(_dir.Root, "baseline.zip");
        SettingsBundle.Pack(targets, baseline, Max, writeEmpty: true);

        // Player A plays: their autoexec and an extra config file.
        Write(targets[0].Path, "player A");
        Write(Path.Combine(targets[1].Path, "Input.ini"), "A binds");

        SettingsBundle.Clear(targets);
        SettingsBundle.Unpack(baseline, targets);

        Assert.Equal("club default", File.ReadAllText(targets[0].Path));
        Assert.False(Directory.Exists(targets[1].Path));
    }

    [Fact]
    public void Entries_escaping_their_target_of_unknown_keys_or_the_wrong_name_are_ignored()
    {
        var profile = new Profile(_dir.Sub("home"));
        var targets = SettingsBundle.Targets(GameWith(_dir.Sub("games", "cs2"), @"%LOCALAPPDATA%\Config", @"{installPath}\cfg\autoexec.cfg"), profile);
        string dirKey = targets[0].Key;
        string fileKey = targets[1].Key;
        string zip = Path.Combine(_dir.Root, "evil.zip");
        using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (string name in new[] { $"{dirKey}/d/../../escaped.txt", "0000000000/d/unknown.txt", $"{dirKey}/x/bad-kind.txt", $"{fileKey}/f/other.cfg", $"{dirKey}/d/ok.txt" })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write("data");
            }
        }

        Assert.Equal(1, SettingsBundle.Unpack(zip, targets));
        Assert.True(File.Exists(Path.Combine(targets[0].Path, "ok.txt")));
        Assert.False(File.Exists(targets[1].Path));
        Assert.False(File.Exists(Path.Combine(profile.UserProfile, "escaped.txt")));
    }

    [Fact]
    public void A_link_planted_inside_a_settings_path_is_never_followed()
    {
        var profile = new Profile(_dir.Sub("home"));
        string secret = _dir.Sub("system");
        Write(Path.Combine(secret, "sam.txt"), "secret");
        var targets = SettingsBundle.Targets(GameWith(null, @"%LOCALAPPDATA%\Game\Config"), profile);
        // The player replaces the settings folder with a link to a folder the Agent (SYSTEM) can reach and they cannot.
        Directory.CreateDirectory(Path.GetDirectoryName(targets[0].Path)!);
        Directory.CreateSymbolicLink(targets[0].Path, secret);

        string zip = Path.Combine(_dir.Root, "leak.zip");
        Assert.Equal(0, SettingsBundle.Pack(targets, zip, Max));

        string payload = Path.Combine(_dir.Root, "payload.zip");
        using (ZipArchive archive = ZipFile.Open(payload, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry($"{targets[0].Key}/d/sam.txt").Open()))
        {
            writer.Write("overwritten");
        }

        Assert.Equal(0, SettingsBundle.Unpack(payload, targets));
        SettingsBundle.Clear(targets);
        Assert.Equal("secret", File.ReadAllText(Path.Combine(secret, "sam.txt")));
    }
}
