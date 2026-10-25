using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ClubShell.Windows.Registry;

/// <summary>
/// Whether Windows setup is still running on this PC — the specialize and OOBE passes of a freshly imaged machine:
/// <c>HKLM\SYSTEM\Setup</c> <c>SystemSetupInProgress</c> or <c>OOBEInProgress</c> is 1. The Agent service is automatic
/// and can start during them, while the machine still carries a temporary name.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsSetup
{
    /// <summary>Key with the setup state values.</summary>
    public const string SetupKey = @"SYSTEM\Setup";

    /// <summary>Reads the live registry; <see langword="false"/> when the values cannot be read.</summary>
    public static bool IsInProgress()
    {
        try
        {
            return IsInProgress(name => RegistryHelper.Get<int>(RegistryHive.LocalMachine, SetupKey, name));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Decides from the two DWORD values returned by <paramref name="read"/> (<see langword="null"/> or 0 when absent).</summary>
    public static bool IsInProgress(Func<string, int?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return read("SystemSetupInProgress") == 1 || read("OOBEInProgress") == 1;
    }
}
