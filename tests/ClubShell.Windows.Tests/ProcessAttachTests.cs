using System.Diagnostics;
using ClubShell.Windows.Processes;

namespace ClubShell.Windows.Tests;

/// <summary>
/// <see cref="ProcessLauncher.TryAttach"/> lets the watchdog take over the kiosk Shell that Winlogon started at logon
/// instead of starting a second copy. Exercised on a real child process in the test runner's own session.
/// </summary>
public sealed class ProcessAttachTests
{
    private static readonly string Ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");

    [Fact]
    public void TryAttach_FindsTheRunningProcessInItsSession()
    {
        using Process child = StartPing();
        try
        {
            using LaunchedProcess? attached = new ProcessLauncher().TryAttach(Ping.ToLowerInvariant(), (uint)child.SessionId);

            attached.Should().NotBeNull();
            attached!.HasExited.Should().BeFalse();
            Process.GetProcessesByName("PING").Select(p => p.Id).Should().Contain(attached.Pid);
        }
        finally
        {
            child.Kill();
        }
    }

    [Fact]
    public void TryAttach_IgnoresOtherSessionsAndOtherPaths()
    {
        using Process child = StartPing();
        try
        {
            var launcher = new ProcessLauncher();

            launcher.TryAttach(Ping, unchecked((uint)child.SessionId + 1000)).Should().BeNull();
            launcher.TryAttach(Path.Combine(Path.GetTempPath(), "PING.EXE"), (uint)child.SessionId).Should().BeNull();
        }
        finally
        {
            child.Kill();
        }
    }

    private static Process StartPing() =>
        Process.Start(new ProcessStartInfo(Ping, "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })
        ?? throw new InvalidOperationException("ping did not start");
}
