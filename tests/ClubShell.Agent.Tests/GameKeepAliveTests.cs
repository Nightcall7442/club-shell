using System.Net;
using System.Security.Cryptography;
using System.Text;
using ClubShell.Agent.AntiCheat;
using ClubShell.Agent.Games;
using ClubShell.Agent.Games.Accounts;
using ClubShell.Agent.Session;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Sessions;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;

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

    [Theory]
    [InlineData(AntiCheatChecks.ServiceStopped, 6, false)]
    [InlineData(AntiCheatChecks.DriverMissing, 6, false)]
    [InlineData(AntiCheatChecks.InjectedModule, 2, false)]
    [InlineData(AntiCheatChecks.InjectedModule, 3, true)]
    public async Task A_runtime_finding_warns_first_and_kills_only_when_critical_three_samples_in_a_row(string check, int samples, bool killed)
    {
        var monitor = AntiCheat(out IAntiCheatGameControl games, out ISessionService sessions, out ClubShell.Agent.Remote.IAdminEventSink shell, out IAntiCheatChecker checker);
        checker.CheckRuntimeAsync(42, Arg.Any<CancellationToken>()).Returns(new AntiCheatCheckResult(AntiCheatKind.Vanguard, false, check));

        for (int i = 0; i < samples; i++)
        {
            await monitor.CheckRunningOnceAsync(CancellationToken.None);
        }

        await shell.Received(1).PublishMessageAsync(Arg.Is<AdminMessage>(m => m.Text.Contains("Valorant")), Arg.Any<CancellationToken>());
        await games.Received(killed ? 1 : 0).KillAsync(42, Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await sessions.Received(killed ? 1 : 0).LockAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_good_sample_between_failures_starts_the_count_again()
    {
        var monitor = AntiCheat(out IAntiCheatGameControl games, out _, out _, out IAntiCheatChecker checker);
        var bad = new AntiCheatCheckResult(AntiCheatKind.Vanguard, false, AntiCheatChecks.InjectedModule);
        var good = new AntiCheatCheckResult(AntiCheatKind.Vanguard, true);
        checker.CheckRuntimeAsync(42, Arg.Any<CancellationToken>()).Returns(bad, bad, good, bad, bad, good);

        for (int i = 0; i < 6; i++)
        {
            await monitor.CheckRunningOnceAsync(CancellationToken.None);
        }

        await games.DidNotReceive().KillAsync(Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        AntiCheatMonitor.Decide(AntiCheatSeverity.Critical, 3, block: false).Should().Be(AntiCheatMonitor.RuntimeVerdict.None, "blockOnViolation off never kills");
    }

    private static AntiCheatMonitor AntiCheat(out IAntiCheatGameControl games, out ISessionService sessions, out ClubShell.Agent.Remote.IAdminEventSink shell, out IAntiCheatChecker checker)
    {
        Game game = TestSupport.Game("Valorant", LauncherType.Riot) with { AntiCheat = AntiCheatKind.Vanguard };
        checker = Substitute.For<IAntiCheatChecker>();
        checker.Kind.Returns(AntiCheatKind.Vanguard);
        checker.CheckAsync(Arg.Any<CancellationToken>()).Returns(new AntiCheatCheckResult(AntiCheatKind.Vanguard, true));
        games = Substitute.For<IAntiCheatGameControl>();
        games.Running.Returns([new RunningGame(game.Id, game.Title, 42, DateTimeOffset.UtcNow, null, GameState.Running)]);
        sessions = Substitute.For<ISessionService>();
        sessions.State.Returns(SessionState.Active);
        shell = Substitute.For<ClubShell.Agent.Remote.IAdminEventSink>();
        var monitor = new AntiCheatMonitor(
            [checker],
            Substitute.For<IServerClient>(),
            Substitute.For<IAgentEventSink>(),
            sessions,
            Substitute.For<IPolicyEnforcer>(),
            TestSupport.Monitor(new AgentSettings()),
            new SystemClock(TimeProvider.System),
            NullLogger<AntiCheatMonitor>.Instance,
            games,
            shell);
        monitor.CheckForLaunchAsync(game, CancellationToken.None).GetAwaiter().GetResult();
        return monitor;
    }

    [Fact]
    public async Task A_lease_is_renewed_before_it_runs_out()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        (AccountPool pool, IServerClient server, ActiveLease lease) = await PoolWithLease(time);
        await using (pool)
        {
            server.RenewAccountLeaseAsync(lease.GameId, lease.LeaseId, Arg.Any<CancellationToken>())
                .Returns(lease.Contract with { Secret = "fresh", ExpiresAt = lease.ExpiresAt.AddMinutes(30) });

            time.Advance(TimeSpan.FromMinutes(4));
            await pool.CheckLeasesAsync(CancellationToken.None);
            await server.DidNotReceive().RenewAccountLeaseAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());

            time.Advance(TimeSpan.FromMinutes(2));
            await pool.CheckLeasesAsync(CancellationToken.None);

            ActiveLease renewed = pool.Get(lease.LeaseId)!;
            renewed.ExpiresAt.Should().Be(lease.ExpiresAt.AddMinutes(30));
            renewed.RevealSecret().Should().Be("pw", "the decrypted secret stays");
        }
    }

    [Fact]
    public async Task An_expired_lease_whose_game_runs_in_an_open_session_is_kept()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        (AccountPool pool, IServerClient server, ActiveLease lease) = await PoolWithLease(time);
        await using (pool)
        {
            server.RenewAccountLeaseAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new ServerApiException(ErrorCode.NotImplemented, HttpStatusCode.NotImplemented, null, null, "no pool"));
            bool gameRuns = true;
            pool.KeepExpired = _ => gameRuns;
            int expired = 0;
            pool.LeaseExpired += (_, _) => expired++;

            time.Advance(TimeSpan.FromMinutes(11));
            await pool.CheckLeasesAsync(CancellationToken.None);
            await pool.CheckLeasesAsync(CancellationToken.None);

            pool.Get(lease.LeaseId).Should().NotBeNull();
            expired.Should().Be(0, "the game the player pays for is not closed");
            await server.Received(1).RenewAccountLeaseAsync(lease.GameId, lease.LeaseId, Arg.Any<CancellationToken>());
            await server.DidNotReceive().ReleaseAccountLeaseAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AccountLeaseRelease>(), Arg.Any<CancellationToken>());

            gameRuns = false;
            await pool.CheckLeasesAsync(CancellationToken.None);
            expired.Should().Be(1);
            pool.Get(lease.LeaseId).Should().BeNull();
        }
    }

    private static async Task<(AccountPool Pool, IServerClient Server, ActiveLease Lease)> PoolWithLease(FakeTimeProvider time)
    {
        byte[] key = Signing.DeriveAccountPoolKey(Encoding.ASCII.GetBytes("secret"));
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plain = Encoding.UTF8.GetBytes("pw");
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        var server = Substitute.For<IServerClient>();
        Game game = TestSupport.Game("Dota 2", LauncherType.Steam);
        server.LeaseAccountAsync(game.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new AccountLease(
            Guid.NewGuid(), LauncherType.Steam, "pool01", Convert.ToBase64String([.. nonce, .. cipher, .. tag]), null, time.GetUtcNow().AddMinutes(10)));
        var tokens = Substitute.For<ITokenStore>();
        tokens.Agent.Returns(new AgentTokens(Guid.NewGuid(), "access", "refresh", "c2VjcmV0", DateTimeOffset.UtcNow.AddHours(1)));
        var pool = new AccountPool(server, tokens, TestSupport.Monitor(new AgentSettings()), new SystemClock(time), NullLogger<AccountPool>.Instance);
        ActiveLease lease = await pool.LeaseAsync(game, Guid.NewGuid(), null, CancellationToken.None);
        return (pool, server, lease);
    }
}
