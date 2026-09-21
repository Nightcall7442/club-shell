using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Security;
using ClubShell.Windows.Network;
using ClubShell.Windows.Sessions;
using ClubShell.Windows.Storage;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Storage;

/// <summary>
/// Mounts the games storage described by <c>storage.gamesShare</c> (ARCHITECTURE.md §6.1 step 8): an SMB share
/// mapped to <c>driveLetter</c> for the Agent (session 0) and, whenever a user is logged on to the console, inside
/// that user's logon session too (drive letters are per logon session); or an iSCSI target logged in through the
/// Microsoft initiator and waited for as a volume. Mounting is retried with exponential backoff, verified with a
/// read test every 30 s and redone on network changes. Credentials come DPAPI-protected from
/// <c>credentialsRef</c> (<c>{ "username": ..., "password": ... }</c>, also used as CHAP for iSCSI).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GamesShareMounter : IHostedService, IDisposable
{
    private const uint NoSession = 0xFFFFFFFF;
    private static readonly TimeSpan VerifyInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan VolumeTimeout = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly NetworkShare _share;
    private readonly IscsiInitiator _iscsi;
    private readonly NetworkProbe _network;
    private readonly ITokenProtector _protector;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<GamesShareMounter> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _mountLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _mounted;
    private bool _disposed;

    /// <summary>Creates the mounter.</summary>
    public GamesShareMounter(NetworkShare share, IscsiInitiator iscsi, NetworkProbe network, ITokenProtector protector, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<GamesShareMounter> logger)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(iscsi);
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _share = share;
        _iscsi = iscsi;
        _network = network;
        _protector = protector;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Raised on every transition of <see cref="IsMounted"/>.</summary>
    public event EventHandler<bool>? Changed;

    /// <summary><see langword="true"/> while the storage is mapped and readable.</summary>
    public bool IsMounted => _mounted;

    /// <summary>Time of the last <see cref="IsMounted"/> transition.</summary>
    public DateTimeOffset? ChangedAt { get; private set; }

    /// <summary><see langword="true"/> when <c>storage.gamesShare.enabled</c>.</summary>
    public bool IsEnabled => _settings.CurrentValue.Storage.GamesShare.Enabled;

    /// <summary>Drive root (<c>G:\</c>) when enabled, otherwise <see langword="null"/>.</summary>
    public string? MountPoint => IsEnabled ? DriveRoot(DriveLetter(_settings.CurrentValue.Storage.GamesShare)) : null;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsEnabled)
        {
            _logger.LogInformation("Games share disabled (storage.gamesShare.enabled = false)");
            return Task.CompletedTask;
        }

        _network.Changed += OnNetworkChanged;
        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _network.Changed -= OnNetworkChanged;
        if (_cts is { } cts)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is { } loop)
        {
            try
            {
                await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Loop cancelled or host stop timed out.
            }
        }

        await UnmountAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One mount attempt (SMB map or iSCSI login + volume wait) followed by a read test; updates <see cref="IsMounted"/>.</summary>
    public async Task<bool> MountAsync(CancellationToken cancellationToken)
    {
        await _mountLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = _settings.CurrentValue.Storage.GamesShare;
            var letter = DriveLetter(config);
            var root = DriveRoot(letter);
            var credentials = await LoadCredentialsAsync(config, cancellationToken).ConfigureAwait(false);

            var ok = config.Iscsi is { } iscsi
                ? await MountIscsiAsync(iscsi, letter, credentials, cancellationToken).ConfigureAwait(false)
                : MountSmb(config, letter, credentials);

            if (ok && !NetworkShare.TestReadAccess(root))
            {
                _logger.LogWarning("Games storage {Root} is mapped but not readable", root);
                ok = false;
            }

            if (ok && config.Iscsi is null)
            {
                MapForConsoleSession(config, letter, credentials);
            }

            SetMounted(ok, root);
            return ok;
        }
        finally
        {
            _mountLock.Release();
        }
    }

    /// <summary>Asks the background loop to re-verify / re-map immediately.</summary>
    public void RequestRemount() => Signal();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _network.Changed -= OnNetworkChanged;
        _cts?.Dispose();
        _wake.Dispose();
        _mountLock.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Splits <c>host[:port]</c>; IPv6 literals without brackets are treated as host only.</summary>
    public static (string Host, int Port) ParsePortal(string portal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portal);
        var text = portal.Trim();
        var colon = text.LastIndexOf(':');
        if (colon > 0 && colon < text.Length - 1 && text.IndexOf(':', StringComparison.Ordinal) == colon
            && int.TryParse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port > 0)
        {
            return (text[..colon], port);
        }

        return (text, IscsiInitiator.DefaultPort);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static char DriveLetter(GamesShareSettings config) =>
        string.IsNullOrEmpty(config.DriveLetter) ? 'G' : char.ToUpperInvariant(config.DriveLetter[0]);

    private static string DriveRoot(char letter) => letter + ":\\";

    private static TimeSpan Backoff(int attempt)
    {
        var factor = Math.Pow(2, Math.Clamp(attempt - 1, 0, 10));
        var delay = TimeSpan.FromMilliseconds(MinBackoff.TotalMilliseconds * factor);
        return delay > MaxBackoff ? MaxBackoff : delay;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        var retries = Math.Max(1, _settings.CurrentValue.Storage.GamesShare.MountRetries);
        while (!cancellationToken.IsCancellationRequested)
        {
            bool ok;
            try
            {
                ok = await MountAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Games storage mount attempt failed");
                ok = false;
            }

            TimeSpan wait;
            if (ok)
            {
                attempt = 0;
                wait = VerifyInterval;
            }
            else
            {
                attempt++;
                wait = Backoff(attempt);
                if (attempt == retries)
                {
                    _logger.LogError("Games storage could not be mounted after {Attempts} attempts; continuing with backoff up to {Max}", attempt, MaxBackoff);
                }
            }

            try
            {
                _ = await _wake.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private bool MountSmb(GamesShareSettings config, char letter, ShareCredentials? credentials)
    {
        try
        {
            _share.Map(letter, config.UncPath, credentials?.Username, credentials?.Password);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException)
        {
            _logger.LogWarning(ex, "Mapping {Unc} to {Letter}: failed", config.UncPath, letter);
            return false;
        }
    }

    private void MapForConsoleSession(GamesShareSettings config, char letter, ShareCredentials? credentials)
    {
        var sessionId = WtsSessions.GetActiveConsoleSessionId();
        if (sessionId is 0 or NoSession)
        {
            return;
        }

        var session = WtsSessions.Get(sessionId);
        if (session is null || !session.HasUser)
        {
            return;
        }

        try
        {
            if (NetworkShare.IsMappedForSession(sessionId, letter, out _))
            {
                return;
            }

            _share.MapForSession(sessionId, letter, config.UncPath, credentials?.Username, credentials?.Password);
            _logger.LogInformation("{Unc} mapped to {Letter}: in session {SessionId} ({User})", config.UncPath, letter, sessionId, session.UserName);
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException)
        {
            _logger.LogWarning(ex, "Mapping {Unc} in session {SessionId} failed", config.UncPath, sessionId);
        }
    }

    private async Task<bool> MountIscsiAsync(IscsiSettings iscsi, char letter, ShareCredentials? credentials, CancellationToken cancellationToken)
    {
        try
        {
            var (host, port) = ParsePortal(iscsi.Portal);
            await _iscsi.EnsureServiceRunningAsync(cancellationToken).ConfigureAwait(false);
            await _iscsi.AddTargetPortalAsync(host, port, cancellationToken).ConfigureAwait(false);
            var chap = credentials is null ? null : new IscsiChapCredentials(credentials.Username, credentials.Password);
            await _iscsi.LoginAsync(iscsi.TargetIqn, persistent: false, chap, cancellationToken).ConfigureAwait(false);
            if (await _iscsi.WaitForVolumeAsync(letter, VolumeTimeout, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            _logger.LogWarning("iSCSI target {Target} logged in but volume {Letter}: did not appear within {Timeout}", iscsi.TargetIqn, letter, VolumeTimeout);
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException or ArgumentException)
        {
            _logger.LogWarning(ex, "iSCSI mount of {Target} failed", iscsi.TargetIqn);
            return false;
        }
    }

    private async Task UnmountAsync(CancellationToken cancellationToken)
    {
        var config = _settings.CurrentValue.Storage.GamesShare;
        if (!config.Enabled)
        {
            return;
        }

        var letter = DriveLetter(config);
        try
        {
            if (config.Iscsi is { } iscsi)
            {
                _ = await _iscsi.LogoutAsync(iscsi.TargetIqn, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _ = _share.Unmap(letter);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            _logger.LogWarning(ex, "Games storage unmount failed");
        }

        SetMounted(false, DriveRoot(letter));
    }

    private async Task<ShareCredentials?> LoadCredentialsAsync(GamesShareSettings config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.CredentialsRef))
        {
            return null;
        }

        var path = _settings.CurrentValue.ResolvePath(config.CredentialsRef);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var ciphertext = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var plaintext = _protector.Unprotect(ciphertext);
            var credentials = JsonSerializer.Deserialize<ShareCredentials>(plaintext, JsonOptions);
            CryptographicOperations.ZeroMemory(plaintext);
            return credentials is { Username.Length: > 0 } ? credentials : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Share credentials {Path} are unreadable; mapping without credentials", path);
            return null;
        }
    }

    private void SetMounted(bool mounted, string root)
    {
        if (_mounted == mounted)
        {
            return;
        }

        _mounted = mounted;
        ChangedAt = _clock.UtcNow;
        _logger.LogInformation("Games storage {Root} {State}", root, mounted ? "mounted" : "unmounted");
        try
        {
            Changed?.Invoke(this, mounted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Games storage Changed listener threw");
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        _logger.LogDebug("Network change detected; re-verifying games storage");
        Signal();
    }

    private void Signal()
    {
        if (_disposed || _wake.CurrentCount > 0)
        {
            return;
        }

        try
        {
            _ = _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
        catch (ObjectDisposedException)
        {
            // Stopped concurrently.
        }
    }

    /// <summary>Contents of <c>credentialsRef</c> after DPAPI unwrapping.</summary>
    private sealed record ShareCredentials(string Username, string Password);
}
