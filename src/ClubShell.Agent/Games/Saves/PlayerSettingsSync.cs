using System.Collections.Concurrent;
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
/// player.
/// <para>
/// The PC's own files at those paths are the club's baseline: they are snapshotted before the first restore and put
/// back after every save, so the next player — with no saved settings, a guest, or after a reset — starts from the
/// club's defaults, never from the previous player's. Restore and save of one game are serialised, so a quick relaunch
/// waits for the save of the last run. Every failure is logged and swallowed: settings never block a launch or an exit.
/// </para>
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
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

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

    private string BaselinePath(Game game) => Path.Combine(BundleRoot, $"baseline-{game.Id:N}.zip");

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

        SemaphoreSlim gate = _locks.GetOrAdd(game.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string tmp = Path.Combine(BundleRoot, $"{userId:N}-{game.Id:N}-download.zip");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TransferTimeout);
            CancellationToken ct = timeout.Token;

            IReadOnlyList<SettingsTarget> targets = SettingsBundle.Targets(game, _profile);
            // The club's own files, kept until the save of this run puts them back. A snapshot left by a crash is
            // still the club's (it was taken before any player's settings were written), so it is never retaken.
            string baseline = BaselinePath(game);
            if (!File.Exists(baseline))
            {
                int packed = await Task.Run(() => SettingsBundle.Pack(targets, baseline, MaxBytes, writeEmpty: true, ct), ct).ConfigureAwait(false);
                if (packed < 0)
                {
                    _logger.LogWarning("Settings paths of {Title} hold more than {Max} bytes; player settings skipped for this game", game.Title, MaxBytes);
                    return false;
                }
            }

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

            int files = await Task.Run(() => SettingsBundle.Unpack(tmp, targets), ct).ConfigureAwait(false);
            _logger.LogInformation("Player settings restored for {Title}: {Files} files", game.Title, files);
            return files > 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Player settings for {Title} not restored; launching with the PC's own", game.Title);
            return false;
        }
        finally
        {
            TryDelete(tmp);
            gate.Release();
        }
    }

    /// <summary>
    /// Zips the player's settings after the game exited, stores them on the server and puts the club's baseline back.
    /// Returns the new bundle, or <see langword="null"/> when skipped or failed.
    /// </summary>
    public async Task<PlayerSettingsBundle?> SaveAsync(Game game, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (!Applies(game, userId))
        {
            return null;
        }

        SemaphoreSlim gate = _locks.GetOrAdd(game.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string tmp = Path.Combine(BundleRoot, $"{userId:N}-{game.Id:N}-upload.zip");
        IReadOnlyList<SettingsTarget> targets = SettingsBundle.Targets(game, _profile);
        try
        {
            return await UploadAsync(game, userId, targets, tmp, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(tmp);
            ResetToBaseline(game, targets);
            gate.Release();
        }
    }

    private async Task<PlayerSettingsBundle?> UploadAsync(Game game, Guid userId, IReadOnlyList<SettingsTarget> targets, string tmp, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TransferTimeout);
            CancellationToken ct = timeout.Token;

            int files = await Task.Run(() => SettingsBundle.Pack(targets, tmp, MaxBytes, writeEmpty: false, ct), ct).ConfigureAwait(false);
            if (files == 0)
            {
                return null;
            }

            if (files < 0)
            {
                _logger.LogWarning("Settings for {Title} are above the {Max} byte limit; not saved", game.Title, MaxBytes);
                return null;
            }

            long size = new FileInfo(tmp).Length;
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Player settings for {Title} not saved", game.Title);
            return null;
        }
    }

    /// <summary>Removes the player's files at the targets and restores the club's snapshot taken before the launch.</summary>
    private void ResetToBaseline(Game game, IReadOnlyList<SettingsTarget> targets)
    {
        string baseline = BaselinePath(game);
        if (!File.Exists(baseline))
        {
            return;
        }

        try
        {
            SettingsBundle.Clear(targets);
            SettingsBundle.Unpack(baseline, targets);
            File.Delete(baseline);
        }
        catch (Exception ex)
        {
            // The snapshot stays for the next attempt; the next restore reuses it instead of snapshotting this player.
            _logger.LogWarning(ex, "Club settings of {Title} could not be put back after the game", game.Title);
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
