using System.Text.Json.Serialization;
using ClubShell.Contracts.Serialization;

namespace ClubShell.Contracts.Pcs;

/// <summary>Seat/PC status as shown on the club map.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<PcStatus>))]
public enum PcStatus
{
    /// <summary>Agent not reachable.</summary>
    Offline,

    /// <summary>Online, no session.</summary>
    Free,

    /// <summary>Session active.</summary>
    Busy,

    /// <summary>Locked (session locked or admin lock).</summary>
    Locked,

    /// <summary>Taken out of service by an admin.</summary>
    Maintenance,

    /// <summary>Reserved by a booking.</summary>
    Booked,
}

/// <summary>Agent ⇄ server connectivity.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ConnectivityState>))]
public enum ConnectivityState
{
    /// <summary>Server reachable.</summary>
    Online,

    /// <summary>Offline mode (ARCHITECTURE.md §8).</summary>
    Offline,
}

/// <summary>Temperatures in °C; 0 when unavailable.</summary>
/// <param name="Cpu">CPU package temperature.</param>
/// <param name="Gpu">GPU temperature.</param>
public sealed record Temperatures(
    double Cpu,
    double Gpu);

/// <summary>Network throughput in Mbit/s.</summary>
/// <param name="Up">Upload.</param>
/// <param name="Down">Download.</param>
public sealed record NetworkThroughput(
    double Up,
    double Down);

/// <summary>One telemetry sample (IPC_PROTOCOL.md §6.7). Payload of <c>sys.metrics</c> and batched to <c>POST /agents/{pcId}/telemetry</c>.</summary>
/// <param name="CpuPct">CPU utilisation 0–100.</param>
/// <param name="GpuPct">GPU utilisation 0–100; 0 if unavailable.</param>
/// <param name="RamUsedMb">RAM in use, MiB.</param>
/// <param name="Temps">Temperatures.</param>
/// <param name="Fps">Frame rate from the running game's overlay hook; <see langword="null"/> when none.</param>
/// <param name="NetMbps">Network throughput.</param>
/// <param name="UptimeSec">OS uptime in seconds.</param>
/// <param name="At">Sample time.</param>
public sealed record PcMetrics(
    double CpuPct,
    double GpuPct,
    int RamUsedMb,
    Temperatures Temps,
    double? Fps,
    NetworkThroughput NetMbps,
    long UptimeSec,
    DateTimeOffset At);
