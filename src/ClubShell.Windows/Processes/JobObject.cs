using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClubShell.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace ClubShell.Windows.Processes;

/// <summary>Optional resource limits applied by <see cref="JobObject.Create"/>.</summary>
/// <param name="ProcessMemoryBytes">Per-process committed memory cap (JOB_OBJECT_LIMIT_PROCESS_MEMORY).</param>
/// <param name="JobMemoryBytes">Job-wide committed memory cap (JOB_OBJECT_LIMIT_JOB_MEMORY).</param>
/// <param name="ActiveProcessLimit">Maximum number of simultaneously live processes.</param>
/// <param name="PriorityClass">Priority class forced on every process (NORMAL_PRIORITY_CLASS, ...).</param>
/// <param name="UiRestrictions">JOB_OBJECT_UILIMIT_* flags (e.g. UILIMIT_EXITWINDOWS | UILIMIT_SYSTEMPARAMETERS).</param>
public sealed record JobLimits(
    ulong? ProcessMemoryBytes = null,
    ulong? JobMemoryBytes = null,
    uint? ActiveProcessLimit = null,
    uint? PriorityClass = null,
    uint UiRestrictions = 0);

/// <summary>
/// Windows job object (ARCHITECTURE.md §2 rule 5): every game/app launched for a session is assigned to one so
/// closing the job (or disposing this object with <c>killOnClose</c>) terminates the whole process tree.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class JobObject : IDisposable
{
    private readonly SafeJobHandle _handle;
    private bool _disposed;

    private JobObject(SafeJobHandle handle, string? name, bool killOnClose)
    {
        _handle = handle;
        Name = name;
        KillOnClose = killOnClose;
    }

    /// <summary>Kernel object name, or <see langword="null"/> for an anonymous job.</summary>
    public string? Name { get; }

    /// <summary><see langword="true"/> when JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE is set.</summary>
    public bool KillOnClose { get; }

    /// <summary>Underlying handle (valid until <see cref="Dispose"/>).</summary>
    public SafeJobHandle Handle => _handle;

    /// <summary>Creates a job object and applies the limits.</summary>
    public static JobObject Create(string? name = null, bool killOnClose = true, JobLimits? limits = null)
    {
        SafeJobHandle job = Win32Error.ThrowIfInvalid(Kernel32.CreateJobObjectW(0, name), nameof(Kernel32.CreateJobObjectW));
        try
        {
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = default;
            uint flags = killOnClose ? NativeConst.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE : 0;
            if (limits is not null)
            {
                if (limits.ProcessMemoryBytes is { } processMemory)
                {
                    flags |= NativeConst.JOB_OBJECT_LIMIT_PROCESS_MEMORY;
                    info.ProcessMemoryLimit = (nuint)processMemory;
                }

                if (limits.JobMemoryBytes is { } jobMemory)
                {
                    flags |= NativeConst.JOB_OBJECT_LIMIT_JOB_MEMORY;
                    info.JobMemoryLimit = (nuint)jobMemory;
                }

                if (limits.ActiveProcessLimit is { } activeLimit)
                {
                    flags |= NativeConst.JOB_OBJECT_LIMIT_ACTIVE_PROCESS;
                    info.BasicLimitInformation.ActiveProcessLimit = activeLimit;
                }

                if (limits.PriorityClass is { } priority)
                {
                    flags |= NativeConst.JOB_OBJECT_LIMIT_PRIORITY_CLASS;
                    info.BasicLimitInformation.PriorityClass = priority;
                }
            }

            info.BasicLimitInformation.LimitFlags = flags;
            Win32Error.ThrowIfFalse(
                Kernel32.SetInformationJobObject(job, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, ref info, (uint)NativeString.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()),
                nameof(Kernel32.SetInformationJobObject));

            if (limits is { UiRestrictions: > 0 })
            {
                JOBOBJECT_BASIC_UI_RESTRICTIONS ui = new() { UIRestrictionsClass = limits.UiRestrictions };
                Win32Error.ThrowIfFalse(
                    Kernel32.SetInformationJobObject(job, JOBOBJECTINFOCLASS.JobObjectBasicUIRestrictions, ref ui, (uint)NativeString.SizeOf<JOBOBJECT_BASIC_UI_RESTRICTIONS>()),
                    nameof(Kernel32.SetInformationJobObject));
            }

            return new JobObject(job, name, killOnClose);
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    /// <summary>Assigns a process (handle needs PROCESS_SET_QUOTA | PROCESS_TERMINATE).</summary>
    public void Assign(SafeHandle processHandle)
    {
        ThrowIfDisposed();
        Win32Error.ThrowIfFalse(Kernel32.AssignProcessToJobObject(_handle, processHandle), nameof(Kernel32.AssignProcessToJobObject));
    }

    /// <summary>Assigns a <see cref="Process"/>.</summary>
    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        Assign(process.SafeHandle);
    }

    /// <summary>Assigns a process by id.</summary>
    public void Assign(int pid)
    {
        using SafeProcessHandle handle = Kernel32.OpenProcess(NativeConst.PROCESS_SET_QUOTA | NativeConst.PROCESS_TERMINATE, false, (uint)pid);
        if (handle.IsInvalid)
        {
            Win32Error.ThrowLastError(nameof(Kernel32.OpenProcess));
        }

        Assign(handle);
    }

    /// <summary>Process ids currently in the job (JOBOBJECT_BASIC_PROCESS_ID_LIST).</summary>
    public IReadOnlyList<int> QueryProcessIds()
    {
        ThrowIfDisposed();
        int capacity = 64;
        while (true)
        {
            int size = 8 + (capacity * nint.Size);
            nint buffer = Marshal.AllocHGlobal(size);
            try
            {
                bool ok = Kernel32.QueryInformationJobObject(_handle, JOBOBJECTINFOCLASS.JobObjectBasicProcessIdList, buffer, (uint)size, out _);
                int error = ok ? 0 : Win32Error.Last();
                if (!ok && error != NativeConst.ERROR_MORE_DATA)
                {
                    Win32Error.Throw(error, nameof(Kernel32.QueryInformationJobObject));
                }

                int assigned = Marshal.ReadInt32(buffer);
                int listed = Marshal.ReadInt32(buffer, 4);
                if (ok && listed >= assigned)
                {
                    var pids = new int[listed];
                    for (int i = 0; i < listed; i++)
                    {
                        pids[i] = (int)Marshal.ReadIntPtr(buffer, 8 + (i * nint.Size));
                    }

                    return pids;
                }

                capacity = Math.Max(capacity * 2, assigned + 16);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>Kills every process in the job.</summary>
    public void Terminate(uint exitCode = 1)
    {
        ThrowIfDisposed();
        Win32Error.ThrowIfFalse(Kernel32.TerminateJobObject(_handle, exitCode), nameof(Kernel32.TerminateJobObject));
    }

    /// <summary><see langword="true"/> when <paramref name="pid"/> belongs to this job (false when the process cannot be opened).</summary>
    public bool IsInJob(int pid)
    {
        ThrowIfDisposed();
        using SafeProcessHandle handle = Kernel32.OpenProcess(NativeConst.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        return !handle.IsInvalid && Kernel32.IsProcessInJob(handle, _handle.DangerousGetHandle(), out bool inJob) && inJob;
    }

    /// <summary><see langword="true"/> when <paramref name="pid"/> is inside any job.</summary>
    public static bool IsInAnyJob(int pid)
    {
        using SafeProcessHandle handle = Kernel32.OpenProcess(NativeConst.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        return !handle.IsInvalid && Kernel32.IsProcessInJob(handle, 0, out bool inJob) && inJob;
    }

    /// <summary>Closes the handle; with <see cref="KillOnClose"/> this terminates the job once the last handle is gone.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
