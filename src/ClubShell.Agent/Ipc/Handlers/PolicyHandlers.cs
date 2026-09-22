using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClubShell.Agent.Policy;
using ClubShell.Contracts.Ipc;
using ClubShell.Contracts.Users;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Options;
using PcPolicy = ClubShell.Contracts.Pcs.Policy;

namespace ClubShell.Agent.Ipc.Handlers;

/// <summary>
/// The UI-facing subset of <c>shell.json</c> (IPC_PROTOCOL.md §6.21), read and written by the Agent on behalf of the
/// Shell. Keys map onto the <c>shell.json</c> schema (<c>locale</c>, <c>theme</c>, <c>sound.*</c>, <c>idle.timeoutSec</c>,
/// <c>ui.showMetricsOverlay</c>, <c>kiosk.allowVirtualKeyboard</c>, <c>kiosk.adminPinHash</c>, <c>features.*</c>) so the
/// Shell reads the same file at start. Writes are atomic (temp file + rename); the parsed document is cached and
/// re-read when the file changes on disk.
/// </summary>
public sealed class ShellSettingsStore
{
    /// <summary>Theme id assumed when <c>shell.json</c> has none.</summary>
    public const string DefaultTheme = "default";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly ILogger<ShellSettingsStore> _logger;
    private readonly object _gate = new();
    private JsonObject? _document;
    private DateTime _documentWriteTimeUtc;

    /// <summary>Creates the store.</summary>
    public ShellSettingsStore(IOptionsMonitor<AgentSettings> settings, ILogger<ShellSettingsStore> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
    }

    /// <summary><c>&lt;paths.programData&gt;\shell.json</c>.</summary>
    public string FilePath => _settings.CurrentValue.ResolvePath(ClubShellPaths.ShellJsonFileName);

    /// <summary>Defaults shipped next to the Agent (<c>config\shell.default.json</c>), used when <see cref="FilePath"/> is absent.</summary>
    public string ShippedDefaultsPath => Path.Combine(AppContext.BaseDirectory, "config", "shell.default.json");

    /// <summary>Themes installed under <c>paths.themes</c> (file names without extension) plus the configured theme.</summary>
    public IReadOnlyList<string> AvailableThemes
    {
        get
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { Get().Theme };
            try
            {
                string dir = _settings.CurrentValue.ThemesDir;
                if (Directory.Exists(dir))
                {
                    foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
                    {
                        names.Add(Path.GetFileNameWithoutExtension(file));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Themes directory could not be listed");
            }

            return names.ToList();
        }
    }

    /// <summary><c>kiosk.adminPinHash</c> (PBKDF2 <c>pbkdf2$…</c> or SHA-256 hex), or <see langword="null"/> when no admin PIN is set.</summary>
    public string? AdminPinHash
    {
        get
        {
            lock (_gate)
            {
                return GetString(Load()["kiosk"]?["adminPinHash"]);
            }
        }
    }

    /// <summary>Feature toggles (<c>features.*</c>, default on).</summary>
    public ShellFeatures Features => Get().Features;

    /// <summary>Current settings.</summary>
    public ShellSettings Get()
    {
        lock (_gate)
        {
            return Map(Load());
        }
    }

    /// <summary>Merges <paramref name="patch"/> into <c>shell.json</c> (callers validate first) and returns the result.</summary>
    public ShellSettings Apply(SettingsSetRequest patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return Mutate(root =>
        {
            if (patch.Locale is { } locale)
            {
                root["locale"] = WireName(locale);
            }

            if (patch.Theme is { } theme)
            {
                root["theme"] = theme;
            }

            if (patch.Volume is { } volume)
            {
                Section(root, "sound")["volume"] = volume;
            }

            if (patch.Muted is { } muted)
            {
                Section(root, "sound")["muted"] = muted;
            }

            if (patch.IdleTimeoutSec is { } idle)
            {
                Section(root, "idle")["timeoutSec"] = idle;
            }

            if (patch.ShowMetricsOverlay is { } overlay)
            {
                Section(root, "ui")["showMetricsOverlay"] = overlay;
            }

            if (patch.AllowVirtualKeyboard is { } keyboard)
            {
                Section(root, "kiosk")["allowVirtualKeyboard"] = keyboard;
            }

            if (patch.UiSounds is { } sounds)
            {
                Section(root, "sound")["uiSounds"] = sounds;
            }
        });
    }

    /// <summary>
    /// Merges the server's <c>shell</c> block from <c>GET /config</c> into <c>shell.json</c>. Only the keys the server
    /// sent are touched, so a player's own volume and locale survive. A feature the server turns off reaches the UI as
    /// <c>features.&lt;name&gt; = false</c>, which hides its tab and stops the screen behind it calling an endpoint the
    /// server does not implement. The running Shell re-reads the file at its next start; there is no live push.
    /// </summary>
    public ShellSettings ApplyServerOverride(ClubShell.Contracts.Pcs.ShellConfigOverride shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        return Mutate(root =>
        {
            if (shell.Locale is { } locale)
            {
                root["locale"] = WireName(locale);
            }

            if (!string.IsNullOrWhiteSpace(shell.Theme))
            {
                root["theme"] = shell.Theme;
            }

            MergeSection(root, "features", shell.Features);
            MergeSection(root, "ads", shell.Ads);
            MergeSection(root, "idle", shell.Idle);
        });
    }

    /// <summary>Copies the top-level keys of <paramref name="element"/> into <paramref name="name"/>; anything that is not an object is ignored.</summary>
    private static void MergeSection(JsonObject root, string name, JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value)
        {
            return;
        }

        JsonObject section = Section(root, name);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            section[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
    }

    /// <summary>Persists the volume (<c>sound.volume</c> / <c>sound.muted</c>).</summary>
    public VolumeState SetVolume(int level, bool? muted)
    {
        ShellSettings result = Mutate(root =>
        {
            JsonObject sound = Section(root, "sound");
            sound["volume"] = Math.Clamp(level, 0, 100);
            if (muted is { } m)
            {
                sound["muted"] = m;
            }
        });
        return new VolumeState(result.Volume, result.Muted);
    }

    /// <summary>Persists the UI locale.</summary>
    public Locale SetLocale(Locale locale) => Mutate(root => root["locale"] = WireName(locale)).Locale;

    /// <summary>Wire form of a locale (<c>en</c>, <c>ru</c>, <c>uz</c>).</summary>
    public static string WireName(Locale locale) => locale.ToString().ToLowerInvariant();

    private static JsonObject Section(JsonObject root, string name)
    {
        if (root[name] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        root[name] = created;
        return created;
    }

    private static string? GetString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static int GetInt(JsonNode? node, int fallback)
    {
        if (node is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue(out int i))
        {
            return i;
        }

        return value.TryGetValue(out double d) && d is >= int.MinValue and <= int.MaxValue ? (int)d : fallback;
    }

    private static bool GetBool(JsonNode? node, bool fallback) =>
        node is JsonValue value && value.TryGetValue(out bool b) ? b : fallback;

    private static ShellSettings Map(JsonObject root)
    {
        JsonNode? sound = root["sound"];
        JsonNode? features = root["features"];
        string localeText = GetString(root["locale"]) ?? "ru";
        Locale locale = Enum.TryParse(localeText, ignoreCase: true, out Locale parsed) ? parsed : Locale.Ru;
        return new ShellSettings(
            locale,
            GetString(root["theme"]) ?? DefaultTheme,
            Array.Empty<string>(),
            Math.Clamp(GetInt(sound?["volume"], GetInt(sound?["defaultVolume"], 60)), 0, 100),
            GetBool(sound?["muted"], false),
            Math.Max(0, GetInt(root["idle"]?["timeoutSec"], 300)),
            GetBool(root["ui"]?["showMetricsOverlay"], false),
            GetBool(root["kiosk"]?["allowVirtualKeyboard"], true),
            GetBool(sound?["uiSounds"], true),
            new ShellFeatures(
                GetBool(features?["shop"], true),
                GetBool(features?["chat"], true),
                GetBool(features?["booking"], true),
                GetBool(features?["tournaments"], true),
                GetBool(features?["profile"], true),
                GetBool(features?["topup"], true),
                GetBool(features?["apps"], true),
                GetBool(features?["callAdmin"], true)));
    }

    private ShellSettings Mutate(Action<JsonObject> mutate)
    {
        lock (_gate)
        {
            JsonObject root = Load();
            mutate(root);
            Save(root);
            return Map(root);
        }
    }

    private JsonObject Load()
    {
        string path = FilePath;
        DateTime writeTime = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (_document is not null && writeTime == _documentWriteTimeUtc)
        {
            return _document;
        }

        JsonObject? loaded = TryRead(path) ?? TryRead(ShippedDefaultsPath);
        _document = loaded ?? new JsonObject { ["version"] = 1 };
        _documentWriteTimeUtc = writeTime;
        return _document;
    }

    private JsonObject? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "shell settings at {Path} could not be read", path);
            return null;
        }
    }

    private void Save(JsonObject root)
    {
        string path = FilePath;
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        File.WriteAllText(temp, root.ToJsonString(WriteOptions));
        File.Move(temp, path, overwrite: true);
        _documentWriteTimeUtc = File.GetLastWriteTimeUtc(path);
        _document = root;
        _logger.LogDebug("shell.json saved");
    }
}

/// <summary><c>policy.*</c> and <c>settings.*</c> requests (IPC_PROTOCOL.md §7.11, §7.13).</summary>
public sealed class PolicyHandlers : IIpcHandlerGroup
{
    /// <summary>Longest accepted <c>idleTimeoutSec</c> (24 h); 0 disables idle handling.</summary>
    public const int MaxIdleTimeoutSec = 86_400;

    private readonly PolicyStore _store;
    private readonly IPolicyEnforcer _enforcer;
    private readonly ShellSettingsStore _shellSettings;
    private readonly ILogger<PolicyHandlers> _logger;

    /// <summary>Creates the group.</summary>
    public PolicyHandlers(PolicyStore store, IPolicyEnforcer enforcer, ShellSettingsStore shellSettings, ILogger<PolicyHandlers> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(enforcer);
        ArgumentNullException.ThrowIfNull(shellSettings);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _enforcer = enforcer;
        _shellSettings = shellSettings;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Register(MessageDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        dispatcher.RegisterNoPayload<PcPolicy>(IpcMessages.Policy.Get, GetPolicyAsync);
        dispatcher.RegisterOptional<PolicyReloadRequest, PolicyReloadResponse>(IpcMessages.Policy.Reload, ReloadAsync);
        dispatcher.RegisterNoPayload<ShellSettings>(IpcMessages.Settings.Get, (_, _) => Task.FromResult(Effective(_shellSettings.Get())));
        dispatcher.Register<SettingsSetRequest, ShellSettings>(IpcMessages.Settings.Set, SetAsync);
    }

    /// <summary>Effective settings: the stored ones with policy locks and the theme list applied.</summary>
    public ShellSettings Effective(ShellSettings stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        bool keyboardAllowed = _enforcer.Current?.Kiosk.AllowVirtualKeyboard ?? true;
        return stored with
        {
            AvailableThemes = _shellSettings.AvailableThemes,
            AllowVirtualKeyboard = stored.AllowVirtualKeyboard && keyboardAllowed,
        };
    }

    private async Task<PcPolicy> GetPolicyAsync(IpcContext context, CancellationToken cancellationToken)
    {
        if (_enforcer.Current is { } current)
        {
            return current;
        }

        if (_store.Last is { } last)
        {
            return last;
        }

        try
        {
            return (await _store.LoadAsync(force: false, cancellationToken).ConfigureAwait(false)).Policy;
        }
        catch (InvalidOperationException ex)
        {
            throw IpcError.ServerUnavailable(ex.Message).ToException();
        }
    }

    private async Task<PolicyReloadResponse> ReloadAsync(IpcContext context, PolicyReloadRequest? request, CancellationToken cancellationToken)
    {
        bool force = request?.Force ?? false;
        PcPolicy policy;
        PolicySource source;
        try
        {
            (policy, source) = await _store.LoadAsync(force, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "policy.reload (force={Force}) found no policy", force);
            throw IpcError.ServerUnavailable("No policy available from the server or the cache").ToException();
        }

        PolicyApplyResult result = await _enforcer.ApplyAsync(policy, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("policy.reload: v{Version} from {Source}, applied={Applied}, changed={Changed}", policy.Version, source, result.Applied, string.Join(",", result.Changed));
        return new PolicyReloadResponse(policy, source, result.Applied, result.Changed);
    }

    private Task<ShellSettings> SetAsync(IpcContext context, SettingsSetRequest request, CancellationToken cancellationToken)
    {
        if (request.IsEmpty)
        {
            throw IpcError.Validation("payload", "empty", "At least one setting must be given").ToException();
        }

        if (request.Volume is { } volume && volume is < 0 or > 100)
        {
            throw IpcError.Validation("volume", "range").ToException();
        }

        if (request.IdleTimeoutSec is { } idle && idle is < 0 or > MaxIdleTimeoutSec)
        {
            throw IpcError.Validation("idleTimeoutSec", "range").ToException();
        }

        if (request.Theme is { } theme)
        {
            if (string.IsNullOrWhiteSpace(theme) || theme.Length > 64 || theme.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw IpcError.Validation("theme", "format").ToException();
            }

            if (!_shellSettings.AvailableThemes.Contains(theme, StringComparer.OrdinalIgnoreCase))
            {
                throw IpcError.Validation("theme", "unknown", $"Theme '{theme}' is not installed").ToException();
            }
        }

        if (request.AllowVirtualKeyboard == true && _enforcer.Current is { Kiosk.AllowVirtualKeyboard: false })
        {
            throw IpcError.PolicyDenied("kiosk.allowVirtualKeyboard", "allowVirtualKeyboard").ToException();
        }

        ShellSettings stored = _shellSettings.Apply(request);
        _logger.LogInformation("settings.set applied: {Fields}", DescribeFields(request));
        return Task.FromResult(Effective(stored));
    }

    private static string DescribeFields(SettingsSetRequest request)
    {
        var fields = new List<string>(8);
        if (request.Locale is not null)
        {
            fields.Add("locale");
        }

        if (request.Theme is not null)
        {
            fields.Add("theme");
        }

        if (request.Volume is not null)
        {
            fields.Add("volume");
        }

        if (request.Muted is not null)
        {
            fields.Add("muted");
        }

        if (request.IdleTimeoutSec is not null)
        {
            fields.Add("idleTimeoutSec");
        }

        if (request.ShowMetricsOverlay is not null)
        {
            fields.Add("showMetricsOverlay");
        }

        if (request.AllowVirtualKeyboard is not null)
        {
            fields.Add("allowVirtualKeyboard");
        }

        if (request.UiSounds is not null)
        {
            fields.Add("uiSounds");
        }

        return string.Join(",", fields);
    }
}
