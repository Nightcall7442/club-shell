#pragma warning disable CA1707 // identifiers should not contain underscores (Win32 names)
#pragma warning disable IDE1006 // naming styles (keybd_event)

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClubShell.Windows.Native;

/// <summary>
/// user32.dll P/Invokes. Every method sets last error; check the return value and call
/// <see cref="Win32Error.ThrowLastError"/> on failure. <c>ref char</c> buffer parameters take
/// <c>ref buffer[0]</c> of a <c>char[]</c> (or <c>ref MemoryMarshal.GetReference(span)</c>).
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class User32
{
    private const string Lib = "user32.dll";

    // ---- hooks --------------------------------------------------------------------------------

    /// <summary>
    /// Installs a hook. For WH_KEYBOARD_LL / WH_MOUSE_LL pass <c>hMod = Kernel32.GetModuleHandleW(null)</c> and <c>dwThreadId = 0</c>.
    /// <paramref name="lpfn"/> is <c>Marshal.GetFunctionPointerForDelegate</c> of a <see cref="HookProc"/> the caller keeps alive.
    /// </summary>
    [LibraryImport(Lib, SetLastError = true)]
    public static partial SafeHookHandle SetWindowsHookExW(int idHook, nint lpfn, nint hMod, uint dwThreadId);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    /// <summary>Installs an out-of-context WinEvent hook. <paramref name="pfnWinEventProc"/> is <c>Marshal.GetFunctionPointerForDelegate</c> of a <see cref="WinEventProc"/> the caller keeps alive.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    public static partial SafeWinEventHookHandle SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, nint pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hWinEventHook);

    // ---- message loop -------------------------------------------------------------------------

    /// <summary>Returns 0 on WM_QUIT, -1 on error, otherwise non-zero.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint DispatchMessageW(ref MSG lpMsg);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessageW(uint idThread, uint Msg, nint wParam, nint lParam);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial void PostQuitMessage(int nExitCode);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint SendMessageW(nint hWnd, uint Msg, nint wParam, nint lParam);

    /// <summary>SendMessageTimeoutW; <paramref name="fuFlags"/> e.g. SMTO_ABORTIFHUNG (0x0002).</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint SendMessageTimeoutW(nint hWnd, uint Msg, nint wParam, nint lParam, uint fuFlags, uint uTimeout, out nuint lpdwResult);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(nint hWnd, uint Msg, nint wParam, nint lParam);

    // ---- window lookup / enumeration ---------------------------------------------------------

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowW(string? lpClassName, string? lpWindowName);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowExW(nint hWndParent, nint hWndChildAfter, string? lpszClass, string? lpszWindow);

    /// <summary>Enumerates top-level windows; <paramref name="lpEnumFunc"/> is a function pointer to an <see cref="EnumWindowsProc"/>.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(nint lpEnumFunc, nint lParam);

    /// <summary>Enumerates top-level windows with a managed callback (kept alive for the duration of the call).</summary>
    public static bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam)
    {
        ArgumentNullException.ThrowIfNull(lpEnumFunc);
        bool ok = EnumWindows(Marshal.GetFunctionPointerForDelegate(lpEnumFunc), lParam);
        GC.KeepAlive(lpEnumFunc);
        return ok;
    }

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetDesktopWindow();

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetShellWindow();

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetForegroundWindow();

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint SetActiveWindow(nint hWnd);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BringWindowToTop(nint hWnd);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial void SwitchToThisWindow(nint hWnd, [MarshalAs(UnmanagedType.Bool)] bool fAltTab);

    /// <summary><paramref name="dwProcessId"/> may be ASFW_ANY (0xFFFFFFFF).</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(uint dwProcessId);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    /// <summary>Returns the thread id that created the window; <paramref name="lpdwProcessId"/> receives the process id.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetWindow(nint hWnd, uint uCmd);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetParent(nint hWnd);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetAncestor(nint hWnd, uint gaFlags);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hWnd);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hWnd);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsZoomed(nint hWnd);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsHungAppWindow(nint hWnd);

    /// <summary>Copies up to <paramref name="nMaxCount"/> - 1 chars plus terminator; returns the copied length.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetWindowTextW(nint hWnd, ref char lpString, int nMaxCount);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetWindowTextLengthW(nint hWnd);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetClassNameW(nint hWnd, ref char lpClassName, int nMaxCount);

    /// <summary>Window title (empty when the window has none or the call fails).</summary>
    public static string GetWindowText(nint hWnd)
    {
        int length = GetWindowTextLengthW(hWnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        char[] buffer = new char[length + 1];
        int copied = GetWindowTextW(hWnd, ref buffer[0], buffer.Length);
        return NativeString.FromBuffer(buffer, copied);
    }

    /// <summary>Window class name (empty on failure).</summary>
    public static string GetClassName(nint hWnd)
    {
        char[] buffer = new char[256];
        int copied = GetClassNameW(hWnd, ref buffer[0], buffer.Length);
        return NativeString.FromBuffer(buffer, copied);
    }

    // ---- window state / geometry -------------------------------------------------------------

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindowAsync(nint hWnd, int nCmdShow);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveWindow(nint hWnd, int X, int Y, int nWidth, int nHeight, [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hWnd, out RECT lpRect);

    /// <summary>Initialize with <see cref="WINDOWPLACEMENT.Create"/> before calling.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetWindowLongPtrW(nint hWnd, int nIndex);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnableWindow(nint hWnd, [MarshalAs(UnmanagedType.Bool)] bool bEnable);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hWnd);

    // ---- window classes / creation (message-only windows) ------------------------------------

    /// <summary>Registers a window class; returns the class atom or 0 (ERROR_CLASS_ALREADY_EXISTS when registered before). Set <c>lpfnWndProc</c> to a function pointer of a <see cref="WndProc"/> kept alive by the caller.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    public static partial ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterClassW(string lpClassName, nint hInstance);

    /// <summary>Creates a window; pass <c>hWndParent = HWND_MESSAGE</c> for a message-only window. Returns 0 on failure.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    // ---- input --------------------------------------------------------------------------------

    /// <summary>Initialize with <see cref="LASTINPUTINFO.Create"/>; idle ms = GetTickCount64() - dwTime (wraps every 49.7 days).</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>Blocks keyboard and mouse input (requires the calling thread's desktop to be the input desktop). Fails with ERROR_ACCESS_DENIED from session 0.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BlockInput([MarshalAs(UnmanagedType.Bool)] bool fBlockIt);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    /// <summary>Injects input; pass <c>ref inputs[0]</c>, <c>inputs.Length</c> and <see cref="INPUT.Size"/>. Returns the number of events inserted.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint SendInput(uint cInputs, ref INPUT pInputs, int cbSize);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial short GetKeyState(int nVirtKey);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial short GetAsyncKeyState(int vKey);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint MapVirtualKeyW(uint uCode, uint uMapType);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int X, int Y);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    /// <summary>Increments/decrements the cursor display counter; returns the new counter.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool bShow);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClipCursor(ref RECT lpRect);

    /// <summary>Pass <c>0</c> to release the cursor clip.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClipCursor(nint lpRect);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hWnd, int id);

    // ---- system parameters / metrics ---------------------------------------------------------

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfoW(uint uiAction, uint uiParam, nint pvParam, uint fWinIni);

    /// <summary>SPI_GETWORKAREA / SPI_SETWORKAREA overload.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfoW(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    /// <summary>Overload for BOOL/UINT-valued parameters (SPI_GETSCREENSAVEACTIVE, SPI_GETFOREGROUNDLOCKTIMEOUT, ...).</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfoW(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);

    /// <summary>Overload for string-valued parameters (SPI_SETDESKWALLPAPER).</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfoW(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetSystemMetrics(int nIndex);

    // ---- monitors / display -------------------------------------------------------------------

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetMonitorInfoW(nint hMonitor, MONITORINFOEXW* lpmi);

    /// <summary>Initialize with <see cref="MONITORINFOEXW.Create"/> before calling.</summary>
    public static unsafe bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEXW lpmi)
    {
        fixed (MONITORINFOEXW* p = &lpmi)
        {
            return GetMonitorInfoW(hMonitor, p);
        }
    }

    /// <summary>Enumerates monitors; pass <c>hdc = 0</c>, <c>lprcClip = 0</c> for all. <paramref name="lpfnEnum"/> is a function pointer to a <see cref="MonitorEnumProc"/>.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(nint hdc, nint lprcClip, nint lpfnEnum, nint dwData);

    /// <summary>Enumerates monitors with a managed callback (kept alive for the duration of the call).</summary>
    public static bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData)
    {
        ArgumentNullException.ThrowIfNull(lpfnEnum);
        bool ok = EnumDisplayMonitors(hdc, lprcClip, Marshal.GetFunctionPointerForDelegate(lpfnEnum), dwData);
        GC.KeepAlive(lpfnEnum);
        return ok;
    }

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumDisplaySettingsW(string? lpszDeviceName, uint iModeNum, DEVMODEW* lpDevMode);

    /// <summary>Initialize with <see cref="DEVMODEW.Create"/>; <paramref name="iModeNum"/> = ENUM_CURRENT_SETTINGS or an index.</summary>
    public static unsafe bool EnumDisplaySettingsW(string? lpszDeviceName, uint iModeNum, ref DEVMODEW lpDevMode)
    {
        fixed (DEVMODEW* p = &lpDevMode)
        {
            return EnumDisplaySettingsW(lpszDeviceName, iModeNum, p);
        }
    }

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int ChangeDisplaySettingsExW(string? lpszDeviceName, DEVMODEW* lpDevMode, nint hwnd, uint dwflags, nint lParam);

    /// <summary>Applies a display mode; returns DISP_CHANGE_*.</summary>
    public static unsafe int ChangeDisplaySettingsExW(string? lpszDeviceName, ref DEVMODEW lpDevMode, nint hwnd, uint dwflags, nint lParam)
    {
        fixed (DEVMODEW* p = &lpDevMode)
        {
            return ChangeDisplaySettingsExW(lpszDeviceName, p, hwnd, dwflags, lParam);
        }
    }

    /// <summary>Resets the device to the registry mode (NULL DEVMODE).</summary>
    public static unsafe int ChangeDisplaySettingsExW(string? lpszDeviceName, nint hwnd, uint dwflags) =>
        ChangeDisplaySettingsExW(lpszDeviceName, null, hwnd, dwflags, 0);

    // ---- desktops -----------------------------------------------------------------------------

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeDesktopHandle OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeDesktopHandle OpenDesktopW(string lpszDesktop, uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadDesktop(SafeDesktopHandle hDesktop);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetThreadDesktop(uint dwThreadId);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseDesktop(nint hDesktop);

    // ---- session / power ----------------------------------------------------------------------

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LockWorkStation();

    /// <summary>Requires SE_SHUTDOWN_NAME enabled on the process token for EWX_SHUTDOWN/EWX_REBOOT/EWX_POWEROFF.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ExitWindowsEx(uint uFlags, uint dwReason);

    /// <summary>From a service use MB_SERVICE_NOTIFICATION or <see cref="Wtsapi32.WTSSendMessageW"/> instead.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBoxW(nint hWnd, string lpText, string? lpCaption, uint uType);

    // ---- drawing / capture --------------------------------------------------------------------

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetDC(nint hWnd);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetWindowDC(nint hWnd);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int ReleaseDC(nint hWnd, nint hDC);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint hIcon);

    /// <summary><paramref name="dwAffinity"/> is WDA_NONE (0), WDA_MONITOR (1) or WDA_EXCLUDEFROMCAPTURE (0x11); the latter keeps the window out of screenshots and capture.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>Convenience: sets or clears WS_EX_TOPMOST without moving or resizing the window.</summary>
    public static bool SetTopmost(nint hWnd, bool topmost) =>
        SetWindowPos(hWnd, topmost ? NativeConst.HWND_TOPMOST : NativeConst.HWND_NOTOPMOST, 0, 0, 0, 0,
            NativeConst.SWP_NOMOVE | NativeConst.SWP_NOSIZE | NativeConst.SWP_NOACTIVATE);

    /// <summary>Convenience: idle time in milliseconds according to GetLastInputInfo (valid only in an interactive session).</summary>
    public static bool TryGetIdleMilliseconds(out ulong idleMs)
    {
        LASTINPUTINFO info = LASTINPUTINFO.Create();
        if (!GetLastInputInfo(ref info))
        {
            idleMs = 0;
            return false;
        }

        ulong now = Kernel32.GetTickCount64();
        ulong last = info.dwTime;
        // dwTime is a 32-bit tick count; align to the same epoch as the 64-bit clock before subtracting.
        ulong now32 = now & 0xFFFFFFFFUL;
        idleMs = now32 >= last ? now32 - last : (0x1_0000_0000UL - last) + now32;
        return true;
    }
}

/// <summary>gdi32.dll P/Invokes needed for screen capture (PrintWindow / BitBlt into a DIB).</summary>
[SupportedOSPlatform("windows")]
public static partial class Gdi32
{
    private const string Lib = "gdi32.dll";

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint ho);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, uint rop);

    /// <summary>Copies bitmap bits into <paramref name="lpvBits"/> (pass <c>ref pixels[0]</c> of a byte[] sized width*height*4 for 32bpp).</summary>
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int GetDIBits(nint hdc, nint hbm, uint start, uint cLines, ref byte lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int GetDeviceCaps(nint hdc, int index);
}
