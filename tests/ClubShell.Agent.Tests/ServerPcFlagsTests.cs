using ClubShell.Agent.Server;
using ClubShell.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClubShell.Agent.Tests;

/// <summary>
/// <c>pcs.stateless</c> from the heartbeat answer (SHELL_CHANGES 78): kept across an Agent restart, and an older server
/// that does not send it changes nothing.
/// </summary>
public sealed class ServerPcFlagsTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("pcflags-");

    [Fact]
    public void The_flag_survives_a_restart_and_null_keeps_it()
    {
        var settings = new AgentSettings();
        settings.Paths.ProgramData = _dir.FullName;
        settings.Paths.Cache = Path.Combine(_dir.FullName, "cache");

        var flags = new ServerPcFlags(TestSupport.Monitor(settings), NullLogger<ServerPcFlags>.Instance);
        flags.Stateless.Should().BeNull();
        flags.Update(true);
        flags.Update(null);
        flags.Stateless.Should().BeTrue();

        var restarted = new ServerPcFlags(TestSupport.Monitor(settings), NullLogger<ServerPcFlags>.Instance);
        restarted.Stateless.Should().BeTrue();
        restarted.Update(false);
        new ServerPcFlags(TestSupport.Monitor(settings), NullLogger<ServerPcFlags>.Instance).Stateless.Should().BeFalse();
    }

    public void Dispose()
    {
        try
        {
            _dir.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
