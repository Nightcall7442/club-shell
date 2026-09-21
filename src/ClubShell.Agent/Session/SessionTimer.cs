using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Options;
using PlaySession = ClubShell.Contracts.Sessions.Session;

namespace ClubShell.Agent.Session;

/// <summary>Snapshot raised on every <see cref="SessionTimer.Tick"/> (once per second while a session is loaded).</summary>
/// <param name="SecondsLeft">Remaining seconds; <see cref="PlaySession.OpenEnded"/> (−1) for open-ended postpaid.</param>
/// <param name="SecondsUsed">Consumed seconds (does not grow while paused; capped at the purchased budget once expired).</param>
/// <param name="Expired">Prepaid budget exhausted.</param>
public readonly record struct SessionTick(int SecondsLeft, int SecondsUsed, bool Expired);

/// <summary>
/// Monotonic session countdown. Elapsed time comes from <see cref="TimeProvider.GetTimestamp"/> (Stopwatch-style, immune
/// to wall-clock jumps); wall time is only used by <see cref="Resync"/> to age the server's figure. One background
/// <see cref="PeriodicTimer"/> ticks every second for the lifetime of the object; <see cref="Tick"/>,
/// <see cref="Warning"/> and <see cref="Expired"/> are raised from that loop, never under the internal lock.
/// </summary>
public sealed class SessionTimer : IDisposable
{
    /// <summary>Drift below this magnitude is ignored by <see cref="Resync"/>.</summary>
    public const double DriftToleranceSeconds = 2.0;

    private static readonly TimeSpan MaxResyncAge = TimeSpan.FromSeconds(30);

    private readonly IClock _clock;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly ILogger<SessionTimer> _logger;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly List<int> _warningsSent = new();
    private int[] _marks = [];

    private bool _running;
    private bool _paused;
    private bool _openEnded;
    private bool _expired;
    private long _anchor;
    private double _leftAtAnchor;
    private double _usedAtAnchor;

    /// <summary>Creates the timer; the tick loop starts immediately and idles until <see cref="Start"/>.</summary>
    public SessionTimer(IClock clock, IOptionsMonitor<AgentSettings> settings, ILogger<SessionTimer> logger)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _clock = clock;
        _settings = settings;
        _logger = logger;
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    /// <summary>Raised once per second while running (also while paused, with frozen values).</summary>
    public event EventHandler<SessionTick>? Tick;

    /// <summary>Raised once per configured minute mark (<c>session.warningMinutes</c>) when the remaining time drops to it.</summary>
    public event EventHandler<int>? Warning;

    /// <summary>Raised once when a bounded budget reaches zero. Re-armed by <see cref="Extend"/>/<see cref="Resync"/> that add time.</summary>
    public event EventHandler? Expired;

    /// <summary><see langword="true"/> between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary><see langword="true"/> while paused.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _running && _paused;
            }
        }
    }

    /// <summary><see langword="true"/> for an open-ended (postpaid) countdown.</summary>
    public bool IsOpenEnded
    {
        get
        {
            lock (_gate)
            {
                return _running && _openEnded;
            }
        }
    }

    /// <summary>Budget exhausted (bounded sessions only).</summary>
    public bool IsExpired
    {
        get
        {
            lock (_gate)
            {
                return _running && _expired;
            }
        }
    }

    /// <summary>Remaining whole seconds; −1 when open-ended; 0 when stopped or expired.</summary>
    public int SecondsLeft
    {
        get
        {
            lock (_gate)
            {
                if (!_running)
                {
                    return 0;
                }

                return _openEnded ? PlaySession.OpenEnded : (int)Math.Floor(ComputeNoLock().Left);
            }
        }
    }

    /// <summary>Consumed whole seconds; 0 when stopped.</summary>
    public int SecondsUsed
    {
        get
        {
            lock (_gate)
            {
                return _running ? (int)Math.Floor(ComputeNoLock().Used) : 0;
            }
        }
    }

    /// <summary>Remaining time; <see cref="Timeout.InfiniteTimeSpan"/> when open-ended, <see cref="TimeSpan.Zero"/> when stopped or expired.</summary>
    public TimeSpan TimeLeft
    {
        get
        {
            lock (_gate)
            {
                if (!_running)
                {
                    return TimeSpan.Zero;
                }

                return _openEnded ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(ComputeNoLock().Left);
            }
        }
    }

    /// <summary>Minute marks already raised through <see cref="Warning"/> (descending order of emission).</summary>
    public IReadOnlyList<int> WarningsSent
    {
        get
        {
            lock (_gate)
            {
                return _warningsSent.ToArray();
            }
        }
    }

    /// <summary>
    /// Starts (or restarts) the countdown. Marks at or above <paramref name="secondsLeft"/> are treated as already sent so a
    /// short session does not fire a burst of stale warnings at t = 0.
    /// </summary>
    /// <param name="secondsLeft">Remaining budget; ignored when <paramref name="openEnded"/>.</param>
    /// <param name="openEnded">No fixed end (postpaid).</param>
    /// <param name="warningsSent">Marks already emitted (restored session).</param>
    /// <param name="secondsUsed">Seconds already consumed (restored session).</param>
    public void Start(int secondsLeft, bool openEnded, IEnumerable<int>? warningsSent = null, int secondsUsed = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(secondsUsed);
        lock (_gate)
        {
            _marks = (_settings.CurrentValue.Session.WarningMinutes ?? []).Where(m => m > 0).Distinct().OrderByDescending(m => m).ToArray();
            _running = true;
            _paused = false;
            _openEnded = openEnded;
            _expired = false;
            _anchor = _clock.GetTimestamp();
            _leftAtAnchor = openEnded ? 0 : Math.Max(0, secondsLeft);
            _usedAtAnchor = secondsUsed;
            _warningsSent.Clear();
            if (warningsSent is not null)
            {
                _warningsSent.AddRange(warningsSent.Where(m => m > 0).Distinct());
            }

            if (!openEnded)
            {
                foreach (var mark in _marks)
                {
                    if (mark * 60 >= secondsLeft && !_warningsSent.Contains(mark))
                    {
                        _warningsSent.Add(mark);
                    }
                }
            }
        }

        _logger.LogDebug("Session timer started: left={SecondsLeft} openEnded={OpenEnded} used={SecondsUsed}", secondsLeft, openEnded, secondsUsed);
    }

    /// <summary>Freezes the countdown; no-op when already paused or stopped.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (!_running || _paused)
            {
                return;
            }

            FoldNoLock();
            _paused = true;
        }
    }

    /// <summary>Continues a paused countdown; no-op otherwise.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (!_running || !_paused)
            {
                return;
            }

            _paused = false;
            _anchor = _clock.GetTimestamp();
        }
    }

    /// <summary>Adds <paramref name="seconds"/> to the budget (bounded sessions) and re-arms warnings now above the remaining time.</summary>
    public void Extend(int seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        lock (_gate)
        {
            if (!_running || _openEnded)
            {
                return;
            }

            FoldNoLock();
            _leftAtAnchor += seconds;
            RearmNoLock();
        }
    }

    /// <summary>Stops the countdown; subsequent reads return zero.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            _paused = false;
            _expired = false;
            _leftAtAnchor = 0;
            _usedAtAnchor = 0;
            _warningsSent.Clear();
        }
    }

    /// <summary>
    /// Corrects drift against the server's figure. <paramref name="serverSecondsLeft"/> is aged by the time elapsed since
    /// <paramref name="serverTime"/> (clamped to 30 s) and adopted when it differs from the local value by more than
    /// <see cref="DriftToleranceSeconds"/>.
    /// </summary>
    /// <returns>Applied correction in seconds (positive = server had more time), 0 when nothing changed.</returns>
    public int Resync(int serverSecondsLeft, DateTimeOffset serverTime)
    {
        int applied;
        lock (_gate)
        {
            if (!_running || _openEnded || serverSecondsLeft < 0)
            {
                return 0;
            }

            var age = _clock.UtcNow - serverTime;
            if (age < TimeSpan.Zero)
            {
                age = TimeSpan.Zero;
            }
            else if (age > MaxResyncAge)
            {
                age = MaxResyncAge;
            }

            var target = Math.Max(0, serverSecondsLeft - (_paused ? 0 : age.TotalSeconds));
            var drift = target - ComputeNoLock().Left;
            if (Math.Abs(drift) < DriftToleranceSeconds)
            {
                return 0;
            }

            FoldNoLock();
            _leftAtAnchor = target;
            applied = (int)Math.Round(drift);
            if (drift > 0)
            {
                RearmNoLock();
            }
        }

        _logger.LogInformation("Session timer resynced by {DriftSeconds}s to server", applied);
        return applied;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Loop ended by cancellation.
        }

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    private (double Left, double Used) ComputeNoLock()
    {
        var elapsed = _paused ? 0 : _clock.GetElapsedTime(_anchor).TotalSeconds;
        if (_openEnded)
        {
            return (-1, _usedAtAnchor + elapsed);
        }

        var left = _leftAtAnchor - elapsed;
        if (left <= 0)
        {
            return (0, _usedAtAnchor + _leftAtAnchor);
        }

        return (left, _usedAtAnchor + elapsed);
    }

    /// <summary>Moves the anchor to now, folding the elapsed time into the stored counters (running state only).</summary>
    private void FoldNoLock()
    {
        if (_paused)
        {
            return;
        }

        var (left, used) = ComputeNoLock();
        _leftAtAnchor = _openEnded ? 0 : left;
        _usedAtAnchor = used;
        _anchor = _clock.GetTimestamp();
    }

    private void RearmNoLock()
    {
        var left = ComputeNoLock().Left;
        _warningsSent.RemoveAll(m => m * 60 < left);
        if (_expired && left > 0)
        {
            _expired = false;
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _clock.Provider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    OnTick();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Session timer tick handler failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
    }

    private void OnTick()
    {
        SessionTick tick;
        List<int>? warnings = null;
        var expiredNow = false;
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            var (left, used) = ComputeNoLock();
            if (!_openEnded && !_paused)
            {
                foreach (var mark in _marks)
                {
                    if (left > 0 && left <= mark * 60 && !_warningsSent.Contains(mark))
                    {
                        _warningsSent.Add(mark);
                        (warnings ??= new List<int>()).Add(mark);
                    }
                }

                if (left <= 0 && !_expired)
                {
                    _expired = true;
                    expiredNow = true;
                }
            }

            tick = new SessionTick(_openEnded ? PlaySession.OpenEnded : (int)Math.Floor(left), (int)Math.Floor(used), _expired);
        }

        Tick?.Invoke(this, tick);
        if (warnings is not null)
        {
            foreach (var mark in warnings)
            {
                Warning?.Invoke(this, mark);
            }
        }

        if (expiredNow)
        {
            Expired?.Invoke(this, EventArgs.Empty);
        }
    }
}
