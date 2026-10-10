using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Runtime.Versioning;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Pcs;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Logging;
using ClubShell.Core.Realtime;
using ClubShell.Windows.Hardware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Server;

/// <summary>
/// In-process sink for Agent diagnostic events (<see cref="TelemetryEventKinds"/>). Any component publishes to it; the
/// <see cref="TelemetryReporter"/> drains it and batches the events into <c>POST /agents/{pcId}/telemetry</c>. Backed by
/// a bounded channel that drops the oldest event when full so a slow uploader never blocks a caller.
/// </summary>
public sealed class TelemetryBus
{
    private static readonly JsonSerializerOptions DataOptions = new(JsonSerializerDefaults.Web);

    private readonly Channel<TelemetryEvent> _channel = Channel.CreateBounded<TelemetryEvent>(
        new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly IClock _clock;

    /// <summary>Creates the bus.</summary>
    public TelemetryBus(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>Reader drained by <see cref="TelemetryReporter"/>.</summary>
    public ChannelReader<TelemetryEvent> Reader => _channel.Reader;

    /// <summary>Publishes an event with an already-built payload.</summary>
    public bool Publish(TelemetryEvent telemetryEvent)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);
        return _channel.Writer.TryWrite(telemetryEvent);
    }

    /// <summary>Publishes an event with a JSON payload.</summary>
    public bool Publish(string kind, JsonElement data)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        return _channel.Writer.TryWrite(new TelemetryEvent(kind, _clock.UtcNow, data));
    }

    /// <summary>Publishes an event, serializing <paramref name="data"/> (an anonymous object is fine).</summary>
    public bool Publish(string kind, object? data)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        JsonElement element;
        try
        {
            element = JsonSerializer.SerializeToElement(data, DataOptions);
        }
        catch (NotSupportedException)
        {
            element = JsonSerializer.SerializeToElement<object?>(null, DataOptions);
        }

        return _channel.Writer.TryWrite(new TelemetryEvent(kind, _clock.UtcNow, element));
    }
}

/// <summary>Pushes sampled <see cref="PcMetrics"/> to the Shell (<c>sys.metrics</c> overlay).</summary>
public interface ISysMetricsSink
{
    /// <summary>Publishes one metrics sample.</summary>
    ValueTask PublishAsync(PcMetrics metrics, CancellationToken cancellationToken);
}

/// <summary>
/// Samples <see cref="PcMetrics"/> every <c>telemetry.metricsIntervalSec</c> into a ring buffer, pushes each sample to
/// the Shell overlay via <see cref="ISysMetricsSink"/>, and uploads a <see cref="TelemetryBatch"/> to
/// <c>POST /agents/{pcId}/telemetry</c> every <c>telemetry.uploadIntervalSec</c> (or sooner when 50 diagnostic events
/// have queued). The full hardware inventory taken at start goes with the first batch; a rescan every
/// <c>telemetry.hardwareRescanSec</c> attaches the inventory to the next batch and emits an
/// <see cref="AgentEventType.HardwareChanged"/> event only when it changed (<see cref="TelemetryHardware"/>). A batch
/// carries at most <see cref="MaxBatchEvents"/> events (the rest go in the next ones). A batch the server rejects with a 4xx other than
/// 401/408/429 is dropped; after any other failure the upload backs off exponentially (upload interval → 15 min) and
/// the oldest samples/events are dropped past their caps.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TelemetryReporter : BackgroundService
{
    /// <summary>Events per <see cref="TelemetryBatch"/>.</summary>
    public const int MaxBatchEvents = 100;

    private const int MaxPendingEvents = 1024;
    private const int LogTailBytes = 128 * 1024;
    private static readonly TimeSpan MaxUploadBackoff = TimeSpan.FromMinutes(15);

    private readonly ServerConnection _connection;
    private readonly IServerClient _server;
    private readonly PerformanceCounters _counters;
    private readonly HardwareInventory _hardware;
    private readonly RealtimeClient _realtime;
    private readonly TelemetryBus _bus;
    private readonly ISysMetricsSink _metricsSink;
    private readonly IClock _clock;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly ILogger<TelemetryReporter> _logger;

    private readonly Queue<PcMetrics> _samples = new();
    private readonly List<TelemetryEvent> _pendingEvents = new();
    private TelemetryHardware _inventory = new();
    private int _logTailRequested;
    private int _uploadFailures;
    private DateTimeOffset _nextUploadAt;

    /// <summary>Creates the reporter.</summary>
    public TelemetryReporter(
        ServerConnection connection,
        IServerClient server,
        PerformanceCounters counters,
        HardwareInventory hardware,
        RealtimeClient realtime,
        TelemetryBus bus,
        ISysMetricsSink metricsSink,
        IClock clock,
        IOptionsMonitor<AgentSettings> settings,
        ILogger<TelemetryReporter> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(realtime);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(metricsSink);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _server = server;
        _counters = counters;
        _hardware = hardware;
        _realtime = realtime;
        _bus = bus;
        _metricsSink = metricsSink;
        _clock = clock;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Requests that the next telemetry batch carry the tail of the current log file.</summary>
    public void RequestLogTail() => Interlocked.Exchange(ref _logTailRequested, 1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _counters.Warmup();
        _inventory = new TelemetryHardware(_hardware.Current);
        var lastUpload = _clock.UtcNow;
        var lastRescan = _clock.UtcNow;

        // The full inventory right after start, so the first batch after a boot or restart carries it rather than the
        // first rescan up to telemetry.hardwareRescanSec (1 h) later. Taken after yielding: the scan (WMI, a few
        // seconds) never holds up the host's start.
        await Task.Yield();
        if (_settings.CurrentValue.Telemetry.Enabled)
        {
            await TakeInventoryAtStartAsync(stoppingToken).ConfigureAwait(false);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = _settings.CurrentValue.Telemetry;
            var metricsInterval = TimeSpan.FromSeconds(Math.Max(1, settings.MetricsIntervalSec));

            try
            {
                await Task.Delay(metricsInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!settings.Enabled)
            {
                DrainBus();
                continue;
            }

            await SampleAsync(stoppingToken).ConfigureAwait(false);
            DrainBus();

            var now = _clock.UtcNow;
            if (now - lastRescan >= TimeSpan.FromSeconds(Math.Max(60, settings.HardwareRescanSec)))
            {
                lastRescan = now;
                await RescanHardwareAsync(now, stoppingToken).ConfigureAwait(false);
            }

            var uploadInterval = TimeSpan.FromSeconds(Math.Max(5, settings.UploadIntervalSec));
            var uploadDue = now - lastUpload >= uploadInterval;
            if ((uploadDue || _pendingEvents.Count >= TelemetryBatchTrigger || _samples.Count >= TelemetryBatch.MaxSamples) && now >= _nextUploadAt)
            {
                if (await UploadAsync(stoppingToken).ConfigureAwait(false))
                {
                    lastUpload = now;
                    _uploadFailures = 0;
                }
                else
                {
                    _uploadFailures++;
                    _nextUploadAt = now + Reconnector.ComputeDelay(_uploadFailures, uploadInterval, MaxUploadBackoff, 2.0);
                }
            }
        }
    }

    private const int TelemetryBatchTrigger = 50;

    /// <summary>
    /// <see langword="true"/> when the server rejected the batch itself (a 4xx other than 401/408/429): sending it again
    /// cannot succeed, so it is dropped instead of blocking every later upload.
    /// </summary>
    public static bool IsRejectedBatch(ServerApiException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return (int)ex.Status is >= 400 and < 500 and not (401 or 408 or 429);
    }

    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var sample = await _counters.SampleAsync(cancellationToken).ConfigureAwait(false);
            _samples.Enqueue(sample);
            while (_samples.Count > TelemetryBatch.MaxSamples)
            {
                _samples.Dequeue();
            }

            try
            {
                await _metricsSink.PublishAsync(sample, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Publishing metrics to the Shell failed");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Sampling performance counters failed");
        }
    }

    private void DrainBus()
    {
        while (_bus.Reader.TryRead(out var telemetryEvent))
        {
            _pendingEvents.Add(telemetryEvent);
        }

        if (_pendingEvents.Count > MaxPendingEvents)
        {
            _pendingEvents.RemoveRange(0, _pendingEvents.Count - MaxPendingEvents);
        }
    }

    private async Task TakeInventoryAtStartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // GetAsync: a scan that registration has just made is reused rather than repeated.
            _inventory.Started(await _hardware.GetAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Hardware inventory at start failed; it goes with the first rescan");
        }
    }

    private async Task RescanHardwareAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            var current = await _hardware.RefreshAsync(cancellationToken).ConfigureAwait(false);
            var diff = _inventory.Rescanned(current);
            if (diff.Count > 0)
            {
                _logger.LogInformation("Hardware changed: {Sections}", string.Join(", ", diff));
                _realtime.TrySendEvent(AgentEvent.Of(AgentEventType.HardwareChanged, now, new HardwareChangedEvent(current, diff)));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Hardware rescan failed");
        }
    }

    private async Task<bool> UploadAsync(CancellationToken cancellationToken)
    {
        if (_connection.PcId is not { } pcId)
        {
            return false;
        }

        if (_samples.Count == 0 && _pendingEvents.Count == 0 && _inventory.Pending is null)
        {
            return true;
        }

        var events = _pendingEvents.Take(MaxBatchEvents).ToArray();
        var samples = _samples.ToArray();
        var includeLogs = events.Length > 0 || Interlocked.Exchange(ref _logTailRequested, 0) == 1;
        var logs = includeLogs ? ReadLogTail() : null;
        var batch = new TelemetryBatch(samples, events, _inventory.Pending, logs);

        try
        {
            await _server.SendTelemetryAsync(pcId, batch, cancellationToken).ConfigureAwait(false);
        }
        catch (ServerApiException ex) when (IsRejectedBatch(ex))
        {
            _logger.LogWarning("Telemetry batch rejected ({Code}, HTTP {Status}); {Samples} samples / {Events} events dropped", ex.Code, (int)ex.Status, samples.Length, events.Length);
        }
        catch (ServerApiException ex)
        {
            _logger.LogWarning("Telemetry upload failed ({Code}); {Samples} samples / {Events} events retained", ex.Code, samples.Length, events.Length);
            TrimForOffline();
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Telemetry upload failed unexpectedly");
            TrimForOffline();
            return false;
        }

        _samples.Clear();
        _pendingEvents.RemoveRange(0, events.Length);
        _inventory.Delivered();
        return true;
    }

    private void TrimForOffline()
    {
        while (_samples.Count > TelemetryBatch.MaxSamples)
        {
            _samples.Dequeue();
        }

        if (_pendingEvents.Count > MaxPendingEvents)
        {
            _pendingEvents.RemoveRange(0, _pendingEvents.Count - MaxPendingEvents);
        }
    }

    private string[]? ReadLogTail()
    {
        try
        {
            var directory = _settings.CurrentValue.LogsDir;
            if (!Directory.Exists(directory))
            {
                return null;
            }

            var pattern = LoggingSetup.AgentLogFilePrefix + "*" + LoggingSetup.LogFileExtension;
            var newest = new DirectoryInfo(directory)
                .GetFiles(pattern)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null)
            {
                return null;
            }

            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = Math.Max(0, stream.Length - LogTailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0)
            {
                return null;
            }

            var take = Math.Min(lines.Length, TelemetryBatch.MaxLogLines);
            return lines[^take..];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Reading the log tail failed");
            return null;
        }
    }
}

/// <summary>
/// Which hardware inventory goes with the telemetry batches: the full one taken at start, whatever was seen before (so
/// every Agent start sends one), then a rescan only when it differs from the last inventory seen, the baseline. A
/// rescan before any baseline is sent in full.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TelemetryHardware
{
    /// <summary>Creates the state.</summary>
    /// <param name="baseline">An inventory already scanned (<see cref="HardwareInventory.Current"/>), when any.</param>
    public TelemetryHardware(HardwareInfo? baseline = null) => Baseline = baseline;

    /// <summary>Last inventory seen; rescans are compared with it.</summary>
    public HardwareInfo? Baseline { get; private set; }

    /// <summary>Inventory the next batch carries, or <see langword="null"/>.</summary>
    public HardwareInfo? Pending { get; private set; }

    /// <summary>The inventory taken at start: the next batch carries it, and it becomes the baseline.</summary>
    public void Started(HardwareInfo current)
    {
        ArgumentNullException.ThrowIfNull(current);
        Baseline = current;
        Pending = current;
    }

    /// <summary>
    /// A periodic rescan, which becomes the baseline. The next batch carries it when it differs from the previous
    /// baseline, or when there was none. Returns the changed sections (<see cref="HardwareInventory.Diff"/>); empty when
    /// nothing changed or there was no baseline to compare with.
    /// </summary>
    public IReadOnlyList<string> Rescanned(HardwareInfo current)
    {
        ArgumentNullException.ThrowIfNull(current);
        IReadOnlyList<string> diff = Array.Empty<string>();
        if (Baseline is { } previous)
        {
            diff = HardwareInventory.Diff(previous, current);
            if (diff.Count > 0)
            {
                Pending = current;
            }
        }
        else
        {
            // No baseline yet: attach the first full inventory so the server has it.
            Pending = current;
        }

        Baseline = current;
        return diff;
    }

    /// <summary>The batch carrying <see cref="Pending"/> was delivered (or rejected for good): nothing is pending any more.</summary>
    public void Delivered() => Pending = null;
}
