using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ClubShell.Windows.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace ClubShell.Windows.Users;

/// <summary>User shell folders that can be redirected.</summary>
public enum ShellFolder
{
    /// <summary>Desktop.</summary>
    Desktop,

    /// <summary>Documents (<c>Personal</c>).</summary>
    Documents,

    /// <summary>Downloads.</summary>
    Downloads,

    /// <summary>Pictures (<c>My Pictures</c>).</summary>
    Pictures,

    /// <summary>Videos (<c>My Video</c>).</summary>
    Videos,

    /// <summary>Saved Games.</summary>
    SavedGames,
}

/// <summary>What <see cref="FolderRedirect.Apply"/> changed.</summary>
/// <param name="UserSid">SID of the redirected user.</param>
/// <param name="HiveKey">HKEY_USERS sub-key the values were written under (SID or mount name).</param>
/// <param name="Targets">Folder → redirected directory.</param>
/// <param name="Snapshot">Registry state before the change (for <see cref="FolderRedirect.Revert"/>).</param>
public sealed record FolderRedirectResult(string UserSid, string HiveKey, IReadOnlyDictionary<ShellFolder, string> Targets, RegistrySnapshot Snapshot);

/// <summary>
/// Redirects a user's shell folders to another drive (e.g. <c>D:\Users\club</c>) by rewriting
/// <c>User Shell Folders</c> (and the cached <c>Shell Folders</c>) in the user's hive, creating the target
/// directories with an ACL of SYSTEM/Administrators/the user only. Works on a live hive (<c>HKU\&lt;sid&gt;</c>)
/// or, when the user is logged off, on NTUSER.DAT loaded through <see cref="RegistryHelper.LoadUserHive"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FolderRedirect
{
    /// <summary>Key with the REG_EXPAND_SZ folder definitions.</summary>
    public const string UserShellFoldersKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";

    /// <summary>Key with the cached absolute paths.</summary>
    public const string ShellFoldersKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders";

    private static readonly IReadOnlyDictionary<ShellFolder, (string ValueName, string DirName)> Map = new Dictionary<ShellFolder, (string, string)>
    {
        [ShellFolder.Desktop] = ("Desktop", "Desktop"),
        [ShellFolder.Documents] = ("Personal", "Documents"),
        [ShellFolder.Downloads] = ("{374DE290-123F-4565-9164-39C4925E467B}", "Downloads"),
        [ShellFolder.Pictures] = ("My Pictures", "Pictures"),
        [ShellFolder.Videos] = ("My Video", "Videos"),
        [ShellFolder.SavedGames] = ("{4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4}", "Saved Games"),
    };

    private readonly ILogger<FolderRedirect> _logger;

    /// <summary>Creates the helper.</summary>
    public FolderRedirect(ILogger<FolderRedirect>? logger = null) => _logger = logger ?? NullLogger<FolderRedirect>.Instance;

    /// <summary>All redirectable folders.</summary>
    public static IReadOnlyList<ShellFolder> AllFolders { get; } = Map.Keys.ToArray();

    /// <summary>Registry value name of a folder under <see cref="UserShellFoldersKey"/>.</summary>
    public static string ValueNameOf(ShellFolder folder) => Map[folder].ValueName;

    /// <summary>
    /// Redirects <paramref name="folders"/> (all by default) of user <paramref name="userSid"/> to
    /// <paramref name="targetBase"/>\&lt;folder&gt;. When <c>HKU\&lt;sid&gt;</c> is not loaded the hive at
    /// <paramref name="ntUserDatPath"/> is mounted for the duration of the call.
    /// </summary>
    public FolderRedirectResult Apply(string userSid, string targetBase, string? ntUserDatPath = null, IReadOnlyCollection<ShellFolder>? folders = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBase);
        IReadOnlyCollection<ShellFolder> selected = folders ?? AllFolders;
        var sid = new SecurityIdentifier(userSid);

        EnsureDirectory(targetBase, sid, protect: true);
        var targets = new Dictionary<ShellFolder, string>();
        foreach (ShellFolder folder in selected)
        {
            string path = Path.Combine(targetBase, Map[folder].DirName);
            EnsureDirectory(path, sid, protect: false);
            targets[folder] = path;
        }

        return WithHive(userSid, ntUserDatPath, hiveKey =>
        {
            RegistrySnapshot before = RegistryHelper.Snapshot(SnapshotTargets(hiveKey, selected));
            foreach ((ShellFolder folder, string path) in targets)
            {
                string name = Map[folder].ValueName;
                RegistryHelper.Set(RegistryHive.Users, RegistryHelper.UserHiveKey(hiveKey, UserShellFoldersKey), name, path, RegistryValueKind.ExpandString);
                RegistryHelper.Set(RegistryHive.Users, RegistryHelper.UserHiveKey(hiveKey, ShellFoldersKey), name, path, RegistryValueKind.String);
            }

            _logger.LogInformation("Redirected {Count} shell folders of {Sid} to {Base}", targets.Count, userSid, targetBase);
            return new FolderRedirectResult(userSid, hiveKey, targets, before);
        });
    }

    /// <summary>Restores the registry values captured by <see cref="Apply"/>; redirected directories are left in place.</summary>
    public void Revert(FolderRedirectResult applied, string? ntUserDatPath = null)
    {
        ArgumentNullException.ThrowIfNull(applied);
        _ = WithHive(applied.UserSid, ntUserDatPath, hiveKey =>
        {
            RegistrySnapshot snapshot = applied.Snapshot;
            if (!string.Equals(hiveKey, applied.HiveKey, StringComparison.OrdinalIgnoreCase))
            {
                // Hive mounted under a different name than at apply time: rebase the captured keys.
                snapshot = new RegistrySnapshot(
                    applied.Snapshot.Values.Select(v => v with { Key = hiveKey + v.Key[applied.HiveKey.Length..] }).ToList(),
                    applied.Snapshot.TakenAt);
            }

            RegistryHelper.Apply(snapshot);
            _logger.LogInformation("Reverted shell folder redirection for {Sid}", hiveKey);
            return true;
        });
    }

    /// <summary>Creates <paramref name="path"/> and grants the user full control; with <paramref name="protect"/> inheritance is cut so only SYSTEM, Administrators and the user have access.</summary>
    public static void EnsureDirectory(string path, SecurityIdentifier user, bool protect)
    {
        ArgumentNullException.ThrowIfNull(user);
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            directory.Create();
        }

        DirectorySecurity acl = directory.GetAccessControl();
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        if (protect)
        {
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
    }

    private static IEnumerable<(RegistryHive Hive, string Key, string Name)> SnapshotTargets(string hiveKey, IEnumerable<ShellFolder> folders)
    {
        foreach (ShellFolder folder in folders)
        {
            string name = Map[folder].ValueName;
            yield return (RegistryHive.Users, RegistryHelper.UserHiveKey(hiveKey, UserShellFoldersKey), name);
            yield return (RegistryHive.Users, RegistryHelper.UserHiveKey(hiveKey, ShellFoldersKey), name);
        }
    }

    /// <summary>Runs <paramref name="action"/> against the live hive, or against a temporarily mounted NTUSER.DAT.</summary>
    private static T WithHive<T>(string userSid, string? ntUserDatPath, Func<string, T> action)
    {
        if (RegistryHelper.Exists(RegistryHive.Users, userSid))
        {
            return action(userSid);
        }

        if (ntUserDatPath is null)
        {
            throw new InvalidOperationException($"HKEY_USERS\\{userSid} is not loaded and no NTUSER.DAT path was given.");
        }

        string mount = "ClubShell_" + userSid;
        using LoadedHive hive = RegistryHelper.LoadUserHive(mount, ntUserDatPath);
        return action(mount);
    }
}
