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
