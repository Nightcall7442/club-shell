using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.ServiceProcess;
using ClubShell.Core.Abstractions;
using ClubShell.Windows.Hardware;
using ClubShell.Windows.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClubShell.Windows.Storage;

/// <summary>CHAP credentials for an iSCSI login.</summary>
/// <param name="Username">CHAP user.</param>
/// <param name="Secret">CHAP secret (12–16 characters for Microsoft targets).</param>
public sealed record IscsiChapCredentials(string Username, string Secret);

/// <summary>An iSCSI session as reported by MSFT_iSCSISession.</summary>
/// <param name="TargetIqn">Target node address.</param>
/// <param name="SessionId">Session identifier accepted by <c>iscsicli LogoutTarget</c>.</param>
/// <param name="IsConnected">Whether the session is connected.</param>
/// <param name="IsPersistent">Whether the login is restored at boot.</param>
public sealed record IscsiSession(string TargetIqn, string SessionId, bool IsConnected, bool IsPersistent);

/// <summary>
/// Microsoft iSCSI initiator driven through <c>iscsicli.exe</c> for actions (portal, login, logout, persistence)
/// and the Storage Management WMI classes (<c>MSFT_iSCSISession</c>, <c>MSFT_iSCSITarget</c>) for state, which avoids
/// parsing localized tool output. The MSiSCSI service must run: <see cref="EnsureServiceRunningAsync"/> starts it and
/// sets it to automatic start so persistent logins survive reboots (agent.json <c>storage.gamesShare.iscsi</c>).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class IscsiInitiator
{
    /// <summary>Default iSCSI portal port.</summary>
    public const int DefaultPort = 3260;

    private const string ServiceName = "MSiSCSI";
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ServiceStartTimeout = TimeSpan.FromSeconds(30);

    private readonly WmiQueries _wmi;
    private readonly IClock _clock;
    private readonly ILogger<IscsiInitiator> _logger;
    private readonly string _iscsicli = Path.Combine(Environment.SystemDirectory, "iscsicli.exe");
    private readonly string _sc = Path.Combine(Environment.SystemDirectory, "sc.exe");

    /// <summary>Creates the wrapper.</summary>
    public IscsiInitiator(WmiQueries wmi, IClock? clock = null, ILogger<IscsiInitiator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(wmi);
        _wmi = wmi;
        _clock = clock ?? SystemClock.Instance;
        _logger = logger ?? NullLogger<IscsiInitiator>.Instance;
    }

    /// <summary>Starts the MSiSCSI service when stopped and sets its start type to automatic.</summary>
    /// <exception cref="InvalidOperationException">The service is missing or did not reach the running state.</exception>
    public async Task EnsureServiceRunningAsync(CancellationToken cancellationToken)
    {
        bool started = await WmiQueries.RunBlockingAsync(StartServiceIfNeeded, ServiceStartTimeout + TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        if (started)
        {
            _logger.LogInformation("MSiSCSI service started");
        }

        try
        {
            ProcessResult result = await ProcessRunner.RunAsync(_sc, new[] { "config", ServiceName, "start=", "auto" }, ToolTimeout, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                _logger.LogWarning("sc config MSiSCSI start=auto exited with {ExitCode}", result.ExitCode);
            }
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex, "sc.exe could not be started");
        }
        catch (System.TimeoutException ex)
        {
            _logger.LogWarning(ex, "sc.exe timed out");
        }
    }

    /// <summary>Registers a target portal (send-targets discovery source).</summary>
    /// <exception cref="InvalidOperationException">iscsicli failed.</exception>
    public async Task AddTargetPortalAsync(string address, int port = DefaultPort, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        string[] args = port == DefaultPort
            ? new[] { "QAddTargetPortal", address }
            : new[] { "AddTargetPortal", address, port.ToString(CultureInfo.InvariantCulture), "*", "*", "*", "*", "*", "*", "*", "*", "*", "*", "*", "*" };
        await RunIscsicliAsync(args, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("iSCSI portal {Address}:{Port} added", address, port);
    }

    /// <summary>Refreshes discovery and lists the known target IQNs (<c>iscsicli ListTargets T</c>).</summary>
    public async Task<IReadOnlyList<string>> ListTargetsAsync(CancellationToken cancellationToken)
    {
        ProcessResult result = await RunIscsicliAsync(new[] { "ListTargets", "T" }, cancellationToken).ConfigureAwait(false);
        return ParseTargetList(result.StandardOutput);
    }

    /// <summary>Logs in to a discovered target now and, when <paramref name="persistent"/>, registers a persistent (boot-time) login too.</summary>
    /// <exception cref="InvalidOperationException">iscsicli failed.</exception>
    public async Task LoginAsync(string targetIqn, bool persistent, IscsiChapCredentials? chap = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIqn);
        if (!await IsConnectedAsync(targetIqn, cancellationToken).ConfigureAwait(false))
        {
            var quick = new List<string> { "QLoginTarget", targetIqn };
            if (chap is not null)
            {
                quick.Add(chap.Username);
                quick.Add(chap.Secret);
            }

            await RunIscsicliAsync(quick, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("iSCSI target {Target} logged in", targetIqn);
        }

        if (persistent)
        {
            // PersistentLoginTarget <Target> <ReportToPNP> <PortalAddr> <PortalSocket> <InitiatorInstance> <Port> <SecurityFlags>
            //   <LoginFlags> <HeaderDigest> <DataDigest> <MaxConnections> <DefaultTime2Wait> <DefaultTime2Retain>
            //   <Username> <Password> <AuthType> <Key> <MappingCount>
            var args = new List<string> { "PersistentLoginTarget", targetIqn, "T" };
            for (int i = 0; i < 15; i++)
            {
                args.Add("*");
            }

            if (chap is not null)
            {
                args[14] = chap.Username;
                args[15] = chap.Secret;
                args[16] = "1";
            }

            args.Add("0");
            await RunIscsicliAsync(args, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("iSCSI target {Target} registered for persistent login", targetIqn);
        }
    }

    /// <summary>Logs out every session of the target; <see langword="false"/> when none existed.</summary>
    /// <exception cref="InvalidOperationException">iscsicli failed.</exception>
    public async Task<bool> LogoutAsync(string targetIqn, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIqn);
        bool any = false;
        foreach (IscsiSession session in await ListSessionsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!string.Equals(session.TargetIqn, targetIqn, StringComparison.OrdinalIgnoreCase) || session.SessionId.Length == 0)
            {
                continue;
            }

            await RunIscsicliAsync(new[] { "LogoutTarget", session.SessionId }, cancellationToken).ConfigureAwait(false);
            any = true;
        }

        if (any)
        {
            _logger.LogInformation("iSCSI target {Target} logged out", targetIqn);
        }

        return any;
    }

    /// <summary>Removes the persistent (boot-time) login of a target registered through the given portal.</summary>
    /// <exception cref="InvalidOperationException">iscsicli failed.</exception>
    public async Task RemovePersistentLoginAsync(string targetIqn, string portalAddress, int portalPort = DefaultPort, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIqn);
        ArgumentException.ThrowIfNullOrWhiteSpace(portalAddress);
        await RunIscsicliAsync(new[] { "RemovePersistentTarget", "*", targetIqn, "*", portalAddress, portalPort.ToString(CultureInfo.InvariantCulture) }, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("iSCSI persistent login for {Target} removed", targetIqn);
    }

    /// <summary>Sessions known to the initiator (empty when the service is stopped).</summary>
    public async Task<IReadOnlyList<IscsiSession>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await _wmi.QueryAsync(WmiQueries.StorageScope, "SELECT SessionIdentifier, TargetNodeAddress, IsConnected, IsPersistent FROM MSFT_iSCSISession", null, cancellationToken).ConfigureAwait(false);
        var result = new List<IscsiSession>(rows.Count);
        foreach (IReadOnlyDictionary<string, object?> row in rows)
        {
            result.Add(new IscsiSession(row.GetString("TargetNodeAddress"), row.GetString("SessionIdentifier"), row.GetBoolean("IsConnected"), row.GetBoolean("IsPersistent")));
        }

        return result;
    }

    /// <summary><see langword="true"/> when a connected session to the target exists (WMI first, <c>iscsicli SessionList</c> as fallback).</summary>
    public async Task<bool> IsConnectedAsync(string targetIqn, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIqn);
        WmiQueryResult targets = await _wmi.TryQueryAsync(WmiQueries.StorageScope, "SELECT NodeAddress, IsConnected FROM MSFT_iSCSITarget", null, cancellationToken).ConfigureAwait(false);
        if (targets.Succeeded)
        {
            foreach (IReadOnlyDictionary<string, object?> row in targets.Rows)
            {
                if (string.Equals(row.GetString("NodeAddress"), targetIqn, StringComparison.OrdinalIgnoreCase))
                {
                    return row.GetBoolean("IsConnected");
                }
            }

            return false;
        }

        try
        {
            ProcessResult result = await ProcessRunner.RunAsync(_iscsicli, new[] { "SessionList" }, ToolTimeout, cancellationToken).ConfigureAwait(false);
            return result.Success && result.StandardOutput.Contains(targetIqn, StringComparison.OrdinalIgnoreCase);
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (System.TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Polls until the drive letter is ready (volume mounted after login) or <paramref name="timeout"/> elapses.</summary>
    public async Task<bool> WaitForVolumeAsync(char driveLetter, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string root = char.ToUpperInvariant(driveLetter) + ":\\";
        long started = _clock.GetTimestamp();
        while (true)
        {
            if (IsVolumeReady(root))
            {
                return true;
            }

            if (_clock.GetElapsedTime(started) >= timeout)
            {
                _logger.LogWarning("Volume {Root} did not become ready within {Timeout}", root, timeout);
                return false;
            }

            await _clock.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Extracts IQN / EUI names from <c>iscsicli ListTargets</c> output (one name per line, labels ignored).</summary>
    public static IReadOnlyList<string> ParseTargetList(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var targets = new List<string>();
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            if ((line.StartsWith("iqn.", StringComparison.OrdinalIgnoreCase) || line.StartsWith("eui.", StringComparison.OrdinalIgnoreCase))
                && !line.Contains(' ', StringComparison.Ordinal)
                && !targets.Contains(line))
            {
                targets.Add(line);
            }
        }

        return targets;
    }

    private static bool IsVolumeReady(string root)
    {
        try
        {
            return new DriveInfo(root).IsReady;
        }
        catch (ArgumentException)
        {
            return false;
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

    private static bool StartServiceIfNeeded()
    {
        using var controller = new ServiceController(ServiceName);
        try
        {
            if (controller.Status == ServiceControllerStatus.Running)
            {
                return false;
            }

            if (controller.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.Paused)
            {
                if (controller.Status == ServiceControllerStatus.Paused)
                {
                    controller.Continue();
                }
                else
                {
                    controller.Start();
                }
            }

            controller.WaitForStatus(ServiceControllerStatus.Running, ServiceStartTimeout);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException("MSiSCSI service is not installed or could not be controlled", ex);
        }
        catch (System.ServiceProcess.TimeoutException ex)
        {
            throw new InvalidOperationException("MSiSCSI service did not start in time", ex);
        }
    }

    private async Task<ProcessResult> RunIscsicliAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(_iscsicli, arguments, ToolTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("iscsicli.exe could not be started", ex);
        }

        if (!result.Success)
        {
            throw new InvalidOperationException($"iscsicli {arguments[0]} failed (exit 0x{result.ExitCode:X8}): {Tail(result.CombinedOutput)}");
        }

        return result;
    }

    private static string Tail(string output)
    {
        string trimmed = output.Trim();
        return trimmed.Length <= 400 ? trimmed : trimmed[^400..];
    }
}
