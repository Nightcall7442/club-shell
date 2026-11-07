using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using ClubShell.Windows.Native;
using ClubShell.Windows.Processes;

namespace ClubShell.Agent.Saves;

/// <summary>Sends one command to the diskless helper and returns its answer.</summary>
public interface IDisklessHelperChannel
{
    /// <summary>
    /// Connects, sends <paramref name="request"/> (one JSON line, <see cref="HelperProtocol"/>), waits up to
    /// <paramref name="timeout"/> in all for the answer and disconnects. Never throws except on
    /// <paramref name="cancellationToken"/>.
    /// </summary>
    Task<HelperResult> SendAsync(byte[] request, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IDisklessHelperChannel"/> over <c>\\.\pipe\club-diskless-helper</c> (SHELL_CHANGES 23). Any process can
/// create a pipe of that name while the helper service is stopped or restarting, the player's included, and the Agent
/// runs as SYSTEM. So the client connects at <see cref="TokenImpersonationLevel.Identification"/> — the default level is
/// impersonation, which would hand a fake server the Agent's SYSTEM token — and sends nothing unless the pipe's server
/// process is the running <c>ClubDisklessHelper</c> service.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DisklessHelperChannel : IDisklessHelperChannel
{
    /// <summary>How long to wait for the pipe; the helper accepts up to 4 connections at once.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<DisklessHelperChannel> _logger;

    /// <summary>Creates the channel.</summary>
    public DisklessHelperChannel(ILogger<DisklessHelperChannel> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Pause between two connection attempts while the channel is missing (the helper restarting).</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    /// <remarks>
    /// The helper restarts itself when it updates (1.8.0+): its channel is gone for 5–30 s, up to 2 minutes on a
    /// rollback. A missing channel, or one served by another process, is therefore retried every
    /// <see cref="RetryInterval"/> until <paramref name="timeout"/> runs out, and the service's PID is looked up afresh
    /// at every connection — the restarted service is a new process, and while it is down anyone may own the name.
    /// </remarks>
    public async Task<HelperResult> SendAsync(byte[] request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTime deadline = DateTime.UtcNow + timeout;
        HelperResult last = new(HelperResultKind.Unreachable, Message: $"{HelperProtocol.PipeName} not found");
        bool securityLogged = false;
        int attempts = 0;
        while (true)
        {
            attempts++;
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return attempts > 2 ? last with { Message = $"{last.Message} ({attempts - 1} attempts)" } : last;
            }

            using var pipe = new NamedPipeClientStream(
                ".",
                HelperProtocol.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            HelperResult? refused = null;
            try
            {
                await pipe.ConnectAsync(remaining < ConnectTimeout ? remaining : ConnectTimeout, cancellationToken).ConfigureAwait(false);
                refused = CheckServer(pipe, ref securityLogged);
            }
            catch (TimeoutException)
            {
                last = new HelperResult(HelperResultKind.Unreachable, Message: $"{HelperProtocol.PipeName} did not open");
            }
            catch (IOException ex)
            {
                last = new HelperResult(HelperResultKind.Unreachable, Message: ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                last = new HelperResult(HelperResultKind.Unreachable, Message: ex.Message);
            }

            if (pipe.IsConnected && refused is null)
            {
                return await HelperProtocol.ExchangeAsync(pipe, request, deadline - DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
            }

            last = refused ?? last;
            remaining = deadline - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining < RetryInterval ? remaining : RetryInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// <see langword="null"/> when the pipe's server is the running helper service, else why not (nothing is sent then).
    /// The first mismatch of a command is logged as a security error, the retries at debug.
    /// </summary>
    private HelperResult? CheckServer(NamedPipeClientStream pipe, ref bool securityLogged)
    {
        if (!Kernel32.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid))
        {
            return new HelperResult(HelperResultKind.Refused, Message: $"server process of the pipe unknown ({Win32Error.Message(Win32Error.Last())})");
        }

        uint? servicePid = ServiceProcess.RunningProcessId(HelperProtocol.ServiceName);
        if (servicePid == serverPid)
        {
            return null;
        }

        string service = servicePid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not running";
        if (!securityLogged)
        {
            _logger.LogError(
                "SECURITY: {Pipe} is served by pid {ServerPid}, not by the {Service} service (pid {ServicePid}); nothing sent",
                HelperProtocol.PipeName,
                serverPid,
                HelperProtocol.ServiceName,
                service);
            securityLogged = true;
        }
        else
        {
            _logger.LogDebug("{Pipe} still served by pid {ServerPid}, service pid {ServicePid}", HelperProtocol.PipeName, serverPid, service);
        }

        return new HelperResult(HelperResultKind.Refused, Message: $"pipe server pid {serverPid} is not the helper service");
    }
}
