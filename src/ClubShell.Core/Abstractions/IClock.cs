namespace ClubShell.Core.Abstractions;

/// <summary>
/// Time source injected everywhere instead of <see cref="DateTimeOffset.UtcNow"/> so that timers, token expiry,
/// signatures and update windows can be driven by a fake <see cref="TimeProvider"/> in tests. Wall clock is used for
/// display and server exchange only; elapsed time must come from <see cref="TimeProvider.GetTimestamp"/>
/// (ARCHITECTURE.md §5.1, §7 "Clock jump").
/// </summary>
public interface IClock
{
    /// <summary>Current UTC wall-clock time.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Current local wall-clock time (club local time for tariff windows and apply windows).</summary>
    DateTimeOffset LocalNow { get; }

    /// <summary>Underlying provider, for monotonic timestamps and cancellable delays.</summary>
    TimeProvider Provider { get; }
}

/// <summary>
/// <see cref="IClock"/> over a <see cref="TimeProvider"/>; defaults to <see cref="TimeProvider.System"/>.
/// Pass a fake provider (e.g. <c>FakeTimeProvider</c>) in tests.
/// </summary>
public sealed class SystemClock : IClock
{
    /// <summary>Shared system-clock instance.</summary>
    public static SystemClock Instance { get; } = new();

    /// <summary>Creates a clock over <see cref="TimeProvider.System"/>.</summary>
    public SystemClock()
        : this(TimeProvider.System)
    {
    }

    /// <summary>Creates a clock over <paramref name="provider"/>.</summary>
    public SystemClock(TimeProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Provider = provider;
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow => Provider.GetUtcNow();

    /// <inheritdoc />
    public DateTimeOffset LocalNow => Provider.GetLocalNow();

    /// <inheritdoc />
    public TimeProvider Provider { get; }
}

/// <summary>Convenience helpers over <see cref="IClock"/>.</summary>
public static class ClockExtensions
{
    /// <summary>Monotonic timestamp (see <see cref="TimeProvider.GetTimestamp"/>).</summary>
    public static long GetTimestamp(this IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return clock.Provider.GetTimestamp();
    }

    /// <summary>Elapsed time since <paramref name="startingTimestamp"/> (monotonic, immune to wall-clock jumps).</summary>
    public static TimeSpan GetElapsedTime(this IClock clock, long startingTimestamp)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return clock.Provider.GetElapsedTime(startingTimestamp);
    }

    /// <summary>Cancellable delay driven by the clock's provider (fakeable in tests).</summary>
    public static Task Delay(this IClock clock, TimeSpan delay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return Task.Delay(delay, clock.Provider, cancellationToken);
    }
}
