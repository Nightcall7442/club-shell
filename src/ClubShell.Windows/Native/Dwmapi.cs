#pragma warning disable CA1707 // identifiers should not contain underscores (Win32 names)

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClubShell.Windows.Native;

/// <summary>dwmapi.dll P/Invokes. All functions return an HRESULT (check with <see cref="HResult.Failed"/>).</summary>
[SupportedOSPlatform("windows")]
public static partial class Dwmapi
{
    private const string Lib = "dwmapi.dll";

    /// <summary>Generic form; <paramref name="pvAttribute"/> points at <paramref name="cbAttribute"/> bytes.</summary>
    [LibraryImport(Lib)]
    internal static partial int DwmSetWindowAttribute(nint hwnd, DWMWINDOWATTRIBUTE dwAttribute, nint pvAttribute, uint cbAttribute);

    /// <summary>DWORD/BOOL-valued attributes (DWMWA_CLOAK, DWMWA_USE_IMMERSIVE_DARK_MODE, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWA_NCRENDERING_POLICY, colors).</summary>
    [LibraryImport(Lib)]
    public static partial int DwmSetWindowAttribute(nint hwnd, DWMWINDOWATTRIBUTE dwAttribute, ref int pvAttribute, uint cbAttribute);

    /// <summary>Generic form; <paramref name="pvAttribute"/> points at <paramref name="cbAttribute"/> bytes.</summary>
    [LibraryImport(Lib)]
    internal static partial int DwmGetWindowAttribute(nint hwnd, DWMWINDOWATTRIBUTE dwAttribute, nint pvAttribute, uint cbAttribute);

    /// <summary>DWORD/BOOL-valued attributes (DWMWA_CLOAKED, DWMWA_NCRENDERING_ENABLED).</summary>
    [LibraryImport(Lib)]
    public static partial int DwmGetWindowAttribute(nint hwnd, DWMWINDOWATTRIBUTE dwAttribute, out int pvAttribute, uint cbAttribute);

    /// <summary>RECT-valued attributes (DWMWA_EXTENDED_FRAME_BOUNDS, DWMWA_CAPTION_BUTTON_BOUNDS).</summary>
    [LibraryImport(Lib)]
    public static partial int DwmGetWindowAttribute(nint hwnd, DWMWINDOWATTRIBUTE dwAttribute, out RECT pvAttribute, uint cbAttribute);

    [LibraryImport(Lib)]
    public static partial int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool pfEnabled);

    [LibraryImport(Lib)]
    internal static partial int DwmFlush();

    /// <summary>Sets a DWORD/BOOL attribute; returns the HRESULT.</summary>
    public static int SetAttribute(nint hwnd, DWMWINDOWATTRIBUTE attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    /// <summary>Reads a DWORD/BOOL attribute; returns 0 when the call fails.</summary>
    public static int GetAttribute(nint hwnd, DWMWINDOWATTRIBUTE attribute) =>
        DwmGetWindowAttribute(hwnd, attribute, out int value, sizeof(int)) >= 0 ? value : 0;

    /// <summary>Visible frame rectangle of a window (DWMWA_EXTENDED_FRAME_BOUNDS), falling back to GetWindowRect.</summary>
    public static RECT GetExtendedFrameBounds(nint hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, out RECT bounds, (uint)NativeString.SizeOf<RECT>()) >= 0)
        {
            return bounds;
        }

        _ = User32.GetWindowRect(hwnd, out RECT rect);
        return rect;
    }

    /// <summary>True when the window is cloaked by the app, the shell or inheritance (DWMWA_CLOAKED) — such windows are invisible even if IsWindowVisible says otherwise.</summary>
    public static bool IsCloaked(nint hwnd) => GetAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED) != 0;

    /// <summary>True when desktop composition is on (always true on Windows 8+; kept for diagnostics).</summary>
    public static bool IsCompositionEnabled() => DwmIsCompositionEnabled(out bool enabled) >= 0 && enabled;
}
