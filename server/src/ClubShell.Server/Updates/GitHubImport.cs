using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClubShell.Contracts.Commands;
using Dapper;
using Npgsql;

namespace ClubShell.Server.Updates;

/// <summary>Where the club server looks for new ClubShell releases by itself (<c>Updates:GitHub*</c>).</summary>
public sealed class GitHubReleaseOptions
{
    /// <summary><c>owner/repo</c> whose GitHub Releases carry <c>manifest.json</c> and the packages; empty — off.</summary>
    public string Repository { get; set; } = "Nightcall7442/club-shell";

    public string ApiUrl { get; set; } = "https://api.github.com";

    /// <summary>A token for a private repository; empty for a public one.</summary>
    public string Token { get; set; } = "";

    /// <summary>How often the server checks by itself while a club updates automatically; 0 — only on the owner's button.</summary>
    public int CheckEveryHours { get; set; } = 6;

    public bool Enabled => Repository.Contains('/', StringComparison.Ordinal);
}

/// <summary>The last check or import, for the admin console («Проверить обновления»).</summary>
public sealed record ImportState(string State, string? Version, string? Error, DateTimeOffset At);

/// <summary>
/// ClubShell updates without a publisher (UPDATES.md §8.3): the club server takes the newest GitHub release of the channel
/// itself — on the owner's «Проверить обновления» in the admin console, and every <c>CheckEveryHours</c> while a club of
/// this server updates automatically. It downloads the release's <c>manifest.json</c> and each component's package into
/// its own storage (the SHA-256 and size must match the manifest; an unsigned package is refused, as every Agent would
/// refuse it), names them by path (<c>/stable/1.2.0/&lt;file&gt;</c>, which an Agent resolves against its own server
/// address) and stores the release as the channel's latest, exactly as <c>publishUpdateManifest</c> would. The PCs then
/// download from the club server, never from GitHub. A release not newer than the channel's is left alone.
/// </summary>
public sealed class GitHubImport(
    GitHubReleaseOptions options, Releases releases, NpgsqlDataSource db, TimeProvider clock, ILogger<GitHubImport> logger)
    : BackgroundService
{
    private readonly HttpClient _http = CreateClient(options);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ImportState _state = new("idle", null, null, DateTimeOffset.MinValue);

    public ImportState State => _state;

    public bool Enabled => options.Enabled;

    /// <summary>Starts a check in the background unless one runs; false when one already runs.</summary>
    public bool Start(string channel)
    {
        if (!_gate.Wait(0))
        {
            return false;
        }

        _state = new ImportState("running", null, null, clock.GetUtcNow());
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(channel, CancellationToken.None);
            }
            finally
            {
                _gate.Release();
            }
        });
        return true;
    }

    /// <summary>One check of <paramref name="channel"/>, with the caller holding the gate.</summary>
    public async Task<ImportState> RunAsync(string channel, CancellationToken ct)
    {
        try
        {
            var version = await ImportAsync(channel, ct);
            _state = version is null
                ? new ImportState("upToDate", (await releases.LatestAsync(channel))?.Version, null, clock.GetUtcNow())
                : new ImportState("imported", version, null, clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            logger.LogWarning(ex, "ClubShell release check on GitHub failed");
            _state = new ImportState("failed", null, ex.Message, clock.GetUtcNow());
        }

        return _state;
    }

    /// <summary>The imported version, or <see langword="null"/> when GitHub has nothing newer than the channel's release.</summary>
    public async Task<string?> ImportAsync(string channel, CancellationToken ct)
    {
        if (!options.Enabled)
        {
            throw new InvalidOperationException("Updates:GitHub:Repository is not set");
        }

        var release = await NewestAsync(channel, ct) ?? throw new InvalidOperationException("No ClubShell release with manifest.json on GitHub");
        var current = await releases.LatestAsync(channel);
        if (current is not null && Releases.Compare(release.Version, current.Version) <= 0)
        {
            return null;
        }

        var manifestUrl = release.Assets.GetValueOrDefault("manifest.json") ?? throw new InvalidOperationException("manifest.json missing");
        var manifest = JsonNode.Parse(await _http.GetStringAsync(manifestUrl, ct))?.AsObject()
            ?? throw new InvalidOperationException("manifest.json is not an object");
        if ((string?)manifest["version"] != release.Version)
        {
            throw new InvalidOperationException($"manifest.json is for {(string?)manifest["version"]}, the release is {release.Version}");
        }

        manifest["channel"] = channel;
        var components = manifest["components"]?.AsObject() ?? throw new InvalidOperationException("manifest.json has no components");
        foreach (var (name, node) in components.ToList())
        {
            if (node is not JsonObject entry)
            {
                continue;
            }

            var package = Contracts.Serialization.JsonDefaults.Deserialize<UpdateManifest>(entry.ToJsonString())
                ?? throw new InvalidOperationException($"components.{name} is not a package");
            if (string.IsNullOrEmpty(package.Signature))
            {
                throw new InvalidOperationException(
                    $"ClubShell {release.Version} is not signed: the PCs would refuse it. Set UPDATE_MANIFEST_KEY_PEM in the repository and build the release again");
            }

            var file = Path.GetFileName(new Uri(package.Url, UriKind.Absolute).AbsolutePath);
            var source = release.Assets.GetValueOrDefault(file)
                ?? throw new InvalidOperationException($"{file} is not attached to the GitHub release");
            await DownloadAsync(source, releases.FilePath(channel, release.Version, file), package, ct);
            entry["url"] = $"/{channel}/{release.Version}/{file}";
            entry["channel"] = channel;
        }

        await releases.SaveAsync(channel, release.Version, JsonElement.Parse(manifest.ToJsonString()), clock.GetUtcNow());
        logger.LogWarning("ClubShell {Version} imported from GitHub to the {Channel} channel", release.Version, channel);
        return release.Version;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || options.CheckEveryHours <= 0)
        {
            return;
        }

        // A little after start: the server is busy with its agents first.
        using var timer = new PeriodicTimer(TimeSpan.FromHours(options.CheckEveryHours), clock);
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), clock, stoppingToken);
            do
            {
                await using (var c = await db.OpenConnectionAsync(stoppingToken))
                {
                    if (!await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM clubs WHERE auto_update)"))
                    {
                        continue;
                    }
                }

                if (await _gate.WaitAsync(0, stoppingToken))
                {
                    try
                    {
                        _state = new ImportState("running", null, null, clock.GetUtcNow());
                        await RunAsync("stable", stoppingToken);
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
        base.Dispose();
    }

    private sealed record GitHubRelease(string Version, Dictionary<string, string> Assets);

    /// <summary>The newest published release of the channel (a pre-release only for <c>beta</c>) that carries a manifest.</summary>
    private async Task<GitHubRelease?> NewestAsync(string channel, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{options.ApiUrl.TrimEnd('/')}/repos/{options.Repository}/releases?per_page=20", ct));
        GitHubRelease? best = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.GetProperty("draft").GetBoolean() || (r.GetProperty("prerelease").GetBoolean() && channel != "beta"))
            {
                continue;
            }

            var version = r.GetProperty("tag_name").GetString()?.TrimStart('v');
            if (Releases.Parse(version) is null)
            {
                continue;
            }

            var assets = r.GetProperty("assets").EnumerateArray().ToDictionary(
                a => a.GetProperty("name").GetString()!, a => a.GetProperty("browser_download_url").GetString()!, StringComparer.Ordinal);
            if (assets.ContainsKey("manifest.json") && (best is null || Releases.Compare(version, best.Version) > 0))
            {
                best = new GitHubRelease(version!, assets);
            }
        }

        return best;
    }

    /// <summary>The package into <paramref name="target"/>, checked against the manifest's SHA-256 and size; kept when it already matches.</summary>
    private async Task DownloadAsync(string url, string target, UpdateManifest package, CancellationToken ct)
    {
        if (File.Exists(target) && new FileInfo(target).Length == package.Size && await HashAsync(target, ct) == package.Sha256.ToLowerInvariant())
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".part";
        using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(temp);
            await response.Content.CopyToAsync(file, ct);
        }

        var size = new FileInfo(temp).Length;
        var hash = await HashAsync(temp, ct);
        if (size != package.Size || hash != package.Sha256.ToLowerInvariant())
        {
            File.Delete(temp);
            throw new InvalidOperationException($"{Path.GetFileName(target)}: got {size} bytes, SHA-256 {hash}; the manifest says {package.Size}, {package.Sha256}");
        }

        File.Move(temp, target, overwrite: true);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    private static HttpClient CreateClient(GitHubReleaseOptions options)
    {
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
        {
            Timeout = TimeSpan.FromMinutes(20),
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ClubShell-Server", "1"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        if (!string.IsNullOrEmpty(options.Token))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
        }

        return http;
    }
}
