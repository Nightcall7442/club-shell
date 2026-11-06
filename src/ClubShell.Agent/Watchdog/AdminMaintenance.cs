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

    /// <summary>Kiosk session the open window was opened in, when known.</summary>
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
        bool opened = Open(until, sessionId);
        Save(until, sessionId);
        _logger.LogWarning("Administrator maintenance until {Until}: Shell restarts paused, USB storage and service tools allowed", until);
        if (opened)
        {
            Raise(true);
        }

        return until;
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
            _timer?.Dispose();
            _timer = null;
        }

        Delete();
        _logger.LogWarning("Administrator maintenance ended ({Reason}); the kiosk is sealed again", reason);
        Raise(false);
    }

    /// <summary>Ends the window when <paramref name="sessionId"/> is the session it was opened in (or that is unknown).</summary>
    public void OnSessionEnded(uint sessionId)
    {
        bool mine;
        lock (_gate)
        {
            mine = _until is not null && (_session is null || _session == sessionId);
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

    private bool Open(DateTimeOffset until, uint? sessionId)
    {
        lock (_gate)
        {
            bool opened = _until is null;
            _until = until;
            _session = sessionId;
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

            _ = Open(state.Until, state.Session);
            _logger.LogWarning("Administrator maintenance restored after an Agent restart, until {Until}", state.Until);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Maintenance state {Path} unreadable; the kiosk stays sealed", path);
            Delete();
        }
    }

    private void Save(DateTimeOffset until, uint? sessionId)
    {
        if (StatePath is not { } path)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new State(until, sessionId, BootedAt())));
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

    private sealed record State(DateTimeOffset Until, uint? Session, DateTimeOffset BootedAt);
}
