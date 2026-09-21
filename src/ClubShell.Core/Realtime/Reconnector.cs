using System.Security.Cryptography;
using ClubShell.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Core.Realtime;

/// <summary>State of a supervised connection.</summary>
public enum ConnectionState
{
    /// <summary>Not connected and not trying (initial, paused by connectivity hint, or stopped).</summary>
    Disconnected,

    /// <summary>A connection attempt is running.</summary>
    Connecting,

    /// <summary>Connected (the delegate called <see cref="Reconnector.MarkConnected"/>).</summary>
    Connected,

    /// <summary>Waiting for the backoff delay before the next attempt.</summary>
    Backoff,
}

/// <summary>Backoff parameters of <see cref="Reconnector"/>.</summary>
public sealed class ReconnectorOptions
{
    /// <summary>First delay (default 1 s).</summary>
    public TimeSpan MinDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Delay cap (default 60 s).</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Exponential factor per failed attempt (default 2).</summary>
    public double Multiplier { get; set; } = 2.0;
}

/// <summary>
/// Generic reconnect supervisor (ARCHITECTURE.md §5.1 "WebSocket"): runs a connect-and-pump delegate in a loop,
/// backing off exponentially (1 s → 60 s, full jitter) after failures, resetting after a successful connection,
/// reconnecting immediately on <see cref="TriggerReconnect"/>, and pausing while the connectivity hint is offline.
/// Attempts never stop until the token is cancelled.
/// </summary>
public sealed class Reconnector : IDisposable
{
    private readonly IClock _clock;
    private readonly ILogger<Reconnector> _logger;
    private readonly ReconnectorOptions _options;
    private readonly SemaphoreSlim _wake = new(0);
    private CancellationTokenSource? _runCts;
    private int _state;
    private int _attempt;
    private int _online = 1;
    private volatile bool _connectedThisRun;
    private volatile bool _disposed;

    /// <summary>Creates a supervisor.</summary>
    public Reconnector(IClock clock, ILogger<Reconnector>? logger = null, ReconnectorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        _logger = logger ?? NullLogger<Reconnector>.Instance;
        _options = options ?? new ReconnectorOptions();
    }

    /// <summary>Raised on every state transition.</summary>
    public event EventHandler<ConnectionState>? StateChanged;

    /// <summary>Current state.</summary>
    public ConnectionState State => (ConnectionState)Volatile.Read(ref _state);

    /// <summary>Consecutive failed attempts since the last successful connection.</summary>
    public int Attempt => Volatile.Read(ref _attempt);

    /// <summary><see langword="false"/> while paused by <see cref="SetConnectivityHint"/>.</summary>
    public bool IsOnlineHint => Volatile.Read(ref _online) == 1;

    /// <summary>Delay chosen for the current/last backoff.</summary>
    public TimeSpan LastDelay { get; private set; }

    /// <summary>Backoff options.</summary>
    public ReconnectorOptions Options => _options;

    /// <summary>
    /// Delay for failed attempt number <paramref name="attempt"/> (1-based): uniform random between
    /// <paramref name="min"/> and <c>min(max, min · multiplier^(attempt−1))</c> (full jitter with a floor).
    /// </summary>
    public static TimeSpan ComputeDelay(int attempt, TimeSpan min, TimeSpan max, double multiplier)
    {
        if (min <= TimeSpan.Zero)
        {
            min = TimeSpan.FromMilliseconds(1);
        }

        if (max < min)
        {
            max = min;
        }

        var exponent = Math.Max(0, attempt - 1);
        var capMs = Math.Min(max.TotalMilliseconds, min.TotalMilliseconds * Math.Pow(Math.Max(1.0, multiplier), exponent));
        var floorMs = (int)Math.Min(int.MaxValue, min.TotalMilliseconds);
        var ceilMs = (int)Math.Min(int.MaxValue, Math.Max(capMs, floorMs));
        var chosen = ceilMs > floorMs ? RandomNumberGenerator.GetInt32(floorMs, ceilMs + 1) : floorMs;
        return TimeSpan.FromMilliseconds(chosen);
    }

    /// <summary>Called by the connect delegate once the connection is established; resets the attempt counter.</summary>
    public void MarkConnected()
    {
        _connectedThisRun = true;
        Interlocked.Exchange(ref _attempt, 0);
        SetState(ConnectionState.Connected);
    }

    /// <summary>Aborts the current attempt/connection and reconnects immediately (e.g. after a token refresh or a config change).</summary>
    public void TriggerReconnect()
    {
        CancelRun();
        Wake();
    }

    /// <summary>Pauses (<see langword="false"/>) or resumes (<see langword="true"/>) connection attempts; pausing aborts the current connection.</summary>
    public void SetConnectivityHint(bool online)
    {
        var previous = Interlocked.Exchange(ref _online, online ? 1 : 0);
        if (previous == (online ? 1 : 0))
        {
            return;
        }

        _logger.LogInformation("Connectivity hint: {Online}", online ? "online" : "offline");
        if (!online)
        {
            CancelRun();
        }

        Wake();
    }

    /// <summary>
    /// Runs <paramref name="connectAndRun"/> until <paramref name="cancellationToken"/> is cancelled. The delegate
    /// should connect, call <see cref="MarkConnected"/>, pump until the connection drops, then return or throw.
    /// </summary>
    public async Task RunAsync(Func<CancellationToken, Task> connectAndRun, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectAndRun);
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!IsOnlineHint)
                {
                    SetState(ConnectionState.Disconnected);
                    await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                SetState(ConnectionState.Connecting);
                _connectedThisRun = false;
                var manual = false;
                var startedAt = _clock.GetTimestamp();
                using (var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    Volatile.Write(ref _runCts, runCts);
                    try
                    {
                        await connectAndRun(runCts.Token).ConfigureAwait(false);
                        _logger.LogInformation("Connection closed after {Elapsed}", _clock.GetElapsedTime(startedAt));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        manual = true;
                        _logger.LogInformation("Connection attempt aborted (reconnect requested or paused)");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Connection attempt {Attempt} failed: {Message}", Attempt + 1, ex.Message);
                    }
                    finally
                    {
                        Volatile.Write(ref _runCts, null);
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await DrainWakeAsync(cancellationToken).ConfigureAwait(false);
                if (manual || !IsOnlineHint)
                {
                    continue;
                }

                if (_connectedThisRun)
                {
                    Interlocked.Exchange(ref _attempt, 0);
                }

                var attempt = Interlocked.Increment(ref _attempt);
                var delay = ComputeDelay(attempt, _options.MinDelay, _options.MaxDelay, _options.Multiplier);
                LastDelay = delay;
                SetState(ConnectionState.Backoff);
                _logger.LogDebug("Reconnecting in {Delay} (attempt {Attempt})", delay, attempt);
                await _wake.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            SetState(ConnectionState.Disconnected);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Volatile.Read(ref _runCts)?.Dispose();
        _wake.Dispose();
        GC.SuppressFinalize(this);
    }

    private void CancelRun()
    {
        var cts = Volatile.Read(ref _runCts);
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished concurrently; the loop will pick up the wake signal.
        }
    }

    private void Wake()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _wake.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently; nothing is waiting.
        }
    }

    private async Task DrainWakeAsync(CancellationToken cancellationToken)
    {
        while (_wake.CurrentCount > 0 && await _wake.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // Consume stale wake signals so a past TriggerReconnect does not cut the next backoff short.
        }
    }

    private void SetState(ConnectionState state)
    {
        var previous = (ConnectionState)Interlocked.Exchange(ref _state, (int)state);
        if (previous == state)
        {
            return;
        }

        try
        {
            StateChanged?.Invoke(this, state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StateChanged listener threw");
        }
    }
}
