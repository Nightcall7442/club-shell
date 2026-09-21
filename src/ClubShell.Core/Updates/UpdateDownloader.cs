using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Ipc;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Http;
using ClubShell.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClubShell.Core.Updates;

/// <summary>
/// Downloads update packages (ARCHITECTURE.md §11) into <c>cache\updates\&lt;component&gt;-&lt;version&gt;.tmp</c> with
/// Bearer auth and HTTP <c>Range</c> resume, throttled progress reports (≤ 2/s), SHA-256 and RSA-PSS verification,
/// then an atomic rename to the final <c>.msi</c>/<c>.exe</c>. Stale files are pruned with <see cref="CleanStale"/>.
/// </summary>
public sealed class UpdateDownloader
{
    /// <summary>Download attempts per package (each resumes where the previous stopped).</summary>
    public const int MaxAttempts = 3;

    /// <summary>Minimum time between two progress reports.</summary>
    public static TimeSpan ProgressInterval { get; } = TimeSpan.FromMilliseconds(500);

    private const int BufferBytes = 1 << 16;
    private const string TempExtension = ".tmp";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITokenStore _tokens;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<UpdateDownloader> _logger;

    /// <summary>Creates the downloader over the <see cref="RetryPolicy.DownloadHttpClientName"/> client.</summary>
    public UpdateDownloader(IHttpClientFactory httpClientFactory, ITokenStore tokens, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<UpdateDownloader> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _tokens = tokens;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Accept packages without a signature check when no public key is supplied (dev/mock server only).</summary>
    public bool AllowUnsignedPackages { get; set; }

    /// <summary>Directory packages are downloaded to (<c>updates.downloadDir</c>).</summary>
    public string DownloadDirectory => _settings.CurrentValue.UpdatesDownloadDir;

    /// <summary>Final path of the package of <paramref name="manifest"/>.</summary>
    public string PackagePath(UpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Path.Combine(DownloadDirectory, manifest.PackageFileName());
    }

    /// <summary>Temporary path used while downloading.</summary>
    public string TempPath(UpdateManifest manifest) =>
        Path.ChangeExtension(PackagePath(manifest), TempExtension);

    /// <summary>
    /// Downloads (or resumes) the package, verifies size, SHA-256 and signature, and returns the final package path.
    /// A previously completed and verified package is returned without a download. Throws <see cref="UpdateException"/>
    /// with the failing <see cref="UpdatePhase"/>.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateManifest manifest, RSA? publicKey, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Directory.CreateDirectory(DownloadDirectory);
        var finalPath = PackagePath(manifest);
        var tempPath = TempPath(manifest);

        if (File.Exists(finalPath))
        {
            try
            {
                await VerifyAsync(finalPath, manifest, publicKey, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("{Component} {Version} already downloaded and verified", manifest.Component, manifest.Version);
                Report(progress, manifest, UpdatePhase.Verifying, manifest.Size, manifest.Size);
                return finalPath;
            }
            catch (UpdateException ex)
            {
                _logger.LogWarning(ex, "Existing package {Path} failed verification; re-downloading", finalPath);
                File.Delete(finalPath);
            }
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadToAsync(manifest, tempPath, progress, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex, cancellationToken))
            {
                _logger.LogWarning(ex, "Download attempt {Attempt} of {Component} {Version} failed; resuming", attempt, manifest.Component, manifest.Version);
                await _clock.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException)
            {
                throw new UpdateException(UpdatePhase.Downloading, $"Download of {manifest.Component} {manifest.Version} failed: {ex.Message}", ex);
            }
        }

        Report(progress, manifest, UpdatePhase.Verifying, 0, manifest.Size);
        try
        {
            await VerifyAsync(tempPath, manifest, publicKey, cancellationToken).ConfigureAwait(false);
        }
        catch (UpdateException)
        {
            TryDelete(tempPath);
            throw;
        }

        File.Move(tempPath, finalPath, overwrite: true);
        Report(progress, manifest, UpdatePhase.Verifying, manifest.Size, manifest.Size);
        _logger.LogInformation("{Component} {Version} downloaded and verified at {Path}", manifest.Component, manifest.Version, finalPath);
        return finalPath;
    }

    /// <summary>Verifies size, SHA-256 and RSA-PSS signature of <paramref name="path"/> against <paramref name="manifest"/>; throws <see cref="UpdateException"/> (<see cref="UpdatePhase.Verifying"/>) on mismatch.</summary>
    public async Task VerifyAsync(string path, UpdateManifest manifest, RSA? publicKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(manifest);

        var length = new FileInfo(path).Length;
        if (length != manifest.Size)
        {
            throw new UpdateException(UpdatePhase.Verifying, $"Package size {length} differs from manifest size {manifest.Size}");
        }

        var sha256 = await Signing.Sha256FileAsync(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(sha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException(UpdatePhase.Verifying, "Package SHA-256 does not match the manifest");
        }

        if (publicKey is null)
        {
            if (!AllowUnsignedPackages)
            {
                throw new UpdateException(UpdatePhase.Verifying, "No update public key configured; refusing unsigned package");
            }

            _logger.LogWarning("Signature of {Path} not verified: no public key (AllowUnsignedPackages)", path);
            return;
        }

        if (!await Signing.VerifyFileSignatureAsync(path, manifest.Signature, publicKey, cancellationToken).ConfigureAwait(false))
        {
            throw new UpdateException(UpdatePhase.Verifying, "Package signature is invalid");
        }
    }

    /// <summary>Deletes temporary and package files older than <paramref name="olderThan"/>, except <paramref name="keep"/> (full paths). Returns the number of files removed.</summary>
    public int CleanStale(TimeSpan olderThan, IReadOnlyCollection<string>? keep = null)
    {
        var directory = DownloadDirectory;
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var cutoff = _clock.UtcNow - olderThan;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (keep is not null && keep.Contains(file, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not delete stale update file {Path}", file);
            }
        }

        return removed;
    }

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or IOException or TimeoutException
        || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for CleanStale.
        }
    }

    private static void Report(IProgress<UpdateProgress>? progress, UpdateManifest manifest, UpdatePhase phase, long done, long total)
    {
        if (progress is null)
        {
            return;
        }

        var percent = total <= 0 ? 0 : (int)Math.Clamp(done * 100 / total, 0, 100);
        progress.Report(new UpdateProgress(manifest.Component, manifest.Version, phase, percent, done, total));
    }

    private async Task DownloadToAsync(UpdateManifest manifest, string tempPath, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        var existing = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0L;
        if (existing > manifest.Size)
        {
            TryDelete(tempPath);
            existing = 0;
        }

        if (existing == manifest.Size)
        {
            Report(progress, manifest, UpdatePhase.Downloading, existing, manifest.Size);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, manifest.Url);
        if (_tokens.Agent is { } tokens)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        }

        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using var client = _httpClientFactory.CreateClient(RetryPolicy.DownloadHttpClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        var append = false;
        switch (response.StatusCode)
        {
            case HttpStatusCode.PartialContent:
                var rangeStart = response.Content.Headers.ContentRange?.From;
                if (rangeStart == existing)
                {
                    append = true;
                }
                else
                {
                    throw new IOException($"Server resumed at {rangeStart?.ToString(CultureInfo.InvariantCulture) ?? "?"} instead of {existing}");
                }

                break;

            case HttpStatusCode.OK:
                existing = 0;
                break;

            case HttpStatusCode.RequestedRangeNotSatisfiable:
                TryDelete(tempPath);
                throw new IOException("Server rejected the resume range; restarting the download");

            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                throw new UpdateException(UpdatePhase.Downloading, $"Package download rejected with HTTP {(int)response.StatusCode}");

            default:
                if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                {
                    throw new HttpRequestException($"Package download failed with HTTP {(int)response.StatusCode}", null, response.StatusCode);
                }

                throw new UpdateException(UpdatePhase.Downloading, $"Package download failed with HTTP {(int)response.StatusCode}");
        }

        var total = manifest.Size;
        var done = existing;
        var lastReport = _clock.GetTimestamp();
        Report(progress, manifest, UpdatePhase.Downloading, done, total);

        var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var target = new FileStream(tempPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.Asynchronous);
            await using (target.ConfigureAwait(false))
            {
                var buffer = new byte[BufferBytes];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    if (done > total)
                    {
                        throw new UpdateException(UpdatePhase.Downloading, $"Server sent more than the manifest size {total}");
                    }

                    if (_clock.GetElapsedTime(lastReport) >= ProgressInterval)
                    {
                        lastReport = _clock.GetTimestamp();
                        Report(progress, manifest, UpdatePhase.Downloading, done, total);
                    }
                }

                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        Report(progress, manifest, UpdatePhase.Downloading, done, total);
        if (done < total)
        {
            throw new IOException($"Download ended at {done} of {total} bytes");
        }
    }
}
