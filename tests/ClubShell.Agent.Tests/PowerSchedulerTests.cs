using ClubShell.Agent.Power;

namespace ClubShell.Agent.Tests;

/// <summary>
/// When the daily <c>power.scheduledShutdown</c> is due (<see cref="PowerScheduler.IsScheduledShutdownDue"/>). The pilot PC
/// with 05:00 turned itself off ~30 s after every boot between 05:00 and 08:00: the fired date is kept in memory only,
/// so a fresh agent saw the shutdown as still due.
/// </summary>
public sealed class PowerSchedulerTests
{
    private static readonly TimeOnly FiveAm = new(5, 0);
    private static readonly DateTime Day = new(2026, 10, 2);

    [Fact]
    public void Due_when_the_moment_passes_while_the_agent_runs()
    {
        PowerScheduler.IsScheduledShutdownDue(FiveAm, Day.AddHours(5).AddMinutes(1), runningSince: Day.AddHours(1), lastFired: null)
            .Should().BeTrue();
    }

    [Fact]
    public void Not_due_after_a_boot_past_the_scheduled_time()
    {
        PowerScheduler.IsScheduledShutdownDue(FiveAm, Day.AddHours(5).AddMinutes(31), runningSince: Day.AddHours(5).AddMinutes(30), lastFired: null)
            .Should().BeFalse();
    }

    [Fact]
    public void Not_due_before_the_moment_after_the_window_or_once_fired_today()
    {
        var since = Day.AddHours(1);
        PowerScheduler.IsScheduledShutdownDue(FiveAm, Day.AddHours(4).AddMinutes(59), since, null).Should().BeFalse();
        PowerScheduler.IsScheduledShutdownDue(FiveAm, Day.AddHours(8), since, null).Should().BeFalse();
        PowerScheduler.IsScheduledShutdownDue(FiveAm, Day.AddHours(6), since, DateOnly.FromDateTime(Day)).Should().BeFalse();
    }

    [Fact]
    public void A_session_that_deferred_it_still_fires_when_it_ends_within_the_window()
    {
        // Running since 22:00 the day before, a session kept the PC busy past 05:00; it ends at 06:30.
        PowerScheduler.IsScheduledShutdownDue(FiveAm, Day.AddHours(6).AddMinutes(30), runningSince: Day.AddHours(-2), lastFired: null)
            .Should().BeTrue();
    }
}
