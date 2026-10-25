using ClubShell.Agent.Games;

namespace ClubShell.Agent.Tests;

/// <summary>
/// <c>games.libraryRoots</c> that are Steam libraries themselves (SHELL_CHANGES 31): the games disk of a club PC holds
/// <c>G:\Steam</c> with its <c>steamapps</c>, which the client's own <c>libraryfolders.vdf</c> may not list.
/// </summary>
public sealed class SteamLibrariesTests
{
    [Fact]
    public void Only_roots_with_a_steamapps_folder_are_Steam_libraries_each_once()
    {
        using var dir = new TempDir();
        string steam = Path.Combine(dir.Root, "G", "Steam");
        string games = Path.Combine(dir.Root, "G", "Games");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(games);

        IReadOnlyList<string> libraries = GameDetector.SteamLibrariesIn([steam, games, " ", steam + Path.DirectorySeparatorChar, Path.Combine(dir.Root, "missing")], Directory.Exists);

        libraries.Should().Equal(Path.GetFullPath(steam));
    }
}
