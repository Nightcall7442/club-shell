using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using ClubShell.Windows.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace ClubShell.Windows.Processes;

/// <summary>One row of a Toolhelp process snapshot.</summary>
/// <param name="Pid">Process id.</param>
/// <param name="ParentPid">Parent process id at creation time (may be recycled).</param>
/// <param name="ExeName">Image file name, e.g. <c>notepad.exe</c>.</param>
public sealed record ProcessSnapshotEntry(int Pid, int ParentPid, string ExeName);

/// <summary>A process terminated by <see cref="ProcessKiller"/>.</summary>
/// <param name="Pid">Process id.</param>
/// <param name="Name">Image file name.</param>
/// <param name="Graceful"><see langword="true"/> when it exited after WM_CLOSE, <see langword="false"/> when TerminateProcess was needed.</param>
public sealed record KilledProcess(int Pid, string Name, bool Graceful);

/// <summary>
/// Terminates process trees, processes by name and everything outside a policy allow-list, while never touching
/// the hard-coded set of Windows-critical images (<see cref="SystemCritical"/>) or the Agent/Shell themselves.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessKiller
{
    private const uint KillAccess = NativeConst.PROCESS_TERMINATE | NativeConst.SYNCHRONIZE | NativeConst.PROCESS_QUERY_LIMITED_INFORMATION;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private readonly ILogger<ProcessKiller> _logger;

    /// <summary>Creates a killer.</summary>
    public ProcessKiller(ILogger<ProcessKiller>? logger = null) => _logger = logger ?? NullLogger<ProcessKiller>.Instance;

    /// <summary>Images that are never terminated, whatever the caller asks (explorer.exe is handled by <c>killExplorer</c>).</summary>
    public static IReadOnlySet<string> SystemCritical { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "memory compression", "secure system", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe",
        "services.exe", "lsass.exe", "lsaiso.exe", "svchost.exe", "dwm.exe", "fontdrvhost.exe", "logonui.exe", "userinit.exe",
        "sihost.exe", "ctfmon.exe", "conhost.exe", "audiodg.exe", "spoolsv.exe", "dllhost.exe", "runtimebroker.exe",
        "taskhostw.exe", "searchindexer.exe", "wmiprvse.exe", "wudfhost.exe", "msmpeng.exe", "nissrv.exe",
        "securityhealthservice.exe", "securityhealthsystray.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe",
        "textinputhost.exe", "applicationframehost.exe", "clubshellagent.exe", "clubshell-shell.exe",
    };

    /// <summary>Kills <paramref name="pid"/> and all of its descendants (children first), gracefully when <paramref name="gracefulTimeout"/> &gt; 0.</summary>
    public IReadOnlyList<KilledProcess> KillTree(int pid, TimeSpan gracefulTimeout)
    {
        List<ProcessSnapshotEntry> snapshot = Toolhelp32.Snapshot();
        var byPid = snapshot.ToDictionary(e => e.Pid);
        var byParent = snapshot.ToLookup(e => e.ParentPid);
        var targets = new List<ProcessSnapshotEntry>();
        var queue = new Queue<int>();
        queue.Enqueue(pid);
        var seen = new HashSet<int>();
        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            if (!seen.Add(current) || !byPid.TryGetValue(current, out ProcessSnapshotEntry? entry))
            {
                continue;
            }

            targets.Add(entry);
            foreach (ProcessSnapshotEntry child in byParent[current])
            {
                if (child.Pid != current && child.Pid != pid)
                {
                    queue.Enqueue(child.Pid);
                }
            }
        }

        targets.Reverse(); // deepest descendants first, root last
        return Kill(targets, gracefulTimeout);
    }

    /// <summary>Kills every process whose image name matches the wildcard <paramref name="pattern"/> (<c>*</c>, <c>?</c>), except <paramref name="exceptPids"/>.</summary>
    public IReadOnlyList<KilledProcess> KillByName(string pattern, IReadOnlySet<int>? exceptPids = null, TimeSpan gracefulTimeout = default)
    {
        Regex regex = WildcardToRegex(pattern);
        List<ProcessSnapshotEntry> targets = Toolhelp32.Snapshot()
            .Where(e => regex.IsMatch(e.ExeName) && !(exceptPids?.Contains(e.Pid) ?? false))
            .ToList();
        return Kill(targets, gracefulTimeout);
    }

    /// <summary>
    /// Kills every process in interactive sessions (session 0 is never touched) whose image name is not in
    /// <paramref name="allowedExeNames"/> (build it with <see cref="StringComparer.OrdinalIgnoreCase"/>), skipping
    /// <paramref name="protectedPids"/>, <see cref="SystemCritical"/> and explorer.exe unless
    /// <paramref name="killExplorer"/> (shell replacement policy) is set.
    /// </summary>
    public IReadOnlyList<KilledProcess> KillOutsideAllowlist(IReadOnlySet<string> allowedExeNames, IReadOnlySet<int> protectedPids, bool killExplorer = false, TimeSpan gracefulTimeout = default)
    {
        ArgumentNullException.ThrowIfNull(allowedExeNames);
        ArgumentNullException.ThrowIfNull(protectedPids);
        int self = Environment.ProcessId;
        var targets = new List<ProcessSnapshotEntry>();
        foreach (ProcessSnapshotEntry entry in Toolhelp32.Snapshot())
        {
            if (entry.Pid == self || protectedPids.Contains(entry.Pid) || allowedExeNames.Contains(entry.ExeName))
            {
                continue;
            }

            if (!killExplorer && entry.ExeName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Kernel32.ProcessIdToSessionId((uint)entry.Pid, out uint session) || session == 0)
            {
                continue;
            }

            targets.Add(entry);
        }

        return Kill(targets, gracefulTimeout);
    }

    /// <summary>Kills a single process; returns <see langword="null"/> when it is protected or already gone.</summary>
    public KilledProcess? KillProcess(int pid, TimeSpan gracefulTimeout)
    {
        ProcessSnapshotEntry? entry = Toolhelp32.Snapshot().FirstOrDefault(e => e.Pid == pid);
        if (entry is null)
        {
            return null;
        }

        List<KilledProcess> killed = Kill(new[] { entry }, gracefulTimeout);
        return killed.Count == 0 ? null : killed[0];
    }

    /// <summary>Converts a <c>*cheat*.exe</c> style pattern into an anchored, case-insensitive regex.</summary>
    public static Regex WildcardToRegex(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        string escaped = Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal);
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private List<KilledProcess> Kill(IReadOnlyList<ProcessSnapshotEntry> targets, TimeSpan gracefulTimeout)
    {
        var killed = new List<KilledProcess>();
        var pending = new List<(ProcessSnapshotEntry Entry, SafeProcessHandle Handle)>();
        try
        {
            foreach (ProcessSnapshotEntry entry in targets)
            {
                if (entry.Pid <= 4 || entry.Pid == Environment.ProcessId || SystemCritical.Contains(entry.ExeName))
                {
                    _logger.LogDebug("Refusing to kill protected process {Name} ({Pid})", entry.ExeName, entry.Pid);
                    continue;
                }

                SafeProcessHandle handle = Kernel32.OpenProcess(KillAccess, false, (uint)entry.Pid);
                if (handle.IsInvalid)
                {
                    int error = Win32Error.Last();
                    handle.Dispose();
                    _logger.LogDebug("OpenProcess({Pid}) failed: {Error}", entry.Pid, Win32Error.Message(error));
                    continue;
                }

                pending.Add((entry, handle));
            }

            if (gracefulTimeout > TimeSpan.Zero)
            {
                foreach ((ProcessSnapshotEntry entry, _) in pending)
                {
                    _ = ProcessWindows.PostClose((uint)entry.Pid);
                }

                long deadline = Environment.TickCount64 + (long)gracefulTimeout.TotalMilliseconds;
                bool anyRunning = true;
                while (anyRunning && Environment.TickCount64 < deadline)
                {
                    anyRunning = false;
                    foreach ((_, SafeProcessHandle handle) in pending)
                    {
                        if (Kernel32.WaitForSingleObject(handle, 0) == (uint)WaitResult.WAIT_TIMEOUT)
                        {
                            anyRunning = true;
                            break;
                        }
                    }

                    if (anyRunning)
                    {
                        Thread.Sleep(PollInterval);
                    }
                }
            }

            foreach ((ProcessSnapshotEntry entry, SafeProcessHandle handle) in pending)
            {
                bool graceful = Kernel32.WaitForSingleObject(handle, 0) == (uint)WaitResult.WAIT_OBJECT_0;
                if (!graceful && !Kernel32.TerminateProcess(handle, 1))
                {
                    int error = Win32Error.Last();
                    if (Kernel32.WaitForSingleObject(handle, 0) != (uint)WaitResult.WAIT_OBJECT_0)
                    {
                        _logger.LogWarning("TerminateProcess({Pid} {Name}) failed: {Error}", entry.Pid, entry.ExeName, Win32Error.Message(error));
                        continue;
                    }
                }

                killed.Add(new KilledProcess(entry.Pid, entry.ExeName, graceful));
                _logger.LogInformation("Killed {Name} ({Pid}) graceful={Graceful}", entry.ExeName, entry.Pid, graceful);
            }
        }
        finally
        {
            foreach ((_, SafeProcessHandle handle) in pending)
            {
                handle.Dispose();
            }
        }

        return killed;
    }
}

/// <summary>Toolhelp32 process snapshot (<see cref="Kernel32.CreateToolhelp32Snapshot"/>), the only cheap way to read parent pids.</summary>
[SupportedOSPlatform("windows")]
internal static class Toolhelp32
{
    /// <summary>All processes with their parent pid and image name; throws on failure.</summary>
    public static unsafe List<ProcessSnapshotEntry> Snapshot()
    {
        nint snapshot = Kernel32.CreateToolhelp32Snapshot(NativeConst.TH32CS_SNAPPROCESS, 0);
        if (snapshot == NativeConst.INVALID_HANDLE_VALUE || snapshot == 0)
        {
            Win32Error.ThrowLastError(nameof(Kernel32.CreateToolhelp32Snapshot));
        }

        try
        {
            var entries = new List<ProcessSnapshotEntry>(256);
            PROCESSENTRY32W entry = PROCESSENTRY32W.Create();
            if (!Kernel32.Process32FirstW(snapshot, &entry))
            {
                Win32Error.ThrowLastError(nameof(Kernel32.Process32FirstW));
            }

            do
            {
                entries.Add(new ProcessSnapshotEntry((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.ExeFile));
            }
            while (Kernel32.Process32NextW(snapshot, &entry));

            return entries;
        }
        finally
        {
            _ = Kernel32.CloseHandle(snapshot);
        }
    }
}
