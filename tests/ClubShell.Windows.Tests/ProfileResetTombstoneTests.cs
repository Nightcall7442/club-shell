using ClubShell.Windows.Users;

namespace ClubShell.Windows.Tests;

/// <summary>SHELL_CHANGES 76: a reset moves the profile aside first and deletes what it moved — and only that.</summary>
public sealed class ProfileResetTombstoneTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("clubshell-users-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void The_profiles_tombstones_are_deleted_and_every_other_folder_kept()
    {
        Create(@"club.reset-20261105120000000\AppData\Local\Temp\x.tmp", @"club.reset-20261104090000000\NTUSER.DAT", @"club\NTUSER.DAT", @"club2.reset-20261105120000000\NTUSER.DAT", @"club.PC\NTUSER.DAT", @"Public\desktop.ini");
        var errors = new List<string>();

        ProfileReset.DeleteTombstones(_root.FullName, "club", errors).Should().Be(0);

        errors.Should().BeEmpty();
        _root.EnumerateDirectories().Select(d => d.Name).Should().BeEquivalentTo(["club", "club2.reset-20261105120000000", "club.PC", "Public"]);
    }

    [Fact]
    public void Read_only_files_go_too()
    {
        string file = Create(@"club.reset-20261105120000000\AppData\Roaming\locked.dat");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        var errors = new List<string>();

        ProfileReset.DeleteTombstones(_root.FullName, "club", errors).Should().Be(0);

        errors.Should().BeEmpty();
        Directory.Exists(Path.Combine(_root.FullName, "club.reset-20261105120000000")).Should().BeFalse();
    }

    [Fact]
    public void A_link_inside_a_tombstone_is_removed_without_emptying_its_target()
    {
        string outside = Create(@"Shared\keep.txt");
        Create(@"club.reset-20261105120000000\NTUSER.DAT");
        Directory.CreateSymbolicLink(Path.Combine(_root.FullName, "club.reset-20261105120000000", "Link"), Path.GetDirectoryName(outside)!);
        var errors = new List<string>();

        ProfileReset.DeleteTombstones(_root.FullName, "club", errors).Should().Be(0);

        errors.Should().BeEmpty();
        File.Exists(outside).Should().BeTrue();
    }

    [Fact]
    public void A_missing_root_has_nothing_to_delete()
    {
        var errors = new List<string>();

        ProfileReset.DeleteTombstones(null, "club", errors).Should().Be(0);
        ProfileReset.DeleteTombstones(Path.Combine(_root.FullName, "missing"), "club", errors).Should().Be(0);

        errors.Should().BeEmpty();
    }

    private string Create(params string[] files)
    {
        string last = "";
        foreach (string file in files)
        {
            last = Path.Combine([_root.FullName, .. file.Split('\\')]);
            Directory.CreateDirectory(Path.GetDirectoryName(last)!);
            File.WriteAllText(last, "x");
        }

        return last;
    }
}
