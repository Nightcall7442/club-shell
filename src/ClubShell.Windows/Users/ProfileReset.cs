using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using ClubShell.Windows.Native;
using ClubShell.Windows.Registry;
using ClubShell.Windows.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace ClubShell.Windows.Users;

/// <summary>Outcome of a profile reset or cache clean.</summary>
/// <param name="Deleted"><see langword="true"/> when the profile (or every cache directory) was removed.</param>
/// <param name="FreedBytes">Bytes that were on disk before deletion.</param>
/// <param name="Errors">Non-fatal problems encountered (paths that could not be removed, ...).</param>
public sealed record ProfileResetResult(bool Deleted, long FreedBytes, IReadOnlyList<string> Errors);

/// <summary>
/// Deletes the kiosk user's profile between sessions (ARCHITECTURE.md §6.1 step 9, <c>resetProfileOnLogout</c>):
/// logs the user off, then tries DeleteProfileW, WMI <c>Win32_UserProfile.Delete</c> and finally a manual
/// directory + ProfileList removal. <see cref="PreservedDirectories"/> are moved aside first and put back by
/// <see cref="RestorePreserved"/> after the next logon. <see cref="CleanCaches"/> is the light variant that only
/// empties caches.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProfileReset
{
    private const int DeleteAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan UnloadTimeout = TimeSpan.FromSeconds(20);
    private readonly ILogger<ProfileReset> _logger;
    private readonly string _markerPath;
    private readonly string _stashRoot;

    /// <summary>Creates the helper; <paramref name="markerPath"/> defaults to <c>%ProgramData%\ClubShell\cache\profile-reset.marker</c>.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="markerPath">Where the time of the last reset is remembered.</param>
    /// <param name="stashRoot">Where <see cref="PreservedDirectories"/> wait between the deletion and the next logon (default: next to the marker).</param>
    public ProfileReset(ILogger<ProfileReset>? logger = null, string? markerPath = null, string? stashRoot = null)
    {
        _logger = logger ?? NullLogger<ProfileReset>.Instance;
        _markerPath = markerPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClubShell", "cache", "profile-reset.marker");
        _stashRoot = stashRoot ?? Path.Combine(Path.GetDirectoryName(_markerPath) ?? ".", "profile-keep");
    }

    /// <summary>
    /// Profile-relative directories carried across <see cref="ResetProfile(string)"/> by default: the anti-cheat
    /// vendors that bootstrap per machine. Deleting these every session is not a clean slate, it is a fresh install
    /// of FACEIT and Vanguard before every match.
    /// </summary>
    public static IReadOnlyList<string> PreservedDirectories { get; } = new[]
    {
        @"AppData\Local\Riot Games",
        @"AppData\Roaming\Riot Games",
        @"AppData\Local\FACEIT",
        @"AppData\Local\FACEIT AC",
        @"AppData\Roaming\FACEIT",
        @"AppData\Roaming\EasyAntiCheat",
        @"AppData\Local\EasyAntiCheat",
        @"AppData\Roaming\BattlEye",
    };

    /// <summary>Profile-relative directories emptied by <see cref="CleanCaches"/>.</summary>
    public static IReadOnlyList<string> CacheDirectories { get; } = new[]
    {
        @"AppData\Local\Temp",
        @"AppData\Local\Microsoft\Windows\INetCache",
        @"AppData\Local\Microsoft\Windows\WebCache",
        @"AppData\Local\Microsoft\Windows\Explorer",
        @"AppData\Local\Microsoft\Windows\WER",
        @"AppData\Local\CrashDumps",
        @"AppData\Local\D3DSCache",
        @"AppData\LocalLow\Temp",
        @"Downloads",
    };

    /// <summary>Time of the last <see cref="TouchLastReset"/>, or <see langword="null"/>.</summary>
    public DateTimeOffset? LastReset
    {
        get
        {
            try
            {
                return File.Exists(_markerPath) && DateTimeOffset.TryParse(File.ReadAllText(_markerPath).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset at)
                    ? at
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Writes the reset marker with the current UTC time.</summary>
    public void TouchLastReset()
    {
        string? directory = Path.GetDirectoryName(_markerPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_markerPath, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>Logs <paramref name="userName"/> off everywhere and deletes its profile directory and registry entry.</summary>
    public ProfileResetResult ResetProfile(string userName) => ResetProfile(userName, PreservedDirectories);

    /// <summary>
    /// Logs <paramref name="userName"/> off everywhere and deletes its profile directory and registry entry.
    /// <paramref name="preserve"/> (profile-relative directories) is moved aside first and put back by
    /// <see cref="RestorePreserved"/> once the profile has been recreated at the next logon.
    /// </summary>
    public ProfileResetResult ResetProfile(string userName, IReadOnlyList<string> preserve)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(preserve);
        var errors = new List<string>();
        string sid = Advapi32.LookupAccountSid(userName) ?? throw new InvalidOperationException($"Account {userName} not found.");

        LogoffEverywhere(userName, errors);
        WaitForHiveUnload(sid);

        string? profilePath = RegistryHelper.GetProfileImagePath(sid);
        Stash(sid, profilePath, preserve, errors);
        long size = profilePath is not null ? DirectorySize(profilePath) : 0;
        bool deleted = false;

        if (profilePath is null && !RegistryHelper.Exists(RegistryHive.LocalMachine, RegistryHelper.ProfileListKey + "\\" + sid))
        {
            _logger.LogInformation("No profile registered for {User} ({Sid}); nothing to reset", userName, sid);
        }
        else
        {
            deleted = Retry(() => DeleteWithApi(sid), "DeleteProfileW", errors)
                || Retry(() => DeleteWithWmi(sid), "Win32_UserProfile.Delete", errors)
                || DeleteManually(sid, profilePath, errors);
        }

        if (deleted)
        {
            _logger.LogInformation("Profile of {User} ({Sid}) deleted, {Bytes} bytes freed", userName, sid, size);
        }
        else
        {
            _logger.LogWarning("Profile of {User} ({Sid}) could not be deleted: {Errors}", userName, sid, string.Join("; ", errors));
        }

        TouchLastReset();
        return new ProfileResetResult(deleted, deleted ? size : 0, errors);
    }

    /// <summary>Empties <see cref="CacheDirectories"/> inside the profile without deleting it (user may stay logged on; locked files are skipped).</summary>
    public ProfileResetResult CleanCaches(string userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        var errors = new List<string>();
        string sid = Advapi32.LookupAccountSid(userName) ?? throw new InvalidOperationException($"Account {userName} not found.");
        string? profilePath = RegistryHelper.GetProfileImagePath(sid);
        if (profilePath is null || !Directory.Exists(profilePath))
        {
            return new ProfileResetResult(false, 0, errors);
        }

        long freed = 0;
        bool all = true;
        foreach (string relative in CacheDirectories)
        {
            string path = Path.Combine(profilePath, relative);
            if (!Directory.Exists(path))
            {
                continue;
            }

            long before = DirectorySize(path);
            all &= DeleteContents(path, errors);
            freed += before - DirectorySize(path);
        }

        _logger.LogInformation("Cleaned caches of {User}: {Bytes} bytes freed, {Errors} errors", userName, freed, errors.Count);
        return new ProfileResetResult(all, freed, errors);
    }

    /// <summary>
    /// Moves the directories stashed by the last <see cref="ResetProfile(string, IReadOnlyList{string})"/> back into
    /// the recreated profile and returns how many were restored, or <see langword="null"/> when the profile does not
    /// exist yet — the stash is kept and the call can be repeated after the next logon.
    /// </summary>
    public int? RestorePreserved(string userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        string? sid = Advapi32.LookupAccountSid(userName);
        string stash = sid is null ? string.Empty : Path.Combine(_stashRoot, sid);
        if (sid is null || !Directory.Exists(stash))
        {
            return 0;
        }

        string? profilePath = RegistryHelper.GetProfileImagePath(sid);
        if (profilePath is null || !Directory.Exists(profilePath))
        {
            _logger.LogInformation("Restore of preserved directories deferred: profile of {User} does not exist yet", userName);
            return null;
        }

        int restored = 0;
        foreach (string source in Directory.GetDirectories(stash))
        {
            string relative = Unescape(Path.GetFileName(source));
            string target = Path.Combine(profilePath, relative);
            try
            {
                if (Directory.Exists(target))
                {
                    // The profile was used before the restore ran; the freshly bootstrapped copy wins.
                    Directory.Delete(source, recursive: true);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.Move(source, target);
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Preserved directory {Relative} could not be restored into {Profile}", relative, profilePath);
            }
        }

        TryDeleteEmpty(stash);
        if (restored > 0)
        {
            _logger.LogInformation("{Count} preserved directory/-ies restored into the profile of {User}", restored, userName);
        }

        return restored;
    }

    /// <summary>Total size of all files under <paramref name="path"/>; unreadable entries are skipped.</summary>
    public static long DirectorySize(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;
        var options = new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try
        {
            foreach (FileInfo file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                total += file.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // partial size is good enough for reporting
        }

        return total;
    }

    // A stashed directory keeps its profile-relative path as its own name; '\' is not legal in one, so it is escaped.
    private static string Escape(string relative) => relative.Replace("\\", "%5C", StringComparison.Ordinal);

    private static string Unescape(string name) => name.Replace("%5C", "\\", StringComparison.Ordinal);

    /// <summary>Moves <paramref name="preserve"/> out of the profile before it is deleted; a stash left over from a failed restore is replaced.</summary>
    private void Stash(string sid, string? profilePath, IReadOnlyList<string> preserve, List<string> errors)
    {
        if (profilePath is null || preserve.Count == 0 || !Directory.Exists(profilePath))
        {
            return;
        }

        string stash = Path.Combine(_stashRoot, sid);
        int moved = 0;
        foreach (string relative in preserve)
        {
            string source = Path.Combine(profilePath, relative);
            if (!Directory.Exists(source))
            {
                continue;
            }

            string target = Path.Combine(stash, Escape(relative));
            try
            {
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }

                Directory.CreateDirectory(stash);
                Directory.Move(source, target);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"preserve {relative}: {ex.Message}");
                _logger.LogWarning(ex, "Preserved directory {Relative} could not be moved out of {Profile}", relative, profilePath);
            }
        }

        if (moved > 0)
        {
            _logger.LogInformation("{Count} directory/-ies moved to {Stash} to survive the reset", moved, stash);
        }
    }

    private void TryDeleteEmpty(string path)
    {
        try
        {
            if (Directory.GetFileSystemEntries(path).Length == 0)
            {
                Directory.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Stash directory {Path} could not be removed", path);
        }
    }

    private void LogoffEverywhere(string userName, List<string> errors)
    {
        foreach (WtsSession session in WtsSessions.Enumerate())
        {
            if (!session.HasUser || !MatchesUser(session, userName))
            {
                continue;
            }

            try
            {
                _logger.LogInformation("Logging off session {SessionId} of {User}", session.Id, userName);
                WtsSessions.Logoff(session.Id, wait: true);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                errors.Add($"Logoff session {session.Id}: {ex.Message}");
            }
        }
    }

    private static bool MatchesUser(WtsSession session, string userName)
    {
        int slash = userName.IndexOf('\\');
        string name = slash >= 0 ? userName[(slash + 1)..] : userName;
        return string.Equals(session.UserName, name, StringComparison.OrdinalIgnoreCase);
    }

    private void WaitForHiveUnload(string sid)
    {
        long deadline = Environment.TickCount64 + (long)UnloadTimeout.TotalMilliseconds;
        while (RegistryHelper.Exists(RegistryHive.Users, sid) && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(500);
        }

        if (RegistryHelper.Exists(RegistryHive.Users, sid))
        {
            _logger.LogWarning("Hive HKU\\{Sid} is still loaded after {Timeout}; deletion may fail", sid, UnloadTimeout);
        }
    }

    private bool Retry(Func<bool> attempt, string what, List<string> errors)
    {
        for (int i = 1; i <= DeleteAttempts; i++)
        {
            try
            {
                if (attempt())
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ManagementException or UnauthorizedAccessException or IOException or System.Runtime.InteropServices.COMException)
            {
                errors.Add($"{what} attempt {i}: {ex.Message}");
                _logger.LogDebug(ex, "{What} attempt {Attempt} failed", what, i);
            }

            if (i < DeleteAttempts)
            {
                Thread.Sleep(RetryDelay);
            }
        }

        return false;
    }

    private static bool DeleteWithApi(string sid)
    {
        if (Userenv.DeleteProfileW(sid, null, null))
        {
            return true;
        }

        int error = Win32Error.Last();
        if (error == NativeConst.ERROR_FILE_NOT_FOUND || error == NativeConst.ERROR_PATH_NOT_FOUND)
        {
            return false;
        }

        Win32Error.Throw(error, nameof(Userenv.DeleteProfileW));
        return false;
    }

    private static bool DeleteWithWmi(string sid)
    {
        using var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_UserProfile WHERE SID = '{sid}'");
        using ManagementObjectCollection profiles = searcher.Get();
        bool any = false;
        foreach (ManagementBaseObject item in profiles)
        {
            using (item)
            {
                if (item is ManagementObject profile)
                {
                    _ = profile.InvokeMethod("Delete", null!);
                    any = true;
                }
            }
        }

        return any;
    }

    private bool DeleteManually(string sid, string? profilePath, List<string> errors)
    {
        bool ok = true;
        if (profilePath is not null && Directory.Exists(profilePath))
        {
            ok = DeleteContents(profilePath, errors);
            if (ok)
            {
                try
                {
                    Directory.Delete(profilePath, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{profilePath}: {ex.Message}");
                    ok = false;
                }
            }
        }

        foreach (string key in new[] { RegistryHelper.ProfileListKey + "\\" + sid, RegistryHelper.ProfileListKey + "\\" + sid + ".bak" })
        {
            try
            {
                _ = RegistryHelper.DeleteKey(RegistryHive.LocalMachine, key, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                errors.Add($"HKLM\\{key}: {ex.Message}");
                ok = false;
            }
        }

        _logger.LogInformation("Manual profile removal for {Sid}: ok={Ok}", sid, ok);
        return ok;
    }

    /// <summary>Deletes everything inside <paramref name="path"/> (not the directory itself); returns <see langword="false"/> when something remained.</summary>
    private static bool DeleteContents(string path, List<string> errors)
    {
        bool ok = true;
        var options = new System.IO.EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true };
        foreach (FileSystemInfo entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            try
            {
                if (entry is DirectoryInfo dir)
                {
                    if ((dir.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        ok &= DeleteContents(dir.FullName, errors);
                    }

                    dir.Attributes = FileAttributes.Normal;
                    dir.Delete(recursive: false);
                }
                else
                {
                    entry.Attributes = FileAttributes.Normal;
                    entry.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{entry.FullName}: {ex.Message}");
                ok = false;
            }
        }

        return ok;
    }
}
