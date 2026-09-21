using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace ClubShell.Windows.Power;

/// <summary>
/// Wake-on-LAN magic packets over UDP. Without an explicit broadcast address the packet goes to the limited
/// broadcast (255.255.255.255) and to the directed broadcast of every operational IPv4 adapter, which is what makes
/// multi-homed club servers reach the right segment. Accepts every common MAC spelling
/// (<c>AA:BB:CC:DD:EE:FF</c>, <c>AA-BB-…</c>, <c>AABBCCDDEEFF</c>, <c>aabb.ccdd.eeff</c>) and an optional SecureOn password.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WakeOnLan
{
    /// <summary>Discard port, the WoL convention.</summary>
    public const int DefaultPort = 9;

    private const int MacLength = 6;

    /// <summary>Sends one magic packet per broadcast target for <paramref name="mac"/>.</summary>
    /// <param name="mac">Target MAC in any common spelling.</param>
    /// <param name="broadcast">Explicit destination; <see langword="null"/> = limited + directed broadcasts.</param>
    /// <param name="port">UDP port (default 9).</param>
    /// <param name="secureOnPassword">Optional 4- or 6-byte SecureOn password in hex.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="ArgumentException">The MAC or password is malformed.</exception>
    /// <exception cref="SocketException">No packet could be sent.</exception>
    public static async Task SendAsync(string mac, IPAddress? broadcast = null, int port = DefaultPort, string? secureOnPassword = null, CancellationToken cancellationToken = default)
    {
        byte[] packet = BuildMagicPacket(ParseMac(mac), secureOnPassword is null ? ReadOnlySpan<byte>.Empty : ParseHex(secureOnPassword, 4, 6));
        IReadOnlyList<IPAddress> targets = broadcast is null ? BroadcastAddresses() : new[] { broadcast };

        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        SocketException? lastError = null;
        int sent = 0;
        foreach (IPAddress target in targets)
        {
            try
            {
                _ = await udp.SendAsync(packet, new IPEndPoint(target, port), cancellationToken).ConfigureAwait(false);
                sent++;
            }
            catch (SocketException ex)
            {
                lastError = ex;
            }
        }

        if (sent == 0 && lastError is not null)
        {
            throw lastError;
        }
    }

    /// <summary>Sends a magic packet for every MAC; malformed entries are skipped, send failures are swallowed per MAC.</summary>
    /// <returns>The MACs that were sent successfully (normalised to <c>AA:BB:CC:DD:EE:FF</c>).</returns>
    public static async Task<IReadOnlyList<string>> SendManyAsync(IEnumerable<string> macs, IPAddress? broadcast = null, int port = DefaultPort, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(macs);
        var sent = new List<string>();
        foreach (string mac in macs)
        {
            byte[] bytes;
            try
            {
                bytes = ParseMac(mac);
            }
            catch (ArgumentException)
            {
                continue;
            }

            try
            {
                await SendAsync(FormatMac(bytes), broadcast, port, null, cancellationToken).ConfigureAwait(false);
                sent.Add(FormatMac(bytes));
            }
            catch (SocketException)
            {
                // Reported through the return value.
            }
        }

        return sent;
    }

    /// <summary>Parses a MAC address in any common spelling into 6 bytes.</summary>
    /// <exception cref="ArgumentException">Not 12 hex digits.</exception>
    public static byte[] ParseMac(string mac)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mac);
        return ParseHex(mac, MacLength, MacLength);
    }

    /// <summary>Formats 6 bytes as <c>AA:BB:CC:DD:EE:FF</c>.</summary>
    public static string FormatMac(ReadOnlySpan<byte> mac)
    {
        if (mac.Length != MacLength)
        {
            throw new ArgumentException("A MAC address has 6 bytes", nameof(mac));
        }

        return string.Create(17, mac.ToArray(), static (span, bytes) =>
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0)
                {
                    span[(i * 3) - 1] = ':';
                }

                span[i * 3] = ToHexUpper(bytes[i] >> 4);
                span[(i * 3) + 1] = ToHexUpper(bytes[i] & 0xF);
            }
        });
    }

    /// <summary>6 × 0xFF, 16 × MAC, optional SecureOn password.</summary>
    public static byte[] BuildMagicPacket(ReadOnlySpan<byte> mac, ReadOnlySpan<byte> password = default)
    {
        if (mac.Length != MacLength)
        {
            throw new ArgumentException("A MAC address has 6 bytes", nameof(mac));
        }

        if (password.Length is not (0 or 4 or 6))
        {
            throw new ArgumentException("A SecureOn password has 4 or 6 bytes", nameof(password));
        }

        var packet = new byte[6 + (16 * MacLength) + password.Length];
        packet.AsSpan(0, 6).Fill(0xFF);
        for (int i = 0; i < 16; i++)
        {
            mac.CopyTo(packet.AsSpan(6 + (i * MacLength), MacLength));
        }

        password.CopyTo(packet.AsSpan(6 + (16 * MacLength)));
        return packet;
    }

    /// <summary>Limited broadcast plus the directed broadcast of every operational IPv4 adapter (deduplicated).</summary>
    public static IReadOnlyList<IPAddress> BroadcastAddresses()
    {
        var result = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                    {
                        continue;
                    }

                    byte[] ip = unicast.Address.GetAddressBytes();
                    byte[] mask = unicast.IPv4Mask.GetAddressBytes();
                    if (ip.Length != 4 || mask.Length != 4)
                    {
                        continue;
                    }

                    var directed = new byte[4];
                    for (int i = 0; i < 4; i++)
                    {
                        directed[i] = (byte)(ip[i] | (byte)~mask[i]);
                    }

                    var address = new IPAddress(directed);
                    if (!result.Contains(address))
                    {
                        result.Add(address);
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Limited broadcast alone remains.
        }

        return result;
    }

    private static byte[] ParseHex(string value, int minBytes, int maxBytes)
    {
        var digits = new List<char>(maxBytes * 2);
        foreach (char c in value)
        {
            if (char.IsAsciiHexDigit(c))
            {
                digits.Add(c);
            }
            else if (c is not (':' or '-' or '.' or ' '))
            {
                throw new ArgumentException($"'{value}' is not a hex address", nameof(value));
            }
        }

        if (digits.Count % 2 != 0 || digits.Count / 2 < minBytes || digits.Count / 2 > maxBytes)
        {
            throw new ArgumentException($"'{value}' must contain {minBytes * 2}–{maxBytes * 2} hex digits", nameof(value));
        }

        var bytes = new byte[digits.Count / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(new string(new[] { digits[i * 2], digits[(i * 2) + 1] }), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    private static char ToHexUpper(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);
}
