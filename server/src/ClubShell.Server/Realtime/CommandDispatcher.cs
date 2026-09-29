using System.Text.Json;
using ClubShell.Contracts.Commands;
using ClubShell.Contracts.Pcs;
using ClubShell.Contracts.Serialization;
using ClubShell.Server.Agents;
using Npgsql;

namespace ClubShell.Server.Realtime;

/// <summary>
/// A command to queue: its type and typed payload (DESIGN §6.4 table). The builders cover the ten AsyncAPI commands with
/// <c>x-server-status: required</c>; payloads are the Contracts DTOs, so the frame is what the agent parses.
/// </summary>
public sealed record NewCommand(ServerCommandType Type, JsonElement? Payload)
{
    public static NewCommand Lock(LockCommand payload) => new(ServerCommandType.Lock, JsonDefaults.ToElement(payload));

    public static NewCommand Unlock() => new(ServerCommandType.Unlock, null);

    /// <summary>Result <c>MessageDeliveryResult</c>; with <c>requiresAck</c> a second REST ack carries <c>ackedAt</c>.</summary>
    public static NewCommand Message(MessageCommand payload) => new(ServerCommandType.Message, JsonDefaults.ToElement(payload));

    public static NewCommand Reboot(PowerCommand payload) => new(ServerCommandType.Reboot, JsonDefaults.ToElement(payload));

    public static NewCommand Shutdown(PowerCommand payload) => new(ServerCommandType.Shutdown, JsonDefaults.ToElement(payload));

    public static NewCommand EndSession(EndSessionCommand payload) => new(ServerCommandType.EndSession, JsonDefaults.ToElement(payload));

    /// <summary>The server has already charged: v1 always sends <c>charge:false</c> (§5.6).</summary>
    public static NewCommand ExtendSession(ExtendSessionCommand payload) => new(ServerCommandType.ExtendSession, JsonDefaults.ToElement(payload));

    public static NewCommand SetPolicy(Policy payload) => new(ServerCommandType.SetPolicy, JsonDefaults.ToElement(payload));

    public static NewCommand ReloadPolicy() => new(ServerCommandType.ReloadPolicy, null);

    /// <summary>Flags must be explicit: an empty payload would also refetch <c>apps</c>/<c>products</c>, which answer 501 (§5.9).</summary>
    public static NewCommand RefreshConfig(RefreshConfigCommand payload) => new(ServerCommandType.RefreshConfig, JsonDefaults.ToElement(payload));
}

/// <summary>
/// Queues a command and sends it at once when the PC is connected (DESIGN §6.4 step 1); otherwise it waits for the next
/// connection or the REST drain. Every command expires after <see cref="AgentOptions.CommandTtlMin"/>. Commands the
/// contract marks <c>notImplemented</c> are refused: the agent may not know them.
/// </summary>
public sealed class CommandDispatcher(CommandRepository commands, AgentSocketHub hub, AgentOptions options)
{
    public static readonly IReadOnlySet<ServerCommandType> Required = new HashSet<ServerCommandType>
    {
        ServerCommandType.Lock,
        ServerCommandType.Unlock,
        ServerCommandType.Message,
        ServerCommandType.Reboot,
        ServerCommandType.Shutdown,
        ServerCommandType.EndSession,
        ServerCommandType.ExtendSession,
        ServerCommandType.SetPolicy,
        ServerCommandType.ReloadPolicy,
        ServerCommandType.RefreshConfig,
    };

    /// <param name="supersedes">An earlier command this one cancels (<c>lock</c> → <c>unlock</c>, AsyncAPI §7).</param>
    public async Task<ServerCommandEnvelope> EnqueueAsync(Guid clubId, Guid pcId, NewCommand command, Guid? supersedes = null, Guid? issuedByStaffId = null)
    {
        var envelope = await QueueAsync(null, clubId, pcId, command, supersedes, issuedByStaffId);
        await SendAsync(pcId, envelope);
        return envelope;
    }

    /// <summary>
    /// Queues without sending, in <paramref name="tx"/> when given: the command commits with the caller's action and its
    /// audit entry (DESIGN §3.7); after the commit the caller sends it with <see cref="SendAsync"/>.
    /// </summary>
    public Task<ServerCommandEnvelope> QueueAsync(
        NpgsqlTransaction? tx, Guid clubId, Guid pcId, NewCommand command, Guid? supersedes = null, Guid? issuedByStaffId = null) =>
        Required.Contains(command.Type)
            ? commands.EnqueueAsync(clubId, pcId, command.Type, command.Payload, supersedes, issuedByStaffId, TimeSpan.FromMinutes(options.CommandTtlMin), tx)
            : throw new ArgumentException($"Command {command.Type.ToWireName()} is notImplemented in the contract and is never sent", nameof(command));

    /// <summary>Sends a queued command at once when the PC is connected.</summary>
    public Task SendAsync(Guid pcId, ServerCommandEnvelope envelope) => hub.TrySendCommandAsync(pcId, envelope);
}
