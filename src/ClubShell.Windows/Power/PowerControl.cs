using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using ClubShell.Core.Abstractions;
using ClubShell.Windows.Native;
using ClubShell.Windows.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Windows.Power;

/// <summary>
/// Power operations for the Agent (LocalSystem, session 0): shutdown / reboot with a countdown through
/// <c>InitiateSystemShutdownExW</c> (abortable), sleep / hibernate, session logoff, power plans via <c>powercfg</c>,
/// a keep-awake thread (<c>SetThreadExecutionState</c> is per thread, so it lives on its own long-lived thread and is
/// re-asserted periodically) and a cancellable scheduled shutdown for <c>power.scheduledShutdown</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class PowerControl
{
    /// <summary>Windows "High performance" power plan.</summary>
    public static readonly Guid HighPerformancePlan = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    /// <summary>Windows "Balanced" power plan.</summary>
    public static readonly Guid BalancedPlan = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    /// <summary>Windows "Power saver" power plan.</summary>
    public static readonly Guid PowerSaverPlan = new("a1841308-3541-4fab-bc81-f71556f20b4a");

    /// <summary>Windows "Ultimate performance" power plan (present on Workstation / Pro for Workstations editions).</summary>
    public static readonly Guid UltimatePerformancePlan = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    private const uint ShutdownReason = NativeConst.SHTDN_REASON_MAJOR_APPLICATION | NativeConst.SHTDN_REASON_MINOR_MAINTENANCE | NativeConst.SHTDN_REASON_FLAG_PLANNED;
    private static readonly TimeSpan KeepAwakeRefresh = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(20);

    private readonly ILogger<PowerControl> _logger;
    private readonly IClock _clock;
    private readonly string _powercfg = Path.Combine(Environment.SystemDirectory, "powercfg.exe");
    private readonly object _gate = new();
    private Thread? _keepAwakeThread;
    private volatile bool _stopKeepAwake;

    /// <summary>Creates the controller.</summary>
    public PowerControl(ILogger<PowerControl>? logger = null, IClock? clock = null)
    {
        _logger = logger ?? NullLogger<PowerControl>.Instance;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary><see langword="true"/> while the keep-awake thread holds the system and display awake.</summary>
    public bool SleepPrevented
    {
        get
        {
            lock (_gate)
            {
                return _keepAwakeThread is not null;
            }
        }
    }

    // ---- shutdown / reboot --------------------------------------------------------------------

    /// <summary>Initiates a shutdown after <paramref name="delaySec"/> seconds (0 = immediately), showing <paramref name="message"/> in the countdown dialog.</summary>
    /// <exception cref="Win32Exception">The request failed (e.g. a shutdown is already in progress: ERROR_SHUTDOWN_IN_PROGRESS).</exception>
    public void Shutdown(int delaySec = 0, string? message = null, bool force = true) => Initiate(delaySec, message, force, reboot: false);

    /// <summary>Initiates a reboot after <paramref name="delaySec"/> seconds (0 = immediately).</summary>
    /// <exception cref="Win32Exception">The request failed.</exception>
    public void Reboot(int delaySec = 0, string? message = null, bool force = true) => Initiate(delaySec, message, force, reboot: true);

    /// <summary>Aborts a pending countdown; <see langword="false"/> when none was pending.</summary>
    public bool Abort()
    {
        EnableShutdownPrivilege();
        if (Advapi32.AbortSystemShutdownW(null))
        {
            _logger.LogInformation("Pending shutdown aborted");
            return true;
        }

        int error = Win32Error.Last();
        if (error == NativeConst.ERROR_NO_SHUTDOWN_IN_PROGRESS)
        {
            return false;
        }

        Win32Error.Throw(error, nameof(Advapi32.AbortSystemShutdownW));
        return false;
    }

    /// <summary>Waits until <paramref name="at"/> (clock driven, cancellable) and then initiates the shutdown or reboot.</summary>
    public async Task ScheduleShutdownAtAsync(DateTimeOffset at, bool reboot, string? message, int graceSec, CancellationToken cancellationToken)
    {
        TimeSpan delay = at - _clock.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            _logger.LogInformation("{Action} scheduled for {At} (in {Delay})", reboot ? "Reboot" : "Shutdown", at, delay);
            await _clock.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Initiate(graceSec, message, force: true, reboot);
    }

    // ---- sleep / lock / logoff ----------------------------------------------------------------

    /// <summary>Suspends to RAM. <paramref name="force"/> skips the application query on older systems.</summary>
    /// <exception cref="Win32Exception">The transition was refused.</exception>
    public void Sleep(bool force = false) => Suspend(hibernate: false, force);

    /// <summary>Hibernates (requires hibernation to be enabled).</summary>
    /// <exception cref="Win32Exception">The transition was refused.</exception>
    public void Hibernate(bool force = false) => Suspend(hibernate: true, force);

    /// <summary>Locks the workstation of the calling session. From session 0 this locks nothing visible; the Shell owns the kiosk lock screen.</summary>
    /// <exception cref="Win32Exception">The call failed.</exception>
    public void LockWorkstation() => Win32Error.ThrowIfFalse(User32.LockWorkStation(), nameof(User32.LockWorkStation));

    /// <summary>Logs off an interactive session (<c>WTSLogoffSession</c>); <paramref name="wait"/> blocks until the logoff completed.</summary>
    /// <exception cref="Win32Exception">The call failed.</exception>
    public void Logoff(uint sessionId, bool wait = false)
    {
        Win32Error.ThrowIfFalse(Wtsapi32.WTSLogoffSession(NativeConst.WTS_CURRENT_SERVER_HANDLE, sessionId, wait), nameof(Wtsapi32.WTSLogoffSession));
        _logger.LogInformation("Session {SessionId} logged off", sessionId);
    }

    // ---- power plan ---------------------------------------------------------------------------

    /// <summary>Activates a power plan (<c>powercfg /setactive</c>).</summary>
    /// <exception cref="InvalidOperationException">powercfg failed (plan missing on this edition).</exception>
    public async Task SetPowerPlanAsync(Guid planId, CancellationToken cancellationToken)
    {
        ProcessResult result = await ProcessRunner.RunAsync(_powercfg, new[] { "/setactive", planId.ToString("D") }, ToolTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"powercfg /setactive {planId:D} failed (exit {result.ExitCode}): {result.CombinedOutput.Trim()}");
        }

        _logger.LogInformation("Power plan {Plan} activated", planId);
    }

    /// <summary>Active power plan id (<c>powercfg /getactivescheme</c>), or <see langword="null"/> when it cannot be read.</summary>
    public async Task<Guid?> GetActivePowerPlanAsync(CancellationToken cancellationToken)
    {
        ProcessResult result = await ProcessRunner.RunAsync(_powercfg, new[] { "/getactivescheme" }, ToolTimeout, cancellationToken).ConfigureAwait(false);
        Match match = GuidRegex().Match(result.StandardOutput);
        return result.Success && match.Success && Guid.TryParse(match.Value, out Guid plan) ? plan : null;
    }

    // ---- keep awake ---------------------------------------------------------------------------

    /// <summary>Keeps system and display awake while <paramref name="prevent"/> is <see langword="true"/> (idempotent).</summary>
    public void PreventSleep(bool prevent)
    {
        lock (_gate)
        {
            if (prevent)
            {
                if (_keepAwakeThread is not null)
                {
                    return;
                }

                _stopKeepAwake = false;
                _keepAwakeThread = new Thread(KeepAwakeLoop) { IsBackground = true, Name = "ClubShell.KeepAwake" };
                _keepAwakeThread.Start();
                _logger.LogInformation("Sleep prevention enabled");
            }
            else if (_keepAwakeThread is not null)
            {
                _stopKeepAwake = true;
                _keepAwakeThread.Interrupt();
                _keepAwakeThread = null;
                _logger.LogInformation("Sleep prevention disabled");
            }
        }
    }

    private void KeepAwakeLoop()
    {
        try
        {
            while (!_stopKeepAwake)
            {
                _ = Kernel32.SetThreadExecutionState(NativeConst.ES_CONTINUOUS | NativeConst.ES_SYSTEM_REQUIRED | NativeConst.ES_DISPLAY_REQUIRED);
                Thread.Sleep(KeepAwakeRefresh);
            }
        }
        catch (ThreadInterruptedException)
        {
            // Woken by PreventSleep(false).
        }
        finally
        {
            _ = Kernel32.SetThreadExecutionState(NativeConst.ES_CONTINUOUS);
        }
    }

    // ---- internals ----------------------------------------------------------------------------

    private void Initiate(int delaySec, string? message, bool force, bool reboot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delaySec);
        EnableShutdownPrivilege();
        if (!Advapi32.InitiateSystemShutdownExW(null, message, (uint)delaySec, force, reboot, ShutdownReason))
        {
            Win32Error.ThrowLastError(nameof(Advapi32.InitiateSystemShutdownExW));
        }

        _logger.LogWarning("{Action} initiated (delay {Delay}s, force {Force}): {Message}", reboot ? "Reboot" : "Shutdown", delaySec, force, message ?? string.Empty);
    }

    private void Suspend(bool hibernate, bool force)
    {
        EnableShutdownPrivilege();
        if (!PowrProf.SetSuspendState(hibernate, force, bWakeupEventsDisabled: false))
        {
            Win32Error.ThrowLastError(nameof(PowrProf.SetSuspendState));
        }

        _logger.LogInformation("{Action} requested", hibernate ? "Hibernate" : "Sleep");
    }

    private void EnableShutdownPrivilege()
    {
        if (!Advapi32.EnableProcessPrivilege(NativeConst.SE_SHUTDOWN_NAME))
        {
            _logger.LogWarning("SeShutdownPrivilege could not be enabled (error {Error}); the power request will probably be denied", Win32Error.Last());
        }
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex GuidRegex();
}
