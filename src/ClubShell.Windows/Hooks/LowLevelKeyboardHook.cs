using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

using ClubShell.Windows.Native;

using Microsoft.Extensions.Logging;

namespace ClubShell.Windows.Hooks;

/// <summary>
/// A single observed keyboard event delivered by <see cref="LowLevelKeyboardHook.Observed"/>. Raised off the
/// hook thread (via a channel) so subscribers can run arbitrary work without stalling input processing.
/// </summary>
/// <param name="Key">The virtual key.</param>
/// <param name="VkCode">Raw virtual-key code from the hook.</param>
/// <param name="ScanCode">Hardware scan code.</param>
/// <param name="Modifiers">Modifiers held at the time of the event (as tracked by the hook).</param>
/// <param name="IsDown"><see langword="true"/> for key-down / sys-key-down, <see langword="false"/> for key-up.</param>
/// <param name="IsInjected"><see langword="true"/> when the event was synthesized (e.g. SendInput).</param>
/// <param name="Blocked"><see langword="true"/> when the hook swallowed this event.</param>
public readonly record struct KeyEvent(
    VirtualKey Key,
    uint VkCode,
    uint ScanCode,
    Modifiers Modifiers,
    bool IsDown,
    bool IsInjected,
    bool Blocked);

/// <summary>
/// A WH_KEYBOARD_LL hook that swallows a configurable set of <see cref="KeyCombo"/>s (kiosk lockdown) and
/// reports every key through <see cref="Observed"/>. The hook runs on a dedicated message-loop thread; a
/// watchdog re-installs it periodically because Windows silently drops a low-level hook whose callback ever
/// exceeds <c>LowLevelHooksTimeout</c>. Thread-safe; the hook callback is allocation-free and returns in
/// well under a millisecond.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LowLevelKeyboardHook : IDisposable
{
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger? _logger;
    private readonly HookProc _proc;               // kept alive by the field for the hook's lifetime
    private readonly nint _procPtr;                // unmanaged thunk of _proc
    private readonly object _sync = new();

    private ImmutableHashSet<KeyCombo> _blocked;
    private volatile bool _enabled = true;
    private volatile bool _blockAll;

    // Modifier state is mutated only from the (single) hook thread, so no synchronization is needed.
    private bool _ctrl;
    private bool _alt;
    private bool _shift;
    private bool _win;

    private long _callbackCount;

    private MessagePumpThread? _pump;
    private SafeHookHandle? _hook;
    private Timer? _watchdog;

    private Channel<KeyEvent>? _events;
    private CancellationTokenSource? _consumerCts;
    private Task? _consumer;

    private bool _started;
    private bool _disposed;

    /// <summary>Creates the hook with an optional initial block set (defaults to <see cref="BlockedKeyCombos.Default"/>).</summary>
    public LowLevelKeyboardHook(IEnumerable<KeyCombo>? blocked = null, ILogger? logger = null)
    {
        _logger = logger;
        _blocked = blocked?.ToImmutableHashSet() ?? BlockedKeyCombos.Default;
        _proc = HookCallback;
        _procPtr = Marshal.GetFunctionPointerForDelegate(_proc);
    }

    /// <summary>Raised for every key the hook sees (down and up), off the hook thread.</summary>
    public event EventHandler<KeyEvent>? Observed;

    /// <summary>Whether combo blocking is active. When <see langword="false"/> the hook observes but never swallows.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>When <see langword="true"/> every non key-repeat event is swallowed (used for a full soft lock).</summary>
    public bool BlockAll
    {
        get => _blockAll;
        set => _blockAll = value;
    }

    /// <summary>The set of combinations to swallow. Swapped atomically; reads in the hook are lock-free.</summary>
    public ImmutableHashSet<KeyCombo> Blocked
    {
        get => Volatile.Read(ref _blocked);
        set => Interlocked.Exchange(ref _blocked, value ?? ImmutableHashSet<KeyCombo>.Empty);
    }

    /// <summary>Number of times the hook callback has run since start (a liveness probe for tests/diagnostics).</summary>
    public long CallbackCount => Interlocked.Read(ref _callbackCount);

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
            _events = Channel.CreateBounded<KeyEvent>(new BoundedChannelOptions(4096)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            });
            ChannelReader<KeyEvent> reader = _events.Reader;
            CancellationToken token = _consumerCts.Token;
            _consumer = Task.Run(() => ConsumeAsync(reader, token));

            _pump = new MessagePumpThread("clubshell-kbhook", InstallHookOrThrow, DisposeHook);
            try
            {
                _pump.Start(); // rethrows a failure from InstallHookOrThrow
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

    /// <summary>Removes the hook and stops the message loop. Safe to call more than once.</summary>
    public void Stop()
    {
        MessagePumpThread? pump;
        Timer? watchdog;
        CancellationTokenSource? cts;
        Task? consumer;
        Channel<KeyEvent>? events;

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

        watchdog?.Dispose();
        pump?.Dispose(); // stops the loop and disposes the hook on the pump thread (DisposeHook)
        events?.Writer.TryComplete();
        try
        {
            cts?.Cancel();
            consumer?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is OperationCanceledException or AggregateException)
        {
            // Consumer cancellation is expected during shutdown.
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

    // Runs on the pump thread. Installs a fresh hook ahead of any survivor, then removes the previous one so
    // no key events are lost across a reinstall.
    private void InstallCore(bool throwOnError)
    {
        SafeHookHandle fresh = User32.SetWindowsHookExW(NativeConst.WH_KEYBOARD_LL, _procPtr, Kernel32.GetModuleHandleW(null), 0);
        if (fresh.IsInvalid)
        {
            int error = Win32Error.Last();
            fresh.Dispose();
            if (throwOnError)
            {
                Win32Error.Throw(error, nameof(User32.SetWindowsHookExW));
            }

            _logger?.LogWarning("Keyboard hook reinstall failed with Win32 error {Error}", error);
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

            ref readonly KBDLLHOOKSTRUCT data = ref Unsafe.AsRef<KBDLLHOOKSTRUCT>((void*)lParam);
            uint message = (uint)wParam;
            bool isDown = message == NativeConst.WM_KEYDOWN || message == NativeConst.WM_SYSKEYDOWN;
            bool isUp = message == NativeConst.WM_KEYUP || message == NativeConst.WM_SYSKEYUP;
            var key = (VirtualKey)(int)data.vkCode;

            UpdateModifiers(key, isDown, isUp);
            Modifiers mods = CurrentModifiers();

            bool swallow = _enabled && (_blockAll || ShouldBlock(key, mods));

            if (Observed is not null)
            {
                _events?.Writer.TryWrite(new KeyEvent(key, data.vkCode, data.scanCode, mods, isDown, data.IsInjected, swallow));
            }

            if (swallow)
            {
                return 1;
            }
        }

        return User32.CallNextHookEx(0, nCode, wParam, lParam);
    }

    private bool ShouldBlock(VirtualKey key, Modifiers mods)
    {
        ImmutableHashSet<KeyCombo> set = Volatile.Read(ref _blocked);
        if (set.IsEmpty)
        {
            return false;
        }

        // Exclude the pressed key's own modifier bit so "Win" alone is (LWin, None), not (LWin, Win).
        Modifiers selfless = mods & ~KeyCombo.ModifierOf(key);
        if (set.Contains(new KeyCombo(key, selfless)))
        {
            return true;
        }

        // Wildcard: any key held with exactly these modifiers (e.g. Win+* for every Start-menu shortcut).
        return selfless != Modifiers.None && set.Contains(new KeyCombo(VirtualKey.None, selfless));
    }

    private void UpdateModifiers(VirtualKey key, bool isDown, bool isUp)
    {
        if (!isDown && !isUp)
        {
            return;
        }

        bool value = isDown;
        switch (KeyCombo.ModifierOf(key))
        {
            case Modifiers.Ctrl:
                _ctrl = value;
                break;
            case Modifiers.Alt:
                _alt = value;
                break;
            case Modifiers.Shift:
                _shift = value;
                break;
            case Modifiers.Win:
                _win = value;
                break;
            default:
                break;
        }
    }

    private Modifiers CurrentModifiers()
    {
        Modifiers mods = Modifiers.None;
        if (_ctrl)
        {
            mods |= Modifiers.Ctrl;
        }

        if (_alt)
        {
            mods |= Modifiers.Alt;
        }

        if (_shift)
        {
            mods |= Modifiers.Shift;
        }

        if (_win)
        {
            mods |= Modifiers.Win;
        }

        return mods;
    }

    private async Task ConsumeAsync(ChannelReader<KeyEvent> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (KeyEvent evt in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    Observed?.Invoke(this, evt);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "A keyboard Observed handler threw");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }
}

/// <summary>
/// A dedicated background thread that owns a Win32 message queue: it runs <paramref name="onStart"/> on the
/// thread (where the owner installs its hook), pumps messages, runs delegates posted with <see cref="Post"/>
/// on the same thread, and runs <c>onStop</c> when the loop exits. Low-level hooks and out-of-context WinEvent
/// hooks both require this: their callbacks are delivered on the thread that installed them and that thread
/// must pump messages. Shared by the keyboard/mouse hooks and <c>TopmostGuard</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MessagePumpThread : IDisposable
{
    private const uint WmRunAction = NativeConst.WM_APP + 0x0101;

    private readonly string _name;
    private readonly Action _onStart;
    private readonly Action _onStop;
    private readonly ManualResetEventSlim _ready = new(initialState: false);
    private readonly ConcurrentQueue<Action> _actions = new();

    private Thread? _thread;
    private uint _threadId;
    private ExceptionDispatchInfo? _startError;
    private volatile bool _stopping;

    public MessagePumpThread(string name, Action onStart, Action onStop)
    {
        _name = name;
        _onStart = onStart;
        _onStop = onStop;
    }

    /// <summary>Starts the thread and blocks until <c>onStart</c> has run; rethrows any exception it raised.</summary>
    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = _name };
        _thread.Start();
        _ready.Wait();
        _startError?.Throw();
    }

    /// <summary>Queues <paramref name="action"/> to run on the pump thread. No-op after <see cref="Stop"/>.</summary>
    public void Post(Action action)
    {
        if (_thread is null || _stopping)
        {
            return;
        }

        _actions.Enqueue(action);
        _ = User32.PostThreadMessageW(_threadId, WmRunAction, 0, 0);
    }

    /// <summary>Signals the loop to exit and joins the thread (bounded wait).</summary>
    public void Stop()
    {
        Thread? thread = _thread;
        if (thread is null)
        {
            return;
        }

        _stopping = true;
        _ = User32.PostThreadMessageW(_threadId, NativeConst.WM_QUIT, 0, 0);
        _ = thread.Join(TimeSpan.FromSeconds(5));
        _thread = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        _ready.Dispose();
    }

    private void Run()
    {
        // Force the thread's message queue into existence so PostThreadMessage can target it.
        _ = User32.PeekMessageW(out _, 0, NativeConst.WM_USER, NativeConst.WM_USER, NativeConst.PM_NOREMOVE);
        _threadId = Kernel32.GetCurrentThreadId();

        try
        {
            _onStart();
        }
        catch (Exception ex)
        {
            _startError = ExceptionDispatchInfo.Capture(ex);
            _ready.Set();
            return;
        }

        _ready.Set();

        while (true)
        {
            int result = User32.GetMessageW(out MSG msg, 0, 0, 0);
            if (result is 0 or -1)
            {
                break; // WM_QUIT (0) or error (-1)
            }

            if (msg.hwnd == 0 && msg.message == WmRunAction)
            {
                while (_actions.TryDequeue(out Action? action))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception)
                    {
                        // A posted action must never tear down the pump; owners log inside their own callbacks.
                    }
                }

                continue;
            }

            _ = User32.TranslateMessage(ref msg);
            _ = User32.DispatchMessageW(ref msg);
        }

        _onStop();
    }
}
