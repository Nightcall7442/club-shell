using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static ClubShell.Server.Tests.Staff;

namespace ClubShell.Server.Tests;

/// <summary>A stand-in for the GitHub Releases API and its asset downloads, on a local port.</summary>
public sealed class FakeGitHub : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, (byte[] Body, string Type)> _routes = new(StringComparer.Ordinal);

    public FakeGitHub()
    {
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port}";
        }

        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public string Url { get; }

    /// <summary>Replaces what the fake serves: the release list and every asset.</summary>
    public void Serve(string path, byte[] body, string type = "application/octet-stream")
    {
        lock (_routes)
        {
            _routes[path] = (body, type);
        }
    }

    public void Dispose() => _listener.Close();

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            (byte[] Body, string Type) route;
            bool found;
            lock (_routes)
            {
                found = _routes.TryGetValue(context.Request.Url!.AbsolutePath, out route);
            }

            context.Response.StatusCode = found ? 200 : 404;
            if (found)
            {
                context.Response.ContentType = route.Type;
                await context.Response.OutputStream.WriteAsync(route.Body);
            }

            context.Response.Close();
        }
    }
}

/// <summary>The server with its update storage, checking the fake GitHub only when asked.</summary>
public sealed class GitHubImportServerFixture : KestrelServerFixture
{
    public GitHubImportServerFixture()
    {
        Settings["Updates:StoragePath"] = Directory.CreateTempSubdirectory("clubshell-import-").FullName;
        Settings["Updates:GitHub:ApiUrl"] = GitHub.Url;
        Settings["Updates:GitHub:Repository"] = "acme/club-shell";
        Settings["Updates:GitHub:CheckEveryHours"] = "0";
    }

    public FakeGitHub GitHub { get; } = new();

    public override async ValueTask DisposeAsync()
    {
        GitHub.Dispose();
        await base.DisposeAsync();
    }
}

/// <summary>
/// «Проверить обновления» (UPDATES.md §8.3): the club server takes the newest GitHub release itself — the packages into its
/// own storage, checked against the manifest, named by path so the PCs download them from the club server — and refuses an
/// unsigned one; a second check finds nothing newer. The owner only: a cashier may not.
/// </summary>
public sealed class GitHubImportTests(GitHubImportServerFixture server) : IClassFixture<GitHubImportServerFixture>
{
    private static readonly byte[] AgentPackage = Encoding.UTF8.GetBytes(new string('a', 3000));
    private static readonly byte[] ShellPackage = Encoding.UTF8.GetBytes(new string('s', 2000));

    [Fact]
    public async Task The_owner_takes_the_newest_signed_github_release_to_the_server_and_the_pcs_get_it_from_there()
    {
        var owner = await LoginAsync(server, OwnerPin);
        Assert.Equal(403, (await RawAsync(server, HttpMethod.Post, "/updates/check", await LoginAsync(server, CashierPin))).Status);

        // An unsigned release: refused, said why.
        Publish("1.2.3", signature: "");
        var failed = await CheckAsync(owner);
        Assert.Equal("failed", failed.GetProperty("state").GetString());
        Assert.Contains("not signed", failed.GetProperty("error").GetString());

        Publish("1.2.3", signature: "c2lnbmVk");
        var imported = await CheckAsync(owner);
        Assert.Equal(("imported", "1.2.3"), (imported.GetProperty("state").GetString(), imported.GetProperty("version").GetString()));

        // The PC is offered it, by path on the club server, and downloads it there with its token.
        await RawExpectAsync(server, 200, HttpMethod.Patch, "/updates", owner, new { auto = true });
        var agent = await TestAgent.CreateAsync(server);
        var offered = await Players.ReadAsync(await agent.SendAsync(HttpMethod.Get, "/api/v1/updates/stable/manifest?component=agent&current=1.0.0"), 200);
        Assert.Equal("/stable/1.2.3/ClubShell-1.2.3.msi", offered.GetProperty("url").GetString());
        Assert.Equal("c2lnbmVk", offered.GetProperty("signature").GetString());
        using var download = new HttpRequestMessage(HttpMethod.Get, offered.GetProperty("url").GetString());
        download.Headers.Authorization = new AuthenticationHeaderValue("Bearer", agent.AccessToken);
        using var raw = server.CreateDefaultClient();
        using (var response = await raw.SendAsync(download))
        {
            Assert.Equal(AgentPackage, await response.Content.ReadAsByteArrayAsync());
        }

        // Nothing newer on GitHub: the release stays.
        var again = await CheckAsync(owner);
        Assert.Equal(("upToDate", "1.2.3"), (again.GetProperty("state").GetString(), again.GetProperty("version").GetString()));

        // A package that does not match its manifest is not taken.
        Publish("1.2.4", signature: "c2lnbmVk", corrupt: true);
        var corrupt = await CheckAsync(owner);
        Assert.Equal("failed", corrupt.GetProperty("state").GetString());
        var status = await RawExpectAsync(server, 200, HttpMethod.Get, "/updates", owner);
        Assert.Equal("1.2.3", status.GetProperty("releases")[0].GetProperty("version").GetString());
        Assert.True(status.GetProperty("canCheck").GetBoolean());
        await RawExpectAsync(server, 200, HttpMethod.Patch, "/updates", owner, new { auto = false });
    }

    /// <summary>Starts a check and waits for its end in <c>GET /admin/updates</c>.</summary>
    private async Task<JsonElement> CheckAsync(string owner)
    {
        Assert.Equal(202, (await RawAsync(server, HttpMethod.Post, "/updates/check", owner)).Status);
        JsonElement check = default;
        await Wait.UntilAsync(async () =>
        {
            check = (await RawExpectAsync(server, 200, HttpMethod.Get, "/updates", owner)).GetProperty("check");
            return check.GetProperty("state").GetString() != "running";
        });
        return check;
    }

    /// <summary>A GitHub release <c>v{version}</c> with manifest.json and the two packages (the agent's corrupted on request).</summary>
    private void Publish(string version, string signature, bool corrupt = false)
    {
        var tag = $"v{version}";
        var download = $"{server.GitHub.Url}/acme/club-shell/releases/download/{tag}";
        object Component(string component, string file, byte[] body) => new
        {
            channel = "stable", component, version, url = $"https://github.com/acme/club-shell/releases/download/{tag}/stable/{version}/{file}",
            sha256 = Convert.ToHexStringLower(SHA256.HashData(body)), size = body.Length, signature, releaseNotes = "Тест", mandatory = false,
            publishedAt = DateTimeOffset.UtcNow, minAgentVersion = (string?)null,
        };
        var agentFile = $"ClubShell-{version}.msi";
        var shellFile = $"ClubShell-Shell-{version}.zip";
        var manifest = new
        {
            schema = 1, product = "ClubShell", version, channel = "stable", publishedAt = DateTimeOffset.UtcNow,
            components = new { agent = Component("agent", agentFile, AgentPackage), shell = Component("shell", shellFile, ShellPackage) },
        };
        server.GitHub.Serve($"/acme/club-shell/releases/download/{tag}/manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest), "application/json");
        server.GitHub.Serve($"/acme/club-shell/releases/download/{tag}/{agentFile}", corrupt ? [.. AgentPackage, 1] : AgentPackage);
        server.GitHub.Serve($"/acme/club-shell/releases/download/{tag}/{shellFile}", ShellPackage);
        object Asset(string name) => new { name, browser_download_url = $"{download}/{name}" };
        var list = new[]
        {
            new { tag_name = tag, draft = false, prerelease = false, assets = new[] { Asset("manifest.json"), Asset(agentFile), Asset(shellFile) } },
            new { tag_name = "v9.9.9-beta.1", draft = false, prerelease = true, assets = new[] { Asset("manifest.json") } },
        };
        server.GitHub.Serve("/repos/acme/club-shell/releases", JsonSerializer.SerializeToUtf8Bytes(list), "application/json");
    }
}
