using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

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
}

/// <summary>A process killed by the allow-list (audit).</summary>
/// <param name="Pid">Process id.</param>
/// <param name="Name">Image file name.</param>
/// <param name="Path">Full image path when known.</param>
/// <param name="Rule">Pattern that matched (deny) or <c>not in allow-list</c>.</param>
/// <param name="At">When (UTC).</param>
public sealed record ProcessBlockedEvent(int Pid, string Name, string? Path, string Rule, DateTimeOffset At);

/// <summary>
/// Section <c>processAllowlist</c>: wildcard patterns (<c>*cheat*</c>, <c>cmd.exe</c>, <c>C:\Games\*</c>) matched
/// case-insensitively against the image name and the full path. In <see cref="AllowlistMode.Allow"/> only matching
/// processes may run, in <see cref="AllowlistMode.Deny"/> matching processes are killed. Applies to the kiosk WTS
/// session only (never session 0), sweeps already-running processes on apply and watches
/// <see cref="ProcessWatcher.ProcessStarted"/> afterwards. <see cref="ProcessKiller.SystemCritical"/>, the Agent and
/// every <see cref="IProtectedProcesses"/> pid are exempt. <see cref="IsAllowed"/> is the same check for launch requests.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessAllowlistModule : IPolicyModule, IDisposable
{
    /// <summary>Section key.</summary>
    public const string SectionKey = "processAllowlist";

    private readonly ProcessWatcher _watcher;
    private readonly ProcessKiller _killer;
    private readonly IProtectedProcesses[] _protected;
    private readonly IClock _clock;
    private readonly ILogger<ProcessAllowlistModule> _logger;
    private readonly ConcurrentDictionary<string, Regex> _regexCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private Ruleset? _rules;
    private bool _subscribed;
    private bool _disposed;

    /// <summary>Creates the module; <paramref name="watcher"/> is shared and not disposed here.</summary>
    public ProcessAllowlistModule(
        ProcessWatcher watcher,
        ProcessKiller killer,
        IEnumerable<IProtectedProcesses> protectedProcesses,
        IClock clock,
        ILogger<ProcessAllowlistModule> logger)
    {
        ArgumentNullException.ThrowIfNull(protectedProcesses);
        _watcher = watcher;
        _killer = killer;
        _protected = protectedProcesses.ToArray();
        _clock = clock;
        _logger = logger;
    }

    /// <summary>A process was killed by the allow-list.</summary>
    public event EventHandler<ProcessBlockedEvent>? ProcessBlocked;

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
        var compiled = new List<(string Pattern, Regex Regex)>(allowlist.Patterns.Count);
        foreach (string pattern in allowlist.Patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            try
            {
                compiled.Add((pattern, _regexCache.GetOrAdd(pattern, static p => ProcessKiller.WildcardToRegex(p))));
            }
            catch (ArgumentException ex)
            {
                notes.Add($"pattern '{pattern}' ignored: {ex.Message}");
            }
        }

        var rules = new Ruleset(allowlist.Mode, compiled.ToArray(), context.KioskSessionId);
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
        string sessionText = rules.SessionId is { } id ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : "console";
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
        foreach ((string pattern, Regex regex) in rules.Patterns)
        {
            bool match;
            try
            {
                match = regex.IsMatch(name) || regex.IsMatch(path);
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
        int session = rules.SessionId ?? unchecked((int)WtsSessions.GetActiveConsoleSessionId());
        if (session <= 0)
        {
            return 0;
        }

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

                    if (Consider(process.Id, process.ProcessName + ".exe", TryGetPath(process)))
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
    private bool Consider(int pid, string name, string? path)
    {
        if (pid <= 4 || pid == Environment.ProcessId || ProcessKiller.SystemCritical.Contains(name))
        {
            return false;
        }

        if (Evaluate(path ?? name, out string? rule))
        {
            return false;
        }

        foreach (IProtectedProcesses source in _protected)
        {
            if (source.IsProtected(pid))
            {
                _logger.LogDebug("Allow-list would block {Name} ({Pid}) but it is protected", name, pid);
                return false;
            }
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

    private void OnProcessStarted(object? sender, ProcessStartedEventArgs e)
    {
        Ruleset? rules = _rules;
        if (rules is null || rules.Patterns.Length == 0 || e.SessionId == 0)
        {
            return;
        }

        uint session = rules.SessionId is { } configured ? unchecked((uint)configured) : WtsSessions.GetActiveConsoleSessionId();
        if (e.SessionId != session)
        {
            return;
        }

        try
        {
            _ = Consider(e.Pid, e.Name, e.Path);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(ex, "Allow-list check failed for {Name} ({Pid})", e.Name, e.Pid);
        }
    }

    /// <summary>Immutable rule snapshot swapped atomically on apply.</summary>
    private sealed record Ruleset(AllowlistMode Mode, (string Pattern, Regex Regex)[] Patterns, int? SessionId);
}
