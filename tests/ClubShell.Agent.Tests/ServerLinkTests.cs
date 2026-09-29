using System.Net;
using ClubShell.Agent.Games;
using ClubShell.Agent.Server;
using ClubShell.Agent.Session;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Games;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Agent.Tests;

// ---------------------------------------------------------------------------------------------
// Agent <-> server link: live-socket token refresh, power-command acks across restarts, launch-report
// outbox, telemetry batch rejection, Riot -> Vanguard launch gate.
// ---------------------------------------------------------------------------------------------

public sealed class ServerLinkTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(T0);

    public void Dispose()
    {
        _dir.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Live_socket_token_is_refreshed_two_minutes_before_exp_then_the_socket_reconnects()
    {
        var tokens = Substitute.For<ITokenStore>();
        tokens.Agent.Returns(new AgentTokens(Guid.NewGuid(), "old", "refresh", "c2VjcmV0", T0.AddHours(1)));
        var refreshed = 0;
        var reconnected = 0;

        Task run = ServerConnection.RefreshBeforeExpiryAsync(
            tokens,
            _ =>
            {
                refreshed++;
                return Task.CompletedTask;
            },
            () => reconnected++,
            new SystemClock(_time),
            _time.GetUtcNow,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

        _time.Advance(TimeSpan.FromMinutes(57));
        run.IsCompleted.Should().BeFalse();
        refreshed.Should().Be(0);

        _time.Advance(TimeSpan.FromMinutes(1));
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        refreshed.Should().Be(1);
        reconnected.Should().Be(1);
    }

    [Fact]
    public async Task Agent_clock_two_hours_ahead_refreshes_once_per_token_lifetime()
    {
        // Local time stored as UTC: the PC clock leads the server by 2 h; exp is server time, the offset is known.
        var ahead = TimeSpan.FromHours(2);
        var agentTime = new FakeTimeProvider(T0 + ahead);
        DateTimeOffset ServerNow() => agentTime.GetUtcNow() - ahead;
        var token = new AgentTokens(Guid.NewGuid(), "t0", "refresh", "c2VjcmV0", T0.AddHours(1));
        var tokens = Substitute.For<ITokenStore>();
        tokens.Agent.Returns(_ => token);
        var refreshed = 0;
        var reconnected = 0;

        Task Start() => ServerConnection.RefreshBeforeExpiryAsync(
            tokens,
            _ =>
            {
                refreshed++;
                token = token with { AccessToken = "t" + refreshed, ExpiresAt = ServerNow().AddHours(1) };
                return Task.CompletedTask;
            },
            () => reconnected++,
            new SystemClock(agentTime),
            ServerNow,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

        // Each reconnect starts the next wait, as ConnectAndRunAsync does; 3 h of 1 h tokens.
        Task run = Start();
        for (var minute = 0; minute < 180; minute++)
        {
            agentTime.Advance(TimeSpan.FromMinutes(1));
            while (!run.IsCompleted && agentTime.ActiveTimers == 0)
            {
                await Task.Delay(1);
            }

            if (run.IsCompleted)
            {
                await run;
                run = Start();
            }
        }

        refreshed.Should().Be(3);
        reconnected.Should().Be(3);
    }

    [Fact]
    public async Task Token_that_still_looks_expired_is_not_refreshed_before_the_floor()
    {
        // Offset not learned yet: the 2 h lead makes the token look expired; refresh + reconnect must not spin.
        var tokens = Substitute.For<ITokenStore>();
        tokens.Agent.Returns(new AgentTokens(Guid.NewGuid(), "old", "refresh", "c2VjcmV0", T0.AddHours(1)));
        var refreshed = 0;

        Task run = ServerConnection.RefreshBeforeExpiryAsync(
            tokens,
            _ =>
            {
                refreshed++;
                return Task.CompletedTask;
            },
            () => { },
            new SystemClock(_time),
            () => _time.GetUtcNow().AddHours(2),
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

        _time.Advance(ServerConnection.MinRefreshDelay - TimeSpan.FromSeconds(1));
        run.IsCompleted.Should().BeFalse();
        refreshed.Should().Be(0);

        _time.Advance(TimeSpan.FromSeconds(1));
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        refreshed.Should().Be(1);
    }

    [Fact]
    public void Executed_power_command_ack_survives_a_restart_for_24_hours()
    {
        var path = Path.Combine(_dir.Root, "cache", "power-acks.json");
        var clock = new SystemClock(_time);
        var id = Guid.NewGuid();
        new PowerAckStore(path, clock, NullLogger.Instance).Save(id, CommandAck.Success(new ReloadPolicyResult(7)));

        new PowerAckStore(path, clock, NullLogger.Instance).TryGet(id, out var cached).Should().BeTrue();
        cached!.Ok.Should().BeTrue();
        cached.Result!.Value.GetProperty("version").GetInt32().Should().Be(7);

        _time.Advance(TimeSpan.FromHours(25));
        new PowerAckStore(path, clock, NullLogger.Instance).TryGet(id, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Undelivered_launch_report_is_queued_and_replayed_by_the_next_flush()
    {
        var settings = new AgentSettings();
        settings.Paths.ProgramData = _dir.Root;
        using var store = new OfflineSessionStore(TestSupport.Monitor(settings), new SystemClock(_time), NullLogger<OfflineSessionStore>.Instance);
        var server = Substitute.For<IServerClient>();
        var gameId = Guid.NewGuid();
        var report = new LaunchReport(Guid.NewGuid(), Guid.NewGuid(), LaunchResult.Success(4242, T0), 900, LauncherType.Riot, new AntiCheatCheckResult(AntiCheatKind.Vanguard, true), LaunchReportPhase.Exit, 0, 3600);
        server.SendLaunchReportAsync(gameId, Arg.Any<LaunchReport>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new ServerApiException(ErrorCode.ServerUnavailable, HttpStatusCode.ServiceUnavailable, null, null)), Task.CompletedTask);

        await store.SendLaunchReportAsync(server, gameId, report, CancellationToken.None);
        (await store.GetQueueStatsAsync(CancellationToken.None)).Pending.Should().Be(1);

        OfflineFlushResult flushed = await store.FlushAsync(server, CancellationToken.None);

        flushed.Sent.Should().Be(1);
        flushed.Remaining.Should().Be(0);
        await server.Received(2).SendLaunchReportAsync(gameId, Arg.Is<LaunchReport>(r => r.Phase == LaunchReportPhase.Exit && r.PlayedSec == 3600), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Launch_report_never_carries_the_unknown_enum_sentinel()
    {
        var settings = new AgentSettings();
        settings.Paths.ProgramData = _dir.Root;
        using var store = new OfflineSessionStore(TestSupport.Monitor(settings), new SystemClock(_time), NullLogger<OfflineSessionStore>.Instance);
        var server = Substitute.For<IServerClient>();
        var gameId = Guid.NewGuid();
        var report = new LaunchReport(Guid.NewGuid(), Guid.NewGuid(), LaunchResult.Success(4242, T0), 900, LauncherType.Steam, new AntiCheatCheckResult(AntiCheatKind.Unknown, true), LaunchReportPhase.Launch);

        await store.SendLaunchReportAsync(server, gameId, report, CancellationToken.None);
        await store.SendLaunchReportAsync(server, gameId, report with { Launcher = LauncherType.Unknown }, CancellationToken.None);

        await server.Received(1).SendLaunchReportAsync(gameId, Arg.Any<LaunchReport>(), Arg.Any<CancellationToken>());
        await server.Received(1).SendLaunchReportAsync(gameId, Arg.Is<LaunchReport>(r => r.AntiCheat.Kind == AntiCheatKind.None), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.RequestTimeout, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public void Telemetry_batch_is_dropped_only_for_a_4xx_other_than_401_408_429(HttpStatusCode status, bool dropped) =>
        TelemetryReporter.IsRejectedBatch(new ServerApiException(ErrorCodes.FromHttpStatus((int)status), status, null, null)).Should().Be(dropped);

    [Fact]
    public void Riot_game_without_an_anticheat_tag_is_gated_as_Vanguard()
    {
        GameLaunchService.EffectiveAntiCheat(TestSupport.Game("VALORANT", LauncherType.Riot)).Should().Be(AntiCheatKind.Vanguard);
        GameLaunchService.EffectiveAntiCheat(TestSupport.Game("VALORANT", LauncherType.Riot) with { AntiCheat = AntiCheatKind.Eac }).Should().Be(AntiCheatKind.Eac);
        GameLaunchService.EffectiveAntiCheat(TestSupport.Game("Dota 2", LauncherType.Steam)).Should().Be(AntiCheatKind.None);
    }
}
