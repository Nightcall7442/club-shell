using System.Runtime.Versioning;
using ClubShell.Windows.Native;
using ClubShell.Windows.Sessions;
using Microsoft.Win32;

namespace ClubShell.Windows.Registry;

/// <summary>Previous state of one registry value, captured before a write so it can be restored.</summary>
/// <param name="Hive">Hive.</param>
/// <param name="Key">Key path below the hive.</param>
/// <param name="Name">Value name (empty = default value).</param>
/// <param name="Value">Previous data, or <see langword="null"/> when the value did not exist.</param>
/// <param name="Kind">Previous kind (<see cref="RegistryValueKind.None"/> when absent).</param>
/// <param name="KeyExisted"><see langword="false"/> when the key itself did not exist.</param>
public sealed record RegistryValueSnapshot(RegistryHive Hive, string Key, string Name, object? Value, RegistryValueKind Kind, bool KeyExisted);

/// <summary>A set of value snapshots taken at one point in time; pass to <see cref="RegistryHelper.Apply"/> to revert.</summary>
/// <param name="Values">Captured values.</param>
/// <param name="TakenAt">Capture time (UTC).</param>
public sealed record RegistrySnapshot(IReadOnlyList<RegistryValueSnapshot> Values, DateTimeOffset TakenAt);

/// <summary>A user hive mounted under HKEY_USERS by <see cref="RegistryHelper.LoadUserHive"/>; disposing unloads it.</summary>
[SupportedOSPlatform("windows")]
public sealed class LoadedHive : IDisposable
{
    private bool _unloaded;

    internal LoadedHive(string mountName, string filePath)
    {
        MountName = mountName;
        FilePath = filePath;
    }

    /// <summary>Sub-key name under HKEY_USERS.</summary>
    public string MountName { get; }

    /// <summary>NTUSER.DAT path.</summary>
    public string FilePath { get; }

    /// <summary>Key path below HKEY_USERS for <paramref name="subKey"/> inside this hive.</summary>
    public string Key(string subKey) => string.IsNullOrEmpty(subKey) ? MountName : MountName + "\\" + subKey;

    /// <summary>Unloads the hive. Every <see cref="RegistryKey"/> opened inside it must be closed first.</summary>
    public void Dispose()
    {
        if (_unloaded)
        {
            return;
        }

        _unloaded = true;
        GC.SuppressFinalize(this);
        RegistryHelper.UnloadUserHive(MountName);
    }
}

/// <summary>
/// Thin, always-64-bit-view wrappers over <see cref="Microsoft.Win32.Registry"/> with snapshot/revert support and
/// user hive loading (RegLoadKeyW). All paths are relative to the hive; HKEY_USERS paths start with the SID or mount name.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RegistryHelper
{
    /// <summary>ProfileList key (HKLM) holding one sub-key per profile SID.</summary>
    public const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    /// <summary>View used for every access.</summary>
    public static RegistryView View => RegistryView.Registry64;

    /// <summary><see langword="true"/> when the key exists.</summary>
    public static bool Exists(RegistryHive hive, string key)
    {
        using RegistryKey? k = Open(hive, key, writable: false);
        return k is not null;
    }

    /// <summary><see langword="true"/> when the value exists.</summary>
    public static bool ValueExists(RegistryHive hive, string key, string name)
    {
        using RegistryKey? k = Open(hive, key, writable: false);
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not null;
    }

    /// <summary>Raw value and kind, or <see langword="null"/> (kind None) when absent.</summary>
    public static object? GetValue(RegistryHive hive, string key, string name, out RegistryValueKind kind, bool expandEnvironment = true)
    {
        kind = RegistryValueKind.None;
        using RegistryKey? k = Open(hive, key, writable: false);
        object? value = k?.GetValue(name, null, expandEnvironment ? RegistryValueOptions.None : RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is not null && k is not null)
        {
            kind = k.GetValueKind(name);
        }

        return value;
    }

    /// <summary>Typed read: <c>int</c> for DWORD, <c>long</c> for QWORD, <c>string</c> for SZ/EXPAND_SZ, <c>string[]</c> for MULTI_SZ, <c>byte[]</c> for BINARY. Returns <paramref name="defaultValue"/> when absent or of another type.</summary>
    public static T? Get<T>(RegistryHive hive, string key, string name, T? defaultValue = default)
    {
        object? value = GetValue(hive, key, name, out _);
        return value is T typed ? typed : defaultValue;
    }

    /// <summary>Creates the key when needed and writes the value.</summary>
    public static void Set(RegistryHive hive, string key, string name, object value, RegistryValueKind kind)
    {
        ArgumentNullException.ThrowIfNull(value);
        using RegistryKey k = EnsureKey(hive, key);
        k.SetValue(name, value, kind);
        k.Flush();
    }

    /// <summary>Deletes a value; returns <see langword="false"/> when the key or value did not exist.</summary>
    public static bool DeleteValue(RegistryHive hive, string key, string name)
    {
        using RegistryKey? k = Open(hive, key, writable: true);
        if (k is null || k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is null)
        {
            return false;
        }

        k.DeleteValue(name, throwOnMissingValue: false);
        k.Flush();
        return true;
    }

    /// <summary>Deletes a key (and its subtree when <paramref name="recursive"/>); returns <see langword="false"/> when absent.</summary>
    public static bool DeleteKey(RegistryHive hive, string key, bool recursive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        using RegistryKey root = RegistryKey.OpenBaseKey(hive, View);
        using (RegistryKey? probe = root.OpenSubKey(key, writable: false))
        {
            if (probe is null)
            {
                return false;
            }
        }

        if (recursive)
        {
            root.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }
        else
        {
            root.DeleteSubKey(key, throwOnMissingSubKey: false);
        }

        return true;
    }

    /// <summary>Opens the key writable, creating it when needed.</summary>
    public static RegistryKey EnsureKey(RegistryHive hive, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        using RegistryKey root = RegistryKey.OpenBaseKey(hive, View);
        return root.CreateSubKey(key, writable: true);
    }

    /// <summary>Opens a key, or <see langword="null"/> when it does not exist.</summary>
    public static RegistryKey? Open(RegistryHive hive, string key, bool writable)
    {
        ArgumentNullException.ThrowIfNull(key);
        using RegistryKey root = RegistryKey.OpenBaseKey(hive, View);
        return key.Length == 0 ? RegistryKey.OpenBaseKey(hive, View) : root.OpenSubKey(key, writable);
    }

    /// <summary>Captures the current state of one value.</summary>
    public static RegistryValueSnapshot SnapshotValue(RegistryHive hive, string key, string name)
    {
        using RegistryKey? k = Open(hive, key, writable: false);
        if (k is null)
        {
            return new RegistryValueSnapshot(hive, key, name, null, RegistryValueKind.None, KeyExisted: false);
        }

        object? value = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        RegistryValueKind kind = value is null ? RegistryValueKind.None : k.GetValueKind(name);
        return new RegistryValueSnapshot(hive, key, name, value, kind, KeyExisted: true);
    }

    /// <summary>Captures the listed values.</summary>
    public static RegistrySnapshot Snapshot(IEnumerable<(RegistryHive Hive, string Key, string Name)> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var values = new List<RegistryValueSnapshot>();
        foreach ((RegistryHive hive, string key, string name) in targets)
        {
            values.Add(SnapshotValue(hive, key, name));
        }

        return new RegistrySnapshot(values, DateTimeOffset.UtcNow);
    }

    /// <summary>Captures every value directly under <paramref name="key"/> (no sub-keys).</summary>
    public static RegistrySnapshot SnapshotKey(RegistryHive hive, string key)
    {
        var values = new List<RegistryValueSnapshot>();
        using (RegistryKey? k = Open(hive, key, writable: false))
        {
            if (k is not null)
            {
                foreach (string name in k.GetValueNames())
                {
                    values.Add(new RegistryValueSnapshot(hive, key, name, k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), k.GetValueKind(name), KeyExisted: true));
                }
            }
        }

        return new RegistrySnapshot(values, DateTimeOffset.UtcNow);
    }

    /// <summary>Restores every value in the snapshot: rewrites existing ones, deletes ones that were absent, removes keys that did not exist and are now empty.</summary>
    public static void Apply(RegistrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (RegistryValueSnapshot v in snapshot.Values)
        {
            if (v.Value is not null)
            {
                Set(v.Hive, v.Key, v.Name, v.Value, v.Kind);
                continue;
            }

            _ = DeleteValue(v.Hive, v.Key, v.Name);
            if (!v.KeyExisted)
            {
                DeleteKeyIfEmpty(v.Hive, v.Key);
            }
        }
    }

    /// <summary>Profile directory of a SID from ProfileList (environment variables expanded), or <see langword="null"/>.</summary>
    public static string? GetProfileImagePath(string sid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        string? path = Get<string>(RegistryHive.LocalMachine, ProfileListKey + "\\" + sid, "ProfileImagePath");
        return string.IsNullOrWhiteSpace(path) ? null : Environment.ExpandEnvironmentVariables(path);
    }

    /// <summary>Key path below HKEY_USERS for <paramref name="subKey"/> of the user hive <paramref name="sidOrMount"/>.</summary>
    public static string UserHiveKey(string sidOrMount, string subKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sidOrMount);
        return string.IsNullOrEmpty(subKey) ? sidOrMount : sidOrMount + "\\" + subKey;
    }

    /// <summary>
    /// Mounts <paramref name="ntUserDatPath"/> under HKEY_USERS\<paramref name="mountName"/> (RegLoadKeyW; needs
    /// SeBackupPrivilege + SeRestorePrivilege, enabled for the call only). Fails while the user is logged on.
    /// </summary>
    public static LoadedHive LoadUserHive(string mountName, string ntUserDatPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ntUserDatPath);
        if (!File.Exists(ntUserDatPath))
        {
            throw new FileNotFoundException("User hive not found.", ntUserDatPath);
        }

        using PrivilegeScope backup = ProcessAsUser.EnablePrivilege(NativeConst.SE_BACKUP_NAME);
        using PrivilegeScope restore = ProcessAsUser.EnablePrivilege(NativeConst.SE_RESTORE_NAME);
        Win32Error.ThrowIfError(Advapi32.RegLoadKeyW(NativeConst.HKEY_USERS, mountName, ntUserDatPath), nameof(Advapi32.RegLoadKeyW));
        return new LoadedHive(mountName, ntUserDatPath);
    }

    /// <summary>Unmounts a hive mounted with <see cref="LoadUserHive"/>; throws ERROR_ACCESS_DENIED while keys inside it are still open.</summary>
    public static void UnloadUserHive(string mountName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountName);
        using PrivilegeScope backup = ProcessAsUser.EnablePrivilege(NativeConst.SE_BACKUP_NAME);
        using PrivilegeScope restore = ProcessAsUser.EnablePrivilege(NativeConst.SE_RESTORE_NAME);
        int result = Advapi32.RegUnLoadKeyW(NativeConst.HKEY_USERS, mountName);
        if (result == NativeConst.ERROR_ACCESS_DENIED)
        {
            // A RegistryKey that was not disposed keeps the hive busy; let finalizers close it and retry once.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            result = Advapi32.RegUnLoadKeyW(NativeConst.HKEY_USERS, mountName);
        }

        Win32Error.ThrowIfError(result, nameof(Advapi32.RegUnLoadKeyW));
    }

    private static void DeleteKeyIfEmpty(RegistryHive hive, string key)
    {
        using (RegistryKey? k = Open(hive, key, writable: false))
        {
            if (k is null || k.ValueCount > 0 || k.SubKeyCount > 0)
            {
                return;
            }
        }

        _ = DeleteKey(hive, key, recursive: false);
    }
}
