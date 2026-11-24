using ClubShell.Agent.Games;
using ClubShell.Agent.Games.Accounts;
using ClubShell.Agent.Session;
using ClubShell.Contracts.Games;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Agent.Tests;

/// <summary>D-99: what keeps a player's game alive — it is ended only where that is meant, and gently.</summary>
public sealed class GameKeepAliveTests
{
    [Fact]
    public async Task Close_asks_every_game_at_once_and_reports_the_ones_that_quit_within_the_grace()
    {
        var asked = new List<int>();
        IReadOnlyList<int> exited = await GracefulGameClose.CloseAsync(
            [100, 200],
            TimeSpan.FromMilliseconds(300),
            (pid, _) =>
            {
                lock (asked)
                {
                    asked.Add(pid);
                }

                return Task.FromResult(true);
            },
            (pid, ct) => pid == 100 ? Task.CompletedTask : Task.Delay(Timeout.Infinite, ct),
            CancellationToken.None);

        asked.Should().BeEquivalentTo([100, 200]);
        exited.Should().Equal(100);
    }

    [Fact]
    public async Task Close_with_no_grace_asks_nothing_and_a_refused_request_is_not_waited_for()
    {
        bool asked = false;
        (await GracefulGameClose.CloseAsync([1], TimeSpan.Zero, (_, _) => Task.FromResult(asked = true), (_, _) => Task.CompletedTask, CancellationToken.None))
            .Should().BeEmpty();
        asked.Should().BeFalse();

        bool waited = false;
        IReadOnlyList<int> exited = await GracefulGameClose.CloseAsync(
            [1],
            TimeSpan.FromSeconds(5),
            (_, _) => Task.FromResult(false),
            (_, _) =>
            {
                waited = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);
        exited.Should().BeEmpty();
        waited.Should().BeFalse("a game that was not asked is killed at once");
    }

    [Fact]
    public void The_games_step_of_the_session_cleanup_waits_for_the_close_grace()
    {
        var settings = new AgentSettings();
        settings.Games.KillGraceSec = 30;
        var cleanup = new SessionCleanup(TestSupport.Monitor(settings), new SystemClock(TimeProvider.System), NullLogger<SessionCleanup>.Instance);

        cleanup.TimeoutOf(SessionCleanup.GamesKillStep).Should().Be(TimeSpan.FromSeconds(75));
        cleanup.TimeoutOf("clipboard.clear").Should().Be(cleanup.StepTimeout);
        settings.Games.KillGraceSec = 0;
        cleanup.TimeoutOf(SessionCleanup.GamesKillStep).Should().Be(cleanup.StepTimeout);
    }

    [Fact]
    public void A_launcher_hand_over_is_waited_for_a_minute_and_a_late_exit_a_little()
    {
        GameSessionTracker.SuccessorWaitFor(TimeSpan.FromSeconds(3)).Should().Be(TimeSpan.FromSeconds(60));
        GameSessionTracker.SuccessorWaitFor(TimeSpan.FromMinutes(9)).Should().Be(TimeSpan.FromSeconds(60), "updaters and settings restarts come early");
        GameSessionTracker.SuccessorWaitFor(TimeSpan.FromHours(2)).Should().Be(TimeSpan.FromSeconds(15), "a player who quits gets the Shell back soon");
    }

    [Fact]
    public void A_launcher_client_another_game_runs_on_is_kept()
    {
        (LauncherType, string)[] running = [(LauncherType.Steam, "Dota 2"), (LauncherType.Exe, "Minecraft")];

        AccountInjector.LauncherKeptBy(LauncherType.Steam, running).Should().Be("Dota 2");
        AccountInjector.LauncherKeptBy(LauncherType.Epic, running).Should().BeNull();
        AccountInjector.LauncherKeptBy(LauncherType.Exe, running).Should().BeNull("a plain exe has no client to kill");
        AccountInjector.LauncherKeptBy(LauncherType.Steam, []).Should().BeNull();
    }
}
