using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using ClubShell.Core.Configuration;
using ClubShell.Windows.Native;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;

namespace ClubShell.Agent.Ipc;

/// <summary>How strictly a connecting kiosk-user process is checked before it may talk to the Agent.</summary>
public enum ClientValidationMode
{
    /// <summary>Only the token SID is checked (kiosk user, SYSTEM or an administrator).</summary>
    None,

    /// <summary>Kiosk-user clients must additionally run from <c>shell.exePath</c>.</summary>
    ExePath,

    /// <summary>
    /// Kiosk-user clients must run from <c>shell.exePath</c> and the executable must carry an Authenticode signature whose
    /// thumbprint matches the expected one (the Agent's own signer when none is configured).
    /// </summary>
    SignatureVerified,
}

/// <summary>Identity of a connected pipe client (IPC_PROTOCOL.md §3).</summary>
/// <param name="Pid">Client process id (<c>GetNamedPipeClientProcessId</c>).</param>
/// <param name="WtsSessionId">WTS session the client runs in.</param>
/// <param name="Sid">User SID of the client token.</param>
/// <param name="ExePath">Full image path, when readable.</param>
/// <param name="IsKiosk">Token user is the kiosk account.</param>
/// <param name="IsPrivileged">Token user is SYSTEM or a member of Administrators (diagnostics client).</param>
public sealed record PipeClientInfo(
    int Pid,
    int WtsSessionId,
    string Sid,
    string? ExePath,
    bool IsKiosk,
    bool IsPrivileged)
{
    /// <summary><see langword="true"/> when the client may use the pipe at all.</summary>
    public bool IsTrusted => IsKiosk || IsPrivileged;
}

/// <summary>
/// Builds the pipe DACL (<c>SYSTEM</c> and <c>Administrators</c> full, kiosk user read/write and never
/// <c>CreateNewInstance</c>, nobody else) and verifies the process behind an accepted connection without impersonation.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PipeSecurityFactory
{
    private static readonly Lazy<string?> AgentSigner = new(() => GetSignerThumbprint(Environment.ProcessPath));

    /// <summary>Authenticode signer thumbprint of the running Agent executable, or <see langword="null"/> when unsigned.</summary>
    public static string? AgentSignerThumbprint => AgentSigner.Value;

    /// <summary>Creates the DACL for a new pipe instance. <paramref name="kioskSid"/> may be empty before the kiosk user is provisioned.</summary>
    public static PipeSecurity Create(string? kioskSid)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        if (TryParseSid(kioskSid, out var kiosk))
        {
            // GENERIC_READ|GENERIC_WRITE from CreateFile maps to FILE_GENERIC_* which include SYNCHRONIZE.
            security.AddAccessRule(new PipeAccessRule(kiosk, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(kiosk, PipeAccessRights.CreateNewInstance, AccessControlType.Deny));
        }

        return security;
    }

    /// <summary>
    /// Identifies the process on the other end of <paramref name="pipe"/> and checks it against the policy. Returns
    /// <see langword="null"/> (after logging why) when the client must be dropped.
    /// </summary>
    /// <param name="pipe">Connected server stream.</param>
    /// <param name="kioskSid">Kiosk account SID (empty when not provisioned yet).</param>
    /// <param name="expectedExePath">Configured Shell executable; checked for kiosk-user clients when <paramref name="mode"/> requires it.</param>
    /// <param name="mode">Strictness.</param>
    /// <param name="expectedSignerThumbprint">Signer thumbprint for <see cref="ClientValidationMode.SignatureVerified"/>; <see langword="null"/> = the Agent's own signer.</param>
    /// <param name="logger">Diagnostics.</param>
    public static PipeClientInfo? ValidateClient(NamedPipeServerStream pipe, string? kioskSid, string? expectedExePath, ClientValidationMode mode, string? expectedSignerThumbprint, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(logger);

        if (!NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid == 0)
        {
            logger.LogWarning("Pipe client rejected: GetNamedPipeClientProcessId failed ({Error})", Win32Error.Message(Win32Error.Last()));
            return null;
        }

        PipeClientInfo? info = Describe((int)pid, kioskSid, logger);
        if (info is null)
        {
            return null;
        }

        if (!info.IsTrusted)
        {
            logger.LogWarning("Pipe client rejected: pid {Pid} ({Exe}) runs as {Sid}, not the kiosk user / SYSTEM / Administrators", info.Pid, info.ExePath, info.Sid);
            return null;
        }

        if (info.IsPrivileged || mode == ClientValidationMode.None)
        {
            return info;
        }

        if (!string.IsNullOrWhiteSpace(expectedExePath))
        {
            if (info.ExePath is null || !PathsEqual(info.ExePath, expectedExePath))
            {
                logger.LogWarning("Pipe client rejected: pid {Pid} image '{Exe}' is not the configured Shell '{Expected}'", info.Pid, info.ExePath, expectedExePath);
                return null;
            }
        }

        if (mode == ClientValidationMode.SignatureVerified)
        {
            var expected = expectedSignerThumbprint ?? AgentSignerThumbprint;
            if (expected is null)
            {
                // Falling back to the weaker ExePath check here would silently downgrade the mode the operator asked
                // for, so a signature-verified pipe with no signer to compare against accepts nobody.
                logger.LogWarning("Pipe client rejected: signed client validation is configured but no signer thumbprint is known (Agent unsigned and ipc.expectedSignerThumbprint unset)");
                return null;
            }

            var actual = info.ExePath is null ? null : GetSignerThumbprint(info.ExePath);
            if (actual is null || !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Pipe client rejected: pid {Pid} image '{Exe}' signer {Actual} does not match {Expected}", info.Pid, info.ExePath, actual ?? "(unsigned)", expected);
                return null;
            }
        }

        return info;
    }

    /// <summary>Reads pid, session, SID and image path of <paramref name="pid"/>; <see langword="null"/> when the process cannot be opened.</summary>
    public static PipeClientInfo? Describe(int pid, string? kioskSid, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        using SafeProcessHandle process = Kernel32.OpenProcess(NativeConst.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (process.IsInvalid)
        {
            logger.LogWarning("Pipe client rejected: OpenProcess({Pid}) failed ({Error})", pid, Win32Error.Message(Win32Error.Last()));
            return null;
        }

        string? exePath = Kernel32.QueryFullProcessImageName(process);
        uint wtsSession = Kernel32.ProcessIdToSessionId((uint)pid, out var session) ? session : uint.MaxValue;

        if (!Advapi32.OpenProcessToken(process, NativeConst.TOKEN_QUERY | NativeConst.TOKEN_DUPLICATE, out SafeTokenHandle token))
        {
            logger.LogWarning("Pipe client rejected: OpenProcessToken({Pid}) failed ({Error})", pid, Win32Error.Message(Win32Error.Last()));
            return null;
        }

        using (token)
        {
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            string sid = identity.User?.Value ?? string.Empty;
            bool privileged = identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            bool kiosk = sid.Length > 0 && string.Equals(sid, kioskSid, StringComparison.OrdinalIgnoreCase);
            return new PipeClientInfo(pid, unchecked((int)wtsSession), sid, exePath, kiosk, privileged);
        }
    }

    /// <summary>SHA-1 thumbprint of the Authenticode signer of <paramref name="path"/>, or <see langword="null"/> when unsigned/unreadable.</summary>
    public static string? GetSignerThumbprint(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return certificate.Thumbprint;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryParseSid(string? value, [NotNullWhen(true)] out SecurityIdentifier? sid)
    {
        sid = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            sid = new SecurityIdentifier(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>kernel32 import missing from <see cref="Kernel32"/>: <c>GetNamedPipeClientProcessId</c>.</summary>
    private static class NativeMethods
    {
#pragma warning disable SYSLIB1054 // DllImport keeps the Agent free of AllowUnsafeBlocks; a single call per connection.
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
#pragma warning restore SYSLIB1054
    }
}

/// <summary>
/// Generates and verifies the Shell handshake token (<c>secure\shell.token</c>, 32 random bytes as 64 lowercase hex;
/// ARCHITECTURE.md §5). The file is regenerated at every Agent start and ACL'd to SYSTEM/Administrators (full) and the
/// kiosk user (read). Comparison is constant-time.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellTokenStore
{
    /// <summary>Random bytes in a token.</summary>
    public const int TokenBytes = 32;

    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly ILogger<ShellTokenStore> _logger;
    private readonly object _gate = new();
    private byte[] _token = Array.Empty<byte>();
    private string? _aclSid;

    /// <summary>Creates the store.</summary>
    public ShellTokenStore(IOptionsMonitor<AgentSettings> settings, ILogger<ShellTokenStore> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Absolute token path (<c>agent.json → ipc.shellTokenPath</c>).</summary>
    public string TokenPath => _settings.CurrentValue.ShellTokenPath;

    /// <summary><see langword="true"/> once <see cref="Generate"/> ran.</summary>
    public bool IsGenerated
    {
        get
        {
            lock (_gate)
            {
                return _token.Length > 0;
            }
        }
    }

    /// <summary>When the current token was written.</summary>
    public DateTimeOffset? GeneratedAt { get; private set; }

    /// <summary>Kiosk SID the file ACL currently grants read access to (<see langword="null"/> = SYSTEM/Administrators only).</summary>
    public string? AclSid
    {
        get
        {
            lock (_gate)
            {
                return _aclSid;
            }
        }
    }

    /// <summary><see langword="true"/> for a well-formed token (64 hex characters).</summary>
    public static bool IsHex64(string? value)
    {
        if (value is null || value.Length != TokenBytes * 2)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Writes a fresh random token to <see cref="TokenPath"/> and applies the ACL. Call once at Agent start, before the Shell is launched.</summary>
    public void Generate(string? kioskSid)
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        var path = TokenPath;
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes(hex));
            ApplyAcl(path, kioskSid);
            _token = Encoding.ASCII.GetBytes(hex);
            _aclSid = string.IsNullOrWhiteSpace(kioskSid) ? null : kioskSid;
            GeneratedAt = DateTimeOffset.UtcNow;
        }

        _logger.LogInformation("Shell token written to {Path} (kiosk read: {Kiosk})", path, string.IsNullOrWhiteSpace(kioskSid) ? "none yet" : kioskSid);
    }

    /// <summary>Re-applies the file ACL when the kiosk SID changed since the last write; <see langword="true"/> when it did.</summary>
    public bool EnsureAcl(string? kioskSid)
    {
        var wanted = string.IsNullOrWhiteSpace(kioskSid) ? null : kioskSid;
        lock (_gate)
        {
            if (_token.Length == 0 || string.Equals(_aclSid, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                ApplyAcl(TokenPath, wanted);
                _aclSid = wanted;
                _logger.LogInformation("Shell token ACL updated for kiosk SID {Sid}", wanted ?? "(none)");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogWarning(ex, "Shell token ACL could not be updated");
                return false;
            }
        }
    }

    /// <summary>Constant-time comparison of a presented token with the generated one.</summary>
    public bool Verify(string? presented)
    {
        if (!IsHex64(presented))
        {
            return false;
        }

        byte[] expected;
        lock (_gate)
        {
            expected = _token;
        }

        if (expected.Length == 0)
        {
            return false;
        }

        var actual = Encoding.ASCII.GetBytes(presented!.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static void ApplyAcl(string path, string? kioskSid)
    {
        var info = new FileInfo(path);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        if (!string.IsNullOrWhiteSpace(kioskSid))
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(kioskSid), FileSystemRights.Read, AccessControlType.Allow));
        }

        info.SetAccessControl(security);
    }
}
