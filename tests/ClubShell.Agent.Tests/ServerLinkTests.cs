using System.Net;
using ClubShell.Agent.Games;
using ClubShell.Agent.Server;
using ClubShell.Agent.Session;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Agent.Tests;

// ---------------------------------------------------------------------------------------------
// Agent <-> server link: live-socket token refresh, power-command acks across restarts, launch-report
// outbox, telemetry batch rejection, hardware inventory at start, Riot -> Vanguard launch gate,
// anti-cheat games refused on a network path.
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

    // ---- network-path anti-cheat gate ---------------------------------------------------------

    [Theory]
    [InlineData(@"\\nas\games\Apex Legends", null)]
    [InlineData(@"//nas/games/Apex Legends", null)]
    [InlineData(@"\\?\UNC\nas\games\Apex Legends", null)]
    [InlineData(@"G:\Apex Legends", null)]
    [InlineData(null, @"\\nas\games\Apex Legends\r5apex.exe")]
    [InlineData(@"D:\Games\Apex Legends", @"G:\Apex Legends\r5apex.exe")]
    public void Anticheat_game_on_a_network_path_is_refused(string? installPath, string? exePath)
    {
        Game game = TestSupport.Game("Apex Legends", LauncherType.Ea, exePath: exePath, installPath: installPath) with { AntiCheat = AntiCheatKind.Eac };

        GameLaunchService.NetworkPathCheck(game, Drives).Should().Be(new AntiCheatCheckResult(AntiCheatKind.Eac, false, "networkPath"));
    }

    [Theory]
    [InlineData(@"D:\Games\Apex Legends", null)]
    [InlineData(@"D:\Games\Apex Legends", "r5apex.exe")]
    [InlineData(@"\\?\D:\Games\Apex Legends", null)]
    [InlineData(null, @"D:\Games\Apex Legends\r5apex.exe")]
    [InlineData(null, "r5apex.exe")]
    [InlineData(null, null)]
    public void Anticheat_game_on_a_local_disk_or_with_no_known_path_is_allowed(string? installPath, string? exePath)
    {
        Game game = TestSupport.Game("Apex Legends", LauncherType.Ea, exePath: exePath, installPath: installPath) with { AntiCheat = AntiCheatKind.Eac };

        GameLaunchService.NetworkPathCheck(game, Drives).Should().BeNull();
    }

    [Fact]
    public void Anticheat_game_on_the_system_drive_is_allowed_by_the_real_drive_lookup()
    {
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
        Game game = TestSupport.Game("Apex Legends", LauncherType.Ea, installPath: Path.Combine(systemRoot, "Games", "Apex Legends")) with { AntiCheat = AntiCheatKind.Eac };

        GameLaunchService.NetworkPathCheck(game).Should().BeNull();
        GameLaunchService.NetworkPathCheck(game with { InstallPath = @"\\nas\games\Apex Legends" }).Should().NotBeNull();
    }

    [Fact]
    public void Game_without_an_anticheat_on_a_network_path_is_allowed()
    {
        Game game = TestSupport.Game("Dota 2", LauncherType.Steam, installPath: @"\\nas\games\steamapps\common\dota 2 beta");

        GameLaunchService.NetworkPathCheck(game, Drives).Should().BeNull();
        GameLaunchService.NetworkPathCheck(game with { InstallPath = @"G:\steamapps\common\dota 2 beta" }, Drives).Should().BeNull();
    }

    [Fact]
    public void Riot_game_without_an_anticheat_tag_on_a_network_path_is_refused_as_Vanguard()
    {
        Game game = TestSupport.Game("VALORANT", LauncherType.Riot, installPath: @"\\nas\games\Riot Games\VALORANT");

        AntiCheatCheckResult? failed = GameLaunchService.NetworkPathCheck(game, Drives);

        failed.Should().Be(new AntiCheatCheckResult(AntiCheatKind.Vanguard, false, GameLaunchService.NetworkPathReason));
        JsonDefaults.Serialize(IpcError.AntiCheatBlocked(failed!.Kind, failed.Reason!)).Should().Be(
            "{\"code\":\"antiCheatBlocked\",\"message\":\"Anti-cheat check failed: networkPath\",\"details\":{\"kind\":\"vanguard\",\"reason\":\"networkPath\"}}");
    }

    /// <summary>Drive table for the network-path gate: <c>G:</c> is a mapped share, every other letter a local disk.</summary>
    private static DriveType Drives(string root) => root == @"G:\" ? DriveType.Network : DriveType.Fixed;

    // ---- hardware inventory in telemetry ------------------------------------------------------

    [Fact]
    public void Inventory_taken_at_start_goes_with_the_first_batch_and_an_unchanged_rescan_does_not_send_it_again()
    {
        HardwareInfo atStart = Hardware(ramMb: 16384);
        var inventory = new TelemetryHardware(baseline: null);

        inventory.Started(atStart);
        inventory.Pending.Should().BeSameAs(atStart, "the first batch after a start carries the full inventory");
        inventory.Delivered();

        // The first rescan (telemetry.hardwareRescanSec later) finds the same hardware, free space aside.
        HardwareInfo rescan = Hardware(ramMb: 16384, freeGb: 120);
        inventory.Rescanned(rescan).Should().BeEmpty();
        inventory.Pending.Should().BeNull("the server already has this inventory");

        HardwareInfo upgraded = Hardware(ramMb: 32768);
        inventory.Rescanned(upgraded).Should().Equal("ramMb");
        inventory.Pending.Should().BeSameAs(upgraded);
    }

    [Fact]
    public void Inventory_taken_at_start_is_sent_even_when_an_earlier_scan_set_the_baseline()
    {
        HardwareInfo scanned = Hardware(ramMb: 16384);
        var inventory = new TelemetryHardware(baseline: scanned);

        inventory.Started(scanned);

        inventory.Pending.Should().BeSameAs(scanned);
    }

    [Fact]
    public void Inventory_stays_pending_until_a_batch_delivers_it()
    {
        var inventory = new TelemetryHardware();
        HardwareInfo atStart = Hardware(ramMb: 16384);

        inventory.Started(atStart);
        inventory.Rescanned(Hardware(ramMb: 16384)).Should().BeEmpty();

        inventory.Pending.Should().BeSameAs(atStart, "an upload failed meanwhile: the start inventory still has to go");
        inventory.Delivered();
        inventory.Pending.Should().BeNull();
    }

    [Fact]
    public void Without_an_inventory_at_start_the_first_rescan_sends_it_in_full()
    {
        var inventory = new TelemetryHardware();
        HardwareInfo first = Hardware(ramMb: 16384);

        inventory.Rescanned(first).Should().BeEmpty("there is nothing to compare with");

        inventory.Pending.Should().BeSameAs(first);
        inventory.Baseline.Should().BeSameAs(first);
    }

    private static HardwareInfo Hardware(int ramMb, double freeGb = 200) => new(
        new CpuInfo("AMD Ryzen 5 5600", 6, 12),
        [new GpuInfo("NVIDIA GeForce RTX 3060", 12288, "560.94")],
        ramMb,
        [new DiskInfo(@"C:\", 476.9, freeGb, DiskType.Nvme)],
        [new MonitorInfo(0, 1920, 1080, 165, true)],
        new NetworkInfo("00-11-22-33-44-55", "192.168.1.21", "Ethernet"),
        new OsInfo("Windows 11 Pro", "26100.2033"),
        []);
}
