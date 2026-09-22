using ClubShell.Agent.AntiCheat;

namespace ClubShell.Agent.Tests;

/// <summary>
/// Covers the <c>bcdedit /enum {current}</c> parser of <see cref="SecureBootChecker"/>. Running bcdedit needs a
/// machine (and an elevated one); the parsing is where the anti-cheat verdict is actually decided, so it is the part
/// worth pinning: a boot element read as "absent" instead of "on" turns a bannable configuration into a pass.
/// </summary>
public sealed class SecureBootCheckerTests
{
    private const string Output = """
        Windows Boot Loader
        -------------------
        identifier              {current}
        device                  partition=C:
        path                    \WINDOWS\system32\winload.efi
        description             Windows 11
        testsigning             Yes
        nointegritychecks       Yes
        debug                   No
        hypervisorlaunchtype    Off
        """;

    [Theory]
    [InlineData("testsigning", true)]
    [InlineData("nointegritychecks", true)]
    [InlineData("debug", false)]
    public void ParseBcdFlag_ReadsBooleanElements(string element, bool expected)
    {
        SecureBootChecker.ParseBcdFlag(Output, element).Should().Be(expected);
    }

    [Fact]
    public void ParseBcdFlag_TreatsAnAbsentElementAsOff()
    {
        SecureBootChecker.ParseBcdFlag(Output, "bootdebug").Should().BeFalse();
    }

    [Fact]
    public void ParseBcdFlag_ReturnsNullForAnUnrecognisedValue()
    {
        SecureBootChecker.ParseBcdFlag("testsigning   Maybe", "testsigning").Should().BeNull();
    }

    [Fact]
    public void ParseBcdFlag_UnderstandsLocalisedValues()
    {
        SecureBootChecker.ParseBcdFlag("testsigning   Да", "testsigning").Should().BeTrue();
        SecureBootChecker.ParseBcdFlag("testsigning   Нет", "testsigning").Should().BeFalse();
    }

    [Fact]
    public void ParseBcdElement_ReadsTheHypervisorLaunchType()
    {
        SecureBootChecker.ParseBcdElement(Output, "hypervisorlaunchtype").Should().Be("Off");
        SecureBootChecker.ParseBcdElement(Output, "missing").Should().BeNull();
    }

    [Fact]
    public void ParseTestSigning_StillAnswersTheOldQuestion()
    {
        SecureBootChecker.ParseTestSigning(Output).Should().BeTrue();
        SecureBootChecker.ParseTestSigning("description   Windows 11").Should().BeFalse();
    }
}
