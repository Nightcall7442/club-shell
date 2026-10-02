using System.Diagnostics;
using ClubShell.Windows.Processes;

namespace ClubShell.Windows.Tests;

/// <summary>
/// "Close game" from the Agent: <see cref="ProcessKiller.KillTree"/> gives the grace only to processes that took
/// WM_CLOSE — from the service session a game's windows are out of reach, and waiting anyway cost every close the whole
/// grace — and the game's <see cref="JobObject"/> catches what the tree walk cannot (a process whose parent exited).
/// Exercised on real windowless processes (cmd, waitfor) in the test runner's own session.
/// </summary>
public sealed class ProcessKillerTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public void KillTree_TerminatesAWindowlessTreeWithoutWaitingForTheGrace()
    {
        HashSet<int> before = Pids("waitfor");
        using Process parent = Start($"/c waitfor /t 60 {Signal()}");
        int child = WaitForNew("waitfor", before);

        var watch = Stopwatch.StartNew();
        IReadOnlyList<KilledProcess> killed = new ProcessKiller().KillTree(parent.Id, TimeSpan.FromSeconds(10));
        watch.Stop();

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "nothing could be asked to close, so nothing is waited for");
        killed.Select(k => k.Pid).Should().Contain(child);
        parent.WaitForExit(5000).Should().BeTrue();
        IsRunning(child).Should().BeFalse();
    }

    [Fact]
    public void TheJobCatchesAProcessWhoseParentHasExited()
    {
        HashSet<int> before = Pids("waitfor");
        using JobObject job = JobObject.Create(null, killOnClose: true);
        // cmd waits a second (time to put it in the job), starts waitfor in the background and exits: waitfor is
        // orphaned, but it inherited the job.
        using Process launcher = Start($"/c ping -n 2 127.0.0.1 >nul & start \"\" /b waitfor /t 60 {Signal()}");
        job.Assign(launcher);
        launcher.WaitForExit(10_000).Should().BeTrue();
        int orphan = WaitForNew("waitfor", before);

        new ProcessKiller().KillTree(launcher.Id, TimeSpan.Zero).Should().BeEmpty("the tree walk starts at a process that is gone");
        IsRunning(orphan).Should().BeTrue();

        job.Terminate();
        WaitUntil(() => !IsRunning(orphan)).Should().BeTrue();
    }

    private static string Signal() => "ClubShellKillerTest" + Guid.NewGuid().ToString("N");

    private static Process Start(string args) =>
        Process.Start(new ProcessStartInfo(Cmd, args) { CreateNoWindow = true, UseShellExecute = false })
        ?? throw new InvalidOperationException("cmd did not start");

    private static HashSet<int> Pids(string name) => Process.GetProcessesByName(name).Select(p =>
    {
        using (p)
        {
            return p.Id;
        }
    }).ToHashSet();

    /// <summary>The pid of a <paramref name="name"/> process that was not running before.</summary>
    private static int WaitForNew(string name, HashSet<int> before)
    {
        int found = 0;
        WaitUntil(() =>
        {
            found = Pids(name).FirstOrDefault(pid => !before.Contains(pid));
            return found != 0;
        }).Should().BeTrue($"{name} should start");
        return found;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool WaitUntil(Func<bool> condition)
    {
        long deadline = Environment.TickCount64 + 10_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                return false;
            }

            Thread.Sleep(50);
        }

        return true;
    }
}
