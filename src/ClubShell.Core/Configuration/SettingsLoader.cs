using System.Collections;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Http;
using ClubShell.Core.Realtime;
using ClubShell.Core.Security;
using ClubShell.Core.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace ClubShell.Core.Configuration;

/// <summary>Where <see cref="SettingsLoader"/> reads and writes.</summary>
public sealed class SettingsLoaderOptions
{
    /// <summary>Host-configuration section read by <see cref="FromConfiguration"/> (<c>ClubShell:ConfigPath</c>, <c>ClubShell:DefaultsPath</c>, <c>ClubShell:Watch</c>).</summary>
    public const string ConfigurationSection = "ClubShell";

    /// <summary>Shipped <c>agent.default.json</c>; missing file = code defaults only.</summary>
    public string DefaultsPath { get; set; } = ClubShellPaths.ShippedAgentDefaults;

    /// <summary>Admin-editable <c>agent.json</c> (created from the defaults when absent).</summary>
    public string ConfigPath { get; set; } = ClubShellPaths.AgentJson;

    /// <summary>Environment override prefix: <c>CLUBSHELL__&lt;section&gt;__&lt;key&gt;</c>.</summary>
    public string EnvironmentPrefix { get; set; } = "CLUBSHELL__";

    /// <summary>Create <see cref="ConfigPath"/> from the defaults when it does not exist.</summary>
    public bool CreateConfigIfMissing { get; set; } = true;

    /// <summary>Watch <see cref="ConfigPath"/> and reload on change.</summary>
    public bool Watch { get; set; } = true;

    /// <summary>Quiet period after the last file event before reloading.</summary>
    public TimeSpan ReloadDebounce { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Environment variables to use instead of the process environment (tests).</summary>
    public IReadOnlyDictionary<string, string?>? EnvironmentOverride { get; set; }

    /// <summary>Builds options from the host configuration (<c>--config &lt;path&gt;</c> is mapped to <c>ClubShell:ConfigPath</c> by the Agent).</summary>
    public static SettingsLoaderOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(ConfigurationSection);
        var options = new SettingsLoaderOptions();
        if (section["ConfigPath"] is { Length: > 0 } configPath)
        {
            options.ConfigPath = configPath;
        }

        if (section["DefaultsPath"] is { Length: > 0 } defaultsPath)
        {
            options.DefaultsPath = defaultsPath;
        }

        if (bool.TryParse(section["Watch"], out var watch))
        {
            options.Watch = watch;
        }

        return options;
    }
}

/// <summary>JSON helpers for <c>agent.json</c>: serializer options and the deep-merge used to layer sources.</summary>
public static class SettingsJson
{
    /// <summary>
    /// camelCase, case-insensitive, nulls written (<c>"pcId": null</c> is part of the schema), camelCase string enums,
    /// <c>HH:mm</c> times and ISO-8601 UTC timestamps (converters shared with <see cref="JsonDefaults"/>), indented output.
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateOptions();

    /// <summary>Serializes settings to a document node.</summary>
    public static JsonObject ToNode(AgentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.SerializeToNode(settings, SerializerOptions) as JsonObject ?? new JsonObject();
    }

    /// <summary>Deserializes a merged document into settings.</summary>
    public static AgentSettings FromNode(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Deserialize<AgentSettings>(SerializerOptions) ?? throw new JsonException("agent.json must be a JSON object");
    }

    /// <summary>
    /// Deep-merges <paramref name="overlay"/> into <paramref name="target"/>: objects merge recursively, arrays, scalars and
    /// <see langword="null"/> replace. Keys match case-insensitively; the target's spelling is kept.
    /// </summary>
    public static void Merge(JsonObject target, JsonObject overlay)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(overlay);
        foreach (var pair in overlay)
        {
            var existingKey = FindKey(target, pair.Key);
            if (pair.Value is JsonObject overlayObject && existingKey is not null && target[existingKey] is JsonObject targetObject)
            {
                Merge(targetObject, overlayObject);
                continue;
            }

            target[existingKey ?? pair.Key] = pair.Value?.DeepClone();
        }
    }

    /// <summary>Key of <paramref name="obj"/> equal to <paramref name="key"/> ignoring case, or <see langword="null"/>.</summary>
    public static string? FindKey(JsonObject obj, string key)
    {
        ArgumentNullException.ThrowIfNull(obj);
        foreach (var pair in obj)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Key;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds an overlay from environment variables <c>&lt;prefix&gt;&lt;section&gt;__&lt;key&gt;</c>. Values that parse as JSON
    /// (numbers, booleans, <c>null</c>, arrays, objects) are used as such; everything else is a string.
    /// </summary>
    public static JsonObject FromEnvironment(IEnumerable<KeyValuePair<string, string?>> variables, string prefix)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        var root = new JsonObject();
        foreach (var (name, value) in variables)
        {
            if (name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var segments = name[prefix.Length..].Split("__", StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            SetPath(root, segments, ParseEnvironmentValue(value));
        }

        return root;
    }

    /// <summary>Builds an overlay from server-provided configuration (<c>GET /agents/{pcId}/config</c>); server wins for keys present.</summary>
    public static JsonObject FromServerConfig(AgentServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var root = new JsonObject();
        if (!string.IsNullOrEmpty(config.PcName))
        {
            root["pcName"] = config.PcName;
        }

        if (!string.IsNullOrEmpty(config.Zone))
        {
            root["zone"] = config.Zone;
        }

        AddSection(root, "session", config.Session);
        AddSection(root, "offline", config.Offline);
        AddSection(root, "games", config.Games);
        AddSection(root, "storage", config.Storage);
        AddSection(root, "telemetry", config.Telemetry);
        AddSection(root, "remoteAdmin", config.RemoteAdmin);
        if (config.Updates is { } updates)
        {
            AddSection(root, "updates", JsonDefaults.ToElement(updates));
        }

        if (!string.IsNullOrEmpty(config.WsUrl))
        {
            root["server"] = new JsonObject { ["wsUrl"] = config.WsUrl };
        }

        return root;
    }

    private static void AddSection(JsonObject root, string key, JsonElement? element)
    {
        if (element is { ValueKind: JsonValueKind.Object } value)
        {
            root[key] = JsonObject.Create(value);
        }
    }

    private static void SetPath(JsonObject root, string[] segments, JsonNode? value)
    {
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var key = FindKey(current, segments[i]) ?? segments[i];
            if (current[key] is JsonObject child)
            {
                current = child;
            }
            else
            {
                var created = new JsonObject();
                current[key] = created;
                current = created;
            }
        }

        var leaf = FindKey(current, segments[^1]) ?? segments[^1];
        current[leaf] = value;
    }

    private static JsonNode? ParseEnvironmentValue(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 0 && (char.IsAsciiDigit(trimmed[0]) || trimmed[0] is '-' or '[' or '{' or '"' || trimmed is "true" or "false" or "null"))
        {
            try
            {
                return JsonNode.Parse(trimmed);
            }
            catch (JsonException)
            {
                // Not JSON (e.g. "-config" or "1st"): fall through to a string value.
            }
        }

        return JsonValue.Create(value);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.Strict,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new ClubTimeConverter());
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>
/// Loads the effective Agent configuration (ARCHITECTURE.md §6.1 step 2, §9): code defaults ⊕ shipped
/// <c>agent.default.json</c> ⊕ <c>C:\ProgramData\ClubShell\agent.json</c> ⊕ <c>CLUBSHELL__*</c> environment
/// variables ⊕ server-provided overrides. Validates every load and keeps the last good snapshot when a reload fails.
/// Watches <c>agent.json</c> (debounced) and exposes changes as <see cref="IOptionsMonitor{TOptions}"/>,
/// <see cref="Changed"/> and <see cref="ReloadToken"/>. Writes are atomic (temp file + move).
/// </summary>
public sealed class SettingsLoader : IOptionsMonitor<AgentSettings>, IOptions<AgentSettings>, IDisposable
{
    private readonly SettingsLoaderOptions _loaderOptions;
    private readonly ILogger<SettingsLoader> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly object _gate = new();
    private readonly Timer _debounce;
    private FileSystemWatcher? _watcher;
    private JsonObject? _baseLayer;
    private JsonObject? _serverOverrides;
    private AgentSettings? _current;
    private CancellationTokenSource _reloadCts = new();
    private bool _disposed;

    /// <summary>Creates a loader; nothing is read until <see cref="LoadAsync"/> / <see cref="Load"/>.</summary>
    public SettingsLoader(SettingsLoaderOptions loaderOptions, ILogger<SettingsLoader>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(loaderOptions);
        _loaderOptions = loaderOptions;
        _logger = logger ?? NullLogger<SettingsLoader>.Instance;
        _debounce = new Timer(_ => OnDebounceElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised after every successful (re)load with the new snapshot.</summary>
    public event EventHandler<AgentSettings>? Changed;

    /// <summary>Loader options.</summary>
    public SettingsLoaderOptions LoaderOptions => _loaderOptions;

    /// <summary>Last good snapshot. Throws until the first load.</summary>
    public AgentSettings Current => Volatile.Read(ref _current) ?? throw new InvalidOperationException("Settings not loaded; call LoadAsync first");

    /// <summary><see langword="true"/> once a snapshot is available.</summary>
    public bool IsLoaded => Volatile.Read(ref _current) is not null;

    /// <summary>Last server overrides applied through <see cref="ApplyServerOverrides"/>.</summary>
    public AgentServerConfig? ServerConfig { get; private set; }

    /// <summary>Token that fires on the next successful (re)load (<see cref="ChangeToken.OnChange(Func{IChangeToken}, Action)"/> compatible).</summary>
    public IChangeToken ReloadToken => new CancellationChangeToken(Volatile.Read(ref _reloadCts).Token);

    /// <inheritdoc />
    AgentSettings IOptionsMonitor<AgentSettings>.CurrentValue => Current;

    /// <inheritdoc />
    AgentSettings IOptions<AgentSettings>.Value => Current;

    /// <inheritdoc />
    AgentSettings IOptionsMonitor<AgentSettings>.Get(string? name) => Current;

    /// <inheritdoc />
    IDisposable? IOptionsMonitor<AgentSettings>.OnChange(Action<AgentSettings, string?> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return new ChangeSubscription(this, listener);
    }

    /// <summary>Synchronous bootstrap load (service start-up, tools); see <see cref="LoadAsync"/>.</summary>
    public AgentSettings Load() => LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Reads and merges every source, validates, publishes the snapshot and starts watching the file. On a reload
    /// with invalid content the previous snapshot is kept and <see cref="OptionsValidationException"/> is thrown.
    /// </summary>
    public async Task<AgentSettings> LoadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var layer = SettingsJson.ToNode(new AgentSettings());

            if (await ReadDocumentAsync(_loaderOptions.DefaultsPath, cancellationToken).ConfigureAwait(false) is { } defaults)
            {
                SettingsJson.Merge(layer, defaults);
            }
            else
            {
                _logger.LogDebug("Shipped defaults not found at {Path}; using code defaults", _loaderOptions.DefaultsPath);
            }

            await EnsureConfigFileAsync(layer, cancellationToken).ConfigureAwait(false);

            if (await ReadDocumentAsync(_loaderOptions.ConfigPath, cancellationToken).ConfigureAwait(false) is { } file)
            {
                SettingsJson.Merge(layer, file);
            }

            SettingsJson.Merge(layer, SettingsJson.FromEnvironment(EnumerateEnvironment(), _loaderOptions.EnvironmentPrefix));

            AgentSettings snapshot;
            lock (_gate)
            {
                _baseLayer = layer;
                snapshot = Rebuild();
            }

            Publish(snapshot);
            StartWatching();
            return snapshot;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>Re-reads every source (same as <see cref="LoadAsync"/>).</summary>
    public Task<AgentSettings> ReloadAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    /// <summary>Applies server overrides on top of the local sources and publishes the new snapshot; invalid overrides are rejected and the previous snapshot kept.</summary>
    public AgentSettings ApplyServerOverrides(AgentServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(_disposed, this);
        AgentSettings snapshot;
        lock (_gate)
        {
            if (_baseLayer is null)
            {
                throw new InvalidOperationException("Settings not loaded; call LoadAsync first");
            }

            var previous = _serverOverrides;
            _serverOverrides = SettingsJson.FromServerConfig(config);
            try
            {
                snapshot = Rebuild();
            }
            catch (OptionsValidationException)
            {
                _serverOverrides = previous;
                throw;
            }

            ServerConfig = config;
        }

        Publish(snapshot);
        return snapshot;
    }

    /// <summary>Writes <paramref name="settings"/> verbatim as <c>agent.json</c> (atomic) and reloads. Prefer <see cref="PatchAsync"/> for targeted edits so environment/server overrides are not baked into the file.</summary>
    public async Task SaveAsync(AgentSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAtomicAsync(SettingsJson.ToNode(settings), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _loadLock.Release();
        }

        await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads <c>agent.json</c> (or an empty object), lets <paramref name="mutate"/> edit it, writes it atomically and reloads.</summary>
    public async Task PatchAsync(Action<JsonObject> mutate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadDocumentAsync(_loaderOptions.ConfigPath, cancellationToken).ConfigureAwait(false) ?? new JsonObject();
            mutate(document);
            await WriteAtomicAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _loadLock.Release();
        }

        await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Persists the identity assigned by the server at registration (<c>pcId</c>, <c>pcName</c>, <c>zone</c>).</summary>
    public Task SetPcIdentityAsync(Guid pcId, string? pcName, string? zone, CancellationToken cancellationToken) =>
        PatchAsync(
            document =>
            {
                document[SettingsJson.FindKey(document, "pcId") ?? "pcId"] = pcId.ToString("D");
                if (!string.IsNullOrEmpty(pcName))
                {
                    document[SettingsJson.FindKey(document, "pcName") ?? "pcName"] = pcName;
                }

                if (!string.IsNullOrEmpty(zone))
                {
                    document[SettingsJson.FindKey(document, "zone") ?? "zone"] = zone;
                }
            },
            cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
        _debounce.Dispose();
        _loadLock.Dispose();
        _reloadCts.Dispose();
        GC.SuppressFinalize(this);
    }

    private AgentSettings Rebuild()
    {
        var effective = (JsonObject)_baseLayer!.DeepClone();
        if (_serverOverrides is not null)
        {
            SettingsJson.Merge(effective, _serverOverrides);
        }

        AgentSettings settings;
        try
        {
            settings = SettingsJson.FromNode(effective);
        }
        catch (JsonException ex)
        {
            throw new OptionsValidationException(Microsoft.Extensions.Options.Options.DefaultName, typeof(AgentSettings), new[] { $"agent.json: {ex.Message}" });
        }

        if (string.IsNullOrWhiteSpace(settings.PcName))
        {
            settings.PcName = Environment.MachineName;
        }

        var errors = AgentSettingsValidator.Check(settings);
        if (errors.Count > 0)
        {
            throw new OptionsValidationException(Microsoft.Extensions.Options.Options.DefaultName, typeof(AgentSettings), errors);
        }

        return settings;
    }

    private void Publish(AgentSettings snapshot)
    {
        Volatile.Write(ref _current, snapshot);
        var previous = Interlocked.Exchange(ref _reloadCts, new CancellationTokenSource());
        try
        {
            previous.Cancel();
        }
        finally
        {
            previous.Dispose();
        }

        try
        {
            Changed?.Invoke(this, snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A settings change listener threw");
        }
    }

    private async Task EnsureConfigFileAsync(JsonObject defaultsLayer, CancellationToken cancellationToken)
    {
        if (!_loaderOptions.CreateConfigIfMissing || File.Exists(_loaderOptions.ConfigPath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_loaderOptions.ConfigPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(_loaderOptions.DefaultsPath))
            {
                File.Copy(_loaderOptions.DefaultsPath, _loaderOptions.ConfigPath, overwrite: false);
            }
            else
            {
                await WriteAtomicAsync(defaultsLayer, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("Created {Path} from defaults", _loaderOptions.ConfigPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not create {Path}; continuing with defaults", _loaderOptions.ConfigPath);
        }
    }

    private static async Task<JsonObject?> ReadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            var node = await JsonNode.ParseAsync(stream, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow }, cancellationToken).ConfigureAwait(false);
            return node switch
            {
                null => new JsonObject(),
                JsonObject obj => obj,
                _ => throw new JsonException($"{path}: root must be a JSON object"),
            };
        }
    }

    private async Task WriteAtomicAsync(JsonObject document, CancellationToken cancellationToken)
    {
        var path = _loaderOptions.ConfigPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, SettingsJson.SerializerOptions);
        await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    private IEnumerable<KeyValuePair<string, string?>> EnumerateEnvironment()
    {
        if (_loaderOptions.EnvironmentOverride is { } overridden)
        {
            return overridden;
        }

        var result = new List<KeyValuePair<string, string?>>();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                result.Add(new KeyValuePair<string, string?>(key, entry.Value?.ToString()));
            }
        }

        return result;
    }

    private void StartWatching()
    {
        if (!_loaderOptions.Watch || _watcher is not null || _disposed)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_loaderOptions.ConfigPath);
        var fileName = Path.GetFileName(_loaderOptions.ConfigPath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName) || !Directory.Exists(directory))
        {
            return;
        }

        lock (_gate)
        {
            if (_watcher is not null)
            {
                return;
            }

            var watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            watcher.Changed += (_, _) => ScheduleReload();
            watcher.Created += (_, _) => ScheduleReload();
            watcher.Renamed += (_, _) => ScheduleReload();
            watcher.Error += (_, e) => _logger.LogWarning(e.GetException(), "agent.json watcher error");
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
    }

    private void ScheduleReload()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _ = _debounce.Change(_loaderOptions.ReloadDebounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently with a file event; nothing to reload.
        }
    }

    private void OnDebounceElapsed() => _ = ReloadSafelyAsync();

    private async Task ReloadSafelyAsync()
    {
        try
        {
            await LoadAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Reloaded {Path}", _loaderOptions.ConfigPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reload of {Path} failed; keeping the previous settings", _loaderOptions.ConfigPath);
        }
    }

    private sealed class ChangeSubscription : IDisposable
    {
        private readonly SettingsLoader _owner;
        private readonly EventHandler<AgentSettings> _handler;

        public ChangeSubscription(SettingsLoader owner, Action<AgentSettings, string?> listener)
        {
            _owner = owner;
            _handler = (_, settings) => listener(settings, Microsoft.Extensions.Options.Options.DefaultName);
            _owner.Changed += _handler;
        }

        public void Dispose() => _owner.Changed -= _handler;
    }
}

/// <summary>Dependency-injection entry point of the Core library.</summary>
public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the platform-neutral services: <see cref="IClock"/>, <see cref="SettingsLoader"/> (also as
    /// <see cref="IOptionsMonitor{TOptions}"/>/<see cref="IOptions{TOptions}"/> of <see cref="AgentSettings"/>),
    /// <see cref="ITokenStore"/>, <see cref="Hwid"/>, the resilient server <see cref="HttpClient"/>,
    /// <see cref="ServerClient"/>, <see cref="RealtimeClient"/>, <see cref="Reconnector"/> and the update pipeline.
    /// <paramref name="configuration"/> is the host configuration (<c>ClubShell:*</c> keys, see
    /// <see cref="SettingsLoaderOptions.FromConfiguration"/>); the Agent configuration itself comes from <c>agent.json</c>.
    /// Register a platform <see cref="IHardwareIdSource"/> before calling this to replace the basic one.
    /// </summary>
    public static IServiceCollection AddClubShellCore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions();
        services.TryAddSingleton<IClock>(SystemClock.Instance);
        services.TryAddSingleton(provider =>
        {
            var loader = new SettingsLoader(SettingsLoaderOptions.FromConfiguration(configuration), provider.GetRequiredService<ILogger<SettingsLoader>>());
            loader.Load();
            return loader;
        });
        services.TryAddSingleton<IOptionsMonitor<AgentSettings>>(provider => provider.GetRequiredService<SettingsLoader>());
        services.TryAddSingleton<IOptions<AgentSettings>>(provider => provider.GetRequiredService<SettingsLoader>());
        services.TryAddSingleton<IValidateOptions<AgentSettings>, AgentSettingsValidator>();

        services.TryAddSingleton<ITokenProtector>(_ => CreateTokenProtector());
        services.TryAddSingleton<ITokenStore>(provider => new TokenStore(
            provider.GetRequiredService<SettingsLoader>().Current.AgentTokensPath,
            provider.GetRequiredService<ITokenProtector>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<ILogger<TokenStore>>()));

        services.TryAddSingleton<IHardwareIdSource, BasicHardwareIdSource>();
        services.TryAddSingleton(provider => new Hwid(
            provider.GetRequiredService<IHardwareIdSource>(),
            Path.Combine(provider.GetRequiredService<SettingsLoader>().Current.SecureDir, ClubShellPaths.HwidFallbackFileName),
            provider.GetRequiredService<ILogger<Hwid>>()));

        services.ConfigureServerHttpClient(configuration);
        services.TryAddSingleton<ServerClient>();
        services.TryAddSingleton<IServerClient>(provider => provider.GetRequiredService<ServerClient>());

        services.TryAddSingleton<RealtimeClient>();
        services.TryAddSingleton(provider => new Reconnector(provider.GetRequiredService<IClock>(), provider.GetRequiredService<ILogger<Reconnector>>()));

        services.TryAddSingleton<UpdateChecker>();
        services.TryAddSingleton<UpdateDownloader>();
        services.TryAddSingleton<IProcessStarter, ProcessStarter>();
        services.TryAddSingleton<IUpdateApplier, UpdateApplier>();
        return services;
    }

    private static ITokenProtector CreateTokenProtector()
    {
        if (OperatingSystem.IsWindows())
        {
            return new DpapiTokenProtector();
        }

        return new NullTokenProtector();
    }
}
