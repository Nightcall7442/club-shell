using System.Globalization;
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
/// logs the user off, moves the profile directory aside and removes its <c>ProfileList</c> entry the moment the hive is
/// unloaded, then deletes the moved directory. Winlogon logs the kiosk on again right after the sign-out
/// (<c>ForceAutoLogon</c>, SHELL_CHANGES 76), so nothing slow may run between the unload and that logon: the new logon
/// must find no profile and get a fresh one. <see cref="PreservedDirectories"/> are taken from the moved directory and
/// put back by <see cref="RestorePreserved"/> after the next logon. <see cref="CleanCaches"/> is the light variant that
/// only empties caches.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProfileReset
{
    /// <summary>Suffix of a profile directory moved aside by a reset, before the time stamp (<c>C:\Users\player.reset-…</c>).</summary>
    public const string TombstoneSuffix = ".reset-";

    private const string ProfileGuidKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileGuid";
    private static readonly TimeSpan UnloadTimeout = TimeSpan.FromSeconds(20);

    // Fine-grained: the hive is unloaded only between the sign-out and the auto-logon that follows it.
    private static readonly TimeSpan UnloadPoll = TimeSpan.FromMilliseconds(50);
    private readonly ILogger<ProfileReset> _logger;
    private readonly string _markerPath;
    private readonly string _dirtyPath;
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
        _dirtyPath = Path.Combine(Path.GetDirectoryName(_markerPath) ?? ".", "profile-dirty.marker");
    }

    /// <summary>
    /// Profile-relative directories carried across <see cref="ResetProfile(string)"/> by default: the anti-cheat
    /// vendors that bootstrap per machine, and the graphics shader caches. Deleting the first every session is not a
    /// clean slate, it is a fresh install of FACEIT and Vanguard before every match; deleting the second makes every
    /// game recompile its shaders and stutter through its first minutes. Neither holds anything of the player.
    /// </summary>
    public static IReadOnlyList<string> PreservedDirectories { get; } = new[]
    {
        @"AppData\Local\D3DSCache",
        @"AppData\Local\NVIDIA\DXCache",
        @"AppData\Local\NVIDIA\GLCache",
        @"AppData\Local\AMD\DxCache",
        @"AppData\Local\AMD\DxcCache",
        @"AppData\Local\AMD\VkCache",
        @"AppData\Local\Intel\ShaderCache",
        @"AppData\Local\Riot Games",
        @"AppData\Roaming\Riot Games",
        @"AppData\Local\FACEIT",
        @"AppData\Local\FACEIT AC",
        @"AppData\Roaming\FACEIT",
        @"AppData\Roaming\EasyAntiCheat",
        @"AppData\Local\EasyAntiCheat",
        @"AppData\Roaming\BattlEye",
    };

    /// <summary>
    /// Profile-relative files and directories inside <see cref="PreservedDirectories"/> that hold a player's sign-in, removed
    /// from whatever is carried across a reset (SHELL_CHANGES 42): the Riot Client's "Stay signed in" session and the
    /// FACEIT client's cookies and web storage (an Electron profile). A preserved entry inside one of them is not carried.
    /// </summary>
    public static IReadOnlyList<string> SignInPaths { get; } = new[]
    {
        @"AppData\Local\Riot Games\Riot Client\Data\RiotGamesPrivateSettings.yaml",
        @"AppData\Local\Riot Games\Riot Client\Data\RiotClientPrivateSettings.yaml",
        @"AppData\Roaming\FACEIT\Cookies",
        @"AppData\Roaming\FACEIT\Network",
        @"AppData\Roaming\FACEIT\Local Storage",
        @"AppData\Roaming\FACEIT\Session Storage",
        @"AppData\Roaming\FACEIT\IndexedDB",
    };

    /// <summary>
    /// Deletes the <see cref="SignInPaths"/> under <paramref name="directory"/>, the copy of the profile-relative
    /// <paramref name="relative"/>; returns how many were there. <see langword="false"/> from <paramref name="carried"/>
    /// when <paramref name="relative"/> is itself a sign-in path or inside one.
    /// </summary>
    public static int RemoveSignIns(string directory, string relative, out bool carried)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentException.ThrowIfNullOrEmpty(relative);
        string prefix = relative.TrimEnd('\\') + "\\";
        carried = true;
        int removed = 0;
        foreach (string signIn in SignInPaths)
        {
            // The preserved entry is the sign-in path or lies inside it.
            if (prefix.StartsWith(signIn + "\\", StringComparison.OrdinalIgnoreCase))
            {
                carried = false;
                continue;
            }

            if (!signIn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string path = Path.Combine([directory, .. signIn[prefix.Length..].Split('\\')]);
            if (File.Exists(path))
            {
                File.Delete(path);
                removed++;
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Profile-relative directories emptied by <see cref="CleanCaches"/> (shader caches stay: see <see cref="PreservedDirectories"/>).</summary>
    public static IReadOnlyList<string> CacheDirectories { get; } = new[]
    {
        @"AppData\Local\Temp",
        @"AppData\Local\Microsoft\Windows\INetCache",
        @"AppData\Local\Microsoft\Windows\WebCache",
        @"AppData\Local\Microsoft\Windows\Explorer",
        @"AppData\Local\Microsoft\Windows\WER",
        @"AppData\Local\CrashDumps",
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

    /// <summary>
    /// User whose profile is owed a reset, or <see langword="null"/> when nothing is outstanding. Set from the moment
    /// a session opens and cleared only once a reset has actually run, so it survives a power cut: the marker is what
    /// tells the next Agent start that the previous player's profile is still on disk.
    /// </summary>
    public string? DirtyUser
    {
        get
        {
            try
            {
                if (!File.Exists(_dirtyPath))
                {
                    return null;
                }

                string user = File.ReadAllText(_dirtyPath).Trim();
                return user.Length == 0 ? null : user;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Records that the profile of <paramref name="userName"/> is in use and owes a reset.</summary>
    public void MarkDirty(string userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        if (string.Equals(DirtyUser, userName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? directory = Path.GetDirectoryName(_dirtyPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_dirtyPath, userName);
        _logger.LogDebug("Profile of {User} marked as owing a reset", userName);
    }

    /// <summary>Drops the marker written by <see cref="MarkDirty"/>; call only after a reset has run.</summary>
    public void ClearDirty()
    {
        try
        {
            File.Delete(_dirtyPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Dirty-profile marker {Path} could not be removed; the next start will reset again", _dirtyPath);
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
    /// Logs <paramref name="userName"/> off and resets its profile: the sessions in <paramref name="sessions"/> (all of the
    /// user's sessions now when <see langword="null"/>) are logged off once each — a session Winlogon logs on after that is
    /// the next player's and is left alone. As soon as the hive is unloaded the profile directory is renamed
    /// (<see cref="TombstoneSuffix"/>) and its <c>ProfileList</c> entry removed, so the next logon creates a fresh profile
    /// under the same name; only then are <paramref name="preserve"/> (profile-relative directories, put back by
    /// <see cref="RestorePreserved"/> after the next logon) carried out of the moved directory and the rest deleted. When
    /// the hive is loaded again before that, or the directory cannot be moved, the new logon has won: nothing is deleted
    /// under it and the result is not <see cref="ProfileResetResult.Deleted"/>, so the debt stays for the next idle moment.
    /// </summary>
    public ProfileResetResult ResetProfile(string userName, IReadOnlyList<string> preserve, IReadOnlyCollection<uint>? sessions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(preserve);
        var errors = new List<string>();
        string sid = Advapi32.LookupAccountSid(userName) ?? throw new InvalidOperationException($"Account {userName} not found.");

        Logoff(userName, sessions, errors);
        if (!WaitForHiveUnload(sid))
        {
            errors.Add("the profile is in use: the kiosk logged on again before it could be reset");
            _logger.LogWarning("Profile of {User} ({Sid}) is loaded again; left for the next idle moment", userName, sid);
            return new ProfileResetResult(false, 0, errors);
        }

        string? profilePath = RegistryHelper.GetProfileImagePath(sid) is { Length: > 0 } image ? Path.TrimEndingDirectorySeparator(image) : null;
        string listKey = RegistryHelper.ProfileListKey + "\\" + sid;
        if (profilePath is null && !RegistryHelper.Exists(RegistryHive.LocalMachine, listKey))
        {
            _logger.LogInformation("No profile registered for {User} ({Sid}); nothing to reset", userName, sid);
            DeleteTombstones(ProfilesRoot(), AccountName(userName), errors);
            TouchLastReset();
            return new ProfileResetResult(false, 0, errors);
        }

        string? tombstone = null;
        if (profilePath is not null && Directory.Exists(profilePath))
        {
            tombstone = profilePath + TombstoneSuffix + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            try
            {
                Directory.Move(profilePath, tombstone);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{profilePath}: {ex.Message}");
                _logger.LogWarning(ex, "Profile of {User} could not be moved aside (in use); left for the next idle moment", userName);
                return new ProfileResetResult(false, 0, errors);
            }
        }

        if (!RemoveProfileEntry(sid, errors))
        {
            // The entry would point at a directory that is gone, and the next logon would get a TEMP profile: put it back.
            if (tombstone is not null)
            {
                try
                {
                    Directory.Move(tombstone, profilePath!);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{tombstone}: {ex.Message}");
                }
            }

            _logger.LogWarning("Profile entry of {User} ({Sid}) could not be removed: {Errors}", userName, sid, string.Join("; ", errors));
            return new ProfileResetResult(false, 0, errors);
        }

        // Out of the way: a logon from now on gets a fresh profile. Nothing below races it.
        long size = 0;
        if (tombstone is not null)
        {
            Stash(sid, tombstone, preserve, errors);
            size = DirectorySize(tombstone);
            // This tombstone and any an earlier reset left behind.
            if (DeleteTombstones(Path.GetDirectoryName(profilePath), Path.GetFileName(profilePath)!, errors) > 0)
            {
                _logger.LogWarning("Old profile directories of {User} not fully deleted; the next reset tries again", userName);
            }
        }

        _logger.LogInformation("Profile of {User} ({Sid}) deleted, {Bytes} bytes freed", userName, sid, size);
        TouchLastReset();
        return new ProfileResetResult(true, size, errors);
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

                // Again here: a stash left by an agent before SHELL_CHANGES 42 still holds the sign-ins.
                if (RemoveSignIns(source, relative, out bool carried) > 0 || !carried)
                {
                    _logger.LogInformation("Sign-in data removed from the preserved {Relative}", relative);
                }

                if (!carried)
                {
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

                if (RemoveSignIns(source, relative, out bool carried) > 0)
                {
                    _logger.LogInformation("Sign-in data removed from {Relative} before it is carried across the reset", relative);
                }

                if (!carried)
                {
                    _logger.LogWarning("{Relative} holds a player's sign-in and is not carried across the reset", relative);
                    continue;
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

    /// <summary>The ids of <paramref name="userName"/>'s sessions now.</summary>
    public static IReadOnlyCollection<uint> SessionsOf(string userName) =>
        [.. WtsSessions.Enumerate().Where(s => s.HasUser && MatchesUser(s, userName)).Select(s => s.Id)];

    /// <summary>Logs each of <paramref name="sessions"/> off once, if it still belongs to <paramref name="userName"/>.</summary>
    private void Logoff(string userName, IReadOnlyCollection<uint>? sessions, List<string> errors)
    {
        foreach (uint id in sessions ?? SessionsOf(userName))
        {
            if (WtsSessions.Get(id) is not { HasUser: true } session || !MatchesUser(session, userName))
            {
                continue;
            }

            try
            {
                _logger.LogInformation("Logging off session {SessionId} of {User}", id, userName);
                WtsSessions.Logoff(id, wait: true);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                errors.Add($"Logoff session {id}: {ex.Message}");
            }
        }
    }

    private static bool MatchesUser(WtsSession session, string userName) =>
        string.Equals(session.UserName, AccountName(userName), StringComparison.OrdinalIgnoreCase);

    /// <summary><c>club</c> for <c>PC\club</c>.</summary>
    private static string AccountName(string userName)
    {
        int slash = userName.IndexOf('\\');
        return slash >= 0 ? userName[(slash + 1)..] : userName;
    }

    /// <summary><see langword="true"/> once HKU\&lt;sid&gt; is unloaded; <see langword="false"/> when it stays loaded for <see cref="UnloadTimeout"/>.</summary>
    private bool WaitForHiveUnload(string sid)
    {
        long deadline = Environment.TickCount64 + (long)UnloadTimeout.TotalMilliseconds;
        while (RegistryHelper.Exists(RegistryHive.Users, sid))
        {
            if (Environment.TickCount64 >= deadline)
            {
                _logger.LogWarning("Hive HKU\\{Sid} is still loaded after {Timeout}", sid, UnloadTimeout);
                return false;
            }

            Thread.Sleep(UnloadPoll);
        }

        return true;
    }

    /// <summary>Where Windows creates profiles (<c>ProfileList\ProfilesDirectory</c>, normally <c>C:\Users</c>).</summary>
    private static string? ProfilesRoot() =>
        RegistryHelper.Get<string>(RegistryHive.LocalMachine, RegistryHelper.ProfileListKey, "ProfilesDirectory") is { Length: > 0 } root
            ? Environment.ExpandEnvironmentVariables(root)
            : null;

    /// <summary>Removes <c>ProfileList\&lt;sid&gt;</c>, its <c>.bak</c> and the <c>ProfileGuid</c> entry it names.</summary>
    private bool RemoveProfileEntry(string sid, List<string> errors)
    {
        bool ok = true;
        string listKey = RegistryHelper.ProfileListKey + "\\" + sid;
        string? guid = RegistryHelper.Get<string>(RegistryHive.LocalMachine, listKey, "Guid");
        foreach (string key in new[] { listKey, listKey + ".bak" })
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

        if (ok && guid is { Length: > 0 })
        {
            try
            {
                _ = RegistryHelper.DeleteKey(RegistryHive.LocalMachine, ProfileGuidKey + "\\" + guid, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Only a stale index entry; the next logon does not read it.
                _logger.LogDebug(ex, "ProfileGuid {Guid} could not be removed", guid);
            }
        }

        return ok;
    }

    /// <summary>
    /// Deletes the directories in <paramref name="root"/> that resets moved the profile directory
    /// <paramref name="profileName"/> to (<c>club.reset-…</c>) and could not delete then (locked files). Returns how many
    /// remain; what stayed is reported in <paramref name="errors"/>.
    /// </summary>
    public static int DeleteTombstones(string? root, string profileName, List<string> errors)
    {
        ArgumentException.ThrowIfNullOrEmpty(profileName);
        ArgumentNullException.ThrowIfNull(errors);
        if (root is null || !Directory.Exists(root))
        {
            return 0;
        }

        int left = 0;
        foreach (string dir in Directory.EnumerateDirectories(root, profileName + TombstoneSuffix + "*"))
        {
            if (!DeleteTree(dir, errors))
            {
                left++;
            }
        }

        return left;
    }

    /// <summary>Deletes <paramref name="path"/> and everything in it; what stays is reported in <paramref name="errors"/>.</summary>
    private static bool DeleteTree(string path, List<string> errors)
    {
        if (!DeleteContents(path, errors))
        {
            return false;
        }

        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{path}: {ex.Message}");
            return false;
        }
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
