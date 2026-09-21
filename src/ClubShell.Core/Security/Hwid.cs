using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Core.Security;

/// <summary>
/// Supplies raw hardware identifiers (baseboard serial, CPU id, SMBIOS system UUID, primary MAC, …). The Windows layer
/// implements it over WMI; <see cref="BasicHardwareIdSource"/> is the platform-neutral fallback.
/// </summary>
public interface IHardwareIdSource
{
    /// <summary>Raw components in a stable order; unusable values (empty, OEM placeholders) are filtered by <see cref="Hwid"/>.</summary>
    Task<IReadOnlyList<string>> GetComponentsAsync(CancellationToken cancellationToken);
}

/// <summary>Platform-neutral source: machine name plus the lowest physical MAC of an operational Ethernet/Wi-Fi adapter.</summary>
public sealed class BasicHardwareIdSource : IHardwareIdSource
{
    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetComponentsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var components = new List<string> { "host:" + Environment.MachineName };
        var macs = new List<string>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                    || adapter.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                var mac = adapter.GetPhysicalAddress().ToString();
                if (mac.Length == 12 && !string.Equals(mac, "000000000000", StringComparison.Ordinal))
                {
                    macs.Add(mac);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No adapter information available; the machine name alone triggers the persistent fallback.
        }

        if (macs.Count > 0)
        {
            macs.Sort(StringComparer.Ordinal);
            components.Add("mac:" + macs[0]);
        }

        return Task.FromResult<IReadOnlyList<string>>(components);
    }
}

/// <summary>
/// Stable hardware id (ARCHITECTURE.md §6.1 step 4): SHA-256 over the normalized components joined with <c>|</c>,
/// as 64 lower-case hex characters. When fewer than <see cref="MinComponents"/> usable components exist (VMs, OEM
/// placeholders) a random GUID persisted next to the secrets is mixed in so the id stays stable across restarts.
/// The value is computed once and cached; a failed computation is retried on the next call.
/// </summary>
public sealed class Hwid
{
    /// <summary>Minimum usable components before the persistent fallback is used.</summary>
    public const int MinComponents = 2;

    private static readonly HashSet<string> Placeholders = new(StringComparer.Ordinal)
    {
        "NONE", "NA", "OEM", "UNKNOWN", "DEFAULT", "DEFAULTSTRING", "TOBEFILLEDBYOEM", "TOBEFILLEDBYODM",
        "SYSTEMSERIALNUMBER", "SERIALNUMBER", "BASEBOARDSERIALNUMBER", "CHASSISSERIALNUMBER", "NOTSPECIFIED",
        "NOTAPPLICABLE", "NOTAVAILABLE", "INVALID", "EMPTY", "NULL", "0123456789", "123456789", "STANDARD",
    };

    private readonly IHardwareIdSource _source;
    private readonly string _fallbackFilePath;
    private readonly ILogger<Hwid> _logger;
    private readonly object _gate = new();
    private Task<string>? _task;

    /// <summary>Creates the provider.</summary>
    /// <param name="source">Raw component source.</param>
    /// <param name="fallbackFilePath">File holding the persistent random fallback (<c>secure\hwid.fallback</c>).</param>
    /// <param name="logger">Logger.</param>
    public Hwid(IHardwareIdSource source, string fallbackFilePath, ILogger<Hwid>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(fallbackFilePath);
        _source = source;
        _fallbackFilePath = fallbackFilePath;
        _logger = logger ?? NullLogger<Hwid>.Instance;
    }

    /// <summary>Returns the cached hardware id, computing it on first use.</summary>
    public Task<string> GetAsync(CancellationToken cancellationToken)
    {
        Task<string> task;
        lock (_gate)
        {
            if (_task is null || _task.IsFaulted || _task.IsCanceled)
            {
                _task = ComputeAsync();
            }

            task = _task;
        }

        return task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Deterministic hash of <paramref name="components"/>: each is normalized with <see cref="Normalize"/>, unusable
    /// ones dropped, the rest joined with <c>|</c> in order and hashed with SHA-256 (64 lower-case hex chars).
    /// </summary>
    public static string Compute(IReadOnlyList<string> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        var usable = new List<string>(components.Count);
        foreach (var component in components)
        {
            if (Normalize(component) is { } normalized)
            {
                usable.Add(normalized);
            }
        }

        if (usable.Count == 0)
        {
            throw new ArgumentException("No usable hardware id components", nameof(components));
        }

        return Signing.Sha256Hex(Encoding.UTF8.GetBytes(string.Join('|', usable)));
    }

    /// <summary>
    /// Upper-cases and strips everything but ASCII letters and digits; returns <see langword="null"/> for empty values,
    /// OEM placeholders (<c>To be filled by O.E.M.</c>, <c>Default string</c>, …) and single-character runs (<c>0000…</c>).
    /// </summary>
    public static string? Normalize(string? component)
    {
        if (string.IsNullOrWhiteSpace(component))
        {
            return null;
        }

        var builder = new StringBuilder(component.Length);
        foreach (var c in component)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        if (builder.Length == 0)
        {
            return null;
        }

        var normalized = builder.ToString();
        if (Placeholders.Contains(normalized))
        {
            return null;
        }

        var first = normalized[0];
        var uniform = true;
        foreach (var c in normalized)
        {
            if (c != first)
            {
                uniform = false;
                break;
            }
        }

        return uniform ? null : normalized;
    }

    /// <summary><see langword="true"/> when <paramref name="component"/> survives <see cref="Normalize"/>.</summary>
    public static bool IsUsable(string? component) => Normalize(component) is not null;

    private async Task<string> ComputeAsync()
    {
        var raw = await _source.GetComponentsAsync(CancellationToken.None).ConfigureAwait(false);
        var usable = new List<string>(raw.Count);
        foreach (var component in raw)
        {
            if (Normalize(component) is { } normalized)
            {
                usable.Add(normalized);
            }
        }

        if (usable.Count < MinComponents)
        {
            _logger.LogWarning("Only {Count} usable hardware id component(s); mixing in the persistent fallback {Path}", usable.Count, _fallbackFilePath);
            usable.Insert(0, "FALLBACK" + await LoadOrCreateFallbackAsync().ConfigureAwait(false));
        }

        var hwid = Compute(usable);
        _logger.LogInformation("Hardware id computed from {Count} component(s)", usable.Count);
        return hwid;
    }

    private async Task<string> LoadOrCreateFallbackAsync()
    {
        if (File.Exists(_fallbackFilePath))
        {
            var existing = (await File.ReadAllTextAsync(_fallbackFilePath).ConfigureAwait(false)).Trim();
            if (Guid.TryParse(existing, out var parsed))
            {
                return parsed.ToString("N");
            }
        }

        var directory = Path.GetDirectoryName(_fallbackFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var created = new Guid(RandomNumberGenerator.GetBytes(16));
        await File.WriteAllTextAsync(_fallbackFilePath, created.ToString("D")).ConfigureAwait(false);
        return created.ToString("N");
    }
}
