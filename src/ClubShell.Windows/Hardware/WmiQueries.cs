using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text.RegularExpressions;
using ClubShell.Contracts.Pcs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace ClubShell.Windows.Hardware;

/// <summary>Outcome of a WMI query: <see cref="Succeeded"/> is <see langword="false"/> when the class or namespace is missing, access was denied or the query timed out.</summary>
/// <param name="Succeeded">Whether the provider answered.</param>
/// <param name="Rows">Rows (empty on failure).</param>
public sealed record WmiQueryResult(bool Succeeded, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

/// <summary>Win32_BaseBoard.</summary>
public sealed record WmiBaseboard(string Manufacturer, string Product, string SerialNumber);

/// <summary>Win32_Processor (cores and threads summed over sockets).</summary>
public sealed record WmiCpu(string Name, int Cores, int Threads, string ProcessorId, int MaxClockMhz);

/// <summary>Win32_VideoController plus the registry VRAM size (Win32 caps <c>AdapterRAM</c> at 4 GiB).</summary>
public sealed record WmiGpu(string Name, long AdapterRamBytes, string DriverVersion, int CurrentWidth, int CurrentHeight, int CurrentRefreshHz);

/// <summary>Win32_OperatingSystem plus the update build revision from the registry.</summary>
public sealed record WmiOs(string Caption, string Version, string BuildNumber, int Ubr)
{
    /// <summary>Product name without the <c>Microsoft </c> prefix, e.g. <c>Windows 11 Pro</c>.</summary>
    public string ProductName => Caption.StartsWith("Microsoft ", StringComparison.Ordinal) ? Caption["Microsoft ".Length..] : Caption;

    /// <summary><c>BuildNumber.Ubr</c>, e.g. <c>26200.1234</c>.</summary>
    public string FullBuild => Ubr > 0 ? string.Create(CultureInfo.InvariantCulture, $"{BuildNumber}.{Ubr}") : BuildNumber;
}

/// <summary>Win32_DiskDrive.</summary>
public sealed record WmiDiskDrive(int Index, string Model, string SerialNumber, long SizeBytes, string InterfaceType, string MediaType);

/// <summary>MSFT_PhysicalDisk (<c>root\Microsoft\Windows\Storage</c>).</summary>
public sealed record WmiPhysicalDisk(int DeviceNumber, string FriendlyName, string SerialNumber, long SizeBytes, int MediaType, int BusType)
{
    /// <summary>Maps MSFT_PhysicalDisk media/bus type to <see cref="DiskType"/> (NVMe and iSCSI are recognised by bus type first).</summary>
    public DiskType ToDiskType() => BusType switch
    {
        17 => DiskType.Nvme,
        9 => DiskType.Network,
        _ => MediaType switch
        {
            3 => DiskType.Hdd,
            4 => DiskType.Ssd,
            5 => DiskType.Ssd,
            _ => DiskType.Unknown,
        },
    };
}

/// <summary>MSFT_Partition (<c>root\Microsoft\Windows\Storage</c>); <see cref="DriveLetter"/> is <c>'\0'</c> when unassigned.</summary>
public sealed record WmiPartition(int DiskNumber, char DriveLetter);

/// <summary>Win32_NetworkAdapterConfiguration with IP enabled.</summary>
public sealed record WmiNetworkAdapter(string Description, string Mac, IReadOnlyList<string> IpAddresses, IReadOnlyList<string> Gateways, int InterfaceIndex);

/// <summary>WmiMonitorID (<c>root\wmi</c>).</summary>
public sealed record WmiMonitor(string Manufacturer, string Model, string SerialNumber, bool Active);

/// <summary>Win32_PnPEntity for USB / HID devices; vendor and product ids are 4 upper-case hex digits (empty when the id carries none).</summary>
public sealed record WmiPnpDevice(string Name, string PnpClass, string DeviceId, string VendorId, string ProductId);

/// <summary>Typed accessors over a WMI row (property name → boxed value, case-insensitive).</summary>
public static class WmiRowExtensions
{
    /// <summary>Raw value or <see langword="null"/>.</summary>
    public static object? Get(this IReadOnlyDictionary<string, object?> row, string key)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.TryGetValue(key, out object? value) ? value : null;
    }

    /// <summary>Trimmed string (arrays are joined with commas); empty when missing.</summary>
    public static string GetString(this IReadOnlyDictionary<string, object?> row, string key)
    {
        object? value = row.Get(key);
        return value switch
        {
            null => string.Empty,
            string s => s.Trim(),
            string[] array => string.Join(',', array),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
        };
    }

    /// <summary>64-bit integer; <paramref name="fallback"/> when missing or not numeric.</summary>
    public static long GetInt64(this IReadOnlyDictionary<string, object?> row, string key, long fallback = 0)
    {
        object? value = row.Get(key);
        if (value is null)
        {
            return fallback;
        }

        try
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return fallback;
        }
        catch (InvalidCastException)
        {
            return fallback;
        }
        catch (OverflowException)
        {
            return fallback;
        }
    }

    /// <summary>32-bit integer (clamped); <paramref name="fallback"/> when missing or not numeric.</summary>
    public static int GetInt32(this IReadOnlyDictionary<string, object?> row, string key, int fallback = 0) =>
        (int)Math.Clamp(row.GetInt64(key, fallback), int.MinValue, int.MaxValue);

    /// <summary>Boolean; <paramref name="fallback"/> when missing.</summary>
    public static bool GetBoolean(this IReadOnlyDictionary<string, object?> row, string key, bool fallback = false)
    {
        object? value = row.Get(key);
        return value switch
        {
            bool b => b,
            null => fallback,
            string s when bool.TryParse(s, out bool parsed) => parsed,
            _ => row.GetInt64(key, fallback ? 1 : 0) != 0,
        };
    }

    /// <summary>String array (a scalar string becomes a single-element list); empty when missing.</summary>
    public static IReadOnlyList<string> GetStringArray(this IReadOnlyDictionary<string, object?> row, string key)
    {
        object? value = row.Get(key);
        switch (value)
        {
            case null:
                return Array.Empty<string>();
            case string[] strings:
                return strings;
            case string s:
                return new[] { s };
            case Array array:
                var list = new List<string>(array.Length);
                foreach (object? item in array)
                {
                    if (Convert.ToString(item, CultureInfo.InvariantCulture) is { Length: > 0 } text)
                    {
                        list.Add(text);
                    }
                }

                return list;
            default:
                return Array.Empty<string>();
        }
    }

    /// <summary>CIM <c>char16</c> value; <c>'\0'</c> when missing.</summary>
    public static char GetChar(this IReadOnlyDictionary<string, object?> row, string key)
    {
        object? value = row.Get(key);
        return value switch
        {
            char c => c,
            ushort u => (char)u,
            short s => (char)s,
            string { Length: > 0 } text => text[0],
            _ => '\0',
        };
    }

    /// <summary>Decodes a <c>uint16[]</c> WMI string (WmiMonitorID fields) up to the first NUL.</summary>
    public static string DecodeUInt16String(object? value)
    {
        if (value is not ushort[] codes)
        {
            return string.Empty;
        }

        int length = Array.IndexOf(codes, (ushort)0);
        if (length < 0)
        {
            length = codes.Length;
        }

        var chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = (char)codes[i];
        }

        return new string(chars).Trim();
    }
}

/// <summary>
/// WMI access with per-query timeouts and graceful degradation: a missing class or namespace, an access error or a
/// timeout yields an empty result (logged at Debug), never an exception. Blocking WMI calls run on
/// <see cref="BlockingScheduler"/> (at most two at a time, ARCHITECTURE.md §7) so they never occupy the thread pool's
/// hot path. A query that exceeds its timeout is abandoned on that scheduler and finishes in the background.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WmiQueries
{
    /// <summary><c>root\cimv2</c>.</summary>
    public const string CimV2 = @"root\cimv2";

    /// <summary><c>root\wmi</c> (ACPI thermal zones, monitor EDID).</summary>
    public const string WmiRoot = @"root\wmi";

    /// <summary><c>root\Microsoft\Windows\Storage</c> (Storage Management API classes).</summary>
    public const string StorageScope = @"root\Microsoft\Windows\Storage";

    /// <summary><c>root\cimv2\Security\MicrosoftTpm</c>.</summary>
    public const string TpmScope = @"root\cimv2\Security\MicrosoftTpm";

    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static readonly ConcurrentExclusiveSchedulerPair SchedulerPair = new(TaskScheduler.Default, maxConcurrencyLevel: 2);

    private readonly ILogger<WmiQueries> _logger;
    private readonly TimeSpan _defaultTimeout;

    /// <summary>Creates the query helper.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="defaultTimeout">Per-query timeout when the caller passes none (default 10 s).</param>
    public WmiQueries(ILogger<WmiQueries>? logger = null, TimeSpan? defaultTimeout = null)
    {
        _logger = logger ?? NullLogger<WmiQueries>.Instance;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>Scheduler for blocking Win32/WMI work (max concurrency 2). Shared by the whole Windows layer.</summary>
    public static TaskScheduler BlockingScheduler => SchedulerPair.ConcurrentScheduler;

    /// <summary>Default per-query timeout.</summary>
    public TimeSpan DefaultTimeout => _defaultTimeout;

    /// <summary>Runs a blocking function on <see cref="BlockingScheduler"/>; throws <see cref="TimeoutException"/> when it does not finish within <paramref name="timeout"/>.</summary>
    public static Task<T> RunBlockingAsync<T>(Func<T> func, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(func);
        Task<T> task = Task.Factory.StartNew(func, cancellationToken, TaskCreationOptions.DenyChildAttach, BlockingScheduler);
        return task.WaitAsync(timeout, cancellationToken);
    }

    // ---- generic queries ----------------------------------------------------------------------

    /// <summary>Synchronous query; returns the rows or an empty list on any provider error (never throws).</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string scope, string wql, TimeSpan? timeout = null) =>
        QueryCore(scope, wql, timeout ?? _defaultTimeout).Rows;

    /// <summary>Asynchronous query on the blocking scheduler; empty on error or timeout (never throws except for cancellation).</summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string scope, string wql, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        WmiQueryResult result = await TryQueryAsync(scope, wql, timeout, cancellationToken).ConfigureAwait(false);
        return result.Rows;
    }

    /// <summary>Like <see cref="QueryAsync"/> but reports whether the provider answered at all (missing class vs. no instances).</summary>
    public async Task<WmiQueryResult> TryQueryAsync(string scope, string wql, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        ArgumentException.ThrowIfNullOrEmpty(wql);
        TimeSpan effective = timeout ?? _defaultTimeout;
        try
        {
            return await RunBlockingAsync(() => QueryCore(scope, wql, effective), effective + TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("WMI query timed out after {Timeout} in {Scope}: {Wql}", effective, scope, wql);
            return new WmiQueryResult(false, Array.Empty<IReadOnlyDictionary<string, object?>>());
        }
    }

    private WmiQueryResult QueryCore(string scope, string wql, TimeSpan timeout)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        try
        {
            var managementScope = new ManagementScope(scope, new ConnectionOptions { Timeout = timeout, EnablePrivileges = true });
            managementScope.Connect();
            var options = new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = timeout };
            using var searcher = new ManagementObjectSearcher(managementScope, new ObjectQuery(wql), options);
            using ManagementObjectCollection results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (PropertyData property in item.Properties)
                    {
                        row[property.Name] = property.Value;
                    }

                    rows.Add(row);
                }
            }

            return new WmiQueryResult(true, rows);
        }
        catch (ManagementException ex)
        {
            _logger.LogDebug(ex, "WMI query failed ({Status}) in {Scope}: {Wql}", ex.ErrorCode, scope, wql);
        }
        catch (COMException ex)
        {
            _logger.LogDebug(ex, "WMI COM failure in {Scope}: {Wql}", scope, wql);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "WMI access denied in {Scope}: {Wql}", scope, wql);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "WMI invalid operation in {Scope}: {Wql}", scope, wql);
        }

        return new WmiQueryResult(false, rows);
    }

    // ---- typed helpers ------------------------------------------------------------------------

    /// <summary>Baseboard manufacturer, product and serial; <see langword="null"/> when unavailable.</summary>
    public async Task<WmiBaseboard?> GetBaseboardAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await QueryAsync(CimV2, "SELECT Manufacturer, Product, SerialNumber FROM Win32_BaseBoard", null, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        IReadOnlyDictionary<string, object?> row = rows[0];
        return new WmiBaseboard(row.GetString("Manufacturer"), row.GetString("Product"), row.GetString("SerialNumber"));
    }

    /// <summary>SMBIOS system UUID (Win32_ComputerSystemProduct.UUID); <see langword="null"/> when unavailable.</summary>
    public async Task<string?> GetSystemUuidAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await QueryAsync(CimV2, "SELECT UUID FROM Win32_ComputerSystemProduct", null, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        string uuid = rows[0].GetString("UUID");
        return uuid.Length == 0 ? null : uuid;
    }

    /// <summary>CPU model, core/thread counts (summed over sockets) and id; <see langword="null"/> when unavailable.</summary>
    public async Task<WmiCpu?> GetCpuAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await QueryAsync(CimV2, "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, ProcessorId, MaxClockSpeed FROM Win32_Processor", null, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        int cores = 0;
        int threads = 0;
        foreach (IReadOnlyDictionary<string, object?> row in rows)
        {
            cores += row.GetInt32("NumberOfCores");
            threads += row.GetInt32("NumberOfLogicalProcessors");
        }

        IReadOnlyDictionary<string, object?> first = rows[0];
        return new WmiCpu(CollapseSpaces(first.GetString("Name")), cores, threads, first.GetString("ProcessorId"), first.GetInt32("MaxClockSpeed"));
    }

    /// <summary>Video controllers (software-only ROOT devices skipped); VRAM comes from the registry when the driver reports more than the 4 GiB WMI cap.</summary>
    public async Task<IReadOnlyList<WmiGpu>> GetGpusAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await QueryAsync(CimV2, "SELECT Name, AdapterRAM, DriverVersion, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate, PNPDeviceID FROM Win32_VideoController", null, cancellationToken).ConfigureAwait(false);
        List<(string Description, long Bytes)> registrySizes = ReadGpuMemoryFromRegistry();
        var result = new List<WmiGpu>(rows.Count);
        foreach (IReadOnlyDictionary<string, object?> row in rows)
        {
            if (row.GetString("PNPDeviceID").StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string name = CollapseSpaces(row.GetString("Name"));
            long ram = row.GetInt64("AdapterRAM");
            foreach ((string description, long bytes) in registrySizes)
            {
                if (string.Equals(description, name, StringComparison.OrdinalIgnoreCase) && bytes > ram)
                {
                    ram = bytes;
                }
            }

            result.Add(new WmiGpu(name, ram, row.GetString("DriverVersion"), row.GetInt32("CurrentHorizontalResolution"), row.GetInt32("CurrentVerticalResolution"), row.GetInt32("CurrentRefreshRate")));
        }

        return result;
    }

    /// <summary>Installed RAM in bytes (sum of Win32_PhysicalMemory.Capacity); <see langword="null"/> when unavailable.</summary>
    public async Task<long?> GetInstalledMemoryBytesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await QueryAsync(CimV2, "SELECT Capacity FROM Win32_PhysicalMemory", null, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        long total = 0;
        foreach (IReadOnlyDictionary<string, object?> row in rows)
        {
            total += row.GetInt64("Capacity");
        }

        return total > 0 ? total : null;
    }

    /// <summary>Operating system caption, version and build (with UBR from the registry); <see langword="null"/> when unavailable.</summary>
    public async Task<WmiOs?> GetOsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await QueryAsync(CimV2, "SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem", null, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        IReadOnlyDictionary<string, object?> row = rows[0];
        return new WmiOs(CollapseSpaces(row.GetString("Caption")), row.GetString("Version"), row.GetString("BuildNumber"), ReadUbr());
    }

    /// <summary>Physical disk drives as seen by Win32_DiskDrive, ordered by index.</summary>
    public async Task<IReadOnlyList<WmiDiskDrive>> GetDiskDrivesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await QueryAsync(CimV2, "SELECT Index, Model, SerialNumber, Size, InterfaceType, MediaType FROM Win32_DiskDrive", null, cancellationToken).ConfigureAwait(false);
        var result = new List<WmiDiskDrive>(rows.Count);
        foreach (IReadOnlyDictionary<string, object?> row in rows)
        {
            result.Add(new WmiDiskDrive(row.GetInt32("Index", -1), CollapseSpaces(row.GetString("Model")), row.GetString("SerialNumber"), row.GetInt64("Size"), row.GetString("InterfaceType"), row.GetString("MediaType")));
        }

        result.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        return result;
    }

    /// <summary>MSFT_PhysicalDisk rows (media type SSD/HDD, bus type NVMe/SATA/iSCSI); empty when the Storage namespace is unavailable.</summary>
    public async Task<IReadOnlyList<WmiPhysicalDisk>> GetPhysicalDisksAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await QueryAsync(StorageScope, "SELECT DeviceId, FriendlyName, SerialNumber, Size, MediaType, BusType FROM MSFT_PhysicalDisk", null, cancellationToken).ConfigureAwait(false);
        var result = new List<WmiPhysicalDisk>(rows.Count);
        foreach (IReadOnlyDictionary<string, object?> row in rows)
        {
            if (!int.TryParse(row.GetString("DeviceId"), NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            {
                continue;
            }

            result.Add(new WmiPhysicalDisk(number, CollapseSpaces(row.GetString("FriendlyName")), row.GetString("SerialNumber"), row.GetInt64("Size"), row.GetInt32("MediaType"), row.GetInt32("BusType")));
        }

        return result;
    }

    /// <summary>MSFT_Partition rows that carry a drive letter.</summary>
    public async Task<IReadOnlyList<WmiPartition>> GetPartitionsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await QueryAsync(StorageScope, "SELECT DiskNumber, DriveLetter FROM MSFT_Partition", null, cancellationToken).ConfigureAwait(false);
        var result = new List<WmiPartition>(rows.Count);
        foreach (IReadOnlyDictionary<string, object?> row in rows)
        {
            char letter = char.ToUpperInvariant(row.GetChar("DriveLetter"));
            if (letter is >= 'A' and <= 'Z')
            {
                result.Add(new WmiPartition(row.GetInt32("DiskNumber", -1), letter));
            }
        }

        return result;
    }

    /// <summary>IP-enabled network adapters (MAC, IPs, gateways).</summary>
    public async Task<IReadOnlyList<WmiNetworkAdapter>> GetNetworkAdaptersAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await QueryAsync(CimV2, "SELECT Description, MACAddress, IPAddress, DefaultIPGateway, InterfaceIndex FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE", null, cancellationToken).ConfigureAwait(false);
        var result = new List<WmiNetworkAdapter>(rows.Count);
        foreach (IReadOnlyDictionary<string, object?> row in rows)
        {
            result.Add(new WmiNetworkAdapter(row.GetString("Description"), row.GetString("MACAddress").ToUpperInvariant(), row.GetStringArray("IPAddress"), row.GetStringArray("DefaultIPGateway"), row.GetInt32("InterfaceIndex")));
        }

        return result;
    }

    /// <summary>Monitors from EDID (WmiMonitorID in <c>root\wmi</c>); works from session 0.</summary>
    public async Task<IReadOnlyList<WmiMonitor>> GetMonitorsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await QueryAsync(WmiRoot, "SELECT ManufacturerName, UserFriendlyName, SerialNumberID, Active FROM WmiMonitorID", null, cancellationToken).ConfigureAwait(false);
        var result = new List<WmiMonitor>(rows.Count);
        foreach (IReadOnlyDictionary<string, object?> row in rows)
        {
            result.Add(new WmiMonitor(
                WmiRowExtensions.DecodeUInt16String(row.Get("ManufacturerName")),
                WmiRowExtensions.DecodeUInt16String(row.Get("UserFriendlyName")),
                WmiRowExtensions.DecodeUInt16String(row.Get("SerialNumberID")),
                row.GetBoolean("Active", fallback: true)));
        }

        return result;
    }

    /// <summary>Present USB and HID PnP devices with their vendor/product ids.</summary>
    public async Task<IReadOnlyList<WmiPnpDevice>> GetUsbHidDevicesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await QueryAsync(CimV2, "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\%' OR DeviceID LIKE 'HID\\\\%'", null, cancellationToken).ConfigureAwait(false);
        var result = new List<WmiPnpDevice>(rows.Count);
        foreach (IReadOnlyDictionary<string, object?> row in rows)
        {
            if (!row.GetBoolean("Present", fallback: true))
            {
                continue;
            }

            string deviceId = row.GetString("DeviceID");
            Match match = VidPidRegex().Match(deviceId);
            string vid = match.Success ? match.Groups[1].Value.ToUpperInvariant() : string.Empty;
            string pid = match.Success ? match.Groups[2].Value.ToUpperInvariant() : string.Empty;
            result.Add(new WmiPnpDevice(row.GetString("Name"), row.GetString("PNPClass"), deviceId, vid, pid));
        }

        return result;
    }

    /// <summary>TPM presence via Win32_Tpm; <see langword="null"/> when the TPM namespace cannot be queried.</summary>
    public async Task<bool?> GetTpmPresentAsync(CancellationToken cancellationToken)
    {
        WmiQueryResult result = await TryQueryAsync(TpmScope, "SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion FROM Win32_Tpm", null, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? result.Rows.Count > 0 : null;
    }

    /// <summary>UEFI Secure Boot state from the registry; <see langword="null"/> on legacy BIOS or when unreadable.</summary>
    public static bool? GetSecureBootEnabled()
    {
        try
        {
            using RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            return key?.GetValue("UEFISecureBootEnabled") is int value ? value != 0 : null;
        }
        catch (SecurityException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- registry helpers ----------------------------------------------------------------------

    private static int ReadUbr()
    {
        try
        {
            using RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return key?.GetValue("UBR") is int ubr ? ubr : 0;
        }
        catch (SecurityException)
        {
            return 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static List<(string Description, long Bytes)> ReadGpuMemoryFromRegistry()
    {
        var result = new List<(string Description, long Bytes)>();
        try
        {
            using RegistryKey? classKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (classKey is null)
            {
                return result;
            }

            foreach (string subKeyName in classKey.GetSubKeyNames())
            {
                if (subKeyName.Length != 4 || !int.TryParse(subKeyName, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    continue;
                }

                using RegistryKey? key = classKey.OpenSubKey(subKeyName);
                if (key is null)
                {
                    continue;
                }

                long bytes = key.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    int i => i,
                    byte[] { Length: >= 8 } b => BitConverter.ToInt64(b, 0),
                    _ => 0,
                };
                if (bytes <= 0)
                {
                    continue;
                }

                result.Add((CollapseSpaces(key.GetValue("DriverDesc") as string ?? string.Empty), bytes));
            }
        }
        catch (SecurityException)
        {
            // Partial results are fine: WMI AdapterRAM remains the fallback.
        }
        catch (IOException)
        {
            // Same as above.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }

        return result;
    }

    private static string CollapseSpaces(string value) =>
        value.Contains("  ", StringComparison.Ordinal) ? string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) : value.Trim();

    [GeneratedRegex("VID_([0-9A-Fa-f]{4}).*?PID_([0-9A-Fa-f]{4})", RegexOptions.CultureInvariant)]
    private static partial Regex VidPidRegex();
}
