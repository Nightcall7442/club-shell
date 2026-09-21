#pragma warning disable CA1707 // identifiers should not contain underscores (Win32 names)

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClubShell.Windows.Native;

/// <summary>
/// wtsapi32.dll P/Invokes for Terminal Services sessions. <c>hServer</c> is WTS_CURRENT_SERVER_HANDLE (0)
/// for the local machine. Buffers come back as <see cref="SafeWtsMemoryHandle"/> (freed with WTSFreeMemory).
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class Wtsapi32
{
    private const string Lib = "wtsapi32.dll";

    // ---- sessions -----------------------------------------------------------------------------

    /// <summary><paramref name="Reserved"/> = 0, <paramref name="Version"/> = 1. Read entries with <see cref="SafeWtsMemoryHandle.ReadArray{T}"/>.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSEnumerateSessionsW(nint hServer, uint Reserved, uint Version, out SafeWtsMemoryHandle ppSessionInfo, out uint pCount);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial void WTSFreeMemory(nint pMemory);

    /// <summary><paramref name="pBytesReturned"/> includes the terminator for string classes.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSQuerySessionInformationW(nint hServer, uint SessionId, WTS_INFO_CLASS WTSInfoClass, out SafeWtsMemoryHandle ppBuffer, out uint pBytesReturned);

    /// <summary>Primary token of the user logged on to <paramref name="SessionId"/>. Requires SE_TCB_NAME (LocalSystem). Fails with ERROR_NO_TOKEN when nobody is logged on.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSQueryUserToken(uint SessionId, out SafeTokenHandle phToken);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSLogoffSession(nint hServer, uint SessionId, [MarshalAs(UnmanagedType.Bool)] bool bWait);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSDisconnectSession(nint hServer, uint SessionId, [MarshalAs(UnmanagedType.Bool)] bool bWait);

    /// <summary>Shows a message box in a session (works from session 0). Lengths are in BYTES (chars * 2). <paramref name="Style"/> is MB_*; <paramref name="pResponse"/> is ID* or IDTIMEOUT / IDASYNC.</summary>
    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSSendMessageW(nint hServer, uint SessionId, string pTitle, uint TitleLength, string pMessage, uint MessageLength, uint Style, uint Timeout, out uint pResponse, [MarshalAs(UnmanagedType.Bool)] bool bWait);

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint WTSOpenServerW(string pServerName);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial void WTSCloseServer(nint hServer);

    // ---- notifications ------------------------------------------------------------------------

    /// <summary>Window-based notifications (WM_WTSSESSION_CHANGE); <paramref name="dwFlags"/> = NOTIFY_FOR_ALL_SESSIONS / NOTIFY_FOR_THIS_SESSION. Services get the same via SERVICE_CONTROL_SESSIONCHANGE.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSRegisterSessionNotification(nint hWnd, uint dwFlags);

    [LibraryImport(Lib, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSUnRegisterSessionNotification(nint hWnd);

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>Alias of <see cref="Kernel32.WTSGetActiveConsoleSessionId"/> (0xFFFFFFFF when no console session).</summary>
    public static uint WTSGetActiveConsoleSessionId() => Kernel32.WTSGetActiveConsoleSessionId();

    /// <summary>All sessions on the local server; throws on failure.</summary>
    public static WTS_SESSION_INFOW[] EnumerateSessions(nint hServer = 0)
    {
        if (!WTSEnumerateSessionsW(hServer, 0, 1, out SafeWtsMemoryHandle buffer, out uint count))
        {
            Win32Error.ThrowLastError(nameof(WTSEnumerateSessionsW));
        }

        using (buffer)
        {
            return buffer.ReadArray<WTS_SESSION_INFOW>((int)count);
        }
    }

    /// <summary>String-valued session information (WTSUserName, WTSDomainName, WTSWinStationName, WTSClientName, ...), or <see langword="null"/> on failure.</summary>
    public static string? QuerySessionString(uint sessionId, WTS_INFO_CLASS infoClass, nint hServer = 0)
    {
        if (!WTSQuerySessionInformationW(hServer, sessionId, infoClass, out SafeWtsMemoryHandle buffer, out _))
        {
            return null;
        }

        using (buffer)
        {
            return buffer.ReadString();
        }
    }

    /// <summary>Struct-valued session information (e.g. <see cref="WTS_INFO_CLASS.WTSConnectState"/> as <see cref="WTS_CONNECTSTATE_CLASS"/>), or <see langword="null"/> on failure or size mismatch.</summary>
    public static T? QuerySessionStruct<T>(uint sessionId, WTS_INFO_CLASS infoClass, nint hServer = 0) where T : unmanaged
    {
        if (!WTSQuerySessionInformationW(hServer, sessionId, infoClass, out SafeWtsMemoryHandle buffer, out uint bytes))
        {
            return null;
        }

        using (buffer)
        {
            return bytes < NativeString.SizeOf<T>() ? null : buffer.ReadStruct<T>();
        }
    }

    /// <summary>"DOMAIN\user" of the session's logged-on user, or <see langword="null"/> when nobody is logged on.</summary>
    public static string? GetSessionUser(uint sessionId)
    {
        string? user = QuerySessionString(sessionId, WTS_INFO_CLASS.WTSUserName);
        if (string.IsNullOrEmpty(user))
        {
            return null;
        }

        string? domain = QuerySessionString(sessionId, WTS_INFO_CLASS.WTSDomainName);
        return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
    }

    /// <summary>Shows a message box in a session and returns the button pressed (IDTIMEOUT on timeout); throws on failure.</summary>
    public static uint SendMessage(uint sessionId, string title, string message, uint style, uint timeoutSec, bool wait)
    {
        if (!WTSSendMessageW(0, sessionId, title, (uint)(title.Length * sizeof(char)), message, (uint)(message.Length * sizeof(char)), style, timeoutSec, out uint response, wait))
        {
            Win32Error.ThrowLastError(nameof(WTSSendMessageW));
        }

        return response;
    }
}
