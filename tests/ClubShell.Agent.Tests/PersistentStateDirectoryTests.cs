using ClubShell.Agent.Storage;

namespace ClubShell.Agent.Tests;

/// <summary>SHELL_CHANGES 53: the SYSTEM-and-Administrators ACL goes only on a directory of the Agent's own.</summary>
public sealed class PersistentStateDirectoryTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("clubshell-persistent-");

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public void A_volume_root_is_refused() =>
        PersistentStateDirectory.Refusal(Path.GetPathRoot(_dir.FullName)!).Should().Be("a volume root");

    [Fact]
    public void A_new_or_empty_directory_or_one_with_only_the_agents_files_is_accepted()
    {
        PersistentStateDirectory.Refusal(Path.Combine(_dir.FullName, "missing")).Should().BeNull();
        PersistentStateDirectory.Refusal(_dir.FullName).Should().BeNull();

        Directory.CreateDirectory(Path.Combine(_dir.FullName, "secure"));
        Directory.CreateDirectory(Path.Combine(_dir.FullName, "cache"));
        File.WriteAllText(Path.Combine(_dir.FullName, "agent-identity.json"), "{}");
        PersistentStateDirectory.Refusal(_dir.FullName).Should().BeNull("an install from before the marker holds only these");
    }

    [Fact]
    public void A_directory_with_other_files_is_refused_unless_the_agent_prepared_it()
    {
        Directory.CreateDirectory(Path.Combine(_dir.FullName, "Games"));
        File.WriteAllText(Path.Combine(_dir.FullName, "notes.txt"), "x");

        PersistentStateDirectory.Refusal(_dir.FullName).Should().StartWith("a directory that holds other files").And.Contain("Games");

        File.WriteAllText(Path.Combine(_dir.FullName, PersistentStateDirectory.MarkerFileName), "x");
        PersistentStateDirectory.Refusal(_dir.FullName).Should().BeNull();
    }
}
