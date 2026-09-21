#pragma warning disable CA1707 // identifiers should not contain underscores (Win32 names)

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClubShell.Windows.Native;

/// <summary>shell32.dll P/Invokes: taskbar (app bar), ShellExecuteEx, known folders, shell notifications, tray icon, icons, recycle bin.</summary>
[SupportedOSPlatform("windows")]
public static partial class Shell32
{
    private const string Lib = "shell32.dll";

    // ---- taskbar / app bars -------------------------------------------------------------------

    /// <summary>Initialize with <see cref="APPBARDATA.Create"/>. ABM_GETSTATE returns ABS_* flags; ABM_SETSTATE sets them via <c>lParam</c>.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    public static partial nuint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    /// <summary>Convenience: sets or clears taskbar auto-hide (ABM_SETSTATE with ABS_AUTOHIDE).</summary>
    public static void SetTaskbarAutoHide(bool autoHide)
    {
        APPBARDATA data = APPBARDATA.Create(User32.FindWindowW("Shell_TrayWnd", null));
        data.lParam = autoHide ? (nint)NativeConst.ABS_AUTOHIDE : 0;
        _ = SHAppBarMessage(NativeConst.ABM_SETSTATE, ref data);
    }

    // ---- ShellExecuteEx -----------------------------------------------------------------------

    /// <summary>Raw form; string members of <paramref name="pExecInfo"/> are unmanaged pointers. Prefer <see cref="ShellExecute"/>.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShellExecuteExW(ref SHELLEXECUTEINFOW pExecInfo);

    /// <summary>
    /// ShellExecuteExW with managed strings and SEE_MASK_NOCLOSEPROCESS | SEE_MASK_FLAG_NO_UI | SEE_MASK_NOASYNC.
    /// Returns the process handle (may be 0 for shell-handled verbs; close with CloseHandle) or throws.
    /// </summary>
    public static nint ShellExecute(string file, string? parameters, string? directory, string? verb, int showCmd, uint extraMask = 0)
    {
        SHELLEXECUTEINFOW info = SHELLEXECUTEINFOW.Create();
        info.fMask = NativeConst.SEE_MASK_NOCLOSEPROCESS | NativeConst.SEE_MASK_FLAG_NO_UI | NativeConst.SEE_MASK_NOASYNC | extraMask;
        info.nShow = showCmd;
        info.lpFile = Marshal.StringToHGlobalUni(file);
        info.lpParameters = parameters is null ? 0 : Marshal.StringToHGlobalUni(parameters);
        info.lpDirectory = directory is null ? 0 : Marshal.StringToHGlobalUni(directory);
        info.lpVerb = verb is null ? 0 : Marshal.StringToHGlobalUni(verb);
        try
        {
            if (!ShellExecuteExW(ref info))
            {
                Win32Error.ThrowLastError(nameof(ShellExecuteExW));
            }

            return info.hProcess;
        }
        finally
        {
            Marshal.FreeHGlobal(info.lpFile);
            Marshal.FreeHGlobal(info.lpParameters);
            Marshal.FreeHGlobal(info.lpDirectory);
            Marshal.FreeHGlobal(info.lpVerb);
        }
    }

    // ---- known folders ------------------------------------------------------------------------

    /// <summary>Returns an HRESULT; <paramref name="ppszPath"/> must be freed with <see cref="Marshal.FreeCoTaskMem"/>. <paramref name="hToken"/> = 0 for the calling user, -1 for the default user, or a user token to resolve another user's folders.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);

    /// <summary>Path of a known folder (see <see cref="KnownFolders"/>) or <see langword="null"/> when it cannot be resolved.</summary>
    public static string? GetKnownFolderPath(Guid folderId, uint flags = NativeConst.KF_FLAG_DEFAULT, nint hToken = 0)
    {
        int hr = SHGetKnownFolderPath(in folderId, flags, hToken, out nint path);
        if (hr < 0 || path == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    // ---- notifications ------------------------------------------------------------------------

    [LibraryImport(Lib, SetLastError = true)]
    public static partial void SHChangeNotify(int wEventId, uint uFlags, nint dwItem1, nint dwItem2);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool Shell_NotifyIconW(uint dwMessage, NOTIFYICONDATAW* lpData);

    /// <summary>Initialize with <see cref="NOTIFYICONDATAW.Create"/>; <paramref name="dwMessage"/> is NIM_*.</summary>
    public static unsafe bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData)
    {
        fixed (NOTIFYICONDATAW* p = &lpData)
        {
            return Shell_NotifyIconW(dwMessage, p);
        }
    }

    // ---- icons --------------------------------------------------------------------------------

    /// <summary>With <paramref name="nIcons"/> = 1 extracts one large and one small icon (destroy with <see cref="User32.DestroyIcon"/>). Returns the number of icons extracted, or the icon count when <paramref name="nIconIndex"/> = -1.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint ExtractIconExW(string lpszFile, int nIconIndex, out nint phiconLarge, out nint phiconSmall, uint nIcons);

    // ---- recycle bin --------------------------------------------------------------------------

    /// <summary>Returns an HRESULT; <paramref name="pszRootPath"/> = <see langword="null"/> empties all drives.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHEmptyRecycleBinW(nint hwnd, string? pszRootPath, uint dwFlags);
}
