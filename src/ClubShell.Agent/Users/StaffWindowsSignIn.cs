using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using ClubShell.Agent.Ipc;
using ClubShell.Agent.Policy;
using ClubShell.Agent.Server;
using ClubShell.Agent.Watchdog;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Sessions;
using ClubShell.Contracts.Users;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Registry;
using ClubShell.Windows.Sessions;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Users;

/// <summary>The Windows side of <see cref="StaffWindowsSignIn"/>: sessions, auto-logon and notices (tests fake it).</summary>
public interface IStaffSignInHost
{
    /// <summary>A Windows session logged on or off (<see cref="SessionChangeWatcher"/>).</summary>
    event EventHandler<SessionChangedEventArgs>? SessionChanged;

    /// <summary>The kiosk account's name.</summary>
    string KioskUserName { get; }

    /// <summary>Winlogon's auto-logon is on and logs the kiosk on: there is a kiosk to sign out and to bring back.</summary>
    bool KioskAutoLogon { get; }

    /// <summary>
    /// Auto-logon was paused for a staff member's sign-in and not resumed (a mark that survives a reboot), so an Agent that
    /// starts without the window resumes it.
    /// </summary>
    bool AutoLogonPausedMark { get; }

    /// <summary>The kiosk's logged-on session, if any.</summary>
    uint? KioskSessionId { get; }

    /// <summary>User name of a logged-on session, or <see langword="null"/>.</summary>
    string? UserOf(uint sessionId);

    /// <summary>The active session of a user other than the kiosk (the console, or Remote Desktop), if any.</summary>
    uint? OtherActiveSession();

    /// <summary>Pauses auto-logon (<see cref="ShellRegistry.PauseAutoLogon"/>) and leaves the mark.</summary>
    void PauseAutoLogon();

    /// <summary>Turns the kiosk's auto-logon back on with its current password and clears the mark.</summary>
    void ResumeAutoLogon();

    /// <summary>Signs a session out of Windows.</summary>
    void SignOut(uint sessionId);

    /// <summary>A message box on a session's desktop that does not wait for an answer.</summary>
    void Notify(uint sessionId, string text);
}

/// <summary>
/// A staff member signs in to their own Windows account on this PC (<c>sys.staffSignIn</c>), to install games, launchers
/// and programs with an administrator's rights — the kiosk account has none, and its profile is reset. The lock screen's
/// «Staff» button sends their PIN from the admin console; the server checks it and that the owner allowed them to service
/// PCs (<c>POST /agents/{pcId}/staff/verify</c>, which also writes the staff journal). Then:
/// <list type="number">
/// <item>a maintenance window opens in its Windows sign-in form (<see cref="AdminMaintenance.BeginWindowsSignIn"/>): the
/// Shell is not restarted, USB storage and service tools work;</item>
/// <item>the kiosk's auto-logon is paused (<c>ForceAutoLogon</c> would otherwise log the kiosk straight back on) and the
/// kiosk is signed out a moment after the answer reached the Shell: Windows shows its sign-in screen;</item>
/// <item>the first other user who signs in is the staff member: their session is attached to the window and auto-logon is
/// turned back on at once, so their sign-out — or a reboot in the middle of an installer — brings the kiosk back by
/// itself; a notice on their desktop says so;</item>
/// <item>when the window ends (their sign-out, the time, a player's session, the Shell back), and nobody signed in within
/// <see cref="SignInWithin"/>, auto-logon is resumed and the kiosk logon guard reboots the PC into the kiosk
/// (<see cref="KioskLogonGuard"/>: only when nobody else is at work, at most once per 15 min).</item>
/// </list>
/// Only on a PC without a player (nobody signed in to the Shell, no session open), online, and whose kiosk logs on by
/// itself. An Agent that restarts in the middle (an installer) keeps the window; one that starts after a reboot finds
/// auto-logon still paused by the mark and resumes it.
/// </summary>
public sealed partial class StaffWindowsSignIn : IHostedService, IDisposable
{
    /// <summary>Length of the window when the Shell asks for no other.</summary>
    public const int DefaultMinutes = 240;

    /// <summary>How long the Windows sign-in screen waits for the staff member.</summary>
    public static readonly TimeSpan SignInWithin = TimeSpan.FromMinutes(10);

    /// <summary>Pause between the answer to the Shell and the kiosk's sign-out, so the answer is shown first.</summary>
    public static readonly TimeSpan SignOutDelay = TimeSpan.FromSeconds(2);

    private readonly IServerClient _server;
    private readonly IServerLinkState _link;
    private readonly ISessionService _sessions;
    private readonly ShellUserContext _users;
    private readonly AdminMaintenance _maintenance;
    private readonly IStaffSignInHost _host;
    private readonly IClock _clock;
    private readonly ILogger<StaffWindowsSignIn> _logger;
    private readonly KioskLogonGuard? _logonGuard;
    private readonly Guid? _configuredPcId;
    private readonly object _gate = new();
    private ITimer? _waitTimer;
    private bool _staffWindow;

    /// <summary>Creates the service.</summary>
    public StaffWindowsSignIn(
        IServerClient server,
        IServerLinkState link,
        ISessionService sessions,
        ShellUserContext users,
        AdminMaintenance maintenance,
        IStaffSignInHost host,
        IClock clock,
        ILogger<StaffWindowsSignIn> logger,
        KioskLogonGuard? logonGuard = null,
        IOptionsMonitor<AgentSettings>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _server = server;
        _link = link;
        _sessions = sessions;
        _users = users;
        _maintenance = maintenance;
        _host = host;
        _clock = clock;
        _logger = logger;
        _logonGuard = logonGuard;
        _configuredPcId = settings?.CurrentValue.PcId;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _host.SessionChanged += OnSessionChanged;
        _maintenance.Changed += OnMaintenanceChanged;
        try
        {
            Recover();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Staff Windows sign-in state could not be recovered at start");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _host.SessionChanged -= OnSessionChanged;
        _maintenance.Changed -= OnMaintenanceChanged;
        StopWaiting();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => StopWaiting();

    /// <summary>
    /// <c>sys.staffSignIn</c>: checks the PIN on the server, opens the window, pauses auto-logon and signs the kiosk out
    /// (after <see cref="SignOutDelay"/>). Refusals are thrown as <see cref="IpcException"/> (see <see cref="SysStaffSignInRequest"/>).
    /// </summary>
    public async Task<SysStaffSignInResponse> SignInAsync(SysStaffSignInRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Pin is null || !PinPattern().IsMatch(request.Pin))
        {
            throw IpcError.Validation("pin", "digits4to8").ToException();
        }

        int minutes = request.Minutes ?? DefaultMinutes;
        if (minutes is < 1 or > AdminMaintenance.MaxMinutes)
        {
            throw IpcError.Validation("minutes", "range", $"must be 1..{AdminMaintenance.MaxMinutes}").ToException();
        }

        if (_sessions.State.IsOpen() || _users.User is not null)
        {
            throw IpcError.Conflict("A player is on this PC: end the session first", "sessionActive").ToException();
        }

        if (!_host.KioskAutoLogon)
        {
            throw IpcError.Conflict("The kiosk does not log on by itself on this PC: sign in to Windows as usual", "kioskOff").ToException();
        }

        if (_link.Connectivity != ConnectivityState.Online || (_configuredPcId ?? _link.PcId) is not { } pcId)
        {
            throw IpcError.AgentOffline().ToException();
        }

        StaffVerifyResponse staff;
        try
        {
            staff = await _server.VerifyStaffPinAsync(pcId, new StaffVerifyRequest(request.Pin), cancellationToken).ConfigureAwait(false);
        }
        catch (ServerApiException ex)
        {
            _logger.LogWarning("Staff Windows sign-in refused by the server: {Code} {Reason}", ex.Code, ex.Reason);
            throw ex.ToIpcError().ToException();
        }

        // The server took a few hundred milliseconds: a player may have signed in at the desk meanwhile.
        if (_sessions.State.IsOpen() || _users.User is not null)
        {
            throw IpcError.Conflict("A player is on this PC: end the session first", "sessionActive").ToException();
        }

        DateTimeOffset until = _maintenance.BeginWindowsSignIn(TimeSpan.FromMinutes(minutes), staff.Name);
        lock (_gate)
        {
            _staffWindow = true;
        }

        try
        {
            _host.PauseAutoLogon();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Auto-logon could not be paused; the staff sign-in is called off");
            _maintenance.End("auto-logon could not be paused");
            throw IpcError.Conflict("Auto-logon could not be paused on this PC", "kioskOff").ToException();
        }

        _logger.LogWarning("Staff member {Staff} ({Role}) signs in to Windows on this PC; the kiosk is signed out, maintenance until {Until}", staff.Name, staff.Role, until);
        WaitForSignIn();
        _ = SignOutKioskSoonAsync();
        return new SysStaffSignInResponse(staff.Name, until, (int)SignInWithin.TotalSeconds);
    }

    private async Task SignOutKioskSoonAsync()
    {
        try
        {
            await Task.Delay(SignOutDelay, _clock.Provider).ConfigureAwait(false);
            if (!_maintenance.AutoLogonPaused)
            {
                return;
            }

            if (_host.KioskSessionId is { } kiosk)
            {
                _host.SignOut(kiosk);
                _logger.LogInformation("Kiosk session {SessionId} signed out for the staff member's Windows sign-in", kiosk);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogError(ex, "The kiosk could not be signed out for the staff member's Windows sign-in");
            GiveUp("the kiosk could not be signed out");
        }
    }

    private void OnSessionChanged(object? sender, SessionChangedEventArgs e)
    {
        try
        {
            if (e.Reason is not (SessionChangeReason.Logon or SessionChangeReason.ConsoleConnect or SessionChangeReason.RemoteConnect))
            {
                return;
            }

            if (!_maintenance.AutoLogonPaused || _host.UserOf(e.SessionId) is not { Length: > 0 } user)
            {
                return;
            }

            if (string.Equals(user, _host.KioskUserName, StringComparison.OrdinalIgnoreCase))
            {
                // The kiosk itself came back (someone typed its password, or an old auto-logon): nothing to wait for.
                _maintenance.End("the kiosk logged on again");
                return;
            }

            Attach(e.SessionId, user);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Staff Windows sign-in: session change {Reason} of {SessionId} not handled", e.Reason, e.SessionId);
        }
    }

    private void Attach(uint sessionId, string user)
    {
        if (!_maintenance.Attach(sessionId))
        {
            return;
        }

        StopWaiting();
        Resume();
        _logger.LogWarning("{User} signed in to Windows (session {SessionId}) for {Staff}; signing out brings the kiosk back", user, sessionId, _maintenance.StaffName);
        if (_maintenance.Until is { } until)
        {
            string time = TimeZoneInfo.ConvertTime(until, _clock.Provider.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture);
            try
            {
                _host.Notify(sessionId,
                    $"Обслуживание до {time}: флешки и служебные программы разрешены.\n" +
                    "Когда закончите, выйдите из Windows (Пуск → Выход): ПК вернётся к игрокам. Перезагрузка тоже возвращает ПК к игрокам.\n\n" +
                    $"Maintenance until {time}. Sign out of Windows when you are done: the PC goes back to the players (a restart does too).");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                _logger.LogDebug(ex, "Maintenance notice not shown in session {SessionId}", sessionId);
            }
        }
    }

    private void OnMaintenanceChanged(object? sender, bool active)
    {
        if (active)
        {
            return;
        }

        StopWaiting();
        bool ours;
        lock (_gate)
        {
            ours = _staffWindow;
            _staffWindow = false;
        }

        if (_host.AutoLogonPausedMark)
        {
            // Nobody signed in to Windows: auto-logon is still paused.
            BringKioskBack("the maintenance window ended");
        }
        else if (ours && _host.KioskSessionId is null)
        {
            // The staff member signed out (or the window ran out while they work): Winlogon logs the kiosk on by itself;
            // if it does not, the kiosk is out because of the Agent and the guard may reboot once nobody is at work.
            _logonGuard?.NoteSignedOut();
        }
    }

    private void WaitForSignIn()
    {
        lock (_gate)
        {
            _waitTimer?.Dispose();
            _waitTimer = _clock.Provider.CreateTimer(_ => GiveUp("nobody signed in to Windows in time"), null, SignInWithin, Timeout.InfiniteTimeSpan);
        }
    }

    private void StopWaiting()
    {
        lock (_gate)
        {
            _waitTimer?.Dispose();
            _waitTimer = null;
        }
    }

    private void GiveUp(string reason)
    {
        try
        {
            if (_maintenance.AutoLogonPaused)
            {
                // Ending the window brings the kiosk back (OnMaintenanceChanged).
                _maintenance.End(reason);
            }
            else if (_host.AutoLogonPausedMark)
            {
                BringKioskBack(reason);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Staff Windows sign-in could not be given up ({Reason})", reason);
        }
    }

    /// <summary>Auto-logon on again; with nobody at the PC the guard reboots it into the kiosk (Winlogon logs on at boot).</summary>
    private void BringKioskBack(string reason)
    {
        Resume();
        if (_host.KioskSessionId is not null)
        {
            return;
        }

        // The kiosk is out because the Agent signed it out: the guard may bring it back, now or when the watchdog asks.
        _logonGuard?.NoteSignedOut();
        if (_host.OtherActiveSession() is null)
        {
            _logger.LogWarning("Staff Windows sign-in over ({Reason}); bringing the kiosk back", reason);
            _ = _logonGuard?.Recover("staffSignIn");
        }
    }

    private void Resume()
    {
        try
        {
            _host.ResumeAutoLogon();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Auto-logon of the kiosk could not be resumed; the shellReplacement policy sets it at its next apply");
        }
    }

    /// <summary>At start: a window restored from before an Agent restart waits again; a paused auto-logon without one is resumed.</summary>
    private void Recover()
    {
        if (_maintenance.WindowsSignIn)
        {
            lock (_gate)
            {
                _staffWindow = true;
            }
        }

        if (_maintenance.AutoLogonPaused)
        {
            if (_host.OtherActiveSession() is { } session && _host.UserOf(session) is { Length: > 0 } user)
            {
                Attach(session, user);
            }
            else
            {
                WaitForSignIn();
            }

            return;
        }

        if (_host.AutoLogonPausedMark && !_maintenance.WindowsSignIn)
        {
            // A reboot (or a window that ended while the Agent was down) left auto-logon paused.
            BringKioskBack("the Agent started without the staff sign-in window");
        }
    }

    [GeneratedRegex("^[0-9]{4,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex PinPattern();
}

/// <summary>The Windows side of <see cref="StaffWindowsSignIn"/>: WTS sessions, Winlogon's auto-logon and the pause mark.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStaffSignInHost : IStaffSignInHost
{
    private const string MarkFileName = "staff-signin-autologon-paused";

    private static readonly TimeSpan NoticeTimeout = TimeSpan.FromSeconds(60);

    private readonly ShellRegistry _shell;
    private readonly IKioskCredentials _kiosk;
    private readonly SessionChangeWatcher _watcher;
    private readonly IOptionsMonitor<AgentSettings> _settings;

    /// <summary>Creates the host.</summary>
    public WindowsStaffSignInHost(ShellRegistry shell, IKioskCredentials kiosk, SessionChangeWatcher watcher, IOptionsMonitor<AgentSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(kiosk);
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(settings);
        _shell = shell;
        _kiosk = kiosk;
        _watcher = watcher;
        _settings = settings;
    }

    /// <inheritdoc />
    public event EventHandler<SessionChangedEventArgs>? SessionChanged
    {
        add => _watcher.SessionChanged += value;
        remove => _watcher.SessionChanged -= value;
    }

    /// <inheritdoc />
    public string KioskUserName => _kiosk.UserName;

    /// <inheritdoc />
    public bool KioskAutoLogon
    {
        get
        {
            AutoLogonState state = ShellRegistry.GetAutoLogon();
            return state.Enabled && string.Equals(state.UserName, _kiosk.UserName, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <inheritdoc />
    public bool AutoLogonPausedMark => File.Exists(MarkPath);

    /// <inheritdoc />
    public uint? KioskSessionId => WtsSessions.FindByUser(_kiosk.UserName)?.Id;

    private string MarkPath => Path.Combine(_settings.CurrentValue.SecureDir, MarkFileName);

    /// <inheritdoc />
    public string? UserOf(uint sessionId) => WtsSessions.Get(sessionId)?.UserName;

    /// <inheritdoc />
    public uint? OtherActiveSession() =>
        WtsSessions.Enumerate().FirstOrDefault(s => s.HasUser && s.IsActive && !string.Equals(s.UserName, _kiosk.UserName, StringComparison.OrdinalIgnoreCase))?.Id;

    /// <inheritdoc />
    public void PauseAutoLogon()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MarkPath)!);
        File.WriteAllText(MarkPath, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        _shell.PauseAutoLogon();
    }

    /// <inheritdoc />
    public void ResumeAutoLogon()
    {
        if (string.IsNullOrWhiteSpace(_kiosk.UserName) || string.IsNullOrEmpty(_kiosk.Password))
        {
            throw new InvalidOperationException("Kiosk credentials unavailable; auto-logon cannot be resumed yet.");
        }

        _shell.SetAutoLogon(_kiosk.UserName, _kiosk.Password);
        File.Delete(MarkPath);
    }

    /// <inheritdoc />
    public void SignOut(uint sessionId) => WtsSessions.Logoff(sessionId, wait: false);

    /// <inheritdoc />
    public void Notify(uint sessionId, string text) => _ = WtsSessions.SendMessage(sessionId, "ClubShell", text, NoticeTimeout, wait: false);
}
