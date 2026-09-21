using System.Runtime.Versioning;
using ClubShell.Windows.Native;

namespace ClubShell.Windows.Sessions;

/// <summary>A Terminal Services session on the local machine.</summary>
/// <param name="Id">Session id (0 = services).</param>
/// <param name="StationName">Window station name, e.g. <c>Console</c>, <c>RDP-Tcp#3</c>.</param>
/// <param name="State">Connection state.</param>
/// <param name="UserName">Logged-on user name, or <see langword="null"/> when nobody is logged on.</param>
/// <param name="Domain">Logon domain, or <see langword="null"/>.</param>
/// <param name="ClientName">RDP client machine name, or <see langword="null"/>.</param>
/// <param name="IsConsole"><see langword="true"/> for the physical console session.</param>
/// <param name="LogonTime">Logon time (UTC), when known.</param>
public sealed record WtsSession(
    uint Id,
    string StationName,
    WTS_CONNECTSTATE_CLASS State,
    string? UserName,
    string? Domain,
    string? ClientName,
    bool IsConsole,
    DateTimeOffset? LogonTime)
{
    /// <summary><c>DOMAIN\user</c> or <see langword="null"/>.</summary>
    public string? QualifiedUser => UserName is null ? null : string.IsNullOrEmpty(Domain) ? UserName : Domain + "\\" + UserName;

    /// <summary><see langword="true"/> when the session is active (user input possible).</summary>
    public bool IsActive => State == WTS_CONNECTSTATE_CLASS.WTSActive;

    /// <summary><see langword="true"/> when a user is logged on.</summary>
    public bool HasUser => !string.IsNullOrEmpty(UserName);
}

/// <summary>WTS session enumeration and control (WTSEnumerateSessionsW / WTSQuerySessionInformationW).</summary>
[SupportedOSPlatform("windows")]
public static class WtsSessions
{
    /// <summary>Every session, including session 0 and listeners.</summary>
    public static IReadOnlyList<WtsSession> Enumerate()
    {
        uint console = Kernel32.WTSGetActiveConsoleSessionId();
        WTS_SESSION_INFOW[] raw = Wtsapi32.EnumerateSessions();
        var result = new List<WtsSession>(raw.Length);
        foreach (WTS_SESSION_INFOW info in raw)
        {
            result.Add(Describe(info.SessionId, info.State, console));
        }

        return result;
    }

    /// <summary>One session by id, or <see langword="null"/> when it does not exist.</summary>
    public static WtsSession? Get(uint sessionId)
    {
        foreach (WTS_SESSION_INFOW info in Wtsapi32.EnumerateSessions())
        {
            if (info.SessionId == sessionId)
            {
                return Describe(info.SessionId, info.State, Kernel32.WTSGetActiveConsoleSessionId());
            }
        }

        return null;
    }

    /// <summary>Session id of the physical console, or 0xFFFFFFFF when no session is attached.</summary>
    public static uint GetActiveConsoleSessionId() => Kernel32.WTSGetActiveConsoleSessionId();

    /// <summary>First session whose user matches <paramref name="userName"/> (<c>name</c> or <c>DOMAIN\name</c>, case-insensitive), preferring active ones.</summary>
    public static WtsSession? FindByUser(string userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        int slash = userName.IndexOf('\\');
        string? domain = slash >= 0 ? userName[..slash] : null;
        string name = slash >= 0 ? userName[(slash + 1)..] : userName;
        WtsSession? match = null;
        foreach (WtsSession session in Enumerate())
        {
            if (!string.Equals(session.UserName, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (domain is not null && !string.Equals(session.Domain, domain, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (session.IsActive)
            {
                return session;
            }

            match ??= session;
        }

        return match;
    }

    /// <summary>Logs the session off (WTSLogoffSession); throws on failure.</summary>
    public static void Logoff(uint sessionId, bool wait) =>
        Win32Error.ThrowIfFalse(Wtsapi32.WTSLogoffSession(NativeConst.WTS_CURRENT_SERVER_HANDLE, sessionId, wait), nameof(Wtsapi32.WTSLogoffSession));

    /// <summary>Disconnects the session without logging off; throws on failure.</summary>
    public static void Disconnect(uint sessionId, bool wait) =>
        Win32Error.ThrowIfFalse(Wtsapi32.WTSDisconnectSession(NativeConst.WTS_CURRENT_SERVER_HANDLE, sessionId, wait), nameof(Wtsapi32.WTSDisconnectSession));

    /// <summary>
    /// Shows a message box inside the session (works from session 0). Returns the ID* button pressed, IDTIMEOUT
    /// after <paramref name="timeout"/> (zero = never) or IDASYNC when <paramref name="wait"/> is <see langword="false"/>.
    /// </summary>
    public static uint SendMessage(uint sessionId, string title, string text, TimeSpan timeout, bool wait = false, uint style = NativeConst.MB_OK | NativeConst.MB_TOPMOST | NativeConst.MB_SETFOREGROUND)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(text);
        uint seconds = timeout <= TimeSpan.Zero ? NativeConst.WTS_MESSAGE_TIMEOUT_NONE : (uint)Math.Ceiling(timeout.TotalSeconds);
        return Wtsapi32.SendMessage(sessionId, title, text, style, seconds, wait);
    }

    private static WtsSession Describe(uint id, WTS_CONNECTSTATE_CLASS state, uint consoleId)
    {
        // WTS_SESSION_INFOW.pWinStationName points into the buffer EnumerateSessions() already freed; query it instead.
        string station = Wtsapi32.QuerySessionString(id, WTS_INFO_CLASS.WTSWinStationName) ?? string.Empty;
        string? user = NullIfEmpty(Wtsapi32.QuerySessionString(id, WTS_INFO_CLASS.WTSUserName));
        string? domain = user is null ? null : NullIfEmpty(Wtsapi32.QuerySessionString(id, WTS_INFO_CLASS.WTSDomainName));
        string? client = NullIfEmpty(Wtsapi32.QuerySessionString(id, WTS_INFO_CLASS.WTSClientName));
        DateTimeOffset? logon = null;
        if (user is not null)
        {
            WTSINFOW? info = Wtsapi32.QuerySessionStruct<WTSINFOW>(id, WTS_INFO_CLASS.WTSSessionInfo);
            if (info is { LogonTime: > 0 } value)
            {
                logon = DateTimeOffset.FromFileTime(value.LogonTime);
            }
        }

        bool isConsole = id == consoleId || string.Equals(station, "Console", StringComparison.OrdinalIgnoreCase);
        return new WtsSession(id, station, state, user, domain, client, isConsole, logon);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
