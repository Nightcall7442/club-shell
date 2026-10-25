using System.Globalization;
using System.Runtime.Versioning;
using ClubShell.Contracts.Pcs;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Security;
using ClubShell.Windows.Native;
using ClubShell.Windows.Network;
using ClubShell.Windows.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Windows.Hardware;

/// <summary>
/// Builds the <see cref="HardwareInfo"/> inventory (IPC_PROTOCOL.md §6.6) from WMI, Win32 and the network probe,
/// caches it for <see cref="CacheTtl"/> (agent.json <c>telemetry.hardwareRescanSec</c>) and computes the
/// <c>hardwareChanged</c> section diff. Also the Windows <see cref="IHardwareIdSource"/>: SMBIOS UUID, baseboard
/// serial, CPU id, system disk serial and primary MAC (ARCHITECTURE.md §6.1).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardwareInventory : IHardwareIdSource, IDisposable
{
    private static readonly string[] PlaceholderUuids =
    {
        "03000200-0400-0500-0006-000700080009",
        "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF",
        "00000000-0000-0000-0000-000000000000",
    };

    private readonly WmiQueries _wmi;
    private readonly Disks _disks;
    private readonly NetworkProbe _network;
    private readonly IClock _clock;
    private readonly ILogger<HardwareInventory> _logger;
    // MSFT_PhysicalDisk.BusType of a disk inside the PC: SCSI 1, ATA 3, RAID 8, SAS 10, SATA 11, NVMe 17. Not iSCSI 9,
    // USB 7, SD 12, MMC 13, Virtual 14, File Backed Virtual 15, Storage Spaces 16, nor Fibre Channel 6 or 1394 4.
    private static readonly HashSet<int> LocalBusTypes = [1, 3, 8, 10, 11, 17];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private HardwareInfo? _cached;
    private long _cachedAt;

    /// <summary>Creates the inventory.</summary>
    /// <param name="wmi">WMI helper.</param>
    /// <param name="disks">Disk helper.</param>
    /// <param name="network">Network probe (primary adapter).</param>
    /// <param name="clock">Clock for cache expiry.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cacheTtl">Cache lifetime (default 1 h).</param>
    public HardwareInventory(WmiQueries wmi, Disks disks, NetworkProbe network, IClock? clock = null, ILogger<HardwareInventory>? logger = null, TimeSpan? cacheTtl = null)
    {
        ArgumentNullException.ThrowIfNull(wmi);
        ArgumentNullException.ThrowIfNull(disks);
        ArgumentNullException.ThrowIfNull(network);
        _wmi = wmi;
        _disks = disks;
        _network = network;
        _clock = clock ?? SystemClock.Instance;
        _logger = logger ?? NullLogger<HardwareInventory>.Instance;
        CacheTtl = cacheTtl ?? TimeSpan.FromHours(1);
    }

    /// <summary>Wire keys of the <see cref="HardwareInfo"/> sections, as reported in <c>hardwareChanged.diff</c>.</summary>
    public static IReadOnlyList<string> SectionKeys { get; } = new[] { "cpu", "gpu", "ramMb", "disks", "monitors", "network", "os", "peripherals" };

    /// <summary>Cache lifetime.</summary>
    public TimeSpan CacheTtl { get; }

    /// <summary>Last built inventory, or <see langword="null"/> before the first scan.</summary>
    public HardwareInfo? Current => Volatile.Read(ref _cached);

    /// <summary>
    /// Returns the cached inventory, rescanning when it is older than <see cref="CacheTtl"/>. Callers that miss the cache
    /// together (registration and the telemetry start on a first boot) share one scan: the one that waited for the other's
    /// takes its result.
    /// </summary>
    public async Task<HardwareInfo> GetAsync(CancellationToken cancellationToken)
    {
        if (Fresh() is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Fresh() ?? await ScanAndCacheAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Rescans unconditionally and updates the cache.</summary>
    public async Task<HardwareInfo> RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ScanAndCacheAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The cached inventory while younger than <see cref="CacheTtl"/>.</summary>
    private HardwareInfo? Fresh()
    {
        HardwareInfo? cached = Current;
        return cached is not null && _clock.GetElapsedTime(Volatile.Read(ref _cachedAt)) < CacheTtl ? cached : null;
    }

    /// <summary>Scans and caches; the caller holds <c>_gate</c>.</summary>
    private async Task<HardwareInfo> ScanAndCacheAsync(CancellationToken cancellationToken)
    {
        HardwareInfo info = await ScanAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _cachedAt, _clock.GetTimestamp());
        Volatile.Write(ref _cached, info);
        _logger.LogInformation("Hardware inventory: {Cpu}, {GpuCount} GPU(s), {RamMb} MiB, {DiskCount} disk(s), {MonitorCount} monitor(s)", info.Cpu.Model, info.Gpu.Count, info.RamMb, info.Disks.Count, info.Monitors.Count);
        return info;
    }

    /// <summary>
    /// Names of the sections (see <see cref="SectionKeys"/>) whose value differs. Free disk space is ignored so routine
    /// usage does not look like a hardware change.
    /// </summary>
    public static IReadOnlyList<string> Diff(HardwareInfo previous, HardwareInfo current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        var changed = new List<string>();
        if (previous.Cpu != current.Cpu)
        {
            changed.Add("cpu");
        }

        if (!previous.Gpu.SequenceEqual(current.Gpu))
        {
            changed.Add("gpu");
        }

        if (previous.RamMb != current.RamMb)
        {
            changed.Add("ramMb");
        }

        if (!DisksEqual(previous.Disks, current.Disks))
        {
            changed.Add("disks");
        }

        if (!previous.Monitors.SequenceEqual(current.Monitors))
        {
            changed.Add("monitors");
        }

        if (previous.Network != current.Network)
        {
            changed.Add("network");
        }

        if (previous.Os != current.Os)
        {
            changed.Add("os");
        }

        if (!previous.Peripherals.SequenceEqual(current.Peripherals))
        {
            changed.Add("peripherals");
        }

        return changed;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetComponentsAsync(CancellationToken cancellationToken)
    {
        Task<string?> uuidTask = _wmi.GetSystemUuidAsync(cancellationToken);
        Task<WmiBaseboard?> boardTask = _wmi.GetBaseboardAsync(cancellationToken);
        Task<WmiCpu?> cpuTask = _wmi.GetCpuAsync(cancellationToken);
        Task<IReadOnlyList<WmiDiskDrive>> drivesTask = _wmi.GetDiskDrivesAsync(cancellationToken);
        Task<IReadOnlyList<WmiPhysicalDisk>> physicalTask = _wmi.GetPhysicalDisksAsync(cancellationToken);
        await Task.WhenAll(uuidTask, boardTask, cpuTask, drivesTask, physicalTask).ConfigureAwait(false);

        var components = new List<string>(5);
        string? uuid = await uuidTask.ConfigureAwait(false);
        if (uuid is not null && Hwid.IsUsable(uuid) && Array.IndexOf(PlaceholderUuids, uuid.ToUpperInvariant()) < 0)
        {
            components.Add("uuid:" + uuid);
        }

        if (await boardTask.ConfigureAwait(false) is { SerialNumber: var serial } && Hwid.IsUsable(serial))
        {
            components.Add("board:" + serial);
        }

        if (await cpuTask.ConfigureAwait(false) is { ProcessorId: var cpuId } && Hwid.IsUsable(cpuId))
        {
            components.Add("cpu:" + cpuId);
        }

        if (HwidDiskSerial(await drivesTask.ConfigureAwait(false), await physicalTask.ConfigureAwait(false)) is { } diskSerial)
        {
            components.Add("disk:" + diskSerial);
        }

        string mac = _network.GetInfo().Mac;
        if (Hwid.IsUsable(mac))
        {
            components.Add("mac:" + mac);
        }

        return components;
    }

    /// <summary>
    /// The disk serial number that goes into the hardware id: that of the lowest-index disk on a local bus (SCSI, ATA,
    /// RAID, SAS, SATA, NVMe — <c>MSFT_PhysicalDisk.BusType</c>) with fixed media, or none. An iSCSI, USB, SD or virtual
    /// disk is skipped: on a diskless PC "Disk 0" is the seat's iSCSI disk, on a hybrid the disk order follows the order
    /// the disks came up in, and either would change the id (SHELL_CHANGES 30). Without bus types (the Storage namespace
    /// is unavailable) the lowest-index disk is taken, as before, so such a PC keeps its id.
    /// </summary>
    public static string? HwidDiskSerial(IReadOnlyList<WmiDiskDrive> drives, IReadOnlyList<WmiPhysicalDisk> physicalDisks)
    {
        ArgumentNullException.ThrowIfNull(drives);
        ArgumentNullException.ThrowIfNull(physicalDisks);
        if (physicalDisks.Count == 0)
        {
            return drives.Count > 0 && Hwid.IsUsable(drives[0].SerialNumber) ? drives[0].SerialNumber : null;
        }

        foreach (WmiDiskDrive drive in drives.OrderBy(d => d.Index))
        {
            WmiPhysicalDisk? physical = physicalDisks.FirstOrDefault(p => p.DeviceNumber == drive.Index);
            if (physical is null || !LocalBusTypes.Contains(physical.BusType) || drive.MediaType.StartsWith("Removable", StringComparison.OrdinalIgnoreCase)
                || drive.MediaType.StartsWith("External", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Hwid.IsUsable(drive.SerialNumber) ? drive.SerialNumber : null;
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- scan ---------------------------------------------------------------------------------

    private async Task<HardwareInfo> ScanAsync(CancellationToken cancellationToken)
    {
        Task<WmiCpu?> cpuTask = _wmi.GetCpuAsync(cancellationToken);
        Task<IReadOnlyList<WmiGpu>> gpuTask = _wmi.GetGpusAsync(cancellationToken);
        Task<long?> ramTask = _wmi.GetInstalledMemoryBytesAsync(cancellationToken);
        Task<WmiOs?> osTask = _wmi.GetOsAsync(cancellationToken);
        Task<IReadOnlyList<DiskInfo>> disksTask = _disks.ListAsync(cancellationToken);
        Task<IReadOnlyList<WmiPnpDevice>> pnpTask = _wmi.GetUsbHidDevicesAsync(cancellationToken);
        await Task.WhenAll(cpuTask, gpuTask, ramTask, osTask, disksTask, pnpTask).ConfigureAwait(false);

        WmiCpu? cpu = await cpuTask.ConfigureAwait(false);
        IReadOnlyList<WmiGpu> gpuList = await gpuTask.ConfigureAwait(false);
        CpuInfo cpuInfo = cpu is null
            ? new CpuInfo("Unknown", Environment.ProcessorCount, Environment.ProcessorCount)
            : new CpuInfo(cpu.Name, cpu.Cores > 0 ? cpu.Cores : Environment.ProcessorCount, cpu.Threads > 0 ? cpu.Threads : Environment.ProcessorCount);

        var gpus = new List<GpuInfo>();
        foreach (WmiGpu gpu in gpuList)
        {
            gpus.Add(new GpuInfo(gpu.Name, (int)(gpu.AdapterRamBytes / (1024 * 1024)), gpu.DriverVersion));
        }

        WmiOs? os = await osTask.ConfigureAwait(false);
        OsInfo osInfo = os is null
            ? new OsInfo("Windows", Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture))
            : new OsInfo(os.ProductName, os.FullBuild);

        List<MonitorInfo> monitors = await GetMonitorsAsync(gpuList, cancellationToken).ConfigureAwait(false);

        return new HardwareInfo(
            cpuInfo,
            gpus,
            ToRamMb(await ramTask.ConfigureAwait(false)),
            await disksTask.ConfigureAwait(false),
            monitors,
            _network.GetInfo(),
            osInfo,
            ToPeripherals(await pnpTask.ConfigureAwait(false)));
    }

    private static int ToRamMb(long? installedBytes)
    {
        long bytes = installedBytes ?? 0;
        if (bytes <= 0)
        {
            MEMORYSTATUSEX status = MEMORYSTATUSEX.Create();
            bytes = Kernel32.GlobalMemoryStatusEx(ref status) ? (long)status.ullTotalPhys : 0;
        }

        return (int)(bytes / (1024 * 1024));
    }

    private async Task<List<MonitorInfo>> GetMonitorsAsync(IReadOnlyList<WmiGpu> gpus, CancellationToken cancellationToken)
    {
        if (IsInteractiveSession())
        {
            List<MonitorInfo> native = EnumerateDisplayMonitors();
            if (native.Count > 0)
            {
                return native;
            }
        }

        // Session 0 only sees a virtual 1024x768 display, so count monitors from EDID and take the mode from the adapter.
        // ponytail: one mode for every monitor; per-monitor modes need WmiMonitorListedSupportedSourceModes if mixed setups appear.
        IReadOnlyList<WmiMonitor> edid = await _wmi.GetMonitorsAsync(cancellationToken).ConfigureAwait(false);
        int count = 0;
        foreach (WmiMonitor monitor in edid)
        {
            if (monitor.Active)
            {
                count++;
            }
        }

        int width = 0;
        int height = 0;
        int hz = 0;
        foreach (WmiGpu gpu in gpus)
        {
            if (gpu.CurrentWidth > 0 && gpu.CurrentHeight > 0)
            {
                width = gpu.CurrentWidth;
                height = gpu.CurrentHeight;
                hz = gpu.CurrentRefreshHz;
                break;
            }
        }

        if (count == 0 && width > 0)
        {
            count = 1;
        }

        var result = new List<MonitorInfo>(count);
        for (int i = 0; i < count; i++)
        {
            result.Add(new MonitorInfo(i, width, height, hz, i == 0));
        }

        return result;
    }

    private static bool IsInteractiveSession() =>
        Kernel32.ProcessIdToSessionId(Kernel32.GetCurrentProcessId(), out uint sessionId) && sessionId != 0;

    private static List<MonitorInfo> EnumerateDisplayMonitors()
    {
        var handles = new List<nint>();
        MonitorEnumProc callback = (nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData) =>
        {
            handles.Add(hMonitor);
            return true;
        };
        bool ok = User32.EnumDisplayMonitors(0, 0, callback, 0);
        GC.KeepAlive(callback);
        var result = new List<MonitorInfo>();
        if (!ok)
        {
            return result;
        }

        foreach (nint handle in handles)
        {
            MONITORINFOEXW info = MONITORINFOEXW.Create();
            if (!User32.GetMonitorInfoW(handle, ref info))
            {
                continue;
            }

            int width = info.rcMonitor.Width;
            int height = info.rcMonitor.Height;
            int hz = 0;
            DEVMODEW mode = DEVMODEW.Create();
            if (User32.EnumDisplaySettingsW(info.DeviceName, NativeConst.ENUM_CURRENT_SETTINGS, ref mode))
            {
                width = (int)mode.dmPelsWidth;
                height = (int)mode.dmPelsHeight;
                hz = (int)mode.dmDisplayFrequency;
            }

            result.Add(new MonitorInfo(result.Count, width, height, hz, info.IsPrimary));
        }

        // Primary display first so index 0 is stable across enumeration order changes.
        result.Sort(static (a, b) => b.Primary.CompareTo(a.Primary));
        for (int i = 0; i < result.Count; i++)
        {
            result[i] = result[i] with { Index = i };
        }

        return result;
    }

    private static List<PeripheralInfo> ToPeripherals(IReadOnlyList<WmiPnpDevice> devices)
    {
        var result = new List<PeripheralInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (WmiPnpDevice device in devices)
        {
            if (device.VendorId.Length == 0 || device.ProductId.Length == 0)
            {
                continue;
            }

            string? kind = ClassifyPeripheral(device);
            if (kind is null || !seen.Add(kind + ":" + device.VendorId + ":" + device.ProductId))
            {
                continue;
            }

            result.Add(new PeripheralInfo(kind, device.Name, device.VendorId, device.ProductId));
        }

        return result;
    }

    /// <summary>Maps a PnP class (and, for HID, the name) to a <see cref="PeripheralKinds"/> value; <see langword="null"/> skips hubs, composite parents and generic HID nodes.</summary>
    private static string? ClassifyPeripheral(WmiPnpDevice device)
    {
        switch (device.PnpClass)
        {
            case "Keyboard":
                return PeripheralKinds.Keyboard;
            case "Mouse":
                return PeripheralKinds.Mouse;
            case "AudioEndpoint":
            case "MEDIA":
                return PeripheralKinds.Headset;
            case "XboxComposite":
            case "XnaComposite":
                return PeripheralKinds.Gamepad;
            case "HIDClass":
                return LooksLikeGamepad(device.Name) ? PeripheralKinds.Gamepad : null;
            default:
                return null;
        }
    }

    private static bool LooksLikeGamepad(string name) =>
        name.Contains("gamepad", StringComparison.OrdinalIgnoreCase)
        || name.Contains("controller", StringComparison.OrdinalIgnoreCase)
        || name.Contains("joystick", StringComparison.OrdinalIgnoreCase)
        || name.Contains("xbox", StringComparison.OrdinalIgnoreCase)
        || name.Contains("dualshock", StringComparison.OrdinalIgnoreCase)
        || name.Contains("dualsense", StringComparison.OrdinalIgnoreCase);

    private static bool DisksEqual(IReadOnlyList<DiskInfo> a, IReadOnlyList<DiskInfo> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Mount != b[i].Mount || a[i].Type != b[i].Type || Math.Abs(a[i].TotalGb - b[i].TotalGb) > 0.05)
            {
                return false;
            }
        }

        return true;
    }
}
