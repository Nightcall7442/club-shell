using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClubShell.Server.Games;

/// <summary>What a search for a game's trailer gave: a direct file link, nothing (<c>notFound</c>), or no answer (try later).</summary>
public sealed record TrailerSearch(string? Url, bool Transient = false)
{
    public static readonly TrailerSearch NotFound = new((string?)null);

    public static readonly TrailerSearch Unreachable = new((string?)null, Transient: true);
}

/// <summary>Finds a trailer file for a game that has none (D-97).</summary>
public interface ITrailerFinder
{
    Task<TrailerSearch> FindAsync(string title, string? launcher, string? launcherAppId, CancellationToken cancellationToken);
}

/// <summary>
/// The Steam store as a source of trailers (D-97). A Steam game (<c>launcher = steam</c> with its app id) is asked for by
/// id: <c>appdetails?appids={id}&amp;filters=movies</c>. Any other game is looked up by title in the store search
/// (<c>storesearch/?term=…&amp;cc=us&amp;l=en</c>) and taken only when an app's name is the same title, letter for letter
/// once case, punctuation and ™/®/© are dropped — a near title (a sequel, an edition) is not that game. Of its movies,
/// the highlighted ones first, the first with a direct progressive file wins, the best quality first — the club's target
/// is 1080p, which <c>max</c> is (else the highest the movie has): mp4 <c>max</c> (H.264, plays in every WebView2), webm
/// <c>max</c>, and only for a movie without one mp4 <c>480</c>, webm <c>480</c>; a movie the store has only as HLS/DASH (<c>hls_h264</c>,
/// <c>dash_*</c>) is skipped. Steam CDN links are taken as https. One question at a time, at most one per
/// <see cref="TrailerOptions.FinderIntervalMs"/>, each with <see cref="TrailerOptions.FinderTimeoutSec"/>.
/// </summary>
public sealed partial class SteamTrailerFinder(ITrailerWeb web, TrailerOptions options, ILogger<SteamTrailerFinder> logger) : ITrailerFinder
{
    public const string Store = "https://store.steampowered.com/api/";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _last = long.MinValue;

    public async Task<TrailerSearch> FindAsync(string title, string? launcher, string? launcherAppId, CancellationToken cancellationToken)
    {
        string? appId = string.Equals(launcher, "steam", StringComparison.OrdinalIgnoreCase) && launcherAppId is { } id && AppId().IsMatch(id) ? id : null;
        if (appId is null)
        {
            var search = await AskAsync($"{Store}storesearch/?term={Uri.EscapeDataString(title)}&cc=us&l=en", cancellationToken);
            if (search is not { } found)
            {
                return TrailerSearch.Unreachable;
            }

            appId = Match(found, title);
            if (appId is null)
            {
                return TrailerSearch.NotFound;
            }
        }

        var details = await AskAsync($"{Store}appdetails?appids={appId}&filters=movies", cancellationToken);
        return details is { } d ? new TrailerSearch(Pick(d, appId)) : TrailerSearch.Unreachable;
    }

    /// <summary>The id of the store search's app named exactly <paramref name="title"/>, else null.</summary>
    public static string? Match(JsonElement search, string title)
    {
        var wanted = Normalize(title);
        if (wanted.Length == 0 || search.ValueKind != JsonValueKind.Object || !search.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && Normalize(name.GetString()!) == wanted
                && (!item.TryGetProperty("type", out var type) || type.GetString() == "app")
                && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var n))
            {
                return n.ToString(CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    /// <summary>The best direct trailer file of <c>appdetails</c> for <paramref name="appId"/>, else null.</summary>
    public static string? Pick(JsonElement details, string appId)
    {
        if (details.ValueKind != JsonValueKind.Object || !details.TryGetProperty(appId, out var app) || app.ValueKind != JsonValueKind.Object
            || !app.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True
            || !app.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("movies", out var movies) || movies.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var ordered = movies.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.Object)
            .OrderByDescending(m => m.TryGetProperty("highlight", out var h) && h.ValueKind == JsonValueKind.True);
        foreach (var movie in ordered)
        {
            foreach (var (format, size) in new[] { ("mp4", "max"), ("webm", "max"), ("mp4", "480"), ("webm", "480") })
            {
                if (movie.TryGetProperty(format, out var files) && files.ValueKind == JsonValueKind.Object
                    && files.TryGetProperty(size, out var file) && file.ValueKind == JsonValueKind.String
                    && Direct(file.GetString()!) is { } url)
                {
                    return url;
                }
            }
        }

        return null;
    }

    /// <summary>A progressive file link as https, or null for a playlist or anything not a web link.</summary>
    private static string? Direct(string link)
    {
        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        var path = uri.AbsolutePath.ToLowerInvariant();
        if (path.EndsWith(".m3u8", StringComparison.Ordinal) || path.EndsWith(".mpd", StringComparison.Ordinal))
        {
            return null;
        }

        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.ToString();
    }

    /// <summary>A title as the match compares it: lower case, letters and digits, single spaces; <c>&amp;</c> is «and».</summary>
    public static string Normalize(string title)
    {
        // ™ would turn into «tm» below.
        var text = title.Replace("™", "", StringComparison.Ordinal).Replace("®", "", StringComparison.Ordinal).Replace("©", "", StringComparison.Ordinal)
            .ToLowerInvariant().Replace("&", " and ", StringComparison.Ordinal);
        var b = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormKC))
        {
            b.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }

        return Spaces().Replace(b.ToString(), " ").Trim();
    }

    /// <summary>One question to the store, after the polite pause; null when it did not answer with JSON in time.</summary>
    private async Task<JsonElement?> AskAsync(string url, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var wait = _last == long.MinValue ? TimeSpan.Zero : TimeSpan.FromMilliseconds(options.FinderIntervalMs) - Stopwatch.GetElapsedTime(_last);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.FinderTimeoutSec));
            try
            {
                using var response = await web.GetAsync(new Uri(url), timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogInformation("Steam store answered {Status} to {Url}", (int)response.StatusCode, url);
                    return null;
                }

                await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var doc = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
                return doc.RootElement.Clone();
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                logger.LogInformation("Steam store did not answer {Url}: {Error}", url, ex.Message);
                return null;
            }
            finally
            {
                _last = Stopwatch.GetTimestamp();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    [GeneratedRegex("^[0-9]{1,10}$", RegexOptions.CultureInvariant)]
    private static partial Regex AppId();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();
}
