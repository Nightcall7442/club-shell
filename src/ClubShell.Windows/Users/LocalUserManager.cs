using System.DirectoryServices.AccountManagement;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using ClubShell.Windows.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Windows.Users;

/// <summary>
/// Local (SAM) account management for the kiosk user (ARCHITECTURE.md §6.1 step 9) through
/// <see cref="PrincipalContext"/> on the local machine. Accounts created here are plain members of
/// <c>BUILTIN\Users</c>; adding anything to <c>BUILTIN\Administrators</c> is refused.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LocalUserManager
{
    /// <summary>Length of generated passwords.</summary>
    public const int PasswordLength = 24;

    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Digits = "23456789";
    private const string Symbols = "!@#$%^&*()-_=+[]{}:;,.?";
    private const string Alphabet = Lower + Upper + Digits + Symbols;
    private readonly ILogger<LocalUserManager> _logger;

    /// <summary>Creates the manager.</summary>
    public LocalUserManager(ILogger<LocalUserManager>? logger = null) => _logger = logger ?? NullLogger<LocalUserManager>.Instance;

    /// <summary>Cryptographically random password satisfying the default Windows complexity policy (one of each class, no ambiguous glyphs).</summary>
    public static string GeneratePassword(int length = PasswordLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 8);
        char[] chars = new char[length];
        chars[0] = Lower[RandomNumberGenerator.GetInt32(Lower.Length)];
        chars[1] = Upper[RandomNumberGenerator.GetInt32(Upper.Length)];
        chars[2] = Digits[RandomNumberGenerator.GetInt32(Digits.Length)];
        chars[3] = Symbols[RandomNumberGenerator.GetInt32(Symbols.Length)];
        for (int i = 4; i < length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        RandomNumberGenerator.Shuffle<char>(chars);
        return new string(chars);
    }

    /// <summary><see langword="true"/> when a local account with that SAM name exists.</summary>
    public bool Exists(string name)
    {
        using PrincipalContext context = Machine();
        using UserPrincipal? user = Find(context, name);
        return user is not null;
    }

    /// <summary>Creates a disabled-password-change, never-expiring local user in BUILTIN\Users and returns its password (generated when <paramref name="password"/> is <see langword="null"/>).</summary>
    public string Create(string name, string? password = null, string? displayName = null, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        password ??= GeneratePassword();
        using PrincipalContext context = Machine();
        using (UserPrincipal user = new(context))
        {
            user.Name = name;
            user.DisplayName = displayName ?? name;
            user.Description = description ?? "ClubShell kiosk account";
            user.PasswordNeverExpires = true;
            user.UserCannotChangePassword = true;
            user.Enabled = true;
            user.SetPassword(password);
            user.Save();
        }

        AddToGroup(name, NativeConst.SID_BUILTIN_USERS);
        _logger.LogInformation("Created local user {User}", name);
        return password;
    }

    /// <summary>Deletes the account (profile directory is left for <see cref="ProfileReset"/>); returns <see langword="false"/> when absent.</summary>
    public bool Delete(string name)
    {
        using PrincipalContext context = Machine();
        using UserPrincipal? user = Find(context, name);
        if (user is null)
        {
            return false;
        }

        user.Delete();
        _logger.LogInformation("Deleted local user {User}", name);
        return true;
    }

    /// <summary>Enables or disables the account.</summary>
    public void SetEnabled(string name, bool enabled)
    {
        using PrincipalContext context = Machine();
        using UserPrincipal user = Require(context, name);
        user.Enabled = enabled;
        user.Save();
        _logger.LogInformation("Local user {User} enabled={Enabled}", name, enabled);
    }

    /// <summary>Sets a new password (administrative reset, no old password needed).</summary>
    public void SetPassword(string name, string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        using PrincipalContext context = Machine();
        using UserPrincipal user = Require(context, name);
        user.SetPassword(password);
        user.PasswordNeverExpires = true;
        user.UserCannotChangePassword = true;
        user.Save();
    }

    /// <summary>Adds the user to a local group by SID (default BUILTIN\Users); BUILTIN\Administrators is refused.</summary>
    public void AddToGroup(string name, string groupSid = NativeConst.SID_BUILTIN_USERS)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupSid);
        if (string.Equals(groupSid, NativeConst.SID_BUILTIN_ADMINISTRATORS, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Kiosk accounts must never be administrators.");
        }

        using PrincipalContext context = Machine();
        using UserPrincipal user = Require(context, name);
        using GroupPrincipal group = GroupPrincipal.FindByIdentity(context, IdentityType.Sid, groupSid)
            ?? throw new InvalidOperationException($"Local group {groupSid} not found.");
        if (group.Members.Contains(user))
        {
            return;
        }

        group.Members.Add(user);
        group.Save();
        _logger.LogInformation("Added {User} to group {Group}", name, group.Name);
    }

    /// <summary>
    /// Idempotent kiosk account provisioning: creates the account when missing, always rotates the password,
    /// re-asserts flags, enabled state and Users membership. Returns <see langword="true"/> when the account was created.
    /// </summary>
    public bool EnsureKioskUser(string name, out string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        password = GeneratePassword();
        if (!Exists(name))
        {
            _ = Create(name, password, name, "ClubShell kiosk account");
            return true;
        }

        SetPassword(name, password);
        SetEnabled(name, true);
        AddToGroup(name, NativeConst.SID_BUILTIN_USERS);
        _logger.LogInformation("Kiosk user {User} verified, password rotated", name);
        return false;
    }

    /// <summary>SAM names of local users starting with <paramref name="prefix"/> (case-insensitive).</summary>
    public IReadOnlyList<string> ListUsersWithPrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var names = new List<string>();
        using PrincipalContext context = Machine();
        using UserPrincipal filter = new(context);
        filter.Name = prefix + "*";
        using PrincipalSearcher searcher = new(filter);
        using PrincipalSearchResult<Principal> results = searcher.FindAll();
        foreach (Principal principal in results)
        {
            using (principal)
            {
                if (principal.SamAccountName is { } sam && sam.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(sam);
                }
            }
        }

        return names;
    }

    /// <summary>SID string of a local account, or <see langword="null"/> when absent.</summary>
    public string? GetSid(string name)
    {
        using PrincipalContext context = Machine();
        using UserPrincipal? user = Find(context, name);
        return user?.Sid?.Value;
    }

    private static PrincipalContext Machine() => new(ContextType.Machine);

    private static UserPrincipal? Find(PrincipalContext context, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, name);
    }

    private static UserPrincipal Require(PrincipalContext context, string name) =>
        Find(context, name) ?? throw new InvalidOperationException($"Local user {name} does not exist.");
}
