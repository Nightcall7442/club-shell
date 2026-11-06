using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ClubShell.Core.Configuration;
using Microsoft.Extensions.Options;

namespace ClubShell.Agent.Storage;

/// <summary>
/// Prepares <c>paths.persistent</c> (<see cref="PathsSettings.Persistent"/>) before anything writes to it: creates the
/// directory and gives it an ACL of SYSTEM and Administrators only, inheritance cut — it holds the agent tokens, the
/// signing secret and the kiosk password, and their DPAPI machine scope lets any local account decrypt a blob it can
/// read. Registered as the first hosted service; a no-op when the setting is empty.
/// <para>
/// That ACL shuts everyone else out, so it is set only on a directory of the Agent's own (SHELL_CHANGES 53): not on a volume
/// root (<c>D:\</c>, a writable <c>G:\</c>), and not on a directory that holds other files. Such a setting is logged as an
/// error and only the Agent's own subdirectories there (<c>secure</c>, <c>cache</c>) are protected. A directory the Agent
/// prepared carries <see cref="MarkerFileName"/>.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PersistentStateDirectory : IHostedService
{
    /// <summary>Marks a directory the Agent prepared as its own.</summary>
    public const string MarkerFileName = ".clubshell-persistent";

    /// <summary>What the Agent itself keeps directly under the persistent directory.</summary>
    private static readonly string[] OwnEntries = [ClubShellPaths.SecureDirName, "cache", "agent-identity.json", MarkerFileName];

    private readonly IOptionsMonitor<AgentSettings> _settings;
    private readonly ILogger<PersistentStateDirectory> _logger;

    /// <summary>Creates the service.</summary>
    public PersistentStateDirectory(IOptionsMonitor<AgentSettings> settings, ILogger<PersistentStateDirectory> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var root = _settings.CurrentValue.Paths.Persistent;
        if (string.IsNullOrWhiteSpace(root))
        {
            return Task.CompletedTask;
        }

        try
        {
            if (Refusal(root) is { } reason)
            {
                AgentSettings settings = _settings.CurrentValue;
                string full = Path.GetFullPath(root);
                foreach (string own in new[] { settings.SecureDir, settings.ResolvePersistentPath(settings.Paths.Cache) })
                {
                    if (Path.GetFullPath(own).StartsWith(Path.TrimEndingDirectorySeparator(full) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        Protect(own);
                    }
                }

                _logger.LogError(
                    "paths.persistent {Path} is {Reason}: its access rights are left as they are and only the Agent's own subdirectories are protected. Give the Agent a directory of its own (DISKLESS.md)",
                    root,
                    reason);
                return Task.CompletedTask;
            }

            Protect(root);
            File.WriteAllText(Path.Combine(root, MarkerFileName), "ClubShell Agent state (paths.persistent)\r\n");
            _logger.LogInformation("Persistent state directory {Path} ready", root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Not fatal: the identity and the outbox then fail to persist there and are logged where they are written.
            _logger.LogError(ex, "Persistent state directory {Path} could not be prepared", root);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Why <paramref name="path"/> must not get the Agent's ACL, or <see langword="null"/>: a volume root, or an existing
    /// directory without <see cref="MarkerFileName"/> that holds entries other than the Agent's own.
    /// </summary>
    public static string? Refusal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Path.GetPathRoot(full) is { Length: > 0 } root && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
        {
            return "a volume root";
        }

        var directory = new DirectoryInfo(full);
        if (!directory.Exists || File.Exists(Path.Combine(full, MarkerFileName)))
        {
            return null;
        }

        List<string> foreign = directory.EnumerateFileSystemInfos()
            .Select(e => e.Name)
            .Where(name => !OwnEntries.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Take(3)
            .ToList();
        return foreign.Count == 0 ? null : $"a directory that holds other files ({string.Join(", ", foreign)})";
    }

    /// <summary>Creates <paramref name="path"/> when missing and sets its ACL to SYSTEM and Administrators (full control, inherited), nothing else.</summary>
    public static void Protect(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            directory.Create();
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }
}
