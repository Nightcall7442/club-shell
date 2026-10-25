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
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PersistentStateDirectory : IHostedService
{
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
            Protect(root);
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
