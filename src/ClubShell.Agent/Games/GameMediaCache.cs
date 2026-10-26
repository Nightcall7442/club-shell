using System.Runtime.Versioning;
using ClubShell.Agent.Storage;
using ClubShell.Contracts.Games;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Games;

/// <summary>
/// The catalogue's art and trailers from <c>cache\media</c> (SHELL_CHANGES 46, <c>storage.mediaCache</c>): after every
/// catalogue change the covers and wide art (and, with <see cref="MediaCacheSettings.Videos"/>, the trailers) are
/// downloaded into <see cref="LocalCache"/>, and the games the Shell gets name the cached files by their
/// ProgramData-relative path (<c>cache\media\…</c>), which the Shell loads through its asset protocol. So the art shows
/// without the internet and from a club server on plain http, which the Shell's CSP would not load; a URL not cached yet
/// is passed on unchanged. A cache outside ProgramData (a <c>paths.cache</c> elsewhere) is not used: the Shell's asset
/// scope does not reach it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GameMediaCache : IHostedService, IDisposable
{
    private readonly GameLibrary _library;
    private readonly LocalCache _cache;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly ILogger<GameMediaCache> _logger;
    private readonly SemaphoreSlim _prefetch = new(1, 1);
    private CancellationTokenSource? _stopping;

    /// <summary>Creates the cache view.</summary>
    public GameMediaCache(GameLibrary library, LocalCache cache, IOptionsMonitor<AgentSettings> settings, ILogger<GameMediaCache> logger)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _library = library;
        _cache = cache;
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _library.Changed += OnCatalogChanged;
        Schedule();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _library.Changed -= OnCatalogChanged;
        _stopping?.Cancel();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _library.Changed -= OnCatalogChanged;
        _stopping?.Dispose();
        _prefetch.Dispose();
    }

    /// <summary><paramref name="game"/> with its cached art and trailer named by their ProgramData-relative paths.</summary>
    public Game Localize(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var settings = _settings.CurrentValue;
        if (!settings.Storage.MediaCache.Enabled)
        {
            return game;
        }

        return game with
        {
            CoverUrl = Local(game.CoverUrl, settings) ?? game.CoverUrl,
            HeroUrl = Local(game.HeroUrl, settings) ?? game.HeroUrl,
            VideoUrl = settings.Storage.MediaCache.Videos ? Local(game.VideoUrl, settings) ?? game.VideoUrl : game.VideoUrl,
        };
    }

    /// <summary>Downloads what the current catalogue names and the cache lacks; returns how many of its URLs are cached.</summary>
    public async Task<int> PrefetchAsync(CancellationToken cancellationToken)
    {
        var media = _settings.CurrentValue.Storage.MediaCache;
        if (!media.Enabled)
        {
            return 0;
        }

        await _prefetch.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cache.MaxBytes = (long)media.MaxMb << 20;
            var urls = _library.Snapshot
                .SelectMany(g => media.Videos ? new[] { g.CoverUrl, g.HeroUrl, g.VideoUrl } : [g.CoverUrl, g.HeroUrl])
                .Where(IsRemote)
                .Select(u => u!)
                .ToList();
            int cached = await _cache.PrefetchAsync(urls, cancellationToken).ConfigureAwait(false);
            await _cache.FlushAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Catalogue media cached: {Cached} of {Total}", cached, urls.Distinct(StringComparer.Ordinal).Count());
            return cached;
        }
        finally
        {
            _prefetch.Release();
        }
    }

    private static bool IsRemote(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private string? Local(string? url, AgentSettings settings)
    {
        if (!IsRemote(url) || _cache.MapUrlToLocal(url!) is not { } path)
        {
            return null;
        }

        string relative = Path.GetRelativePath(settings.Paths.ProgramData, path);
        return Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal) ? null : relative.Replace('/', '\\');
    }

    private void OnCatalogChanged(object? sender, string version) => Schedule();

    private void Schedule()
    {
        if (_stopping is not { IsCancellationRequested: false } stopping)
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await PrefetchAsync(stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Caching the catalogue media failed");
                }
            },
            CancellationToken.None);
    }
}
