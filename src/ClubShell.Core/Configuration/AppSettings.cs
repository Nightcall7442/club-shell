using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Pcs;
using Microsoft.Extensions.Options;

namespace ClubShell.Core.Configuration;

/// <summary>Well-known locations under <c>C:\ProgramData\ClubShell</c> (ARCHITECTURE.md §9).</summary>
public static class ClubShellPaths
{
    /// <summary>Default root: <c>%ProgramData%\ClubShell</c> (<c>/usr/share/ClubShell</c> on non-Windows hosts, tests only).</summary>
    public static string ProgramData { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClubShell");

    /// <summary><c>agent.json</c> file name.</summary>
    public const string AgentJsonFileName = "agent.json";

    /// <summary><c>shell.json</c> file name.</summary>
    public const string ShellJsonFileName = "shell.json";

    /// <summary><c>policies.json</c> file name.</summary>
    public const string PoliciesJsonFileName = "policies.json";

    /// <summary>Relative cache directory.</summary>
    public const string CacheDirName = "cache";

    /// <summary>Relative logs directory.</summary>
    public const string LogsDirName = "logs";

    /// <summary>Relative DPAPI secrets directory.</summary>
    public const string SecureDirName = "secure";

    /// <summary>Relative themes directory.</summary>
    public const string ThemesDirName = "themes";

    /// <summary>Relative staged-update directory.</summary>
    public const string PendingUpdateDirName = "pending-update";

    /// <summary>Relative downloaded-packages directory.</summary>
    public const string UpdatesCacheDirName = "cache\\updates";

    /// <summary>DPAPI-wrapped agent tokens file (<c>secure\agent.tokens</c>).</summary>
    public const string AgentTokensFileName = "agent.tokens";

    /// <summary>Persistent random hardware-id fallback (<c>secure\hwid.fallback</c>).</summary>
    public const string HwidFallbackFileName = "hwid.fallback";

    /// <summary>Default <c>agent.json</c> path.</summary>
    public static string AgentJson => Path.Combine(ProgramData, AgentJsonFileName);

    /// <summary>Default <c>shell.json</c> path.</summary>
    public static string ShellJson => Path.Combine(ProgramData, ShellJsonFileName);

    /// <summary>Default <c>policies.json</c> path.</summary>
    public static string PoliciesJson => Path.Combine(ProgramData, PoliciesJsonFileName);

    /// <summary>Default cache directory.</summary>
    public static string Cache => Path.Combine(ProgramData, CacheDirName);

    /// <summary>Default logs directory.</summary>
    public static string Logs => Path.Combine(ProgramData, LogsDirName);

    /// <summary>Default secrets directory.</summary>
    public static string Secure => Path.Combine(ProgramData, SecureDirName);

    /// <summary>Default themes directory.</summary>
    public static string Themes => Path.Combine(ProgramData, ThemesDirName);

    /// <summary>Default staged-update directory.</summary>
    public static string PendingUpdate => Path.Combine(ProgramData, PendingUpdateDirName);

    /// <summary>Shipped defaults next to the executable (<c>config\agent.default.json</c>, copied by the Agent csproj).</summary>
    public static string ShippedAgentDefaults => Path.Combine(AppContext.BaseDirectory, "config", "agent.default.json");

    /// <summary>Resolves <paramref name="path"/> against <paramref name="root"/> unless it is already rooted. Backslashes are normalized for the host OS.</summary>
    public static string Resolve(string root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(normalized) ? normalized : Path.GetFullPath(Path.Combine(root, normalized));
    }
}

/// <summary>Root of <c>agent.json</c> (ARCHITECTURE.md §12.1). Property names mirror the JSON keys in camelCase.</summary>
public sealed class AgentSettings
{
    /// <summary>Schema version implemented by this build.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Schema version of the document.</summary>
    [Range(1, CurrentVersion)]
    public int Version { get; set; } = CurrentVersion;

    /// <summary>PC id assigned by the server at registration; persisted by the Agent.</summary>
    public Guid? PcId { get; set; }

    /// <summary>PC name; defaults to the machine name. Server value wins.</summary>
    public string? PcName { get; set; }

    /// <summary>Zone; server value wins.</summary>
    public string? Zone { get; set; }

    /// <summary>Central server connection.</summary>
    [Required]
    public ServerSettings Server { get; set; } = new();

    /// <summary>Named-pipe IPC.</summary>
    [Required]
    public IpcSettings Ipc { get; set; } = new();

    /// <summary>Shell process and kiosk user.</summary>
    [Required]
    public ShellHostSettings Shell { get; set; } = new();

    /// <summary>Session timer.</summary>
    [Required]
    public SessionSettings Session { get; set; } = new();

    /// <summary>Offline mode.</summary>
    [Required]
    public OfflineSettings Offline { get; set; } = new();

    /// <summary>Games library and launchers.</summary>
    [Required]
    public GamesSettings Games { get; set; } = new();

    /// <summary>Storage (games share).</summary>
    [Required]
    public StorageSettings Storage { get; set; } = new();

    /// <summary>Self-update.</summary>
    [Required]
    public UpdatesSettings Updates { get; set; } = new();

    /// <summary>Telemetry.</summary>
    [Required]
    public TelemetrySettings Telemetry { get; set; } = new();

    /// <summary>Anti-cheat checks (wire key <c>anticheat</c>).</summary>
    [Required]
    public AntiCheatSettings Anticheat { get; set; } = new();

    /// <summary>Remote administration.</summary>
    [Required]
    public RemoteAdminSettings RemoteAdmin { get; set; } = new();

    /// <summary>Power management.</summary>
    [Required]
    public PowerSettings Power { get; set; } = new();

    /// <summary>Logging.</summary>
    [Required]
    public LoggingSettings Logging { get; set; } = new();

    /// <summary>Directory layout (not part of the shipped file; defaults to <see cref="ClubShellPaths"/>).</summary>
    [Required]
    public PathsSettings Paths { get; set; } = new();

    /// <summary>Resolves a path from the config (relative paths are relative to <see cref="PathsSettings.ProgramData"/>).</summary>
    public string ResolvePath(string path) => ClubShellPaths.Resolve(Paths.ProgramData, path);

    /// <summary>Absolute cache directory.</summary>
    [JsonIgnore]
    public string CacheDir => ResolvePath(Paths.Cache);

    /// <summary>Absolute logs directory (<c>logging.directory</c>).</summary>
    [JsonIgnore]
    public string LogsDir => ResolvePath(Logging.Directory);

    /// <summary>Absolute secrets directory.</summary>
    [JsonIgnore]
    public string SecureDir => ResolvePath(Paths.Secure);

    /// <summary>Absolute themes directory.</summary>
    [JsonIgnore]
    public string ThemesDir => ResolvePath(Paths.Themes);

    /// <summary>Absolute staged-update directory.</summary>
    [JsonIgnore]
    public string PendingUpdateDir => ResolvePath(Paths.PendingUpdate);

    /// <summary>Absolute update download directory (<c>updates.downloadDir</c>).</summary>
    [JsonIgnore]
    public string UpdatesDownloadDir => ResolvePath(Updates.DownloadDir);

    /// <summary>Absolute path of the shell token file (<c>ipc.shellTokenPath</c>).</summary>
    [JsonIgnore]
    public string ShellTokenPath => ResolvePath(Ipc.ShellTokenPath);

    /// <summary>Absolute path of the DPAPI-wrapped agent tokens file.</summary>
    [JsonIgnore]
    public string AgentTokensPath => Path.Combine(SecureDir, ClubShellPaths.AgentTokensFileName);
}

/// <summary><c>agent.json → server</c>.</summary>
public sealed class ServerSettings
{
    /// <summary>REST base URL including <c>/api/v1</c>.</summary>
    [Required]
    public string BaseUrl { get; set; } = "https://club.example.uz/api/v1";

    /// <summary>WebSocket URL (<c>wss://…/ws/agent</c>).</summary>
    [Required]
    public string WsUrl { get; set; } = "wss://club.example.uz/ws/agent";

    /// <summary>Club API key for first registration; may be blank once tokens exist.</summary>
    public string ClubApiKey { get; set; } = "";

    /// <summary>Per-request timeout in seconds.</summary>
    [Range(1, 600)]
    public int TimeoutSec { get; set; } = 15;

    /// <summary>Base64 SPKI SHA-256 pins; empty = system trust only.</summary>
    public string[] TlsPinSha256 { get; set; } = Array.Empty<string>();

    /// <summary>Retry policy.</summary>
    [Required]
    public RetrySettings Retry { get; set; } = new();

    /// <summary>Circuit breaker.</summary>
    [Required]
    public CircuitBreakerSettings CircuitBreaker { get; set; } = new();

    /// <summary>Accepted clock skew for request signatures, in seconds.</summary>
    [Range(0, 86400)]
    public int ClockSkewToleranceSec { get; set; } = 300;

    /// <summary>Send <c>X-Timestamp</c>/<c>X-Signature</c>; disable only against a mock server (<c>MOCK_SKIP_SIGNATURE</c>).</summary>
    public bool SigningEnabled { get; set; } = true;
}

/// <summary><c>agent.json → server.retry</c>.</summary>
public sealed class RetrySettings
{
    /// <summary>Total attempts including the first.</summary>
    [Range(1, 20)]
    public int MaxAttempts { get; set; } = 5;

    /// <summary>First backoff delay.</summary>
    [Range(1, 600000)]
    public int BaseDelayMs { get; set; } = 500;

    /// <summary>Backoff cap.</summary>
    [Range(1, 3600000)]
    public int MaxDelayMs { get; set; } = 15000;
}

/// <summary><c>agent.json → server.circuitBreaker</c>.</summary>
public sealed class CircuitBreakerSettings
{
    /// <summary>Failures within the sampling window that open the circuit.</summary>
    [Range(2, 1000)]
    public int Failures { get; set; } = 5;

    /// <summary>Seconds the circuit stays open (also the sampling window).</summary>
    [Range(1, 3600)]
    public int OpenSec { get; set; } = 30;
}

/// <summary><c>agent.json → ipc</c>.</summary>
public sealed class IpcSettings
{
    /// <summary>Pipe name without the <c>\\.\pipe\</c> prefix.</summary>
    [Required]
    public string PipeName { get; set; } = "clubshell-agent";

    /// <summary>Maximum frame size in bytes.</summary>
    [Range(1024, 64 * 1024 * 1024)]
    public int MaxMessageBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Expected <c>sys.ping</c> interval from the Shell.</summary>
    [Range(1, 300)]
    public int HeartbeatIntervalSec { get; set; } = 5;

    /// <summary>Missed pings before the Shell is killed and restarted.</summary>
    [Range(1, 100)]
    public int MissedHeartbeatsBeforeKill { get; set; } = 3;

    /// <summary>Maximum concurrent pipe clients.</summary>
    [Range(1, 64)]
    public int MaxConnections { get; set; } = 4;

    /// <summary>Per-connection request rate limit.</summary>
    [Range(1, 10000)]
    public int RequestsPerSecond { get; set; } = 50;

    /// <summary>Shell token file, relative to the ProgramData root.</summary>
    [Required]
    public string ShellTokenPath { get; set; } = "secure\\shell.token";
}

/// <summary><c>agent.json → shell</c>.</summary>
public sealed class ShellHostSettings
{
    /// <summary>Shell executable launched by the watchdog.</summary>
    [Required]
    public string ExePath { get; set; } = "C:\\Program Files\\ClubShell\\Shell\\clubshell-shell.exe";

    /// <summary>Kiosk user provisioning.</summary>
    [Required]
    public KioskUserSettings KioskUser { get; set; } = new();

    /// <summary>WTS enumeration interval.</summary>
    [Range(100, 60000)]
    public int WatchdogIntervalMs { get; set; } = 2000;

    /// <summary>Delay before restarting a crashed Shell.</summary>
    [Range(0, 60000)]
    public int RestartDelayMs { get; set; } = 1500;

    /// <summary>Crash-loop guard.</summary>
    [Range(1, 60)]
    public int MaxRestartsPerMinute { get; set; } = 5;

    /// <summary>Seconds to wait for <c>auth.hello</c> after launch.</summary>
    [Range(1, 600)]
    public int LaunchTimeoutSec { get; set; } = 30;
}

/// <summary><c>agent.json → shell.kioskUser</c>.</summary>
public sealed class KioskUserSettings
{
    /// <summary>Local account name.</summary>
    [Required]
    public string Name { get; set; } = "club";

    /// <summary>Create the account when missing.</summary>
    public bool CreateIfMissing { get; set; } = true;

    /// <summary>Rotate the password at every Agent start.</summary>
    public bool RotatePasswordOnStart { get; set; } = true;

    /// <summary>Reset the profile after each session.</summary>
    public bool ResetProfileOnLogout { get; set; } = true;

    /// <summary>Folder copied into a fresh profile, when set.</summary>
    public string? ProfileTemplate { get; set; }

    /// <summary>
    /// Profile-relative directories carried across a profile reset; <see langword="null"/> keeps the built-in
    /// anti-cheat list (<c>ProfileReset.PreservedDirectories</c>), an empty array preserves nothing. Wiping these
    /// makes FACEIT and Riot bootstrap from scratch every session, which costs the player a grace period and the
    /// club a stream of false <c>serviceStopped</c> reports.
    /// </summary>
    public string[]? PreserveOnReset { get; set; }
}

/// <summary><c>agent.json → session</c>.</summary>
public sealed class SessionSettings
{
    /// <summary>Remaining-minute marks at which <c>session.warning</c> is emitted.</summary>
    public int[] WarningMinutes { get; set; } = new[] { 10, 5, 1 };

    /// <summary>Server heartbeat interval.</summary>
    [Range(5, 3600)]
    public int HeartbeatSec { get; set; } = 30;

    /// <summary>Grace period after <c>endsAt</c> before the forced lock.</summary>
    [Range(0, 3600)]
    public int GraceSec { get; set; } = 60;

    /// <summary>Idle auto-lock; 0 = disabled (policy wins when set).</summary>
    [Range(0, 86400)]
    public int AutoLockOnIdleSec { get; set; }

    /// <summary>Interval at which session state is persisted to <c>cache\session.json</c>.</summary>
    [Range(1, 600)]
    public int PersistIntervalSec { get; set; } = 5;
}

/// <summary><c>agent.json → offline</c>.</summary>
public sealed class OfflineSettings
{
    /// <summary>Offline mode enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Allow new sessions while offline.</summary>
    public bool AllowNewSessions { get; set; } = true;

    /// <summary>Maximum minutes of offline play.</summary>
    [Range(0, 100000)]
    public int MaxOfflineMinutes { get; set; } = 240;

    /// <summary>Outbox capacity.</summary>
    [Range(1, 10000000)]
    public int MaxQueue { get; set; } = 10000;

    /// <summary>SQLite outbox path (relative to ProgramData).</summary>
    [Required]
    public string StorePath { get; set; } = "cache\\offline.db";

    /// <summary>Cached user TTL.</summary>
    [Range(1, 100000)]
    public int UserCacheTtlHours { get; set; } = 168;
}

/// <summary><c>agent.json → games</c>.</summary>
public sealed class GamesSettings
{
    /// <summary>Library roots scanned for installs.</summary>
    public string[] LibraryRoots { get; set; } = new[] { "D:\\Games", "G:\\Games" };

    /// <summary>Library rescan interval.</summary>
    [Range(10, 86400)]
    public int ScanIntervalSec { get; set; } = 900;

    /// <summary>Default launch timeout.</summary>
    [Range(1, 3600)]
    public int LaunchTimeoutSec { get; set; } = 90;

    /// <summary>Graceful-close wait before a forced kill.</summary>
    [Range(0, 600)]
    public int KillGraceSec { get; set; } = 10;

    /// <summary>Account pool.</summary>
    [Required]
    public AccountPoolSettings AccountPool { get; set; } = new();

    /// <summary>Cloud saves.</summary>
    [Required]
    public CloudSaveSettings CloudSave { get; set; } = new();

    /// <summary>Launcher clients.</summary>
    [Required]
    public LaunchersSettings Launchers { get; set; } = new();
}

/// <summary><c>agent.json → games.accountPool</c>.</summary>
public sealed class AccountPoolSettings
{
    /// <summary>Use pooled accounts.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Lease TTL.</summary>
    [Range(60, 604800)]
    public int LeaseTtlSec { get; set; } = 14400;

    /// <summary>Release the lease when the game exits.</summary>
    public bool ReleaseOnExit { get; set; } = true;
}

/// <summary><c>agent.json → games.cloudSave</c>.</summary>
public sealed class CloudSaveSettings
{
    /// <summary>Cloud saves enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Local bundle directory (relative to ProgramData).</summary>
    [Required]
    public string Root { get; set; } = "cache\\saves";

    /// <summary>Maximum bundle size.</summary>
    [Range(1, 100000)]
    public int MaxMb { get; set; } = 512;
}

/// <summary>One launcher client (<c>agent.json → games.launchers.*</c>).</summary>
public sealed class LauncherSettings
{
    /// <summary>Launcher executable.</summary>
    [Required]
    public string ExePath { get; set; } = "";
}

/// <summary><c>agent.json → games.launchers</c>.</summary>
public sealed class LaunchersSettings
{
    /// <summary>Steam.</summary>
    public LauncherSettings? Steam { get; set; } = new() { ExePath = "C:\\Program Files (x86)\\Steam\\steam.exe" };

    /// <summary>Epic Games Launcher.</summary>
    public LauncherSettings? Epic { get; set; } = new() { ExePath = "C:\\Program Files (x86)\\Epic Games\\Launcher\\Portal\\Binaries\\Win32\\EpicGamesLauncher.exe" };

    /// <summary>Battle.net (wire key <c>battleNet</c>).</summary>
    public LauncherSettings? BattleNet { get; set; } = new() { ExePath = "C:\\Program Files (x86)\\Battle.net\\Battle.net Launcher.exe" };

    /// <summary>Riot Client.</summary>
    public LauncherSettings? Riot { get; set; } = new() { ExePath = "C:\\Riot Games\\Riot Client\\RiotClientServices.exe" };

    /// <summary>EA app.</summary>
    public LauncherSettings? Ea { get; set; } = new() { ExePath = "C:\\Program Files\\Electronic Arts\\EA Desktop\\EA Desktop\\EADesktop.exe" };

    /// <summary>Ubisoft Connect.</summary>
    public LauncherSettings? Ubisoft { get; set; } = new() { ExePath = "C:\\Program Files (x86)\\Ubisoft\\Ubisoft Game Launcher\\UbisoftConnect.exe" };

    /// <summary>Settings for <paramref name="launcher"/>; <see langword="null"/> for <see cref="LauncherType.Exe"/> or when not configured.</summary>
    public LauncherSettings? ForLauncher(LauncherType launcher) => launcher switch
    {
        LauncherType.Steam => Steam,
        LauncherType.Epic => Epic,
        LauncherType.BattleNet => BattleNet,
        LauncherType.Riot => Riot,
        LauncherType.Ea => Ea,
        LauncherType.Ubisoft => Ubisoft,
        _ => null,
    };
}

/// <summary><c>agent.json → storage</c>.</summary>
public sealed class StorageSettings
{
    /// <summary>Games network share.</summary>
    [Required]
    public GamesShareSettings GamesShare { get; set; } = new();
}

/// <summary><c>agent.json → storage.gamesShare</c>.</summary>
public sealed class GamesShareSettings
{
    /// <summary>Mount the share at startup.</summary>
    public bool Enabled { get; set; }

    /// <summary>UNC path.</summary>
    public string UncPath { get; set; } = "\\\\nas01\\games";

    /// <summary>Drive letter to map.</summary>
    [StringLength(1, MinimumLength = 1)]
    public string DriveLetter { get; set; } = "G";

    /// <summary>DPAPI file with <c>{ username, password }</c> (relative to ProgramData).</summary>
    public string CredentialsRef { get; set; } = "secure\\share.cred";

    /// <summary>Mount attempts.</summary>
    [Range(1, 20)]
    public int MountRetries { get; set; } = 3;

    /// <summary>iSCSI target, when used instead of SMB.</summary>
    public IscsiSettings? Iscsi { get; set; }
}

/// <summary><c>agent.json → storage.gamesShare.iscsi</c>.</summary>
public sealed class IscsiSettings
{
    /// <summary>Portal <c>host:port</c>.</summary>
    [Required]
    public string Portal { get; set; } = "";

    /// <summary>Target IQN.</summary>
    [Required]
    public string TargetIqn { get; set; } = "";

    /// <summary>
    /// Mark the target's disks read-only before the volume is used. Set it for a LUN several PCs mount at once: the
    /// first client that writes to a shared games volume — Windows setting the NTFS dirty bit is enough — corrupts it
    /// for every other client. Off by default because a LUN dedicated to one PC is legitimately writable.
    /// </summary>
    public bool ReadOnly { get; set; }
}

/// <summary><c>agent.json → updates</c>.</summary>
public sealed class UpdatesSettings
{
    /// <summary>Channel (policy overrides).</summary>
    public UpdateChannel Channel { get; set; } = UpdateChannel.Stable;

    /// <summary>Manifest check interval.</summary>
    [Range(60, 604800)]
    public int CheckIntervalSec { get; set; } = 3600;

    /// <summary>Apply staged updates automatically when idle.</summary>
    public bool AutoInstall { get; set; } = true;

    /// <summary>Local-time window for non-mandatory updates; <see langword="null"/> = any time when idle.</summary>
    public UpdateApplyWindowSettings? ApplyWindow { get; set; } = new();

    /// <summary>PEM public key used to verify package signatures.</summary>
    [Required]
    public string PublicKeyPath { get; set; } = "C:\\Program Files\\ClubShell\\Agent\\update-public.pem";

    /// <summary>Download directory (relative to ProgramData).</summary>
    [Required]
    public string DownloadDir { get; set; } = "cache\\updates";
}

/// <summary><c>agent.json → updates.applyWindow</c>; <see cref="To"/> &lt; <see cref="From"/> wraps midnight.</summary>
public sealed class UpdateApplyWindowSettings
{
    /// <summary>Window start (local <c>HH:mm</c>).</summary>
    public TimeOnly From { get; set; } = new(4, 0);

    /// <summary>Window end (local <c>HH:mm</c>).</summary>
    public TimeOnly To { get; set; } = new(7, 0);

    /// <summary>Contract form.</summary>
    public TimeWindow ToTimeWindow() => new(From, To);

    /// <summary><see langword="true"/> when <paramref name="time"/> falls inside the window (midnight wrap supported).</summary>
    public bool Contains(TimeOnly time) => From <= To ? time >= From && time < To : time >= From || time < To;
}

/// <summary><c>agent.json → telemetry</c>.</summary>
public sealed class TelemetrySettings
{
    /// <summary>Telemetry enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Local sampling / <c>sys.metrics</c> interval.</summary>
    [Range(1, 3600)]
    public int MetricsIntervalSec { get; set; } = 5;

    /// <summary>Batch upload interval.</summary>
    [Range(5, 86400)]
    public int UploadIntervalSec { get; set; } = 60;

    /// <summary>Hardware inventory rescan interval.</summary>
    [Range(60, 604800)]
    public int HardwareRescanSec { get; set; } = 3600;
}

/// <summary><c>agent.json → anticheat</c>.</summary>
public sealed class AntiCheatSettings
{
    /// <summary>Check interval.</summary>
    [Range(5, 86400)]
    public int CheckIntervalSec { get; set; } = 30;

    /// <summary>Require Secure Boot.</summary>
    public bool RequireSecureBoot { get; set; }

    /// <summary>Require a TPM.</summary>
    public bool RequireTpm { get; set; }

    /// <summary>
    /// Require hypervisor-enforced code integrity. Off by default because VBS costs frames and many clubs disable it;
    /// clubs whose titles are checked for it (FACEIT, Vanguard) turn it on and get <c>hvciOff</c> as a violation
    /// instead of a log line, including when the hypervisor itself is off in the boot configuration.
    /// </summary>
    public bool RequireHvci { get; set; }

    /// <summary>Report violations to the server.</summary>
    public bool ReportViolations { get; set; } = true;
}

/// <summary><c>agent.json → remoteAdmin</c>.</summary>
public sealed class RemoteAdminSettings
{
    /// <summary>Allow screenshots.</summary>
    public bool AllowScreenCapture { get; set; } = true;

    /// <summary>Allow remote input.</summary>
    public bool AllowRemoteInput { get; set; } = true;

    /// <summary>Capture frame rate.</summary>
    [Range(1, 60)]
    public int CaptureFps { get; set; } = 5;

    /// <summary>JPEG quality.</summary>
    [Range(1, 100)]
    public int CaptureQuality { get; set; } = 60;

    /// <summary>Show the on-screen indicator during remote control.</summary>
    public bool ShowIndicator { get; set; } = true;
}

/// <summary><c>agent.json → power</c>.</summary>
public sealed class PowerSettings
{
    /// <summary>Send/accept Wake-on-LAN.</summary>
    public bool WolEnabled { get; set; } = true;

    /// <summary>Grace period before shutdown/reboot.</summary>
    [Range(0, 3600)]
    public int ShutdownGraceSec { get; set; } = 20;

    /// <summary>Allow the Shell to request a reboot.</summary>
    public bool AllowShellReboot { get; set; } = true;
}

/// <summary><c>agent.json → logging</c>.</summary>
public sealed class LoggingSettings
{
    /// <summary>Minimum level: <c>Verbose|Debug|Information|Warning|Error|Fatal</c>.</summary>
    [Required]
    [RegularExpression("^(Verbose|Debug|Information|Warning|Error|Fatal)$")]
    public string Level { get; set; } = "Information";

    /// <summary>Log directory (relative to ProgramData).</summary>
    [Required]
    public string Directory { get; set; } = "logs";

    /// <summary>Retention in days.</summary>
    [Range(1, 3650)]
    public int RetainDays { get; set; } = 14;

    /// <summary>Per-file size cap.</summary>
    [Range(1, 10240)]
    public int MaxFileMb { get; set; } = 20;

    /// <summary>Mirror Warning+ to the Windows Event Log.</summary>
    public bool EventLog { get; set; } = true;
}

/// <summary>Directory layout; every relative entry is resolved against <see cref="ProgramData"/>.</summary>
public sealed class PathsSettings
{
    /// <summary>Root directory.</summary>
    [Required]
    public string ProgramData { get; set; } = ClubShellPaths.ProgramData;

    /// <summary>Cache directory.</summary>
    [Required]
    public string Cache { get; set; } = ClubShellPaths.CacheDirName;

    /// <summary>Logs directory (informational; <c>logging.directory</c> is authoritative).</summary>
    [Required]
    public string Logs { get; set; } = ClubShellPaths.LogsDirName;

    /// <summary>Secrets directory.</summary>
    [Required]
    public string Secure { get; set; } = ClubShellPaths.SecureDirName;

    /// <summary>Themes directory.</summary>
    [Required]
    public string Themes { get; set; } = ClubShellPaths.ThemesDirName;

    /// <summary>Staged-update directory.</summary>
    [Required]
    public string PendingUpdate { get; set; } = ClubShellPaths.PendingUpdateDirName;
}

/// <summary>
/// Validates <see cref="AgentSettings"/>: DataAnnotations on every nested settings object plus semantic checks
/// (absolute <c>http(s)</c>/<c>ws(s)</c> URLs, retry bounds). Registered as <see cref="IValidateOptions{TOptions}"/>.
/// </summary>
public sealed class AgentSettingsValidator : IValidateOptions<AgentSettings>
{
    /// <summary>Validates <paramref name="settings"/> and returns the list of problems (empty when valid).</summary>
    public static IReadOnlyList<string> Check(AgentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new List<string>();
        ValidateObject(settings, "", errors);

        if (!Uri.TryCreate(settings.Server?.BaseUrl, UriKind.Absolute, out var baseUrl) || (baseUrl.Scheme != Uri.UriSchemeHttps && baseUrl.Scheme != Uri.UriSchemeHttp))
        {
            errors.Add("server.baseUrl: must be an absolute http(s) URL");
        }

        if (!Uri.TryCreate(settings.Server?.WsUrl, UriKind.Absolute, out var wsUrl) || (wsUrl.Scheme != Uri.UriSchemeWss && wsUrl.Scheme != Uri.UriSchemeWs))
        {
            errors.Add("server.wsUrl: must be an absolute ws(s) URL");
        }

        if (settings.Server?.Retry is { } retry && retry.MaxDelayMs < retry.BaseDelayMs)
        {
            errors.Add("server.retry.maxDelayMs: must be >= baseDelayMs");
        }

        if (settings.Session?.WarningMinutes is { } marks && Array.Exists(marks, m => m <= 0))
        {
            errors.Add("session.warningMinutes: every mark must be > 0");
        }

        return errors;
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AgentSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = Check(options);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateObject(object instance, string path, List<string> errors)
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true))
        {
            foreach (var result in results)
            {
                var members = string.Join(", ", result.MemberNames.Select(m => JsonNamingPolicy.CamelCase.ConvertName(m)));
                errors.Add($"{path}{members}: {result.ErrorMessage}");
            }
        }

        foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0 || property.PropertyType.Namespace != typeof(AgentSettings).Namespace)
            {
                continue;
            }

            if (property.GetValue(instance) is { } child)
            {
                ValidateObject(child, $"{path}{JsonNamingPolicy.CamelCase.ConvertName(property.Name)}.", errors);
            }
        }
    }
}
