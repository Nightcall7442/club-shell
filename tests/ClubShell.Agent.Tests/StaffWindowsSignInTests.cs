using System.Net;
using System.Text.Json;
using ClubShell.Agent.Ipc;
using ClubShell.Agent.Server;
using ClubShell.Agent.Users;
using ClubShell.Agent.Watchdog;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Users;
using ClubShell.Core.Abstractions;
using ClubShell.Windows.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;

namespace ClubShell.Agent.Tests;

/// <summary>
/// A staff member's sign-in to their own Windows account (<c>sys.staffSignIn</c>): the PIN is the server's to check; on a
/// yes the maintenance window opens without a session, auto-logon is paused and the kiosk signed out; the first other user
/// to sign in is attached and auto-logon resumed; their sign-out, the time running out or nobody signing in brings the
/// kiosk back. The kiosk's own sign-out does not end the window.
/// </summary>
public sealed class StaffWindowsSignInTests : IDisposable
{
    private const uint KioskSession = 1;
    private const uint StaffSession = 3;
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid PcId = Guid.Parse("6a1f2b3c-4d5e-4f60-8a7b-9c0d1e2f3a4b");

    private readonly FakeTimeProvider _time = new(T0);
    private readonly SystemClock _clock;
    private readonly IServerClient _server = Substitute.For<IServerClient>();
    private readonly ISessionService _sessions = Substitute.For<ISessionService>();
    private readonly ShellUserContext _users = new();
    private readonly FakeLink _link = new();
    private readonly FakeHost _host = new();
    private readonly AdminMaintenance _maintenance;
    private readonly StaffWindowsSignIn _signIn;

    public StaffWindowsSignInTests()
    {
        _clock = new SystemClock(_time);
        _sessions.State.Returns(SessionState.Idle);
        _server.VerifyStaffPinAsync(PcId, Arg.Is<StaffVerifyRequest>(r => r.Pin == "2468"), Arg.Any<CancellationToken>())
            .Returns(new StaffVerifyResponse(Guid.NewGuid(), "Техник Ильхом", "cashier"));
        _maintenance = new AdminMaintenance(_clock, NullLogger<AdminMaintenance>.Instance);
        _signIn = new StaffWindowsSignIn(_server, _link, _sessions, _users, _maintenance, _host, _clock, NullLogger<StaffWindowsSignIn>.Instance);
        _signIn.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _signIn.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _signIn.Dispose();
        _maintenance.Dispose();
    }

    [Fact]
    public async Task The_pin_opens_a_window_pauses_auto_logon_and_signs_the_kiosk_out_after_the_answer()
    {
        var answer = await _signIn.SignInAsync(new SysStaffSignInRequest("2468"), CancellationToken.None);

        answer.StaffName.Should().Be("Техник Ильхом");
        answer.Until.Should().Be(T0 + TimeSpan.FromMinutes(StaffWindowsSignIn.DefaultMinutes));
        answer.SignInWithinSec.Should().Be(600);
        _maintenance.WindowsSignIn.Should().BeTrue();
        _maintenance.AutoLogonPaused.Should().BeTrue();
        _host.Paused.Should().BeTrue();
        _host.SignedOut.Should().BeEmpty("the Shell shows the answer before the kiosk goes");

        await AdvanceAsync(StaffWindowsSignIn.SignOutDelay);
        _host.SignedOut.Should().Equal(KioskSession);

        // The kiosk's own sign-out (the watchdog passes every one on) is part of the plan, not the end of the window.
        _host.Logoff(KioskSession);
        _maintenance.OnSessionEnded(KioskSession);
        _maintenance.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task The_staff_members_session_is_attached_auto_logon_resumes_and_their_sign_out_ends_the_window()
    {
        await _signIn.SignInAsync(new SysStaffSignInRequest("2468", Minutes: 90), CancellationToken.None);
        await AdvanceAsync(StaffWindowsSignIn.SignOutDelay);
        _host.Logoff(KioskSession);

        _host.Logon(StaffSession, "Administrator");

        _maintenance.Session.Should().Be(StaffSession);
        _maintenance.AutoLogonPaused.Should().BeFalse();
        _host.Paused.Should().BeFalse("a restart or the staff member's sign-out must bring the kiosk back by itself");
        _host.Notices.Should().ContainSingle(n => n.Session == StaffSession && n.Text.Contains("10:30", StringComparison.Ordinal));

        // Past the sign-in wait nothing is given up: they are in.
        await AdvanceAsync(StaffWindowsSignIn.SignInWithin + TimeSpan.FromMinutes(1));
        _maintenance.IsActive.Should().BeTrue();

        _maintenance.OnSessionEnded(StaffSession);
        _maintenance.IsActive.Should().BeFalse();
        _host.Paused.Should().BeFalse();
    }

    [Fact]
    public async Task Nobody_signing_in_in_time_resumes_auto_logon_and_ends_the_window()
    {
        await _signIn.SignInAsync(new SysStaffSignInRequest("2468"), CancellationToken.None);
        await AdvanceAsync(StaffWindowsSignIn.SignOutDelay);
        _host.Logoff(KioskSession);

        await AdvanceAsync(StaffWindowsSignIn.SignInWithin);

        _maintenance.IsActive.Should().BeFalse();
        _host.Paused.Should().BeFalse();
        _host.Resumes.Should().Be(1);
    }

    [Fact]
    public async Task The_kiosk_logging_on_again_while_nobody_signed_in_ends_the_window()
    {
        await _signIn.SignInAsync(new SysStaffSignInRequest("2468"), CancellationToken.None);
        await AdvanceAsync(StaffWindowsSignIn.SignOutDelay);
        _host.Logoff(KioskSession);

        _host.Logon(7, FakeHost.Kiosk);

        _maintenance.IsActive.Should().BeFalse();
        _host.Paused.Should().BeFalse();
    }

    [Fact]
    public async Task A_player_on_the_pc_an_offline_agent_or_a_pc_without_kiosk_auto_logon_is_refused_before_the_server()
    {
        _sessions.State.Returns(SessionState.Active);
        (await RefusalAsync("2468")).Should().Be((ErrorCode.Conflict, "sessionActive"));
        _sessions.State.Returns(SessionState.Idle);

        _link.Connectivity = ConnectivityState.Offline;
        (await RefusalAsync("2468")).Code.Should().Be(ErrorCode.AgentOffline);
        _link.Connectivity = ConnectivityState.Online;

        _host.AutoLogon = false;
        (await RefusalAsync("2468")).Should().Be((ErrorCode.Conflict, "kioskOff"));
        _host.AutoLogon = true;

        (await RefusalAsync("12a4")).Code.Should().Be(ErrorCode.Validation);
        (await RefusalAsync("123")).Code.Should().Be(ErrorCode.Validation);

        await _server.DidNotReceive().VerifyStaffPinAsync(Arg.Any<Guid>(), Arg.Any<StaffVerifyRequest>(), Arg.Any<CancellationToken>());
        _maintenance.IsActive.Should().BeFalse();
        _host.Paused.Should().BeFalse();
    }

    [Theory]
    [InlineData(ErrorCode.Unauthorized, HttpStatusCode.Unauthorized, "invalidPin")]
    [InlineData(ErrorCode.Forbidden, HttpStatusCode.Forbidden, "pcService")]
    public async Task The_servers_refusal_reaches_the_shell_and_nothing_changes(ErrorCode code, HttpStatusCode status, string reason)
    {
        _server.VerifyStaffPinAsync(PcId, Arg.Is<StaffVerifyRequest>(r => r.Pin == "1357"), Arg.Any<CancellationToken>())
            .ThrowsAsync(ServerError(code, status, $$"""{"reason":"{{reason}}"}"""));

        (await RefusalAsync("1357")).Should().Be((code, reason));
        _maintenance.IsActive.Should().BeFalse();
        _host.Paused.Should().BeFalse();
        await AdvanceAsync(StaffWindowsSignIn.SignOutDelay);
        _host.SignedOut.Should().BeEmpty();
    }

    [Fact]
    public async Task An_agent_started_after_a_reboot_resumes_an_auto_logon_left_paused()
    {
        var host = new FakeHost { Paused = true };
        using var maintenance = new AdminMaintenance(_clock, NullLogger<AdminMaintenance>.Instance);
        using var signIn = new StaffWindowsSignIn(_server, _link, _sessions, _users, maintenance, host, _clock, NullLogger<StaffWindowsSignIn>.Instance);

        await signIn.StartAsync(CancellationToken.None);

        host.Paused.Should().BeFalse();
        await signIn.StopAsync(CancellationToken.None);
    }

    private async Task<(ErrorCode Code, string? Reason)> RefusalAsync(string pin)
    {
        Func<Task> act = () => _signIn.SignInAsync(new SysStaffSignInRequest(pin), CancellationToken.None);
        IpcError error = (await act.Should().ThrowAsync<IpcException>()).Which.Error;
        string? reason = error.Details is { ValueKind: JsonValueKind.Object } details && details.TryGetProperty("reason", out var r) ? r.GetString() : null;
        return (error.Code, reason);
    }

    private async Task AdvanceAsync(TimeSpan by)
    {
        _time.Advance(by);

        // Let continuations scheduled by the timers run.
        await Task.Delay(50);
    }

    private static ServerApiException ServerError(ErrorCode code, HttpStatusCode status, string details)
    {
        using var document = JsonDocument.Parse(details);
        return new ServerApiException(code, status, new ServerError(code, code.Describe(), document.RootElement.Clone(), "t-1"), "t-1");
    }

    private sealed class FakeLink : IServerLinkState
    {
        public ConnectivityState Connectivity { get; set; } = ConnectivityState.Online;

        public DateTimeOffset ConnectivitySince => T0;

        public Guid? PcId => StaffWindowsSignInTests.PcId;
    }

    private sealed class FakeHost : IStaffSignInHost
    {
        public const string Kiosk = "player";

        private readonly Dictionary<uint, string> _sessions = new() { [KioskSession] = Kiosk };

        public event EventHandler<SessionChangedEventArgs>? SessionChanged;

        public bool AutoLogon { get; set; } = true;

        public bool Paused { get; set; }

        public int Resumes { get; private set; }

        public List<uint> SignedOut { get; } = [];

        public List<(uint Session, string Text)> Notices { get; } = [];

        public string KioskUserName => Kiosk;

        public bool KioskAutoLogon => AutoLogon && !Paused;

        public bool AutoLogonPausedMark => Paused;

        public uint? KioskSessionId => _sessions.FirstOrDefault(s => s.Value == Kiosk) is { Value: not null } kiosk ? kiosk.Key : null;

        public string? UserOf(uint sessionId) => _sessions.GetValueOrDefault(sessionId);

        public uint? OtherActiveSession() => _sessions.FirstOrDefault(s => s.Value != Kiosk) is { Value: not null } other ? other.Key : null;

        public void PauseAutoLogon() => Paused = true;

        public void ResumeAutoLogon()
        {
            Paused = false;
            Resumes++;
        }

        public void SignOut(uint sessionId) => SignedOut.Add(sessionId);

        public void Notify(uint sessionId, string text) => Notices.Add((sessionId, text));

        public void Logon(uint sessionId, string user)
        {
            _sessions[sessionId] = user;
            SessionChanged?.Invoke(this, new SessionChangedEventArgs(SessionChangeReason.Logon, sessionId, T0));
        }

        public void Logoff(uint sessionId)
        {
            _sessions.Remove(sessionId);
            SessionChanged?.Invoke(this, new SessionChangedEventArgs(SessionChangeReason.Logoff, sessionId, T0));
        }
    }
}
