// Win32 identifiers keep their native names so they can be looked up in the SDK headers verbatim.
#pragma warning disable CA1008 // enums should have zero value
#pragma warning disable CA1027 // mark enums with FlagsAttribute
#pragma warning disable CA1028 // enum storage should be Int32
#pragma warning disable CA1034 // nested types should not be visible
#pragma warning disable CA1051 // do not declare visible instance fields (interop structs)
#pragma warning disable CA1069 // enums should not have duplicate values
#pragma warning disable CA1707 // identifiers should not contain underscores
#pragma warning disable CA1711 // identifiers should not have incorrect suffix
#pragma warning disable CA1712 // do not prefix enum values with type name
#pragma warning disable CA1714 // flags enums should have plural names
#pragma warning disable CA1715 // identifiers should have correct prefix
#pragma warning disable CA1720 // identifiers should not contain type names
#pragma warning disable CA1724 // type names should not match namespaces
#pragma warning disable CA1815 // override equals on value types
#pragma warning disable CA2217 // do not mark enums with FlagsAttribute
#pragma warning disable CA2201 // COMException is the correct type for a failed HRESULT (HResult.ThrowIfFailed)
#pragma warning disable IDE1006 // naming styles

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

// Every generated P/Invoke stub in this assembly resolves its DLL from System32 only (no DLL hijacking from CWD).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace ClubShell.Windows.Native;

// ---------------------------------------------------------------------------------------------
// Delegates. LibraryImport signatures take the callback as a raw function pointer (nint): convert with
// Marshal.GetFunctionPointerForDelegate and keep the delegate instance alive (store it in a field) for as
// long as Win32 may call it. User32 exposes delegate-taking overloads for the synchronous Enum* callbacks.
// ---------------------------------------------------------------------------------------------

/// <summary>Low-level hook procedure (WH_KEYBOARD_LL / WH_MOUSE_LL). Return non-zero to swallow the input.</summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
public delegate nint HookProc(int nCode, nint wParam, nint lParam);

/// <summary>Callback for <see cref="User32.EnumWindows"/>. Return <see langword="false"/> to stop enumeration.</summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
[return: MarshalAs(UnmanagedType.Bool)]
public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

/// <summary>Callback for <see cref="User32.EnumDisplayMonitors"/>. Return <see langword="false"/> to stop enumeration.</summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
[return: MarshalAs(UnmanagedType.Bool)]
public delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData);

/// <summary>Callback for <see cref="User32.SetWinEventHook"/>.</summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
public delegate void WinEventProc(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

/// <summary>WNDPROC for a window class registered with <see cref="User32.RegisterClassExW"/>.</summary>
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
public delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

// ---------------------------------------------------------------------------------------------
// SafeHandles. Every class has a public parameterless constructor (required by the LibraryImport
// generator for out/return marshalling and by CA1419).
// ---------------------------------------------------------------------------------------------

/// <summary>HHOOK from <see cref="User32.SetWindowsHookExW"/>; released with UnhookWindowsHookEx.</summary>
[SupportedOSPlatform("windows")]
public sealed class SafeHookHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Creates an invalid handle to be filled by interop.</summary>
    public SafeHookHandle() : base(ownsHandle: true) { }

    /// <summary>Wraps an existing HHOOK.</summary>
    public SafeHookHandle(nint existingHandle, bool ownsHandle) : base(ownsHandle) => SetHandle(existingHandle);

    /// <inheritdoc />
    protected override bool ReleaseHandle() => User32.UnhookWindowsHookEx(handle);
}

/// <summary>HWINEVENTHOOK from <see cref="User32.SetWinEventHook"/>; released with UnhookWinEvent.</summary>
[SupportedOSPlatform("windows")]
public sealed class SafeWinEventHookHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Creates an invalid handle to be filled by interop.</summary>
    public SafeWinEventHookHandle() : base(ownsHandle: true) { }

    /// <summary>Wraps an existing HWINEVENTHOOK.</summary>
    public SafeWinEventHookHandle(nint existingHandle, bool ownsHandle) : base(ownsHandle) => SetHandle(existingHandle);

    /// <inheritdoc />
    protected override bool ReleaseHandle() => User32.UnhookWinEvent(handle);
}

/// <summary>Access token handle (OpenProcessToken, DuplicateTokenEx, LogonUserW, WTSQueryUserToken); released with CloseHandle.</summary>
[SupportedOSPlatform("windows")]
public sealed class SafeTokenHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Creates an invalid handle to be filled by interop.</summary>
    public SafeTokenHandle() : base(ownsHandle: true) { }

    /// <summary>Wraps an existing token handle.</summary>
    public SafeTokenHandle(nint existingHandle, bool ownsHandle) : base(ownsHandle) => SetHandle(existingHandle);

    /// <inheritdoc />
    protected override bool ReleaseHandle() => Kernel32.CloseHandle(handle);
}

/// <summary>Job object handle (CreateJobObjectW); released with CloseHandle. Closing kills the job when JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE is set.</summary>
[SupportedOSPlatform("windows")]
public sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Creates an invalid handle to be filled by interop.</summary>
    public SafeJobHandle() : base(ownsHandle: true) { }

    /// <summary>Wraps an existing job handle.</summary>
    public SafeJobHandle(nint existingHandle, bool ownsHandle) : base(ownsHandle) => SetHandle(existingHandle);

    /// <inheritdoc />
    protected override bool ReleaseHandle() => Kernel32.CloseHandle(handle);
}

/// <summary>Event handle (CreateEventW); released with CloseHandle.</summary>
[SupportedOSPlatform("windows")]
public sealed class SafeEventHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Creates an invalid handle to be filled by interop.</summary>
    public SafeEventHandle() : base(ownsHandle: true) { }

    /// <summary>Wraps an existing event handle.</summary>
    public SafeEventHandle(nint existingHandle, bool ownsHandle) : base(ownsHandle) => SetHandle(existingHandle);

    /// <inheritdoc />
    protected override bool ReleaseHandle() => Kernel32.CloseHandle(handle);
}

/// <summary>Device / file handle from <see cref="Kernel32.CreateFileW"/> (used with DeviceIoControl); released with CloseHandle.</summary>
[SupportedOSPlatform("windows")]
public sealed class SafeDeviceHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Creates an invalid handle to be filled by interop.</summary>
    public SafeDeviceHandle() : base(ownsHandle: true) { }

    /// <summary>Wraps an existing kernel handle.</summary>
    public SafeDeviceHandle(nint existingHandle, bool ownsHandle) : base(ownsHandle) => SetHandle(existingHandle);

    /// <inheritdoc />
    protected override bool ReleaseHandle() => Kernel32.CloseHandle(handle);
}

/// <summary>HMODULE from <see cref="Kernel32.LoadLibraryW"/>; released with FreeLibrary.</summary>
[SupportedOSPlatform("windows")]
public sealed class SafeLibraryHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Creates an invalid handle to be filled by interop.</summary>
    public SafeLibraryHandle() : base(ownsHandle: true) { }

    /// <summary>Wraps an existing HMODULE.</summary>
    public SafeLibraryHandle(nint existingHandle, bool ownsHandle) : base(ownsHandle) => SetHandle(existingHandle);

    /// <inheritdoc />
    protected override bool ReleaseHandle() => Kernel32.FreeLibrary(handle);
}

/// <summary>SC_HANDLE (OpenSCManagerW / OpenServiceW); released with CloseServiceHandle.</summary>
[SupportedOSPlatform("windows")]
public sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Creates an invalid handle to be filled by interop.</summary>
    public SafeServiceHandle() : base(ownsHandle: true) { }

    /// <summary>Wraps an existing SC_HANDLE.</summary>
    public SafeServiceHandle(nint existingHandle, bool ownsHandle) : base(ownsHandle) => SetHandle(existingHandle);

    /// <inheritdoc />
    protected override bool ReleaseHandle() => Advapi32.CloseServiceHandle(handle);
}

/// <summary>HDESK (OpenInputDesktop / OpenDesktopW); released with CloseDesktop.</summary>
[SupportedOSPlatform("windows")]
public sealed class SafeDesktopHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Creates an invalid handle to be filled by interop.</summary>
    public SafeDesktopHandle() : base(ownsHandle: true) { }

    /// <summary>Wraps an existing HDESK.</summary>
    public SafeDesktopHandle(nint existingHandle, bool ownsHandle) : base(ownsHandle) => SetHandle(existingHandle);

    /// <inheritdoc />
    protected override bool ReleaseHandle() => User32.CloseDesktop(handle);
}

/// <summary>Memory returned by WTS* APIs; released with WTSFreeMemory.</summary>
[SupportedOSPlatform("windows")]
public sealed class SafeWtsMemoryHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Creates an invalid handle to be filled by interop.</summary>
    public SafeWtsMemoryHandle() : base(ownsHandle: true) { }

    /// <summary>Wraps an existing WTS buffer.</summary>
    public SafeWtsMemoryHandle(nint existingHandle, bool ownsHandle) : base(ownsHandle) => SetHandle(existingHandle);

    /// <summary>Reads the buffer as a null-terminated UTF-16 string (empty when the buffer is invalid).</summary>
    public string ReadString() => IsInvalid ? string.Empty : Marshal.PtrToStringUni(handle) ?? string.Empty;

    /// <summary>Reads a blittable struct at the start of the buffer.</summary>
    public unsafe T ReadStruct<T>() where T : unmanaged => Unsafe.ReadUnaligned<T>((void*)handle);

    /// <summary>Reads <paramref name="count"/> consecutive blittable structs from the buffer.</summary>
    public unsafe T[] ReadArray<T>(int count) where T : unmanaged
    {
        if (count <= 0 || IsInvalid)
        {
            return Array.Empty<T>();
        }

        T[] result = new T[count];
        int size = Unsafe.SizeOf<T>();
        for (int i = 0; i < count; i++)
        {
            result[i] = Unsafe.ReadUnaligned<T>((void*)(handle + (i * size)));
        }

        return result;
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        Wtsapi32.WTSFreeMemory(handle);
        return true;
    }
}

/// <summary>Memory allocated by the OS with LocalAlloc (ConvertSidToStringSidW, ConvertStringSidToSidW); released with LocalFree.</summary>
[SupportedOSPlatform("windows")]
public sealed class SafeLocalMemHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Creates an invalid handle to be filled by interop.</summary>
    public SafeLocalMemHandle() : base(ownsHandle: true) { }

    /// <summary>Wraps an existing HLOCAL.</summary>
    public SafeLocalMemHandle(nint existingHandle, bool ownsHandle) : base(ownsHandle) => SetHandle(existingHandle);

    /// <summary>Reads the buffer as a null-terminated UTF-16 string (empty when the buffer is invalid).</summary>
    public string ReadString() => IsInvalid ? string.Empty : Marshal.PtrToStringUni(handle) ?? string.Empty;

    /// <inheritdoc />
    protected override bool ReleaseHandle() => Kernel32.LocalFree(handle) == 0;
}

/// <summary>PCREDENTIALW* array from <see cref="Advapi32.CredEnumerateW"/>; released with CredFree.</summary>
[SupportedOSPlatform("windows")]
public sealed class SafeCredentialsHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Creates an invalid handle to be filled by interop.</summary>
    public SafeCredentialsHandle() : base(ownsHandle: true) { }

    /// <summary>Wraps an existing credential buffer.</summary>
    public SafeCredentialsHandle(nint existingHandle, bool ownsHandle) : base(ownsHandle) => SetHandle(existingHandle);

    /// <summary>Copies the <paramref name="count"/> CREDENTIALW records the buffer points to.</summary>
    public unsafe CREDENTIALW[] ReadCredentials(int count)
    {
        if (count <= 0 || IsInvalid)
        {
            return Array.Empty<CREDENTIALW>();
        }

        CREDENTIALW[] result = new CREDENTIALW[count];
        for (int i = 0; i < count; i++)
        {
            nint entry = Marshal.ReadIntPtr(handle, i * nint.Size);
            result[i] = Unsafe.ReadUnaligned<CREDENTIALW>((void*)entry);
        }

        return result;
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        Advapi32.CredFree(handle);
        return true;
    }
}

// ---------------------------------------------------------------------------------------------
// Error helpers
// ---------------------------------------------------------------------------------------------

/// <summary>Win32 last-error helpers for LibraryImport methods declared with <c>SetLastError = true</c>.</summary>
[SupportedOSPlatform("windows")]
public static class Win32Error
{
    /// <summary>Last Win32 error captured after the most recent P/Invoke on this thread.</summary>
    public static int Last() => Marshal.GetLastPInvokeError();

    /// <summary>System message for a Win32 error code.</summary>
    public static string Message(int code) => new Win32Exception(code).Message;

    /// <summary>Throws <see cref="Win32Exception"/> for the last error, naming the failing API.</summary>
    [DoesNotReturn]
    public static void ThrowLastError(string api) => Throw(Last(), api);

    /// <summary>Throws <see cref="Win32Exception"/> for an explicit error code, naming the failing API.</summary>
    [DoesNotReturn]
    public static void Throw(int code, string api) =>
        throw new Win32Exception(code, $"{api} failed with Win32 error {code} (0x{code:X8}): {Message(code)}");

    /// <summary>Throws for the last error when <paramref name="ok"/> is <see langword="false"/>.</summary>
    public static void ThrowIfFalse(bool ok, string api)
    {
        if (!ok)
        {
            ThrowLastError(api);
        }
    }

    /// <summary>Throws for the last error when <paramref name="value"/> is zero; otherwise returns it.</summary>
    public static nint ThrowIfZero(nint value, string api)
    {
        if (value == 0)
        {
            ThrowLastError(api);
        }

        return value;
    }

    /// <summary>Throws when an API that returns an ERROR_* code directly did not return ERROR_SUCCESS.</summary>
    public static void ThrowIfError(int win32Result, string api)
    {
        if (win32Result != NativeConst.ERROR_SUCCESS)
        {
            Throw(win32Result, api);
        }
    }

    /// <summary>Throws for the last error when a SafeHandle came back invalid; otherwise returns the handle.</summary>
    public static T ThrowIfInvalid<T>(T handle, string api) where T : SafeHandle
    {
        if (handle.IsInvalid)
        {
            int error = Last();
            handle.Dispose();
            Throw(error, api);
        }

        return handle;
    }
}

/// <summary>HRESULT helpers.</summary>
[SupportedOSPlatform("windows")]
public static class HResult
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_POINTER = unchecked((int)0x80004003);
    public const int E_ABORT = unchecked((int)0x80004004);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
    public const int E_ACCESSDENIED = unchecked((int)0x80070005);
    public const int E_HANDLE = unchecked((int)0x80070006);
    public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int DWM_E_COMPOSITIONDISABLED = unchecked((int)0x80263001);

    /// <summary>True when <paramref name="hr"/> is a success code.</summary>
    public static bool Succeeded(int hr) => hr >= 0;

    /// <summary>True when <paramref name="hr"/> is a failure code.</summary>
    public static bool Failed(int hr) => hr < 0;

    /// <summary>Extracts the Win32 error code from a FACILITY_WIN32 HRESULT, or returns the HRESULT itself otherwise.</summary>
    public static int ToWin32(int hr) => (hr & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? hr & 0xFFFF : hr;

    /// <summary>Converts a Win32 error code into an HRESULT (HRESULT_FROM_WIN32).</summary>
    public static int FromWin32(int error) => error <= 0 ? error : unchecked((int)(((uint)error & 0x0000FFFF) | 0x80070000));

    /// <summary>Throws <see cref="COMException"/> for a failed HRESULT, naming the failing API.</summary>
    public static void ThrowIfFailed(int hr, string api)
    {
        if (hr < 0)
        {
            Exception inner = Marshal.GetExceptionForHR(hr) ?? new COMException(api, hr);
            throw new COMException($"{api} failed with HRESULT 0x{hr:X8}: {inner.Message}", hr);
        }
    }
}

/// <summary>Helpers for fixed UTF-16 buffers inside interop structs and for <c>ref char</c> output buffers.</summary>
[SupportedOSPlatform("windows")]
public static class NativeString
{
    /// <summary>Reads a null-terminated UTF-16 string from a fixed <see cref="ushort"/> buffer of <paramref name="capacity"/> code units.</summary>
    public static unsafe string FromFixed(ushort* buffer, int capacity)
    {
        int length = 0;
        while (length < capacity && buffer[length] != 0)
        {
            length++;
        }

        return length == 0 ? string.Empty : new string((char*)buffer, 0, length);
    }

    /// <summary>Writes <paramref name="value"/> null-terminated into a fixed <see cref="ushort"/> buffer, truncating to <paramref name="capacity"/> - 1 code units.</summary>
    public static unsafe void ToFixed(string? value, ushort* buffer, int capacity)
    {
        int n = Math.Min(value?.Length ?? 0, capacity - 1);
        for (int i = 0; i < n; i++)
        {
            buffer[i] = value![i];
        }

        buffer[n] = 0;
    }

    /// <summary>Takes the first <paramref name="length"/> chars of a buffer filled by a Win32 API that returns the copied length.</summary>
    public static string FromBuffer(ReadOnlySpan<char> buffer, int length) =>
        length <= 0 ? string.Empty : new string(buffer[..Math.Min(length, buffer.Length)]);

    /// <summary>Reads a null-terminated UTF-16 string from a buffer filled by a Win32 API.</summary>
    public static string FromBuffer(ReadOnlySpan<char> buffer)
    {
        int end = buffer.IndexOf('\0');
        return end < 0 ? new string(buffer) : new string(buffer[..end]);
    }

    /// <summary>Managed size of an unmanaged struct (equals the unmanaged layout size for the blittable structs in this namespace).</summary>
    public static int SizeOf<T>() where T : unmanaged => Unsafe.SizeOf<T>();
}

// ---------------------------------------------------------------------------------------------
// Enums
// ---------------------------------------------------------------------------------------------

/// <summary>WTS_CONNECTSTATE_CLASS.</summary>
public enum WTS_CONNECTSTATE_CLASS
{
    WTSActive = 0,
    WTSConnected = 1,
    WTSConnectQuery = 2,
    WTSShadow = 3,
    WTSDisconnected = 4,
    WTSIdle = 5,
    WTSListen = 6,
    WTSReset = 7,
    WTSDown = 8,
    WTSInit = 9,
}

/// <summary>WTS_INFO_CLASS for WTSQuerySessionInformationW.</summary>
public enum WTS_INFO_CLASS
{
    WTSInitialProgram = 0,
    WTSApplicationName = 1,
    WTSWorkingDirectory = 2,
    WTSOEMId = 3,
    WTSSessionId = 4,
    WTSUserName = 5,
    WTSWinStationName = 6,
    WTSDomainName = 7,
    WTSConnectState = 8,
    WTSClientBuildNumber = 9,
    WTSClientName = 10,
    WTSClientDirectory = 11,
    WTSClientProductId = 12,
    WTSClientHardwareId = 13,
    WTSClientAddress = 14,
    WTSClientDisplay = 15,
    WTSClientProtocolType = 16,
    WTSIdleTime = 17,
    WTSLogonTime = 18,
    WTSIncomingBytes = 19,
    WTSOutgoingBytes = 20,
    WTSIncomingFrames = 21,
    WTSOutgoingFrames = 22,
    WTSClientInfo = 23,
    WTSSessionInfo = 24,
    WTSSessionInfoEx = 25,
    WTSConfigInfo = 26,
    WTSValidationInfo = 27,
    WTSSessionAddressV4 = 28,
    WTSIsRemoteSession = 29,
}

/// <summary>JOBOBJECTINFOCLASS.</summary>
public enum JOBOBJECTINFOCLASS
{
    JobObjectBasicAccountingInformation = 1,
    JobObjectBasicLimitInformation = 2,
    JobObjectBasicProcessIdList = 3,
    JobObjectBasicUIRestrictions = 4,
    JobObjectSecurityLimitInformation = 5,
    JobObjectEndOfJobTimeInformation = 6,
    JobObjectAssociateCompletionPortInformation = 7,
    JobObjectBasicAndIoAccountingInformation = 8,
    JobObjectExtendedLimitInformation = 9,
    JobObjectJobSetInformation = 10,
    JobObjectGroupInformation = 11,
    JobObjectNotificationLimitInformation = 12,
    JobObjectLimitViolationInformation = 13,
    JobObjectGroupInformationEx = 14,
    JobObjectCpuRateControlInformation = 15,
    JobObjectCompletionFilter = 16,
    JobObjectCompletionCounter = 17,
    JobObjectNetRateControlInformation = 32,
    JobObjectNotificationLimitInformation2 = 33,
    JobObjectLimitViolationInformation2 = 34,
    JobObjectCreateSilo = 35,
    JobObjectSiloBasicInformation = 36,
}

/// <summary>DWMWINDOWATTRIBUTE.</summary>
public enum DWMWINDOWATTRIBUTE
{
    DWMWA_NCRENDERING_ENABLED = 1,
    DWMWA_NCRENDERING_POLICY = 2,
    DWMWA_TRANSITIONS_FORCEDISABLED = 3,
    DWMWA_ALLOW_NCPAINT = 4,
    DWMWA_CAPTION_BUTTON_BOUNDS = 5,
    DWMWA_NONCLIENT_RTL_LAYOUT = 6,
    DWMWA_FORCE_ICONIC_REPRESENTATION = 7,
    DWMWA_FLIP3D_POLICY = 8,
    DWMWA_EXTENDED_FRAME_BOUNDS = 9,
    DWMWA_HAS_ICONIC_BITMAP = 10,
    DWMWA_DISALLOW_PEEK = 11,
    DWMWA_EXCLUDED_FROM_PEEK = 12,
    DWMWA_CLOAK = 13,
    DWMWA_CLOAKED = 14,
    DWMWA_FREEZE_REPRESENTATION = 15,
    DWMWA_PASSIVE_UPDATE_MODE = 16,
    DWMWA_USE_HOSTBACKDROPBRUSH = 17,
    DWMWA_USE_IMMERSIVE_DARK_MODE = 20,
    DWMWA_WINDOW_CORNER_PREFERENCE = 33,
    DWMWA_BORDER_COLOR = 34,
    DWMWA_CAPTION_COLOR = 35,
    DWMWA_TEXT_COLOR = 36,
    DWMWA_VISIBLE_FRAME_BORDER_THICKNESS = 37,
    DWMWA_SYSTEMBACKDROP_TYPE = 38,
}

/// <summary>TOKEN_INFORMATION_CLASS.</summary>
public enum TOKEN_INFORMATION_CLASS
{
    TokenUser = 1,
    TokenGroups = 2,
    TokenPrivileges = 3,
    TokenOwner = 4,
    TokenPrimaryGroup = 5,
    TokenDefaultDacl = 6,
    TokenSource = 7,
    TokenType = 8,
    TokenImpersonationLevel = 9,
    TokenStatistics = 10,
    TokenRestrictedSids = 11,
    TokenSessionId = 12,
    TokenGroupsAndPrivileges = 13,
    TokenSessionReference = 14,
    TokenSandBoxInert = 15,
    TokenAuditPolicy = 16,
    TokenOrigin = 17,
    TokenElevationType = 18,
    TokenLinkedToken = 19,
    TokenElevation = 20,
    TokenHasRestrictions = 21,
    TokenAccessInformation = 22,
    TokenVirtualizationAllowed = 23,
    TokenVirtualizationEnabled = 24,
    TokenIntegrityLevel = 25,
    TokenUIAccess = 26,
    TokenMandatoryPolicy = 27,
    TokenLogonSid = 28,
    TokenIsAppContainer = 29,
    TokenCapabilities = 30,
    TokenAppContainerSid = 31,
    TokenAppContainerNumber = 32,
    TokenUserClaimAttributes = 33,
    TokenDeviceClaimAttributes = 34,
    TokenRestrictedUserClaimAttributes = 35,
    TokenRestrictedDeviceClaimAttributes = 36,
    TokenDeviceGroups = 37,
    TokenRestrictedDeviceGroups = 38,
    TokenSecurityAttributes = 39,
    TokenIsRestricted = 40,
    TokenProcessTrustLevel = 41,
    TokenPrivateNameSpace = 42,
    TokenSingletonAttributes = 43,
    TokenBnoIsolation = 44,
    TokenChildProcessFlags = 45,
    TokenIsLessPrivilegedAppContainer = 46,
    TokenIsSandboxed = 47,
}

/// <summary>TOKEN_TYPE.</summary>
public enum TOKEN_TYPE
{
    TokenPrimary = 1,
    TokenImpersonation = 2,
}

/// <summary>SECURITY_IMPERSONATION_LEVEL.</summary>
public enum SECURITY_IMPERSONATION_LEVEL
{
    SecurityAnonymous = 0,
    SecurityIdentification = 1,
    SecurityImpersonation = 2,
    SecurityDelegation = 3,
}

/// <summary>SID_NAME_USE.</summary>
public enum SID_NAME_USE
{
    SidTypeUser = 1,
    SidTypeGroup = 2,
    SidTypeDomain = 3,
    SidTypeAlias = 4,
    SidTypeWellKnownGroup = 5,
    SidTypeDeletedAccount = 6,
    SidTypeInvalid = 7,
    SidTypeUnknown = 8,
    SidTypeComputer = 9,
    SidTypeLabel = 10,
    SidTypeLogonSession = 11,
}

/// <summary>COMPUTER_NAME_FORMAT for GetComputerNameExW.</summary>
public enum COMPUTER_NAME_FORMAT
{
    ComputerNameNetBIOS = 0,
    ComputerNameDnsHostname = 1,
    ComputerNameDnsDomain = 2,
    ComputerNameDnsFullyQualified = 3,
    ComputerNamePhysicalNetBIOS = 4,
    ComputerNamePhysicalDnsHostname = 5,
    ComputerNamePhysicalDnsDomain = 6,
    ComputerNamePhysicalDnsFullyQualified = 7,
    ComputerNameMax = 8,
}

/// <summary>Wait results returned by WaitForSingleObject.</summary>
public enum WaitResult : uint
{
    WAIT_OBJECT_0 = 0x00000000,
    WAIT_ABANDONED = 0x00000080,
    WAIT_TIMEOUT = 0x00000102,
    WAIT_FAILED = 0xFFFFFFFF,
}

// ---------------------------------------------------------------------------------------------
// Structs (all blittable: only primitives, nint, nested blittable structs and fixed ushort buffers).
// Sizes are the x64 layouts from the Windows SDK headers.
// ---------------------------------------------------------------------------------------------

/// <summary>POINT.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X;
    public int Y;

    public POINT(int x, int y)
    {
        X = x;
        Y = y;
    }
}

/// <summary>RECT (left/top/right/bottom, right and bottom exclusive).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public RECT(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public readonly int Width => Right - Left;

    public readonly int Height => Bottom - Top;

    public static RECT FromXYWH(int x, int y, int width, int height) => new(x, y, x + width, y + height);
}

/// <summary>MSG.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MSG
{
    public nint hwnd;
    public uint message;
    public nint wParam;
    public nint lParam;
    public uint time;
    public POINT pt;
}

/// <summary>KBDLLHOOKSTRUCT (lParam of a WH_KEYBOARD_LL hook).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct KBDLLHOOKSTRUCT
{
    public uint vkCode;
    public uint scanCode;
    public uint flags;
    public uint time;
    public nuint dwExtraInfo;

    public readonly bool IsExtended => (flags & NativeConst.LLKHF_EXTENDED) != 0;

    public readonly bool IsInjected => (flags & NativeConst.LLKHF_INJECTED) != 0;

    public readonly bool AltDown => (flags & NativeConst.LLKHF_ALTDOWN) != 0;

    public readonly bool IsKeyUp => (flags & NativeConst.LLKHF_UP) != 0;
}

/// <summary>MSLLHOOKSTRUCT (lParam of a WH_MOUSE_LL hook).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MSLLHOOKSTRUCT
{
    public POINT pt;
    public uint mouseData;
    public uint flags;
    public uint time;
    public nuint dwExtraInfo;

    public readonly bool IsInjected => (flags & NativeConst.LLMHF_INJECTED) != 0;

    /// <summary>Wheel delta (WM_MOUSEWHEEL / WM_MOUSEHWHEEL) or X button (WM_XBUTTON*) from the high word of <see cref="mouseData"/>.</summary>
    public readonly short HighWord => unchecked((short)(mouseData >> 16));
}

/// <summary>LASTINPUTINFO for GetLastInputInfo.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct LASTINPUTINFO
{
    public uint cbSize;
    public uint dwTime;

    public static LASTINPUTINFO Create() => new() { cbSize = (uint)Unsafe.SizeOf<LASTINPUTINFO>() };
}

/// <summary>WINDOWPLACEMENT.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct WINDOWPLACEMENT
{
    public uint length;
    public uint flags;
    public uint showCmd;
    public POINT ptMinPosition;
    public POINT ptMaxPosition;
    public RECT rcNormalPosition;

    public static WINDOWPLACEMENT Create() => new() { length = (uint)Unsafe.SizeOf<WINDOWPLACEMENT>() };
}

/// <summary>MONITORINFOEXW (104 bytes). Pass through <see cref="User32.GetMonitorInfoW(nint, ref MONITORINFOEXW)"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MONITORINFOEXW
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    public fixed ushort szDevice[32];

    public static MONITORINFOEXW Create() => new() { cbSize = (uint)Unsafe.SizeOf<MONITORINFOEXW>() };

    public readonly bool IsPrimary => (dwFlags & NativeConst.MONITORINFOF_PRIMARY) != 0;

    /// <summary>Device name, e.g. <c>\\.\DISPLAY1</c>.</summary>
    public string DeviceName
    {
        get
        {
            fixed (ushort* p = szDevice)
            {
                return NativeString.FromFixed(p, 32);
            }
        }
    }
}

/// <summary>DEVMODEW (220 bytes), display-device variant of the union.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DEVMODEW
{
    public fixed ushort dmDeviceName[32];
    public ushort dmSpecVersion;
    public ushort dmDriverVersion;
    public ushort dmSize;
    public ushort dmDriverExtra;
    public uint dmFields;
    public int dmPositionX;
    public int dmPositionY;
    public uint dmDisplayOrientation;
    public uint dmDisplayFixedOutput;
    public short dmColor;
    public short dmDuplex;
    public short dmYResolution;
    public short dmTTOption;
    public short dmCollate;
    public fixed ushort dmFormName[32];
    public ushort dmLogPixels;
    public uint dmBitsPerPel;
    public uint dmPelsWidth;
    public uint dmPelsHeight;
    public uint dmDisplayFlags;
    public uint dmDisplayFrequency;
    public uint dmICMMethod;
    public uint dmICMIntent;
    public uint dmMediaType;
    public uint dmDitherType;
    public uint dmReserved1;
    public uint dmReserved2;
    public uint dmPanningWidth;
    public uint dmPanningHeight;

    public static DEVMODEW Create() => new() { dmSize = (ushort)Unsafe.SizeOf<DEVMODEW>() };

    public string DeviceName
    {
        get
        {
            fixed (ushort* p = dmDeviceName)
            {
                return NativeString.FromFixed(p, 32);
            }
        }
    }
}

/// <summary>STARTUPINFOW (104 bytes on x64). String members are raw LPWSTR pointers (allocate with Marshal.StringToHGlobalUni).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct STARTUPINFOW
{
    public uint cb;
    public nint lpReserved;
    public nint lpDesktop;
    public nint lpTitle;
    public uint dwX;
    public uint dwY;
    public uint dwXSize;
    public uint dwYSize;
    public uint dwXCountChars;
    public uint dwYCountChars;
    public uint dwFillAttribute;
    public uint dwFlags;
    public ushort wShowWindow;
    public ushort cbReserved2;
    public nint lpReserved2;
    public nint hStdInput;
    public nint hStdOutput;
    public nint hStdError;

    public static STARTUPINFOW Create() => new() { cb = (uint)Unsafe.SizeOf<STARTUPINFOW>() };
}

/// <summary>PROCESS_INFORMATION. Both handles must be closed with CloseHandle.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PROCESS_INFORMATION
{
    public nint hProcess;
    public nint hThread;
    public uint dwProcessId;
    public uint dwThreadId;
}

/// <summary>SECURITY_ATTRIBUTES.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SECURITY_ATTRIBUTES
{
    public uint nLength;
    public nint lpSecurityDescriptor;
    public int bInheritHandle;

    public static SECURITY_ATTRIBUTES Create(bool inheritHandle = false) => new()
    {
        nLength = (uint)Unsafe.SizeOf<SECURITY_ATTRIBUTES>(),
        bInheritHandle = inheritHandle ? 1 : 0,
    };
}

/// <summary>LUID.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct LUID
{
    public uint LowPart;
    public int HighPart;
}

/// <summary>LUID_AND_ATTRIBUTES.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct LUID_AND_ATTRIBUTES
{
    public LUID Luid;
    public uint Attributes;
}

/// <summary>TOKEN_PRIVILEGES with a single entry (the common AdjustTokenPrivileges case).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TOKEN_PRIVILEGES
{
    public uint PrivilegeCount;
    public LUID_AND_ATTRIBUTES Privileges;

    public static TOKEN_PRIVILEGES Single(LUID luid, uint attributes) => new()
    {
        PrivilegeCount = 1,
        Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = attributes },
    };
}

/// <summary>SID_AND_ATTRIBUTES.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SID_AND_ATTRIBUTES
{
    public nint Sid;
    public uint Attributes;
}

/// <summary>TOKEN_USER.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TOKEN_USER
{
    public SID_AND_ATTRIBUTES User;
}

/// <summary>TOKEN_MANDATORY_LABEL (TokenIntegrityLevel).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TOKEN_MANDATORY_LABEL
{
    public SID_AND_ATTRIBUTES Label;
}

/// <summary>TOKEN_ELEVATION.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TOKEN_ELEVATION
{
    public uint TokenIsElevated;
}

/// <summary>WTS_SESSION_INFOW.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct WTS_SESSION_INFOW
{
    public uint SessionId;
    public nint pWinStationName;
    public WTS_CONNECTSTATE_CLASS State;

    /// <summary>Window station name, e.g. "Console" or "Services".</summary>
    public readonly string WinStationName => pWinStationName == 0 ? string.Empty : Marshal.PtrToStringUni(pWinStationName) ?? string.Empty;
}

/// <summary>IO_COUNTERS.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct IO_COUNTERS
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

/// <summary>JOBOBJECT_BASIC_LIMIT_INFORMATION.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
{
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public nuint MinimumWorkingSetSize;
    public nuint MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public nuint Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
}

/// <summary>JOBOBJECT_EXTENDED_LIMIT_INFORMATION.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
{
    public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
    public IO_COUNTERS IoInfo;
    public nuint ProcessMemoryLimit;
    public nuint JobMemoryLimit;
    public nuint PeakProcessMemoryUsed;
    public nuint PeakJobMemoryUsed;
}

/// <summary>JOBOBJECT_BASIC_PROCESS_ID_LIST header (followed by <c>NumberOfProcessIdsInList</c> ULONG_PTR entries).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct JOBOBJECT_BASIC_PROCESS_ID_LIST
{
    public uint NumberOfAssignedProcesses;
    public uint NumberOfProcessIdsInList;
    public nuint ProcessIdList;
}

/// <summary>JOBOBJECT_BASIC_UI_RESTRICTIONS.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct JOBOBJECT_BASIC_UI_RESTRICTIONS
{
    public uint UIRestrictionsClass;
}

/// <summary>APPBARDATA (SHAppBarMessage).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct APPBARDATA
{
    public uint cbSize;
    public nint hWnd;
    public uint uCallbackMessage;
    public uint uEdge;
    public RECT rc;
    public nint lParam;

    public static APPBARDATA Create(nint hWnd = 0) => new() { cbSize = (uint)Unsafe.SizeOf<APPBARDATA>(), hWnd = hWnd };
}

/// <summary>SHELLEXECUTEINFOW. String members are raw LPCWSTR pointers; prefer <see cref="Shell32.ShellExecute"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SHELLEXECUTEINFOW
{
    public uint cbSize;
    public uint fMask;
    public nint hwnd;
    public nint lpVerb;
    public nint lpFile;
    public nint lpParameters;
    public nint lpDirectory;
    public int nShow;
    public nint hInstApp;
    public nint lpIDList;
    public nint lpClass;
    public nint hkeyClass;
    public uint dwHotKey;
    public nint hIcon;
    public nint hProcess;

    public static SHELLEXECUTEINFOW Create() => new() { cbSize = (uint)Unsafe.SizeOf<SHELLEXECUTEINFOW>() };
}

/// <summary>MEMORYSTATUSEX (64 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MEMORYSTATUSEX
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;

    public static MEMORYSTATUSEX Create() => new() { dwLength = (uint)Unsafe.SizeOf<MEMORYSTATUSEX>() };
}

/// <summary>SYSTEM_INFO.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SYSTEM_INFO
{
    public ushort wProcessorArchitecture;
    public ushort wReserved;
    public uint dwPageSize;
    public nint lpMinimumApplicationAddress;
    public nint lpMaximumApplicationAddress;
    public nuint dwActiveProcessorMask;
    public uint dwNumberOfProcessors;
    public uint dwProcessorType;
    public uint dwAllocationGranularity;
    public ushort wProcessorLevel;
    public ushort wProcessorRevision;
}

/// <summary>FILETIME.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct FILETIME
{
    public uint dwLowDateTime;
    public uint dwHighDateTime;

    public readonly long ToTicks() => ((long)dwHighDateTime << 32) | dwLowDateTime;

    public readonly DateTime ToDateTimeUtc() => DateTime.FromFileTimeUtc(ToTicks());
}

/// <summary>CREDENTIALW (Credential Manager). Read via <see cref="SafeCredentialsHandle.ReadCredentials"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CREDENTIALW
{
    public uint Flags;
    public uint Type;
    public nint TargetName;
    public nint Comment;
    public FILETIME LastWritten;
    public uint CredentialBlobSize;
    public nint CredentialBlob;
    public uint Persist;
    public uint AttributeCount;
    public nint Attributes;
    public nint TargetAlias;
    public nint UserName;

    public readonly string TargetNameString => TargetName == 0 ? string.Empty : Marshal.PtrToStringUni(TargetName) ?? string.Empty;

    public readonly string UserNameString => UserName == 0 ? string.Empty : Marshal.PtrToStringUni(UserName) ?? string.Empty;
}

/// <summary>SERVICE_STATUS.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SERVICE_STATUS
{
    public uint dwServiceType;
    public uint dwCurrentState;
    public uint dwControlsAccepted;
    public uint dwWin32ExitCode;
    public uint dwServiceSpecificExitCode;
    public uint dwCheckPoint;
    public uint dwWaitHint;
}

/// <summary>PROFILEINFOW (LoadUserProfileW). String members are raw LPWSTR pointers.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PROFILEINFOW
{
    public uint dwSize;
    public uint dwFlags;
    public nint lpUserName;
    public nint lpProfilePath;
    public nint lpDefaultPath;
    public nint lpServerName;
    public nint lpPolicyPath;
    public nint hProfile;

    public static PROFILEINFOW Create() => new() { dwSize = (uint)Unsafe.SizeOf<PROFILEINFOW>() };
}

/// <summary>MOUSEINPUT (member of <see cref="INPUT"/>).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MOUSEINPUT
{
    public int dx;
    public int dy;
    public uint mouseData;
    public uint dwFlags;
    public uint time;
    public nuint dwExtraInfo;
}

/// <summary>KEYBDINPUT (member of <see cref="INPUT"/>).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT
{
    public ushort wVk;
    public ushort wScan;
    public uint dwFlags;
    public uint time;
    public nuint dwExtraInfo;
}

/// <summary>HARDWAREINPUT (member of <see cref="INPUT"/>).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct HARDWAREINPUT
{
    public uint uMsg;
    public ushort wParamL;
    public ushort wParamH;
}

/// <summary>Union member of <see cref="INPUT"/>.</summary>
[StructLayout(LayoutKind.Explicit)]
public struct INPUTUNION
{
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
    [FieldOffset(0)] public HARDWAREINPUT hi;
}

/// <summary>INPUT for SendInput (40 bytes on x64).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct INPUT
{
    public uint type;
    public INPUTUNION u;

    /// <summary>Size in bytes to pass as <c>cbSize</c> to SendInput.</summary>
    public static int Size => Unsafe.SizeOf<INPUT>();

    public static INPUT Keyboard(ushort vk, ushort scan, uint flags) => new()
    {
        type = NativeConst.INPUT_KEYBOARD,
        u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };

    public static INPUT Mouse(int dx, int dy, uint mouseData, uint flags) => new()
    {
        type = NativeConst.INPUT_MOUSE,
        u = new INPUTUNION { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = mouseData, dwFlags = flags } },
    };
}

/// <summary>NOTIFYICONDATAW (Shell_NotifyIconW), Vista+ layout.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NOTIFYICONDATAW
{
    public uint cbSize;
    public nint hWnd;
    public uint uID;
    public uint uFlags;
    public uint uCallbackMessage;
    public nint hIcon;
    public fixed ushort szTip[128];
    public uint dwState;
    public uint dwStateMask;
    public fixed ushort szInfo[256];
    public uint uVersion;
    public fixed ushort szInfoTitle[64];
    public uint dwInfoFlags;
    public Guid guidItem;
    public nint hBalloonIcon;

    public static NOTIFYICONDATAW Create() => new() { cbSize = (uint)Unsafe.SizeOf<NOTIFYICONDATAW>() };

    public void SetTip(string? tip)
    {
        fixed (ushort* p = szTip)
        {
            NativeString.ToFixed(tip, p, 128);
        }
    }

    public void SetInfo(string? title, string? body)
    {
        fixed (ushort* p = szInfoTitle)
        {
            NativeString.ToFixed(title, p, 64);
        }

        fixed (ushort* p = szInfo)
        {
            NativeString.ToFixed(body, p, 256);
        }
    }
}

/// <summary>BITMAPINFOHEADER (GetDIBits / CreateDIBSection).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;

    /// <summary>Top-down 32bpp BGRA header for a width x height capture.</summary>
    public static BITMAPINFOHEADER Bgra32TopDown(int width, int height) => new()
    {
        biSize = (uint)Unsafe.SizeOf<BITMAPINFOHEADER>(),
        biWidth = width,
        biHeight = -height,
        biPlanes = 1,
        biBitCount = 32,
        biCompression = NativeConst.BI_RGB,
    };
}

/// <summary>WNDCLASSEXW (80 bytes on x64); string members are raw LPCWSTR pointers, <c>lpfnWndProc</c> a function pointer.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct WNDCLASSEXW
{
    public uint cbSize;
    public uint style;
    public nint lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public nint hInstance;
    public nint hIcon;
    public nint hCursor;
    public nint hbrBackground;
    public nint lpszMenuName;
    public nint lpszClassName;
    public nint hIconSm;

    public static WNDCLASSEXW Create() => new() { cbSize = (uint)Unsafe.SizeOf<WNDCLASSEXW>() };
}

/// <summary>PROCESSENTRY32W (568 bytes on x64) for <see cref="Kernel32.Process32FirstW"/> / <see cref="Kernel32.Process32NextW"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct PROCESSENTRY32W
{
    public uint dwSize;
    public uint cntUsage;
    public uint th32ProcessID;
    public nuint th32DefaultHeapID;
    public uint th32ModuleID;
    public uint cntThreads;
    public uint th32ParentProcessID;
    public int pcPriClassBase;
    public uint dwFlags;
    public fixed ushort szExeFile[NativeConst.MAX_PATH];

    public static PROCESSENTRY32W Create() => new() { dwSize = (uint)Unsafe.SizeOf<PROCESSENTRY32W>() };

    /// <summary>Image file name, e.g. <c>notepad.exe</c>.</summary>
    public string ExeFile
    {
        get
        {
            fixed (ushort* p = szExeFile)
            {
                return NativeString.FromFixed(p, NativeConst.MAX_PATH);
            }
        }
    }
}

/// <summary>WTSINFOW (216 bytes) returned for <see cref="WTS_INFO_CLASS.WTSSessionInfo"/>. Times are FILETIME ticks.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct WTSINFOW
{
    public WTS_CONNECTSTATE_CLASS State;
    public uint SessionId;
    public uint IncomingBytes;
    public uint OutgoingBytes;
    public uint IncomingFrames;
    public uint OutgoingFrames;
    public uint IncomingCompressedBytes;
    public uint OutgoingCompressedBytes;
    public fixed ushort WinStationName[32];
    public fixed ushort Domain[17];
    public fixed ushort UserName[21];
    public long ConnectTime;
    public long DisconnectTime;
    public long LastInputTime;
    public long LogonTime;
    public long CurrentTime;
}

/// <summary>NETRESOURCEW for <see cref="Mpr.WNetAddConnection2W"/>; string members are raw LPWSTR pointers.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NETRESOURCEW
{
    public uint dwScope;
    public uint dwType;
    public uint dwDisplayType;
    public uint dwUsage;
    public nint lpLocalName;
    public nint lpRemoteName;
    public nint lpComment;
    public nint lpProvider;
}

/// <summary>LSA_UNICODE_STRING (lengths in bytes, Buffer not necessarily null-terminated).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct LSA_UNICODE_STRING
{
    public ushort Length;
    public ushort MaximumLength;
    public nint Buffer;
}

/// <summary>LSA_OBJECT_ATTRIBUTES (48 bytes on x64); all members zero except Length.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct LSA_OBJECT_ATTRIBUTES
{
    public uint Length;
    public nint RootDirectory;
    public nint ObjectName;
    public uint Attributes;
    public nint SecurityDescriptor;
    public nint SecurityQualityOfService;

    public static LSA_OBJECT_ATTRIBUTES Create() => new() { Length = (uint)Unsafe.SizeOf<LSA_OBJECT_ATTRIBUTES>() };
}

/// <summary>KNOWNFOLDERID GUIDs for SHGetKnownFolderPath.</summary>
[SupportedOSPlatform("windows")]
public static class KnownFolders
{
    public static readonly Guid FOLDERID_Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    public static readonly Guid FOLDERID_Documents = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
    public static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    public static readonly Guid FOLDERID_Pictures = new("33E28130-4E1E-4676-835A-98395C3BC3BB");
    public static readonly Guid FOLDERID_Videos = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");
    public static readonly Guid FOLDERID_Music = new("4BD8D571-6D19-48D3-BE97-422220080E43");
    public static readonly Guid FOLDERID_SavedGames = new("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4");
    public static readonly Guid FOLDERID_LocalAppData = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
    public static readonly Guid FOLDERID_LocalAppDataLow = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");
    public static readonly Guid FOLDERID_RoamingAppData = new("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D");
    public static readonly Guid FOLDERID_ProgramData = new("62AB5D82-FDC1-4DC3-A9DD-070D1D495D97");
    public static readonly Guid FOLDERID_ProgramFiles = new("905E63B6-C1BF-494E-B29C-65B732D3D21A");
    public static readonly Guid FOLDERID_ProgramFilesX86 = new("7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E");
    public static readonly Guid FOLDERID_Profile = new("5E6C858F-0E22-4760-9AFE-EA3317B67173");
    public static readonly Guid FOLDERID_Public = new("DFDF76A2-C82A-4D63-906A-5644AC457385");
    public static readonly Guid FOLDERID_PublicDesktop = new("C4AA340D-F20F-4863-AFEF-F87EF2E6BA25");
    public static readonly Guid FOLDERID_Startup = new("B97D20BB-F46A-4C97-BA10-5E3608430854");
    public static readonly Guid FOLDERID_CommonStartup = new("82A5EA35-D9CD-47C5-9629-E15D2F714E6E");
    public static readonly Guid FOLDERID_StartMenu = new("625B53C3-AB48-4EC1-BA1F-A1EF4146FC19");
    public static readonly Guid FOLDERID_Windows = new("F38BF404-1D43-42F2-9305-67DE0B28FC23");
    public static readonly Guid FOLDERID_System = new("1AC14E77-02E7-4E5D-B744-2EB1AE5198B7");
}

// ---------------------------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------------------------

/// <summary>Win32 constants used by the P/Invoke layer, named exactly as in the SDK headers.</summary>
[SupportedOSPlatform("windows")]
public static class NativeConst
{
    // ---- generic -----------------------------------------------------------------------------
    public const uint INFINITE = 0xFFFFFFFF;
    public const nint INVALID_HANDLE_VALUE = -1;
    public const int MAX_PATH = 260;
    public const int UNLEN = 256;
    public const int TRUE = 1;
    public const int FALSE = 0;

    // ---- Win32 error codes -------------------------------------------------------------------
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_HANDLE = 6;
    public const int ERROR_NOT_ENOUGH_MEMORY = 8;
    public const int ERROR_INVALID_DATA = 13;
    public const int ERROR_NOT_READY = 21;
    public const int ERROR_SHARING_VIOLATION = 32;
    public const int ERROR_NOT_SUPPORTED = 50;
    public const int ERROR_ALREADY_ASSIGNED = 85;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_ALREADY_EXISTS = 183;
    public const int ERROR_MORE_DATA = 234;
    public const int ERROR_NO_MORE_ITEMS = 259;
    public const int ERROR_NONE_MAPPED = 1332;
    public const int ERROR_NO_TOKEN = 1008;
    public const int ERROR_PRIVILEGE_NOT_HELD = 1314;
    public const int ERROR_LOGON_FAILURE = 1326;
    public const int ERROR_NOT_ALL_ASSIGNED = 1300;
    public const int ERROR_NO_SUCH_LOGON_SESSION = 1312;
    public const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    public const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
    public const int ERROR_SERVICE_NOT_ACTIVE = 1062;
    public const int ERROR_NOT_FOUND = 1168;
    public const int ERROR_SESSION_CREDENTIAL_CONFLICT = 1219;
    public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    public const int ERROR_CLASS_ALREADY_EXISTS = 1410;
    public const int ERROR_INVALID_WINDOW_HANDLE = 1400;
    public const int ERROR_TIMEOUT = 1460;
    public const int ERROR_SHUTDOWN_IN_PROGRESS = 1115;
    public const int ERROR_NO_SHUTDOWN_IN_PROGRESS = 1116;
    public const int ERROR_NOT_CONNECTED = 2250;

    // ---- registry pseudo-handles (sign-extended on x64) ---------------------------------------
    public const nint HKEY_CLASSES_ROOT = unchecked((int)0x80000000);
    public const nint HKEY_CURRENT_USER = unchecked((int)0x80000001);
    public const nint HKEY_LOCAL_MACHINE = unchecked((int)0x80000002);
    public const nint HKEY_USERS = unchecked((int)0x80000003);

    // ---- Toolhelp32 ---------------------------------------------------------------------------
    public const uint TH32CS_SNAPPROCESS = 0x00000002;

    // ---- LSA policy access --------------------------------------------------------------------
    public const uint POLICY_GET_PRIVATE_INFORMATION = 0x00000004;
    public const uint POLICY_CREATE_SECRET = 0x00000020;

    // ---- WNet ---------------------------------------------------------------------------------
    public const uint RESOURCETYPE_DISK = 0x00000001;
    public const uint CONNECT_TEMPORARY = 0x00000004;

    // ---- hooks --------------------------------------------------------------------------------
    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;
    public const int WH_SHELL = 10;
    public const int WH_CBT = 5;
    public const int HC_ACTION = 0;

    public const uint LLKHF_EXTENDED = 0x01;
    public const uint LLKHF_LOWER_IL_INJECTED = 0x02;
    public const uint LLKHF_INJECTED = 0x10;
    public const uint LLKHF_ALTDOWN = 0x20;
    public const uint LLKHF_UP = 0x80;
    public const uint LLMHF_INJECTED = 0x01;
    public const uint LLMHF_LOWER_IL_INJECTED = 0x02;

    // ---- window messages ---------------------------------------------------------------------
    public const uint WM_NULL = 0x0000;
    public const uint WM_CREATE = 0x0001;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_MOVE = 0x0003;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_ACTIVATE = 0x0006;
    public const uint WM_SETFOCUS = 0x0007;
    public const uint WM_KILLFOCUS = 0x0008;
    public const uint WM_SETTEXT = 0x000C;
    public const uint WM_GETTEXT = 0x000D;
    public const uint WM_GETTEXTLENGTH = 0x000E;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_QUERYENDSESSION = 0x0011;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_ENDSESSION = 0x0016;
    public const uint WM_SHOWWINDOW = 0x0018;
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint WM_ACTIVATEAPP = 0x001C;
    public const uint WM_SETCURSOR = 0x0020;
    public const uint WM_GETMINMAXINFO = 0x0024;
    public const uint WM_WINDOWPOSCHANGING = 0x0046;
    public const uint WM_WINDOWPOSCHANGED = 0x0047;
    public const uint WM_POWER = 0x0048;
    public const uint WM_COPYDATA = 0x004A;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_NCCREATE = 0x0081;
    public const uint WM_NCDESTROY = 0x0082;
    public const uint WM_NCHITTEST = 0x0084;
    public const uint WM_NCACTIVATE = 0x0086;
    public const uint WM_NCLBUTTONDOWN = 0x00A1;
    public const uint WM_KEYFIRST = 0x0100;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_CHAR = 0x0102;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;
    public const uint WM_SYSCHAR = 0x0106;
    public const uint WM_KEYLAST = 0x0109;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_SYSCOMMAND = 0x0112;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_MOUSEFIRST = 0x0200;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_RBUTTONDBLCLK = 0x0206;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_MBUTTONUP = 0x0208;
    public const uint WM_MBUTTONDBLCLK = 0x0209;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_XBUTTONDOWN = 0x020B;
    public const uint WM_XBUTTONUP = 0x020C;
    public const uint WM_XBUTTONDBLCLK = 0x020D;
    public const uint WM_MOUSEHWHEEL = 0x020E;
    public const uint WM_MOUSELAST = 0x020E;
    public const uint WM_DEVICECHANGE = 0x0219;
    public const uint WM_POWERBROADCAST = 0x0218;
    public const uint WM_WTSSESSION_CHANGE = 0x02B1;
    public const uint WM_DWMCOMPOSITIONCHANGED = 0x031E;
    public const uint WM_USER = 0x0400;
    public const uint WM_APP = 0x8000;

    public const uint PM_NOREMOVE = 0x0000;
    public const uint PM_REMOVE = 0x0001;
    public const uint PM_NOYIELD = 0x0002;

    public const nint SC_CLOSE = 0xF060;
    public const nint SC_MINIMIZE = 0xF020;
    public const nint SC_MAXIMIZE = 0xF030;
    public const nint SC_RESTORE = 0xF120;
    public const nint SC_MONITORPOWER = 0xF170;
    public const nint SC_SCREENSAVE = 0xF140;
    public const nint SC_TASKLIST = 0xF130;

    // ---- WTS session change (wParam of WM_WTSSESSION_CHANGE / SERVICE_CONTROL_SESSIONCHANGE) --
    public const uint WTS_CONSOLE_CONNECT = 0x1;
    public const uint WTS_CONSOLE_DISCONNECT = 0x2;
    public const uint WTS_REMOTE_CONNECT = 0x3;
    public const uint WTS_REMOTE_DISCONNECT = 0x4;
    public const uint WTS_SESSION_LOGON = 0x5;
    public const uint WTS_SESSION_LOGOFF = 0x6;
    public const uint WTS_SESSION_LOCK = 0x7;
    public const uint WTS_SESSION_UNLOCK = 0x8;
    public const uint WTS_SESSION_REMOTE_CONTROL = 0x9;
    public const uint WTS_SESSION_CREATE = 0xA;
    public const uint WTS_SESSION_TERMINATE = 0xB;
    public const uint NOTIFY_FOR_THIS_SESSION = 0;
    public const uint NOTIFY_FOR_ALL_SESSIONS = 1;
    public const nint WTS_CURRENT_SERVER_HANDLE = 0;
    public const uint WTS_CURRENT_SESSION = 0xFFFFFFFF;
    public const uint WTS_ANY_SESSION = 0xFFFFFFFE;

    // ---- virtual keys -------------------------------------------------------------------------
    public const uint VK_LBUTTON = 0x01;
    public const uint VK_RBUTTON = 0x02;
    public const uint VK_CANCEL = 0x03;
    public const uint VK_MBUTTON = 0x04;
    public const uint VK_XBUTTON1 = 0x05;
    public const uint VK_XBUTTON2 = 0x06;
    public const uint VK_BACK = 0x08;
    public const uint VK_TAB = 0x09;
    public const uint VK_CLEAR = 0x0C;
    public const uint VK_RETURN = 0x0D;
    public const uint VK_SHIFT = 0x10;
    public const uint VK_CONTROL = 0x11;
    public const uint VK_MENU = 0x12;
    public const uint VK_PAUSE = 0x13;
    public const uint VK_CAPITAL = 0x14;
    public const uint VK_ESCAPE = 0x1B;
    public const uint VK_SPACE = 0x20;
    public const uint VK_PRIOR = 0x21;
    public const uint VK_NEXT = 0x22;
    public const uint VK_END = 0x23;
    public const uint VK_HOME = 0x24;
    public const uint VK_LEFT = 0x25;
    public const uint VK_UP = 0x26;
    public const uint VK_RIGHT = 0x27;
    public const uint VK_DOWN = 0x28;
    public const uint VK_SNAPSHOT = 0x2C;
    public const uint VK_INSERT = 0x2D;
    public const uint VK_DELETE = 0x2E;
    public const uint VK_LWIN = 0x5B;
    public const uint VK_RWIN = 0x5C;
    public const uint VK_APPS = 0x5D;
    public const uint VK_SLEEP = 0x5F;
    public const uint VK_NUMPAD0 = 0x60;
    public const uint VK_F1 = 0x70;
    public const uint VK_F2 = 0x71;
    public const uint VK_F3 = 0x72;
    public const uint VK_F4 = 0x73;
    public const uint VK_F5 = 0x74;
    public const uint VK_F6 = 0x75;
    public const uint VK_F7 = 0x76;
    public const uint VK_F8 = 0x77;
    public const uint VK_F9 = 0x78;
    public const uint VK_F10 = 0x79;
    public const uint VK_F11 = 0x7A;
    public const uint VK_F12 = 0x7B;
    public const uint VK_NUMLOCK = 0x90;
    public const uint VK_SCROLL = 0x91;
    public const uint VK_LSHIFT = 0xA0;
    public const uint VK_RSHIFT = 0xA1;
    public const uint VK_LCONTROL = 0xA2;
    public const uint VK_RCONTROL = 0xA3;
    public const uint VK_LMENU = 0xA4;
    public const uint VK_RMENU = 0xA5;
    public const uint VK_BROWSER_BACK = 0xA6;
    public const uint VK_BROWSER_HOME = 0xAC;
    public const uint VK_VOLUME_MUTE = 0xAD;
    public const uint VK_VOLUME_DOWN = 0xAE;
    public const uint VK_VOLUME_UP = 0xAF;
    public const uint VK_MEDIA_NEXT_TRACK = 0xB0;
    public const uint VK_MEDIA_PREV_TRACK = 0xB1;
    public const uint VK_MEDIA_STOP = 0xB2;
    public const uint VK_MEDIA_PLAY_PAUSE = 0xB3;
    public const uint VK_LAUNCH_MAIL = 0xB4;
    public const uint VK_LAUNCH_APP1 = 0xB6;
    public const uint VK_LAUNCH_APP2 = 0xB7;

    // ---- SendInput / keybd_event -------------------------------------------------------------
    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;
    public const uint INPUT_HARDWARE = 2;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint KEYEVENTF_SCANCODE = 0x0008;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_XDOWN = 0x0080;
    public const uint MOUSEEVENTF_XUP = 0x0100;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_HWHEEL = 0x1000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const int WHEEL_DELTA = 120;

    // ---- RegisterHotKey modifiers --------------------------------------------------------------
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    // ---- ShowWindow ---------------------------------------------------------------------------
    public const int SW_HIDE = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_NORMAL = 1;
    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_SHOWMAXIMIZED = 3;
    public const int SW_MAXIMIZE = 3;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_SHOW = 5;
    public const int SW_MINIMIZE = 6;
    public const int SW_SHOWMINNOACTIVE = 7;
    public const int SW_SHOWNA = 8;
    public const int SW_RESTORE = 9;
    public const int SW_SHOWDEFAULT = 10;
    public const int SW_FORCEMINIMIZE = 11;

    // ---- SetWindowPos -------------------------------------------------------------------------
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOREDRAW = 0x0008;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_HIDEWINDOW = 0x0080;
    public const uint SWP_NOCOPYBITS = 0x0100;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_NOSENDCHANGING = 0x0400;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;
    public const nint HWND_TOP = 0;
    public const nint HWND_BOTTOM = 1;
    public const nint HWND_TOPMOST = -1;
    public const nint HWND_NOTOPMOST = -2;
    public const nint HWND_BROADCAST = 0xFFFF;
    public const nint HWND_MESSAGE = -3;

    // ---- GetWindowLongPtr indices -------------------------------------------------------------
    public const int GWL_WNDPROC = -4;
    public const int GWL_HINSTANCE = -6;
    public const int GWL_HWNDPARENT = -8;
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int GWL_USERDATA = -21;
    public const int GWL_ID = -12;

    // ---- window styles ------------------------------------------------------------------------
    public const uint WS_OVERLAPPED = 0x00000000;
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_CHILD = 0x40000000;
    public const uint WS_MINIMIZE = 0x20000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_DISABLED = 0x08000000;
    public const uint WS_CLIPSIBLINGS = 0x04000000;
    public const uint WS_CLIPCHILDREN = 0x02000000;
    public const uint WS_MAXIMIZE = 0x01000000;
    public const uint WS_CAPTION = 0x00C00000;
    public const uint WS_BORDER = 0x00800000;
    public const uint WS_DLGFRAME = 0x00400000;
    public const uint WS_VSCROLL = 0x00200000;
    public const uint WS_HSCROLL = 0x00100000;
    public const uint WS_SYSMENU = 0x00080000;
    public const uint WS_THICKFRAME = 0x00040000;
    public const uint WS_MINIMIZEBOX = 0x00020000;
    public const uint WS_MAXIMIZEBOX = 0x00010000;
    public const uint WS_OVERLAPPEDWINDOW = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
    public const uint WS_EX_DLGMODALFRAME = 0x00000001;
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_ACCEPTFILES = 0x00000010;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_WINDOWEDGE = 0x00000100;
    public const uint WS_EX_CLIENTEDGE = 0x00000200;
    public const uint WS_EX_APPWINDOW = 0x00040000;
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint WS_EX_NOACTIVATE = 0x08000000;

    // ---- SetLayeredWindowAttributes -----------------------------------------------------------
    public const uint LWA_COLORKEY = 0x00000001;
    public const uint LWA_ALPHA = 0x00000002;

    // ---- ExitWindowsEx ------------------------------------------------------------------------
    public const uint EWX_LOGOFF = 0x00000000;
    public const uint EWX_SHUTDOWN = 0x00000001;
    public const uint EWX_REBOOT = 0x00000002;
    public const uint EWX_FORCE = 0x00000004;
    public const uint EWX_POWEROFF = 0x00000008;
    public const uint EWX_FORCEIFHUNG = 0x00000010;
    public const uint EWX_RESTARTAPPS = 0x00000040;
    public const uint EWX_HYBRID_SHUTDOWN = 0x00400000;
    public const uint SHTDN_REASON_MAJOR_OTHER = 0x00000000;
    public const uint SHTDN_REASON_MAJOR_APPLICATION = 0x00040000;
    public const uint SHTDN_REASON_MAJOR_SYSTEM = 0x00050000;
    public const uint SHTDN_REASON_MINOR_OTHER = 0x00000000;
    public const uint SHTDN_REASON_MINOR_MAINTENANCE = 0x00000001;
    public const uint SHTDN_REASON_MINOR_INSTALLATION = 0x00000002;
    public const uint SHTDN_REASON_MINOR_UPGRADE = 0x00000003;
    public const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000;

    // ---- privileges ---------------------------------------------------------------------------
    public const string SE_SHUTDOWN_NAME = "SeShutdownPrivilege";
    public const string SE_REMOTE_SHUTDOWN_NAME = "SeRemoteShutdownPrivilege";
    public const string SE_TCB_NAME = "SeTcbPrivilege";
    public const string SE_DEBUG_NAME = "SeDebugPrivilege";
    public const string SE_INCREASE_QUOTA_NAME = "SeIncreaseQuotaPrivilege";
    public const string SE_ASSIGNPRIMARYTOKEN_NAME = "SeAssignPrimaryTokenPrivilege";
    public const string SE_BACKUP_NAME = "SeBackupPrivilege";
    public const string SE_RESTORE_NAME = "SeRestorePrivilege";
    public const string SE_TAKE_OWNERSHIP_NAME = "SeTakeOwnershipPrivilege";
    public const string SE_SECURITY_NAME = "SeSecurityPrivilege";
    public const string SE_LOAD_DRIVER_NAME = "SeLoadDriverPrivilege";
    public const string SE_SYSTEMTIME_NAME = "SeSystemtimePrivilege";
    public const string SE_MANAGE_VOLUME_NAME = "SeManageVolumePrivilege";
    public const string SE_IMPERSONATE_NAME = "SeImpersonatePrivilege";
    public const string SE_CHANGE_NOTIFY_NAME = "SeChangeNotifyPrivilege";
    public const uint SE_PRIVILEGE_ENABLED_BY_DEFAULT = 0x00000001;
    public const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    public const uint SE_PRIVILEGE_REMOVED = 0x00000004;
    public const uint SE_PRIVILEGE_USED_FOR_ACCESS = 0x80000000;

    // ---- access rights ------------------------------------------------------------------------
    public const uint DELETE = 0x00010000;
    public const uint READ_CONTROL = 0x00020000;
    public const uint WRITE_DAC = 0x00040000;
    public const uint WRITE_OWNER = 0x00080000;
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint STANDARD_RIGHTS_REQUIRED = 0x000F0000;
    public const uint STANDARD_RIGHTS_READ = READ_CONTROL;
    public const uint STANDARD_RIGHTS_WRITE = READ_CONTROL;
    public const uint STANDARD_RIGHTS_EXECUTE = READ_CONTROL;
    public const uint STANDARD_RIGHTS_ALL = 0x001F0000;
    public const uint SPECIFIC_RIGHTS_ALL = 0x0000FFFF;
    public const uint ACCESS_SYSTEM_SECURITY = 0x01000000;
    public const uint MAXIMUM_ALLOWED = 0x02000000;
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint GENERIC_EXECUTE = 0x20000000;
    public const uint GENERIC_ALL = 0x10000000;

    public const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    public const uint TOKEN_DUPLICATE = 0x0002;
    public const uint TOKEN_IMPERSONATE = 0x0004;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint TOKEN_QUERY_SOURCE = 0x0010;
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint TOKEN_ADJUST_GROUPS = 0x0040;
    public const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    public const uint TOKEN_ADJUST_SESSIONID = 0x0100;
    public const uint TOKEN_READ = STANDARD_RIGHTS_READ | TOKEN_QUERY;
    public const uint TOKEN_WRITE = STANDARD_RIGHTS_WRITE | TOKEN_ADJUST_PRIVILEGES | TOKEN_ADJUST_GROUPS | TOKEN_ADJUST_DEFAULT;
    public const uint TOKEN_EXECUTE = STANDARD_RIGHTS_EXECUTE;
    public const uint TOKEN_ALL_ACCESS = STANDARD_RIGHTS_REQUIRED | TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_IMPERSONATE | TOKEN_QUERY
        | TOKEN_QUERY_SOURCE | TOKEN_ADJUST_PRIVILEGES | TOKEN_ADJUST_GROUPS | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID;

    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_CREATE_THREAD = 0x0002;
    public const uint PROCESS_SET_SESSIONID = 0x0004;
    public const uint PROCESS_VM_OPERATION = 0x0008;
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint PROCESS_VM_WRITE = 0x0020;
    public const uint PROCESS_DUP_HANDLE = 0x0040;
    public const uint PROCESS_CREATE_PROCESS = 0x0080;
    public const uint PROCESS_SET_QUOTA = 0x0100;
    public const uint PROCESS_SET_INFORMATION = 0x0200;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_ALL_ACCESS = STANDARD_RIGHTS_REQUIRED | SYNCHRONIZE | 0xFFFF;

    public const uint JOB_OBJECT_ASSIGN_PROCESS = 0x0001;
    public const uint JOB_OBJECT_SET_ATTRIBUTES = 0x0002;
    public const uint JOB_OBJECT_QUERY = 0x0004;
    public const uint JOB_OBJECT_TERMINATE = 0x0008;
    public const uint JOB_OBJECT_ALL_ACCESS = STANDARD_RIGHTS_REQUIRED | SYNCHRONIZE | 0x1F;

    public const uint DESKTOP_READOBJECTS = 0x0001;
    public const uint DESKTOP_CREATEWINDOW = 0x0002;
    public const uint DESKTOP_CREATEMENU = 0x0004;
    public const uint DESKTOP_HOOKCONTROL = 0x0008;
    public const uint DESKTOP_JOURNALRECORD = 0x0010;
    public const uint DESKTOP_JOURNALPLAYBACK = 0x0020;
    public const uint DESKTOP_ENUMERATE = 0x0040;
    public const uint DESKTOP_WRITEOBJECTS = 0x0080;
    public const uint DESKTOP_SWITCHDESKTOP = 0x0100;

    // ---- process creation ---------------------------------------------------------------------
    public const uint DEBUG_PROCESS = 0x00000001;
    public const uint CREATE_SUSPENDED = 0x00000004;
    public const uint DETACHED_PROCESS = 0x00000008;
    public const uint CREATE_NEW_CONSOLE = 0x00000010;
    public const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const uint CREATE_DEFAULT_ERROR_MODE = 0x04000000;
    public const uint CREATE_NO_WINDOW = 0x08000000;
    public const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    public const uint CREATE_PRESERVE_CODE_AUTHZ_LEVEL = 0x02000000;
    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    public const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    public const uint IDLE_PRIORITY_CLASS = 0x00000040;
    public const uint HIGH_PRIORITY_CLASS = 0x00000080;
    public const uint REALTIME_PRIORITY_CLASS = 0x00000100;
    public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
    public const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
    public const uint STARTF_USESHOWWINDOW = 0x00000001;
    public const uint STARTF_USESIZE = 0x00000002;
    public const uint STARTF_USEPOSITION = 0x00000004;
    public const uint STARTF_USESTDHANDLES = 0x00000100;
    public const uint STILL_ACTIVE = 259;
    public const uint LOGON_WITH_PROFILE = 0x00000001;
    public const uint LOGON_NETCREDENTIALS_ONLY = 0x00000002;

    // ---- LogonUserW ---------------------------------------------------------------------------
    public const uint LOGON32_LOGON_INTERACTIVE = 2;
    public const uint LOGON32_LOGON_NETWORK = 3;
    public const uint LOGON32_LOGON_BATCH = 4;
    public const uint LOGON32_LOGON_SERVICE = 5;
    public const uint LOGON32_LOGON_UNLOCK = 7;
    public const uint LOGON32_LOGON_NETWORK_CLEARTEXT = 8;
    public const uint LOGON32_LOGON_NEW_CREDENTIALS = 9;
    public const uint LOGON32_PROVIDER_DEFAULT = 0;
    public const uint LOGON32_PROVIDER_WINNT50 = 3;

    // ---- LoadUserProfile flags ----------------------------------------------------------------
    public const uint PI_NOUI = 0x00000001;
    public const uint PI_APPLYPOLICY = 0x00000002;

    // ---- job object limits --------------------------------------------------------------------
    public const uint JOB_OBJECT_LIMIT_WORKINGSET = 0x00000001;
    public const uint JOB_OBJECT_LIMIT_PROCESS_TIME = 0x00000002;
    public const uint JOB_OBJECT_LIMIT_JOB_TIME = 0x00000004;
    public const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;
    public const uint JOB_OBJECT_LIMIT_AFFINITY = 0x00000010;
    public const uint JOB_OBJECT_LIMIT_PRIORITY_CLASS = 0x00000020;
    public const uint JOB_OBJECT_LIMIT_PRESERVE_JOB_TIME = 0x00000040;
    public const uint JOB_OBJECT_LIMIT_SCHEDULING_CLASS = 0x00000080;
    public const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
    public const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;
    public const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x00000400;
    public const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;
    public const uint JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK = 0x00001000;
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    public const uint JOB_OBJECT_LIMIT_SUBSET_AFFINITY = 0x00004000;
    public const uint JOB_OBJECT_UILIMIT_HANDLES = 0x00000001;
    public const uint JOB_OBJECT_UILIMIT_READCLIPBOARD = 0x00000002;
    public const uint JOB_OBJECT_UILIMIT_WRITECLIPBOARD = 0x00000004;
    public const uint JOB_OBJECT_UILIMIT_SYSTEMPARAMETERS = 0x00000008;
    public const uint JOB_OBJECT_UILIMIT_DISPLAYSETTINGS = 0x00000010;
    public const uint JOB_OBJECT_UILIMIT_GLOBALATOMS = 0x00000020;
    public const uint JOB_OBJECT_UILIMIT_DESKTOP = 0x00000040;
    public const uint JOB_OBJECT_UILIMIT_EXITWINDOWS = 0x00000080;

    // ---- SystemParametersInfo / GetSystemMetrics ---------------------------------------------
    public const uint SPI_GETWORKAREA = 0x0030;
    public const uint SPI_SETWORKAREA = 0x002F;
    public const uint SPI_GETSCREENSAVEACTIVE = 0x0010;
    public const uint SPI_SETSCREENSAVEACTIVE = 0x0011;
    public const uint SPI_GETSCREENSAVETIMEOUT = 0x000E;
    public const uint SPI_SETSCREENSAVETIMEOUT = 0x000F;
    public const uint SPI_SETDESKWALLPAPER = 0x0014;
    public const uint SPI_GETSTICKYKEYS = 0x003A;
    public const uint SPI_SETSTICKYKEYS = 0x003B;
    public const uint SPI_GETTOGGLEKEYS = 0x0034;
    public const uint SPI_SETTOGGLEKEYS = 0x0035;
    public const uint SPI_GETFILTERKEYS = 0x0032;
    public const uint SPI_SETFILTERKEYS = 0x0033;
    public const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;
    public const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;
    public const uint SPIF_UPDATEINIFILE = 0x0001;
    public const uint SPIF_SENDCHANGE = 0x0002;
    public const uint SPIF_SENDWININICHANGE = 0x0002;
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;
    public const int SM_CXVSCROLL = 2;
    public const int SM_CYCAPTION = 4;
    public const int SM_SWAPBUTTON = 23;
    public const int SM_CXFULLSCREEN = 16;
    public const int SM_CYFULLSCREEN = 17;
    public const int SM_CMONITORS = 80;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_REMOTESESSION = 0x1000;
    public const int SM_SHUTTINGDOWN = 0x2000;

    // ---- monitors / display -------------------------------------------------------------------
    public const uint MONITOR_DEFAULTTONULL = 0x00000000;
    public const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    public const uint MONITORINFOF_PRIMARY = 0x00000001;
    public const uint ENUM_CURRENT_SETTINGS = 0xFFFFFFFF;
    public const uint ENUM_REGISTRY_SETTINGS = 0xFFFFFFFE;
    public const uint DM_POSITION = 0x00000020;
    public const uint DM_DISPLAYORIENTATION = 0x00000080;
    public const uint DM_BITSPERPEL = 0x00040000;
    public const uint DM_PELSWIDTH = 0x00080000;
    public const uint DM_PELSHEIGHT = 0x00100000;
    public const uint DM_DISPLAYFLAGS = 0x00200000;
    public const uint DM_DISPLAYFREQUENCY = 0x00400000;
    public const uint CDS_UPDATEREGISTRY = 0x00000001;
    public const uint CDS_TEST = 0x00000002;
    public const uint CDS_FULLSCREEN = 0x00000004;
    public const uint CDS_GLOBAL = 0x00000008;
    public const uint CDS_SET_PRIMARY = 0x00000010;
    public const uint CDS_NORESET = 0x10000000;
    public const uint CDS_RESET = 0x40000000;
    public const int DISP_CHANGE_SUCCESSFUL = 0;
    public const int DISP_CHANGE_RESTART = 1;
    public const int DISP_CHANGE_FAILED = -1;
    public const int DISP_CHANGE_BADMODE = -2;
    public const int DISP_CHANGE_NOTUPDATED = -3;
    public const int DISP_CHANGE_BADFLAGS = -4;
    public const int DISP_CHANGE_BADPARAM = -5;
    public const int DISP_CHANGE_BADDUALVIEW = -6;

    // ---- GetWindow ----------------------------------------------------------------------------
    public const uint GW_HWNDFIRST = 0;
    public const uint GW_HWNDLAST = 1;
    public const uint GW_HWNDNEXT = 2;
    public const uint GW_HWNDPREV = 3;
    public const uint GW_OWNER = 4;
    public const uint GW_CHILD = 5;
    public const uint GA_PARENT = 1;
    public const uint GA_ROOT = 2;
    public const uint GA_ROOTOWNER = 3;

    // ---- PrintWindow --------------------------------------------------------------------------
    public const uint PW_CLIENTONLY = 0x00000001;
    public const uint PW_RENDERFULLCONTENT = 0x00000002;

    // ---- GDI ----------------------------------------------------------------------------------
    public const uint SRCCOPY = 0x00CC0020;
    public const uint CAPTUREBLT = 0x40000000;
    public const uint BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;

    // ---- WinEvents ----------------------------------------------------------------------------
    public const uint EVENT_MIN = 0x00000001;
    public const uint EVENT_MAX = 0x7FFFFFFF;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MENUSTART = 0x0004;
    public const uint EVENT_SYSTEM_MENUPOPUPSTART = 0x0006;
    public const uint EVENT_SYSTEM_SWITCHSTART = 0x0014;
    public const uint EVENT_SYSTEM_SWITCHEND = 0x0015;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_SYSTEM_DESKTOPSWITCH = 0x0020;
    public const uint EVENT_OBJECT_CREATE = 0x8000;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_HIDE = 0x8003;
    public const uint EVENT_OBJECT_FOCUS = 0x8005;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNTHREAD = 0x0001;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const uint WINEVENT_INCONTEXT = 0x0004;
    public const int OBJID_WINDOW = 0;
    public const int CHILDID_SELF = 0;

    // ---- MessageBox ---------------------------------------------------------------------------
    public const uint MB_OK = 0x00000000;
    public const uint MB_OKCANCEL = 0x00000001;
    public const uint MB_YESNO = 0x00000004;
    public const uint MB_ICONERROR = 0x00000010;
    public const uint MB_ICONQUESTION = 0x00000020;
    public const uint MB_ICONWARNING = 0x00000030;
    public const uint MB_ICONINFORMATION = 0x00000040;
    public const uint MB_SYSTEMMODAL = 0x00001000;
    public const uint MB_TOPMOST = 0x00040000;
    public const uint MB_SETFOREGROUND = 0x00010000;
    public const uint MB_SERVICE_NOTIFICATION = 0x00200000;
    public const int IDOK = 1;
    public const int IDCANCEL = 2;
    public const int IDYES = 6;
    public const int IDNO = 7;
    public const int IDTIMEOUT = 32000;
    public const int IDASYNC = 32001;

    // ---- SetThreadExecutionState --------------------------------------------------------------
    public const uint ES_SYSTEM_REQUIRED = 0x00000001;
    public const uint ES_DISPLAY_REQUIRED = 0x00000002;
    public const uint ES_AWAYMODE_REQUIRED = 0x00000040;
    public const uint ES_CONTINUOUS = 0x80000000;

    // ---- CreateFileW / DeviceIoControl -------------------------------------------------------
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint FILE_SHARE_DELETE = 0x00000004;
    public const uint CREATE_NEW = 1;
    public const uint CREATE_ALWAYS = 2;
    public const uint OPEN_EXISTING = 3;
    public const uint OPEN_ALWAYS = 4;
    public const uint TRUNCATE_EXISTING = 5;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public const uint FILE_READ_ATTRIBUTES = 0x0080;
    public const uint FILE_DEVICE_MASS_STORAGE = 0x0000002D;
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    public const uint FSCTL_LOCK_VOLUME = 0x00090018;
    public const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
    public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;

    // ---- SHAppBarMessage ----------------------------------------------------------------------
    public const uint ABM_NEW = 0x00000000;
    public const uint ABM_REMOVE = 0x00000001;
    public const uint ABM_QUERYPOS = 0x00000002;
    public const uint ABM_SETPOS = 0x00000003;
    public const uint ABM_GETSTATE = 0x00000004;
    public const uint ABM_GETTASKBARPOS = 0x00000005;
    public const uint ABM_ACTIVATE = 0x00000006;
    public const uint ABM_GETAUTOHIDEBAR = 0x00000007;
    public const uint ABM_SETAUTOHIDEBAR = 0x00000008;
    public const uint ABM_WINDOWPOSCHANGED = 0x00000009;
    public const uint ABM_SETSTATE = 0x0000000A;
    public const uint ABS_AUTOHIDE = 0x00000001;
    public const uint ABS_ALWAYSONTOP = 0x00000002;
    public const uint ABE_LEFT = 0;
    public const uint ABE_TOP = 1;
    public const uint ABE_RIGHT = 2;
    public const uint ABE_BOTTOM = 3;

    // ---- ShellExecuteEx -----------------------------------------------------------------------
    public const uint SEE_MASK_DEFAULT = 0x00000000;
    public const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
    public const uint SEE_MASK_NOASYNC = 0x00000100;
    public const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
    public const uint SEE_MASK_UNICODE = 0x00004000;
    public const uint SEE_MASK_NO_CONSOLE = 0x00008000;
    public const uint SEE_MASK_NOZONECHECKS = 0x00800000;

    // ---- SHChangeNotify -----------------------------------------------------------------------
    public const int SHCNE_ASSOCCHANGED = 0x08000000;
    public const int SHCNE_UPDATEDIR = 0x00001000;
    public const int SHCNE_UPDATEITEM = 0x00002000;
    public const uint SHCNF_IDLIST = 0x0000;
    public const uint SHCNF_PATHW = 0x0005;
    public const uint SHCNF_FLUSH = 0x1000;

    // ---- SHGetKnownFolderPath ----------------------------------------------------------------
    public const uint KF_FLAG_DEFAULT = 0x00000000;
    public const uint KF_FLAG_CREATE = 0x00008000;
    public const uint KF_FLAG_DONT_VERIFY = 0x00004000;
    public const uint KF_FLAG_DEFAULT_PATH = 0x00000400;
    public const uint KF_FLAG_NOT_PARENT_RELATIVE = 0x00000200;

    // ---- SHEmptyRecycleBin --------------------------------------------------------------------
    public const uint SHERB_NOCONFIRMATION = 0x00000001;
    public const uint SHERB_NOPROGRESSUI = 0x00000002;
    public const uint SHERB_NOSOUND = 0x00000004;

    // ---- Shell_NotifyIcon ---------------------------------------------------------------------
    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;
    public const uint NIM_SETFOCUS = 0x00000003;
    public const uint NIM_SETVERSION = 0x00000004;
    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const uint NIF_STATE = 0x00000008;
    public const uint NIF_INFO = 0x00000010;
    public const uint NIF_GUID = 0x00000020;
    public const uint NIF_SHOWTIP = 0x00000080;
    public const uint NIIF_NONE = 0x00000000;
    public const uint NIIF_INFO = 0x00000001;
    public const uint NIIF_WARNING = 0x00000002;
    public const uint NIIF_ERROR = 0x00000003;
    public const uint NOTIFYICON_VERSION_4 = 4;

    // ---- DWM ----------------------------------------------------------------------------------
    public const int DWMNCRP_USEWINDOWSTYLE = 0;
    public const int DWMNCRP_DISABLED = 1;
    public const int DWMNCRP_ENABLED = 2;
    public const int DWMWCP_DEFAULT = 0;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWCP_ROUND = 2;
    public const int DWMWCP_ROUNDSMALL = 3;
    public const int DWM_CLOAKED_APP = 0x00000001;
    public const int DWM_CLOAKED_SHELL = 0x00000002;
    public const int DWM_CLOAKED_INHERITED = 0x00000004;

    // ---- Credential Manager -------------------------------------------------------------------
    public const uint CRED_TYPE_GENERIC = 1;
    public const uint CRED_TYPE_DOMAIN_PASSWORD = 2;
    public const uint CRED_TYPE_DOMAIN_CERTIFICATE = 3;
    public const uint CRED_TYPE_DOMAIN_VISIBLE_PASSWORD = 4;
    public const uint CRED_TYPE_GENERIC_CERTIFICATE = 5;
    public const uint CRED_TYPE_DOMAIN_EXTENDED = 6;
    public const uint CRED_ENUMERATE_ALL_CREDENTIALS = 0x1;
    public const uint CRED_PERSIST_SESSION = 1;
    public const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    public const uint CRED_PERSIST_ENTERPRISE = 3;

    // ---- Service Control Manager --------------------------------------------------------------
    public const uint SC_MANAGER_CONNECT = 0x0001;
    public const uint SC_MANAGER_CREATE_SERVICE = 0x0002;
    public const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    public const uint SC_MANAGER_LOCK = 0x0008;
    public const uint SC_MANAGER_QUERY_LOCK_STATUS = 0x0010;
    public const uint SC_MANAGER_MODIFY_BOOT_CONFIG = 0x0020;
    public const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
    public const uint SERVICE_QUERY_CONFIG = 0x0001;
    public const uint SERVICE_CHANGE_CONFIG = 0x0002;
    public const uint SERVICE_QUERY_STATUS = 0x0004;
    public const uint SERVICE_ENUMERATE_DEPENDENTS = 0x0008;
    public const uint SERVICE_START = 0x0010;
    public const uint SERVICE_STOP = 0x0020;
    public const uint SERVICE_PAUSE_CONTINUE = 0x0040;
    public const uint SERVICE_INTERROGATE = 0x0080;
    public const uint SERVICE_USER_DEFINED_CONTROL = 0x0100;
    public const uint SERVICE_ALL_ACCESS = 0xF01FF;
    public const uint SERVICE_CONTROL_STOP = 0x00000001;
    public const uint SERVICE_CONTROL_PAUSE = 0x00000002;
    public const uint SERVICE_CONTROL_CONTINUE = 0x00000003;
    public const uint SERVICE_CONTROL_INTERROGATE = 0x00000004;
    public const uint SERVICE_CONTROL_SESSIONCHANGE = 0x0000000E;
    public const uint SERVICE_STOPPED = 0x00000001;
    public const uint SERVICE_START_PENDING = 0x00000002;
    public const uint SERVICE_STOP_PENDING = 0x00000003;
    public const uint SERVICE_RUNNING = 0x00000004;
    public const uint SERVICE_CONTINUE_PENDING = 0x00000005;
    public const uint SERVICE_PAUSE_PENDING = 0x00000006;
    public const uint SERVICE_PAUSED = 0x00000007;

    // ---- WTSSendMessage / MessageBox result ---------------------------------------------------
    public const uint WTS_MESSAGE_TIMEOUT_NONE = 0;

    // ---- well-known SIDs (string form) -------------------------------------------------------
    public const string SID_LOCAL_SYSTEM = "S-1-5-18";
    public const string SID_LOCAL_SERVICE = "S-1-5-19";
    public const string SID_NETWORK_SERVICE = "S-1-5-20";
    public const string SID_BUILTIN_ADMINISTRATORS = "S-1-5-32-544";
    public const string SID_BUILTIN_USERS = "S-1-5-32-545";
    public const string SID_EVERYONE = "S-1-1-0";
    public const string SID_INTERACTIVE = "S-1-5-4";
    public const string SID_AUTHENTICATED_USERS = "S-1-5-11";
}
