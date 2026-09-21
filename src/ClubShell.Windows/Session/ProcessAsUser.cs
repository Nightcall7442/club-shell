using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClubShell.Windows.Native;
using ClubShell.Windows.Processes;
using Microsoft.Win32.SafeHandles;

namespace ClubShell.Windows.Sessions;

/// <summary>
/// Enables one privilege on the current process token for the lifetime of the scope and restores the previous
/// state on dispose (privileges that were already enabled are left enabled).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PrivilegeScope : IDisposable
{
    private readonly SafeTokenHandle _token;
    private readonly bool _restore;
    private bool _disposed;

    internal PrivilegeScope(string privilegeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privilegeName);
        PrivilegeName = privilegeName;
        if (!Advapi32.OpenProcessToken(Kernel32.GetCurrentProcess(), NativeConst.TOKEN_ADJUST_PRIVILEGES | NativeConst.TOKEN_QUERY, out _token))
        {
            Win32Error.ThrowLastError(nameof(Advapi32.OpenProcessToken));
        }

        try
        {
            if (!Advapi32.LookupPrivilegeValueW(null, privilegeName, out LUID luid))
            {
                Win32Error.ThrowLastError(nameof(Advapi32.LookupPrivilegeValueW));
            }

            TOKEN_PRIVILEGES state = TOKEN_PRIVILEGES.Single(luid, NativeConst.SE_PRIVILEGE_ENABLED);
            int size = NativeString.SizeOf<TOKEN_PRIVILEGES>();
            nint previous = Marshal.AllocHGlobal(size);
            nint returned = Marshal.AllocHGlobal(sizeof(uint));
            try
            {
                Marshal.WriteInt32(previous, 0);
                bool ok = Advapi32.AdjustTokenPrivileges(_token, false, ref state, (uint)size, previous, returned);
                int error = Win32Error.Last();
                if (!ok)
                {
                    Win32Error.Throw(error, nameof(Advapi32.AdjustTokenPrivileges));
                }

                if (error == NativeConst.ERROR_NOT_ALL_ASSIGNED)
                {
                    throw new Win32Exception(error, $"{privilegeName} is not held by the current process token.");
                }

                // PreviousState lists only privileges the call changed: an empty list means it was already enabled.
                _restore = Marshal.ReadInt32(previous) > 0;
            }
            finally
            {
                Marshal.FreeHGlobal(previous);
                Marshal.FreeHGlobal(returned);
            }
        }
        catch
        {
            _token.Dispose();
            throw;
        }
    }

    /// <summary>Privilege name (SE_*_NAME).</summary>
    public string PrivilegeName { get; }

    /// <summary>Disables the privilege again when this scope enabled it.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_restore)
        {
            _ = Advapi32.SetPrivilege(_token, PrivilegeName, enable: false);
        }

        _token.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Creates processes in interactive sessions from the LocalSystem service (ARCHITECTURE.md §2). Every privilege
/// (SeTcb, SeAssignPrimaryToken, SeIncreaseQuota) is enabled only for the duration of the call.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProcessAsUser
{
    /// <summary>Interactive desktop used for every launch.</summary>
    public const string DefaultDesktop = "winsta0\\default";

    /// <summary>Default creation flags for an interactive process.</summary>
    public const uint DefaultCreationFlags = NativeConst.CREATE_UNICODE_ENVIRONMENT | NativeConst.CREATE_NEW_CONSOLE;

    /// <summary>Enables a privilege on the current process token until the returned scope is disposed.</summary>
    public static PrivilegeScope EnablePrivilege(string privilegeName) => new(privilegeName);

    /// <summary>
    /// Primary token of the user logged on to <paramref name="sessionId"/> (WTSQueryUserToken, requires LocalSystem),
    /// duplicated as a primary token; with <paramref name="elevated"/> the linked (full) token is used when the
    /// account is a filtered administrator.
    /// </summary>
    public static SafeTokenHandle GetUserToken(uint sessionId, bool elevated = false)
    {
        using PrivilegeScope tcb = EnablePrivilege(NativeConst.SE_TCB_NAME);
        if (!Wtsapi32.WTSQueryUserToken(sessionId, out SafeTokenHandle token))
        {
            Win32Error.ThrowLastError(nameof(Wtsapi32.WTSQueryUserToken));
        }

        using (token)
        {
            if (elevated)
            {
                using SafeTokenHandle? linked = GetLinkedToken(token);
                if (linked is not null)
                {
                    return DuplicatePrimary(linked);
                }
            }

            return DuplicatePrimary(token);
        }
    }

    /// <summary>Duplicates any token as a primary token with MAXIMUM_ALLOWED access.</summary>
    public static SafeTokenHandle DuplicatePrimary(SafeTokenHandle token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!Advapi32.DuplicateTokenEx(token, NativeConst.MAXIMUM_ALLOWED, 0, SECURITY_IMPERSONATION_LEVEL.SecurityIdentification, TOKEN_TYPE.TokenPrimary, out SafeTokenHandle primary))
        {
            Win32Error.ThrowLastError(nameof(Advapi32.DuplicateTokenEx));
        }

        return primary;
    }

    /// <summary>Linked (elevated) token of a UAC-filtered token, or <see langword="null"/> when there is none.</summary>
    public static SafeTokenHandle? GetLinkedToken(SafeTokenHandle token)
    {
        ArgumentNullException.ThrowIfNull(token);
        nint buffer = Marshal.AllocHGlobal(nint.Size);
        try
        {
            if (!Advapi32.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenLinkedToken, buffer, (uint)nint.Size, out _))
            {
                return null;
            }

            nint linked = Marshal.ReadIntPtr(buffer);
            return linked == 0 ? null : new SafeTokenHandle(linked, ownsHandle: true);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>WTS session id the token belongs to.</summary>
    public static uint GetTokenSessionId(SafeTokenHandle token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!Advapi32.TryGetTokenDword(token, TOKEN_INFORMATION_CLASS.TokenSessionId, out uint sessionId))
        {
            Win32Error.ThrowLastError(nameof(Advapi32.GetTokenInformation));
        }

        return sessionId;
    }

    /// <summary>
    /// CreateProcessAsUserW on <paramref name="desktop"/>. <paramref name="environmentBlock"/> is a Unicode block
    /// (0 = inherit the Agent's). Returns the pid and an all-access process handle; the thread handle is closed.
    /// </summary>
    public static (uint Pid, SafeProcessHandle Handle) CreateProcessAsUser(
        SafeTokenHandle token,
        string exe,
        string? args,
        string? workingDir,
        nint environmentBlock,
        string desktop = DefaultDesktop,
        uint creationFlags = DefaultCreationFlags,
        bool hidden = false)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(exe);
        using PrivilegeScope assign = EnablePrivilege(NativeConst.SE_ASSIGNPRIMARYTOKEN_NAME);
        using PrivilegeScope quota = EnablePrivilege(NativeConst.SE_INCREASE_QUOTA_NAME);

        STARTUPINFOW startup = STARTUPINFOW.Create();
        startup.lpDesktop = Marshal.StringToHGlobalUni(desktop);
        if (hidden)
        {
            startup.dwFlags |= NativeConst.STARTF_USESHOWWINDOW;
            startup.wShowWindow = (ushort)NativeConst.SW_HIDE;
        }

        try
        {
            string commandLine = ProcessLauncher.BuildCommandLine(exe, args);
            string directory = workingDir ?? Path.GetDirectoryName(exe) ?? Environment.SystemDirectory;
            if (!Advapi32.CreateProcessAsUser(token, exe, commandLine, environmentBlock, directory, creationFlags, false, ref startup, out PROCESS_INFORMATION info))
            {
                Win32Error.ThrowLastError(nameof(Advapi32.CreateProcessAsUserW));
            }

            _ = Kernel32.CloseHandle(info.hThread);
            return (info.dwProcessId, new SafeProcessHandle(info.hProcess, ownsHandle: true));
        }
        finally
        {
            Marshal.FreeHGlobal(startup.lpDesktop);
        }
    }

    /// <summary>
    /// Runs <paramref name="exe"/> as LocalSystem inside an interactive session (own token duplicated, TokenSessionId
    /// changed; requires SeTcbPrivilege). Used for the fallback overlay, never for player-facing programs.
    /// </summary>
    public static (uint Pid, SafeProcessHandle Handle) RunAsSystemInSession(uint sessionId, string exe, string? args, string? workingDir = null, bool hidden = false)
    {
        if (!Advapi32.OpenProcessToken(Kernel32.GetCurrentProcess(), NativeConst.TOKEN_ALL_ACCESS, out SafeTokenHandle own))
        {
            Win32Error.ThrowLastError(nameof(Advapi32.OpenProcessToken));
        }

        using (own)
        {
            using SafeTokenHandle system = DuplicatePrimary(own);
            using (EnablePrivilege(NativeConst.SE_TCB_NAME))
            {
                uint target = sessionId;
                if (!Advapi32.SetTokenInformation(system, TOKEN_INFORMATION_CLASS.TokenSessionId, ref target, sizeof(uint)))
                {
                    Win32Error.ThrowLastError(nameof(Advapi32.SetTokenInformation));
                }
            }

            bool added = false;
            nint environment = 0;
            try
            {
                system.DangerousAddRef(ref added);
                if (!Userenv.CreateEnvironmentBlock(out environment, system.DangerousGetHandle(), false))
                {
                    Win32Error.ThrowLastError(nameof(Userenv.CreateEnvironmentBlock));
                }

                return CreateProcessAsUser(system, exe, args, workingDir, environment, DefaultDesktop, DefaultCreationFlags, hidden);
            }
            finally
            {
                if (environment != 0)
                {
                    _ = Userenv.DestroyEnvironmentBlock(environment);
                }

                if (added)
                {
                    system.DangerousRelease();
                }
            }
        }
    }

    /// <summary>Interactive logon (LOGON32_LOGON_INTERACTIVE) returning a primary token; <paramref name="domain"/> defaults to the local machine.</summary>
    public static SafeTokenHandle LogonUser(string username, string? domain, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(password);
        if (!Advapi32.LogonUserW(username, domain ?? ".", password, NativeConst.LOGON32_LOGON_INTERACTIVE, NativeConst.LOGON32_PROVIDER_DEFAULT, out SafeTokenHandle token))
        {
            Win32Error.ThrowLastError(nameof(Advapi32.LogonUserW));
        }

        return token;
    }
}
