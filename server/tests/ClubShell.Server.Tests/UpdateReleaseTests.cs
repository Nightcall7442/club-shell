using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClubShell.Server.Realtime;
using Microsoft.Extensions.DependencyInjection;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>The server with a release publisher token and its own package folder.</summary>
public sealed class UpdateServerFixture : KestrelServerFixture
{
    public const string PublishToken = "publish-test-token-0123456789abcdef";

    public UpdateServerFixture()
    {
        Settings["Updates:PublishToken"] = PublishToken;
        Settings["Updates:StoragePath"] = Directory.CreateTempSubdirectory("clubshell-updates-").FullName;
    }
}

/// <summary>
/// ClubShell updates from the club server (UPDATES.md §8): the publisher uploads packages and posts the signed release
/// manifest with its token; a PC is offered the release only once the owner turns on automatic updates; it downloads the
/// package with its access token alone (Range works, nothing outside the folder); the owner's «Обновить ClubShell» sends
/// <c>update</c> commands with the manifest to connected PCs that are behind, Shell first.
/// </summary>
public sealed class UpdateReleaseTests(UpdateServerFixture server) : IClassFixture<UpdateServerFixture>
{
    private static readonly byte[] Package = Encoding.UTF8.GetBytes(new string('x', 4096));

    [Fact]
    public async Task The_publisher_uploads_and_publishes_a_release_and_reads_it_back_with_its_token()
    {
        using var raw = server.CreateDefaultClient();
        Assert.Equal(401, (int)(await raw.SendAsync(Put("1.5.0/ClubShell-1.5.0.msi", "wrong-token-0123456789abcdefghij"))).StatusCode);
        Assert.Equal(404, (int)(await raw.SendAsync(Put("1.5.0/..%2Fsecret", UpdateServerFixture.PublishToken))).StatusCode);
        await PublishAsync(raw, "1.5.0");

        // An unsigned manifest is refused: every Agent would refuse its package anyway.
        using var unsigned = await raw.SendAsync(Post(Manifest("1.6.0", signature: "")));
        Assert.Equal(400, (int)unsigned.StatusCode);
        using var otherChannel = await raw.SendAsync(Post(Manifest("1.6.0", channel: "beta")));
        Assert.Equal(400, (int)otherChannel.StatusCode);

        // publish.ps1's check: GET the manifest of each component with current=0.0.0 and its token.
        foreach (var component in new[] { "agent", "shell" })
        {
            using var check = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/updates/stable/manifest?component={component}&current=0.0.0");
            check.Headers.Authorization = new AuthenticationHeaderValue("Bearer", UpdateServerFixture.PublishToken);
            using var response = await server.Http.SendAsync(check);
            var entry = await Players.ReadAsync(response, 200);
            Assert.Equal(("1.5.0", component), (entry.GetProperty("version").GetString(), entry.GetProperty("component").GetString()));
        }
    }

    [Fact]
    public async Task A_pc_is_offered_the_release_only_with_automatic_updates_and_downloads_it_with_its_token()
    {
        using var raw = server.CreateDefaultClient();
        await PublishAsync(raw, "1.5.0");
        var agent = await TestAgent.CreateAsync(server);
        var owner = await LoginAsync(server, OwnerPin);
        await SetAutoAsync(raw, owner, false);

        const string query = "/api/v1/updates/stable/manifest?component=agent&current=1.4.2";
        await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, query), 204);
        var config = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, agent.Path("config")), 200);
        Assert.False(config.GetProperty("updates").GetProperty("enabled").GetBoolean());

        await SetAutoAsync(raw, owner, true);
        var offered = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, query), 200);
        Assert.Equal("1.5.0", offered.GetProperty("version").GetString());
        await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, "/api/v1/updates/stable/manifest?component=agent&current=1.5.0"), 204);
        config = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, agent.Path("config")), 200);
        Assert.True(config.GetProperty("updates").GetProperty("enabled").GetBoolean());

        // The package: the Agent's token alone, as its downloader sends it; Range for a resumed download.
        var url = new Uri(offered.GetProperty("url").GetString()!).PathAndQuery;
        using (var anonymous = await raw.GetAsync(url))
        {
            Assert.Equal(401, (int)anonymous.StatusCode);
        }

        using var full = new HttpRequestMessage(HttpMethod.Get, url);
        full.Headers.Authorization = new AuthenticationHeaderValue("Bearer", agent.AccessToken);
        using (var response = await raw.SendAsync(full))
        {
            Assert.Equal(200, (int)response.StatusCode);
            Assert.Equal(Package, await response.Content.ReadAsByteArrayAsync());
        }

        using var range = new HttpRequestMessage(HttpMethod.Get, url);
        range.Headers.Authorization = new AuthenticationHeaderValue("Bearer", agent.AccessToken);
        range.Headers.Range = new RangeHeaderValue(4000, null);
        using (var response = await raw.SendAsync(range))
        {
            Assert.Equal(206, (int)response.StatusCode);
            Assert.Equal(96, (await response.Content.ReadAsByteArrayAsync()).Length);
        }

        await SetAutoAsync(raw, owner, false);
    }

    [Fact]
    public async Task Update_now_sends_the_manifest_to_connected_pcs_that_are_behind_shell_first()
    {
        using var raw = server.CreateDefaultClient();
        await PublishAsync(raw, "1.5.0");
        var connected = await TestAgent.CreateAsync(server);
        var offline = await TestAgent.CreateAsync(server);
        await Players.ReadAsync(await connected.HeartbeatAsync(), 200);
        using var socket = await WsTestSocket.ConnectAsync(server, connected.AccessToken);
        await Wait.UntilAsync(() => server.Services.GetRequiredService<AgentSocketHub>().IsConnected(connected.PcId));
        var owner = await LoginAsync(server, OwnerPin);

        // A cashier may not; the owner gets a result per PC.
        using (var refused = await raw.SendAsync(Admin(HttpMethod.Post, "/updates/apply", await LoginAsync(server, CashierPin), new { })))
        {
            Assert.Equal(403, (int)refused.StatusCode);
        }

        using var apply = await raw.SendAsync(Admin(HttpMethod.Post, "/updates/apply", owner, new { pcIds = new[] { connected.PcId, offline.PcId } }));
        var body = await Players.ReadAsync(apply, 200);
        Assert.Equal("1.5.0", body.GetProperty("version").GetString());
        var results = body.GetProperty("results").EnumerateArray().ToDictionary(r => r.GetProperty("pcId").GetGuid(), r => r.GetProperty("result").GetString());
        Assert.Equal("sent", results[connected.PcId]);
        Assert.Equal("offline", results[offline.PcId]);

        var first = await NextCommandAsync(socket);
        var second = await NextCommandAsync(socket);
        Assert.Equal(("shell", "agent"), (Component(first), Component(second)));
        Assert.True(first.GetProperty("payload").GetProperty("applyNow").GetBoolean());
        Assert.Equal("1.5.0", first.GetProperty("payload").GetProperty("manifest").GetProperty("version").GetString());
        Assert.Equal(1, await Players.ScalarAsync<int>(server,
            "SELECT count(*)::int FROM audit_entries WHERE pc_id = @PcId AND action = 'pcCommand' AND meta ->> 'kind' = 'update'", new { connected.PcId }));

        // The status shows the release and the PCs with their versions; a PC on the release is up to date.
        using var status = await raw.SendAsync(Admin(HttpMethod.Get, "/updates", owner));
        var state = await Players.ReadAsync(status, 200);
        Assert.Contains(state.GetProperty("releases").EnumerateArray(), r => r.GetProperty("channel").GetString() == "stable" && r.GetProperty("version").GetString() == "1.5.0");
        Assert.Contains(state.GetProperty("pcs").EnumerateArray(), p => p.GetProperty("id").GetGuid() == connected.PcId && p.GetProperty("online").GetBoolean());

        await PublishAsync(raw, "1.4.2");
        using var again = await raw.SendAsync(Admin(HttpMethod.Post, "/updates/apply", owner, new { pcIds = new[] { connected.PcId } }));
        Assert.Equal("upToDate", (await Players.ReadAsync(again, 200)).GetProperty("results")[0].GetProperty("result").GetString());
    }

    private static string Component(JsonElement command) => command.GetProperty("payload").GetProperty("component").GetString()!;

    private static async Task<JsonElement> NextCommandAsync(WsTestSocket socket)
    {
        while (true)
        {
            var frame = await socket.ReceiveAsync();
            if (frame.GetProperty("type").GetString() == "command" && frame.GetProperty("name").GetString() == "update")
            {
                return frame;
            }
        }
    }

    private async Task PublishAsync(HttpClient raw, string version)
    {
        foreach (var file in new[] { $"ClubShell-{version}.msi", $"ClubShell-Shell-{version}.zip" })
        {
            using var put = await raw.SendAsync(Put($"{version}/{file}", UpdateServerFixture.PublishToken));
            Assert.Equal(201, (int)put.StatusCode);
        }

        using var post = await raw.SendAsync(Post(Manifest(version)));
        Assert.True(post.IsSuccessStatusCode, await post.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage Put(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/stable/" + path) { Content = new ByteArrayContent(Package) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage Post(object manifest)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/updates/stable/manifest") { Content = JsonContent.Create(manifest) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", UpdateServerFixture.PublishToken);
        return request;
    }

    private object Manifest(string version, string channel = "stable", string signature = "c2ln")
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(Package));
        object Component(string component, string file) => new
        {
            channel = "stable", component, version, url = $"http://localhost/stable/{version}/{file}", sha256 = sha, size = Package.Length,
            signature, releaseNotes = "Тест", mandatory = false, publishedAt = DateTimeOffset.UtcNow, minAgentVersion = (string?)null,
        };

        return new
        {
            schema = 1, product = "ClubShell", version, channel, publishedAt = DateTimeOffset.UtcNow,
            components = new { agent = Component("agent", $"ClubShell-{version}.msi"), shell = Component("shell", $"ClubShell-Shell-{version}.zip") },
            manifestSignatures = new { agent = "c2ln", shell = "c2ln" },
        };
    }

    private async Task SetAutoAsync(HttpClient raw, string owner, bool auto)
    {
        using var response = await raw.SendAsync(Admin(HttpMethod.Patch, "/updates", owner, new { auto }));
        Assert.Equal(200, (int)response.StatusCode);
    }

    private static HttpRequestMessage Admin(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, "/api/v1/admin" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }
}
