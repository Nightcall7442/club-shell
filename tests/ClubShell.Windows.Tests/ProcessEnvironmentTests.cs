using ClubShell.Windows.Processes;

namespace ClubShell.Windows.Tests;

/// <summary>
/// The Shell's environment (SHELL_CHANGES 40): the kiosk account's own <c>CLUBSHELL_*</c> variables (<c>setx
/// CLUBSHELL_DEV 1</c>) are dropped before the Agent's are added; everything else of the user's block stays.
/// </summary>
public sealed class ProcessEnvironmentTests
{
    [Fact]
    public void The_users_own_prefixed_variables_are_dropped_and_the_agents_set()
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Path"] = @"C:\Windows",
            ["CLUBSHELL_DEV"] = "1",
            ["clubshell_data_dir"] = @"C:\Users\club\fake",
            ["CLUBSHELL_PIPE"] = "evil",
            ["TEMP"] = @"C:\Users\club\AppData\Local\Temp",
        };

        ProcessEnvironment.Merge(
            variables,
            new Dictionary<string, string> { ["CLUBSHELL_PIPE"] = "clubshell-agent", ["CLUBSHELL_LOCALE"] = "ru" },
            ["CLUBSHELL_"]);

        variables.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["Path"] = @"C:\Windows",
            ["TEMP"] = @"C:\Users\club\AppData\Local\Temp",
            ["CLUBSHELL_PIPE"] = "clubshell-agent",
            ["CLUBSHELL_LOCALE"] = "ru",
        });
    }

    [Fact]
    public void Without_prefixes_only_the_extra_variables_are_set()
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CLUBSHELL_DEV"] = "1" };

        ProcessEnvironment.Merge(variables, new Dictionary<string, string> { ["A"] = "b" }, null);

        variables.Should().ContainKey("CLUBSHELL_DEV").And.ContainKey("A");
    }
}
