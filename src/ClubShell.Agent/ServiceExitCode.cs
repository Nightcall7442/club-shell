using System.ServiceProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClubShell.Agent;

/// <summary>
/// Turns a faulted <see cref="BackgroundService"/> into a failed service stop. Under the default
/// <see cref="BackgroundServiceExceptionBehavior.StopHost"/> the host stops cleanly, the service reports
/// <c>SERVICE_STOPPED</c> with exit code 0, and the SCM recovery actions (restart after 5 s, ARCHITECTURE §7; they
/// count only non-zero stops with <c>failureflag 1</c>) never run: the PC would stay without billing, watchdog and
/// lock until someone reboots it.
/// </summary>
public static class ServiceExitCode
{
    /// <summary>
    /// Watches <paramref name="host"/>: when it starts stopping because a background service faulted, the service
    /// stop is reported with exit code 1 (and <see cref="Environment.ExitCode"/> is set for console runs). Call after
    /// <c>Build()</c> and before <c>RunAsync()</c>; the returned function gives the process exit code afterwards.
    /// </summary>
    public static Func<int> Attach(IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        // Resolved now: RunAsync disposes the host, but the services keep their ExecuteTask.
        BackgroundService[] workers = host.Services.GetServices<IHostedService>().OfType<BackgroundService>().ToArray();
        IHostLifetime? lifetime = host.Services.GetService<IHostLifetime>();
        int exitCode = 0;
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
        {
            // The host calls StopApplication only after the faulted task completed, so the fault is visible here, and
            // the lifetime reports SERVICE_STOPPED (with its ExitCode) after this callback.
            if (!Array.Exists(workers, static w => w.ExecuteTask is { IsFaulted: true }))
            {
                return;
            }

            exitCode = 1;
            Environment.ExitCode = 1;
            if (OperatingSystem.IsWindows() && lifetime is ServiceBase service)
            {
                service.ExitCode = 1;
            }
        });
        return () => exitCode;
    }
}
