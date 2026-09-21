using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

using ClubShell.Windows.Native;

using Microsoft.Extensions.Logging;

namespace ClubShell.Windows.Hooks;

/// <summary>
/// A mouse event surfaced by <see cref="LowLevelMouseHook.Activity"/>, used for idle tracking and diagnostics.
/// Raised off the hook thread and throttled so a busy pointer does not flood subscribers.
/// </summary>
/// <param name="X">Screen X of the pointer.</param>
/// <param name="Y">Screen Y of the pointer.</param>
/// <param name="Message">The mouse message (WM_MOUSEMOVE, WM_LBUTTONDOWN, ...).</param>
/// <param name="Injected"><see langword="true"/> when the event was synthesized.</param>
public readonly record struct MouseActivity(int X, int Y, uint Message, bool Injected);

/// <summary>
/// A WH_MOUSE_LL hook for kiosk lockdown: optional right-click suppression, cursor confinement to a rectangle,
/// edge-swipe suppression and pointer-activity reporting. Same dedicated message-loop thread and reinstall
/// watchdog as <see cref="LowLevelKeyboardHook"/>; the callback is allocation-free and returns in well under a
/// millisecond.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LowLevelMouseHook : IDisposable
{
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger? _logger;
    private readonly HookProc _proc; // kept alive by the field for the hook's lifetime
    private readonly nint _procPtr;  // unmanaged thunk of _proc
    private readonly object _sync = new();

    private volatile bool _enabled = true;
    private volatile bool _blockRightClick;
    private volatile bool _suppressEdgeSwipe;
    private volatile int _edgeBandPx = 20;
    private volatile int _activityThrottleMs = 100;
    private ConfineRegion? _confine;

    private long _callbackCount;
    private long _lastActivityTick;

    private MessagePumpThread? _pump;
    private SafeHookHandle? _hook;
    private Timer? _watchdog;

    private Channel<MouseActivity>? _events;
    private CancellationTokenSource? _consumerCts;
    private Task? _consumer;

    private bool _started;
    private bool _disposed;

    /// <summary>Creates the hook.</summary>
    public LowLevelMouseHook(ILogger? logger = null)
    {
        _logger = logger;
        _proc = HookCallback;
        _procPtr = Marshal.GetFunctionPointerForDelegate(_proc);
    }

    /// <summary>Raised (throttled, off the hook thread) for pointer movement and button events.</summary>
    public event EventHandler<MouseActivity>? Activity;

    /// <summary>Whether the hook enforces its rules. When <see langword="false"/> it only reports activity.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Swallow right mouse button down/up/double-click (blocks context menus).</summary>
    public bool BlockRightClick
    {
        get => _blockRightClick;
        set => _blockRightClick = value;
    }

    /// <summary>Swallow injected moves in the outer edge band (defeats touch edge-swipe gestures).</summary>
    public bool SuppressEdgeSwipe
    {
        get => _suppressEdgeSwipe;
        set => _suppressEdgeSwipe = value;
    }

    /// <summary>Width in pixels of the edge band used by <see cref="SuppressEdgeSwipe"/> (default 20).</summary>
    public int EdgeBandPx
    {
        get => _edgeBandPx;
        set => _edgeBandPx = Math.Max(1, value);
    }

    /// <summary>Minimum interval between <see cref="Activity"/> events in milliseconds (default 100).</summary>
    public int ActivityThrottleMs
    {
        get => _activityThrottleMs;
        set => _activityThrottleMs = Math.Max(0, value);
    }

    /// <summary>Number of times the hook callback has run since start (a liveness probe).</summary>
    public long CallbackCount => Interlocked.Read(ref _callbackCount);

    /// <summary>
    /// Confines the cursor to <paramref name="rect"/> (screen coordinates) via ClipCursor, and swallows injected
    /// moves that escape it. Pass <see langword="null"/> to release the confinement.
    /// </summary>
    public void SetConfineRect(RECT? rect)
    {
        if (rect is { } r)
        {
            Interlocked.Exchange(ref _confine, new ConfineRegion(r));
            _ = User32.ClipCursor(ref r);
        }
        else
        {
            Interlocked.Exchange(ref _confine, null);
            _ = User32.ClipCursor(0);
        }
    }

    /// <summary>Installs the hook and starts the message loop. Throws <see cref="System.ComponentModel.Win32Exception"/> if the hook cannot be set.</summary>
    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _consumerCts = new CancellationTokenSource();
            _events = Channel.CreateBounded<MouseActivity>(new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            });
            ChannelReader<MouseActivity> reader = _events.Reader;
            CancellationToken token = _consumerCts.Token;
            _consumer = Task.Run(() => ConsumeAsync(reader, token));

            _pump = new MessagePumpThread("clubshell-mousehook", InstallHookOrThrow, DisposeHook);
            try
            {
                _pump.Start();
            }
            catch
            {
                CleanupFailedStart();
                throw;
            }

            _watchdog = new Timer(_ => _pump?.Post(ReinstallHook), null, WatchdogInterval, WatchdogInterval);
            _started = true;
        }
    }

    /// <summary>Removes the hook, releases any confinement and stops the message loop. Safe to call more than once.</summary>
    public void Stop()
    {
        MessagePumpThread? pump;
        Timer? watchdog;
        CancellationTokenSource? cts;
        Task? consumer;
        Channel<MouseActivity>? events;

        lock (_sync)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            pump = _pump;
            watchdog = _watchdog;
            cts = _consumerCts;
            consumer = _consumer;
            events = _events;
            _pump = null;
            _watchdog = null;
            _consumerCts = null;
            _consumer = null;
            _events = null;
        }

        if (Volatile.Read(ref _confine) is not null)
        {
            SetConfineRect(null);
        }

        watchdog?.Dispose();
        pump?.Dispose();
        events?.Writer.TryComplete();
        try
        {
            cts?.Cancel();
            consumer?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is OperationCanceledException or AggregateException)
        {
            // Expected during shutdown.
        }
        finally
        {
            cts?.Dispose();
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

    // Called under _sync when Start fails: the pump thread has already exited, so tear down the consumer.
    private void CleanupFailedStart()
    {
        MessagePumpThread? pump = _pump;
        _pump = null;
        pump?.Dispose();
        _events?.Writer.TryComplete();
        try
        {
            _consumerCts?.Cancel();
            _consumer?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is OperationCanceledException or AggregateException)
        {
            // Expected while unwinding.
        }
        finally
        {
            _consumerCts?.Dispose();
            _consumerCts = null;
            _consumer = null;
            _events = null;
        }
    }

    private void InstallHookOrThrow() => InstallCore(throwOnError: true);

    private void ReinstallHook() => InstallCore(throwOnError: false);

    private void InstallCore(bool throwOnError)
    {
        SafeHookHandle fresh = User32.SetWindowsHookExW(NativeConst.WH_MOUSE_LL, _procPtr, Kernel32.GetModuleHandleW(null), 0);
        if (fresh.IsInvalid)
        {
            int error = Win32Error.Last();
            fresh.Dispose();
            if (throwOnError)
            {
                Win32Error.Throw(error, nameof(User32.SetWindowsHookExW));
            }

            _logger?.LogWarning("Mouse hook reinstall failed with Win32 error {Error}", error);
            return;
        }

        SafeHookHandle? previous = Interlocked.Exchange(ref _hook, fresh);
        previous?.Dispose();
    }

    private void DisposeHook() => Interlocked.Exchange(ref _hook, null)?.Dispose();

    private unsafe nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode == NativeConst.HC_ACTION)
        {
            Interlocked.Increment(ref _callbackCount);

            ref readonly MSLLHOOKSTRUCT data = ref Unsafe.AsRef<MSLLHOOKSTRUCT>((void*)lParam);
            uint message = (uint)wParam;
            bool injected = data.IsInjected;
            POINT pt = data.pt;

            bool swallow = false;
            if (_enabled)
            {
                if (_blockRightClick && IsRightButton(message))
                {
                    swallow = true;
                }
                else if (message == NativeConst.WM_MOUSEMOVE)
                {
                    if (_suppressEdgeSwipe && injected && NearEdge(pt))
                    {
                        swallow = true;
                    }
                    else if (injected && Volatile.Read(ref _confine) is { } region && !Contains(region.Rect, pt))
                    {
                        swallow = true;
                    }
                }
            }

            RaiseActivity(pt, message, injected);

            if (swallow)
            {
                return 1;
            }
        }

        return User32.CallNextHookEx(0, nCode, wParam, lParam);
    }

    private void RaiseActivity(POINT pt, uint message, bool injected)
    {
        if (Activity is null)
        {
            return;
        }

        long now = Environment.TickCount64;
        int throttle = _activityThrottleMs;
        if (throttle > 0 && now - _lastActivityTick < throttle)
        {
            return;
        }

        _lastActivityTick = now;
        _events?.Writer.TryWrite(new MouseActivity(pt.X, pt.Y, message, injected));
    }

    private static bool IsRightButton(uint message) =>
        message is NativeConst.WM_RBUTTONDOWN or NativeConst.WM_RBUTTONUP or NativeConst.WM_RBUTTONDBLCLK;

    private static bool Contains(in RECT rect, POINT pt) =>
        pt.X >= rect.Left && pt.X < rect.Right && pt.Y >= rect.Top && pt.Y < rect.Bottom;

    private bool NearEdge(POINT pt)
    {
        int left = User32.GetSystemMetrics(NativeConst.SM_XVIRTUALSCREEN);
        int top = User32.GetSystemMetrics(NativeConst.SM_YVIRTUALSCREEN);
        int right = left + User32.GetSystemMetrics(NativeConst.SM_CXVIRTUALSCREEN);
        int bottom = top + User32.GetSystemMetrics(NativeConst.SM_CYVIRTUALSCREEN);
        int band = _edgeBandPx;
        return pt.X <= left + band || pt.X >= right - band || pt.Y <= top + band || pt.Y >= bottom - band;
    }

    private async Task ConsumeAsync(ChannelReader<MouseActivity> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (MouseActivity evt in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    Activity?.Invoke(this, evt);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "A mouse Activity handler threw");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    /// <summary>Immutable holder so the confinement rectangle can be swapped atomically and read lock-free.</summary>
    private sealed class ConfineRegion(RECT rect)
    {
        public RECT Rect { get; } = rect;
    }
}
