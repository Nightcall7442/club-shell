using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using ClubShell.Windows.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Windows.Storage;

/// <summary>
/// Maps and unmaps SMB shares to drive letters through <see cref="Mpr.WNetAddConnection2W"/> /
/// <see cref="Mpr.WNetCancelConnection2W"/>. Mappings are never persisted (<c>CONNECT_TEMPORARY</c>). Drive letters belong to a logon
/// session: a mapping made by the Agent (LocalSystem, session 0) is invisible to the kiosk user, so the
/// <c>ForSession</c> variants impersonate the session's user token (<c>WTSQueryUserToken</c>) and map inside that
/// logon session, which is what the games share needs (ARCHITECTURE.md §6.1 step 8).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NetworkShare
{
    private readonly ILogger<NetworkShare> _logger;

    /// <summary>Creates the mapper.</summary>
    public NetworkShare(ILogger<NetworkShare>? logger = null)
    {
        _logger = logger ?? NullLogger<NetworkShare>.Instance;
    }

    /// <summary>
    /// Maps <paramref name="uncPath"/> to <paramref name="driveLetter"/> in the caller's logon session. An existing
    /// mapping of the letter or a conflicting credential set for the server is dropped and the mapping retried once.
    /// </summary>
    /// <exception cref="Win32Exception">The connection failed (ERROR_BAD_NET_NAME, ERROR_LOGON_FAILURE, ERROR_ACCESS_DENIED, ...).</exception>
    public void Map(char driveLetter, string uncPath, string? username, string? password)
    {
        string local = LocalName(driveLetter);
        string remote = NormalizeUnc(uncPath);
        int error = AddConnection(local, remote, username, password);
        if (error == NativeConst.ERROR_ALREADY_ASSIGNED)
        {
            if (IsMapped(driveLetter, out string? target) && target is not null && target.Contains(remote.TrimStart('\\'), StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("{Drive} already mapped to {Unc}", local, remote);
                return;
            }

            _ = CancelConnection(local, force: true);
            error = AddConnection(local, remote, username, password);
        }
        else if (error == NativeConst.ERROR_SESSION_CREDENTIAL_CONFLICT)
        {
            _ = CancelConnection(remote, force: true);
            error = AddConnection(local, remote, username, password);
        }

        Win32Error.ThrowIfError(error, "WNetAddConnection2W");
        _logger.LogInformation("{Drive} mapped to {Unc}", local, remote);
    }

    /// <summary>Removes the mapping of <paramref name="driveLetter"/>; <see langword="false"/> when it was not mapped.</summary>
    /// <exception cref="Win32Exception">The disconnect failed for another reason (e.g. open files without <paramref name="force"/>).</exception>
    public bool Unmap(char driveLetter, bool force = true)
    {
        int error = CancelConnection(LocalName(driveLetter), force);
        if (error == NativeConst.ERROR_NOT_CONNECTED)
        {
            return false;
        }

        Win32Error.ThrowIfError(error, "WNetCancelConnection2W");
        _logger.LogInformation("{Drive} unmapped", LocalName(driveLetter));
        return true;
    }

    /// <summary>
    /// <see langword="true"/> when the letter is a network mapping in the caller's logon session;
    /// <paramref name="target"/> receives the NT device path (contains the UNC path) or <see langword="null"/>.
    /// </summary>
    public static bool IsMapped(char driveLetter, out string? target)
    {
        target = Kernel32.QueryDosDevice(LocalName(driveLetter));
        return target is not null
            && (target.Contains("LanmanRedirector", StringComparison.OrdinalIgnoreCase) || target.Contains("\\Mup\\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Maps the share inside the logon session of the user logged on to <paramref name="sessionId"/> (requires LocalSystem).</summary>
    /// <exception cref="Win32Exception">The session has no user token or the mapping failed.</exception>
    public void MapForSession(uint sessionId, char driveLetter, string uncPath, string? username, string? password) =>
        RunAsSessionUser(sessionId, () =>
        {
            Map(driveLetter, uncPath, username, password);
            return true;
        });

    /// <summary>Removes a mapping inside the user's logon session; <see langword="false"/> when it was not mapped.</summary>
    /// <exception cref="Win32Exception">The session has no user token or the disconnect failed.</exception>
    public bool UnmapForSession(uint sessionId, char driveLetter, bool force = true) =>
        RunAsSessionUser(sessionId, () => Unmap(driveLetter, force));

    /// <summary>Like <see cref="IsMapped"/>, evaluated inside the user's logon session.</summary>
    /// <exception cref="Win32Exception">The session has no user token.</exception>
    public static bool IsMappedForSession(uint sessionId, char driveLetter, out string? target)
    {
        (bool mapped, string? found) = RunAsSessionUser(sessionId, () =>
        {
            bool result = IsMapped(driveLetter, out string? path);
            return (result, path);
        });
        target = found;
        return mapped;
    }

    /// <summary>Attempts to enumerate <paramref name="path"/> (drive root or UNC); <see langword="false"/> on any access or I/O failure.</summary>
    public static bool TestReadAccess(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            if (!Directory.Exists(path))
            {
                return false;
            }

            using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            _ = entries.MoveNext();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---- internals ----------------------------------------------------------------------------

    private static string LocalName(char driveLetter)
    {
        char upper = char.ToUpperInvariant(driveLetter);
        if (upper is < 'A' or > 'Z')
        {
            throw new ArgumentOutOfRangeException(nameof(driveLetter), driveLetter, "Drive letter must be A-Z");
        }

        return upper + ":";
    }

    private static string NormalizeUnc(string uncPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uncPath);
        string normalized = uncPath.Trim().Replace('/', '\\').TrimEnd('\\');
        if (!normalized.StartsWith("\\\\", StringComparison.Ordinal) || normalized.Length < 5)
        {
            throw new ArgumentException($"'{uncPath}' is not a UNC path", nameof(uncPath));
        }

        return normalized;
    }

    private static int AddConnection(string localName, string remoteName, string? username, string? password)
    {
        var resource = new NETRESOURCEW
        {
            dwType = NativeConst.RESOURCETYPE_DISK,
            lpLocalName = Marshal.StringToHGlobalUni(localName),
            lpRemoteName = Marshal.StringToHGlobalUni(remoteName),
        };
        try
        {
            return Mpr.WNetAddConnection2W(ref resource, string.IsNullOrEmpty(password) ? null : password, string.IsNullOrEmpty(username) ? null : username, NativeConst.CONNECT_TEMPORARY);
        }
        finally
        {
            Marshal.FreeHGlobal(resource.lpLocalName);
            Marshal.FreeHGlobal(resource.lpRemoteName);
        }
    }

    private static int CancelConnection(string name, bool force) => Mpr.WNetCancelConnection2W(name, 0, force);

    /// <summary>Runs <paramref name="action"/> while impersonating the user of <paramref name="sessionId"/>; WNet and DosDevices then resolve in that logon session.</summary>
    private static T RunAsSessionUser<T>(uint sessionId, Func<T> action)
    {
        if (!Wtsapi32.WTSQueryUserToken(sessionId, out SafeTokenHandle token))
        {
            Win32Error.ThrowLastError(nameof(Wtsapi32.WTSQueryUserToken));
        }

        using (token)
        {
            // WindowsIdentity duplicates the handle, so the WTS token can be released right after.
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            return WindowsIdentity.RunImpersonated(identity.AccessToken, action);
        }
    }
}
