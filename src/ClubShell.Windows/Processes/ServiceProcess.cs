using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClubShell.Windows.Native;

namespace ClubShell.Windows.Processes;

/// <summary>The process behind a Windows service, as the Service Control Manager knows it.</summary>
[SupportedOSPlatform("windows")]
public static class ServiceProcess
{
    /// <summary>
    /// Process id of <paramref name="serviceName"/> while it is running (<c>QueryServiceStatusEx</c>);
    /// <see langword="null"/> when the service is not installed, not running, or cannot be queried.
    /// </summary>
    public static uint? RunningProcessId(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        using SafeServiceHandle manager = Advapi32.OpenSCManagerW(null, null, NativeConst.SC_MANAGER_CONNECT);
        if (manager.IsInvalid)
        {
            return null;
        }

        using SafeServiceHandle service = Advapi32.OpenServiceW(manager, serviceName, NativeConst.SERVICE_QUERY_STATUS);
        if (service.IsInvalid)
        {
            return null;
        }

        uint size = (uint)Marshal.SizeOf<SERVICE_STATUS_PROCESS>();
        if (!Advapi32.QueryServiceStatusEx(service, NativeConst.SC_STATUS_PROCESS_INFO, out SERVICE_STATUS_PROCESS status, size, out _))
        {
            return null;
        }

        return status.dwCurrentState == NativeConst.SERVICE_RUNNING && status.dwProcessId != 0 ? status.dwProcessId : null;
    }
}
