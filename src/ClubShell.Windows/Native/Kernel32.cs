#pragma warning disable CA1707 // identifiers should not contain underscores (Win32 names)

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ClubShell.Windows.Native;

/// <summary>
/// kernel32.dll P/Invokes. Process handles are the BCL <see cref="SafeProcessHandle"/> so
/// <c>Process.SafeHandle</c> can be passed directly; job/event/device/library handles use the
/// SafeHandle types from Structs.cs.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class Kernel32
{
    private const string Lib = "kernel32.dll";

    // ---- handles ------------------------------------------------------------------------------

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);

    [LibraryImport(Lib, EntryPoint = "OpenProcess", SetLastError = true)]
    private static partial nint OpenProcessRaw(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    /// <summary>Opens a process; the returned handle is invalid on failure (check <c>IsInvalid</c> and <see cref="Win32Error.Last"/>).</summary>
    public static SafeProcessHandle OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId) =>
        new(OpenProcessRaw(dwDesiredAccess, bInheritHandle, dwProcessId), ownsHandle: true);

    /// <summary>Pseudo-handle (-1) for the current process; never close it.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint GetCurrentProcess();

    [LibraryImport(Lib, SetLastError = true)]
    public static partial uint GetCurrentProcessId();

    [LibraryImport(Lib, SetLastError = true)]
    public static partial uint GetCurrentThreadId();

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateHandle(nint hSourceProcessHandle, nint hSourceHandle, nint hTargetProcessHandle, out nint lpTargetHandle, uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwOptions);

    // ---- processes ----------------------------------------------------------------------------

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(SafeHandle hProcess, uint uExitCode);

    /// <summary>Returns WAIT_OBJECT_0 / WAIT_TIMEOUT / WAIT_ABANDONED / WAIT_FAILED (see <see cref="WaitResult"/>).</summary>
    [LibraryImport(Lib, SetLastError = true)]
    public static partial uint WaitForSingleObject(SafeHandle hHandle, uint dwMilliseconds);

    /// <summary><paramref name="lpExitCode"/> is STILL_ACTIVE (259) while the process runs.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(SafeHandle hProcess, out uint lpExitCode);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetPriorityClass(SafeHandle hProcess, uint dwPriorityClass);

    /// <summary>Returns 0 on failure.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    public static partial uint GetPriorityClass(SafeHandle hProcess);

    /// <summary><paramref name="dwFlags"/> 0 = Win32 path, PROCESS_NAME_NATIVE (1) = NT path. <paramref name="lpdwSize"/> is in/out chars.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageNameW(SafeHandle hProcess, uint dwFlags, ref char lpExeName, ref uint lpdwSize);

    /// <summary>Full image path of a process opened with PROCESS_QUERY_LIMITED_INFORMATION, or <see langword="null"/> on failure.</summary>
    public static string? QueryFullProcessImageName(SafeHandle hProcess)
    {
        char[] buffer = new char[1024];
        uint size = (uint)buffer.Length;
        return QueryFullProcessImageNameW(hProcess, 0, ref buffer[0], ref size) ? NativeString.FromBuffer(buffer, (int)size) : null;
    }

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    /// <summary>Session id of the physical console, or 0xFFFFFFFF when nobody is attached.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    public static partial uint WTSGetActiveConsoleSessionId();

    // ---- Toolhelp32 (process snapshots) -------------------------------------------------------

    /// <summary>Returns INVALID_HANDLE_VALUE on failure; close with <see cref="CloseHandle"/>. Use TH32CS_SNAPPROCESS for processes.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    /// <summary>Initialize with <see cref="PROCESSENTRY32W.Create"/> (dwSize) before the first call.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool Process32FirstW(nint hSnapshot, PROCESSENTRY32W* lppe);

    /// <summary>Returns <see langword="false"/> with ERROR_NO_MORE_FILES after the last entry.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool Process32NextW(nint hSnapshot, PROCESSENTRY32W* lppe);

    // ---- job objects --------------------------------------------------------------------------

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeJobHandle CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(SafeJobHandle hJob, JOBOBJECTINFOCLASS JobObjectInformationClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation, uint cbJobObjectInformationLength);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(SafeJobHandle hJob, JOBOBJECTINFOCLASS JobObjectInformationClass, ref JOBOBJECT_BASIC_UI_RESTRICTIONS lpJobObjectInformation, uint cbJobObjectInformationLength);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(SafeJobHandle hJob, JOBOBJECTINFOCLASS JobObjectInformationClass, nint lpJobObjectInformation, uint cbJobObjectInformationLength);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryInformationJobObject(SafeJobHandle hJob, JOBOBJECTINFOCLASS JobObjectInformationClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation, uint cbJobObjectInformationLength, out uint lpReturnLength);

    /// <summary>Generic variant: <paramref name="lpJobObjectInformation"/> points at a caller-allocated buffer (e.g. JOBOBJECT_BASIC_PROCESS_ID_LIST).</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryInformationJobObject(SafeJobHandle hJob, JOBOBJECTINFOCLASS JobObjectInformationClass, nint lpJobObjectInformation, uint cbJobObjectInformationLength, out uint lpReturnLength);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AssignProcessToJobObject(SafeJobHandle hJob, SafeHandle hProcess);

    /// <summary><paramref name="hJob"/> = 0 asks whether the process is in any job.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsProcessInJob(SafeHandle hProcess, nint hJob, [MarshalAs(UnmanagedType.Bool)] out bool Result);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateJobObject(SafeJobHandle hJob, uint uExitCode);

    /// <summary>Creates a job whose processes die when the last handle closes (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE); throws on failure.</summary>
    public static SafeJobHandle CreateKillOnCloseJob(string? name = null)
    {
        SafeJobHandle job = Win32Error.ThrowIfInvalid(CreateJobObjectW(0, name), nameof(CreateJobObjectW));
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = default;
        limits.BasicLimitInformation.LimitFlags = NativeConst.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, ref limits, (uint)NativeString.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            int error = Win32Error.Last();
            job.Dispose();
            Win32Error.Throw(error, nameof(SetInformationJobObject));
        }

        return job;
    }

    // ---- events / sync ------------------------------------------------------------------------

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeEventHandle CreateEventW(nint lpEventAttributes, [MarshalAs(UnmanagedType.Bool)] bool bManualReset, [MarshalAs(UnmanagedType.Bool)] bool bInitialState, string? lpName);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetEvent(SafeHandle hEvent);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ResetEvent(SafeHandle hEvent);

    // ---- system information -------------------------------------------------------------------

    [LibraryImport(Lib, SetLastError = true)]
    public static partial ulong GetTickCount64();

    [LibraryImport(Lib, SetLastError = true)]
    public static partial void GetSystemInfo(out SYSTEM_INFO lpSystemInfo);

    /// <summary>Initialize with <see cref="MEMORYSTATUSEX.Create"/> before calling.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetComputerNameExW(COMPUTER_NAME_FORMAT NameType, ref char lpBuffer, ref uint nSize);

    /// <summary>Computer name in the requested format, or empty on failure.</summary>
    public static string GetComputerNameEx(COMPUTER_NAME_FORMAT nameType)
    {
        char[] buffer = new char[256];
        uint size = (uint)buffer.Length;
        return GetComputerNameExW(nameType, ref buffer[0], ref size) ? NativeString.FromBuffer(buffer, (int)size) : string.Empty;
    }

    /// <summary>Returns the previous execution state, or 0 on failure. Use ES_CONTINUOUS | ES_DISPLAY_REQUIRED to keep the screen on during a session.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    public static partial uint SetThreadExecutionState(uint esFlags);

    // ---- volumes / disks ----------------------------------------------------------------------

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeInformationW(string? lpRootPathName, ref char lpVolumeNameBuffer, uint nVolumeNameSize, out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength, out uint lpFileSystemFlags, ref char lpFileSystemNameBuffer, uint nFileSystemNameSize);

    /// <summary>Volume label, serial and file system for a root such as <c>C:\</c>; returns <see langword="false"/> on failure.</summary>
    public static bool TryGetVolumeInformation(string rootPath, out string label, out uint serial, out string fileSystem)
    {
        char[] labelBuffer = new char[261];
        char[] fsBuffer = new char[261];
        bool ok = GetVolumeInformationW(rootPath, ref labelBuffer[0], (uint)labelBuffer.Length, out serial, out _, out _, ref fsBuffer[0], (uint)fsBuffer.Length);
        label = ok ? NativeString.FromBuffer(labelBuffer) : string.Empty;
        fileSystem = ok ? NativeString.FromBuffer(fsBuffer) : string.Empty;
        return ok;
    }

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpaceExW(string? lpDirectoryName, out ulong lpFreeBytesAvailableToCaller, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    /// <summary>Bitmask of present drive letters (bit 0 = A:). 0 on failure.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    public static partial uint GetLogicalDrives();

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetDriveTypeW(string? lpRootPathName);

    /// <summary>Resolves a DOS device (e.g. "C:") to its NT target(s); the buffer holds a multi-string. Returns chars written or 0.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint QueryDosDeviceW(string? lpDeviceName, ref char lpTargetPath, uint ucchMax);

    /// <summary>First NT device path for a DOS device (e.g. "C:" → "\Device\HarddiskVolume3"), or <see langword="null"/>.</summary>
    public static string? QueryDosDevice(string deviceName)
    {
        char[] buffer = new char[1024];
        uint written = QueryDosDeviceW(deviceName, ref buffer[0], (uint)buffer.Length);
        return written == 0 ? null : NativeString.FromBuffer(buffer);
    }

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeDeviceHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    /// <summary>Buffers are raw pointers (pin with <c>fixed</c> or allocate with Marshal.AllocHGlobal); <paramref name="lpOverlapped"/> = 0 for synchronous calls.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeHandle hDevice, uint dwIoControlCode, nint lpInBuffer, uint nInBufferSize, nint lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, nint lpOverlapped);

    // ---- modules ------------------------------------------------------------------------------

    /// <summary><paramref name="lpModuleName"/> = <see langword="null"/> returns the executable's HMODULE (the value to pass to SetWindowsHookExW).</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandleW(string? lpModuleName);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeLibraryHandle LoadLibraryW(string lpLibFileName);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FreeLibrary(nint hLibModule);

    /// <summary>Export names are ANSI; marshalled as UTF-8 (identical for ASCII names).</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GetProcAddress(SafeLibraryHandle hModule, string lpProcName);

    [LibraryImport(Lib, EntryPoint = "GetProcAddress", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GetProcAddress(nint hModule, string lpProcName);

    // ---- memory -------------------------------------------------------------------------------

    /// <summary>Returns 0 on success (HLOCAL semantics), otherwise the handle.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint LocalFree(nint hMem);
}

/// <summary>pdh.dll: Perflib name lookup (localized performance counter / category names).</summary>
[SupportedOSPlatform("windows")]
public static partial class Pdh
{
    private const string Lib = "pdh.dll";

    /// <summary>Resolves a Perflib name index (HKLM\...\Perflib\009\Counter) to the localized name. Returns PDH_STATUS (0 = success); <paramref name="pcchNameBufferSize"/> is in/out chars.</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhLookupPerfNameByIndexW(string? szMachineName, uint dwNameIndex, ref char szNameBuffer, ref uint pcchNameBufferSize);

    /// <summary>Localized name for a Perflib index, or <see langword="null"/> when the lookup fails.</summary>
    public static string? LookupPerfNameByIndex(uint index)
    {
        char[] buffer = new char[1024];
        uint size = (uint)buffer.Length;
        return PdhLookupPerfNameByIndexW(null, index, ref buffer[0], ref size) == 0 ? NativeString.FromBuffer(buffer) : null;
    }
}

/// <summary>powrprof.dll: system power transitions.</summary>
[SupportedOSPlatform("windows")]
public static partial class PowrProf
{
    private const string Lib = "powrprof.dll";

    /// <summary>Suspends (sleep) or hibernates the system; needs SE_SHUTDOWN_NAME. BOOLEAN parameters and return value are 1 byte.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool bHibernate, [MarshalAs(UnmanagedType.U1)] bool bForce, [MarshalAs(UnmanagedType.U1)] bool bWakeupEventsDisabled);
}

/// <summary>mpr.dll: WNet drive mappings. Both functions return ERROR_SUCCESS or a Win32 error code directly (no last-error).</summary>
[SupportedOSPlatform("windows")]
public static partial class Mpr
{
    private const string Lib = "mpr.dll";

    /// <summary>Connects <c>lpNetResource.lpLocalName</c> to <c>lpRemoteName</c>; <paramref name="lpPassword"/> / <paramref name="lpUserName"/> = <see langword="null"/> use the caller's credentials.</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int WNetAddConnection2W(ref NETRESOURCEW lpNetResource, string? lpPassword, string? lpUserName, uint dwFlags);

    /// <summary>Returns ERROR_NOT_CONNECTED when <paramref name="lpName"/> (drive letter or UNC path) is not mapped.</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int WNetCancelConnection2W(string lpName, uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fForce);
}
