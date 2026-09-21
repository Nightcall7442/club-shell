using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using ClubShell.Windows.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace ClubShell.Windows.Processes;

/// <summary>A process appeared.</summary>
/// <param name="Pid">Process id.</param>
/// <param name="Name">Image file name.</param>
/// <param name="Path">Full image path when it could still be resolved.</param>
/// <param name="ParentPid">Parent process id.</param>
/// <param name="SessionId">WTS session id (0 = services).</param>
/// <param name="At">Observation time (UTC).</param>
public sealed record ProcessStartedEventArgs(int Pid, string Name, string? Path, int ParentPid, uint SessionId, DateTimeOffset At);

/// <summary>A process disappeared.</summary>
/// <param name="Pid">Process id.</param>
/// <param name="Name">Image file name.</param>
/// <param name="ExitCode">Exit status when known (WMI only; <see langword="null"/> in polling mode).</param>
/// <param name="At">Observation time (UTC).</param>
public sealed record ProcessExitedEventArgs(int Pid, string Name, int? ExitCode, DateTimeOffset At);

/// <summary>
/// Raises <see cref="ProcessStarted"/> / <see cref="ProcessExited"/> for every process on the machine, using
/// WMI <c>Win32_ProcessStartTrace</c> / <c>Win32_ProcessStopTrace</c> (needs SYSTEM/admin) and falling back to a
/// Toolhelp snapshot diff every <c>pollInterval</c> when WMI is unavailable. Handlers run on WMI/thread-pool threads.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessWatcher : IAsyncDisposable, IDisposable
{
    private readonly ILogger<ProcessWatcher> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly object _gate = new();
    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates a watcher (not started).</summary>
    public ProcessWatcher(ILogger<ProcessWatcher>? logger = null, TimeSpan? pollInterval = null)
    {
        _logger = logger ?? NullLogger<ProcessWatcher>.Instance;
        _pollInterval = pollInterval is { } p && p > TimeSpan.Zero ? p : TimeSpan.FromSeconds(1);
    }

    /// <summary>A process was created.</summary>
    public event EventHandler<ProcessStartedEventArgs>? ProcessStarted;

    /// <summary>A process exited.</summary>
    public event EventHandler<ProcessExitedEventArgs>? ProcessExited;

    /// <summary><see langword="true"/> when the snapshot-diff fallback is in use instead of WMI.</summary>
    public bool IsPolling { get; private set; }

    /// <summary>Subscribes to WMI traces, or starts polling when WMI fails.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
            try
            {
                StartWmi();
                IsPolling = false;
                _logger.LogInformation("Process watcher using WMI process traces");
            }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or TypeInitializationException)
            {
                _logger.LogWarning(ex, "WMI process trace unavailable; falling back to polling every {Interval}", _pollInterval);
                StopWmi();
                StartPolling();
            }
        }
    }

    /// <summary>Completes with the exit code of <paramref name="pid"/>; throws <see cref="ArgumentException"/> when no such process exists.</summary>
    public static async Task<int> WatchPidAsync(int pid, CancellationToken ct)
    {
        using SafeProcessHandle handle = Kernel32.OpenProcess(NativeConst.SYNCHRONIZE | NativeConst.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle.IsInvalid)
        {
            int error = Win32Error.Last();
            if (error == NativeConst.ERROR_INVALID_PARAMETER)
            {
                throw new ArgumentException($"Process {pid} does not exist.", nameof(pid));
            }

            Win32Error.Throw(error, nameof(Kernel32.OpenProcess));
        }

        return await ProcessWait.WaitForExitAsync(handle, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task? poll;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            poll = _pollTask;
            cts = _pollCts;
            _pollTask = null;
            _pollCts = null;
        }

        cts?.Cancel();
        if (poll is not null)
        {
            try
            {
                await poll.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        cts?.Dispose();
        await Task.Run(StopWmi).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private static string? TryGetImagePath(uint pid)
    {
        using SafeProcessHandle handle = Kernel32.OpenProcess(NativeConst.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        return handle.IsInvalid ? null : Kernel32.QueryFullProcessImageName(handle);
    }

    private static uint ReadUInt(ManagementBaseObject obj, string property)
    {
        object? value = obj.Properties[property]?.Value;
        return value is null ? 0 : Convert.ToUInt32(value, CultureInfo.InvariantCulture);
    }

    private static string ReadString(ManagementBaseObject obj, string property) =>
        obj.Properties[property]?.Value as string ?? string.Empty;

    private void StartWmi()
    {
        _startWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
        _startWatcher.EventArrived += OnWmiStart;
        _stopWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace"));
        _stopWatcher.EventArrived += OnWmiStop;
        _startWatcher.Start();
        _stopWatcher.Start();
    }

    private void StopWmi()
    {
        ManagementEventWatcher? start = Interlocked.Exchange(ref _startWatcher, null);
        ManagementEventWatcher? stop = Interlocked.Exchange(ref _stopWatcher, null);
        foreach (ManagementEventWatcher? watcher in new[] { start, stop })
        {
            if (watcher is null)
            {
                continue;
            }

            try
            {
                watcher.Stop();
            }
            catch (ManagementException ex)
            {
                _logger.LogDebug(ex, "WMI watcher stop failed");
            }

            watcher.Dispose();
        }
    }

    private void OnWmiStart(object? sender, EventArrivedEventArgs e)
    {
        try
        {
            uint pid = ReadUInt(e.NewEvent, "ProcessID");
            uint parent = ReadUInt(e.NewEvent, "ParentProcessID");
            uint session = ReadUInt(e.NewEvent, "SessionID");
            string name = ReadString(e.NewEvent, "ProcessName");
            Raise(new ProcessStartedEventArgs((int)pid, name, TryGetImagePath(pid), (int)parent, session, DateTimeOffset.UtcNow));
        }
        catch (Exception ex) when (ex is ManagementException or InvalidCastException or FormatException)
        {
            _logger.LogDebug(ex, "Malformed Win32_ProcessStartTrace event");
        }
    }

    private void OnWmiStop(object? sender, EventArrivedEventArgs e)
    {
        try
        {
            uint pid = ReadUInt(e.NewEvent, "ProcessID");
            string name = ReadString(e.NewEvent, "ProcessName");
            int exit = unchecked((int)ReadUInt(e.NewEvent, "ExitStatus"));
            Raise(new ProcessExitedEventArgs((int)pid, name, exit, DateTimeOffset.UtcNow));
        }
        catch (Exception ex) when (ex is ManagementException or InvalidCastException or FormatException)
        {
            _logger.LogDebug(ex, "Malformed Win32_ProcessStopTrace event");
        }
    }

    private void StartPolling()
    {
        IsPolling = true;
        var cts = new CancellationTokenSource();
        _pollCts = cts;
        _pollTask = Task.Run(() => PollLoopAsync(cts.Token));
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        Dictionary<int, ProcessSnapshotEntry> known = Toolhelp32.Snapshot().ToDictionary(e => e.Pid);
        using var timer = new PeriodicTimer(_pollInterval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            Dictionary<int, ProcessSnapshotEntry> current;
            try
            {
                current = Toolhelp32.Snapshot().ToDictionary(e => e.Pid);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _logger.LogDebug(ex, "Process snapshot failed; retrying next tick");
                continue;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (ProcessSnapshotEntry entry in current.Values)
            {
                if (!known.ContainsKey(entry.Pid))
                {
                    uint session = Kernel32.ProcessIdToSessionId((uint)entry.Pid, out uint s) ? s : 0;
                    Raise(new ProcessStartedEventArgs(entry.Pid, entry.ExeName, TryGetImagePath((uint)entry.Pid), entry.ParentPid, session, now));
                }
            }

            foreach (ProcessSnapshotEntry entry in known.Values)
            {
                if (!current.ContainsKey(entry.Pid))
                {
                    Raise(new ProcessExitedEventArgs(entry.Pid, entry.ExeName, null, now));
                }
            }

            known = current;
        }
    }

    private void Raise(ProcessStartedEventArgs args)
    {
        try
        {
            ProcessStarted?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProcessStarted handler threw for pid {Pid}", args.Pid);
        }
    }

    private void Raise(ProcessExitedEventArgs args)
    {
        try
        {
            ProcessExited?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProcessExited handler threw for pid {Pid}", args.Pid);
        }
    }
}
