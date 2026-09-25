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
/// "Sit at any PC — it plays like home": before a launch the player's own settings for the game (binds, sensitivity,
/// graphics — <see cref="Game.SettingsPaths"/>) are downloaded and put in place, after the game exits they are zipped
/// and stored back on the server. Unlike <see cref="CloudSaveSync"/>, which follows a pooled account, these follow the
/// player. Every failure is logged and swallowed: settings never block a launch or an exit.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PlayerSettingsSync
{
    private const string ZipMediaType = "application/zip";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerClient _server;
    private readonly IKioskProfilePaths _profile;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly ILogger<PlayerSettingsSync> _logger;

    /// <summary>Creates the sync.</summary>
    public PlayerSettingsSync(
        IHttpClientFactory httpClientFactory,
        IServerClient server,
        IKioskProfilePaths profile,
        IOptionsMonitor<AgentSettings> settings,
        ILogger<PlayerSettingsSync> logger)
    {
        _httpClientFactory = httpClientFactory;
        _server = server;
        _profile = profile;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Maximum time for one download or upload.</summary>
    public static TimeSpan TransferTimeout => TimeSpan.FromMinutes(2);

    private PlayerSettingsSyncSettings Options => _settings.CurrentValue.Games.PlayerSettings;

    private long MaxBytes => Options.MaxMb * 1024L * 1024L;

    private string BundleRoot => _settings.CurrentValue.ResolvePath(Options.Root);

    private bool Applies(Game game, Guid userId) =>
        Options.Enabled && userId != Guid.Empty && game.SettingsPaths is { Count: > 0 };

    /// <summary>Puts the player's saved settings for <paramref name="game"/> in place. Returns <see langword="true"/> when something was restored.</summary>
    public async Task<bool> RestoreAsync(Game game, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (!Applies(game, userId))
        {
            return false;
        }

        string tmp = Path.Combine(BundleRoot, $"{userId:N}-{game.Id:N}-download.zip");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TransferTimeout);
            CancellationToken ct = timeout.Token;

            PlayerSettingsBundle? bundle = await _server.GetPlayerSettingsAsync(userId, game.Id, ct).ConfigureAwait(false);
            if (bundle is null)
            {
                return false;
            }

            if (bundle.SizeBytes > MaxBytes)
            {
                _logger.LogWarning("Settings for {Title} are {Size} bytes, above the {Max} byte limit; skipped", game.Title, bundle.SizeBytes, MaxBytes);
                return false;
            }

            Directory.CreateDirectory(BundleRoot);
            using HttpClient http = _httpClientFactory.CreateClient(RetryPolicy.DownloadHttpClientName);
            using (HttpResponseMessage response = await http.GetAsync(new Uri(bundle.Url), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using FileStream file = new(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await CopyBoundedAsync(body, file, MaxBytes, ct).ConfigureAwait(false);
            }

            string sha = await Signing.Sha256FileAsync(tmp, ct).ConfigureAwait(false);
            if (!sha.Equals(bundle.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Settings for {Title} failed SHA-256 verification; skipped", game.Title);
                return false;
            }

            int files = await Task.Run(() => SettingsBundle.Unpack(tmp, SettingsBundle.Targets(game, _profile)), ct).ConfigureAwait(false);
            _logger.LogInformation("Player settings restored for {Title}: {Files} files", game.Title, files);
            return files > 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ServerApiException or HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException or UriFormatException)
        {
            _logger.LogWarning(ex, "Player settings for {Title} not restored; launching with the PC's own", game.Title);
            return false;
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    /// <summary>Zips the player's settings after the game exited and stores them on the server. Returns the new bundle, or <see langword="null"/> when skipped or failed.</summary>
    public async Task<PlayerSettingsBundle?> SaveAsync(Game game, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (!Applies(game, userId))
        {
            return null;
        }

        string tmp = Path.Combine(BundleRoot, $"{userId:N}-{game.Id:N}-upload.zip");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TransferTimeout);
            CancellationToken ct = timeout.Token;

            Directory.CreateDirectory(BundleRoot);
            int files = await Task.Run(() => SettingsBundle.Pack(SettingsBundle.Targets(game, _profile), tmp), ct).ConfigureAwait(false);
            if (files == 0)
            {
                return null;
            }

            long size = new FileInfo(tmp).Length;
            if (size > MaxBytes)
            {
                _logger.LogWarning("Settings for {Title} are {Size} bytes, above the {Max} byte limit; not saved", game.Title, size, MaxBytes);
                return null;
            }

            string sha = await Signing.Sha256FileAsync(tmp, ct).ConfigureAwait(false);
            SaveUploadTarget target = await _server.GetPlayerSettingsUploadTargetAsync(userId, game.Id, ct).ConfigureAwait(false);
            if (size > target.MaxBytes)
            {
                _logger.LogWarning("Settings for {Title} ({Size} bytes) exceed the server limit {Max}; not saved", game.Title, size, target.MaxBytes);
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

            PlayerSettingsBundle saved = await _server
                .CommitPlayerSettingsAsync(userId, game.Id, new PlayerSettingsCommitRequest(target.UploadUrl, sha, size), ct)
                .ConfigureAwait(false);
            _logger.LogInformation("Player settings saved for {Title}: {Files} files, {Size} bytes", game.Title, files, size);
            return saved;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ServerApiException or HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException or UriFormatException)
        {
            _logger.LogWarning(ex, "Player settings for {Title} not saved", game.Title);
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
