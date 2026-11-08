using ClubShell.Windows.Processes;

namespace ClubShell.Windows.Tests;

/// <summary>The command line the launch log shows (SHELL_CHANGES 83): exact, but without a pooled account's secrets.</summary>
public sealed class ProcessCommandLineLogTests
{
    [Fact]
    public void The_logged_command_line_hides_the_credentials_and_keeps_the_rest()
    {
        string line = ProcessLauncher.BuildCommandLine(@"C:\Program Files (x86)\Steam\steam.exe", "-login club_07 s3cret -applaunch 730 -novid");

        ProcessLauncher.Redact(line, " -login club_07 s3cret ").Should()
            .Be("\"C:\\Program Files (x86)\\Steam\\steam.exe\" *** -applaunch 730 -novid");
        ProcessLauncher.Redact(line, null).Should().Be(line);
        ProcessLauncher.Redact(@"H:\cs16\hl.exe -game cstrike", "  ").Should().Be(@"H:\cs16\hl.exe -game cstrike");
    }
}
