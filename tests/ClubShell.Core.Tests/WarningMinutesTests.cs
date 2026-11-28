using ClubShell.Core.Configuration;

namespace ClubShell.Core.Tests;

/// <summary>Low-time warnings (D-109): 10, 7 and 5 minutes left and the last minute; the old shipped defaults are upgraded.</summary>
public sealed class WarningMinutesTests
{
    [Fact]
    public void The_default_warns_at_10_7_and_5_minutes_and_at_the_last_minute() =>
        new AgentSettings().Session.EffectiveWarningMinutes().Should().Equal(10, 7, 5, 1);

    [Theory]
    [InlineData(new[] { 10, 5, 1 })]
    [InlineData(new[] { 15, 12, 9, 6, 3, 1 })]
    public void An_old_default_every_pc_carries_in_agent_json_means_the_new_one(int[] legacy)
    {
        var settings = new AgentSettings();
        settings.Session.WarningMinutes = legacy;
        settings.Session.EffectiveWarningMinutes().Should().Equal(10, 7, 5, 1);
    }

    [Fact]
    public void A_club_s_own_marks_are_kept_sorted_with_the_last_minute()
    {
        var settings = new AgentSettings();
        settings.Session.WarningMinutes = [2, 20, 10, 10];
        settings.Session.EffectiveWarningMinutes().Should().Equal(20, 10, 2, 1);
    }

    [Fact]
    public void An_empty_club_list_still_warns_at_the_last_minute()
    {
        var settings = new AgentSettings();
        settings.Session.WarningMinutes = [];
        settings.Session.EffectiveWarningMinutes().Should().Equal(1);
    }
}
