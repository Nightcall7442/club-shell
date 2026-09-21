using System.Net;
using ClubShell.Agent.Session;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Wallet;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;
using PlaySession = ClubShell.Contracts.Sessions.Session;

namespace ClubShell.Agent.Tests;

// ---------------------------------------------------------------------------------------------
// SessionTimer on a fake TimeProvider (monotonic timestamps and the 1 s PeriodicTimer are both driven
// by FakeTimeProvider.Advance; advances are whole seconds so the last tick lands on the target time).
// ---------------------------------------------------------------------------------------------

public sealed class SessionTimerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(T0);
    private readonly AgentSettings _settings = new();
    private readonly SessionTimer _timer;

    public SessionTimerTests()
    {
        _settings.Session.WarningMinutes = [15, 5, 1];
        _timer = new SessionTimer(new SystemClock(_time), TestSupport.Monitor(_settings), NullLogger<SessionTimer>.Instance);
    }

    public void Dispose()
    {
        _timer.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Start_CountsDownOnMonotonicTime()
    {
        _timer.IsRunning.Should().BeFalse();
        _timer.SecondsLeft.Should().Be(0);

        _timer.Start(600, openEnded: false);

        _timer.IsRunning.Should().BeTrue();
        _timer.IsPaused.Should().BeFalse();
        _timer.IsOpenEnded.Should().BeFalse();
        _timer.SecondsLeft.Should().Be(600);
        _timer.TimeLeft.Should().Be(TimeSpan.FromMinutes(10));
        _timer.SecondsUsed.Should().Be(0);

        _time.Advance(TimeSpan.FromSeconds(10));

        _timer.SecondsLeft.Should().Be(590);
        _timer.SecondsUsed.Should().Be(10);
        _timer.IsExpired.Should().BeFalse();
    }

    [Fact]
    public void PauseAndResume_FreezeAndContinueTheCountdown()
    {
        _timer.Start(600, openEnded: false);
        _time.Advance(TimeSpan.FromSeconds(10));

        _timer.Pause();
        _timer.IsPaused.Should().BeTrue();
        _time.Advance(TimeSpan.FromSeconds(30));
        _timer.SecondsLeft.Should().Be(590, "a paused timer does not count");
        _timer.SecondsUsed.Should().Be(10);
        _timer.Pause();
        _timer.SecondsLeft.Should().Be(590, "pausing twice is a no-op");

        _timer.Resume();
        _timer.IsPaused.Should().BeFalse();
        _time.Advance(TimeSpan.FromSeconds(5));
        _timer.SecondsLeft.Should().Be(585);
        _timer.SecondsUsed.Should().Be(15);
        _timer.Resume();
        _timer.SecondsLeft.Should().Be(585, "resuming a running timer is a no-op");
    }

    [Fact]
    public void Extend_AddsBudget_AndRearmsWarningsAboveTheNewRemainingTime()
    {
        _timer.Start(120, openEnded: false);
        _timer.WarningsSent.Should().Equal(15, 5);

        _timer.Extend(600);

        _timer.SecondsLeft.Should().Be(720);
        _timer.WarningsSent.Should().Equal(15);

        _timer.Extend(300);

        _timer.SecondsLeft.Should().Be(1020);
        _timer.WarningsSent.Should().BeEmpty();
    }

    [Fact]
    public void Extend_RejectsNonPositiveSeconds()
    {
        _timer.Start(60, openEnded: false);
        Action zero = () => _timer.Extend(0);
        Action negative = () => _timer.Extend(-5);

        zero.Should().Throw<ArgumentOutOfRangeException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Stop_ResetsEverything()
    {
        _timer.Start(600, openEnded: false);
        _time.Advance(TimeSpan.FromSeconds(5));

        _timer.Stop();

        _timer.IsRunning.Should().BeFalse();
        _timer.SecondsLeft.Should().Be(0);
        _timer.SecondsUsed.Should().Be(0);
        _timer.TimeLeft.Should().Be(TimeSpan.Zero);
        _timer.WarningsSent.Should().BeEmpty();
    }

    [Fact]
    public void OpenEnded_CountsUsageOnly()
    {
        _timer.Start(0, openEnded: true);

        _timer.IsOpenEnded.Should().BeTrue();
        _timer.SecondsLeft.Should().Be(PlaySession.OpenEnded);
        _timer.TimeLeft.Should().Be(Timeout.InfiniteTimeSpan);

        _time.Advance(TimeSpan.FromSeconds(90));
        _timer.Extend(60);

        _timer.SecondsLeft.Should().Be(PlaySession.OpenEnded, "extend is meaningless for an open-ended session");
        _timer.SecondsUsed.Should().Be(90);
        _timer.IsExpired.Should().BeFalse();
    }

    [Fact]
    public void Resync_AdoptsTheServerFigureOnlyBeyondTheTolerance()
    {
        _timer.Start(600, openEnded: false);

        _timer.Resync(601, T0).Should().Be(0, "1 s drift is within tolerance");
        _timer.SecondsLeft.Should().Be(600);

        _timer.Resync(650, T0).Should().Be(50);
        _timer.SecondsLeft.Should().Be(650);

        _timer.Resync(500, T0 - TimeSpan.FromSeconds(10)).Should().Be(-160, "the server figure is aged by 10 s before comparison");
        _timer.SecondsLeft.Should().Be(490);
    }

    [Fact]
    public async Task Warnings_FireOnceEachAtTheConfiguredMarks_ThenExpiredOnce()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var warnings = new List<int>();
        int expiredCount = 0;
        using var warned = new SemaphoreSlim(0);
        using var expired = new SemaphoreSlim(0);
        using var ticked = new SemaphoreSlim(0);
        _timer.Warning += (_, mark) =>
        {
            lock (warnings)
            {
                warnings.Add(mark);
            }

            warned.Release();
        };
        _timer.Expired += (_, _) =>
        {
            Interlocked.Increment(ref expiredCount);
            expired.Release();
        };
        _timer.Tick += (_, _) => ticked.Release();

        _timer.Start(20 * 60, openEnded: false);
        await _time.WaitForTimersAsync(1, cts.Token);

        _time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1)); // 14:59 left
        await warned.WaitAsync(cts.Token);
        Snapshot(warnings).Should().Equal(15);

        _time.Advance(TimeSpan.FromMinutes(10)); // 4:59 left
        await warned.WaitAsync(cts.Token);
        Snapshot(warnings).Should().Equal(15, 5);

        _time.Advance(TimeSpan.FromMinutes(4)); // 0:59 left
        await warned.WaitAsync(cts.Token);
        Snapshot(warnings).Should().Equal(15, 5, 1);
        _timer.IsExpired.Should().BeFalse();

        _time.Advance(TimeSpan.FromMinutes(1)); // 0:00
        await expired.WaitAsync(cts.Token);

        // A few more ticks past zero: nothing fires twice.
        while (ticked.CurrentCount > 0)
        {
            await ticked.WaitAsync(cts.Token);
        }

        _time.Advance(TimeSpan.FromSeconds(3));
        await ticked.WaitAsync(cts.Token);

        Snapshot(warnings).Should().Equal(15, 5, 1);
        Volatile.Read(ref expiredCount).Should().Be(1);
        _timer.IsExpired.Should().BeTrue();
        _timer.SecondsLeft.Should().Be(0);
        _timer.SecondsUsed.Should().Be(20 * 60, "usage is capped at the purchased budget");
        _timer.WarningsSent.Should().Equal(15, 5, 1);
    }

    [Fact]
    public async Task Extend_AfterExpiry_ReArmsTheExpiredEvent()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int expiredCount = 0;
        using var expired = new SemaphoreSlim(0);
        _timer.Expired += (_, _) =>
        {
            Interlocked.Increment(ref expiredCount);
            expired.Release();
        };
        _timer.Start(30, openEnded: false);
        await _time.WaitForTimersAsync(1, cts.Token);

        _time.Advance(TimeSpan.FromSeconds(30));
        await expired.WaitAsync(cts.Token);
        _timer.IsExpired.Should().BeTrue();

        _timer.Extend(60);
        _timer.IsExpired.Should().BeFalse();
        _timer.SecondsLeft.Should().Be(60);

        _time.Advance(TimeSpan.FromSeconds(60));
        await expired.WaitAsync(cts.Token);

        Volatile.Read(ref expiredCount).Should().Be(2);
    }

    private static int[] Snapshot(List<int> values)
    {
        lock (values)
        {
            return values.ToArray();
        }
    }
}

// ---------------------------------------------------------------------------------------------
// SessionManager state machine over a substituted IServerClient and a real (temp SQLite) offline store
// ---------------------------------------------------------------------------------------------

public sealed class SessionManagerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid PcId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid TariffId = Guid.NewGuid();

    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(T0);
    private readonly IServerClient _server = Substitute.For<IServerClient>();
    private readonly ISessionEventSink _sink = Substitute.For<ISessionEventSink>();
    private readonly List<SessionEvent> _events = new();
    private readonly OfflineSessionStore _store;
    private readonly SessionTimer _timer;
    private readonly SessionManager _manager;

    public SessionManagerTests()
    {
        var settings = new AgentSettings { PcId = PcId, Zone = "vip" };
        settings.Paths.ProgramData = _dir.Root;
        // Fake-time advances fire hundreds of ticks at once; a long persist interval keeps the background SQLite writes few.
        settings.Session.PersistIntervalSec = 600;
        var monitor = TestSupport.Monitor(settings);
        var clock = new SystemClock(_time);
        _store = new OfflineSessionStore(monitor, clock, NullLogger<OfflineSessionStore>.Instance);
        _timer = new SessionTimer(clock, monitor, NullLogger<SessionTimer>.Instance);
        _manager = new SessionManager(_server, _store, _timer, clock, monitor, [_sink], NullLogger<SessionManager>.Instance);
        _manager.Changed += (_, e) =>
        {
            lock (_events)
            {
                _events.Add(e);
            }
        };
    }

    public void Dispose()
    {
        _manager.Dispose();
        _timer.Dispose();
        _store.Dispose();
        _dir.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- online -------------------------------------------------------------------------------

    [Fact]
    public async Task Start_CreatesOnTheServer_ActivatesTheTimer_AndPersists()
    {
        Guid id = Guid.NewGuid();
        _server.CreateSessionAsync(Arg.Any<SessionCreateRequest>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(ServerSession(id));

        PlaySession session = await _manager.StartAsync(Request(), CancellationToken.None);

        session.Id.Should().Be(id);
        session.State.Should().Be(SessionState.Active);
        session.SecondsLeft.Should().Be(3600);
        session.SecondsUsed.Should().Be(0);
        session.EndsAt.Should().Be(T0 + TimeSpan.FromHours(1));
        _manager.State.Should().Be(SessionState.Active);
        _manager.Current!.Id.Should().Be(id);
        _manager.TimeLeft.Should().Be(TimeSpan.FromHours(1));
        _manager.IsOfflineSession.Should().BeFalse();
        _timer.IsRunning.Should().BeTrue();
        Events().Should().ContainSingle().Which.Type.Should().Be(SessionEventType.Started);
        await _sink.Received(1).PublishAsync(Arg.Is<SessionEvent>(e => e.SessionId == id && e.Type == SessionEventType.Started), Arg.Any<CancellationToken>());
        await _sink.Received().PublishSessionUpdatedAsync(Arg.Is<PlaySession>(s => s.Id == id && s.State == SessionState.Active), Arg.Any<CancellationToken>());
        await _server.Received(1).CreateSessionAsync(Arg.Is<SessionCreateRequest>(r => r.PcId == PcId && r.UserId == UserId && r.Minutes == 60), Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        StoredSession? stored = await _store.LoadCurrentAsync(CancellationToken.None);
        stored!.Session.Id.Should().Be(id);
        stored.PendingCreate.Should().BeNull();
        (await _store.GetQueueStatsAsync(CancellationToken.None)).Pending.Should().Be(0, "online events are not queued");
    }

    [Fact]
    public async Task Start_WhileASessionIsOpen_IsSessionAlreadyActive()
    {
        Guid id = Guid.NewGuid();
        _server.CreateSessionAsync(Arg.Any<SessionCreateRequest>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(ServerSession(id));
        await _manager.StartAsync(Request(), CancellationToken.None);

        Func<Task> again = () => _manager.StartAsync(Request(), CancellationToken.None);

        (await again.Should().ThrowAsync<IpcException>()).Which.Code.Should().Be(ErrorCode.SessionAlreadyActive);
        _manager.Current!.Id.Should().Be(id);
        await _server.Received(1).CreateSessionAsync(Arg.Any<SessionCreateRequest>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PauseAndResume_FreezeTheCountdown_AndConfirmWithTheServer()
    {
        Guid id = Guid.NewGuid();
        _server.CreateSessionAsync(Arg.Any<SessionCreateRequest>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(ServerSession(id));
        _server.PauseSessionAsync(id, Arg.Any<CancellationToken>()).Returns(ServerSession(id, state: SessionState.Paused));
        _server.ResumeSessionAsync(id, Arg.Any<CancellationToken>()).Returns(ServerSession(id));
        await _manager.StartAsync(Request(), CancellationToken.None);

        PlaySession paused = await _manager.PauseAsync("break", CancellationToken.None);

        paused.State.Should().Be(SessionState.Paused);
        paused.PausedAt.Should().Be(T0);
        _manager.State.Should().Be(SessionState.Paused);
        _timer.IsPaused.Should().BeTrue();
        _time.Advance(TimeSpan.FromSeconds(30));
        _manager.Current!.SecondsLeft.Should().Be(3600, "a paused session does not consume time");

        Func<Task> pauseAgain = () => _manager.PauseAsync(null, CancellationToken.None);
        (await pauseAgain.Should().ThrowAsync<IpcException>()).Which.Code.Should().Be(ErrorCode.Conflict);

        PlaySession resumed = await _manager.ResumeAsync(CancellationToken.None);

        resumed.State.Should().Be(SessionState.Active);
        resumed.PausedAt.Should().BeNull();
        _timer.IsPaused.Should().BeFalse();
        _time.Advance(TimeSpan.FromSeconds(10));
        _manager.Current!.SecondsLeft.Should().Be(3590);
        _manager.Current!.SecondsUsed.Should().Be(10);
        Events().Select(e => e.Type).Should().Equal(SessionEventType.Started, SessionEventType.Paused, SessionEventType.Resumed);
        await _server.Received(1).PauseSessionAsync(id, Arg.Any<CancellationToken>());
        await _server.Received(1).ResumeSessionAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task End_SettlesWithTheServer_AndReturnsToIdle()
    {
        Guid id = Guid.NewGuid();
        _server.CreateSessionAsync(Arg.Any<SessionCreateRequest>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(ServerSession(id));
        _server.EndSessionAsync(id, Arg.Any<SessionEndReport>(), Arg.Any<CancellationToken>())
            .Returns(new SessionEndResult(ServerSession(id, state: SessionState.Ended) with { SecondsUsed = 600 }, Money.Uzs(10_000), Money.Zero));
        await _manager.StartAsync(Request(), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(600));

        SessionEndResult result = await _manager.EndAsync(SessionEndReason.User, CancellationToken.None);

        result.Charged.Should().Be(Money.Uzs(10_000));
        result.Refunded.Should().Be(Money.Zero);
        result.Session.State.Should().Be(SessionState.Ended);
        result.Session.SecondsUsed.Should().Be(600);
        _manager.State.Should().Be(SessionState.Idle);
        _manager.Current.Should().BeNull();
        _manager.TimeLeft.Should().Be(TimeSpan.Zero);
        _timer.IsRunning.Should().BeFalse();
        Events().Last().Type.Should().Be(SessionEventType.Ended);
        await _server.Received(1).EndSessionAsync(id, Arg.Is<SessionEndReport>(r => r.Reason == SessionEndReason.User && r.SecondsUsed == 600), Arg.Any<CancellationToken>());
        await _sink.Received(1).PublishSessionEndedAsync(Arg.Is<SessionEndedEvent>(e => e.Session.Id == id && e.Reason == SessionEndReason.User && e.Charged == Money.Uzs(10_000)), Arg.Any<CancellationToken>());
        (await _store.LoadCurrentAsync(CancellationToken.None)).Should().BeNull("the persisted session is cleared");

        Func<Task> endAgain = () => _manager.EndAsync(SessionEndReason.User, CancellationToken.None);
        (await endAgain.Should().ThrowAsync<IpcException>()).Which.Code.Should().Be(ErrorCode.SessionNotActive);
    }

    [Fact]
    public async Task Extend_AddsMinutes_AndAdoptsTheServerFigures()
    {
        Guid id = Guid.NewGuid();
        _server.CreateSessionAsync(Arg.Any<SessionCreateRequest>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(ServerSession(id));
        _server.ExtendSessionAsync(id, Arg.Is<SessionExtendRequest>(r => r.Minutes == 30 && r.TariffId == null), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ServerSession(id, secondsLeft: 5400, cost: 15_000));
        await _manager.StartAsync(Request(), CancellationToken.None);

        PlaySession extended = await _manager.ExtendAsync(30, null, CancellationToken.None);

        extended.SecondsLeft.Should().Be(5400);
        extended.Cost.Should().Be(Money.Uzs(15_000));
        extended.EndsAt.Should().Be(T0 + TimeSpan.FromMinutes(90));
        _manager.TimeLeft.Should().Be(TimeSpan.FromMinutes(90));
        SessionEvent last = Events().Last();
        last.Type.Should().Be(SessionEventType.Extended);
        SessionExtendedData data = last.DataAs<SessionExtendedData>()!;
        data.Minutes.Should().Be(30);
        data.Cost.Should().Be(Money.Uzs(5_000), "the delta between the server cost before and after");

        Func<Task> zero = () => _manager.ExtendAsync(0, null, CancellationToken.None);
        (await zero.Should().ThrowAsync<IpcException>()).Which.Code.Should().Be(ErrorCode.Validation);
    }

    // ---- offline ------------------------------------------------------------------------------

    [Fact]
    public async Task Start_WhenTheServerIsUnavailable_FallsBackToAnOfflineSession_AndQueuesEvents()
    {
        _server.CreateSessionAsync(Arg.Any<SessionCreateRequest>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(Unavailable());
        _server.GetTariffsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new EtagResponse<TariffsResponse>(new TariffsResponse([StandardTariff()], T0), "W/\"tariffs-1\"", false));

        PlaySession session = await _manager.StartAsync(Request(), CancellationToken.None);

        session.State.Should().Be(SessionState.Active);
        session.PcId.Should().Be(PcId);
        session.UserId.Should().Be(UserId);
        session.SecondsLeft.Should().Be(3600);
        session.IsPrepaid.Should().BeTrue();
        session.Cost.Should().Be(Money.Uzs(6_000), "60 minutes at 6 000 UZS/h from the cached tariff");
        _manager.IsOfflineSession.Should().BeTrue();
        _manager.State.Should().Be(SessionState.Active);

        StoredSession stored = (await _store.LoadCurrentAsync(CancellationToken.None))!;
        stored.Session.Id.Should().Be(session.Id);
        stored.PendingCreate.Should().NotBeNull("the create call is still owed to the server");
        stored.PendingCreate!.Request.ClientSessionId.Should().Be(session.Id);
        stored.PendingCreate.Request.Minutes.Should().Be(60);
        (await _store.GetQueueStatsAsync(CancellationToken.None)).Pending.Should().Be(1, "session.started is queued for replay");

        await _manager.PauseAsync("offline break", CancellationToken.None);

        _manager.State.Should().Be(SessionState.Paused);
        await _server.DidNotReceive().PauseSessionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        (await _store.GetQueueStatsAsync(CancellationToken.None)).Pending.Should().Be(2);
    }

    [Fact]
    public async Task Start_Offline_IsRefusedWhenOfflineSessionsAreDisabled()
    {
        var settings = new AgentSettings { PcId = PcId };
        settings.Paths.ProgramData = _dir.Root;
        settings.Session.PersistIntervalSec = 600;
        settings.Offline.AllowNewSessions = false;
        var clock = new SystemClock(_time);
        using var timer = new SessionTimer(clock, TestSupport.Monitor(settings), NullLogger<SessionTimer>.Instance);
        using var manager = new SessionManager(_server, _store, timer, clock, TestSupport.Monitor(settings), [], NullLogger<SessionManager>.Instance);
        _server.CreateSessionAsync(Arg.Any<SessionCreateRequest>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(Unavailable());

        Func<Task> start = () => manager.StartAsync(Request(), CancellationToken.None);

        (await start.Should().ThrowAsync<IpcException>()).Which.Code.Should().Be(ErrorCode.AgentOffline);
        manager.State.Should().Be(SessionState.Idle);
    }

    [Fact]
    public async Task Flush_PostsTheQueuedEventsAsOneBatch_WhenTheServerIsBack()
    {
        _server.CreateSessionAsync(Arg.Any<SessionCreateRequest>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(Unavailable());
        _server.GetTariffsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new EtagResponse<TariffsResponse>(new TariffsResponse([StandardTariff()], T0), null, false));
        PlaySession session = await _manager.StartAsync(Request(), CancellationToken.None);
        await _manager.PauseAsync(null, CancellationToken.None);
        await _manager.ResumeAsync(CancellationToken.None);

        OfflineFlushResult flush = await _store.FlushAsync(_server, CancellationToken.None);

        flush.Sent.Should().Be(3);
        flush.Remaining.Should().Be(0);
        flush.DeadLettered.Should().Be(0);
        await _server.Received(1).PostSessionEventsAsync(
            session.Id,
            Arg.Is<SessionEventsBatch>(b => b.Events.Count == 3
                && b.Events[0].Type == SessionEventType.Started
                && b.Events[1].Type == SessionEventType.Paused
                && b.Events[2].Type == SessionEventType.Resumed
                && b.Events[0].SessionId == session.Id),
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());
        (await _store.GetQueueStatsAsync(CancellationToken.None)).Pending.Should().Be(0);
    }

    [Fact]
    public async Task Flush_StopsAndBacksOff_WhileTheServerIsStillDown()
    {
        _server.CreateSessionAsync(Arg.Any<SessionCreateRequest>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(Unavailable());
        _server.GetTariffsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new EtagResponse<TariffsResponse>(new TariffsResponse([StandardTariff()], T0), null, false));
        _server.PostSessionEventsAsync(Arg.Any<Guid>(), Arg.Any<SessionEventsBatch>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(Unavailable());
        await _manager.StartAsync(Request(), CancellationToken.None);

        OfflineFlushResult flush = await _store.FlushAsync(_server, CancellationToken.None);

        flush.Sent.Should().Be(0);
        flush.Remaining.Should().Be(1, "the event stays queued");
        _store.FlushBackoffRemaining.Should().BePositive();
    }

    [Fact]
    public async Task Sync_ReplaysTheOfflineCreate_AdoptsTheServerId_AndFlushesUnderIt()
    {
        _server.CreateSessionAsync(Arg.Any<SessionCreateRequest>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(Unavailable());
        _server.GetTariffsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new EtagResponse<TariffsResponse>(new TariffsResponse([StandardTariff()], T0), null, false));
        PlaySession local = await _manager.StartAsync(Request(), CancellationToken.None);
        Guid serverId = Guid.NewGuid();
        _server.CreateSessionAsync(Arg.Any<SessionCreateRequest>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(ServerSession(serverId, cost: 6_000));
        _server.GetCurrentSessionAsync(PcId, Arg.Any<CancellationToken>()).Returns(ServerSession(serverId, cost: 6_000));

        await _manager.SyncWithServerAsync(CancellationToken.None);

        _manager.IsOfflineSession.Should().BeFalse();
        _manager.Current!.Id.Should().Be(serverId);
        _manager.State.Should().Be(SessionState.Active);
        await _server.Received(1).CreateSessionAsync(Arg.Is<SessionCreateRequest>(r => r.ClientSessionId == local.Id), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _server.Received(1).PostSessionEventsAsync(
            serverId,
            Arg.Is<SessionEventsBatch>(b => b.Events.Count == 1 && b.Events[0].Type == SessionEventType.Started && b.Events[0].SessionId == serverId),
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());
        StoredSession stored = (await _store.LoadCurrentAsync(CancellationToken.None))!;
        stored.Session.Id.Should().Be(serverId);
        stored.PendingCreate.Should().BeNull();
    }

    // ---- helpers --------------------------------------------------------------------------------

    private List<SessionEvent> Events()
    {
        lock (_events)
        {
            return new List<SessionEvent>(_events);
        }
    }

    private static SessionCreateRequest Request() => new(PcId, UserId, TariffId, 60, Prepaid: true);

    private static PlaySession ServerSession(Guid id, int secondsLeft = 3600, SessionState state = SessionState.Active, long cost = 10_000) =>
        new(id, UserId, PcId, state, T0, T0 + TimeSpan.FromSeconds(secondsLeft), state == SessionState.Paused ? T0 : null, TariffId, secondsLeft, 0, Money.Uzs(cost), true, []);

    private static Tariff StandardTariff() => new(TariffId, "Standard", Money.Uzs(6_000), 30, null, [], [], false);

    private static ServerApiException Unavailable() =>
        new(ErrorCode.ServerUnavailable, HttpStatusCode.ServiceUnavailable, null, null, "connection refused");
}
