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
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SteamFolderAccess
{
    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    private readonly GameDetector _detector;
    private readonly GameLibrary _library;
    private readonly ILogger<SteamFolderAccess> _logger;
    private readonly object _gate = new();
    private Task _running = Task.CompletedTask;

    /// <summary>Creates the helper.</summary>
    public SteamFolderAccess(GameDetector detector, GameLibrary library, ILogger<SteamFolderAccess> logger)
    {
        _detector = detector;
        _library = library;
        _logger = logger;
    }

    /// <summary>Whether <paramref name="exe"/> is the Steam client (<c>steam.exe</c>).</summary>
    public static bool IsSteam(string? exe) => string.Equals(Path.GetFileName(exe), "steam.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="folder"/> is a Steam installation and nothing else: <c>steam.exe</c> and
    /// <c>steamclient.dll</c> in it, and neither a drive root nor inside Windows. The catalogue's paths come from the
    /// club's owner; without this, a game "C:\Windows\System32\steam.exe" would have opened System32 to every player.
    /// </summary>
    public static bool IsSteamInstallation(DirectoryInfo folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        string full = Path.TrimEndingDirectorySeparator(folder.FullName);
        string windows = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        return folder.Parent is not null
            && !string.IsNullOrEmpty(windows)
            && !full.Equals(windows, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(full, "steam.exe"))
            && File.Exists(Path.Combine(full, "steamclient.dll"));
    }

    /// <summary>
    /// Makes the Steam folders writable for the players in the background, unless a pass is running already: the Steam
    /// client's folder and the folder of every catalogue game that is <c>steam.exe</c> itself.
    /// </summary>
    public Task EnsureInBackground()
    {
        lock (_gate)
        {
            if (_running.IsCompleted)
            {
                _running = Task.Run(EnsureAll);
            }

            return _running;
        }
    }

    /// <summary>Whether BUILTIN\Users may modify <paramref name="folder"/> and everything in it.</summary>
    public static bool IsWritableByUsers(DirectoryInfo folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        FileSystemRights allowed = 0;
        FileSystemRights denied = 0;
        foreach (FileSystemAccessRule rule in folder.GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            // Only a rule on the folder itself that its contents inherit counts.
            if (!Users.Equals(rule.IdentityReference) || (rule.InheritanceFlags & Inherit) != Inherit
                || (rule.PropagationFlags & (PropagationFlags.InheritOnly | PropagationFlags.NoPropagateInherit)) != 0)
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

        return (allowed & FileSystemRights.Modify) == FileSystemRights.Modify && (denied & FileSystemRights.Write) == 0;
    }

    /// <summary>Grants BUILTIN\Users Modify on <paramref name="folder"/>, inherited by its contents; <see langword="false"/> when already so.</summary>
    public static bool Grant(DirectoryInfo folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (IsWritableByUsers(folder))
        {
            return false;
        }

        DirectorySecurity acl = folder.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Modify, Inherit, PropagationFlags.None, AccessControlType.Allow));
        folder.SetAccessControl(acl);
        return true;
    }

    private void EnsureAll()
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Path.GetDirectoryName(_detector.ResolveLauncherExe(LauncherType.Steam)) is { Length: > 0 } client)
        {
            folders.Add(client);
        }

        foreach (Game game in _library.Snapshot)
        {
            if (game.Launcher == LauncherType.Exe && IsSteam(game.ExePath) && Path.IsPathRooted(game.ExePath) && Path.GetDirectoryName(game.ExePath) is { Length: > 0 } dir)
            {
                folders.Add(dir);
            }
        }

        foreach (string path in folders)
        {
            var folder = new DirectoryInfo(path);
            try
            {
                if (!folder.Exists)
                {
                    continue;
                }

                if (!IsSteamInstallation(folder))
                {
                    _logger.LogWarning("{Folder} is not a Steam installation; its rights are left alone", path);
                    continue;
                }

                if (IsWritableByUsers(folder))
                {
                    _logger.LogDebug("Steam folder {Folder} is writable for the players", path);
                    continue;
                }

                _logger.LogInformation("Steam folder {Folder} is not writable for the players; granting BUILTIN\\Users Modify", path);
                Grant(folder);
                _logger.LogInformation("Steam folder {Folder} is writable for the players now", path);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException or InvalidOperationException or ArgumentException)
            {
                // A network share (games on a NAS) keeps its own rights; Steam cannot run from a read-only one.
                _logger.LogWarning(ex, "Steam folder {Folder} could not be made writable for the players", path);
            }
        }
    }
}
