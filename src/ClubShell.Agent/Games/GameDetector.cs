using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using ClubShell.Contracts.Games;
using ClubShell.Core.Abstractions;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Registry;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace ClubShell.Agent.Games;

/// <summary>
/// Detects local installs per launcher (Steam manifests, Epic <c>.item</c> files, Battle.net / EA / Ubisoft registry
/// keys, Riot product settings, plain executables). Results are cached for <see cref="CacheTtl"/> per game.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GameDetector
{
    private const int MaxParallelism = 8;
    private const string UninstallKey = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallKey64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly Dictionary<string, string[]> BattleNetProducts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wow"] = new[] { "World of Warcraft" },
        ["wow_classic"] = new[] { "World of Warcraft Classic" },
        ["pro"] = new[] { "Overwatch" },
        ["d3"] = new[] { "Diablo III" },
        ["d4"] = new[] { "Diablo IV" },
        ["fenris"] = new[] { "Diablo IV" },
        ["d2r"] = new[] { "Diablo II Resurrected" },
        ["osi"] = new[] { "Diablo II Resurrected" },
        ["hs"] = new[] { "Hearthstone" },
        ["wtcg"] = new[] { "Hearthstone" },
        ["s2"] = new[] { "StarCraft II" },
        ["s1"] = new[] { "StarCraft" },
        ["heroes"] = new[] { "Heroes of the Storm" },
        ["w3"] = new[] { "Warcraft III" },
        ["odin"] = new[] { "Call of Duty", "Call of Duty Modern Warfare" },
        ["auks"] = new[] { "Call of Duty", "Call of Duty Modern Warfare II" },
        ["lazr"] = new[] { "Call of Duty Modern Warfare" },
        ["zeus"] = new[] { "Call of Duty Black Ops Cold War" },
        ["fore"] = new[] { "Call of Duty Vanguard" },
    };

    private static readonly Dictionary<string, string> RiotProducts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["valorant"] = "VALORANT",
        ["league_of_legends"] = "League of Legends",
        ["bacon"] = "Legends of Runeterra",
        ["lor"] = "Legends of Runeterra",
    };

    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly IClock _clock;
    private readonly ILogger<GameDetector> _logger;
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, GameInstallStatus Status)> _cache = new();

    /// <summary>Creates a detector.</summary>
    public GameDetector(IOptionsMonitor<AgentSettings> settings, IClock clock, ILogger<GameDetector> logger)
    {
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>How long a detection result is reused.</summary>
    public static TimeSpan CacheTtl => TimeSpan.FromSeconds(60);

    /// <summary>Drops every cached result.</summary>
    public void Invalidate() => _cache.Clear();

    /// <summary>Resolved launcher executable (configured path, registry, or well-known location); <see langword="null"/> when absent.</summary>
    public string? ResolveLauncherExe(LauncherType launcher)
    {
        string? configured = _settings.CurrentValue.Games.Launchers.ForLauncher(launcher)?.ExePath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        string? ubisoftDir = RegistryString(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Ubisoft\Launcher", "InstallDir")?.Replace('/', '\\');
        string?[] candidates = launcher switch
        {
            LauncherType.Steam => new[] { Combine(SteamRoot(), "steam.exe") },
            LauncherType.Epic => new[] { Path.Combine(ProgramFilesX86(), "Epic Games", "Launcher", "Portal", "Binaries", "Win32", "EpicGamesLauncher.exe") },
            LauncherType.BattleNet => new[] { Path.Combine(ProgramFilesX86(), "Battle.net", "Battle.net.exe"), Path.Combine(ProgramFilesX86(), "Battle.net", "Battle.net Launcher.exe") },
            LauncherType.Riot => new[] { Combine(RiotClientDir(), "RiotClientServices.exe") },
            LauncherType.Ea => new[]
            {
                Combine(RegistryString(RegistryHive.LocalMachine, @"SOFTWARE\Electronic Arts\EA Desktop", "InstallLocation"), @"EA Desktop\EADesktop.exe"),
                Path.Combine(ProgramFiles(), "Electronic Arts", "EA Desktop", "EA Desktop", "EADesktop.exe"),
            },
            LauncherType.Ubisoft => new[]
            {
                Combine(ubisoftDir, "UbisoftConnect.exe"),
                Combine(ubisoftDir, "upc.exe"),
                Path.Combine(ProgramFilesX86(), "Ubisoft", "Ubisoft Game Launcher", "UbisoftConnect.exe"),
            },
            _ => Array.Empty<string?>(),
        };
        return candidates.FirstOrDefault(c => c is not null && File.Exists(c));
    }

    /// <summary>Detects the install state of <paramref name="game"/> (cached for <see cref="CacheTtl"/>).</summary>
    public async Task<GameInstallStatus> DetectAsync(Game game, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        DateTimeOffset now = _clock.UtcNow;
        if (_cache.TryGetValue(game.Id, out (DateTimeOffset At, GameInstallStatus Status) hit) && now - hit.At < CacheTtl)
        {
            return hit.Status;
        }

        GameInstallStatus status = await Task.Run(() => Detect(game, now), cancellationToken).ConfigureAwait(false);
        _cache[game.Id] = (now, status);
        return status;
    }

    /// <summary>Detects every game in parallel (bounded), keyed by game id.</summary>
    public async Task<IReadOnlyDictionary<Guid, GameInstallStatus>> DetectAllAsync(IEnumerable<Game> games, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(games);
        var result = new ConcurrentDictionary<Guid, GameInstallStatus>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(games, options, async (game, ct) =>
        {
            try
            {
                result[game.Id] = await DetectAsync(game, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Install detection failed for {GameId} ({Title})", game.Id, game.Title);
                result[game.Id] = new GameInstallStatus(game.Id, false, null, game.SizeGb, game.Version, null, false);
            }
        }).ConfigureAwait(false);
        return result;
    }

    private GameInstallStatus Detect(Game game, DateTimeOffset now)
    {
        bool launcherReady = game.Launcher == LauncherType.Exe || ResolveLauncherExe(game.Launcher) is not null;
        (string? installPath, double? sizeGb, string? version) = game.Launcher switch
        {
            LauncherType.Steam => DetectSteam(game),
            LauncherType.Epic => DetectEpic(game),
            LauncherType.BattleNet => DetectBattleNet(game),
            LauncherType.Riot => DetectRiot(game),
            LauncherType.Ea => DetectEa(game),
            LauncherType.Ubisoft => DetectUbisoft(game),
            _ => (null, null, null),
        };

        installPath ??= DetectByPath(game);
        bool installed = installPath is not null && Directory.Exists(installPath) && (game.ExePath is null || ResolveExe(game, installPath) is not null);
        return new GameInstallStatus(
            game.Id,
            installed,
            installed ? installPath : null,
            sizeGb ?? game.SizeGb,
            version ?? game.Version,
            installed ? now : null,
            launcherReady);
    }

    // ---- Steam ----------------------------------------------------------------------------------

    private (string?, double?, string?) DetectSteam(Game game)
    {
        if (string.IsNullOrWhiteSpace(game.LauncherAppId))
        {
            return (null, null, null);
        }

        foreach (string library in SteamLibraries())
        {
            string manifest = Path.Combine(library, "steamapps", $"appmanifest_{game.LauncherAppId}.acf");
            if (!File.Exists(manifest))
            {
                continue;
            }

            try
            {
                VdfNode root = VdfNode.Parse(File.ReadAllText(manifest));
                VdfNode? state = root["AppState"];
                string? dir = state?["installdir"]?.Value;
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }

                int flags = int.TryParse(state?["StateFlags"]?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int f) ? f : 0;
                if ((flags & 4) == 0)
                {
                    _logger.LogDebug("Steam app {AppId} present but not fully installed (StateFlags={Flags})", game.LauncherAppId, flags);
                    continue;
                }

                double? size = long.TryParse(state?["SizeOnDisk"]?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes) ? ToGb(bytes) : null;
                return (Path.Combine(library, "steamapps", "common", dir), size, state?["buildid"]?.Value);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                _logger.LogDebug(ex, "Cannot read Steam manifest {Manifest}", manifest);
            }
        }

        return (null, null, null);
    }

    private List<string> SteamLibraries()
    {
        var libraries = new List<string>();
        string? root = SteamRoot();
        if (root is null)
        {
            return libraries;
        }

        libraries.Add(root);
        string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
        {
            return libraries;
        }

        try
        {
            VdfNode parsed = VdfNode.Parse(File.ReadAllText(vdf));
            VdfNode? folders = parsed["libraryfolders"] ?? parsed["LibraryFolders"];
            if (folders is null)
            {
                return libraries;
            }

            foreach ((string key, VdfNode node) in folders.Children)
            {
                if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    continue;
                }

                string? path = node.Value ?? node["path"]?.Value;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    string normalized = Path.GetFullPath(path.Replace("\\\\", "\\", StringComparison.Ordinal));
                    if (!libraries.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                    {
                        libraries.Add(normalized);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            _logger.LogDebug(ex, "Cannot read {Vdf}", vdf);
        }

        return libraries;
    }

    private string? SteamRoot()
    {
        string? configured = _settings.CurrentValue.Games.Launchers.Steam?.ExePath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetDirectoryName(configured);
        }

        string? registry = RegistryString(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
            ?? RegistryString(RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        if (registry is not null && Directory.Exists(registry))
        {
            return registry;
        }

        string fallback = Path.Combine(ProgramFilesX86(), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }

    // ---- Epic -----------------------------------------------------------------------------------

    private (string?, double?, string?) DetectEpic(Game game)
    {
        string manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (string.IsNullOrWhiteSpace(game.LauncherAppId) || !Directory.Exists(manifests))
        {
            return (null, null, null);
        }

        foreach (string file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(file));
                JsonElement root = doc.RootElement;
                string appName = Str(root, "AppName");
                string composite = Str(root, "CatalogNamespace") + ":" + Str(root, "CatalogItemId");
                if (!appName.Equals(game.LauncherAppId, StringComparison.OrdinalIgnoreCase)
                    && !composite.Equals(game.LauncherAppId, StringComparison.OrdinalIgnoreCase)
                    && !Str(root, "CatalogItemId").Equals(game.LauncherAppId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (root.TryGetProperty("bIsIncompleteInstall", out JsonElement incomplete) && incomplete.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                string location = Str(root, "InstallLocation");
                double? size = root.TryGetProperty("InstallSize", out JsonElement s) && s.TryGetInt64(out long bytes) ? ToGb(bytes) : null;
                string? version = root.TryGetProperty("AppVersionString", out JsonElement v) ? v.GetString() : null;
                return (string.IsNullOrWhiteSpace(location) ? null : location, size, version);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogDebug(ex, "Cannot read Epic manifest {File}", file);
            }
        }

        return (null, null, null);
    }

    // ---- Battle.net -----------------------------------------------------------------------------

    private (string?, double?, string?) DetectBattleNet(Game game)
    {
        var names = new List<string> { game.Title };
        if (!string.IsNullOrWhiteSpace(game.LauncherAppId) && BattleNetProducts.TryGetValue(game.LauncherAppId, out string[]? known))
        {
            names.AddRange(known);
        }

        (string? path, string? version) = FindUninstallEntry(names, publisherHint: "Blizzard");
        return (path, null, version);
    }

    // ---- Riot -----------------------------------------------------------------------------------

    private (string?, double?, string?) DetectRiot(Game game)
    {
        string product = game.LauncherAppId ?? string.Empty;
        string metadata = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games", "Metadata");
        if (!string.IsNullOrEmpty(product) && Directory.Exists(metadata))
        {
            foreach (string dir in Directory.EnumerateDirectories(metadata, product + ".*"))
            {
                foreach (string yaml in Directory.EnumerateFiles(dir, "*.product_settings.yaml"))
                {
                    try
                    {
                        foreach (string line in File.ReadLines(yaml))
                        {
                            string trimmed = line.Trim();
                            if (trimmed.StartsWith("product_install_full_path:", StringComparison.OrdinalIgnoreCase))
                            {
                                string value = trimmed["product_install_full_path:".Length..].Trim().Trim('"');
                                if (Directory.Exists(value))
                                {
                                    return (value, null, null);
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogDebug(ex, "Cannot read Riot product settings {Yaml}", yaml);
                    }
                }
            }
        }

        var names = new List<string> { game.Title };
        if (RiotProducts.TryGetValue(product, out string? display))
        {
            names.Add(display);
        }

        (string? path, string? version) = FindUninstallEntry(names, publisherHint: "Riot");
        return (path, null, version);
    }

    // ---- EA -------------------------------------------------------------------------------------

    private (string?, double?, string?) DetectEa(Game game)
    {
        foreach (string root in new[] { @"SOFTWARE\WOW6432Node\EA Games", @"SOFTWARE\EA Games", @"SOFTWARE\WOW6432Node\Electronic Arts", @"SOFTWARE\Electronic Arts" })
        {
            using RegistryKey? key = RegistryHelper.Open(RegistryHive.LocalMachine, root, writable: false);
            if (key is null)
            {
                continue;
            }

            foreach (string sub in key.GetSubKeyNames())
            {
                if (!TitleMatches(sub, game.Title) && !sub.Equals(game.LauncherAppId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? dir = RegistryString(RegistryHive.LocalMachine, root + "\\" + sub, "Install Dir");
                if (dir is not null && Directory.Exists(dir))
                {
                    return (dir, null, RegistryString(RegistryHive.LocalMachine, root + "\\" + sub, "DisplayVersion"));
                }
            }
        }

        (string? path, string? version) = FindUninstallEntry(new[] { game.Title }, publisherHint: "Electronic Arts");
        return (path, null, version);
    }

    // ---- Ubisoft --------------------------------------------------------------------------------

    private (string?, double?, string?) DetectUbisoft(Game game)
    {
        if (!string.IsNullOrWhiteSpace(game.LauncherAppId))
        {
            string key = @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\" + game.LauncherAppId;
            string? dir = RegistryString(RegistryHive.LocalMachine, key, "InstallDir");
            if (dir is not null)
            {
                string normalized = dir.Replace('/', '\\').TrimEnd('\\');
                if (Directory.Exists(normalized))
                {
                    return (normalized, null, null);
                }
            }
        }

        (string? path, string? version) = FindUninstallEntry(new[] { game.Title }, publisherHint: "Ubisoft");
        return (path, null, version);
    }

    // ---- Generic --------------------------------------------------------------------------------

    /// <summary>Catalogue <c>installPath</c>, then <c>&lt;libraryRoot&gt;\&lt;title&gt;</c>, then an absolute <c>exePath</c>.</summary>
    private string? DetectByPath(Game game)
    {
        if (!string.IsNullOrWhiteSpace(game.InstallPath) && Directory.Exists(game.InstallPath))
        {
            return game.InstallPath;
        }

        foreach (string root in _settings.CurrentValue.Games.LibraryRoots)
        {
            string candidate = Path.Combine(root, SafeDirName(game.Title));
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        if (!string.IsNullOrWhiteSpace(game.ExePath) && Path.IsPathRooted(game.ExePath) && File.Exists(game.ExePath))
        {
            return Path.GetDirectoryName(game.ExePath);
        }

        return null;
    }

    /// <summary>Absolute executable path for <paramref name="game"/> inside <paramref name="installPath"/>, or <see langword="null"/> when missing.</summary>
    public static string? ResolveExe(Game game, string? installPath)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (string.IsNullOrWhiteSpace(game.ExePath))
        {
            return null;
        }

        if (Path.IsPathRooted(game.ExePath))
        {
            return File.Exists(game.ExePath) ? game.ExePath : null;
        }

        if (installPath is null)
        {
            return null;
        }

        string full = Path.GetFullPath(Path.Combine(installPath, game.ExePath));
        return File.Exists(full) ? full : null;
    }

    private (string? Path, string? Version) FindUninstallEntry(IReadOnlyList<string> names, string publisherHint)
    {
        foreach (string root in new[] { UninstallKey, UninstallKey64 })
        {
            using RegistryKey? key = RegistryHelper.Open(RegistryHive.LocalMachine, root, writable: false);
            if (key is null)
            {
                continue;
            }

            foreach (string sub in key.GetSubKeyNames())
            {
                string subKey = root + "\\" + sub;
                string? display = RegistryString(RegistryHive.LocalMachine, subKey, "DisplayName");
                if (display is null || !names.Any(n => TitleMatches(display, n)))
                {
                    continue;
                }

                string? publisher = RegistryString(RegistryHive.LocalMachine, subKey, "Publisher");
                if (publisher is not null && !publisher.Contains(publisherHint, StringComparison.OrdinalIgnoreCase) && names.Count > 1)
                {
                    continue;
                }

                string? location = RegistryString(RegistryHive.LocalMachine, subKey, "InstallLocation");
                if (!string.IsNullOrWhiteSpace(location) && Directory.Exists(location))
                {
                    return (location.TrimEnd('\\'), RegistryString(RegistryHive.LocalMachine, subKey, "DisplayVersion"));
                }
            }
        }

        foreach (string root in _settings.CurrentValue.Games.LibraryRoots)
        {
            foreach (string name in names)
            {
                string candidate = Path.Combine(root, SafeDirName(name));
                if (Directory.Exists(candidate))
                {
                    return (candidate, null);
                }
            }
        }

        return (null, null);
    }

    private static bool TitleMatches(string candidate, string title)
    {
        string a = Normalize(candidate);
        string b = Normalize(title);
        return a.Length > 0 && b.Length > 0 && (a == b || a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal));
    }

    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    private static string SafeDirName(string title)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(title.Length);
        foreach (char c in title)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return sb.ToString().Trim();
    }

    private static string? RegistryString(RegistryHive hive, string key, string name)
    {
        try
        {
            return RegistryHelper.Get<string>(hive, key, name) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private string? RiotClientDir()
    {
        string installs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games", "RiotClientInstalls.json");
        if (File.Exists(installs))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(installs));
                foreach (string prop in new[] { "rc_live", "rc_default" })
                {
                    if (doc.RootElement.TryGetProperty(prop, out JsonElement e) && e.ValueKind == JsonValueKind.String && e.GetString() is { } exe && File.Exists(exe))
                    {
                        return Path.GetDirectoryName(exe);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogDebug(ex, "Cannot read {File}", installs);
            }
        }

        string fallback = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "Riot Games", "Riot Client");
        return Directory.Exists(fallback) ? fallback : null;
    }

    private static string? Combine(string? dir, string file) => dir is null ? null : Path.Combine(dir, file);

    private static string ProgramFilesX86() => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    private static string ProgramFiles() => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    private static string Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : string.Empty;

    private static double ToGb(long bytes) => Math.Round(bytes / 1_073_741_824d, 1);
}

/// <summary>
/// Minimal Valve KeyValues (VDF) document: quoted <c>"key" "value"</c> pairs and <c>"key" { ... }</c> blocks,
/// <c>//</c> comments, <c>\"</c> <c>\\</c> <c>\n</c> <c>\t</c> escapes. Enough for <c>libraryfolders.vdf</c>,
/// <c>appmanifest_*.acf</c>, <c>loginusers.vdf</c> and <c>config.vdf</c>.
/// </summary>
internal sealed class VdfNode
{
    /// <summary>Leaf value; <see langword="null"/> for a block.</summary>
    public string? Value { get; set; }

    /// <summary>Child nodes in document order (case-insensitive keys).</summary>
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Child by key, or <see langword="null"/>.</summary>
    public VdfNode? this[string key] => Children.TryGetValue(key, out VdfNode? node) ? node : null;

    /// <summary>Gets or creates a child block.</summary>
    public VdfNode Block(string key)
    {
        if (!Children.TryGetValue(key, out VdfNode? node) || node.Value is not null)
        {
            node = new VdfNode();
            Children[key] = node;
        }

        return node;
    }

    /// <summary>Sets a leaf value.</summary>
    public void Set(string key, string value) => Children[key] = new VdfNode { Value = value };

    /// <summary>Parses a document; throws <see cref="FormatException"/> on malformed input.</summary>
    public static VdfNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = Tokenize(text);
        int index = 0;
        var root = new VdfNode();
        ParseBlock(tokens, ref index, root, top: true);
        return root;
    }

    /// <summary>Serializes back to Valve's tab-indented format.</summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        Write(sb, this, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, VdfNode node, int depth)
    {
        string indent = new('\t', depth);
        foreach ((string key, VdfNode child) in node.Children)
        {
            if (child.Value is not null)
            {
                sb.Append(indent).Append('"').Append(Escape(key)).Append("\"\t\t\"").Append(Escape(child.Value)).Append("\"\n");
            }
            else
            {
                sb.Append(indent).Append('"').Append(Escape(key)).Append("\"\n").Append(indent).Append("{\n");
                Write(sb, child, depth + 1);
                sb.Append(indent).Append("}\n");
            }
        }
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void ParseBlock(List<(char Kind, string Text)> tokens, ref int index, VdfNode target, bool top)
    {
        while (index < tokens.Count)
        {
            (char kind, string text) = tokens[index];
            if (kind == '}')
            {
                if (top)
                {
                    throw new FormatException("Unexpected '}' at top level");
                }

                index++;
                return;
            }

            if (kind != 's')
            {
                throw new FormatException($"Expected key, got '{text}'");
            }

            index++;
            if (index >= tokens.Count)
            {
                throw new FormatException($"Key '{text}' has no value");
            }

            (char nextKind, string nextText) = tokens[index];
            if (nextKind == '{')
            {
                index++;
                var block = new VdfNode();
                ParseBlock(tokens, ref index, block, top: false);
                target.Children[text] = block;
            }
            else if (nextKind == 's')
            {
                index++;
                target.Children[text] = new VdfNode { Value = nextText };
            }
            else
            {
                throw new FormatException($"Unexpected '{nextText}' after key '{text}'");
            }
        }

        if (!top)
        {
            throw new FormatException("Unterminated block");
        }
    }

    private static List<(char Kind, string Text)> Tokenize(string text)
    {
        var tokens = new List<(char, string)>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
            }
            else if (c is '{' or '}')
            {
                tokens.Add((c, c.ToString()));
                i++;
            }
            else if (c == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                        sb.Append(text[i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => text[i] });
                    }
                    else
                    {
                        sb.Append(text[i]);
                    }

                    i++;
                }

                if (i >= text.Length)
                {
                    throw new FormatException("Unterminated string");
                }

                i++;
                tokens.Add(('s', sb.ToString()));
            }
            else
            {
                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"'))
                {
                    i++;
                }

                tokens.Add(('s', text[start..i]));
            }
        }

        return tokens;
    }
}
