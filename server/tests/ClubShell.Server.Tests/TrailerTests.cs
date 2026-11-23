using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClubShell.Server.Games;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>The internet of the trailer store, canned: each URL answers what a test put there, anything else 404.</summary>
public sealed class FakeTrailerWeb : ITrailerWeb
{
    private readonly ConcurrentDictionary<string, (HttpStatusCode Status, string Type, byte[] Body)> _routes = new(StringComparer.Ordinal);

    public ConcurrentQueue<string> Requests { get; } = new();

    public void Serve(string url, byte[] body, string type = "application/octet-stream", HttpStatusCode status = HttpStatusCode.OK) =>
        _routes[new Uri(url).AbsoluteUri] = (status, type, body);

    public void ServeJson(string url, string json) => Serve(url, Encoding.UTF8.GetBytes(json), "application/json");

    public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        Requests.Enqueue(url.AbsoluteUri);
        var response = _routes.TryGetValue(url.AbsoluteUri, out var route)
            ? new HttpResponseMessage(route.Status) { Content = new ByteArrayContent(route.Body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(_routes.TryGetValue(url.AbsoluteUri, out var r) ? r.Type : "text/html");
        return Task.FromResult(response);
    }
}

/// <summary>The example catalog, canned internet, the store on a temp folder, at most 1 MB a trailer, no pause between store questions.</summary>
public sealed class TrailerServerFixture : ServerFixture
{
    public const long MaxBytes = 1024 * 1024;

    public FakeTrailerWeb Web { get; } = new();

    public TrailerServerFixture()
    {
        Settings["Catalog:GamesSeedPath"] = ExampleCatalogFixture.ConfigFile("games.example.json");
        Settings["Trailers:StoragePath"] = Path.Combine(DataDir, "trailers");
        Settings["Trailers:MaxBytes"] = MaxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Settings["Trailers:FinderIntervalMs"] = "0";
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton<ITrailerWeb>(Web));
    }

    /// <summary>Worker passes until nothing is due.</summary>
    public async Task DrainAsync()
    {
        var worker = Services.GetRequiredService<TrailerWorker>();
        for (var i = 0; i < 50 && await worker.RunOnceAsync() > 0; i++)
        {
        }
    }
}

/// <summary>
/// D-97: the club server keeps every game's trailer — uploaded by the owner, downloaded once from a link, or found in the
/// Steam store — and serves it to the PCs with byte ranges; the PCs get its URL in the catalog.
/// </summary>
public sealed class TrailerTests(TrailerServerFixture server) : IClassFixture<TrailerServerFixture>
{
    /// <summary>The start of an MP4 (<c>ftyp isom</c>) and some bytes after it.</summary>
    private static byte[] Mp4(int size = 4000)
    {
        var bytes = new byte[size];
        new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D, 0x00, 0x00, 0x02, 0x00 }.CopyTo(bytes, 0);
        for (var i = 16; i < size; i++)
        {
            bytes[i] = (byte)(i * 7);
        }

        return bytes;
    }

    private static readonly byte[] Webm = [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81, 0x01, 0x42, 0x82, 0x84, .. "webm"u8, 0x42, 0x87, 0x81, 0x04, 0, 0, 0, 0];

    private static readonly byte[] Html = "<!doctype html><html><head><title>YouTube</title></head><body></body></html>"u8.ToArray();

    private static readonly byte[] Hls = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-STREAM-INF:BANDWIDTH=800000\n480.m3u8\n"u8.ToArray();

    private async Task<HttpResponseMessage> UploadAsync(string token, Guid gameId, byte[] bytes, string type = "video/mp4")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/games/{gameId}/trailer") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var raw = server.CreateDefaultClient();
        return await raw.SendAsync(request);
    }

    private async Task<(int Status, JsonElement Body)> SendRawAsync(HttpMethod method, string path, string token, object? body = null)
    {
        using var raw = server.CreateDefaultClient();
        using var response = await raw.SendAsync(Request(method, path, token, body));
        return ((int)response.StatusCode, await Players.ReadAsync(response, (int)response.StatusCode));
    }

    private async Task<Guid> AddGameAsync(string owner, object card)
    {
        var (status, body) = await SendRawAsync(HttpMethod.Post, "/games", owner, card);
        Assert.Equal(200, status);
        return body.GetProperty("game").GetProperty("id").GetGuid();
    }

    private async Task<JsonElement?> AdminTrailerAsync(string owner, Guid gameId)
    {
        var (_, list) = await SendRawAsync(HttpMethod.Get, "/games", owner);
        var game = list.GetProperty("items").EnumerateArray().Single(g => g.GetProperty("id").GetGuid() == gameId);
        var trailer = game.GetProperty("trailer");
        return trailer.ValueKind == JsonValueKind.Null ? null : trailer;
    }

    /// <summary>A text field of the agent's game; the agent's JSON leaves a null out.</summary>
    private static string? Text(JsonElement game, string name) =>
        game.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task<JsonElement> AgentGameAsync(TestAgent agent, Guid gameId) =>
        (await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, "/api/v1/games"), 200)).GetProperty("items").EnumerateArray()
        .Single(g => g.GetProperty("id").GetGuid() == gameId);

    [Fact]
    public async Task The_owner_uploads_a_video_and_the_pcs_play_it_from_the_server_with_byte_ranges()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var gameId = await AddGameAsync(owner, new { title = "Upload Test", launcher = "exe", exePath = @"G:\Up\up.exe", videoUrl = "https://cdn.example.com/up-old.mp4" });
        var version = await Players.ScalarAsync<int>(server, "SELECT catalog_version FROM clubs");
        var video = Mp4();

        using var ok = await UploadAsync(owner, gameId, video, "application/octet-stream");
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var trailer = JsonElement.Parse(await ok.Content.ReadAsStringAsync()).GetProperty("trailer");
        Assert.Equal(("ready", "upload", "video/mp4", video.Length), (trailer.GetProperty("status").GetString(), trailer.GetProperty("origin").GetString(),
            trailer.GetProperty("contentType").GetString(), trailer.GetProperty("size").GetInt32()));
        var url = trailer.GetProperty("url").GetString()!;
        Assert.StartsWith("/api/v1/trailers/", url);
        Assert.Equal(version + 1, await Players.ScalarAsync<int>(server, "SELECT catalog_version FROM clubs"));

        // The PCs get the stored copy, relative to the server (the agent puts its address in front); the card's old link is gone.
        var agent = await TestAgent.CreateAsync(server);
        var seen = await AgentGameAsync(agent, gameId);
        Assert.Equal(url, seen.GetProperty("videoUrl").GetString());
        Assert.Null(Text(seen, "videoSourceUrl"));
        Assert.Equal("ready", (await AdminTrailerAsync(owner, gameId))!.Value.GetProperty("status").GetString());

        // Anyone reads it: whole, and in byte ranges.
        using var raw = server.CreateDefaultClient();
        using (var whole = await raw.GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
            Assert.Equal("video/mp4", whole.Content.Headers.ContentType!.MediaType);
            Assert.Equal(video, await whole.Content.ReadAsByteArrayAsync());
            Assert.Contains("bytes", whole.Headers.AcceptRanges);
            Assert.Contains("immutable", whole.Headers.CacheControl!.ToString());
            Assert.Equal("nosniff", whole.Headers.GetValues("X-Content-Type-Options").Single());
        }

        using (var part = new HttpRequestMessage(HttpMethod.Get, url))
        {
            part.Headers.Range = new RangeHeaderValue(100, 199);
            using var response = await raw.SendAsync(part);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal($"bytes 100-199/{video.Length}", response.Content.Headers.ContentRange!.ToString());
            Assert.Equal(video[100..200], await response.Content.ReadAsByteArrayAsync());
        }

        using (var tail = new HttpRequestMessage(HttpMethod.Get, url))
        {
            tail.Headers.Range = new RangeHeaderValue(video.Length - 10, null);
            using var response = await raw.SendAsync(tail);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(video[^10..], await response.Content.ReadAsByteArrayAsync());
        }

        using (var outside = new HttpRequestMessage(HttpMethod.Get, url))
        {
            outside.Headers.Range = new RangeHeaderValue(video.Length + 5, null);
            using var response = await raw.SendAsync(outside);
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
            Assert.Equal($"bytes */{video.Length}", response.Content.Headers.ContentRange!.ToString());
        }

        using (var missing = await raw.GetAsync($"/api/v1/trailers/{Guid.NewGuid()}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        // A WebM passes too; a page, a playlist, a picture, nothing and too much do not — by their bytes, whatever the header says.
        using (var webm = await UploadAsync(owner, gameId, Webm, "video/webm"))
        {
            Assert.Equal(HttpStatusCode.Created, webm.StatusCode);
            Assert.Equal("video/webm", JsonElement.Parse(await webm.Content.ReadAsStringAsync()).GetProperty("trailer").GetProperty("contentType").GetString());
        }

        foreach (var (bytes, reason) in new[]
                 {
                     (Html, "format"), (Hls, "format"), (Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="), "format"),
                     ([], "required"), (Mp4((int)TrailerServerFixture.MaxBytes + 1), "max"),
                 })
        {
            using var refused = await UploadAsync(owner, gameId, bytes);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal(("file", reason), StaffAdminTests.Details(JsonElement.Parse(await refused.Content.ReadAsStringAsync())));
        }

        // Nothing half-written is left of the refused ones.
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(server.Services.GetRequiredService<TrailerOptions>().StoragePath)), f => f.EndsWith(".part", StringComparison.Ordinal));

        var cashier = await LoginAsync(server, CashierPin);
        using (var notOwner = await UploadAsync(cashier, gameId, video))
        {
            Assert.Equal(HttpStatusCode.Forbidden, notOwner.StatusCode);
        }

        using (var noGame = await UploadAsync(owner, Guid.NewGuid(), video))
        {
            Assert.Equal(HttpStatusCode.NotFound, noGame.StatusCode);
        }

        // Taken off: the PCs have none, and the finder leaves the game alone.
        Assert.Equal(200, (await SendRawAsync(HttpMethod.Delete, $"/games/{gameId}/trailer", owner)).Status);
        Assert.Null(Text(await AgentGameAsync(agent, gameId), "videoUrl"));
        Assert.Null(await AdminTrailerAsync(owner, gameId));
        var (_, find) = await SendRawAsync(HttpMethod.Post, "/games/trailers/find", owner);
        Assert.DoesNotContain(gameId, find.GetProperty("gameIds").EnumerateArray().Select(g => g.GetGuid()));
    }

    [Fact]
    public async Task A_link_is_downloaded_once_and_what_is_not_a_video_file_is_refused_with_a_reason()
    {
        var owner = await LoginAsync(server, OwnerPin);
        var video = Mp4(64 * 1024);
        server.Web.Serve("https://cdn.example.com/dl/ok.mp4", video, "video/mp4");
        server.Web.Serve("https://cdn.example.com/dl/page", Html, "text/html");
        server.Web.Serve("https://cdn.example.com/dl/master.m3u8", Hls, "application/vnd.apple.mpegurl");
        server.Web.Serve("https://cdn.example.com/dl/big.mp4", Mp4((int)TrailerServerFixture.MaxBytes + 10), "video/mp4");
        server.Web.Serve("https://cdn.example.com/dl/gone.mp4", [], "text/plain", HttpStatusCode.Gone);
        server.Web.Serve("https://cdn.example.com/dl/busy.mp4", [], "text/plain", HttpStatusCode.ServiceUnavailable);

        async Task<Guid> Card(string name, string link) =>
            await AddGameAsync(owner, new { title = $"DL {name}", launcher = "exe", exePath = $@"G:\DL\{name}.exe", videoUrl = link });
        var ok = await Card("ok", "https://cdn.example.com/dl/ok.mp4");
        var page = await Card("page", "https://cdn.example.com/dl/page");
        var hls = await Card("hls", "https://cdn.example.com/dl/master.m3u8");
        var big = await Card("big", "https://cdn.example.com/dl/big.mp4");
        var gone = await Card("gone", "https://cdn.example.com/dl/gone.mp4");
        var busy = await Card("busy", "https://cdn.example.com/dl/busy.mp4");
        var youtube = await Card("yt", "https://www.youtube.com/watch?v=dQw4w9WgXcQ");

        // Until the copy is there, the PCs get the link itself.
        var agent = await TestAgent.CreateAsync(server);
        Assert.Equal("https://cdn.example.com/dl/ok.mp4", (await AgentGameAsync(agent, ok)).GetProperty("videoUrl").GetString());
        Assert.Equal("downloading", (await AdminTrailerAsync(owner, ok))!.Value.GetProperty("status").GetString());

        await server.DrainAsync();

        var stored = (await AdminTrailerAsync(owner, ok))!.Value;
        Assert.Equal(("ready", "link", "https://cdn.example.com/dl/ok.mp4", video.Length), (stored.GetProperty("status").GetString(),
            stored.GetProperty("origin").GetString(), stored.GetProperty("sourceUrl").GetString(), stored.GetProperty("size").GetInt32()));
        var seen = await AgentGameAsync(agent, ok);
        Assert.Equal(stored.GetProperty("url").GetString(), seen.GetProperty("videoUrl").GetString());
        Assert.Equal("https://cdn.example.com/dl/ok.mp4", seen.GetProperty("videoSourceUrl").GetString());
        using (var raw = server.CreateDefaultClient())
        {
            Assert.Equal(video, await raw.GetByteArrayAsync(seen.GetProperty("videoUrl").GetString()));
        }

        foreach (var (game, reason) in new[] { (page, "notAVideo"), (hls, "streamingFormat"), (big, "tooLarge"), (gone, "unavailable"), (youtube, "notAVideo") })
        {
            var t = (await AdminTrailerAsync(owner, game))!.Value;
            Assert.Equal(("failed", reason), (t.GetProperty("status").GetString(), t.GetProperty("reason").GetString()));
        }

        // A page or a playlist never reaches the PCs; a link that is only too big for the server still may play there.
        Assert.Null(Text(await AgentGameAsync(agent, page), "videoUrl"));
        Assert.Null(Text(await AgentGameAsync(agent, hls), "videoUrl"));
        Assert.Equal("https://cdn.example.com/dl/big.mp4", (await AgentGameAsync(agent, big)).GetProperty("videoUrl").GetString());

        // A busy CDN is tried again later; YouTube is never asked.
        var later = (await AdminTrailerAsync(owner, busy))!.Value;
        Assert.Equal(("downloading", "unreachable"), (later.GetProperty("status").GetString(), later.GetProperty("reason").GetString()));
        Assert.DoesNotContain(server.Web.Requests, r => r.Contains("youtube", StringComparison.Ordinal));
        Assert.Single(server.Web.Requests, r => r == "https://cdn.example.com/dl/ok.mp4");

        // A new link replaces the copy; the same card saved again downloads nothing.
        server.Web.Serve("https://cdn.example.com/dl/new.webm", Webm, "video/webm");
        var card = new { title = "DL ok", launcher = "exe", exePath = @"G:\DL\ok.exe", videoUrl = "https://cdn.example.com/dl/new.webm" };
        Assert.Equal(200, (await SendRawAsync(HttpMethod.Put, $"/games/{ok}", owner, card)).Status);
        await server.DrainAsync();
        Assert.Equal("video/webm", (await AdminTrailerAsync(owner, ok))!.Value.GetProperty("contentType").GetString());
        Assert.Equal(200, (await SendRawAsync(HttpMethod.Put, $"/games/{ok}", owner, card)).Status);
        Assert.Equal("ready", (await AdminTrailerAsync(owner, ok))!.Value.GetProperty("status").GetString());
        Assert.Single(server.Web.Requests, r => r == "https://cdn.example.com/dl/new.webm");
    }

    [Fact]
    public async Task The_finder_takes_a_steam_games_progressive_file_and_matches_other_games_by_exact_title_only()
    {
        var owner = await LoginAsync(server, OwnerPin);
        server.Web.ServeJson(SteamTrailerFinder.Store + "appdetails?appids=4242&filters=movies", AppDetails("4242"));
        server.Web.Serve("https://cdn.akamai.steamstatic.com/steam/apps/9002/movie_max.mp4", Mp4(), "video/mp4");
        server.Web.ServeJson(
            SteamTrailerFinder.Store + "storesearch/?term=Hollow%20Knight&cc=us&l=en",
            """{"total":2,"items":[{"type":"app","name":"Hollow Knight: Silksong","id":1030300},{"type":"app","name":"Hollow Knight™","id":367520}]}""");
        server.Web.ServeJson(
            SteamTrailerFinder.Store + "appdetails?appids=367520&filters=movies",
            """{"367520":{"success":true,"data":{"movies":[{"id":1,"name":"Launch","webm":{"480":"http://cdn.akamai.steamstatic.com/steam/apps/1/movie480_vp9.webm"}}]}}}""");
        server.Web.Serve("https://cdn.akamai.steamstatic.com/steam/apps/1/movie480_vp9.webm", Webm, "video/webm");
        server.Web.ServeJson(
            SteamTrailerFinder.Store + "storesearch/?term=Knight%20Club&cc=us&l=en",
            """{"total":1,"items":[{"type":"app","name":"Knight Club 2","id":555}]}""");

        var steam = await AddGameAsync(owner, new { title = "Some Steam Game", launcher = "steam", launcherAppId = "4242" });
        var byTitle = await AddGameAsync(owner, new { title = "Hollow Knight", launcher = "exe", exePath = @"G:\HK\hk.exe" });
        var near = await AddGameAsync(owner, new { title = "Knight Club", launcher = "exe", exePath = @"G:\KC\kc.exe" });
        var uploaded = await AddGameAsync(owner, new { title = "Owner Video", launcher = "steam", launcherAppId = "4242" });
        using (var up = await UploadAsync(owner, uploaded, Mp4(2000)))
        {
            Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        }

        Assert.Equal("searching", (await AdminTrailerAsync(owner, steam))!.Value.GetProperty("status").GetString());
        await server.DrainAsync();

        var found = (await AdminTrailerAsync(owner, steam))!.Value;
        Assert.Equal(("ready", "found", "https://cdn.akamai.steamstatic.com/steam/apps/9002/movie_max.mp4"), (found.GetProperty("status").GetString(),
            found.GetProperty("origin").GetString(), found.GetProperty("sourceUrl").GetString()));
        var webm = (await AdminTrailerAsync(owner, byTitle))!.Value;
        Assert.Equal(("ready", "https://cdn.akamai.steamstatic.com/steam/apps/1/movie480_vp9.webm"), (webm.GetProperty("status").GetString(),
            webm.GetProperty("sourceUrl").GetString()));
        var none = (await AdminTrailerAsync(owner, near))!.Value;
        Assert.Equal(("failed", "notFound"), (none.GetProperty("status").GetString(), none.GetProperty("reason").GetString()));
        Assert.DoesNotContain(server.Web.Requests, r => r.Contains("appids=555", StringComparison.Ordinal));

        // The owner's upload stays as it was; «Найти трейлеры» asks again only for the games without one.
        var mine = (await AdminTrailerAsync(owner, uploaded))!.Value;
        Assert.Equal(("ready", "upload"), (mine.GetProperty("status").GetString(), mine.GetProperty("origin").GetString()));
        var (status, find) = await SendRawAsync(HttpMethod.Post, "/games/trailers/find", owner);
        Assert.Equal(200, status);
        var queued = find.GetProperty("gameIds").EnumerateArray().Select(g => g.GetGuid()).ToList();
        Assert.Contains(near, queued);
        Assert.DoesNotContain(uploaded, queued);
        Assert.DoesNotContain(steam, queued);
        Assert.Equal(queued.Count, find.GetProperty("queued").GetInt32());
        Assert.Equal("searching", (await AdminTrailerAsync(owner, near))!.Value.GetProperty("status").GetString());

        var cashier = await LoginAsync(server, CashierPin);
        Assert.Equal(403, (await SendRawAsync(HttpMethod.Post, "/games/trailers/find", cashier)).Status);
    }

    [Fact]
    public async Task The_seeds_links_reach_the_pcs_until_the_copy_is_stored()
    {
        // games.example.json: Counter-Strike 2 with a Steam CDN link.
        var cs2 = Guid.Parse("5f504f55-cff1-4d58-bedc-5976a3cee48b");
        var link = await Players.ScalarAsync<string>(server, "SELECT data ->> 'videoUrl' FROM games WHERE id = @cs2", new { cs2 });
        Assert.StartsWith("https://", link);
        Assert.Equal("link", await Players.ScalarAsync<string>(server, "SELECT origin FROM game_trailers WHERE game_id = @cs2", new { cs2 }));
        Assert.Equal(link, await Players.ScalarAsync<string>(server, "SELECT source_url FROM game_trailers WHERE game_id = @cs2", new { cs2 }));
        var agent = await TestAgent.CreateAsync(server);
        var seen = await AgentGameAsync(agent, cs2);
        if (await Players.ScalarAsync<string>(server, "SELECT status FROM game_trailers WHERE game_id = @cs2", new { cs2 }) != "ready")
        {
            Assert.Equal(link, seen.GetProperty("videoUrl").GetString());
        }
    }

    [Fact]
    public void The_finder_picks_the_highlighted_progressive_mp4_and_skips_streaming_only_movies()
    {
        using var details = JsonDocument.Parse(AppDetails("4242"));
        Assert.Equal("https://cdn.akamai.steamstatic.com/steam/apps/9002/movie_max.mp4", SteamTrailerFinder.Pick(details.RootElement, "4242"));

        using var hlsOnly = JsonDocument.Parse(
            """{"7":{"success":true,"data":{"movies":[{"id":1,"highlight":true,"hls_h264":"https://video.fastly.steamstatic.com/store_trailers/7/1/hls_264_master.m3u8","dash_h264":"https://video.fastly.steamstatic.com/store_trailers/7/1/dash_h264.mpd"}]}}}""");
        Assert.Null(SteamTrailerFinder.Pick(hlsOnly.RootElement, "7"));
        // Quality first (the club's target is 1080p): a movie's max webm beats its 480p mp4.
        using var maxWebm = JsonDocument.Parse(
            """{"8":{"success":true,"data":{"movies":[{"id":2,"mp4":{"480":"https://cdn.akamai.steamstatic.com/steam/apps/2/movie480.mp4"},"webm":{"480":"https://cdn.akamai.steamstatic.com/steam/apps/2/movie480_vp9.webm","max":"https://cdn.akamai.steamstatic.com/steam/apps/2/movie_max_vp9.webm"}}]}}}""");
        Assert.Equal("https://cdn.akamai.steamstatic.com/steam/apps/2/movie_max_vp9.webm", SteamTrailerFinder.Pick(maxWebm.RootElement, "8"));
        using var failed = JsonDocument.Parse("""{"7":{"success":false}}""");
        Assert.Null(SteamTrailerFinder.Pick(failed.RootElement, "7"));

        using var search = JsonDocument.Parse("""{"items":[{"type":"app","name":"DOOM Eternal","id":1},{"type":"app","name":"DOOM","id":2}]}""");
        Assert.Equal("2", SteamTrailerFinder.Match(search.RootElement, "Doom"));
        Assert.Null(SteamTrailerFinder.Match(search.RootElement, "Doom 3"));
        Assert.Equal("counter strike 2", SteamTrailerFinder.Normalize("Counter-Strike® 2™"));
    }

    [Fact]
    public async Task The_finder_waits_between_store_questions()
    {
        var web = new FakeTrailerWeb();
        web.ServeJson(SteamTrailerFinder.Store + "appdetails?appids=1&filters=movies", """{"1":{"success":true,"data":{"movies":[]}}}""");
        var finder = new SteamTrailerFinder(web, new TrailerOptions { FinderIntervalMs = 300 }, NullLogger<SteamTrailerFinder>.Instance);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null((await finder.FindAsync("A", "steam", "1", default)).Url);
        Assert.Null((await finder.FindAsync("A", "steam", "1", default)).Url);
        Assert.True(watch.ElapsedMilliseconds >= 280, $"{watch.ElapsedMilliseconds} ms");

        // A store that does not answer is asked again later, not taken for «none found».
        Assert.True((await finder.FindAsync("Unknown", "exe", null, default)).Transient);
    }

    /// <summary>appdetails with a streaming-only movie first, then a highlighted one with mp4 max/480 and webm.</summary>
    private static string AppDetails(string appId) => """
        {"APPID":{"success":true,"data":{"movies":[
          {"id":9001,"name":"New format","thumbnail":"x","hls_h264":"https://video.fastly.steamstatic.com/store_trailers/APPID/9001/hls_264_master.m3u8","dash_h264":"https://video.fastly.steamstatic.com/store_trailers/APPID/9001/dash_h264.mpd","highlight":false},
          {"id":9002,"name":"Trailer","thumbnail":"x","webm":{"480":"http://cdn.akamai.steamstatic.com/steam/apps/9002/movie480_vp9.webm","max":"http://cdn.akamai.steamstatic.com/steam/apps/9002/movie_max_vp9.webm"},
           "mp4":{"480":"http://cdn.akamai.steamstatic.com/steam/apps/9002/movie480.mp4","max":"http://cdn.akamai.steamstatic.com/steam/apps/9002/movie_max.mp4"},"highlight":true}
        ]}}}
        """.Replace("APPID", appId, StringComparison.Ordinal);
}
