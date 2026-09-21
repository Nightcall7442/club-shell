using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Windows.Network;

/// <summary>Details of the adapter that carries the server route.</summary>
/// <param name="Name">Friendly interface name (what netsh calls <c>name=</c>).</param>
/// <param name="Description">Driver description.</param>
/// <param name="Id">Interface GUID string.</param>
/// <param name="Mac"><c>AA:BB:CC:DD:EE:FF</c>.</param>
/// <param name="Ipv4">IPv4 address (empty when none).</param>
/// <param name="Gateway">IPv4 default gateway (empty when none).</param>
/// <param name="SpeedBps">Link speed in bits per second (-1 when unknown).</param>
/// <param name="Type">Interface type.</param>
public sealed record NetworkAdapterDetails(
    string Name,
    string Description,
    string Id,
    string Mac,
    string Ipv4,
    string Gateway,
    long SpeedBps,
    NetworkInterfaceType Type);

/// <summary>
/// Primary network adapter discovery, server reachability probe and change notifications. The primary adapter is the
/// one whose address the OS picks to reach the server (UDP connect, no packet sent); without a server host, or when
/// offline, it is the first operational Ethernet/Wi-Fi adapter with an IPv4 gateway. Reachability is a TCP connect to
/// the server port (ICMP is often filtered in clubs). <see cref="Changed"/> follows <see cref="NetworkChange"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NetworkProbe : IDisposable
{
    private readonly ILogger<NetworkProbe> _logger;
    private readonly string? _serverHost;
    private readonly int _serverPort;
    private bool _disposed;

    /// <summary>Creates the probe.</summary>
    /// <param name="serverHost">Central server host (name or IP) used for route selection and reachability; may be <see langword="null"/>.</param>
    /// <param name="serverPort">Server TCP port (default 443).</param>
    /// <param name="logger">Logger.</param>
    public NetworkProbe(string? serverHost = null, int serverPort = 443, ILogger<NetworkProbe>? logger = null)
    {
        _serverHost = string.IsNullOrWhiteSpace(serverHost) ? null : serverHost;
        _serverPort = serverPort;
        _logger = logger ?? NullLogger<NetworkProbe>.Instance;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
    }

    /// <summary>Raised (on a thread-pool thread) when addresses or availability change.</summary>
    public event EventHandler? Changed;

    /// <summary>Contract view of the primary adapter; empty strings when the machine has no usable adapter.</summary>
    public Contracts.Pcs.NetworkInfo GetInfo()
    {
        NetworkAdapterDetails? adapter = GetPrimaryAdapter();
        return adapter is null
            ? new Contracts.Pcs.NetworkInfo(string.Empty, string.Empty, string.Empty)
            : new Contracts.Pcs.NetworkInfo(adapter.Mac, adapter.Ipv4, adapter.Name);
    }

    /// <summary>Primary adapter details, or <see langword="null"/> when no operational adapter exists.</summary>
    public NetworkAdapterDetails? GetPrimaryAdapter()
    {
        List<NetworkInterface> candidates = OperationalAdapters();
        if (candidates.Count == 0)
        {
            return null;
        }

        IPAddress? routeSource = _serverHost is null ? null : LocalAddressTowards(_serverHost, _serverPort);
        NetworkInterface? chosen = null;
        if (routeSource is not null)
        {
            foreach (NetworkInterface adapter in candidates)
            {
                foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.Equals(routeSource))
                    {
                        chosen = adapter;
                        break;
                    }
                }

                if (chosen is not null)
                {
                    break;
                }
            }
        }

        chosen ??= FirstWithGateway(candidates) ?? candidates[0];
        return Describe(chosen);
    }

    /// <summary>All operational physical adapters (Ethernet / Wi-Fi, status Up).</summary>
    public static IReadOnlyList<NetworkAdapterDetails> GetPhysicalAdapters()
    {
        List<NetworkInterface> adapters = OperationalAdapters();
        var result = new List<NetworkAdapterDetails>(adapters.Count);
        foreach (NetworkInterface adapter in adapters)
        {
            result.Add(Describe(adapter));
        }

        return result;
    }

    /// <summary>TCP-connects to the configured server; <see langword="false"/> when no host is configured or the connect fails/times out.</summary>
    public Task<bool> IsOnlineAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _serverHost is null ? Task.FromResult(false) : IsOnlineAsync(_serverHost, _serverPort, timeout, cancellationToken);

    /// <summary>TCP-connects to <paramref name="host"/>:<paramref name="port"/> within <paramref name="timeout"/>.</summary>
    public async Task<bool> IsOnlineAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (SocketException ex)
        {
            _logger.LogDebug(ex, "Connectivity probe to {Host}:{Port} failed", host, port);
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Connectivity probe to {Host}:{Port} timed out after {Timeout}", host, port, timeout);
            return false;
        }
    }

    /// <summary>Formats a physical address as <c>AA:BB:CC:DD:EE:FF</c>.</summary>
    public static string FormatMac(PhysicalAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? string.Empty : BitConverter.ToString(bytes).Replace('-', ':');
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        GC.SuppressFinalize(this);
    }

    private static List<NetworkInterface> OperationalAdapters()
    {
        var result = new List<NetworkInterface>();
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus == OperationalStatus.Up
                    && adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet)
                {
                    result.Add(adapter);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No adapter information: treated as "no adapters".
        }

        return result;
    }

    private static NetworkInterface? FirstWithGateway(List<NetworkInterface> adapters)
    {
        foreach (NetworkInterface adapter in adapters)
        {
            if (Ipv4Gateway(adapter).Length > 0 && Ipv4Address(adapter).Length > 0)
            {
                return adapter;
            }
        }

        return null;
    }

    private static NetworkAdapterDetails Describe(NetworkInterface adapter) => new(
        adapter.Name,
        adapter.Description,
        adapter.Id,
        FormatMac(adapter.GetPhysicalAddress()),
        Ipv4Address(adapter),
        Ipv4Gateway(adapter),
        adapter.Speed,
        adapter.NetworkInterfaceType);

    private static string Ipv4Address(NetworkInterface adapter)
    {
        foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
        {
            if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && !IsAutoConfigured(unicast.Address))
            {
                return unicast.Address.ToString();
            }
        }

        return string.Empty;
    }

    private static string Ipv4Gateway(NetworkInterface adapter)
    {
        foreach (GatewayIPAddressInformation gateway in adapter.GetIPProperties().GatewayAddresses)
        {
            if (gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any))
            {
                return gateway.Address.ToString();
            }
        }

        return string.Empty;
    }

    /// <summary>169.254.x.x APIPA addresses mean "no DHCP lease" and are never the primary address.</summary>
    private static bool IsAutoConfigured(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    private IPAddress? LocalAddressTowards(string host, int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (IPAddress.TryParse(host, out IPAddress? ip))
            {
                if (ip.AddressFamily != AddressFamily.InterNetwork)
                {
                    return null;
                }

                socket.Connect(ip, port);
            }
            else
            {
                socket.Connect(host, port);
            }

            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException ex)
        {
            _logger.LogDebug(ex, "Route lookup towards {Host} failed; falling back to the first gateway adapter", host);
            return null;
        }
    }

    private void OnAddressChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}
