using System.Net;
using ClubShell.Agent.Games;
using ClubShell.Agent.Storage;
using ClubShell.Contracts.Games;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Tests;

/// <summary>SHELL_CHANGES 46: the Shell gets the catalogue's cached art and trailers as <c>cache\media\…</c> paths.</summary>
public sealed class GameMediaCacheTests : IDisposable
{
    private const string Cover = "https://cdn.example.com/cs2/cover.jpg";
    private const string Hero = "http://10.0.0.5/media/cs2-hero.png";
    private const string Video = "https://cdn.example.com/cs2/trailer.mp4";

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("clubshell-media-");
    private readonly AgentSettings _settings = new();
    private readonly IOptionsMonitor<AgentSettings> _options = Substitute.For<IOptionsMonitor<AgentSettings>>();

    public GameMediaCacheTests()
    {
        _settings.Paths.ProgramData = _dir.FullName;
        _options.CurrentValue.Returns(_settings);
    }

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public async Task Cached_art_is_given_by_its_programdata_relative_path_and_the_rest_unchanged()
    {
        using var cache = NewCache();
        await cache.GetOrDownloadAsync(Cover, null, CancellationToken.None);
        await cache.GetOrDownloadAsync(Hero, null, CancellationToken.None);
        await cache.GetOrDownloadAsync(Video, null, CancellationToken.None);
        using var media = NewMedia(cache);

        var game = media.Localize(Game(cover: Cover, hero: Hero, video: Video));

        game.CoverUrl.Should().Be(@"cache\media\" + LocalCache.KeyFor(Cover));
        game.HeroUrl.Should().Be(@"cache\media\" + LocalCache.KeyFor(Hero));
        game.VideoUrl.Should().Be(Video, "trailers are cached only with storage.mediaCache.videos");
        File.Exists(Path.Combine(_dir.FullName, "cache", "media", LocalCache.KeyFor(Cover))).Should().BeTrue();

        _settings.Storage.MediaCache.Videos = true;
        media.Localize(Game(cover: Cover, hero: Hero, video: Video)).VideoUrl.Should().Be(@"cache\media\" + LocalCache.KeyFor(Video));

        media.Localize(Game(cover: "https://cdn.example.com/other.jpg", hero: null, video: null))
            .Should().Match<Game>(g => g.CoverUrl == "https://cdn.example.com/other.jpg" && g.HeroUrl == null && g.VideoUrl == null);
    }

    [Fact]
    public async Task Nothing_is_rewritten_when_the_cache_is_off()
    {
        using var cache = NewCache();
        await cache.GetOrDownloadAsync(Cover, null, CancellationToken.None);
        using var media = NewMedia(cache);

        _settings.Storage.MediaCache.Enabled = false;
        media.Localize(Game(cover: Cover, hero: null, video: null)).CoverUrl.Should().Be(Cover);
    }

    [Fact]
    public async Task A_cache_outside_programdata_is_not_used()
    {
        _settings.Paths.ProgramData = Path.Combine(_dir.FullName, "ProgramData");
        _settings.Paths.Cache = Path.Combine(_dir.FullName, "elsewhere");
        using var cache = NewCache();
        (await cache.GetOrDownloadAsync(Cover, null, CancellationToken.None)).Should().StartWith(_settings.Paths.Cache);
        using var media = NewMedia(cache);

        media.Localize(Game(cover: Cover, hero: null, video: null)).CoverUrl.Should().Be(Cover, "the Shell's asset scope does not reach it");
    }

    private LocalCache NewCache()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new Bytes()));
        return new LocalCache(factory, _options, SystemClock.Instance, NullLogger<LocalCache>.Instance);
    }

    private GameMediaCache NewMedia(LocalCache cache)
    {
        var library = new GameLibrary(
            Substitute.For<IServerClient>(), new GameDetector(_options, SystemClock.Instance, NullLogger<GameDetector>.Instance), _options,
            SystemClock.Instance, NullLogger<GameLibrary>.Instance);
        return new GameMediaCache(library, cache, _options, NullLogger<GameMediaCache>.Instance);
    }

    private static Game Game(string cover, string? hero, string? video) => new(
        Guid.NewGuid(), "Counter-Strike 2", LauncherType.Steam, "730", null, null, null, false, ["shooter"], [], cover, hero, video, "", 0, 0,
        null, false, AntiCheatKind.None, null, 1.5);

    private sealed class Bytes : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
    }
}
