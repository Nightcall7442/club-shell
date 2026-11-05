namespace ClubShell.Agent.Users;

/// <summary>What <see cref="TempUserProvisioner"/> does with an existing account that is in a privileged local group.</summary>
public enum PrivilegedAccountAction
{
    /// <summary>Not privileged: take it over.</summary>
    Accept,

    /// <summary>Privileged: leave it untouched and report <c>privilegedAccount</c>.</summary>
    Refuse,

    /// <summary>Privileged, and <c>shell.kioskUser.removeFromPrivilegedGroups</c> is on: remove it from those groups, then take it over.</summary>
    RemoveFromGroups,
}

/// <summary>Which password <see cref="TempUserProvisioner"/> keeps for an existing kiosk account.</summary>
public enum KioskPasswordSource
{
    /// <summary>The one in <c>secure\kiosk.cred</c>.</summary>
    Stored,

    /// <summary>The one Winlogon holds for auto-logon (LSA secret <c>DefaultPassword</c>).</summary>
    AutoLogon,

    /// <summary>A new random password, set now.</summary>
    New,
}

/// <summary>Decisions of <see cref="TempUserProvisioner"/> that need no Windows API, so they can be tested anywhere.</summary>
public static class KioskProvisioningRules
{
    /// <summary>What to do with an existing account found in <paramref name="privilegedGroups"/> privileged local groups.</summary>
    public static PrivilegedAccountAction OnPrivilegedGroups(int privilegedGroups, bool removeFromPrivilegedGroups) =>
        privilegedGroups <= 0 ? PrivilegedAccountAction.Accept
        : removeFromPrivilegedGroups ? PrivilegedAccountAction.RemoveFromGroups
        : PrivilegedAccountAction.Refuse;

    /// <summary>
    /// Which password to keep and whether a rotation stays owed. A rotation is due at start (<paramref name="rotateOnStart"/>),
    /// when the stored password is older than the rotation period, or when it no longer logs on. While Windows auto-logon
    /// has yet to log the kiosk on (<paramref name="autoLogonPending"/>) a due rotation waits for the kiosk session: Winlogon
    /// reads the LSA secret and checks it against SAM, and changing both in between fails that logon. The wait needs a
    /// password that logs on now — the stored one, else the auto-logon one; without either the password is set now.
    /// The probes are called only when the answer depends on them, and each at most once (each is a <c>LogonUser</c>).
    /// </summary>
    public static (KioskPasswordSource Source, bool RotationOwed) PlanPassword(
        Func<bool> storedLogsOn, bool storedFresh, bool rotateOnStart, bool autoLogonPending, Func<bool> autoLogonPasswordLogsOn)
    {
        ArgumentNullException.ThrowIfNull(storedLogsOn);
        ArgumentNullException.ThrowIfNull(autoLogonPasswordLogsOn);
        bool? stored = null;
        bool Stored() => stored ??= storedLogsOn();
        if (!rotateOnStart && storedFresh && Stored())
        {
            return (KioskPasswordSource.Stored, false);
        }

        if (autoLogonPending)
        {
            if (Stored())
            {
                return (KioskPasswordSource.Stored, true);
            }

            if (autoLogonPasswordLogsOn())
            {
                return (KioskPasswordSource.AutoLogon, true);
            }
        }

        return (KioskPasswordSource.New, false);
    }

    /// <summary>Letters the club's games disks use (SHELL_CHANGES 73): G: online games, H: offline games.</summary>
    public static readonly IReadOnlySet<char> GamesDiskLetters = new HashSet<char> { 'G', 'H' };

    /// <summary>
    /// The drive the kiosk user's folders go to when no root is configured, among the non-system fixed drives
    /// (SHELL_CHANGES 73): never a games disk — one on an iSCSI bus, or G: / H: — whose writes go to the seat's layer on the
    /// club server (a shared quota, reset at every boot); the drive that already holds the folders, else the one with the
    /// most free space that is not the drive of a <c>games.libraryRoots</c> entry. A library drive that already holds
    /// them (a hybrid's D:) keeps them.
    /// </summary>
    public static string? PickDataDrive(IEnumerable<DataDriveCandidate> drives, IEnumerable<string> libraryRoots)
    {
        ArgumentNullException.ThrowIfNull(drives);
        ArgumentNullException.ThrowIfNull(libraryRoots);
        HashSet<char> libraryLetters = libraryRoots.Select(DriveLetter).Where(l => l is not null).Select(l => l!.Value).ToHashSet();
        List<DataDriveCandidate> usable = drives
            .Where(d => !d.Iscsi && DriveLetter(d.Name) is { } letter && !GamesDiskLetters.Contains(letter))
            .ToList();
        if (usable.FirstOrDefault(d => d.HoldsFolders) is { } holding)
        {
            return holding.Name;
        }

        return usable
            .Where(d => !libraryLetters.Contains(DriveLetter(d.Name)!.Value))
            .OrderByDescending(d => d.FreeBytes)
            .FirstOrDefault()?.Name;
    }

    /// <summary><c>D</c> for <c>D:\</c> or <c>d:\Games</c>; <see langword="null"/> without a drive letter.</summary>
    private static char? DriveLetter(string? path) =>
        path is { Length: >= 2 } && path[1] == ':' && char.IsAsciiLetter(path[0]) ? char.ToUpperInvariant(path[0]) : null;

    /// <summary>
    /// The directory the kiosk user's shell folders go to: <c>&lt;root&gt;\&lt;user&gt;</c> for a configured root, else
    /// <c>&lt;detected data drive&gt;\ClubShell\Users\&lt;user&gt;</c>; <see langword="null"/> when there is neither.
    /// </summary>
    public static string? RedirectTarget(string? configuredRoot, string? detectedDrive, string userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.Combine(configuredRoot, userName);
        }

        return detectedDrive is null ? null : Path.Combine(detectedDrive, "ClubShell", "Users", userName);
    }
}

/// <summary>A non-system fixed drive as <see cref="KioskProvisioningRules.PickDataDrive"/> weighs it.</summary>
/// <param name="Name">Root, <c>D:\</c>.</param>
/// <param name="FreeBytes">Free space.</param>
/// <param name="HoldsFolders">It already holds <c>ClubShell\Users\&lt;user&gt;</c>.</param>
/// <param name="Iscsi">It is on an iSCSI disk.</param>
public sealed record DataDriveCandidate(string Name, long FreeBytes, bool HoldsFolders, bool Iscsi);
