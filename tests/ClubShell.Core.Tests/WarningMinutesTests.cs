using ClubShell.Core.Configuration;

namespace ClubShell.Core.Tests;

/// <summary>Low-time warnings: every 3 minutes from 15 minutes left and the last minute; the old shipped default is upgraded.</summary>
public sealed class WarningMinutesTests
{
    [Fact]
    public void The_default_warns_every_3_minutes_from_15_and_at_the_last_minute() =>
        new AgentSettings().Session.EffectiveWarningMinutes().Should().Equal(15, 12, 9, 6, 3, 1);

    [Fact]
    public void The_old_default_every_pc_carries_in_agent_json_means_the_new_one()
    {
        var settings = new AgentSettings();
        settings.Session.WarningMinutes = [10, 5, 1];
        settings.Session.EffectiveWarningMinutes().Should().Equal(15, 12, 9, 6, 3, 1);
    }

    [Fact]
    public void A_club_s_own_marks_are_kept()
    {
        var settings = new AgentSettings();
        settings.Session.WarningMinutes = [20, 10, 2];
        settings.Session.EffectiveWarningMinutes().Should().Equal(20, 10, 2);
    }
}
