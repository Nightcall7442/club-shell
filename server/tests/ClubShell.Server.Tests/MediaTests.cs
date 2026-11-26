using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>
/// D-93: the owner uploads the club's pictures (PNG/JPEG/WebP/GIF by their bytes, ≤ 5 MB), anyone reads them back by id,
/// and the club's default shell theme goes to the PCs.
/// </summary>
public sealed class MediaTests(ServerFixture server) : LedgerCheckedTest(server), IClassFixture<ServerFixture>
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private async Task<HttpResponseMessage> UploadAsync(string token, byte[] bytes, string type, string kind = "logo")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/media?kind={kind}") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var raw = Server.CreateDefaultClient();
        return await raw.SendAsync(request);
    }

    [Fact]
    public async Task The_owner_uploads_a_logo_and_anyone_reads_it_back_only_pictures_pass()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        using var ok = await UploadAsync(owner, Png, "image/png");
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var body = JsonElement.Parse(await ok.Content.ReadAsStringAsync());
        var url = body.GetProperty("url").GetString()!;
        Assert.StartsWith("/api/v1/media/", url);

        using var raw = Server.CreateDefaultClient();
        using var read = await raw.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("image/png", read.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Png, await read.Content.ReadAsByteArrayAsync());
        Assert.Contains("immutable", read.Headers.CacheControl!.ToString());

        // An SVG or a page dressed as a PNG is refused: only the bytes decide.
        using var svg = await UploadAsync(owner, "<svg xmlns='http://www.w3.org/2000/svg'><script>1</script></svg>"u8.ToArray(), "image/png");
        Assert.Equal(HttpStatusCode.BadRequest, svg.StatusCode);
        using var big = await UploadAsync(owner, [.. Png, .. new byte[5 * 1024 * 1024]], "image/png");
        Assert.Equal(HttpStatusCode.BadRequest, big.StatusCode);
        using var kind = await UploadAsync(owner, Png, "image/png", "virus");
        Assert.Equal(HttpStatusCode.BadRequest, kind.StatusCode);

        var cashier = await LoginAsync(Server, CashierPin);
        using var notOwner = await UploadAsync(cashier, Png, "image/png");
        Assert.Equal(HttpStatusCode.Forbidden, notOwner.StatusCode);
        using var missing = await raw.GetAsync($"/api/v1/media/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task The_clubs_default_theme_goes_to_the_pcs_and_only_a_bundled_one_is_taken()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        object Branding(string theme) => new { clubName = "Arena", accent = "#9ADFFF", logoUrl = (string?)null, wallpaperUrl = (string?)null, theme };
        try
        {
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { branding = Branding("light") });
            var agent = await TestAgent.CreateAsync(Server);
            var config = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, agent.Path("config")), 200);
            Assert.Equal("light", config.GetProperty("shell").GetProperty("theme").GetString());
            Assert.Equal(("branding.theme", "enum"),
                StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Patch, "/club", owner, new { branding = Branding("matrix") })));
        }
        finally
        {
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { branding = Branding("default") });
        }
    }

    [Fact]
    public async Task The_pc_label_the_owner_sets_goes_to_the_pcs_and_a_bad_one_is_refused()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        object Branding(object? pcLabel) => new { clubName = "Arena", accent = "#9ADFFF", logoUrl = (string?)null, wallpaperUrl = (string?)null, pcLabel };
        try
        {
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner,
                new { branding = Branding(new { format = "number", large = true, @lock = true, idle = false, topBar = true }) });
            var agent = await TestAgent.CreateAsync(Server);
            var config = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, agent.Path("config")), 200);
            var label = config.GetProperty("shell").GetProperty("club").GetProperty("pcLabel");
            Assert.Equal("number", label.GetProperty("format").GetString());
            Assert.True(label.GetProperty("large").GetBoolean());
            Assert.False(label.GetProperty("idle").GetBoolean());
            Assert.Equal(("branding.pcLabel.format", "enum"),
                StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Patch, "/club", owner, new { branding = Branding(new { format = "big" }) })));
            Assert.Equal(("branding.pcLabel.large", "type"),
                StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Patch, "/club", owner, new { branding = Branding(new { large = "yes" }) })));
        }
        finally
        {
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { branding = Branding(null) });
        }
    }

    private static object PlainBranding(string? wallpaperUrl = null) =>
        new { clubName = "Arena", accent = "#9ADFFF", logoUrl = (string?)null, wallpaperUrl };

    [Fact]
    public async Task The_player_screen_layout_goes_to_the_pcs_with_each_screens_picture_resolved()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var media = "/api/v1/media/" + Guid.NewGuid();
        var lockPicture = "/api/v1/media/" + Guid.NewGuid();
        try
        {
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new
            {
                branding = new
                {
                    clubName = "Arena", accent = "#9ADFFF", logoUrl = (string?)null, wallpaperUrl = media,
                    font = "mono", tint = "#102030",
                    @lock = new
                    {
                        logo = new { show = true, position = "topCenter", size = "xl", name = false },
                        wallpaperUrl = lockPicture, dim = 40, blur = true, clock = false, language = true,
                        title = new { ru = "  Привет  ", uz = "", en = (string?)null },
                    },
                    idle = new { logo = new { position = "bottomRight" }, prices = false, freeBadge = false, cta = new { en = "Tap" } },
                    desktop = new { logo = new { show = false, size = "l" } },
                },
            });
            var agent = await TestAgent.CreateAsync(Server);
            var config = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, agent.Path("config")), 200);
            var club = config.GetProperty("shell").GetProperty("club");
            var look = club.GetProperty("look");
            Assert.Equal(media, club.GetProperty("wallpaperUrl").GetString());
            Assert.Equal("mono", look.GetProperty("font").GetString());
            Assert.Equal("#102030", look.GetProperty("tint").GetString());

            var lockLook = look.GetProperty("lock");
            Assert.Equal("topCenter", lockLook.GetProperty("logo").GetProperty("position").GetString());
            Assert.Equal("xl", lockLook.GetProperty("logo").GetProperty("size").GetString());
            Assert.False(lockLook.GetProperty("logo").GetProperty("name").GetBoolean());
            Assert.Equal(lockPicture, lockLook.GetProperty("wallpaperUrl").GetString());
            Assert.Equal(40, lockLook.GetProperty("dim").GetInt32());
            Assert.True(lockLook.GetProperty("blur").GetBoolean());
            Assert.False(lockLook.GetProperty("clock").GetBoolean());
            Assert.True(lockLook.GetProperty("language").GetBoolean());
            Assert.Equal("Привет", lockLook.GetProperty("title").GetProperty("ru").GetString());

            // The idle screen keeps its slides without a picture of its own; the desktop falls back to the club's.
            var idle = look.GetProperty("idle");
            Assert.False(idle.TryGetProperty("wallpaperUrl", out var idleUrl) && idleUrl.ValueKind != JsonValueKind.Null);
            Assert.Equal("bottomRight", idle.GetProperty("logo").GetProperty("position").GetString());
            Assert.Equal("m", idle.GetProperty("logo").GetProperty("size").GetString());
            Assert.False(idle.GetProperty("prices").GetBoolean());
            Assert.False(idle.GetProperty("freeBadge").GetBoolean());
            Assert.True(idle.GetProperty("clock").GetBoolean());
            Assert.Equal("Tap", idle.GetProperty("cta").GetProperty("en").GetString());
            Assert.False(idle.TryGetProperty("dim", out var idleDim) && idleDim.ValueKind != JsonValueKind.Null);
            var desktop = look.GetProperty("desktop");
            Assert.False(desktop.GetProperty("logo").GetProperty("show").GetBoolean());
            Assert.Equal(media, desktop.GetProperty("wallpaperUrl").GetString());
        }
        finally
        {
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { branding = PlainBranding() });
        }

        // A club that never opened the designer sends no look at all: today's screens exactly.
        var plain = await TestAgent.CreateAsync(Server);
        var plainConfig = await Players.ReadAsync(await plain.SendAsync(HttpMethod.Get, plain.Path("config")), 200);
        Assert.False(plainConfig.GetProperty("shell").GetProperty("club").TryGetProperty("look", out var none) && none.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task A_screen_picture_on_the_server_carries_the_servers_address_when_the_club_wallpaper_is_a_link()
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var upload = "/api/v1/media/" + Guid.NewGuid();
        try
        {
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new
            {
                branding = new
                {
                    clubName = "Arena", accent = "#9ADFFF", logoUrl = (string?)null, wallpaperUrl = "https://example.com/hall.jpg",
                    idle = new { wallpaperUrl = upload },
                },
            });
            var agent = await TestAgent.CreateAsync(Server);
            var club = (await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, agent.Path("config")), 200)).GetProperty("shell").GetProperty("club");
            // The Agent puts its server in front of shell.club.wallpaperUrl only: that is where the shell finds it.
            Assert.Equal(upload, club.GetProperty("wallpaperUrl").GetString());
            Assert.Equal("https://example.com/hall.jpg", club.GetProperty("look").GetProperty("lock").GetProperty("wallpaperUrl").GetString());
            Assert.Equal(upload, club.GetProperty("look").GetProperty("idle").GetProperty("wallpaperUrl").GetString());
        }
        finally
        {
            await ExpectAsync(Server, 200, HttpMethod.Patch, "/club", owner, new { branding = PlainBranding() });
        }
    }

    [Theory]
    [InlineData("""{"font":"comic"}""", "branding.font", "enum")]
    [InlineData("""{"tint":"red"}""", "branding.tint", "format")]
    [InlineData("""{"lock":[]}""", "branding.lock", "schema")]
    [InlineData("""{"lock":{"logo":{"position":"bottomLeft"}}}""", "branding.lock.logo.position", "enum")]
    [InlineData("""{"idle":{"logo":{"position":"middle"}}}""", "branding.idle.logo.position", "enum")]
    [InlineData("""{"desktop":{"logo":{"position":"topRight"}}}""", "branding.desktop.logo.position", "enum")]
    [InlineData("""{"desktop":{"logo":{"size":"xl"}}}""", "branding.desktop.logo.size", "enum")]
    [InlineData("""{"lock":{"logo":{"show":"yes"}}}""", "branding.lock.logo.show", "type")]
    [InlineData("""{"lock":{"dim":81}}""", "branding.lock.dim", "max")]
    [InlineData("""{"idle":{"dim":-1}}""", "branding.idle.dim", "min")]
    [InlineData("""{"idle":{"dim":12.5}}""", "branding.idle.dim", "type")]
    [InlineData("""{"lock":{"blur":1}}""", "branding.lock.blur", "type")]
    [InlineData("""{"idle":{"prices":"no"}}""", "branding.idle.prices", "type")]
    [InlineData("""{"lock":{"wallpaperUrl":"file:///c:/x.jpg"}}""", "branding.lock.wallpaperUrl", "format")]
    [InlineData("""{"lock":{"title":"Hi"}}""", "branding.lock.title", "schema")]
    [InlineData("""{"lock":{"title":{"ru":5}}}""", "branding.lock.title.ru", "type")]
    [InlineData("""{"idle":{"cta":{"uz":"0123456789012345678901234567890123456789012345678901234567890"}}}""", "branding.idle.cta.uz", "max")]
    public async Task A_bad_player_screen_layout_is_refused_with_its_field(string patch, string field, string reason)
    {
        var owner = await LoginAsync(Server, OwnerPin);
        var branding = System.Text.Json.Nodes.JsonNode.Parse(patch)!.AsObject();
        branding["clubName"] = "Arena";
        branding["accent"] = "#9ADFFF";
        branding["logoUrl"] = null;
        branding["wallpaperUrl"] = null;
        Assert.Equal((field, reason),
            StaffAdminTests.Details(await ExpectAsync(Server, 400, HttpMethod.Patch, "/club", owner, new { branding })));
    }
}
