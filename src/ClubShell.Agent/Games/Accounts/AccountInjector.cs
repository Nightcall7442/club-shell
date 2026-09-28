using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClubShell.Agent.Policy;
using ClubShell.Contracts.Errors;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Native;
using ClubShell.Windows.Processes;
using ClubShell.Windows.Registry;
using ClubShell.Windows.Sessions;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ClubShell.Agent.Games.Accounts;

/// <summary>Profile folders of the kiosk user (resolved by the users subsystem; the Agent runs as SYSTEM).</summary>
public interface IKioskProfilePaths
{
    /// <summary><c>C:\Users\&lt;kiosk&gt;\AppData\Local</c>.</summary>
    string LocalAppData { get; }

    /// <summary><c>C:\Users\&lt;kiosk&gt;\AppData\Roaming</c>.</summary>
    string RoamingAppData { get; }

    /// <summary><c>C:\Users\&lt;kiosk&gt;</c>.</summary>
    string UserProfile { get; }
}

/// <summary>Outcome of a credential injection.</summary>
/// <param name="ExtraArgs">Arguments placed on the launcher command line before the launch command (see <see cref="AccountInjector.CredentialArgs"/>), or <see langword="null"/>.</param>
/// <param name="EnvVars">Extra environment variables for the launcher process (never credentials).</param>
/// <param name="RestoreAction">Undo action (restores backed-up config files); <see langword="null"/> when nothing was changed.</param>
/// <param name="Error">Why the launcher was not prepared cleanly (sent as <see cref="LaunchReport.InjectionError"/>), or <see langword="null"/>.</param>
public sealed record InjectionResult(string? ExtraArgs, IReadOnlyDictionary<string, string> EnvVars, Func<CancellationToken, Task>? RestoreAction, string? Error = null)
{
    /// <summary>Nothing injected, nothing to restore.</summary>
    public static InjectionResult None { get; } = new(null, ImmutableDictionary<string, string>.Empty, null);

    /// <summary>Runs <see cref="RestoreAction"/> when present.</summary>
    public Task RestoreAsync(CancellationToken cancellationToken) => RestoreAction?.Invoke(cancellationToken) ?? Task.CompletedTask;
}

/// <summary>Per-launcher credential injection (config patching / launch arguments).</summary>
public interface ILauncherCredentialStrategy
{
    /// <summary>Launcher handled.</summary>
    LauncherType Launcher { get; }

    /// <summary>Prepares the launcher so that <paramref name="lease"/>'s account is used for <paramref name="game"/>.</summary>
    Task<InjectionResult> InjectAsync(Game game, ActiveLease lease, string? launcherExe, CancellationToken cancellationToken);
}

/// <summary>
/// Injects pooled-account credentials before a launch and cleans up after exit: kills the launcher client, backs up
/// and patches its config in the kiosk profile, restores the backups and clears Windows Credential Manager entries
/// the launcher may have stored for the kiosk user.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AccountInjector
{
    /// <summary><c>extra.authType</c> of an Epic lease whose <see cref="AccountLease.Secret"/> is a one-time exchange code, not a password.</summary>
    public const string ExchangeCodeAuthType = "exchangeCode";

    /// <summary>Steam's per-user key; <see cref="SteamAutoLogin"/> writes it in the kiosk user's hive.</summary>
    public const string SteamUserKey = @"Software\Valve\Steam";

    private const uint DuplicateSameAccess = 0x2;
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(5);

    private readonly IKioskProfilePaths _profile;
    private readonly IKioskSessionLocator _kiosk;
    private readonly GameDetector _detector;
    private readonly ProcessKiller _killer;
    private readonly ILogger<AccountInjector> _logger;
    private readonly Dictionary<LauncherType, ILauncherCredentialStrategy> _strategies;

    /// <summary>Creates the injector with the built-in strategies.</summary>
    public AccountInjector(
        IKioskProfilePaths profile,
        IKioskSessionLocator kiosk,
        GameDetector detector,
        ProcessKiller killer,
        IKioskCredentials kioskAccount,
        IOptionsMonitor<AgentSettings> settings,
        ILogger<AccountInjector> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _profile = profile;
        _kiosk = kiosk;
        _detector = detector;
        _killer = killer;
        _logger = logger;
        var files = new ProfileFiles(profile, logger);
        Func<bool> allowPassword = () => settings.CurrentValue.Games.AccountPool.AllowPasswordOnCommandLine;
        _strategies = new ILauncherCredentialStrategy[]
        {
            new SteamStrategy(files, kioskAccount, allowPassword, logger),
            new EpicStrategy(files, allowPassword, logger),
            new BattleNetStrategy(files, logger),
            new RiotStrategy(files),
            new EaStrategy(files, logger),
            new UbisoftStrategy(files, logger),
            new ExeStrategy(),
        }.ToDictionary(s => s.Launcher);
    }

    /// <summary>Launcher client image names killed before injection and after exit.</summary>
    public static IReadOnlyList<string> LauncherProcessNames(LauncherType launcher) => launcher switch
    {
        LauncherType.Steam => new[] { "steam.exe", "steamwebhelper.exe" },
        LauncherType.Epic => new[] { "EpicGamesLauncher.exe", "EpicWebHelper.exe" },
        LauncherType.BattleNet => new[] { "Battle.net.exe", "Battle.net Launcher.exe", "Battle.net Helper.exe" },
        LauncherType.Riot => new[] { "RiotClientServices.exe", "RiotClientUx.exe", "RiotClientUxRender.exe", "RiotClientCrashHandler.exe" },
        LauncherType.Ea => new[] { "EADesktop.exe", "EALocalHostSvc.exe", "EACefSubProcess.exe" },
        LauncherType.Ubisoft => new[] { "UbisoftConnect.exe", "upc.exe", "UplayWebCore.exe", "UbisoftGameLauncher.exe" },
        _ => Array.Empty<string>(),
    };

    /// <summary>Credential Manager target-name filters cleared after exit.</summary>
    public static IReadOnlyList<string> CredentialFilters(LauncherType launcher) => launcher switch
    {
        LauncherType.Epic => new[] { "Epic*" },
        LauncherType.BattleNet => new[] { "Battle.net*", "Blizzard*" },
        LauncherType.Riot => new[] { "Riot*" },
        LauncherType.Ea => new[] { "EA*", "Origin*", "Electronic Arts*" },
        LauncherType.Ubisoft => new[] { "Ubisoft*", "Uplay*" },
        _ => Array.Empty<string>(),
    };

    /// <summary>
    /// The only place a lease secret may reach a launcher command line. An Epic lease with <c>extra.authType</c> =
    /// <see cref="ExchangeCodeAuthType"/> carries a one-time exchange code (<c>-AUTH_TYPE=exchangecode</c>). Otherwise the
    /// account password goes on the command line (Steam <c>-login</c>, Epic <c>-AUTH_PASSWORD</c>) only when
    /// <paramref name="allowPasswordOnCommandLine"/> (<c>games.accountPool.allowPasswordOnCommandLine</c>, default off)
    /// and no session files were injected; in every other case there are no arguments.
    /// </summary>
    public static string? CredentialArgs(LauncherType launcher, string username, string secret, string? authType, bool sessionInjected, bool allowPasswordOnCommandLine)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(secret);
        if (IsExchangeCode(launcher, authType))
        {
            return "-AUTH_LOGIN=unused -AUTH_PASSWORD=" + Quote(secret) + " -AUTH_TYPE=exchangecode";
        }

        if (sessionInjected || !allowPasswordOnCommandLine)
        {
            return null;
        }

        return launcher switch
        {
            LauncherType.Steam => "-login " + Quote(username) + " " + Quote(secret),
            LauncherType.Epic => "-AUTH_LOGIN=" + Quote(username) + " -AUTH_PASSWORD=" + Quote(secret) + " -AUTH_TYPE=password",
            _ => null,
        };
    }

    /// <summary>
    /// Steam auto-login values for <c>HKEY_USERS\&lt;kioskSid&gt;\</c><see cref="SteamUserKey"/>: the kiosk user's hive,
    /// never the (possibly read-only) Steam directory. An empty <paramref name="autoLoginUser"/> clears auto-login
    /// (<c>AutoLoginUser = ""</c>, <c>RememberPassword = 0</c>).
    /// </summary>
    public static IReadOnlyList<(string Key, string Name, object Value, RegistryValueKind Kind)> SteamAutoLogin(string kioskSid, string autoLoginUser)
    {
        ArgumentNullException.ThrowIfNull(autoLoginUser);
        string key = RegistryHelper.UserHiveKey(kioskSid, SteamUserKey);
        return new (string, string, object, RegistryValueKind)[]
        {
            (key, "AutoLoginUser", autoLoginUser, RegistryValueKind.String),
            (key, "RememberPassword", autoLoginUser.Length > 0 ? 1 : 0, RegistryValueKind.DWord),
        };
    }

    /// <summary>Kills the launcher client, then applies the launcher's strategy. Throws <see cref="IpcException"/> when the lease cannot be used.</summary>
    public async Task<InjectionResult> InjectAsync(Game game, ActiveLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(lease);
        if (!_strategies.TryGetValue(lease.Launcher, out ILauncherCredentialStrategy? strategy))
        {
            return InjectionResult.None;
        }

        await KillLauncherAsync(lease.Launcher, cancellationToken).ConfigureAwait(false);
        string? exe = _detector.ResolveLauncherExe(lease.Launcher);
        InjectionResult result = await strategy.InjectAsync(game, lease, exe, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Injected {Launcher} credentials for {Title} (account {Username})", lease.Launcher, game.Title, lease.Username);
        return result;
    }

    /// <summary>Restores patched config, kills the launcher client and clears Credential Manager entries. Never throws.</summary>
    public async Task RestoreAsync(InjectionResult result, LauncherType launcher, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        await KillLauncherAsync(launcher, cancellationToken).ConfigureAwait(false);
        try
        {
            await result.RestoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Config restore for {Launcher} failed", launcher);
        }

        await ClearCredentialManagerAsync(launcher, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes the kiosk user's Credential Manager entries matching <see cref="CredentialFilters"/> (impersonating the kiosk session token).</summary>
    public Task ClearCredentialManagerAsync(LauncherType launcher, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> filters = CredentialFilters(launcher);
        int? session = _kiosk.ActiveSessionId;
        if (filters.Count == 0 || session is null)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            try
            {
                using SafeTokenHandle token = ProcessAsUser.GetUserToken((uint)session.Value);
                nint self = Kernel32.GetCurrentProcess();
                Win32Error.ThrowIfFalse(
                    Kernel32.DuplicateHandle(self, token.DangerousGetHandle(), self, out nint dup, 0, false, DuplicateSameAccess),
                    nameof(Kernel32.DuplicateHandle));
                GC.KeepAlive(token);
                using var access = new SafeAccessTokenHandle(dup);
                int deleted = WindowsIdentity.RunImpersonated(access, () =>
                {
                    int count = 0;
                    foreach (string filter in filters)
                    {
                        foreach ((string target, string _, uint type) in Advapi32.EnumerateCredentials(filter))
                        {
                            if (Advapi32.CredDeleteW(target, type, 0))
                            {
                                count++;
                            }
                        }
                    }

                    return count;
                });
                if (deleted > 0)
                {
                    _logger.LogInformation("Removed {Count} {Launcher} Credential Manager entries from the kiosk profile", deleted, launcher);
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Credential Manager cleanup for {Launcher} failed", launcher);
            }
        }, cancellationToken);
    }

    private Task KillLauncherAsync(LauncherType launcher, CancellationToken cancellationToken) => Task.Run(() =>
    {
        foreach (string name in LauncherProcessNames(launcher))
        {
            try
            {
                IReadOnlyList<KilledProcess> killed = _killer.KillByName(name, null, KillGrace);
                if (killed.Count > 0)
                {
                    _logger.LogDebug("Killed {Count} {Name} before/after account injection", killed.Count, name);
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _logger.LogDebug(ex, "KillByName({Name}) failed", name);
            }
        }
    }, cancellationToken);

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>In-memory backups of small config files, restored (or deleted when they did not exist) on undo.</summary>
    private sealed class FileBackups
    {
        private const long MaxBackupBytes = 8 * 1024 * 1024;
        private readonly List<(string Path, byte[]? Content)> _entries = new();

        public void Capture(string path)
        {
            if (_entries.Any(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            byte[]? content = null;
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > MaxBackupBytes)
                {
                    throw new IOException($"{path} is too large to back up");
                }

                content = File.ReadAllBytes(path);
            }

            _entries.Add((path, content));
        }

        public bool IsEmpty => _entries.Count == 0;

        public async Task RestoreAsync(CancellationToken cancellationToken)
        {
            foreach ((string path, byte[]? content) in _entries)
            {
                if (content is null)
                {
                    File.Delete(path);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllBytesAsync(path, content, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public InjectionResult ToResult(string? extraArgs, IReadOnlyCollection<string>? problems = null) =>
            new(extraArgs, ImmutableDictionary<string, string>.Empty, IsEmpty ? null : RestoreAsync, problems is { Count: > 0 } ? string.Join("; ", problems) : null);
    }

    /// <summary>Writes server-provided session files (<c>extra.files</c>: path → base64) into the kiosk profile.</summary>
    private sealed class ProfileFiles
    {
        private readonly IKioskProfilePaths _profile;
        private readonly ILogger _logger;

        public ProfileFiles(IKioskProfilePaths profile, ILogger logger)
        {
            _profile = profile;
            _logger = logger;
        }

        public string Expand(string path) => path
            .Replace("%LOCALAPPDATA%", _profile.LocalAppData, StringComparison.OrdinalIgnoreCase)
            .Replace("%APPDATA%", _profile.RoamingAppData, StringComparison.OrdinalIgnoreCase)
            .Replace("%USERPROFILE%", _profile.UserProfile, StringComparison.OrdinalIgnoreCase);

        public string Local(params string[] parts) => Path.Combine(new[] { _profile.LocalAppData }.Concat(parts).ToArray());

        public string Roaming(params string[] parts) => Path.Combine(new[] { _profile.RoamingAppData }.Concat(parts).ToArray());

        /// <summary>Applies <c>extra.files</c>; returns how many files were written.</summary>
        public async Task<int> ApplyAsync(ActiveLease lease, FileBackups backups, CancellationToken cancellationToken)
        {
            if (lease.Extra is not { ValueKind: JsonValueKind.Object } extra || !extra.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            int written = 0;
            foreach (JsonProperty entry in files.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string target = Path.GetFullPath(Expand(entry.Name));
                if (!IsInsideProfile(target))
                {
                    _logger.LogWarning("Refusing to write lease file outside the kiosk profile: {Path}", target);
                    continue;
                }

                byte[] content;
                try
                {
                    content = Convert.FromBase64String(entry.Value.GetString() ?? string.Empty);
                }
                catch (FormatException)
                {
                    _logger.LogWarning("Lease file {Path} is not valid base64; skipped", target);
                    continue;
                }

                backups.Capture(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, content, cancellationToken).ConfigureAwait(false);
                written++;
            }

            return written;
        }

        public async Task WriteTextAsync(string path, string text, FileBackups backups, CancellationToken cancellationToken)
        {
            backups.Capture(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }

        private bool IsInsideProfile(string fullPath)
        {
            string root = Path.GetFullPath(_profile.UserProfile).TrimEnd('\\') + "\\";
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static readonly SearchValues<char> QuoteTriggers = SearchValues.Create(" \"\t");

    private static string Quote(string value) =>
        value.Length > 0 && value.AsSpan().IndexOfAny(QuoteTriggers) < 0 ? value : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static bool IsExchangeCode(LauncherType launcher, string? authType) =>
        launcher == LauncherType.Epic && string.Equals(authType, ExchangeCodeAuthType, StringComparison.OrdinalIgnoreCase);

    /// <summary><see cref="CredentialArgs"/> for <paramref name="lease"/>; a password on the command line is logged as a warning on every use.</summary>
    private static string? LaunchArgs(ActiveLease lease, bool sessionInjected, bool allowPassword, ILogger logger)
    {
        string? authType = lease.ExtraString("authType");
        string? args = CredentialArgs(lease.Launcher, lease.Username, lease.RevealSecret(), authType, sessionInjected, allowPassword);
        if (args is not null && !IsExchangeCode(lease.Launcher, authType))
        {
            logger.LogWarning(
                "SECURITY: games.accountPool.allowPasswordOnCommandLine is on; the {Launcher} password of account {Username} is on the launcher command line, readable by every process in the kiosk session",
                lease.Launcher,
                lease.Username);
        }

        return args;
    }

    // ---- strategies ------------------------------------------------------------------------------

    /// <summary>
    /// Steam: session files from <c>extra.files</c> (e.g. <c>%LOCALAPPDATA%\Steam\local.vdf</c>) with auto-login set to the
    /// leased account in the kiosk hive; without them auto-login is cleared there (<see cref="SteamAutoLogin"/>),
    /// <c>loginusers.vdf</c> is neutralized best effort and Steam starts without credentials. Auto-login is cleared again
    /// on restore, so Steam's own "remember me" never carries an account to the next player.
    /// </summary>
    private sealed class SteamStrategy : ILauncherCredentialStrategy
    {
        private readonly ProfileFiles _files;
        private readonly IKioskCredentials _kiosk;
        private readonly Func<bool> _allowPassword;
        private readonly ILogger _logger;

        public SteamStrategy(ProfileFiles files, IKioskCredentials kiosk, Func<bool> allowPassword, ILogger logger)
        {
            _files = files;
            _kiosk = kiosk;
            _allowPassword = allowPassword;
            _logger = logger;
        }

        public LauncherType Launcher => LauncherType.Steam;

        public async Task<InjectionResult> InjectAsync(Game game, ActiveLease lease, string? launcherExe, CancellationToken cancellationToken)
        {
            var backups = new FileBackups();
            var problems = new List<string>();
            bool session = await _files.ApplyAsync(lease, backups, cancellationToken).ConfigureAwait(false) > 0;
            if (SetAutoLogin(session ? lease.Username : string.Empty) is { } error)
            {
                problems.Add(error);
            }

            if (!session)
            {
                await NeutralizeLoginUsersAsync(launcherExe, backups, cancellationToken).ConfigureAwait(false);
            }

            string? args = LaunchArgs(lease, session, _allowPassword(), _logger);
            if (!session && args is null)
            {
                _logger.LogWarning("Steam lease {LeaseId} carries no session files; Steam starts without credentials", lease.LeaseId);
                problems.Add("Steam lease has no session files; Steam started without credentials");
            }

            InjectionResult result = backups.ToResult(args, problems);
            return result with
            {
                RestoreAction = async ct =>
                {
                    _ = SetAutoLogin(string.Empty);
                    await result.RestoreAsync(ct).ConfigureAwait(false);
                },
            };
        }

        /// <summary>Writes <see cref="SteamAutoLogin"/> into the kiosk hive; returns the problem, or <see langword="null"/>.</summary>
        private string? SetAutoLogin(string user)
        {
            string sid = _kiosk.Sid;
            try
            {
                if (string.IsNullOrEmpty(sid) || !RegistryHelper.Exists(RegistryHive.Users, sid))
                {
                    throw new InvalidOperationException("the kiosk user's hive is not loaded");
                }

                foreach ((string key, string name, object value, RegistryValueKind kind) in SteamAutoLogin(sid, user))
                {
                    RegistryHelper.Set(RegistryHive.Users, key, name, value, kind);
                }

                return null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                _logger.LogWarning(ex, "Cannot set Steam auto-login in the kiosk hive; a previous Steam account may auto-login");
                return "Steam auto-login reset in the kiosk hive failed: " + ex.Message;
            }
        }

        /// <summary>Best effort: the Steam directory may sit on a read-only games volume.</summary>
        private async Task NeutralizeLoginUsersAsync(string? launcherExe, FileBackups backups, CancellationToken cancellationToken)
        {
            string? steamDir = launcherExe is null ? null : Path.GetDirectoryName(launcherExe);
            string? loginUsers = steamDir is null ? null : Path.Combine(steamDir, "config", "loginusers.vdf");
            if (loginUsers is null || !File.Exists(loginUsers))
            {
                return;
            }

            try
            {
                VdfNode root = VdfNode.Parse(await File.ReadAllTextAsync(loginUsers, cancellationToken).ConfigureAwait(false));
                VdfNode? users = root["users"];
                if (users is null)
                {
                    return;
                }

                foreach (VdfNode user in users.Children.Values)
                {
                    user.Set("RememberPassword", "0");
                    user.Set("MostRecent", "0");
                    user.Set("AllowAutoLogin", "0");
                }

                backups.Capture(loginUsers);
                await File.WriteAllTextAsync(loginUsers, root.Serialize(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                _logger.LogInformation(ex, "Cannot neutralize {File} (best effort; auto-login is reset in the kiosk hive)", loginUsers);
            }
        }
    }

    /// <summary>
    /// Epic Games Launcher: remembered login stripped from <c>GameUserSettings.ini</c>, then <c>extra.files</c>; a one-time
    /// exchange code (<c>extra.authType = "exchangeCode"</c>) goes on the command line, a password only when allowed.
    /// </summary>
    private sealed class EpicStrategy : ILauncherCredentialStrategy
    {
        private readonly ProfileFiles _files;
        private readonly Func<bool> _allowPassword;
        private readonly ILogger _logger;

        public EpicStrategy(ProfileFiles files, Func<bool> allowPassword, ILogger logger)
        {
            _files = files;
            _allowPassword = allowPassword;
            _logger = logger;
        }

        public LauncherType Launcher => LauncherType.Epic;

        public async Task<InjectionResult> InjectAsync(Game game, ActiveLease lease, string? launcherExe, CancellationToken cancellationToken)
        {
            var backups = new FileBackups();
            var problems = new List<string>();
            string ini = _files.Local("EpicGamesLauncher", "Saved", "Config", "Windows", "GameUserSettings.ini");
            if (File.Exists(ini))
            {
                try
                {
                    backups.Capture(ini);
                    string[] lines = await File.ReadAllLinesAsync(ini, cancellationToken).ConfigureAwait(false);
                    var kept = new List<string>(lines.Length);
                    bool inRememberMe = false;
                    foreach (string line in lines)
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith('['))
                        {
                            inRememberMe = trimmed.Equals("[RememberMe]", StringComparison.OrdinalIgnoreCase);
                        }

                        if (inRememberMe && (trimmed.StartsWith("Data=", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("Enable=", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        kept.Add(line);
                    }

                    await File.WriteAllLinesAsync(ini, kept, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Cannot strip remembered Epic login from {File}", ini);
                    problems.Add("Epic remembered login could not be removed: " + ex.Message);
                }
            }

            bool session = await _files.ApplyAsync(lease, backups, cancellationToken).ConfigureAwait(false) > 0;
            string? args = LaunchArgs(lease, session, _allowPassword(), _logger);
            if (!session && args is null)
            {
                _logger.LogWarning("Epic lease {LeaseId} carries no exchange code or session files; the launcher starts without credentials", lease.LeaseId);
                problems.Add("Epic lease has no exchange code or session files; the launcher started without credentials");
            }

            return backups.ToResult(args, problems);
        }
    }

    /// <summary>Battle.net: <c>Client.SavedAccountNames</c> in <c>Battle.net.config</c> pre-fills the account; session files from <c>extra.files</c>.</summary>
    private sealed class BattleNetStrategy : ILauncherCredentialStrategy
    {
        private readonly ProfileFiles _files;
        private readonly ILogger _logger;

        public BattleNetStrategy(ProfileFiles files, ILogger logger)
        {
            _files = files;
            _logger = logger;
        }

        public LauncherType Launcher => LauncherType.BattleNet;

        public async Task<InjectionResult> InjectAsync(Game game, ActiveLease lease, string? launcherExe, CancellationToken cancellationToken)
        {
            var backups = new FileBackups();
            string config = _files.Roaming("Battle.net", "Battle.net.config");
            try
            {
                JsonObject root = File.Exists(config)
                    ? JsonNode.Parse(await File.ReadAllTextAsync(config, cancellationToken).ConfigureAwait(false)) as JsonObject ?? new JsonObject()
                    : new JsonObject();
                if (root["Client"] is not JsonObject client)
                {
                    client = new JsonObject();
                    root["Client"] = client;
                }

                client["SavedAccountNames"] = lease.Username;
                await _files.WriteTextAsync(config, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), backups, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogWarning(ex, "Cannot patch {File}", config);
            }

            int written = await _files.ApplyAsync(lease, backups, cancellationToken).ConfigureAwait(false);
            if (written == 0)
            {
                _logger.LogWarning("Battle.net lease {LeaseId} carries no session files; the player must enter the password", lease.LeaseId);
            }

            return backups.ToResult(null);
        }
    }

    /// <summary>Riot Client: session data must come with the lease (<c>extra.privateSettingsYaml</c> or <c>extra.files</c>); there is no password launch option.</summary>
    private sealed class RiotStrategy : ILauncherCredentialStrategy
    {
        private readonly ProfileFiles _files;

        public RiotStrategy(ProfileFiles files) => _files = files;

        public LauncherType Launcher => LauncherType.Riot;

        public async Task<InjectionResult> InjectAsync(Game game, ActiveLease lease, string? launcherExe, CancellationToken cancellationToken)
        {
            var backups = new FileBackups();
            int written = 0;
            string? yaml = lease.ExtraString("privateSettingsYaml");
            if (!string.IsNullOrWhiteSpace(yaml))
            {
                await _files.WriteTextAsync(_files.Local("Riot Games", "Riot Client", "Data", "RiotGamesPrivateSettings.yaml"), yaml, backups, cancellationToken).ConfigureAwait(false);
                written++;
            }

            string? clientYaml = lease.ExtraString("clientPrivateSettingsYaml");
            if (!string.IsNullOrWhiteSpace(clientYaml))
            {
                await _files.WriteTextAsync(_files.Local("Riot Games", "Riot Client", "Data", "RiotClientPrivateSettings.yaml"), clientYaml, backups, cancellationToken).ConfigureAwait(false);
                written++;
            }

            written += await _files.ApplyAsync(lease, backups, cancellationToken).ConfigureAwait(false);
            if (written == 0)
            {
                await backups.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
                throw IpcError.Of(ErrorCode.AccountPoolExhausted, "Riot lease carries no session data (privateSettingsYaml)").ToException();
            }

            return backups.ToResult(null);
        }
    }

    /// <summary>EA app: session files from <c>extra.files</c>; without them the EA app shows its login form.</summary>
    private sealed class EaStrategy : ILauncherCredentialStrategy
    {
        private readonly ProfileFiles _files;
        private readonly ILogger _logger;

        public EaStrategy(ProfileFiles files, ILogger logger)
        {
            _files = files;
            _logger = logger;
        }

        public LauncherType Launcher => LauncherType.Ea;

        public async Task<InjectionResult> InjectAsync(Game game, ActiveLease lease, string? launcherExe, CancellationToken cancellationToken)
        {
            var backups = new FileBackups();
            var hint = new JsonObject { ["username"] = lease.Username, ["gameId"] = game.Id.ToString(), ["leasedAt"] = lease.LeasedAt.ToString("O") };
            await _files.WriteTextAsync(_files.Local("Electronic Arts", "EA Desktop", "clubshell-account.json"), hint.ToJsonString(), backups, cancellationToken).ConfigureAwait(false);
            int written = await _files.ApplyAsync(lease, backups, cancellationToken).ConfigureAwait(false);
            if (written == 0)
            {
                _logger.LogWarning("EA lease {LeaseId} carries no session files; the player must log in interactively", lease.LeaseId);
            }

            return backups.ToResult(null);
        }
    }

    /// <summary>Ubisoft Connect: <c>user.login</c> in <c>settings.yml</c> pre-fills the account; session files from <c>extra.files</c>.</summary>
    private sealed class UbisoftStrategy : ILauncherCredentialStrategy
    {
        private readonly ProfileFiles _files;
        private readonly ILogger _logger;

        public UbisoftStrategy(ProfileFiles files, ILogger logger)
        {
            _files = files;
            _logger = logger;
        }

        public LauncherType Launcher => LauncherType.Ubisoft;

        public async Task<InjectionResult> InjectAsync(Game game, ActiveLease lease, string? launcherExe, CancellationToken cancellationToken)
        {
            var backups = new FileBackups();
            string settings = _files.Local("Ubisoft Game Launcher", "settings.yml");
            try
            {
                var lines = new List<string>();
                if (File.Exists(settings))
                {
                    lines.AddRange(await File.ReadAllLinesAsync(settings, cancellationToken).ConfigureAwait(false));
                }

                int userIndex = lines.FindIndex(l => l.TrimEnd().Equals("user:", StringComparison.Ordinal));
                int loginIndex = lines.FindIndex(l => l.TrimStart().StartsWith("login:", StringComparison.Ordinal));
                string loginLine = "  login: " + lease.Username;
                if (loginIndex >= 0)
                {
                    lines[loginIndex] = loginLine;
                }
                else if (userIndex >= 0)
                {
                    lines.Insert(userIndex + 1, loginLine);
                }
                else
                {
                    lines.Add("user:");
                    lines.Add(loginLine);
                }

                await _files.WriteTextAsync(settings, string.Join('\n', lines) + "\n", backups, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Cannot patch {File}", settings);
            }

            await _files.ApplyAsync(lease, backups, cancellationToken).ConfigureAwait(false);
            return backups.ToResult(null);
        }
    }

    /// <summary>Plain executables need nothing.</summary>
    private sealed class ExeStrategy : ILauncherCredentialStrategy
    {
        public LauncherType Launcher => LauncherType.Exe;

        public Task<InjectionResult> InjectAsync(Game game, ActiveLease lease, string? launcherExe, CancellationToken cancellationToken) =>
            Task.FromResult(InjectionResult.None);
    }
}
