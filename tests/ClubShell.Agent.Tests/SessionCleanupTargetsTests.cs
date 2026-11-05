using ClubShell.Agent.Session;
using ClubShell.Core.Configuration;

namespace ClubShell.Agent.Tests;

/// <summary>SHELL_CHANGES 85: what the next player must not find on the games disks is cleaned after every session.</summary>
public sealed class SessionCleanupTargetsTests
{
    [Fact]
    public void The_configured_paths_and_steams_loginusers_are_cleaned_by_default()
    {
        var settings = new AgentSettings();
        string steam = Path.Combine(Path.GetTempPath(), "games", "Steam", "steam.exe");
        settings.Games.Launchers.Steam!.ExePath = steam;
        settings.Session.CleanupPaths = [Path.Combine("%CLUBSHELL_TEST_ROOT%", "cstrike", "config.cfg"), "  "];
        Environment.SetEnvironmentVariable("CLUBSHELL_TEST_ROOT", Path.Combine(Path.GetTempPath(), "H"));

        SessionCleanup.CleanupTargets(settings).Should().Equal(
            Path.Combine(Path.GetTempPath(), "H", "cstrike", "config.cfg"),
            Path.Combine(Path.GetTempPath(), "games", "Steam", "config", "loginusers.vdf"));
    }

    [Fact]
    public void Steams_file_is_left_alone_when_turned_off_or_without_a_steam_path()
    {
        var settings = new AgentSettings();
        settings.Session.CleanSteamLoginUsers = false;
        SessionCleanup.CleanupTargets(settings).Should().BeEmpty();

        settings.Session.CleanSteamLoginUsers = true;
        settings.Games.Launchers.Steam!.ExePath = "";
        SessionCleanup.CleanupTargets(settings).Should().BeEmpty();
    }
}
