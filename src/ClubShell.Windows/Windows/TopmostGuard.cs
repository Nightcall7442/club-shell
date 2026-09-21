using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using ClubShell.Windows.Hooks;
using ClubShell.Windows.Native;

using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace ClubShell.Windows.Windows;

/// <summary>
/// Keeps a target window (the kiosk Shell) topmost and in the foreground. It reacts instantly to focus theft
/// through an <c>EVENT_SYSTEM_FOREGROUND</c> WinEvent hook and re-asserts on a periodic timer as a safety net.
/// An allowlist of window handles, process ids and process names may legitimately hold the foreground (a
/// running game, a remote-admin overlay); <see cref="Pause"/>/<see cref="Resume"/> suspend the guard for the
/// duration of a game session. Thread-safe.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TopmostGuard : IDisposable
{
    private readonly ILogger? _logger;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private readonly HashSet<nint> _allowedHwnds = [];
    private readonly HashSet<uint> _allowedPids = [];
    private readonly HashSet<string> _allowedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly WinEventProc _winEventProc; // kept alive by the field for the hook's lifetime
    private readonly nint _winEventProcPtr;      // unmanaged thunk of _winEventProc

    private nint _target;
    private int _paused;
    private int _reasserting;

    private MessagePumpThread? _pump;
    private SafeWinEventHookHandle? _winHook;
    private Timer? _timer;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates the guard. <paramref name="interval"/> is the re-assert cadence (default 250 ms).</summary>
    public TopmostGuard(nint target = 0, TimeSpan? interval = null, ILogger? logger = null)
    {
        _target = target;
        _interval = interval is { } i && i > TimeSpan.Zero ? i : TimeSpan.FromMilliseconds(250);
        _logger = logger;
        _winEventProc = OnForegroundChanged;
        _winEventProcPtr = Marshal.GetFunctionPointerForDelegate(_winEventProc);
    }

    /// <summary>Whether the guard is currently paused.</summary>
    public bool IsPaused => Volatile.Read(ref _paused) == 1;

    /// <summary>The window the guard keeps on top. Update it when the Shell window is recreated.</summary>
    public void SetTarget(nint hwnd) => Volatile.Write(ref _target, hwnd);

    /// <summary>Starts guarding. Throws <see cref="System.ComponentModel.Win32Exception"/> if the WinEvent hook cannot be set.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _pump = new MessagePumpThread("clubshell-topmost", InstallHookOrThrow, DisposeHook);
            try
            {
                _pump.Start();
            }
            catch
            {
                MessagePumpThread? pump = _pump;
                _pump = null;
                pump?.Dispose();
                throw;
            }

            _timer = new Timer(_ => Reassert(User32.GetForegroundWindow()), null, _interval, _interval);
            _started = true;
        }
    }

    /// <summary>Stops guarding. Safe to call more than once.</summary>
    public void Stop()
    {
        MessagePumpThread? pump;
        Timer? timer;

        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            pump = _pump;
            timer = _timer;
            _pump = null;
            _timer = null;
        }

        timer?.Dispose();
        pump?.Dispose();
    }

    /// <summary>Suspends re-assertion and drops the target's topmost flag so a game can cover it.</summary>
    public void Pause()
    {
        Volatile.Write(ref _paused, 1);
        nint target = Volatile.Read(ref _target);
        if (target != 0 && User32.IsWindow(target))
        {
            _ = User32.SetTopmost(target, false);
        }
    }

    /// <summary>Resumes re-assertion and immediately re-asserts against the current foreground window.</summary>
    public void Resume()
    {
        Volatile.Write(ref _paused, 0);
        Reassert(User32.GetForegroundWindow());
    }

    /// <summary>Permits a specific window to hold the foreground.</summary>
    public void AllowWindow(nint hwnd)
    {
        lock (_gate)
        {
            _allowedHwnds.Add(hwnd);
        }
    }

    /// <summary>Permits every window of a process id to hold the foreground (e.g. a launched game).</summary>
    public void AllowPid(uint pid)
    {
        lock (_gate)
        {
            _allowedPids.Add(pid);
        }
    }

    /// <summary>Permits every window of a process by executable name (without extension, case-insensitive).</summary>
    public void AllowProcess(string processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        lock (_gate)
        {
            _allowedNames.Add(processName);
        }
    }

    /// <summary>Removes a previously allowed window handle.</summary>
    public void DisallowWindow(nint hwnd)
    {
        lock (_gate)
        {
            _ = _allowedHwnds.Remove(hwnd);
        }
    }

    /// <summary>Removes a previously allowed process id.</summary>
    public void DisallowPid(uint pid)
    {
        lock (_gate)
        {
            _ = _allowedPids.Remove(pid);
        }
    }

    /// <summary>Clears every allowlist entry.</summary>
    public void ClearAllowlist()
    {
        lock (_gate)
        {
            _allowedHwnds.Clear();
            _allowedPids.Clear();
            _allowedNames.Clear();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void InstallHookOrThrow()
    {
        SafeWinEventHookHandle hook = User32.SetWinEventHook(
            NativeConst.EVENT_SYSTEM_FOREGROUND,
            NativeConst.EVENT_SYSTEM_FOREGROUND,
            0,
            _winEventProcPtr,
            0,
            0,
            NativeConst.WINEVENT_OUTOFCONTEXT | NativeConst.WINEVENT_SKIPOWNPROCESS);
        if (hook.IsInvalid)
        {
            int error = Win32Error.Last();
            hook.Dispose();
            Win32Error.Throw(error, nameof(User32.SetWinEventHook));
        }

        _winHook = hook;
    }

    private void DisposeHook()
    {
        _winHook?.Dispose();
        _winHook = null;
    }

    private void OnForegroundChanged(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (eventType == NativeConst.EVENT_SYSTEM_FOREGROUND && idObject == NativeConst.OBJID_WINDOW && Volatile.Read(ref _paused) == 0)
        {
            Reassert(hwnd);
        }
    }

    private void Reassert(nint foreground)
    {
        if (Volatile.Read(ref _paused) == 1)
        {
            return;
        }

        nint target = Volatile.Read(ref _target);
        if (target == 0 || !User32.IsWindow(target))
        {
            return;
        }

        if (Interlocked.Exchange(ref _reasserting, 1) == 1)
        {
            return; // a re-assert is already in flight
        }

        try
        {
            _ = User32.SetTopmost(target, true);
            if (foreground != target && !IsAllowed(foreground))
            {
                _logger?.LogDebug("Reclaiming foreground from window {Foreground} for kiosk target {Target}", foreground, target);
                _ = WindowManager.ForceForeground(target);
            }
        }
        finally
        {
            Volatile.Write(ref _reasserting, 0);
        }
    }

    private bool IsAllowed(nint hwnd)
    {
        if (hwnd == 0)
        {
            return true; // no foreground window to contend with
        }

        if (hwnd == Volatile.Read(ref _target))
        {
            return true;
        }

        lock (_gate)
        {
            if (_allowedHwnds.Contains(hwnd))
            {
                return true;
            }
        }

        _ = User32.GetWindowThreadProcessId(hwnd, out uint pid);

        lock (_gate)
        {
            if (_allowedPids.Contains(pid))
            {
                return true;
            }
        }

        string? name = ProcessName(pid);
        if (name is not null)
        {
            lock (_gate)
            {
                if (_allowedNames.Contains(name))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? ProcessName(uint pid)
    {
        using SafeProcessHandle handle = Kernel32.OpenProcess(NativeConst.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle.IsInvalid)
        {
            return null;
        }

        string? path = Kernel32.QueryFullProcessImageName(handle);
        return path is null ? null : Path.GetFileNameWithoutExtension(path);
    }
}
