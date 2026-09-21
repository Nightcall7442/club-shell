using System.Collections.Concurrent;
using System.Runtime.Versioning;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Ipc;
using ClubShell.Agent.Server;
using ClubShell.Core.Abstractions;
using ClubShell.Windows.Sessions;
using Microsoft.Extensions.Logging;

namespace ClubShell.Agent.Remote;

/// <summary>
/// Delivers admin-originated UI events to the Shell over IPC: <c>admin.message</c> and
/// <c>admin.remoteControl</c>. Implemented by the IPC layer; declared here so <see cref="AdminMessageService"/> and
/// <see cref="RemoteInputService"/> can raise them without depending on the pipe server.
/// </summary>
public interface IAdminEventSink
{
    /// <summary>Publishes an <c>admin.message</c> to the Shell.</summary>
    ValueTask PublishMessageAsync(AdminMessage message, CancellationToken cancellationToken);

    /// <summary>Publishes an <c>admin.remoteControl</c> transition to the Shell.</summary>
    ValueTask PublishRemoteControlAsync(RemoteControlEvent remoteControl, CancellationToken cancellationToken);
}

/// <summary>
/// Handles the <see cref="ServerCommandType.Message"/> command (SERVER_API.md §6.1): converts the payload to an
/// <see cref="AdminMessage"/> and delivers it to the Shell, falling back to <see cref="WtsSessions.SendMessage"/> in the
/// interactive session when the Shell is disconnected. Messages that require acknowledgement are tracked with a TTL;
/// the Shell's <c>sys.ackAdminMessage</c> resolves them, letting the caller send the server the second delivery ack.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AdminMessageService
{
    /// <summary>How long a message that requires acknowledgement is retained before it is considered unacknowledged.</summary>
    public static TimeSpan AckTtl { get; } = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan WtsMessageTimeout = TimeSpan.FromSeconds(30);

    private readonly IAdminEventSink _sink;
    private readonly IShellConnectionState _shell;
    private readonly IClock _clock;
    private readonly ILogger<AdminMessageService> _logger;
    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();

    /// <summary>Creates the service.</summary>
    public AdminMessageService(
        IAdminEventSink sink,
        IShellConnectionState shell,
        IClock clock,
        ILogger<AdminMessageService> logger)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _sink = sink;
        _shell = shell;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Delivers <paramref name="command"/> to the Shell (or the interactive session as a fallback) and returns the
    /// immediate delivery result. When <see cref="MessageCommand.RequiresAck"/> is set, the message is tracked until
    /// acknowledged or <see cref="AckTtl"/> elapses; await <see cref="WaitForAckAsync"/> for the acknowledgement.
    /// </summary>
    public async Task<MessageDeliveryResult> DeliverAsync(MessageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var deliveredAt = _clock.UtcNow;
        var message = AdminMessage.FromCommand(command, deliveredAt);

        PruneExpired(deliveredAt);
        if (command.RequiresAck)
        {
            RegisterPending(message.Id, deliveredAt);
        }

        if (_shell.IsConnected)
        {
            try
            {
                await _sink.PublishMessageAsync(message, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Admin message {Id} delivered to the Shell (requiresAck={RequiresAck})", message.Id, command.RequiresAck);
                return new MessageDeliveryResult(deliveredAt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Delivering admin message {Id} to the Shell failed; using the session fallback", message.Id);
            }
        }

        SendViaWts(message);
        return new MessageDeliveryResult(deliveredAt);
    }

    /// <summary>Records the user's acknowledgement (from <c>sys.ackAdminMessage</c>); <see langword="false"/> when unknown or already resolved.</summary>
    public bool Ack(Guid messageId)
    {
        if (_pending.TryGetValue(messageId, out var pending) && pending.Completion.TrySetResult(_clock.UtcNow))
        {
            _logger.LogInformation("Admin message {Id} acknowledged by the user", messageId);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Awaits the acknowledgement of a previously delivered message. Returns the delivery result with
    /// <see cref="MessageDeliveryResult.AckedAt"/> set when acknowledged within <see cref="AckTtl"/>, otherwise
    /// <see langword="null"/>. Unknown ids return delivery-only.
    /// </summary>
    public async Task<MessageDeliveryResult> WaitForAckAsync(Guid messageId, CancellationToken cancellationToken)
    {
        if (!_pending.TryGetValue(messageId, out var pending))
        {
            return new MessageDeliveryResult(_clock.UtcNow);
        }

        var remaining = pending.ExpiresAt - _clock.UtcNow;
        try
        {
            if (remaining <= TimeSpan.Zero)
            {
                return new MessageDeliveryResult(pending.DeliveredAt);
            }

            var ackedAt = await pending.Completion.Task.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            return new MessageDeliveryResult(pending.DeliveredAt, ackedAt);
        }
        catch (TimeoutException)
        {
            _logger.LogInformation("Admin message {Id} not acknowledged within {Ttl}", messageId, AckTtl);
            return new MessageDeliveryResult(pending.DeliveredAt);
        }
        catch (OperationCanceledException)
        {
            return new MessageDeliveryResult(pending.DeliveredAt);
        }
        finally
        {
            Remove(messageId);
        }
    }

    private void RegisterPending(Guid messageId, DateTimeOffset deliveredAt)
    {
        var completion = new TaskCompletionSource<DateTimeOffset?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[messageId] = new Pending(deliveredAt, deliveredAt + AckTtl, completion);
    }

    private void Remove(Guid messageId)
    {
        if (_pending.TryRemove(messageId, out var pending))
        {
            pending.Completion.TrySetResult(null);
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var pair in _pending)
        {
            if (pair.Value.ExpiresAt <= now && _pending.TryRemove(pair.Key, out var pending))
            {
                pending.Completion.TrySetResult(null);
            }
        }
    }

    private void SendViaWts(AdminMessage message)
    {
        var sessionId = WtsSessions.GetActiveConsoleSessionId();
        if (sessionId == uint.MaxValue)
        {
            _logger.LogWarning("No interactive session for the admin message {Id} fallback", message.Id);
            return;
        }

        try
        {
            _ = WtsSessions.SendMessage(sessionId, message.From, message.Text, WtsMessageTimeout, wait: false);
            _logger.LogInformation("Admin message {Id} shown via WTSSendMessage in session {Session}", message.Id, sessionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "WTSSendMessage fallback for message {Id} failed", message.Id);
        }
    }

    private sealed record Pending(DateTimeOffset DeliveredAt, DateTimeOffset ExpiresAt, TaskCompletionSource<DateTimeOffset?> Completion);
}
