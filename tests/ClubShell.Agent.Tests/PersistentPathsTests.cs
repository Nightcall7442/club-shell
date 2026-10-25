using ClubShell.Core.Configuration;

namespace ClubShell.Agent.Tests;

/// <summary>
/// <c>paths.persistent</c> (SHELL_CHANGES 25): on a PC whose C: is clean at every boot the identity, the outbox and the
/// power acks go to a directory that survives the reboot; everything else stays under <c>paths.programData</c>.
/// </summary>
public sealed class PersistentPathsTests
{
    private static readonly string ProgramData = Path.Combine(Path.GetTempPath(), "cs-programdata");
    private static readonly string Persistent = Path.Combine(Path.GetTempPath(), "cs-persistent");

    [Fact]
    public void Without_a_persistent_directory_everything_stays_under_program_data()
    {
        var settings = new AgentSettings();
        settings.Paths.ProgramData = ProgramData;

        settings.SecureDir.Should().Be(Path.Combine(ProgramData, "secure"));
        settings.ResolvePersistentPath(settings.Offline.StorePath).Should().Be(settings.ResolvePath(settings.Offline.StorePath));
        settings.ResolvePersistentPath("agent-identity.json").Should().Be(Path.Combine(ProgramData, "agent-identity.json"));
    }

    [Fact]
    public void With_a_persistent_directory_the_identity_the_outbox_and_the_secrets_move_there_and_the_rest_stays()
    {
        var settings = new AgentSettings();
        settings.Paths.ProgramData = ProgramData;
        settings.Paths.Persistent = Persistent;

        settings.SecureDir.Should().Be(Path.Combine(Persistent, "secure"));
        settings.AgentTokensPath.Should().StartWith(Persistent);
        settings.ResolvePersistentPath("agent-identity.json").Should().Be(Path.Combine(Persistent, "agent-identity.json"));
        settings.ResolvePersistentPath(settings.Offline.StorePath).Should().StartWith(Persistent);
        settings.CacheDir.Should().Be(Path.Combine(ProgramData, "cache"), "the media and catalogue caches may be rebuilt after a reboot");
        settings.LogsDir.Should().StartWith(ProgramData);
    }
}
