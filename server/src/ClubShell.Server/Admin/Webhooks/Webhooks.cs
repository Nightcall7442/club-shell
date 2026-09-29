using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClubShell.Server.Infrastructure;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Admin;

/// <summary>
/// Club events for the owner's webhooks (<c>AdminClubEvent</c>, DESIGN §8): an event is written to <c>webhook_outbox</c>
/// once per enabled webhook subscribed to it, in the transaction of the action that caused it — so it goes out only if
/// that action commits, and survives a restart. <see cref="WebhookWorker"/> delivers it: <c>POST url</c> with
/// <c>AdminWebhookPayload {event, at, text, data}</c> and <c>X-ClubShell-Event</c>.
/// </summary>
public static class Webhooks
{
    /// <summary>The <c>events</c> reference of <c>GET /admin/club</c>, in the contract's order.</summary>
    public static readonly string[] Events = ["shiftClosed", "pcOffline", "bigTopup", "lowStock", "ruleFired", "sessionOpened", "suspicious", "hardware"];

    public static Task EnqueueAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid clubId, string @event, DateTimeOffset at, string text, object data) =>
        Events.Contains(@event, StringComparer.Ordinal)
            ? c.ExecuteAsync(
                """
                INSERT INTO webhook_outbox (club_id, webhook_id, event, payload, next_at, created_at)
                SELECT club_id, id, @event, @payload::jsonb, @at, @at FROM webhooks WHERE club_id = @clubId AND enabled AND @event = ANY(events)
                """,
                new
                {
                    clubId, @event, at,
                    payload = ServerJson.Jsonb(JsonSerializer.Serialize(new { @event, at = ServerJson.FormatTime(at), text, data }, AdminJson.Options)),
                },
                tx)
            : throw new ArgumentException($"Unknown club event {@event}", nameof(@event));

    private static readonly System.Globalization.NumberFormatInfo Spaced = new() { NumberGroupSeparator = " ", NumberDecimalDigits = 0 };

    /// <summary>Tiyin as the mock's texts write them: <c>12 000 сум</c> (no culture data needed).</summary>
    public static string Sum(long tiyin) => Math.Round(Math.Abs(tiyin) / 100m, MidpointRounding.AwayFromZero).ToString("N0", Spaced) + " сум";
}

/// <summary>
/// The SSRF guard of webhook delivery (DESIGN §8, OQ-11): a webhook may point only at a public address. Refused: loopback,
/// private (RFC 1918, CGNAT 100.64/10, IPv6 ULA fc00::/7), link-local (169.254/16 — the cloud metadata address
/// 169.254.169.254 is in it — and fe80::/10), unspecified, multicast, broadcast, reserved, and the IPv4-mapped, NAT64
/// (<c>64:ff9b::/96</c>, <c>64:ff9b:1::/48</c>), 6to4 (<c>2002::/16</c>) and IPv4-compatible (<c>::/96</c>) forms of all of
/// them; the IPv6 discard (<c>100::/64</c>) and documentation (<c>2001:db8::/32</c>) prefixes are refused outright. A host name is resolved and must have only allowed addresses; <see cref="WebhookClient"/> checks the address it
/// actually connects to again, so a name that re-resolves to a private address after the check still gets nowhere.
/// </summary>
public static class WebhookTargets
{
    public static bool IsAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224
                || (b[0] == 100 && b[1] >= 64 && b[1] < 128)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] < 32)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && b[1] is 18 or 19));
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                    || (b[0] & 0xFE) == 0xFC
                    || address.IsIPv6Teredo
                    || (b[0] == 0x01 && Zero(b, 1, 7)) // 100::/64, the discard prefix
                    || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)) // 2001:db8::/32, documentation
                && EmbeddedIPv4(b).All(IsAllowed);
        }

        return false;
    }

    /// <summary>
    /// The IPv4 addresses a gateway would reach through an IPv6 one, which must pass the IPv4 denylist too: NAT64
    /// (<c>64:ff9b::/96</c>, and the local-use <c>64:ff9b:1::/48</c> — its RFC 6052 slots, bytes 6–7 and 9–10, and the last 32
    /// bits), 6to4 (<c>2002::/16</c>, bytes 2–5) and IPv4-compatible (<c>::/96</c>).
    /// </summary>
    private static IEnumerable<IPAddress> EmbeddedIPv4(byte[] b)
    {
        var nat64 = b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B;
        if ((nat64 && Zero(b, 4, 8)) || Zero(b, 0, 12))
        {
            yield return new IPAddress(b.AsSpan(12, 4));
        }

        if (nat64 && b[4] == 0x00 && b[5] == 0x01)
        {
            yield return new IPAddress(b.AsSpan(12, 4));
            yield return new IPAddress(new byte[] { b[6], b[7], b[9], b[10] });
        }

        if (b[0] == 0x20 && b[1] == 0x02)
        {
            yield return new IPAddress(b.AsSpan(2, 4));
        }
    }

    private static bool Zero(byte[] bytes, int start, int count) => bytes.AsSpan(start, count).IndexOfAnyExcept((byte)0) < 0;

    /// <summary>An http(s) URL whose host resolves only to allowed addresses; false for anything else (and on a DNS failure).</summary>
    public static async Task<bool> IsAllowedAsync(Uri url, CancellationToken cancellationToken = default)
    {
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) || url.UserInfo.Length > 0)
        {
            return false;
        }

        if (IPAddress.TryParse(url.IdnHost, out var literal))
        {
            return IsAllowed(literal);
        }

        if (url.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) || url.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(url.IdnHost, cancellationToken);
            return addresses.Length > 0 && addresses.All(IsAllowed);
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

/// <summary>
/// The HTTP side of webhook delivery: one POST, 5 s timeout, no redirects; the HTTP status or 0 for a network error, a
/// timeout or a refused address. Tests replace it (<c>WebhookTests</c>); the SSRF check before it stays in the worker.
/// </summary>
public class WebhookClient
{
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, cancellationToken) =>
        {
            // The address really connected to, after DNS: a rebinding name cannot slip past the check in the worker.
            var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
            var allowed = addresses.Where(WebhookTargets.IsAllowed).ToArray();
            if (allowed.Length == 0 || allowed.Length != addresses.Length)
            {
                throw new HttpRequestException("Webhook address refused");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    public virtual async Task<int> PostAsync(Uri url, string @event, string payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-ClubShell-Event", @event);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return (int)response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
    }
}
