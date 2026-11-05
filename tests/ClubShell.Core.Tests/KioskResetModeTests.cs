using ClubShell.Core.Configuration;

namespace ClubShell.Core.Tests;

/// <summary>SHELL_CHANGES 78: <c>shell.kioskUser.resetMode</c> takes only the modes the Agent knows.</summary>
public sealed class KioskResetModeTests
{
    [Fact]
    public void The_default_is_auto() => new AgentSettings().Shell.KioskUser.ResetMode.Should().Be("auto");

    [Theory]
    [InlineData("auto")]
    [InlineData("logoff")]
    [InlineData("reboot")]
    public void A_known_mode_is_accepted(string mode)
    {
        var settings = new AgentSettings();
        settings.Shell.KioskUser.ResetMode = mode;

        AgentSettingsValidator.Check(settings).Should().NotContain(e => e.Contains("resetMode"));
    }

    [Fact]
    public void Another_mode_is_refused()
    {
        var settings = new AgentSettings();
        settings.Shell.KioskUser.ResetMode = "shutdown";

        AgentSettingsValidator.Check(settings).Should().Contain(e => e.StartsWith("shell.kioskUser.resetMode"));
    }
}
