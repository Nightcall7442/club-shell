using System.Text.Json.Serialization;
using ClubShell.Contracts.Serialization;

namespace ClubShell.Contracts.Pcs;

/// <summary>Physical disk type.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<DiskType>))]
public enum DiskType
{
    /// <summary>Spinning disk.</summary>
    Hdd,

    /// <summary>SATA SSD.</summary>
    Ssd,

    /// <summary>NVMe SSD.</summary>
    Nvme,

    /// <summary>Network share / iSCSI.</summary>
    Network,

    /// <summary>Could not be determined.</summary>
    Unknown,
}

/// <summary>Well-known values of <see cref="PeripheralInfo.Kind"/>.</summary>
public static class PeripheralKinds
{
    /// <summary>Keyboard.</summary>
    public const string Keyboard = "keyboard";

    /// <summary>Mouse.</summary>
    public const string Mouse = "mouse";

    /// <summary>Headset / audio device.</summary>
    public const string Headset = "headset";

    /// <summary>Gamepad.</summary>
    public const string Gamepad = "gamepad";

    /// <summary>Anything else.</summary>
    public const string Other = "other";
}

/// <summary>CPU description.</summary>
/// <param name="Model">Model string from SMBIOS/WMI.</param>
/// <param name="Cores">Physical cores.</param>
/// <param name="Threads">Logical processors.</param>
public sealed record CpuInfo(
    string Model,
    int Cores,
    int Threads);

/// <summary>GPU description.</summary>
/// <param name="Model">Adapter model.</param>
/// <param name="VramMb">Dedicated VRAM in MiB.</param>
/// <param name="Driver">Driver version.</param>
public sealed record GpuInfo(
    string Model,
    int VramMb,
    string Driver);

/// <summary>Logical disk.</summary>
/// <param name="Mount">Mount point, e.g. <c>C:</c>.</param>
/// <param name="TotalGb">Capacity in GB (1 fraction digit).</param>
/// <param name="FreeGb">Free space in GB (1 fraction digit).</param>
/// <param name="Type">Physical type.</param>
public sealed record DiskInfo(
    string Mount,
    double TotalGb,
    double FreeGb,
    DiskType Type);

/// <summary>Attached monitor.</summary>
/// <param name="Index">Display index (0-based).</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Hz">Refresh rate.</param>
/// <param name="Primary">Primary display.</param>
public sealed record MonitorInfo(
    int Index,
    int Width,
    int Height,
    int Hz,
    bool Primary);

/// <summary>Primary network adapter.</summary>
/// <param name="Mac">MAC address <c>AA:BB:CC:DD:EE:FF</c>.</param>
/// <param name="Ip">IPv4 address.</param>
/// <param name="Adapter">Adapter name.</param>
public sealed record NetworkInfo(
    string Mac,
    string Ip,
    string Adapter);

/// <summary>Operating system.</summary>
/// <param name="Version">Product name, e.g. <c>Windows 11 Pro</c>.</param>
/// <param name="Build">Build, e.g. <c>26200.1234</c>.</param>
public sealed record OsInfo(
    string Version,
    string Build);

/// <summary>USB/HID peripheral.</summary>
/// <param name="Kind">Kind (<see cref="PeripheralKinds"/>).</param>
/// <param name="Name">Device name.</param>
/// <param name="VendorId">USB vendor id (hex).</param>
/// <param name="ProductId">USB product id (hex).</param>
public sealed record PeripheralInfo(
    string Kind,
    string Name,
    string VendorId,
    string ProductId);

/// <summary>Hardware inventory of a PC (IPC_PROTOCOL.md §6.6). Sent at registration and on change.</summary>
/// <param name="Cpu">CPU.</param>
/// <param name="Gpu">GPUs.</param>
/// <param name="RamMb">Installed RAM in MiB.</param>
/// <param name="Disks">Logical disks.</param>
/// <param name="Monitors">Monitors.</param>
/// <param name="Network">Primary adapter.</param>
/// <param name="Os">Operating system.</param>
/// <param name="Peripherals">Peripherals.</param>
public sealed record HardwareInfo(
    CpuInfo Cpu,
    IReadOnlyList<GpuInfo> Gpu,
    int RamMb,
    IReadOnlyList<DiskInfo> Disks,
    IReadOnlyList<MonitorInfo> Monitors,
    NetworkInfo Network,
    OsInfo Os,
    IReadOnlyList<PeripheralInfo> Peripherals);
