using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Security;
using ClubShell.Contracts.Pcs;
using ClubShell.Core.Abstractions;
using ClubShell.Windows.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace ClubShell.Windows.Hardware;

/// <summary>
/// Telemetry sampler producing <see cref="PcMetrics"/> (IPC_PROTOCOL.md §6.7): CPU from
/// <c>Processor Information(_Total)\% Processor Time</c>, GPU from the summed <c>GPU Engine(*engtype_3D)</c>
/// utilisation counters (absent on some drivers → falls back to the vendor sensor, else 0), RAM from
/// <c>GlobalMemoryStatusEx</c>, network throughput from the IP interface byte counters, disk queue from
/// <c>PhysicalDisk(_Total)</c>, temperatures from <see cref="SensorHub"/> and uptime from <c>GetTickCount64</c>.
/// Counter names are English in code and translated through the Perflib index (<c>PdhLookupPerfNameByIndex</c>),
/// which is what makes this work on Russian/Uzbek Windows where category names are localized. Every counter is
/// created lazily, warmed up with a first sample and isolated: a failing counter reports 0 and is recreated on the
/// next instance refresh. Not thread-safe by design; <see cref="SampleAsync"/> serialises through the blocking scheduler.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PerformanceCounters : IDisposable
{
    private const string PerflibEnglishKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib\009";
    private const string GpuEngineSuffix = "engtype_3D";
    private const int RefreshInstancesEvery = 12;

    private static readonly TimeSpan SampleTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly ILogger<PerformanceCounters> _logger;
    private readonly IClock _clock;
    private readonly SensorHub? _sensors;
    private readonly Dictionary<string, int> _perflibIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _localizedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PerformanceCounter> _gpuCounters = new();
    private readonly List<string> _gpuInstances = new();
    private PerformanceCounter? _cpu;
    private PerformanceCounter? _diskQueue;
    private bool _perflibLoaded;
    private bool _gpuCategoryMissing;
    private int _samples;
    private long _lastBytesIn;
    private long _lastBytesOut;
    private long _lastNetTimestamp;
    private bool _disposed;

    /// <summary>Creates the sampler.</summary>
    /// <param name="sensors">Temperature source; <see langword="null"/> reports 0 °C.</param>
    /// <param name="clock">Clock for the <see cref="PcMetrics.At"/> stamp.</param>
    /// <param name="logger">Logger.</param>
    public PerformanceCounters(SensorHub? sensors = null, IClock? clock = null, ILogger<PerformanceCounters>? logger = null)
    {
        _sensors = sensors;
        _clock = clock ?? SystemClock.Instance;
        _logger = logger ?? NullLogger<PerformanceCounters>.Instance;
    }

    /// <summary>Current disk queue length (<c>PhysicalDisk(_Total)\Current Disk Queue Length</c>) as of the last sample.</summary>
    public double DiskQueueLength { get; private set; }

    /// <summary><see langword="true"/> when GPU Engine counters were found on the last refresh.</summary>
    public bool GpuCountersAvailable
    {
        get
        {
            lock (_gate)
            {
                return _gpuCounters.Count > 0;
            }
        }
    }

    /// <summary>Creates every counter and takes the first (discarded) sample so the next one is meaningful. Blocks up to a few seconds on first use.</summary>
    public void Warmup()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RefreshCounters();
            _ = SampleNetworkMbps();
        }
    }

    /// <summary>Samples counters and sensors.</summary>
    public async Task<PcMetrics> SampleAsync(CancellationToken cancellationToken)
    {
        RawSample raw;
        try
        {
            raw = await WmiQueries.RunBlockingAsync(SampleCounters, SampleTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Performance counter sampling timed out; reporting zeros");
            raw = new RawSample(0, null, 0, 0);
        }

        SensorReading sensors = _sensors is null ? SensorReading.Empty : await _sensors.ReadAsync(cancellationToken).ConfigureAwait(false);
        return Build(raw, sensors);
    }

    /// <summary>Synchronous variant without sensors (temperatures 0, GPU from counters only).</summary>
    public PcMetrics Sample() => Build(SampleCounters(), SensorReading.Empty);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cpu?.Dispose();
            _cpu = null;
            _diskQueue?.Dispose();
            _diskQueue = null;
            DisposeGpuCounters();
        }

        GC.SuppressFinalize(this);
    }

    // ---- sampling ---------------------------------------------------------------------------

    private PcMetrics Build(RawSample raw, SensorReading sensors)
    {
        MEMORYSTATUSEX memory = MEMORYSTATUSEX.Create();
        int ramUsedMb = 0;
        if (Kernel32.GlobalMemoryStatusEx(ref memory))
        {
            ramUsedMb = (int)((memory.ullTotalPhys - memory.ullAvailPhys) / (1024UL * 1024UL));
        }

        double gpuPct = raw.GpuPct ?? sensors.GpuUtilization ?? 0;
        return new PcMetrics(
            Math.Round(raw.CpuPct, 1),
            Math.Round(Math.Clamp(gpuPct, 0, 100), 1),
            ramUsedMb,
            new Temperatures(sensors.CpuTemperature ?? 0, sensors.GpuTemperature ?? 0),
            null,
            new NetworkThroughput(Math.Round(raw.UpMbps, 2), Math.Round(raw.DownMbps, 2)),
            (long)(Kernel32.GetTickCount64() / 1000UL),
            _clock.UtcNow);
    }

    private RawSample SampleCounters()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_samples % RefreshInstancesEvery == 0)
            {
                RefreshCounters();
            }

            _samples++;
            double cpu = Math.Clamp(Read(ref _cpu, "cpu"), 0, 100);
            double? gpu = null;
            if (_gpuCounters.Count > 0)
            {
                gpu = Math.Min(100, SumAndPrune(_gpuCounters, _gpuInstances));
            }

            DiskQueueLength = Read(ref _diskQueue, "disk queue");
            (double up, double down) = SampleNetworkMbps();
            return new RawSample(cpu, gpu, up, down);
        }
    }

    private double Read(ref PerformanceCounter? counter, string what)
    {
        if (counter is null)
        {
            return 0;
        }

        try
        {
            return counter.NextValue();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "Counter {Counter} failed; it will be recreated", what);
        }
        catch (Win32Exception ex)
        {
            _logger.LogDebug(ex, "Counter {Counter} failed; it will be recreated", what);
        }

        counter.Dispose();
        counter = null;
        return 0;
    }

    private double SumAndPrune(List<PerformanceCounter> counters, List<string> instances)
    {
        double sum = 0;
        for (int i = counters.Count - 1; i >= 0; i--)
        {
            try
            {
                sum += counters[i].NextValue();
            }
            catch (InvalidOperationException)
            {
                // Instance vanished (process exited); drop it until the next refresh.
                counters[i].Dispose();
                counters.RemoveAt(i);
                instances.RemoveAt(i);
            }
            catch (Win32Exception)
            {
                counters[i].Dispose();
                counters.RemoveAt(i);
                instances.RemoveAt(i);
            }
        }

        return sum;
    }

    private (double UpMbps, double DownMbps) SampleNetworkMbps()
    {
        long bytesIn = 0;
        long bytesOut = 0;
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                IPInterfaceStatistics stats = adapter.GetIPStatistics();
                bytesIn += stats.BytesReceived;
                bytesOut += stats.BytesSent;
            }
        }
        catch (NetworkInformationException ex)
        {
            _logger.LogDebug(ex, "Network statistics unavailable");
            return (0, 0);
        }

        long now = _clock.GetTimestamp();
        double up = 0;
        double down = 0;
        if (_lastNetTimestamp != 0)
        {
            double seconds = _clock.GetElapsedTime(_lastNetTimestamp).TotalSeconds;
            if (seconds > 0.2 && bytesIn >= _lastBytesIn && bytesOut >= _lastBytesOut)
            {
                up = (bytesOut - _lastBytesOut) * 8.0 / seconds / 1_000_000.0;
                down = (bytesIn - _lastBytesIn) * 8.0 / seconds / 1_000_000.0;
            }
        }

        _lastBytesIn = bytesIn;
        _lastBytesOut = bytesOut;
        _lastNetTimestamp = now;
        return (up, down);
    }

    // ---- counter lifecycle --------------------------------------------------------------------

    private void RefreshCounters()
    {
        _cpu ??= TryCreate("Processor Information", "% Processor Time", "_Total") ?? TryCreate("Processor", "% Processor Time", "_Total");
        _diskQueue ??= TryCreate("PhysicalDisk", "Current Disk Queue Length", "_Total");
        RefreshGpuCounters();
    }

    private void RefreshGpuCounters()
    {
        if (_gpuCategoryMissing)
        {
            return;
        }

        List<string> current = Instances("GPU Engine");
        current.RemoveAll(static name => !name.EndsWith(GpuEngineSuffix, StringComparison.Ordinal));
        if (current.Count == 0 && _gpuCounters.Count == 0 && _samples == 0)
        {
            _gpuCategoryMissing = !CategoryExists("GPU Engine");
            if (_gpuCategoryMissing)
            {
                _logger.LogInformation("GPU Engine counters are not available; GPU utilisation will come from the vendor sensor when present");
            }

            return;
        }

        if (current.Count == _gpuInstances.Count && current.TrueForAll(_gpuInstances.Contains))
        {
            return;
        }

        DisposeGpuCounters();
        foreach (string instance in current)
        {
            PerformanceCounter? counter = TryCreate("GPU Engine", "Utilization Percentage", instance);
            if (counter is not null)
            {
                _gpuCounters.Add(counter);
                _gpuInstances.Add(instance);
            }
        }
    }

    private void DisposeGpuCounters()
    {
        foreach (PerformanceCounter counter in _gpuCounters)
        {
            counter.Dispose();
        }

        _gpuCounters.Clear();
        _gpuInstances.Clear();
    }

    private PerformanceCounter? TryCreate(string category, string counter, string instance)
    {
        PerformanceCounter? created = null;
        try
        {
            created = new PerformanceCounter(Localize(category), Localize(counter), instance, readOnly: true);
            _ = created.NextValue();
            return created;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "Counter {Category}\\{Counter}({Instance}) unavailable", category, counter, instance);
        }
        catch (Win32Exception ex)
        {
            _logger.LogDebug(ex, "Counter {Category}\\{Counter}({Instance}) unavailable", category, counter, instance);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Counter {Category}\\{Counter}({Instance}) unavailable", category, counter, instance);
        }

        created?.Dispose();
        return null;
    }

    private List<string> Instances(string category)
    {
        try
        {
            return new List<string>(new PerformanceCounterCategory(Localize(category)).GetInstanceNames());
        }
        catch (InvalidOperationException)
        {
            return new List<string>();
        }
        catch (Win32Exception)
        {
            return new List<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    private bool CategoryExists(string category)
    {
        try
        {
            return PerformanceCounterCategory.Exists(Localize(category));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---- name localization --------------------------------------------------------------------

    /// <summary>Maps an English counter/category name to the local-language name through the Perflib 009 index; returns the input when unknown.</summary>
    private string Localize(string english)
    {
        if (_localizedNames.TryGetValue(english, out string? cached))
        {
            return cached;
        }

        LoadPerflibIndex();
        string result = english;
        if (_perflibIndex.TryGetValue(english, out int index) && LookupPerfName(index) is { Length: > 0 } localized)
        {
            result = localized;
        }

        _localizedNames[english] = result;
        return result;
    }

    private void LoadPerflibIndex()
    {
        if (_perflibLoaded)
        {
            return;
        }

        _perflibLoaded = true;
        try
        {
            using RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(PerflibEnglishKey);
            if (key?.GetValue("Counter") is not string[] pairs)
            {
                return;
            }

            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                if (int.TryParse(pairs[i], NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                {
                    _ = _perflibIndex.TryAdd(pairs[i + 1], index);
                }
            }
        }
        catch (SecurityException ex)
        {
            _logger.LogDebug(ex, "Perflib index unreadable; using English counter names");
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Perflib index unreadable; using English counter names");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Perflib index unreadable; using English counter names");
        }
    }

    private static string? LookupPerfName(int index) => Pdh.LookupPerfNameByIndex((uint)index);

    private sealed record RawSample(double CpuPct, double? GpuPct, double UpMbps, double DownMbps);
}
