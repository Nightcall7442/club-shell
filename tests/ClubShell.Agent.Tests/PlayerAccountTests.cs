using System.Net;
using System.Text.Json;
using ClubShell.Agent.Ipc;
using ClubShell.Agent.Ipc.Handlers;
using ClubShell.Agent.Policy;
using ClubShell.Agent.Server;
using ClubShell.Agent.Session;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Users;
using ClubShell.Contracts.Wallet;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute.ExceptionExtensions;
using PlaySession = ClubShell.Contracts.Sessions.Session;

namespace ClubShell.Agent.Tests;

/// <summary>
/// Player accounts on a PC through the real IPC handlers, session manager, lock, offline store and token store, with a
/// substituted server client: sign-out (G1), the sign-in across an Agent restart (G2), a refused or revoked sign-in (G3,
/// G10), the Shell's limits on session requests (G4, G6, G7, G15), the staff lock (G5), the wallet push (G9), the
/// connectivity a Shell is told (G11), the PIN (G12) and the server's quote (G13).
/// </summary>
public sealed class PlayerAccountTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid PcId = Guid.NewGuid();
    private static readonly Guid TariffId = Guid.NewGuid();

    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(T0);
    private readonly SystemClock _clock;
    private readonly AgentSettings _settings;
    private readonly IOptionsMonitor<AgentSettings> _monitor;
    private readonly IServerClient _server = Substitute.For<IServerClient>();
    private readonly IIpcEventPublisher _publisher = Substitute.For<IIpcEventPublisher>();
    private readonly ISessionEventSink _sink = Substitute.For<ISessionEventSink>();
    private readonly ShellUserContext _users = new();
    private readonly FakeLink _link = new();
    private readonly Hwid _hwid;
    private readonly TokenStore _tokens;
    private readonly OfflineSessionStore _store;
    private readonly SessionTimer _timer;
    private readonly SessionManager _sessions;
    private readonly SessionLock _lock;
    private readonly SignInGuard _guard;
    private readonly ShellSettingsStore _shellSettings;
    private readonly MessageDispatcher _dispatcher;
    private readonly User _member;
    private readonly User _guest;

    public PlayerAccountTests()
    {
        _clock = new SystemClock(_time);
        _settings = new AgentSettings { PcId = PcId, Zone = "vip" };
        _settings.Paths.ProgramData = _dir.Root;
        _settings.Session.PersistIntervalSec = 600;
        _monitor = TestSupport.Monitor(_settings);
        _hwid = new Hwid(Substitute.For<IHardwareIdSource>(), Path.Combine(_dir.Root, "hwid.fallback"));
        _tokens = new TokenStore(Path.Combine(_dir.Root, "agent.tokens"), new NullTokenProtector(), _clock);
        _store = new OfflineSessionStore(_monitor, _clock, NullLogger<OfflineSessionStore>.Instance, _tokens);
        _timer = new SessionTimer(_clock, _monitor, NullLogger<SessionTimer>.Instance);
        _sessions = new SessionManager(_server, _store, _timer, _clock, _monitor, [_sink], NullLogger<SessionManager>.Instance);
        _lock = NewLock();
        _guard = new SignInGuard(_users, _sessions, _lock, _tokens, _publisher, _server, _clock, NullLogger<SignInGuard>.Instance);
        _shellSettings = new ShellSettingsStore(_monitor, NullLogger<ShellSettingsStore>.Instance);
        IKioskCredentials kiosk = Substitute.For<IKioskCredentials>();
        kiosk.UserName.Returns("club");
        var handlers = new SessionHandlers(
            _sessions, _lock, _store, _server, _hwid, _users, new ShellTokenStore(_monitor, NullLogger<ShellTokenStore>.Instance), _link,
            Substitute.For<IPolicyEnforcer>(), kiosk, _shellSettings, Substitute.For<IServiceProvider>(), _monitor, _clock, NullLogger<SessionHandlers>.Instance, _tokens);
        var domain = new UserDomainHandlers(_server, _users, _sessions, _link, _lock, _shellSettings, _monitor, _clock, NullLogger<UserDomainHandlers>.Instance);
        _dispatcher = new MessageDispatcher([handlers, domain], _clock, NullLogger<MessageDispatcher>.Instance);
        _member = new User(Guid.NewGuid(), "an", "Anna", null, UserRole.Member, Money.Uzs(50_000), 0, 0, T0.AddDays(-10), T0, Locale.Ru, []);
        _guest = _member with { Id = Guid.NewGuid(), Username = "guest-12", DisplayName = "Guest 12", Role = UserRole.Guest };
        _server.ServerNow.Returns(T0);
        _server.GetTariffsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new EtagResponse<TariffsResponse>(new TariffsResponse([new Tariff(TariffId, "Standard", Money.Uzs(6_000), 30, null, [], [], false)], T0), null, false));
    }

    public void Dispose()
    {
        _guard.Dispose();
        _lock.Dispose();
        _sessions.Dispose();
        _timer.Dispose();
        _store.Dispose();
        _tokens.Dispose();
        _dir.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- G1: sign-out ---------------------------------------------------------------------------

    [Fact]
    public async Task Logout_finishes_when_the_pipe_closes_under_it()
    {
        PlaySession session = await SignInWithSessionAsync(_member);
        _server.EndSessionAsync(session.Id, Arg.Any<SessionEndReport>(), Arg.Any<CancellationToken>())
            .Returns(new SessionEndResult(session with { State = SessionState.Ended }, Money.Uzs(0), Money.Uzs(1_000)));
        // A cancelled request token is what the server client sees when the pipe closes: the call fails then.
        _server.LogoutAsync(Arg.Any<LogoutRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<CancellationToken>().IsCancellationRequested ? Task.FromCanceled(ci.Arg<CancellationToken>()) : Task.CompletedTask);
        using var pipe = new CancellationTokenSource();
        // The profile reset after the session stops the Shell: its pipe, and this request's token, go away.
        _sessions.Changed += (_, e) =>
        {
            if (e.Type == SessionEventType.Ended)
            {
                pipe.Cancel();
            }
        };

        IpcEnvelope reply = await SendAsync(IpcMessages.Auth.Logout, new AuthLogoutRequest(SessionEndReason.User), pipe.Token);

        reply.IsError.Should().BeFalse(reply.Error?.Message);
        reply.PayloadAs<AuthLogoutResponse>()!.SessionEnded.Should().BeTrue();
        _users.User.Should().BeNull("the player is signed out although the pipe closed half-way");
        _users.IsSigningOut.Should().BeFalse();
        _sessions.State.Should().Be(SessionState.Idle);
        await _server.Received(1).LogoutAsync(Arg.Is<LogoutRequest>(r => r.Reason == SessionEndReason.User), Arg.Any<CancellationToken>());
        (await _store.GetQueueStatsAsync(CancellationToken.None)).Pending.Should().Be(0, "the server sign-out went through: nothing is queued");
    }

    [Fact]
    public async Task Logout_with_the_server_unreachable_signs_out_locally_and_queues_the_end_and_the_sign_out()
    {
        PlaySession session = await SignInWithSessionAsync(_member);
        _server.EndSessionAsync(Arg.Any<Guid>(), Arg.Any<SessionEndReport>(), Arg.Any<CancellationToken>()).ThrowsAsync(Unavailable());
        _server.LogoutAsync(Arg.Any<LogoutRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(Unavailable());

        IpcEnvelope reply = await SendAsync(IpcMessages.Auth.Logout, new AuthLogoutRequest());

        reply.IsError.Should().BeFalse(reply.Error?.Message);
        _users.User.Should().BeNull();
        _sessions.State.Should().Be(SessionState.Idle);
        (await _store.GetQueueStatsAsync(CancellationToken.None)).Pending.Should().Be(2, "the session's end and the sign-out wait in the outbox");
        _tokens.User!.UserId.Should().Be(_member.Id, "the token is kept to replay the sign-out with");

        // The server is back: the end goes first, then the sign-out with the kept token.
        _server.ClearReceivedCalls();
        _server.LogoutAsync(Arg.Any<LogoutRequest>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        OfflineFlushResult flush = await _store.FlushAsync(_server, CancellationToken.None);

        flush.Remaining.Should().Be(0);
        Received.InOrder(() =>
        {
            _server.PostSessionEventsAsync(session.Id, Arg.Is<SessionEventsBatch>(b => b.Events.Any(e => e.Type == SessionEventType.Ended)), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
            _server.LogoutAsync(Arg.Is<LogoutRequest>(r => r.Reason == SessionEndReason.User), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task A_new_sign_in_drops_a_queued_sign_out()
    {
        await _store.QueueLogoutAsync(_member.Id, SessionEndReason.User, CancellationToken.None);
        _link.Connectivity = ConnectivityState.Online;
        _server.LoginAsync(Arg.Any<AuthRequest>(), Arg.Any<CancellationToken>()).Returns(new AuthResponse(_member, null, "token-a2", "refresh", T0.AddHours(8)));

        IpcEnvelope reply = await SendAsync(IpcMessages.Auth.Login, new AuthLoginRequest(AuthKind.Password, "an", "secret"));

        reply.IsError.Should().BeFalse(reply.Error?.Message);
        (await _store.GetQueueStatsAsync(CancellationToken.None)).Pending.Should().Be(0, "replaying it would sign the player out of the new sign-in");
    }

    [Fact]
    public async Task Profile_reset_waits_for_a_running_sign_out()
    {
        IDisposable signingOut = _users.BeginSignOut();
        Task<bool> wait = _users.WaitForSignOutAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(50);
        wait.IsCompleted.Should().BeFalse();

        signingOut.Dispose();

        (await wait).Should().BeTrue();
        (await _users.WaitForSignOutAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None)).Should().BeTrue("nothing runs any more");
    }

    // ---- G2: sign-in across an Agent restart ----------------------------------------------------

    [Fact]
    public async Task Sign_in_is_saved_while_the_session_runs_and_restored_after_an_agent_restart()
    {
        using var saved = new SignedInUserStore(_users, _sessions, new NullTokenProtector(), _monitor, _clock, NullLogger<SignedInUserStore>.Instance);
        PlaySession session = await SignInWithSessionAsync(_member);
        File.Exists(saved.FilePath).Should().BeTrue();
        saved.Load()!.User.Id.Should().Be(_member.Id);

        // The Agent restarts: a new process restores the session (the server still has it open) and then the player.
        _server.GetCurrentSessionAsync(PcId, Arg.Any<CancellationToken>()).Returns(session);
        using var timer = new SessionTimer(_clock, _monitor, NullLogger<SessionTimer>.Instance);
        using var sessions = new SessionManager(_server, _store, timer, _clock, _monitor, [], NullLogger<SessionManager>.Instance);
        var users = new ShellUserContext();
        using var restarted = new SignedInUserStore(users, sessions, new NullTokenProtector(), _monitor, _clock, NullLogger<SignedInUserStore>.Instance);
        await sessions.RestoreAsync(CancellationToken.None);

        restarted.Restore().Should().BeTrue();

        users.User!.Id.Should().Be(_member.Id);
        users.User.DisplayName.Should().Be("Anna");
        users.Mode.Should().Be(ConnectivityState.Online);
    }

    [Fact]
    public async Task Saved_sign_in_is_deleted_at_sign_out_and_dropped_when_the_session_is_gone()
    {
        using var saved = new SignedInUserStore(_users, _sessions, new NullTokenProtector(), _monitor, _clock, NullLogger<SignedInUserStore>.Instance);
        PlaySession session = await SignInWithSessionAsync(_member);
        File.Exists(saved.FilePath).Should().BeTrue();

        _users.Clear();
        File.Exists(saved.FilePath).Should().BeFalse("signed out: nothing to restore");

        _users.Set(_member, T0.AddHours(8), ConnectivityState.Online);
        File.Exists(saved.FilePath).Should().BeTrue();
        _server.EndSessionAsync(session.Id, Arg.Any<SessionEndReport>(), Arg.Any<CancellationToken>())
            .Returns(new SessionEndResult(session with { State = SessionState.Ended }, Money.Uzs(0), Money.Uzs(0)));
        await _sessions.EndAsync(SessionEndReason.User, CancellationToken.None);
        File.Exists(saved.FilePath).Should().BeFalse("the session ended");

        // A file left behind (crash) for a session the server no longer has: dropped at restore.
        _users.Clear();
        saved.Save(new SignedInUser(_member, T0.AddHours(8), ConnectivityState.Online, session.Id, T0));
        saved.Restore().Should().BeFalse();
        File.Exists(saved.FilePath).Should().BeFalse();
        _users.User.Should().BeNull();
    }

    // ---- G3: a refused player token -------------------------------------------------------------

    [Fact]
    public async Task Refused_player_token_locks_the_session_and_signing_in_again_resumes_it()
    {
        PlaySession session = await SignInWithSessionAsync(_member);

        await _guard.HandleTokenRejectedAsync(_member.Id, CancellationToken.None);

        _sessions.State.Should().Be(SessionState.Locked, "the session is kept, behind the lock");
        _lock.Origin.Should().Be(SessionLockOrigin.SignIn);
        _users.User.Should().BeNull();
        _tokens.User.Should().BeNull("the refused token is useless");
        await _publisher.Received(1).PublishAsync(IpcMessages.Events.AuthExpired, Arg.Is<AuthExpired>(e => e.Reason == AuthExpiredReason.TokenExpired), Arg.Any<CancellationToken>());

        _server.LoginAsync(Arg.Any<AuthRequest>(), Arg.Any<CancellationToken>()).Returns(new AuthResponse(_member, session with { State = SessionState.Locked }, "token-2", "refresh-2", T0.AddHours(8)));
        IpcEnvelope login = await SendAsync(IpcMessages.Auth.Login, new AuthLoginRequest(AuthKind.Password, "an", "secret"));

        login.IsError.Should().BeFalse(login.Error?.Message);
        _sessions.State.Should().Be(SessionState.Active, "the same player signed in again: the session goes on");
        login.PayloadAs<AuthLoginResponse>()!.Session!.State.Should().Be(SessionState.Active);
    }

    [Fact]
    public async Task Another_player_signing_in_does_not_open_a_session_locked_for_its_player()
    {
        await SignInWithSessionAsync(_member);
        await _guard.HandleTokenRejectedAsync(_member.Id, CancellationToken.None);
        User other = _member with { Id = Guid.NewGuid(), Username = "bo" };

        (await _lock.ResumeAfterSignInAsync(other, CancellationToken.None)).Should().BeFalse();

        _sessions.State.Should().Be(SessionState.Locked);
    }

    [Fact]
    public async Task Refused_token_or_local_expiry_without_a_session_signs_the_player_out()
    {
        _users.Set(_member, T0.AddMinutes(1), ConnectivityState.Online);

        await _guard.ExpireIfDueAsync(CancellationToken.None);
        _users.User.Should().NotBeNull("not expired yet");

        _time.Advance(TimeSpan.FromMinutes(2));
        await _guard.ExpireIfDueAsync(CancellationToken.None);

        _users.User.Should().BeNull();
        await _publisher.Received(1).PublishAsync(IpcMessages.Events.AuthExpired, Arg.Is<AuthExpired>(e => e.Reason == AuthExpiredReason.TokenExpired), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Local_token_expiry_does_not_touch_a_running_session()
    {
        await SignInWithSessionAsync(_member, expiresAt: T0.AddMinutes(1));
        _time.Advance(TimeSpan.FromMinutes(2));

        await _guard.ExpireIfDueAsync(CancellationToken.None);

        _users.User.Should().NotBeNull("the server extends the token while the session runs; a real expiry comes back as 401");
        _sessions.State.Should().Be(SessionState.Active);
    }

    [Fact]
    public async Task UserTokenRejected_from_the_server_client_is_handled()
    {
        await SignInWithSessionAsync(_member);

        _server.UserTokenRejected += Raise.EventWith(_server, new UserTokenRejectedEventArgs(_member.Id, "expired"));

        for (int i = 0; i < 100 && _users.User is not null; i++)
        {
            await Task.Delay(20);
        }

        _users.User.Should().BeNull();
        _sessions.State.Should().Be(SessionState.Locked);
    }

    // ---- G4 / G6: what the Shell may ask of a session ---------------------------------------------

    [Theory]
    [InlineData(SessionEndReason.Admin)]
    [InlineData(SessionEndReason.TimeUp)]
    [InlineData(SessionEndReason.AgentRestart)]
    public async Task Session_end_and_sign_out_from_the_shell_accept_only_user_and_idle(SessionEndReason reason)
    {
        await SignInWithSessionAsync(_member);

        IpcEnvelope end = await SendAsync(IpcMessages.Session.End, new SessionEndRequest(reason));
        IpcEnvelope logout = await SendAsync(IpcMessages.Auth.Logout, new AuthLogoutRequest(reason));

        foreach (IpcEnvelope reply in new[] { end, logout })
        {
            reply.Error!.Code.Should().Be(ErrorCode.Forbidden);
            reply.Error.DetailsAs<ReasonDetails>()!.Reason.Should().Be("staffOnly");
        }

        _sessions.State.Should().Be(SessionState.Active);
        _users.User.Should().NotBeNull();
        await _server.DidNotReceive().EndSessionAsync(Arg.Any<Guid>(), Arg.Any<SessionEndReport>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Session_end_as_idle_from_the_shell_is_accepted()
    {
        PlaySession session = await SignInWithSessionAsync(_member);
        _server.EndSessionAsync(session.Id, Arg.Any<SessionEndReport>(), Arg.Any<CancellationToken>())
            .Returns(new SessionEndResult(session with { State = SessionState.Ended }, Money.Uzs(0), Money.Uzs(0)));

        IpcEnvelope reply = await SendAsync(IpcMessages.Session.End, new SessionEndRequest(SessionEndReason.Idle));

        reply.IsError.Should().BeFalse(reply.Error?.Message);
        await _server.Received(1).EndSessionAsync(session.Id, Arg.Is<SessionEndReport>(r => r.Reason == SessionEndReason.Idle), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pause_and_resume_from_the_shell_are_staff_only()
    {
        await SignInWithSessionAsync(_member);

        IpcEnvelope pause = await SendAsync(IpcMessages.Session.Pause, new SessionPauseRequest("coffee"));
        IpcEnvelope resume = await SendAsync(IpcMessages.Session.Resume);

        foreach (IpcEnvelope reply in new[] { pause, resume })
        {
            reply.Error!.Code.Should().Be(ErrorCode.Forbidden);
            reply.Error.DetailsAs<ReasonDetails>()!.Reason.Should().Be("staffOnly");
        }

        _sessions.State.Should().Be(SessionState.Active, "billing does not stop on the player's request");
        await _server.DidNotReceive().PauseSessionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ---- G7: offline ----------------------------------------------------------------------------

    [Fact]
    public async Task Offline_pay_as_you_go_from_the_shell_is_refused()
    {
        _users.Set(_member, T0.AddHours(8), ConnectivityState.Offline);

        IpcEnvelope reply = await SendAsync(IpcMessages.Session.Start, new SessionStartRequest(TariffId, Prepaid: false));

        reply.Error!.Code.Should().Be(ErrorCode.Conflict);
        reply.Error.DetailsAs<ReasonDetails>()!.Reason.Should().Be("offlineUnavailable");
        await _server.DidNotReceive().CreateSessionAsync(Arg.Any<SessionCreateRequest>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Offline_prepaid_start_takes_from_the_cached_balance_and_shows_it()
    {
        await _store.CacheUserAsync("an", null, _member with { Balance = Money.Uzs(10_000) }, CancellationToken.None);
        _users.Set(_member with { Balance = Money.Uzs(10_000) }, T0.AddHours(8), ConnectivityState.Offline);
        _server.CreateSessionAsync(Arg.Any<SessionCreateRequest>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(Unavailable());

        IpcEnvelope reply = await SendAsync(IpcMessages.Session.Start, new SessionStartRequest(TariffId, Prepaid: true, Minutes: 60));

        reply.IsError.Should().BeFalse(reply.Error?.Message);
        _users.User!.Balance.Should().Be(Money.Uzs(4_000), "6 000 for the hour taken from the cached 10 000");
        IpcEnvelope extend = await SendAsync(IpcMessages.Session.Extend, new SessionExtendRequest(60));
        extend.Error!.Code.Should().Be(ErrorCode.InsufficientFunds);
    }

    [Fact]
    public async Task Offline_session_requests_must_be_the_signed_in_players()
    {
        await _store.CacheUserAsync("an", null, _member, CancellationToken.None);
        _users.Set(_member, T0.AddHours(8), ConnectivityState.Offline);
        _server.CreateSessionAsync(Arg.Any<SessionCreateRequest>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(Unavailable());
        (await SendAsync(IpcMessages.Session.Start, new SessionStartRequest(TariffId, Prepaid: true, Minutes: 60))).IsError.Should().BeFalse();
        User other = _member with { Id = Guid.NewGuid(), Username = "bo" };
        _users.Clear();
        _users.Set(other, T0.AddHours(8), ConnectivityState.Offline);

        IpcEnvelope extend = await SendAsync(IpcMessages.Session.Extend, new SessionExtendRequest(30));
        IpcEnvelope end = await SendAsync(IpcMessages.Session.End, new SessionEndRequest());
        IpcEnvelope lockIt = await SendAsync(IpcMessages.Session.Lock, new SessionLockRequest());

        foreach (IpcEnvelope reply in new[] { extend, end, lockIt })
        {
            reply.Error!.Code.Should().Be(ErrorCode.Forbidden);
            reply.Error.DetailsAs<ReasonDetails>()!.Reason.Should().Be("otherUser");
        }

        _sessions.State.Should().Be(SessionState.Active);
    }

    // ---- G5: staff lock -------------------------------------------------------------------------

    [Fact]
    public async Task Staff_lock_is_not_opened_by_the_player_and_auth_status_reports_it()
    {
        PlaySession session = await SignInWithSessionAsync(_member);
        await _store.SavePinHashAsync(_member.Id, SessionLock.HashPin("1234"), CancellationToken.None);
        (await SendAsync(IpcMessages.Session.Lock, new SessionLockRequest("break"))).IsError.Should().BeFalse();
        _lock.Origin.Should().Be(SessionLockOrigin.Player);

        // The cash desk locks the PC while the player's own lock is up: it becomes a staff lock, and the Shell is told.
        await _lock.LockByStaffAsync("pay at the desk", CancellationToken.None);

        _lock.IsLockedByStaff.Should().BeTrue();
        await _sink.Received().PublishSessionUpdatedAsync(Arg.Is<PlaySession>(s => s.Id == session.Id && s.State == SessionState.Locked), Arg.Any<CancellationToken>());
        IpcEnvelope status = await SendAsync(IpcMessages.Auth.Status);
        status.PayloadAs<AuthStatusResponse>()!.LockedByStaff.Should().BeTrue();
        IpcEnvelope unlock = await SendAsync(IpcMessages.Session.Unlock, new SessionUnlockRequest(Pin: "1234"));
        unlock.Error!.Code.Should().Be(ErrorCode.Forbidden);
        unlock.Error.DetailsAs<ReasonDetails>()!.Reason.Should().Be("staffLock");
        await _server.DidNotReceive().VerifyPinAsync(Arg.Any<Guid>(), Arg.Any<PinVerifyRequest>(), Arg.Any<CancellationToken>());

        // An Agent restart keeps it a staff lock.
        using SessionLock restarted = NewLock();
        restarted.IsLockedByStaff.Should().BeTrue();

        await _lock.UnlockByStaffAsync(CancellationToken.None);

        _sessions.State.Should().Be(SessionState.Active);
        _lock.Origin.Should().BeNull();
        (await SendAsync(IpcMessages.Auth.Status)).PayloadAs<AuthStatusResponse>()!.LockedByStaff.Should().BeFalse();
    }

    // ---- G15: a guest cannot lock -----------------------------------------------------------------

    [Fact]
    public async Task Guest_cannot_lock_the_session()
    {
        await SignInWithSessionAsync(_guest);

        IpcEnvelope reply = await SendAsync(IpcMessages.Session.Lock, new SessionLockRequest());

        reply.Error!.Code.Should().Be(ErrorCode.Forbidden);
        reply.Error.DetailsAs<ReasonDetails>()!.Reason.Should().Be("guestLock");
        _sessions.State.Should().Be(SessionState.Active);
    }

    // ---- G12: PIN ---------------------------------------------------------------------------------

    [Fact]
    public async Task Pin_unlock_online_is_checked_by_the_server_and_then_works_offline_from_the_cached_hash()
    {
        await SignInWithSessionAsync(_member);
        _server.VerifyPinAsync(_member.Id, Arg.Any<PinVerifyRequest>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        (await SendAsync(IpcMessages.Session.Lock, new SessionLockRequest())).IsError.Should().BeFalse();

        IpcEnvelope online = await SendAsync(IpcMessages.Session.Unlock, new SessionUnlockRequest(Pin: "4321"));

        online.IsError.Should().BeFalse(online.Error?.Message);
        await _server.Received(1).VerifyPinAsync(_member.Id, Arg.Is<PinVerifyRequest>(r => r.Pin == "4321"), Arg.Any<CancellationToken>());
        string hash = (await _store.GetPinHashAsync(_member.Id, CancellationToken.None))!;
        hash.Should().NotContain("4321", "only a salted hash is kept");

        _link.Connectivity = ConnectivityState.Offline;
        (await SendAsync(IpcMessages.Session.Lock, new SessionLockRequest())).IsError.Should().BeFalse();
        IpcEnvelope wrong = await SendAsync(IpcMessages.Session.Unlock, new SessionUnlockRequest(Pin: "1111"));
        wrong.Error!.Code.Should().Be(ErrorCode.Unauthorized);
        wrong.Error.DetailsAs<ReasonDetails>()!.Reason.Should().Be("badSecret");

        IpcEnvelope offline = await SendAsync(IpcMessages.Session.Unlock, new SessionUnlockRequest(Pin: "4321"));

        offline.IsError.Should().BeFalse(offline.Error?.Message);
        _sessions.State.Should().Be(SessionState.Active);
        await _server.Received(1).VerifyPinAsync(Arg.Any<Guid>(), Arg.Any<PinVerifyRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pin_unlock_maps_the_servers_bad_secret_and_no_pin()
    {
        await SignInWithSessionAsync(_member);
        (await SendAsync(IpcMessages.Session.Lock, new SessionLockRequest())).IsError.Should().BeFalse();
        _server.VerifyPinAsync(_member.Id, Arg.Any<PinVerifyRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(ServerError(ErrorCode.Unauthorized, HttpStatusCode.Unauthorized, """{ "reason": "badSecret", "attemptsLeft": 3 }"""));

        IpcEnvelope wrong = await SendAsync(IpcMessages.Session.Unlock, new SessionUnlockRequest(Pin: "0000"));

        wrong.Error!.Code.Should().Be(ErrorCode.Unauthorized);
        wrong.Error.Details!.Value.GetProperty("reason").GetString().Should().Be("badSecret");
        wrong.Error.Details!.Value.GetProperty("attemptsLeft").GetInt32().Should().Be(3);

        _server.VerifyPinAsync(_member.Id, Arg.Any<PinVerifyRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(ServerError(ErrorCode.NotFound, HttpStatusCode.NotFound, """{ "reason": "noPin" }"""));
        IpcEnvelope none = await SendAsync(IpcMessages.Session.Unlock, new SessionUnlockRequest(Pin: "0000"));

        none.Error!.Code.Should().Be(ErrorCode.Unauthorized);
        none.Error.DetailsAs<ReasonDetails>()!.Reason.Should().Be("noPin");
        _sessions.State.Should().Be(SessionState.Locked);
    }

    [Fact]
    public async Task Offline_pin_unlock_without_a_cached_pin_is_no_pin()
    {
        await SignInWithSessionAsync(_member);
        (await SendAsync(IpcMessages.Session.Lock, new SessionLockRequest())).IsError.Should().BeFalse();
        _link.Connectivity = ConnectivityState.Offline;

        IpcEnvelope reply = await SendAsync(IpcMessages.Session.Unlock, new SessionUnlockRequest(Pin: "1234"));

        reply.Error!.Code.Should().Be(ErrorCode.Unauthorized);
        reply.Error.DetailsAs<ReasonDetails>()!.Reason.Should().Be("noPin");
        await _server.DidNotReceive().VerifyPinAsync(Arg.Any<Guid>(), Arg.Any<PinVerifyRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Profile_update_with_a_pin_caches_its_hash_and_auth_status_reports_pin_set()
    {
        _link.Connectivity = ConnectivityState.Online;
        _users.Set(_member, T0.AddHours(8), ConnectivityState.Online);
        (await SendAsync(IpcMessages.Auth.Status)).PayloadAs<AuthStatusResponse>()!.PinSet.Should().BeFalse();
        _server.UpdateUserAsync(_member.Id, Arg.Any<ProfileUpdateRequest>(), Arg.Any<CancellationToken>()).Returns(_member with { Flags = ["pinSet"] });

        IpcEnvelope reply = await SendAsync(IpcMessages.Profile.Update, new ProfileUpdateRequest(Pin: "2580"));

        reply.IsError.Should().BeFalse(reply.Error?.Message);
        string hash = (await _store.GetPinHashAsync(_member.Id, CancellationToken.None))!;
        OfflineSessionStore.VerifyPassword(hash, "2580").Should().BeTrue();
        (await SendAsync(IpcMessages.Auth.Status)).PayloadAs<AuthStatusResponse>()!.PinSet.Should().BeTrue();
    }

    // ---- G13: quote -------------------------------------------------------------------------------

    [Fact]
    public async Task Quote_asks_the_server_for_this_pc_and_player_and_is_unavailable_offline()
    {
        _link.Connectivity = ConnectivityState.Online;
        _users.Set(_member, T0.AddHours(8), ConnectivityState.Online);
        var quote = new SessionQuote(60, Money.Uzs(6_000), Money.Uzs(5_400), 100, 10, "loyalty", false);
        _server.QuoteSessionAsync(Arg.Any<SessionQuoteRequest>(), Arg.Any<CancellationToken>()).Returns(quote);

        IpcEnvelope reply = await SendAsync(IpcMessages.Session.Quote, new SessionQuoteIpcRequest(TariffId, 60));

        reply.PayloadAs<SessionQuote>().Should().Be(quote);
        await _server.Received(1).QuoteSessionAsync(new SessionQuoteRequest(PcId, _member.Id, TariffId, 60), Arg.Any<CancellationToken>());

        _link.Connectivity = ConnectivityState.Offline;
        IpcEnvelope offline = await SendAsync(IpcMessages.Session.Quote, new SessionQuoteIpcRequest(TariffId));

        offline.Error!.Code.Should().Be(ErrorCode.Conflict);
        offline.Error.DetailsAs<ReasonDetails>()!.Reason.Should().Be("offlineUnavailable");
    }

    // ---- sign-in: refusals, endElsewhere, guest name, short logins (G8, G16) ------------------------

    [Fact]
    public async Task Sign_in_refusal_keeps_the_servers_reason_and_details_and_endElsewhere_is_passed()
    {
        _link.Connectivity = ConnectivityState.Online;
        _server.LoginAsync(Arg.Is<AuthRequest>(r => r.EndElsewhere != true), Arg.Any<CancellationToken>())
            .ThrowsAsync(ServerError(ErrorCode.Conflict, HttpStatusCode.Conflict, $$"""{ "reason": "activeSessionElsewhere", "pcId": "{{Guid.Empty}}", "pcName": "PC-07" }"""));
        _server.LoginAsync(Arg.Is<AuthRequest>(r => r.EndElsewhere == true), Arg.Any<CancellationToken>())
            .Returns(new AuthResponse(_member, null, "token", "refresh", T0.AddHours(8)));

        IpcEnvelope refused = await SendAsync(IpcMessages.Auth.Login, new AuthLoginRequest(AuthKind.Password, "an", "secret"));

        refused.Error!.Code.Should().Be(ErrorCode.Conflict);
        refused.Error.Details!.Value.GetProperty("reason").GetString().Should().Be("activeSessionElsewhere");
        refused.Error.Details!.Value.GetProperty("pcName").GetString().Should().Be("PC-07");

        IpcEnvelope signedIn = await SendAsync(IpcMessages.Auth.Login, new AuthLoginRequest(AuthKind.Password, "an", "secret", EndElsewhere: true));

        signedIn.IsError.Should().BeFalse(signedIn.Error?.Message);
        await _server.Received().LoginAsync(Arg.Is<AuthRequest>(r => r.EndElsewhere == true && r.Username == "an"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rate_limited_sign_in_does_not_fall_back_to_the_offline_cache()
    {
        _link.Connectivity = ConnectivityState.Online;
        await _store.CacheUserAsync("an", OfflineSessionStore.HashPassword("secret", 1000), _member, CancellationToken.None);
        _server.LoginAsync(Arg.Any<AuthRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(ServerError(ErrorCode.RateLimited, HttpStatusCode.TooManyRequests, """{ "retryAfterSec": 30 }"""));

        IpcEnvelope reply = await SendAsync(IpcMessages.Auth.Login, new AuthLoginRequest(AuthKind.Password, "an", "secret"));

        reply.Error!.Code.Should().Be(ErrorCode.RateLimited);
        _users.User.Should().BeNull("the cache must not sidestep the server's limit on attempts");
    }

    [Fact]
    public async Task Guest_display_name_and_one_letter_logins_reach_the_server()
    {
        _link.Connectivity = ConnectivityState.Online;
        _server.GuestLoginAsync(Arg.Any<GuestAuthRequest>(), Arg.Any<CancellationToken>()).Returns(new AuthResponse(_guest, null, "token", "refresh", T0.AddHours(8)));
        _server.LoginAsync(Arg.Any<AuthRequest>(), Arg.Any<CancellationToken>()).Returns(new AuthResponse(_member, null, "token", "refresh", T0.AddHours(8)));

        (await SendAsync(IpcMessages.Auth.Login, new AuthLoginRequest(AuthKind.Guest, Username: " Rustam "))).IsError.Should().BeFalse();
        _users.Clear();
        IpcEnvelope shortLogin = await SendAsync(IpcMessages.Auth.Login, new AuthLoginRequest(AuthKind.Password, "a", "secret"));

        shortLogin.IsError.Should().BeFalse(shortLogin.Error?.Message);
        await _server.Received(1).GuestLoginAsync(Arg.Is<GuestAuthRequest>(r => r.DisplayName == "Rustam"), Arg.Any<CancellationToken>());
        await _server.Received(1).LoginAsync(Arg.Is<AuthRequest>(r => r.Username == "a"), Arg.Any<CancellationToken>());
    }

    // ---- G9: wallet push --------------------------------------------------------------------------

    [Fact]
    public async Task Wallet_push_updates_the_signed_in_players_balance_and_ignores_other_players()
    {
        await _store.CacheUserAsync("an", null, _member, CancellationToken.None);
        _users.Set(_member, T0.AddHours(8), ConnectivityState.Online);
        IServiceProvider services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(OfflineSessionStore)).Returns(_store);
        var bridge = new IpcEventBridge(_users, _shellSettings, services, _clock, NullLogger<IpcEventBridge>.Instance);
        IServerPushSink push = bridge;

        await push.PublishAsync(WsPushKind.WalletUpdated, JsonDefaults.ToElement(new Balance(Guid.NewGuid(), Money.Uzs(1), Money.Uzs(0), "UZS", T0)), CancellationToken.None);

        _users.User!.Balance.Should().Be(Money.Uzs(50_000), "a push for another player is not this player's balance");
        bridge.Outbox.TryRead(out _).Should().BeFalse("and it is not forwarded");

        await push.PublishAsync(WsPushKind.WalletUpdated, JsonDefaults.ToElement(new Balance(_member.Id, Money.Uzs(70_000), Money.Uzs(5_000), "UZS", T0)), CancellationToken.None);

        _users.User!.Balance.Should().Be(Money.Uzs(70_000));
        _users.LastBalance!.Bonus.Should().Be(Money.Uzs(5_000));
        (await _store.GetCachedUserAsync(_member.Id, CancellationToken.None))!.Balance.Should().Be(Money.Uzs(70_000), "an offline start checks the latest balance");
        bridge.Outbox.TryRead(out IpcEnvelope? forwarded).Should().BeTrue();
        forwarded!.Name.Should().Be(IpcMessages.Events.WalletUpdated);
    }

    // ---- G10: revocation reasons ------------------------------------------------------------------

    [Theory]
    [InlineData("sessionEnded", AuthExpiredReason.SessionEnded)]
    [InlineData("seatTaken", AuthExpiredReason.SeatTaken)]
    [InlineData("seatMoved", AuthExpiredReason.SeatMoved)]
    [InlineData("signedInElsewhere", AuthExpiredReason.SignedInElsewhere)]
    [InlineData("blacklisted", AuthExpiredReason.Blacklisted)]
    [InlineData("passwordReset", AuthExpiredReason.PasswordReset)]
    [InlineData("somethingNew", AuthExpiredReason.Revoked)]
    public void Revocation_reasons_map_to_the_shells_reasons(string wire, AuthExpiredReason expected) =>
        SignInGuard.MapRevokedReason(wire).Should().Be(expected);

    [Fact]
    public async Task Revocation_of_a_moved_seat_takes_the_servers_view_and_tells_the_shell_why()
    {
        await SignInWithSessionAsync(_member);
        _server.GetCurrentSessionAsync(PcId, Arg.Any<CancellationToken>()).Returns((PlaySession?)null);

        await _guard.HandleRevokedAsync(new UserRevokedPush(_member.Id, "seatMoved"), CancellationToken.None);

        _sessions.State.Should().Be(SessionState.Idle, "the server moved the session away; it is not settled here with a revoked token");
        await _server.DidNotReceive().EndSessionAsync(Arg.Any<Guid>(), Arg.Any<SessionEndReport>(), Arg.Any<CancellationToken>());
        _users.User.Should().BeNull();
        await _publisher.Received(1).PublishAsync(IpcMessages.Events.AuthExpired, Arg.Is<AuthExpired>(e => e.Reason == AuthExpiredReason.SeatMoved), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revocation_that_leaves_the_session_open_locks_it_and_another_players_revocation_is_ignored()
    {
        PlaySession session = await SignInWithSessionAsync(_member);
        _server.GetCurrentSessionAsync(PcId, Arg.Any<CancellationToken>()).Returns(session);

        await _guard.HandleRevokedAsync(new UserRevokedPush(Guid.NewGuid(), "blacklisted"), CancellationToken.None);
        _users.User.Should().NotBeNull();

        await _guard.HandleRevokedAsync(new UserRevokedPush(_member.Id, "passwordReset"), CancellationToken.None);

        _users.User.Should().BeNull();
        _sessions.State.Should().Be(SessionState.Locked, "kept for the player, who signs in again with the new password");
        await _publisher.Received(1).PublishAsync(IpcMessages.Events.AuthExpired, Arg.Is<AuthExpired>(e => e.Reason == AuthExpiredReason.PasswordReset), Arg.Any<CancellationToken>());
    }

    // ---- G11: connectivity ------------------------------------------------------------------------

    [Fact]
    public async Task A_shell_is_told_the_current_connectivity_and_auth_status_reports_it()
    {
        _link.Connectivity = ConnectivityState.Offline;
        IServiceProvider services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IServerLinkState)).Returns(_link);
        var bridge = new IpcEventBridge(_users, _shellSettings, services, _clock, NullLogger<IpcEventBridge>.Instance);

        await bridge.PublishCurrentConnectivityAsync(CancellationToken.None);

        bridge.Outbox.TryRead(out IpcEnvelope? sent).Should().BeTrue();
        sent!.Name.Should().Be(IpcMessages.Events.SysConnectivity);
        sent.PayloadAs<ConnectivityEvent>()!.State.Should().Be(ConnectivityState.Offline);

        // Signed in online, then the server went away: auth.status says so.
        _users.Set(_member, T0.AddHours(8), ConnectivityState.Online);
        (await SendAsync(IpcMessages.Auth.Status)).PayloadAs<AuthStatusResponse>()!.Mode.Should().Be(ConnectivityState.Offline);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private SessionLock NewLock() =>
        new(_sessions, _server, _store, _hwid, _clock, _monitor, NullLogger<SessionLock>.Instance, null, _tokens, _sink, _users);

    /// <summary>Signs <paramref name="user"/> in online (with a player token) and starts a server session of theirs.</summary>
    private async Task<PlaySession> SignInWithSessionAsync(User user, DateTimeOffset? expiresAt = null)
    {
        _link.Connectivity = ConnectivityState.Online;
        await _tokens.SetUserAsync(new UserTokens(user.Id, "token-" + user.Username, "refresh", expiresAt ?? T0.AddHours(8)), CancellationToken.None);
        _users.Set(user, expiresAt ?? T0.AddHours(8), ConnectivityState.Online);
        var session = new PlaySession(Guid.NewGuid(), user.Id, PcId, SessionState.Active, T0, T0.AddHours(1), null, TariffId, 3600, 0, Money.Uzs(6_000), true, []);
        _server.CreateSessionAsync(Arg.Any<SessionCreateRequest>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(session);
        return await _sessions.StartAsync(new SessionCreateRequest(PcId, user.Id, TariffId, 60, true), CancellationToken.None);
    }

    private Task<IpcEnvelope> SendAsync(string name, CancellationToken cancellationToken = default) =>
        _dispatcher.DispatchAsync(Context(), IpcEnvelope.Request(name), cancellationToken);

    private Task<IpcEnvelope> SendAsync<TPayload>(string name, TPayload payload, CancellationToken cancellationToken = default) =>
        _dispatcher.DispatchAsync(Context(), IpcEnvelope.Request(name, payload), cancellationToken);

    /// <summary>The context <see cref="PipeServer"/> builds for a Shell that completed <c>auth.hello</c>.</summary>
    private IpcContext Context()
    {
        User? user = _users.User;
        PlaySession? session = _sessions.Current;
        bool open = session is not null && _sessions.State.IsOpen();
        IpcAuthLevel level = user is null ? IpcAuthLevel.Hello : open ? IpcAuthLevel.Session : IpcAuthLevel.User;
        return new IpcContext(Guid.NewGuid(), level, user, open ? session : null, 1, 1, static (_, _, _) => { });
    }

    private static ServerApiException Unavailable() =>
        new(ErrorCode.ServerUnavailable, HttpStatusCode.ServiceUnavailable, null, null, "connection refused");

    private static ServerApiException ServerError(ErrorCode code, HttpStatusCode status, string details)
    {
        using var document = JsonDocument.Parse(details);
        return new ServerApiException(code, status, new ServerError(code, code.Describe(), document.RootElement.Clone(), "t-1"), "t-1");
    }

    /// <summary>A server link whose connectivity the test sets.</summary>
    private sealed class FakeLink : IServerLinkState
    {
        public ConnectivityState Connectivity { get; set; } = ConnectivityState.Offline;

        public DateTimeOffset ConnectivitySince => T0;

        public Guid? PcId => PlayerAccountTests.PcId;
    }
}
