using ClubShell.Core.Configuration;

namespace ClubShell.Core.Tests;

/// <summary>SHELL_CHANGES 82: <c>games.registryDefaults</c> reaches only the player's own settings under HKCU\Software.</summary>
public sealed class GameRegistryDefaultTests
{
    [Theory]
    [InlineData(@"Software\Valve\Half-Life\Settings", @"Software\Valve\Half-Life\Settings")]
    [InlineData(@"HKCU\Software\Valve\Half-Life\Settings", @"Software\Valve\Half-Life\Settings")]
    [InlineData(@"HKEY_CURRENT_USER\SOFTWARE\CSPro\Half-Life\Settings\", @"SOFTWARE\CSPro\Half-Life\Settings")]
    [InlineData("Software/Valve/Half-Life/Settings", @"Software\Valve\Half-Life\Settings")]
    public void A_key_under_software_is_taken_relative_to_hkcu(string key, string expected) =>
        GameRegistryDefault.NormalizeKey(key).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Software")]
    [InlineData(@"Control Panel\Desktop")]
    [InlineData(@"HKLM\Software\Valve")]
    [InlineData(@"Software\Policies\Microsoft\Windows\System")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\policies")]
    [InlineData(@"Software\Valve\..\Policies")]
    [InlineData(@"Software\\Valve")]
    public void Anything_else_is_refused(string? key) => GameRegistryDefault.NormalizeKey(key).Should().BeNull();

    [Fact]
    public void A_refused_key_fails_validation()
    {
        var settings = new AgentSettings();
        settings.Games.RegistryDefaults.Add(new GameRegistryDefault { Key = @"Software\Valve\Half-Life\Settings", Dwords = { ["ScreenWindowed"] = 0 } });
        settings.Games.RegistryDefaults.Add(new GameRegistryDefault { Key = @"Software\Policies\Microsoft" });

        AgentSettingsValidator.Check(settings).Where(e => e.StartsWith("games.registryDefaults")).Should().ContainSingle()
            .Which.Should().StartWith("games.registryDefaults[1].key");
    }
}
