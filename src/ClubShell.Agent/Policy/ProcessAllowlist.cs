using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.RegularExpressions;

using ClubShell.Agent.Watchdog;
using ClubShell.Contracts.Pcs;
using ClubShell.Core.Abstractions;
using ClubShell.Windows.Processes;
using ClubShell.Windows.Sessions;

using PcPolicy = ClubShell.Contracts.Pcs.Policy;

namespace ClubShell.Agent.Policy;

/// <summary>Processes the allow-list must never kill: the Shell, running games, remote-admin helpers. Register any number of providers.</summary>
public interface IProtectedProcesses
{
    /// <summary><see langword="true"/> when <paramref name="pid"/> must be left alone.</summary>
    bool IsProtected(int pid);

    /// <summary>
    /// <see langword="true"/> when the processes <paramref name="pid"/> starts must be left alone too: a running game's
    /// helpers and anti-cheat. Not the Shell's: a file dialog there would hand the player a <c>cmd.exe</c> nothing kills.
    /// </summary>
    bool ProtectsChildrenOf(int pid) => false;
}

/// <summary>A process killed by the allow-list (audit).</summary>
/// <param name="Pid">Process id.</param>
/// <param name="Name">Image file name.</param>
/// <param name="Path">Full image path when known.</param>
/// <param name="Rule">Pattern that matched (deny) or <c>not in allow-list</c>.</param>
/// <param name="At">When (UTC).</param>
public sealed record ProcessBlockedEvent(int Pid, string Name, string? Path, string Rule, DateTimeOffset At);

/// <summary>
/// Section <c>processAllowlist</c>: wildcard patterns matched case-insensitively — a pattern with a path separator
/// (<c>C:\Games\*</c>, <c>*\Downloads\*.exe</c>) against the full path, any other (<c>*cheat*</c>, <c>cmd.exe</c>) against
/// the image name only, so <c>*cheat*</c> no longer hits every exe in an <c>EasyAntiCheat</c> folder. In
/// <see cref="AllowlistMode.Allow"/> only matching processes may run, in <see cref="AllowlistMode.Deny"/> matching
/// processes are killed. Applies to the kiosk WTS session only (never session 0), sweeps already-running processes on
/// apply and watches <see cref="ProcessWatcher.ProcessStarted"/> afterwards. Exempt: <see cref="ProcessKiller.SystemCritical"/>,
/// the Agent and the processes it starts, every <see cref="IProtectedProcesses"/> pid and the children of a pid it
/// <see cref="IProtectedProcesses.ProtectsChildrenOf">protects with its children</see> (a game's helpers), and files
/// whose Authenticode signature verifies to an anti-cheat vendor (<see cref="AntiCheatSigners"/>).
/// <see cref="IsAllowed"/> is the same pattern check for launch requests.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessAllowlistModule : IPolicyModule, IDisposable
{
    /// <summary>Section key.</summary>
    public const string SectionKey = "processAllowlist";

    /// <summary>
    /// Anti-cheat vendors whose signed files are never killed, matched as a prefix of the signing certificate's
    /// <c>CN</c> or <c>O</c>: Easy Anti-Cheat (Epic Games, formerly Kamu / EasyAntiCheat Oy), BattlEye, Riot Vanguard,
    /// FACEIT, PunkBuster (Even Balance), XIGNCODE (Wellbia) and nProtect GameGuard (INCA Internet).
    /// </summary>
    public static IReadOnlyList<string> AntiCheatSigners { get; } =
    [
        "Epic Games", "EasyAntiCheat", "Kamu", "BattlEye Innovations", "Riot Games", "FACEIT", "Even Balance", "Wellbia", "INCA Internet",
    ];

    private const int SignerCacheLimit = 512;

    private readonly ProcessWatcher _watcher;
    private readonly ProcessKiller _killer;
    private readonly IProtectedProcesses[] _protected;
    private readonly IClock _clock;
    private readonly AdminMaintenance? _maintenance;
    private readonly ILogger<ProcessAllowlistModule> _logger;
    private readonly ConcurrentDictionary<string, Regex> _regexCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SignerCheck> _signers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private Ruleset? _rules;
    private bool _subscribed;
    private bool _disposed;

    /// <summary>
    /// Creates the module; <paramref name="watcher"/> is shared and not disposed here. While an administrator's
    /// <paramref name="maintenance"/> window is open nothing is killed; when it ends the session is swept again.
    /// </summary>
    public ProcessAllowlistModule(
        ProcessWatcher watcher,
        ProcessKiller killer,
        IEnumerable<IProtectedProcesses> protectedProcesses,
        IClock clock,
        ILogger<ProcessAllowlistModule> logger,
        AdminMaintenance? maintenance = null)
    {
        ArgumentNullException.ThrowIfNull(protectedProcesses);
        _watcher = watcher;
        _killer = killer;
        _protected = protectedProcesses.ToArray();
        _clock = clock;
        _logger = logger;
        _maintenance = maintenance;
        if (maintenance is not null)
        {
            maintenance.Changed += OnMaintenanceChanged;
        }
    }

    /// <summary>A process was killed by the allow-list.</summary>
    public event EventHandler<ProcessBlockedEvent>? ProcessBlocked;

    /// <summary>Subject of the verified Authenticode signer of a file, or <see langword="null"/>; replaceable for tests.</summary>
    public Func<string, string?> VerifiedSigner { get; set; } = Authenticode.VerifiedSigner;

    /// <inheritdoc />
    public string Section => SectionKey;

    /// <summary><see langword="true"/> while at least one pattern is enforced.</summary>
    public bool IsActive => _rules is { Patterns.Length: > 0 };

    /// <summary>Whether <paramref name="path"/> (full path or bare image name) may run under the current rules; <see langword="true"/> when no rules are active.</summary>
    public bool IsAllowed(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Evaluate(path, out _);
    }

    /// <inheritdoc />
    public Task<PolicyModuleResult> ApplyAsync(PcPolicy policy, PolicyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        ProcessAllowlistPolicy allowlist = policy.ProcessAllowlist;
        var notes = new List<string>();
        var compiled = new List<Rule>(allowlist.Patterns.Count);
        foreach (string pattern in allowlist.Patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            try
            {
                compiled.Add(new Rule(pattern, _regexCache.GetOrAdd(pattern, static p => ProcessKiller.WildcardToRegex(p)), IsPathPattern(pattern)));
            }
            catch (ArgumentException ex)
            {
                notes.Add($"pattern '{pattern}' ignored: {ex.Message}");
            }
        }

        var rules = new Ruleset(allowlist.Mode, compiled.ToArray(), context.KioskSessionId, context.KioskUserSid);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _rules = rules;
            if (!_subscribed)
            {
                _watcher.ProcessStarted += OnProcessStarted;
                _subscribed = true;
            }
        }

        if (rules.Patterns.Length == 0)
        {
            notes.Add("no patterns; every process allowed");
            return Task.FromResult(PolicyModuleResult.Ok(notes.ToArray()));
        }

        _watcher.Start();
        int killed = Sweep(rules);
        string sessionText = rules.SessionId is { } id
            ? id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "console (only while the kiosk account is logged on there)";
        notes.Add($"{allowlist.Mode} mode with {rules.Patterns.Length} pattern(s) in session {sessionText}; initial sweep killed {killed} process(es)");
        return Task.FromResult(PolicyModuleResult.Ok(notes.ToArray()));
    }

    /// <inheritdoc />
    public Task RevertAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Unsubscribe();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_maintenance is not null)
        {
            _maintenance.Changed -= OnMaintenanceChanged;
        }

        Unsubscribe();
        lock (_gate)
        {
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private void Unsubscribe()
    {
        lock (_gate)
        {
            _rules = null;
            if (_subscribed)
            {
                _watcher.ProcessStarted -= OnProcessStarted;
                _subscribed = false;
            }
        }
    }

    /// <summary>Allowed when no rule matches in deny mode / a rule matches in allow mode; <paramref name="rule"/> names the decisive pattern when blocked.</summary>
    private bool Evaluate(string path, out string? rule)
    {
        rule = null;
        Ruleset? rules = _rules;
        if (rules is null || rules.Patterns.Length == 0)
        {
            return true;
        }

        string name = Path.GetFileName(path);
        foreach ((string pattern, Regex regex, bool matchesPath) in rules.Patterns)
        {
            bool match;
            try
            {
                match = regex.IsMatch(matchesPath ? path : name);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            if (match)
            {
                rule = rules.Mode == AllowlistMode.Deny ? pattern : null;
                return rules.Mode == AllowlistMode.Allow;
            }
        }

        if (rules.Mode == AllowlistMode.Allow)
        {
            rule = "not in allow-list";
            return false;
        }

        return true;
    }

    private int Sweep(Ruleset rules)
    {
        if (TargetSession(rules) is not { } session || session <= 0)
        {
            return 0;
        }

        Dictionary<int, int> parents = ParentsByPid();
        int killed = 0;
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != session)
                    {
                        continue;
                    }

                    int? parent = parents.TryGetValue(process.Id, out int p) ? p : null;
                    if (Consider(process.Id, process.ProcessName + ".exe", TryGetPath(process), parent))
                    {
                        killed++;
                    }
                }
                catch (InvalidOperationException)
                {
                    // exited between enumeration and inspection
                }
                catch (Win32Exception)
                {
                    // access denied (protected process)
                }
            }
        }

        return killed;
    }

    private static Dictionary<int, int> ParentsByPid()
    {
        try
        {
            var parents = new Dictionary<int, int>();
            foreach (ProcessSnapshotEntry entry in ProcessKiller.Snapshot())
            {
                parents[entry.Pid] = entry.ParentPid;
            }

            return parents;
        }
        catch (Win32Exception)
        {
            return [];
        }
    }

    private static string? TryGetPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Kills <paramref name="pid"/> when the rules say so; <see langword="true"/> when it was killed.</summary>
    private bool Consider(int pid, string name, string? path, int? parentPid)
    {
        if (pid <= 4 || pid == Environment.ProcessId || ProcessKiller.SystemCritical.Contains(name))
        {
            return false;
        }

        if (Evaluate(path ?? name, out string? rule))
        {
            return false;
        }

        if (_maintenance?.IsActive == true)
        {
            _logger.LogInformation("Allow-list leaves {Name} ({Pid}) alone: administrator maintenance", name, pid);
            return false;
        }

        if (Exemption(pid, parentPid, path) is { } why)
        {
            _logger.LogDebug("Allow-list would block {Name} ({Pid}) but {Reason}", name, pid, why);
            return false;
        }

        KilledProcess? killed = _killer.KillProcess(pid, TimeSpan.Zero);
        if (killed is null)
        {
            return false;
        }

        string reason = rule ?? "?";
        _logger.LogWarning("Process allow-list killed {Name} ({Pid}) path={Path} rule={Rule}", name, pid, path, reason);
        ProcessBlocked?.Invoke(this, new ProcessBlockedEvent(pid, name, path, reason, _clock.UtcNow));
        return true;
    }

    /// <summary>Why a process the rules block is still left alone, or <see langword="null"/> when it is not.</summary>
    private string? Exemption(int pid, int? parentPid, string? path)
    {
        foreach (IProtectedProcesses source in _protected)
        {
            if (source.IsProtected(pid))
            {
                return "it is protected";
            }
        }

        if (parentPid is { } parent && parent > 4)
        {
            // The Agent's own helpers (the clipboard clear runs cmd.exe in the player's session).
            if (parent == Environment.ProcessId)
            {
                return "the Agent started it";
            }

            foreach (IProtectedProcesses source in _protected)
            {
                if (source.ProtectsChildrenOf(parent))
                {
                    return $"its parent {parent} is a running game";
                }
            }
        }

        return path is not null && AntiCheatSigner(path) is { } signer ? $"it is signed by {signer}" : null;
    }

    /// <summary>The anti-cheat vendor that signed <paramref name="path"/>, cached per file version (path, size, write time).</summary>
    private string? AntiCheatSigner(string path)
    {
        long length;
        DateTime written;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return null;
            }

            length = file.Length;
            written = file.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        if (_signers.TryGetValue(path, out SignerCheck? cached) && cached.Length == length && cached.Written == written)
        {
            return cached.Vendor;
        }

        string? vendor = null;
        try
        {
            vendor = VerifiedSigner(path) is { } subject ? AntiCheatVendor(subject) : null;
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Signature check of {Path} failed", path);
        }

        if (_signers.Count >= SignerCacheLimit)
        {
            _signers.Clear();
        }

        _signers[path] = new SignerCheck(length, written, vendor);
        return vendor;
    }

    /// <summary>The <see cref="AntiCheatSigners"/> entry a certificate subject belongs to, or <see langword="null"/>.</summary>
    public static string? AntiCheatVendor(string subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        foreach (string name in Authenticode.NamesOf(subject))
        {
            foreach (string vendor in AntiCheatSigners)
            {
                if (name.StartsWith(vendor, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
        }

        return null;
    }

    /// <summary><see langword="true"/> for a pattern matched against the full path: one with a path separator.</summary>
    public static bool IsPathPattern(string pattern) =>
        pattern.Contains('\\', StringComparison.Ordinal) || pattern.Contains('/', StringComparison.Ordinal);

    /// <summary>When a maintenance window ends, what the administrator left running in the kiosk session is swept.</summary>
    private void OnMaintenanceChanged(object? sender, bool active)
    {
        if (active || _rules is not { Patterns.Length: > 0 } rules)
        {
            return;
        }

        try
        {
            int killed = Sweep(rules);
            _logger.LogInformation("Allow-list sweep after maintenance killed {Count} process(es)", killed);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Allow-list sweep after maintenance failed");
        }
    }

    private void OnProcessStarted(object? sender, ProcessStartedEventArgs e)
    {
        Ruleset? rules = _rules;
        if (rules is null || rules.Patterns.Length == 0 || e.SessionId == 0)
        {
            return;
        }

        if (TargetSession(rules) is not { } session || e.SessionId != unchecked((uint)session))
        {
            return;
        }

        try
        {
            _ = Consider(e.Pid, e.Name, e.Path, e.ParentPid);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(ex, "Allow-list check failed for {Name} ({Pid})", e.Name, e.Pid);
        }
    }

    /// <summary>Immutable rule snapshot swapped atomically on apply.</summary>
    /// <summary>
    /// The session the rules apply to: the kiosk session found at apply time, otherwise the console session only while the
    /// kiosk account is the one logged on there — never an administrator's session (on the pilot PC, before the kiosk
    /// account had ever logged on, the console fallback killed the administrator's PowerShell, cmd and mmc).
    /// </summary>
    private static int? TargetSession(Ruleset rules)
    {
        if (rules.SessionId is { } known)
        {
            return known;
        }

        uint console = WtsSessions.GetActiveConsoleSessionId();
        if (rules.KioskSid is null || console == 0 || console == uint.MaxValue)
        {
            return null;
        }

        try
        {
            if (WtsSessions.Get(console) is not { UserName: { Length: > 0 } user } session)
            {
                return null;
            }

            string domain = string.IsNullOrEmpty(session.Domain) ? Environment.MachineName : session.Domain;
            string sid = new NTAccount(domain, user).Translate(typeof(SecurityIdentifier)).Value;
            return string.Equals(sid, rules.KioskSid, StringComparison.OrdinalIgnoreCase) ? unchecked((int)console) : null;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return null;
        }
    }

    private sealed record Ruleset(AllowlistMode Mode, Rule[] Patterns, int? SessionId, string? KioskSid);

    /// <summary>A compiled pattern; <paramref name="MatchesPath"/> when it is matched against the full path instead of the image name.</summary>
    private sealed record Rule(string Pattern, Regex Regex, bool MatchesPath);

    private sealed record SignerCheck(long Length, DateTime Written, string? Vendor);
}
