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

    /// <inheritdoc />
    public async Task<HelperResult> SendAsync(byte[] request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTime deadline = DateTime.UtcNow + timeout;
        using var pipe = new NamedPipeClientStream(
            ".",
            HelperProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            TimeSpan connect = timeout < ConnectTimeout ? timeout : ConnectTimeout;
            await pipe.ConnectAsync(connect, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new HelperResult(HelperResultKind.Unreachable, Message: $"{HelperProtocol.PipeName} did not open within {ConnectTimeout.TotalSeconds:0}s");
        }
        catch (IOException ex)
        {
            return new HelperResult(HelperResultKind.Unreachable, Message: ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new HelperResult(HelperResultKind.Unreachable, Message: ex.Message);
        }

        if (!Kernel32.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid))
        {
            return new HelperResult(HelperResultKind.Refused, Message: $"server process of the pipe unknown ({Win32Error.Message(Win32Error.Last())})");
        }

        uint? servicePid = ServiceProcess.RunningProcessId(HelperProtocol.ServiceName);
        if (servicePid != serverPid)
        {
            _logger.LogError(
                "SECURITY: {Pipe} is served by pid {ServerPid}, not by the {Service} service (pid {ServicePid}); nothing sent",
                HelperProtocol.PipeName,
                serverPid,
                HelperProtocol.ServiceName,
                servicePid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not running");
            return new HelperResult(HelperResultKind.Refused, Message: $"pipe server pid {serverPid} is not the helper service");
        }

        return await HelperProtocol.ExchangeAsync(pipe, request, deadline - DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
    }
}
