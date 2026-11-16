using System.Text.Json;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Watchdog;

/// <summary>
/// An administrator's maintenance window on this PC (SHELL_CHANGES 28, 33, 34), opened by <c>sys.adminExit</c> when the
/// Shell closes after a PIN unlock: the watchdog does not restart the Shell and does not count the exit as a crash, USB
/// storage works and the process deny-list is not enforced. It ends when it times out, when the kiosk session it was
/// opened in signs out, when a player's session starts, when the Shell connects again, or on a lock from the cash desk
/// (<see cref="AdminMaintenanceCloser"/>, SHELL_CHANGES 49). It is kept in <c>secure\maintenance.json</c> and comes back
/// after an Agent restart in the same Windows boot (an installer, an update), never after a reboot, which always comes
/// back sealed (SHELL_CHANGES 69).
/// <para>
/// A staff member's Windows sign-in (<c>sys.staffSignIn</c>, <see cref="BeginWindowsSignIn"/>) opens the window without a
/// session: the kiosk is signed out on purpose, so its sign-out does not end it, and the kiosk's auto-logon stays paused
/// (<see cref="AutoLogonPaused"/>) until the staff member's own Windows session is <see cref="Attach">attached</see>; from
/// then on it is that session's sign-out that ends the window.
/// </para>
/// </summary>
public sealed class AdminMaintenance : IDisposable
{
    /// <summary>Length of a window when the Shell asks for no other.</summary>
    public const int DefaultMinutes = 30;

    /// <summary>Longest window.</summary>
    public const int MaxMinutes = 480;

    /// <summary>File of the open window under the secure directory.</summary>
    public const string StateFileName = "maintenance.json";

    /// <summary>How far two readings of the boot time may differ and still be the same boot.</summary>
    private static readonly TimeSpan SameBootTolerance = TimeSpan.FromMinutes(2);

    private readonly IClock _clock;
    private readonly ILogger<AdminMaintenance> _logger;
    private readonly IOptionsMonitor<AgentSettings>? _settings;
    private readonly Func<TimeSpan> _uptime;
    private readonly object _gate = new();
    private ITimer? _timer;
    private DateTimeOffset? _until;
    private uint? _session;
    private bool _windows;
    private string? _staff;

    /// <summary>
    /// Creates the window, closed — or, with <paramref name="settings"/>, as an Agent that restarted in the same boot left
    /// it. <paramref name="uptime"/> (time since Windows started) is for tests.
    /// </summary>
    public AdminMaintenance(IClock clock, ILogger<AdminMaintenance> logger, IOptionsMonitor<AgentSettings>? settings = null, Func<TimeSpan>? uptime = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _clock = clock;
        _logger = logger;
        _settings = settings;
        _uptime = uptime ?? (() => TimeSpan.FromMilliseconds(Environment.TickCount64));
        Restore();
    }

    /// <summary>Raised with <see langword="true"/> when a window opens and <see langword="false"/> when it ends.</summary>
    public event EventHandler<bool>? Changed;

    /// <summary><see langword="true"/> while a window is open and has not timed out.</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _until is { } until && until > _clock.UtcNow;
            }
        }
    }

    /// <summary>End of the open window, or <see langword="null"/>.</summary>
    public DateTimeOffset? Until
    {
        get
        {
            lock (_gate)
            {
                return _until;
            }
        }
    }

    /// <summary>
    /// The open window is a staff member's Windows sign-in (<see cref="BeginWindowsSignIn"/>): the kiosk is signed out and
    /// <see cref="Session"/> is the staff member's own Windows session once attached.
    /// </summary>
    public bool WindowsSignIn
    {
        get
        {
            lock (_gate)
            {
                return _windows && _until is { } until && until > _clock.UtcNow;
            }
        }
    }

    /// <summary>
    /// The kiosk's auto-logon must stay paused: a staff member's Windows sign-in is open and nobody has signed in to Windows
    /// yet. Whoever sets auto-logon (the <c>shellReplacement</c> policy) leaves it alone meanwhile, or Winlogon could log the
    /// kiosk on in front of the staff member.
    /// </summary>
    public bool AutoLogonPaused
    {
        get
        {
            lock (_gate)
            {
                return _windows && _session is null && _until is { } until && until > _clock.UtcNow;
            }
        }
    }

    /// <summary>Staff member of a Windows sign-in window, for notices and logs.</summary>
    public string? StaffName
    {
        get
        {
            lock (_gate)
            {
                return _staff;
            }
        }
    }

    /// <summary>Kiosk session the open window was opened in (a Windows sign-in: the staff member's session), when known.</summary>
    public uint? Session
    {
        get
        {
            lock (_gate)
            {
                return _session;
            }
        }
    }

    private string? StatePath => _settings is null ? null : Path.Combine(_settings.CurrentValue.SecureDir, StateFileName);

    /// <summary>
    /// Opens a window of <paramref name="duration"/> for the kiosk session <paramref name="sessionId"/> (or extends the open
    /// one); returns when it ends.
    /// </summary>
    public DateTimeOffset Begin(TimeSpan duration, uint? sessionId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        DateTimeOffset until = _clock.UtcNow + duration;
        bool opened = Open(until, sessionId, windows: false, staff: null);
        Save();
        _logger.LogWarning("Administrator maintenance until {Until}: Shell restarts paused, USB storage and service tools allowed", until);
        if (opened)
        {
            Raise(true);
        }

        return until;
    }

    /// <summary>
    /// Opens a window of <paramref name="duration"/> for <paramref name="staffName"/>'s sign-in to their own Windows account
    /// (or turns the open one into it): no session yet, auto-logon held paused until <see cref="Attach"/>; returns when it ends.
    /// </summary>
    public DateTimeOffset BeginWindowsSignIn(TimeSpan duration, string staffName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        ArgumentException.ThrowIfNullOrWhiteSpace(staffName);
        DateTimeOffset until = _clock.UtcNow + duration;
        bool opened = Open(until, sessionId: null, windows: true, staff: staffName);
        Save();
        _logger.LogWarning("Staff member {Staff} signs in to Windows; maintenance until {Until}: Shell restarts paused, USB storage and service tools allowed", staffName, until);
        if (opened)
        {
            Raise(true);
        }

        return until;
    }

    /// <summary>
    /// The staff member signed in to Windows as session <paramref name="sessionId"/>: its sign-out now ends the window, and
    /// auto-logon is no longer held. <see langword="false"/> when no Windows sign-in window waits for a session.
    /// </summary>
    public bool Attach(uint sessionId)
    {
        lock (_gate)
        {
            if (!_windows || _session is not null || _until is not { } until || until <= _clock.UtcNow)
            {
                return false;
            }

            _session = sessionId;
        }

        Save();
        _logger.LogWarning("Staff member's Windows session {SessionId} attached to the maintenance window", sessionId);
        return true;
    }

    /// <summary>Ends the open window (a no-op when none is open).</summary>
    public void End(string reason)
    {
        lock (_gate)
        {
            if (_until is null)
            {
                return;
            }

            _until = null;
            _session = null;
            _windows = false;
            _staff = null;
            _timer?.Dispose();
            _timer = null;
        }

        Delete();
        _logger.LogWarning("Administrator maintenance ended ({Reason}); the kiosk is sealed again", reason);
        Raise(false);
    }

    /// <summary>
    /// Ends the window when <paramref name="sessionId"/> is the session it was opened in (or that is unknown); a Windows
    /// sign-in window only when it is the staff member's attached session — the kiosk's own sign-out is part of it.
    /// </summary>
    public void OnSessionEnded(uint sessionId)
    {
        bool mine;
        lock (_gate)
        {
            mine = _until is not null && (_windows ? _session == sessionId : _session is null || _session == sessionId);
        }

        if (mine)
        {
            End($"session {sessionId} ended");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private bool Open(DateTimeOffset until, uint? sessionId, bool windows, string? staff)
    {
        lock (_gate)
        {
            bool opened = _until is null;
            _until = until;
            _session = sessionId;
            _windows = windows;
            _staff = staff;
            _timer?.Dispose();
            _timer = _clock.Provider.CreateTimer(_ => End("timed out"), null, until - _clock.UtcNow, Timeout.InfiniteTimeSpan);
            return opened;
        }
    }

    private DateTimeOffset BootedAt() => _clock.UtcNow - _uptime();

    private void Restore()
    {
        if (StatePath is not { } path || !File.Exists(path))
        {
            return;
        }

        try
        {
            State? state = JsonSerializer.Deserialize<State>(File.ReadAllText(path));
            DateTimeOffset now = _clock.UtcNow;
            if (state is null || (state.BootedAt - BootedAt()).Duration() > SameBootTolerance || state.Until <= now)
            {
                Delete();
                return;
            }

            _ = Open(state.Until, state.Session, state.WindowsSignIn, state.Staff);
            _logger.LogWarning("Administrator maintenance restored after an Agent restart, until {Until}", state.Until);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Maintenance state {Path} unreadable; the kiosk stays sealed", path);
            Delete();
        }
    }

    private void Save()
    {
        if (StatePath is not { } path)
        {
            return;
        }

        State state;
        lock (_gate)
        {
            if (_until is not { } until)
            {
                return;
            }

            state = new State(until, _session, BootedAt(), _windows, _staff);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(state));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Maintenance window not saved; an Agent restart ends it");
        }
    }

    private void Delete()
    {
        if (StatePath is not { } path)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Maintenance state {Path} not deleted", path);
        }
    }

    private void Raise(bool active)
    {
        EventHandler<bool>? handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<bool> handler in handlers.GetInvocationList().Cast<EventHandler<bool>>())
        {
            try
            {
                handler(this, active);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A maintenance {State} handler failed", active ? "start" : "end");
            }
        }
    }

    private sealed record State(DateTimeOffset Until, uint? Session, DateTimeOffset BootedAt, bool WindowsSignIn = false, string? Staff = null);
}
