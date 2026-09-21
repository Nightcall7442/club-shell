using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Http;
using ClubShell.Core.Security;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Storage;

/// <summary>
/// Content cache for covers, videos, ads and themes under <c>cache\media\</c>: files are streamed to a temporary
/// name and renamed atomically, keyed by the SHA-256 of their URL, verified against an optional SHA-256, tracked in
/// <c>index.json</c> and evicted least-recently-used once the directory exceeds <see cref="MaxBytes"/>. The Shell
/// serves the returned absolute paths through the Tauri asset protocol.
/// </summary>
public sealed class LocalCache : IDisposable
{
    /// <summary>Directory name under <c>cache\</c>.</summary>
    public const string MediaDirName = "media";

    /// <summary>Index file name inside <see cref="RootDirectory"/>.</summary>
    public const string IndexFileName = "index.json";

    private const int IndexVersion = 1;
    private const int BufferBytes = 1 << 16;
    private const string TempExtension = ".tmp";
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<LocalCache> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<string>> _inflight = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _downloads;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly object _loadGate = new();
    private bool _loaded;
    private bool _disposed;

    /// <summary>Creates the cache; <paramref name="maxConcurrentDownloads"/> bounds parallel fetches.</summary>
    public LocalCache(IHttpClientFactory httpClientFactory, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<LocalCache> logger, int maxConcurrentDownloads = 4)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentDownloads, 1);
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _clock = clock;
        _logger = logger;
        _downloads = new SemaphoreSlim(maxConcurrentDownloads, maxConcurrentDownloads);
    }

    /// <summary>Size cap; the least recently used files are deleted when exceeded. Default 4 GiB.</summary>
    public long MaxBytes { get; set; } = 4L << 30;

    /// <summary>Absolute cache directory (<c>cache\media</c>).</summary>
    public string RootDirectory => Path.Combine(_settings.CurrentValue.CacheDir, MediaDirName);

    /// <summary>Number of cached files.</summary>
    public int Count
    {
        get
        {
            EnsureLoaded();
            return _entries.Count;
        }
    }

    /// <summary>Bytes on disk according to the index.</summary>
    public long TotalBytes
    {
        get
        {
            EnsureLoaded();
            long total = 0;
            foreach (var entry in _entries.Values)
            {
                total += entry.Size;
            }

            return total;
        }
    }

    /// <summary>
    /// Local path of <paramref name="url"/>, downloading it when absent or when <paramref name="sha256"/> differs
    /// from the cached hash. Concurrent calls for the same URL share one download. Throws
    /// <see cref="HttpRequestException"/>, <see cref="IOException"/> or <see cref="InvalidDataException"/> (hash mismatch).
    /// </summary>
    public async Task<string> GetOrDownloadAsync(string url, string? sha256, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureLoaded();
        var key = KeyFor(url);
        if (TryGetFresh(key, sha256, out var path))
        {
            return path;
        }

        var download = _inflight.GetOrAdd(key, _ => DownloadAsync(key, url, sha256));
        try
        {
            return await download.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _inflight.TryRemove(new KeyValuePair<string, Task<string>>(key, download));
        }
    }

    /// <summary>Downloads every URL not yet cached (bounded concurrency); failures are logged. Returns the number of URLs now cached.</summary>
    public async Task<int> PrefetchAsync(IEnumerable<string> urls, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(urls);
        var distinct = urls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.Ordinal).ToArray();
        var results = await Task.WhenAll(distinct.Select(u => PrefetchOneAsync(u, cancellationToken))).ConfigureAwait(false);
        var cached = 0;
        foreach (var ok in results)
        {
            if (ok)
            {
                cached++;
            }
        }

        return cached;
    }

    /// <summary>Local path of an already cached <paramref name="url"/> (marks it recently used), or <see langword="null"/>.</summary>
    public string? MapUrlToLocal(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || _disposed)
        {
            return null;
        }

        EnsureLoaded();
        return TryGetFresh(KeyFor(url), null, out var path) ? path : null;
    }

    /// <summary>Writes the index (last-access times) to disk.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        EnsureLoaded();
        await SaveIndexAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes every cached file and the index.</summary>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        EnsureLoaded();
        foreach (var entry in _entries.Values)
        {
            if (_entries.TryRemove(entry.Key, out var removed))
            {
                TryDelete(PathOf(removed));
            }
        }

        await SaveIndexAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _downloads.Dispose();
        _indexLock.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Cache file name for <paramref name="url"/>: 40 hex characters of its SHA-256 plus the URL's extension.</summary>
    public static string KeyFor(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var hash = Signing.Sha256Hex(Encoding.UTF8.GetBytes(url));
        var extension = string.Empty;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var candidate = Path.GetExtension(uri.AbsolutePath);
            if (candidate.Length is > 1 and <= 8 && candidate.Skip(1).All(char.IsAsciiLetterOrDigit))
            {
                extension = candidate;
            }
        }

        return string.Concat(hash.AsSpan(0, 40), extension);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; picked up by a later eviction.
        }
    }

    private string PathOf(CacheEntry entry) => Path.Combine(RootDirectory, entry.FileName);

    private string IndexPath => Path.Combine(RootDirectory, IndexFileName);

    private bool TryGetFresh(string key, string? sha256, out string path)
    {
        path = string.Empty;
        if (!_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (sha256 is not null && !string.Equals(entry.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var file = PathOf(entry);
        if (!File.Exists(file))
        {
            _ = _entries.TryRemove(key, out _);
            return false;
        }

        _entries[key] = entry with { LastAccessAt = _clock.UtcNow };
        path = file;
        return true;
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_loadGate)
        {
            if (_loaded)
            {
                return;
            }

            LoadIndex();
            _loaded = true;
        }
    }

    private void LoadIndex()
    {
        var path = IndexPath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var document = JsonSerializer.Deserialize<IndexDocument>(File.ReadAllBytes(path), JsonOptions);
            if (document is null)
            {
                return;
            }

            foreach (var entry in document.Entries)
            {
                if (entry is { Key.Length: > 0, FileName.Length: > 0 } && File.Exists(PathOf(entry)))
                {
                    _entries[entry.Key] = entry;
                }
            }

            _logger.LogInformation("Media cache index loaded: {Count} files", _entries.Count);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Media cache index {Path} is unreadable; starting empty", path);
        }
    }

    private async Task SaveIndexAsync(CancellationToken cancellationToken)
    {
        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(RootDirectory);
            var document = new IndexDocument(IndexVersion, _entries.Values.ToArray());
            var temp = IndexPath + TempExtension;
            await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions), cancellationToken).ConfigureAwait(false);
            File.Move(temp, IndexPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Media cache index could not be written");
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task<bool> PrefetchOneAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            _ = await GetOrDownloadAsync(url, null, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or UriFormatException)
        {
            _logger.LogWarning(ex, "Prefetch of {Url} failed", url);
            return false;
        }
    }

    private async Task<string> DownloadAsync(string key, string url, string? expectedSha256)
    {
        using var timeout = new CancellationTokenSource(DownloadTimeout);
        var token = timeout.Token;
        await _downloads.WaitAsync(token).ConfigureAwait(false);
        var root = RootDirectory;
        var final = Path.Combine(root, key);
        var temp = final + TempExtension;
        try
        {
            Directory.CreateDirectory(root);
            using var client = _httpClientFactory.CreateClient(RetryPolicy.DownloadHttpClientName);
            using var response = await client.GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            _ = response.EnsureSuccessStatusCode();

            long size = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.Asynchronous);
                await using (target.ConfigureAwait(false))
                {
                    var buffer = new byte[BufferBytes];
                    int read;
                    while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, read);
                        size += read;
                    }

                    await target.FlushAsync(token).ConfigureAwait(false);
                }
            }

            var sha256 = Signing.ToHex(hash.GetHashAndReset());
            if (expectedSha256 is not null && !string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(temp);
                throw new InvalidDataException($"SHA-256 of {url} does not match the expected hash");
            }

            File.Move(temp, final, overwrite: true);
            _entries[key] = new CacheEntry(key, url, key, size, sha256, _clock.UtcNow);
            _logger.LogDebug("Cached {Url} as {File} ({Bytes} bytes)", url, key, size);
            Evict();
            await SaveIndexAsync(token).ConfigureAwait(false);
            return final;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        finally
        {
            _downloads.Release();
        }
    }

    private void Evict()
    {
        var cap = MaxBytes;
        if (cap <= 0)
        {
            return;
        }

        long total = 0;
        foreach (var entry in _entries.Values)
        {
            total += entry.Size;
        }

        if (total <= cap)
        {
            return;
        }

        var freed = 0L;
        var removed = 0;
        foreach (var entry in _entries.Values.OrderBy(e => e.LastAccessAt))
        {
            if (total - freed <= cap)
            {
                break;
            }

            if (_entries.TryRemove(entry.Key, out var victim))
            {
                TryDelete(PathOf(victim));
                freed += victim.Size;
                removed++;
            }
        }

        _logger.LogInformation("Media cache evicted {Count} files ({Bytes} bytes) to stay under {Cap} bytes", removed, freed, cap);
    }

    /// <summary>One cached file.</summary>
    private sealed record CacheEntry(string Key, string Url, string FileName, long Size, string Sha256, DateTimeOffset LastAccessAt);

    /// <summary>On-disk index.</summary>
    private sealed record IndexDocument(int Version, IReadOnlyList<CacheEntry> Entries);
}
