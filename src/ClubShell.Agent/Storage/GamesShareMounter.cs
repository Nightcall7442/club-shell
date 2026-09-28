using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Contracts.Commands;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Core.Security;
using ClubShell.Windows.Network;
using ClubShell.Windows.Registry;
using ClubShell.Windows.Sessions;
using ClubShell.Windows.Storage;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace ClubShell.Agent.Storage;

/// <summary>
/// Mounts the games SMB share described by <c>storage.gamesShare</c> (ARCHITECTURE.md §6.1 step 8) at
/// <c>driveLetter</c> for the Agent (session 0) and, whenever a user is logged on to the console, inside that user's
/// logon session too (drive letters are per logon session). Mounting is retried with exponential backoff; a healthy
/// mapping is only verified (still readable) every 30 s and on network changes, never redone. Settings changes
/// (<c>agent.json</c>, server config) are followed: enabling starts mounting, and disabling or changing
/// <c>uncPath</c> / <c>driveLetter</c> / <c>credentialsRef</c> first unmaps what the previous settings mapped.
/// Stopping the Agent (restart, update) leaves the mapping alone, so a running game keeps its files. Credentials
/// come DPAPI-protected from <c>credentialsRef</c> (<c>{ "username": ..., "password": ... }</c>).
/// <para>
/// The Agent never mounts iSCSI: a games library over iSCSI belongs to ClubDisklessHelper, which marks the disk
/// read-only before it goes online. A configured <c>storage.gamesShare.iscsi</c> makes this mounter mount nothing
/// (fail closed) and log an error.
/// </para>
/// <para>
/// When ClubDisklessHelper is installed (docs/DISKLESS.md) the helper owns the games library volume and this
/// mounter stands down completely: it never maps or unmaps anything, even when <c>storage.gamesShare</c> is also
/// configured — two owners of one drive letter would fight every 30 s.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GamesShareMounter : IHostedService, IDisposable
{
    private const uint NoSession = 0xFFFFFFFF;
    private static readonly TimeSpan VerifyInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    /// <summary>Windows service name of ClubDisklessHelper.</summary>
    public const string DisklessHelperServiceName = "ClubDisklessHelper";

    private readonly NetworkShare _share;
    private readonly NetworkProbe _network;
    private readonly ITokenProtector _protector;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<GamesShareMounter> _logger;
    private readonly Func<bool> _disklessHelperInstalled;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private IDisposable? _onChange;
    private volatile ShareConfig? _applied;
    private ShareCredentials? _credentials;
    private volatile bool _mounted;
    private bool _disposed;

    /// <summary>Creates the mounter.</summary>
    /// <param name="disklessHelperInstalled">Probe for ClubDisklessHelper; defaults to checking its service key.</param>
    public GamesShareMounter(NetworkShare share, NetworkProbe network, ITokenProtector protector, IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<GamesShareMounter> logger, Func<bool>? disklessHelperInstalled = null)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _share = share;
        _network = network;
        _protector = protector;
        _settings = settings;
        _clock = clock;
        _logger = logger;
        _disklessHelperInstalled = disklessHelperInstalled ?? IsDisklessHelperInstalled;
    }

    /// <summary>Raised on every transition of <see cref="IsMounted"/>.</summary>
    public event EventHandler<bool>? Changed;

    /// <summary><see langword="true"/> while the storage is mapped and readable.</summary>
    public bool IsMounted => _mounted;

    /// <summary>Time of the last <see cref="IsMounted"/> transition.</summary>
    public DateTimeOffset? ChangedAt { get; private set; }

    /// <summary><see langword="true"/> when <c>storage.gamesShare.enabled</c>.</summary>
    public bool IsEnabled => _settings.CurrentValue.Storage.GamesShare.Enabled;

    /// <summary><see langword="true"/> after <see cref="StartAsync"/> found ClubDisklessHelper installed: the helper owns the library.</summary>
    public bool OwnedByDisklessHelper { get; private set; }

    /// <summary>Drive root (<c>G:\</c>) when enabled, otherwise <see langword="null"/>.</summary>
    public string? MountPoint => IsEnabled ? DriveRoot(DriveLetter(_settings.CurrentValue.Storage.GamesShare)) : null;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        OwnedByDisklessHelper = _disklessHelperInstalled();
        if (OwnedByDisklessHelper)
        {
            if (IsEnabled)
            {
                _logger.LogWarning(
                    "storage.gamesShare is enabled, but {Service} is installed and owns the games library volume; the Agent will not map or unmap anything. Set storage.gamesShare.enabled = false",
                    DisklessHelperServiceName);
            }
            else
            {
                _logger.LogInformation("Games library volume is managed by {Service}", DisklessHelperServiceName);
            }

            return Task.CompletedTask;
        }

        // The loop always runs (it only waits while nothing is to be mounted), so enabling from the server works.
        var initial = ShareConfig.From(_settings.CurrentValue.Storage.GamesShare);
        LogConfig(initial, _settings.CurrentValue.Storage.GamesShare);
        _applied = initial;
        _onChange = _settings.OnChange((settings, _) => OnSettingsChanged(settings));
        _network.Changed += OnNetworkChanged;
        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    /// <summary>Stops the loop. The mapping is left in place: an Agent restart or update must not pull the library from under a running game.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _network.Changed -= OnNetworkChanged;
        _onChange?.Dispose();
        _onChange = null;
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
    }

    /// <summary>Asks the background loop to re-verify / re-map immediately.</summary>
    public void RequestRemount() => Signal();

    /// <summary>Games library state for the heartbeat (<c>gamesVolume</c>). The Agent does not see ClubDisklessHelper's mount, so <c>mounted</c> is unknown then.</summary>
    public HeartbeatGamesVolume VolumeState()
    {
        if (OwnedByDisklessHelper)
        {
            return new HeartbeatGamesVolume(GamesVolumeOwner.DisklessHelper, null, null, null);
        }

        var config = _settings.CurrentValue.Storage.GamesShare;
        return config.Enabled
            ? new HeartbeatGamesVolume(GamesVolumeOwner.Agent, _mounted, DriveLetter(config).ToString(), ChangedAt)
            : new HeartbeatGamesVolume(GamesVolumeOwner.None, false, null, null);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _network.Changed -= OnNetworkChanged;
        _onChange?.Dispose();
        _cts?.Dispose();
        _wake.Dispose();
        GC.SuppressFinalize(this);
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

    private static bool IsDisklessHelperInstalled() =>
        RegistryHelper.Exists(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\" + DisklessHelperServiceName);

    private static char DriveLetter(GamesShareSettings config) =>
        string.IsNullOrEmpty(config.DriveLetter) ? 'G' : char.ToUpperInvariant(config.DriveLetter[0]);

    private static string DriveRoot(char letter) => letter + ":\\";

    private static TimeSpan Backoff(int attempt)
    {
        var factor = Math.Pow(2, Math.Clamp(attempt - 1, 0, 10));
        var delay = TimeSpan.FromMilliseconds(MinBackoff.TotalMilliseconds * factor);
        return delay > MaxBackoff ? MaxBackoff : delay;
    }

    private void LogConfig(ShareConfig config, GamesShareSettings settings)
    {
        if (config.Mounts)
        {
            _logger.LogInformation("Games share {Unc} -> {Letter}:", config.UncPath, config.Letter);
        }
        else if (config.Enabled)
        {
            _logger.LogError(
                "storage.gamesShare.iscsi is set ({Target}), but the Agent does not mount iSCSI: the games library over iSCSI is handled by {Service} (docs/DISKLESS.md). Nothing is mounted; install {Service} or set storage.gamesShare.iscsi = null for an SMB share",
                settings.Iscsi?.TargetIqn,
                DisklessHelperServiceName,
                DisklessHelperServiceName);
        }
        else
        {
            _logger.LogInformation("Games share disabled (storage.gamesShare.enabled = false)");
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var settings = _settings.CurrentValue.Storage.GamesShare;
            var config = ShareConfig.From(settings);
            if (_applied is { } previous && previous != config)
            {
                // Unmap by the old snapshot: the new settings no longer say what was mapped.
                if (previous.Mounts)
                {
                    Unmap(previous);
                }

                _credentials = null;
                attempt = 0;
                LogConfig(config, settings);
            }

            _applied = config;
            var wait = Timeout.InfiniteTimeSpan;
            if (config.Mounts)
            {
                bool ok;
                try
                {
                    ok = await MountAsync(config, cancellationToken).ConfigureAwait(false);
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

                if (ok)
                {
                    attempt = 0;
                    wait = VerifyInterval;
                }
                else
                {
                    attempt++;
                    wait = Backoff(attempt);
                    if (attempt == Math.Max(1, settings.MountRetries))
                    {
                        _logger.LogError("Games storage could not be mounted after {Attempts} attempts; continuing with backoff up to {Max}", attempt, MaxBackoff);
                    }
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

    /// <summary>One mount attempt followed by a read test; a mapping made earlier that is still readable is only verified.</summary>
    private async Task<bool> MountAsync(ShareConfig config, CancellationToken cancellationToken)
    {
        var root = DriveRoot(config.Letter);
        var ok = _mounted && NetworkShare.TestReadAccess(root);
        if (!ok)
        {
            _credentials = await LoadCredentialsAsync(config.CredentialsRef, cancellationToken).ConfigureAwait(false);
            ok = MountSmb(config);
            if (ok && !NetworkShare.TestReadAccess(root))
            {
                _logger.LogWarning("Games storage {Root} is mapped but not readable", root);
                ok = false;
            }
        }

        if (ok)
        {
            MapForConsoleSession(config);
        }

        SetMounted(ok, root);
        return ok;
    }

    private bool MountSmb(ShareConfig config)
    {
        try
        {
            _share.Map(config.Letter, config.UncPath, _credentials?.Username, _credentials?.Password);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException)
        {
            _logger.LogWarning(ex, "Mapping {Unc} to {Letter}: failed", config.UncPath, config.Letter);
            return false;
        }
    }

    private void MapForConsoleSession(ShareConfig config)
    {
        if (ConsoleUserSession() is not { } sessionId)
        {
            return;
        }

        try
        {
            if (NetworkShare.IsMappedForSession(sessionId, config.Letter, out _))
            {
                return;
            }

            _share.MapForSession(sessionId, config.Letter, config.UncPath, _credentials?.Username, _credentials?.Password);
            _logger.LogInformation("{Unc} mapped to {Letter}: in session {SessionId}", config.UncPath, config.Letter, sessionId);
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException)
        {
            _logger.LogWarning(ex, "Mapping {Unc} in session {SessionId} failed", config.UncPath, sessionId);
        }
    }

    /// <summary>Removes the mapping of <paramref name="config"/> from session 0 and the console user's session.</summary>
    private void Unmap(ShareConfig config)
    {
        try
        {
            _ = _share.Unmap(config.Letter);
            if (ConsoleUserSession() is { } sessionId && NetworkShare.IsMappedForSession(sessionId, config.Letter, out _))
            {
                _ = _share.UnmapForSession(sessionId, config.Letter);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never let a failed unmap end the loop: the new settings still have to be mounted.
            _logger.LogWarning(ex, "Unmapping {Unc} from {Letter}: failed", config.UncPath, config.Letter);
        }

        SetMounted(false, DriveRoot(config.Letter));
    }

    private static uint? ConsoleUserSession()
    {
        var sessionId = WtsSessions.GetActiveConsoleSessionId();
        return sessionId is not (0 or NoSession) && WtsSessions.Get(sessionId) is { HasUser: true } ? sessionId : null;
    }

    private async Task<ShareCredentials?> LoadCredentialsAsync(string credentialsRef, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentialsRef))
        {
            return null;
        }

        var path = _settings.CurrentValue.ResolvePath(credentialsRef);
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

    private void OnSettingsChanged(AgentSettings settings)
    {
        // Every settings publish lands here (server config refresh, agent.json edit); only a share change wakes the loop.
        if (ShareConfig.From(settings.Storage.GamesShare) != _applied)
        {
            Signal();
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

    /// <summary>The <c>storage.gamesShare</c> values a mapping depends on; a different value means unmap and start over.</summary>
    private sealed record ShareConfig(bool Enabled, bool Iscsi, char Letter, string UncPath, string CredentialsRef)
    {
        /// <summary><see langword="true"/> when the Agent maps an SMB share (enabled, no iSCSI).</summary>
        public bool Mounts => Enabled && !Iscsi;

        public static ShareConfig From(GamesShareSettings settings) =>
            new(settings.Enabled, settings.Iscsi is not null, DriveLetter(settings), settings.UncPath ?? "", settings.CredentialsRef ?? "");
    }

    /// <summary>Contents of <c>credentialsRef</c> after DPAPI unwrapping.</summary>
    private sealed record ShareCredentials(string Username, string Password);
}
