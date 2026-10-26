using ClubShell.Windows.Users;

namespace ClubShell.Windows.Tests;

/// <summary>SHELL_CHANGES 42: what is carried across a profile reset never carries a player's sign-in.</summary>
public sealed class ProfileResetSignInTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("clubshell-signin-");

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public void The_riot_client_session_is_removed_and_the_rest_of_the_folder_kept()
    {
        string riot = Create(@"Riot Client\Data\RiotGamesPrivateSettings.yaml", @"Riot Client\Data\RiotClientPrivateSettings.yaml", @"Riot Client\Config\RiotClientSettings.yaml");

        ProfileReset.RemoveSignIns(riot, @"AppData\Local\Riot Games", out bool carried).Should().Be(2);

        carried.Should().BeTrue();
        File.Exists(Path.Combine(riot, "Riot Client", "Data", "RiotGamesPrivateSettings.yaml")).Should().BeFalse();
        File.Exists(Path.Combine(riot, "Riot Client", "Data", "RiotClientPrivateSettings.yaml")).Should().BeFalse();
        File.Exists(Path.Combine(riot, "Riot Client", "Config", "RiotClientSettings.yaml")).Should().BeTrue();
    }

    [Fact]
    public void The_faceit_cookies_and_web_storage_are_removed()
    {
        string faceit = Create(@"Network\Cookies", @"Local Storage\leveldb\000003.log", "Cookies", @"GPUCache\data_0", "settings.json");

        ProfileReset.RemoveSignIns(faceit, @"AppData\Roaming\FACEIT\", out bool carried).Should().Be(3);

        carried.Should().BeTrue();
        Directory.EnumerateFileSystemEntries(faceit).Select(Path.GetFileName).Should().BeEquivalentTo(["GPUCache", "settings.json"]);
    }

    [Fact]
    public void An_entry_inside_a_sign_in_path_is_not_carried()
    {
        string storage = Create("leveldb");

        ProfileReset.RemoveSignIns(storage, @"AppData\Roaming\faceit\Local Storage", out bool carried).Should().Be(0);

        carried.Should().BeFalse();
    }

    [Fact]
    public void Other_preserved_folders_are_left_alone()
    {
        string shaders = Create("cache.bin");

        ProfileReset.RemoveSignIns(shaders, @"AppData\Local\NVIDIA\DXCache", out bool carried).Should().Be(0);

        carried.Should().BeTrue();
        File.Exists(Path.Combine(shaders, "cache.bin")).Should().BeTrue();
    }

    private string Create(params string[] files)
    {
        string root = Path.Combine(_dir.FullName, Guid.NewGuid().ToString("N"));
        foreach (string file in files)
        {
            string path = Path.Combine([root, .. file.Split('\\')]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }

        Directory.CreateDirectory(root);
        return root;
    }
}
