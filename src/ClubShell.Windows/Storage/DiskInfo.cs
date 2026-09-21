using System.Runtime.Versioning;
using ClubShell.Contracts.Pcs;
using ClubShell.Windows.Hardware;
using ClubShell.Windows.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Windows.Storage;

/// <summary>
/// Logical disks for the hardware inventory and free-space checks before game installs / cache growth. Capacity uses
/// binary gigabytes (1024³) so the numbers match what Explorer shows; the physical type per drive letter comes from
/// the Storage Management WMI classes (MSFT_Partition → MSFT_PhysicalDisk) and degrades to <see cref="DiskType.Unknown"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Disks
{
    private const double BytesPerGb = 1024d * 1024d * 1024d;

    private readonly WmiQueries _wmi;
    private readonly ILogger<Disks> _logger;

    /// <summary>Creates the helper.</summary>
    public Disks(WmiQueries wmi, ILogger<Disks>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(wmi);
        _wmi = wmi;
        _logger = logger ?? NullLogger<Disks>.Instance;
    }

    /// <summary>Fixed and network drives that are ready, as <see cref="DiskInfo"/> (mount <c>C:</c>, GB with one decimal).</summary>
    public async Task<IReadOnlyList<DiskInfo>> ListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<char, DiskType> physical = await GetDriveTypesAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<DiskInfo>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Network))
            {
                continue;
            }

            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                char letter = char.ToUpperInvariant(drive.Name[0]);
                DiskType type = drive.DriveType == DriveType.Network
                    ? DiskType.Network
                    : physical.TryGetValue(letter, out DiskType known) ? known : DiskType.Unknown;
                result.Add(new DiskInfo(letter + ":", ToGb(drive.TotalSize), ToGb(drive.AvailableFreeSpace), type));
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Drive {Drive} skipped", drive.Name);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogDebug(ex, "Drive {Drive} skipped", drive.Name);
            }
        }

        return result;
    }

    /// <summary>Physical type per drive letter from MSFT_Partition / MSFT_PhysicalDisk; empty when the Storage namespace is unavailable.</summary>
    public async Task<IReadOnlyDictionary<char, DiskType>> GetDriveTypesAsync(CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<WmiPartition>> partitionsTask = _wmi.GetPartitionsAsync(cancellationToken);
        Task<IReadOnlyList<WmiPhysicalDisk>> disksTask = _wmi.GetPhysicalDisksAsync(cancellationToken);
        IReadOnlyList<WmiPartition> partitions = await partitionsTask.ConfigureAwait(false);
        IReadOnlyList<WmiPhysicalDisk> disks = await disksTask.ConfigureAwait(false);

        var byNumber = new Dictionary<int, DiskType>(disks.Count);
        foreach (WmiPhysicalDisk disk in disks)
        {
            byNumber[disk.DeviceNumber] = disk.ToDiskType();
        }

        var result = new Dictionary<char, DiskType>();
        foreach (WmiPartition partition in partitions)
        {
            if (byNumber.TryGetValue(partition.DiskNumber, out DiskType type))
            {
                result[partition.DriveLetter] = type;
            }
        }

        return result;
    }

    /// <summary><see langword="true"/> when the drive letter sits on an SSD or NVMe disk.</summary>
    public async Task<bool> IsSsdAsync(char driveLetter, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<char, DiskType> types = await GetDriveTypesAsync(cancellationToken).ConfigureAwait(false);
        return types.TryGetValue(char.ToUpperInvariant(driveLetter), out DiskType type) && type is DiskType.Ssd or DiskType.Nvme;
    }

    /// <summary>Free bytes available to the caller on the volume holding <paramref name="path"/> (works for UNC paths too).</summary>
    /// <exception cref="System.ComponentModel.Win32Exception">The volume could not be queried.</exception>
    public static long FreeSpaceBytes(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Win32Error.ThrowIfFalse(Kernel32.GetDiskFreeSpaceExW(path, out ulong free, out _, out _), nameof(Kernel32.GetDiskFreeSpaceExW));
        return free > (ulong)long.MaxValue ? long.MaxValue : (long)free;
    }

    /// <summary>Free space in binary GB with one decimal.</summary>
    public static double FreeSpaceGb(string path) => ToGb(FreeSpaceBytes(path));

    /// <summary>
    /// Ensures at least <paramref name="minGb"/> is free on the volume of <paramref name="path"/>, invoking
    /// <paramref name="cleanup"/> once when it is not; returns whether the requirement holds afterwards.
    /// </summary>
    public async Task<bool> EnsureFreeSpaceAsync(string path, double minGb, Func<CancellationToken, Task>? cleanup, CancellationToken cancellationToken)
    {
        long required = (long)(minGb * BytesPerGb);
        long free = FreeSpaceBytes(path);
        if (free >= required)
        {
            return true;
        }

        _logger.LogWarning("Only {FreeGb:F1} GB free on {Path}; {RequiredGb:F1} GB required", ToGb(free), path, minGb);
        if (cleanup is null)
        {
            return false;
        }

        await cleanup(cancellationToken).ConfigureAwait(false);
        free = FreeSpaceBytes(path);
        _logger.LogInformation("Cleanup finished; {FreeGb:F1} GB free on {Path}", ToGb(free), path);
        return free >= required;
    }

    private static double ToGb(long bytes) => Math.Round(bytes / BytesPerGb, 1);
}
