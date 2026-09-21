using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using ClubShell.Core.Abstractions;
using ClubShell.Windows.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Windows.Hardware;

/// <summary>One sensor snapshot; every value is <see langword="null"/> when the source cannot provide it.</summary>
/// <param name="CpuTemperature">CPU / package temperature in °C.</param>
/// <param name="GpuTemperature">GPU temperature in °C.</param>
/// <param name="GpuUtilization">GPU utilisation 0–100 (from the vendor tool, used when no GPU perf counter exists).</param>
public sealed record SensorReading(double? CpuTemperature, double? GpuTemperature, double? GpuUtilization)
{
    /// <summary>Reading with no values.</summary>
    public static SensorReading Empty { get; } = new(null, null, null);

    /// <summary>Fills the missing values of this reading from <paramref name="other"/>.</summary>
    public SensorReading Merge(SensorReading other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new SensorReading(CpuTemperature ?? other.CpuTemperature, GpuTemperature ?? other.GpuTemperature, GpuUtilization ?? other.GpuUtilization);
    }
}

/// <summary>A temperature / utilisation source. Implementations never throw: unavailable data is <see cref="SensorReading.Empty"/>.</summary>
public interface ISensorProvider
{
    /// <summary>Short provider name for diagnostics.</summary>
    string Name { get; }

    /// <summary>Reads the current values.</summary>
    Task<SensorReading> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// CPU temperature from <c>MSAcpi_ThermalZoneTemperature</c> (<c>root\wmi</c>, deci-Kelvin). Many consumer boards
/// expose no ACPI zone or a constant value; then the reading is empty. Requires administrator (the Agent is LocalSystem).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AcpiThermalZoneProvider : ISensorProvider
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);
    private readonly WmiQueries _wmi;

    /// <summary>Creates the provider.</summary>
    public AcpiThermalZoneProvider(WmiQueries wmi)
    {
        ArgumentNullException.ThrowIfNull(wmi);
        _wmi = wmi;
    }

    /// <inheritdoc />
    public string Name => "acpi";

    /// <inheritdoc />
    public async Task<SensorReading> ReadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _wmi.QueryAsync(WmiQueries.WmiRoot, "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature", QueryTimeout, cancellationToken).ConfigureAwait(false);
        double? hottest = null;
        foreach (IReadOnlyDictionary<string, object?> row in rows)
        {
            long deciKelvin = row.GetInt64("CurrentTemperature");
            if (deciKelvin <= 0)
            {
                continue;
            }

            double celsius = (deciKelvin / 10.0) - 273.15;
            if (celsius is > 0 and < 150 && (hottest is null || celsius > hottest))
            {
                hottest = Math.Round(celsius, 1);
            }
        }

        return new SensorReading(hottest, null, null);
    }
}

/// <summary>
/// GPU temperature and utilisation from <c>nvidia-smi</c> when an NVIDIA driver is installed. AMD (ADL/ADLX) is not
/// implemented: on AMD-only machines the GPU temperature stays <see langword="null"/> and utilisation comes from the
/// GPU Engine performance counters only.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NvidiaSmiProvider : ISensorProvider
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(2);
    private static readonly string[] Arguments = { "--query-gpu=temperature.gpu,utilization.gpu", "--format=csv,noheader,nounits" };

    private readonly ILogger<NvidiaSmiProvider> _logger;
    private readonly string? _executable;

    /// <summary>Creates the provider; <paramref name="executablePath"/> overrides the default search (System32, then the NVSMI folder under Program Files).</summary>
    public NvidiaSmiProvider(ILogger<NvidiaSmiProvider>? logger = null, string? executablePath = null)
    {
        _logger = logger ?? NullLogger<NvidiaSmiProvider>.Instance;
        _executable = executablePath is not null ? (File.Exists(executablePath) ? executablePath : null) : Locate();
    }

    /// <inheritdoc />
    public string Name => "nvidia-smi";

    /// <summary><see langword="true"/> when nvidia-smi.exe was found at construction time.</summary>
    public bool Available => _executable is not null;

    /// <inheritdoc />
    public async Task<SensorReading> ReadAsync(CancellationToken cancellationToken)
    {
        if (_executable is null)
        {
            return SensorReading.Empty;
        }

        try
        {
            ProcessResult result = await ProcessRunner.RunAsync(_executable, Arguments, RunTimeout, cancellationToken).ConfigureAwait(false);
            return result.Success ? Parse(result.StandardOutput) : SensorReading.Empty;
        }
        catch (Win32Exception ex)
        {
            _logger.LogDebug(ex, "nvidia-smi could not be started");
        }
        catch (TimeoutException ex)
        {
            _logger.LogDebug(ex, "nvidia-smi timed out");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "nvidia-smi failed");
        }

        return SensorReading.Empty;
    }

    /// <summary>Parses the first line of <c>--query-gpu=temperature.gpu,utilization.gpu --format=csv,noheader,nounits</c> output (e.g. <c>65, 12</c>).</summary>
    public static SensorReading Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (string line in output.Split('\n'))
        {
            string[] parts = line.Split(',');
            if (parts.Length < 2)
            {
                continue;
            }

            double? temperature = double.TryParse(parts[0].AsSpan().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double t) && t > 0 ? t : null;
            double? utilization = double.TryParse(parts[1].AsSpan().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double u) ? Math.Clamp(u, 0, 100) : null;
            if (temperature is not null || utilization is not null)
            {
                return new SensorReading(null, temperature, utilization);
            }
        }

        return SensorReading.Empty;
    }

    private static string? Locate()
    {
        string[] candidates =
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        };
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>
/// Composite sensor: queries every provider concurrently, merges the first non-null value per field in provider order
/// and caches the result for <see cref="CacheTtl"/> (default 2 s) so metric sampling and the UI overlay share one read.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SensorHub : ISensorProvider, IDisposable
{
    private readonly ISensorProvider[] _providers;
    private readonly IClock _clock;
    private readonly ILogger<SensorHub> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SensorReading _last = SensorReading.Empty;
    private long _lastTimestamp;
    private bool _hasLast;

    /// <summary>Creates the hub over <paramref name="providers"/>.</summary>
    public SensorHub(IEnumerable<ISensorProvider> providers, IClock? clock = null, ILogger<SensorHub>? logger = null, TimeSpan? cacheTtl = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToArray();
        _clock = clock ?? SystemClock.Instance;
        _logger = logger ?? NullLogger<SensorHub>.Instance;
        CacheTtl = cacheTtl ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>Default composition: ACPI thermal zones plus nvidia-smi.</summary>
    public static SensorHub CreateDefault(WmiQueries wmi, IClock? clock = null, ILogger<SensorHub>? logger = null) =>
        new(new ISensorProvider[] { new AcpiThermalZoneProvider(wmi), new NvidiaSmiProvider() }, clock, logger);

    /// <inheritdoc />
    public string Name => "hub";

    /// <summary>How long a reading is reused.</summary>
    public TimeSpan CacheTtl { get; }

    /// <summary>Providers in merge priority order.</summary>
    public IReadOnlyList<ISensorProvider> Providers => _providers;

    /// <inheritdoc />
    public async Task<SensorReading> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_hasLast && _clock.GetElapsedTime(_lastTimestamp) < CacheTtl)
            {
                return _last;
            }

            var tasks = new Task<SensorReading>[_providers.Length];
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = ReadOneAsync(_providers[i], cancellationToken);
            }

            SensorReading[] readings = await Task.WhenAll(tasks).ConfigureAwait(false);
            SensorReading merged = SensorReading.Empty;
            foreach (SensorReading reading in readings)
            {
                merged = merged.Merge(reading);
            }

            _last = merged;
            _lastTimestamp = _clock.GetTimestamp();
            _hasLast = true;
            return merged;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<SensorReading> ReadOneAsync(ISensorProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            return await provider.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Providers are external (WMI, vendor tools); one failing source must not blank the whole sample.
            _logger.LogDebug(ex, "Sensor provider {Provider} failed", provider.Name);
            return SensorReading.Empty;
        }
    }
}
