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
}
