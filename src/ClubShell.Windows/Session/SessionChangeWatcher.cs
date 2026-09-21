using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClubShell.Windows.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Windows.Sessions;

/// <summary>Why a session changed (WM_WTSSESSION_CHANGE wParam).</summary>
public enum SessionChangeReason
{
    /// <summary>WTS_CONSOLE_CONNECT.</summary>
    ConsoleConnect,

    /// <summary>WTS_CONSOLE_DISCONNECT.</summary>
    ConsoleDisconnect,

    /// <summary>WTS_REMOTE_CONNECT.</summary>
    RemoteConnect,

    /// <summary>WTS_REMOTE_DISCONNECT.</summary>
    RemoteDisconnect,

    /// <summary>WTS_SESSION_LOGON.</summary>
    Logon,

    /// <summary>WTS_SESSION_LOGOFF.</summary>
    Logoff,

    /// <summary>WTS_SESSION_LOCK.</summary>
    Lock,

    /// <summary>WTS_SESSION_UNLOCK.</summary>
    Unlock,

    /// <summary>WTS_SESSION_REMOTE_CONTROL.</summary>
    RemoteControl,

    /// <summary>WTS_SESSION_CREATE.</summary>
    SessionCreate,

    /// <summary>WTS_SESSION_TERMINATE.</summary>
    SessionRemove,
}

/// <summary>A session change notification.</summary>
/// <param name="Reason">What happened.</param>
/// <param name="SessionId">Affected session.</param>
/// <param name="At">Observation time (UTC).</param>
public sealed record SessionChangedEventArgs(SessionChangeReason Reason, uint SessionId, DateTimeOffset At);

/// <summary>
/// Delivers WTS session change notifications from a message-only window on a dedicated thread registered with
/// WTSRegisterSessionNotification(NOTIFY_FOR_ALL_SESSIONS). When the window cannot be created (no window station)
/// it falls back to diffing <see cref="WtsSessions.Enumerate"/> every <c>pollInterval</c>; the fallback cannot see
/// lock/unlock. Handlers run on the watcher thread and must not block.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionChangeWatcher : IAsyncDisposable, IDisposable
{
    private const uint WmStop = NativeConst.WM_APP + 0x51;
    private static readonly TimeSpan ThreadJoinTimeout = TimeSpan.FromSeconds(5);
    private readonly ILogger<SessionChangeWatcher> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly WndProc _wndProc;
    private readonly object _gate = new();
    private Thread? _thread;
    private nint _hwnd;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates a watcher (not started).</summary>
    public SessionChangeWatcher(ILogger<SessionChangeWatcher>? logger = null, TimeSpan? pollInterval = null)
    {
        _logger = logger ?? NullLogger<SessionChangeWatcher>.Instance;
        _pollInterval = pollInterval is { } p && p > TimeSpan.Zero ? p : TimeSpan.FromSeconds(2);
        _wndProc = WindowProc;
    }

    /// <summary>Raised for every session change.</summary>
    public event EventHandler<SessionChangedEventArgs>? SessionChanged;

    /// <summary><see langword="true"/> when the enumeration-diff fallback is active instead of window notifications.</summary>
    public bool IsPolling { get; private set; }

    /// <summary>Maps a WM_WTSSESSION_CHANGE wParam to a reason; <see langword="null"/> for unknown codes.</summary>
    public static SessionChangeReason? MapReason(uint wParam) => wParam switch
    {
        NativeConst.WTS_CONSOLE_CONNECT => SessionChangeReason.ConsoleConnect,
        NativeConst.WTS_CONSOLE_DISCONNECT => SessionChangeReason.ConsoleDisconnect,
        NativeConst.WTS_REMOTE_CONNECT => SessionChangeReason.RemoteConnect,
        NativeConst.WTS_REMOTE_DISCONNECT => SessionChangeReason.RemoteDisconnect,
        NativeConst.WTS_SESSION_LOGON => SessionChangeReason.Logon,
        NativeConst.WTS_SESSION_LOGOFF => SessionChangeReason.Logoff,
        NativeConst.WTS_SESSION_LOCK => SessionChangeReason.Lock,
        NativeConst.WTS_SESSION_UNLOCK => SessionChangeReason.Unlock,
        NativeConst.WTS_SESSION_REMOTE_CONTROL => SessionChangeReason.RemoteControl,
        NativeConst.WTS_SESSION_CREATE => SessionChangeReason.SessionCreate,
        NativeConst.WTS_SESSION_TERMINATE => SessionChangeReason.SessionRemove,
        _ => null,
    };

    /// <summary>Starts the notification window thread, or polling when the window cannot be set up.</summary>
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
        }

        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => MessageLoop(ready))
        {
            Name = "ClubShell.SessionChangeWatcher",
            IsBackground = true,
        };
        thread.Start();
        try
        {
            ready.Task.GetAwaiter().GetResult();
            _thread = thread;
            IsPolling = false;
            _logger.LogInformation("Session change watcher registered for WM_WTSSESSION_CHANGE");
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex, "Session notification window unavailable; polling WTS sessions every {Interval}", _pollInterval);
            thread.Join(ThreadJoinTimeout);
            StartPolling();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Thread? thread;
        nint hwnd;
        Task? poll;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            thread = _thread;
            hwnd = _hwnd;
            poll = _pollTask;
            cts = _pollCts;
            _thread = null;
            _pollTask = null;
            _pollCts = null;
        }

        if (thread is not null)
        {
            if (hwnd != 0)
            {
                _ = User32.PostMessageW(hwnd, WmStop, 0, 0);
            }

            await Task.Run(() => thread.Join(ThreadJoinTimeout)).ConfigureAwait(false);
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
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private void MessageLoop(TaskCompletionSource<bool> ready)
    {
        string className = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ClubShell.SessionWatcher.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}");
        nint instance = Kernel32.GetModuleHandleW(null);
        nint classNamePtr = Marshal.StringToHGlobalUni(className);
        bool classRegistered = false;
        try
        {
            WNDCLASSEXW wc = WNDCLASSEXW.Create();
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc);
            wc.hInstance = instance;
            wc.lpszClassName = classNamePtr;
            if (User32.RegisterClassExW(ref wc) == 0)
            {
                int error = Win32Error.Last();
                if (error != NativeConst.ERROR_CLASS_ALREADY_EXISTS)
                {
                    ready.TrySetException(new Win32Exception(error, "RegisterClassExW failed: " + Win32Error.Message(error)));
                    return;
                }
            }

            classRegistered = true;
            nint hwnd = User32.CreateWindowExW(0, className, "ClubShell session watcher", 0, 0, 0, 0, 0, NativeConst.HWND_MESSAGE, 0, instance, 0);
            if (hwnd == 0)
            {
                int error = Win32Error.Last();
                ready.TrySetException(new Win32Exception(error, "CreateWindowExW failed: " + Win32Error.Message(error)));
                return;
            }

            if (!Wtsapi32.WTSRegisterSessionNotification(hwnd, NativeConst.NOTIFY_FOR_ALL_SESSIONS))
            {
                int error = Win32Error.Last();
                _ = User32.DestroyWindow(hwnd);
                ready.TrySetException(new Win32Exception(error, "WTSRegisterSessionNotification failed: " + Win32Error.Message(error)));
                return;
            }

            lock (_gate)
            {
                _hwnd = hwnd;
            }

            ready.TrySetResult(true);
            while (User32.GetMessageW(out MSG msg, 0, 0, 0) > 0)
            {
                _ = User32.TranslateMessage(ref msg);
                _ = User32.DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session watcher message loop failed");
            ready.TrySetException(ex);
        }
        finally
        {
            lock (_gate)
            {
                _hwnd = 0;
            }

            if (classRegistered)
            {
                _ = User32.UnregisterClassW(className, instance);
            }

            Marshal.FreeHGlobal(classNamePtr);
        }
    }

    private nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case NativeConst.WM_WTSSESSION_CHANGE:
                    if (MapReason(unchecked((uint)wParam)) is { } reason)
                    {
                        Raise(reason, unchecked((uint)lParam));
                    }

                    return 0;
                case WmStop:
                    _ = Wtsapi32.WTSUnRegisterSessionNotification(hWnd);
                    _ = User32.DestroyWindow(hWnd);
                    return 0;
                case NativeConst.WM_DESTROY:
                    User32.PostQuitMessage(0);
                    return 0;
                default:
                    return User32.DefWindowProcW(hWnd, msg, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            // Exceptions must never cross into the native message dispatcher.
            _logger.LogError(ex, "Session watcher window procedure failed for message 0x{Message:X}", msg);
            return 0;
        }
    }

    private void StartPolling()
    {
        IsPolling = true;
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _pollCts = cts;
            _pollTask = Task.Run(() => PollLoopAsync(cts.Token));
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        Dictionary<uint, WtsSession> known = Snapshot();
        using var timer = new PeriodicTimer(_pollInterval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            Dictionary<uint, WtsSession> current;
            try
            {
                current = Snapshot();
            }
            catch (Win32Exception ex)
            {
                _logger.LogDebug(ex, "WTS enumeration failed; retrying next tick");
                continue;
            }

            foreach ((uint id, WtsSession now) in current)
            {
                if (!known.TryGetValue(id, out WtsSession? before))
                {
                    Raise(SessionChangeReason.SessionCreate, id);
                    if (now.HasUser)
                    {
                        Raise(SessionChangeReason.Logon, id);
                    }

                    continue;
                }

                if (!before.HasUser && now.HasUser)
                {
                    Raise(SessionChangeReason.Logon, id);
                }
                else if (before.HasUser && !now.HasUser)
                {
                    Raise(SessionChangeReason.Logoff, id);
                }

                if (before.State != now.State)
                {
                    bool remote = !now.IsConsole;
                    if (now.State == WTS_CONNECTSTATE_CLASS.WTSActive)
                    {
                        Raise(remote ? SessionChangeReason.RemoteConnect : SessionChangeReason.ConsoleConnect, id);
                    }
                    else if (now.State == WTS_CONNECTSTATE_CLASS.WTSDisconnected)
                    {
                        Raise(remote ? SessionChangeReason.RemoteDisconnect : SessionChangeReason.ConsoleDisconnect, id);
                    }
                }
            }

            foreach ((uint id, WtsSession before) in known)
            {
                if (!current.ContainsKey(id))
                {
                    if (before.HasUser)
                    {
                        Raise(SessionChangeReason.Logoff, id);
                    }

                    Raise(SessionChangeReason.SessionRemove, id);
                }
            }

            known = current;
        }
    }

    private static Dictionary<uint, WtsSession> Snapshot()
    {
        var result = new Dictionary<uint, WtsSession>();
        foreach (WtsSession session in WtsSessions.Enumerate())
        {
            result[session.Id] = session;
        }

        return result;
    }

    private void Raise(SessionChangeReason reason, uint sessionId)
    {
        var args = new SessionChangedEventArgs(reason, sessionId, DateTimeOffset.UtcNow);
        _logger.LogDebug("Session {SessionId}: {Reason}", sessionId, reason);
        try
        {
            SessionChanged?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SessionChanged handler threw for session {SessionId} ({Reason})", sessionId, reason);
        }
    }
}

