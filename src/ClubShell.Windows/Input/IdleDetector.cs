using System.Runtime.Versioning;

using ClubShell.Windows.Native;

using Microsoft.Extensions.Logging;

namespace ClubShell.Windows.Input;

/// <summary>
/// Tracks user inactivity by polling <c>GetLastInputInfo</c> on a fixed cadence and raising
/// <see cref="IdleStateChanged"/> when the idle time crosses <see cref="IdleThreshold"/>. External input
/// sources that <c>GetLastInputInfo</c> does not see (a gamepad, an injected activity signal) can call
/// <see cref="NotifyActivity"/> to reset the clock. Hysteresis prevents flapping at the boundary.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IdleDetector : IAsyncDisposable
{
    private readonly TimeSpan _pollInterval;
    private readonly ILogger? _logger;
    private readonly object _sync = new();

    private long _thresholdTicks;
    private long _hysteresisTicks;
    private long _activityOverrideTick;
    private int _isIdle;

    private PeriodicTimer? _timer;
    private Task? _loop;
    private CancellationTokenSource? _cts;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Creates the detector. <paramref name="threshold"/> is the inactivity that counts as idle;
    /// <paramref name="pollInterval"/> defaults to 1 second; <paramref name="hysteresis"/> is how far idle must
    /// fall to count as active again (defaults to the smaller of 1 second and the threshold).
    /// </summary>
    public IdleDetector(TimeSpan threshold, TimeSpan? pollInterval = null, TimeSpan? hysteresis = null, ILogger? logger = null)
    {
        _thresholdTicks = Math.Max(0, threshold.Ticks);
        _hysteresisTicks = Math.Clamp((hysteresis ?? TimeSpan.FromSeconds(1)).Ticks, 0, _thresholdTicks);
        _pollInterval = pollInterval is { } p && p > TimeSpan.Zero ? p : TimeSpan.FromSeconds(1);
        _logger = logger;
        _activityOverrideTick = Environment.TickCount64;
    }

    /// <summary>Raised when the idle state flips; the argument is <see langword="true"/> when the session became idle.</summary>
    public event EventHandler<bool>? IdleStateChanged;

    /// <summary>The inactivity threshold. Updating it re-clamps the hysteresis to not exceed the new value.</summary>
    public TimeSpan IdleThreshold
    {
        get => TimeSpan.FromTicks(Volatile.Read(ref _thresholdTicks));
        set
        {
            long ticks = Math.Max(0, value.Ticks);
            Volatile.Write(ref _thresholdTicks, ticks);
            if (Volatile.Read(ref _hysteresisTicks) > ticks)
            {
                Volatile.Write(ref _hysteresisTicks, ticks);
            }
        }
    }

    /// <summary>Current inactivity, i.e. the shorter of the OS idle time and the time since <see cref="NotifyActivity"/>.</summary>
    public TimeSpan Idle => CurrentIdle();

    /// <summary>Whether the session is currently considered idle.</summary>
    public bool IsIdle => Volatile.Read(ref _isIdle) == 1;

    /// <summary>Records external activity now, resetting the idle clock on the next evaluation.</summary>
    public void NotifyActivity() => Volatile.Write(ref _activityOverrideTick, Environment.TickCount64);

    /// <summary>Starts polling. Idempotent.</summary>
    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
            _cts = new CancellationTokenSource();
            _timer = new PeriodicTimer(_pollInterval);
            _loop = Task.Run(() => RunAsync(_timer, _cts.Token));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;
        PeriodicTimer? timer;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            loop = _loop;
            cts = _cts;
            timer = _timer;
            _loop = null;
            _cts = null;
            _timer = null;
        }

        GC.SuppressFinalize(this);

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed elsewhere.
        }

        timer?.Dispose();

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        cts?.Dispose();
    }

    private async Task RunAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Evaluate(CurrentIdle());
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    private TimeSpan CurrentIdle()
    {
        long overrideMs = Environment.TickCount64 - Volatile.Read(ref _activityOverrideTick);
        TimeSpan idle = TimeSpan.FromMilliseconds(overrideMs < 0 ? 0 : overrideMs);

        if (User32.TryGetIdleMilliseconds(out ulong baseMs))
        {
            TimeSpan baseIdle = TimeSpan.FromMilliseconds(baseMs);
            if (baseIdle < idle)
            {
                idle = baseIdle;
            }
        }

        return idle;
    }

    private void Evaluate(TimeSpan idle)
    {
        long idleTicks = idle.Ticks;
        long threshold = Volatile.Read(ref _thresholdTicks);
        long hysteresis = Volatile.Read(ref _hysteresisTicks);
        bool currentlyIdle = Volatile.Read(ref _isIdle) == 1;

        if (!currentlyIdle && idleTicks >= threshold)
        {
            Volatile.Write(ref _isIdle, 1);
            Raise(true);
        }
        else if (currentlyIdle && idleTicks < hysteresis)
        {
            Volatile.Write(ref _isIdle, 0);
            Raise(false);
        }
    }

    private void Raise(bool isIdle)
    {
        try
        {
            IdleStateChanged?.Invoke(this, isIdle);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "An IdleStateChanged handler threw");
        }
    }
}
