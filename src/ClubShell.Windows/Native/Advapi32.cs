#pragma warning disable CA1707 // identifiers should not contain underscores (Win32 names)

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClubShell.Windows.Native;

/// <summary>
/// advapi32.dll P/Invokes: tokens, privileges, process creation as another user, account/SID lookup,
/// system shutdown, Service Control Manager and Credential Manager.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class Advapi32
{
    private const string Lib = "advapi32.dll";

    // ---- tokens -------------------------------------------------------------------------------

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(SafeHandle ProcessHandle, uint DesiredAccess, out SafeTokenHandle TokenHandle);

    /// <summary>Overload for the pseudo-handle from <see cref="Kernel32.GetCurrentProcess"/>.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint ProcessHandle, uint DesiredAccess, out SafeTokenHandle TokenHandle);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateTokenEx(SafeTokenHandle hExistingToken, uint dwDesiredAccess, nint lpTokenAttributes, SECURITY_IMPERSONATION_LEVEL ImpersonationLevel, TOKEN_TYPE TokenType, out SafeTokenHandle phNewToken);

    /// <summary>Generic variant; <paramref name="TokenInformation"/> points at a caller buffer of <paramref name="TokenInformationLength"/> bytes.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(SafeTokenHandle TokenHandle, TOKEN_INFORMATION_CLASS TokenInformationClass, nint TokenInformation, uint TokenInformationLength, out uint ReturnLength);

    /// <summary>DWORD-valued classes (TokenSessionId, TokenElevation, TokenElevationType, TokenUIAccess, ...).</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(SafeTokenHandle TokenHandle, TOKEN_INFORMATION_CLASS TokenInformationClass, ref uint TokenInformation, uint TokenInformationLength, out uint ReturnLength);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetTokenInformation(SafeTokenHandle TokenHandle, TOKEN_INFORMATION_CLASS TokenInformationClass, nint TokenInformation, uint TokenInformationLength);

    /// <summary>DWORD-valued classes (TokenSessionId: requires SE_TCB_NAME, i.e. LocalSystem).</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetTokenInformation(SafeTokenHandle TokenHandle, TOKEN_INFORMATION_CLASS TokenInformationClass, ref uint TokenInformation, uint TokenInformationLength);

    /// <summary>Reads a DWORD token class; returns <see langword="false"/> and sets last error on failure.</summary>
    public static bool TryGetTokenDword(SafeTokenHandle token, TOKEN_INFORMATION_CLASS tokenClass, out uint value)
    {
        value = 0;
        return GetTokenInformation(token, tokenClass, ref value, sizeof(uint), out _);
    }

    /// <summary>Reads the token's user SID as a string (S-1-5-...), or <see langword="null"/> on failure.</summary>
    public static string? GetTokenUserSid(SafeTokenHandle token)
    {
        _ = GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, 0, 0, out uint needed);
        if (needed == 0)
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, buffer, needed, out _))
            {
                return null;
            }

            // TOKEN_USER starts with the SID pointer.
            nint sid = Marshal.ReadIntPtr(buffer);
            return ConvertSidToString(sid);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---- privileges ---------------------------------------------------------------------------

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupPrivilegeValueW(string? lpSystemName, string lpName, out LUID lpLuid);

    /// <summary>Pass <paramref name="PreviousState"/> = 0 and <paramref name="ReturnLength"/> = 0 when the old state is not needed. Check ERROR_NOT_ALL_ASSIGNED via last error even on success.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustTokenPrivileges(SafeTokenHandle TokenHandle, [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, nint PreviousState, nint ReturnLength);

    /// <summary>Enables (or disables) one named privilege on <paramref name="token"/>; returns <see langword="false"/> when the privilege is not held.</summary>
    public static bool SetPrivilege(SafeTokenHandle token, string privilegeName, bool enable)
    {
        if (!LookupPrivilegeValueW(null, privilegeName, out LUID luid))
        {
            return false;
        }

        TOKEN_PRIVILEGES state = TOKEN_PRIVILEGES.Single(luid, enable ? NativeConst.SE_PRIVILEGE_ENABLED : 0);
        if (!AdjustTokenPrivileges(token, false, ref state, (uint)NativeString.SizeOf<TOKEN_PRIVILEGES>(), 0, 0))
        {
            return false;
        }

        return Win32Error.Last() != NativeConst.ERROR_NOT_ALL_ASSIGNED;
    }

    /// <summary>Enables a privilege on the current process token (e.g. SE_SHUTDOWN_NAME before ExitWindowsEx).</summary>
    public static bool EnableProcessPrivilege(string privilegeName)
    {
        if (!OpenProcessToken(Kernel32.GetCurrentProcess(), NativeConst.TOKEN_ADJUST_PRIVILEGES | NativeConst.TOKEN_QUERY, out SafeTokenHandle token))
        {
            return false;
        }

        using (token)
        {
            return SetPrivilege(token, privilegeName, enable: true);
        }
    }

    // ---- process creation ---------------------------------------------------------------------

    /// <summary>Raw form: <paramref name="lpCommandLine"/> is a writable LPWSTR (or 0); prefer <see cref="CreateProcessAsUser"/>.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcessAsUserW(SafeTokenHandle hToken, string? lpApplicationName, nint lpCommandLine, nint lpProcessAttributes, nint lpThreadAttributes, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint dwCreationFlags, nint lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    /// <summary>Raw form: <paramref name="lpCommandLine"/> is a writable LPWSTR (or 0); prefer <see cref="CreateProcessWithToken"/>.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcessWithTokenW(SafeTokenHandle hToken, uint dwLogonFlags, string? lpApplicationName, nint lpCommandLine, uint dwCreationFlags, nint lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    /// <summary>
    /// CreateProcessAsUserW with managed strings. Caller must close <c>hProcess</c>/<c>hThread</c> of the result
    /// and set <c>lpDesktop</c> in <paramref name="startupInfo"/> (e.g. via <see cref="Marshal.StringToHGlobalUni"/> of "winsta0\default").
    /// </summary>
    public static bool CreateProcessAsUser(SafeTokenHandle token, string? applicationName, string? commandLine, nint environmentBlock, string? currentDirectory, uint creationFlags, bool inheritHandles, ref STARTUPINFOW startupInfo, out PROCESS_INFORMATION processInformation)
    {
        nint cmd = commandLine is null ? 0 : Marshal.StringToHGlobalUni(commandLine);
        try
        {
            return CreateProcessAsUserW(token, applicationName, cmd, 0, 0, inheritHandles, creationFlags, environmentBlock, currentDirectory, ref startupInfo, out processInformation);
        }
        finally
        {
            if (cmd != 0)
            {
                Marshal.FreeHGlobal(cmd);
            }
        }
    }

    /// <summary>CreateProcessWithTokenW with managed strings (requires the caller to run in an interactive session; use <see cref="CreateProcessAsUser"/> from a service).</summary>
    public static bool CreateProcessWithToken(SafeTokenHandle token, uint logonFlags, string? applicationName, string? commandLine, nint environmentBlock, string? currentDirectory, uint creationFlags, ref STARTUPINFOW startupInfo, out PROCESS_INFORMATION processInformation)
    {
        nint cmd = commandLine is null ? 0 : Marshal.StringToHGlobalUni(commandLine);
        try
        {
            return CreateProcessWithTokenW(token, logonFlags, applicationName, cmd, creationFlags, environmentBlock, currentDirectory, ref startupInfo, out processInformation);
        }
        finally
        {
            if (cmd != 0)
            {
                Marshal.FreeHGlobal(cmd);
            }
        }
    }

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LogonUserW(string lpszUsername, string? lpszDomain, string? lpszPassword, uint dwLogonType, uint dwLogonProvider, out SafeTokenHandle phToken);

    // ---- accounts / SIDs ----------------------------------------------------------------------

    /// <summary><paramref name="Sid"/> points at a caller buffer of <paramref name="cbSid"/> bytes (call once with 0 to size). Domain buffer is in chars.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupAccountNameW(string? lpSystemName, string lpAccountName, nint Sid, ref uint cbSid, ref char ReferencedDomainName, ref uint cchReferencedDomainName, out SID_NAME_USE peUse);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupAccountSidW(string? lpSystemName, nint Sid, ref char Name, ref uint cchName, ref char ReferencedDomainName, ref uint cchReferencedDomainName, out SID_NAME_USE peUse);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ConvertSidToStringSidW(nint Sid, out SafeLocalMemHandle StringSid);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ConvertStringSidToSidW(string StringSid, out SafeLocalMemHandle Sid);

    /// <summary>String form of a binary SID, or <see langword="null"/> on failure.</summary>
    public static string? ConvertSidToString(nint sid)
    {
        if (sid == 0 || !ConvertSidToStringSidW(sid, out SafeLocalMemHandle handle))
        {
            return null;
        }

        using (handle)
        {
            return handle.ReadString();
        }
    }

    /// <summary>Resolves "DOMAIN\name" / "name" to its SID string, or <see langword="null"/> when not found.</summary>
    public static string? LookupAccountSid(string accountName)
    {
        uint cbSid = 0;
        uint cchDomain = 0;
        char[] domain = new char[1];
        _ = LookupAccountNameW(null, accountName, 0, ref cbSid, ref domain[0], ref cchDomain, out _);
        if (cbSid == 0)
        {
            return null;
        }

        domain = new char[Math.Max(cchDomain, 1u)];
        nint sid = Marshal.AllocHGlobal((int)cbSid);
        try
        {
            return LookupAccountNameW(null, accountName, sid, ref cbSid, ref domain[0], ref cchDomain, out _) ? ConvertSidToString(sid) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(sid);
        }
    }

    /// <summary>Resolves a SID string to "DOMAIN\name", or <see langword="null"/> when not mapped.</summary>
    public static string? LookupAccountName(string sidString)
    {
        if (!ConvertStringSidToSidW(sidString, out SafeLocalMemHandle sid))
        {
            return null;
        }

        using (sid)
        {
            char[] name = new char[256];
            char[] domain = new char[256];
            uint cchName = (uint)name.Length;
            uint cchDomain = (uint)domain.Length;
            if (!LookupAccountSidW(null, sid.DangerousGetHandle(), ref name[0], ref cchName, ref domain[0], ref cchDomain, out _))
            {
                return null;
            }

            string domainPart = NativeString.FromBuffer(domain, (int)cchDomain);
            string namePart = NativeString.FromBuffer(name, (int)cchName);
            return domainPart.Length == 0 ? namePart : domainPart + "\\" + namePart;
        }
    }

    // ---- shutdown -----------------------------------------------------------------------------

    /// <summary>Requires SE_SHUTDOWN_NAME (or SE_REMOTE_SHUTDOWN_NAME for a remote machine).</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitiateSystemShutdownExW(string? lpMachineName, string? lpMessage, uint dwTimeout, [MarshalAs(UnmanagedType.Bool)] bool bForceAppsClosed, [MarshalAs(UnmanagedType.Bool)] bool bRebootAfterShutdown, uint dwReason);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AbortSystemShutdownW(string? lpMachineName);

    // ---- Service Control Manager --------------------------------------------------------------

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeServiceHandle OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeServiceHandle OpenServiceW(SafeServiceHandle hSCManager, string lpServiceName, uint dwDesiredAccess);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ControlService(SafeServiceHandle hService, uint dwControl, out SERVICE_STATUS lpServiceStatus);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryServiceStatus(SafeServiceHandle hService, out SERVICE_STATUS lpServiceStatus);

    /// <summary><paramref name="lpServiceArgVectors"/> = 0 for no arguments.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool StartServiceW(SafeServiceHandle hService, uint dwNumServiceArgs, nint lpServiceArgVectors);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseServiceHandle(nint hSCObject);

    // ---- registry hives (not covered by Microsoft.Win32.Registry) -----------------------------

    /// <summary>Mounts a hive file under <paramref name="hKey"/>\<paramref name="lpSubKey"/> (needs SE_BACKUP_NAME + SE_RESTORE_NAME). Returns ERROR_SUCCESS or a Win32 error code.</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegLoadKeyW(nint hKey, string lpSubKey, string lpFile);

    /// <summary>Unmounts a hive loaded with <see cref="RegLoadKeyW"/>; ERROR_ACCESS_DENIED while keys inside it are open. Returns ERROR_SUCCESS or a Win32 error code.</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegUnLoadKeyW(nint hKey, string lpSubKey);

    // ---- LSA private data (secrets); every function returns an NTSTATUS ----------------------

    /// <summary>Initialize <paramref name="ObjectAttributes"/> with <see cref="LSA_OBJECT_ATTRIBUTES.Create"/>; <paramref name="SystemName"/> = 0 for the local machine. Close with <see cref="LsaClose"/>.</summary>
    [LibraryImport(Lib)]
    public static partial int LsaOpenPolicy(nint SystemName, ref LSA_OBJECT_ATTRIBUTES ObjectAttributes, uint DesiredAccess, out nint PolicyHandle);

    /// <summary><paramref name="PrivateData"/> = <see langword="null"/> deletes the secret.</summary>
    [LibraryImport(Lib)]
    internal static unsafe partial int LsaStorePrivateData(nint PolicyHandle, LSA_UNICODE_STRING* KeyName, LSA_UNICODE_STRING* PrivateData);

    [LibraryImport(Lib)]
    internal static partial int LsaClose(nint ObjectHandle);

    [LibraryImport(Lib)]
    internal static partial uint LsaNtStatusToWinError(int Status);

    // ---- Credential Manager -------------------------------------------------------------------

    /// <summary><paramref name="Filter"/> like "Steam*" or <see langword="null"/> with CRED_ENUMERATE_ALL_CREDENTIALS. Fails with ERROR_NOT_FOUND when empty.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CredEnumerateW(string? Filter, uint Flags, out uint Count, out SafeCredentialsHandle Credentials);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CredDeleteW(string TargetName, uint Type, uint Flags);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial void CredFree(nint Buffer);

    /// <summary>Lists (targetName, userName, type) for the calling user's credentials matching <paramref name="filter"/>; empty when none.</summary>
    public static IReadOnlyList<(string TargetName, string UserName, uint Type)> EnumerateCredentials(string? filter)
    {
        uint flags = filter is null ? NativeConst.CRED_ENUMERATE_ALL_CREDENTIALS : 0;
        if (!CredEnumerateW(filter, flags, out uint count, out SafeCredentialsHandle handle))
        {
            return Array.Empty<(string, string, uint)>();
        }

        using (handle)
        {
            CREDENTIALW[] items = handle.ReadCredentials((int)count);
            var result = new List<(string, string, uint)>(items.Length);
            foreach (CREDENTIALW c in items)
            {
                result.Add((c.TargetNameString, c.UserNameString, c.Type));
            }

            return result;
        }
    }
}

/// <summary>userenv.dll P/Invokes: environment blocks and user profiles for CreateProcessAsUser / profile reset.</summary>
[SupportedOSPlatform("windows")]
public static partial class Userenv
{
    private const string Lib = "userenv.dll";

    /// <summary><paramref name="hToken"/> = 0 builds the system environment; otherwise pass <c>token.DangerousGetHandle()</c> while holding the SafeHandle. Free with <see cref="DestroyEnvironmentBlock"/>.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateEnvironmentBlock(out nint lpEnvironment, nint hToken, [MarshalAs(UnmanagedType.Bool)] bool bInherit);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyEnvironmentBlock(nint lpEnvironment);

    /// <summary>Initialize with <see cref="PROFILEINFOW.Create"/> and set <c>lpUserName</c>; <c>hProfile</c> receives the registry hive handle (unload with <see cref="UnloadUserProfile"/>).</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LoadUserProfileW(SafeTokenHandle hToken, ref PROFILEINFOW lpProfileInfo);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnloadUserProfile(SafeTokenHandle hToken, nint hProfile);

    /// <summary>Deletes the profile directory and registry entry for a SID string. Fails while the profile is loaded.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteProfileW(string lpSidString, string? lpProfilePath, string? lpComputerName);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetUserProfileDirectoryW(SafeTokenHandle hToken, ref char lpProfileDir, ref uint lpcchSize);

    /// <summary>Profile directory of the token's user, or <see langword="null"/> when the profile is not loaded.</summary>
    public static string? GetUserProfileDirectory(SafeTokenHandle token)
    {
        char[] buffer = new char[NativeConst.MAX_PATH + 1];
        uint size = (uint)buffer.Length;
        return GetUserProfileDirectoryW(token, ref buffer[0], ref size) ? NativeString.FromBuffer(buffer) : null;
    }
}
