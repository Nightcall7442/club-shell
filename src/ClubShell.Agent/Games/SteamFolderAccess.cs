using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ClubShell.Contracts.Games;

namespace ClubShell.Agent.Games;

/// <summary>
/// Steam runs as the player and needs its own folder writable: it updates itself and keeps its state there. Its
/// installer grants that to BUILTIN\Users; a Steam folder copied onto the PC or installed otherwise does not, and Steam
/// then stops at "The Steam install folder is currently not writable", whose "Repair" needs an administrator the kiosk
/// does not have. The Agent (LocalSystem) grants it instead, as the installer would: Modify for BUILTIN\Users on the
/// folder, inherited by everything in it. Checked at start and before Steam is started; the grant itself (Windows walks
/// the whole folder) runs once in the background.
/// <para>
/// The grant runs as LocalSystem on a path, so it is given only where no player could have steered it: the Steam client
/// folder the administrator set (agent.json, the installer's registry entry, Program Files) — never a catalogue path —
/// that is a real Steam installation on a local disk outside Windows, with no junction or symlink on the way, owned by
/// Administrators, SYSTEM or TrustedInstaller all the way up, none of which a player may delete or rename (so it cannot
/// be swapped between the check and the grant). A folder the players can write already needs nothing.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SteamFolderAccess
{
    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>Groups every player's token carries.</summary>
    private static readonly SecurityIdentifier[] Players =
    [
        Users,
        new(WellKnownSidType.AuthenticatedUserSid, null),
        new(WellKnownSidType.WorldSid, null),
        new(WellKnownSidType.InteractiveSid, null),
    ];

    /// <summary>Owners a player cannot be.</summary>
    private static readonly SecurityIdentifier[] TrustedOwners =
    [
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new(WellKnownSidType.LocalSystemSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // NT SERVICE\TrustedInstaller
    ];

    private readonly GameDetector _detector;
    private readonly ILogger<SteamFolderAccess> _logger;
    private readonly object _gate = new();
    private Task _running = Task.CompletedTask;

    /// <summary>Creates the helper.</summary>
    public SteamFolderAccess(GameDetector detector, ILogger<SteamFolderAccess> logger)
    {
        _detector = detector;
        _logger = logger;
    }

    /// <summary>Whether <paramref name="exe"/> is the Steam client (<c>steam.exe</c>).</summary>
    public static bool IsSteam(string? exe) => string.Equals(Path.GetFileName(exe), "steam.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Makes the Steam client's folder writable for the players in the background, unless a pass is running already.</summary>
    public Task EnsureInBackground()
    {
        lock (_gate)
        {
            if (_running.IsCompleted)
            {
                _running = Task.Run(Ensure);
            }

            return _running;
        }
    }

    /// <summary>Whether <paramref name="folder"/> holds a Steam installation: <c>steam.exe</c> and <c>steamclient.dll</c>.</summary>
    public static bool IsSteamInstallation(DirectoryInfo folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return File.Exists(Path.Combine(folder.FullName, "steam.exe")) && File.Exists(Path.Combine(folder.FullName, "steamclient.dll"));
    }

    /// <summary>Whether <paramref name="folder"/> is on a network share (a UNC path or a mapped network drive).</summary>
    public static bool IsOnNetwork(DirectoryInfo folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        string full = folder.FullName;
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            return Path.GetPathRoot(full) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Whether the players may already modify <paramref name="folder"/> and everything in it.</summary>
    public static bool IsWritableByPlayers(DirectoryInfo folder) =>
        (PlayerRights(folder, inheritableOnly: true) & FileSystemRights.Modify) == FileSystemRights.Modify;

    /// <summary>
    /// Whether a grant on <paramref name="folder"/> by name lands on that folder and nowhere else; <paramref name="reason"/>
    /// says why not. Not a drive root, not inside Windows; neither it nor any folder above it a junction or symlink, owned
    /// by anyone but Administrators, SYSTEM or TrustedInstaller, or deletable by a player (itself, or through its parent).
    /// </summary>
    public static bool IsSafeToGrant(DirectoryInfo folder, out string reason)
    {
        ArgumentNullException.ThrowIfNull(folder);
        string full = Path.TrimEndingDirectorySeparator(folder.FullName);
        string windows = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (folder.Parent is null)
        {
            reason = "a drive root";
            return false;
        }

        if (string.IsNullOrEmpty(windows) || full.Equals(windows, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            reason = "inside Windows";
            return false;
        }

        for (DirectoryInfo? d = folder; d is not null; d = d.Parent)
        {
            d.Refresh();
            if ((d.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                reason = $"{d.FullName} is a junction or symlink";
                return false;
            }

            if (d.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
                || !TrustedOwners.Contains(owner))
            {
                reason = $"{d.FullName} is not owned by Administrators, SYSTEM or TrustedInstaller";
                return false;
            }

            if ((PlayerRights(d, inheritableOnly: false) & FileSystemRights.Delete) != 0
                || (d.Parent is { } parent && (PlayerRights(parent, inheritableOnly: false) & FileSystemRights.DeleteSubdirectoriesAndFiles) != 0))
            {
                reason = $"players may delete or rename {d.FullName}";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>Grants BUILTIN\Users Modify on <paramref name="folder"/>, inherited by its contents; <see langword="false"/> when the players may write it already.</summary>
    public static bool Grant(DirectoryInfo folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (IsWritableByPlayers(folder))
        {
            return false;
        }

        DirectorySecurity acl = folder.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Modify, Inherit, PropagationFlags.None, AccessControlType.Allow));
        folder.SetAccessControl(acl);
        return true;
    }

    /// <summary>
    /// What the players' groups may do on <paramref name="folder"/> itself (allowed minus denied); with
    /// <paramref name="inheritableOnly"/>, only rights its contents inherit as well.
    /// </summary>
    private static FileSystemRights PlayerRights(DirectoryInfo folder, bool inheritableOnly)
    {
        FileSystemRights allowed = 0;
        FileSystemRights denied = 0;
        foreach (FileSystemAccessRule rule in folder.GetAccessControl(AccessControlSections.Access).GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference is not SecurityIdentifier sid || !Players.Contains(sid) || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
            {
                continue;
            }

            if (inheritableOnly && ((rule.InheritanceFlags & Inherit) != Inherit || (rule.PropagationFlags & PropagationFlags.NoPropagateInherit) != 0))
            {
                continue;
            }

            if (rule.AccessControlType == AccessControlType.Allow)
            {
                allowed |= rule.FileSystemRights;
            }
            else
            {
                denied |= rule.FileSystemRights;
            }
        }

        return allowed & ~denied;
    }

    private void Ensure()
    {
        if (Path.GetDirectoryName(_detector.ResolveLauncherExe(LauncherType.Steam)) is not { Length: > 0 } path)
        {
            return;
        }

        var folder = new DirectoryInfo(path);
        try
        {
            if (!folder.Exists || !IsSteamInstallation(folder))
            {
                _logger.LogWarning("{Folder} is not a Steam installation; its rights are left alone", path);
                return;
            }

            if (IsOnNetwork(folder))
            {
                // Its rights belong to the server, and one Steam folder cannot serve several PCs at once anyway.
                _logger.LogWarning("Steam at {Folder} is on a network drive: Steam must be installed on each PC's own disk", path);
                return;
            }

            if (IsWritableByPlayers(folder))
            {
                _logger.LogDebug("Steam folder {Folder} is writable for the players", path);
                return;
            }

            if (!IsSafeToGrant(folder, out string reason))
            {
                _logger.LogWarning("Steam folder {Folder} is not writable for the players and is left alone: {Reason}", path, reason);
                return;
            }

            _logger.LogInformation("Steam folder {Folder} is not writable for the players; granting BUILTIN\\Users Modify", path);
            Grant(folder);
            _logger.LogInformation("Steam folder {Folder} is writable for the players now", path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException or InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(ex, "Steam folder {Folder} could not be made writable for the players", path);
        }
    }
}
