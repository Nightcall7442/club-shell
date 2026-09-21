using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.Versioning;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Serialization;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Games;

/// <summary>
/// Local games catalogue: server catalogue (ETag-cached, persisted atomically to <c>cache\games.json</c>) merged with
/// <see cref="GameDetector"/> install results. Snapshots are immutable and swapped atomically.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GameLibrary : IDisposable
{
    private const string CacheFileName = "games.json";
    private const string EtagFileName = "games.etag";
    private const int ServerPageSize = 500;

    private readonly IServerClient _server;
    private readonly GameDetector _detector;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<GameLibrary> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private CatalogSnapshot _snapshot = CatalogSnapshot.Empty;

    /// <summary>Creates the library (call <see cref="LoadAsync"/> at startup).</summary>
    public GameLibrary(IServerClient server, GameDetector detector, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<GameLibrary> logger)
    {
        _server = server;
        _detector = detector;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Raised after the catalogue or install state changed; argument is the new <see cref="CatalogVersion"/>.</summary>
    public event EventHandler<string>? Changed;

    /// <summary>Catalogue version (server ETag-like value; <c>"0"</c> before any load).</summary>
    public string CatalogVersion => Volatile.Read(ref _snapshot).Version;

    /// <summary>Current games with install state merged in.</summary>
    public ImmutableArray<Game> Snapshot => Volatile.Read(ref _snapshot).Games;

    /// <summary>Number of games in the catalogue.</summary>
    public int Count => Snapshot.Length;

    /// <summary>When the catalogue was last fetched from the server.</summary>
    public DateTimeOffset? LastRefreshAt { get; private set; }

    /// <summary>Path of the persisted catalogue.</summary>
    public string CachePath => Path.Combine(_settings.CurrentValue.CacheDir, CacheFileName);

    /// <summary>Loads the persisted catalogue, then tries a server refresh (network failure is logged, not thrown).</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await LoadCacheAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshAsync(force: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ServerApiException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Catalogue refresh failed; serving {Count} cached games (version {Version})", Count, CatalogVersion);
            if (Count > 0)
            {
                await RescanAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Fetches the catalogue from the server (all pages), using the stored ETag unless <paramref name="force"/>;
    /// merges install detection and persists. Returns <see langword="true"/> when the catalogue changed.
    /// </summary>
    public async Task<bool> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CatalogSnapshot current = Volatile.Read(ref _snapshot);
            string? etag = force ? null : current.ETag;
            string? zone = _settings.CurrentValue.Zone;
            EtagResponse<GamesListResponse> first = await _server.GetGamesAsync(zone, 1, ServerPageSize, etag, cancellationToken).ConfigureAwait(false);
            LastRefreshAt = _clock.UtcNow;
            if (first.NotModified)
            {
                _logger.LogDebug("Catalogue unchanged (ETag {ETag})", etag);
                return false;
            }

            GamesListResponse page = first.Require();
            var games = new List<Game>(page.Total > 0 ? page.Total : page.Items.Count);
            games.AddRange(page.Items);
            for (int n = 2; games.Count < page.Total && page.Items.Count > 0; n++)
            {
                page = (await _server.GetGamesAsync(zone, n, ServerPageSize, null, cancellationToken).ConfigureAwait(false)).Require();
                games.AddRange(page.Items);
            }

            IReadOnlyDictionary<Guid, GameInstallStatus> installs = await _detector.DetectAllAsync(games, cancellationToken).ConfigureAwait(false);
            var next = CatalogSnapshot.Create(Merge(games, installs), page.CatalogVersion, first.ETag);
            await PersistAsync(next, cancellationToken).ConfigureAwait(false);
            Swap(next);
            _logger.LogInformation("Catalogue refreshed: {Count} games, version {Version}", next.Games.Length, next.Version);
            return true;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Re-runs install detection over the current catalogue without contacting the server.</summary>
    public async Task RescanAsync(CancellationToken cancellationToken)
    {
        _detector.Invalidate();
        CatalogSnapshot current = Volatile.Read(ref _snapshot);
        if (current.Games.IsEmpty)
        {
            return;
        }

        IReadOnlyDictionary<Guid, GameInstallStatus> installs = await _detector.DetectAllAsync(current.Games, cancellationToken).ConfigureAwait(false);
        var next = CatalogSnapshot.Create(Merge(current.Games, installs), current.Version, current.ETag);
        if (!next.Games.SequenceEqual(current.Games))
        {
            Swap(next);
        }
    }

    /// <summary>Handles a <c>refreshConfig</c> command; returns <see langword="true"/> when the games cache was refreshed.</summary>
    public async Task<bool> HandleRefreshConfigAsync(RefreshConfigCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Games != true && !command.IsAll)
        {
            return false;
        }

        await RefreshAsync(force: true, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Game by id from the current snapshot, or <see langword="null"/>.</summary>
    public Game? Get(Guid gameId) => Volatile.Read(ref _snapshot).ById.TryGetValue(gameId, out Game? game) ? game : null;

    /// <summary>Game by id; falls back to the server when unknown locally (returns <see langword="null"/> on 404 / offline).</summary>
    public async Task<Game?> GetAsync(Guid gameId, CancellationToken cancellationToken)
    {
        if (Get(gameId) is { } local)
        {
            return local;
        }

        try
        {
            Game remote = await _server.GetGameAsync(gameId, cancellationToken).ConfigureAwait(false);
            GameInstallStatus status = await _detector.DetectAsync(remote, cancellationToken).ConfigureAwait(false);
            return Apply(remote, status);
        }
        catch (ServerApiException ex)
        {
            _logger.LogDebug(ex, "Game {GameId} not available from server", gameId);
            return null;
        }
    }

    /// <summary>Fresh install status for one game (bypasses the snapshot's cached merge).</summary>
    public async Task<GameInstallStatus> GetInstallStatusAsync(Guid gameId, CancellationToken cancellationToken)
    {
        Game game = Get(gameId) ?? throw IpcError.NotFound($"Game {gameId}").ToException();
        return await _detector.DetectAsync(game, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Filters, sorts and pages the current snapshot.</summary>
    public Task<GamesListResponse> ListAsync(GamesListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        CatalogSnapshot snapshot = Volatile.Read(ref _snapshot);
        IEnumerable<Game> query = snapshot.Games;

        if (!string.IsNullOrWhiteSpace(request.Category))
        {
            query = query.Where(g => g.Category.Any(c => c.Equals(request.Category, StringComparison.OrdinalIgnoreCase)));
        }

        if (request.Launcher is { } launcher)
        {
            query = query.Where(g => g.Launcher == launcher);
        }

        if (request.InstalledOnly == true)
        {
            query = query.Where(g => g.Installed);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            string term = request.Search.Trim();
            query = query.Where(g => g.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                || g.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        query = (request.Sort ?? GamesSort.Popularity) switch
        {
            GamesSort.Title => query.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase),
            GamesSort.LastPlayed => query.OrderByDescending(g => g.LastPlayedAt ?? DateTimeOffset.MinValue).ThenByDescending(g => g.Popularity),
            _ => query.OrderByDescending(g => g.Popularity).ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase),
        };

        List<Game> all = query.ToList();
        int pageSize = Math.Clamp(request.PageSize ?? GamesListRequest.DefaultPageSize, 1, GamesListRequest.MaxPageSize);
        int page = Math.Max(1, request.Page ?? 1);
        List<Game> items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new GamesListResponse(items, all.Count, page, pageSize, snapshot.Version));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _refreshGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Game Apply(Game game, GameInstallStatus status) => game with
    {
        Installed = status.Installed,
        InstallPath = status.InstallPath ?? game.InstallPath,
        Version = status.Version ?? game.Version,
        SizeGb = status.SizeGb > 0 ? status.SizeGb : game.SizeGb,
    };

    private static ImmutableArray<Game> Merge(IEnumerable<Game> games, IReadOnlyDictionary<Guid, GameInstallStatus> installs)
    {
        ImmutableArray<Game>.Builder builder = ImmutableArray.CreateBuilder<Game>();
        foreach (Game game in games)
        {
            builder.Add(installs.TryGetValue(game.Id, out GameInstallStatus? status) ? Apply(game, status) : game with { Installed = false });
        }

        return builder.ToImmutable();
    }

    private void Swap(CatalogSnapshot next)
    {
        Volatile.Write(ref _snapshot, next);
        try
        {
            Changed?.Invoke(this, next.Version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GameLibrary.Changed handler threw");
        }
    }

    private async Task LoadCacheAsync(CancellationToken cancellationToken)
    {
        string path = CachePath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            GamesListResponse? cached;
            await using (FileStream stream = File.OpenRead(path))
            {
                cached = await JsonDefaults.DeserializeAsync<GamesListResponse>(stream, cancellationToken).ConfigureAwait(false);
            }

            if (cached is null)
            {
                return;
            }

            string etagPath = Path.Combine(Path.GetDirectoryName(path)!, EtagFileName);
            string? etag = File.Exists(etagPath) ? (await File.ReadAllTextAsync(etagPath, cancellationToken).ConfigureAwait(false)).Trim() : null;
            Swap(CatalogSnapshot.Create(cached.Items.ToImmutableArray(), cached.CatalogVersion, string.IsNullOrEmpty(etag) ? null : etag));
            _logger.LogInformation("Loaded {Count} games from cache (version {Version})", cached.Items.Count, cached.CatalogVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "Games cache {Path} unreadable; ignoring", path);
        }
    }

    private async Task PersistAsync(CatalogSnapshot snapshot, CancellationToken cancellationToken)
    {
        string path = CachePath;
        string dir = Path.GetDirectoryName(path)!;
        try
        {
            Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            var document = new GamesListResponse(snapshot.Games, snapshot.Games.Length, 1, Math.Max(1, snapshot.Games.Length), snapshot.Version);
            await using (FileStream stream = new(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                await JsonDefaults.SerializeAsync(stream, document, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tmp, path, overwrite: true);
            string etagPath = Path.Combine(dir, EtagFileName);
            if (snapshot.ETag is null)
            {
                File.Delete(etagPath);
            }
            else
            {
                await File.WriteAllTextAsync(etagPath, snapshot.ETag, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Cannot persist games cache to {Path}", path);
        }
    }

    /// <summary>Immutable catalogue state swapped as one reference.</summary>
    private sealed class CatalogSnapshot
    {
        private CatalogSnapshot(ImmutableArray<Game> games, ImmutableDictionary<Guid, Game> byId, string version, string? etag)
        {
            Games = games;
            ById = byId;
            Version = version;
            ETag = etag;
        }

        public static CatalogSnapshot Empty { get; } = new(ImmutableArray<Game>.Empty, ImmutableDictionary<Guid, Game>.Empty, "0", null);

        public ImmutableArray<Game> Games { get; }

        public ImmutableDictionary<Guid, Game> ById { get; }

        public string Version { get; }

        public string? ETag { get; }

        public static CatalogSnapshot Create(ImmutableArray<Game> games, string version, string? etag)
        {
            ImmutableDictionary<Guid, Game>.Builder byId = ImmutableDictionary.CreateBuilder<Guid, Game>();
            foreach (Game game in games)
            {
                byId[game.Id] = game;
            }

            return new CatalogSnapshot(games, byId.ToImmutable(), string.IsNullOrEmpty(version) ? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) : version, etag);
        }
    }
}
