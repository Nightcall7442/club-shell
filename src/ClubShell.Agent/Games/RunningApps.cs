using System.Collections.Concurrent;
using System.Runtime.Versioning;
using ClubShell.Contracts.Games;
using ClubShell.Contracts.Ipc;
using ClubShell.Windows.Native;
using ClubShell.Windows.Processes;
using Microsoft.Win32.SafeHandles;

namespace ClubShell.Agent.Games;

/// <summary>One process of the player's session, as <see cref="RunningApps"/> judges it.</summary>
/// <param name="Pid">Process id.</param>
/// <param name="ParentPid">Parent pid at creation (may be recycled; <paramref name="StartedAt"/> tells).</param>
/// <param name="ImagePath">Full image path, when readable.</param>
/// <param name="StartedAt">Creation time, when readable.</param>
public sealed record AppProcess(int Pid, int ParentPid, string? ImagePath, DateTimeOffset? StartedAt);

/// <summary>The processes of one interactive session (a seam: the tests feed <see cref="RunningApps"/> their own).</summary>
public interface IAppProcessSource
{
    /// <summary>Every process of session <paramref name="sessionId"/>.</summary>
    IReadOnlyList<AppProcess> Snapshot(int sessionId);
}

/// <summary>
/// Which apps of the catalogue run in the player's session, for <c>apps.running</c>, <c>apps.kill</c> and the
/// "already running" check of <c>apps.launch</c>.
/// </summary>
/// <remarks>
/// An app is rarely one process. Discord's <c>Update.exe</c> starts <c>app-1.0.x\Discord.exe</c> and exits; Steam's UI is
/// <c>steamwebhelper.exe</c>; a browser started a second time hands the work to the instance already running and exits.
/// So an app's processes are, in the player's session: the process the Agent started for it (while it lives), every
/// descendant of an app process, and every process whose image is the app's executable or lives in the app's own folder.
/// The folder rule is off for shared folders (<c>C:\Windows\System32</c>, <c>C:\Program Files</c>, a drive root, …),
/// where it would take in unrelated programs. Processes of a running game, the Shell, the Agent and Windows' own are
/// never an app's: "close" must never take one of those down with it.
/// </remarks>
public sealed class RunningApps
{
    /// <summary>How far a process's creation time may be from the Agent's launch time for its pid to still be that launch.</summary>
    private static readonly TimeSpan LaunchSkew = TimeSpan.FromSeconds(10);

    /// <summary>Last folder names that are shared by many programs: an exe in one of them owns no folder.</summary>
    private static readonly HashSet<string> SharedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "windows", "system32", "syswow64", "program files", "program files (x86)", "programdata", "users", "appdata",
        "local", "locallow", "roaming", "programs", "temp", "desktop", "downloads", "games",
    };

    /// <summary>Images never counted as an app's, by file name.</summary>
    private static readonly HashSet<string> NeverApp = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "clubshellagent.exe", "clubshell-shell.exe", "clubshell.exe",
    };

    private readonly ConcurrentDictionary<Guid, Launch> _launches = new();

    /// <summary>A process the Agent started for an app.</summary>
    /// <param name="AppId">App id.</param>
    /// <param name="Pid">Process id.</param>
    /// <param name="StartedAt">Launch time.</param>
    public sealed record Launch(Guid AppId, int Pid, DateTimeOffset StartedAt);

    /// <summary>Remembers that <paramref name="pid"/> was started for <paramref name="appId"/> (the latest launch wins).</summary>
    public void Record(Guid appId, int pid, DateTimeOffset startedAt) => _launches[appId] = new Launch(appId, pid, startedAt);

    /// <summary>Forgets every launch (the session ended: whatever is still running is no longer this player's).</summary>
    public void Clear() => _launches.Clear();

    /// <summary>The launch recorded for <paramref name="appId"/>, if any.</summary>
    public Launch? LaunchOf(Guid appId) => _launches.TryGetValue(appId, out Launch? launch) ? launch : null;

    /// <summary>
    /// The running apps of <paramref name="catalogue"/> among <paramref name="processes"/> (oldest first), skipping
    /// <paramref name="excludedPids"/> (running games). Launches whose processes are all gone are forgotten.
    /// </summary>
    public IReadOnlyList<RunningApp> Resolve(IReadOnlyList<App> catalogue, IReadOnlyList<AppProcess> processes, IReadOnlySet<int> excludedPids)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(excludedPids);
        var items = new List<RunningApp>();
        Dictionary<int, AppProcess> byPid = processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
        // One process is one app's: a descendant of Steam that lives in Discord's folder is Steam's (the first app wins).
        var taken = new HashSet<int>(excludedPids);
        foreach (App app in catalogue)
        {
            Launch? launch = LaunchOf(app.Id);
            IReadOnlyList<int> pids = Match(app, launch, processes, taken);
            if (pids.Count == 0)
            {
                if (launch is not null)
                {
                    _launches.TryRemove(new KeyValuePair<Guid, Launch>(app.Id, launch));
                }

                continue;
            }

            taken.UnionWith(pids);
            int main = launch is not null && pids.Contains(launch.Pid)
                ? launch.Pid
                : pids.OrderBy(p => byPid[p].StartedAt ?? DateTimeOffset.MaxValue).ThenBy(p => p).First();
            DateTimeOffset startedAt = launch is not null && pids.Contains(launch.Pid)
                ? launch.StartedAt
                : byPid[main].StartedAt ?? launch?.StartedAt ?? DateTimeOffset.UtcNow;
            items.Add(new RunningApp(app.Id, app.Title, main, pids, startedAt));
        }

        return items.OrderBy(i => i.StartedAt).ToList();
    }

    /// <summary>
    /// Pids of <paramref name="app"/> among <paramref name="processes"/>, ascending; see the class remarks.
    /// <paramref name="excludedPids"/> are never matched and do not pass descent on.
    /// </summary>
    public static IReadOnlyList<int> Match(App app, Launch? launch, IReadOnlyList<AppProcess> processes, IReadOnlySet<int> excludedPids)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(excludedPids);
        string exe = NormalizePath(app.ExePath);
        string? home = OwnFolder(exe);
        var matched = new Dictionary<int, AppProcess>();
        foreach (AppProcess p in processes)
        {
            if (!Eligible(p, excludedPids))
            {
                continue;
            }

            string image = NormalizePath(p.ImagePath);
            bool launched = launch is not null && p.Pid == launch.Pid
                && (p.StartedAt is not { } at || (at - launch.StartedAt).Duration() <= LaunchSkew);
            bool own = image.Length > 0 && exe.Length > 0
                && (image == exe || (home is not null && image.StartsWith(home + "\\", StringComparison.Ordinal) && !IsGameFolder(image)));
            if (launched || own)
            {
                matched[p.Pid] = p;
            }
        }

        // Descendants, to a fixed point. A parent pid is only a number: a child is never older than its parent.
        bool grew = matched.Count > 0;
        while (grew)
        {
            grew = false;
            foreach (AppProcess p in processes)
            {
                if (matched.ContainsKey(p.Pid) || !Eligible(p, excludedPids) || !matched.TryGetValue(p.ParentPid, out AppProcess? parent))
                {
                    continue;
                }

                if (p.StartedAt is { } child && parent.StartedAt is { } born && child < born)
                {
                    continue;
                }

                matched[p.Pid] = p;
                grew = true;
            }
        }

        return matched.Keys.Order().ToList();
    }

    /// <summary>
    /// The folder an app's executable owns (its directory, normalized), or <see langword="null"/> when that directory is
    /// shared by other programs: under the Windows directory, a drive root or a first-level folder, or a well-known
    /// shared folder (<see cref="SharedFolders"/>).
    /// </summary>
    public static string? OwnFolder(string exePath)
    {
        string exe = NormalizePath(exePath);
        int cut = exe.LastIndexOf('\\');
        if (cut <= 0)
        {
            return null;
        }

        string dir = exe[..cut];
        string[] parts = dir.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        // `c:` alone or `c:\tools`: a drive root or a first-level folder holds unrelated programs.
        if (parts.Length < 3 || SharedFolders.Contains(parts[^1]))
        {
            return null;
        }

        string windows = NormalizePath(Environment.GetEnvironmentVariable("SystemRoot") is { Length: > 0 } root ? root : @"C:\Windows");
        if (dir == windows || dir.StartsWith(windows + "\\", StringComparison.Ordinal) || parts[1] == "windows")
        {
            return null;
        }

        return dir;
    }

    /// <summary><c>C:/Program Files/X/</c> → <c>c:\program files\x</c>: one spelling for comparing Windows paths on any OS.</summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string p = path.Trim().Trim('"').Replace('/', '\\');
        while (p.Contains("\\\\", StringComparison.Ordinal) && !p.StartsWith("\\\\", StringComparison.Ordinal))
        {
            p = p.Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        return p.TrimEnd('\\').ToLowerInvariant();
    }

    private static bool Eligible(AppProcess p, IReadOnlySet<int> excludedPids)
    {
        if (p.Pid <= 4 || excludedPids.Contains(p.Pid) || p.Pid == Environment.ProcessId)
        {
            return false;
        }

        string name = FileName(NormalizePath(p.ImagePath));
        return !NeverApp.Contains(name) && !name.StartsWith("clubshell", StringComparison.Ordinal)
               && !ProcessKiller.SystemCritical.Contains(name);
    }

    /// <summary>A Steam library's game folder inside the Steam client's own: the game is not the client.</summary>
    private static bool IsGameFolder(string image) => image.Contains("\\steamapps\\", StringComparison.Ordinal);

    private static string FileName(string normalized)
    {
        int cut = normalized.LastIndexOf('\\');
        return cut < 0 ? normalized : normalized[(cut + 1)..];
    }
}

/// <summary>The processes of an interactive session, read from a Toolhelp snapshot (<see cref="IAppProcessSource"/>).</summary>
[SupportedOSPlatform("windows")]
public sealed class SessionProcessSource : IAppProcessSource
{
    /// <inheritdoc />
    public IReadOnlyList<AppProcess> Snapshot(int sessionId)
    {
        var list = new List<AppProcess>();
        foreach (ProcessSnapshotEntry entry in ProcessKiller.Snapshot())
        {
            if (entry.Pid <= 4 || !Kernel32.ProcessIdToSessionId((uint)entry.Pid, out uint session) || session != (uint)sessionId)
            {
                continue;
            }

            string? image;
            using (SafeProcessHandle handle = Kernel32.OpenProcess(NativeConst.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)entry.Pid))
            {
                image = handle.IsInvalid ? null : Kernel32.QueryFullProcessImageName(handle);
            }

            list.Add(new AppProcess(entry.Pid, entry.ParentPid, image ?? entry.ExeName, StartTime(entry.Pid)));
        }

        return list;
    }

    private static DateTimeOffset? StartTime(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}
