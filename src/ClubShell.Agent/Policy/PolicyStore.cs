using System.Text.Json;

using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Serialization;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;

using Microsoft.Extensions.Options;

using PcPolicy = ClubShell.Contracts.Pcs.Policy;

namespace ClubShell.Agent.Policy;

/// <summary>
/// Where the policy comes from, in priority order: the server (<c>GET /agents/{pcId}/policies</c> with ETag), the
/// cache file <c>ProgramData\policies.json</c> (last applied snapshot, ARCHITECTURE.md §12.3) and the shipped
/// <c>config\policies.example.json</c>. The cache file is written atomically and watched so an administrator edit
/// (bumped <c>version</c> or <c>updatedAt</c>) raises <see cref="Changed"/>.
/// </summary>
public sealed class PolicyStore : IDisposable
{
    private const long SelfWriteGraceMs = 2000;
    private static readonly TimeSpan WatchDebounce = TimeSpan.FromMilliseconds(500);

    private readonly IServerClient _server;
    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly ILogger<PolicyStore> _logger;
    private readonly string? _cachePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _watchGate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private long _selfWriteUntil;
    private PcPolicy? _last;
    private PolicySource? _lastSource;
    private string? _etag;
    private bool _disposed;

    /// <summary>Creates the store.</summary>
    /// <param name="server">Server client.</param>
    /// <param name="settings">Agent settings (PC id, ProgramData root).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cachePath">Override of the cache file path (tests).</param>
    /// <param name="shippedDefaultsPath">Override of the shipped defaults path (tests).</param>
    public PolicyStore(
        IServerClient server,
        IOptionsMonitor<AgentSettings> settings,
        ILogger<PolicyStore> logger,
        string? cachePath = null,
        string? shippedDefaultsPath = null)
    {
        _server = server;
        _settings = settings;
        _logger = logger;
        _cachePath = cachePath;
        ShippedDefaultsPath = shippedDefaultsPath ?? Path.Combine(AppContext.BaseDirectory, "config", "policies.example.json");
    }

    /// <summary>Raised when the cache file was changed by someone else and parsed into a valid policy.</summary>
    public event EventHandler<PcPolicy>? Changed;

    /// <summary>Cache file: <c>&lt;paths.programData&gt;\policies.json</c>.</summary>
    public string CachePath => _cachePath ?? _settings.CurrentValue.ResolvePath(ClubShellPaths.PoliciesJsonFileName);

    /// <summary>Shipped fallback next to the executable.</summary>
    public string ShippedDefaultsPath { get; }

    /// <summary>Last policy returned by <see cref="LoadAsync"/> / passed to <see cref="SaveAsync"/>.</summary>
    public PcPolicy? Last => _last;

    /// <summary>Origin of <see cref="Last"/>.</summary>
    public PolicySource? LastSource => _lastSource;

    /// <summary>ETag of the last server copy.</summary>
    public string? ETag => _etag;

    /// <summary>Structural validation of a deserialized document (sections present, version non-negative).</summary>
    public static IReadOnlyList<string> Validate(PcPolicy? policy)
    {
        var problems = new List<string>();
        if (policy is null)
        {
            problems.Add("policy document is empty");
            return problems;
        }

        if (policy.Version < 0)
        {
            problems.Add("version must be >= 0");
        }

        if (policy.ShellReplacement is null)
        {
            problems.Add("shellReplacement is required");
        }
        else if (policy.ShellReplacement.Enabled && string.IsNullOrWhiteSpace(policy.ShellReplacement.ShellExe))
        {
            problems.Add("shellReplacement.shellExe is required when enabled");
        }

        if (policy.ProcessAllowlist is null)
        {
            problems.Add("processAllowlist is required");
        }
        else if (policy.ProcessAllowlist.Patterns is null)
        {
            problems.Add("processAllowlist.patterns is required");
        }

        if (policy.Usb is null)
        {
            problems.Add("usb is required");
        }

        if (policy.WebFilter is null)
        {
            problems.Add("webFilter is required");
        }
        else if (policy.WebFilter.BlockedDomains is null || policy.WebFilter.AllowedDomains is null || policy.WebFilter.DnsServers is null)
        {
            problems.Add("webFilter.blockedDomains, allowedDomains and dnsServers are required");
        }

        if (policy.Explorer is null)
        {
            problems.Add("explorer is required");
        }
        else if (policy.Explorer.BlockedKeyCombos is null)
        {
            problems.Add("explorer.blockedKeyCombos is required");
        }

        if (policy.Power is null)
        {
            problems.Add("power is required");
        }

        if (policy.Updates is null)
        {
            problems.Add("updates is required");
        }

        if (policy.Anticheat is null)
        {
            problems.Add("anticheat is required");
        }
        else if (policy.Anticheat.Required is null)
        {
            problems.Add("anticheat.required is required");
        }

        if (policy.Kiosk is null)
        {
            problems.Add("kiosk is required");
        }

        return problems;
    }

    /// <summary>
    /// Loads the policy: server first (ETag-conditional unless <paramref name="force"/>), then the in-memory copy,
    /// the cache file and the shipped defaults. A file never replaces a higher version already known.
    /// </summary>
    /// <exception cref="InvalidOperationException">No source produced a valid policy.</exception>
    public async Task<(PcPolicy Policy, PolicySource Source)> LoadAsync(bool force, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (await TryLoadFromServerAsync(force, cancellationToken).ConfigureAwait(false) is { } fromServer)
            {
                return (fromServer, PolicySource.Server);
            }

            if (_last is { } last && _lastSource is { } source)
            {
                return (last, source);
            }

            if (await TryLoadFileAsync(CachePath, PolicySource.Cache, cancellationToken).ConfigureAwait(false) is { } cached)
            {
                return (cached, PolicySource.Cache);
            }

            if (await TryLoadFileAsync(ShippedDefaultsPath, PolicySource.File, cancellationToken).ConfigureAwait(false) is { } shipped)
            {
                return (shipped, PolicySource.File);
            }

            throw new InvalidOperationException($"No policy available: server unreachable, no cache at '{CachePath}' and no shipped defaults at '{ShippedDefaultsPath}'.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Writes <paramref name="policy"/> to the cache file atomically (temp file + rename) and makes it <see cref="Last"/>.</summary>
    public async Task SaveAsync(PcPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await WriteCacheAsync(policy, cancellationToken).ConfigureAwait(false);
            _last = policy;
            _lastSource ??= PolicySource.File;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts watching the cache file for external edits (idempotent).</summary>
    public void Watch()
    {
        lock (_watchGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_watcher is not null)
            {
                return;
            }

            string path = CachePath;
            string directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"Cache path '{path}' has no directory.");
            Directory.CreateDirectory(directory);
            _debounce = new Timer(_ => OnDebounced(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            var watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            watcher.Changed += Bump;
            watcher.Created += Bump;
            watcher.Renamed += Bump;
            watcher.Error += (_, e) => _logger.LogWarning(e.GetException(), "Policy file watcher error");
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            _logger.LogDebug("Watching {Path} for policy edits", path);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_watchGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _watcher?.Dispose();
            _watcher = null;
            _debounce?.Dispose();
            _debounce = null;
        }

        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<PcPolicy?> TryLoadFromServerAsync(bool force, CancellationToken cancellationToken)
    {
        if (_settings.CurrentValue.PcId is not { } pcId)
        {
            _logger.LogDebug("PC not registered yet; policy served from the local copy");
            return null;
        }

        try
        {
            EtagResponse<PcPolicy> response = await _server.GetPoliciesAsync(pcId, force ? null : _etag, cancellationToken).ConfigureAwait(false);
            if (response.NotModified)
            {
                if (_last is { } unchanged)
                {
                    _lastSource = PolicySource.Server;
                    return unchanged;
                }

                response = await _server.GetPoliciesAsync(pcId, null, cancellationToken).ConfigureAwait(false);
            }

            PcPolicy policy = response.Require();
            IReadOnlyList<string> problems = Validate(policy);
            if (problems.Count > 0)
            {
                _logger.LogError("Server policy rejected: {Problems}", string.Join("; ", problems));
                return null;
            }

            if (_last is { } previous && policy.Version < previous.Version)
            {
                _logger.LogWarning("Server policy v{Version} is older than the known v{Known}; the server is authoritative", policy.Version, previous.Version);
            }

            _etag = response.ETag;
            _last = policy;
            _lastSource = PolicySource.Server;
            try
            {
                await WriteCacheAsync(policy, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Policy v{Version} could not be cached at {Path}", policy.Version, CachePath);
            }

            return policy;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Policy fetch from the server failed; using the local copy");
            return null;
        }
    }

    /// <summary>Reads and validates a file; rejects a version lower than <see cref="Last"/>. Caller holds the gate.</summary>
    private async Task<PcPolicy?> TryLoadFileAsync(string path, PolicySource source, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            PcPolicy? policy = JsonDefaults.Deserialize<PcPolicy>(bytes);
            IReadOnlyList<string> problems = Validate(policy);
            if (problems.Count > 0 || policy is null)
            {
                _logger.LogError("Policy file {Path} rejected: {Problems}", path, string.Join("; ", problems));
                return null;
            }

            if (_last is { } previous && policy.Version < previous.Version)
            {
                _logger.LogWarning("Policy file {Path} v{Version} ignored: v{Known} is already known", path, policy.Version, previous.Version);
                return null;
            }

            _last = policy;
            _lastSource = source;
            _logger.LogInformation("Policy v{Version} loaded from {Path}", policy.Version, path);
            return policy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(ex, "Policy file {Path} could not be read", path);
            return null;
        }
    }

    private async Task WriteCacheAsync(PcPolicy policy, CancellationToken cancellationToken)
    {
        string path = CachePath;
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = path + ".tmp";
        Volatile.Write(ref _selfWriteUntil, Environment.TickCount64 + SelfWriteGraceMs);
        await File.WriteAllBytesAsync(temp, JsonDefaults.SerializeToUtf8Bytes(policy), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
        Volatile.Write(ref _selfWriteUntil, Environment.TickCount64 + SelfWriteGraceMs);
        _logger.LogDebug("Policy v{Version} written to {Path}", policy.Version, path);
    }

    private void Bump(object sender, FileSystemEventArgs e) => _debounce?.Change(WatchDebounce, Timeout.InfiniteTimeSpan);

    private void OnDebounced()
    {
        if (Environment.TickCount64 < Volatile.Read(ref _selfWriteUntil))
        {
            return;
        }

        _ = ReloadFromFileAsync();
    }

    private async Task ReloadFromFileAsync()
    {
        try
        {
            PcPolicy? previous = _last;
            PcPolicy? policy;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                policy = await TryLoadFileAsync(CachePath, PolicySource.Cache, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            if (policy is null || (previous is not null && previous.Version == policy.Version && previous.UpdatedAt == policy.UpdatedAt))
            {
                return;
            }

            _logger.LogInformation("Policy file edited externally: v{Version}", policy.Version);
            Changed?.Invoke(this, policy);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Policy file reload failed");
        }
    }
}
