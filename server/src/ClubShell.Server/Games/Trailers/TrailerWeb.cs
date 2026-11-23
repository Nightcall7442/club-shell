using System.Net;
using System.Net.Sockets;
using ClubShell.Server.Admin;

namespace ClubShell.Server.Games;

/// <summary>Where the club server keeps its trailers and how it fetches them (<c>Trailers:*</c>, D-97).</summary>
public sealed class TrailerOptions
{
    /// <summary>Folder of the stored copies (<c>{fileId}.mp4</c>/<c>.webm</c>); relative to the content root, on the data volume.</summary>
    public string StoragePath { get; set; } = "data/trailers";

    /// <summary>The biggest trailer the server takes, uploaded or downloaded.</summary>
    public long MaxBytes { get; set; } = 500L * 1024 * 1024;

    /// <summary>How long one download may take, start to end.</summary>
    public int DownloadTimeoutSec { get; set; } = 300;

    /// <summary>How long one question to the Steam store may take.</summary>
    public int FinderTimeoutSec { get; set; } = 15;

    /// <summary>The least time between two questions to the Steam store (a polite rate).</summary>
    public int FinderIntervalMs { get; set; } = 1000;

    /// <summary>Look for a trailer of every game without one by itself (on seed, on a new or edited card); the owner's button works either way.</summary>
    public bool AutoFind { get; set; } = true;
}

/// <summary>
/// The internet as the trailer store sees it: one GET whose answer the caller reads (headers first) and disposes. The
/// finder and the downloader never touch <see cref="HttpClient"/> themselves, so tests answer with canned bytes.
/// </summary>
public interface ITrailerWeb
{
    Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>
/// The real <see cref="ITrailerWeb"/>: http(s) only, at most five redirects, and — as webhook delivery (DESIGN §8) — only
/// public addresses, checked on the address actually connected to: a link the owner pasted cannot make the server read
/// its own network (<c>169.254.169.254</c>, the club's router). No proxy, as the webhooks.
/// </summary>
public sealed class TrailerWeb : ITrailerWeb, IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
            var allowed = addresses.Where(WebhookTargets.IsAllowed).ToArray();
            if (allowed.Length == 0 || allowed.Length != addresses.Length)
            {
                throw new HttpRequestException("Trailer address refused");
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
        // Each caller sets its own deadline (a download takes minutes, a store question seconds).
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestHeaders = { { "User-Agent", "ClubShell-Server" }, { "Accept-Language", "en" } },
    };

    public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        if (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
        {
            throw new HttpRequestException("Only http(s) links");
        }

        return _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>What a trailer file is, by its first bytes — never by its name or the type the sender claims.</summary>
public static class TrailerFormat
{
    /// <summary>Bytes enough to tell a video from a page or a playlist.</summary>
    public const int HeadBytes = 4096;

    /// <summary>
    /// <c>video/mp4</c> (an ISO BMFF <c>ftyp</c> box, not a HEIF/AVIF picture or a QuickTime <c>.mov</c>), <c>video/webm</c>
    /// (an EBML header of DocType <c>webm</c>; plain Matroska is not one), else null.
    /// </summary>
    public static string? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 12 && b[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = b[8..12];
            return brand.SequenceEqual("qt  "u8) || brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("mif1"u8)
                || brand.SequenceEqual("msf1"u8) || brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8)
                ? null
                : "video/mp4";
        }

        if (b.Length >= 4 && b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3)
        {
            return b[..Math.Min(b.Length, 64)].IndexOf("webm"u8) >= 0 ? "video/webm" : null;
        }

        return null;
    }

    /// <summary>
    /// Why bytes that are not a video are refused: <c>streamingFormat</c> for an HLS (<c>#EXTM3U</c>) or DASH (<c>&lt;MPD</c>)
    /// playlist — the shell plays one file, not a stream of pieces — else <c>notAVideo</c> (a web page such as YouTube's,
    /// a picture, an error text).
    /// </summary>
    public static string Refusal(ReadOnlySpan<byte> head, string? contentType)
    {
        var text = System.Text.Encoding.UTF8.GetString(head[..Math.Min(head.Length, 1024)]).TrimStart('﻿', ' ', '\t', '\r', '\n');
        var type = contentType?.ToLowerInvariant() ?? "";
        return text.StartsWith("#EXTM3U", StringComparison.Ordinal) || text.Contains("<MPD", StringComparison.Ordinal)
            || type.Contains("mpegurl", StringComparison.Ordinal) || type.Contains("dash+xml", StringComparison.Ordinal)
            ? "streamingFormat"
            : "notAVideo";
    }

    /// <summary>A link to a video site's page, never a file: refused without downloading it.</summary>
    public static bool IsVideoPage(Uri url)
    {
        var host = url.IdnHost.ToLowerInvariant();
        string[] pages = ["youtube.com", "youtu.be", "vimeo.com", "rutube.ru", "vk.com", "vkvideo.ru", "twitch.tv", "dailymotion.com", "tiktok.com"];
        return pages.Any(p => host == p || host.EndsWith("." + p, StringComparison.Ordinal));
    }

    public static string Extension(string contentType) => contentType == "video/webm" ? ".webm" : ".mp4";
}
