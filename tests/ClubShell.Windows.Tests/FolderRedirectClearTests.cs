using ClubShell.Windows.Users;

namespace ClubShell.Windows.Tests;

/// <summary>
/// The redirected folders live outside the profile, so a profile reset alone left the previous player's files to the
/// next one (SHELL_CHANGES 26): <see cref="FolderRedirect.Clear"/> empties them and nothing else.
/// </summary>
public sealed class FolderRedirectClearTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "clubshell-redirect-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_base))
            {
                Directory.Delete(_base, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public void Clear_empties_every_redirected_folder_and_keeps_the_folders_and_anything_else()
    {
        string desktop = Write("Desktop", "notes.txt");
        string nested = Write(Path.Combine("Documents", "My Games", "CS2"), "config.cfg");
        File.SetAttributes(Path.Combine(_base, "Documents", "My Games", "CS2", "config.cfg"), FileAttributes.ReadOnly);
        Write("Saved Games", "save.dat");
        string other = Write("NotAShellFolder", "keep.txt");

        new FolderRedirect().Clear(_base).Should().Be(0);

        Directory.Exists(Path.Combine(_base, "Desktop")).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(Path.Combine(_base, "Desktop")).Should().BeEmpty();
        Directory.EnumerateFileSystemEntries(Path.Combine(_base, "Documents")).Should().BeEmpty();
        Directory.EnumerateFileSystemEntries(Path.Combine(_base, "Saved Games")).Should().BeEmpty();
        File.Exists(desktop).Should().BeFalse();
        File.Exists(nested).Should().BeFalse();
        File.Exists(other).Should().BeTrue("only the redirected shell folders are emptied");
    }

    [Fact]
    public void A_link_inside_is_removed_without_touching_what_it_points_to()
    {
        string outside = Path.Combine(_base, "..", Path.GetFileName(_base) + "-target");
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "games.bin"), "x");
            Directory.CreateDirectory(Path.Combine(_base, "Downloads"));
            string link = Path.Combine(_base, "Downloads", "link");
            Directory.CreateSymbolicLink(link, outside);

            new FolderRedirect().Clear(_base).Should().Be(0);

            Directory.Exists(link).Should().BeFalse();
            File.Exists(Path.Combine(outside, "games.bin")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Clear_of_a_base_without_redirected_folders_does_nothing() =>
        new FolderRedirect().Clear(_base).Should().Be(0);

    private string Write(string folder, string file)
    {
        string dir = Path.Combine(_base, folder);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, file);
        File.WriteAllText(path, "x");
        return path;
    }
}
