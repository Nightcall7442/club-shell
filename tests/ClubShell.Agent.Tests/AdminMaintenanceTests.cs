using ClubShell.Agent.Watchdog;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Tests;

/// <summary>
/// The administrator's maintenance window (SHELL_CHANGES 28, 33, 34): opened by <c>sys.adminExit</c>, it ends when it
/// times out or when the kiosk session it was opened in signs out; listeners hear the start and the end once each.
/// </summary>
public sealed class AdminMaintenanceTests
{
    [Fact]
    public void A_window_lasts_until_it_times_out()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 25, 20, 0, 0, TimeSpan.Zero));
        using var window = new AdminMaintenance(new SystemClock(time), NullLogger<AdminMaintenance>.Instance);
        var changes = new List<bool>();
        window.Changed += (_, active) => changes.Add(active);

        window.IsActive.Should().BeFalse();
        window.Begin(TimeSpan.FromMinutes(30), sessionId: 2).Should().Be(time.Now + TimeSpan.FromMinutes(30));
        window.IsActive.Should().BeTrue();
        time.Now += TimeSpan.FromMinutes(31);
        window.IsActive.Should().BeFalse("a window that timed out is over even before its timer fires");
        changes.Should().Equal(true);
    }

    [Fact]
    public void Signing_out_of_the_maintenance_session_ends_the_window_and_another_session_does_not()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        using var window = new AdminMaintenance(new SystemClock(time), NullLogger<AdminMaintenance>.Instance);
        var changes = new List<bool>();
        window.Changed += (_, active) => changes.Add(active);

        window.Begin(TimeSpan.FromMinutes(30), sessionId: 2);
        window.Begin(TimeSpan.FromMinutes(60), sessionId: 2);
        window.Until.Should().Be(time.Now + TimeSpan.FromMinutes(60), "a second exit extends the window");
        window.OnSessionEnded(5);
        window.IsActive.Should().BeTrue("an RDP or other session signing out is not the kiosk handing back");
        window.OnSessionEnded(2);
        window.IsActive.Should().BeFalse();
        window.Until.Should().BeNull();
        window.End("again");
        changes.Should().Equal(true, false);
    }

    [Fact]
    public void A_failing_listener_does_not_keep_the_others_from_hearing_the_end()
    {
        using var window = new AdminMaintenance(new SystemClock(new ManualTime(DateTimeOffset.UnixEpoch)), NullLogger<AdminMaintenance>.Instance);
        bool heard = false;
        window.Changed += (_, _) => throw new InvalidOperationException("broken listener");
        window.Changed += (_, active) => heard = !active;

        window.Begin(TimeSpan.FromMinutes(1), sessionId: null);
        window.OnSessionEnded(7);

        heard.Should().BeTrue("a window opened without a known session ends with any sign-out");
    }

    [Fact]
    public void An_agent_restart_in_the_same_boot_keeps_the_window_and_a_reboot_does_not()
    {
        using var dir = new TempDir();
        var time = new ManualTime(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        TimeSpan uptime = TimeSpan.FromHours(1);
        IOptionsMonitor<AgentSettings> settings = Settings(dir.Path);

        using (var first = new AdminMaintenance(new SystemClock(time), NullLogger<AdminMaintenance>.Instance, settings, () => uptime))
        {
            first.Begin(TimeSpan.FromMinutes(30), sessionId: 3);
        }

        time.Now += TimeSpan.FromMinutes(5);
        uptime += TimeSpan.FromMinutes(5);
        using (var restarted = new AdminMaintenance(new SystemClock(time), NullLogger<AdminMaintenance>.Instance, settings, () => uptime))
        {
            restarted.IsActive.Should().BeTrue("the Agent restarted in the same boot (an installer, an update)");
            restarted.Until.Should().Be(time.Now + TimeSpan.FromMinutes(25));
            restarted.Session.Should().Be(3u);
        }

        uptime = TimeSpan.FromMinutes(2); // rebooted
        using var rebooted = new AdminMaintenance(new SystemClock(time), NullLogger<AdminMaintenance>.Instance, settings, () => uptime);
        rebooted.IsActive.Should().BeFalse("a reboot always comes back sealed");
        File.Exists(Path.Combine(dir.Path, "secure", AdminMaintenance.StateFileName)).Should().BeFalse();
    }

    [Fact]
    public void An_ended_or_expired_window_is_not_restored()
    {
        using var dir = new TempDir();
        var time = new ManualTime(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        IOptionsMonitor<AgentSettings> settings = Settings(dir.Path);
        TimeSpan Uptime() => TimeSpan.FromHours(1) + (time.Now - new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));

        using (var window = new AdminMaintenance(new SystemClock(time), NullLogger<AdminMaintenance>.Instance, settings, Uptime))
        {
            window.Begin(TimeSpan.FromMinutes(30), sessionId: 3);
            window.End("the Shell connected again");
        }

        new AdminMaintenance(new SystemClock(time), NullLogger<AdminMaintenance>.Instance, settings, Uptime).IsActive.Should().BeFalse();

        using (var window = new AdminMaintenance(new SystemClock(time), NullLogger<AdminMaintenance>.Instance, settings, Uptime))
        {
            window.Begin(TimeSpan.FromMinutes(10), sessionId: 3);
        }

        time.Now += TimeSpan.FromMinutes(11);
        new AdminMaintenance(new SystemClock(time), NullLogger<AdminMaintenance>.Instance, settings, Uptime).IsActive.Should().BeFalse();
    }

    private static IOptionsMonitor<AgentSettings> Settings(string root)
    {
        var settings = new AgentSettings();
        settings.Paths.ProgramData = root;
        settings.Paths.Secure = Path.Combine(root, "secure");
        var options = Substitute.For<IOptionsMonitor<AgentSettings>>();
        options.CurrentValue.Returns(settings);
        return options;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("clubshell-maint-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>A clock whose time only moves when the test says so (timers still run on the system clock).</summary>
    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
