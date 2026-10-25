using ClubShell.Core.Abstractions;

namespace ClubShell.Agent.Watchdog;

/// <summary>
/// An administrator's maintenance window on this PC (SHELL_CHANGES 28, 33, 34), opened by <c>sys.adminExit</c> when the
/// Shell closes after a PIN unlock: the watchdog does not restart the Shell and does not count the exit as a crash, USB
/// storage works and the process deny-list is not enforced. It ends when it times out, when the kiosk session it was
/// opened in signs out, or when the Agent restarts (it is kept in memory only, so a reboot always comes back sealed).
/// </summary>
public sealed class AdminMaintenance : IDisposable
{
    /// <summary>Length of a window when the Shell asks for no other.</summary>
    public const int DefaultMinutes = 30;

    /// <summary>Longest window.</summary>
    public const int MaxMinutes = 480;

    private readonly IClock _clock;
    private readonly ILogger<AdminMaintenance> _logger;
    private readonly object _gate = new();
    private ITimer? _timer;
    private DateTimeOffset? _until;
    private uint? _session;

    /// <summary>Creates the window, closed.</summary>
    public AdminMaintenance(IClock clock, ILogger<AdminMaintenance> logger)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _clock = clock;
        _logger = logger;
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
    /// Opens a window of <paramref name="duration"/> for the kiosk session <paramref name="sessionId"/> (or extends the open
    /// one); returns when it ends.
    /// </summary>
    public DateTimeOffset Begin(TimeSpan duration, uint? sessionId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        DateTimeOffset until = _clock.UtcNow + duration;
        bool opened;
        lock (_gate)
        {
            opened = _until is null;
            _until = until;
            _session = sessionId;
            _timer?.Dispose();
            _timer = _clock.Provider.CreateTimer(_ => End("timed out"), null, duration, Timeout.InfiniteTimeSpan);
        }

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
}
