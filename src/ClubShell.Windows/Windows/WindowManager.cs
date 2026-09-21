using System.Diagnostics;
using System.Runtime.Versioning;

using ClubShell.Windows.Native;

namespace ClubShell.Windows.Windows;

/// <summary>A snapshot of a top-level window.</summary>
/// <param name="Hwnd">Window handle.</param>
/// <param name="Pid">Owning process id.</param>
/// <param name="Title">Window title (may be empty).</param>
/// <param name="ClassName">Window class name.</param>
/// <param name="Visible">Whether the window is visible.</param>
/// <param name="Bounds">Window rectangle in screen coordinates.</param>
public sealed record WindowInfo(nint Hwnd, uint Pid, string Title, string ClassName, bool Visible, RECT Bounds);

/// <summary>
/// Top-level window operations for kiosk control: finding windows, enumerating them, forcing a window to
/// full-screen borderless, minimizing everything else, stealing foreground reliably, closing gracefully,
/// excluding a window from screen capture and checking whether a window is responding.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowManager
{
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private const uint AsfwAny = 0xFFFFFFFF;

    /// <summary>First top-level window of the given class, or 0.</summary>
    public static nint FindByClass(string className) => User32.FindWindowW(className, null);

    /// <summary>First top-level window with the exact title, or 0.</summary>
    public static nint FindByTitle(string title) => User32.FindWindowW(null, title);

    /// <summary>First visible top-level window owned by <paramref name="pid"/>, or 0.</summary>
    public static nint FindByPid(uint pid)
    {
        nint found = 0;
        bool Callback(nint hWnd, nint lParam)
        {
            _ = User32.GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid == pid && User32.IsWindowVisible(hWnd))
            {
                found = hWnd;
                return false; // stop enumeration
            }

            return true;
        }

        _ = User32.EnumWindows(Callback, 0);
        return found;
    }

    /// <summary>All top-level windows.</summary>
    public static IReadOnlyList<WindowInfo> EnumerateTopLevel()
    {
        List<WindowInfo> windows = [];
        bool Callback(nint hWnd, nint lParam)
        {
            _ = User32.GetWindowThreadProcessId(hWnd, out uint pid);
            _ = User32.GetWindowRect(hWnd, out RECT rect);
            windows.Add(new WindowInfo(
                hWnd,
                pid,
                User32.GetWindowText(hWnd),
                User32.GetClassName(hWnd),
                User32.IsWindowVisible(hWnd),
                rect));
            return true;
        }

        _ = User32.EnumWindows(Callback, 0);
        return windows;
    }

    /// <summary>
    /// Strips the caption/border styles from <paramref name="hwnd"/> and resizes it to fill a monitor
    /// (the one it is on, or <paramref name="monitor"/> when non-zero). Returns <see langword="false"/> when
    /// the monitor cannot be resolved.
    /// </summary>
    public static bool SetFullscreenBorderless(nint hwnd, nint monitor = 0)
    {
        if (hwnd == 0 || !User32.IsWindow(hwnd))
        {
            return false;
        }

        nint target = monitor != 0 ? monitor : User32.MonitorFromWindow(hwnd, NativeConst.MONITOR_DEFAULTTOPRIMARY);
        MONITORINFOEXW info = MONITORINFOEXW.Create();
        if (!User32.GetMonitorInfoW(target, ref info))
        {
            return false;
        }

        RECT bounds = info.rcMonitor;

        const uint stripStyle = NativeConst.WS_CAPTION | NativeConst.WS_THICKFRAME | NativeConst.WS_MINIMIZEBOX
            | NativeConst.WS_MAXIMIZEBOX | NativeConst.WS_SYSMENU | NativeConst.WS_BORDER | NativeConst.WS_DLGFRAME;
        const uint stripExStyle = NativeConst.WS_EX_DLGMODALFRAME | NativeConst.WS_EX_WINDOWEDGE | NativeConst.WS_EX_CLIENTEDGE;

        uint style = (uint)User32.GetWindowLongPtrW(hwnd, NativeConst.GWL_STYLE);
        style = (style & ~stripStyle) | NativeConst.WS_POPUP | NativeConst.WS_VISIBLE;
        _ = User32.SetWindowLongPtrW(hwnd, NativeConst.GWL_STYLE, (nint)style);

        uint exStyle = (uint)User32.GetWindowLongPtrW(hwnd, NativeConst.GWL_EXSTYLE);
        exStyle &= ~stripExStyle;
        _ = User32.SetWindowLongPtrW(hwnd, NativeConst.GWL_EXSTYLE, (nint)exStyle);

        return User32.SetWindowPos(
            hwnd,
            NativeConst.HWND_TOP,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            NativeConst.SWP_FRAMECHANGED | NativeConst.SWP_SHOWWINDOW | NativeConst.SWP_NOOWNERZORDER);
    }

    /// <summary>Minimizes every visible top-level window except <paramref name="except"/> (and the shell/desktop).</summary>
    public static void MinimizeAllExcept(nint except)
    {
        nint shell = User32.GetShellWindow();
        nint desktop = User32.GetDesktopWindow();

        bool Callback(nint hWnd, nint lParam)
        {
            if (hWnd != except && hWnd != shell && hWnd != desktop
                && User32.IsWindowVisible(hWnd) && !User32.IsIconic(hWnd))
            {
                _ = User32.ShowWindowAsync(hWnd, NativeConst.SW_MINIMIZE);
            }

            return true;
        }

        _ = User32.EnumWindows(Callback, 0);
    }

    /// <summary>
    /// Brings <paramref name="hwnd"/> to the foreground reliably, working around the foreground-lock rules by
    /// attaching the input queues of the current and foreground threads.
    /// </summary>
    public static bool ForceForeground(nint hwnd)
    {
        if (hwnd == 0 || !User32.IsWindow(hwnd))
        {
            return false;
        }

        if (User32.IsIconic(hwnd))
        {
            _ = User32.ShowWindow(hwnd, NativeConst.SW_RESTORE);
        }

        nint foreground = User32.GetForegroundWindow();
        uint targetThread = User32.GetWindowThreadProcessId(hwnd, out _);
        uint foregroundThread = foreground == 0 ? 0 : User32.GetWindowThreadProcessId(foreground, out _);
        uint currentThread = Kernel32.GetCurrentThreadId();

        bool attachedForeground = false;
        bool attachedTarget = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attachedForeground = User32.AttachThreadInput(currentThread, foregroundThread, true);
            }

            if (targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread)
            {
                attachedTarget = User32.AttachThreadInput(currentThread, targetThread, true);
            }

            _ = User32.AllowSetForegroundWindow(AsfwAny);
            _ = User32.BringWindowToTop(hwnd);
            bool ok = User32.SetForegroundWindow(hwnd);
            _ = User32.SetActiveWindow(hwnd);
            return ok;
        }
        finally
        {
            if (attachedTarget)
            {
                _ = User32.AttachThreadInput(currentThread, targetThread, false);
            }

            if (attachedForeground)
            {
                _ = User32.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    /// <summary>
    /// Requests a graceful close (WM_CLOSE) and waits up to <paramref name="timeout"/> for the window to go
    /// away. Returns <see langword="true"/> once the window no longer exists.
    /// </summary>
    public static bool Close(nint hwnd, TimeSpan timeout)
    {
        if (hwnd == 0 || !User32.IsWindow(hwnd))
        {
            return true;
        }

        _ = User32.PostMessageW(hwnd, NativeConst.WM_CLOSE, 0, 0);

        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (!User32.IsWindow(hwnd))
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return !User32.IsWindow(hwnd);
    }

    /// <summary>Excludes (or restores) <paramref name="hwnd"/> from screen capture via WDA_EXCLUDEFROMCAPTURE.</summary>
    public static bool SetDisplayAffinity(nint hwnd, bool excludeFromCapture) =>
        User32.SetWindowDisplayAffinity(hwnd, excludeFromCapture ? WdaExcludeFromCapture : WdaNone);

    /// <summary>
    /// Whether <paramref name="hwnd"/> is processing messages, probed with a WM_NULL <c>SendMessageTimeout</c>
    /// that aborts if the window is hung.
    /// </summary>
    public static bool IsResponding(nint hwnd, TimeSpan timeout)
    {
        if (hwnd == 0 || !User32.IsWindow(hwnd))
        {
            return false;
        }

        uint ms = (uint)Math.Clamp(timeout.TotalMilliseconds, 1, uint.MaxValue);
        nint result = User32.SendMessageTimeoutW(hwnd, NativeConst.WM_NULL, 0, 0, SmtoAbortIfHung, ms, out _);
        return result != 0;
    }
}
