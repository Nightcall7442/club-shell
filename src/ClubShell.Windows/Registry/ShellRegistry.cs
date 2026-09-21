using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using ClubShell.Windows.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace ClubShell.Windows.Registry;

/// <summary>
/// Shell replacement and auto-logon (ARCHITECTURE.md §2 rules 2 and 3).
/// <para>
/// Per-user shell replacement writes <c>Shell</c> under the user's <c>Winlogon</c> key and under
/// <c>Policies\System</c> (the "Custom User Interface" policy userinit honours) inside <c>HKU\&lt;sid&gt;</c>, so
/// administrators keep explorer. The machine-wide variant (<c>userSid</c> = <see langword="null"/>) rewrites HKLM
/// Winlogon\Shell and affects EVERY account including administrators; use it only on dedicated kiosk images.
/// </para>
/// <para>
/// Auto-logon stores the password as the LSA secret <c>DefaultPassword</c> (LsaStorePrivateData) rather than the
/// clear-text <c>DefaultPassword</c> registry value.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellRegistry
{
    /// <summary>Winlogon key below HKLM.</summary>
    public const string MachineWinlogonKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

    /// <summary>Winlogon key below a user hive.</summary>
    public const string UserWinlogonKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";

    /// <summary>Policies\System key below a user hive (Custom User Interface policy).</summary>
    public const string UserPoliciesSystemKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";

    /// <summary>Default Windows shell.</summary>
    public const string ExplorerShell = "explorer.exe";

    /// <summary>LSA secret name read by winlogon for auto-logon.</summary>
    public const string DefaultPasswordSecret = "DefaultPassword";

    private readonly ILogger<ShellRegistry> _logger;

    /// <summary>Creates the helper.</summary>
    public ShellRegistry(ILogger<ShellRegistry>? logger = null) => _logger = logger ?? NullLogger<ShellRegistry>.Instance;

    /// <summary><see langword="true"/> when HKLM Winlogon AutoAdminLogon is "1".</summary>
    public bool IsAutoLogonEnabled => RegistryHelper.Get<string>(RegistryHive.LocalMachine, MachineWinlogonKey, "AutoAdminLogon") == "1";

    /// <summary>User name configured for auto-logon, or <see langword="null"/>.</summary>
    public string? AutoLogonUserName => RegistryHelper.Get<string>(RegistryHive.LocalMachine, MachineWinlogonKey, "DefaultUserName");

    /// <summary>Sets the shell for the user hive <paramref name="userSid"/> (SID or loaded mount name), or machine-wide when <see langword="null"/>.</summary>
    public void SetCustomShell(string? userSid, string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        if (userSid is null)
        {
            RegistryHelper.Set(RegistryHive.LocalMachine, MachineWinlogonKey, "Shell", exePath, RegistryValueKind.String);
            _logger.LogWarning("Machine-wide shell set to {Shell}: this applies to every account including administrators", exePath);
            return;
        }

        RegistryHelper.Set(RegistryHive.Users, RegistryHelper.UserHiveKey(userSid, UserWinlogonKey), "Shell", exePath, RegistryValueKind.String);
        RegistryHelper.Set(RegistryHive.Users, RegistryHelper.UserHiveKey(userSid, UserPoliciesSystemKey), "Shell", exePath, RegistryValueKind.String);
        _logger.LogInformation("Shell for {Sid} set to {Shell}", userSid, exePath);
    }

    /// <summary>Removes the custom shell: deletes the per-user values, or resets HKLM Winlogon\Shell to explorer.exe.</summary>
    public void RestoreExplorer(string? userSid)
    {
        if (userSid is null)
        {
            RegistryHelper.Set(RegistryHive.LocalMachine, MachineWinlogonKey, "Shell", ExplorerShell, RegistryValueKind.String);
            _logger.LogInformation("Machine-wide shell restored to explorer.exe");
            return;
        }

        _ = RegistryHelper.DeleteValue(RegistryHive.Users, RegistryHelper.UserHiveKey(userSid, UserWinlogonKey), "Shell");
        _ = RegistryHelper.DeleteValue(RegistryHive.Users, RegistryHelper.UserHiveKey(userSid, UserPoliciesSystemKey), "Shell");
        _logger.LogInformation("Per-user shell for {Sid} removed (explorer.exe)", userSid);
    }

    /// <summary>Effective shell: per-user override when <paramref name="userSid"/> is given and set, otherwise the HKLM value (explorer.exe when unset).</summary>
    public string GetCurrentShell(string? userSid)
    {
        if (userSid is not null)
        {
            string? user = RegistryHelper.Get<string>(RegistryHive.Users, RegistryHelper.UserHiveKey(userSid, UserPoliciesSystemKey), "Shell")
                ?? RegistryHelper.Get<string>(RegistryHive.Users, RegistryHelper.UserHiveKey(userSid, UserWinlogonKey), "Shell");
            if (!string.IsNullOrWhiteSpace(user))
            {
                return user;
            }
        }

        return RegistryHelper.Get<string>(RegistryHive.LocalMachine, MachineWinlogonKey, "Shell") is { Length: > 0 } machine ? machine : ExplorerShell;
    }

    /// <summary>
    /// Enables auto-logon for <paramref name="userName"/>: AutoAdminLogon=1, DefaultUserName/DefaultDomainName,
    /// password stored as LSA secret, any clear-text DefaultPassword value removed.
    /// <paramref name="count"/> sets AutoLogonCount (auto-logon disables itself after that many boots).
    /// </summary>
    public void SetAutoLogon(string userName, string password, string domain = ".", int? count = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(password);
        LsaSecrets.Store(DefaultPasswordSecret, password);
        RegistryHelper.Set(RegistryHive.LocalMachine, MachineWinlogonKey, "DefaultUserName", userName, RegistryValueKind.String);
        RegistryHelper.Set(RegistryHive.LocalMachine, MachineWinlogonKey, "DefaultDomainName", string.IsNullOrWhiteSpace(domain) ? "." : domain, RegistryValueKind.String);
        RegistryHelper.Set(RegistryHive.LocalMachine, MachineWinlogonKey, "AutoAdminLogon", "1", RegistryValueKind.String);
        _ = RegistryHelper.DeleteValue(RegistryHive.LocalMachine, MachineWinlogonKey, "DefaultPassword");
        if (count is { } n)
        {
            RegistryHelper.Set(RegistryHive.LocalMachine, MachineWinlogonKey, "AutoLogonCount", n, RegistryValueKind.DWord);
        }
        else
        {
            _ = RegistryHelper.DeleteValue(RegistryHive.LocalMachine, MachineWinlogonKey, "AutoLogonCount");
        }

        _logger.LogInformation("Auto-logon enabled for {Domain}\\{User}", domain, userName);
    }

    /// <summary>Disables auto-logon and removes the stored password (registry value and LSA secret).</summary>
    public void ClearAutoLogon()
    {
        RegistryHelper.Set(RegistryHive.LocalMachine, MachineWinlogonKey, "AutoAdminLogon", "0", RegistryValueKind.String);
        _ = RegistryHelper.DeleteValue(RegistryHive.LocalMachine, MachineWinlogonKey, "DefaultPassword");
        _ = RegistryHelper.DeleteValue(RegistryHive.LocalMachine, MachineWinlogonKey, "AutoLogonCount");
        LsaSecrets.Store(DefaultPasswordSecret, null);
        _logger.LogInformation("Auto-logon disabled");
    }
}

/// <summary>LSA private data (secrets) over <see cref="Advapi32"/>; every LSA function returns an NTSTATUS.</summary>
[SupportedOSPlatform("windows")]
internal static class LsaSecrets
{
    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>, or deletes the secret when <paramref name="value"/> is <see langword="null"/>.</summary>
    public static unsafe void Store(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        LSA_OBJECT_ATTRIBUTES attributes = LSA_OBJECT_ATTRIBUTES.Create();
        ThrowIfFailed(Advapi32.LsaOpenPolicy(0, ref attributes, NativeConst.POLICY_CREATE_SECRET | NativeConst.POLICY_GET_PRIVATE_INFORMATION, out nint policy), nameof(Advapi32.LsaOpenPolicy));
        try
        {
            fixed (char* keyChars = key)
            fixed (char* valueChars = value ?? string.Empty)
            {
                LSA_UNICODE_STRING keyString = Make(keyChars, key.Length);
                if (value is null)
                {
                    ThrowIfFailed(Advapi32.LsaStorePrivateData(policy, &keyString, null), nameof(Advapi32.LsaStorePrivateData));
                }
                else
                {
                    LSA_UNICODE_STRING valueString = Make(valueChars, value.Length);
                    ThrowIfFailed(Advapi32.LsaStorePrivateData(policy, &keyString, &valueString), nameof(Advapi32.LsaStorePrivateData));
                }
            }
        }
        finally
        {
            _ = Advapi32.LsaClose(policy);
        }
    }

    private static unsafe LSA_UNICODE_STRING Make(char* chars, int length) => new()
    {
        Buffer = (nint)chars,
        Length = checked((ushort)(length * sizeof(char))),
        MaximumLength = checked((ushort)((length + 1) * sizeof(char))),
    };

    private static void ThrowIfFailed(int status, string api)
    {
        if (status < 0)
        {
            int error = unchecked((int)Advapi32.LsaNtStatusToWinError(status));
            throw new Win32Exception(error, string.Format(CultureInfo.InvariantCulture, "{0} failed with NTSTATUS 0x{1:X8}: {2}", api, status, Win32Error.Message(error)));
        }
    }
}
