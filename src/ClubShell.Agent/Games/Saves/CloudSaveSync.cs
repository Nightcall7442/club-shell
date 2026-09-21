using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using ClubShell.Agent.Games.Accounts;
using ClubShell.Contracts.Games;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Http;
using ClubShell.Core.Security;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Games.Saves;

/// <summary>
/// Restores a pooled account's cloud-save bundle before launch and uploads the save directory after exit
/// (SERVER_API.md §4.6). Every failure is logged and swallowed: saves never block a launch or an exit.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CloudSaveSync
{
    private const string ZipMediaType = "application/zip";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerClient _server;
    private readonly IKioskProfilePaths _profile;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<CloudSaveSync> _logger;

    /// <summary>Creates the sync.</summary>
    public CloudSaveSync(
        IHttpClientFactory httpClientFactory,
        IServerClient server,
        IKioskProfilePaths profile,
        IOptionsMonitor<AgentSettings> settings,
        IClock clock,
        ILogger<CloudSaveSync> logger)
    {
        _httpClientFactory = httpClientFactory;
        _server = server;
        _profile = profile;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Maximum time for one download or upload.</summary>
    public static TimeSpan TransferTimeout => TimeSpan.FromMinutes(5);

    /// <summary><c>games.cloudSave.enabled</c>.</summary>
    public bool Enabled => _settings.CurrentValue.Games.CloudSave.Enabled;

    /// <summary>Maximum bundle size in bytes (<c>games.cloudSave.maxMb</c>).</summary>
    public long MaxBytes => _settings.CurrentValue.Games.CloudSave.MaxMb * 1024L * 1024L;

    /// <summary>Local bundle directory (<c>games.cloudSave.root</c>).</summary>
    public string BundleRoot => _settings.CurrentValue.ResolvePath(_settings.CurrentValue.Games.CloudSave.Root);

    /// <summary>
    /// Save directory for <paramref name="game"/>: <c>extra.saveDir</c> of the lease (supports <c>%LOCALAPPDATA%</c>,
    /// <c>%APPDATA%</c>, <c>%USERPROFILE%</c>, <c>{installPath}</c>, <c>{title}</c>), otherwise the launcher's
    /// conventional location under the kiosk profile.
    /// </summary>
    public string? ResolveSaveDir(Game game, ActiveLease lease)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(lease);
        string? template = lease.ExtraString("saveDir");
        if (string.IsNullOrWhiteSpace(template))
        {
            template = game.Launcher switch
            {
                LauncherType.Epic => @"%LOCALAPPDATA%\{title}\Saved",
                LauncherType.Riot => @"%LOCALAPPDATA%\Riot Games\{title}",
                LauncherType.BattleNet or LauncherType.Ea or LauncherType.Ubisoft => @"%USERPROFILE%\Documents\{title}",
                _ => @"%USERPROFILE%\Saved Games\{title}",
            };
        }

        string expanded = template
            .Replace("%LOCALAPPDATA%", _profile.LocalAppData, StringComparison.OrdinalIgnoreCase)
            .Replace("%APPDATA%", _profile.RoamingAppData, StringComparison.OrdinalIgnoreCase)
            .Replace("%USERPROFILE%", _profile.UserProfile, StringComparison.OrdinalIgnoreCase)
            .Replace("{installPath}", game.InstallPath ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", SafeName(game.Title), StringComparison.OrdinalIgnoreCase);
        if (expanded.Contains("{installPath}", StringComparison.OrdinalIgnoreCase) || !Path.IsPathRooted(expanded))
        {
            return null;
        }

        return Path.GetFullPath(expanded);
    }

    /// <summary>Downloads, verifies and extracts <see cref="ActiveLease.CloudSave"/> into the save directory. Returns <see langword="true"/> on success.</summary>
    public async Task<bool> DownloadAsync(Game game, ActiveLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(lease);
        if (!Enabled || lease.CloudSave is not { } bundle)
        {
            return false;
        }

        string? saveDir = ResolveSaveDir(game, lease);
        if (saveDir is null)
        {
            _logger.LogWarning("No save directory resolvable for {Title}; cloud save skipped", game.Title);
            return false;
        }

        if (bundle.SizeBytes > MaxBytes)
        {
            _logger.LogWarning("Cloud save for {Title} is {Size} bytes, above the {Max} byte limit; skipped", game.Title, bundle.SizeBytes, MaxBytes);
            return false;
        }

        string tmp = Path.Combine(BundleRoot, $"{lease.LeaseId:N}-download.zip");
        long started = _clock.GetTimestamp();
        try
        {
            Directory.CreateDirectory(BundleRoot);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TransferTimeout);
            CancellationToken ct = timeout.Token;

            using HttpClient http = _httpClientFactory.CreateClient(RetryPolicy.DownloadHttpClientName);
            using HttpResponseMessage response = await http.GetAsync(new Uri(bundle.Url), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (FileStream file = new(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await CopyBoundedAsync(body, file, MaxBytes, ct).ConfigureAwait(false);
            }

            string sha = await Signing.Sha256FileAsync(tmp, ct).ConfigureAwait(false);
            if (!sha.Equals(bundle.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Cloud save for {Title} failed SHA-256 verification; skipped", game.Title);
                return false;
            }

            Directory.CreateDirectory(saveDir);
            await Task.Run(() => ZipFile.ExtractToDirectory(tmp, saveDir, overwriteFiles: true), ct).ConfigureAwait(false);
            _logger.LogInformation("Cloud save restored for {Title} into {Dir} ({Size} bytes, {Elapsed} ms)", game.Title, saveDir, bundle.SizeBytes, (int)_clock.GetElapsedTime(started).TotalMilliseconds);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException or UriFormatException)
        {
            _logger.LogWarning(ex, "Cloud save download for {Title} failed; launching without it", game.Title);
            return false;
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    /// <summary>Zips the save directory and <c>PUT</c>s it to a fresh <see cref="SaveUploadTarget"/>. Returns the upload descriptor for the lease release, or <see langword="null"/> when skipped/failed.</summary>
    public async Task<CloudSaveUpload?> UploadAsync(Game game, ActiveLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(lease);
        if (!Enabled)
        {
            return null;
        }

        string? saveDir = ResolveSaveDir(game, lease);
        if (saveDir is null || !Directory.Exists(saveDir))
        {
            return null;
        }

        string tmp = Path.Combine(BundleRoot, $"{lease.LeaseId:N}-upload.zip");
        long started = _clock.GetTimestamp();
        try
        {
            Directory.CreateDirectory(BundleRoot);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TransferTimeout);
            CancellationToken ct = timeout.Token;

            await Task.Run(() => ZipFile.CreateFromDirectory(saveDir, tmp, CompressionLevel.Optimal, includeBaseDirectory: false), ct).ConfigureAwait(false);
            long size = new FileInfo(tmp).Length;
            if (size > MaxBytes)
            {
                _logger.LogWarning("Save bundle for {Title} is {Size} bytes, above the {Max} byte limit; not uploaded", game.Title, size, MaxBytes);
                return null;
            }

            string sha = await Signing.Sha256FileAsync(tmp, ct).ConfigureAwait(false);
            SaveUploadTarget target = await _server.GetSaveUploadTargetAsync(game.Id, lease.LeaseId, ct).ConfigureAwait(false);
            if (size > target.MaxBytes)
            {
                _logger.LogWarning("Save bundle for {Title} ({Size} bytes) exceeds the server limit {Max}; not uploaded", game.Title, size, target.MaxBytes);
                return null;
            }

            using HttpClient http = _httpClientFactory.CreateClient(RetryPolicy.DownloadHttpClientName);
            await using (FileStream file = new(tmp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            using (var content = new StreamContent(file))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue(ZipMediaType);
                content.Headers.ContentLength = size;
                using HttpResponseMessage response = await http.PutAsync(new Uri(target.UploadUrl), content, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }

            _logger.LogInformation("Cloud save uploaded for {Title} ({Size} bytes, {Elapsed} ms)", game.Title, size, (int)_clock.GetElapsedTime(started).TotalMilliseconds);
            return new CloudSaveUpload(target.UploadUrl, sha, size);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ServerApiException or HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException or UriFormatException)
        {
            _logger.LogWarning(ex, "Cloud save upload for {Title} failed; lease released without a save", game.Title);
            return null;
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException($"Bundle exceeds {maxBytes} bytes");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string SafeName(string title)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = title.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        return new string(chars).Trim();
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Cannot delete temp bundle {Path}", path);
        }
    }
}
